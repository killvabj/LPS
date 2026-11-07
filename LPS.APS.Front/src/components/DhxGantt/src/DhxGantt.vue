<script setup lang="ts">
import '@vendor/dhtmlx-gantt/codebase/dhtmlxgantt.css'
import gantt from '@vendor/dhtmlx-gantt/codebase/dhtmlxgantt.es.js'
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type {
  DhxGanttCompareTask,
  DhxGanttDragPayload,
  DhxGanttDragRejectedPayload,
  DhxGanttDragStartContext,
  DhxGanttDropValidationPayload,
  DhxGanttLoadBand,
  DhxGanttPayload,
  DhxGanttScaleUnit,
  DhxGanttTask
} from './types'

const ganttApi = gantt as any

const props = withDefaults(
  defineProps<{
    tasks: DhxGanttTask[]
    links?: DhxGanttPayload['links']
    compareTasks?: DhxGanttCompareTask[]
    showCompare?: boolean
    showActual?: boolean
    highlightTaskIds?: Array<string | number>
    highlightLaneIds?: Array<string | number>
    readonly?: boolean
    allowResize?: boolean
    allowProgress?: boolean
    rowHeight?: number
    scaleUnit?: DhxGanttScaleUnit
    dateFrom?: string
    dateTo?: string
    /** 资源×时间桶负荷带（页面层算好，组件只负责画到底层泳道条上） */
    loadBands?: DhxGanttLoadBand[]
    /** 「数量」列的单位：任务单位一致时由页面传入（表头写「数量(PS)」，格子只留数字）；空串 = 表头只写「数量」 */
    qtyUnit?: string
    beforeTaskDrag?: (context: DhxGanttDragStartContext) => boolean | string
    validateTaskDrop?: (payload: DhxGanttDropValidationPayload) => boolean | string
  }>(),
  {
    links: () => [],
    compareTasks: () => [],
    showCompare: true,
    showActual: true,
    highlightTaskIds: () => [],
    highlightLaneIds: () => [],
    readonly: false,
    allowResize: true,
    allowProgress: true,
    rowHeight: 46,
    scaleUnit: 'day',
    dateFrom: undefined,
    dateTo: undefined,
    loadBands: () => [],
    qtyUnit: '',
    beforeTaskDrag: undefined,
    validateTaskDrop: undefined
  }
)
const emits = defineEmits<{
  taskClick: [taskId: string | number]
  taskDblClick: [taskId: string | number, e?: MouseEvent]
  taskDragEnd: [payload: DhxGanttDragPayload]
  taskDragRejected: [payload: DhxGanttDragRejectedPayload]
  taskContextMenu: [taskId: string | number, event: MouseEvent]
  /** 纵向滚动接近底部（供无限加载触发；节流/去重由调用方负责） */
  nearBottom: []
  /** 时间轴刻度层级变化（小时/天/周/月），供页面显示当前缩放档位 */
  zoomChange: [levelName: string]
}>()

/** 当前缩放层级下标（0 小时 … 3 月） */
const zoomLevelIndex = ref(1)

const rootRef = ref<HTMLDivElement | null>(null)
let initialized = false
let eventIds: string[] = []
let compareLayerId: string | number | null = null
let actualLayerId: string | number | null = null
let laneOverlayLayerId: string | number | null = null
let progressLayerId: string | number | null = null
let loadBandLayerId: string | number | null = null
let outerLabelLayerId: string | number | null = null
let todayMarkerId: string | number | null = null
const dragSnapshots = new Map<string | number, DhxGanttTask>()
const formatDate = gantt.date.date_to_str('%Y-%m-%d %H:%i')
const pad2 = (n: number) => String(n).padStart(2, '0')
const WEEKDAY_CN = ['日', '一', '二', '三', '四', '五', '六'] as const
const isWeekendDate = (date: Date) => date.getDay() === 0 || date.getDay() === 6
// 时间轴日刻度：10-27周二；月视图的日刻度只保留「27周二」
const dayScaleLabel = (date: Date) =>
  `${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}周${WEEKDAY_CN[date.getDay()]}`
const dayOfMonthScaleLabel = (date: Date) => `${date.getDate()}周${WEEKDAY_CN[date.getDay()]}`

/**
 * 缩放层级（知名排产系统的通用能力：时间轴能放大到小时、缩小到月）。
 *
 * ⚠️ dhtmlx `ext.zoom` 的 `_setLevel` 会把层级的 `scales` / `min_column_width`
 *   **直接 mixin 进 `gantt.config`**（`mixin(target, source, true)` = 覆盖语义）
 *   ⇒ **刻度配置的唯一所有者是缩放层级**：页面若再写 `gantt.config.scales`，会被层级覆盖。
 *   所以本组件里页面只管「窗口」(start_date/end_date)，刻度一律由这里下发。
 *
 * 层级下标越小 = 越细（0 小时 … 3 月），与 `ext.zoom.zoomIn()`（index-1）方向一致。
 */
const ZOOM_LEVELS = [
  {
    name: '小时',
    scales: [
      { unit: 'day', step: 1, format: dayScaleLabel },
      { unit: 'hour', step: 1, format: '%H:%i' }
    ],
    // 「00:00」实测文字宽 36~40px ⇒ 列宽低于 40 必然挤压（2026-11-05 实测 28px 挤成一团）。
    // 44 是窄屏兜底：视口够宽时列会被拉得更宽（1 天 24 列 ⇒ 约 56px），不触发滚动。
    min_column_width: 44
  },
  {
    name: '天',
    scales: [{ unit: 'day', step: 1, format: dayScaleLabel }],
    min_column_width: 70
  },
  {
    name: '周',
    scales: [
      { unit: 'week', step: 1, format: '第%W周' },
      { unit: 'day', step: 1, format: dayScaleLabel }
    ],
    min_column_width: 70
  },
  {
    name: '月',
    scales: [
      { unit: 'month', step: 1, format: '%Y-%m' },
      { unit: 'day', step: 1, format: dayOfMonthScaleLabel }
    ],
    min_column_width: 70
  }
] as const

const ZOOM_INDEX_BY_UNIT: Record<DhxGanttScaleUnit, number> = {
  hour: 0,
  day: 1,
  week: 2,
  month: 3
}

/** 左表单元格文本转义（单元格内容按 HTML 插入，防注入） */
const escapeHtml = (value: unknown): string =>
  String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')

/**
 * 条宽 vs 标签宽：撑得下写条内，撑不下写在条右侧（外挂层）。
 * 用 canvas 按实际字体测量（缓存文本宽度），避免按字数估算导致忽长忽短。
 */
const LABEL_MEASURE_FONT =
  '11px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif'
const BAR_LABEL_PADDING_X = 12
/** 任务号与状态标记之间的水平间距（与 `.dhx-bar-mark { margin-left: 6px }` 同值） */
const BAR_MARK_GAP = 6
/** 与 CSS `:deep(.gantt_task_line) { min-width: 14px }` 同值：DOM 实际条宽 = max(位置宽, 14) */
const BAR_MIN_WIDTH = 14
const OUTER_LABEL_GAP = 6
const barRenderWidth = (pos: any) => Math.max(Number(pos?.width) || 0, BAR_MIN_WIDTH)
let labelMeasureCtx: CanvasRenderingContext2D | null = null
const labelWidthCache = new Map<string, number>()
const measureLabelWidth = (text: string): number => {
  const cached = labelWidthCache.get(text)
  if (cached !== undefined) return cached
  if (labelMeasureCtx === null) {
    labelMeasureCtx = document.createElement('canvas').getContext('2d')
    if (labelMeasureCtx) labelMeasureCtx.font = LABEL_MEASURE_FONT
  }
  const width = labelMeasureCtx ? labelMeasureCtx.measureText(text).width : text.length * 7
  labelWidthCache.set(text, width)
  return width
}
const barLabelFits = (task: any): boolean => {
  if (!task?.barLabel) return false
  const pos = ganttApi.getTaskPosition(task, task.start_date, task.end_date)
  // 2026-11-06：必须把状态标记（+Nh / ✓ / 延期 M）一起算进去。
  // 只量任务号时，44px 的条判定「放得下 S102」，但再加 +392h 就装不下 ⇒
  // flex 把任务号压成 0 宽，条上只剩被裁掉一半的红字（用户实测截图）。
  let need = measureLabelWidth(String(task.barLabel))
  if (task.barMarkText) need += measureLabelWidth(String(task.barMarkText)) + BAR_MARK_GAP
  return barRenderWidth(pos) >= need + BAR_LABEL_PADDING_X
}

const cloneTask = (task: any): DhxGanttTask => JSON.parse(JSON.stringify(task)) as DhxGanttTask
const compareTaskMap = () => new Map(props.compareTasks.map((item) => [String(item.taskId), item]))
const taskMap = () => new Map(props.tasks.map((item) => [String(item.id), item]))
const highlightTaskIdSet = () => new Set(props.highlightTaskIds.map((item) => String(item)))
const highlightLaneIdSet = () => new Set(props.highlightLaneIds.map((item) => String(item)))

