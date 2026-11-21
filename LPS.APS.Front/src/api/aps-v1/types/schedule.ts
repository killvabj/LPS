/**
 * 排程结果查询 DTO（已与后端对齐）
 *
 * 来源：lps/LPS.APS.Application/Services/Query/Dto/
 *  - GanttDataDto.cs
 *  - PlanVersionSummaryDto.cs
 *  - ScheduleSummaryDto.cs
 *
 * 接口路径（来自 ScheduleController）：
 *  - GET /api/schedule/versions?take=30
 *  - GET /api/schedule/gantt/{planVersionId}
 *  - GET /api/schedule/summary/{planVersionId}
 *
 * 2026-11-18（r14281 后）：`TaskListItemDto.mesDispatchStatus` 的**属性已由后端补上**
 *   （5号位 r14281 入库 `LPS.APS.Core/Dto/TaskQueryDtos.cs`，增 `MesDispatchStatus`）
 *   ⇒ 我方前端**零改动即生效**。
 *   ⚠️ 我方 dev 复测时**环境未更新到 r14281**（本地 lps/ 工作副本 r14277、dev 进程 14:27 启动）
 *   ⇒ 999473 全量 28,021 行仍 0 次带键 ⇒ **读不到键＝环境滞后，不是契约不符**。
 *   依据：5号位 2026-11-18《复函R3-R3prime认账属性已补_第四次函码不一致根因与护栏机制》。
 *   （此前同一日按 dev ≥r14276 测得的「恒不出现」为**当时事实**，根因＝后端投影丢列，已由 r14281 修掉。）
 *
 * 2026-11-18（复函V 后销账）：dev 已由 Owner 更新到 **r14282**（进程 PID 12044 / 19:34:02）
 *   ⇒ 实测 28,021 行中 **2,694 行带 `mesDispatchStatus`**（值全 `CREATED`）、25,327 行不带键（队列外）；
 *   P06 详情 `task.mesDispatchStatus = "CREATED"` ⇒ **键已到位**（上面的「环境滞后」结论作废）。
 *
 * 2026-11-19（依据 5号位 2026-11-18《复函V…》§二 R3″ 双键名过渡期）：钉死**同一权威位的
 *   三个键名**与各消费点 —— 甘特位 `dispatchStatus`（本文件 `GanttTaskDto`）／P05 位
 *   `mesDispatchStatus`（本文件 `TaskListItemDto`）／**P11 位 `dispatchStatus`**
 *   （`MesStatusOperationRowDto`，见 `types/mes.ts`）。5号位 R3″ 的映射表只具名前两个
 *   ⇒ 第三个键名已函告（等其映射表一并收口；破坏窗核准前前端不自行合并或改名）。
 *
 * 2026-11-19（依据 5号位 2026-11-19《复函R3三键全收录_映射表草案即本函…》§二）：
 *   **V1.1 映射表（三键版）已出**（即刻生效的过渡契约）⇒ 本文件两个键**按表登记**：
 *   · 键① `GanttTaskDto.dispatchStatus` ⇒ 统一目标 `mesDispatchStatus`；
 *   · 键② `TaskListItemDto.mesDispatchStatus` ⇒ **终名，不变**；
 *   · **收口批次 = V1.1 破坏窗**（0号位 核准后 5号位一批做①③改名，前端改词典＋三处取字段）
 *     ⇒ **核准前我方不改名**，只登记不改键。
 *   ⚠️ **键①②缺键语义 = 「该 Task 不在下发队列」**（护栏语义表 r14286 第 17/18 行）
 *     ⇒ 页面显「不在下发队列」，**禁**解释成「未下发」/「无需 MES」；
 *     原「缺键 ⇒ 显『未提供』」的写法**作废**（与 P05 列、CSV 拉齐同一句话）。
 *   ⚠️ 键③（P11 工序行）**行驱动、语义差一拍**：行在则值必在，「缺键」＝该行不存在
 *     ⇒ 我方**按行渲染**，不套用键①②的分类文案（详见 `types/mes.ts`）。
 *
 * 2026-11-20：补两处后端已落码的投影键（依据 5号位-2026-11-20-复函-P01-P13收口v0.2清单十七项逐条裁决_三条可投影已落码B1F1J1_其余不可原因与后移给足-给4号位抄3号位.md
 *   §一 B1「阶段中文名 / 大工艺类目」与 §一 F1「负荷行部门三列」）：
 *   · `TaskListItemDto` 增 `stageName` / `stageCategoryName`（源 = APS 内 `ext_MES_StageDict`，
 *     字典缺码 ⇒ 后端给 null ⇒ **整键省略 = 「未提供」**，前端不造译名）。
 *   · `ResourceLoadRowDto` 增 `productionDepartmentId` / `departmentCode` / `departmentName`
 *     （源 = Resource→ProductionDepartment 直连；**缺键 = 该设备无归属部门** ⇒ 显「未提供」）。
 */

import type {
  DomainKey,
  FactType,
  IsoDateTime,
  MesEligibility,
  PlanVersionStatus,
  TaskLockMarker,
  TaskStatus
} from './common'
import type { ReasonCodeValue } from './explanation'
import type { SetupSource } from './setup'

/** 计划版本摘要（版本下拉用） */
export interface PlanVersionSummaryDto {
  id: number
  versionCode: string
  versionCategory: string
  planHorizonStart: IsoDateTime
  planHorizonEnd: IsoDateTime
  status: PlanVersionStatus
  /**
   * **域内当前生效版本标志**（后端 `PlanVersionSummaryDto.IsDefault`，JSON `isDefault`）。
   *
   * ⚠️ **不存在全局唯一 isDefault**：后端 DB 约束 `UQ_PlanVersion_OneActivePerDomain`
   *   保证**每域至多一条** ACTIVE ⇒ 跨域账号会看到**多条** `isDefault=true`。
   * ⚠️ 跨域取页面默认时**按 `activatedAt` 最新者**（后端 DTO 注释明确要求），
   *   禁按 `MAX(PV)` / `MAX(Run)` 选版（5号位 2026-11-16 矩阵 §0.1 给 4号位的硬条款）。
   * ⚠️ 取不到 isDefault 行 ⇒ 该域「当前无正式计划版本」（**不是 0、不是失败**）。
   */
  isDefault?: boolean
  /** 实测后端返该字段（如 `SYN-DOMAIN` / `FAMILY_X`）；旧 DTO 漏了它 */
  domainKey?: string
  computedAt?: IsoDateTime
  totalTasks?: number
  createdAt: IsoDateTime
  /**
   * 产生该版本的排程运行 Id（`PlanVersionSummaryDto.SourceScheduleRunId`）。
   * 冻结 `01 v1.13:1912` 要求展示「必要 Run 信息」的取值来源。
   */
  sourceScheduleRunId?: number
  /** 该版本被正式采用（成为 ACTIVE）的时间；未采用过则缺键 */
  activatedAt?: IsoDateTime
  /**
   * 数据截止时刻（冻结 `01 §77A 第 6 项`；源=来源 ScheduleRun.DataCutoffTime）。
   * 5号位 2026-11-05 B10 已落码：无来源 Run ⇒ null ⇒ **整键省略**（`WhenWritingNull`）。
   */
  dataCutoffTime?: IsoDateTime
  /**
   * 触发方式·批型（源=来源 Run.RunType：`FULL_SCHEDULE` / `MANUAL_RESCHEDULE` / `LOCAL_RESCHEDULE` / …）。
   * 5号位 2026-11-05 B10 已落码；无来源 Run ⇒ 整键省略。
   */
  runType?: string
  /**
   * 触发来源（源=来源 Run.TriggeredBy，如 `admin`）。
   * 5号位 2026-11-05 B10 已落码；无来源 Run ⇒ 整键省略。
   */
  triggeredBy?: string
  /**
   * 候选/归档版本指向的基线版本（`PlanVersionSummaryDto.BasePlanVersionId`）。
   * 取值规则：仅 CANDIDATE/ARCHIVED 可能有值，ACTIVE/BUILDING/FAILED 必为空。
   * 冻结 `01 v1.13:1912` 要求展示的「Base」。
   */
  basePlanVersionId?: number
  /**
   * 失败原因。⚠️ **当前恒缺键**：`PlanVersion` 表没有 ErrorMessage 列，
   * 后端 DTO 注释自陈「须 2号位 DDL 变更后填充」。不要把它渲染成「无失败原因」。
   */
  errorMessage?: string
}

