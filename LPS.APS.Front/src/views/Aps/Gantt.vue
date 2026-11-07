<script setup lang="ts">
/**
 * APS V1 4号位 — 甘特图/资源计划（页面 5 / U01 / U06 / U14）
 *
 * 4号位文档第 6 节硬约束：
 *  - 仅展示 FinalTask，不展示 LogicalProductionDemand
 *  - Task 状态仅允许 5 种枚举值：PLANNED / RELEASED / IN_PROGRESS / COMPLETED / CANCELLED
 *  - 本页面只读，所有"改动"通过阶段 B / C / D 的接口进行
 *
 * 本文件作为端到端冒烟测试页：
 *  - 验证 DhxGantt 包装组件 + schedule store + mock fixtures 链路
 *  - 验证 5 状态枚举在 dhtmlx-gantt 上正确染色
 *  - 验证只读模式不触发 drag 事件
 *
 * 2026-10-31：lockMarker 三态（EXECUTION / FIRM / FROZEN）**整块撤除**（5号位 D1-c 裁决）。
 *   理由：①`Task.IsLocked` 全库 100% false 且写方写死 false；②`DemandSupplyHardLock` 0 行；
 *   ③冻结 07:4937 明写 FROZEN_ZONE_LOCK 只进 ScheduleExplanationFact、不进 Task 状态字段
 *   ⇒ 三态无任何落盘源，后端「永不发键」（DTO 预留位保留）。我方不拿 BIT 硬塞三态（那是错标）。
 */

import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'
import { formatUtcDate, formatUtcDateTimeSec } from '@/utils/datetime'
import { resourceCodeNameParts } from '@/utils/apsResource'
import { DhxGantt } from '@/components/DhxGantt'
import type {
  DhxGanttTask,
  DhxGanttDragPayload,
  DhxGanttDragRejectedPayload,
  DhxGanttLoadBand,
  DhxGanttScaleUnit
} from '@/components/DhxGantt/src/types'
import { useScheduleStore } from '@/store/modules/aps/schedule'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { useDomainStore } from '@/store/modules/aps/domain'
import { departmentLookupApi, domainApi, triggerBusinessEntry, scheduleApi } from '@/api/aps-v1'
import type { BusinessEntrySubmission } from '@/api/aps-v1'
import DomainUnavailableBadge from './components/DomainUnavailableBadge.vue'
import GanttAdjustDialog from './components/GanttAdjustDialog.vue'
import ResourceRescheduleDialog from './components/ResourceRescheduleDialog.vue'
import DomainRescheduleDialog from './components/DomainRescheduleDialog.vue'
import {
  PLAN_VERSION_STATUS_LABELS,
  TASK_STATUS_LABELS,
  roleLabelOf,
  type DomainKey,
  type GanttBucketUnit,
  type GanttResourceDto,
  type GanttTaskDelayReason,
  type GanttTaskDto,
  type PlanVersionStatus,
  type PlanVersionSummaryDto,
  type RoleKey,
  type RunType,
  type TaskStatus
} from '@/api/aps-v1/types'
import { SETUP_SOURCE_META, type SetupSource } from '@/api/aps-v1/types/setup'
import { reasonLabelOf } from '@/api/aps-v1/types/explanation'
import { RUN_TYPE_LABEL } from '@/store/modules/aps/run'