const forceClearDragState = () => {
  const timeline = ganttApi.$ui?.getView?.('timeline') as any
  const tasksDnd = timeline?._tasks_dnd as any
  if (tasksDnd?.clear_drag_state) tasksDnd.clear_drag_state()
  if (ganttApi.unselectTask) ganttApi.unselectTask()
  ganttApi.render?.()
}
const parseShadowDate = (value?: string) => {
  if (!value) return null
  const parsed = new Date(value)
  if (!Number.isNaN(parsed.getTime())) return parsed
  try {
    return ganttApi.date.parseDate(value, ganttApi.config.xml_date)
  } catch {
    return null
  }
}

const buildDragPayload = (id: string | number, mode: string): DhxGanttDragPayload => {
  const task = cloneTask(gantt.getTask(id))
  const sourceTask = dragSnapshots.get(id) || task
  const taskStartDate = parseShadowDate(String(task.start_date)) || new Date(task.start_date)
  const taskEndDate = gantt.calculateEndDate(taskStartDate as any, task.duration)
  return {
    taskId: id,
    task,
    sourceTask,
    mode,
    sourceLaneId: sourceTask.parent,
    targetLaneId: task.parent,
    targetStart: formatDate(taskStartDate),
    targetEnd: formatDate(taskEndDate)
  }
}

/**
 * 时间轴**窗口**（起止）。
 * 刻度不在这里设 —— 刻度由缩放层级独占（见 ZOOM_LEVELS 注释），
 * 这里若再写 `gantt.config.scales` 会被层级覆盖，等于白写。
 */
const applyWindowConfig = (scaleUnit: DhxGanttScaleUnit) => {
  // 设置日期范围：优先使用外部传入的值，否则根据 scaleUnit 设置默认值
  if (props.dateFrom && props.dateTo) {
    gantt.config.start_date = new Date(props.dateFrom)
    gantt.config.end_date = new Date(props.dateTo)
    return
  }
  // 默认时间范围：今天开始
  const now = new Date()
  now.setHours(0, 0, 0, 0)
  let rangeEnd = new Date(now)
  rangeEnd.setDate(rangeEnd.getDate() + 7) // 默认7天

  if (scaleUnit === 'hour') {
    rangeEnd = new Date(now)
    rangeEnd.setDate(rangeEnd.getDate() + 1) // 今日：1天
  } else if (scaleUnit === 'day') {
    rangeEnd = new Date(now)
    rangeEnd.setDate(rangeEnd.getDate() + 7) // 7天
  } else if (scaleUnit === 'week') {
    rangeEnd = new Date(now)
    rangeEnd.setDate(rangeEnd.getDate() + 30) // 30天
  } else if (scaleUnit === 'month') {
    rangeEnd = new Date(now)
    rangeEnd.setMonth(rangeEnd.getMonth() + 3) // 3个月
  }

  gantt.config.start_date = now
  gantt.config.end_date = rangeEnd
}

/** 缩放层级是否已就绪（ext.zoom 必须在 gantt.init 之后才能 init） */
let zoomReady = false

const syncZoomState = () => {
  const zoom = ganttApi.ext?.zoom
  const idx =
    zoom && typeof zoom.getCurrentLevel === 'function'
      ? zoom.getCurrentLevel()
      : zoomLevelIndex.value
  zoomLevelIndex.value = idx
  emits('zoomChange', ZOOM_LEVELS[idx]?.name ?? '天')
}

/**
 * 当前视口里这些行的最早开始时刻。
 *
 * 虚拟渲染只渲染「纵向可见的行」⇒ DOM 里的 .gantt_row 就是用户正在看的那些行，
 * 拿它们的开始时刻当锚点，换刻度后用户看到的仍是同一批任务的条子。
 */
const earliestVisibleRowStart = (): Date | null => {
  const root = rootRef.value
  if (!root) return null
  let min: Date | null = null
  for (const row of Array.from(root.querySelectorAll('.gantt_row'))) {
    const id = row.getAttribute('task_id')
    if (!id) continue
    const task = ganttApi.getTask?.(id)
    if (!task) continue
    const start = parseShadowDate(task.start_date)
    if (start && (!min || start < min)) min = start
  }
  return min
}

/** 全部任务里最早的开始时刻（视口里一行都没有时的兜底） */
const earliestTaskStart = (): Date | null => {
  let min: Date | null = null
  // ⚠️ 本 dhtmlx 9.1.3 打包件**没有 getTasks()**，只有 eachTask(cb)
  ganttApi.eachTask?.((task: any) => {
    const start = parseShadowDate(task.start_date)
    if (start && (!min || start < min)) min = start
  })
  return min
}

/**
 * 对外暴露：用户**正在看**的那批任务的最早开始时刻。
 * 页面层用它把「收窄后的窗口」锚在用户当前视野上（而不是跳回窗口左端）。
 */
const visibleRowStart = (): Date | null => earliestVisibleRowStart() ?? earliestTaskStart()

/**
 * 换刻度后把视口锚回「用户正在看的那几行」。
 *
 * dhtmlx `ext.zoom` 的 setLevel 会把视口**重新居中到「切换前的可视日期」**；
 * 本页窗口固定 [今天, 今天+N]，切换前的可视中心多落在窗口中段，
 * 而纵向可见的那些行的条子都在最近一两天 ⇒ 切到「小时」档后视口停在任务区间之外，
 * 虚拟渲染把任务条整段裁掉 ⇒ 时间轴整屏空白（领导看到的「这甘特图用不了」）。
 *
 * 规则：换刻度后把时间轴左端对齐到「当前可见行的最早开始时刻」——
 *       用户看的是哪几行，换刻度后还是哪几行，条子必然在屏幕里。
 */
const clampViewportToTaskRange = () => {
  const target = earliestVisibleRowStart() ?? earliestTaskStart()
  if (!target) return
  ganttApi.showDate?.(target)
}

/** 换刻度档位后的统一收尾：同步页面显示的档位 + 把视口拉回有数据的区间 */
const afterZoomLevelChanged = () => {
  syncZoomState()
  clampViewportToTaskRange()
}

const setZoomLevel = (index: number) => {
  const zoom = ganttApi.ext?.zoom
  if (!zoomReady || !zoom || typeof zoom.setLevel !== 'function') return
  const idx = Math.max(0, Math.min(ZOOM_LEVELS.length - 1, index))
  zoom.setLevel(idx)
  afterZoomLevelChanged()
}

/** 按档位名设置刻度（小时 / 天 / 周 / 月）；dhtmlx 的 setLevel 支持按 name 匹配层级 */
const setZoomLevelByName = (name: string) => {
  const zoom = ganttApi.ext?.zoom
  if (!zoomReady || !zoom || typeof zoom.setLevel !== 'function') return
  zoom.setLevel(name)
  afterZoomLevelChanged()
}

/** 启用缩放扩展：层级自带 scales + min_column_width，init 末尾会 setLevel 生效 */
const initZoom = () => {
  const zoom = ganttApi.ext?.zoom
  if (!zoom || typeof zoom.init !== 'function') return
  zoom.init({
    levels: ZOOM_LEVELS as unknown as any[],
    activeLevelIndex: ZOOM_INDEX_BY_UNIT[props.scaleUnit] ?? 1,
    minColumnWidth: 28,
    maxColumnWidth: 240,
    // 不劫持滚轮：滚轮留给时间轴横向滚动（知名系统里滚轮缩放是常见做法，
    // 但本组件的横向滚动也用滚轮，两者冲突时优先保证能看全时间轴）
    trigger: null
  })
  zoomReady = true
  syncZoomState()
}

/**
 * 窗口 + 刻度层级。
 * ⚠️ 层级**相同就不重设**：`ext.zoom.setLevel` 会重绘并把可视日期重新居中，
 *   每次翻页/追加数据都触发会把用户的横向滚动位置冲掉。
 */
const applyScaleConfig = (scaleUnit: DhxGanttScaleUnit) => {
  applyWindowConfig(scaleUnit)
  const target = ZOOM_INDEX_BY_UNIT[scaleUnit] ?? 1
  const zoom = ganttApi.ext?.zoom
  const current = zoom && typeof zoom.getCurrentLevel === 'function' ? zoom.getCurrentLevel() : -1
  if (zoomReady && current !== target) setZoomLevel(target)
}

/**
 * 悬浮提示只挂在**右侧时间轴的任务条**上（左侧行名列不弹）。
 *
 * Why：tooltip 插件默认的选择器是 `[task_id]:not(.gantt_task_row)`，
 *   它同时命中左侧网格行（`.gantt_row`，也带 task_id）⇒ 鼠标停在左边行名列上
 *   也会弹出与右侧条子一模一样的黑框（内容重复、遮挡行名）。
 *
 * How：onGanttReady 时摘掉默认选择器，改挂 `.gantt_task_line`（只在右侧时间轴）。
 *   必须在插件的 onGanttReady 之后注册本函数 ⇒ 事件监听按注册顺序触发，本函数最后跑。
 */
