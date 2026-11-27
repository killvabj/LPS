using System;

namespace LPS.APS.Core.Dto;

/// <summary>
/// A34 落库行：<c>dbo.APS_DemandStageTimingResult</c>（每 <c>(PlanVersionId, LogicalDemandKey)</c> 至多一行）。
///
/// <para><b>定位</b>：把 1号位 出口的 <see cref="DemandCompletionFact"/>（需求级时间事实）持久化下来。
/// 此前该事实**只在内存、从不落库**——只在 <c>PeggingOrchestrator</c> 里聚合成一条计数型解释记录，
/// Application／Engine／Web 零消费 ⇒ 排完即失，查不到「某需求哪天能交」。</para>
///
/// <para><b>载体归属</b>：<c>LPS.APS.Core</c> ＝ 2号位 辖区。1号位 填时间值，2号位 落载体与读侧。</para>
///
/// <para><b>口径（0号位 2026-11-24 裁决 v1.2 §二）</b></para>
/// <list type="bullet">
/// <item>计划时间一律 <c>DATETIME2(7)</c>，不得按秒截断；</item>
/// <item><c>CreatedAt</c> 用 <c>DATETIME2(7) DEFAULT GETDATE()</c>（DB 本地）；**不得**由 GETDATE 造假计划时间；</item>
/// <item><c>TIMING_ONLY</c> 非 Task／非 MES／非承诺供给，其净量**不得**与 Share／Unscheduled 重复计；</item>
/// <item>不加外键——生命周期由 <c>PlanVersionId</c> 随 PV 清理保证。</item>
/// </list>
/// </summary>
public sealed class DemandStageTimingResultRow
{

    public int PlanVersionId { get; init; }

    /// <summary>＝ <c>PlanVersion.SourceScheduleRunId</c>。</summary>
    public int ScheduleRunId { get; init; }

    public string DomainKey { get; init; } = string.Empty;

    /// <summary>需求键（<c>{PlanVersionId}_{AllocationSequence}</c>）。**唯一键成分**。</summary>
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>业务稳定键 <c>ORDER_{OrderId}_{MaterialCode}_{FactoryId}</c>（可空）。</summary>
    public string? DemandKey { get; init; }

    public long? OrderId { get; init; }
    public long? OrderCanonicalId { get; init; }
    public int MaterialId { get; init; }
    public int? DemandFactoryId { get; init; }
    public string? ResolvedBOMNO { get; init; }
    public long? RepWorksetId { get; init; }

    /// <summary><c>ROOT</c> / <c>EDGE</c>（取自需求角色，非链路推断）。</summary>
    public string? StageScopeType { get; init; }

    /// <summary>需求净产出量 Q。</summary>
    public decimal DemandNetQty { get; init; }

    /// <summary>
    /// 仅计时量（无任何真实 Task 承接的那部分）。
    /// <c>TIMING_ONLY</c> 行 = 整条净量；<c>TASK_WITH_TIMING</c> 行 = 0（份额已由 AllocationTaskShare 承载）。
    /// </summary>
    public decimal TimingOnlyQty { get; init; }

    /// <summary><see cref="DemandStageTimingResultType"/>。</summary>
    public string ResultType { get; init; } = DemandStageTimingResultType.TimingOnly;

    /// <summary>计划开始时间。<c>null</c> ⇒ 未产出（**不得**用 GETDATE 补）。</summary>
    public DateTime? PlanningStartTime { get; init; }

    /// <summary>计划完成时间 = <see cref="DemandCompletionFact.CompletionTime"/>。</summary>
    public DateTime? PlannedCompletionTime { get; init; }

    /// <summary>
    /// 完成时间是否可信。<c>null</c> ＝ 1号位 未标注（旧载荷兼容），按原样透传、**不改写**。
    /// </summary>
    public bool? CompletionTimeCertain { get; init; }

    /// <summary><see cref="DemandStageTimingTimeStatus"/>。</summary>
    public string TimeStatus { get; init; } = DemandStageTimingTimeStatus.Unknown;

    /// <summary>
    /// Stage 段 JSON（Stage 顺序／时间／厂／日历）。
    /// V1 只承载需求级事实：1号位 当前仅给「需求级完成时间」与「做到 RequiredStage 的完成时间」，
    /// **没有逐 Stage 段明细** ⇒ 无可用段时落 <c>null</c>（不造假空数组、不臆造段）。
    /// </summary>
    public string? StageSegmentsJson { get; init; }

    /// <summary>输入摘要（可选，SHA256 十六进制 64 位）。</summary>
    public string? InputDigest { get; init; }

    /// <summary>证据 JSON（可选）。</summary>
    public string? EvidenceJson { get; init; }

    /// <summary>
    /// 创建时间。
    /// 列默认值为 <c>DEFAULT GETDATE()</c>（DB 本地）；但本行由 <c>SqlBulkCopy</c> 写入，
    /// **未映射的列取不到默认值** ⇒ 由调用方显式赋值。应用与库同主机，取值等价于 DB 本地时间。
    /// </summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary><c>APS_DemandStageTimingResult.ResultType</c> 取值。</summary>
public static class DemandStageTimingResultType
{
    /// <summary>有真实 Task 承接，时间来自 Task 链。</summary>
    public const string TaskWithTiming = "TASK_WITH_TIMING";

    /// <summary>无真实 Task（无 Routing Stage 的纯时间）。**不是供给、不是承诺。**</summary>
    public const string TimingOnly = "TIMING_ONLY";
}

/// <summary><c>APS_DemandStageTimingResult.TimeStatus</c> 取值（由 1号位 三态标注映射）。</summary>
public static class DemandStageTimingTimeStatus
{
    /// <summary><c>CompletionTimeCertain = true</c>：时间由真实事实推出，可作承诺／供给可用时间。</summary>
    public const string Certain = "CERTAIN";

    /// <summary><c>false</c>：缺 LT／缺合法 WORK 窗口 ⇒ 不可作承诺，不可作供给可用时间。</summary>
    public const string Uncertain = "UNCERTAIN";

    /// <summary><c>null</c>：1号位 未标注（旧载荷）⇒ 按现状，不擅自升降级。</summary>
    public const string Unknown = "UNKNOWN";
}