/** 资源行（甘特图 Y 轴） */
export interface GanttResourceDto {
  resourceId: number
  resourceCode: string
  resourceName: string
  /** 适用 Domain（U01：多 Domain 时按 Domain 染色/分组） */
  domainKey: DomainKey
  /** 所属工厂 */
  factoryId?: number
  factoryName?: string
  /** 所属生产部门 */
  productionDepartmentId?: number
  productionDepartmentName?: string
  /** 所属 Stage（INJECTION/ASSEMBLY/TEST 等） */
  stage?: string
  /**
   * Section 十二 / U20：资源不可用窗口（设备故障、维护等）
   *  - V1 不建设 PAUSE/RESUME 状态闭环（审核报告 P1-13）；只展示给 PMC，由 PMC 决定是否发起 LOCAL_RESCHEDULE / MANUAL_RESCHEDULE
   *  - 影响窗口与甘特条叠加渲染（背景阴影 / 标线）
   */
  unavailableWindows?: ResourceUnavailableWindow[]
}

/** 资源不可用窗口 */
export interface ResourceUnavailableWindow {
  from: IsoDateTime
  to: IsoDateTime
  /** 不可用原因 */
  reason: string
  /** 影响哪些 Task ID（后端推断；4号位不计算，仅展示） */
  impactedTaskIds?: number[]
}

/**
 * 任务条（甘特图 X 轴矩形）
 *
 * 4号位文档第 6 节约束：
 *  - 仅展示 FinalTask，不展示 LogicalProductionDemand
 *  - status 仅允许 5 种枚举值之一
 *  - 不允许拖拽直接改库
 */
