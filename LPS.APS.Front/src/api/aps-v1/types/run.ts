/**
 * 运行 / 候选触发 DTO —— 对齐后端**已部署**契约（2026-10-16）
 *
 * 2026-10-16 代码级审计修正（`/aps/run` 页面恒空）：
 *  此处的 `RunStatusDto` 聚合体是 4号位 早前自拟的，后端**从未实现**。
 *  `GET /api/governance/runs` 的端点说明已写明「**只查 ScheduleRun 表、不做跨域综合统计**」，
 *  实际返回的是 `ScheduleRunGov[]` 裸数组；页面按聚合体取值 ⇒ `recentRuns` 恒 undefined ⇒
 *  整页 KPI 恒 0。现按三个真实数据面重排：
 *   - Run 列表   ← `GET /api/governance/runs`            （3号位，裸数组）
 *   - 生效版本   ← `GET /api/overview/active-plan`       （5号位，`ActiveVersionPanel[]`）
 *   - 域状态拆解 ← `GET /api/domain-status/{runId}`      （5号位，`RunDomainStatusItem[]`）
 *
 * 已删（后端无源，见《4号位致3号位/5号位·运行页联调缺口回执》）：
 *  - `RunStatusDto` 聚合体 · `MesEligibilityDto` · `MesDispatchInput` · `MesDispatchResult`
 *  - `PartialSuccessBreakdown` / `DomainRunResult`（改用 5号位 `/api/domain-status/{runId}`）
 *  - `PlanVersionDto`（改用 5号位 `ActiveVersionPanel`）
 */

import type { DomainKey, IsoDateTime, TaskStatus } from './common'

/**
 * ScheduleRun 状态权威值域 —— 冻结 07 §4.5：`RUNNING / COMPLETED / PARTIAL_SUCCESS / FAILED`，默认 COMPLETED。
 * @see 冻结文档/07_APS_数据库字段说明文档 §4.5 ScheduleRun 表 Status 行
 *
 * ⚠️ 历史踩坑：前端曾自拟 `PENDING` / `SUCCESS`，冻结值域里**都不存在**，而真正的 `COMPLETED` 反而漏了
 *    ⇒ 「成功」卡恒 0、后端真跑出 COMPLETED 时不计入任何卡片。
 */
export const RUN_STATUSES = ['RUNNING', 'COMPLETED', 'PARTIAL_SUCCESS', 'FAILED'] as const
export type RunStatus = (typeof RUN_STATUSES)[number]

/** ScheduleRun 运行类型（冻结 07 §4.5：FULL_SCHEDULE / MANUAL_RESCHEDULE / LOCAL_RESCHEDULE / SIMULATION / INSERT_ORDER_WHATIF） */
export const RUN_TYPES = [
  'FULL_SCHEDULE',
  'MANUAL_RESCHEDULE',
  'LOCAL_RESCHEDULE',
  'SIMULATION',
  'INSERT_ORDER_WHATIF'
] as const
export type RunType = (typeof RUN_TYPES)[number]

/**
 * ScheduleRun 行 —— 对齐后端 `ScheduleRunGov`（`GET /api/governance/runs` 的数组元素）。
 * @see lps/LPS.APS.Core/DTOs/Governance/ScheduleRunGov.cs
 *
 * ⚠️ 后端 `WhenWritingNull` 会把 null 字段**整个从 JSON 里省掉**，故除 id/runType/status/
 *    dataCutoffTime/startedAt 外全部按可选处理，模板不得假设它们一定在。
 */
export interface ScheduleRunGovDto {
  id: number
  runType: RunType
  status: RunStatus
  /** 触发来源：'Hangfire' / UserId / 'API' / 'Agent' */
  triggeredBy?: string | null
  /** 本次运行统一数据切片边界 */
  dataCutoffTime: IsoDateTime
  /** 绑定的策略包版本 Id（可为 null = 无绑定） */
  strategyProfileVersionId?: number | null
  /** 白天 Candidate 所基于的当前 ACTIVE 计划版本 */
  basePlanVersionId?: number | null
  /** 运行启动时冻结的预期 Domain 集合（JSON 数组字符串；后端不给解析后的数组） */
  expectedDomainKeysJson?: string | null
  /** 局部重排范围 JSON（v5.1.4 起不再承载 ExpectedDomainKeys，仅保留只读） */
  scopeJson?: string | null
  startedAt: IsoDateTime
  /** 终态完成时间（COMPLETED / PARTIAL_SUCCESS / FAILED 均写入；RUNNING 时为 null ⇒ 键缺失） */
  completedAt?: IsoDateTime | null
  /** 失败错误信息（仅 FAILED） */
  errorMessage?: string | null
}

/** FAILED 恢复输入（U19：新建 Run，不直接改 FAILED → RUNNING） */
export interface RecoverFailedRunInput {
  originalRunId: number
  domainKeys: DomainKey[]
  reason: string
  actor: string
}