import {
  ElAlert,
  ElButton,
  ElCheckbox,
  ElDescriptions,
  ElDescriptionsItem,
  ElDivider,
  ElDropdown,
  ElDropdownMenu,
  ElDropdownItem,
  ElEmpty,
  ElIcon,
  ElInput,
  ElOption,
  ElOptionGroup,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const scheduleStore = useScheduleStore()
const { versions, displayedVersions, currentVersionId, ganttData, loading, error } =
  storeToRefs(scheduleStore)

/**
 * ElSelect v-model 不接受 null，用一个本地 ref 桥接 store.currentVersionId
 */
const selectedVersionId = ref<number | undefined>(undefined)
watch(
  currentVersionId,
  (v) => {
    selectedVersionId.value = v ?? undefined
  },
  { immediate: true }
)
watch(selectedVersionId, (v) => {
  if (v !== undefined && v !== currentVersionId.value) {
    scheduleStore.selectVersion(v, currentGanttWindow())
  }
})

/* ===== 时间尺度（窗口 + 刻度合并成一个控件）===== */

/**
 * 「时间尺度」＝ **看多少天**（窗口）+ **一格多长**（刻度），由同一个下拉决定。
 *
 * ⚠️ 合并前是两个控件：「时间范围」radio（今日/7/30/90 天）+「刻度」下拉（小时/天/周/月）。
 *   两者是 1:1 对应（今日=小时、7 天=天、30 天=周、90 天=月）且互相自动纠正
 *   ⇒ 同一个轴写了两遍：改刻度后 radio 自动取消选中，用户只会以为「时间范围失灵了」。
 *   现在只留一个下拉：一档 = 一个 (窗口跨度, 刻度粒度) 组合，行为不变、认知减半。
 *
 * - 窗口：决定向后端要哪一段数据（取多少条任务）
 * - 刻度：决定画布上一格多长 ⇒ 列宽 ⇒ 任务条能不能写字
 *   （领导反馈「条太细看不见」⇒ 必须能按小时看，4 小时的条才有可读的宽度）
 */
const zoomUnit = ref<DhxGanttScaleUnit>('day')

/** 各尺度的默认窗口跨度（自然日）：小时看 1 天、天看 7 天、周看 30 天、月看 90 天
 *  ⚠️ 小时档必须是 1 天：2 天 = 48 个 28px 小时列，「00:00」标签实际宽 36~40px
 *     ⇒ 每格都溢出、刻度挤成一团（2026-11-05 实测）。1 天时列被拉宽到 ~56px 才放得下。 */
const SCALE_SPAN_DAYS: Record<DhxGanttScaleUnit, number> = {
  hour: 1,
  day: 7,
  week: 30,
  month: 90
}

/**
 * 当前时间窗（自定义）。
 *
 * null ＝ 默认窗（今天起、按当前尺度的跨度）；
 * 非 null ＝ 实际窗口（换尺度时锚在用户当前视野）。
 */
const ganttWindow = ref<{ from: string; to: string; spanDays: number } | null>(null)

/**
 * 窗口参数一律发**朴素墙钟**串（不带 Z）。
 *
 * ⚠️ 后端按朴素字面量比较 from/to（dev 2026-10-30 实测：`from=2026-10-28T12:00:00`
 *   与 `from=2026-10-28T12:00:00.000Z` 的 `totalTasks` 逐位相同 ⇒ Z 被忽略、不做时区换算；
 *   两个端点 `/gantt` 与 `/buckets` 口径一致）。
 * ⇒ 早前用 `toISOString()` 会把本地午夜折成 UTC（前一天 16:00），数据窗比可见轴早 8 小时：
 *   可见轴最后 8 小时没有数据、左边多出 8 小时的任务落在轴外看不见。
 *   甘特条本身按朴素串本地解析（`dayjs(naive)`），桶起点同理 ⇒ 四者现在同一墙钟基准。
 */
function wallClock(d: dayjs.Dayjs): string {
  return d.format('YYYY-MM-DDTHH:mm:ss')
}

/**
 * 时间轴窗口 + 刻度。
 * - 窗口（from/to）：自定义窗优先，否则默认窗（今天起 + 当前尺度的跨度）
 * - 刻度（scaleUnit）：始终由 **zoomUnit**（「时间尺度」）决定
 */
const dateRange = computed<{
  from: string
  to: string
  scaleUnit: DhxGanttScaleUnit
}>(() => {
  if (ganttWindow.value) {
    return {
      from: ganttWindow.value.from,
      to: ganttWindow.value.to,
      scaleUnit: zoomUnit.value
    }
  }
  const start = dayjs().startOf('day')
  const spanDays = SCALE_SPAN_DAYS[zoomUnit.value] ?? 7
  return {
    from: wallClock(start),
    to: wallClock(start.add(spanDays, 'day')),
    scaleUnit: zoomUnit.value
  }
})

/** 当前窗口跨度（自然日）：自定义窗取实际跨度，默认窗取当前尺度的跨度 */
const currentSpanDays = computed<number>(
  () => ganttWindow.value?.spanDays ?? SCALE_SPAN_DAYS[zoomUnit.value] ?? 7
)

/** 当前可视窗 → 后端加载窗口
 *  2026-10-29 起**一律窗口加载**（不再整版本全量）：百万级版本整包会压死后端，
 *  且 5号位 新契约下裸调只返回 8000 条默认页。 */
function currentGanttWindow(): { from: string; to: string; resourceIds: number[] } {
  return {
    from: dateRange.value.from,
    to: dateRange.value.to,
    resourceIds: selectedResourceIds.value
  }
}

/* ===== 视图切换：甘特图 / 资源负荷 / 任务清单 =====
 *  三者看同一份数据、同一时间窗：
 *    - 甘特图：任务条（排程结果，条 = 计划占用区间）
 *    - 资源负荷：资源 × 时间桶的**负荷量**（资源级聚合，不受甘特条已加载条数影响）
 *    - 任务清单：同一批任务的列表形态
 *  ⇒ 同页 Tab：默认甘特占满整屏，切过去才出现，零额外请求。 */
const viewTab = ref<'gantt' | 'load' | 'tasks'>('gantt')

// 切回甘特时让 dhtmlx 重算尺寸：隐藏期间容器宽高归零，不重算会留下一张缩成 0 宽的图
watch(viewTab, (v) => {
  if (v === 'gantt') nextTick(() => window.dispatchEvent(new Event('resize')))
})

/* ===== 任务清单（冻结 09 §六 / 01:1914「今日/未来任务清单」）=====
 *  - 数据源 = 甘特体 `ganttData.tasks`（同一个已部署端点，不另造查询）
 *  - 时间窗与甘特时间轴同源（dateRange：由「时间尺度」决定）
 *  - 只做**列表形态**补齐；MES 下发/工单/执行状态后端尚未接入，不在此伪造 */
const TASK_LIST_PAGE_SIZE = 50
const taskListPage = ref(1)

/**
 * 当前窗口的文字标签：**一律显示实际起止日**。
 *
 * 合并前有「预设档名」（未来 7 天）和「自定义起止日」两种口径 ⇒ 拿档位名冒充实际窗口会骗人
 * （窗口可能被换尺度挪过）。
 */
const windowLabel = computed<string>(() => {
  const from = dayjs(dateRange.value.from).format('MM-DD')
  const span = currentSpanDays.value
  // 1 天时起止是同一天，写成「11-05 ~ 11-05」既冗长又像笔误
  if (span === 1) return `${from}（1 天）`
  const to = dayjs(dateRange.value.to).subtract(1, 'day').format('MM-DD')
  return `${from} ~ ${to}（${span} 天）`
})

const ganttResourceMap = computed(() => {
  const map = new Map<number, GanttResourceDto>()
  for (const r of ganttData.value?.resources ?? []) map.set(r.resourceId, r)
  return map
})

/**
 * 资源多选筛选：**后端早就支持 `resourceIds`**（dev 2026-11-03 实测：
 * `resourceIds=13306` ⇒ 资源 34→1、任务 4483→13；两个 id ⇒ 资源 2、任务 1197），
 * 本页一直没把参数传上去 ⇒ 计划员无法「只看某几条产线」。
 *
 * ⚠️ 过滤后端只回**被选中**的资源 ⇒ 选项表不能跟着缩：用 `knownResources` 累积已见过的资源。
 */
const selectedResourceIds = ref<number[]>([])
/** 已见过的资源全集（过滤时只新增、不删除 ⇒ 下拉选项始终是全量） */
const knownResources = ref<GanttResourceDto[]>([])
watch(
  () => ganttData.value?.resources,
  (list) => {
    if (!list || list.length === 0) return
    const map = new Map<number, GanttResourceDto>()
    for (const r of knownResources.value) map.set(r.resourceId, r)
    for (const r of list) map.set(r.resourceId, r)
    knownResources.value = [...map.values()].sort((a, b) =>
      a.resourceCode.localeCompare(b.resourceCode)
    )
  },
  { immediate: true }
)

/** 下拉选项：资源编码 + 名称（列表不显示内部 ID，见「列表不显示内部 ID」口径） */
const resourceOptions = computed(() =>
  knownResources.value.map((r) => ({
    id: r.resourceId,
    label: resourceCodeNameParts(r).join(' ')
  }))
)

/** 资源筛选变化 ⇒ 重新按新过滤条件取数（甘特体 + 负荷桶，两端同过滤） */
watch(selectedResourceIds, () => {
  reloadCurrentWindow()
})

/** 任务清单的行/计数/分页定义在下方 `filteredTasks` 之后（清单必须与甘特条同源） */

// 切时间窗 / 切版本 → 回第一页（避免停在越界页）
watch([ganttWindow, () => ganttData.value?.planVersionId], () => {
  taskListPage.value = 1
})

/* ===== 导出任务清单（CSV）=====
 *  - 导出**当前看到的**那批任务（同一时间窗 + 同一关键词筛选 ⇒ 与清单页逐行一致）
 *  - 字段只取后端已给的字段（MES 执行状态未接入，不伪造列）
 *  - 加 UTF-8 BOM（U+FEFF）：Excel 打开中文不乱码（无 BOM 时 Excel 按 ANSI 解码）
 */
const EXPORT_COLUMNS = [
  '任务号',
  '订单号',
  '物料编码',
  '物料名称',
  '工序',
  '部门',
  '阶段',
  '生产指示号',
  '资源编码',
  '资源名称',
  '人工槽',
  '计划开始',
  '计划结束',
  '时长(h)',
  '状态',
  'MES工单号',
  '下发状态',
  '客户交期',
  '延期时长(h)',
  '主要原因',
  '延期',
  '数量',
  '单位'
] as const

/** CSV 单元格转义：含分隔符/引号/换行时用双引号包起来，内部引号翻倍 */
function csvCell(value: unknown): string {
  const s = value == null ? '' : String(value)
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

function exportTaskList(): void {
  const rows = taskListRows.value
  if (rows.length === 0) {
    ElMessage.warning('当前窗口内没有可导出的任务。')
    return
  }
  const resourceMap = ganttResourceMap.value
  const lines = [EXPORT_COLUMNS.join(',')]
  for (const t of rows) {
    const r = t.resourceId == null ? undefined : resourceMap.get(t.resourceId)
    const start = t.plannedStartTime ? formatUtcDateTimeSec(t.plannedStartTime) : ''
    const end = t.plannedEndTime ? formatUtcDateTimeSec(t.plannedEndTime) : ''
    const hours =
      t.plannedStartTime && t.plannedEndTime
        ? hoursBetween(t.plannedStartTime, t.plannedEndTime).toFixed(2)
        : ''
    lines.push(
      [
        t.taskNo,
        t.orderNo ?? '',
        t.materialCode ?? '',
        t.materialName ?? '',
        t.operationCode,
        t.departmentName ?? '',
        t.stageCode ?? '',
        t.mtS_InstructionNo ?? '',
        r?.resourceCode ?? '未分配',
        r?.resourceName ?? '未分配',
        t.manualSlotId != null ? '人工' : '',
        start,
        end,
        hours,
        TASK_STATUS_LABELS[sanitizeStatus(t.status)] ?? t.status,
        t.mesWorkOrderNo ?? '',
        t.dispatchStatus ?? '',
        t.customerDueDate ? formatUtcDate(t.customerDueDate) : '',
        t.delayHours != null ? t.delayHours.toFixed(1) : '',
        primaryDelayReasonText(t) ?? '',
        t.isDelayed ? '是' : '',
        t.netQty,
        t.uom
      ]
        .map(csvCell)
        .join(',')
    )
  }
  const blob = new Blob(['\uFEFF' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `任务清单_${dayjs(dateRange.value.from).format('YYYYMMDD')}_${dayjs(
    dateRange.value.to
  ).format('YYYYMMDD')}.csv`
  a.click()
  URL.revokeObjectURL(url)
  ElMessage.success(`已导出 ${rows.length} 条任务。`)
}

/* ===== 多维度切换（U01：资源 / Stage / 工厂 / 部门 / 订单 / 物料；Domain 维度暂隐藏） ===== */
type GroupByKey = 'resource' | 'domain' | 'stage' | 'factory' | 'department' | 'order' | 'material'
const groupBy = ref<GroupByKey>('resource')
const GROUP_BY_OPTIONS: Array<{ value: GroupByKey; label: string }> = [
  { value: 'resource', label: '按资源' },
  // 「按 Domain」暂不放出：现网版本全部单域（实测 999432 全量 235,738 条任务同属 BJ_FAMILY_FRL），
  // 按域分组恒只有一条泳道、无信息量。多域版本出现时再放开（domain 分支与域色板保留）。
  { value: 'stage', label: '按阶段' },
  { value: 'factory', label: '按工厂' },
  { value: 'department', label: '按生产部门' },
  // 2026-11-03：排产员最常用的另两个视角（按订单看、按产品看）；数据已在手上（orderNo/materialCode）
  { value: 'order', label: '按订单' },
  { value: 'material', label: '按物料' }
]

/** Domain 染色（U01：ACTIVE 多 Domain 各 Domain 正确展示）
 *  - v1.2 Domain专项：键改 FAMILY_ 前缀
 *  - ⚠️ 真实 Domain 由后端字典配置（dev 上 15 个），这张表只覆盖早期 mock 键；
 *    真实域的取色走 domainColor()，不在这张表里的键不再一律灰回退。
 */
const DOMAIN_COLORS: Record<string, string> = {
  FAMILY_INJECTION: '#3b82f6',
  FAMILY_ASSEMBLY: '#8b5cf6',
  FAMILY_TEST: '#10b981',
  ALL: '#94a3b8'
}

/** 域调色板（纯展示，非业务数据）：域字典数量会变，故按字典顺序取色而非硬编码色表 */
const DOMAIN_PALETTE = [
  '#3b82f6',
  '#8b5cf6',
  '#10b981',
  '#f59e0b',
  '#ef4444',
  '#06b6d4',
  '#ec4899',
  '#84cc16',
  '#f97316',
  '#6366f1',
  '#14b8a6',
  '#a855f7',
  '#d946ef',
  '#22c55e',
  '#eab308',
  '#0ea5e9'
]

/** Domain 字典（v1.2 Domain专项：从 useDomainStore 派生） */
const domainStore = useDomainStore()

/**
 * domainKey → 展示色
 *  1) DOMAIN_COLORS 显式指定优先（早期 mock 域）
 *  2) 否则按域字典（definitions，登录时 bootstrap 拉全量）顺序取色 ⇒ 每个域一个稳定色
 *  3) 字典里没有（数据引用了未同步的域）⇒ 按 key 散列取色，不再灰回退
 */
function domainColor(domainKey: string): string {
  const explicit = DOMAIN_COLORS[domainKey]
  if (explicit) return explicit
  const keys = domainStore.definitions.map((d) => d.domainKey).sort((a, b) => a.localeCompare(b))
  const idx = keys.indexOf(domainKey)
  if (idx >= 0) return DOMAIN_PALETTE[idx % DOMAIN_PALETTE.length]
  let h = 0
  for (let i = 0; i < domainKey.length; i++) h = (h * 31 + domainKey.charCodeAt(i)) >>> 0
  return DOMAIN_PALETTE[h % DOMAIN_PALETTE.length]
}

/** 当前数据中存在 + store 启用的 Domain 列表（domainKey 排序）
 *  用途只剩「域分项计数」与域染色/泳道；域筛选已撤（见 versionOptionLabel 注释）。 */
const availableDomains = computed<DomainKey[]>(() => {
  const storeSet = new Set(domainStore.activeDomains.map((d) => d.domainKey))
  const set = new Set<string>()
  ganttData.value?.tasks.forEach((t) => set.add(t.domainKey))
  return Array.from(set)
    .filter((k) => storeSet.has(k))
    .sort() as DomainKey[]
})

/** 排程域显示名：中文名优先，缺失回退 domainKey（与排程域维护页一致；筛选值仍为 domainKey） */
function domainLabel(dk: string): string {
  return domainStore.label(dk)
}

/** Domain 任务数（按过滤后统计；U01 一目了然） */
const domainCounts = computed<Record<string, number>>(() => {
  const counts: Record<string, number> = {}
  for (const t of filteredTasks.value) {
    counts[t.domainKey] = (counts[t.domainKey] ?? 0) + 1
  }
  return counts
})

/* ===== 关键词搜索（知名排产系统的通用能力：按任务号/订单号/物料/资源定位）=====
 *  - 纯前端过滤**已加载**的任务（不另发请求、不猜后端语义）
 *  - 甘特图、泳道、任务清单**同源**：搜索词改一处，三处同时收敛 ⇒ 不会出现「图上有、清单里没有」
 *  - 命中字段：任务号 / 生产指示号 / MES 工单号 / 订单号 / 物料编码 / 物料名称 / 工序 / 资源编码 / 资源名称
 */
const keyword = ref('')

/** 关键字归一：去空白 + 小写（大小写不敏感） */
function keywordKey(): string {
  return keyword.value.trim().toLowerCase()
}

/** 单条任务是否命中关键字 */
function matchKeyword(t: GanttTaskDto, resourceMap: Map<number, GanttResourceDto>): boolean {
  const kw = keywordKey()
  if (!kw) return true
  const r = t.resourceId == null ? undefined : resourceMap.get(t.resourceId)
  const haystack = [
    t.taskNo,
    t.mtS_InstructionNo,
    t.mesWorkOrderNo,
    t.orderNo,
    t.materialCode,
    t.materialName,
    t.operationCode,
    r?.resourceCode,
    r?.resourceName
  ]
  return haystack.some((v) => v != null && String(v).toLowerCase().includes(kw))
}

/* ===== 快捷过滤（2026-11-03：主流排产系统的「只看延期 / 只看某工序」开关）=====
 *  - 「只看延期」用后端已给的 `isDelayed`（前端**不自己算**延期，延期判定归后端口径）；
 *  - 「只看工序」用后端已给的 `operationCode`；
 *  - 两项与既有筛选（排程域 / 资源多选 / 受影响 / 关键词）**同时生效（AND）**。
 *  - 工序选项取**已加载任务的去重工序**，不受过滤影响 ⇒ 打开筛选后选项不会自己消失。
 */
const onlyDelayed = ref(false)
const operationFilter = ref<string[]>([])

/** 可选工序清单（工序号 + 当前任务条数；工序号是后端原样给的编码，不自造中文名） */
const availableOperations = computed<Array<{ value: string; label: string }>>(() => {
  const counts = new Map<string, number>()
  for (const t of ganttData.value?.tasks ?? []) {
    const code = t.operationCode?.trim()
    if (!code) continue
    counts.set(code, (counts.get(code) ?? 0) + 1)
  }
  return Array.from(counts.entries())
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([code, count]) => ({ value: code, label: `${code}（${count} 条）` }))
})

/** 过滤后的任务（P5：受影响筛选 + 关键词搜索；AND 关系）
 *  域不再参与筛选：版本即域，域信息在版本下拉里。 */
const filteredTasks = computed(() => {
  let tasks = ganttData.value?.tasks ?? []
  if (onlyDelayed.value) {
    tasks = tasks.filter((t) => t.isDelayed)
  }
  if (operationFilter.value.length > 0) {
    tasks = tasks.filter((t) => operationFilter.value.includes(t.operationCode))
  }
  tasks = applyImpactFilter(tasks)
  if (keywordKey()) {
    const map = ganttResourceMap.value
    tasks = tasks.filter((t) => matchKeyword(t, map))
  }
  return tasks
})

/**
 * 任务清单行 = **甘特条同一份**过滤结果（filteredTasks）
 *
 * 2026-11-04 改口径：数据源从 `ganttData.tasks` 改为 `filteredTasks`。
 *   改前清单只按「与窗口有交集 + 关键词」过滤，**不接**「只看延期 / 只看工序 / 排程域 / 受影响」，
 *   于是勾上「只看延期」后：图上 2431 条、KPI 域计数 2431，而清单仍是 3334 条 ⇒ 同页两个数打架。
 *   改后清单 = filteredTasks（域 / 只看延期 / 工序 / 受影响 / 关键词 全走一遍）
 *   ⇒ 清单行数 == KPI 各域计数之和 == 图上条数，三者恒等。
 * ⚠️ 后端 `/gantt?from&to` 已按窗口过滤 ⇒ 前端不再重复判窗口边界（重复判定会与后端口径差一条边界任务）。
 * ⚠️ 无计划时间的任务（后端「无计划时间的任务保留」）照常进清单，开始/结束留空 ⇒ 不被静默丢。
 */
const taskListRows = computed(() =>
  [...filteredTasks.value].sort(
    (a: GanttTaskDto, b: GanttTaskDto) =>
      (a.plannedStartTime ?? '').localeCompare(b.plannedStartTime ?? '') ||
      a.taskNo.localeCompare(b.taskNo)
  )
)

const taskListTotal = computed(() => taskListRows.value.length)
const taskListPaged = computed(() => {
  const start = (taskListPage.value - 1) * TASK_LIST_PAGE_SIZE
  return taskListRows.value.slice(start, start + TASK_LIST_PAGE_SIZE)
})

/* ===== 泳道（Y 轴；按 groupBy 维度动态生成） ===== */
interface Lane {
  id: string
  label: string
  color: string
  textColor: string
  /** 资源名称（机型）。不上行标签，放悬停里看 */
  resourceName?: string
}

/**
 * 无设备资源任务（资源/桶两端形态不同、必须对齐；dev 2026-10-30 实测）：
 *  - 明细体 `/gantt`：`resourceId` **整键省略**（`WhenWritingNull`）⇒ JS 侧是 undefined；
 *  - 桶体 `/buckets`：无设备任务聚成一行 `resourceId = -1`。
 *
 * 2026-11-06 实测（TZP 版本 1 天窗口）：无设备任务 7,199 条**全部**带 `manualSlotId`，
 * 一条「既无设备也无人工槽」的都没有 ⇒ 这些是**人工槽任务**，不是「没排/没分配」。
 * 因此按资源维度时不再合成一条「未分配资源」，而是按生产部门拆成 `人工 | 工厂 | 部门`。
 */
const UNASSIGNED_RESOURCE_ID = -1
const UNASSIGNED_LANE_ID = 'lane-unassigned'
function isUnassignedTask(t: GanttTaskDto): boolean {
  return t.resourceId == null || t.resourceId === UNASSIGNED_RESOURCE_ID
}
/** 人工槽任务（无设备资源 + 有 manualSlotId；两键互斥） */
function isLaborTask(t: GanttTaskDto): boolean {
  return t.resourceId == null && t.manualSlotId != null
}
/** 人工泳道 id：按**生产部门**成行（同一部门的人工槽合并；id 不嵌标签，标签可变） */
function laborLaneId(t: GanttTaskDto): string {
  return `lane-manual-${t.productionDepartmentId ?? 0}`
}
/** 三段式泳道标签：名称 | 工厂 | 部门（设备行与人工行同一格式） */
function laneLabel(name: string, factory?: string | null, department?: string | null): string {
  return `${name} | ${factory || '—'} | ${department || '—'}`
}

/**
 * 部门 → 工厂名称（人工泳道与「工厂」维度都要用）。
 *
 * 任务明细体只带 `productionDepartmentId` + `departmentName`，**没有工厂**；
 * 工厂名从既有只读接口取：
 *  - `GET /governance/lookups/departments?limit=500`（部门 → factoryId；缺省 200 只回 200/287）
 *  - `GET /governance/factories`（factoryId → 名称）
 * 两条任一失败 ⇒ 退回「窗口资源表里同部门设备的工厂名」，再取不到就显示「—」（不伪造）。
 */
const deptFactoryName = ref(new Map<number, string>())
async function loadDeptFactoryDict(): Promise<void> {
  const [depts, factories] = await Promise.all([
    departmentLookupApi.listDepartments(500),
    domainApi.listFactories().catch(() => [] as Array<{ id: number; name: string }>)
  ])
  if (!depts.length) return
  const factoryById = new Map<number, string>(
    factories.map((f: { id: number; name: string }) => [f.id, f.name] as [number, string])
  )
  const map = new Map<number, string>()
  for (const d of depts) {
    const name = d.factoryId != null ? factoryById.get(d.factoryId) : undefined
    if (name) map.set(d.masterId, name)
  }
  deptFactoryName.value = map
}

/** 资源表兜底：同部门设备的工厂名（部门接口无权限 / 缺行时用） */
const resourceDeptFactoryName = computed(() => {
  const map = new Map<number, string>()
  for (const r of ganttData.value?.resources ?? []) {
    if (r.productionDepartmentId != null && r.factoryName) {
      map.set(r.productionDepartmentId, r.factoryName)
    }
  }
  return map
})

/** 工厂名：有设备取设备行（后端已给），人工/无资源任务按部门反查 */
function factoryNameOfTask(t: GanttTaskDto, resource: GanttResourceDto | undefined): string {
  if (resource?.factoryName) return resource.factoryName
  const deptId = t.productionDepartmentId
  if (deptId == null) return ''
  return deptFactoryName.value.get(deptId) ?? resourceDeptFactoryName.value.get(deptId) ?? ''
}

/**
 * 维度泳道（阶段 / 工厂 / 生产部门）取值。
 *
 * 有设备资源 ⇒ 用资源上的字段（后端权威）；无设备的人工任务 ⇒ 用任务自带字段
 * （`stageCode` / `departmentName`，工厂按部门反查）——不能把人工任务再丢进「未分配资源」。
 * 字段确实没配才显示「未配置」（禁英文 UNKNOWN）。
 */
const DIM_UNCONFIGURED_LABEL = '未配置'
function stageDimOf(t: GanttTaskDto, resource: GanttResourceDto | undefined): string {
  if (resource) return resource.stage || DIM_UNCONFIGURED_LABEL
  return t.stageCode?.trim() || DIM_UNCONFIGURED_LABEL
}
function factoryDimOf(t: GanttTaskDto, resource: GanttResourceDto | undefined): string {
  if (resource) return resource.factoryName || DIM_UNCONFIGURED_LABEL
  return factoryNameOfTask(t, undefined) || DIM_UNCONFIGURED_LABEL
}
function departmentDimOf(t: GanttTaskDto, resource: GanttResourceDto | undefined): string {
  if (resource) return resource.productionDepartmentName || DIM_UNCONFIGURED_LABEL
  return t.departmentName?.trim() || DIM_UNCONFIGURED_LABEL
}

/**
 * 订单 / 物料两个维度的**泳道键取值**（2026-11-03 新增）。
 *
 * ⚠️ 必须**同源**：lanes 造泳道行 id 与 getTaskGroupKey 造任务条的 parent，
 *   两边算出来的值不一致 ⇒ 任务条找不到自己的泳道行 ⇒ 整批条子不显示（静默丢数据）。
 * ⚠️ 空值兜底不能留空串：空串会造出 id 形如 `lane-order-` 的行，与真实订单号混在一起。
 */
const ORDER_UNLABELED = '未标注订单'
const MATERIAL_UNLABELED = '未标注物料'
function orderGroupValue(t: GanttTaskDto): string {
  return t.orderNo?.trim() || ORDER_UNLABELED
}
function materialGroupValue(t: GanttTaskDto): string {
  return t.materialCode?.trim() || MATERIAL_UNLABELED
}

const lanes = computed<Lane[]>(() => {
  const data = ganttData.value
  if (!data) return []

  // 按资源：每个设备一行；人工任务按生产部门另行成行（统一格式：名称 | 工厂 | 部门）
  if (groupBy.value === 'resource') {
    // 只列「已加载任务里出现过」的设备。
    // 若把后端返回的**全量资源清单**都铺成泳道，就会出现一大片没有任何任务条的空行，
    // 看起来像「设备没排任务」。任务翻页到了，对应泳道自然出现（行序见函数末尾的首现序排序）。
    const loadedResourceIds = new Set<number>()
    for (const t of data.tasks) {
      if (t.resourceId != null && t.resourceId !== UNASSIGNED_RESOURCE_ID) {
        loadedResourceIds.add(t.resourceId)
      }
    }
    const rows: Lane[] = data.resources
      .filter((r) => loadedResourceIds.has(r.resourceId))
      .map((r) => {
        // 设备号是现场对机的唯一抓手；资源名称是机型（同类设备重名很多，如多台 CNC车床），
        // 写在行标签里只占宽度、把工厂/部门挤成半截 ⇒ 名称移到悬停
        const identity = resourceCodeNameParts(r)[0] ?? ''
        const name = (r.resourceName ?? '').trim()
        return {
          id: `lane-${r.resourceId}`,
          label: laneLabel(identity, r.factoryName, r.productionDepartmentName),
          color: '#e2e8f0',
          textColor: '#1e293b',
          resourceName: name && name !== identity ? name : ''
        }
      })
    // 人工槽任务（无设备）按生产部门成行：
    //   实测无设备任务全部挂 manualSlotId（1 天窗口 7,199 条 / 38 个部门）⇒「未分配资源」是错标。
    //   人工行与设备行一起按任务流首现序排（见函数末尾），不再固定置顶。
    const laborByDept = new Map<number, { dept: string; factory: string }>()
    let hasTrueUnassigned = false
    for (const t of data.tasks) {
      if (!isUnassignedTask(t)) continue
      if (!isLaborTask(t)) {
        hasTrueUnassigned = true
        continue
      }
      const deptId = t.productionDepartmentId ?? 0
      if (!laborByDept.has(deptId)) {
        const dept = t.departmentName?.trim() || ''
        laborByDept.set(deptId, { dept, factory: factoryNameOfTask(t, undefined) })
      }
    }
    const laborRows: Lane[] = [...laborByDept.entries()].map(([deptId, v]) => ({
      id: `lane-manual-${deptId}`,
      label: laneLabel('人工', v.factory, v.dept),
      color: '#e2e8f0',
      textColor: '#1e293b'
    }))
    // 兜底：理论上不存在（实测 0 条），真出现也不静默丢
    if (hasTrueUnassigned) {
      laborRows.push({
        id: UNASSIGNED_LANE_ID,
        label: laneLabel('未分配', '', ''),
        color: '#e2e8f0',
        textColor: '#1e293b'
      })
    }
    // 泳道行序 = 任务流里首次出现的顺序（2026-11-06，随后端 B 案时间序游标）。
    // 旧规则「人工泳道置顶」是给旧游标 `ISNULL(ResourceId,-1)` 兜底的 —— 那时人工任务恒在第一页；
    // 后端已改按计划开始时刻穿插（5号位 2026-11-06 裁决），再置顶人工就与任务流错位。
    // 只排**显示行序**，不动任务流：任务条仍按后端返回序追加，不本地重排。
    const firstSeenOrder = new Map<string, number>()
    data.tasks.forEach((t: GanttTaskDto, i: number) => {
      const id = isUnassignedTask(t)
        ? isLaborTask(t)
          ? laborLaneId(t)
          : UNASSIGNED_LANE_ID
        : `lane-${t.resourceId}`
      if (!firstSeenOrder.has(id)) firstSeenOrder.set(id, i)
    })
    return [...rows, ...laborRows].sort(
      (a, b) =>
        (firstSeenOrder.get(a.id) ?? Number.MAX_SAFE_INTEGER) -
        (firstSeenOrder.get(b.id) ?? Number.MAX_SAFE_INTEGER)
    )
  }

  // 以下 4 类按 Task 维度动态聚合；用 filteredTasks 做数据源
  const tasks = filteredTasks.value
  const resourceMap = new Map(data.resources.map((r) => [r.resourceId, r]))

  if (groupBy.value === 'domain') {
    const seen = new Map<string, string>()
    for (const t of tasks) {
      if (!seen.has(t.domainKey)) {
        seen.set(t.domainKey, domainColor(t.domainKey))
      }
    }
    return Array.from(seen.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([dk, color]) => ({
        id: `lane-domain-${dk}`,
        label: `${domainLabel(dk)} (${tasks.filter((t) => t.domainKey === dk).length})`,
        color,
        textColor: '#ffffff'
      }))
  }

  if (groupBy.value === 'stage') {
    const seen = new Map<string, { color: string }>()
    for (const t of tasks) {
      const r = resourceMap.get(t.resourceId ?? -1)
      const stage = stageDimOf(t, r)
      if (!seen.has(stage)) {
        seen.set(stage, { color: r?.domainKey ? domainColor(r.domainKey) : '#94a3b8' })
      }
    }
    return Array.from(seen.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([stage, info]) => ({
        id: `lane-stage-${stage}`,
        label: stage,
        color: info.color,
        textColor: '#ffffff'
      }))
  }

  if (groupBy.value === 'factory') {
    const seen = new Map<string, string>()
    for (const t of tasks) {
      const r = resourceMap.get(t.resourceId ?? -1)
      const name = factoryDimOf(t, r)
      if (!seen.has(name)) seen.set(name, '#94a3b8')
    }
    return Array.from(seen.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([name, color]) => ({
        id: `lane-factory-${name}`,
        label: name,
        color,
        textColor: '#ffffff'
      }))
  }

  if (groupBy.value === 'department') {
    const seen = new Map<string, string>()
    for (const t of tasks) {
      const r = resourceMap.get(t.resourceId ?? -1)
      const name = departmentDimOf(t, r)
      if (!seen.has(name)) seen.set(name, '#94a3b8')
    }
    return Array.from(seen.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([name, color]) => ({
        id: `lane-dept-${name}`,
        label: name,
        color,
        textColor: '#ffffff'
      }))
  }

  // 按订单 / 按物料（2026-11-03）：排产员最常用的另两个视角；任务行自带 orderNo / materialCode
  //   ⇒ 纯前端分组，零后端依赖。
  // ⚠️ 泳道配色用中性浅灰（不用 Domain 配色）：这两个维度与排程域无关，
  //   套域色会让人误以为「颜色 = 排程域」。
  if (groupBy.value === 'order') {
    const counts = new Map<string, number>()
    for (const t of tasks) {
      const key = orderGroupValue(t)
      counts.set(key, (counts.get(key) ?? 0) + 1)
    }
    return Array.from(counts.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([orderNo, count]) => ({
        id: `lane-order-${orderNo}`,
        label: `${orderNo}（${count} 条）`,
        color: '#e2e8f0',
        textColor: '#1e293b'
      }))
  }

  if (groupBy.value === 'material') {
    const counts = new Map<string, number>()
    const names = new Map<string, string>()
    for (const t of tasks) {
      const key = materialGroupValue(t)
      counts.set(key, (counts.get(key) ?? 0) + 1)
      if (!names.has(key) && t.materialName) names.set(key, t.materialName)
    }
    return Array.from(counts.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([code, count]) => ({
        id: `lane-material-${code}`,
        label: `${names.get(code) ?? code}（${code}，${count} 条）`,
        color: '#e2e8f0',
        textColor: '#1e293b'
      }))
  }

  return []
})

/* ===== 状态 / 锁定 染色（仅展示，不写库） ===== */
const STATUS_COLORS: Record<TaskStatus, string> = {
  // 2026-11-06 用户指定：已排产任务条改青绿色 #7FB8B0（原灰蓝 #94a3b8）
  PLANNED: '#7FB8B0',
  RELEASED: '#3b82f6',
  IN_PROGRESS: '#8b5cf6',
  COMPLETED: '#059669',
  CANCELLED: '#64748b'
}

/** 图例：条子填充色的含义（按 Domain 分组时填充色切换为域色） */
const legendItems = computed<Array<{ label: string; color: string }>>(() => {
  if (groupBy.value === 'domain') {
    return availableDomains.value.map((dk) => ({ label: domainLabel(dk), color: domainColor(dk) }))
  }
  return (Object.keys(STATUS_COLORS) as TaskStatus[]).map((key) => ({
    label: TASK_STATUS_LABELS[key],
    color: STATUS_COLORS[key]
  }))
})

/* 注：原「四类事实标签（FACT/RESULT/ESTIMATED/RECOMMENDATION）」映射表已撤——
 * 5号位 2026-11-05 B7 裁决「不做、撤」：全部冻结文档无 Task「事实类型」定义
 * （07 的 ESTIMATED 属 Supply CommitmentStatus），故永久缺键 ⇒ 前端撤展示位、删死代码。 */

/* ===== 计划版本下拉标签（状态枚举中文映射 + 所属排程域） =====
 * 版本本身隶属一个排程域（`PlanVersion.DomainKey`），历史上另有一个「排程域」多选筛选；
 * 但任务域 = COALESCE(Order.DomainKey, 版本域)，现网版本全单域 ⇒ 该筛选恒只有 1 个选项、
 * 全选与清空结果相同，纯空转，且切版本时残留会把新版本过滤成 0 条。
 * 2026-11-04 用户拍板：撤掉筛选，把域写进版本下拉，用户选版本时就能看到域。 */
function versionOptionLabel(v: PlanVersionSummaryDto): string {
  const label = PLAN_VERSION_STATUS_LABELS[v.status as PlanVersionStatus] ?? v.status
  const domain = v.domainKey ? domainLabel(v.domainKey) : ''
  return `${v.versionCode}（${label}）${domain ? ` · ${domain}` : ''}`
}

/**
 * 版本项悬停信息（冻结 01 §77A②：看清当前 PlanVersion / 生成时间 / DataCutoffTime / 触发方式 / ACTIVE / Candidate / Base / Run）
 *  - 只渲染 `PlanVersionSummaryDto` 已有字段；无来源 Run 的版本这三项缺键 ⇒ 写「未提供」，不伪造
 *  - 走悬停而不是页面元信息条：甘特主视图要占满高度（10-27 版本信息条加完即撤）
 */
function versionOptionTitle(v: PlanVersionSummaryDto): string {
  const status = PLAN_VERSION_STATUS_LABELS[v.status as PlanVersionStatus] ?? v.status
  const base =
    v.basePlanVersionId != null
      ? (versions.value?.find((x: PlanVersionSummaryDto) => x.id === v.basePlanVersionId)
          ?.versionCode ?? `#${v.basePlanVersionId}`)
      : ''
  return [
    `版本：${v.versionCode}（${status}）`,
    `排程域：${v.domainKey ? domainLabel(v.domainKey) : '未提供'}`,
    `生成时间：${v.computedAt ? formatUtcDateTimeSec(v.computedAt) : '未提供'}`,
    `创建时间：${v.createdAt ? formatUtcDateTimeSec(v.createdAt) : '未提供'}`,
    v.activatedAt ? `激活时间：${formatUtcDateTimeSec(v.activatedAt)}` : '',
    // 冻结 01 §77A 第 6 项：DataCutoffTime / 触发方式（5号位 2026-11-05 B10 已落码）
    `数据截止时间：${v.dataCutoffTime ? formatUtcDateTimeSec(v.dataCutoffTime) : '未提供'}`,
    `触发方式：${v.runType ? (RUN_TYPE_LABEL[v.runType as RunType] ?? v.runType) : '未提供'}`,
    `触发来源：${v.triggeredBy || '未提供'}`,
    `来源排程运行：${v.sourceScheduleRunId != null ? `Run #${v.sourceScheduleRunId}` : '未提供'}`,
    `基线版本：${base || '无（非候选/归档版本）'}`,
    `任务数：${v.totalTasks != null ? v.totalTasks : '未提供'}`
  ]
    .filter(Boolean)
    .join('\n')
}

/**
 * 版本下拉分组（10-31 审计缺口 #3）
 *  - ①「常用版本」= store 的 `displayedVersions`：ACTIVE 置顶 → 最近 3 条 CANDIDATE → 2 条 FAILED → 当前已选兜底
 *  - ②「其他版本」= 其余全部
 * ⚠️ **其余版本仍可达**：分组只是让"常用/失败/历史"分得开，不做版本裁剪
 *   （dev 实测 14 个版本，其中 10 个 FAILED —— 平铺时用户分不出哪条能看，
 *    但若只留 displayedVersions 就再也选不到 999334 等其它 ACTIVE 版本，属能力回退）。
 */
const versionGroups = computed<{ key: string; label: string; items: PlanVersionSummaryDto[] }[]>(
  () => {
    const pinned: PlanVersionSummaryDto[] = displayedVersions.value ?? []
    const all: PlanVersionSummaryDto[] = versions.value ?? []
    const pinnedIds = new Set<number>(pinned.map((v) => v.id))
    const rest = all.filter((v) => !pinnedIds.has(v.id))
    return [
      { key: 'pinned', label: '常用版本', items: pinned },
      { key: 'rest', label: '其他版本', items: rest }
    ].filter((g) => g.items.length > 0)
  }
)

/* ===== 文档第 16 节：MES 下发资格展示 =====
 * 后端 `GanttTaskDto.MesEligible` **不是任何表的列**（全库无此列，`Task` 表 30 列里也没有），
 * 是「接口先占位、真值未落盘」的产物 —— 5号位 2026-10-17 回执 §三 已查库确认。
 * 该函已把它从 **`bool` + `CAST(0 AS BIT)` 恒 false 改为 `bool?` + `CAST(NULL AS BIT)`**
 * （`GanttDataDto.cs:163`；同型的 `CrossDomainBlocked` 一并改）：不改则「判定为否」与「未判定」不可区分。
 *
 * ⇒ 契约下 `null`（未接入期）经 `WhenWritingNull` **整个键不出现**，实测 v2 203/203 无该键。
 *   无论 `false` 还是缺键，本页一律呈现「未接入」：不宣称可/不可下发，只说明判定能力尚未接入。
 *   后端给出真值（true/false 或三态字符串）后，这里按新契约自动放开，无需改判定逻辑。 */
function mesEligibilityOf(task: GanttTaskDto | null): 'ELIGIBLE' | 'INELIGIBLE' | 'UNKNOWN' {
  const raw = task?.mesEligible
  return raw === 'ELIGIBLE' || raw === 'INELIGIBLE' || raw === 'UNKNOWN' ? raw : 'UNKNOWN'
}
const MES_ELIGIBLE_LABEL = {
  ELIGIBLE: '可下发',
  INELIGIBLE: '不可下发',
  UNKNOWN: '未接入'
} as const
const MES_ELIGIBLE_TYPE = {
  ELIGIBLE: 'success',
  INELIGIBLE: 'error',
  UNKNOWN: 'info'
} as const

/* ===== 换型时间来源（4 态，`SetupOptimizer.cs:60-72`）=====
 * 后端落库值仅 EXACT / DEFAULT / SETUP_RULE_MISSING_ZERO_FALLBACK / INITIAL_SETUP_STATE。
 * 未知值原样展示，绝不 `SETUP_SOURCE_META[x].tag` 直取 —— 取到 undefined 会抛 TypeError，
 * 整个详情 Drawer 会白屏。 */
function setupSourceMetaOf(
  raw: string | null | undefined
): { label: string; tag: 'info' | 'success' | 'warning' | 'danger' } | null {
  if (!raw) return null
  return SETUP_SOURCE_META[raw as SetupSource] ?? { label: raw, tag: 'info' }
}

/* ===== 文档第 11 节：异常与原因解释 — 根因展示标签 =====
 * 2026-10-17：标签改为复用冻结 `07 v5.1.13 §八.1` 15 码字典（`REASON_CODE_LABELS`），
 * 不再在甘特页自拟一套平行码表（原 8 码与冻结只有 DUE_DATE_RISK 一处重合，违反 U-004）。
 * 未入字典的码中性展示「待核对·<码值>」，不臆造中文名。
 * 2026-10-31：改走**全站唯一口径** `reasonLabelOf`（types/explanation.ts）——
 *   ① 空串（写方未填原因码，实库 95%）不再渲染成「待核对·」半空标签，改为「未标注原因码」；
 *   ② 消除本页与排程解释页 / CTP 页各自查表的口径漂移。 */
/** 图标为纯前端装饰，冻结未约束；只对能一眼归类的码给专属图标，其余走中性问号。 */
const DELAY_REASON_ICON: Record<string, string> = {
  MATERIAL_SHORTAGE: 'vi-mdi:package-variant',
  RESOURCE_CAPACITY_WAIT: 'vi-mdi:gauge-low',
  PRECEDENCE_WAIT: 'vi-mdi:arrow-up-bold-circle-outline',
  FROZEN_ZONE_LOCK: 'vi-mdi:lock-alert',
  CROSS_DOMAIN_VERSION_MISMATCH_RISK: 'vi-mdi:alert-octagon',
  CROSS_ORG_HANDOFF: 'vi-mdi:swap-horizontal',
  LOGISTICS_DELAY: 'vi-mdi:truck-alert',
  EQUIPMENT_BREAKDOWN_RISK: 'vi-mdi:tools',
  DUE_DATE_RISK: 'vi-mdi:clock-alert'
}
function reasonIcon(code: string): string {
  return DELAY_REASON_ICON[code] ?? 'vi-mdi:help-circle'
}
/** 冻结 `07 v5.1.13 :4938`：INFO / WARN / ERROR 三值，不含 CRITICAL */
const SEVERITY_TAG_TYPE: Record<GanttTaskDelayReason['severity'], 'info' | 'warning' | 'danger'> = {
  INFO: 'info',
  WARN: 'warning',
  ERROR: 'danger'
}
const SEVERITY_LABEL: Record<GanttTaskDelayReason['severity'], string> = {
  INFO: '提示',
  WARN: '告警',
  ERROR: '错误'
}
const ISSUE_CATEGORY_DESC: Record<NonNullable<GanttTaskDelayReason['issueCategory']>, string> = {
  DATA_ISSUE: '上游主数据 / 业务事实层面问题（PO ETA / PI Position / 库存等）',
  SCHEDULING_ISSUE: '排程引擎层面问题（产能 / 锁 / 上游工序 / 设备冲突等）'
}
/** 「其他根因」桶的说明：后端未标注问题分类（数据问题 / 排程问题）的根因统一列在这里。 */
const OTHER_CATEGORY_DESC = '未标注问题分类（数据问题 / 排程问题）的根因，统一列在这里。'

/* ===== DTO → DhxGantt 映射 ===== */
function formatDate(iso: string | undefined): string {
  if (!iso) return dayjs().format('YYYY-MM-DD HH:mm')
  return dayjs(iso).format('YYYY-MM-DD HH:mm')
}

function hoursBetween(startIso: string, endIso: string): number {
  const diff = dayjs(endIso).diff(dayjs(startIso), 'hour', true)
  return Math.max(1, Math.round(diff || 1))
}

/**
 * 4号位约束 #1：保证 status 只能是 5 种枚举之一
 * 后端理论上已是，但前端再做一次硬校验，避免 mock / 老数据渗入非法值
 */
function sanitizeStatus(input: string | undefined): TaskStatus {
  if (input && input in TASK_STATUS_LABELS) return input as TaskStatus
  return 'PLANNED'
}

/**
 * 任务条颜色
 *  - 按 Domain 分组时：Domain 色（让泳道视觉一致，U01）
 *  - 其他维度：Status 色（保持状态可读）
 */
function pickBarColor(task: GanttTaskDto, status: TaskStatus): string {
  if (groupBy.value === 'domain') {
    return domainColor(task.domainKey)
  }
  return STATUS_COLORS[status] ?? '#94a3b8'
}

/**
 * 决定任务所属泳道 ID（**必须与 lanes 里的 id 逐字一致**，否则任务条找不到自己的泳道行）
 *  - resource: lane-{resourceId}；人工槽任务 → lane-manual-{productionDepartmentId}；
 *              真·未分配（既无设备也无人工槽，实测 0 条）→ lane-unassigned
 *  - domain:   lane-domain-{domainKey}
 *  - stage:    lane-stage-{stageDimOf(...)}
 *  - factory:  lane-factory-{factoryDimOf(...)}
 *  - department: lane-dept-{departmentDimOf(...)}
 *  - order:    lane-order-{orderGroupValue(...)}；material: lane-material-{materialGroupValue(...)}
 * ⚠️ 三个维度泳道的 id 里嵌的是**兜底后的标签**（未配置），不是原始字段值 ——
 *   两边（lanes 的 id 与这里的 key）必须都用同一取值助手，否则空维度任务的 parent 匹配不到任何泳道行。
 */
function getTaskGroupKey(task: GanttTaskDto, resourceMap: Map<number, GanttResourceDto>): string {
  switch (groupBy.value) {
    case 'domain':
      return `lane-domain-${task.domainKey}`
    case 'stage': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return `lane-stage-${stageDimOf(task, r)}`
    }
    case 'factory': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return `lane-factory-${factoryDimOf(task, r)}`
    }
    case 'department': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return `lane-dept-${departmentDimOf(task, r)}`
    }
    case 'order':
      return `lane-order-${orderGroupValue(task)}`
    case 'material':
      return `lane-material-${materialGroupValue(task)}`
    case 'resource':
    default:
      if (!isUnassignedTask(task)) return `lane-${task.resourceId}`
      return isLaborTask(task) ? laborLaneId(task) : UNASSIGNED_LANE_ID
  }
}