export interface GanttTaskDto {
  taskId: number
  taskNo: string
  /** 主 Order（任务主要承接的订单；U04：可能与 taskShare 中其他 Order 共担） */
  orderId: number
  orderNo?: string
  /** 适用 Domain（U01） */
  domainKey: DomainKey
  materialId: number
  materialCode?: string
  materialName?: string
  resourceId?: number
  operationCode: string
  operationSeq: number
  /*
   * 任务清单信息补全（5号位 2026-11-05 知会，r14043/14044）：10 个可选键，缺值整键省略。
   * `operationName` 已撤（Task 表无此列）⇒ 工序语义按 operationCode + stageCode 呈现。
   */
  /** 生产部门 Id（Task.ProductionDepartmentId） */
  productionDepartmentId?: number
  /** 生产部门名称（JOIN ProductionDepartment.DeptName，如「CN机加_制造3-4课951HL」） */
  departmentName?: string
  /** 阶段码（Task.StageCode） */
  stageCode?: string
  /** 任务类型（Task.TaskType；现全库单值 PRODUCTION） */
  taskType?: string
  /** 路线码（Task.RouteCode；实库多为通用快照 DEFAULT，不可用作对合键） */
  routeCode?: string
  /** 路径 Id（Task.PathId，多 BOM 支号；配合 MaterialId+OperationCode 定位） */
  pathId?: number
  /** 人工槽 Id（Task.ManualSlotId；有值=人工作业行，资源侧与设备互斥） */
  manualSlotId?: number
  /** 生产指示号（Task.MTS_InstructionNo；⚠ JSON 键名是 mtS_InstructionNo，不是 mts_） */
  mtS_InstructionNo?: string
  /** 执行批内部键（Task.StageExecutionBatchDraftKey，GUID 句柄，仅追溯/分组用） */
  stageExecutionBatchKey?: string
  /** 执行批量（Task.StageExecutionBatchQty，整批串行执行的数量载体） */
  stageExecutionBatchQty?: number
  /** U05：净需求数量（去掉不良品的实际可用数量） */
  netQty: number
  /**
   * U05：计划加工数量（含良率补偿，大于等于 netQty）
   *
   * 2026-10-17：后端 `ScheduleQueryService.cs:206` 取的是**真实列** `t.PlannedProcessQty`
   *   （dev 实测 v2 203/203 非空）。仍保持可选：字段未落盘时整键省略
   *   ⇒ 前端不展示该字段（不拿 undefined 当 0，也不当"已确认无补偿"）。
   */
  plannedProcessQty?: number
  /** 兼容字段：旧字段，等于 plannedProcessQty（不让前端误以为是净需求） */
  quantity: number
  uom: string
  plannedStartTime?: IsoDateTime
  plannedEndTime?: IsoDateTime
  status: TaskStatus
  /** 延期判定已由后端按**日期**口径算好（`CAST(PlannedEndTime AS date) > CustomerDueDate`） */
  isDelayed: boolean
  /** 客户交期（`Order.CustomerDueDate`，DATE 精度）—— 冻结 `01:1907` 用户输出第 2 项 */
  customerDueDate?: IsoDateTime
  /**
   * 延期时长，**自然小时**（交日零点 → 计划完工），未延期为缺键。
   * 5号位 回执 §四：非工作日小时需接三级日历，本版不提供。
   */
  delayHours?: number
  /**
   * 关键路径判定（冻结 `07:3683` / `08:4249`）。
   *
   * ⚠️ **三态**（5号位 2026-11-05 B5 读侧已落码）：DTO 由 `bool` 改 `bool?`，
   *   查询层投 `CAST(NULL AS BIT)` ⇒ **未判定时整键省略**（不是恒 `false`）。
   *   - `true`  = 判定为关键路径
   *   - `false` = 判定为非关键路径
   *   - 缺键    = **未判定**（1号位 求解算法未落地，写侧两处 `IsCriticalPath=false` 在 2号位 PeggingOrchestrator）
   *   ⇒ 前端三态分开展示，不得把「未判定」显示成「不是关键路径」。
   */
  isCriticalPath?: boolean
  /**
   * 工时，**单位：小时**（冻结 `07:3680`）。
   *
   * 单位三方可追溯：冻结写小时；写入方 `PeggingOrchestrator.cs:1052-1053` 按**分钟**落库
   *   （注释原文「单位=分钟」）；查询层 2026-10-17 改为 `t.Duration / 60.0` 返回小时
   *   （5号位 R6 选方案 a，库值不改、换算只在查询层）。
   * 实测（dev v2，2026-10-19）：2.66666666 / 6.844445，与 `plannedEndTime - plannedStartTime` 一致。
   *
   * ⚠️ 前端**不拿它算甘特条长** —— DHTMLX Gantt 的 `duration` 是库自身字段（单位小时），
   *   与本字段无关，仍由 `plannedEndTime - plannedStartTime` 现算，避免两处口径分叉。
   */
  duration?: number
  /**
   * 不可移动标识（仅 UI 显示）
   *
   * ⚠️ **永久缺键**（5号位 2026-10-31 D1-c 裁决「撤掉」：后端永不发该键，DTO 预留位保留）
   *   ①`Task.IsLocked` 全库 100% false 且写方 `PeggingOrchestrator` 写死 false；
   *   ②`DemandSupplyHardLock` 0 行；③冻结 07:4937 明写 FROZEN_ZONE_LOCK 只进
   *   `ScheduleExplanationFact`、**不进 Task 状态字段** ⇒ 三态无任何落盘源。
   *   前端已删除对应 UI（三态标签 / 甘特条派生字段），此处仅保留类型位以对齐后端契约预留位。
   */
  lockMarker?: TaskLockMarker
  /**
   * U04：TaskShare — 一个 Task 承接多 Order 时，每个 Order 承担的数量
   *  - 当 taskShare 仅一条且 orderId == task.orderId 时 = 单 Order
   *  - 当 taskShare 多条或包含非主 orderId 的项 = 共担
   */
  taskShare?: TaskShareLine[]
  /**
   * U14：是否被外 Domain 共享设备阻挡
   *
   * ⚠️ **V1 不做、永久缺键**（5号位 2026-11-05 B4 裁决「不做」）：
   *   判定语义无冻结明文（`Pegging.IsCrossDomain` 是跨域**边属性**、不是阻挡判定），
   *   V1.1 负荷率议题请 0号位 定「共享资源占用展示」语义后再回来接。
   *   前端展示位已随之撤除（与 B6/B7/B8 同判）；类型保留为后端 DTO 占位。
   */
  crossDomainBlocked?: boolean | null
  /** U14：阻挡原因（外 Domain 共享设备 / 资源冲突 等）—— 同上，V1 恒缺键 */
  crossDomainBlockReason?: string
  /**
   * U06：数量-时间拆分（一个 Task 的数量被拆为多个时段加工）
   *
   * ⚠️ **V1 不做、永久缺键**（5号位 2026-11-05 B8 裁决「撤」）：
   *   无载体表；冻结 `01 §76` 的分段属 Supply 跨域事实，不是 Task 内拆分。
   *   类型位保留以对齐后端 DTO 预留位；**前端已撤展示位**，不再渲染「数量-时间拆分」。
   */
  taskSegments?: TaskSegmentLine[]
  /**
   * Section 二.2：四类信息标签（FACT / RESULT / ESTIMATED / RECOMMENDATION）
   *
   * ⚠️ **V1 不做、永久缺键**（5号位 2026-11-05 B7 裁决「撤」）：
   *   全部冻结文档无 Task「事实类型」定义（07 的 ESTIMATED 属 Supply CommitmentStatus）。
   *   类型位保留以对齐后端 DTO 预留位；**前端已撤展示位**。
   */
  factType?: FactType
  /**
   * Section 十六：MES 下发资格（4号位只展示 + 调用服务，不直接写 MES）
   *
   * ⚠️ **不是任何表的列**（全库无 `MesEligible`，`Task` 表 30 列里也没有）——
   *   是「接口先占位、真值未落盘」的产物（5号位 2026-10-17 回执 §三 已查库确认）。
   * ⚠️ 契约 **2026-10-17 由 `bool` 改为 `bool?`**（`GanttDataDto.cs:163`；SQL `CAST(0 AS BIT)` → `CAST(NULL AS BIT)`）：
   *   非可空时「判定为否」与「未判定」不可区分，硬编码 `false` 会被读成「判定为不可下发」。
   *   未接入期返回 `null` ⇒ 受 `WhenWritingNull` 影响**整个键在响应中不出现**（实测 v2：203/203 无该键）。
   * ⇒ 前端 `null`/`undefined` 一律按「未接入」呈现（见 `Gantt.vue` 的 `mesEligibilityOf`），
   *   保留三态字符串以便后端将来换成真三态时无缝接管。
   */
  mesEligible?: boolean | null | MesEligibility
  /** 不可下发原因（Section 16）—— 当前后端 `CAST(NULL ...)`，整键省略 */
  mesIneligibleReasons?: string[]
  /**
   * MES 工单号（源=`Task.MESWorkOrderNo`；冻结 `07:3687` 可追溯执行身份）。
   *
   * ⚠️ **JSON 键 = `mesWorkOrderNo`**（5号位 2026-11-05 B2① 实测定稿；注意不是 `mESWorkOrderNo`
   *   —— CamelCase 只把 `MES` 段折成小写、`W` 保留，与 `MTS_` 的整段折规则不同）。
   * 空 / 未建单 ⇒ null ⇒ **整键省略**（不显示空串）⇒ 前端按「未建单」呈现。
   */
  mesWorkOrderNo?: string
  /**
   * 下发状态（源=`TaskDispatch.DispatchStatus`——「是否已正式下发 MES」的权威载体，
   * 解 `07:3721` 状态 0 的 PLANNED/RELEASED 歧义）。
   *
   * 5号位 2026-11-05 B2② 已落码（查询层批量富化后透出）：**无下发记录 ⇒ 缺键 = 未下发**。
   * 值域由写侧决定、后端自由字符串 ⇒ 未收录的值原样透出，不自造映射。
   *
   * ⚠️ **甘特位键名**（历史契约，11-05 起已消费，不擅改）：同一权威位另有
   *   P05 位 `mesDispatchStatus`（终名）与 **P11 位 `dispatchStatus`**（批次工序行 DTO）
   *   ⇒ 实为**三个键名**，统一归 V1.1 破坏窗，前端**不得自行合并或改名**。
   *   · 消费点：`Gantt.vue` 的**详情抽屉**与**悬停提示**（两处均走词典 `mesDispatchStatusLabel`）。
   *
   * ✅ **V1.1 映射表（三键版）已出**（5号位 2026-11-19 复函 §二，即刻生效的过渡契约）：
   *   · 本键 = 映射表 **键①**，统一目标 = `mesDispatchStatus`；
   *   · **收口批次 = V1.1 破坏窗**（0号位 核准后 5号位一批做①③改名，前端改词典＋三处取字段）
   *     ⇒ **核准前我方不改名**，仅按表登记。
   *
   * ⚠️ **缺键语义 = 「该 Task 不在下发队列」**（护栏语义表 r14286 第 18 行）
   *   ⇒ 页面显「**不在下发队列**」；**禁**解释成「未下发」/「无需 MES」（不得断言成状态）。
   *   （与 P05 清单列、清单 CSV 显示同一句话。）
   */
  dispatchStatus?: string
  /**
   * Section 十一：异常与原因解释 — 根因（不只 DUE_DATE_RISK）
   *  - 后端 GanttDataDto 当前不带，由 3 号位落地后对齐；
   *  - 前端先以可选项存在，4 号位文档第 11 节硬约束：必须展示根因，不能只丢 DUE_DATE_RISK
   *  - issueCategory 区分数据问题 / 排程问题（Section 11 第三段）
   */
  delayReasons?: GanttTaskDelayReason[]
  /**
   * Section 11：是否被上游 Stage 阻挡（前序延迟）
   *
   * ⚠️ **V1 不做**（5号位 2026-11-05 B6 裁决「不做」）：冻结无展示条款；
   *   推导口径（StageHandoffWait + 整批完成时间）涉 1号位 时间真相，后端不自造。
   *   类型位保留；**前端已撤展示位**。
   */
  upstreamStageBlocked?: boolean
  /** Section 11：上游 Stage 名称（同 B6：V1 不做，前端已撤展示位） */
  upstreamStageCode?: string
  /**
   * SetupSource **4 态**：换型规则命中来源（`SetupOptimizer.cs:60-72`，0号位 20260922 裁决）
   *   - EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK / INITIAL_SETUP_STATE
   *   - nullable：未填充时**整键省略**（`WhenWritingNull`）⇒ 前端拿到 undefined
   *   - 同产品连续归 DEFAULT（不是独立来源）；`SAME_PRODUCT` / `NONE` 不是正式落库值
   */
  setupSource?: SetupSource
}

