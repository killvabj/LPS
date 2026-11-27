using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// A35 投影器：把**真正判不明身份**的需求，映射为可落库的
/// <see cref="DemandScheduleTechnicalIssueRow"/>。
///
/// <para><b>只收一种</b>：<see cref="DemandStageChainBinding.Status"/> ＝
/// <see cref="StageChainBindingStatus.Conflict"/> 的绑定——
/// 即「请求实例未经证明、且候选链内容不一致 ⇒ 本需求不出链」的 Fail-Closed 结果。</para>
///
/// <para><b>为什么不收别的原因</b>（0号位 2026-11-24 裁决 v1.1 §3 / v1.2 §6 逐字）：
/// 「289 条 RoleConflict <b>不等于</b> 289 条同型号歧义。逐 Demand 按合法 Workset 选出链后应恢复正常需求；
/// <b>只有真实身份不可判明的具体边</b>记技术 Issue，不得影响其它订单。」
/// ⇒ <c>NO_CHAIN</c>（本请求无链）、<c>NO_ANCHOR</c>（三键全空）、<c>NO_ROWS</c>（本批无该物料行）都<b>不是</b>技术异常：
/// 前两者是数据缺口、后者是采购件类的合法形态，收进来就是把「缺数据」误标成「出问题」。</para>
///
/// <para><b>证据里不复制数量</b>：裁决要求「不可承诺数量<b>引用</b>」而非复制第二份权威账
/// ⇒ 只写引用出处，不写数值。权威数量仍在 LPD／未排账里。</para>
/// </summary>
public static class DemandTechnicalIssueProjector
{
    /// <summary>投影结果。</summary>
    public sealed class Result
    {
        public IReadOnlyList<DemandScheduleTechnicalIssueRow> Rows { get; init; }
            = Array.Empty<DemandScheduleTechnicalIssueRow>();

        /// <summary>绑定状态为 CONFLICT 的需求数（＝本次应收的技术异常数）。</summary>
        public int ConflictDemandCount { get; init; }

        /// <summary>CONFLICT 绑定在需求表里找不到对应需求 ⇒ 未产行（正常应为 0）。</summary>
        public int ConflictWithoutDemand { get; init; }

        /// <summary>
        /// 「同需求同形状键但**多条真实父边**」被合并成一行组数。
        /// 这些边**没有丢**——全部写进了该行的 `EvidenceJson.ChainRefIds`；
        /// 该计数用于让合并可被察觉（0 = 没发生合并）。
        /// </summary>
        public int MultiEdgeCollapsedCount { get; init; }

        /// <summary>上述被合并的边总数（含每组的第一条）。</summary>
        public int CollapsedEdgeTotal { get; init; }
    }

    /// <summary>
    /// 投影。
    /// </summary>
    /// <param name="planVersionId">计划版本 Id。</param>
    /// <param name="scheduleRunId"><c>PlanVersion.SourceScheduleRunId</c>。</param>
    /// <param name="domainKey">域键。</param>
    /// <param name="demands">本次需求（提供 OrderId／MaterialId／DemandKey 等身份列）。</param>
    /// <param name="bindings">逐需求绑定（<see cref="DemandStageChainBinding"/>）。</param>
    /// <param name="createdAt">创建时间（BulkCopy 未映射列取不到列默认值，须显式给）。</param>
    public static Result Project(
        int planVersionId,
        int scheduleRunId,
        string domainKey,
        IReadOnlyList<LogicalProductionDemand>? demands,
        IReadOnlyList<DemandStageChainBinding>? bindings,
        DateTime? createdAt = null)
    {
        var rows = new List<DemandScheduleTechnicalIssueRow>();
        if (bindings is null || bindings.Count == 0)
            return new Result { Rows = rows };

        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal);
        foreach (var d in demands ?? Array.Empty<LogicalProductionDemand>())
            if (!string.IsNullOrEmpty(d.LogicalDemandKey))
                demandByKey.TryAdd(d.LogicalDemandKey, d);

        var conflictCount = 0;
        var withoutDemand = 0;
        var now = createdAt ?? DateTime.Now;
        // 唯一键 (PV, 需求键, 绑定键, 技术码) 去重——同一需求若出现多条同键绑定，只落一行。
        // 0号位 v1.20 §二十四.2/§二十五.3：同一 `LogicalDemandKey` 下**多条真实父边**可能共享
        //   同一形状键 `BindingKey = scope|BOMNO|RepWorksetId`（该键不含父件）。
        //   若按 `(需求键, BindingKey)` 去重并**静默丢弃**，就等于丢掉另一条边的异常事实。
        //   但 DB 唯一键 `(PV, 需求键, BindingKey, IssueCode)` 不含父件 ⇒ 也**不能拆成两行**（会插入失败）。
        //   ⇒ 定：**一行保留，把该键下所有 ChainRefId 与候选签名全部写进 EvidenceJson**，并计数留痕。
        var collapsed = new Dictionary<(string, string?), EdgeAccumulator>();

