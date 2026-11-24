namespace LPS.APS.Core.Dto;

/// <summary>
/// P05 服务端任务清单查询结果（11-16 矩阵 A4，执行单第一阶段欠账首件）。
/// 一行=一个真实 Operation Task.Id（**绝不按 TaskNo 折叠**——一 TaskNo≈3.31 Task，D05-17 实测）；
/// 三态显式：VersionExists=false（PV 不存在）／ScopeDenied=true（越权，页面显示"无权限"而非"无数据"）／
/// 正常页（Total=服务端全量命中、Items=本页已加载——两数分开，v0.2 §P05"加载数≠全部数"红线）。
/// </summary>
public sealed class TaskListResultDto
{
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }
    public List<TaskListItemDto> Items { get; init; } = new();
    /// <summary>同 WHERE 全量命中数（COUNT 与列表**单源同式**）。</summary>
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// P05 列表行。列名照 v0.2 §P05 默认业务列；**NULL=未知，前端不得转 0/"待开工"/"正常"**。
/// MES 列只透传 `mesWorkOrderNo` 原值（可空）——"无需MES/暂不可下发"的权威分类标志不存在
/// （矩阵 Q-a 挂 0/2号位），**后端与前端都不得由工单号空值推断**。
/// </summary>
// 非 sealed：Application 侧详情投影按行继承加身份列（TaskDetailRow）。
public class TaskListItemDto
{
    /// <summary>Task.Id——行身份（P06 详情键）。</summary>
    public long TaskId { get; init; }
    /// <summary>Stage 执行批业务身份（多工序共享，禁当行键）。</summary>
    public string TaskNo { get; init; } = string.Empty;
    public int PlanVersionId { get; init; }

    public int MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    /// <summary>主数据缺行 ⇒ null，前端回退编码，不编造。</summary>
    public string? MaterialName { get; init; }

    public string? StageCode { get; init; }
    /// <summary>大工艺中文名（B1，11-20 4号位件）：`ext_MES_StageDict.StageName`（ODS 同义词，29 行主数据），
    /// 字典缺该码 ⇒ null=未提供（**不自造译名**）。</summary>
    public string? StageName { get; init; }
    /// <summary>大工艺类目中文（`ext_MES_StageDict.CategoryName`，如"机加/组立/铸造"）——v0.2 P05 首列"大工艺"。</summary>
    public string? StageCategoryName { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    /// <summary>工序名（D05：99.07% 填充；null=未判定）。</summary>
    public string? OperationName { get; init; }

    /// <summary>设备（与人工槽互斥补全：每行二者其一，999473 实测 16,303+11,718=28,021）。</summary>
    public int? ResourceId { get; init; }
    /// <summary>资源编码（N2，11-17 补：v0.2 P05「执行资源」列标识位；主数据缺行 ⇒ null）。</summary>
    public string? ResourceCode { get; init; }
    public string? ResourceName { get; init; }
    public int? ManualSlotId { get; init; }
    public string? ManualSlotCode { get; init; }

    public int? ProductionDepartmentId { get; init; }
    public string? ProductionDepartmentName { get; init; }

    /// <summary>计划净量（Task.Quantity）。</summary>
    public decimal Quantity { get; init; }
    /// <summary>计划加工量（可空）。</summary>
    public decimal? PlannedProcessQty { get; init; }
    public string Uom { get; init; } = string.Empty;

    public DateTime? PlannedStartTime { get; init; }
    public DateTime? PlannedEndTime { get; init; }
    /// <summary>时长（小时，naive；null=未知）。</summary>
    public decimal? Duration { get; init; }

    /// <summary>APS 计划态（999473 现值域={PLANNED}，原值透传不做白名单）。</summary>
    public string Status { get; init; } = string.Empty;
    public bool IsLocked { get; init; }
    public bool IsCriticalPath { get; init; }
    public string TaskType { get; init; } = string.Empty;

    /// <summary>PI/出荷执行身份（MTS_InstructionNo，可空=未提供）。</summary>
    public string? MtsInstructionNo { get; init; }
    /// <summary>MES 工单号原值；空=未提供事实，**不等于**下发失败或无需 MES（v0.2 §P05）。</summary>
    public string? MesWorkOrderNo { get; init; }
    /// <summary>MES 下发状态（源=TaskDispatch.DispatchStatus，按 UX_TaskDispatch_SourceTask 唯一键 1:1 投影；
    /// **缺键=该 Task 不在下发队列 ⇒ null**——≠「未下发」、≠「无需 MES」（队列资格权威位未建=Q-a；
    /// 消费环留痕缺口=Q-d，禁由空值反推任何一侧）。R3′（11-18 4号位件·我方 N2 函"两属性"实只落一个，
    /// SQL 有列 DTO 无属性 ⇒ Dapper 静默丢列、键恒不出现——第四次函码不一致，认账补）。</summary>
    public string? MesDispatchStatus { get; init; }
    /// <summary>换型来源标签（D05-19：59%=SETUP_RULE_MISSING_ZERO_FALLBACK，展示须带此底）。</summary>
    public string? SetupSource { get; init; }
}

/// <summary>
/// P06 单 Task 详情（矩阵 A5）：列表行全部字段 + 版本身份 + 份额明细（AllocationTaskShare）。
/// 共享 Task 的份额=多根订单**逐行给全**，页面不得贴单一订单为唯一需求来源（v0.2 §P06）。
/// 前后依赖边、MES 实际执行事实（报工五态）=本轮**未提供**（载体与口径挂 Q-a/Q-d，不造）。
/// </summary>
public sealed class TaskDetailResultDto
{
    public bool Found { get; init; }
    public bool ScopeDenied { get; init; }
    public TaskListItemDto? Task { get; init; }