/** Section 11：根因条目（4号位文档第 11 节硬约束） */
export interface GanttTaskDelayReason {
  /**
   * 根因类型编码 —— 复用冻结 `07 v5.1.13 §八.1` 权威 15 码全集（与 `ScheduleExplanationFact.ReasonCode` 同一码表）。
   *
   * 2026-10-17 更正：本类型此前自拟了 8 个非冻结码（MATERIAL_AVAILABILITY / CAPACITY_SHORTAGE /
   *   UPSTREAM_DELAY / LOCK_BLOCKING / CROSS_DOMAIN_BLOCK / EQUIPMENT_FAILURE / DUE_DATE_RISK / OTHER），
   *   与冻结码表只有 DUE_DATE_RISK 一处重合 —— 违反 `规则清单 v1.9 U-004`「不新增第 16 码」。
   *   后端一旦开始填 `delayReasons`，按冻结 15 码返回，本类型必须能接住。
   *
   * 2026-10-31：类型放宽为 `ReasonCodeValue`（= 冻结 15 码 **或空串**）——
   * 后端 `GanttDelayReasonDto.ReasonCode` 是 `public string ... = string.Empty`，
   * 且实库 ORDER 级事实 95% 为空串（5号位 10-31 实查）⇒ 空串是「写方未填原因码」的常态，
   * 前端按 `reasonLabelOf` 中性展示为「未标注原因码」，不留空白标签。
   */
  reasonCode: ReasonCodeValue
  /**
   * 数据问题 vs 排程问题（Section 11 第三段；后端 `GanttDelayReasonDto.IssueCategory`）
   *
   * 2026-10-31 降为**可选**：后端 DTO 本就是 `string?`（可空 ⇒ null 时 JSON **整键省略**），
   * 且 r13982（5号位 10-31 入库知会）起 `delayReasons` 只下发 `reasonCode / severity / impactHours`
   * —— `ScheduleExplanationFact` 没有 IssueCategory 列（DDL 08:8850-8865），
   * 后端源码注释明写「IssueCategory/Description/RelatedObjectRef 三键无载体…本期不给（缺键）」。
   * ⇒ 缺 `issueCategory` 时前端归「其他根因」桶照常出行，不得因分不了类就把整块根因区渲染成空白。
   */
  issueCategory?: 'DATA_ISSUE' | 'SCHEDULING_ISSUE'
  /**
   * 该条事实挂在哪一级对象上（5号位 2026-11-05 Q2-a 放宽落码后新增的**行级键**）
   *
   *  - `TASK`  = 任务级事实：这个任务自己的原因；
   *  - `ORDER` = 订单级事实：整张订单的原因，后端按 `TaskId` 透传到该单的**每个**任务
   *              ⇒ 页面必须标「订单级」，否则用户会当成「这个任务自己的原因」；
   *  - 缺键 = 后端未标注层级 ⇒ 按任务级中性展示，不猜。
   *
   * 依据：5号位 11-05 复函 Q2-a（过滤由 `ObjectType='TASK'` 放宽为 TASK∪ORDER，
   *   实测窗口内 4 条真值全为 `ORDER`）。
   */
  objectType?: 'TASK' | 'ORDER'
  /**
   * 严重度 —— 冻结 `07 v5.1.13 :4938`：`INFO / WARN / ERROR`，**不含 CRITICAL**
   * （CRITICAL 仅用于 BOM Issues 表）。此前误含 `CRITICAL` / `WARNING`，一并归一。
   */
  severity: 'INFO' | 'WARN' | 'ERROR'
  /**
   * 文档第 11 节列出的根因示例：采购料9月3日才可用、MC01无产能、上游Stage完成晚、Firm任务占关键窗口
   *
   * 2026-10-31 降为**可选**：同 `issueCategory`，后端本期不给（仅存在于 `EvidenceJson` 内）⇒ 缺失时前端不渲染描述行，不留空串。
   */
  description?: string
  /** 关联对象引用（PO 编号 / 设备编码 / 上游 Task 等） */
  relatedObjectRef?: string
  /** 影响小时数 */
  impactHours?: number
}

/** U04：TaskShare 一行（一个 Task 承接的某个 Order 的份额） */
export interface TaskShareLine {
  orderId: number
  orderNo: string
  shareQty: number
  /**
   * 该 Order 在 Task 上的占比（0-1）。
   *
   * 2026-10-31 更正：后端 r13982 起按**同任务内归一**（ShareQty / Σ 同任务份额），
   * 多份额任务 Σ=1.0 —— 不是除以任务数量，也不是 shareQty / plannedProcessQty。
   */
  shareRatio?: number
}

/** U06：数量-时间拆分（一个 Task 的数量被拆为多个时段加工） */
export interface TaskSegmentLine {
  /** 段序号（1-based，便于 UI 展示） */
  segmentSeq: number
  startTime: IsoDateTime
  endTime: IsoDateTime
  /** 该段加工的数量（与 plannedProcessQty 之和 = 总数） */
  qty: number
  /** 该段数量占总加工数量的占比（0-1） */
  qtyRatio?: number
  /** 拆分原因（产能不足 / 设备故障 / 物料分批 等） */
  reason?: string
}

