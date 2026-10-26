namespace LPS.APS.Core.Dto;

/// <summary>
/// 1号位排程结果（纯内存，不含正式 TaskId）。
/// 2号位收到后在统一事务中将 FinalTaskDraft 实例化为正式 [Task]。
/// </summary>
public sealed class DomainSolveResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsRoughCut { get; init; }

    public IReadOnlyList<FinalTaskDraft> FinalTasks { get; init; }
        = Array.Empty<FinalTaskDraft>();

    public IReadOnlyList<AllocationTaskShare> AllocationShares { get; init; }
        = Array.Empty<AllocationTaskShare>();

    public IReadOnlyList<UnscheduledTaskResult> UnscheduledTasks { get; init; }
        = Array.Empty<UnscheduledTaskResult>();

    /// <summary>Task-to-Task 血缘，使用 FinalDraftId 键，由1号位在排程后返回</summary>
    public IReadOnlyList<FinalTaskPeggingDraft> PhysicalPeggingDrafts { get; init; }
        = Array.Empty<FinalTaskPeggingDraft>();

    /// <summary>排程决策解释事实（材料何时到、设备负荷、为何延期等）</summary>
    public IReadOnlyList<ScheduleExplanationFact> ExplanationFacts { get; init; }
        = Array.Empty<ScheduleExplanationFact>();

    /// <summary>
    /// 求解过程追溯记录（非排程结果，仅决策说明）。
    /// 1号位 在求解过程中产出（如 SCOPE_REFERENCE_MISSING / SetupResolution 等），
    /// 2号位 原样落库/透传，不进 ReasonCode 体系（0号位 Q4）。
    /// </summary>
    public IReadOnlyList<SolveTraceNote> SolveTraceNotes { get; init; }
        = Array.Empty<SolveTraceNote>();

    /// <summary>
    /// Demand 级最终完成 / 供给可用时间（0号位 2026-09-29 裁决 §10.2/§10.3、§十四第9项）。
    ///
    /// 【为什么需要】末端无 Routing Stage 的时间（如 BJ_FINAL 完工 4h）**没有对应的真实 Operation Task**，
    ///   故不能由 <see cref="FinalTaskDraft"/> 表达；而 0号位 §9.2 明文**禁止**为此伪造
    ///   假 RoutingOperation / 假 Task.Id / 假 TaskNo / 假 MESWorkOrder。
    ///   ⇒ 该时间必须走本独立列表，**不得混入 <see cref="FinalTasks"/>**。
    ///
    /// 【口径】CompletionTime = 末端真实 Task.End + 其**之后**各无 Routing Stage 的 StageLeadTime 之和。
    ///   整条需求无任何真实 Operation Task 时 = 该 Stage 链自起点起的纯 LeadTime 累计。
    ///
    /// 【纯内存】2号位 消费后自行决定落库/透传形态；**不得**据 <see cref="DemandCompletionFact.StageTimings"/>
    ///   建 Task / TaskNo / MES工单 / Execution Batch。
    ///
    /// 【载体归属（2026-09-29 澄清）】本属性与下面两个类在 `LPS.APS.Core` —— 属 **2号位 辖区**；
    ///   1号位 边界只到 `LPS.APS.Scheduling/**`（不含 Core）⇒ **载体由 2号位 落，值由 1号位 填**。
    ///   1号位 提请件：《无RoutingStage时间出口字段_1号位致2号位_字段载体落码提请_v1.0_20260929》§三。
    /// </summary>
    public IReadOnlyList<DemandCompletionFact> DemandCompletions { get; init; }
        = Array.Empty<DemandCompletionFact>();

    public SolveSummary Summary { get; init; } = new();
}

/// <summary>
/// 1号位排定后的内存草稿（含资源、实际时间、合并拆分后数量）。
/// 仍不是数据库正式 Task。
/// </summary>
public sealed class FinalTaskDraft
{
    public string FinalDraftId { get; init; } = Guid.NewGuid().ToString();
    public string SourceDraftId { get; init; } = string.Empty;
    public int MaterialId { get; init; }
    public int FactoryId { get; init; }
    public string StageCode { get; init; } = string.Empty;
    public string OperationCode { get; init; } = string.Empty;

