export type DhxGanttScaleUnit = 'day' | 'week' | 'month' | 'hour'

export interface DhxGanttTask {
  id: string | number
  text: string
  start_date: string
  duration: number
  progress?: number
  actualStart?: string
  actualEnd?: string
  parent?: string | number
  type?: 'task' | 'project'
  open?: boolean
  readonly?: boolean
  color?: string
  textColor?: string
  orderId?: string
  materialName?: string
  laneId?: string
  status?: string
  details?: string
  changeReason?: string
  /** 左表「开始」列文本（YYYY-MM-DD HH:mm；泳道无任务时留空，不写假时间） */
  startText?: string
  /** 左表「结束」列文本（YYYY-MM-DD HH:mm；任务=计划完工，泳道=占用区间终点） */
  endText?: string
  /** 左表「数量」列文本（计划加工数量 + 单位） */
  qtyText?: string
  /** 任务条标签（「工序 · 生产指示号」）。条宽撑得下写条内，撑不下写条右侧。 */
  barLabel?: string
  /** 条内状态标记类型：`done` 已完成打勾 / `delay` 延期 */
  barMark?: 'done' | 'delay' | ''
  /** 条内标记文字（`✓` / `+3h`），空串 = 不画 */
  barMarkText?: string
  /**
   * 悬浮提示行（页面层按业务口径拼好：任务号/订单/资源/工时/状态/延期…）。
   * 组件只负责渲染，不自己拼业务语义。
   */
  tooltipLines?: string[]
  /** 泳道行在该时间窗内没有任务（不画占用条，避免兜底假条） */
  laneEmpty?: boolean
  /** 行级条高（dhtmlx 原生支持）；泳道摘要条固定 16px，比任务条矮一半 */
  bar_height?: number
  warningCount?: number
  isCritical?: boolean
  zone?: 'frozen' | 'firm' | 'open'
  reasonCode?: string
  delayedHours?: number
  planFreezeFlag?: boolean
  executionLockFlag?: boolean
  isLocked?: boolean
  lockSource?: 'none' | 'plan_freeze' | 'execution' | 'manual' | 'mixed'
  exceptionSeverity?: 'none' | 'low' | 'medium' | 'high'
  laneStatus?: 'normal' | 'maintenance' | 'overloaded'
  freezeBoundary?: string
}

/**
 * 资源×时间桶负荷带（数据源 = 5号位 2026-10-30 `GET /api/schedule/gantt/{id}/buckets`）
 *
 * ⚠️ `start` / `end` 是**朴素本地墙钟**（后端 naive 口径，与甘特条同一时区基准），
 *   `end` 为**开区间右端**（桶起点 + 生效桶宽）⇒ 组件直接按此画，不再做时区换算。
 */
export interface DhxGanttLoadBand {
  /** 泳道行 id（与 DhxGanttTask.id 对应；未分配资源用页面的未分配泳道 id） */
  laneId: string
  /** 桶起点 */
  start: string
  /** 桶终点（开区间） */
  end: string
  /** 桶内任务条数（负荷强度） */
  taskCount: number
  /** 桶内工时之和（小时） */
  loadHours?: number
}

export interface DhxGanttCompareTask {
  taskId: string | number
  laneId?: string
  taskCode?: string
  orderId?: string
  materialName?: string
  start?: string
  end?: string
  baselineLaneId?: string
  baselineStart?: string
  baselineEnd?: string
  status?: string
  isCritical?: boolean
  driftHours?: number
  changedFields?: string[]
}
export interface DhxGanttLink {
  id: string | number
  source: string | number
  target: string | number
  type: string | number
}

export interface DhxGanttPayload {
  data: DhxGanttTask[]
  links?: DhxGanttLink[]
}

export interface DhxGanttDragStartContext {
  taskId: string | number
  task: DhxGanttTask
  mode: string
}

export interface DhxGanttDragPayload {
  taskId: string | number
  task: DhxGanttTask
  sourceTask: DhxGanttTask
  mode: string
  sourceLaneId?: string | number
  targetLaneId?: string | number
  targetStart: string
  targetEnd: string
}

export type DhxGanttDropValidationPayload = DhxGanttDragPayload

export interface DhxGanttDragRejectedPayload {
  taskId: string | number
  task: DhxGanttTask
  reason: string
}
