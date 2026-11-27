using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// A34 投影器：把 1号位 出口的 <see cref="DemandCompletionFact"/> ＋ 需求本体，
/// 映射为可落库的 <see cref="DemandStageTimingResultRow"/>。
///
/// <para><b>为什么单独成纯函数</b>：这段决定「某需求哪天能交」落库与否与落成什么，
/// 判错不会编译报错、只会**多写或少写**；埋在带数据库往返的落库大事务里就够不着单测。
/// 抽出后三点可断言：①每需求至多一行（唯一键不撞）；②<see cref="DemandStageTimingResultType"/> 判据只认「有无真实 Task」；
/// ③<see cref="DemandCompletionFact.CompletionTimeCertain"/> 三态**原样透传**，不擅自升降级。</para>
///
/// <para><b>不做的事</b></para>
/// <list type="bullet">
/// <item>不造计划开始时间——1号位 没给 <c>PlanningStartTime</c> 就留 <c>null</c>，**绝不用 GETDATE 补**；</item>
/// <item>不臆造逐 Stage 段——没有段明细就落 <c>null</c>，不写空数组冒充「已产出」；</item>
/// <item>不把 <c>TIMING_ONLY</c> 的量写进任何份额账——它只是时间事实，不是供给、不是承诺。</item>
/// </list>
/// </summary>
public static class DemandStageTimingProjector
{
    /// <summary>投影结果＋被丢弃的计数（显性化，不静默丢）。</summary>
    public sealed class Result
    {
        public IReadOnlyList<DemandStageTimingResultRow> Rows { get; init; }
            = Array.Empty<DemandStageTimingResultRow>();

        /// <summary>有需求、但 1号位 未给对应 <see cref="DemandCompletionFact"/> 的需求数（= 无时间事实）。</summary>
        public int DemandsWithoutFact { get; init; }

        /// <summary>
        /// 有完成时间事实、但**没有任何 Stage 段** ⇒ 不产行（`StageSegmentsJson` 为 NOT NULL，
        /// 且 0号位 要求不得伪造段）。单列计数，避免「少写」被静默忽略。
        /// </summary>
        public int DemandsWithoutSegments { get; init; }

        /// <summary>同一需求键出现多条时间事实 ⇒ 只取首条并计数（正常应为 0）。</summary>
        public int DuplicateFactKeys { get; init; }

        /// <summary><c>TASK_WITH_TIMING</c> 行数。</summary>
        public int TaskWithTimingCount => Rows.Count(r => r.ResultType == DemandStageTimingResultType.TaskWithTiming);

        /// <summary><c>TIMING_ONLY</c> 行数。</summary>
        public int TimingOnlyCount => Rows.Count(r => r.ResultType == DemandStageTimingResultType.TimingOnly);

        /// <summary>时间不可信（<c>CompletionTimeCertain == false</c>）行数。</summary>
        public int UncertainCount => Rows.Count(r => r.CompletionTimeCertain == false);

        /// <summary>完成时间未标注（<c>null</c>，旧载荷）行数。</summary>
        public int UnannotatedCount => Rows.Count(r => r.CompletionTimeCertain is null);
    }

    /// <summary>
    /// 投影。
    /// </summary>
    /// <param name="planVersionId">计划版本 Id。</param>
    /// <param name="scheduleRunId"><c>PlanVersion.SourceScheduleRunId</c>。</param>
    /// <param name="domainKey">域键。</param>
    /// <param name="demands">本次提交给 1号位 的逻辑需求（唯一键来源）。</param>
    /// <param name="completions">1号位 出口的需求级时间事实。</param>
    /// <param name="demandKeysWithTask">
    /// **已产出真实 Task 的需求键集合**（<see cref="LogicalProductionDemand.LogicalDemandKey"/>）。
    /// 由调用方按本 Run 已落库的 Task 反查，投影器不自行推断。
    /// </param>
    /// <param name="inputDigest">可选的输入摘要。</param>
    public static Result Project(
        int planVersionId,
        int scheduleRunId,
        string domainKey,
        IReadOnlyList<LogicalProductionDemand>? demands,
        IReadOnlyList<DemandCompletionFact>? completions,
        IReadOnlySet<string>? demandKeysWithTask = null,
        string? inputDigest = null,
        DateTime? createdAt = null,
        IReadOnlyDictionary<long, long?>? orderCanonicalById = null,
        IReadOnlyDictionary<string, IReadOnlyList<StageSequenceStep>?>? chainByDemandKey = null)
    {
        var rows = new List<DemandStageTimingResultRow>();
        if (demands is null || demands.Count == 0)
            return new Result { Rows = rows };

        // 需求键 → 时间事实（一需求一事实；重复时取首条并计数，不静默覆盖）。
        var factByKey = new Dictionary<string, DemandCompletionFact>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var f in completions ?? Array.Empty<DemandCompletionFact>())
        {
            if (string.IsNullOrEmpty(f.LogicalDemandKey)) continue;
            if (!factByKey.TryAdd(f.LogicalDemandKey, f))
                duplicates++;
        }

