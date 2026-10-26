using LPS.APS.Core.DTOs.Scope;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Enum;

namespace LPS.APS.Core.Dto;

/// <summary>
/// 2号位从 ScheduleContext 裁剪后传给1号位的纯内存请求。
/// 不含 SupplyPool、BOM 原始快照、Ledger、PSA 或任何数据库对象。
/// 符合1↔2接口冻结文档 v1.0_20260814 §2.3 九类输入要求
/// </summary>
public sealed class DomainSolveRequest
{
    public long? ScheduleRunId { get; init; }
    public int PlanVersionId { get; init; }
    public string DomainKey { get; init; } = string.Empty;
    public DateTime? DataCutoffTime { get; init; }
    public DateTime PlanningStart { get; init; }
    public DateTime PlanningEnd { get; init; }

    public IReadOnlyList<LogicalProductionDemand> LogicalProductionDemands { get; init; }
        = Array.Empty<LogicalProductionDemand>();

    /// <summary>
    /// 多层 BOM「任务喂任务」血缘输入（PM 2026-09-10 裁决 R2）：父 LogicalProductionDemand → 子 LogicalProductionDemand
    /// 运行时关系。1号位据此 + 拆批/合批 + Routing 生成真实 TaskDependency（FinalTaskPeggingDraft）。
    /// 子件全库存 / 采购占位（0910 §二十 Case B）时仍产 link 以保留「父需求→子需求」真相，ProducerLogicalDemandKey 为 null；1号位 据此不产该边的 TaskDependency。
    /// </summary>
    public IReadOnlyList<MaterialRequirementLink> MaterialRequirementLinks { get; init; }
        = Array.Empty<MaterialRequirementLink>();

    public IReadOnlyList<AllocationLineage> AllocationLineage { get; init; }
        = Array.Empty<AllocationLineage>();

    /// <summary>
    /// PI 级 ERP 权威剩余事实（AUD-1-D04 / 1号位 §1.3，2026-10-12 投影）。
    ///
    /// 语义：<c>PiRemainingQty = max(PiQuantity − ISNULL(PiReceivedQty,0), 0)</c> —— **全链唯一总量根**
    /// （用户 2026-10-09 定盘；大工艺/MES 工序各层只回答「剩余现在在哪」，任何层不得突破它）。
    /// 2号位 装载层在 <c>LoadPiRemainingFactsAsync</c> 已产出事实集（B-01 身份闸门已剔全部客户订单行），
    /// 此处按 (PI号 × 物料 × 工厂) 全量投影给 1号位，供 B-009/B-010 数量门禁与 V1_4 NEW-04 运行累计账评估。
    /// **一个 PI 一条**，不在此层做「取剩余最大」聚合（那是 1号位 索引口径，且同一 PI 跨物料须按三元组区分）。
    /// ⚠ 不得以本字段冒充 <c>StageFreeEligibleQty</c>（F-02 红线：二者是两个量，混用会静默放大可用量）。
    /// </summary>
    public IReadOnlyList<PiRemainingFact> PiRemainingFacts { get; init; }
        = Array.Empty<PiRemainingFact>();

    /// <summary>
    /// (PI × 物料 × Stage) 级 **Stage 入口基线自由量**（1号位 §1.4(a)／06 接口 §257-262，2026-10-12 投影）。
    ///
    /// 【为什么需要】1号位 B-009 合法量门禁 = <c>min(配置 Max, PI Original, PI Remaining, StageFreeEligibleQty − 同PI同Stage已占)</c>。
    ///   前两项已由 <see cref="PiRemainingFacts"/> 承载；**后两项只有上游（2号位 装载）能给**。
    ///   1号位 侧消费形状 = 其自有记录 <c>PiStageQuantityContext</c>（不入 Core 契约，见其类注释）；
    ///   本列表即该形状的 **Core 入参 1:1 对应**（字段名逐字对齐，见 <see cref="PiStageQuantityBaseline"/>）。
    ///
    /// 【分工切分（1号位 2026-10-10 §1.4(a)）】2号位 认领「入口基线」、1号位 认领「本轮增量累计」
    ///   （<c>PiStageCommitLedger</c>，同 Run 内逐批按身份累加后并入 <c>AlreadyCommittedQty</c>）。
    ///   ⇒ 本列表是**进入本轮求解那一刻**的基线，**不含**本轮新形成的批。
    ///
    /// 【口径出处】1号位 §1.4(a) 两问（是否含 A/B 已执行 / 是否按 NextOperationContext 限定）**已由冻结口径作答**：
    ///   业务基线 v1.10 §1231（同句重复于 03 v1.8 §43／06 v1.35 §257／07 v5.1.10 §4590／10 v1.7 §48／11 v2.1 §50）逐字：
    ///   「<c>StageFreeEligibleQty</c>（PI Position/NextOperationContext、A/B已执行、Q→A/B/C 及已被新增批占用事实限定的
    ///   当前Stage自由可新成批量）」⇒ **是／是**。上界公式本身＝2号位 数量真相域（5号位 2026-10-10 五轮回执 §三@1 交还）。
    ///
    /// 【硬约束】仅对 **C 桶（非连续份额）＋ 有真实 MTS PI ＋ 非 UNLOCATED** 的需求产出；
    ///   缺位置事实或该 Stage 自由量为 0 ⇒ **不产行**（1号位 记「缺 Stage 合法自由量上下文」显式未满足）；
    ///   **绝不以 <c>PiRemainingFact.PiRemainingQty</c> 冒充**（F-02 红线：二者是两个量，混用会静默放大可用量）。
    /// </summary>
    public IReadOnlyList<PiStageQuantityBaseline> PiStageQuantityBaselines { get; init; }
        = Array.Empty<PiStageQuantityBaseline>();

    public IReadOnlyList<RoutingOperation> RoutingOperations { get; init; }
        = Array.Empty<RoutingOperation>();

    public IReadOnlyList<RoutingDependency> RoutingDependencies { get; init; }
        = Array.Empty<RoutingDependency>();

    public IReadOnlyList<OperationResourceEligibility> OperationResourceEligibility { get; init; }
        = Array.Empty<OperationResourceEligibility>();

    /// <summary>
    /// 物料×阶段→默认生产部门 上下文（PM 裁定：最小 B）。
    /// 2号位裁剪当前 Domain 涉及的 (MaterialId, StageCode) 传入；1号位按 (MaterialId, StageCode)
    /// 锁 ProductionDepartmentId 后过滤 Routing 三件套，不得重新推导部门。
    /// </summary>
    public IReadOnlyList<MaterialStageDepartmentContextDto> MaterialStageDepartmentContexts { get; init; }
        = Array.Empty<MaterialStageDepartmentContextDto>();

