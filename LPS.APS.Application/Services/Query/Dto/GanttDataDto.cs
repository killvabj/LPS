namespace LPS.APS.Application.Services.Query.Dto;

/// <summary>
/// 甘特图数据（前端一次性拉取整个版本的 Task + Resource 明细）
/// </summary>
public class GanttDataDto
{
    public int PlanVersionId { get; set; }
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>
    /// PlanVersion 状态（ACTIVE / CANDIDATE / ARCHIVED）
    ///
    /// v1.5 §16.3：Candidate 结果查询时必须返回，供4号位区分当前版本类型。
    /// </summary>
    public string? PlanVersionStatus { get; set; }

    /// <summary>
    /// Candidate 基准版本 ID（仅 CANDIDATE 状态时有值）
    ///
    /// v1.5 §16.3：Candidate 与 Base 比较所需。
    /// ACTIVE / ARCHIVED 版本为 null。
    /// </summary>
    public int? BasePlanVersionId { get; set; }

    public DateTime PlanHorizonStart { get; set; }
    public DateTime PlanHorizonEnd { get; set; }

    /// <summary>
    /// 资源行（甘特图 Y 轴）
    /// </summary>
    public IReadOnlyList<GanttResourceDto> Resources { get; set; } = Array.Empty<GanttResourceDto>();

    /// <summary>
    /// 任务条（甘特图 X 轴上的矩形）
    /// </summary>
    public IReadOnlyList<GanttTaskDto> Tasks { get; set; } = Array.Empty<GanttTaskDto>();

    /// <summary>
    /// 是否因业务范围（Domain）不足而被拒绝（fail-closed）。
    /// true = 当前用户无该 Domain 授权，返回空是"越权"而非"没数据"。
    /// </summary>
    public bool ScopeDenied { get; set; }

    /// <summary>
    /// 是否为空结果（版本不存在 / 越权 / 该版本确无 Task）。
    /// 与 <see cref="ScopeDenied"/>、<see cref="VersionExists"/> 配合区分三种空态。
    /// </summary>
    public bool IsEmpty { get; set; }

    /// <summary>
    /// 版本是否真实存在。false = 该 planVersionId 不存在（VersionCode 为空串）。
    /// 显式位，替代「<c>versionCode === ''</c>」这种隐式契约（4号位 10-17 §2.3）。
    /// 三种空态：版本不存在(<c>false</c>) / 越权(<c>scopeDenied</c>) / 存在但无任务(全 true + isEmpty)。
    /// </summary>
    public bool VersionExists { get; set; }

    // ---- 10-29 大数据量分页契约（4号位 三步走·第 1 步；5号位 复函 2026-10-29 定稿） ----

    /// <summary>
    /// 本次生效的单次任务条上限（缺省 8000，硬上限 20000）。
    /// ⚠ 向后兼容断点：<b>不传 limit 也不再返回整版本全量</b>——默认 8000 + hasMore（任何调用方不得再一次拉百万行）。
    /// </summary>
    public int Limit { get; set; }

    /// <summary>
    /// <b>当前过滤口径</b>（from/to/resourceIds）下的任务总数——同谓词 COUNT，不是版本总数，
    /// 勿与 <c>PlanVersionSummaryDto.TotalTasks</c> 混用（4号位 10-29 §3.4 坑#3）。
    /// 版本不存在 / 越权时为 null（无意义，不拿 0 冒充"没有"）。
    /// </summary>
    public int? TotalTasks { get; set; }

    /// <summary>
    /// 是否还有下一页。实现=多取 1 行判溢出后裁掉（TOP limit+1），零额外查询、永不撒谎；
    /// 禁止静默截断（4号位 10-29 §3.4 坑#1）。
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// 游标回显：<c>hasMore=true</c> 时必给，原样回填 afterResourceId/afterStartAt/afterTaskId 即取下一页。
    /// 排序键全序=<c>(ISNULL(PlannedStartTime,'1753-01-01'), Id)</c>（11-06 B 案时间序）；
    /// 未排（NULL 起始）归一 1753-01-01 排最前；<c>resourceId</c> 仅信息回显、**不参与游标**（afterResourceId 被服务端忽略）。
    /// </summary>
    public GanttCursorDto? NextAfter { get; set; }
}

