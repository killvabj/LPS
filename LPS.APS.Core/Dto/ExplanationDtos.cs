namespace LPS.APS.Core.Dto;

/// <summary>
/// 排程原因解释DTO（Explanation：5号位提供给4号位）
///
/// 数据来源：ScheduleExplanationFact表（1号位产出，2号位落盘）
/// 5号位只读取展示，不重新裁决延期原因
/// </summary>
public sealed class ExplanationDto
{
    public long Id { get; init; }
    public int PlanVersionId { get; init; }
    public int? ScheduleRunId { get; init; }

    /// <summary>对象类型：ORDER / TASK / RESOURCE / STAGE / DOMAIN</summary>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>关联订单ID</summary>
    public long? OrderId { get; init; }

    /// <summary>关联任务ID</summary>
    public long? TaskId { get; init; }

    /// <summary>关联资源ID</summary>
    public int? ResourceId { get; init; }

    /// <summary>关联Stage代码</summary>
    public string? StageCode { get; init; }

    /// <summary>结构化原因码</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>严重级别：INFO / WARN / ERROR</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>影响工时（小时）</summary>
    public decimal? ImpactHours { get; init; }

    /// <summary>证据JSON（结构化详情）</summary>
    public string? EvidenceJson { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Explanation查询汇总DTO
/// </summary>
public sealed class ExplanationSummaryDto
{
    /// <summary>PlanVersionId</summary>
    public int PlanVersionId { get; init; }

    /// <summary>总Explanation数</summary>
    public int TotalCount { get; init; }

    /// <summary>按严重级别统计</summary>
    public int ErrorCount { get; init; }
    public int WarnCount { get; init; }
    public int InfoCount { get; init; }

    /// <summary>按ReasonCode分组统计</summary>
    public List<ReasonCodeCountDto> ReasonCodeCounts { get; init; } = new();

    /// <summary>Explanation列表</summary>
    public List<ExplanationDto> Items { get; init; } = new();
}

/// <summary>
/// ReasonCode统计
/// </summary>
public sealed class ReasonCodeCountDto
{
    public string ReasonCode { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal? TotalImpactHours { get; init; }
}

/// <summary>
/// Demand 数量对账读模型行（**FULL-06**，0号位 2026-11-09 任务书 §2.A：5读、2写、1算）。
///
/// 源=`ScheduleExplanationFact` 对账族三行族，判据=证据键 `UnscheduledSource`
/// （`UNSCHEDULED_TASK` 1号位未排回执／`RECONCILIATION_REMAINDER` 对账剩余量／`RECONCILIATION_FULLY_SCHEDULED` 已排满正向证据）。
/// **仅按 ObjectType='ORDER'+TaskId IS NULL 过滤会混入 1号位 其他诊断族**（实测 999464：2,390 行
/// `evidenceType=PI_FREE_OVER_COMMIT/EXECUTION_OVER_COMMIT` 无本键，属不同事实类型，不得合并累计）。
///
/// 读侧纪律：Q/S/U **原样透出，不重算、不用 min(S,Q)/max(Q−S,0) 掩盖超排**——
/// 实测 999464 有 211 行 S&gt;Q（全部落 FULLY_SCHEDULED/INFO 族，Q≠S+U），修数是 2号位 FULL-01 的职责，
/// 本读模型只保证"API 与 2 写入原始逐行一致"。四类状态自足可分：无对账记录=无行；有零未排=INFO 族；
/// 未排原因缺口=ReasonCodeGap；技术失败=该行不落（Run 级事实另行核对）。
/// </summary>
public sealed class DemandReconciliationRowDto
{
    public long Id { get; init; }
    public int PlanVersionId { get; init; }
    public int? ScheduleRunId { get; init; }
    public long? OrderId { get; init; }
    public string? StageCode { get; init; }

    /// <summary>1号位 给的冻结 15 码内原因（列原值；空=未给/越域/已排满——见 ReasonCodeGap/UpstreamReasonCode 区分）</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>INFO / WARN / ERROR（ERROR⇒确有未排净量）</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>需求身份键 `{PlanVersionId}_{AllocationSequence}`（含 /WO、/FREE 分片后缀形态，原样）</summary>
    public string? LogicalDemandKey { get; init; }

    /// <summary>物料 Id（UNSCHEDULED_TASK 族 demand 未匹配时可 NULL）</summary>
    public int? MaterialId { get; init; }

    /// <summary>域标识（写侧 voucher.DomainKey）</summary>
    public string? DomainKey { get; init; }

    public int? AllocationSequence { get; init; }

    /// <summary>Q＝需求净产出量（NetOutputQty，落库原文）</summary>
    public decimal? NetOutputQty { get; init; }

    /// <summary>S＝已排份额（ΣAllocationTaskShare.ShareQty，同 LogicalDemandKey，落库原文）</summary>
    public decimal? ScheduledShareQty { get; init; }

    /// <summary>U＝写侧落库的未排净量原文（写式=max(Q−S,0)；S&gt;Q 时 U=0 而 Q≠S+U——由本行 Q/S 原值显形，读侧不修）</summary>
    public decimal? UnscheduledQty { get; init; }

    /// <summary>事实类型判别位（三族之一，定义见类型注释）</summary>
    public string? UnscheduledSource { get; init; }

    /// <summary>部分已排却被 1号位 列为未排程（V1 整单记账假定被打破的显性化）；非本键族=NULL</summary>
    public bool? OverlapDetected { get; init; }

    /// <summary>未排原因缺口标记（只在确有未排净量时成立；U=0 已排满不是缺口）</summary>
    public bool? ReasonCodeGap { get; init; }

    /// <summary>域外/失效原因原值留痕（非改码非映射；NULL=无留痕）</summary>
    public string? UpstreamReasonCode { get; init; }

    public DateTime CreatedAt { get; init; }
}
