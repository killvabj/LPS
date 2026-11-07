/**
 * 排产总览 DTO —— 对齐 5号位 OverviewController 真实端点（2026-10-16）
 *
 * Overview **没有聚合端点**（5号位 2026-10-15 定案），四个数据面各自独立调：
 *  - GET /api/overview/active-plan               → ActiveVersionPanel[]（每 Domain 一行；planVersionId 唯一来源）
 *  - GET /api/order-query/summary?planVersionId= → 订单摘要（总数 + 4 类计数）
 *  - GET /api/overview/resource-bottleneck?planVersionId= → 资源负荷 + 不可用窗口
 *  - GET /api/overview/candidate-summary         → 待确认 Candidate 数
 *  - GET /api/domain-status/{scheduleRunId}      → 排程域状态（6 值）
 *
 * 已撤：`PlanOverviewDto` 复合体（mock 时代设计，后端从未实现该聚合端点）。
 * 已删：`estimatedOnlyOrderCount` / 「仅估算」订单卡 ——
 *       冻结 07 §6.6a + 规则 P-009：规划性采购占位**仅内存、不建表**，
 *       Overview 是事后读模型，物理上给不出（5号位 2026-10-15 §十 结论，4号位 10-16 回 (A) 删除）。
 */

import type { DomainKey, IsoDateTime, PlanVersionStatus } from './common'
import type { OrderSummaryDto } from './order'

/** 当前 ACTIVE 版本（/api/overview/active-plan 数组元素）
 *  - 每个 Domain 至多一行（库级唯一索引 UQ_PlanVersion_OneActivePerDomain，前端无需去重）
 *  - 无 ACTIVE / 无授权域 → 后端返**空数组**（旧版返 null，前端两种都要兼容）
 */
export interface ActiveVersionPanel {
  planVersionId: number
  versionCode: string
  domainKey: DomainKey | null
  /** 版本生命周期状态；下钻为 ACTIVE 行时一般为 'ACTIVE' */
  status: PlanVersionStatus
  planHorizonStart: IsoDateTime
  planHorizonEnd: IsoDateTime
  /** 后端 WhenWritingNull ⇒ 该字段可能整个缺失，渲染需容缺 */
  activatedAt?: IsoDateTime | null
  sourceScheduleRunId?: number | null
  /** 以下为后端附加字段（可选消费） */
  /**
   * 计划任务数（PlanVersion 表冗余列，`OverviewQueryRepository.cs:135` `ISNULL(TotalTasks,0)`）。
   * 页面在「当前生效版本」卡里展示此值。
   */
  totalTasks?: number
  /**
   * 计划订单数（PlanVersion 表冗余列）。
   *
   * ⚠️ **与实时计数不一致，页面暂不渲染**：dev 实测 planVersionId=999334
   *   本列 = 53，而 `/api/order-query/summary` 的 `TotalCount`
   *   （`OrderQueryRepository.cs:307+` = `COUNT(*) FROM [Order] WHERE PlanVersionId=@Id`）= 55。
   *   同页两个数字打架 ⇒ 页面不展示这一列（展示实时值即可），差额已出函请 5号位 核对
   *   （冗余列是登记时的快照，还是实时维护？）。**不要**在页面把两个数并排显示。
   */
  totalOrders?: number
  createdAt?: IsoDateTime
}

/** 订单摘要（/api/order-query/summary）
 *
 * 与订单页共用同一份 DTO（别名指向 `types/order.ts` 的 `OrderSummaryDto`），
 * 避免第二份字段列表与第一份漂移。
 *
 * 注意：**没有** estimatedOnlyCount —— 后端 DTO 无该字段（见文件头「已删」说明）。
 * `riskCount` / `unscheduledCount` 前端**均不渲染**，理由见 `OrderSummaryDto` 各字段注释。
 */
export type OrderSummaryCounts = OrderSummaryDto