/**
 * 悬浮提示行（业务口径统一在页面层，组件只负责渲染）。
 *
 * 为什么放在页面层：状态/延期/资源的**中文口径**只有页面知道（TASK_STATUS_LABELS、
 * 资源字典），组件里拼会把术语口径散到两处。
 * 行序固定 ⇒ 悬停时从上往下读得快（知名排产系统的悬浮卡就是这种固定信息结构）。
 */
function buildTooltipLines(t: GanttTaskDto, resource: GanttResourceDto | undefined): string[] {
  const status = sanitizeStatus(t.status)
  const lines: string[] = []
  lines.push(`任务：${t.taskNo}`)
  if (t.mtS_InstructionNo) lines.push(`生产指示号：${t.mtS_InstructionNo}`)
  if (t.orderNo) lines.push(`订单：${t.orderNo}`)
  if (t.materialName) {
    lines.push(`物料：${t.materialName}${t.materialCode ? `（${t.materialCode}）` : ''}`)
  }
  if (t.mesWorkOrderNo) lines.push(`MES 工单：${t.mesWorkOrderNo}`)
  if (t.dispatchStatus) lines.push(`下发状态：${t.dispatchStatus}`)
  lines.push(
    `资源：${
      resource?.resourceCode ||
      resource?.resourceName ||
      // 无设备但挂人工槽 ⇒ 人工作业，不是「未分配」
      (t.manualSlotId != null ? '人工' : '未分配')
    }`
  )
  lines.push(`工序：${t.operationCode}（第 ${t.operationSeq} 道）`)
  if (t.plannedStartTime && t.plannedEndTime) {
    lines.push(`计划：${formatDate(t.plannedStartTime)} ~ ${formatDate(t.plannedEndTime)}`)
    lines.push(`工时：${hoursBetween(t.plannedStartTime, t.plannedEndTime)} h`)
  }
  lines.push(`状态：${TASK_STATUS_LABELS[status]}`)
  if (t.isDelayed) {
    lines.push(
      t.delayHours != null
        ? `延期：${Math.round(t.delayHours)} h（计划完工晚于客户交期）`
        : '延期：是（后端未给延期时长）'
    )
  }
  if (t.plannedProcessQty != null) lines.push(`计划加工数量：${t.plannedProcessQty} ${t.uom}`)
  lines.push(`净需求：${t.netQty} ${t.uom}`)
  return lines
}

/**
 * 左表「数量」列的单位（2026-11-06 用户要求）。
 *
 * 过滤后任务单位一致 ⇒ 单位提到表头（「数量(PS)」），格子里只写数字；
 * 单位混合或有空单位 ⇒ 返回空串，表头保持「数量」、格子带回单位——否则表头会把少数行说错。
 */
const sharedQtyUnit = computed(() => {
  let unit: string | null = null
  for (const t of filteredTasks.value) {
    const u = (t.uom ?? '').trim()
    if (!u) return ''
    if (unit === null) unit = u
    else if (unit !== u) return ''
  }
  return unit ?? ''
})

const dhxTasks = computed<DhxGanttTask[]>(() => {
  const data = ganttData.value
  if (!data) return []
  const resourceMap = new Map(data.resources.map((r) => [r.resourceId, r]))

  // 泳道行（Y 轴；按 groupBy 维度动态生成）
  // 泳道条 = 该行最早开工 → 最晚完工的占用区间（不是第一条任务的时长）
  const laneRows: DhxGanttTask[] = lanes.value.map((lane) => {
    const laneTasks = filteredTasks.value.filter((t) => getTaskGroupKey(t, resourceMap) === lane.id)
    let spanStart: string | undefined
    let spanEnd: string | undefined
    for (const t of laneTasks) {
      if (
        t.plannedStartTime &&
        (!spanStart || dayjs(t.plannedStartTime).isBefore(dayjs(spanStart)))
      ) {
        spanStart = t.plannedStartTime
      }
      if (t.plannedEndTime && (!spanEnd || dayjs(t.plannedEndTime).isAfter(dayjs(spanEnd)))) {
        spanEnd = t.plannedEndTime
      }
    }
    const delayedCount = laneTasks.filter((t) => t.isDelayed).length
    return {
      id: lane.id,
      text: lane.label,
      start_date: formatDate(spanStart),
      duration: spanStart && spanEnd ? hoursBetween(spanStart, spanEnd) : 1,
      type: 'project',
      parent: 0,
      open: true,
      readonly: true,
      color: lane.color,
      textColor: lane.textColor,
      // 左表「开始 / 结束」列：泳道行显示占用区间；无任务时留空
      // （不能走 formatDate(undefined) —— 它兜底返回「当前时间」，会在空行上显示假时间）
      startText: spanStart ? formatDate(spanStart) : '',
      endText: spanEnd ? formatDate(spanEnd) : '',
      laneEmpty: laneTasks.length === 0,
      // 泳道条 = 摘要条（2026-11-06）：比任务条矮一半，条上写「N 条」「延期 M」。
      // 不放设备编号：按资源分组时它已是左表行名首段，聚合泳道（工厂/部门/阶段…）没有单一设备。
      bar_height: 16,
      barLabel: laneTasks.length ? `${laneTasks.length} 条` : '',
      barMark: delayedCount > 0 ? 'delay' : '',
      barMarkText: delayedCount > 0 ? `延期 ${delayedCount}` : '',
      // 泳道条悬浮提示：维度 / 泳道 / 占用区间 / 任务条数 / 延期条数
      tooltipLines: [
        `维度：${GROUP_BY_OPTIONS.find((o) => o.value === groupBy.value)?.label ?? groupBy.value}`,
        `泳道：${lane.label}`,
        ...(lane.resourceName ? [`设备名称：${lane.resourceName}`] : []),
        laneTasks.length && spanStart && spanEnd
          ? `占用：${formatDate(spanStart)} ~ ${formatDate(spanEnd)}`
          : '占用：当前时间窗内无任务',
        `任务：${laneTasks.length} 条${delayedCount > 0 ? ` · 延期 ${delayedCount} 条` : ''}`
      ],
      details:
        laneTasks.length && spanStart && spanEnd
          ? `${lane.label}｜占用 ${formatUtcDateTimeSec(spanStart)} ~ ${formatUtcDateTimeSec(spanEnd)}｜${laneTasks.length} 个任务`
          : `${lane.label}｜当前时间窗内无任务`
    }
  })

  // 任务条（已应用 Domain 过滤器；U01/U04/U14/U06 全上）
  const taskRows: DhxGanttTask[] = filteredTasks.value.flatMap((t) => {
    const status = sanitizeStatus(t.status)
    const resource = resourceMap.get(t.resourceId ?? -1)
    const baseRow: DhxGanttTask & Record<string, unknown> = {
      id: t.taskId,
      // 左表首列「工序 · 生产指示号 / 物料」（物料并入首列，口径与任务清单/导出一致）
      text: gridLabelOf(t),
      start_date: formatDate(t.plannedStartTime),
      startText: t.plannedStartTime ? formatDate(t.plannedStartTime) : '',
      duration: hoursBetween(t.plannedStartTime!, t.plannedEndTime!),
      parent: getTaskGroupKey(t, resourceMap),
      type: 'task',
      open: true,
      // 任务条：可拖动（拖完会触发 handleTaskDragged → 弹确认 → 走 MANUAL_RESCHEDULE）
      // 全图 readonly 由 canManualReschedule 在 wrapper 层控制，VIEWER 时 dhtmlx-gantt 整图禁用 drag_move
      readonly: false,
      color: pickBarColor(t, status),
      textColor: '#ffffff',
      status,
      orderId: t.orderNo,
      materialName: t.materialName,
      // 左表「结束 / 数量」两列（2026-11-06 工厂口径）：结束用后端计划完工原值，
      // 不用 duration 反推（hoursBetween 会四舍五入到整小时，08:01:45 会被显示成 09:00）
      endText: t.plannedEndTime ? formatDate(t.plannedEndTime) : '',
      // 单位已提到表头时不重复写行内单位
      qtyText: sharedQtyUnit.value
        ? String(t.plannedProcessQty ?? t.netQty)
        : `${t.plannedProcessQty ?? t.netQty} ${t.uom}`.trim(),
      laneId: t.resourceId ? String(t.resourceId) : '',
      warningCount: t.isDelayed ? 1 : 0,
      operationCode: t.operationCode,
      operationSeq: t.operationSeq,
      netQty: t.netQty,
      plannedProcessQty: t.plannedProcessQty,
      quantity: t.quantity,
      uom: t.uom,
      isDelayed: t.isDelayed,
      // 冻结 07:3683 / 08:4249；dev 当前全 0（2号位 未标记），有值时自动触发 dhx-task-critical 样式
      isCritical: !!t.isCriticalPath,
      laneName: resource?.resourceName,
      domainKey: t.domainKey,
      shareCount: t.taskShare?.length ?? 1,
      // U14 跨 Domain 阻挡字段不再往下传（B4 展示位已撤；DhxGantt 侧本就未消费该字段）
      // 任务号标签：一律写在条内（条宽不足由组件省略号截断）
      barLabel: barLabelOf(t),
      // 条内状态标记（对齐主流排产系统）：已完成打勾 / 延期标 +Nh
      barMark: status === 'COMPLETED' ? 'done' : t.isDelayed ? 'delay' : '',
      barMarkText:
        status === 'COMPLETED'
          ? '✓'
          : t.isDelayed && t.delayHours
            ? `+${Math.round(t.delayHours)}h`
            : '',
      tooltipLines: buildTooltipLines(t, resource)
    }

    // U06「数量-时间拆分」子条逻辑已撤（5号位 2026-11-05 B8「不做、撤」）：
    //   taskSegments 永久缺键（无载体表），原来的「有拆分 ⇒ 追加 N 条子条」分支永不命中，属死代码。
    return [baseRow]
  })

  return [...laneRows, ...taskRows]
})

/* ===== 负荷带（资源×时间桶；5号位 2026-10-30 `/buckets`）=====
 *  背景：百万级版本按游标分页只能拿到前 N 条任务，泳道大部分是空的（首屏 8000 条只覆盖 3/208 条泳道）。
 *  负荷带是**资源级聚合**，能把整窗负荷铺到所有泳道上 ⇒ 空泳道也有信息。
 *
 *  口径（5号位 2026-10-30 复函 Q1/Q2 书面定稿）：
 *  - 桶宽按后端**实际生效**的 `bucketWidth`（不是请求值）⇒ 降级后不画错；
 *  - `bucketStart` **可早于窗口 from**（周桶锚=周一）⇒ **不裁首桶**；
 *  - 泳道只按 `resourceId` 分组；桶侧 `-1` 是所有无设备任务的聚合，**拆不到人工部门泳道**
 *    ⇒ 2026-11-06 起不在甘特上画（不把聚合负荷重复摊到 38 条人工泳道造成假信号）；
 *  - `bucketStart` 缺键 = **未排桶行**（任务无计划开始时间）⇒ 单列出来，不静默丢。
 */

/** 按窗口跨度挑桶宽（负荷带桶宽跟时间轴尺度对齐：小时/天→日桶、周→周桶、月→月桶） */
function bucketUnitForSpan(spanDays: number): GanttBucketUnit {
  if (spanDays <= 14) return 'day'
  if (spanDays <= 60) return 'week'
  return 'month'
}

/** 负荷带请求窗口（与甘特本体同窗、同墙钟口径；桶宽随时间轴粒度） */
function currentBucketWindow(): {
  from: string
  to: string
  bucket: GanttBucketUnit
  resourceIds: number[]
} {
  return {
    from: dateRange.value.from,
    to: dateRange.value.to,
    bucket: bucketUnitForSpan(currentSpanDays.value),
    resourceIds: selectedResourceIds.value
  }
}

/** 桶起点 + 桶宽 = 桶终点（自然日历：日 +1 天 / 周 +7 天 / 月 +1 个月） */
function addBucketUnit(start: string, unit: GanttBucketUnit): string {
  const d = dayjs(start)
  if (unit === 'day') return wallClock(d.add(1, 'day'))
  if (unit === 'week') return wallClock(d.add(7, 'day'))
  return wallClock(d.add(1, 'month'))
}

/** 负荷带是否可见：桶是**资源级**聚合（桶体 domainKey = 版本域，GROUP BY 无任务域维度），
 *  任务域却是订单级 ⇒ 除「按资源」外的维度无法把桶归到泳道，故不画（不硬凑）。 */
const loadBandVisible = computed(() => groupBy.value === 'resource')

const loadBands = computed<DhxGanttLoadBand[]>(() => {
  if (!loadBandVisible.value) return []
  const unit = scheduleStore.effectiveBucketWidth
  if (!unit) return []
  const out: DhxGanttLoadBand[] = []
  for (const group of scheduleStore.bucketGroups) {
    // `-1` = 全部无设备任务（人工为主）的聚合行；人工泳道已按部门拆开，无法归属 ⇒ 不画
    if (group.resourceId === UNASSIGNED_RESOURCE_ID) continue
    const laneId = `lane-${group.resourceId}`
    for (const row of group.rows) {
      if (!row.bucketStart) continue
      out.push({
        laneId,
        start: row.bucketStart,
        end: addBucketUnit(row.bucketStart, unit),
        taskCount: row.taskCount,
        loadHours: row.loadHours ?? undefined
      })
    }
  }
  return out
})

/** 未排期任务（`bucketStart` 缺键 ⇒ 该资源有「无计划开始时间」的任务）：可见去处，不静默丢 */
const unscheduledLoad = computed(() => {
  const rows = scheduleStore.unscheduledBucketRows
  const count = rows.reduce((n, r) => n + r.taskCount, 0)
  return { count, rows }
})

/* ===== 资源负荷视图（行 = 资源，列 = 时间桶，色深 = 负荷量）=====
 *  - 数据源同上：`/buckets` 的**资源级聚合**（不受甘特条已加载条数影响；首屏泳道空的问题也绕开）
 *  - 行 = 版本内**全部**资源（含窗内零负荷的资源）—— 只列出有负荷的资源会让人以为其余资源没事
 *  - 色深 = 该桶任务数，按**全局**最大任务数归一 ⇒ 行与行之间可比
 *  - ⚠️ 后端**未提供产能口径**（`/buckets` 无 capacity / availableHours 字段）⇒ 本视图只呈现「负荷量」，
 *    **不标超载 / 欠载**（不拿任务数冒充产能利用率）。
 *  - 📌 **口径已锁、落地时点 = V1.1**（5号位 2026-11-02 复函 §二；冻结 `01 §77A.1` 负荷率后移 V1.1，
 *    前移申请已终止，不再提）：分子 = **工时口径**（任务工时按桶重叠**切分**后求和，V1.1 拟名 `loadHoursInBucket`）；
 *    ⛔ **墙钟 `occupiedHours` 方案已撤回** —— 跨天任务 119 条，夜间时段不是产能占用，
 *    按墙钟算会把正常排产天天标成超载（假负荷信号）。
 *    分母 `availableHours`（三级合并窗口 ∩ 桶；授权加班计入；未登记 = 缺键 ≠ 0）这半边未被证伪，V1.1 沿用；
 *    分母缺键 ⇒ 不判。⛔ 分子仍**不得**直接用现有 `loadHours`（整条任务工时计入起点桶 ⇒ 日桶宽下虚高），
 *    修法 = 按桶切分工时。且「三级无登记＝缺键（无口径）」与「明确全 OFF＝0（零可排）」是两种不同事实，不可合并。
 *    ⛔ **超载判定不随本轮落地**（将来若做，判据同为工时口径，且需 0号位 定「回归仪表 vs 告警」定位）
 *    ⇒ 本视图维持现状：负荷量热力图 + 「暂无产能口径，暂不标超载。」
 */
const loadHeatmap = computed(() => {
  const unit = scheduleStore.effectiveBucketWidth
  const groups = scheduleStore.bucketGroups
  const columnSet = new Set<string>()
  const cells = new Map<string, Map<string, { count: number; hours: number | null }>>()

  const rows: Array<{ key: string; label: string; sub: string }> = []
  const rowIndex = new Map<string, number>()
  for (const r of ganttData.value?.resources ?? []) {
    rowIndex.set(String(r.resourceId), rows.length)
    rows.push({
      key: String(r.resourceId),
      label: r.resourceCode || r.resourceName,
      sub: r.factoryName || r.productionDepartmentName || ''
    })
  }
  // 无设备任务行（桶侧 -1）：实测全部是人工槽任务，且桶是聚合行、拆不到部门 ⇒ 用中性口径
  const unassignedKey = String(UNASSIGNED_RESOURCE_ID)
  if (!rowIndex.has(unassignedKey)) {
    rowIndex.set(unassignedKey, rows.length)
    rows.push({ key: unassignedKey, label: '人工（未指定设备）', sub: '' })
  }

  for (const g of groups) {
    const key = String(g.resourceId)
    if (!rowIndex.has(key)) {
      // 桶里有、但资源列表里没有（资源已停用等）⇒ 补一行，不静默丢
      rowIndex.set(key, rows.length)
      const first = g.rows[0]
      rows.push({
        key,
        label: first?.resourceCode || first?.resourceName || `资源 #${g.resourceId}`,
        sub: ''
      })
    }
    let rowCells = cells.get(key)
    if (!rowCells) {
      rowCells = new Map()
      cells.set(key, rowCells)
    }
    for (const r of g.rows) {
      if (!r.bucketStart) continue
      columnSet.add(r.bucketStart)
      rowCells.set(r.bucketStart, { count: r.taskCount, hours: r.loadHours ?? null })
    }
  }

  const columns = [...columnSet].sort()
  let maxCount = 0
  for (const rowCells of cells.values()) {
    for (const c of rowCells.values()) maxCount = Math.max(maxCount, c.count)
  }
  return { unit, columns, rows, cells, maxCount }
})

/** 桶表头：日桶带星期，周/月桶只到日 */
function loadColumnLabel(start: string): string {
  const d = dayjs(start)
  if (loadHeatmap.value.unit === 'day') {
    return `${d.format('MM-DD')}${['日', '一', '二', '三', '四', '五', '六'][d.day()]}`
  }
  return d.format('MM-DD')
}

function loadCellOf(rowKey: string, column: string) {
  return loadHeatmap.value.cells.get(rowKey)?.get(column)
}

function loadCellTitle(rowKey: string, column: string): string {
  const cell = loadCellOf(rowKey, column)
  if (!cell) return '该时间桶无任务'
  const hours = cell.hours != null ? `${cell.hours.toFixed(1)} h` : '未提供'
  return `${column.slice(0, 10)}：${cell.count} 个任务 · 工时 ${hours}`
}

/**
 * 负荷网格的**扁平单元格列表**（表头行 + 每个资源行）。
 * 做成扁平数组而不是 `<template v-for>`：模板里两种写法都被 eslint 规则挑刺，
 * 扁平数组顺带让「表头 sticky」只依赖 grid 的行顺序，不依赖 DOM 结构。
 */
const loadGridCells = computed(() => {
  const { columns, rows, cells, maxCount } = loadHeatmap.value
  const out: Array<{
    key: string
    kind: 'head' | 'label' | 'value'
    text: string
    sub?: string
    title?: string
    style?: Record<string, string>
  }> = []
  out.push({ key: 'head-label', kind: 'head', text: '资源' })
  for (const c of columns) out.push({ key: `head-${c}`, kind: 'head', text: loadColumnLabel(c) })
  for (const row of rows) {
    out.push({ key: `label-${row.key}`, kind: 'label', text: row.label, sub: row.sub })
    for (const c of columns) {
      const cell = cells.get(row.key)?.get(c)
      const ratio = cell && maxCount > 0 ? cell.count / maxCount : 0
      out.push({
        key: `cell-${row.key}-${c}`,
        kind: 'value',
        text: cell ? String(cell.count) : '',
        title: loadCellTitle(row.key, c),
        style:
          cell && cell.count
            ? { background: `rgba(37, 99, 235, ${(0.08 + ratio * 0.6).toFixed(3)})` }
            : {}
      })
    }
  }
  return out
})