    /// <summary>
    /// 无 Routing 阶段的提前期（PM《无Routing Stage统一处理建议》§九/§十，2026-09-28：
    /// **2号位 装载 → 1号位 消费**）。
    ///
    /// 语义：`StagePath 决定阶段是否存在及顺序；Routing 决定该 Stage 内部有无小工序；
    /// Routing 不存在 ≠ Stage 不存在。` 对「有效 Stage 但无 RoutingOperation」的 (物料, 阶段)，
    /// 2号位 按三级命中顺序（部门级 `DEPT_EXACT` → 工厂级 `FACTORY_STAGE_DEFAULT` → 全局级 `GLOBAL_STAGE_DEFAULT`）
    /// 解析 `StageLeadTimeParam` 后给出提前期；**走完三级仍未命中**则记 `STAGE_LEADTIME_MISSING` 并**按 3 天兜底**
    /// （PM 2026-09-29 裁决）——即本列表对每个缺口 (物料,阶段) **都有 Fact**，1号位 不必自备兜底值。
    /// ⚠️ **2026-09-29 用户更正**：本节此前写的「「命中到参数行但折算值 ≤ 0」**同样视同未命中**、同样给 72h 兜底」
    /// **已作废**。**命中就是命中** —— 折算值为 0（或负）**按命中产出**：`LeadTimeHours = 0`、
    /// `MatchLevel` = 实际命中级（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00` ⇒ 值 0 + `FACTORY_STAGE_DEFAULT`）。
    /// ⇒ 消费侧认 `MatchLevel`，但**不要**假定「值 0 ⇒ 一定是兜底」：值为 0 也可能是一条真实命中的参数。
    /// 1号位 据此为该阶段保留时间与前后依赖（不因无 Routing 而丢弃该阶段）。
    ///
    /// 注：Stage 存在性由 `MaterialStageDepartmentContexts`（含 (MaterialId, StageCode)）承载；
    /// **Stage 顺序由 `StageSequenceFacts` 承载**；本字段只补「无小工序时该阶段占多少时间」。
    /// </summary>
    public IReadOnlyList<StageLeadTimeFact> StageLeadTimes { get; init; }
        = Array.Empty<StageLeadTimeFact>();

    /// <summary>
    /// **逐 Stage 计划加工量**（G-1，1号位 2026-10-25 核证回执 §五 点名；1↔2 契约）。
    ///
    /// 【为什么需要】`LogicalProductionDemand.PlannedProcessQty` 是需求级单值，现被**全 Stage 复用**——
    ///   一道 Stage 干完、下一道仍按整批量算 ⇒ 多 Stage 的加工负载被重复计入。
    ///   本列表按 (需求片, Stage) 给量，供 1号位 按各 Stage 实际取值。
    ///
    /// 【键必须是 `LogicalDemandKey` 而非 `MaterialId`】同一物料可被切成多个需求片
    ///   （连续份额 / 拆批 / 跨域），各片良率与起点可不同；用物料做键会把不同片的量**合并成一个**
    ///   （重演 AUD-1-R02 类「静默同质化」）。
    ///
    /// 【四条硬约束（2026-10-25 我方会签，缺一即不满足）】
    ///   1. **整表为空 ⇒ 全部沿用需求级 `PlannedProcessQty`**，零行为变化。
    ///   2. **表非空时，「缺行」必须按 Stage 链是否存在分两种处置，不得混为一谈**：
    ///      ① **该需求物料在 `StageSequenceChains` 里没有链**（采购件 / 无 StagePath）⇒ 不存在逐 Stage 语义，
    ///         沿用需求级量（与投影前一致，不构成「把整链量带到单 Stage」）；
    ///      ② **物料有链、而本需求在表内一行都没有**（`StartStageCode` / `RequiredStageCode` 不在链上、
    ///         或净量 ≤ 0）⇒ **该需求 Stage 量未匹配 ⇒ 按 Issue / 未排处理，不得回落需求级量** ——
    ///         这正是 0号位《03_R04 交叉核证》§三-2 逐字要求「Stage 链一致、匹配完备，未匹配保留 Issue/未排」。
    ///      同样，**表内有该需求但缺某 Stage 行 ⇒ 该 Stage 不注入**、按未排/Issue 计数。
    ///      我方装载侧对 ①② 逐类计数并落日志（含 `NonContinuationMidChainDemandCount` 诊断），不静默。
    ///   3. 本字段**不改**既有净量、PI 供给、已完成 Stage 语义（`PlannedProcessQty` 原样保留、口径不动）。
    ///   4. 数值是**该 Stage 的计划加工量（投入侧）**：终点 Stage 的净产出 = `NetOutputQty`，
    ///      逐段由后向前按**该段良率**反算投入（上段净产出 = 下段投入）⇒ 首段投入 = 净量 ÷ ∏各段良率。
    ///      良率三级 = `(物料编码, StageCode)` 精确 → 物料级默认 → 100
    ///      （**实现类 = `PlanningYieldResolver.Resolve`**；重复键取最低；越界值按未配置）。
    ///      ⚠ **不要**去 `SupplyPool.PlanningYieldPercent` 找这套口径：它跳过 `StageCode` 非空的规则、且命中第一条
    ///      就返回，是**另一套**（需求级 `PlannedProcessQty` 沿用它，本表不用）。按旧文档字面实现会静默丢掉
    ///      Stage 级良率 —— 正好是本表最核心的那一半（1号位 2026-10-25 G-1 回执 §二）。
    ///      物料无任何良率规则时各段系数为 1 ⇒ 首段投入＝净量。
    ///   5. **链内 `StageCode` 唯一**（`EffectiveStageChainResolver` 按 `StageCode` 分组取 `MIN(StageSeq)`）⇒
    ///      同一 (需求键, Stage) 不会出现两行，消费侧按该键建字典不会撞键、无需去重（1号位 2026-10-25 G-1 回执 §一）。
    ///
    /// 【与既有 `PlannedProcessQty` 的关系（1号位 2026-10-25 G-1 回执 §三 收窄）】
    ///   单 Stage 链时本行数值与需求级 `PlannedProcessQty` **同式同舍入**
    ///   （`Math.Round(净量 × 100 ÷ 良率, 4)`）**恒真**；但**「同值」是有条件的**：
    ///   仅当该物料**没有** `(物料编码, StageCode)` 级良率规则、且物料级规则无冲突重复时才同值。
    ///   · 有 Stage 级规则 ⇒ 本表用它、需求级量看不到它（`PlanningYieldPercent` 跳过 Stage 级）；
    ///   · 物料级规则有重复且值不同 ⇒ 本表取最低、需求级量取第一条。
    ///   ⇒ 发布 Stage 级良率规则之日，单 Stage 需求的量变是**预期**，不是回归。
    ///
    /// 【交付状态 2026-10-25】契约 + 实值投影均已落码（`DemandStageProcessQuantityProjector` ＋
    ///   `PeggingOrchestrator.BuildStageProcessQuantitiesAsync`）；本域无需求/无链时仍传空表。
    /// </summary>
    public IReadOnlyList<DemandStageProcessQuantity> StageProcessQuantities { get; init; }
        = Array.Empty<DemandStageProcessQuantity>();