    /// <summary>
    /// 工序顺序号。DAG 路由（RoutingOperation/RoutingDependency v5.0）已废弃线性 OperationSeq，无客观"顺序号"；
    /// 排程顺序由 RoutingDependency 承载，与本字段无关。默认 0（落库 Task.OperationSeq=0 列兼容），
    /// 1号位可选透传 MES 源工序号，不强制填。
    /// </summary>
    public int OperationSeq { get; init; }
    public string TaskType { get; init; } = "NEW_REQUIREMENT";
    /// <summary>
    /// 排定资源（非资源工序 UNCONSTRAINED/WAIT_ONLY 产出的 Task 为 NULL；0号位 2026-09-22 裁决「非资源工序 Task 可有 Operation+Start/End、Resource 为空」）。
    /// </summary>
    public int? ResourceId { get; init; }
    public string ResourceCode { get; init; } = string.Empty;
    public string? RouteCode { get; init; }
    public long? PathId { get; init; }
    public decimal Quantity { get; init; }
    public decimal PlannedProcessQty { get; init; }
    public string UOM { get; init; } = string.Empty;
    public DateTime PlannedStartTime { get; init; }
    public DateTime PlannedEndTime { get; init; }
    public decimal SetupTime { get; init; }

    /// <summary>
    /// 有效加工分钟（1号位 2026-10-26 回执 §一 点名新增的 1→2 出口字段）。
    ///
    /// 【口径】＝标准工时 × 该 draft 的 <see cref="PlannedProcessQty"/> ÷ 产能系数（CapacityFactor）。
    ///   **不含** Setup（单列在 <see cref="SetupTime"/>）、**不含**跨日历中断（中断由评估层按日历算，不落库）。
    ///   是**不变量**：压实、重排、Setup 重解析都不改它。
    ///
    ///   ⚠ 乘数取**该 draft 自己的 <see cref="PlannedProcessQty"/>**（计划加工数量，含计划良率放大）；
    ///   不得套需求级的 `demand.PlannedProcessQty` —— 拆批／合批时 1号位 已把拆后值／合并值
    ///   **写进该 draft 的 `PlannedProcessQty`**（拆批 `PhaseFourLocalRepair.cs:1786`、
    ///   合批 `PhaseTwoInitialScheduler.cs:7606`），故消费侧**不需特判**。
    ///   <see cref="Quantity"/> 是净合格产出（`NetOutputQty`，**不含**良率放大），不可当乘数 —— 拿它算会短一截。
    ///   现役加工时长的五个计算点一律乘 `PlannedProcessQty`（1号位 2026-10-26 回执 §五 逐处列出）。
    ///   **本字段由 1号位 填值，我方只按值使用、不自行推算。**
    ///
    /// 【用途】我方回写 <c>Task.Duration</c> 时的有效加工分量：装「工时」时
    ///   <c>Duration = EffectiveProcessMinutes + SetupTime</c>；若 0号位 裁装「有效加工」则两者同值。
    ///
    /// null ＝ 1号位 未给值。消费侧回落 <c>PlannedEndTime − PlannedStartTime</c> 并**计数**：
    ///   单窗下该值与有效加工碰巧相等，**跨窗 Task 落到回落分支值会偏大** ⇒ 必须被缺值计数抓出，不得静默通过。
    /// </summary>
    public decimal? EffectiveProcessMinutes { get; init; }

    /// <summary>
    /// 工时出口字段（落库 <c>Task.Duration</c>，即冻结 DDL v5.1.8.9 <c>:4323</c> 的既有列、<c>:4337</c> 已 INCLUDE 进查询索引）。
    ///
    /// 【语义】以 1号位 2026-10-26 回执 §一 为准：装<b>工时 ＝ Setup ＋ 加工</b>，不含跨窗中断；
    ///   依据冻结 <c>07 :33</c>「Setup 占设备但不属于有效加工，计划负荷不能代替有效加工评分」。
    ///   **有效加工另由 <see cref="EffectiveProcessMinutes"/> 承载，两者不是一个数（差一个 Setup），不可互相顶替。**
    ///   若 0号位 裁定装「有效加工」，则以 <see cref="EffectiveProcessMinutes"/> 同值写入。
    ///
    /// 【单位】现行库值口径＝**分钟**；冻结《字段说明》v5.1.15 <c>:3696</c> 标「小时」，
    ///   单位订正已出提案、待 0号位 裁，我方不直改冻结件。
    ///
    /// 【现状】本属性在生产代码中**无赋值点、无消费点**（2026-10-26 核实）。
    ///   历史 <c>Task.Duration</c> 一直由我方以墙钟跨度 <c>PlannedEndTime − PlannedStartTime</c> 回写；
    ///   按新语义该算式需连同 <see cref="SetupTime"/> 一并调整（落地顺序见 1号位 2026-10-26 回执 §九）。
    /// </summary>
    public decimal? Duration { get; init; }