/* ===== 任务详情 Drawer ===== */
const drawerVisible = ref(false)
const selectedTaskId = ref<number | null>(null)
const selectedTask = computed<GanttTaskDto | null>(() => {
  if (selectedTaskId.value === null) return null
  return ganttData.value?.tasks.find((t) => t.taskId === selectedTaskId.value) ?? null
})

/** 换型时间来源标签（未知值降级原样显示，不查表硬取） */
const selectedSetupSourceMeta = computed(() => setupSourceMetaOf(selectedTask.value?.setupSource))

/** 当前 Task 关联的资源（用于展示 Resource Down / 不可用窗口） */
const selectedResource = computed(() => {
  const t = selectedTask.value
  if (!t?.resourceId) return null
  return ganttData.value?.resources.find((r) => r.resourceId === t.resourceId) ?? null
})

/** 当前 Task 的 plannedStart/End 与资源的 unavailableWindows 是否有交集（轻量客户端判定） */
const resourceImpactsSelected = computed(() => {
  const t = selectedTask.value
  const r = selectedResource.value
  if (!t?.plannedStartTime || !t?.plannedEndTime || !r?.unavailableWindows?.length) return []
  const tStart = new Date(t.plannedStartTime).getTime()
  const tEnd = new Date(t.plannedEndTime).getTime()
  return r.unavailableWindows.filter((w) => {
    const wStart = new Date(w.from).getTime()
    const wEnd = new Date(w.to).getTime()
    return wStart < tEnd && wEnd > tStart
  })
})

/**
 * Section 11：当前 Task 根因按 issueCategory 分组（数据问题 / 排程问题）
 *  - 文档第 11 节硬约束：数据问题与排程问题要分开显示
 *  - 仅展示后端下发的 ScheduleExplanationFact，前端不计算
 *  - 2026-10-31：后端 r13982 起 `delayReasons` 只带 reasonCode / severity / impactHours，
 *    **没有 issueCategory**（`ScheduleExplanationFact` 无该列，后端本期缺键）
 *    ⇒ 未分类的根因落「其他根因」桶照常出行；否则两个分类桶都空 ⇒ 整块根因区渲染成空白。
 */
const explanationReasonsByCategory = computed<{
  DATA_ISSUE: NonNullable<GanttTaskDto['delayReasons']>
  SCHEDULING_ISSUE: NonNullable<GanttTaskDto['delayReasons']>
  OTHER: NonNullable<GanttTaskDto['delayReasons']>
}>(() => {
  const reasons: GanttTaskDelayReason[] = selectedTask.value?.delayReasons ?? []
  const dataIssue = reasons.filter((r) => r.issueCategory === 'DATA_ISSUE')
  const schedulingIssue = reasons.filter((r) => r.issueCategory === 'SCHEDULING_ISSUE')
  const other = reasons.filter(
    (r) => r.issueCategory !== 'DATA_ISSUE' && r.issueCategory !== 'SCHEDULING_ISSUE'
  )
  // SCHEDULING_ISSUE 内部按 severity 倒序：ERROR > WARN > INFO（冻结 :4938 三值）
  const sevOrder = { ERROR: 3, WARN: 2, INFO: 1 } as const
  const sortBySeverity = (a: GanttTaskDelayReason, b: GanttTaskDelayReason) =>
    sevOrder[b.severity] - sevOrder[a.severity]
  dataIssue.sort(sortBySeverity)
  schedulingIssue.sort(sortBySeverity)
  other.sort(sortBySeverity)
  return { DATA_ISSUE: dataIssue, SCHEDULING_ISSUE: schedulingIssue, OTHER: other }
})

/**
 * 空态判据用「实际可渲染的根因行数」，不用 `delayReasons?.length`：
 * 两者当前相等（每条根因都落进三个桶之一），但同一来源才不会被将来的分桶改动带偏。
 */
const explanationRenderedReasonCount = computed(
  () =>
    explanationReasonsByCategory.value.DATA_ISSUE.length +
    explanationReasonsByCategory.value.SCHEDULING_ISSUE.length +
    explanationReasonsByCategory.value.OTHER.length
)

/**
 * 任务条标签：「工序|生产指示号」。
 *
 * 工序取后端 `operationCode`（接口无工序名称字段）；任务号在派工链里会被多道工序共用，
 * 且对计划员没有阅读价值 ⇒ 条上不再展示，需要时在悬浮提示/详情抽屉里看。
 *
 * 2026-11-06 工厂口径：订单号在车间窗口里常常整片相同（实测版本 999428 的 8000 条任务
 * 只有 1 个订单号），写在条上等于零信息；生产指示号才是班组对单/报工用的执行身份
 * （同版本 7814 条有值、985 个不同值）。订单号改到悬停里看。
 */
function barLabelOf(t: GanttTaskDto): string {
  const instruction = t.mtS_InstructionNo?.trim()
  return `${t.operationCode}${instruction ? '|' + instruction : ''}`
}

/** 左表首列标签：「工序|生产指示号 | 物料编码」（物料不再单列，列宽让给首列） */
function gridLabelOf(t: GanttTaskDto): string {
  const material = t.materialCode?.trim()
  return material ? `${barLabelOf(t)} | ${material}` : barLabelOf(t)
}

const SEVERITY_WEIGHT = { ERROR: 3, WARN: 2, INFO: 1 } as const

/**
 * 任务清单「主要原因」：取 severity 最高的一条（冻结 01 §77A② 要求清单能看到主要原因）。
 * 无事实 ⇒ 返回 null，页面显「—」，不编造原因。
 *
 * 2026-11-05：5号位 Q2-a 放宽过滤后，订单级事实（`objectType='ORDER'`）会透传到该单的每个任务
 *   ⇒ 必须标「（订单级）」，否则用户会以为「这个任务自己的原因」。
 */
function primaryDelayReasonText(t: GanttTaskDto): string | null {
  const list = t.delayReasons ?? []
  if (list.length === 0) return null
  const top = [...list].sort(
    (a, b) => (SEVERITY_WEIGHT[b.severity] ?? 0) - (SEVERITY_WEIGHT[a.severity] ?? 0)
  )[0]
  const label = reasonLabelOf(top.reasonCode)
  const text = list.length > 1 ? `${label} 等 ${list.length} 项` : label
  return top.objectType === 'ORDER' ? `${text}（订单级）` : text
}

/* ===== P5 §12 看板提示：扫所有任务，计算受设备故障影响的 ID 集合 =====
 * 文档第 12 节：故障事实 → ImpactAssessment → ScheduleExplanationFact → RescheduleRecommendation → 看板提示
 * 实现思路：
 *  - 对每个 Task，按 resourceId 找对应的 Resource
 *  - 如果 Task 时间窗与 Resource.unavailableWindows 任一窗口相交，则该 Task 算受影响
 *  - 结果传给 DhxGantt 的 :highlight-task-ids，wrapper 自动给任务条加 dhx-task-impacted class
 *    （CSS 黄色 box-shadow 圈，已写好在 wrapper 里）*/
const impactedTaskIds = computed<number[]>(() => {
  const data = ganttData.value
  if (!data) return []
  const resourceMap = new Map(data.resources.map((r) => [r.resourceId, r]))
  const ids: number[] = []
  for (const t of data.tasks) {
    if (!t.plannedStartTime || !t.plannedEndTime) continue
    const r = resourceMap.get(t.resourceId ?? -1)
    if (!r?.unavailableWindows?.length) continue
    const tStart = new Date(t.plannedStartTime).getTime()
    const tEnd = new Date(t.plannedEndTime).getTime()
    const hit = r.unavailableWindows.some((w) => {
      const wStart = new Date(w.from).getTime()
      const wEnd = new Date(w.to).getTime()
      return wStart < tEnd && wEnd > tStart
    })
    if (hit) ids.push(t.taskId)
  }
  return ids
})

/** 受影响任务的精简信息（taskId + taskNo + 资源 code），供 KPI Tooltip 展示 */
const impactedTaskList = computed(() => {
  const data = ganttData.value
  if (!data) return []
  const resourceMap = new Map(data.resources.map((r) => [r.resourceId, r]))
  const set = new Set(impactedTaskIds.value)
  return data.tasks
    .filter((t) => set.has(t.taskId))
    .map((t) => ({
      taskId: t.taskId,
      taskNo: t.taskNo,
      resourceCode: resourceMap.get(t.resourceId ?? -1)?.resourceCode ?? '-'
    }))
})

/** P5 §12 受影响订单：点击 KPI 数字切换只看受影响任务 */
const filterImpactedOnly = ref(false)

/** 把"只看受影响"过滤器合进 filteredTasks
 *  ⚠️ 入参/返回值写死 GanttTaskDto[]：写成 `typeof filteredTasks.value` 会与 filteredTasks
 *     构成**循环类型依赖**（filteredTasks 调本函数、本函数的类型又来自 filteredTasks）⇒ t 变成隐式 any。 */
function applyImpactFilter(tasks: GanttTaskDto[]): GanttTaskDto[] {
  if (!filterImpactedOnly.value) return tasks
  const set = new Set(impactedTaskIds.value)
  return tasks.filter((t) => set.has(t.taskId))
}

function openTaskDetail(taskId: string | number) {
  // 1) 泳道行被点击（dhtmlx-gantt 把泳道当成 project 类型渲染，ID = "lane-{resourceId}"）
  //    这种点击不应该打开任务详情，应该明确告诉用户"请点任务条而不是泳道行"
  const raw = String(taskId)
  if (raw.startsWith('lane-')) {
    ElMessage.info('请点击下方的任务条（彩色矩形），泳道行不承载任务详情')
    return
  }

  // 2) 其它异常 ID（如 dhtmlx-gantt 内部 ID）
  if (raw.startsWith('lane_')) {
    ElMessage.info(`请点击下方的任务条，泳道行 ID=${raw}`)
    return
  }

  // U06：segment 子条 ID 形如 "{parentTaskId}_seg_{seq}"，点击时跳到父 Task
  const segIdx = raw.lastIndexOf('_seg_')
  if (segIdx > 0) {
    selectedTaskId.value = Number(raw.slice(0, segIdx))
  } else {
    selectedTaskId.value = Number(raw)
  }

  // 3) 防御：selectedTaskId 拿不到对应任务（极少见；类型不匹配 / 数据未加载）
  const matched = ganttData.value?.tasks.find((t) => t.taskId === selectedTaskId.value) ?? null
  if (!matched) {
    console.warn(
      '[Gantt] selectedTaskId not matched. raw=',
      raw,
      'parsed=',
      selectedTaskId.value,
      'available taskIds=',
      ganttData.value?.tasks.slice(0, 5).map((t) => t.taskId)
    )
    ElMessage.warning(`未找到任务 #${selectedTaskId.value}，数据可能尚未加载完成`)
    drawerVisible.value = true
    return
  }

  drawerVisible.value = true
}

/* ===== 事件处理 ===== */
/**
 * 单击 → Drawer 打开
 *  - 浏览器在 dblclick 之前会派发 2 次 click 事件，若直接打开 Drawer 会"闪一下"
 *  - 用 setTimeout 延迟 ~250ms，等 dblclick 来抢
 *  - dblclick 抢到就把这个定时器清掉
 */
let pendingSingleClickTimer: number | null = null

function handleTaskClick(taskId: string | number) {
  if (pendingSingleClickTimer !== null) {
    window.clearTimeout(pendingSingleClickTimer)
  }
  pendingSingleClickTimer = window.setTimeout(() => {
    pendingSingleClickTimer = null
    openTaskDetail(taskId)
  }, 250)
}

function cancelPendingSingleClick(): void {
  if (pendingSingleClickTimer !== null) {
    window.clearTimeout(pendingSingleClickTimer)
    pendingSingleClickTimer = null
  }
}

/**
 * 4号位文档第 6 节最后一句：如需人工调整，应发起 MANUAL_RESCHEDULE 形成 Candidate，再比较、确认
 * 4号位文档第 24 节：禁止"任意拖拽直接改正式 Task"
 *
 * 现在允许拖动，但 DhxGantt 包装层永远 onBeforeTaskChanged=false → 自动回滚位置（不写库），
 * 这里只负责：拖完弹确认框 → 走 runApi.triggerReschedule（runType=LOCAL_RESCHEDULE）→ Candidate 页
 * 角色门控已通过 :readonly="!canManualReschedule" 在 dhtmlx-gantt 层拦住，VIEWER 完全拖不了
 */

/* ===== 统计 KPI ===== */
const stats = computed(() => {
  const tasks = ganttData.value?.tasks ?? []
  return {
    total: tasks.length,
    delayed: tasks.filter((t) => t.isDelayed).length,
    completed: tasks.filter((t) => sanitizeStatus(t.status) === 'COMPLETED').length,
    inProgress: tasks.filter((t) => sanitizeStatus(t.status) === 'IN_PROGRESS').length
  }
})

/* ===== 空态分类（5号位 2026-10-17 R8 新增 `scopeDenied` / `isEmpty`，回执 §六补 `versionExists`）=====
 * 三个信号现已能分清三种空态：
 *   版本不存在   → `versionExists=false`
 *   越权         → `scopeDenied=true`
 *   存在但无任务 → 两者皆真/皆假 + `isEmpty=true`（计数是真实 0）
 * ⚠️ 早期版本这里用 `versionCode === ''` 当第三信号（隐式契约），已换为后端显式位 `versionExists`
 *   （`GanttDataDto.cs:56`，DTO 注释点名「替代 versionCode === '' 隐式契约」）。
 *   兜底仍保留 `!d.versionCode`：老版本后端未部署该字段时行为不变（不退回「无任务」误判）。 */
/**
 * 当前版本的任务总数（版本列表口径，与窗口无关）——用于区分「版本本身没任务」与「当前窗口没任务」
 *
 * ⚠️ 2026-11-04 二改：曾一度**显示**在 KPI 行上（「· 版本共 N 条（整个计划区间）」），现已撤掉：
 *   用户反馈同屏数字打架 —— 「任务 N」与「任务清单」页签徽标是同一个数（纯重复），
 *   「版本共 13201」又是整个计划区间的口径，摆在按窗口看的甘特工具栏只会让人对不上。
 *   现在任务数**只在「任务清单」页签徽标**显示（且跟着筛选走）。
 *   两个数的口径差异仍需记住：
 *     - 任务清单条数 = 当前时间窗内、且**当前筛选生效**的任务数（默认窗＝今天起 7 天，天天在滚）；
 *     - `versionTotalTasks` = 整个计划区间的任务总数（不随日期变）。
 *   ⚠️ 两者口径不同但都能对上：dev 实测 999333（QT/其他）
 *     7990(10-30~11-04) + 3334(11-04~11-11) + 1877(11-11~01-28) = 13201。
 *   数据源注意：`versionTotalTasks` 来自 `/api/schedule/versions` 的 `PlanVersionSummaryDto.TotalTasks`
 *     = 实时 `COUNT(*) FROM [Task] WHERE PlanVersionId=@Id`（带部门范围谓词）；
 *     总览页版本卡那个数来自 `/api/overview/active-plan` 的 `PlanVersion.TotalTasks` **冗余列**
 *     = 排程收尾时的快照（`SchedulingOrchestrator.cs:1291`）。dev 实测两者逐位相同（13201/96/0），
 *     但来源不同 ⇒ 排程后再有任务增删时两者会分叉，属后端口径差异，不是前端算错。
 */
const versionTotalTasks = computed(
  () => versions.value.find((v) => v.id === currentVersionId.value)?.totalTasks ?? null
)

type EmptyKind = 'scope-denied' | 'version-missing' | 'no-tasks' | 'window-empty' | null
const emptyKind = computed<EmptyKind>(() => {
  if (loading.value || error.value) return null
  const d = ganttData.value
  if (!d) return null
  if (!d.isEmpty && d.tasks.length > 0) return null
  if (d.scopeDenied) return 'scope-denied'
  if (d.versionExists === false || !d.versionCode) return 'version-missing'
  // 5号位 2026-10-29 起带过滤口径 `totalTasks`：窗口内 0 条但版本有任务 ⇒ 是「窗口空」不是「版本空」
  if (d.totalTasks === 0 && (versionTotalTasks.value ?? 0) > 0) return 'window-empty'
  return 'no-tasks'
})
const EMPTY_TEXT = computed<Record<Exclude<EmptyKind, null>, { title: string; hint: string }>>(
  () => ({
    'scope-denied': {
      title: '当前账号无权查看该版本',
      hint: '该版本不在你的排程域授权范围内。如需查看，请联系管理员开通对应排程域。'
    },
    'version-missing': {
      title: '该计划版本不存在',
      hint: '可能已被清理。请在左上角重新选择一个计划版本。'
    },
    'no-tasks': {
      title: '当前版本没有排程结果',
      hint: '该版本可能尚未算完，或算完后没有产出任务。'
    },
    'window-empty': {
      title: '当前时间范围没有排程任务',
      hint: `该版本共 ${versionTotalTasks.value ?? 0} 条任务，当前时间窗（${windowLabel.value}）内没有任务。`
    }
  })
)

/* ===== 空窗兜底：把「那批任务在哪儿」摆出来，并给一键跳转 =====
 *
 * 11-04 用户反馈：「NIGHTLY_20261030_FAMILY_TZP 在甘特图上没有任务，总览里却有 96 条」。
 *   实测根因之一：这 96 条全排在 **2026-10-30 08:00 ~ 2026-11-02 08:00**，
 *   而甘特图默认窗是「今天起 7 天」（11-04 ~ 11-10）⇒ 窗口外 ⇒ 后端回 0 条（**数据事实，不是前端算错**）。
 *   但页面只写一句「当前时间范围没有排程任务」⇒ 用户无从判断「是没数据」还是「没看对时间」。
 *
 * 取任务真实区间：查一次**整个计划区间**的**桶聚合**（`/gantt/{id}/buckets?bucket=day`）。
 *   桶聚合是资源×天的计数（桶项自带 firstStart/lastEnd），**不是明细体** ⇒ 轻；
 *   明细体走全区间可能上百万条，不能走。
 */
const taskSpan = ref<{ from: string; to: string } | null>(null)
const taskSpanLoading = ref(false)
let taskSpanCtrl: AbortController | null = null

/** 当前版本的计划区间（版本级，非窗口级） */
const planHorizon = computed(() => {
  const v = versions.value.find((x: PlanVersionSummaryDto) => x.id === currentVersionId.value)
  return v ? { start: v.planHorizonStart, end: v.planHorizonEnd } : null
})

async function loadTaskSpan(): Promise<void> {
  const id = currentVersionId.value
  const hz = planHorizon.value
  if (id === null || !hz) return
  taskSpanCtrl?.abort()
  const ctrl = new AbortController()
  taskSpanCtrl = ctrl
  taskSpanLoading.value = true
  try {
    const dto = await scheduleApi.getGanttBuckets(id, {
      from: hz.start,
      to: hz.end,
      bucket: 'day',
      signal: ctrl.signal
    })
    let min: string | null = null
    let max: string | null = null
    for (const it of dto.items ?? []) {
      if (it.firstStart && (min === null || it.firstStart < min)) min = it.firstStart
      if (it.lastEnd && (max === null || it.lastEnd > max)) max = it.lastEnd
    }
    taskSpan.value = min && max ? { from: min, to: max } : null
  } catch {
    taskSpan.value = null
  } finally {
    taskSpanLoading.value = false
  }
}

/** 任务真实区间的白话文案（提示条用） */
const taskSpanText = computed(() => {
  const s = taskSpan.value
  if (!s) return ''
  return `${dayjs(s.from).format('MM-DD HH:mm')} ~ ${dayjs(s.to).format('MM-DD HH:mm')}`
})

/** 空窗 ⇒ 查一次任务真实区间；不空 / 换版本 ⇒ 清掉 */
watch(
  [emptyKind, currentVersionId],
  ([k]) => {
    if (k === 'window-empty') {
      void loadTaskSpan()
    } else {
      taskSpan.value = null
    }
  },
  { immediate: true }
)

/**
 * 跳到该版本任务所在的区间。
 * 起点挪到任务真实起点、终点盖到任务真实终点 ⇒ 甘特图 / 任务清单 / 资源负荷三处同时有数据。
 */
function jumpToTaskSpan(): void {
  const span = taskSpan.value
  if (!span) return
  const from = dayjs(span.from).startOf('day')
  const to = dayjs(span.to).endOf('day')
  ganttWindow.value = {
    from: wallClock(from),
    to: wallClock(to),
    spanDays: Math.max(1, to.diff(from, 'day') + 1)
  }
}
/** 本次是否只加载了当前窗口的前 N 条（5号位 limit 生效后；老后端无 hasMore） */
const hasMoreInWindow = computed(() => ganttData.value?.hasMore === true)

/**
 * 任务清单空态文案（接 `emptyKind` 口径，版本级 / 窗口级不混说）
 * ⚠️ 旧文案恒为「该时间范围内没有任务」⇒ 版本本身没产出排程结果时也说成"时间范围里没有"，
 *   与画布顶部的 4 态提示（`EMPTY_TEXT`）自相矛盾（10-31 审计缺口 #3）。
 */
const taskListEmptyText = computed(() => {
  switch (emptyKind.value) {
    case 'scope-denied':
      return '当前账号无权查看该版本'
    case 'version-missing':
      return '该计划版本不存在'
    case 'no-tasks':
      return '当前版本没有排程结果'
    case 'window-empty':
      return '当前时间范围内没有排程任务'
    default:
      return '该时间范围内没有任务'
  }
})

/** 纵向滑到甘特底部 → 追加下一页（去重/防抖在 store 里） */
function handleGanttNearBottom(): void {
  if (scheduleStore.canLoadMore) void scheduleStore.loadMore()
}

/** 顶部「加载更多 / 重试」按钮 */
function loadMoreGantt(): void {
  void scheduleStore.loadMore()
}

/* ===== 加载 =====
 * 2026-10-27 用户要求撤掉工具栏的「刷新」按钮：它会走 loadVersions()，
 * 而 loadVersions() 会把选中版本重置成「第一个 ACTIVE 版本」⇒ 手选的版本被冲掉。
 * 数据更新仍有两条路径：切版本下拉（拉该版本甘特）、整页重载。
 */
async function refresh() {
  // 部门→工厂字典：与甘特取数并行，不阻塞首屏；拿到后人工泳道标签自行补全工厂段
  void loadDeptFactoryDict()
  // P0：先加载用户信息（决定按钮可见性 Section 18 角色门控）
  if (!apsAuth.userInfo) {
    await apsAuth.loadUserInfo()
  }
  await scheduleStore.loadVersions()
  if (scheduleStore.currentVersionId !== null) {
    await scheduleStore.loadCurrent(currentGanttWindow())
    // 负荷带与甘特本体是两个端点、两条独立取数链（store 内各自单飞/可中止）
    void scheduleStore.loadBuckets(currentBucketWindow())
  }
}

// 切版本 → 按当前时间窗加载（与 refresh 的调用同键单飞，不会双发）
watch(currentVersionId, (id) => {
  if (id === null) return
  // ⚠️ 切版本必须**清掉资源多选**：resourceIds 直接发给后端，新版本没有这些资源 ⇒ 后端回 0 条。
  //   （排程域筛选已于 2026-11-04 撤销，不再需要清理；域信息在版本下拉里。）
  selectedResourceIds.value = []
  void scheduleStore.loadCurrent(currentGanttWindow())
  void scheduleStore.loadBuckets(currentBucketWindow())
})

// 换尺度改了窗口 → 按新窗口重取（后端按窗口过滤，本页不再持有整版本数据）
watch(ganttWindow, () => {
  if (currentVersionId.value !== null) {
    void scheduleStore.loadCurrent(currentGanttWindow())
    void scheduleStore.loadBuckets(currentBucketWindow())
  }
})