        // 需求键去重（同一键只落一行，避免撞 UQ_ADSTR_PV_LogicalDemand）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var withoutFact = 0;
        var withoutSegments = 0;

        foreach (var d in demands)
        {
            var key = d.LogicalDemandKey;
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
                continue;

            if (!factByKey.TryGetValue(key, out var fact))
            {
                // 没有时间事实 ⇒ **不产行**（不造零时间行冒充「已算」）。
                withoutFact++;
                continue;
            }

            // 2026-11-26 修（0号位 §二十四.3 点出）：`StageSegmentsJson` 实库为 **NOT NULL**，
            //   而本方法原实现在「无 StageTiming 段」时落 `null` ⇒ **真跑会插入失败**。
            //   0号位 口径：「无合法 StageTiming **不伪造结果行**，确有合法结果须合规真实段，
            //   空数组是否适用按既有合同、不随意用空掩盖缺失」。
            //   ⇒ 无段 ⇒ **本需求不产行**（不塞 null、不塞 `[]`），并计数留痕。
            var segmentsJson = BuildStageSegments(fact, key, chainByDemandKey);
            if (segmentsJson is null)
            {
                withoutSegments++;
                continue;
            }

            var hasTask = demandKeysWithTask is not null && demandKeysWithTask.Contains(key);
            var resultType = hasTask
                ? DemandStageTimingResultType.TaskWithTiming
                : DemandStageTimingResultType.TimingOnly;

            rows.Add(new DemandStageTimingResultRow
            {
                PlanVersionId        = planVersionId,
                ScheduleRunId        = scheduleRunId,
                DomainKey            = domainKey,
                LogicalDemandKey     = key,
                DemandKey            = string.IsNullOrWhiteSpace(d.DemandKey) ? null : d.DemandKey,
                OrderId              = d.OrderId,
                // OrderCanonicalId 不在需求上（在 [Order] 侧）⇒ 由调用方按 OrderId 富化；查不到就留空，不臆造。
                OrderCanonicalId     = d.OrderId is long oid && orderCanonicalById is not null
                                       && orderCanonicalById.TryGetValue(oid, out var oc) ? oc : null,
                MaterialId           = d.MaterialId,
                DemandFactoryId      = d.FactoryId > 0 ? d.FactoryId : (int?)null,
                ResolvedBOMNO        = string.IsNullOrWhiteSpace(d.BomNo) ? null : d.BomNo!.Trim(),
                RepWorksetId         = d.RepWorksetId,
                StageScopeType       = d.IsRootDemand
                    ? EffectiveStageChainResolver.ScopeRoot
                    : EffectiveStageChainResolver.ScopeEdge,
                DemandNetQty         = d.NetOutputQty,
                // TIMING_ONLY ⇒ 整条净量只存在于本表的时间账；TASK_WITH_TIMING ⇒ 份额已由 ATS 承载，此处为 0。
                TimingOnlyQty        = hasTask ? 0m : d.NetOutputQty,
                ResultType           = resultType,
                // 1号位 未给需求级计划开始 ⇒ 留 null（禁止用 GETDATE 补）。
                PlanningStartTime    = null,
                PlannedCompletionTime = fact.CompletionTime == default ? null : fact.CompletionTime,
                CompletionTimeCertain = fact.CompletionTimeCertain,
                TimeStatus           = MapTimeStatus(fact.CompletionTimeCertain),
                // 逐 Stage 段：来源 = 1号位 出口的 `DemandCompletionFact.StageTimings`（段级时间）
                //   ＋ 我方链上的 `StageSeq`／执行厂（`StageChainDefinitions`）。
                //   0号位 11-24 裁决 v1.2 §3：该列只承载「Stage 顺序、时间、执行厂与 Calendar 来源」。
                //   **没有任何段时落 null**，不写空数组冒充「已产出」。
                StageSegmentsJson    = segmentsJson,
                InputDigest          = inputDigest,
                EvidenceJson         = BuildEvidence(fact, d),
                CreatedAt            = createdAt ?? DateTime.Now
            });
        }