    /// <summary>
    /// 换型来源（唯一落库值 = 4 态：INITIAL_SETUP_STATE / EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK，
    /// 0号位 2026-09-22 裁决替换旧 5 值 EXACT/DEFAULT/SAME_PRODUCT/NONE/INITIAL）。
    /// SetupTime=0 三语义与 SetupTime&gt;0 二语义无法由数值区分，必须显式来源字段，禁止查询层反推。
    /// 2号位 只承载 string + 原样落库，**不做映射**，SetupOutcome→4 态转换在 1号位 SetupOutcomeToSource。
    /// </summary>
    public string? SetupSource { get; init; }

    public int Priority { get; init; }
    public bool IsVirtual { get; init; }

    /// <summary>
    /// 执行批键（《APS_V1_1号位有限产能排程开发实施包 v1.6》§1号位新增/替换实施要求，0号位 2026-10-07 裁决 §六 授权补载体）：
    /// 「`ExecutionBatchDraftKey` 用于把多 Operation FinalTask 归为同一执行批；FinalTask 必须回传真实 RouteCode/PathId
    ///   并原样回传 ContinuationKey」。
    ///
    /// 【口径】同一执行批（同一 Routing 选择结果）下的多个 Operation FinalTask 共享同一 Key。
    ///   **生成权属 1号位**（0号位 2026-10-07 裁决 §七：本项「无前置生产职责」，1号位 今天即可完成）。
    ///   冻结要求「每个 Execution Batch 只允许一条完整 Path」⇒ 同一 Key 下所有 FinalTask 的
    ///   (RouteCode, PathId) 必须一致。
    ///
    /// ⚠ 与既有 <see cref="StageExecutionBatchDraftKey"/> **不是同一字段**（后者是 Stage 级、按 Stage 归并，
    ///   0号位 已明确二者不可互相顶替）。
    /// </summary>
    public string? ExecutionBatchDraftKey { get; init; }

    /// <summary>
    /// 连续份额身份键（原样回传，《APS_V1_1号位有限产能排程开发实施包 v1.6》Q3 + §1号位新增/替换实施要求：
    /// 「FinalTask 必须…原样回传 ContinuationKey」）。
    ///
    /// 【口径】1号位 **不生成、不解析**，从源 <c>LogicalProductionDemand.ContinuationKey</c> **逐字拷贝**。
    ///   null = 源需求无该身份。
    /// </summary>
    public string? ContinuationKey { get; init; }

    public string? StageExecutionBatchDraftKey { get; init; }
    public decimal? StageExecutionBatchQty { get; init; }
    public long? ExistingMESPlanReleaseId { get; init; }
    public long? ExecutionLockId { get; init; }
}

/// <summary>
/// Task-to-Task 物理血缘草稿，使用 FinalDraftId 键（C1修复：替代基于原始DraftId的PhysicalPeggingDraft）
/// P0-13修复：补充DependencyType和LagTime语义
/// </summary>
public sealed class FinalTaskPeggingDraft
{
    public string UpstreamFinalDraftId { get; init; } = string.Empty;
    public string DownstreamFinalDraftId { get; init; } = string.Empty;
    public int UpstreamMaterialId { get; init; }
    public int DownstreamMaterialId { get; init; }
    public decimal Quantity { get; init; }
    public string UOM { get; init; } = string.Empty;
    public int InheritedPriority { get; init; }

    /// <summary>
    /// 依赖类型：ES=结束-开始（默认）, SS=开始-开始, FF=结束-结束
    /// V1先只实现ES
    /// </summary>
    public string DependencyType { get; init; } = "ES";

    /// <summary>
    /// 延迟时间（分钟，0=紧跟前驱完成）
    /// </summary>
    public decimal LagTime { get; init; }
}