/** FAILED 恢复结果 */
export interface RecoverFailedRunResult {
  newRunId: number
  originalRunId: number
  /** 原始 Run 状态保持 FAILED（4号位文档第 15 节） */
  originalRunStatus: 'FAILED'
  newRunStatus: RunStatus
  startedAt: IsoDateTime
}

/**
 * 候选重排触发输入（4号位文档第 6 节最后一句 + 审核报告 P0-02 / P0-03）
 *
 *  - runType 决定 LOCAL_RESCHEDULE（小范围）vs MANUAL_RESCHEDULE（较大范围人工主动）
 *  - 小范围 = 单 Task / 单 Resource / 指定订单 / 指定时间窗口
 *  - 较大范围 = 多 Task 跨 Domain 的人工主动重排（Gantt toolbar"批量人工重排"入口）
 *  - P0-02 严格单 Domain：跨 Domain CTP 由多个单 Domain WHATIF 链式计算后汇总
 *  - P0-03 范围 → runType 分流：
 *      局部明确范围 → LOCAL_RESCHEDULE（单值 objectRefId）
 *      较大范围人工主动 → MANUAL_RESCHEDULE（多 Task objectRefIds）
 *  - actor / reason 必填，落到 Section 17 审计
 *  - objectRef / objectRefIds 用于审计（Section 17）
 *  - Section 12 设备故障也可借此触发"建议重排"
 *
 * objectRefId 与 objectRefIds 互斥：
 *  - objectRefType='TASK' 且 runType='MANUAL_RESCHEDULE' → 必须传 objectRefIds（批量）
 *  - 其他 objectRefType（ORDER / DOMAIN / RESOURCE）以及 LOCAL_RESCHEDULE → 必须传 objectRefId（单值）
 *
 * 2026-09-17 3号位 回执后重构（@see 3号位回执_致4号位_治理端点对接确认_v1.0_20260917.md §四）：
 *  - 后端实际入参 `CreateCandidateRunRequest`（非 `TriggerRescheduleInput`）
 *  - 字段对齐：reason → Remark；objectRefIds 砍掉（前端传不到后端）；新增 Purpose（与 RunType 配对）
 *  - 权限码：`aps.plan.run`（后端 L626 已挂；aps.run.reschedule / aps.run.candidate 均不存在）
 *  - RunType×Purpose 冻结合法组合：
 *      MANUAL_RESCHEDULE → MANUAL_ADJUSTMENT
 *      LOCAL_RESCHEDULE → INSERT_RESCHEDULE | MANUAL_ADJUSTMENT
 *      INSERT_ORDER_WHATIF → CTP | INSERT_IMPACT_ANALYSIS（永不可激活）
 */
export interface TriggerRescheduleInput {
  /** runType 分流（P0-03）；INSERT_ORDER_WHATIF 仅供 WHATIF 码（永不可激活） */
  runType: 'LOCAL_RESCHEDULE' | 'MANUAL_RESCHEDULE' | 'INSERT_ORDER_WHATIF'
  /** Purpose 必填，与 runType 配对（@see RunLifecycleService.cs:65-71） */
  purpose: 'MANUAL_ADJUSTMENT' | 'INSERT_RESCHEDULE' | 'CTP' | 'INSERT_IMPACT_ANALYSIS'
  /** 单计划域（P0-02：白天候选运行严格单计划域；跨 Domain 由多个单 Domain 链式调用） */
  domainKey: DomainKey
  /** 触发人（必填，Section 17 审计要求） */
  actor: string
  /**
   * 备注：**前端停传**（2026-09-21 3号位 回执 §一.3——`CandidateRunCreateSpec` 无 Remark 属性，
   * 后端维持丢弃；DTO 上保留字段仅为契约位标注）。保留可选属性仅为兼容旧调用方。
   */
  remark?: string
  /** 基于的 ACTIVE 计划版本（缺省按 DomainKey 解析 ACTIVE） */
  basePlanVersionId?: number
  /** 数据切片边界（缺省 now） */
  dataCutoffTime?: IsoDateTime
  /**
   * §10A 白天人工调整范围载荷（ScopeJsonV2）。缺省不传 = 旧调用兼容（后端静默放行）。
   * @see 冻结文档《4号位页面与业务操作开发实施包 v1.4》§十A
   */
  scope?: ScopeJsonV2
}

/* ==================== §10A 白天人工调整：ScopeJsonV2 契约 ==================== */

/**
 * 八码业务触发类型（后端 `BusinessTriggerType`，`[EnumMember]` 契约字面量，字符串传输非数字）。
 * @see lps/LPS.APS.Core/Enum/BusinessTriggerType.cs:15-40
 */
export const BUSINESS_TRIGGER_TYPES = [
  'NEW_ORDER_CTP',
  'NEW_ORDER_IMPACT',
  'NEW_ORDER_INSERT',
  'EXISTING_ORDER_ADVANCE',
  'GANTT_ADJUSTMENT',
  'EQUIPMENT_FAILURE',
  'RESOURCE_CALENDAR_CHANGE',
  'DOMAIN_MANUAL_RESCHEDULE'
] as const