    /// <summary>
    /// Stage 顺序事实（PM《BOM取用_Pegging_Stage_Routing完整链路说明》§七，2026-09-28：
    /// `StageSeq` 是排程大工艺顺序的唯一权威来源）。
    ///
    /// 【为什么需要】1号位 消费 `LogicalProductionDemand.RequiredStageCode`（供给阈值 Stage）时，
    ///   「做到该阶段为止」须判断哪些 Stage 在该阶段**之前** ⇒ **必须有顺序**；
    ///   `StageLeadTimes`（无 Routing 阶段保留前后依赖）同理。
    ///   此前 `MaterialStageDepartmentContexts` 只有 (MaterialId, StageCode, Dept) 三字段、**无任何顺序载体**，
    ///   致上述两个能力无处落地。
    ///
    /// 【形态说明】本字段给的是**每个物料一条完整有序链**（`StageSequenceChain`，每步带 `StageSeq` 数值），
    ///   不预判消费侧建图形态（StageDependency / 有序列表均可）—— 满足 1号位 2026-09-28 回执 §二 的撤回前提。
    /// 【粒度】同一物料在本次 BOM 里 ROOT ∪ EDGE 取并集，同一 StageCode 取最小 StageSeq。
    /// 【空值语义】空 = 无 StagePath 数据（数据缺口 / 采购件），1号位 按无顺序信息处理。
    /// </summary>
    public IReadOnlyList<StageSequenceChain> StageSequenceChains { get; init; }
        = Array.Empty<StageSequenceChain>();

    public IReadOnlyList<MaterialAvailabilitySlice> MaterialConstraints { get; init; }
        = Array.Empty<MaterialAvailabilitySlice>();

    public IReadOnlyList<ResourceDefinition> Resources { get; init; }
        = Array.Empty<ResourceDefinition>();

    /// <summary>
    /// **纯设备** `ResourceId` 白名单（2号位 装载侧投影 → 1号位 消费）。
    ///
    /// 【语义】`null` ⇒ **未投影** ⇒ 1号位 保持现行行为（资源集合含人工槽合成键），**零回归**；
    ///   非 null ⇒ 只列**纯设备** `ResourceId`（已剔除人工槽合成键 `ManualSlotResourceOffset + ManualSlotId`）。
    ///   ⚠ **空集合 ≠ null**：空 = 本 Run 已投影、但一个纯设备都没有；null = 未投影。
    ///
    /// 【键域】与 <see cref="ResourceDefinition.ResourceId"/> **同域**，原样透传；
    ///   1号位 **不做任何「资源身份判别」、不硬编码任何阈值**（其回执 §四 逐字）。
    ///
    /// 【消费语义（1号位 2026-10-16 回执 §四 点名）】非 null 时按其**分别**算「含人工槽」与「纯设备」
    ///   两个排序键，**默认仍取含人工槽那个** ⇒ 默认行为不变。用于 R-2「**设备**有效稼动」口径的留痕增强：
    ///   冻结 v1.13 R-2 = 设备有效稼动，而资源集合含人工槽 ⇒ 单一排序键无法区分两个口径。
    ///
    /// 【为什么判据在 2号位】合成键由 2号位 生成（`SolverResourceProjection`）；
    ///   1号位 侧 `ManualSlot` / `1_000_000_000` grep = 0 命中 ⇒ 不得由 1号位 自判。
    ///
    /// 【契约归属】本字段由 1号位 点名落地（1↔2 契约字段只按 1号位 点名扩）；
    ///   声明由双方共同确认，**填充由 2号位 装载侧做、消费由 1号位 Solver 做**。
    /// </summary>
    public IReadOnlyList<int>? DeviceResourceIds { get; init; }

    public IReadOnlyList<ResourceCalendarSlot> CalendarSlots { get; init; }
        = Array.Empty<ResourceCalendarSlot>();