/// <summary>
/// AllocationSequence 在最终 Task 中的数量份额（供2号位写物理 Pegging 用）
/// </summary>
public sealed class AllocationTaskShare
{
    public string FinalDraftId { get; init; } = string.Empty;
    public long AllocationSequence { get; init; }
    public decimal ComponentQty { get; init; }
}

public sealed class UnscheduledTaskResult
{
    /// <summary>
    /// 需求键。**值域是 `LogicalDemandKey`，不是 `FinalDraftId`** ——
    /// 由 Solver 在 `PhaseFiveCompression.ReportUnscheduled` 处原样回传，
    /// 消费侧必须按需求键域 join（`PeggingOrchestrator` 据此反查 `NetOutputQty`）。
    /// </summary>
    public string DraftId { get; init; } = string.Empty;

    /// <summary>未排原因码。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 该需求的未排量 U（＝ `NetOutputQty` − 已落定份额之和，≥ 0）。
    ///
    /// 【为什么在 Solver 出口给数量】部分落定时「已排量可交、未排量只有键没有量」，
    /// 下游只能按「完全未排」去猜，与「未排 200 可追溯」的基线要求不符。
    /// 与 `PeggingOrchestrator` 侧同名同义，供 Allocation 级三量对账 `Σ ShareQty + Σ U = Σ Q`。
    /// 未填写时按 0 处理（未排量不进份额、不造伪 Task）。
    /// </summary>
    public decimal UnscheduledQty { get; init; }
}

/// <summary>
/// 排程决策解释事实（材料何时到、设备负荷、为何延期等）
/// 1号位必须返回真正原因，不能只给时间结果
/// </summary>
public sealed class ScheduleExplanationFact
{
    public string FinalDraftId { get; init; } = string.Empty;
    public string ObjectType { get; init; } = string.Empty;
    public long? OrderId { get; init; }
    public int? ResourceId { get; init; }
    public string? StageCode { get; init; }
    public string ReasonCode { get; init; } = string.Empty;
    public string? Severity { get; init; }
    public decimal? ImpactHours { get; init; }
    public string? EvidenceJson { get; init; }
}

/// <summary>
/// 求解过程追溯记录（非排程结果，仅决策说明）。
/// 1号位 产出载体，用于承载 SCOPE_REFERENCE_MISSING / SetupResolution / 目标未达 等求解过程决策上下文。
/// 不进 ReasonCode 体系（0号位 Q4 裁决）。
/// </summary>
public sealed class SolveTraceNote
{
    /// <summary>追溯键（如 LogicalDemandKey / DraftId / 资源Id）</summary>
    public string Key { get; init; } = string.Empty;
    /// <summary>原因码（如 SCOPE_REFERENCE_MISSING / TARGET_MISSED / SETUP_RESOLUTION）</summary>
    public string ReasonCode { get; init; } = string.Empty;
    /// <summary>可读说明</summary>
    public string? Message { get; init; }
    /// <summary>级别：Info / Warning / Error</summary>
    public string Level { get; init; } = "Info";
}

public sealed class SolveSummary
{
    public int TotalDrafts { get; init; }
    public int ScheduledCount { get; init; }
    public int UnscheduledCount { get; init; }
    public long ElapsedMs { get; init; }
    public int IssueCount { get; init; }
    public bool UsedRoughCut { get; init; }
}

/// <summary>
/// 一个 Demand 的最终时间事实（0号位 2026-09-29 裁决 §10.2/§10.3）。
/// 与 <see cref="AllocationTaskShare"/> 的区别：Share 是**数量份额**（落真实末端 Task），
/// 本类是**时间结果**（可含无 Routing Stage 的纯时间，无对应 Task）。
///
/// 【载体归属】本类在 `LPS.APS.Core` = **2号位 辖区**（1号位 边界不含 Core）⇒ 2号位 落载体、1号位 填值。
/// </summary>
public sealed class DemandCompletionFact
{
    public string LogicalDemandKey { get; init; } = string.Empty;
    public int MaterialId { get; init; }
    public long AllocationSequence { get; init; }

    /// <summary>供给阈值 Stage（`LogicalProductionDemand.RequiredStageCode`）；无阈值语义时为 null。</summary>
    public string? RequiredStageCode { get; init; }

    /// <summary>
    /// 最终完成时间 = 末端真实 Task.End + 其**之后**各无 Routing Stage 的 LeadTime 之和。
    /// 整条需求无任何真实 Operation Task 时 = 该 Stage 链自起点起的纯 LeadTime 累计。
    /// </summary>
    public DateTime CompletionTime { get; init; }