/* ===== 回到今天（长时间窗横滚后一键回到今天竖线） ===== */
const ganttRef = ref<InstanceType<typeof DhxGantt> | null>(null)
function scrollToToday(): void {
  ganttRef.value?.showDate(new Date())
}

/* ===== 时间轴缩放（知名排产系统的通用能力）=====
 *  - 「时间尺度」一档 = 一个 (窗口跨度, 刻度粒度) 组合（见 SCALE_SPAN_DAYS）。
 *  - 刻度由 dhtmlx `ext.zoom` 独占下发，页面只管档位，不直接写 `gantt.config.scales`。 */
const ZOOM_OPTIONS = ['小时', '天', '周', '月'] as const
const ZOOM_UNIT_BY_NAME: Record<string, DhxGanttScaleUnit> = {
  小时: 'hour',
  天: 'day',
  周: 'week',
  月: 'month'
}
const zoomLevelName = ref<string>('天')

/**
 * 时间尺度变更的统一入口（组件侧任何改动——下拉——都会回抛 zoom-change）。
 *  - 同步页面状态（档位名 + 刻度单位）
 *  - **窗口跟着尺度一起换**：一档尺度 = 一个 (跨度, 粒度) 组合（见 SCALE_SPAN_DAYS）
 *
 * ⚠️ **程序回显不改窗口**：组件在 scaleUnit 变化后也会回抛一次 zoom-change（那是页面自己
 *   设的刻度，不是用户操作）⇒ 必须先判「档位是否真的变了」，否则会形成
 *   「改窗口 → 组件回抛 → 改窗口」的循环。
 */
function onZoomChange(name: string): void {
  const unit = ZOOM_UNIT_BY_NAME[name]
  if (!unit) return
  const userChanged = unit !== zoomUnit.value
  zoomLevelName.value = name
  zoomUnit.value = unit
  if (!userChanged) return
  applyScaleWindow(unit)
}

/**
 * 按尺度重算时间窗（换尺度时调用）。
 *
 * - 跨度：取该尺度的默认跨度（SCALE_SPAN_DAYS）——一档尺度一个跨度，不再有「窗口与刻度各自为政」
 * - 起点：**锚在用户当前视野**（当前可见行最早开始）⇒ 用户看的是哪几行，换尺度后还是哪几行
 *   （锚在「今天」会跳到空窗：排产数据常在未来几天，今天起算的窗口里一条任务都没有）
 * - 窗口一变 ⇒ watch(ganttWindow) 按新窗重取数
 */
function applyScaleWindow(unit: DhxGanttScaleUnit): void {
  const spanDays = SCALE_SPAN_DAYS[unit] ?? 7
  const anchor = ganttRef.value?.visibleRowStart?.()
  const from = (anchor ? dayjs(anchor) : dayjs().startOf('day')).startOf('day')
  ganttWindow.value = {
    from: wallClock(from),
    to: wallClock(from.add(spanDays, 'day')),
    spanDays
  }
}
function setZoomLevel(name: string): void {
  ganttRef.value?.setZoomLevelByName(name)
}

/** 按当前窗口重新取数（甘特本体 + 负荷带；两个端点各自单飞） */
function reloadCurrentWindow(): void {
  if (currentVersionId.value === null) return
  void scheduleStore.loadCurrent(currentGanttWindow())
  void scheduleStore.loadBuckets(currentBucketWindow())
}

onMounted(refresh)

/* ===== MANUAL_RESCHEDULE（文档第 6 节最后一句 / Section 12 / Section 17 审计） ===== */
const apsAuth = useApsAuthStore()
const router = useRouter()

/** PMC 及以上才可发起重排（v1.2 §23.1 权限码门控）
 *  - aps.reschedule.manual：dev seed 下 aps.planner + aps.admin.system 持有
 *  - 不用角色判断的原因：与 3号位后端二次校验用同一码；前端显隐 = 后端能写
 */
const canManualReschedule = computed(() => apsAuth.has('aps.reschedule.manual'))

/** 角色徽章颜色（P2 角色切换器 — v1.2 DDL 角色码版） */
const ROLE_TAG: Record<RoleKey, 'success' | 'warning' | 'info' | 'primary' | 'danger'> = {
  'aps.viewer.management': 'info',
  'aps.planner': 'success',
  'aps.admin.aps': 'warning',
  'aps.supervisor.workshop': 'primary',
  'aps.coordinator.material': 'primary',
  'aps.service.api': 'primary',
  'aps.admin.system': 'danger'
}

/* ===== §10A 白天人工调整：本页承载 4 个独立业务入口 =====
 * 冻结文档《APS_V1_4号位页面与业务操作开发实施包 v1.4》§十A（L595-672）：
 * V1 只提供 5 个独立中文业务入口，**不提供"其它明确对象局部调整"万能入口**。
 *  - 本页：甘特图调整(10A.2) / 设备故障后重排(10A.3) / 资源日历调整后重排(10A.4) / 整 Domain 人工重排(10A.5)
 *  - Order 页：已有订单提前(10A.1)
 * 统一出口 triggerBusinessEntry → POST /api/governance/run/candidate（Policy = aps.plan.run）
 *  - 权限：canPlanRun（端点要求 aps.plan.run）；拖拽只读门控仍用 canManualReschedule
 *  - 单 Domain（P0-02）：每个入口只能选一个 domainKey，跨 Domain 由多次单 Domain 调用链式汇总
 *  - U41：Domain 选项按 apsAuth.dataScope.domainKeys 收敛——非空(受限)仅授权 Domain，空(global)全量
 *  - 载荷构建/预校验集中在 runScope.ts（validateScopeDraft / buildScope），本页只做预填与跳转
 */
type BusinessEntryKey =
  | 'GANTT_ADJUSTMENT'
  | 'EQUIPMENT_FAILURE'
  | 'RESOURCE_CALENDAR_CHANGE'
  | 'DOMAIN_MANUAL_RESCHEDULE'

/** 端点权限码（aps.plan.run）——五入口统一门控 */
const canPlanRun = computed(() => apsAuth.has('aps.plan.run'))

const BUSINESS_DOMAIN_OPTIONS = computed<DomainKey[]>(() => {
  const all = domainStore.activeDomains.map((d) => d.domainKey)
  const allowed = apsAuth.dataScope.domainKeys as readonly string[]
  return allowed.length === 0 ? all : all.filter((k) => allowed.includes(k))
})

/**
 * 「当前页面上看得见的那个域」—— 白天人工调整 4 个入口的默认域。
 *
 * ⚠️ 工具栏入口不带任何预填（`entryDomainKey` 为 undefined）⇒ 弹窗会退到
 *   `domainOptions[0]`（dev 实测 = FAMILY_X），而当前版本所属域是 **FAMILY_QT**
 *   ⇒ 弹窗开出来「已选 0 / 0 个任务（当前排程域）」，用户得自己再切一次域。
 * 取值顺序：
 *   ① 当前计划版本的 domainKey（版本本身属一个域）② 已加载任务里第一个可见的域
 *   ③ 都没有 ⇒ undefined（弹窗退回 domainOptions[0]，保持原行为）
 * ⚠️ 必须落在 `BUSINESS_DOMAIN_OPTIONS` 内，否则 ElSelect 选不中、只显示裸 key。
 */
const currentDomainKey = computed<DomainKey | undefined>(() => {
  const opts = BUSINESS_DOMAIN_OPTIONS.value
  const versionDomain = versions.value.find(
    (v: PlanVersionSummaryDto) => v.id === currentVersionId.value
  )?.domainKey
  if (versionDomain && opts.includes(versionDomain as DomainKey)) return versionDomain as DomainKey
  const loaded = (ganttData.value?.tasks ?? [])
    .map((t: GanttTaskDto) => t.domainKey)
    .find((dk: string) => opts.includes(dk as DomainKey))
  return loaded as DomainKey | undefined
})

/** 入口 → Candidate 页 source 标签 */
const ENTRY_SOURCE: Record<BusinessEntryKey, string> = {
  GANTT_ADJUSTMENT: 'gantt-adjust',
  EQUIPMENT_FAILURE: 'equipment-failure',
  RESOURCE_CALENDAR_CHANGE: 'resource-calendar',
  DOMAIN_MANUAL_RESCHEDULE: 'domain-manual'
}

/** 当前打开的入口 Dialog（null = 全关） */
const entryDialog = ref<BusinessEntryKey | null>(null)
const entrySubmitting = ref(false)
const entryDomainKey = ref<DomainKey | undefined>(undefined)
const entryPreselectTaskIds = ref<number[]>([])
const entryPreselectTargetTime = ref<string | undefined>(undefined)
const entryPreselectResourceIds = ref<number[]>([])

/** 打开甘特图调整（拖拽 / Task Drawer 单条预填；工具栏入口无预填） */
function openGanttAdjust(taskIds: number[] = [], targetTime?: string, domainKey?: DomainKey): void {
  entryPreselectTaskIds.value = taskIds
  entryPreselectTargetTime.value = targetTime
  entryPreselectResourceIds.value = []
  entryDomainKey.value = domainKey
  entryDialog.value = 'GANTT_ADJUSTMENT'
}

/** 打开资源类入口（10A.3 / 10A.4；Resource Drawer 单条预填） */
function openResourceEntry(
  key: 'EQUIPMENT_FAILURE' | 'RESOURCE_CALENDAR_CHANGE',
  resourceIds: number[] = [],
  domainKey?: DomainKey
): void {
  entryPreselectTaskIds.value = []
  entryPreselectTargetTime.value = undefined
  entryPreselectResourceIds.value = resourceIds
  entryDomainKey.value = domainKey
  entryDialog.value = key
}

/** 打开整 Domain 人工重排（10A.5） */
function openDomainManual(): void {
  entryPreselectTaskIds.value = []
  entryPreselectTargetTime.value = undefined
  entryPreselectResourceIds.value = []
  entryDomainKey.value = undefined
  entryDialog.value = 'DOMAIN_MANUAL_RESCHEDULE'
}

/** 工具栏「白天人工调整」下拉分发 */
function onEntryCommand(key: BusinessEntryKey): void {
  if (key === 'GANTT_ADJUSTMENT') openGanttAdjust()
  else if (key === 'DOMAIN_MANUAL_RESCHEDULE') openDomainManual()
  else openResourceEntry(key)
}

/** Dialog 提交 → 统一出口 → 跳 Candidate 对比页（页面 5） */
async function submitBusinessEntry(payload: BusinessEntrySubmission): Promise<void> {
  const actor = apsAuth.userInfo?.userCode ?? 'unknown'
  entrySubmitting.value = true
  try {
    const result = await triggerBusinessEntry({
      trigger: payload.trigger,
      domainKey: payload.domainKey,
      draft: payload.draft,
      actor
    })
    ElMessage.success(
      `已发起：批次 #${result.newRunId}，候选版本 #${result.candidatePlanVersionId}。跳转对比页…`
    )
    entryDialog.value = null
    drawerVisible.value = false
    router.push({
      name: 'ApsCandidate',
      query: {
        candidatePlanVersionId: String(result.candidatePlanVersionId),
        basePlanVersionId: String(result.basePlanVersionId),
        source: ENTRY_SOURCE[payload.trigger as BusinessEntryKey] ?? 'manual'
      }
    })
  } catch (err) {
    ElMessage.error(`发起重排失败：${(err as Error)?.message ?? '未知错误'}`)
  } finally {
    entrySubmitting.value = false
  }
}

/** mock 模式角色切换：写一个 wrapper，便于 @command 回调 */
function switchMockRole(role: RoleKey): void {
  apsAuth.mockSwitchRole(role)
  ElMessage.success(`已切换角色：${role}（仅演示模式）`)
}

/** Task 状态 → ElTag 类型映射（P3 浮窗用） */
function statusTagType(status: TaskStatus): 'success' | 'warning' | 'info' | 'primary' | 'danger' {
  switch (status) {
    case 'PLANNED':
      return 'info'
    case 'RELEASED':
      return 'primary'
    case 'IN_PROGRESS':
      return 'warning'
    case 'COMPLETED':
      return 'success'
    case 'CANCELLED':
      return 'danger'
    default:
      return 'info'
  }
}

/* ===== P3：双击任务条 → 轻量 Popover；右键任务条 → 完整菜单 ===== */
interface FloatingPopState {
  visible: boolean
  x: number
  y: number
  task: GanttTaskDto | null
}

/** 把 dhtmlx-gantt 传过来的 taskId 解析成 GanttTaskDto（兼容 lane-* / *_seg_* 前缀） */
function resolveTaskByRawId(raw: string | number): GanttTaskDto | null {
  const s = String(raw)
  if (s.startsWith('lane-') || s.startsWith('lane_')) return null
  const segIdx = s.lastIndexOf('_seg_')
  const taskId = segIdx > 0 ? Number(s.slice(0, segIdx)) : Number(s)
  if (!Number.isFinite(taskId)) return null
  return ganttData.value?.tasks.find((t) => t.taskId === taskId) ?? null
}

const dblClickPopover = ref<FloatingPopState>({ visible: false, x: 0, y: 0, task: null })
const contextMenu = ref<FloatingPopState>({ visible: false, x: 0, y: 0, task: null })

function handleTaskDblClick(taskId: string | number, e?: MouseEvent): void {
  const task = resolveTaskByRawId(taskId)
  if (!task) {
    ElMessage.info('双击无效：请双击任务条（彩色矩形），泳道 / Segment 行不承载双击交互')
    return
  }
  contextMenu.value.visible = false
  dblClickPopover.value = {
    visible: true,
    x: e?.clientX ?? Math.round(window.innerWidth / 2),
    y: e?.clientY ?? Math.round(window.innerHeight / 2),
    task
  }
}

/** P3 修复：直接在 Gantt.vue 的 gantt-area 上挂 capture 监听
 *  - 不依赖 DhxGantt 组件的 taskDblClick emit
 *  - 不依赖 dhtmlx-gantt 内部事件
 *  - 先于 gantt 内部处理（capture 阶段）
 *  - 兼容任务条的不同 class 名（gantt_task_line / gantt_task_line_full / data-task-id）
 */
function handleAreaDblClick(e: MouseEvent): void {
  const target = e.target as HTMLElement | null
  if (!target) return
  // 兼容 dhtmlx-gantt 7+ 的多种任务条渲染
  const taskBar = target.closest(
    '.gantt_task_line, .gantt_task_line_full, .gantt_row[data-task-id], .gantt_row[task_id]'
  ) as HTMLElement | null
  if (!taskBar) return
  const taskId = taskBar.getAttribute('task_id') ?? taskBar.getAttribute('data-task-id')
  if (!taskId) return
  // dblclick 抢先：取消待执行的单击（避免 Drawer 闪一下）
  cancelPendingSingleClick()
  // 关掉可能已经开着的 Drawer（双击语义：聚焦浮窗，关闭详情面板）
  drawerVisible.value = false
  handleTaskDblClick(taskId, e)
}

function handleTaskContextMenu(taskId: string | number, e: MouseEvent): void {
  const task = resolveTaskByRawId(taskId)
  if (!task) return
  dblClickPopover.value.visible = false
  // 防止 dhtmlx-gantt 默认行为抢焦点：把右键位置减去甘特图容器的 offset
  const root = document.querySelector('.aps-gantt-page') as HTMLElement | null
  const rect = root?.getBoundingClientRect()
  contextMenu.value = {
    visible: true,
    x: e.clientX - (rect?.left ?? 0),
    y: e.clientY - (rect?.top ?? 0),
    task
  }
}

/* ===== P4：软拖动 → 打开 §10A.2 甘特图调整 Dialog（预填单条 Task + 软目标时间） =====
 * 4号位文档第 6 节最后一句 + 第 24 节：禁止"任意拖拽直接改正式 Task"，必须经 Candidate 比较确认
 *  - DhxGantt wrapper 的 onBeforeTaskChanged 永远 return false → 位置自动回滚（不写库）
 *  - 拖拽只支持单 Task（payload.taskId 单值，组件不支持多 Task 拖拽）；多 Task 由 Dialog 表格多选承担
 *  - 角色门控：:readonly="!canManualReschedule"（VIEWER 在组件层就拖不动）
 */
function dhxTimeToIso(v: string): string {
  return new Date(v.replace(' ', 'T')).toISOString()
}

function handleTaskDragged(payload: DhxGanttDragPayload): void {
  const task = ganttData.value?.tasks.find((t) => t.taskId === Number(payload.taskId)) ?? null
  if (!task) {
    ElMessage.error(`未找到任务 #${payload.taskId}，请刷新页面后重试`)
    return
  }

  ElMessage.info(`任务位置已自动回滚到原计划；已在调整表单预填 ${task.taskNo} 的软目标时间`)
  openGanttAdjust([task.taskId], dhxTimeToIso(payload.targetStart), task.domainKey)
}

/** DhxGantt 包装层主动拒绝的拖动（一般不会触发，因为 wrapper 永远 onBeforeTaskChanged=false 让位置回滚） */
function handleTaskDragRejected(payload: DhxGanttDragRejectedPayload): void {
  ElMessage?.warning?.(payload.reason ?? '拖动被拒绝')
}

function closeDblClickPopover(): void {
  dblClickPopover.value.visible = false
}
function closeContextMenu(): void {
  contextMenu.value.visible = false
}
/** P3：Drawer 关闭 - 自实现 Drawer 后，click 100% 触发，直接关 */
function closeDrawer(): void {
  drawerVisible.value = false
}

/** 复制文本到剪贴板（modern API 失败时回退 execCommand） */
async function copyText(text: string, label = '已复制'): Promise<void> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
    } else {
      const ta = document.createElement('textarea')
      ta.value = text
      ta.style.position = 'fixed'
      ta.style.opacity = '0'
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
    }
    ElMessage.success(`${label}：${text}`)
  } catch (err) {
    ElMessage.error(`复制失败：${(err as Error)?.message ?? '未知错误'}`)
  }
}

type CtxAction = 'detail' | 'copyTaskNo' | 'copyOrderNo' | 'goOrder' | 'goPi'

function onContextMenuAction(action: CtxAction): void {
  const t: GanttTaskDto | null = contextMenu.value.task
  if (!t) {
    closeContextMenu()
    return
  }
  switch (action) {
    case 'detail':
      selectedTaskId.value = t.taskId
      drawerVisible.value = true
      break
    case 'copyTaskNo':
      void copyText(t.taskNo, '已复制 TaskNo')
      break
    case 'copyOrderNo':
      if (!t.orderNo) {
        ElMessage.warning('该任务无 OrderNo，无法复制')
      } else {
        void copyText(t.orderNo, '已复制 OrderNo')
      }
      break
    case 'goOrder':
      if (!t.orderNo) {
        ElMessage.warning('该任务无 OrderNo，无法跳转订单页')
      } else {
        router.push({ name: 'ApsOrder', query: { orderNo: t.orderNo } })
      }
      break
    case 'goPi':
      router.push({ name: 'ApsPi' })
      break
  }
  closeContextMenu()
}

function onDblClickCopy(): void {
  const t = dblClickPopover.value.task
  if (!t) return
  void copyText(t.taskNo, '已复制 TaskNo')
}

/** 双击浮窗 → 打开完整详情 Drawer */
function openDetailFromDblClickPopover(): void {
  const t = dblClickPopover.value.task
  if (!t) return
  selectedTaskId.value = t.taskId
  drawerVisible.value = true
  closeDblClickPopover()
}

/** 全局 mousedown / Esc：点击空白关闭 floating；Esc 关闭 floating + Drawer */
function handleGlobalClose(e: MouseEvent): void {
  const target = e.target as HTMLElement | null
  if (!target) return
  if (target.closest('.aps-floating-popover')) return
  if (target.closest('.aps-ctx-menu')) return
  // 点击 Drawer panel 内部不关闭（Drawer 自己处理 click）
  if (target.closest('.aps-task-drawer')) return
  closeDblClickPopover()
  closeContextMenu()
}
function handleGlobalKey(e: KeyboardEvent): void {
  if (e.key === 'Escape') {
    closeDblClickPopover()
    closeContextMenu()
    if (drawerVisible.value) closeDrawer()
  }
}
onMounted(() => {
  document.addEventListener('mousedown', handleGlobalClose)
  document.addEventListener('keydown', handleGlobalKey)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', handleGlobalClose)
  document.removeEventListener('keydown', handleGlobalKey)
  cancelPendingSingleClick()
  // 离开页面 → 中止在飞甘特请求（避免「前端放弃、后端白算」）
  scheduleStore.cancelGanttLoad()
})

/**
 * Task Drawer「发起重排」→ 打开 §10A.2 甘特图调整 Dialog（预填单条 Task）
 * 4号位文档第 6 节最后一句：需人工调整时发起 Candidate 运行，再比较、确认
 * P0-02：单计划域（domainKey = task.domainKey）
 * P0-03：单 Task 属局部明确范围 → LOCAL_RESCHEDULE × MANUAL_ADJUSTMENT（§10A.2）
 */
function triggerReschedule(): void {
  const task = selectedTask.value
  if (!task) return
  openGanttAdjust([task.taskId], task.plannedStartTime, task.domainKey)
}

/**
 * Resource Drawer「基于设备故障建议重排」→ 打开 §10A.3 设备故障后重排 Dialog（预填该资源）
 *
 * 4号位文档第 12 节硬约束 + 审核报告 P1-13：
 *  - 设备故障 → 仅展示影响窗口 + 提供建议重排入口，**V1 不建设 PAUSE/RESUME 状态闭环**，决策由 PMC 发起候选运行
 *  - 影响窗口 ≥1 个才允许触发（按钮 v-if 已卡住，这里再 defensive 兜底）
 * P0-02：单计划域（domainKey = resource.domainKey）
 * 载荷：changedResourceIds = [该 resourceId]（§10A.3）
 */
function triggerRescheduleForResource(): void {
  const task = selectedTask.value
  const resource = selectedResource.value
  if (!task || !resource) return

  if (resourceImpactsSelected.value.length === 0) {
    ElMessage.warning('当前资源不可用窗口不影响本任务，无需重排')
    return
  }

  openResourceEntry('EQUIPMENT_FAILURE', [resource.resourceId], resource.domainKey)
}
</script>