    /// <summary>
    /// **工厂 / 部门级合法 WORK 窗口**（G-2，1号位 2026-10-25 核证回执 §五 点名；1↔2 契约）。
    ///
    /// 【为什么需要】<see cref="CalendarSlots"/> 只承载**资源级**日历，工厂/部门作用域在投影前被抹掉；
    ///   而「无 Routing 的 Stage 要占多少小时」只能用该 Stage 所属部门及其工厂的合法 WORK 窗口逐片累计。
    ///   本字段与 <see cref="CalendarSlots"/> **并存不替换**：后者仍只用于资源可排性。
    ///
    /// 【口径（2026-10-25 我方会签；落地时按 RC-001 收紧为「2号位 合成、1号位 只消费」）】
    ///   1. **只给合成后的生效 WORK 窗口**（绝对时间），不是原始上下级行。合成规则与资源三级日历**同一条**：
    ///      有事实的层参与、**WORK 取交集**、**OFF 作例外裁剪**（含 `IsAuthorizedOverride` / `AllowLowerOverride`
    ///      双向授权），**无事实的层不参与（＝继承上级）**；**绝不相加**、**绝不补 8h / 7×24**。
    ///      依据：规则清单 v1.7 RC-001「**2号位 合成合法能力窗口、1号位 只消费**」。
    ///      ⚠ 因此 1号位 侧**不需要**、也**不得**再自行实现「部门有事实用部门、否则用厂级」的回落 —— 该回落
    ///      在两层都有事实时是错的（会把工厂级 OFF 当天用部门 WORK 复活，违反 RC-006「OFF 不得被上级复活」）。
    ///   2. 1号位 **只**用它推进**无 Routing Stage** 的时间；**不得**用它推导资源可排性（那是 <see cref="CalendarSlots"/>）。
    ///   3. **按 `(FactoryId, DepartmentId)` 精确取用，无任何回落分支**：本域部门上下文能解析出的每个部门，
    ///      都有一组专属行（该部门自身无事实时，这组行＝其工厂行的物化副本，即「继承」已在投影侧完成）。
    ///      **部门取源 = `MaterialStageDepartmentContexts`** —— 本字段的部门集合正是从它取
    ///      `ProductionDepartmentId` 去重而来（`PeggingOrchestrator.LoadDepartmentWorkWindowsAsync`），
    ///      **不是** `StageSequenceChains[].ProductionDepartmentId`。后者是另一条取源、类型还是 `int?`；
    ///      两者不一致时消费侧会去查一个本字段**根本没投影**的部门，拿到空列表后按「该日无工时」静默跳过，
    ///      而且自己查不出来（1号位 2026-10-25 G-2 回执 §二）。按 `contexts` 取则「消费侧查的部门 ⊆ 已投影部门」
    ///      是**结构性保证**，不依赖两侧数据一致。
    ///   4. **`DepartmentId = null` 的行不是排程通道**（1号位 2026-10-25 G-2 回执 §三 定）：
    ///      它是厂级口径，只用于「该厂整期有没有事实」的对账参考，**不得**用来给「缺部门上下文」的需求兜底排程 ——
    ///      那等于用厂级日历把一个部门未知的 Stage 推着走完、且全程无痕，与 PM 2026-09-28 §三
    ///      「`StageCode` 不能直接决定生产部门，必须通过 `MaterialStageDeptContext` 确定」冲突。
    ///      需求缺部门上下文 ⇒ 消费侧仍走既有 `MISSING_PRODUCTION_DEPARTMENT_CONTEXT` Fail Closed。
    ///   5. **某 scope 某日不在列表 ≠ 缺数据**，＝ 该日无合法工时（不得回落 8h／7×24）。
    ///      整表为空 = 本域**没有任何可投影的工厂/部门归属**（或无日历事实）；消费侧按「输入契约缺陷」Fail Closed，
    ///      不得当成「全程不推进」静默通过。
    ///   6. **投影上界 = `PlanHorizonEnd + 180 天`**，与资源日历（`CalendarSlots`）**同一个常量、同一个值**
    ///      （`SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays`）。正排不受 `PlanningEnd` 约束，日历须铺到
    ///      「兜底日期」；两处各写一个数时，无 Routing 的 Stage 会在 `PlanHorizonEnd` 处集中「排不下」，
    ///      而那不是产能问题、是投影截断（1号位 2026-10-25 G-2 回执 §四）。
    ///      ⚠ 本字段止于该上界；<see cref="PlanningEnd"/> 仍是 90 天有限产能求解窗口，两者不混。
    ///   7. **两种「查不到」的成因以只读计数随载荷给出**（1号位 2026-10-25 G-2 回执 §五 乙）：
    ///      <see cref="DepartmentWorkWindowConflictDayCount"/> 与 <see cref="DepartmentsWithoutFactoryCount"/>。
    ///      这两种属**数据侧缺陷**，按 RC-007 落到「该 scope 该日无工时」即可，**不阻断 Run**（阻断会让一条部门
    ///      主档缺行停掉整个厂的排程）。计划期非法与投影截断属**投影缺陷**，已在装载侧 Fail Closed、
    ///      不产出本载荷，故不在计数之列。
    ///
    /// 【覆盖现状 2026-10-25 实测】源表 `ProductionWorkCalendarSlot` 69 行全部 `IsActive=1`，主体为
    ///   「周一至周五 08:00–17:00」循环行；有日历的 5 个厂下挂 280/287 个活动部门（97.6%）⇒ **厂级兜底可用**；
    ///   **部门级仅 963、1098 两行且待 5号位 确认是否测试数据** ⇒ 依赖部门级 OFF 的用例须标 `EVIDENCE_PENDING`。
    ///   厂 5 `EffectiveTo=2026-10-28`，此后该厂无事实。
    ///
    /// 【交付状态 2026-10-25】契约 + **装载投影均已落**（`DepartmentWorkWindowProjector`，按本域
    ///   `MaterialStageDepartmentContexts` 的部门/工厂裁剪、逐日摊平到 `PlanningStart`–`PlanningEnd`）；
    ///   1号位 消费侧置值前，本字段已带真实数据。
    /// </summary>
    public IReadOnlyList<DepartmentWorkWindow> DepartmentWorkWindows { get; init; }
        = Array.Empty<DepartmentWorkWindow>();

    /// <summary>
    /// **只读诊断计数**：同层矛盾被整日拒绝的「scope × 日」数（G-2 诊断位，1号位 2026-10-25 G-2 回执 §五 乙）。
    ///
    /// 【为什么在载荷里】消费侧看到的「该日查不到窗口」至少四种成因、在窗口列表上**完全同形**：
    ///   真无日历事实 / 计划期非法 / 部门主档缺 `FactoryId` / 同层矛盾被整日拒绝。
    ///   后两种是数据侧缺陷（该回 5号位 核源数据），不阻断 Run；不单列计数就只能记成「无工时」。
    ///   `0` = 本域无此类；**不要**把它当排程判据，它只用于出口留痕。
    /// </summary>
    public int DepartmentWorkWindowConflictDayCount { get; init; }

    /// <summary>
    /// **只读诊断计数**：本域部门在 `ProductionDepartment` 主档查不到 `FactoryId` ⇒ 无法投影、整期无窗口的部门数
    /// （G-2 诊断位，1号位 2026-10-25 G-2 回执 §五 乙）。`0` = 无此类；同样只作留痕、不作排程判据，
    /// **不得**据此给这些部门回落厂级行（见 <see cref="DepartmentWorkWindows"/> 口径第 4 条）。
    /// </summary>
    public int DepartmentsWithoutFactoryCount { get; init; }

    public IReadOnlyList<ResourceEligibilityDefinition> ResourceEligibility { get; init; }
        = Array.Empty<ResourceEligibilityDefinition>();

    public IReadOnlyList<ExecutionConstraint> ExecutionConstraints { get; init; }
        = Array.Empty<ExecutionConstraint>();

    /// <summary>
    /// **契约②：订单保护事实**（2号位 → 1号位）。
    ///
    /// 与 <see cref="ExecutionConstraints"/> **两条不同的契约**（冻结规则清单 §十四 L-001「语义不得混同」）：
    ///   · <see cref="ExecutionConstraints"/> = **硬锚点，锁物理位置** —— 收 Frozen（窗口层＋执行层）与执行事实；
    ///   · **本字段** = **订单级保护事实，不锁位置** —— 收 **Firm**（`PM回复0923-1` §三.2「Firm 不是锁」）。
    ///
    /// 【投结论、不投判据】键用 `OrderId`（1号位 内存键），**不投** `OrderCanonicalId`/`TaskId`
    ///   —— 既有红线：`RunScope` 注明「`OrderCanonicalId`/`TaskId` 已转 1号位 内存键，long 官方 Id 不进 Solver 契约」
    ///   （`APS_V1_白天候选各号位配合事项_1号位致2号位_回执_v1.0_20260924` §三 预提1）。
    ///   窗口内/外判定同样由 2号位 算完（同件 §三 预提2：1号位 纯内存、不持有 Base 旧计划 ⇒ 结构上做不了）。
    ///
    /// 【V1 实产范围】键 `OrderId` 非空且**有保护**的订单才投影；`ProtectionType = None` 不投影
    ///   （`APS_V1_订单保护事实载体_2号位致1号位_收敛回执_v1.0_20260923` §五）。
    /// </summary>
    public IReadOnlyList<OrderProtection> OrderProtections { get; init; }
        = Array.Empty<OrderProtection>();