    /// <summary>供给可用时间。V1 与 <see cref="CompletionTime"/> 同值（已对齐）。</summary>
    public DateTime AvailableTime { get; init; }

    /// <summary>
    /// **完成时间是否可信**（G-3，1号位 2026-10-25 核证回执 §五 点名；Core 契约）。
    ///
    /// 三态：
    ///   · `null` = 未标注 ⇒ **按现状**（向后兼容，旧载荷/未适配生产者一律落此支）；
    ///   · `true`  = 时间由真实事实推出，可作承诺与供给可用时间；
    ///   · `false` = **时间不可信**（缺 LT / 缺合法 WORK 窗口）⇒ 消费侧**不得**把它当承诺、
    ///     不得作为供给可用时间、**不得**据它推下游 `AvailableTime`。
    ///
    /// 【为什么加标注位而不把 `CompletionTime` 改可空】改可空会让所有消费方静默拿到
    ///   `default(DateTime)`（0001-01-01），比现状更糟；`SolveTraceNote` 是自由文本诊断通道，
    ///   不能替代结构化契约（1号位 同件 §五 G-3 理由，我方同意）。
    ///
    /// 【2号位 读侧义务】`false` 的完成时间进入「时间不可信」计数（证据 JSON / 未排口径），
    ///   不进入任何承诺或供给可用时间。
    ///
    /// 【交付状态 2026-10-25】契约骨架已落；**生产侧尚未置值**（恒 `null` ⇒ 零回归）。
    ///   置值随「缺 LT / 缺 WORK 两类判据」在 1号位 消费侧扩判据后同步落码。
    /// </summary>
    public bool? CompletionTimeCertain { get; init; }

    /// <summary>
    /// 做到 `RequiredStageCode` 为止的完成时间（0号位 §10.3「Demand / RequiredStage」两个口径都给，已对齐）。
    /// `RequiredStageCode` 为 null、或该 Stage 不在本物料 Stage 链中时为 null。
    /// </summary>
    public DateTime? RequiredStageCompletionTime { get; init; }

    /// <summary>
    /// 承载 <see cref="CompletionTime"/> 的**末端真实生产 Task** 的 `FinalDraftId`。
    /// 整条需求无真实 Operation Task 时为 **null**（此时不生成 AllocationTaskShare，符合既有闭合不变式）。
    /// </summary>
    public string? TerminalFinalDraftId { get; init; }

    /// <summary>
    /// 被计入本时间的 Stage 级时间节点（**纯内存，非 Task**，对应 0号位 §9.3 的 `StageTimingNode`）。
    /// </summary>
    public IReadOnlyList<StageTimingSegmentFact> StageTimings { get; init; }
        = Array.Empty<StageTimingSegmentFact>();
}

/// <summary>
/// 一段 Stage 级时间（0号位 2026-09-29 裁决 §9.3 的 `StageTimingNode`）。
/// **显式标记 <see cref="NodeType"/>，避免日后被误持久化为真实 Operation Task。**
/// </summary>
public sealed class StageTimingSegmentFact
{
    /// <summary>
    /// 恒为 `"STAGE_TIMING"`（0号位 §9.3 建议的显式标记；**2号位 写库口的判据** ——
    /// §9.2 禁止把无 Routing Stage 伪装成真实 Task，2号位 在 `INSERT INTO [Task]` 前据此拒绝）。
    /// </summary>
    public string NodeType { get; init; } = "STAGE_TIMING";

    public string StageCode { get; init; } = string.Empty;

    /// <summary>本段占用的提前期（小时）。</summary>
    public decimal LeadTimeHours { get; init; }

    /// <summary>
    /// 命中层级（审计用）。**V1 合法值域 = 恰好 4 个**（与 <see cref="StageLeadTimeFact.MatchLevel"/> 同一契约）：
    /// `DEPT_EXACT` / `FACTORY_STAGE_DEFAULT` / `GLOBAL_STAGE_DEFAULT` / `STAGE_LEADTIME_MISSING`。
    /// 4 个之外的值（含旧名 `MATERIAL` / `FAMILY`）⇒ **契约违例**。
    /// </summary>
    public string MatchLevel { get; init; } = string.Empty;

    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
}