const applyTooltipScope = () => {
  const tooltips = ganttApi.ext?.tooltips
  if (!tooltips) return
  const attr = ganttApi.config?.task_attribute || 'task_id'
  tooltips.detach(`[${attr}]:not(.gantt_task_row)`)
  tooltips.tooltipFor({
    selector: '.gantt_task_line',
    html: (e: MouseEvent) => {
      const id = ganttApi.locate(e)
      if (!ganttApi.isTaskExists(id)) return null
      const task = ganttApi.getTask(id)
      return ganttApi.templates.tooltip_text(task.start_date, task.end_date, task)
    }
  })
}

const applyConfig = () => {
  gantt.config.readonly = props.readonly
  gantt.config.row_height = props.rowHeight
  gantt.config.xml_date = '%Y-%m-%d %H:%i'
  gantt.config.date_format = '%Y-%m-%d %H:%i'
  // 任务 duration 统一按「小时」传入（页面 hoursBetween 返回小时），
  // vendor 默认单位是 day，会把 4 小时画成 4 天（条宽放大 24 倍）
  gantt.config.duration_unit = 'hour'
  gantt.config.duration_step = 1
  gantt.config.drag_resize = !props.readonly && props.allowResize
  gantt.config.drag_move = !props.readonly
  gantt.config.drag_progress = !props.readonly && props.allowProgress
  // 禁用「建依赖」拖拽点：本组件 links 恒为空（后端不下发任务间依赖），
  // 留着这两个圆点只能拖出只在内存里的假连线，刷新即消失
  gantt.config.drag_links = false
  gantt.config.open_tree_initially = true
  gantt.config.grid_width = 690
  gantt.config.bar_height = 32
  gantt.config.grid_resize = true
  gantt.config.show_progress = false // 禁用内置进度条，使用自定义进度显示
  // 禁用原生右键菜单，使用自定义菜单
  gantt.config.context_menu = false
  // 启用虚拟渲染，提升大量数据时的渲染性能
  gantt.config.virtual_render = true
  gantt.config.lazy_parsing = true
  // 防止自动滚动
  gantt.config.scroll_to_task = false
  gantt.config.auto_scheduling = false
  // 禁用 showEvent 防止滚动
  if ((gantt as any).showEvent) {
    ;(gantt as any).showEvent = function () {}
  }
  // 列宽合计 = grid_width（690）：工序·指示号/物料 340 + 开始 130 + 结束 130 + 数量 90
  // 2026-11-06 工厂口径：撤掉「时长」列（泳道行 75h 是占用跨度、子任务 1/2h 是加工工时，
  //   同列不同义易被读成算错）⇒ 改为「开始 / 结束 / 数量」，工时看悬停
  // 物料并入首列（「物料」独立列在窄屏下只剩半截编码，且拉低左表信息密度）
  // 放不下时用原生 title 悬停显示完整文本（与「任务清单」页签的溢出提示同一套交互）
  // dhtmlx 官方 d.ts 的 GanttColumn 未声明 template，运行时支持 ⇒ 走 any 通道
  const titleTemplate = (field: string) => (task: any) =>
    `<span title="${escapeHtml(task?.[field] ?? '')}">${escapeHtml(task?.[field] ?? '')}</span>`
  gantt.config.columns = [
    {
      name: 'text',
      label: '工序 / 指示号 / 物料',
      tree: true,
      width: 340,
      template: titleTemplate('text')
    } as any,
    {
      // 用页面拼好的 startText 而不是 start_date：空泳道（该窗口内没任务）要显示空白，
      // 不能让 dhtmlx 把兜底日期画成「开始时间」
      name: 'startText',
      label: '开始',
      align: 'center',
      width: 130,
      template: titleTemplate('startText')
    } as any,
    {
      name: 'endText',
      label: '结束',
      align: 'center',
      width: 130,
      template: titleTemplate('endText')
    } as any,
    {
      name: 'qtyText',
      // 单位一致时提到表头「数量(PS)」，格子里只剩数字；单位混合时页面传空串，保持「数量」+ 行内单位
      label: props.qtyUnit ? `数量(${props.qtyUnit})` : '数量',
      align: 'left',
      width: 90,
      template: titleTemplate('qtyText')
    } as any
  ]

  applyScaleConfig(props.scaleUnit)

  // 左表「开始」列显示到时分（条子按小时摆放，只给日期看不出几点开工）
  gantt.templates.date_grid = (date: Date) =>
    `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${pad2(date.getHours())}:${pad2(date.getMinutes())}`
  // 周末底纹：表头刻度 + 时间轴背景格
  gantt.templates.scale_cell_class = (date: Date) =>
    isWeekendDate(date) ? 'dhx-scale-weekend' : ''
  gantt.templates.timeline_cell_class = (_task: any, date: Date) =>
    isWeekendDate(date) ? 'dhx-timeline-weekend' : ''

  gantt.templates.task_class = (_, __, task) => {
    const classes: string[] = []
    if (task.type === 'project') {
      classes.push('dhx-task-lane')
      // 2026-11-06 A4：浅色泳道（资源/订单/物料，深色字）与彩色泳道（域/阶段/工厂/部门，白字）
      // 分开着色 —— 此前统一刷浅绿，整屏 20 条泳道一片绿，看不出区分度。
      classes.push(task.textColor === '#ffffff' ? 'dhx-lane-strong' : 'dhx-lane-light')
    }
    // 泳道在该时间窗内没有任务时不画条（避免 1 小时兜底假条）
    if (task.type === 'project' && task.laneEmpty) classes.push('dhx-lane-empty')
    if (task.readonly) classes.push('dhx-task-readonly')
    if (highlightTaskIdSet().has(String(task.id))) classes.push('dhx-task-impacted')
    if (task.type === 'project' && highlightLaneIdSet().has(String(task.id)))
      classes.push('dhx-lane-impacted')
    if (task.isCritical) classes.push('dhx-task-critical')
    if (task.warningCount) classes.push('dhx-task-warning')
    // APS 状态枚举为大写（COMPLETED），样式选择器为小写，需统一后再拼类名
    if (task.status) classes.push(`dhx-status-${String(task.status).toLowerCase()}`)
    if (task.zone) classes.push(`dhx-zone-${task.zone}`)
    if (task.reasonCode === 'LOGISTICS_DELAY') classes.push('dhx-reason-logistics-delay')
    if (task.planFreezeFlag) classes.push('dhx-task-frozen')
    if (task.executionLockFlag) classes.push('dhx-task-execution-locked')
    if (task.isLocked) classes.push('dhx-task-manual-locked')
    if (task.laneStatus === 'maintenance') classes.push('dhx-task-on-maintenance-lane')
    return classes.join(' ')
  }

  /**
   * 条内标签：「工序 | 订单号」撑得下写在条上（省略号兜底），撑不下整条不写字、
   * 交给右侧外挂标签层（见 renderOuterLabel），避免半截文字。
   * 泳道摘要条（2026-11-06）：写「N 条」，有延期时右侧补红字「延期 M」。
   */
  gantt.templates.task_text = (_start: any, _end: any, task: any) => {
    if (!task || !task.barLabel) return ''
    if (!barLabelFits(task)) return ''
    if (task.type === 'project') {
      const count = `<span class="dhx-lane-count">${escapeHtml(task.barLabel)}</span>`
      const delayMark = task.barMarkText
        ? `<span class="dhx-lane-mark is-delay">${escapeHtml(task.barMarkText)}</span>`
        : ''
      return `<span class="dhx-lane-inner">${count}${delayMark}</span>`
    }
    const label = `<span class="dhx-bar-label">${escapeHtml(task.barLabel)}</span>`
    const markClass = task.barMark === 'done' ? 'is-done' : 'is-delay'
    const mark = task.barMarkText
      ? `<span class="dhx-bar-mark ${markClass}">${escapeHtml(task.barMarkText)}</span>`
      : ''
    return `<span class="dhx-bar-inner">${label}${mark}</span>`
  }

  /**
   * 悬浮提示：页面层已按业务口径拼好行（任务号 / 订单 / 资源 / 工时 / 状态 / 延期…），
   * 组件只负责渲染，不自己拼业务语义（术语口径统一在页面层）。
   */
  gantt.templates.tooltip_text = (_start: any, _end: any, task: any) => {
    const lines: string[] = []
    if (Array.isArray(task?.tooltipLines) && task.tooltipLines.length) {
      lines.push(...task.tooltipLines)
    } else {
      lines.push(`任务：${task?.text || '-'}`)
    }
    const note = task?.changeReason || task?.details
    if (note) lines.push(`说明：${note}`)
    return lines.map((line) => escapeHtml(line)).join('<br/>')
  }
}