    public SolverStrategySnapshot StrategySnapshot { get; init; } = new();

    public CandidateContext? CandidateContext { get; init; }

    /// <summary>
    /// 局部重排范围投影（白天候选 Run 专用；null = FULL 语义）。
    /// 2号位 由 ScheduleRun.ScopeJson 反序列化后按本块键体系投影；
    /// OrderCanonicalId / TaskId 已转 1号位 内存键，long 官方 Id 不进 Solver 契约。
    /// InScopeLogicalDemandKeys 按冻结 v1.7 §69 留空：影响范围由 1号位 从变化 Seed 动态传播推导，
    /// 2号位 只转 ChangeSeed/TaskTarget/ChangedResourceIds（不预填可移动集合 / Scope 外 Anchor·Block）。
    /// </summary>
    public RunScope? RunScope { get; init; }

    /// <summary>
    /// 前序 Domain 成功后的共享 Resource 占用块（FULL §9）。
    /// 1号位将其作为不可用时间窗阻挡后续 Domain 在真实共享 Resource 上重叠占用。
    /// Candidate 的对应物是 CandidateContext.ExternalDomainResourceBlocks（§11）；本字段 FULL 专用、Candidate 时为 null。
    /// </summary>
    public IReadOnlyList<ResourceBlock> UpstreamDomainResourceBlocks { get; init; }
        = Array.Empty<ResourceBlock>();
}

/// <summary>
/// Pegging Allocation到FinalTask的追溯信息（接口冻结§2.3第3类）
/// 不等同于PeggingSupplyAllocation持久化表
/// </summary>
public sealed class AllocationLineage
{
    public long AllocationSequence { get; init; }
    public string DemandKey { get; init; } = string.Empty;
    public int MaterialId { get; init; }
    public string SupplyType { get; init; } = string.Empty;
    public string SupplyKey { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public DateTime? AvailableTime { get; init; }
}

/// <summary>
/// 某个逻辑生产需求的材料，在什么时间有多少数量真正可用（接口冻结§2.3第6类）
/// 必须支持多段Quantity-Time：40件15日+60件17日，不能压成100件17日
/// </summary>
public sealed class MaterialAvailabilitySlice
{
    public long AllocationSequence { get; init; }
    public int MaterialId { get; init; }
    public int FactoryId { get; init; }
    public decimal Quantity { get; init; }
    public DateTime AvailableTime { get; init; }
    public string? SourceType { get; init; }
    public string? SourceKey { get; init; }
    public string? Commitment { get; init; }
    public string? Confidence { get; init; }
}

/// <summary>
/// (PI × 物料 × Stage) 级 **Stage 入口基线自由量**（2号位 装载 → 1号位 数量门禁；1号位 §1.4(a)，2026-10-12）。
///
/// **与 1号位 <c>PhaseTwoInitialScheduler.PiStageQuantityContext</c> 逐字段 1:1 对应（同名同序）**：
///   1号位 按 (PI 号, 物料ID, StageCode) 索引 <see cref="DomainSolveRequest.PiStageQuantityBaselines"/> 即可
///   直接构造其消费形状，**无需任何换算**（唯一差异：1号位 侧 <c>AlreadyCommittedQty</c> 有默认值 0m，本侧必填）。
///
/// ⚠ 本类型是**运行时入参**：不落任何物理列／表、不入冻结 DDL，也不改 <c>[Order]</c>／TaskSplitRuleConfig 等实体
///   （与冻结 DDL 08 §2.14 同向——该处明示此类「保守批目标／上限**只在 Run 内使用，不落入本表**」）。
/// </summary>
public sealed class PiStageQuantityBaseline
{
    /// <summary>生产指示号（= 需求 <c>LogicalProductionDemand.ProductionInstructionNo</c>）。</summary>
    public string ProductionInstructionNo { get; init; } = string.Empty;

    /// <summary>物料ID（= 需求 <c>LogicalProductionDemand.MaterialId</c>）。</summary>
    public int MaterialId { get; init; }

    /// <summary>目标 Stage（= 需求 <c>LogicalProductionDemand.StartStageCode</c>；批形成先于 Routing 展开，故取需求级起点）。</summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>
    /// 该 (PI × 物料 × Stage) 的 **Stage 合法自由可用量（毛量口径，未扣已占）**。
    ///
    /// 取值 = <c>min(该 Stage 待加工位置量, NextOperationContext 归于该 Stage 的位置量)</c>——
    ///   二者是**同一物理池的两个视图**（5号位 2026-10-10 五轮回执 §三@1：位置量与工单执行剩余量「可能重叠」），
    ///   故取小保守，不做加法。
    /// 位置量只取 <c>FIRST_STAGE_PENDING</c> / <c>STAGE_WAITING</c> 两类；
    ///   <c>XC</c> / <c>INTERPLANT_TRANSIT</c> / <c>UNLOCATED</c> 不在任何 Stage 上 ⇒ **不计入**。
    /// </summary>
    public decimal StageFreeEligibleQty { get; init; }

    /// <summary>
    /// 该 (PI × 物料 × Stage) 在**入口**已被占用的量 = 既有 MES 执行连续份额（A/B）在该 Stage 的执行剩余量。
    ///
    /// 门禁实际可用 = <see cref="StageFreeEligibleQty"/> − 本值 − 1号位 本轮增量（<c>PiStageCommitLedger</c>）。
    /// 已 clamp 到 <c>[0, StageFreeEligibleQty]</c>：位置量与执行量可能重叠，且已占大于毛量时门禁会出负
    ///   （= 位置不可信），按保守取毛量为上限并留痕，**不产负值**。
    /// </summary>
    public decimal AlreadyCommittedQty { get; init; }
}

/// <summary>
/// 一次ScheduleRun冻结给1号位使用的Solver参数包（接口冻结§2.3第7类）
/// 1号位不需要Demand排序、库存规则等，那些已由2号位执行完成
/// </summary>
public sealed class SolverStrategySnapshot
{
    public long? StrategyProfileVersionId { get; init; }
    public long? ParameterSetVersionId { get; init; }

    /// <summary>已激活消费点用的裁剪参数（执行用子集：AllowSplit/AllowMerge/SchedulingDirection + P1-02 B 组）。</summary>
    public FiniteCapacityParameters Parameters { get; init; } = new();