/** 甘特图数据（一个版本全量） */
export interface GanttDataDto {
  planVersionId: number
  versionCode: string
  /** 5号位 2026-10-17 R7 已补；版本不存在时缺键 */
  planVersionStatus?: PlanVersionStatus
  /**
   * Candidate 的**基线版本 Id**（仅 CANDIDATE 非 null；ACTIVE / ARCHIVED 为 null）
   *  - 后端 `GanttDataDto.BasePlanVersionId`（v1.5 §16.3：Candidate 与 Base 比较所需）
   *  - 页面用它挂「基线」徽标；**不自己算**基线是谁（矩阵 A1「禁自算」）
   */
  basePlanVersionId?: number | null
  planHorizonStart: IsoDateTime
  planHorizonEnd: IsoDateTime
  resources: GanttResourceDto[]
  tasks: GanttTaskDto[]
  /**
   * 5号位 2026-10-17 R8：`true` = 越权（Domain 授权不足，fail-closed）。
   * 前端据此给「无权限」空态，而不是「没有任务」。
   */
  scopeDenied?: boolean
  /**
   * `true` = 版本不存在 / 越权 / （过滤后）无任务。
   * ⚠️ 单独用它分不出「版本不存在」与「无任务」——两者都是 `scopeDenied=false + isEmpty=true`；
   *   第三信号用下面的 `versionExists`（显式位），**不再靠 `versionCode === ''` 隐式判断**。
   */
  isEmpty?: boolean
  /**
   * 5号位 2026-10-17 回执 §六新增（`GanttDataDto.cs:56`，DTO 注释点名「替代 `versionCode === ''` 隐式契约」）。
   * `false` = 该 planVersionId 不存在。
   * 三种空态：版本不存在(`false`) / 越权(`scopeDenied=true`) / 存在但无任务(全 true + isEmpty=true)。
   *
   * 实测（dev 2026-10-19）：v2 → `true`；999999 → `false` + `isEmpty=true` + `versionCode=''`。
   * ⚠️ 它是**非可空 `bool`**，版本不存在时仍会出现（值为 false），不会像可空字段那样整键省略。
   */
  versionExists?: boolean
  /**
   * 5号位 2026-10-29 新增（复函 §二）：**当前过滤口径**（from/to/resourceIds）下的任务总数。
   * ⚠️ 与 `PlanVersionSummaryDto.totalTasks`（版本总数）不是同一个数，不要混用。
   * 版本不存在 / 越权时为 null；老后端无此键（undefined）。
   */
  totalTasks?: number | null
  /**
   * 还有下一页。后端用 `TOP limit+1` 探测溢出后裁掉，**永不撒谎**。
   * 🔴 5号位 落码后「不传 limit 不再返回全量」——裸调只有 8000 条 + `hasMore=true`，
   * 所以它是「本次是否取全」的唯一信号；undefined = 老后端（未部署该契约）。
   */
  hasMore?: boolean
  /** 服务端生效的 limit 回显（老后端无此键） */
  limit?: number
  /** 游标回显（hasMore=true 时必给）：`{resourceId, plannedStartTime, taskId}`，下一步翻页用 */
  nextAfter?: GanttCursor | null
}

/**
 * 甘特分页游标（5号位 2026-10-29 契约）：`nextAfter` 原样回填即为下一页。
 * ⚠️ 必须整包回填：后端已把未分配资源归一为 -1，用行内原始 null 自拼会错位。
 */
export interface GanttCursor {
  resourceId: number
  plannedStartTime: IsoDateTime
  taskId: number
}

/**
 * 甘特负荷带桶宽（5号位 2026-10-30 r13972 契约）：`day | week | month`，缺省 `day`。
 *
 * 桶锚按**自然日历**切（后端 `ScheduleQueryService.cs:367-375`）：
 *  - day   = 当日 00:00
 *  - week  = **周一** 00:00（`DATEDIFF(week,0,x)` 以 1900-01-01 周一为基）
 *  - month = 当月 1 日 00:00
 * 且**不随 `from` 的时刻滑动**（`from=07:30` 时日桶起点仍为当日 00:00）。
 */
export type GanttBucketUnit = 'day' | 'week' | 'month'

/**
 * 甘特「资源×时间桶」聚合层（5号位 2026-10-30 r13972 落码知会，dev 已部署实测）
 *
 * ⚠️ 键的存在性有两套语义，**不可按 `=== null` 判存在**（后端 `Program.cs:125`
 *   设了 `DefaultIgnoreCondition = WhenWritingNull` ⇒ **所有 null 一律整键省略**）：
 *  - `totalTasks`：版本不存在 / 越权 ⇒ **整键省略**；空窗 ⇒ **键在、值 0**（两种形态都要处理）
 *  - `items[].bucketStart`：**缺键 = 未排桶行**（该任务 `PlannedStartTime IS NULL`）
 *  - `items[].resourceCode/resourceName/factoryName/productionDepartmentName/stage`：
 *    未分配行（`resourceId=-1`）⇒ 整键省略
 */
export interface GanttBucketsDto {
  planVersionId: number
  versionCode: string
  /** `false` = 版本不存在（此时 `totalTasks` 整键省略、`items` 为空） */
  versionExists?: boolean
  /** `true` = 越权（Domain 授权不足，fail-closed） */
  scopeDenied?: boolean
  /** `true` = 该版本在窗口内确实无任务 */
  isEmpty?: boolean
  /** 请求的桶宽回显（缺省 `day`） */
  bucket?: GanttBucketUnit
  /**
   * **实际生效的桶宽**（降级后可能 ≠ `bucket`）。
   * 后端降级链：行数 > 50,000 时 `day→week→month`，回显 `bucketWidth` + `bucketDowngraded`；
   * 月桶仍超 ⇒ 真 HTTP 400。**图层必须按 `bucketWidth` 画，不是按 `bucket`。**
   */
  bucketWidth?: GanttBucketUnit
  /** `bucket !== bucketWidth` 时为 `true` */
  bucketDowngraded?: boolean
  /** 窗口起回显（naive，无 Z） */
  from?: IsoDateTime
  /** 窗口止回显（naive，无 Z） */
  to?: IsoDateTime
  /** 窗口口径任务总数；恒等式 `Σ items[].taskCount === totalTasks`。不存在/越权 ⇒ 整键省略 */
  totalTasks?: number | null
  /** 一行 = 一个 (资源, 桶) 对；**稀疏输出**（只有存在任务的资源×桶对才出现） */
  items?: GanttBucketRowDto[]
}

/**
 * 一行 = 一个 (资源, 桶) 对。
 *
 * ⚠️ `domainKey` 是**版本域**（`PlanVersion.DomainKey`），不是任务所属域 ⇒
 *   同一版本内所有行恒相同，**不可用于分域/泳道分组**；未分配行（`-1`）也有值。
 */