const renderCompareShadow = (task: DhxGanttTask) => {
  if (!props.showCompare || task.type === 'project') return null
  const compare = compareTaskMap().get(String(task.id))
  if (!compare) return null

  const anchorTask = compare.baselineLaneId
    ? taskMap().get(String(compare.baselineLaneId))
    : undefined
  const start = parseShadowDate(compare.start || compare.baselineStart)
  const end = parseShadowDate(compare.end || compare.baselineEnd)
  if (!start || !end) return null

  const position = ganttApi.getTaskPosition((anchorTask || task) as any, start, end)
  if (!position || position.width <= 0) return null

  const node = document.createElement('button')
  node.type = 'button'
  node.className = `dhx-compare-shadow${compare.isCritical ? ' is-critical' : ''}${compare.changedFields?.length ? ' is-changed' : ''}`
  node.style.left = `${position.left}px`
  node.style.top = `${position.top + Math.max(7, Math.round((position.rowHeight - 12) / 2))}px`
  node.style.width = `${Math.max(position.width, 10)}px`
  node.style.height = '12px'
  const currentStart = task.start_date || '-'
  const currentEnd = formatDate(gantt.calculateEndDate(task.start_date, task.duration))
  const baselineStart = compare.start || compare.baselineStart || '-'
  const baselineEnd = compare.end || compare.baselineEnd || '-'
  const laneChanged = Boolean(
    compare.baselineLaneId && task.laneId && compare.baselineLaneId !== task.laneId
  )
  node.title = [
    `任务：${compare.taskCode || compare.orderId || compare.taskId}`,
    `基线开始：${baselineStart}`,
    `基线结束：${baselineEnd}`,
    `当前开始：${currentStart}`,
    `当前结束：${currentEnd}`,
    `漂移小时：${compare.driftHours ?? 0}h`,
    `是否换设备：${laneChanged ? '是' : '否'}`,
    `变更字段：${compare.changedFields?.join(' / ') || '-'}`
  ].join('\n')
  const drift = compare.driftHours ?? 0
  const driftLabel = drift > 0 ? `+${drift}h` : drift < 0 ? `${drift}h` : '基线'
  if (Math.max(position.width, 10) > 72) {
    node.innerHTML = `<span>${driftLabel}</span>`
  }
  node.onclick = (event) => {
    event.preventDefault()
    event.stopPropagation()
    emits('taskClick', compare.taskId)
  }
  return node
}

const renderActualProgress = (task: DhxGanttTask) => {
  if (!props.showActual || task.type === 'project' || !task.actualStart) return null
  const actualStart = parseShadowDate(task.actualStart)
  // 对于running状态的任务，如果没有actualEnd，使用任务结束时间或当前时间（取较小值）
  let actualEnd = task.actualEnd ? parseShadowDate(task.actualEnd) : null
  if (!actualEnd) {
    // 使用任务结束时间和当前时间中的较小值，避免进度条超出任务范围
    const taskEnd = gantt.calculateEndDate(task.start_date as any, task.duration)
    const now = new Date()
    actualEnd = taskEnd && taskEnd < now ? taskEnd : now
  }
  if (!actualStart || !actualEnd || actualEnd <= actualStart) return null
  const position = ganttApi.getTaskPosition(task as any, actualStart, actualEnd)
  if (!position || position.width <= 0) return null

  const node = document.createElement('div')
  const isFinished = Boolean(task.actualEnd)
  node.className = `dhx-actual-progress${isFinished ? ' is-finished' : ' is-running'}`
  node.style.left = `${position.left}px`
  node.style.top = `${position.top + position.rowHeight - 9}px`
  node.style.width = `${Math.max(position.width, 6)}px`
  node.title = isFinished
    ? `实际完成：${task.actualStart} ~ ${task.actualEnd}`
    : `实际进行中：${task.actualStart} ~ 当前时刻`

  if (!isFinished) {
    const dot = document.createElement('span')
    dot.className = 'dhx-actual-progress-dot'
    node.appendChild(dot)

    if (position.width > 64) {
      const badge = document.createElement('span')
      badge.className = 'dhx-actual-progress-badge'
      badge.textContent = '进行中'
      node.appendChild(badge)
    }
  }

  return node
}

// 渲染计划进度条（替代内置进度条，更清晰显示）
const renderProgressBar = (task: DhxGanttTask) => {
  if (task.type === 'project' || !task.progress || task.progress <= 0) return null

  const taskRow = ganttApi.getTaskPosition(
    task as any,
    task.start_date,
    gantt.calculateEndDate(task.start_date, task.duration)
  )
  if (!taskRow) return null

  const progressWidth = Math.max(taskRow.width * task.progress, 8) // 最小宽度8px
  const isFinished = task.progress >= 1

  const node = document.createElement('div')
  node.className = `dhx-plan-progress${isFinished ? ' is-finished' : ''}`
  node.style.left = `${taskRow.left}px`
  node.style.top = `${taskRow.top + 6}px` // 进度条在任务条上方偏移
  node.style.width = `${progressWidth}px`
  node.style.height = '6px'
  node.title = `计划进度：${Math.round(task.progress * 100)}%`

  return node
}

const renderLaneOverlay = (task: DhxGanttTask) => {
  if (task.type !== 'project') return null
  const wrappers: HTMLElement[] = []
  const row = ganttApi.getTaskPosition(
    task as any,
    task.start_date,
    gantt.calculateEndDate(task.start_date, task.duration)
  )
  if (!row) return null

  if (highlightLaneIdSet().has(String(task.id))) {
    const impact = document.createElement('div')
    impact.className = 'dhx-lane-overlay is-impact'
    impact.style.left = '0px'
    impact.style.top = `${row.top + 2}px`
    impact.style.width = '100%'
    impact.style.height = `${Math.max(row.rowHeight - 4, 20)}px`
    impact.title = `${task.text} 为本次拖拽预演影响范围`
    wrappers.push(impact)
  }

  if (task.laneStatus === 'maintenance') {
    const maintenance = document.createElement('div')
    maintenance.className = 'dhx-lane-overlay is-maintenance'
    maintenance.style.left = '0px'
    maintenance.style.top = `${row.top + 2}px`
    maintenance.style.width = '100%'
    maintenance.style.height = `${Math.max(row.rowHeight - 4, 20)}px`
    maintenance.title = `${task.text} 处于维护窗口`
    wrappers.push(maintenance)
  }

  if (task.freezeBoundary) {
    const boundaryDate = parseShadowDate(task.freezeBoundary)
    if (boundaryDate) {
      const position = ganttApi.getTaskPosition(task as any, boundaryDate, boundaryDate)
      const boundary = document.createElement('div')
      boundary.className = 'dhx-freeze-boundary'
      boundary.style.left = `${Math.max(position.left, 0)}px`
      boundary.style.top = `${row.top}px`
      boundary.style.height = `${row.rowHeight}px`
      boundary.title = `冻结边界：${task.freezeBoundary}`
      wrappers.push(boundary)
    }
  }

  if (!wrappers.length) return null
  const container = document.createElement('div')
  wrappers.forEach((item) => container.appendChild(item))
  return container
}
/**
 * 负荷带：资源×时间桶的负荷强度，画在泳道行底部。
 *
 * ⚠️ 桶起点可能**早于窗口 from**（周桶锚=周一，5号位 2026-10-30 复函 Q1-b 已书面确认
 *   「前端不裁首桶是对的」）⇒ 这里直接按桶起点画，不按窗口裁剪。
 * ⚠️ 强度口径 = 本桶任务数 / **全部泳道**内最大桶任务数 ⇒ 深浅跨泳道可比
 *   （早期版本按行内归一，结果每条泳道看着都"很满"，横向比较是假的；
 *     与「资源负荷」页签的热力图口径保持一致）。
 */
let loadBandCache: {
  key: DhxGanttLoadBand[]
  map: Map<string, DhxGanttLoadBand[]>
  maxCount: number
} | null = null
const loadBandMap = (): {
  map: Map<string, DhxGanttLoadBand[]>
  maxCount: number
} => {
  const bands = props.loadBands ?? []
  if (loadBandCache && loadBandCache.key === bands) return loadBandCache
  const map = new Map<string, DhxGanttLoadBand[]>()
  let maxCount = 0
  for (const band of bands) {
    if (band.taskCount > maxCount) maxCount = band.taskCount
    const list = map.get(band.laneId)
    if (list) list.push(band)
    else map.set(band.laneId, [band])
  }
  loadBandCache = { key: bands, map, maxCount }
  return loadBandCache
}