        // ① 先按「实际唯一键」聚合：同一 (需求键, 形状键) 的多条父边**全部收进同一个累加器**。
        //   行必须**聚合完成后再建**——否则先建的行里证据只有第一条边（本实现上一版即栽在这一点）。
        var demandByGroup = new Dictionary<(string, string?), LogicalProductionDemand>();

        foreach (var b in bindings)
        {
            if (!string.Equals(b.Status, StageChainBindingStatus.Conflict, StringComparison.Ordinal))
                continue;

            conflictCount++;

            if (string.IsNullOrEmpty(b.LogicalDemandKey)
                || !demandByKey.TryGetValue(b.LogicalDemandKey, out var demand))
            {
                withoutDemand++;
                continue;
            }

            var groupKey = (b.LogicalDemandKey, b.BindingKey);
            if (collapsed.TryGetValue(groupKey, out var acc))
            {
                acc.Add(b);   // 同形状键的另一条父边：累积证据，不新起行
                continue;
            }

            collapsed[groupKey] = new EdgeAccumulator(b);
            demandByGroup[groupKey] = demand;
        }

        // ② 聚合完成后再出
        foreach (var (groupKey, acc) in collapsed)
        {
            var b = acc.First;
            var demand = demandByGroup[groupKey];

            rows.Add(new DemandScheduleTechnicalIssueRow
            {
                PlanVersionId      = planVersionId,
                ScheduleRunId      = scheduleRunId,
                DomainKey          = domainKey,
                LogicalDemandKey   = b.LogicalDemandKey,
                BindingKey         = b.BindingKey,
                DemandKey          = string.IsNullOrWhiteSpace(demand.DemandKey) ? null : demand.DemandKey,
                OrderId            = demand.OrderId,
                MaterialId         = demand.MaterialId > 0 ? demand.MaterialId : (int?)null,
                StageScopeType     = b.StageScopeType,
                ResolvedBOMNO      = b.ResolvedBOMNO,
                RepWorksetId       = b.RepWorksetId,
                // 0号位 v1.20 §二十四.3：两类既定技术码不得被自造码取代，且「合法不同订单不同链」不得误标。
                //   · 真实**需求级**角色冲突 = 同一需求被挂上**不同 StageScopeType** 的绑定
                //     （`ScopeOf` 只取 `IsRootDemand`，故正常恒同 scope ⇒ 本码当前**恒为 0，属防御性**）；
                //   · 其余身份歧义仍记 `StageChainIdentityAmbiguous`——**该码属自造，已列待 0号位 裁定**。
                // 优先序（0号位 v1.20 §3）：CAST/MACH 实体歧义 > 角色冲突 > 第三码。
                IssueCode          = HasCastMachAmbiguity(acc)
                    ? DemandScheduleIssueCode.MaterialIdentityAmbiguousCastMach
                    : acc.Scopes.Count > 1
                        ? DemandScheduleIssueCode.StageChainRoleConflict
                        : DemandScheduleIssueCode.StageChainIdentityAmbiguous,
                // Fail-Closed：本需求确实排不出来 ⇒ ERROR（WARN 会被当成"可忽略"）。
                Severity           = "ERROR",
                // 0号位 已裁：显式赋 DETECTED，不靠列默认值、不加 CHECK。
                IssueStatus        = DemandScheduleIssueStatus.Detected,
                EvidenceJson       = BuildEvidence(acc, demand),
                CreatedAt          = now
            });
        }