<template>
  <div class="aps-gantt-page">
    <!-- ===== 顶部工具栏 ===== -->
    <div class="toolbar">
      <div class="toolbar-left">
        <span class="label">版本</span>
        <ElSelect
          v-model="selectedVersionId"
          placeholder="选择计划版本"
          size="default"
          style="width: 280px"
          :loading="loading"
          :disabled="versions.length === 0"
        >
          <ElOptionGroup v-for="g in versionGroups" :key="g.key" :label="g.label">
            <ElOption
              v-for="v in g.items"
              :key="v.id"
              :label="versionOptionLabel(v)"
              :value="v.id"
              :title="versionOptionTitle(v)"
            />
          </ElOptionGroup>
        </ElSelect>

        <!-- 时间尺度：一档 = 一个 (看多少天, 一格多长) 组合 ⇒ 窗口与刻度合并成一个控件 -->
        <span class="label">时间尺度</span>
        <ElSelect
          :model-value="zoomLevelName"
          class="zoom-select"
          aria-label="时间轴时间尺度"
          size="default"
          style="width: 84px"
          title="时间尺度：一档同时决定看多少天和一格多长时间（小时看日内细节，天看日计划，周月看总览）"
          @update:model-value="setZoomLevel"
        >
          <ElOption v-for="opt in ZOOM_OPTIONS" :key="opt" :label="opt" :value="opt" />
        </ElSelect>
        <ElButton size="default" text title="把时间轴滚动到今天" @click="scrollToToday">
          回到今天
        </ElButton>

        <span class="window-label" :title="'当前时间窗：' + windowLabel">{{ windowLabel }}</span>

        <span class="label">维度</span>
        <ElSelect v-model="groupBy" size="default" style="width: 130px">
          <ElOption
            v-for="opt in GROUP_BY_OPTIONS"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>

        <!-- 域信息已并入「版本」下拉（每个版本自带所属域）⇒ 不再单设「排程域」筛选 -->
        <DomainUnavailableBadge />

        <!-- 资源多选：后端已支持 resourceIds 过滤（计划员「只看某几条产线」） -->
        <ElSelect
          v-model="selectedResourceIds"
          multiple
          collapse-tags
          collapse-tags-tooltip
          size="default"
          style="width: 190px"
          placeholder="全部资源"
          :disabled="resourceOptions.length === 0"
          aria-label="资源筛选"
        >
          <ElOption
            v-for="opt in resourceOptions"
            :key="opt.id"
            :label="opt.label"
            :value="opt.id"
          />
        </ElSelect>

        <!-- 只看延期：后端已给 isDelayed（前端不再自己算延期） -->
        <ElCheckbox v-model="onlyDelayed" class="gantt-filter-check">只看延期</ElCheckbox>

        <!-- 只看某几道工序：工序号是后端原样给的编码 -->
        <ElSelect
          v-model="operationFilter"
          multiple
          collapse-tags
          collapse-tags-tooltip
          size="default"
          style="width: 170px"
          placeholder="全部工序"
          :disabled="availableOperations.length === 0"
          aria-label="工序筛选"
        >
          <ElOption
            v-for="opt in availableOperations"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </ElSelect>

        <!-- 关键词搜索：甘特图 / 泳道 / 任务清单三处同源过滤 -->
        <ElInput
          v-model="keyword"
          class="gantt-search"
          size="default"
          clearable
          placeholder="任务号 / 指示号 / 订单号 / 物料 / 资源"
          aria-label="搜索任务"
        >
          <template #prefix>
            <Icon icon="vi-ep:search" />
          </template>
        </ElInput>
        <span v-if="keyword.trim()" class="search-hint"> 命中 {{ filteredTasks.length }} 条 </span>

        <!-- 业务按钮跟在搜索框之后：与筛选控件同一行流式排布（窄屏只多换行，不再独占一行）。
             插单入口已撤（2026-11-05 用户要求）—— 该功能仍可在 /aps/ctp 页选用途触发。 -->
        <!-- 角色指示器 + 切换器（仅 mock 模式显示；生产模式自动隐藏） -->
        <ElDropdown
          v-if="apsAuth.mockAvailableRoles.length > 0"
          trigger="click"
          @command="switchMockRole"
        >
          <ElTag
            :type="ROLE_TAG[apsAuth.mockActiveRole]"
            effect="dark"
            class="role-badge"
            title="点击切换角色（仅演示模式可用）"
          >
            <Icon icon="vi-ep:user" />
            {{ apsAuth.mockActiveRole }}
            <Icon icon="vi-ep:arrow-down" class="role-caret" />
          </ElTag>
          <template #dropdown>
            <ElDropdownMenu>
              <ElDropdownItem
                v-for="r in apsAuth.mockAvailableRoles"
                :key="r"
                :command="r"
                :disabled="r === apsAuth.mockActiveRole"
              >
                <ElTag :type="ROLE_TAG[r]" size="small" effect="plain">{{ r }}</ElTag>
                <span class="role-hint">
                  {{
                    r === 'aps.viewer.management'
                      ? '只读'
                      : r === 'aps.admin.system'
                        ? '全部权限'
                        : ''
                  }}
                </span>
              </ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>
        <!-- §10A 白天人工调整：5 个独立中文业务入口（本页 4 项 + Order 页「已有订单提前」）
             V1 不提供"其它明确对象局部调整"万能入口（冻结文档 §十A） -->
        <ElDropdown trigger="click" :disabled="!canPlanRun" @command="onEntryCommand">
          <ElButton
            type="warning"
            plain
            :disabled="!canPlanRun"
            title="白天人工调整 — 4 个独立业务入口"
          >
            <Icon icon="vi-mdi:account-multiple-check" />
            白天人工调整
            <Icon icon="vi-ep:arrow-down" class="role-caret" />
          </ElButton>
          <template #dropdown>
            <ElDropdownMenu>
              <ElDropdownItem command="GANTT_ADJUSTMENT"> 甘特图调整（多个任务） </ElDropdownItem>
              <ElDropdownItem command="EQUIPMENT_FAILURE">
                设备故障后重排（多资源）
              </ElDropdownItem>
              <ElDropdownItem command="RESOURCE_CALENDAR_CHANGE">
                资源日历调整后重排（多资源）
              </ElDropdownItem>
              <ElDropdownItem command="DOMAIN_MANUAL_RESCHEDULE" divided>
                整排程域人工重排
              </ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>
      </div>
    </div>

    <!-- ===== 视图切换（甘特图 / 任务清单）：同一份数据，切 Tab 不重新请求 =====
         提示（错误 / 空态）与 Tab 同一行：只放一行短标题，详细说明走 title 悬停 -->
    <div class="view-bar">
      <div class="view-tabs" role="tablist" aria-label="甘特图视图切换">
        <button
          type="button"
          role="tab"
          class="view-tab"
          :class="{ 'is-active': viewTab === 'gantt' }"
          :aria-selected="viewTab === 'gantt'"
          @click="viewTab = 'gantt'"
        >
          甘特图
        </button>
        <button
          type="button"
          role="tab"
          class="view-tab"
          :class="{ 'is-active': viewTab === 'load' }"
          :aria-selected="viewTab === 'load'"
          @click="viewTab = 'load'"
        >
          资源负荷
          <span class="view-tab-count">{{ loadHeatmap.rows.length }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="view-tab"
          :class="{ 'is-active': viewTab === 'tasks' }"
          :aria-selected="viewTab === 'tasks'"
          @click="viewTab = 'tasks'"
        >
          任务清单
          <span
            class="view-tab-count"
            title="当前生效条数（已按排程域/只看延期/只看工序/搜索过滤，与图上条数一致）"
            >{{ taskListTotal }}</span
          >
        </button>
      </div>

      <!-- 错误 / 空状态：一行短提示（悬停看完整说明） -->
      <span v-if="error" class="view-bar-note is-error" :title="`加载失败：${error}`">
        <Icon icon="vi-ep:warning-filled" />
        加载失败：{{ error }}
      </span>
      <span
        v-else-if="scheduleStore.moreError"
        class="view-bar-note is-error"
        :title="scheduleStore.moreError"
      >
        <Icon icon="vi-ep:warning-filled" />
        加载更多失败：{{ scheduleStore.moreError }}
        <ElButton link size="small" @click="loadMoreGantt">重试</ElButton>
      </span>
      <span v-else-if="emptyKind" class="view-bar-note" :title="EMPTY_TEXT[emptyKind].hint">
        <Icon icon="vi-ep:info-filled" />
        {{ EMPTY_TEXT[emptyKind].title }}
        <!-- 空窗但该版本确实有任务：把那批任务的真实区间摆出来，并给一键跳转
             （否则用户只看到「没有任务」，分不清是没数据还是没看对时间） -->
        <template v-if="emptyKind === 'window-empty'">
          <template v-if="taskSpan">
            该版本 {{ versionTotalTasks ?? 0 }} 条任务排在 {{ taskSpanText }}，不在当前时间窗（{{
              windowLabel
            }}）
          </template>
          <ElButton link size="small" :loading="taskSpanLoading" @click="jumpToTaskSpan">
            跳到任务所在时间
          </ElButton>
        </template>
      </span>
      <!-- 5号位 limit 生效后：窗口内任务超过单次上限时明示「已加载 / 共 N 条」，可下滑或点按钮继续 -->
      <span
        v-else-if="hasMoreInWindow"
        class="view-bar-note"
        :title="`当前时间范围共 ${ganttData?.totalTasks ?? '未知'} 条任务，已加载 ${stats.total} 条（单次上限 ${ganttData?.limit ?? 4000}）。向下滚动甘特图可继续加载。`"
      >
        <Icon icon="vi-ep:info-filled" />
        已加载前 {{ stats.total }} 条 / 共 {{ ganttData?.totalTasks ?? '?' }} 条，向下滚动继续加载
        <ElButton link size="small" :loading="scheduleStore.loadingMore" @click="loadMoreGantt">
          加载更多
        </ElButton>
      </span>

      <!-- 右侧：统计 KPI + 图例（图例只描述甘特条，任务清单页签下隐藏） -->
      <div class="view-bar-right">
        <span
          class="stat"
          :title="`资源＝当前窗口内后端返回的资源数；泳道＝当前维度下的分组行数；域分项＝当前筛选生效的任务按排程域分布（合计＝任务清单条数）`"
        >
          资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道
          <strong>{{ lanes.length }}</strong
          ><!-- 域分项只在 **≥2 个域** 时显示：
                单域时它恒等于「任务清单」那个数（各域之和 = 生效条数）⇒ 并排两个一样的数纯属重复。
                多域时它才是真正的「分布」信息（哪个域多少条），必须留。 -->
          <template v-if="availableDomains.length > 1"
            ><span
              v-for="dk in availableDomains"
              :key="dk"
              class="stat-domain"
              :style="{ color: domainColor(dk) }"
            >
              | {{ domainLabel(dk) }} <strong>{{ domainCounts[dk] ?? 0 }}</strong></span
            ></template
          >
          <!-- 未排期任务（桶体 bucketStart 缺键 ⇒ 无计划开始时间）：可见去处，不静默丢 -->
          <ElTooltip v-if="unscheduledLoad.count > 0" placement="bottom" :show-after="100">
            <template #content>
              <div class="impacted-tooltip">
                <div class="impacted-tooltip-title">
                  未排期任务（无计划开始时间）共 {{ unscheduledLoad.count }} 条：
                </div>
                <ul class="impacted-tooltip-list">
                  <li v-for="r in unscheduledLoad.rows.slice(0, 10)" :key="r.resourceId">
                    <span class="impacted-tooltip-res">
                      {{
                        r.resourceCode ||
                        r.resourceName ||
                        (r.resourceId === -1 ? '人工（未指定设备）' : `资源 #${r.resourceId}`)
                      }}
                    </span>
                    <span>{{ r.taskCount }} 条</span>
                  </li>
                  <li v-if="unscheduledLoad.rows.length > 10" class="impacted-tooltip-more">
                    …还有 {{ unscheduledLoad.rows.length - 10 }} 个资源
                  </li>
                </ul>
              </div>
            </template>
            <span
              class="stat-unscheduled"
              :title="`未排期任务 ${unscheduledLoad.count} 条（无计划开始时间）`"
            >
              | 未排期 <strong>{{ unscheduledLoad.count }}</strong>
            </span>
          </ElTooltip>
          <!-- P5 §12 受影响订单：现有 KPI 行内追加；0 新面板 -->
          <ElTooltip v-if="impactedTaskIds.length > 0" placement="bottom" :show-after="100">
            <template #content>
              <div class="impacted-tooltip">
                <div class="impacted-tooltip-title">
                  受设备故障影响的任务（{{ impactedTaskIds.length }} 个）：
                </div>
                <ul class="impacted-tooltip-list">
                  <li v-for="t in impactedTaskList.slice(0, 10)" :key="t.taskId">
                    <code>{{ t.taskNo }}</code>
                    <span class="impacted-tooltip-res">@ {{ t.resourceCode }}</span>
                  </li>
                  <li v-if="impactedTaskList.length > 10" class="impacted-tooltip-more">
                    …还有 {{ impactedTaskList.length - 10 }} 个
                  </li>
                </ul>
              </div>
            </template>
            <span
              class="stat-impacted"
              :class="{ 'is-active': filterImpactedOnly }"
              role="button"
              :title="filterImpactedOnly ? '点击取消只看受影响' : '点击只看受影响任务'"
              @click="filterImpactedOnly = !filterImpactedOnly"
            >
              <Icon icon="vi-mdi:alert-circle" />
              | 受影响 <strong>{{ impactedTaskIds.length }}</strong>
            </span>
          </ElTooltip>
        </span>

        <div v-show="viewTab === 'gantt'" class="gantt-legend" aria-hidden="true">
          <span v-for="item in legendItems" :key="item.label" class="legend-item">
            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}
          </span>
        </div>
      </div>
    </div>

    <!-- ===== 甘特图 =====
         软拖动（4号位文档第 6 节最后一句 + 第 24 节）：
         - VIEWER 角色 :readonly=true，整图不可拖
         - PMC+ 角色   :readonly=false，任务条可拖（lane 行仍然 task.readonly=true）
         - 拖完不直接写库：DhxGantt wrapper 的 onBeforeTaskChanged 永远 return false → 自动回滚位置
         - 然后 emit taskDragEnd → 这里弹确认 → 走 runApi.triggerReschedule（runType=LOCAL_RESCHEDULE）→ Candidate 页 -->
    <div v-show="viewTab === 'gantt'" class="gantt-area" @dblclick.capture="handleAreaDblClick">
      <DhxGantt
        ref="ganttRef"
        :tasks="dhxTasks"
        :readonly="!canManualReschedule"
        :allow-resize="false"
        :allow-progress="false"
        :scale-unit="dateRange.scaleUnit"
        :date-from="dateRange.from"
        :date-to="dateRange.to"
        :show-compare="false"
        :show-actual="false"
        :highlight-task-ids="impactedTaskIds"
        :load-bands="loadBands"
        :qty-unit="sharedQtyUnit"
        @task-click="handleTaskClick"
        @task-dbl-click="handleTaskDblClick"
        @task-context-menu="handleTaskContextMenu"
        @task-drag-end="handleTaskDragged"
        @task-drag-rejected="handleTaskDragRejected"
        @near-bottom="handleGanttNearBottom"
        @zoom-change="onZoomChange"
      />
    </div>

    <!-- ===== 资源负荷视图（行=资源 × 列=时间桶，色深=负荷量）=====
         数据源 = 5号位 `/buckets`（资源级聚合）。
         ⚠️ 无产能口径 ⇒ 只呈现负荷量、不标超载（见 loadHeatmap 注释）。 -->
    <div v-show="viewTab === 'load'" class="load-pane">
      <div class="load-note">
        深浅＝该时间桶内的任务数（资源级聚合，不受甘特条已加载条数影响）。
        <span class="load-note-warn">暂无产能口径，暂不标超载。</span>
      </div>
      <ElEmpty
        v-if="!loadHeatmap.columns.length"
        description="当前时间窗内没有负荷数据"
        :image-size="60"
      />
      <div v-else class="load-grid-wrap">
        <div
          class="load-grid"
          :style="{
            gridTemplateColumns: `220px repeat(${loadHeatmap.columns.length}, minmax(48px, 1fr))`
          }"
        >
          <div
            v-for="cell in loadGridCells"
            :key="cell.key"
            class="load-cell"
            :class="
              cell.kind === 'value'
                ? 'load-value'
                : cell.kind === 'label'
                  ? 'load-row-label'
                  : 'load-head'
            "
            :style="cell.style"
            :title="cell.title"
          >
            <template v-if="cell.kind === 'label'">
              <span class="load-row-name">{{ cell.text }}</span>
              <span v-if="cell.sub" class="load-row-sub">{{ cell.sub }}</span>
            </template>
            <template v-else>{{ cell.text }}</template>
          </div>
        </div>
      </div>
    </div>

    <!-- ===== 任务清单（今日 / 未来）：冻结 09 §六 / 01:1914 =====
         与甘特时间轴同窗；列表只展示后端已给字段，MES 下发/执行状态未接入不伪造 -->
    <div v-show="viewTab === 'tasks'" class="task-list-pane">
      <ElEmpty v-if="!taskListTotal" :description="taskListEmptyText" :image-size="60" />
      <template v-else>
        <div class="task-list-head">
          <span>任务清单（{{ windowLabel }}）</span>
          <!-- 导出按钮跟着任务清单走：导出内容就是这张表（与清单逐行一致） -->
          <ElButton
            size="small"
            plain
            title="导出当前时间窗内的任务清单（CSV，与任务清单页逐行一致）"
            @click="exportTaskList"
          >
            <Icon icon="vi-ep:download" />
            导出清单
          </ElButton>
        </div>
        <ElTable :data="taskListPaged" size="small" border max-height="520" style="width: 100%">
          <ElTableColumn prop="taskNo" label="任务号" min-width="150" show-overflow-tooltip />
          <ElTableColumn label="订单号" min-width="140" show-overflow-tooltip>
            <template #default="{ row }">{{ row.orderNo ?? '—' }}</template>
          </ElTableColumn>
          <ElTableColumn label="物料" min-width="160" show-overflow-tooltip>
            <template #default="{ row }">{{ row.materialCode ?? '—' }}</template>
          </ElTableColumn>
          <ElTableColumn prop="operationCode" label="工序" width="100" />
          <!-- 任务清单信息补全（5号位 2026-11-05 r14043/14044）：部门名 / 阶段 / 生产指示号
               缺键=后端无值 ⇒ 显示「—」（旧版本接口不会带这三键） -->
          <ElTableColumn label="部门" min-width="160" show-overflow-tooltip>
            <template #default="{ row }">{{ row.departmentName ?? '—' }}</template>
          </ElTableColumn>
          <ElTableColumn label="阶段" width="110" show-overflow-tooltip>
            <template #default="{ row }">{{ row.stageCode ?? '—' }}</template>
          </ElTableColumn>
          <ElTableColumn label="生产指示号" min-width="150" show-overflow-tooltip>
            <template #default="{ row }">{{ row.mtS_InstructionNo ?? '—' }}</template>
          </ElTableColumn>
          <ElTableColumn label="资源" min-width="140" show-overflow-tooltip>
            <template #default="{ row }">
              {{ ganttResourceMap.get(row.resourceId ?? -1)?.resourceCode ?? '—' }}
            </template>
          </ElTableColumn>
          <!-- 人工槽：有 manualSlotId 才是人工作业行（与设备资源互斥）；内部 id 不上列表 -->
          <ElTableColumn label="人工槽" width="90" align="center">
            <template #default="{ row }">
              <span v-if="row.manualSlotId != null">人工</span>
              <span v-else class="task-list-muted">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="计划开始" width="160">
            <template #default="{ row }">{{
              row.plannedStartTime ? formatUtcDateTimeSec(row.plannedStartTime) : '—'
            }}</template>
          </ElTableColumn>
          <ElTableColumn label="计划结束" width="160">
            <template #default="{ row }">{{
              row.plannedEndTime ? formatUtcDateTimeSec(row.plannedEndTime) : '—'
            }}</template>
          </ElTableColumn>
          <ElTableColumn label="状态" width="100">
            <template #default="{ row }">{{
              TASK_STATUS_LABELS[row.status] ?? row.status
            }}</template>
          </ElTableColumn>
          <!-- MES 工单号（源=Task.MESWorkOrderNo，JSON 键 mesWorkOrderNo；5号位 2026-11-05 B2①）
               缺键 = 尚未建单 ⇒ 显示「—」，不编造工单号 -->
          <ElTableColumn label="MES 工单号" min-width="160" show-overflow-tooltip>
            <template #default="{ row }">
              <code v-if="row.mesWorkOrderNo" class="code-tag">{{ row.mesWorkOrderNo }}</code>
              <span v-else class="task-list-muted" title="该任务尚未关联 MES 工单">—</span>
            </template>
          </ElTableColumn>
          <!-- 下发状态（源=TaskDispatch.DispatchStatus，JSON 键 dispatchStatus；5号位 2026-11-05 B2②）
               缺键 = 没有下发记录 = 未下发；值域由写侧决定，后端自由字符串 ⇒ 原样展示 -->
          <ElTableColumn label="下发状态" width="120">
            <template #default="{ row }">
              <span v-if="row.dispatchStatus">{{ row.dispatchStatus }}</span>
              <span v-else class="task-list-muted" title="没有 MES 下发记录">未下发</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="客户交期" width="110">
            <template #default="{ row }">{{
              row.customerDueDate ? formatUtcDate(row.customerDueDate) : '—'
            }}</template>
          </ElTableColumn>
          <ElTableColumn label="延期时长" width="100" align="right">
            <template #default="{ row }">{{
              row.delayHours != null ? row.delayHours.toFixed(1) + ' h' : '—'
            }}</template>
          </ElTableColumn>
          <ElTableColumn label="主要原因" min-width="180" show-overflow-tooltip>
            <template #default="{ row }">
              <span v-if="primaryDelayReasonText(row)">{{ primaryDelayReasonText(row) }}</span>
              <span v-else class="task-list-muted" title="后端未下发延期原因事实">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="延期" width="80" align="center">
            <template #default="{ row }">
              <ElTag v-if="row.isDelayed" type="danger" size="small">延期</ElTag>
              <span v-else class="task-list-muted">-</span>
            </template>
          </ElTableColumn>
        </ElTable>
        <div class="task-list-pager">
          <ElPagination
            v-model:current-page="taskListPage"
            :page-size="TASK_LIST_PAGE_SIZE"
            :total="taskListTotal"
            layout="total, prev, pager, next, jumper"
            small
            background
          />
        </div>
      </template>
    </div>

    <!-- ===== P3：双击任务条 → 轻量浮窗（Task 摘要 + 复制 TaskNo） ===== -->
    <div
      v-if="dblClickPopover.visible && dblClickPopover.task"
      class="aps-floating-popover"
      :style="{ left: dblClickPopover.x + 'px', top: dblClickPopover.y + 'px' }"
    >
      <div class="popover-arrow"></div>
      <div class="popover-title">
        <Icon icon="vi-ep:pointer" class="popover-icon" />
        <span class="popover-taskno">{{ dblClickPopover.task.taskNo }}</span>
        <ElTag size="small" type="info" effect="plain">
          {{ dblClickPopover.task.domainKey }}
        </ElTag>
      </div>
      <ElDivider class="popover-divider" />
      <div class="popover-row">
        <span class="row-label">订单号：</span>
        <span class="row-val">
          {{ dblClickPopover.task.orderNo ?? '—' }}
        </span>
      </div>
      <div class="popover-row">
        <span class="row-label">计划开始：</span>
        <span class="row-val">
          {{ formatDate(dblClickPopover.task.plannedStartTime) }}
        </span>
      </div>
      <div class="popover-row">
        <span class="row-label">状态：</span>
        <ElTag size="small" :type="statusTagType(dblClickPopover.task.status)">
          {{ TASK_STATUS_LABELS[dblClickPopover.task.status] ?? dblClickPopover.task.status }}
        </ElTag>
      </div>
      <ElDivider class="popover-divider" />
      <div class="popover-actions">
        <ElButton size="small" type="primary" plain @click="onDblClickCopy">
          <Icon icon="vi-ep:copy-document" /> 复制 TaskNo
        </ElButton>
        <ElButton size="small" @click="openDetailFromDblClickPopover">
          <Icon icon="vi-ep:view" /> 查看完整详情
        </ElButton>
      </div>
    </div>

    <!-- ===== P3：右键任务条 → 完整菜单（5 个动作） ===== -->
    <div
      v-if="contextMenu.visible && contextMenu.task"
      class="aps-ctx-menu"
      :style="{ left: contextMenu.x + 'px', top: contextMenu.y + 'px' }"
      @click.stop
    >
      <div class="ctx-menu-header">
        <Icon icon="vi-ep:pointer" />
        <span>{{ contextMenu.task.taskNo }}</span>
      </div>
      <ul class="ctx-menu-list">
        <li class="ctx-menu-item" @click="onContextMenuAction('detail')">
          <Icon icon="vi-ep:view" /> 查看任务详情
        </li>
        <li class="ctx-menu-item" @click="onContextMenuAction('copyTaskNo')">
          <Icon icon="vi-ep:copy-document" /> 复制 TaskNo
          <code class="ctx-menu-code">{{ contextMenu.task.taskNo }}</code>
        </li>
        <li
          class="ctx-menu-item"
          :class="{ 'is-disabled': !contextMenu.task.orderNo }"
          :title="contextMenu.task.orderNo ? '' : '该任务无 OrderNo'"
          @click="onContextMenuAction('copyOrderNo')"
        >
          <Icon icon="vi-ep:document" /> 复制 OrderNo
          <code v-if="contextMenu.task.orderNo" class="ctx-menu-code">
            {{ contextMenu.task.orderNo }}
          </code>
          <span v-else class="ctx-menu-empty">—</span>
        </li>
        <li
          class="ctx-menu-item"
          :class="{ 'is-disabled': !contextMenu.task.orderNo }"
          :title="contextMenu.task.orderNo ? '' : '该任务无 OrderNo'"
          @click="onContextMenuAction('goOrder')"
        >
          <Icon icon="vi-ep:position" /> 跳转到订单页
          <Icon icon="vi-ep:arrow-right" class="ctx-menu-arrow" />
        </li>
        <li class="ctx-menu-item" @click="onContextMenuAction('goPi')">
          <Icon icon="vi-ep:data-board" /> 跳转到 PI 页
          <Icon icon="vi-ep:arrow-right" class="ctx-menu-arrow" />
        </li>
      </ul>
    </div>

    <!-- ===== 任务详情 Drawer（自实现 v-if + Transition + Teleport，避开 Element Plus append-to-body + slot 的 click 不触发 bug） =====
         设计要点：
         1. 不依赖 ElDrawer：自渲染 panel，自己捕获 click，绝对不会被全局 Header 用户头像遮挡
         2. mask 仍设 pointer-events: none，让双击任务条能穿透到 Gantt 任务条（drawerVisible 由 handleAreaDblClick 先置 false 再触发 popover）
         3. Esc 关闭由 handleGlobalKey 统一处理（已有）— 只需把 drawerVisible 也置 false
         4. 关闭按钮是原生 <button>，不依赖 Element Plus 的 click 事件链路 -->
    <Teleport to="body">
      <Transition name="aps-drawer-slide">
        <div
          v-if="drawerVisible"
          class="aps-task-drawer-mask"
          aria-hidden="false"
          @click.self="closeDrawer"
        >
          <div class="aps-task-drawer" role="dialog" aria-label="任务详情" @click.stop>
            <!-- 顶部 header：自渲染，绝对不会被全局 Header 遮挡 -->
            <div class="aps-drawer-header">
              <span class="aps-drawer-title">
                <Icon icon="vi-ep:tickets" class="aps-drawer-title-icon" />
                任务详情
                <ElTag
                  v-if="selectedTask"
                  size="small"
                  type="info"
                  effect="plain"
                  class="aps-drawer-taskno"
                >
                  {{ selectedTask.taskNo }}
                </ElTag>
              </span>
              <button
                type="button"
                class="aps-drawer-close-btn"
                aria-label="关闭 Drawer"
                @click="closeDrawer"
              >
                <Icon icon="vi-ep:close" />
                <span>关闭</span>
              </button>
            </div>

            <!-- Drawer 主体（可滚动） -->
            <div class="aps-drawer-body">
              <template v-if="selectedTask">
                <div class="detail-section">
                  <ElDescriptions :column="1" border size="small">
                    <ElDescriptionsItem label="任务编号">{{
                      selectedTask.taskNo
                    }}</ElDescriptionsItem>
                    <ElDescriptionsItem label="排程域">
                      <ElTag size="small" effect="plain">{{ selectedTask.domainKey }}</ElTag>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="主订单">{{
                      selectedTask.orderNo ?? '-'
                    }}</ElDescriptionsItem>
                    <ElDescriptionsItem label="物料">{{
                      selectedTask.materialName ?? '-'
                    }}</ElDescriptionsItem>
                    <ElDescriptionsItem label="工序">
                      {{ selectedTask.operationCode }} (Seq {{ selectedTask.operationSeq }})
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="数量">
                      <span class="qty-cell">
                        <span class="qty-net">
                          净需求 <strong>{{ selectedTask.netQty }}</strong> {{ selectedTask.uom }}
                        </span>
                        <!-- 2026-10-17：后端恒返 null（ScheduleQueryService.cs:158 CAST(NULL AS ...)），
                             拿不到就不展示这半段，避免出现「计划加工」后面空着、或被误读成"没有良率补偿" -->
                        <template v-if="selectedTask.plannedProcessQty != null">
                          <span class="qty-arrow">→</span>
                          <span class="qty-plan">
                            计划加工 <strong>{{ selectedTask.plannedProcessQty }}</strong>
                            {{ selectedTask.uom }}
                          </span>
                        </template>
                        <ElTooltip
                          v-if="selectedTask.plannedProcessQty != null"
                          :content="`计划加工 = 净需求 ÷ 良率（默认 95%）；两者不可混用`"
                          placement="top"
                        >
                          <ElTag size="small" type="info" effect="plain" style="margin-left: 6px">
                            说明
                          </ElTag>
                        </ElTooltip>
                      </span>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="计划开始">
                      {{ selectedTask.plannedStartTime }}
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="计划结束">
                      {{ selectedTask.plannedEndTime }}
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="状态">
                      <ElTag
                        :color="STATUS_COLORS[sanitizeStatus(selectedTask.status)]"
                        size="small"
                        style="color: #fff"
                      >
                        {{ TASK_STATUS_LABELS[sanitizeStatus(selectedTask.status)] }}
                      </ElTag>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="客户交期">
                      {{ formatUtcDate(selectedTask.customerDueDate) }}
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="是否延期">
                      <ElTag :type="selectedTask.isDelayed ? 'danger' : 'success'" size="small">
                        {{ selectedTask.isDelayed ? '是' : '否' }}
                      </ElTag>
                      <!-- 延期时长取后端 `delayHours`（自然小时：交日零点 → 计划完工），
                           不在前端用交期与完工时间相减重算 -->
                      <span v-if="selectedTask.delayHours != null" class="delay-hours">
                        延 {{ selectedTask.delayHours.toFixed(1) }} 小时
                      </span>
                    </ElDescriptionsItem>
                    <!-- 关键路径三态（5号位 2026-11-05 B5 读侧已落码）：
                         true=判定为是 / false=判定为否 / 缺键=未判定（不再拿恒 false 冒充「不是关键路径」） -->
                    <ElDescriptionsItem label="关键路径">
                      <ElTag
                        v-if="selectedTask.isCriticalPath === true"
                        type="danger"
                        size="small"
                        effect="dark"
                      >
                        关键路径
                      </ElTag>
                      <ElTag
                        v-else-if="selectedTask.isCriticalPath === false"
                        type="info"
                        size="small"
                        effect="plain"
                      >
                        非关键路径
                      </ElTag>
                      <span v-else class="muted-text">未判定</span>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="MES 工单号">
                      <code v-if="selectedTask.mesWorkOrderNo" class="code-tag">{{
                        selectedTask.mesWorkOrderNo
                      }}</code>
                      <span v-else class="muted-text">未建单</span>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="下发状态">
                      <span v-if="selectedTask.dispatchStatus">{{
                        selectedTask.dispatchStatus
                      }}</span>
                      <span v-else class="muted-text">未下发</span>
                    </ElDescriptionsItem>
                    <ElDescriptionsItem label="换型时间来源">
                      <ElTag
                        v-if="selectedSetupSourceMeta"
                        :type="selectedSetupSourceMeta.tag"
                        size="small"
                        effect="dark"
                      >
                        {{ selectedSetupSourceMeta.label }}
                      </ElTag>
                      <ElTooltip
                        v-if="selectedTask.setupSource === 'SETUP_RULE_MISSING_ZERO_FALLBACK'"
                        content="有前后产品但没有换型规则 → 按 0 分钟兜底，属数据质量问题，已进入「缺失规则查询」"
                        placement="top"
                      >
                        <ElTag size="small" type="info" effect="plain" style="margin-left: 6px">
                          兜底
                        </ElTag>
                      </ElTooltip>
                      <span v-else-if="!selectedTask.setupSource">-</span>
                      <ElTooltip
                        v-if="selectedTask.setupSource === 'INITIAL_SETUP_STATE'"
                        content="没有上一产品（班头首单 / 冷启动）按 0 分钟计，属正常生产事实，不是数据质量问题"
                        placement="top"
                      >
                        <ElTag size="small" type="info" effect="plain" style="margin-left: 6px">
                          说明
                        </ElTag>
                      </ElTooltip>
                    </ElDescriptionsItem>
                  </ElDescriptions>

                  <!-- Section 十一：异常与原因解释 — 根因展示（4号位文档第 11 节硬约束）
                       文档原文："页面必须优先显示根因...不要只显示 DUE_DATE_RISK。
                       数据问题与排程问题要分开显示。"
                       根因类型（reasonCode）来自 ScheduleExplanationFact，4号位只展示不计算。 -->
                  <div class="explanation-section">
                    <div class="explanation-title">
                      <Icon icon="vi-mdi:comment-question-outline" />
                      原因解释
                      <ElTooltip
                        content="优先展示具体根因（如采购料到货晚 / 设备无产能 / 上游工序完成晚 / 固定任务占用关键窗口等），并按「数据问题」与「排程问题」分开列出。"
                        placement="top"
                      >
                        <ElIcon class="explanation-hint">
                          <Icon icon="vi-mdi:information-outline" />
                        </ElIcon>
                      </ElTooltip>
                    </div>

                    <!-- 无任何根因 -->
                    <div
                      v-if="explanationRenderedReasonCount === 0 && !selectedTask.isDelayed"
                      class="explanation-empty"
                    >
                      <Icon icon="vi-mdi:check-circle-outline" />
                      当前任务无延期根因
                    </div>

                    <!-- 分类渲染：先数据问题，再排程问题（文档第 11 节第三段硬约束） -->
                    <template v-else>
                      <!-- 数据问题 -->
                      <div
                        v-if="explanationReasonsByCategory.DATA_ISSUE.length > 0"
                        class="explanation-category is-data"
                      >
                        <div class="explanation-category-title">
                          <Icon icon="vi-mdi:database-alert" />
                          数据问题（{{ explanationReasonsByCategory.DATA_ISSUE.length }}）
                          <ElTooltip :content="ISSUE_CATEGORY_DESC.DATA_ISSUE" placement="top">
                            <ElIcon class="explanation-hint">
                              <Icon icon="vi-mdi:information-outline" />
                            </ElIcon>
                          </ElTooltip>
                        </div>
                        <ul class="explanation-reason-list">
                          <li
                            v-for="(r, idx) in explanationReasonsByCategory.DATA_ISSUE"
                            :key="`data-${idx}`"
                            class="explanation-reason-item"
                          >
                            <Icon :icon="reasonIcon(r.reasonCode)" class="reason-type-icon" />
                            <span class="reason-type-label">
                              {{ reasonLabelOf(r.reasonCode) }}
                            </span>
                            <ElTag
                              size="small"
                              :type="SEVERITY_TAG_TYPE[r.severity]"
                              effect="plain"
                            >
                              {{ SEVERITY_LABEL[r.severity] }}
                            </ElTag>
                            <!-- 订单级事实：后端按 TaskId 透传到该单每个任务（5号位 11-05 Q2-a）-->
                            <ElTag
                              v-if="r.objectType === 'ORDER'"
                              size="small"
                              type="info"
                              effect="plain"
                              title="这条原因挂在订单上（订单级），不是该任务独有的"
                            >
                              订单级
                            </ElTag>
                            <span v-if="r.description" class="reason-desc">{{
                              r.description
                            }}</span>
                            <span v-if="r.relatedObjectRef" class="reason-ref">
                              <code class="code-tag">{{ r.relatedObjectRef }}</code>
                            </span>
                            <span v-if="r.impactHours" class="reason-impact">
                              延 {{ r.impactHours }}h
                            </span>
                          </li>
                        </ul>
                      </div>

                      <!-- 排程问题 -->
                      <div
                        v-if="explanationReasonsByCategory.SCHEDULING_ISSUE.length > 0"
                        class="explanation-category is-scheduling"
                      >
                        <div class="explanation-category-title">
                          <Icon icon="vi-mdi:chart-timeline" />
                          排程问题（{{ explanationReasonsByCategory.SCHEDULING_ISSUE.length }}）
                          <ElTooltip
                            :content="ISSUE_CATEGORY_DESC.SCHEDULING_ISSUE"
                            placement="top"
                          >
                            <ElIcon class="explanation-hint">
                              <Icon icon="vi-mdi:information-outline" />
                            </ElIcon>
                          </ElTooltip>
                        </div>
                        <ul class="explanation-reason-list">
                          <li
                            v-for="(r, idx) in explanationReasonsByCategory.SCHEDULING_ISSUE"
                            :key="`sched-${idx}`"
                            class="explanation-reason-item"
                          >
                            <Icon :icon="reasonIcon(r.reasonCode)" class="reason-type-icon" />
                            <span class="reason-type-label">
                              {{ reasonLabelOf(r.reasonCode) }}
                            </span>
                            <ElTag
                              size="small"
                              :type="SEVERITY_TAG_TYPE[r.severity]"
                              effect="plain"
                            >
                              {{ SEVERITY_LABEL[r.severity] }}
                            </ElTag>
                            <!-- 订单级事实：后端按 TaskId 透传到该单每个任务（5号位 11-05 Q2-a）-->
                            <ElTag
                              v-if="r.objectType === 'ORDER'"
                              size="small"
                              type="info"
                              effect="plain"
                              title="这条原因挂在订单上（订单级），不是该任务独有的"
                            >
                              订单级
                            </ElTag>
                            <span v-if="r.description" class="reason-desc">{{
                              r.description
                            }}</span>
                            <span v-if="r.relatedObjectRef" class="reason-ref">
                              <code class="code-tag">{{ r.relatedObjectRef }}</code>
                            </span>
                            <span v-if="r.impactHours" class="reason-impact">
                              延 {{ r.impactHours }}h
                            </span>
                          </li>
                        </ul>
                      </div>

                      <!-- 其他根因：后端未给 issueCategory（r13982 起本期缺键）时，根因仍要出行，
                           不能因为分不了类就把整块根因区渲染成空白 -->
                      <div
                        v-if="explanationReasonsByCategory.OTHER.length > 0"
                        class="explanation-category is-other"
                      >
                        <div class="explanation-category-title">
                          <Icon icon="vi-mdi:help-circle" />
                          其他根因（{{ explanationReasonsByCategory.OTHER.length }}）
                          <ElTooltip :content="OTHER_CATEGORY_DESC" placement="top">
                            <ElIcon class="explanation-hint">
                              <Icon icon="vi-mdi:information-outline" />
                            </ElIcon>
                          </ElTooltip>
                        </div>
                        <ul class="explanation-reason-list">
                          <li
                            v-for="(r, idx) in explanationReasonsByCategory.OTHER"
                            :key="`other-${idx}`"
                            class="explanation-reason-item"
                          >
                            <Icon :icon="reasonIcon(r.reasonCode)" class="reason-type-icon" />
                            <span class="reason-type-label">
                              {{ reasonLabelOf(r.reasonCode) }}
                            </span>
                            <ElTag
                              size="small"
                              :type="SEVERITY_TAG_TYPE[r.severity]"
                              effect="plain"
                            >
                              {{ SEVERITY_LABEL[r.severity] }}
                            </ElTag>
                            <!-- 订单级事实：后端按 TaskId 透传到该单每个任务（5号位 11-05 Q2-a）-->
                            <ElTag
                              v-if="r.objectType === 'ORDER'"
                              size="small"
                              type="info"
                              effect="plain"
                              title="这条原因挂在订单上（订单级），不是该任务独有的"
                            >
                              订单级
                            </ElTag>
                            <span v-if="r.description" class="reason-desc">{{
                              r.description
                            }}</span>
                            <span v-if="r.relatedObjectRef" class="reason-ref">
                              <code class="code-tag">{{ r.relatedObjectRef }}</code>
                            </span>
                            <span v-if="r.impactHours" class="reason-impact">
                              延 {{ r.impactHours }}h
                            </span>
                          </li>
                        </ul>
                      </div>

                      <!-- 仅有 isDelayed 但没具体根因：如实说明「后端没有任务级原因」，
                           不谎称走了 DUE_DATE_RISK 兜底（5号位 2026-10-17 R5：解释事实目前落在订单级，
                           任务级仅 63 行，查询层起填解决不了） -->
                      <div
                        v-if="explanationRenderedReasonCount === 0 && selectedTask.isDelayed"
                        class="explanation-no-detail"
                      >
                        <Icon icon="vi-mdi:clock-alert-outline" />
                        当前任务已延期，但系统尚未提供任务级延期原因。
                        可到「排程解释」页按订单查看订单级原因。
                      </div>
                    </template>
                  </div>

                  <!-- Section 十六：MES 下发资格（取 mesEligibilityOf，bool 契约已归一为「未判定」） -->
                  <ElAlert
                    :type="MES_ELIGIBLE_TYPE[mesEligibilityOf(selectedTask)]"
                    :closable="false"
                    show-icon
                    class="mes-eligible-alert"
                  >
                    <template #title>
                      <span class="mes-eligible-title">
                        MES 下发资格：
                        <strong>{{ MES_ELIGIBLE_LABEL[mesEligibilityOf(selectedTask)] }}</strong>
                      </span>
                    </template>
                    <template v-if="mesEligibilityOf(selectedTask) === 'INELIGIBLE'">
                      <ul class="mes-reason-list">
                        <li v-for="r in selectedTask.mesIneligibleReasons ?? []" :key="r">
                          {{ r }}
                        </li>
                      </ul>
                      <div class="mes-note">页面仅展示与调用下发服务，不直接写 MES 接口表。</div>
                    </template>
                    <template v-else-if="mesEligibilityOf(selectedTask) === 'ELIGIBLE'">
                      已满足下发条件，可通过 MES Dispatch 服务下发。
                    </template>
                    <template v-else>
                      后端尚未接入 MES 资格判定，页面不显示「可下发 / 不可下发」。
                      待资格判定落地后，这里会按真实结果展示。
                    </template>
                  </ElAlert>

                  <!-- U14：跨 Domain 共享设备阻挡 —— 展示位已撤（B4）。
                       5号位 2026-11-05 答复：判定语义无冻结明文、`Pegging.IsCrossDomain` 是边属性非阻挡判定，
                       V1 不做（字段恒缺键）⇒ 与 B6/B7/B8 同判撤除，不留永不出现的告警位。 -->

                  <!-- U04：TaskShare 一个 Task 承接多 Order -->
                  <div
                    v-if="selectedTask.taskShare && selectedTask.taskShare.length > 1"
                    class="taskshare-section"
                  >
                    <div class="taskshare-title">
                      <Icon icon="vi-mdi:share-variant" />
                      订单份额 — 一个任务承接 {{ selectedTask.taskShare.length }} 个订单
                    </div>
                    <ElTable :data="selectedTask.taskShare" size="small" border>
                      <ElTableColumn prop="orderNo" label="订单号" width="160">
                        <template #default="scope">
                          <code v-if="scope?.row" class="code-tag">{{ scope.row.orderNo }}</code>
                        </template>
                      </ElTableColumn>
                      <ElTableColumn prop="shareQty" label="份额数量" width="120" align="right">
                        <template #default="scope">
                          <span v-if="scope?.row"
                            >{{ scope.row.shareQty }} {{ selectedTask.uom }}</span
                          >
                        </template>
                      </ElTableColumn>
                      <ElTableColumn label="占比" min-width="180">
                        <template #default="scope">
                          <div v-if="scope?.row" class="share-bar-wrap">
                            <div
                              class="share-bar"
                              :style="{
                                width: ((scope.row.shareRatio ?? 0) * 100).toFixed(1) + '%'
                              }"
                            ></div>
                            <span class="share-ratio"
                              >{{ ((scope.row.shareRatio ?? 0) * 100).toFixed(1) }}%</span
                            >
                          </div>
                        </template>
                      </ElTableColumn>
                    </ElTable>
                  </div>
                  <!-- 缺数据只陈述「未提供」：后端未下发 TaskShare 时无法断言该任务就是单订单 -->
                  <div v-else class="taskshare-section">
                    <span class="muted-text">
                      <Icon icon="vi-mdi:link-variant" /> 未提供订单份额数据（TaskShare）
                    </span>
                  </div>

                  <!-- 注：U06「数量-时间拆分」整块已撤（5号位 2026-11-05 B8 裁决「不做、撤」）：
                       无载体表，冻结 01 §76 的分段属 Supply 跨域事实、不是 Task 内拆分 ⇒ 永久缺键。
                       不再留「等它有」的预期位，也不再渲染中性占位文案。 -->
                  <!-- Section 十二 / U20：设备故障影响（仅展示 + 建议；V1 不建设 PAUSE/RESUME 状态闭环，由 PMC 发起 LOCAL_RESCHEDULE） -->
                  <div
                    v-if="selectedResource?.unavailableWindows?.length"
                    class="resource-down-section"
                  >
                    <div class="resource-down-title">
                      <Icon icon="vi-mdi:tools" />
                      资源不可用窗口（{{ selectedResource.resourceCode }}）
                    </div>
                    <ElTable :data="selectedResource.unavailableWindows" size="small" border>
                      <ElTableColumn label="开始" width="170">
                        <template #default="scope">
                          <span v-if="scope?.row">{{ scope.row.from?.slice(0, 16) }}</span>
                        </template>
                      </ElTableColumn>
                      <ElTableColumn label="结束" width="170">
                        <template #default="scope">
                          <span v-if="scope?.row">{{ scope.row.to?.slice(0, 16) }}</span>
                        </template>
                      </ElTableColumn>
                      <ElTableColumn label="原因" min-width="200">
                        <template #default="scope">
                          <span v-if="scope?.row" class="muted-text">{{ scope.row.reason }}</span>
                        </template>
                      </ElTableColumn>
                      <ElTableColumn label="是否影响本任务" width="140" align="center">
                        <template #default="scope">
                          <ElTag
                            v-if="
                              scope?.row &&
                              resourceImpactsSelected.some(
                                (w) => w.from === scope.row.from && w.to === scope.row.to
                              )
                            "
                            type="warning"
                            size="small"
                          >
                            影响
                          </ElTag>
                          <span v-else class="muted-text">-</span>
                        </template>
                      </ElTableColumn>
                    </ElTable>
                    <div class="resource-down-note">
                      <Icon icon="vi-mdi:information-outline" />
                      排产只展示不可用窗口并提供重排入口，不会直接改动任务状态。
                    </div>

                    <!-- P1：基于设备故障建议重排（Section 12 入口）
                         - 仅在有 ≥1 个影响窗口时显示
                         - 走与 P0 同一 runApi.triggerReschedule（runType=LOCAL_RESCHEDULE），但 objectRefType=RESOURCE -->
                    <div
                      v-if="canManualReschedule && resourceImpactsSelected.length > 0"
                      class="resource-down-action"
                    >
                      <ElDivider />
                      <div class="resource-down-action-row">
                        <ElButton type="danger" @click="triggerRescheduleForResource">
                          <Icon icon="vi-mdi:tools" />
                          基于设备故障建议重排（{{ resourceImpactsSelected.length }} 个影响窗口）
                        </ElButton>
                        <ElTooltip
                          content="设备故障只做展示与建议重排，不会直接改动任务状态。提交后由计划员在候选版本对比页确认采用。"
                          placement="top"
                        >
                          <ElIcon class="action-hint">
                            <Icon icon="vi-mdi:information-outline" />
                          </ElIcon>
                        </ElTooltip>
                      </div>
                      <div class="resource-down-action-note">
                        将对资源 <strong>{{ selectedResource.resourceCode }}</strong> 所属排程域
                        (<ElTag size="small" effect="plain">{{ selectedResource.domainKey }}</ElTag
                        >) 发起重排，将重排该资源涉及的任务
                      </div>
                    </div>
                  </div>

                  <!-- 文档第 6 节最后一句 + §10A.2：人工调整单 Task 走「甘特图调整」入口 → Candidate → 确认 -->
                  <div v-if="canPlanRun" class="action-section">
                    <ElDivider />
                    <div class="action-row">
                      <ElButton type="warning" @click="triggerReschedule">
                        <Icon icon="vi-mdi:restart-alert" />
                        发起甘特图调整
                      </ElButton>
                      <ElTooltip
                        content="人工调整不会直接改动当前生效的计划：系统先算出一版候选计划，由计划员对比确认后才采用。整排程域等较大范围入口在工具栏「白天人工调整」下拉。"
                        placement="top"
                      >
                        <ElIcon class="action-hint">
                          <Icon icon="vi-mdi:information-outline" />
                        </ElIcon>
                      </ElTooltip>
                    </div>
                    <div class="action-note">
                      当前账号：<strong>{{
                        apsAuth.userInfo?.userName ?? apsAuth.userInfo?.userCode ?? '-'
                      }}</strong>
                      （角色：<span class="role-tag">{{
                        apsAuth.roles.map(roleLabelOf).join(' / ') || '无'
                      }}</span
                      >） · 触发后会跳到候选版本对比页，由计划员确认后才生效
                    </div>
                  </div>
                  <div v-else class="action-section">
                    <ElDivider />
                    <div class="action-note readonly-note">
                      <Icon icon="vi-mdi:lock-outline" />
                      当前账号没有手工调整计划的权限，无法发起重排。如需调整，请联系计划员操作。
                    </div>
                  </div>
                </div>
              </template>
              <template v-else>
                <div class="detail-empty">请选择任务查看详情</div>
              </template>
            </div>
          </div>
        </div>
      </Transition>
    </Teleport>

    <!-- ===== §10A 白天人工调整：4 个独立业务入口 Dialog =====
         冻结文档《APS_V1_4号位页面与业务操作开发实施包 v1.4》§十A（L595-672）：
         LOCAL/MANUAL 候选运行 → 统一出口 triggerBusinessEntry → 跳 Candidate 对比页确认
         （已订单提前 §10A.1 在 Order 页；本页承载 §10A.2-10A.5） -->
    <GanttAdjustDialog
      :visible="entryDialog === 'GANTT_ADJUSTMENT'"
      :submitting="entrySubmitting"
      :domain-options="BUSINESS_DOMAIN_OPTIONS"
      :tasks="filteredTasks"
      :preselected-task-ids="entryPreselectTaskIds"
      :preselect-target-time="entryPreselectTargetTime"
      :default-domain-key="entryDomainKey ?? currentDomainKey"
      @update:visible="(v) => (entryDialog = v ? 'GANTT_ADJUSTMENT' : null)"
      @submit="submitBusinessEntry"
    />

    <ResourceRescheduleDialog
      :visible="entryDialog === 'EQUIPMENT_FAILURE'"
      :submitting="entrySubmitting"
      trigger="EQUIPMENT_FAILURE"
      :domain-options="BUSINESS_DOMAIN_OPTIONS"
      :resources="ganttData?.resources ?? []"
      :preselected-resource-ids="entryPreselectResourceIds"
      :default-domain-key="entryDomainKey ?? currentDomainKey"
      @update:visible="(v) => (entryDialog = v ? 'EQUIPMENT_FAILURE' : null)"
      @submit="submitBusinessEntry"
    />

    <ResourceRescheduleDialog
      :visible="entryDialog === 'RESOURCE_CALENDAR_CHANGE'"
      :submitting="entrySubmitting"
      trigger="RESOURCE_CALENDAR_CHANGE"
      :domain-options="BUSINESS_DOMAIN_OPTIONS"
      :resources="ganttData?.resources ?? []"
      :preselected-resource-ids="entryPreselectResourceIds"
      :default-domain-key="entryDomainKey ?? currentDomainKey"
      @update:visible="(v) => (entryDialog = v ? 'RESOURCE_CALENDAR_CHANGE' : null)"
      @submit="submitBusinessEntry"
    />

    <DomainRescheduleDialog
      :visible="entryDialog === 'DOMAIN_MANUAL_RESCHEDULE'"
      :submitting="entrySubmitting"
      :domain-options="BUSINESS_DOMAIN_OPTIONS"
      :default-domain-key="entryDomainKey ?? currentDomainKey"
      @update:visible="(v) => (entryDialog = v ? 'DOMAIN_MANUAL_RESCHEDULE' : null)"
      @submit="submitBusinessEntry"
    />
  </div>