const renderLoadBand = (task: DhxGanttTask) => {
  if (task.type !== 'project') return null
  const { map, maxCount } = loadBandMap()
  const bands = map.get(String(task.id))
  if (!bands || !bands.length) return null
  const row = ganttApi.getTaskPosition(
    task as any,
    task.start_date,
    gantt.calculateEndDate(task.start_date, task.duration)
  )
  if (!row) return null
  if (maxCount <= 0) return null
  const container = document.createElement('div')
  container.className = 'dhx-load-band'
  for (const band of bands) {
    const start = parseShadowDate(band.start)
    const end = parseShadowDate(band.end)
    if (!start || !end || end <= start) continue
    const pos = ganttApi.getTaskPosition(task as any, start, end)
    if (!pos || pos.width <= 0) continue
    const cell = document.createElement('div')
    cell.className = 'dhx-load-band-cell'
    cell.style.left = `${pos.left}px`
    // 贴着泳道行底边：条高 9px，比原来 7px 更容易看清深浅差
    cell.style.top = `${row.top + Math.max(row.rowHeight - 9, 0)}px`
    cell.style.width = `${pos.width}px`
    cell.style.height = '9px'
    cell.style.opacity = String(0.25 + (band.taskCount / maxCount) * 0.75)
    cell.title = [
      `桶：${band.start.slice(0, 10)} ~ ${band.end.slice(0, 10)}（右端不含）`,
      `任务 ${band.taskCount} 条 · 工时 ${band.loadHours != null ? band.loadHours.toFixed(2) : '-'} h`
    ].join('\n')
    container.appendChild(cell)
  }
  return container.children.length ? container : null
}

/**
 * 条外标签：只有条宽撑不下「工序 | 订单号」时才画，紧贴条的右缘。
 * 返回 DOM 元素交给 dhtmlx 的任务图层，滚动/缩放时随条自动重排。
 * 泳道摘要条同样适用（窄泳道条「N 条」+ 红字「延期 M」写条外）。
 */
const renderOuterLabel = (task: any) => {
  if (!task) return null
  if (!task.barLabel) return null
  if (barLabelFits(task)) return null
  const pos = ganttApi.getTaskPosition(task, task.start_date, task.end_date)
  if (!pos) return null
  const el = document.createElement('div')
  el.className = task.type === 'project' ? 'dhx-bar-outer-label is-lane' : 'dhx-bar-outer-label'
  // 2026-11-06：窄条条内写不下时，延期小时（+Nh）跟在标签后面用红字补上，
  // 否则窄条既没有条内文字、也没有任何延期提示。
  const labelText = String(task.barLabel)
  const delayText = task.barMark === 'delay' && task.barMarkText ? String(task.barMarkText) : ''
  const delayMark = delayText
    ? `<span class="dhx-outer-mark is-delay">${escapeHtml(delayText)}</span>`
    : ''
  el.innerHTML = `<span class="dhx-outer-label-text">${escapeHtml(labelText)}</span>${delayMark}`
  el.title = delayText ? `${labelText} ${delayText}` : labelText
  el.style.left = `${Math.round(pos.left + barRenderWidth(pos) + OUTER_LABEL_GAP)}px`
  // getTaskPosition().top 是行顶，条在行内垂直居中（dhtmlx: (rowHeight - barHeight) / 2）
  const barHeight = Number(pos.height) || 0
  const rowHeight = Number(pos.rowHeight) || gantt.config.row_height || barHeight
  el.style.top = `${Math.round(pos.top + (rowHeight - barHeight) / 2)}px`
  el.style.height = `${Math.round(barHeight)}px`
  el.style.maxWidth = '220px'
  return el
}

const syncLayers = () => {
  if (!initialized) return
  if (compareLayerId !== null) ganttApi.removeTaskLayer(compareLayerId as any)
  if (outerLabelLayerId !== null) ganttApi.removeTaskLayer(outerLabelLayerId as any)
  if (actualLayerId !== null) ganttApi.removeTaskLayer(actualLayerId as any)
  if (laneOverlayLayerId !== null) ganttApi.removeTaskLayer(laneOverlayLayerId as any)
  if (progressLayerId !== null) ganttApi.removeTaskLayer(progressLayerId as any)
  if (loadBandLayerId !== null) ganttApi.removeTaskLayer(loadBandLayerId as any)
  compareLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderCompareShadow(task)) as
    | string
    | number
  actualLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderActualProgress(task)) as
    | string
    | number
  laneOverlayLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderLaneOverlay(task)) as
    | string
    | number
  progressLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderProgressBar(task)) as
    | string
    | number
  loadBandLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderLoadBand(task)) as
    | string
    | number
  outerLabelLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderOuterLabel(task)) as
    | string
    | number
}

/**
 * 今天竖线。gantt.clearAll() 会把 marker 数据源一起清掉（marker 走 datastores 服务注册），
 * 所以每次 parseData 之后都要重建，不能只在 onMounted 建一次。
 */
const ensureTodayMarker = (): void => {
  if (typeof ganttApi.addMarker !== 'function') return
  todayMarkerId = null
  todayMarkerId = ganttApi.addMarker({
    start_date: new Date(),
    css: 'dhx-today-marker',
    text: '今天',
    title: '今天'
  })
}

/** 纵向滚动容器（dhtmlx 的垂直滚动条元素） */
const getVerticalScroller = (): HTMLElement | null =>
  (rootRef.value?.querySelector('.gantt_ver_scroll') as HTMLElement | null) ?? null

/** 上一次解析的任务条数：用于识别「追加一页」（只为这种场景保住纵向滚动位置） */
let lastParsedCount = 0

const parseData = async () => {
  if (!initialized || !rootRef.value) return
  const isAppend = props.tasks.length > lastParsedCount
  const prevScrollTop = getVerticalScroller()?.scrollTop ?? 0
  await nextTick()
  gantt.clearAll()
  dragSnapshots.clear()
  gantt.parse({ data: props.tasks, links: props.links || [] })
  // parse完成后，重新应用日期范围配置（parse可能会覆盖start_date/end_date）
  if (props.dateFrom && props.dateTo) {
    gantt.config.start_date = new Date(props.dateFrom)
    gantt.config.end_date = new Date(props.dateTo)
  }
  syncLayers()
  ensureTodayMarker()
  // 使用 ganttApi.render() 确保强制重绘
  ganttApi.render()
  lastParsedCount = props.tasks.length
  // 追加一页后整图重解析会把滚动条弹回顶部；恢复用户原先所在位置，否则无法连续下滑加载
  if (isAppend && prevScrollTop > 0) {
    const restore = () => {
      const el = getVerticalScroller()
      if (el) el.scrollTop = prevScrollTop
    }
    restore()
    requestAnimationFrame(restore)
  }
}

let nativeContextMenuHandler: ((e: MouseEvent) => void) | null = null
let nativeDblClickHandler: ((e: MouseEvent) => void) | null = null

const getTaskIdFromEvent = (e: MouseEvent): string | number | null => {
  const target = e.target as HTMLElement

  // 尝试找任务条元素 - DhtmlxGantt 的任务条有 task_id 属性
  const taskBar = target.closest('.gantt_task_line')
  if (taskBar) {
    const taskId = taskBar.getAttribute('task_id')
    if (taskId) return taskId
  }

  // 尝试找任务行
  const taskRow = target.closest('.gantt_row')
  if (taskRow) {
    const taskId = taskRow.getAttribute('task_id')
    if (taskId) return taskId
  }

  // 最后尝试通过 DhtmlxGantt API 获取
  try {
    const x = e.clientX
    const y = e.clientY
    // getTaskByPos is a DhtmlxGantt method to get task at position
    if (typeof ganttApi.getTaskByPos === 'function') {
      const result = ganttApi.getTaskByPos(x, y)
      if (result && result.id) return result.id
    }
  } catch (err) {
    // ignore
  }

  return null
}

/** 把时间轴滚动到指定日期（「回到今天」按钮用） */
const showDate = (date: Date | string): void => {
  const target = date instanceof Date ? date : new Date(date)
  if (Number.isNaN(target.getTime())) return
  ganttApi.showDate?.(target)
  ganttApi.render?.()
}

defineExpose({ showDate, setZoomLevelByName, visibleRowStart })