    // ── P1-02：⑤⑥ 全字段整块透传（PM/2号位 2026-09-14 拍板：整块引用，避免同 ⑤⑥ 逐批平铺）──
    /// <summary>P1-02：⑤ Solver 策略整块（Mode/Bottleneck/OnTimeTarget/Split/Setup/StageOverlap/AllowMerge）。<br/>
    /// 1号位从整块读取正式参数（瓶颈阈值 Bottleneck/OnTimeTarget/Split原始值/Setup/StageOverlap），
    /// 替换换不了的硬编码。强类型零漂移，2号位不再逐批投影。</summary>
    public SolverStrategyBlock SolverStrategy { get; init; } = new();

    /// <summary>P1-02：⑥ Candidate 技术 Guardrail 整块（限时/传播/警告/TopN/拆分候选）。<br/>
    /// 1号位从整块读取（NormalMs/SoftMs/LocalHardMs/MaxRepairAttempts/MaxPropagationRounds/ResourceTopN/WarnOnlyOnMaxImpacted）。
    /// 强类型零漂移。</summary>
    public CandidateGuardrailBlock CandidateGuardrail { get; init; } = new();

    /// <summary>⑦ 产品转换换型规则（v1.2 §九/§十）。2号位 按本 Run RuleSetVersionId 从 SetupTransitionRule 表装载，
    /// 筛 IsActive 投影后按 Domain（ProductionDepartmentId + StageCode）裁剪；1号位 SetupOptimizer.BuildRuleLookups 消费
    /// （EXACT/DEFAULT 命中，无规则 0 分钟，禁止 RoutingOperation.SetupTime 兜底）。</summary>
    public IReadOnlyList<SetupTransitionRuleSnapshot> SetupTransitionRules { get; init; }
        = Array.Empty<SetupTransitionRuleSnapshot>();

    /// <summary>⑧ 批量策略（Batch Policy）规则（0号位 2026-10-07《未命名的Markdown文件 (7).md》§四/§十/§十一）。
    /// 粒度 = <c>Material + ProductionDepartment</c>（冻结 B-001）。**正式命中必须是明确部门**：
    /// <c>ProductionDepartmentId == null</c> 仅历史兼容，**不得**当 Material 级默认（AUD-1-004 已删除该兜底）。
    /// 2号位 数据源 = **本 Run 冻结策略包的 ⑧ 块**（<c>ParameterSetVersion.ContentSnapshotJson.BatchPolicy</c>，
    /// 经 <c>FrozenStrategySnapshotProvider.DeserializeBatchPolicy</c> 反序列化）；
    /// **装载时不回读 <c>TaskSplitRuleConfig</c> 物理表**（该表只在策略包「发布」那一刻被读一次）。
    /// ⚠ 2026-10-13 更正：本注释原写「**整块透传、不裁剪**」，**已失效** —— 2026-10-12 起装载期按域裁剪
    /// （<c>PeggingOrchestrator.BatchPolicies = TaskSplitRuleConfigProjector.CropToDomain(...)</c>，
    /// 按 <c>demandMaterialIds</c> 单轴裁，保留全部部门变体，≈85×）。⇒ **列表里看不到某物料 = 域裁剪结果，不是装载缺失。**
    /// ⚠ 本属性**只承载「已生效规则行」**，**无状态位** ⇒ 单看它无法区分 MISSING 与 INVALID
    /// （冻结 06 **v1.36 <c>:17</c>**／规则清单 **v1.7 <c>B-013</c>** 要求 <c>VALID/MISSING/INVALID</c> 三态）。
    /// **三态＋不可用载体已于 2026-10-13 落码 = <see cref="BatchPolicyStates"/>（同层，本 Run 逐业务键）**
    /// ⇒ 消费侧**不得**再把「本列表为空／查不到行」一律读作 MISSING（06 <c>:17</c> 逐字已判违规）。
    /// 本列表仍为 **Valid 行的规则参数来源**（载体只给状态，不给参数）。
    /// 1号位 <c>PhaseOneConstraintBuilder</c> 收进 <c>ConstraintContext.ExecutionBatchPolicies</c>，
    /// 由 <c>PhaseTwoInitialScheduler</c> 做 C 桶 Batch Decision。
    /// 空 ⇒ **C 桶（新增需求）缺策略 fail-closed**（<c>BATCH_POLICY_MISSING</c>，P0-01 / svn r13558）；
    /// A/B 既存执行批不受本判据管辖（恒 1 批）。**1号位 不得自造全局默认策略**（§十一 第 3 条）。</summary>
    public IReadOnlyList<BatchPolicyRuleSnapshot> BatchPolicies { get; init; }
        = Array.Empty<BatchPolicyRuleSnapshot>();