/** 资源负荷（/api/overview/resource-bottleneck 数组元素） */
export interface OverviewResourceBottleneck {
  resourceId: number
  resourceCode: string
  resourceName: string
  resourceType: string
  taskCount: number
  totalPlannedHours: number
  /** 负荷率 = 实际工时 / 可用工时，>1 为过载
   *
   * ⚠️ 后端当前是 `NULL AS UtilizationRate` **占位字面量**（`OverviewQueryRepository.cs:91`），
   *   可用工时来自三级日历，尚未接入 ⇒ 恒缺键/恒 null。前端显示 `-`，不拿它算百分比。
   *
   * 📌 **口径已锁、落地时点 = V1.1**（5号位 2026-11-02 复函 §二；冻结 `01 §77A.1` 负荷率后移 V1.1，
   *   前移申请已终止，不再提）：
   *   - 分子 = **工时口径**（任务工时按桶重叠**切分**后求和；V1.1 拟名 `loadHoursInBucket`）；
   *     ⛔ **墙钟 `occupiedHours` 方案已撤回** —— 跨天任务 119 条，夜间时段不是产能占用，
   *        按墙钟算会把正常排产天天标成超载（假负荷信号）。
   *   - 分母 = `availableHours`（三级合并窗口 ∩ 桶；授权加班计入；未登记 = 缺键 ≠ 0）—— 这半边未被证伪，V1.1 沿用。
   *   - 分母缺键 ⇒ 不判。⛔ `loadHours`（整条任务工时计入**起点桶**）仍**禁止**直接作分子
   *      （日桶宽下必然虚高：40 h 任务起点周一 ⇒ 周一格 444%）；修法 = 按桶**切分工时**，不是切墙钟。
   *   - ⛔ **超载判定不随本轮落地**：将来若做，判据同为工时口径（Σ工时In桶 > 可用窗口分钟），
   *      且需 0号位 定「回归仪表 vs 告警」定位。
   */
  utilizationRate?: number | null
  /**
   * ⚠️ 当前**恒 false 且是字面量**（`OverviewQueryRepository.cs:93` `0 AS IsBottleneck`），
   *   同块注释自陈「Bottleneck判定应由 2号位 正式Query提供，5号位 不做二次计算」。
   * ⇒ `false` 区分不了「判定为非瓶颈」与「未判定」，**前端不得据此渲染「正常」**。
   *
   * 📌 **不要等 2026-09-21 R1 契约**：那一版（C2/C3 段）已于 **2026-09-22 作废**，
   *   `availableHours / plannedHours / loadRate` **不会再来**。现行口径见上（工时分子 / `availableHours`）。
   */
  isBottleneck: boolean
  /** 资源级「明确停机」窗口（ResourceCalendarSlot.AvailableFlag = 0），
   *  已限定在该 planVersionId 的计划范围内；无 OFF 时为空数组（非 null）
   */
  unavailableWindows: UnavailableWindow[]
}

export interface UnavailableWindow {
  from: IsoDateTime
  to: IsoDateTime
  /** 取自窗口 Remark，可空（后端 WhenWritingNull 会省略该字段） */
  reason?: string | null
}

/** 排程域状态值域（5号位 2026-10-15 §7.2 定的 6 值；3号位 RunLifecycleService 由 PlanVersion.Status 派生）
 *  历史：前端曾有 4 分类 FAILED / BLOCKED_BY_UPSTREAM / PI_POSITION_ISSUE / ODS_CONTRACT_FAILED，
 *        其中后 3 个后端**无此状态值** ⇒ 收敛（细分说明走 reason 字段，不进状态枚举）
 */
export const RUN_DOMAIN_STATUSES = [
  'COMPLETED',
  'CANDIDATE',
  'RUNNING',
  'FAILED',
  'BLOCKED',
  'NOT_STARTED'
] as const

export type RunDomainStatus = (typeof RUN_DOMAIN_STATUSES)[number]

export const RUN_DOMAIN_STATUS_LABELS: Record<RunDomainStatus, string> = {
  COMPLETED: '已完成',
  CANDIDATE: '待确认',
  RUNNING: '进行中',
  FAILED: '失败',
  BLOCKED: '被阻断',
  NOT_STARTED: '未开始'
}

export const RUN_DOMAIN_STATUS_TAG: Record<
  RunDomainStatus,
  'success' | 'primary' | 'warning' | 'info' | 'danger'
> = {
  COMPLETED: 'success',
  CANDIDATE: 'warning',
  RUNNING: 'primary',
  FAILED: 'danger',
  BLOCKED: 'danger',
  NOT_STARTED: 'info'
}

/** 需要报为「异常」的域状态（其余为正常/进行中） */
export const RUN_DOMAIN_ISSUE_STATUSES: RunDomainStatus[] = ['FAILED', 'BLOCKED']

/** 排程域状态（/api/domain-status/{scheduleRunId} 数组元素） */
export interface RunDomainStatusItem {
  domainKey: DomainKey
  status: RunDomainStatus | string
  /** FAILED = 失败信息；BLOCKED = 被阻断说明；其余可为空 */
  reason?: string | null
  planVersionId?: number | null
  planVersionCode?: string | null
  startedAt: IsoDateTime
  computedAt?: IsoDateTime | null
  activatedAt?: IsoDateTime | null
}

/** 待处理 Candidate 摘要（/api/overview/candidate-summary） */
export interface OverviewCandidateSummary {
  pendingCount: number
  candidates: OverviewCandidateBrief[]
}

export interface OverviewCandidateBrief {
  planVersionId: number
  versionCode: string
  domainKey?: DomainKey | null
  createdAt: IsoDateTime
  createdByUserName?: string | null
  status?: string | null
  sourceScheduleRunId?: number | null
  runType?: string | null
  basePlanVersionId?: number | null
  canActivate: boolean
}
