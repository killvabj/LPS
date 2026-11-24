namespace LPS.APS.Core.Dto;

/// <summary>
/// P11 状态查询读面（**B6，11-17；0号位 Q-d 裁：P11 状态查询=5号位职责**）。
/// 行粒度=**Stage 执行批（TaskNo）**（v0.2 §4.11.3：有 TaskNo 时按执行批；一批多工序行）。
///
/// **三层状态严格分列，互不冒充**（Q-d 第四项）：
/// ① APS 下发层 = <c>*Count</c> 四计数 + <c>dispatchStatusKinds</c>（TaskDispatch 原词表）；
/// ② MES 工单层 = <c>workOrderStatus</c>（MESWorkOrderSnapshot 原值）；
/// ③ MES 报工层 = <c>good/scrap/rework/remaining…</c>（OperationProgressSnapshot 数量列）。
///
/// **如实边界（写进契约，防误读）**：
/// · `CREATED` 仅=APS 下发成功，不代表 MES 开工/完工（v0.2 §4.11.3）；
/// · 下发四态中 `PROCESSING/FAILED` **消费环现不落痕 + UpdatedAt 全表 NULL**（D05-20/Q-d：留痕不足
///   ≠ 从未下发，也 ≠ 已消费）——计数列照原词表给，**页面不得据缺痕断言链未开**；
/// · "需MES/无需MES/暂不可下发"分类权威标志**不存在**（Q-a 未裁 ⇒ <c>mesRequirementCategory</c> 恒 null，
///   前端显示"未提供"，禁由工单号空值反推）；
/// · 报工行可缺（PI 无报工快照行=常态，<c>reported=false</c>=**未判定**，不是"零报工"）；
/// · WorkOrderStatus 四词表 {RELEASED, IN_PROGRESS, CLOSED, DELETED} 实测在用；多工单批不合并（statusMixed=true）。
/// </summary>
public sealed class MesStatusListResultDto
{
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }
    public int PlanVersionId { get; init; }
    public string? DomainKey { get; init; }
    public string? PlanVersionStatus { get; init; }
    /// <summary>PV 的来源 Run（快照按 Run 装载，非按 PV——此位空=版本无源批，快照三列必 null）。</summary>
    public int? SourceScheduleRunId { get; init; }
    public DateTime? DataCutoffTime { get; init; }

    /// <summary>快照事实同步时点（页面"数据截至/源更新"两列的权威来源；null=该源未取到）。</summary>
    public DateTime? WoSnapshotSourceUpdatedAt { get; init; }
    public DateTime? OpsSnapshotSourceUpdatedAt { get; init; }
    /// <summary>TaskDispatch 最近入队时刻（UpdatedAt 全表 NULL=消费环留痕缺口，此处只给 CreatedAt 面）。</summary>
    public DateTime? DispatchMaxCreatedAt { get; init; }

    public List<MesStatusBatchRowDto> Items { get; init; } = new();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>P11 列表行：一个 Stage 执行批。</summary>
public sealed class MesStatusBatchRowDto
{
    public string TaskNo { get; init; } = string.Empty;
    public string PiNo { get; init; } = string.Empty;
    public string? StageCode { get; init; }
    /// <summary>指令号原值（出口组装合法=出荷指示号；空=无，不判非法——Q16/0号位既有裁）。</summary>
    public string? InstructionNo { get; init; }
    public string? WorkOrderType { get; init; }
    public string? MaterialCode { get; init; }
    public string? DeptCode { get; init; }

    public int OperationCount { get; init; }
    public DateTime? PlannedStart { get; init; }
    public DateTime? PlannedEnd { get; init; }
    /// <summary>批计划量（Task.StageExecutionBatchQty 原值——**非工序行求和**，FULL-07 禁行级求和红线）。</summary>
    public decimal? BatchQty { get; init; }

    // ① APS 下发层（词表原值分桶）
    public int CreatedCount { get; init; }
    public int PendingCount { get; init; }
    public int FailedCount { get; init; }
    /// <summary>四词表外新值计数（现=0；防 2号位 扩词后静默丢类）。</summary>
    public int OtherCount { get; init; }

    // ② MES 工单层
    /// <summary>批内去重工单数；&gt;1 时 workOrderNo/Status 只给首个并置 workOrderStatusMixed。</summary>
    public int WorkOrderCount { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? WorkOrderStatus { get; init; }
    public bool WorkOrderStatusMixed { get; init; }

    // ③ MES 报工层（null=无报工快照行=未判定，不是零）
    public bool Reported { get; init; }
    public decimal? PlannedQtyReported { get; init; }
    public decimal? GoodQty { get; init; }
    public decimal? ScrapQty { get; init; }
    public decimal? ReworkQty { get; init; }
    public decimal? RemainingQty { get; init; }
    public DateTime? LastReportTime { get; init; }

    /// <summary>需MES/无需MES/暂不可下发——**恒 null**（分类权威未建，Q-a；显示"未提供"）。</summary>
    public string? MesRequirementCategory { get; init; }
}

/// <summary>P11 批详情（点行展开）：批汇总 + 工序级下发/报工行 + 关联工单快照行。</summary>
public sealed class MesStatusBatchDetailDto
{
    public bool Found { get; init; }
    public MesStatusBatchRowDto? Batch { get; init; }
    public List<MesStatusOperationRowDto> Operations { get; init; } = new();
}

/// <summary>批内工序行（TaskDispatch 原行 + 同 (Run,PI,Stage,OperationName) 报工 join）。</summary>
public sealed class MesStatusOperationRowDto
{
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationName { get; init; }
    public string DispatchStatus { get; init; } = string.Empty;
    public string? MesWorkOrderNo { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public decimal Quantity { get; init; }
    public string Uom { get; init; } = string.Empty;
    public DateTime? PlannedStart { get; init; }
    public DateTime? PlannedEnd { get; init; }
    public string? ResourceCode { get; init; }
    // 报工侧（可缺=未判定）
    public decimal? GoodQty { get; init; }
    public decimal? ScrapQty { get; init; }
    public decimal? ReworkQty { get; init; }
    public decimal? RemainingQty { get; init; }
    public DateTime? LastReportTime { get; init; }
}

/// <summary>P06 单任务执行事实（P11 同键只读投影；E2 五态派生挂载位——0 裁前 Reported 三态：true 有报工/false 无行且快照 Run 有效/null 未提供）。</summary>
public sealed class MesTaskExecFactsDto
{
    public string? WorkOrderStatus { get; set; }
    public bool Reported { get; set; }
    public decimal? PlannedQtyReported { get; set; }
    public decimal? GoodQty { get; set; }
    public decimal? ScrapQty { get; set; }
    public decimal? ReworkQty { get; set; }
    public decimal? RemainingQty { get; set; }
    public DateTime? LastReportTime { get; set; }
}
