namespace LPS.APS.Scheduling.Algorithms;

using LPS.APS.Core.Dto;

/// <summary>
/// **关键路径判定**（EX-T05；冻结 07 字段说明 §4.4 `Task.IsCriticalPath`「1=关键路径」）。
///
/// <para><b>口径来源（不是我自己定的）</b></para>
/// <list type="bullet">
///   <item>0号位 v1.2（2026-11-17 §B3-1）逐字：「**`IsCriticalPath` 不得凭某条边局部零 Slack 写真**」。</item>
///   <item>《03 有限产能排产与滚动 90 天》v2.2 §33：只沿**最终有效关键路径**，**不机械累加所有等待**。</item>
///   <item>07 字段说明 §4.4 `Task.IsCriticalPath`：「1=关键路径」。</item>
/// </list>
///
/// <para><b>本实现 = 「绑定链回溯」，不是 slack 法</b></para>
/// <list type="number">
///   <item>建边：①同需求内按 `PlannedStartTime` 串接；②跨物料「子件末 Task → 父件首 Task（带 `LagMinutes`）」；
///    ③**同资源**：同一 `ResourceId` 上，结束最晚且 ≤ 当前开工的那个 Task。</item>
///   <item>判「**绑定**」：前驱的 `PlannedEnd + lag` 与当前 `PlannedStart` **相差 ≤ 容差**才算绑定。
///     若所有前驱都明显早于当前开工 ⇒ 当前 Task 是被**未表示为 Task 的约束**（日历/物料/锁）挡住的
///     ⇒ **链在此中断**，继续往前追就是「机械累加等待」，正是 §33 禁止的。</item>
///   <item>结果 = 从**项目终点 Task**（`PlannedEndTime` 最大者）沿绑定边**反向可达**的全部 Task。</item>
/// </list>
///
/// <para><b>为什么不用 slack</b>：实测 PV999597 上 slack 法只挑出 **1/6,426** 条
/// （排程相邻工序常隔数周，其余 Task 都能自由后移而不影响终点）—— 既违反 v1.2 禁令，也没有业务含义。</para>
///
/// <para><b>已知局限（如实声明）</b></para>
/// <list type="bullet">
///   <item>同资源前驱只按「结束最晚且 ≤ 开工」取一个；若资源上存在**多资源并行占用**（本 DTO 不表达），会漏。</item>
///   <item>合批后的 Task 只挂**锚点** `SourceDraftId`，被并入方的跨物料边无法还原（与 EX-T03 同源局限）。</item>
///   <item>跨域父子边不在本图内。</item>
/// </list>
/// </summary>
internal static class CriticalPathCalculator
{
    /// <summary>绑定判定容差：跨窗扣停机、小数时长换算会带来 1 秒内误差。</summary>
    private static readonly TimeSpan BindingTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 计算关键 Task 集合（**绑定链回溯**）。
    /// </summary>
    /// <param name="tasks">已排定的最终 Task（同一 PlanVersion/域）。</param>
    /// <param name="crossMaterialLagMinutes">`(父需求键, 子需求键) → 滞后分钟`，通常取 `ConstraintContext.CrossMaterialLagMinutes`。</param>
    /// <returns>关键 Task 的 <see cref="FinalTaskDraft.FinalDraftId"/> 集合（无 Task 时为空集）。</returns>
    public static IReadOnlySet<string> Compute(
        IReadOnlyList<FinalTaskDraft> tasks,
        IReadOnlyDictionary<(string Parent, string Child), decimal>? crossMaterialLagMinutes = null)
    {
        var critical = new HashSet<string>(StringComparer.Ordinal);
        if (tasks is null || tasks.Count == 0)
        {
            return critical;
        }

        var lags = crossMaterialLagMinutes ?? new Dictionary<(string, string), decimal>();

        // ── 1. 前驱表：taskId → [(predId, lagMinutes)] ───────────────────
        var predecessors = new Dictionary<string, List<(string Pred, decimal LagMinutes)>>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            predecessors[t.FinalDraftId] = new List<(string, decimal)>(4);
        }

        void AddEdge(string predId, string succId, decimal lagMinutes)
        {
            if (string.Equals(predId, succId, StringComparison.Ordinal)) return;
            if (!predecessors.TryGetValue(succId, out var list)) return;
            list.Add((predId, lagMinutes));
        }

