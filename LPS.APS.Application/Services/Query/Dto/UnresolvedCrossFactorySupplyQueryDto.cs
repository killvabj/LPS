namespace LPS.APS.Application.Services.Query.Dto;

/// <summary>
/// 跨厂未闭合净缺口查询结果（CFV1-08，2026-11-13）。
///
/// <para><b>字段与冻结同名同义</b>：`07 v5.1.17` 字段合同 ＋ `08 DDL v5.1.8.11` 表定义。
/// 本 DTO 只做**原样投影**，不做任何推算、不补默认值、不猜源厂或需用时间。</para>
///
/// <para><b>为什么要有 <see cref="ResultState"/></b>：`06 v1.42` 第 5 条与 `13 v1.9` 都明令
/// 「**无记录的可信零缺口** 与 **接口不可用／装载失败** 必须区别」——
/// 不能把查询失败显示成「没有缺口」。故：</para>
/// <list type="bullet">
///   <item><see cref="ResultState"/> = <c>OK</c> ⇒ 查询**确实执行成功**；
///         <see cref="ItemCount"/> = 0 才是**可信的零缺口**。</item>
///   <item><see cref="ResultState"/> = <c>QUERY_FAILED</c> ⇒ 查询**没跑成**，此时
///         <see cref="ItemCount"/> 恒 0 但**不代表没有缺口**，调用方（5号位→4号位）
///         必须呈现为「查询不可用」，不得呈现为「无未闭合」。</item>
/// </list>
/// </summary>
public sealed class UnresolvedCrossFactorySupplyQueryDto
{
    public int ScheduleRunId { get; init; }
    public int PlanVersionId { get; init; }

    /// <summary><c>OK</c> / <c>QUERY_FAILED</c>。见类型注释。</summary>
    public string ResultState { get; init; } = UnresolvedCrossFactorySupplyQueryState.Ok;

    /// <summary>查询失败原因（仅 <c>QUERY_FAILED</c> 时非空）。</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>命中的未闭合结果条数。**只有 <c>ResultState=OK</c> 时 0 才表示真的没有缺口。**</summary>
    public int ItemCount { get; init; }

    public IReadOnlyList<UnresolvedCrossFactorySupplyItemDto> Items { get; init; }
        = Array.Empty<UnresolvedCrossFactorySupplyItemDto>();
}

/// <summary>`UnresolvedCrossFactorySupplyQueryDto.ResultState` 的取值。</summary>
public static class UnresolvedCrossFactorySupplyQueryState
{
    /// <summary>查询成功执行。此时 <c>ItemCount=0</c> ＝ 可信的零缺口。</summary>
    public const string Ok = "OK";

    /// <summary>查询未成功执行。**不得**呈现为「无缺口」。</summary>
    public const string QueryFailed = "QUERY_FAILED";
}

/// <summary>
/// 单条未闭合结果（字段逐字对齐 `07 v5.1.17`／`08 DDL v5.1.8.11`）。
///
/// <para><b>展示纪律（`13 v1.9`）</b>：<see cref="SourceFactoryId"/>、<see cref="SourceDomainKey"/>、
/// <see cref="NeedByTime"/> **只有可信已知才有值**，未知即为 <c>null</c>；
/// 呈现方**不得**用目标厂、记录 <see cref="CreatedAt"/> 或订单交期顶替。</para>
/// </summary>
public sealed class UnresolvedCrossFactorySupplyItemDto
{
    public long Id { get; init; }
    public string ResultRecordKey { get; init; } = string.Empty;

    public long? RootOrderId { get; init; }
    public string RootDemandKey { get; init; } = string.Empty;

    public int? MaterialId { get; init; }
    public string MaterialCode { get; init; } = string.Empty;

    public int TargetFactoryId { get; init; }
    public string DomainKey { get; init; } = string.Empty;

    /// <summary>仅可信已知的真实源厂；未知为 <c>null</c>。</summary>
    public int? SourceFactoryId { get; init; }
    /// <summary>仅可信已知的真实源 Domain；未知为 <c>null</c>。</summary>
    public string? SourceDomainKey { get; init; }
    /// <summary>物料业务需用时点；未知为 <c>null</c>，**不得**用 <see cref="CreatedAt"/> 代替。</summary>
    public DateTime? NeedByTime { get; init; }

    public decimal RequiredQty { get; init; }
    public decimal LegallyAllocatedQty { get; init; }
    public decimal UnresolvedQty { get; init; }

    /// <summary><c>SH_MISSING</c> / <c>SH_CROSS_DOMAIN_FUTURE</c> / <c>PI_CROSS_DOMAIN_FUTURE</c> / <c>SH_TIME_UNCLOSED</c> / <c>UNKNOWN</c>。</summary>
    public string UnresolvedCategory { get; init; } = string.Empty;

    public string? ActualSHOrderNo { get; init; }
    public string? ActualProductionInstructionNo { get; init; }

    /// <summary>只含 `sourceRefs` / `sourceSnapshotTime` / `reasonDetail` 附加证据；不另造第二套主字段。</summary>
    public string? SourceEvidenceJson { get; init; }

    /// <summary>来源依据类别，或明确的未证实标识。非空。</summary>
    public string EvidenceBasisCode { get; init; } = string.Empty;

    /// <summary>真实问题备注（不是推断）。</summary>
    public string? IssueRemark { get; init; }

    /// <summary>恒 <c>NOT_COMMITTED</c>。本对象**不是** Supply / Allocation / 已承诺交期。</summary>
    public string CommitmentStatus { get; init; } = "NOT_COMMITTED";

    public DateTime CreatedAt { get; init; }
}