/// <summary>甘特分页游标（11-06 B 案：生效键=二元组 plannedStartTime+taskId；resourceId 仅信息回显不参与游标）</summary>
public class GanttCursorDto
{
    /// <summary>信息回显（11-06 起不参与游标；服务端忽略回传值；无资源=-1 归一）</summary>
    public int ResourceId { get; set; }

    /// <summary>游标键1：计划开始时刻；未排（NULL）归一为 1753-01-01T00:00:00（SQL datetime 下限；0001 会参数溢出，勿改回）</summary>
    public DateTime PlannedStartTime { get; set; }

    /// <summary>游标键2：Task Id（唯一尾键，全序保证翻页不漏行不重行）</summary>
    public long TaskId { get; set; }
}

public class GanttResourceDto
{
    public int ResourceId { get; set; }
    public string ResourceCode { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public int? FactoryId { get; set; }
    public int? ProductionDepartmentId { get; set; }

    /// <summary>
    /// 资源所属域标识（G2-b 档①；U01 多 Domain 染色分组）
    /// F-G4：域不再以 FACTORY_{id} 命名规则推导，资源域 = 所属 PlanVersion.DomainKey（DomainDefinition 唯一权威源）
    /// </summary>
    public string? DomainKey { get; set; }

    /// <summary>
    /// 工厂名称（G2-b 档①；域主数据投影）
    /// </summary>
    public string? FactoryName { get; set; }

    /// <summary>
    /// 生产部门名称（G2-b 档①；域主数据投影）
    /// </summary>
    public string? ProductionDepartmentName { get; set; }

    /// <summary>
    /// 工序阶段标识/名称（G2-b 档①；ProductionDepartment.StageCode，部门 vs 阶段 1:1）
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// 设备不可用窗口（G2-b 档②；依赖排程/根因事实，未就绪返回 null，v1.3 待 2号位 对齐）
    /// </summary>
    public IReadOnlyList<GanttUnavailableWindowDto>? UnavailableWindows { get; set; }
}

public class GanttTaskDto
{
    public long TaskId { get; set; }
    public string TaskNo { get; set; } = string.Empty;
    public long OrderId { get; set; }
    public string? OrderNo { get; set; }
    public int MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string? MaterialName { get; set; }
    public int? ResourceId { get; set; }
    public string OperationCode { get; set; } = string.Empty;
    public int OperationSeq { get; set; }
    public decimal Quantity { get; set; }
    public string UOM { get; set; } = string.Empty;
    public DateTime? PlannedStartTime { get; set; }
    public DateTime? PlannedEndTime { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 是否延期（按**日期**比较：PlannedEndTime 的日期 > CustomerDueDate）。
    /// 注：CustomerDueDate 为 DATE（到天），PlannedEndTime 为 DATETIME2。
    /// 修复前直接比较二者会被隐式提升为"完工时刻 vs 交日零点"，导致**当天完工也被判延期**。
    /// </summary>
    public bool IsDelayed { get; set; }

    /// <summary>
    /// 客户交期（[Order].CustomerDueDate，DATE）。冻结 01:1907 第 2 项要求输出。
    /// </summary>
    public DateTime? CustomerDueDate { get; set; }

    /// <summary>
    /// 延期时长（小时）= PlannedEndTime − CustomerDueDate，仅延期时为正；未延期为 null。
    /// </summary>
    public decimal? DelayHours { get; set; }

    /// <summary>
    /// 是否关键路径。**11-05 B5 定稿：V1 恒缺键（未判定）**——落盘列 Task.IsCriticalPath 写侧恒 false
    /// （PeggingOrchestrator 两处写死），false 并非"判过且非关键"而是"未算"；按 10-17 MesEligible 先例
    /// 改 bool? 且查询层改投 CAST(NULL)（不让假 false 冒充判定）。真值归属 1号位（01:355），已标转办。
    /// </summary>
    public bool? IsCriticalPath { get; set; }

    /// <summary>
    /// 计划工时，单位**小时**：取自库值 <c>Task.Duration</c>（DDL 08:4323，库值口径亦为**小时**）。
    /// 以库值为准，不用前端时间差反算。
    ///
    /// 单位口径已按冻结《字段说明》v5.1.15 <c>:3696</c>「单位：小时」收敛（v5.1.16 <c>:3703</c> 同位同文）：
    /// 写入方按小时落库，读取侧不再折半换算（0号位 2026-10-26 正式回复 §:60）。
    /// </summary>
    public decimal? Duration { get; set; }

    /// <summary>
    /// 任务所属域标识（G2-b 档①；U01，从 Order.DomainKey 投影）
    /// </summary>
    public string? DomainKey { get; set; }

    /// <summary>
    /// 净需求数量（G2-b 档①；U05，落盘 Task.Quantity = NetOutputQty 净合格产出）
    /// </summary>
    public decimal? NetQty { get; set; }

    /// <summary>
    /// 含良率补偿数量（G2-b 档①；U05 净需求 vs 含良率）
    /// 取 Task.PlannedProcessQty（DDL 08:4319 有定义、2号位 PeggingOrchestrator 落盘）。
    /// 原注释「Task 表当前无此列」有误，已更正（2026-10-17，4号位 R2）。
    /// </summary>
    public decimal? PlannedProcessQty { get; set; }

    /// <summary>
    /// MES 下发资格（G2-b 档①；U16）。
    /// <para><c>true</c>=可下发；<c>false</c>=判定为不可下发；<c>null</c>=未判定（资格判定尚未接入）。</para>
    /// ⚠️ 该字段**不是任何表的列**——数据库无 `MesEligible` 列，当前由查询层硬编码返回，属接口占位。
    /// 原为非可空 <c>bool</c> + 恒 <c>false</c>，会导致「判定为否」与「未判定」在契约上不可区分；
    /// 2026-10-17 改为可空，未接入期统一返回 <c>null</c>（前端按「未接入」展示）。
    /// </summary>
    public bool? MesEligible { get; set; }

    /// <summary>
    /// 不可下发原因（G2-b 档①；U17，硬枚举见 v1.2 §二 2.3）
    /// ⚠️ 未就绪返回 null，待 5号位/2号位 对齐
    /// </summary>
    public IReadOnlyList<string>? MesIneligibleReasons { get; set; }

    /// <summary>
    /// 跨域阻挡标记（G2-b 档①；U14）。
    /// <para><c>true</c>=被阻挡；<c>false</c>=判定为未阻挡；<c>null</c>=未判定（判定未接入）。</para>
    /// ⚠️ 该字段**不是任何表的列**——数据库无 `CrossDomainBlocked` 列，当前由查询层硬编码返回，属接口占位。
    /// 与 <see cref="MesEligible"/> 同因改可空：避免「判定为否」与「未判定」不可区分（2026-10-17）。
    /// </summary>
    public bool? CrossDomainBlocked { get; set; }

    /// <summary>
    /// 跨域阻挡原因（G2-b 档①；复用 G7 域依赖）
    /// ⚠️ 未就绪返回 null，待 2号位 对齐
    /// </summary>
    public string? CrossDomainBlockReason { get; set; }

    /// <summary>
    /// Demand Protection 锁标记（G2-b 档②；U08，未就绪返回 null，v1.3）
    /// </summary>
    public string? LockMarker { get; set; }

    /// <summary>
    /// 多 Order 共担一个 Task 的份额（G2-b 档②；U04）。
    /// 2026-10-31 落地（4号位 字段饥饿函 A2）：源=`AllocationTaskShare`（IX_ATS_Task 按 TaskId seek），
    /// 键名按前端契约取单数 `taskShare`（JSON，原属性名 TaskShares 多一个 s 导致前端读不到）。
    /// 行形状：orderId=RootOrderId；orderNo=join Order(RootOrderId)；shareQty=ShareQty；
    /// shareRatio=ShareQty/ΣShareQty(同 Task)——**占比按同任务内份额归一，不是除以 Task 数量**。
    /// </summary>
    public IReadOnlyList<GanttTaskShareDto>? TaskShare { get; set; }

    /// <summary>
    /// 数量-时间分段（G2-b 档②；U06，40+60 不压成 100，未就绪返回 null，v1.3）
    /// </summary>
    public IReadOnlyList<GanttTaskSegmentDto>? TaskSegments { get; set; }

    /// <summary>
    /// 延期原因明细（G2-b 档②；根因优先，未就绪返回 null，v1.3 待 2号位/1号位 对齐）
    /// </summary>
    public IReadOnlyList<GanttDelayReasonDto>? DelayReasons { get; set; }

    /// <summary>
    /// 事实类型（G2-b 档②；FACT/RESULT/ESTIMATED/RECOMMENDATION，未就绪返回 null，v1.3）
    /// </summary>
    public string? FactType { get; set; }

    /// <summary>
    /// 上游工序阶段阻挡（G2-b 档②；未就绪返回 null，v1.3）
    /// </summary>
    public bool? UpstreamStageBlocked { get; set; }

    /// <summary>
    /// 上游阻挡阶段编码（G2-b 档②；未就绪返回 null，v1.3）
    /// </summary>
    public string? UpstreamStageCode { get; set; }

    // ─── 0号位 11-05「表里有的都反出来，部门显示名称」清单（Task 列直投；JSON 均 camelCase）───

    /// <summary>生产部门 Id（Task.ProductionDepartmentId，实库 100% 有值）。JSON: productionDepartmentId</summary>
    public int? ProductionDepartmentId { get; set; }

    /// <summary>部门名称（JOIN ProductionDepartment.DeptName，如「CN机加_制造3-3课755HL」）。JSON: departmentName</summary>
    public string? DepartmentName { get; set; }

    // （工序名不设——Task 表无此列，Owner 11-05 裁「表里有的才吐」，衍生 JOIN 一并撤）

    /// <summary>阶段码（Task.StageCode，100% 有值）。JSON: stageCode</summary>
    public string? StageCode { get; set; }

    /// <summary>任务类型（Task.TaskType，实库现全 PRODUCTION 单值域）。JSON: taskType</summary>
    public string? TaskType { get; set; }

    /// <summary>路线码（Task.RouteCode 原样；实库多为通用快照「DEFAULT」，与 RoutingDependency 对合时不可靠，如实回传）。JSON: routeCode</summary>
    public string? RouteCode { get; set; }

    /// <summary>路径 Id（Task.PathId，多 BOM 支号；配合 MaterialId+OperationCode 定位）。JSON: pathId</summary>
    public int? PathId { get; set; }

    /// <summary>人工槽 Id（Task.ManualSlotId，人工作业行才有值=资源侧与设备互斥身份）。JSON: manualSlotId</summary>
    public int? ManualSlotId { get; set; }

    /// <summary>生产指示号（Task.MTS_InstructionNo，A 桶关联 PI）。⚠ JSON 键=**`mtS_InstructionNo`**（同订单列表规律）。</summary>
    public string? MTS_InstructionNo { get; set; }

    /// <summary>执行批内部键（Task.StageExecutionBatchDraftKey，GUID 句柄，供追溯非展示）。JSON: stageExecutionBatchKey</summary>
    public string? StageExecutionBatchKey { get; set; }

    /// <summary>执行批量（Task.StageExecutionBatchQty，整批串行执行的数量载体）。JSON: stageExecutionBatchQty</summary>
    public decimal? StageExecutionBatchQty { get; set; }

    /// <summary>
    /// MES 工单号（Task.MESWorkOrderNo，冻结 07:3687 可追溯执行身份；A/B 桶绑定原单、C 桶后建新单）。
    /// 空/未建单 ⇒ null ⇒ **整键省略**（不显示空串）。
    /// ⚠ JSON 键=**`mesWorkOrderNo`**（实测 11-05：连续大写段 MESW 后接小写 o，CamelCase 只折到 "MES" 全小写、W 保留；
    /// 与 `MTS_`→`mtS_`（带下划线、整段折到倒数第二个）规则**不同**——本键无下划线。11-05 B2① 定稿。）
    /// </summary>
    public string? MESWorkOrderNo { get; set; }

    /// <summary>
    /// 下发状态（TaskDispatch.DispatchStatus——"是否已正式下发 MES"的权威载体，解 07:3721 状态0 的 PLANNED/RELEASED 歧义）。
    /// 无下发记录 ⇒ 缺键=未下发；dev 现况 TaskDispatch 0 行、键整体缺省属真实终态。11-05 B2② 定稿。
    /// </summary>
    public string? DispatchStatus { get; set; }

    /// <summary>
    /// 换型/Setup 来源（R3 SetupSource 输出链；v1.5 §9/§10）
    /// 1号位 产出 → 2号位 原样落库 [Task].[SetupSource] → 本 DTO 透传。
    /// 值契约：0号位 20260922 四态 INITIAL_SETUP_STATE / EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK（nullable string）。
    ///         内部 SetupOutcome 5 值（ExactHit/DefaultHit/SameProductZero/InitialState/RuleMissing）经 SetupOutcomeToSource 归并后落库；SAME_PRODUCT/NONE 不再是正式值。
    /// 用途：甘特每 Task 展示换型标签（EXACT/DEFAULT 正常；SETUP_RULE_MISSING_ZERO_FALLBACK 红 Tag 规则缺失；INITIAL_SETUP_STATE 班头首单普通展示）。
    /// </summary>
    public string? SetupSource { get; set; }
}

/// <summary>设备不可用窗口（G2-b 档②）</summary>
public class GanttUnavailableWindowDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? Reason { get; set; }
    public IReadOnlyList<long>? ImpactedTaskIds { get; set; }
}

/// <summary>多 Order 共担一个 Task 的份额（G2-b 档②）</summary>
public class GanttTaskShareDto
{
    public long OrderId { get; set; }
    public string? OrderNo { get; set; }
    public decimal ShareQty { get; set; }
    public decimal ShareRatio { get; set; }
}

/// <summary>数量-时间分段（G2-b 档②）</summary>
public class GanttTaskSegmentDto
{
    public int SegmentSeq { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public decimal Qty { get; set; }
    public decimal QtyRatio { get; set; }
    public string? Reason { get; set; }
}

/// <summary>延期原因明细（G2-b 档②）</summary>
public class GanttDelayReasonDto
{
    public string ReasonCode { get; set; } = string.Empty;
    public string? IssueCategory { get; set; }
    public string? Severity { get; set; }
    public string? Description { get; set; }
    public string? RelatedObjectRef { get; set; }
    public decimal? ImpactHours { get; set; }

    /// <summary>
    /// 事实层级（11-05 Q2 定稿）：'TASK'=任务级正式原因；'ORDER'=订单级事实顺挂本任务（2号位现况主体）。
    /// 前端对 ORDER 级须标注"订单级原因"，不得冒充任务级；缺键=旧数据无层级（V1 不会出现）。JSON: objectType
    /// </summary>
    public string? ObjectType { get; set; }
}