</template>

<style lang="less" scoped>
.aps-gantt-page {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 140px);
  padding: 0;
  overflow: hidden;
  background: #f5f7fa;
  gap: 12px;
}

/* 工具栏：单行流式排布（业务按钮跟在搜索框之后）
   （2026-11-05 用户要求：按钮不再独占一行，窄屏下少占一行） */
.toolbar {
  display: flex;
  align-items: center;
  padding: 8px 12px;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
  gap: 8px;

  .toolbar-left {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    flex: 1;
    min-width: 0;
  }

  .label {
    font-size: 12px;
    color: #64748b;
  }

  /* 当前时间窗标签（一律显示实际起止日） */
  .window-label {
    font-size: 12px;
    color: #475569;
    white-space: nowrap;
  }

  /* 关键词搜索框 */
  .gantt-search {
    width: 220px;
  }

  /* 搜索命中计数 */
  .search-hint {
    font-size: 12px;
    color: #2563eb;
    white-space: nowrap;
  }
}

/* 统计 KPI（视图切换行右侧：资源 / 泳道 / 各域任务数） */
.stat {
  margin: 0;
  font-size: 11px;
  color: #94a3b8;
  white-space: nowrap;

  strong {
    color: #1e293b;
  }

  .stat-domain {
    font-weight: 500;

    strong {
      font-family: 'Courier New', monospace;
      font-weight: 700;
    }
  }

  /* P5 §12：受影响 KPI 项（点切换只看受影响） */
  .stat-impacted {
    display: inline-flex;
    padding: 1px 8px;
    margin-left: 8px;
    font-size: 12px;
    color: #dc2626;
    cursor: pointer;
    background: #fef2f2;
    border: 1px solid #fecaca;
    border-radius: 10px;
    transition: all 0.12s ease;
    align-items: center;
    gap: 2px;

    strong {
      margin-left: 2px;
      font-family: 'Courier New', monospace;
      font-weight: 700;
    }

    &:hover {
      background: #fee2e2;
      border-color: #fca5a5;
    }

    &.is-active {
      color: #fff;
      background: #dc2626;
      border-color: #dc2626;

      strong {
        color: #fff;
      }
    }
  }

  /* 未排期任务（桶体 bucketStart 缺键 ⇒ 无计划开始时间） */
  .stat-unscheduled {
    display: inline-flex;
    padding: 1px 8px;
    margin-left: 8px;
    font-size: 12px;
    color: #b45309;
    background: #fffbeb;
    border: 1px solid #fde68a;
    border-radius: 10px;
    align-items: center;
    gap: 2px;

    strong {
      margin-left: 2px;
      font-family: 'Courier New', monospace;
      font-weight: 700;
    }
  }
}

