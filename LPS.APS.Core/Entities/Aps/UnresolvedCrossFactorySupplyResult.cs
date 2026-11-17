namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 跨厂未闭合净缺口结果（CFV1-08，2026-11-13）。
///
/// <para><b>这不是 Supply / Allocation</b>，也不是已承诺交期，更不是 BOM Issue ——
/// 它只记录「本该有供给、但确实没有合法来源」的那部分数量，供 4号位 经 5号位 API 以非承诺口径呈现。</para>
///
/// <para>依据：`08 DDL v5.1.8.11` :9985-10050（表定义）、`07 字段说明 v5.1.17` :24（
/// <c>ResultRecordKey</c> 要求 Run+PV 内唯一且稳定幂等）、`11 2号位 v2.7` §5。</para>
///
/// <para><b>填值纪律</b>：</para>
/// <list type="bullet">
///   <item><see cref="SourceFactoryId"/>/<see cref="SourceDomainKey"/>/<see cref="NeedByTime"/> ——
///         **仅真实可证时填**，未知一律 NULL；**不得**用目标厂、当前时点、<see cref="CreatedAt"/> 或订单交期代替。</item>
///   <item><see cref="EvidenceBasisCode"/> 必须非空（真实来源类别，或明确的 <c>SOURCE_UNVERIFIED</c>）。</item>
///   <item><see cref="CommitmentStatus"/> 恒 <c>NOT_COMMITTED</c>（库上有 CHECK 约束兜底）。</item>
/// </list>
///
/// <para><b>终态写入保护（CF-04）</b>：Round1 阶段 `AvailableAt=NULL` 只是运行中状态，
/// **不得预写 <c>SH_TIME_UNCLOSED</c>**；只有唯一 Solver 失败／硬依赖无法闭合／终态仍无法形成
/// `AvailableTime` 时才登记。同 Run/PV 业务成功闭合时须清理已生成的异常候选。</para>
/// </summary>
public class UnresolvedCrossFactorySupplyResult
{
    public long Id { get; set; }

    public int ScheduleRunId { get; set; }
    public int PlanVersionId { get; set; }

    /// <summary>
    /// 2号位 生成的稳定幂等结果键，**Run+PV 内唯一**。
    /// 重算时先替换本 Run/PV 相关结果，不保留伪历史。
    /// </summary>
    public string ResultRecordKey { get; set; } = string.Empty;

    public long? RootOrderId { get; set; }
    public string RootDemandKey { get; set; } = string.Empty;

    public int? MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;

    public int TargetFactoryId { get; set; }
    public string DomainKey { get; set; } = string.Empty;

    /// <summary>仅可信已知的真实源厂；未知 NULL。</summary>
    public int? SourceFactoryId { get; set; }
    /// <summary>仅可信已知的真实源 Domain；未知 NULL。</summary>
    public string? SourceDomainKey { get; set; }
    /// <summary>物料业务需用时点；**不得**用 CreatedAt 或订单交期顶替。</summary>
    public DateTime? NeedByTime { get; set; }

    /// <summary>真实来源类别或 <c>SOURCE_UNVERIFIED</c>，非空。</summary>
    public string EvidenceBasisCode { get; set; } = string.Empty;

    public string? IssueRemark { get; set; }

    public decimal RequiredQty { get; set; }
    public decimal LegallyAllocatedQty { get; set; }
    public decimal UnresolvedQty { get; set; }

    /// <summary>
    /// <c>SH_MISSING</c> / <c>SH_CROSS_DOMAIN_FUTURE</c> / <c>PI_CROSS_DOMAIN_FUTURE</c> /
    /// <c>SH_TIME_UNCLOSED</c> / <c>UNKNOWN</c>（库上 CHECK 约束兜底）。
    /// </summary>
    public string UnresolvedCategory { get; set; } = string.Empty;

    public string? ActualSHOrderNo { get; set; }
    public string? ActualProductionInstructionNo { get; set; }

    /// <summary>只放 `sourceRefs` / `sourceSnapshotTime` / `reasonDetail` 附加证据，不复制第二套主字段。</summary>
    public string? SourceEvidenceJson { get; set; }

    public string CommitmentStatus { get; set; } = UnresolvedCommitmentStatus.NotCommitted;

    public DateTime CreatedAt { get; set; }
}

/// <summary>未闭合结果的承诺状态值域（库上 CHECK 只允许此一值）。</summary>
public static class UnresolvedCommitmentStatus
{
    public const string NotCommitted = "NOT_COMMITTED";
}

/// <summary>未闭合分类值域（与库上 `CK_UCFS_Category` 逐字一致）。</summary>
public static class UnresolvedCategoryCodes
{
    /// <summary>缺真实 SH 单据的 SH 型净缺口。</summary>
    public const string ShMissing = "SH_MISSING";
    /// <summary>已知跨 Domain、依赖未来制造的 SH 缺口（V1 不展开）。</summary>
    public const string ShCrossDomainFuture = "SH_CROSS_DOMAIN_FUTURE";
    /// <summary>PI 外域未来续工缺口（V1 不展开）。</summary>
    public const string PiCrossDomainFuture = "PI_CROSS_DOMAIN_FUTURE";
    /// <summary>SH 整单终态时间无法闭合（**仅在 Solver 失败/硬依赖不闭合时才写**）。</summary>
    public const string ShTimeUnclosed = "SH_TIME_UNCLOSED";
    /// <summary>来源或分类确实无法判定。</summary>
    public const string Unknown = "UNKNOWN";
}