    // ── 版本身份（统一跳转上下文，v0.2 §5）──
    public string? VersionCode { get; init; }
    public string? DomainKey { get; init; }
    /// <summary>ACTIVE/CANDIDATE/ARCHIVED——当前 ACTIVE 才代表"正式计划"。</summary>
    public string? PlanVersionStatus { get; init; }
    /// <summary>数据截至=源 Run 的 DataCutoffTime；僵尸/失败 Run 如实给原值。</summary>
    public DateTime? DataCutoffTime { get; init; }

    // ── P06 H4（11-23 4号位件）：源 Run 身份（PV→SourceScheduleRunId→ScheduleRun；无源=未提供键省略）──
    public long? SourceRunId { get; init; }
    /// <summary>JSON: sourceRunType</summary>
    public string? SourceRunType { get; init; }
    /// <summary>JSON: runTriggeredBy</summary>
    public string? RunTriggeredBy { get; init; }

    // ── P06 H3（11-23 4号位件）：MES 真实执行事实（P11 同键单源；UI-22：与下发态分列不互冒）──
    public string? MesWorkOrderStatus { get; init; }
    /// <summary>null=未提供（无源 Run）；true=有报工行；false=快照 Run 有效但零报工行（未判定≠未开工）。</summary>
    public bool? MesReported { get; init; }
    public decimal? MesPlannedQtyReported { get; init; }
    public decimal? MesGoodQty { get; init; }
    public decimal? MesScrapQty { get; init; }
    public decimal? MesReworkQty { get; init; }
    public decimal? MesRemainingQty { get; init; }
    public DateTime? MesLastReportTime { get; init; }

    public List<TaskShareDto> Shares { get; init; } = new();
    /// <summary>份额表无行=事实为"无份额记录"（≠0 需求，≠未提供——页面文案分三态）。</summary>
    public bool SharesProvided { get; init; } = true;
}

/// <summary>P06 份额行：Task ↔ Demand/根订单。</summary>
public sealed class TaskShareDto
{
    public long AllocationSequence { get; init; }
    /// <summary>DemandType（现值域含 ORDER 等，原值透传）。</summary>
    public string? DemandType { get; init; }
    /// <summary>DemandKey 明文（形如 ORDER_{RootOrderId}_{物料}_{n}，D05-14）。</summary>
    public string? DemandKey { get; init; }
    public long? RootOrderId { get; init; }
    /// <summary>根订单号=同 PV 的 [Order] 反查（D05-12：RootOrderNo 空但此路径 100% 命中）。</summary>
    public string? RootOrderNo { get; init; }
    public decimal? ShareQty { get; init; }
}