/* P5 §12 Tooltip 内容 */
.impacted-tooltip {
  max-width: 280px;
  font-size: 12px;
  line-height: 1.6;
}

.impacted-tooltip-title {
  margin-bottom: 4px;
  font-weight: 600;
  color: #1e293b;
}

.impacted-tooltip-list {
  padding: 0;
  margin: 0;
  list-style: none;

  li {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 1px 0;

    code {
      padding: 0 4px;
      font-family: 'Courier New', monospace;
      font-size: 11px;
      color: #dc2626;
      background: #fef2f2;
      border-radius: 3px;
    }
  }
}

.impacted-tooltip-res {
  font-size: 11px;
  color: #64748b;
}

.impacted-tooltip-more {
  margin-top: 4px;
  font-style: italic;
  color: #94a3b8;
}

/* 视图切换（甘特图 / 任务清单） */
/* 视图切换行：Tab 与提示同一行，整体压到一行高 */
.view-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 26px;
  flex-wrap: nowrap;
}

/* 视图切换行右侧：统计 KPI + 图例（靠右对齐，不占图表高度） */
.view-bar-right {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 24px;
  margin-left: auto;
  min-width: 0;
  flex-wrap: nowrap;

  .gantt-legend {
    padding-left: 16px;
    overflow: hidden;
    border-left: 1px solid #e2e8f0;
  }
}

.view-tabs {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  background: #fff;
  border-radius: 6px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
}

.view-tab {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 12px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  font-size: 13px;
  line-height: 1.4;
  color: #475569;
  cursor: pointer;

  &:hover {
    color: #1e293b;
    background: #f1f5f9;
  }

  &.is-active {
    color: #2563eb;
    font-weight: 600;
    background: #eff6ff;
  }
}

.view-tab-count {
  padding: 0 5px;
  border-radius: 8px;
  background: #e2e8f0;
  font-size: 11px;
  font-weight: 400;
  color: #475569;
}

/* 行内短提示（错误 / 空态）：一行标题，详细说明挂 title 悬停 */
.view-bar-note {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  font-size: 12px;
  color: #64748b;
  cursor: help;

  &.is-error {
    color: #f56c6c;
  }
}

/* 甘特图区 */
.gantt-area {
  position: relative;
  flex: 1;
  min-height: 0;
  overflow: hidden;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);

  :deep(.dhx-gantt-root) {
    height: 100%;
  }
}

/* 图例：视图切换行右侧，紧跟在统计 KPI 之后（与知名排产系统的图例位置一致） */
.gantt-legend {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: nowrap;
  font-size: 11px;
  line-height: 16px;
  color: #475569;
  white-space: nowrap;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}

.legend-swatch {
  width: 10px;
  height: 10px;
  border-radius: 2px;
}

/* 详情 */
.detail-section {
  display: flex;
  padding: 0 4px;
  flex-direction: column;
  gap: 14px;
}

.detail-empty {
  padding: 40px 0;
  color: #94a3b8;
  text-align: center;
}

/* U05：净需求 vs 计划加工 */
.qty-cell {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.qty-net {
  color: #94a3b8;

  strong {
    font-weight: 600;
    color: #475569;
  }
}

.qty-arrow {
  font-weight: 600;
  color: #cbd5e1;
}

.qty-plan {
  color: #1e293b;

  strong {
    font-weight: 700;
    color: #1e293b;
  }
}

/* U04：TaskShare */
.taskshare-section {
  padding: 12px;
  margin-top: 4px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
}

.taskshare-title {
  display: flex;
  margin-bottom: 10px;
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
  align-items: center;
  gap: 6px;
}

/* 注：U06「数量-时间拆分」样式随展示位一并撤除（5号位 2026-11-05 B8「不做、撤」）。 */

/* Section 16：MES 下发资格 */
.mes-eligible-alert {
  margin-top: 4px;
}

/* Section 11：异常与原因解释 — 根因展示 */
.explanation-section {
  padding: 12px;
  background: linear-gradient(180deg, #fefce8 0%, #fef9c3 100%);
  border: 1px solid #fde68a;
  border-radius: 6px;
}

.explanation-title {
  display: flex;
  margin-bottom: 10px;
  font-size: 13px;
  font-weight: 600;
  color: #92400e;
  align-items: center;
  gap: 6px;

  .explanation-hint {
    font-size: 14px;
    color: #94a3b8;
    cursor: help;
  }
}

.explanation-empty {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: #059669;
}

.explanation-category {
  padding: 8px 10px;
  margin-top: 8px;
  background: #fff;
  border-left: 3px solid #94a3b8;
  border-radius: 4px;
}

.explanation-category.is-data {
  border-left-color: #d97706;
}

.explanation-category.is-scheduling {
  border-left-color: #6366f1;
}

.explanation-category.is-other {
  border-left-color: #64748b;
}

.explanation-category-title {
  display: flex;
  margin-bottom: 6px;
  font-size: 12px;
  font-weight: 600;
  color: #1e293b;
  align-items: center;
  gap: 6px;

  .explanation-hint {
    font-size: 13px;
    color: #94a3b8;
    cursor: help;
  }
}

.explanation-reason-list {
  padding: 0;
  margin: 0;
  list-style: none;
}

.explanation-reason-item {
  display: flex;
  padding: 4px 0;
  font-size: 12px;
  line-height: 1.5;
  color: #1e293b;
  border-bottom: 1px dashed #fde68a;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;

  &:last-child {
    border-bottom: none;
  }

  .reason-type-icon {
    font-size: 14px;
    color: #6366f1;
  }

  .reason-type-label {
    font-weight: 600;
  }

  .reason-desc {
    padding-left: 22px;
    font-size: 11px;
    color: #475569;
    flex-basis: 100%;
  }

  .reason-ref {
    margin-left: auto;
    font-size: 11px;
    color: #64748b;
  }

  .reason-impact {
    padding: 0 6px;
    font-size: 11px;
    font-weight: 600;
    color: #92400e;
    background: #fef3c7;
    border-radius: 3px;
  }
}

.explanation-no-detail {
  display: flex;
  padding: 8px 10px;
  margin-top: 8px;
  font-size: 12px;
  line-height: 1.5;
  color: #92400e;
  background: #fff7ed;
  border: 1px dashed #fdba74;
  border-radius: 4px;
  align-items: flex-start;
  gap: 6px;
}

/* 注：上游 Stage 阻挡样式随展示位一并撤除（5号位 2026-11-05 B6「不做」）。 */

.mes-eligible-title {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;

  strong {
    font-weight: 700;
  }
}

.mes-reason-list {
  padding-left: 20px;
  margin: 4px 0 0;
  font-size: 12px;
  color: #7f1d1d;

  li {
    margin: 2px 0;
  }
}

.mes-note {
  margin-top: 4px;
  font-size: 11px;
  color: #94a3b8;
}

/* Section 12 / U20：资源故障影响 */
.resource-down-section {
  padding: 12px;
  margin-top: 4px;
  background: #fee2e2;
  border: 1px solid #fecaca;
  border-radius: 6px;
}

.resource-down-title {
  display: flex;
  margin-bottom: 10px;
  font-size: 13px;
  font-weight: 600;
  color: #991b1b;
  align-items: center;
  gap: 6px;
}

.resource-down-note {
  display: inline-flex;
  margin-top: 8px;
  font-size: 11px;
  color: #7f1d1d;
  align-items: center;
  gap: 4px;
}

/* P1：设备故障建议重排 - 操作区（红色调，与上方故障区视觉一致） */
.resource-down-action {
  padding-top: 4px;
  margin-top: 10px;
}

.resource-down-action-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.resource-down-action-note {
  margin-top: 6px;
  font-size: 11px;
  line-height: 1.6;
  color: #7f1d1d;

  strong {
    font-weight: 700;
    color: #991b1b;
  }
}

/* Section 6 + Section 12 + Section 17 + 审核报告 P0-02/P0-03：LOCAL_RESCHEDULE 操作区 */
.action-section {
  padding-top: 4px;
  margin-top: 8px;
}

.action-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.action-hint {
  font-size: 16px;
  color: #94a3b8;
  cursor: help;
}

.action-note {
  margin-top: 6px;
  font-size: 11px;
  line-height: 1.6;
  color: #64748b;

  strong {
    color: #1e293b;
  }
}

.role-tag {
  display: inline-block;
  padding: 1px 6px;
  margin: 0 2px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  font-weight: 600;
  color: #3730a3;
  background: #e0e7ff;
  border-radius: 8px;
}

/* P2 角色徽章（P2 mock 角色切换器） */
.role-badge {
  display: inline-flex;
  padding: 4px 10px;
  font-size: 12px;
  letter-spacing: 0.5px;
  cursor: pointer;
  transition: transform 0.15s ease;
  align-items: center;
  gap: 4px;

  &:hover {
    transform: translateY(-1px);
  }
}

.role-caret {
  font-size: 10px;
  opacity: 0.7;
}

.role-hint {
  margin-left: 8px;
  font-size: 11px;
  color: #94a3b8;
}

.readonly-note {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: #94a3b8;
}

.muted-text {
  display: inline-flex;
  font-size: 12px;
  color: #94a3b8;
  align-items: center;
  gap: 4px;
}

.delay-hours {
  margin-left: 8px;
  font-size: 12px;
  color: #ef4444;
}

.share-bar-wrap {
  position: relative;
  display: flex;
  height: 18px;
  overflow: hidden;
  background: #e2e8f0;
  border-radius: 4px;
  align-items: center;
  gap: 8px;
}

.share-bar {
  height: 100%;
  background: linear-gradient(90deg, #3b82f6, #6366f1);
  border-radius: 4px;
}

.share-ratio {
  position: absolute;
  right: 8px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  font-weight: 600;
  color: #1e293b;
}

.code-tag {
  padding: 2px 6px;
  font-family: 'Courier New', monospace;
  font-size: 12px;
  color: #475569;
  background: #f1f5f9;
  border-radius: 3px;
}

/* 甘特任务条：U04 / U14 / U06 视觉标记 */
:deep(.gantt_task_line) {
  /* P4 软拖动：PMC+ 角色下任务条可拖，鼠标变 grab 提示 */
  cursor: grab;

  &:active {
    cursor: grabbing;
  }

  &.share-marked {
    border: 2px dashed #6366f1 !important;
    box-shadow: inset 0 0 0 2px rgb(99 102 241 / 15%);
  }

  &.domain-blocked {
    background-image: repeating-linear-gradient(
      45deg,
      transparent,
      transparent 4px,
      rgb(255 255 255 / 35%) 4px,
      rgb(255 255 255 / 35%) 8px
    ) !important;
    border: 2px solid #ef4444 !important;
    box-shadow: 0 0 0 1px #ef4444;
  }

  &.segment-bar {
    border: 1px dashed rgb(0 0 0 / 40%) !important;
    border-top-style: solid;
    border-bottom-style: solid;
    opacity: 0.85;
    box-shadow: inset 0 0 0 1px rgb(255 255 255 / 25%);
  }
}

:deep(.gantt_marker.cross_domain_block) {
  color: #fff !important;
  background: #ef4444 !important;
}

/* ===== P3 重写：自实现 Drawer（避开 Element Plus append-to-body + slot click bug） ===== */

/* mask：半透明视觉遮罩，但 pointer-events: none 让 dblclick 穿透到任务条
 * 用户在 mask 区域双击任务条会先关 drawer 再出 popover（handleAreaDblClick 已实现） */
.aps-task-drawer-mask {
  position: fixed;
  z-index: 2999;
  display: flex;
  pointer-events: none;
  background: rgb(15 23 42 / 18%);
  inset: 0;
  justify-content: flex-end;
}

/* panel：右侧抽屉本体，绝对不会被全局 Header 遮挡 */
.aps-task-drawer {
  position: relative;
  display: flex;
  width: 420px;
  height: 100%;
  max-width: 90vw;
  overflow: hidden;
  pointer-events: auto;
  background: #fff;
  box-shadow: -4px 0 24px rgb(15 23 42 / 18%);
  flex-direction: column;
}

/* 顶部 header：自渲染，绝对不会被全局 Header 遮挡 */
.aps-drawer-header {
  display: flex;
  width: 100%;
  padding: 14px 16px;
  background: #fff;
  border-bottom: 1px solid #e2e8f0;
  flex-shrink: 0;
  align-items: center;
  justify-content: space-between;
}

.aps-drawer-title {
  display: flex;
  min-width: 0;
  font-size: 15px;
  font-weight: 600;
  color: #1e293b;
  align-items: center;
  gap: 8px;
  flex: 1;
}

.aps-drawer-title-icon {
  color: #2563eb;
  flex-shrink: 0;
}

.aps-drawer-taskno {
  margin-left: 4px;
  font-family: 'Courier New', monospace;
}

/* 主体：可滚动 */
.aps-drawer-body {
  min-height: 0;
  padding: 16px;
  overflow-y: auto;
  flex: 1;
}

/* 自实现关闭按钮：原生 <button>，避免 Element Plus ElButton 的 click 事件丢失 */
.aps-drawer-close-btn {
  display: inline-flex;
  padding: 5px 12px;
  font-family: inherit;
  font-size: 13px;
  font-weight: 500;
  color: #1e293b;
  cursor: pointer;
  background: #fff;
  border: 1px solid #cbd5e1;
  border-radius: 4px;
  transition: all 0.15s ease;
  align-items: center;
  gap: 4px;

  &:hover {
    background: #f1f5f9;
    border-color: #94a3b8;
  }

  &:active {
    background: #e2e8f0;
  }
}

/* 抽屉滑入动画 */
.aps-drawer-slide-enter-active,
.aps-drawer-slide-leave-active {
  transition: opacity 0.22s ease;
}

.aps-drawer-slide-enter-active .aps-task-drawer,
.aps-drawer-slide-leave-active .aps-task-drawer {
  transition: transform 0.22s ease;
}

.aps-drawer-slide-enter-from,
.aps-drawer-slide-leave-to {
  opacity: 0;
}

.aps-drawer-slide-enter-from .aps-task-drawer,
.aps-drawer-slide-leave-to .aps-task-drawer {
  transform: translateX(100%);
}

/* ===== P3：双击任务条 → 轻量浮窗 ===== */
.aps-floating-popover {
  position: fixed;
  z-index: 3000;
  max-width: 360px;
  min-width: 280px;
  padding: 12px 14px;
  font-size: 13px;
  color: #1e293b;
  background: #fff;
  border-radius: 6px;
  box-shadow:
    0 4px 16px rgb(15 23 42 / 18%),
    0 0 0 1px rgb(15 23 42 / 6%);
  user-select: none;
}

.popover-arrow {
  position: absolute;
  top: -6px;
  left: 16px;
  width: 12px;
  height: 12px;
  background: #fff;
  transform: rotate(45deg);
  box-shadow: -1px -1px 1px rgb(15 23 42 / 4%);
}

.popover-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}

.popover-icon {
  color: #2563eb;
}

.popover-taskno {
  font-family: 'Courier New', monospace;
  color: #1e293b;
}

.popover-divider {
  margin: 8px 0 !important;
}

.popover-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 4px 0;
  line-height: 1.6;
}

.row-label {
  flex-shrink: 0;
  width: 80px;
  font-size: 12px;
  color: #64748b;
}

.row-val {
  font-size: 13px;
  color: #1e293b;
}

.popover-actions {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}

/* ===== P3：右键任务条 → 完整菜单 ===== */
.aps-ctx-menu {
  position: fixed;
  z-index: 3000;
  min-width: 220px;
  overflow: hidden;
  font-size: 13px;
  background: #fff;
  border-radius: 6px;
  box-shadow:
    0 4px 16px rgb(15 23 42 / 18%),
    0 0 0 1px rgb(15 23 42 / 6%);
  user-select: none;
}

.ctx-menu-header {
  display: flex;
  padding: 8px 14px;
  font-family: 'Courier New', monospace;
  font-weight: 600;
  color: #1e293b;
  background: #f1f5f9;
  border-bottom: 1px solid #e2e8f0;
  align-items: center;
  gap: 6px;
}

.ctx-menu-list {
  padding: 4px 0;
  margin: 0;
  list-style: none;
}

.ctx-menu-item {
  display: flex;
  padding: 8px 14px;
  color: #1e293b;
  cursor: pointer;
  transition: background-color 0.12s ease;
  align-items: center;
  gap: 8px;

  &:hover {
    background: #f1f5f9;
  }

  &.is-disabled {
    color: #94a3b8;
    cursor: not-allowed;

    &:hover {
      background: transparent;
    }
  }
}

.ctx-menu-code {
  padding: 1px 6px;
  margin-left: auto;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  color: #3730a3;
  background: #e0e7ff;
  border-radius: 4px;
}

.ctx-menu-empty {
  margin-left: auto;
  color: #cbd5e1;
}

.ctx-menu-arrow {
  margin-left: auto;
  color: #94a3b8;
}

/* ===== 任务清单（今日 / 未来） ===== */
/* ===== 资源负荷视图（资源 × 时间桶热力格）===== */
.load-pane {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 16px;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
}

.load-note {
  font-size: 12px;
  color: #64748b;
}

.load-note-warn {
  color: #b45309;
}

.load-grid-wrap {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.load-grid {
  display: grid;
  gap: 1px;
  align-content: start;
}

.load-cell {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 26px;
  padding: 2px 4px;
  font-size: 12px;
  background: #f8fafc;
  border: 1px solid #eef2f7;
}

.load-head {
  position: sticky;
  top: 0;
  z-index: 2;
  font-weight: 600;
  color: #475569;
  background: #eef4ff;
}

.load-row-label {
  justify-content: flex-start;
  gap: 6px;
  overflow: hidden;
  text-align: left;
  background: #fff;
}

.load-row-name {
  overflow: hidden;
  color: #1e293b;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.load-row-sub {
  flex-shrink: 0;
  font-size: 11px;
  color: #94a3b8;
}

.load-value {
  font-weight: 600;
  color: #1e293b;
  cursor: default;
}

.task-list-pane {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 12px 16px;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);

  .task-list-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    font-size: 14px;
    font-weight: 600;
    color: #1e293b;
  }

  .task-list-pager {
    display: flex;
    justify-content: flex-end;
    margin-top: 10px;
  }

  .task-list-muted {
    color: #cbd5e1;
  }
}

/* ===== §10A 入口 Dialog 已抽为独立组件（GanttAdjustDialog / ResourceRescheduleDialog /
   DomainRescheduleDialog），此处不再保留 Drawer 样式 ===== */
</style>