    /// <summary>⑧块**三态＋不可用**最小载体（2→1 最小技术合同，2026-10-13；2号位 择定形态）。
    ///
    /// **语义**：非 <c>null</c> ⇒ 2号位 已按本 Run 业务键（<c>MaterialId + ProductionDepartmentId</c>，
    /// 覆盖本域需求物料含下阶 BOM）**逐键**给出状态与真实匹配来源；**键未被覆盖 ⇒ 按「状态不可获得」处置**
    /// （不得推断）。
    /// <c>null</c> ⇒ **接口未传递状态**（旧载荷／未适配的生产者）⇒ 消费侧**保持现行口径**
    /// （精确命中 ⇒ <c>Valid</c>；其余 ⇒ 状态不可获得／<c>Unknown</c>）。现行口径**从不产出 <c>Missing</c>**
    /// ⇒ **不会误启 <c>Q_C</c>**，故 <c>null</c> 路径**合规且零回归**，但**不得据以取 <c>Q_C</c> 兜底**。
    ///
    /// **与 1号位 侧类型的映射**（<c>LPS.APS.Scheduling/Solvers/PhaseTwoInitialScheduler.cs</c>，
    /// <c>internal enum BatchPolicyMatchState</c>）：<c>Valid→Valid</c>、<c>Missing→Missing</c>、
    /// <c>Invalid→Invalid</c>、<c>Unavailable→Unknown</c>（出口码 <c>BATCH_POLICY_STATE_UNAVAILABLE</c>）。
    /// 1号位 的 <c>Missing</c> 分支**已落码但今日不可达** —— 其注释逐字：「<c>Missing</c> 分支**保留**
    /// （**载体到位后由 2号位 可信状态激活**）—— 今日不可达」⇒ **本属性即该「可信状态」**。
    ///
    /// **为什么与 <see cref="BatchPolicies"/> 并存而不是替换它**：06 v1.36 <c>:17</c> 只允许「**最小 DTO**」，
    /// 且要求「**不得**把所有物料规则发送 Solver」。本载体只增加 <c>(键, 状态, 来源)</c> 三元组
    /// （**不含规则参数**），规则参数继续由 <see cref="BatchPolicies"/> 承载 ⇒ **纯增量、向后兼容**：
    /// 未适配的消费侧忽略本属性即回到旧行为，**零回归**。
    ///
    /// **判定口径**（<c>TaskSplitRuleConfigProjector.BuildStates</c>）：VALID = 精确键命中且在**本 Run 生效时点**
    /// 的生效窗口 <c>[EffectiveFrom, EffectiveTo)</c> 内；INVALID = 本键有规则但不在窗口内（发布后失效、未治理隔离）；
    /// MISSING = 经完整核对确无匹配规则（含 ⑧块整体为空、本部门无命中而别的部门有记录）；
    /// UNAVAILABLE = 装载失败／未传递状态。
    ///
    /// ⚠ 消费侧口径**变更点**：旧口径「<c>BatchPolicies</c> 为空或查不到行 ⇒ 一律 MISSING」已判违规
    /// （06 <c>:17</c> 逐字「不能仅凭旧 <c>BatchPolicies</c> 规则行集合为空或查不到行就无差别返回 NULL」）。</summary>
    public IReadOnlyList<BatchPolicyStateSnapshot>? BatchPolicyStates { get; init; }
}

/// <summary>
/// Candidate Run专用上下文（接口冻结§2.3第9类）
/// FULL Run时为null
/// </summary>
public sealed class CandidateContext
{
    /// <summary>
    /// Base 稳定锚点 = ScheduleRun.BasePlanVersionId（创建 Run 时由 3号位冻结的当前 ACTIVE PlanVersion）。
    /// PM 2026-09-07 P0-04 2.1：Candidate 前后比较基线，2号位运行期必须始终用此值，不得中途再查「此刻最新 ACTIVE」。
    /// </summary>
    public int? BasePlanVersionId { get; init; }

    /// <summary>变化 Seed：Candidate Pegging 相对 Base ACTIVE 发生变化的逻辑生产需求键（DemandKey/AllocationSequence/LogicalDemandKey 之一）</summary>
    public IReadOnlyList<string> ChangeSeedKeys { get; init; } = Array.Empty<string>();

    /// <summary>其它 Domain 当前 ACTIVE 在共享 Resource 上的不可移动占用（PM 0907：不是 Quantity-Time）</summary>
    public IReadOnlyList<ResourceBlock> ExternalDomainResourceBlocks { get; init; } = Array.Empty<ResourceBlock>();
}

/// <summary>
/// 其它Domain ACTIVE共享资源占用的不可用时间窗
/// PM 2026-09-07 P0-04：Candidate 的外部 Domain 阻挡块必须携带来源域/版本/不可移动语义，
/// 与 FULL 的 UpstreamDomainResourceBlocks 共用本结构（FULL 时 SourceDomainKey/SourcePlanVersionId 可空）。
/// </summary>
public sealed class ResourceBlock
{
    public int ResourceId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string Reason { get; init; } = string.Empty;

    /// <summary>来源 DomainKey（外部 ACTIVE 阻挡块的归属域；FULL 前序 Domain 时也有值）</summary>
    public string? SourceDomainKey { get; init; }

    /// <summary>来源 PlanVersionId（外部 ACTIVE 阻挡块的归属版本）</summary>
    public int? SourcePlanVersionId { get; init; }

    /// <summary>不可移动标记（PM 0907：Candidate 外 Domain 阻挡块 Immutable=true，1号位不得挤动）</summary>
    public bool Immutable { get; init; } = true;
}

/// <summary>Task 间依赖意图（排程前保留，用于排程后生成 PhysicalPeggingDraft）</summary>
public sealed class TaskDependencyDraft
{
    public string FromDraftId { get; init; } = string.Empty;
    public string ToDraftId { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public long AllocationSequence { get; init; }
}

public sealed class ResourceDefinition
{
    public int ResourceId { get; init; }
    public string ResourceCode { get; init; } = string.Empty;
    public string FactoryCode { get; init; } = string.Empty;
    public decimal Capacity { get; init; }
}

public sealed class ResourceCalendarSlot
{
    public int ResourceId { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool IsAvailable { get; init; }
}

/// <summary>
/// 逐 Stage 计划加工量（G-1 元素；消费方 = <see cref="DomainSolveRequest.StageProcessQuantities"/>）。
/// 四条硬约束见该属性的文档注释 —— 尤其「缺行不得以需求级量补位」。
/// </summary>
public sealed class DemandStageProcessQuantity
{
    /// <summary>需求片键（= `LogicalProductionDemand.LogicalDemandKey`；**不用物料 ID 作键**）。</summary>
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>目标 Stage（= 该需求 Stage 链上的 StageCode）。</summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>该 Stage 的计划加工量（投入侧，已按现行良率口径放大）。</summary>
    public decimal PlannedProcessQty { get; init; }
}

/// <summary>
/// 工厂 / 部门级**生效**合法 WORK 窗口（G-2 元素；消费方 = <see cref="DomainSolveRequest.DepartmentWorkWindows"/>）。
/// 四条口径见该属性的文档注释。
/// **本类只表示「已经过三级继承合成、可以直接用来累计工时」的窗口**：原始 OFF 行、被 OFF 裁掉的时间片、
/// 被上级 OFF 压住的部门 WORK，都不会出现在这里（RC-001：合成在我方、消费在 1号位）。
/// </summary>
public sealed class DepartmentWorkWindow
{
    public int FactoryId { get; init; }

    /// <summary>
    /// 生产部门 Id；**`null` = 厂级行**（厂级事实本身没有部门）。
    /// 可空是为了让厂级与部门级同处一个列表，无需拆两个集合。
    /// </summary>
    public int? DepartmentId { get; init; }

    /// <summary>窗口起（含）。已按自然日拆片，不跨午夜。</summary>
    public DateTime Start { get; init; }

    /// <summary>窗口止（不含）。`End &gt; Start` 恒成立 —— 零长片不会出现在本列表。</summary>
    public DateTime End { get; init; }
}

public sealed class ResourceEligibilityDefinition
{
    public int ResourceId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string RouteKey { get; init; } = string.Empty;
    public int Priority { get; init; }
}

public sealed class ExecutionConstraint
{
    public string DraftId { get; init; } = string.Empty;
    public int ResourceId { get; init; }
    public DateTime LockedStart { get; init; }
    public DateTime LockedEnd { get; init; }
    public string ConstraintType { get; init; } = string.Empty;