onMounted(async () => {
  if (!rootRef.value) return
  applyConfig()
  // addMarker/deleteMarker 由 marker 扩展提供，未 plugins 前这两个方法根本不存在；
  // tooltip 扩展负责悬浮提示（未 plugins 时悬停任务条没有任何内容）
  ganttApi.plugins({ marker: true, tooltip: true })
  // 插件的 onGanttReady 先注册 ⇒ 本函数后跑，才能摘掉它的默认监听范围
  ganttApi.attachEvent('onGanttReady', () => applyTooltipScope())
  gantt.init(rootRef.value)
  // 缩放扩展必须在 init 之后启用：它的 init 会 setLevel → 写 config.scales → 重绘
  initZoom()

  // 原生 contextmenu 事件监听，确保右键菜单能触发
  nativeContextMenuHandler = (e: MouseEvent) => {
    e.preventDefault()
    e.stopPropagation()

    // 保存滚动位置
    let currentScrollTop = 0
    let currentScrollLeft = 0

    if (rootRef.value) {
      const taskArea = rootRef.value.querySelector('.gantt_task_area') as HTMLElement
      if (taskArea) {
        currentScrollTop = taskArea.scrollTop
        currentScrollLeft = taskArea.scrollLeft || 0
      }
    }

    const taskId = getTaskIdFromEvent(e)
    if (taskId !== null) {
      emits('taskContextMenu', taskId, e)

      // 恢复滚动位置
      if (rootRef.value) {
        const taskArea = rootRef.value.querySelector('.gantt_task_area') as HTMLElement
        if (taskArea) {
          taskArea.scrollTop = currentScrollTop
          taskArea.scrollLeft = currentScrollLeft
        }
      }
    }
  }
  // P3 修复：原生 contextmenu / dblclick 监听（dhtmlx-gantt 在 readonly 模式下不 dispatch onTaskDblClick）
  // 使用 capture 阶段确保在 gantt 内部处理前拦截事件
  if (rootRef.value) {
    rootRef.value.addEventListener('contextmenu', nativeContextMenuHandler, true)
  }
  // 双击：只对 .gantt_task_line 子元素响应（避免空白/Header 误触）
  nativeDblClickHandler = (e: MouseEvent) => {
    const target = e.target as HTMLElement
    const taskBar = target.closest('.gantt_task_line')
    if (!taskBar) return
    const taskId = taskBar.getAttribute('task_id')
    if (!taskId) return
    emits('taskDblClick', taskId, e)
  }
  if (rootRef.value) {
    rootRef.value.addEventListener('dblclick', nativeDblClickHandler, true)
  }

  eventIds = [
    gantt.attachEvent('onTaskClick', (id) => {
      // 保存滚动位置
      let currentScrollTop = 0
      let currentScrollLeft = 0
      let scrollEl: HTMLElement | null = null

      if (rootRef.value) {
        const selectors = ['.gantt_task_area', '.gantt_task_bg', '.gantt_bg', '.dhx_gantt']
        for (const sel of selectors) {
          const el = rootRef.value.querySelector(sel) as HTMLElement
          if (el && el.scrollTop !== undefined) {
            currentScrollTop = el.scrollTop
            currentScrollLeft = el.scrollLeft || 0
            scrollEl = el
            break
          }
        }
      }

      emits('taskClick', id)

      // 恢复滚动位置
      if (scrollEl) {
        scrollEl.scrollTop = currentScrollTop
        scrollEl.scrollLeft = currentScrollLeft
      }

      return true
    }),
    // P3 修复：dblclick 走 nativeDblClickHandler（dhtmlx-gantt readonly 模式下不 dispatch onTaskDblClick）
    // contextmenu 走 nativeContextMenuHandler（同样原因）
    gantt.attachEvent('onBeforeTaskDrag', (id, mode) => {
      const task = cloneTask(gantt.getTask(id))
      dragSnapshots.set(id, task)
      const result = props.beforeTaskDrag?.({ taskId: id, task, mode })
      if (typeof result === 'string') {
        emits('taskDragRejected', { taskId: id, task, reason: result })
        return false
      }
      if (result === false) {
        emits('taskDragRejected', { taskId: id, task, reason: '当前任务不允许直接拖拽。' })
        return false
      }
      return true
    }),
    gantt.attachEvent('onBeforeTaskChanged', (id, mode) => {
      const payload = buildDragPayload(id, mode)
      const result = props.validateTaskDrop?.(payload)
      const snapshot = dragSnapshots.get(id)

      if (snapshot) {
        ganttApi.silent(() => {
          const currentTask = gantt.getTask(id)
          currentTask.start_date = snapshot.start_date as any
          currentTask.duration = snapshot.duration
          currentTask.parent = snapshot.parent
          ganttApi.updateTask(id)
        })
      }

      if (typeof result === 'string') {
        queueMicrotask(() => {
          forceClearDragState()
          emits('taskDragRejected', { taskId: id, task: payload.task, reason: result })
        })
        return false
      }
      if (result === false) {
        queueMicrotask(() => {
          forceClearDragState()
          emits('taskDragRejected', { taskId: id, task: payload.task, reason: '目标落点不合法。' })
        })
        return false
      }

      queueMicrotask(() => {
        forceClearDragState()
        emits('taskDragEnd', payload)
      })
      return false
    }),
    gantt.attachEvent('onAfterTaskDrag', () => true),
    // 纵向滚动接近底部 → 通知页面追加下一页；真正加载由调用方的 hasMore + 在飞去重决定
    gantt.attachEvent('onGanttScroll', () => {
      const el = getVerticalScroller()
      if (!el || el.scrollHeight <= el.clientHeight + 1) return
      if (el.scrollHeight - el.scrollTop - el.clientHeight > 240) return
      emits('nearBottom')
    })
  ]

  initialized = true
  await parseData()
})

// 监听 props 变化，但只在 tasks 或 links 实际改变时重新解析
watch(
  () => [props.tasks, props.links],
  async () => {
    if (initialized) {
      applyConfig()
      await parseData()
    }
  }
)

// 监听 readonly / allowResize / allowProgress 变化，立即重应用 dhtmlx-gantt 的拖拽配置
// （Gantt 页面按角色切换 readonly：PMC+ 允许拖动；VIEWER 一律禁拖，
//  原 wrapper 只在初始化时设 drag_move，角色切换后必须重新应用）
watch(
  () => [props.readonly, props.allowResize, props.allowProgress],
  () => {
    if (!initialized) return
    applyConfig()
    // 重渲染，确保 cursor: grab → default 立即生效
    ganttApi.render?.()
  }
)

// 监听 scaleUnit 变化，重新应用时间轴配置
watch(
  () => props.scaleUnit,
  () => {
    if (initialized) {
      applyScaleConfig(props.scaleUnit)
      ganttApi.render?.()
    }
  }
)

// 监听负荷带变化：图层回调读的是 props，但 dhtmlx 需要一次重绘才会重新调用它
watch(
  () => props.loadBands,
  () => {
    // 缓存键是数组引用，父组件**原地改**同一数组时键不变 ⇒ 这里强制失效
    loadBandCache = null
    if (initialized) ganttApi.render?.()
  }
)

// 监听 dateFrom/dateTo 变化，更新甘特图显示范围
watch(
  () => [props.dateFrom, props.dateTo],
  async () => {
    if (initialized) {
      applyScaleConfig(props.scaleUnit)
      // 重新解析数据以应用新的日期范围
      await parseData()
    }
  }
)

onBeforeUnmount(() => {
  for (const eventId of eventIds) {
    gantt.detachEvent(eventId)
  }
  eventIds = []
  if (nativeContextMenuHandler && rootRef.value) {
    rootRef.value.removeEventListener('contextmenu', nativeContextMenuHandler, { capture: true })
    nativeContextMenuHandler = null
  }
  if (nativeDblClickHandler && rootRef.value) {
    rootRef.value.removeEventListener('dblclick', nativeDblClickHandler, { capture: true })
    nativeDblClickHandler = null
  }
  if (compareLayerId !== null) ganttApi.removeTaskLayer(compareLayerId as any)
  if (actualLayerId !== null) ganttApi.removeTaskLayer(actualLayerId as any)
  if (laneOverlayLayerId !== null) ganttApi.removeTaskLayer(laneOverlayLayerId as any)
  if (loadBandLayerId !== null) ganttApi.removeTaskLayer(loadBandLayerId as any)
  if (outerLabelLayerId !== null) ganttApi.removeTaskLayer(outerLabelLayerId as any)
  compareLayerId = null
  actualLayerId = null
  laneOverlayLayerId = null
  loadBandLayerId = null
  outerLabelLayerId = null
  loadBandCache = null
  // clearAll 不清 marker 数据源，不主动删会随组件重挂载叠加
  if (todayMarkerId !== null) {
    ganttApi.deleteMarker?.(todayMarkerId)
    todayMarkerId = null
  }
  if (initialized) {
    gantt.clearAll()
  }
  lastParsedCount = 0
})
</script>

<template>
  <div ref="rootRef" class="dhx-gantt-root"></div>
</template>

<style scoped lang="less">
.dhx-gantt-root {
  width: 100%;
  height: 100%;
  /**
   * ⚠️ 这里**不能**设固定高度地板（如 min-height: 680px）。
   *
   * 根节点是 dhtmlx 的容器宿主，它的高度一旦被钉死：
   *  ① 容器比可视区高 ⇒ 超出部分被父级 `.gantt-area` 的 overflow:hidden 裁掉，
   *     **底部那条横向滚动条正好落在被裁掉的那一段里 ⇒ 用户看不到横向滚动条**；
   *  ② dhtmlx 靠 iframe 监听「容器自身尺寸变化」来决定要不要重排，
   *     尺寸被钉死 ⇒ 监听不到变化 ⇒ 内部布局也不跟着重排。
   *
   * 正确做法：高度 100% 跟随父容器（.gantt-area 高度由 flex 决定），
   * 视口/工具栏行数一变，容器尺寸就变 ⇒ dhtmlx 自动重排 ⇒ 横向滚动条始终贴在可视区底边。
   */
  min-height: 0;
  contain: strict;
  overflow-anchor: none; // 禁用滚动锚点
}

