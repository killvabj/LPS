namespace LPS.APS.Application.Services.Query.Dto;

/// <summary>
/// 甘特「资源×时间桶」聚合层（三步走·第 2 步；GET /api/schedule/gantt/{id}/buckets）。
/// 契约口径=5号位 2026-10-30 复函九条定稿（4号位 10-30 形态确认函的答复件）：
/// ① 桶条同源 naive——列无时区，桶=库值自然日历切、序列化与 plannedStartTime 同款（无 Z 无 offset），两端都不做时区换算；
/// ② 未排任务（PlannedStartTime IS NULL）成独立行：<c>bucketStart</c> 整键省略、仅 taskCount 计数（本项目 null=缺键惯例）；
/// ③ loadHours=SUM(Duration)/60.0，与明细体 Duration 同一表达式（工时口径，非墙钟跨度）；
/// ④ 整条归一桶 + **钳制归桶**：起点早于窗头的任务归首个桶（CASE WHEN start&lt;from THEN from）
///    ——保证恒等式 <c>Σ items.taskCount ≡ totalTasks</c>（4号位 B3 断言从"差额可解释"加强为恒等）；
/// ⑤ 无资源任务 resourceId=-1 单出行（与明细 ISNULL(ResourceId,-1) 归一同款，元数据为 null）；
/// ⑥ 行直接带资源元数据（与 GanttResourceDto 同字段同 JOIN，省一次往返、单源）；
/// ⑦ 空态三位沿用 versionExists/scopeDenied/isEmpty；
/// ⑨ 稀疏输出（仅存在任务的 资源×桶 对，绝不笛卡尔）；行数>50,000 自动 day→week→month 降级并回显
///    bucketWidth=实际生效桶宽 + bucketDowngraded；月桶仍超 ⇒ 真 HTTP 400 要求 resourceIds 收窄。
/// </summary>
public class GanttBucketsDto
{
    public int PlanVersionId { get; set; }
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>版本是否真实存在（缺键/ false ⇒ 不存在；200+flag 惯例，非 404，同明细体 10-19 定例）。</summary>
    public bool VersionExists { get; set; }

    /// <summary>越权（fail-closed）：true=无该 Domain 授权，items 空。</summary>
    public bool ScopeDenied { get; set; }

    /// <summary>该版本（窗口口径）确实无任何任务。</summary>
    public bool IsEmpty { get; set; }

    /// <summary>请求的桶宽回显（day|week|month）。</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>实际生效桶宽（降级后可能与 Bucket 不同）。</summary>
    public string BucketWidth { get; set; } = string.Empty;

    /// <summary>是否发生过降级（Bucket≠BucketWidth 时为 true）。</summary>
    public bool BucketDowngraded { get; set; }

    /// <summary>窗口起回显（naive，无 Z）。</summary>
    public DateTime? From { get; set; }

    /// <summary>窗口止回显（naive，无 Z）。</summary>
    public DateTime? To { get; set; }

    /// <summary>窗口口径任务总数（与明细体同谓词 COUNT）；恒等式 Σ items.taskCount == 此值。不存在/越权=缺键。</summary>
    public int? TotalTasks { get; set; }

    public IReadOnlyList<GanttBucketRowDto> Items { get; set; } = Array.Empty<GanttBucketRowDto>();
}

/// <summary>一行 = 一个 (资源, 桶) 对；仅存在任务的对才出现（稀疏输出）。</summary>
public class GanttBucketRowDto
{
    /// <summary>ISNULL(ResourceId,-1)；-1=未分配资源行。</summary>
    public int ResourceId { get; set; }

    // 资源元数据（与 GanttResourceDto 同名同 JOIN；-1 行全为 null）
    public string? ResourceCode { get; set; }
    public string? ResourceName { get; set; }
    public string? FactoryName { get; set; }
    public string? ProductionDepartmentName { get; set; }
    public string? Stage { get; set; }

    /// <summary>域=PlanVersion.DomainKey（F-G4 定例，非 FACTORY_{id} 推导）。</summary>
    public string? DomainKey { get; set; }

    /// <summary>桶起点，naive 无 Z；**缺键=未排桶**（PlannedStartTime IS NULL 的聚合行）。</summary>
    public DateTime? BucketStart { get; set; }

    /// <summary>落桶任务条数（整条计一次，不切段）。</summary>
    public long TaskCount { get; set; }

    /// <summary>桶内任务整条工时之和（小时）=SUM(Duration)/60.0，与明细体 Duration 同表达式。</summary>
    public decimal? LoadHours { get; set; }

    /// <summary>桶内最早计划开始（naive）。</summary>
    public DateTime? FirstStart { get; set; }

    /// <summary>桶内最晚计划完成（naive；如实可越窗——lastEnd 不钳制）。</summary>
    public DateTime? LastEnd { get; set; }
}