export interface GanttBucketRowDto {
  /** `-1` = 未分配资源行（**该值恒在**，不是 null） */
  resourceId: number
  /** 未分配行整键省略 */
  resourceCode?: string
  /** 未分配行整键省略 */
  resourceName?: string
  /** 未分配行整键省略 */
  factoryName?: string
  /** 未分配行整键省略 */
  productionDepartmentName?: string
  /** 未分配行整键省略 */
  stage?: string
  /** 版本域（`PlanVersion.DomainKey`）；未分配行**同样有值** */
  domainKey?: string
  /**
   * 桶起点（naive 无 Z）。
   * ⚠️ **缺键 = 未排桶行**（该任务 `PlannedStartTime IS NULL`）—— 不得静默丢弃。
   * ⚠️ **可能早于窗口 `from`**：周桶锚 = 周一、钳制归桶（`GREATEST(start, from)`）所致，
   *   实测窗 10-20→10-27 的周桶起点为 `2026-10-19`（早于 from）⇒ **不得按 from 裁首桶**。
   */
  bucketStart?: IsoDateTime
  /** 落桶任务条数（整条计一次，不切段） */
  taskCount: number
  /** 桶内工时之和（小时）= `SUM(Duration)/60`，与明细体 `duration`（小时）同值
   *
   * ⚠️ **禁止直接用作负荷率分子**（5号位 2026-11-02 复函 §二）：`loadHours` 是「整条任务工时计入**起点桶**」，
   *   桶宽一旦大于任务时长，整段工时就会全压在起点那一格 ⇒ 日桶宽下**必然虚高**
   *   （40 h 任务起点周一 ⇒ 周一格 444%）。修法 = 按桶**切分工时**（V1.1 拟名 `loadHoursInBucket`），
   *   不是切墙钟 —— **墙钟 `occupiedHours` 方案已撤回**（跨天任务的夜间时段不是产能占用 ⇒ 天天假超载）。
   */
  loadHours?: number | null
  /** 桶内最早计划开始（naive；如实可越窗，不钳制） */
  firstStart?: IsoDateTime
  /** 桶内最晚计划完成（naive；**如实可越窗**，不钳制） */
  lastEnd?: IsoDateTime
}

/**
 * P12 资源负荷三率（5号位 2026-11-17 r14239 落码；矩阵 A6/A7）
 *
 * `GET /api/schedule/resource-load/{planVersionId}?from=&to=[&resourceIds=…]`
 * 一行 = **设备 × 业务日**。
 */
export interface ResourceLoadResultDto {
  planVersionId: number
  domainKey?: string
  /** `false` = 计划版本不存在（其余字段为空） */
  versionExists?: boolean
  /** `true` = 越权（域/部门范围不足，fail-closed） */
  scopeDenied?: boolean
  planVersionStatus?: string
  /** 源批次的数据截止时间（naive） */
  dataCutoffTime?: IsoDateTime
  /** 窗口起（naive，后端按 `.Date` 取整） */
  from?: IsoDateTime
  /** 窗口止（naive，后端按 `.Date` 取整） */
  to?: IsoDateTime
  /** 本次返回的设备数（不传 `resourceIds` 时为窗内排产设备 TOP200） */
  resourceCount?: number
  /**
   * `true` = 窗内排产设备 **> 200** 被截断（`resourcesTruncated`）。
   * ⇒ 页面必须提示收窄（否则用户以为看全了）。
   */
  resourcesTruncated?: boolean
  /** 行 = 设备 × 业务日（**全矩阵**：窗内每一天 × 每台设备都有行） */
  rows?: ResourceLoadRowDto[]
  /**
   * `false` = **Setup 时长无持久列** ⇒ ③换型损失率整列 null（0号位 Q-b 裁定）。
   * 前端该位显「未具备数据条件」，**不显 0、不显「未提供」**。
   */
  setupHoursAvailable?: boolean
}

/** 一行 = 一个 (设备, 业务日) */
export interface ResourceLoadRowDto {
  resourceId: number
  resourceCode?: string
  resourceName?: string
  /**
   * 生产部门 Id（5号位 2026-11-20 复函 F1 / r14322 已落码；Resource→ProductionDepartment 直连）。
   *
   * ⚠️ 缺键 = **该设备无归属部门**（不是「没投影」）⇒ 前端显「未提供」。
   */
  productionDepartmentId?: number
  /** 部门编码（缺键 = 未提供） */
  departmentCode?: string
  /** 部门名称（如「BJ机加_制造2-7课920HL」；缺键 = 未提供） */
  departmentName?: string
  /** 业务日（naive，00:00:00） */
  businessDate: IsoDateTime
  /** 当日已排工时（跨午夜按日切片，秒级） */
  scheduledHours: number
  /** 当日排产任务条数 */
  taskCount: number
  /**
   * ① **日历负荷率** = 当日已排 ÷ 三级继承解析可用小时 × 100。
   * - 缺键/null ⇒ 分母未判定（**不显 0**；看 `notApplicableReason`）
   * - `0` ⇒ **明确零**（有可用小时、当日无排产）
   */
  calendarLoadRatePct?: number | null
  /**
   * ② **固定 16h 负荷率** = 当日已排 ÷ 16 × 100。
   * ⚠️ 恒可算，但**不是真实可用时间**（不得代替①、不得当 MES 利用率）。
   */
  fixed16LoadRatePct?: number
  /**
   * ③ **Setup 换型损失率** —— 现无持久列 ⇒ 恒 null（`setupHoursAvailable=false`）。
   * ⇒ 前端显「未具备数据条件」。
   */
  setupLossRatePct?: number | null
  /** Setup 工时（Q-b：恒 null，不代算） */
  setupHours?: number | null
  /** ①分母 = 当日三级继承解析出的可用小时（缺键 = 未解析出窗口） */
  calendarAvailableHours?: number | null
  /**
   * ①分母**依据哪级日历**：`Resource` / `Department` / `Factory`。
   * ⇒ 页面用它说明「这个可用小时数是按哪一级日历算出来的」。
   */
  calendarSource?: string | null
  /**
   * 该行**不适用**的原因（非空 ⇒ 该率显「不适用」，**禁显 0**）。三类：
   *  - 当日合法可用小时=0（日历当日 OFF / 无 WORK 片）
   *  - 当日未解析出合法 Calendar 窗口（**严禁按 8h/全天假设**）
   *  - 该设备被日历解析件按授权范围剔除或无登记 ⇒ 分母未判定
   */
  notApplicableReason?: string | null
}

/**
 * P05 任务清单（服务端分页；5号位 2026-11-16 矩阵 A4 / r14237）
 *
 * `GET /api/schedule/tasks/{planVersionId}`
 *
 * ⚠️ **total 与 items 是两个数**：`total` = 服务端全量命中（COUNT 与列表**同一段 WHERE**），
 *   `items` = **本页已加载**。前端分页器的「共 N 条」必须用 `total`，页面上的表格只画 `items`
 *   —— 把本页条数说成总数就是 v0.2 §P05「加载数≠全部数」红线。
 * ⚠️ **版本不存在时 `page` / `pageSize` 返 0**（后端早退分支不填这两键）
 *   ⇒ 空态文案不能拿 `page` 判「翻到了空页」。
 */
