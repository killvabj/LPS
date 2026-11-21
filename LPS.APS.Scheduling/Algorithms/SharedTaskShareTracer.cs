namespace LPS.APS.Scheduling.Algorithms;

using LPS.APS.Core.Dto;

/// <summary>
/// **共享 Task 份额逆追**（EX-T03 的消费侧算法；0号位 v1.8 §六 逐字：
/// 「A09 共享 Task 多根单、三层 BOM 多 PO/SH **最后必要数量链**，**1号位先做消费算法**再与 2号位同 Run 联验」）。
///
/// <para><b>管辖区说明</b>：本类只做**消费侧计算**——输入是 Solver 已产出的
/// <see cref="AllocationTaskShare"/>（Task → AllocationSequence 的份额）与
/// <see cref="FinalTaskPeggingDraft"/>（Task → Task 的上下游边），
/// 以及 2号位 提供的 `AllocationSequence → 根单键` 映射。
/// **不新增 DTO 字段、不要求 2号位 预知 Solver 输出**（0号位 §四 D-01 原文）。</para>
///
/// <para><b>两个方法分工</b></para>
/// <list type="bullet">
///   <item><see cref="AttributeByRoot"/>：把每个 Task 的份额按 `AllocationSequence` 归属到根单，
///     得到「**多根单各自实际拿到多少**」。一个 Task 被多个 Allocation 共享时，各拿各自份额。</item>
///   <item><see cref="TraceLastNecessaryUpstream"/>：沿 `FinalTaskPeggingDraft` **向上游**走，
///     收集构成某下游 Task 的**最后必要数量链**（每个上游 Task 及其被需要的量）。</item>
/// </list>
///
/// <para><b>守恒铁律</b>：归属结果**不得丢量**。映射缺失的 `AllocationSequence` 必须进
/// <see cref="RootAttribution.UnattributedQty"/> 并列出键；<b>不允许</b>静默丢弃。</para>
///
/// <para><b>已知局限</b>：共享 Task 只挂**锚点**的 `SourceDraftId`（被并入方份额在
/// `AllocationTaskShare` 里，不在 Task 上）⇒ 本类一律以 `AllocationTaskShare` 为准，**不按 Task 的
/// `SourceDraftId` 反推**，否则合批场景会丢份额。</para>
/// </summary>
internal static class SharedTaskShareTracer
{
    /// <summary>按根单归属的结果。</summary>
    /// <param name="ByRoot">根单键 → 归属到的数量（同键累加）。</param>
    /// <param name="UnattributedQty">映射缺失、无法归属的数量合计。</param>
    /// <param name="UnmappedAllocationSequences">映射缺失的 `AllocationSequence`（去重、升序）。</param>
    public sealed record RootAttribution(
        IReadOnlyDictionary<string, decimal> ByRoot,
        decimal UnattributedQty,
        IReadOnlyList<long> UnmappedAllocationSequences);

    /// <summary>
    /// 把每条 `AllocationTaskShare` 归属到它所属的根单。
    /// </summary>
    /// <param name="shares">每个 Task 的份额（同一 Task 可有多条，对应多个 Allocation）。</param>
    /// <param name="allocationToRoot">`AllocationSequence → 根单键`（**由 2号位 提供**；缺失项计入未归属，不猜）。</param>
    public static RootAttribution AttributeByRoot(
        IReadOnlyList<AllocationTaskShare> shares,
        IReadOnlyDictionary<long, string> allocationToRoot)
    {
        var byRoot = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var unmapped = new SortedSet<long>();
        decimal unattributed = 0m;

        if (shares is null || shares.Count == 0)
        {
            return new RootAttribution(byRoot, 0m, Array.Empty<long>());
        }

        foreach (var share in shares)
        {
            if (allocationToRoot is not null
                && allocationToRoot.TryGetValue(share.AllocationSequence, out var root)
                && !string.IsNullOrEmpty(root))
            {
                byRoot.TryGetValue(root, out var cur);
                byRoot[root] = cur + share.ComponentQty;
            }
            else
            {
                unattributed += share.ComponentQty;
                unmapped.Add(share.AllocationSequence);
            }
        }

        return new RootAttribution(byRoot, unattributed, unmapped.ToArray());
    }

    /// <summary>
    /// 沿 `FinalTaskPeggingDraft` 从 <paramref name="downstreamFinalDraftId"/> **向上游**走，
    /// 返回「最后必要数量链」：每个被走到的上游 Task 及其**在该链上被需要的量**。
    ///
    /// <para>累计规则：同一上游被多条路径需要 ⇒ **累加**，不取 max、不取 first。
    /// BOM 用量系数若已在 Pegging 边的 `Quantity` 里折算，本方法**不得再乘任何系数**（不重复折算）。</para>
    ///
    /// <para>成环直接抛 <see cref="InvalidOperationException"/>（宁可显式失败，也不返回残缺链）。</para>
    /// </summary>
    /// <param name="downstreamFinalDraftId">起点（下游 Task）。</param>
    /// <param name="peggingDrafts">Task → Task 的上下游边。</param>
    public static IReadOnlyDictionary<string, decimal> TraceLastNecessaryUpstream(
        string downstreamFinalDraftId,
        IReadOnlyList<FinalTaskPeggingDraft> peggingDrafts)
    {
        var required = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(downstreamFinalDraftId)
            || peggingDrafts is null
            || peggingDrafts.Count == 0)
        {
            return required;
        }

        // 上游索引：Downstream → [(Upstream, Qty)]
        var upstreamOf = new Dictionary<string, List<(string Upstream, decimal Qty)>>(StringComparer.Ordinal);
        foreach (var e in peggingDrafts)
        {
            if (string.IsNullOrEmpty(e.DownstreamFinalDraftId) || string.IsNullOrEmpty(e.UpstreamFinalDraftId))
            {
                continue;
            }
            if (!upstreamOf.TryGetValue(e.DownstreamFinalDraftId, out var list))
            {
                list = new List<(string, decimal)>();
                upstreamOf[e.DownstreamFinalDraftId] = list;
            }
            list.Add((e.UpstreamFinalDraftId, e.Quantity));
        }

        // 迭代式 DFS（避免深链爆栈）；onPath 做环检测
        var onPath = new HashSet<string>(StringComparer.Ordinal) { downstreamFinalDraftId };
        var stack = new Stack<(string Node, int NextChild)>();
        stack.Push((downstreamFinalDraftId, 0));

        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();
            if (!upstreamOf.TryGetValue(node, out var children))
            {
                children = new List<(string, decimal)>();
            }

            if (next >= children.Count)
            {
                onPath.Remove(node);
                continue;
            }

            var (upstream, qty) = children[next];
            stack.Push((node, next + 1));

            if (!onPath.Add(upstream))
            {
                throw new InvalidOperationException(
                    $"Pegging 边成环：{upstream} 已在当前路径上（起点 {downstreamFinalDraftId}）");
            }

            required.TryGetValue(upstream, out var cur);
            required[upstream] = cur + qty;

            stack.Push((upstream, 0));
        }

        // 起点本身不是"上游"，从结果里剔除
        required.Remove(downstreamFinalDraftId);
        return required;
    }
}