export type BusinessTriggerType = (typeof BUSINESS_TRIGGER_TYPES)[number]

/** 八码中文 label（§10A 入口标题 / 下拉项直接用） */
export const BUSINESS_TRIGGER_LABELS: Record<BusinessTriggerType, string> = {
  NEW_ORDER_CTP: '新订单 CTP 评估',
  NEW_ORDER_IMPACT: '插单影响分析',
  NEW_ORDER_INSERT: '新订单插单',
  EXISTING_ORDER_ADVANCE: '已有订单提前',
  GANTT_ADJUSTMENT: '甘特图调整',
  EQUIPMENT_FAILURE: '设备故障后重排',
  RESOURCE_CALENDAR_CHANGE: '资源日历调整后重排',
  DOMAIN_MANUAL_RESCHEDULE: '整 Domain 人工重排'
}

/** 优先模式（后端 `PriorityMode`，字符串字面量） */
export type PriorityMode = 'NORMAL' | 'EXPEDITE'

export const PRIORITY_MODES = ['NORMAL', 'EXPEDITE'] as const

export const PRIORITY_MODE_LABELS: Record<PriorityMode, string> = {
  NORMAL: '普通（NORMAL）',
  EXPEDITE: '加急（EXPEDITE）'
}

/** 订单目标：本次 Candidate 的手工目标交期（不修改正式 DueDate） */
export interface OrderTargetDto {
  /**
   * 订单规范 Id（后端 `Order_Canonical.Id`，long）。
   * ⚠️ 与订单查询列表的 `orderId`（`[Order].Id`，本地分区自增主键）**不是同一 ID 空间**。
   */
  orderCanonicalId: number
  /** 本次 Run 的手工目标交期（ISO） */
  manualTargetDueDate: IsoDateTime
}

/** Task 目标：软目标时间（Solver 可返回其它可行时间，禁止直接 UPDATE 正式 Task） */
export interface TaskTargetDto {
  /** Gantt/Versions 任务主键 */
  taskId: number
  /** 软目标时间（后端为非空 DateTime，必填） */
  targetTime: IsoDateTime
}

/**
 * 局部重排范围载荷 v2（后端 `ScopeJsonV2`，camelCase）。
 * @see lps/LPS.APS.Core/DTOs/Governance/ScopeJsonV2.cs:12-28
 *
 * 后端校验（`ScopeJsonV2Validator`，违规 → HTTP 400）：
 *  - 八码 ↔ RunType × Purpose 固定映射（大小写敏感精确匹配）
 *  - PriorityMode 轴：GANTT/EQUIPMENT/RESOURCE_CALENDAR/NEW_ORDER_CTP 禁 EXPEDITE、
 *    NEW_ORDER_IMPACT 禁 NORMAL、DOMAIN_MANUAL_RESCHEDULE **须省略该键**
 *  - orderTargets 按 orderCanonicalId 去重唯一
 */
export interface ScopeJsonV2 {
  trigger: BusinessTriggerType
  /** 省略 = 不指定（后端对部分码要求必须省略该键） */
  priorityMode?: PriorityMode
  /** 订单目标（10A.1） */
  orderTargets?: OrderTargetDto[]
  /** Task 软目标（10A.2） */
  taskTargets?: TaskTargetDto[]
  /** 已正式不可用 / 已正式改 Calendar 的资源 Id（10A.3 / 10A.4） */
  changedResourceIds?: number[]
}

/** 候选重排触发结果：返回新的 ScheduleRunId + Candidate PlanVersionId */
export interface TriggerRescheduleResult {
  newRunId: number
  /** 产出的 CANDIDATE PlanVersionId（前端跳到 Candidate 对比页） */
  candidatePlanVersionId: number
  /** 基准 PlanVersionId（当前 ACTIVE 版本） */
  basePlanVersionId: number
  startedAt: IsoDateTime
  /** 预计运行耗时（秒） */
  estimatedDurationSeconds?: number
}

/** @deprecated 用 TriggerRescheduleInput；保留以兼容老调用方 */
export type TriggerManualRescheduleInput = TriggerRescheduleInput
/** @deprecated 用 TriggerRescheduleResult */
export type TriggerManualRescheduleResult = TriggerRescheduleResult

/** Task 状态机检查器（前端只读展示，禁止转换） */
export function assertValidTaskStatus(status: string): TaskStatus {
  const valid = ['PLANNED', 'RELEASED', 'IN_PROGRESS', 'COMPLETED', 'CANCELLED']
  if (!valid.includes(status)) {
    throw new Error(`[4号位文档第6节] Task 状态非法: ${status}，仅允许 ${valid.join('/')}`)
  }
  return status as TaskStatus
}