export interface TaskListResultDto {
  /** `false` = 计划版本不存在（此时 items 空、total=0） */
  versionExists?: boolean
  /** `true` = 越权（域/部门范围不足，fail-closed）——是「无权限」不是「无数据」 */
  scopeDenied?: boolean
  /** 本页已加载的行 */
  items?: TaskListItemDto[]
  /** 服务端全量命中数（COUNT 与列表同一 WHERE） */
  total?: number
  /** 当前页码（后端回显） */
  page?: number
  /** 当前页长（后端回显；缺省 50、钳 ≤200） */
  pageSize?: number
}

/**
 * P05 任务清单一行 = **一个真实 Operation Task.Id**。
 *
 * ⚠️ **行键必须是 `taskId`**：`taskNo` 是 Stage 内执行批业务号，一个 TaskNo 可对应多条
 *   Operation Task（5号位 11-16 实测：TaskNo 混版，PEGG-999432 系行也在 999473 内）
 *   ⇒ 按 TaskNo 折叠会把两条工序任务并成一条。
 * ⚠️ **带 `?` 的键 = 后端 NULL ⇒ 整键省略 = 未知**（`WhenWritingNull`）：
 *   前端一律显「未提供」，**禁转 0 / 禁显「正常」/ 禁当空串**。
 * ⚠️ 本 DTO **不含**订单号 / 客户交期 / 延期 / 延期原因（矩阵 A4 的 25 字段清单里没有）
 *   ⇒ 这些列在本读面里**不存在**，不得从甘特体或别处拼凑补齐（两套口径混算）。
 *   ⚠️ **下发状态已由 5号位 2026-11-18 复函 N2 补上**（`mesDispatchStatus`）⇒ 唯一例外。
 */
export interface TaskListItemDto {
  /** 行身份（= `Task.Id`，Operation 级） */
  taskId: number
  /** Stage 内执行批业务号（**不是行键**） */
  taskNo: string
  planVersionId: number
  materialId: number
  materialCode?: string
  materialName?: string
  /** 阶段码（Task.StageCode） */
  stageCode?: string
  /**
   * 阶段中文名（5号位 2026-11-20 复函 B1 / r14321 已落码）。
   *
   * - 源 = APS 内 `ext_MES_StageDict`（29 行主数据，同义于 ODS 直连）
   * - **字典缺码 ⇒ 后端给 null ⇒ 整键省略 = 「未提供」**（前端不造译名）
   * - 前端回落用冻结字典 `stageLabelOf`（同源，未命中码原样透出）
   */
  stageName?: string | null
  /**
   * 大工艺类目中文名（5号位 2026-11-20 复函 B1 / r14321）。
   *
   * ⚠️ 源字典缺码 ⇒ null = 未提供（**不是**「无类目」）
   */
  stageCategoryName?: string | null
  operationCode: string
  /** 工序名（可空 = 未判定 ⇒ 只显编码，不猜） */
  operationName?: string
  /**
   * 设备资源 Id（与人工槽互斥：二者其一）。
   *
   * ⚠️ **恒投影，不是「按需投影」**（dev 2026-11-17 实测：不带 `resourceIds` 筛选时，
   *   前 50 行里 29 行缺该键、21 行带该键 ⇒ 缺键 = **该 Task 真没有设备资源**，
   *   多为人工槽任务；不是「没传 resourceIds 就少投影」）。
   */
  resourceId?: number
  /**
   * 设备资源编码（5号位 2026-11-18 复函 N2 补：v0.2 P05「设备/人工槽」列的标识位）。
   *
   * ⚠️ 缺键 = **主数据缺行**（该 Task 有资源但资源主数据没有编码），不是「没投影」。
   */
  resourceCode?: string
  /** 设备资源名（缺键 = 无设备资源） */
  resourceName?: string
  /** 人工槽 Id（内部号，不上列表） */
  manualSlotId?: number
  /** 人工槽编码（**跨部门不唯一** ⇒ 须与部门名组合显示） */
  manualSlotCode?: string
  productionDepartmentId?: number
  productionDepartmentName?: string
  /** 计划净量（= `Task.Quantity`） */
  quantity: number
  /** 计划加工量（可空 = 未提供 ⇒ 不拿净量顶替） */
  plannedProcessQty?: number
  uom: string
  plannedStartTime?: IsoDateTime
  plannedEndTime?: IsoDateTime
  /** 计划工时（小时；可空 = 未知） */
  duration?: number
  /** APS 计划状态（原值透传，后端不做白名单映射） */
  status: string
  /**
   * 不可移动标识。
   *
   * ⚠️ **实库恒 false**（写方 `PeggingOrchestrator` 写死 false，`DemandSupplyHardLock` 0 行）
   *   ⇒ 前端**不渲染**该列（显示「否」等于替系统断言「这条不是不可移动」）。
   */
  isLocked: boolean
  /**
   * 关键路径判定。
   *
   * ⚠️ 与甘特体的**三态**不同：本读面投影的是真实列 `Task.IsCriticalPath`（非空 bool）。
   *   但实库同样恒 false（1号位 求解算法未落地）⇒ 前端**不渲染**该列。
   */
  isCriticalPath: boolean
  /** 任务类型（现全库单值 PRODUCTION） */
  taskType: string
  /** 生产指示号（= `Task.MTS_InstructionNo`；JSON 键 `mtsInstructionNo`） */
  mtsInstructionNo?: string
  /**
   * MES 工单号（= `Task.MESWorkOrderNo`；JSON 键 `mesWorkOrderNo`）。
   *
   * ⚠️ 缺键 = **未关联 MES 工单**；它**不**表示「无需 MES」也**不**表示「不可下发」
   *   （权威分类标志位尚未建立，见矩阵 A4 与 Q-a）⇒ 前端不得由空值推断分类。
   */
  mesWorkOrderNo?: string
  /**
   * MES 下发状态（源 = TaskDispatch.DispatchStatus，JSON 键 `mesDispatchStatus`）。
   *
   * ⚠️ **null 的语义是「该 Task 不在下发队列」**（5号位 契约护栏）——
   *   **≠「未下发」**、**≠「无需 MES」**（队列资格权威位未建，别由空值推断分类）。
   * ⚠️ 值域现只有 `CREATED` / `PENDING`（`PROCESSING` / `FAILED` 现不落痕）⇒
   *   **别按四态全集解释它**；词典未收录的值原样显示后端码。
   *
   * ✅ **2026-11-18（r14281）该属性已由后端补上**（`TaskQueryDtos.cs` 增 `MesDispatchStatus`）
   *   ⇒ 键一出现本列即生效，**前端零改动**。
   *   ✅ **2026-11-18 复函V 后 dev 已更新到 r14282**（Owner 执行 update＋重建＋重启，
   *   进程 PID 12044 / 19:34:02）⇒ 实测 999473 全量 28,021 行中 **2,694 行带键**（值全 `CREATED`）、
   *   25,327 行不带键（**不在下发队列**）；P06 详情 `task.mesDispatchStatus = "CREATED"`。
   *   （此前"读不到键"是**环境滞后**，非契约不符 —— 已销账。）
   *
   * ⚠️ **双键名过渡期（5号位 2026-11-18 R3″ 裁）**：同一权威位两个键名 ——
   *   甘特行 `dispatchStatus`（11-05 起已消费，历史契约不擅改）／本 DTO `mesDispatchStatus`（终名）。
   *   统一为一个键＝破坏性变更，归 V1.1 破坏窗 ⇒ **前端两处各自保留、不得自行合并或改名**，
   *   值域与中文词典同源（`mesDispatchStatusLabel`）。
   *
   * ✅ **V1.1 映射表（三键版）已出**（5号位 2026-11-19 复函 §二，即刻生效的过渡契约）：
   *   · 本键 = 映射表 **键②** = **终名，不变**；
   *   · 键①（甘特位 `dispatchStatus`）与键③（P11 工序行 `dispatchStatus`）统一 → 本键；
   *   · **收口批次 = V1.1 破坏窗**（0号位 核准后执行）⇒ **核准前我方不改名**，仅按表登记。
   *
   * ⚠️ **缺键语义 = 「该 Task 不在下发队列」**（护栏语义表 r14286 第 17 行）
   *   ⇒ 页面显「**不在下发队列**」；**禁**解释成「未下发」/「无需 MES」。
   *   ⚠️ **实为三个键名**（复函V 后我方核出）：另有 **P11 批次工序行** `dispatchStatus`
   *   （`MesStatusOperationRowDto`，见 `types/mes.ts`）——该键**行驱动、语义差一拍**，
   *   已在 5号位 2026-11-19 复函 §二 键③ 收录（我方按行渲染，**不套用本键的分类文案**）。
   */
  mesDispatchStatus?: string
  /** 换型来源标签（可空 = 未提供） */
  setupSource?: string
}