        return new Result
        {
            Rows = rows,
            ConflictDemandCount = conflictCount,
            ConflictWithoutDemand = withoutDemand,
            MultiEdgeCollapsedCount = collapsed.Values.Count(a => a.EdgeCount > 1),
            CollapsedEdgeTotal = collapsed.Values.Where(a => a.EdgeCount > 1).Sum(a => a.EdgeCount)
        };
    }

    /// <summary>
    /// 证据：候选链（构成歧义的那些路径）、原始请求键、拒绝原因。
    /// **不写数量**——裁决要求引用出处而非复制第二份权威账。
    /// </summary>
    /// <summary>
    /// 同一形状键下的**多条真实父边**。唯一键装不下父件，故把边身份全收在证据里。
    /// </summary>
    private sealed class EdgeAccumulator
    {
        private readonly DemandStageChainBinding _first;
        private readonly List<string> _chainRefIds = new();
        private readonly List<string> _signatures = new();

        public EdgeAccumulator(DemandStageChainBinding first)
        {
            _first = first;
            Add(first);

        }

        public void Add(DemandStageChainBinding b)
        {
            EdgeCount++;
            if (!string.IsNullOrEmpty(b.StageScopeType)) Scopes.Add(b.StageScopeType);
            if (!string.IsNullOrEmpty(b.ChainRefId) && !_chainRefIds.Contains(b.ChainRefId!))
                _chainRefIds.Add(b.ChainRefId!);
            foreach (var sig in b.CandidateChainSignatures ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(sig) && !_signatures.Contains(sig))
                    _signatures.Add(sig);
        }

        public int EdgeCount { get; private set; }

        /// <summary>组内出现过的 StageScopeType 集合（防御性：正常恒为 1 个）。</summary>
        public HashSet<string> Scopes { get; } = new(StringComparer.Ordinal);
        public DemandStageChainBinding First => _first;
        public IReadOnlyList<string> ChainRefIds => _chainRefIds;
        public IReadOnlyList<string> CandidateSignatures => _signatures;
    }

    /// <summary>
    /// 同型号实体歧义判据（0号位 11-24 v1.2 §6）：同一物料的候选链**分别以 CAST 阶段与 MACH 阶段为首**
    /// ⇒ 同一 ERP 物料号同时表达 CAST 半成品与 MACH 完成品。
    ///
    /// <para>只按**候选链首阶段**判定：单条链内「CAST→MACH」是正常工艺顺序，不构成本歧义；
    /// 必须两条**不同**候选链的链首分属 CAST 与 MACH 两族。</para>
    /// </summary>
    private static bool HasCastMachAmbiguity(EdgeAccumulator acc)
    {
        var sigs = acc.CandidateSignatures;
        if (sigs.Count < 2) return false;

        static string FirstStage(string sig)
        {
            var first = sig.Split('|', 2)[0];
            var idx = first.IndexOf(':');
            return idx >= 0 ? first[(idx + 1)..] : first;
        }

        var firsts = sigs.Select(FirstStage).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hasCast = firsts.Any(f => f.IndexOf("CAST", StringComparison.OrdinalIgnoreCase) >= 0);
        var hasMach = firsts.Any(f => f.IndexOf("MACH", StringComparison.OrdinalIgnoreCase) >= 0);
        return hasCast && hasMach;
    }

    private static string BuildEvidence(EdgeAccumulator acc, LogicalProductionDemand demand)
    {
        var binding = acc.First;
        var payload = new
        {
            Reason = "请求实例未经证明（非 ANCHORED 身份）且候选链内容不一致 ⇒ 不出链（J16-01 准入），"
                   + "该需求不产 Task、按未排真相计入 U",
            CandidateChainSignatures = acc.CandidateSignatures.Count > 0 ? acc.CandidateSignatures : binding.CandidateChainSignatures,
            CandidateChainCount = acc.CandidateSignatures.Count > 0 ? acc.CandidateSignatures.Count : binding.CandidateChainCount,
            // 同一形状键下的**全部真实父边追溯键**（唯一键不含父件，故必须在这里保住）
            ChainRefIds = acc.ChainRefIds,
            EdgeCount = acc.EdgeCount,
            // 真实父子边追溯键：`BindingKey`（作用域|BOMNO|RepWorksetId）**不含父件**，
            //   而 EDGE 的 (BOMNO,子件) 有 25.7% 挂多父件 ⇒ 单靠它在多条边之间分不开。
            //   `ChainRefId` 的实例键含父件（EffectiveStageChainResolver.InstanceKey），能唯一定位边。
            //   放在 EvidenceJson 里是为了**不改表结构**（加列属 DDL 变更，没必要为此走裁决）。
            ChainRefId = binding.ChainRefId,   // 兼容单边场景（EdgeCount=1 时与 ChainRefIds[0] 同值）
            binding.StageScopeType,
            binding.ResolvedBOMNO,
            binding.RepWorksetId,
            demand.RequestDetailId,
            demand.StartStageCode,
            demand.RequiredStageCode,
            // 数量只给**引用出处**，不给值：权威账在 LPD／未排账，本表不复制。
            UncommittedQtyRef = "LogicalProductionDemand.NetOutputQty（权威账不复制）"
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }
}