    // 第4轮Anchor补充：Stage/Operation信息，用于原地继承锁定Task
    public string? StageCode { get; init; }
    public string? OperationCode { get; init; }

    // 第4轮Anchor补充：锁定数量，原地继承该份额，只排剩余可移动份额
    public decimal? LockedQuantity { get; init; }

    // P1-01：净合格数量（锁定 Task 的净产出，YIELD 场景 != 产能加工数量）
    public decimal? LockedNetOutputQty { get; init; }

    // P1-01：产能加工数量（锁定 Task 的计划加工量）
    public decimal? LockedPlannedProcessQty { get; init; }

    // 第4轮Anchor补充：稳定TaskKey，用于跨轮次识别同一Task
    public string? TaskKey { get; init; }
}

/// <summary>
/// **订单保护事实**（契约②，`DomainSolveRequest.OrderProtections` 的元素）。
///
/// 语义见 <see cref="OrderProtectionType"/> / <see cref="OrderProtectionReason"/> 的文档注释。
/// **只通报、不约束**：1号位 不得据此禁止移动任何 Task（`PM回复0923-1` §三.2/§四/§八）。
/// 需要"锁物理位置"的（Frozen / 执行事实）走 `ExecutionConstraints`。
/// </summary>
public sealed class OrderProtection
{
    /// <summary>
    /// 订单 Id（= `LogicalProductionDemand.OrderId` 同一内存键；1号位 侧唯一可用键）。
    /// 依 `APS_V1_订单保护事实载体_1号位致2号位_认收回执_v1.0_20260923` §二「键须改 `OrderId`」，
    /// 取代 PM 草案的 `OrderCanonicalId`。**恒非空**（为空则该订单不可作键 ⇒ 2号位 不投影）。
    /// </summary>
    public long? OrderId { get; init; }

    /// <summary>保护类型（Firm / Frozen；`None` 不投影）。</summary>
    public OrderProtectionType ProtectionType { get; init; } = OrderProtectionType.None;

    /// <summary>保护原因（窗口层 / 执行事实层，见 <see cref="OrderProtectionReason"/>）。</summary>
    public OrderProtectionReason ProtectionReason { get; init; } = OrderProtectionReason.None;
}

public sealed class FiniteCapacityParameters
{
    public bool AllowSplit { get; init; } = false;
    public bool AllowMerge { get; init; } = false;
    public string SchedulingDirection { get; init; } = "BACKWARD";

    // ── P1-02 B 组 live 硬编码（3号位 已冻结、2号位 投影；字段先行、1号位 换读后逐项激活）──
    public int ImpactedTaskWarningPercent { get; init; } = 30;   // 传播警戒（PhaseFourLocalRepair maxAffectedRatio）
    public int MaxPropagationRounds { get; init; } = 10;          // 传播轮数（PhaseFourLocalRepair maxPropagationRounds）
    public int SplitAlternatives { get; init; } = 3;              // Split 候选（PhaseFourLocalRepair {2,3}）
    public decimal MinBatchQty { get; init; } = 0.1m;             // 拆分下限（PhaseFourLocalRepair qtyPerSplit<0.1）
}

/// <summary>
/// SolverStrategyMode ↔ SchedulingDirection 字符串固定映射（P1-02 §五-3：1↔2 契约正式化）。
/// 由 2号位 在投影处唯一使用；1号位 消费 SchedulingDirection 字符串（PhaseTwoInitialScheduler）。
/// 2026-10-07（0号位 裁决）：Mode += Auto，透传 "AUTO"——由 1号位 按当前求解上下文决定最终方向（不按 OrderType 硬编码）。
/// </summary>
public static class SolverStrategyModeMap
{
    public static string ToDirection(SolverStrategyMode mode) => mode switch
    {
        SolverStrategyMode.Forward  => "FORWARD",
        SolverStrategyMode.Backward => "BACKWARD",
        SolverStrategyMode.Mixed    => "MIXED",
        SolverStrategyMode.Auto     => "AUTO",         // 1号位 按求解上下文决定最终方向（0号位 2026-10-07 裁决）
        _                            => "BACKWARD"    // 防御未知枚举，等效 Backward（与历史行为一致）
    };
}

/// <summary>Run 级局部重排范围投影（2号位 → 1号位，强类型，零 string-JSON）。
/// 键体系已转 1号位 内存键；null ⇒ FULL Run 语义（向后兼容，缺省不改变现有行为）。</summary>
public sealed class RunScope
{
    public BusinessTriggerType Trigger { get; init; }

    /// <summary>优先级模式；DOMAIN_MANUAL_RESCHEDULE 为 null（走既有正式优先规则）。</summary>
    public PriorityMode? PriorityMode { get; init; }

    /// <summary>范围内逻辑生产需求键（LogicalDemandKey）；M5 详设前留空。</summary>
    public IReadOnlyList<string> InScopeLogicalDemandKeys { get; init; } = Array.Empty<string>();

    /// <summary>Run 级交期覆盖：LogicalDemandKey → ManualTargetDueDate（OrderCanonicalId 已按需求展开）。</summary>
    public IReadOnlyList<DueDateOverride> DueDateOverrides { get; init; } = Array.Empty<DueDateOverride>();

    /// <summary>Task 软目标：(DraftId, OperationCode) → TargetTime（TaskId 已投影为复合键）。</summary>
    public IReadOnlyList<TaskTargetOverride> TaskTargetOverrides { get; init; } = Array.Empty<TaskTargetOverride>();

    /// <summary>本次局部重排涉及资源 Id（int，与 ResourceDefinition.ResourceId 同域，原样透传）。</summary>
    public IReadOnlyList<int> ChangedResourceIds { get; init; } = Array.Empty<int>();

    /// <summary>本次 Candidate 操作允许影响的业务对象范围（权限框；0号位 §五/§七「谁可发起什么变化」）。
    /// 3号位 生成、2号位 纯透传不判权限、1号位 遵守；null = 未投影（历史载荷/非 Candidate）按无授权 fail-safe。</summary>
    public BusinessScopeDto? BusinessScope { get; init; }
}

/// <summary>Run 级交期覆盖：逻辑需求键 → 手工目标交期（仅 ManualTargetDueDate 非空行投影）。</summary>
public sealed class DueDateOverride
{
    public string LogicalDemandKey { get; init; } = string.Empty;
    public DateTime ManualTargetDueDate { get; init; }
}

/// <summary>Task 软目标：(DraftId, OperationCode) → TargetTime（与 ExecutionConstraint 同复合键口径）。</summary>
public sealed class TaskTargetOverride
{
    public string DraftId { get; init; } = string.Empty;
    public string OperationCode { get; init; } = string.Empty;
    public DateTime TargetTime { get; init; }
}
