using System;

namespace LPS.APS.Core.Dto;

/// <summary>
/// A34 读侧视图：<c>dbo.APS_DemandStageTimingResult</c> 的查询返回行。
///
/// <para><b>为什么单独一个类型</b>：写侧实体 <see cref="DemandStageTimingResultRow"/> **不带 `Id`**
/// （`SqlBulkCopy` 不能写自增列），而读侧需要 `Id` 做分页/追溯 ⇒ 两者字段集合必须不同，
/// 共用一个类型就会在写侧把 `Id` 带进去、在运行时炸。</para>
/// </summary>
public sealed class DemandStageTimingResultView
{
    public long Id { get; init; }
    public int PlanVersionId { get; init; }
    public int ScheduleRunId { get; init; }
    public string DomainKey { get; init; } = string.Empty;
    public string LogicalDemandKey { get; init; } = string.Empty;
    public string? DemandKey { get; init; }
    public long? OrderId { get; init; }
    public long? OrderCanonicalId { get; init; }
    public int MaterialId { get; init; }
    public int? DemandFactoryId { get; init; }
    public string? ResolvedBOMNO { get; init; }
    public long? RepWorksetId { get; init; }
    public string? StageScopeType { get; init; }
    public decimal DemandNetQty { get; init; }
    public decimal TimingOnlyQty { get; init; }
    public string ResultType { get; init; } = string.Empty;
    public DateTime? PlanningStartTime { get; init; }
    public DateTime? PlannedCompletionTime { get; init; }
    public bool? CompletionTimeCertain { get; init; }
    public string TimeStatus { get; init; } = string.Empty;
    public string? StageSegmentsJson { get; init; }
    public string? InputDigest { get; init; }
    public string? EvidenceJson { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// 该行的时间**能否当承诺/供给可用时间用**。
    /// 只有 <c>CompletionTimeCertain == true</c> 才算数；<c>false</c> 与 <c>null</c> 一律不可当承诺
    /// （<c>null</c> ＝ 1号位 未标注的旧载荷，按现状处理，**不得**由读侧擅自升级）。
    /// </summary>
    public bool IsUsableAsCommitment => CompletionTimeCertain == true;
}

/// <summary>A35 读侧视图：<c>dbo.APS_DemandScheduleTechnicalIssue</c> 的查询返回行。</summary>
public sealed class DemandScheduleTechnicalIssueView
{
    public long Id { get; init; }
    public int PlanVersionId { get; init; }
    public int ScheduleRunId { get; init; }
    public string DomainKey { get; init; } = string.Empty;
    public string LogicalDemandKey { get; init; } = string.Empty;
    public string? BindingKey { get; init; }
    public string? DemandKey { get; init; }
    public long? OrderId { get; init; }
    public int? MaterialId { get; init; }
    public string? StageScopeType { get; init; }
    public string? ResolvedBOMNO { get; init; }
    public long? RepWorksetId { get; init; }
    public string IssueCode { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string IssueStatus { get; init; } = string.Empty;
    public string? EvidenceJson { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// 读侧分页结果：**必须让调用方知道"还有没有"**。
///
/// <para><b>为什么需要它</b>（0号位 v1.20 §二十四.3）：原实现给 `take` 设 5,000 硬上限，
/// 超出的行被**静默截断**——调用方拿到 5,000 条会误以为这就是全部。
/// 「5,000 条上限」本身可以保留（防一次拉穿），但**截断必须可感知**。</para>
///
/// <para><b>怎么做到不丢行</b>：内部多取 1 条用于探测；`HasMore = true` 时，
/// 调用方用 `NextSkip = Skip + Rows.Count` 继续翻页，直到 `HasMore = false`。
/// 不额外跑 `COUNT(*)`（A34 可达数十万行，全量计数代价高且与翻页无关）。</para>
/// </summary>
public sealed class DemandResultPage<T>
{
    /// <summary>本页数据（最多 <c>take</c> 条）。</summary>
    public IReadOnlyList<T> Rows { get; init; } = Array.Empty<T>();

    /// <summary>本次请求的偏移量（原样回显，便于拼下一页）。</summary>
    public int Skip { get; init; }

    /// <summary>**是否还有更多行**。false ＝ 已到末尾，本次结果是完整的。</summary>
    public bool HasMore { get; init; }

    /// <summary>下一页应传的 <c>skip</c>（＝ <see cref="Skip"/> ＋ 本页行数）。</summary>
    public int NextSkip => Skip + Rows.Count;

    /// <summary>本页是否因 <c>take</c> 上限而被截断（等价于 <see cref="HasMore"/>，语义更直白）。</summary>
    public bool Truncated => HasMore;
}
