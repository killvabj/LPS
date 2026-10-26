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
import type { ReasonCode } from './explanation'
import type { SetupSource } from './setup'

/** 计划版本摘要（版本下拉用） */
export interface PlanVersionSummaryDto {
  id: number
  versionCode: string
  versionCategory: string
  planHorizonStart: IsoDateTime
  planHorizonEnd: IsoDateTime
  status: PlanVersionStatus
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
  /** 冻结 `07:3683` / `08:4249`：1=关键路径。dev 当前全 0（2号位 未标记） */
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
  /** 4号位文档第 6 节：不可移动标识（仅 UI 显示） */
  lockMarker?: TaskLockMarker
  /**
   * U04：TaskShare — 一个 Task 承接多 Order 时，每个 Order 承担的数量
   *  - 当 taskShare 仅一条且 orderId == task.orderId 时 = 单 Order
   *  - 当 taskShare 多条或包含非主 orderId 的项 = 共担
   */
  taskShare?: TaskShareLine[]
  /**
   * U14：是否被外 Domain 共享设备阻挡
   *  - true 时 UI 必须在任务条 + 详情 Drawer 显示阻挡原因
   *  - `null`/`undefined` = **未判定**（判定未接入），不是「未阻挡」
   */
  crossDomainBlocked?: boolean | null
  /** U14：阻挡原因（外 Domain 共享设备 / 资源冲突 等） */
  crossDomainBlockReason?: string
  /**
   * U06：数量-时间拆分（一个 Task 的数量被拆为多个时段加工）
   *  - 存在时 UI 渲染为多段连续条 + Drawer 显示拆分明细
   *  - 例：100 PCS 拆为 [60 PCS @ STG-1, 40 PCS @ STG-2]
   */
  taskSegments?: TaskSegmentLine[]
  /**
   * Section 二.2：四类信息标签（FACT / RESULT / ESTIMATED / RECOMMENDATION）
   *  - FACT          设备故障 / PI Position / PO ETA 等真实事实
   *  - RESULT        FinalTask / PlannedStart/End 等排程结果
   *  - ESTIMATED     Planning-only Purchase Placeholder 等估算
   *  - RECOMMENDATION 建议发起重排 / 建议确认 Candidate
   *  - 不显式给出时默认 RESULT
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
   * Section 十一：异常与原因解释 — 根因（不只 DUE_DATE_RISK）
   *  - 后端 GanttDataDto 当前不带，由 3 号位落地后对齐；
   *  - 前端先以可选项存在，4 号位文档第 11 节硬约束：必须展示根因，不能只丢 DUE_DATE_RISK
   *  - issueCategory 区分数据问题 / 排程问题（Section 11 第三段）
   */
  delayReasons?: GanttTaskDelayReason[]
  /** Section 11：是否被上游 Stage 阻挡（前序延迟） */
  upstreamStageBlocked?: boolean
  /** Section 11：上游 Stage 名称 */
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
   */
  reasonCode: ReasonCode
  /** 数据问题 vs 排程问题（Section 11 第三段；后端 `GanttDelayReasonDto.IssueCategory`） */
  issueCategory: 'DATA_ISSUE' | 'SCHEDULING_ISSUE'
  /**
   * 严重度 —— 冻结 `07 v5.1.13 :4938`：`INFO / WARN / ERROR`，**不含 CRITICAL**
   * （CRITICAL 仅用于 BOM Issues 表）。此前误含 `CRITICAL` / `WARNING`，一并归一。
   */
  severity: 'INFO' | 'WARN' | 'ERROR'
  /** 文档第 11 节列出的根因示例：采购料9月3日才可用、MC01无产能、上游Stage完成晚、Firm任务占关键窗口 */
  description: string
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
  /** 该 Order 在 Task 上的占比（0-1）；按 shareQty / plannedProcessQty 计算 */
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