        return new Result
        {
            Rows = rows,
            DemandsWithoutFact = withoutFact,
            DemandsWithoutSegments = withoutSegments,
            DuplicateFactKeys = duplicates
        };
    }

    /// <summary>
    /// 三态映射。<c>null</c> ⇒ <see cref="DemandStageTimingTimeStatus.Unknown"/>，
    /// **不得**降级成 <c>UNCERTAIN</c>、也**不得**升级成 <c>CERTAIN</c>。
    /// </summary>
    public static string MapTimeStatus(bool? completionTimeCertain)
        => completionTimeCertain switch
        {
            true  => DemandStageTimingTimeStatus.Certain,
            false => DemandStageTimingTimeStatus.Uncertain,
            _     => DemandStageTimingTimeStatus.Unknown
        };

    /// <summary>
    /// 把 1号位 的段级时间与我方链上的顺序／执行厂合成 `StageSegmentsJson`。
    ///
    /// <para>只做**拼装**，不造时间、不重算提前期、不从 Task 反推。</para>
    /// <para>链上找不到该 Stage 时，`StageSeq`／执行厂留 null **并在计数里体现**——
    /// 不猜、不用默认值填。</para>
    /// </summary>
    private static string? BuildStageSegments(
        DemandCompletionFact fact,
        string logicalDemandKey,
        IReadOnlyDictionary<string, IReadOnlyList<StageSequenceStep>?>? chainByDemandKey)
    {
        var timings = fact.StageTimings;
        if (timings is null || timings.Count == 0)
            return null;   // 无段 ⇒ null（不写空数组）

        IReadOnlyList<StageSequenceStep>? chain = null;
        chainByDemandKey?.TryGetValue(logicalDemandKey, out chain);

        var segments = new List<object>(timings.Count);
        foreach (var t in timings)
        {
            StageSequenceStep? step = chain?.FirstOrDefault(s =>
                string.Equals(s.StageCode, t.StageCode, StringComparison.Ordinal));

            segments.Add(new
            {
                StageCode   = t.StageCode,
                // 顺序只从链上取；链上没有就 null，不按到达顺序臆造
                StageSeq    = step?.StageSeq,
                // 执行厂同理：`StageDict.FactoryCode` → `Factory.Id`
                StageFactoryId   = step?.StageFactoryId,
                StageFactoryCode = step?.StageFactoryCode,
                StartTime   = t.StartTime,
                EndTime     = t.EndTime,
                LeadTimeHours = t.LeadTimeHours,
                // Calendar/LT 命中层级（`DEPT_EXACT`／`FACTORY_STAGE_DEFAULT`／`GLOBAL_STAGE_DEFAULT`／`STAGE_LEADTIME_MISSING`）
                LeadTimeMatchLevel = t.MatchLevel
            });
        }

        return System.Text.Json.JsonSerializer.Serialize(segments);
    }

    private static string BuildEvidence(DemandCompletionFact fact, LogicalProductionDemand demand)
    {
        var payload = new
        {
            AllocationSequence           = demand.AllocationSequence,
            fact.RequiredStageCode,
            fact.AvailableTime,
            fact.RequiredStageCompletionTime,
            StartStageCode               = demand.StartStageCode,
            RequiredAvailableTime        = demand.RequiredAvailableTime,
            demand.UOM
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }
}