        // ① 同需求内：按开工时间串接（`OperationSeq` 契约恒 0，不可用 —— 见 FinalTaskDraft 注释）
        foreach (var g in tasks.GroupBy(t => t.SourceDraftId ?? string.Empty, StringComparer.Ordinal))
        {
            var ordered = g.OrderBy(t => t.PlannedStartTime).ThenBy(t => t.FinalDraftId, StringComparer.Ordinal).ToList();
            for (var i = 0; i + 1 < ordered.Count; i++)
            {
                AddEdge(ordered[i].FinalDraftId, ordered[i + 1].FinalDraftId, 0m);
            }
        }

        // ② 跨物料：子件末 Task → 父件首 Task（带 lag）
        var firstByDemand = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal);
        var lastByDemand = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            var key = t.SourceDraftId ?? string.Empty;
            if (!firstByDemand.TryGetValue(key, out var f) || t.PlannedStartTime < f.PlannedStartTime) firstByDemand[key] = t;
            if (!lastByDemand.TryGetValue(key, out var l) || t.PlannedEndTime > l.PlannedEndTime) lastByDemand[key] = t;
        }
        foreach (var kv in lags)
        {
            if (lastByDemand.TryGetValue(kv.Key.Child ?? string.Empty, out var childLast)
                && firstByDemand.TryGetValue(kv.Key.Parent ?? string.Empty, out var parentFirst))
            {
                AddEdge(childLast.FinalDraftId, parentFirst.FinalDraftId, kv.Value);
            }
        }

        // ③ 同资源：按开工排序，前一个「结束 ≤ 当前开工且结束最晚」的 Task
        foreach (var g in tasks.Where(t => t.ResourceId.HasValue).GroupBy(t => t.ResourceId!.Value))
        {
            var ordered = g.OrderBy(t => t.PlannedStartTime).ThenBy(t => t.PlannedEndTime).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var cur = ordered[i];
                FinalTaskDraft? best = null;
                for (var j = i - 1; j >= 0; j--)
                {
                    if (ordered[j].PlannedEndTime <= cur.PlannedStartTime)
                    {
                        best = ordered[j];
                        break;   // 已按开工排序，继续往前只会更早 ⇒ 取最近的一个
                    }
                }
                if (best is not null)
                {
                    AddEdge(best.FinalDraftId, cur.FinalDraftId, 0m);
                }
            }
        }

        // ── 2. 绑定判定：前驱(End+lag) 与 当前 Start 相差 ≤ 容差 ──────────
        var byId = new Dictionary<string, FinalTaskDraft>(tasks.Count, StringComparer.Ordinal);
        foreach (var t in tasks) byId.TryAdd(t.FinalDraftId, t);

        List<string> BindingPredecessors(FinalTaskDraft task)
        {
            var list = new List<string>(2);
            if (!predecessors.TryGetValue(task.FinalDraftId, out var preds) || preds.Count == 0)
            {
                return list;
            }

            // 先取「最晚满足条件」的那个作为唯一的绑定前驱
            DateTime bestRequired = DateTime.MinValue;
            string? bestId = null;
            foreach (var (predId, lag) in preds)
            {
                if (!byId.TryGetValue(predId, out var pred)) continue;
                var required = pred.PlannedEndTime.AddMinutes((double)lag);
                if (required <= task.PlannedStartTime.Add(BindingTolerance) && required > bestRequired)
                {
                    bestRequired = required;
                    bestId = predId;
                }
            }

            if (bestId is null) return list;

            // 与开工相差在容差内 ⇒ 真正绑定；否则本 Task 被未表示为 Task 的约束挡住 ⇒ 链在此中断
            if ((task.PlannedStartTime - bestRequired).Duration() <= BindingTolerance)
            {
                list.Add(bestId);
            }
            return list;
        }

        // ── 3. 从项目终点 Task 反向回溯 ──────────────────────────────────
        var projectEnd = tasks.Max(t => t.PlannedEndTime);
        var stack = new Stack<string>();
        foreach (var t in tasks)
        {
            if ((t.PlannedEndTime - projectEnd).Duration() <= BindingTolerance && critical.Add(t.FinalDraftId))
            {
                stack.Push(t.FinalDraftId);
            }
        }

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!byId.TryGetValue(id, out var task)) continue;
            foreach (var predId in BindingPredecessors(task))
            {
                if (critical.Add(predId))
                {
                    stack.Push(predId);
                }
            }
        }

        return critical;
    }
}
