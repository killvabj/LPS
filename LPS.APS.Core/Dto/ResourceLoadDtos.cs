namespace LPS.APS.Core.Dto;

/// <summary>
/// P12 资源负荷与 Setup/换型三率（11-16 矩阵 A6/A7，执行单第一阶段欠账第三件）。
/// 一行 = 一个 <c>(设备 ResourceId × 业务日期)</c>，绑定 <c>DomainKey＋PlanVersionId＋ResourceId＋业务日期</c>（v0.2 §P12 统计规则）。
/// **三率均为计划指标，非 MES 实际利用率**；跨午夜 Task 按日期切片（每片只算落在该业务日内的小时）。
/// 三态显式：有值 / 明确零（有可用小时但当日无排产）/ <see cref="NotApplicableReason"/> 非空=不适用（禁 0 冒充）。
/// </summary>
public sealed class ResourceLoadResultDto
{
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }
    public int PlanVersionId { get; init; }
    public string? DomainKey { get; init; }
    public string? PlanVersionStatus { get; init; }
    /// <summary>源 Run 数据截至（僵尸/失败 Run 如实给原值）。</summary>
    public DateTime? DataCutoffTime { get; init; }

    /// <summary>本次实际统计窗（naive 业务日边界，含首末两日）。</summary>
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    /// <summary>参与统计的设备数。</summary>
    public int ResourceCount { get; init; }
    /// <summary>未显式给 resourceIds 且窗内设备 &gt;200 ⇒ true（本次只统计前 200 台，页面须提示收窄）。</summary>
    public bool ResourcesTruncated { get; init; }
    public List<ResourceLoadRowDto> Rows { get; init; } = new();
    /// <summary>Setup 权威时长（1号位 Solver 产物）当前是否可回溯——false 时 ③ 恒不适用（Q-b 裁：不得用重匹配/Duration 代）。缺=数据未具备完整条件，非页面缺陷。</summary>
    public bool SetupHoursAvailable { get; init; }
}

/// <summary>
/// 设备×业务日 一行。分母权威=<see cref="CalendarAvailableHours"/>（三级继承解析出的当日合法 WORK 小时，
/// 经 ResourceCalendarService 同一解析件；越权设备不在此端点，fail-closed 由服务层剔除）。
/// </summary>
public sealed class ResourceLoadRowDto
{
    public int ResourceId { get; init; }
    public string? ResourceCode { get; init; }
    public string? ResourceName { get; init; }

    /// <summary>业务日（date 级，naive 工厂墙钟）。</summary>
    public DateTime BusinessDate { get; init; }

    /// <summary>该设备当日已排生产 Task 小时（=①②③共用分子；跨午夜切片后）。零=真零。</summary>
    public decimal ScheduledHours { get; init; }
    /// <summary>当日 Task 数（切片去重前，按 Task.Id 计）。</summary>
    public int TaskCount { get; init; }
    /// <summary>当日计划 Setup 小时（0号位 Q-b：仅当 1号位真实产物可回溯才给；否则 null 且 rate 不适用）。</summary>
    public decimal? SetupHours { get; init; }

    // ── 三率（v0.2 §P12 0号位指定公式）──
    /// <summary>① 资源日历计划负荷率 = ScheduledHours / CalendarAvailableHours ×100；可用小时为 0/缺 ⇒ null+不适用原因。</summary>
    public decimal? CalendarLoadRatePct { get; init; }
    /// <summary>② 固定16小时计划负荷率 = ScheduledHours / 16 ×100（恒可算；非真实可用时间，不能代替①或当 MES 利用率）。</summary>
    public decimal Fixed16LoadRatePct { get; init; }
    /// <summary>③ Setup换型损失率 = SetupHours / ScheduledHours ×100；Setup 缺值或分子0 ⇒ null+不适用原因。</summary>
    public decimal? SetupLossRatePct { get; init; }

    // ── 分母与不适用（三态的第三态）──
    /// <summary>当日三级继承解析出的有效可用生产小时；null=无合法 Calendar（≠0，禁当全天开放/禁套 8h）。</summary>
    public decimal? CalendarAvailableHours { get; init; }
    /// <summary>该设备当日日历来源层级（Factory/Department/Resource，供展示"依据哪级日历"）；null=未解析出。</summary>
    public string? CalendarSource { get; init; }
    /// <summary>非空=本行三率不适用（人可读原因，前端显示"不适用"，禁显示 0）；空=正常出率。</summary>
    public string? NotApplicableReason { get; init; }
}