/**
 * P06 单任务详情（5号位 2026-11-16 矩阵 A5 / r14237）
 *
 * `GET /api/schedule/task/{taskId}`
 *
 * ⚠️ **三态分报，禁混**（后端源码 `GetTaskDetailAsync`）：
 *  - `found=false` ⇒ 该 taskId 不存在（**不是**无权限）
 *  - `found=true && scopeDenied=true` ⇒ 任务存在但**越权**（域/部门范围不足）
 *  - `found=true && scopeDenied=false` ⇒ 可读
 * ⚠️ **前后依赖边、MES 执行事实 = 未提供**（后端未投影）⇒ 前端显「未提供」，不造。
 */
export interface TaskDetailResultDto {
  /** 该 taskId 是否存在 */
  found?: boolean
  /** 越权（true ⇒ 任务存在但当前账号不可读） */
  scopeDenied?: boolean
  /** 任务行（字段同 `TaskListItemDto`） */
  task?: TaskListItemDto
  /** 计划版本编码 */
  versionCode?: string
  /** 所属排程域 */
  domainKey?: string
  /** 版本状态（ACTIVE / CANDIDATE / ARCHIVED） */
  planVersionStatus?: PlanVersionStatus
  /** 快照数据截止时间（僵尸/失败源 Run 如实给原值） */
  dataCutoffTime?: IsoDateTime
  /** 订单份额（**逐行全量**：共享 Task 多根订单逐行给，不贴单一订单） */
  shares?: TaskShareDetailDto[]
  /** 份额是否由后端下发（false = 未提供；true = 已下发，即使为空数组） */
  sharesProvided?: boolean
}

/**
 * 任务份额一行（矩阵 A5：`AllocationTaskShare`）
 *
 * ⚠️ `shareQty` 为 0/缺键 = **「无份额记录」** ≠ 「0 需求」；
 *   `rootOrderNo` 缺键 = 后端反查不到订单号（**不是**无订单）。
 */
export interface TaskShareDetailDto {
  /** 分配顺序号（同一 Task 内唯一） */
  allocationSequence?: number
  /** 需求类型（如 ORDER） */
  demandType?: string
  /** 需求键 */
  demandKey?: string
  /** 根订单 Id（内部号，不上列表） */
  rootOrderId?: number
  /** 根订单号（列表显示用） */
  rootOrderNo?: string
  /** 份额数量（可空 = 未提供） */
  shareQty?: number
}

/** 排程概要 KPI（顶部面板用） */
export interface ScheduleSummaryDto {
  planVersionId: number
  versionCode: string
  status: PlanVersionStatus
  totalTasks: number
  scheduledTasks: number
  unscheduledTasks: number
  /**
   * 🔴 **口径与甘特体不一致，暂不可用于展示**（2026-10-19 实测 v2：summary 191 vs 甘特体 179）。
   * 甘特体 `GanttTaskDto.isDelayed` 按**日期**比（`CAST(PlannedEndTime AS date) > CustomerDueDate`，
   *   2026-10-17 按 4号位 Q1 修正，交期当天完工不算延期）；本字段仍是
   *   `PlannedEndTime > CustomerDueDate` 裸 datetime 比（`ScheduleQueryService.cs:301`），
   *   把「交日当天 00:00 之后完工」也计入 ⇒ v2 上多出 12 条（179 + 12 = 191，逐条对得上）。
   * ⇒ 前端甘特页的「延期任务数」仍从甘特体现算，不用本字段。已出函 5号位。
   */
  delayedTasks: number
  totalOrders: number
  delayedOrders: number
  /** 无任务时整键省略 */
  firstTaskStart?: IsoDateTime
  /** 无任务时整键省略 */
  lastTaskEnd?: IsoDateTime
  /**
   * ⚠️ **恒缺键**：后端 2026-10-17 删掉了 `pv.DurationSeconds`（该列只在 `ScheduleRun` 上，
   * `PlanVersion` 没有），DTO 字段保留但**不再有取值来源**。5号位 回执 §六 已确认。
   * 「求解耗时」若要展示，需走 `ScheduleRun.DurationSeconds` 的独立字段/端点。
   */
  computeDurationSeconds?: number
  /**
   * 版本是否真实存在。`false` = 该 planVersionId 不存在（此时计数全 0），
   * 不得当成「该版本存在但没有排程结果」。与 `scopeDenied` 配合区分三种空态。
   *
   * 5号位 2026-10-19 §2.3 把原键名 `versionFound` 统一为 `versionExists`（与 `GanttDataDto` 同名），
   * 已于 **2026-10-19 实测部署到 dev**（`GET /api/schedule/summary/2` 返回 `versionExists: true`）。
   */
  versionExists: boolean
  /** true = 因排程域授权不足被拒（fail-closed），是「越权」不是「没数据」。dev 实测返回 `false` */
  scopeDenied: boolean
}