:deep(.gantt_grid_scale),
:deep(.gantt_task_scale) {
  background: linear-gradient(180deg, #f6f9ff 0%, #edf4ff 100%);
  border-bottom: 1px solid #dce8f8;
}

// 周末底纹：刻度格 + 时间轴背景格（知名排产系统的通用做法，便于分辨非工作日）
:deep(.gantt_scale_cell.dhx-scale-weekend) {
  background: #eef2f7;
}

:deep(.gantt_task_cell.dhx-timeline-weekend) {
  background-color: #f8fafc;
}

// 今天竖线：灰色虚线 + 浅灰标签（实心红线过抢眼，只作参照线用）
:deep(.gantt_marker.dhx-today-marker) {
  width: 1px;
  background: repeating-linear-gradient(to bottom, #cbd5e1 0 4px, transparent 4px 8px);
}

:deep(.gantt_marker.dhx-today-marker .gantt_marker_content) {
  padding: 0 4px;
  font-size: 10px;
  line-height: 14px;
  color: #64748b;
  background: #f1f5f9;
  border-radius: 0 0 3px;
}

:deep(.gantt_row),
:deep(.gantt_task_row) {
  border-bottom: 1px solid #eef3fb;
}

:deep(.dhx-compare-shadow) {
  position: absolute;
  z-index: 2;
  padding: 0 6px;
  overflow: hidden;
  font-size: 9px;
  line-height: 10px;
  color: #35598f;
  text-overflow: ellipsis;
  white-space: nowrap;
  pointer-events: auto;
  cursor: pointer;
  background: rgb(255 255 255 / 15%);
  border: 1px dashed rgb(37 99 235 / 75%);
  border-radius: 999px;
}

:deep(.dhx-compare-shadow.is-changed) {
  background: rgb(254 243 199 / 35%);
  border-color: rgb(245 158 11 / 85%);
}

:deep(.dhx-compare-shadow.is-critical) {
  box-shadow: inset 0 0 0 1px rgb(190 24 93 / 55%);
}

:deep(.dhx-actual-progress) {
  position: absolute;
  bottom: 4px !important;
  z-index: 3;
  height: 4px;
  pointer-events: none;
  border-radius: 999px;
  box-shadow: 0 0 0 1px rgb(255 255 255 / 65%);
}

:deep(.dhx-actual-progress.is-finished) {
  background: linear-gradient(90deg, #10b981 0%, #059669 100%);
}

:deep(.dhx-actual-progress.is-running) {
  background: repeating-linear-gradient(90deg, #10b981 0, #10b981 10px, #34d399 10px, #34d399 16px);
  opacity: 0.9;
}

:deep(.dhx-actual-progress-dot) {
  position: absolute;
  top: 50%;
  right: -3px;
  width: 8px;
  height: 8px;
  background: #059669;
  border: 2px solid #fff;
  border-radius: 999px;
  transform: translate(50%, -50%);
  box-shadow: 0 0 0 2px rgb(5 150 105 / 28%);
}

:deep(.dhx-actual-progress-badge) {
  position: absolute;
  top: -14px;
  left: 8px;
  padding: 0 4px;
  font-size: 8px;
  line-height: 12px;
  color: #fff;
  white-space: nowrap;
  background: rgb(5 150 105 / 92%);
  border-radius: 999px;
  box-shadow: 0 0 0 1px rgb(255 255 255 / 70%);
}

:deep(.dhx-lane-overlay.is-impact) {
  position: absolute;
  z-index: 1;
  pointer-events: none;
  background: linear-gradient(90deg, rgb(251 191 36 / 12%) 0%, rgb(251 191 36 / 4%) 100%);
  border: 1px dashed rgb(245 158 11 / 65%);
  border-radius: 8px;
}

:deep(.dhx-lane-overlay.is-maintenance) {
  position: absolute;
  z-index: 1;
  pointer-events: none;
  background: repeating-linear-gradient(
    135deg,
    rgb(239 68 68 / 8%) 0,
    rgb(239 68 68 / 8%) 10px,
    rgb(255 255 255 / 0%) 10px,
    rgb(255 255 255 / 0%) 20px
  );
  border: 1px dashed rgb(239 68 68 / 45%);
  border-radius: 8px;
}

:deep(.dhx-freeze-boundary) {
  position: absolute;
  z-index: 4;
  width: 0;
  pointer-events: none;
  border-left: 2px dashed rgb(15 23 42 / 70%);
}

// 泳道占用带（2026-11-06 A4）：不再整片刷浅绿。
//  - 浅色泳道（按资源/订单/物料）：中性淡灰蓝底 + 深色字；
//  - 彩色泳道（按排程域/阶段/工厂/部门）：保留维度底色，只叠一层半透明白柔化。
:deep(.dhx-task-lane.dhx-lane-light .gantt_task_content) {
  font-weight: 600;
  color: #334155 !important;
  background: linear-gradient(180deg, #eef2f8 0%, #e6ecf5 100%) !important;
  border: 1px solid #d8e1ee;
}

:deep(.dhx-task-lane.dhx-lane-strong .gantt_task_content) {
  font-weight: 600;
  color: #fff !important;
  text-shadow: 0 1px 2px rgb(15 23 42 / 40%);
  background: linear-gradient(
    180deg,
    rgb(255 255 255 / 16%) 0%,
    rgb(255 255 255 / 4%) 100%
  ) !important;
  border: 1px solid rgb(255 255 255 / 45%);
}

// 泳道摘要条（2026-11-06）：条高由行级 bar_height=16 交给 dhtmlx 定位，
// 这里只管造型 —— 比 32px 任务条矮一半、两端有收边，一眼可分「这是汇总不是任务」。
:deep(.gantt_task_line.dhx-task-lane) {
  border-radius: 5px;
  box-shadow:
    0 1px 2px rgb(15 23 42 / 10%),
    inset 3px 0 0 rgb(148 163 184 / 30%),
    inset -3px 0 0 rgb(148 163 184 / 30%);
}

:deep(.gantt_task_line.dhx-lane-strong) {
  box-shadow:
    0 1px 2px rgb(15 23 42 / 16%),
    inset 3px 0 0 rgb(255 255 255 / 50%),
    inset -3px 0 0 rgb(255 255 255 / 50%);
}

:deep(.gantt_task_line.dhx-task-lane .gantt_task_content) {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0 8px;
  box-sizing: border-box;
  border-radius: 5px;
  font-size: 11px;
  line-height: 1;
}

// 泳道条文字：左侧「N 条」、右侧红字「延期 M」
:deep(.dhx-lane-inner) {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 100%;
  height: 100%;
  overflow: hidden;
}

:deep(.dhx-lane-count) {
  font-weight: 600;
  white-space: nowrap;
}

:deep(.dhx-lane-mark.is-delay) {
  flex: none;
  font-weight: 700;
  white-space: nowrap;
}

:deep(.dhx-lane-light .dhx-lane-mark.is-delay) {
  color: #b91c1c;
}

:deep(.dhx-lane-strong .dhx-lane-mark.is-delay) {
  color: #fee2e2;
  text-shadow: 0 1px 2px rgb(15 23 42 / 45%);
}

// 条内状态标记（固定右侧）：已完成打勾（浅绿）/ 延期 +Nh（红）
:deep(.dhx-bar-mark) {
  flex: none;
  margin-left: 6px;
  font-weight: 700;
}

:deep(.dhx-bar-mark.is-done) {
  color: #ecfdf5;
}

:deep(.dhx-bar-mark.is-delay) {
  color: #b91c1c;
}

:deep(.dhx-task-readonly .gantt_task_content) {
  opacity: 0.78;
}

:deep(.dhx-task-impacted .gantt_task_content) {
  box-shadow:
    0 0 0 3px rgb(251 191 36 / 95%),
    0 0 0 6px rgb(251 191 36 / 18%);
}

:deep(.dhx-lane-impacted .gantt_task_content) {
  border-color: #f59e0b;
}

:deep(.dhx-task-critical .gantt_task_content) {
  box-shadow: inset 0 0 0 2px rgb(245 158 11 / 82%);
}

// 2026-11-06 用户指定：延期条不再画任何红色线条（原条底 2px 红刻度）。
// 延期仍由条内红色 +Nh 文字表达。

// 时间窗内无任务的泳道：不画兜底 1 小时假条
:deep(.gantt_task_line.dhx-lane-empty) {
  display: none;
}

// 状态背景色（status优先级低）
:deep(.dhx-status-frozen .gantt_task_content) {
  background-color: #2563eb !important;
}

:deep(.dhx-status-firm .gantt_task_content) {
  background-color: #10b981 !important;
}

:deep(.dhx-status-locked .gantt_task_content) {
  background-color: #f59e0b !important;
}

:deep(.dhx-status-delayed .gantt_task_content) {
  background-color: #ef4444 !important;
}

:deep(.dhx-status-open .gantt_task_content) {
  background-color: #94a3b8 !important;
  filter: saturate(1.05);
}

:deep(.dhx-status-running .gantt_task_content) {
  box-shadow: inset 0 4px 0 0 #8b5cf6 !important;
}

:deep(.dhx-status-completed .gantt_task_content) {
  box-shadow: inset 0 4px 0 0 #059669 !important;
}

:deep(.dhx-status-material_shortage .gantt_task_content) {
  box-shadow: inset 0 4px 0 0 #d97706 !important;
}

:deep(.dhx-status-cancelled .gantt_task_content) {
  box-shadow: inset 0 4px 0 0 #475569 !important;
}

// 区域背景色（zone优先级高，会覆盖status）
:deep(.dhx-zone-frozen .gantt_task_content) {
  background-color: #2563eb !important;
}

:deep(.dhx-zone-firm .gantt_task_content) {
  background-color: #10b981 !important;
}

:deep(.dhx-zone-locked .gantt_task_content) {
  background-color: #f59e0b !important;
}

:deep(.dhx-zone-delayed .gantt_task_content) {
  background-color: #ef4444 !important;
}

:deep(.dhx-zone-open .gantt_task_content) {
  background-color: #94a3b8 !important;
  filter: saturate(1.05);
}

:deep(.dhx-zone-running .gantt_task_content) {
  background-color: #8b5cf6 !important;
}

:deep(.dhx-zone-completed .gantt_task_content) {
  background-color: #059669 !important;
}

// 条内文字一律裁在条内（任务号由 .dhx-bar-label 省略号截断）
:deep(.gantt_task_content) {
  z-index: 1;
  overflow: hidden !important;
}

/**
 * 任务条最小心宽保底。
 * 4 小时的条在「天」刻度下只有 ~12px（一个日列的 1/6），肉眼几乎看不见、也点不中 ——
 * 这正是「这甘特图用户用不了」的直接原因。
 * CSS 的 min-width 能盖过 dhtmlx 写在行内 style 上的 width（used width = max(width, min-width)）。
 */
:deep(.gantt_task_line) {
  min-width: 14px;
  // 2026-11-06 A1：圆角 + 极淡投影，条从「色块」变成可点的实体
  border-radius: 6px;
  box-shadow: 0 1px 2px rgb(15 23 42 / 14%);
}

// 状态色是画在 .gantt_task_content 上的（!important），圆角要同步，否则直角会露出来
:deep(.gantt_task_line .gantt_task_content) {
  border-radius: 6px;
}

// 条内任务号（始终渲染；条宽不足时由省略号截断，见 templates.task_text）
:deep(.dhx-bar-label) {
  display: block;
  overflow: hidden;
  font-size: 11px;
  line-height: 26px;
  text-overflow: ellipsis;
  white-space: nowrap;
  // 2026-11-06 A3：中等深浅的条底色上纯白字发虚，加一层淡投影提升可读性
  font-weight: 600;
  text-shadow: 0 1px 2px rgb(15 23 42 / 45%);
}

// 条内布局：任务号占满剩余宽度，状态标记固定在右侧
:deep(.dhx-bar-inner) {
  display: flex;
  align-items: center;
  height: 100%;
  padding: 0 6px;
  box-sizing: border-box;
}

:deep(.dhx-bar-inner .dhx-bar-label) {
  flex: 1 1 auto;
  min-width: 0;
  text-align: left;
}

// 条外标签：条宽撑不下「工序 | 订单号」时写在条右侧（淡底胶囊，压在周末底纹/今日线上也看得清）
:deep(.dhx-bar-outer-label) {
  position: absolute;
  display: flex;
  align-items: center;
  font-size: 11px;
  line-height: 1.2;
  color: #334155;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  pointer-events: none;
  z-index: 2;
  // 2026-11-06 A3：淡底胶囊
  padding: 0 5px;
  background: rgb(255 255 255 / 88%);
  border-radius: 4px;
  box-shadow: 0 0 0 1px rgb(148 163 184 / 22%);
}

:deep(.dhx-outer-label-text) {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

// 窄条条外的延期小时：红字跟在任务号后（2026-11-06）
:deep(.dhx-outer-mark.is-delay) {
  flex: none;
  margin-left: 4px;
  font-weight: 700;
  color: #dc2626;
}

// 窄泳道条条外摘要（2026-11-06）：「N 条 · 延期 M」也用淡底胶囊，字号略小避免抢任务条的视觉
:deep(.dhx-bar-outer-label.is-lane) {
  font-size: 10px;
  font-weight: 600;
  padding: 0 4px;
}

// 计划冻结-斜纹条纹（用于planFreezeFlag=true的任务）
:deep(.dhx-task-frozen .gantt_task_content) {
  background-image: repeating-linear-gradient(
    45deg,
    rgb(255 255 255 / 35%) 0,
    rgb(255 255 255 / 35%) 3px,
    transparent 3px,
    transparent 10px
  ) !important;
  background-position: 0 0 !important;
  background-size: 20px 20px !important;
}

// 文字颜色：保持 zone 背景色下的高对比度
:deep(.dhx-zone-frozen .gantt_task_content),
:deep(.dhx-zone-firm .gantt_task_content),
:deep(.dhx-zone-locked .gantt_task_content),
:deep(.dhx-zone-running .gantt_task_content),
:deep(.dhx-zone-completed .gantt_task_content) {
  padding-right: 24px !important;
  font-weight: 600;
  color: #fff !important;
}

:deep(.dhx-zone-delayed .gantt_task_content) {
  font-weight: 600;
  color: #fff !important;
}

:deep(.dhx-zone-open .gantt_task_content) {
  color: #1e293b !important;
}

// delayed 状态：顶部红色边框 + 红色加粗文字
:deep(.dhx-status-delayed .gantt_task_content) {
  font-weight: 700 !important;
  color: #ef4444 !important;
  box-shadow: inset 0 4px 0 0 #ef4444 !important;
}

:deep(.dhx-zone-delayed .gantt_task_content) {
  font-weight: 700 !important;
  color: #ef4444 !important;
}

// Running 状态：顶部紫色线条（即使背景是 zone 的颜色，也能看到紫色表示正在执行）
:deep(.dhx-status-running .gantt_task_content) {
  box-shadow: inset 0 4px 0 0 #8b5cf6 !important;
}

:deep(.dhx-task-execution-locked .gantt_task_content) {
  box-shadow:
    inset 0 0 0 2px rgb(255 255 255 / 80%),
    0 0 0 1px rgb(15 23 42 / 35%);
}

:deep(.dhx-task-critical .gantt_task_content) {
  box-shadow: inset 0 0 0 2px rgb(245 158 11 / 82%);
}

// 同时有 critical + execution-locked 时：合并两者的 box-shadow
:deep(.dhx-task-critical.dhx-task-execution-locked .gantt_task_content) {
  box-shadow:
    inset 0 0 0 2px rgb(245 158 11 / 82%),
    inset 0 0 0 4px rgb(255 255 255 / 80%),
    0 0 0 3px rgb(15 23 42 / 35%) !important;
}

:deep(.dhx-task-manual-locked .gantt_task_content::after) {
  position: absolute;
  top: 50%;
  right: 4px;
  z-index: 10;
  padding: 1px 4px;
  font-size: 8px;
  line-height: 1.2;
  color: #fff;
  pointer-events: none;
  background: #0f172a;
  border-radius: 999px;
  content: 'LOCK';
  transform: translateY(-50%);
}

:deep(.dhx-task-on-maintenance-lane .gantt_task_content) {
  box-shadow: 0 0 0 2px rgb(239 68 68 / 18%);
}

:deep(.dhx-reason-logistics-delay .gantt_task_content) {
  border: 2px solid rgb(127 29 29 / 95%);
}

:deep(.dhx-reason-logistics-delay .gantt_task_content::before) {
  position: absolute;
  top: 0;
  right: 0;
  width: 0;
  height: 0;
  border-color: transparent #991b1b transparent transparent;
  border-style: solid;
  border-width: 0 10px 10px 0;
  content: '';
}

// 资源负荷带（资源×时间桶；画在泳道行底部，深浅 = 该桶任务数）
:deep(.dhx-load-band) {
  position: absolute;
  z-index: 2;
  pointer-events: none;
}

:deep(.dhx-load-band-cell) {
  position: absolute;
  background: linear-gradient(180deg, rgb(37 99 235 / 85%) 0%, rgb(59 130 246 / 65%) 100%);
  border-radius: 2px;
}

// 计划进度条（自定义，更清晰显示）
:deep(.dhx-plan-progress) {
  position: absolute;
  z-index: 5;
  pointer-events: none;
  background: linear-gradient(90deg, rgb(255 255 255 / 50%) 0%, rgb(255 255 255 / 90%) 100%);
  border-radius: 3px;
  box-shadow: 0 1px 3px rgb(0 0 0 / 20%);
}

:deep(.dhx-plan-progress.is-finished) {
  background: linear-gradient(90deg, rgb(255 255 255 / 60%) 0%, rgb(255 255 255 / 100%) 100%);
}
</style>
