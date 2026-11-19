<script setup lang="ts">
/**
 * APS V1 4号位 — 运行与下发（页面 9 / U01 / U02 / U19）
 *
 * 2026-10-16 重整（依据《4号位致3号位/5号位·运行页联调缺口回执》）：
 *  - 数据源从自拟聚合体 `RunStatusDto` 换成三个真实数据面（见 store/run.ts）
 *  - 状态词表改冻结 07 §4.5 权威四值（此前自拟 PENDING / SUCCESS 冻结里不存在）
 *  - **MES 下发区块整块下线**：后端无资格查询端点；`POST /api/mes/dispatch` 只认
 *    `factoryCode` 且端点说明写明「不触发 2号位 排程」，是登记 stub；
 *    冻结 §十 要求的链路是 4号位 → 5号位 → 2号位，前端直调亦违反该链路
 *  - PARTIAL 三列拆解改为「排程域状态拆解」，数据来自 5号位 `GET /api/domain-status/{runId}`
 *
 * 2026-11-11 工厂用户视角改版（A1/A2/A4/A5/A6）：
 *  - 主视图从「四张批次 KPI 卡」改成「最近一次排产」卡（批次号/时间/类型/触发来源/
 *    域数/策略包版本码 + 域状态分布 + 域级失败警示条）；批次统计降级为一行小字
 *  - 排程域状态由 4 组拆 5 组：异常 / 待我确认 / 进行中 / 已完成 / 未开始
 *  - 域级失败给「看异常解释 →」；待确认给「去确认 →」（按权限码显隐）
 *  - 删「最近状态摘要」卡：完成率（PARTIAL 不计完成）3/8=37% 会被当成质量指标误导
 *  - 触发来源翻中文；策略包版本显示版本码（不再给内部 Id）；刷新按钮提到顶部
 *
 * 2026-11-12 接 3号位《运行与下发页改版四项落码…回执》：
 *  - R1/R2/R3：域状态补「该域本次计划区间 / 任务数 / 稳定失败原因码」
 *    （计划区间与任务数取不到显示「—」；原因码翻中文区分「根因 / 被拖累」，无码不下结论）
 *  - R4：批次记录表接服务端分页端点 `GET /api/governance/runs/paged`（total 与列表同 where）；
 *    该端点尚未部署 ⇒ 自动降级到数组端点，页面照常有数据、只是不显示翻页控件
 *  - 主视图「最近一次排产」只跟第 1 页走（翻页不换主视图）
 */

import { computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  useRunStore,
  RUN_STATUS_LABEL,
  RUN_STATUS_TAG,
  RUN_TYPE_LABEL,
  type RunListItem
} from '@/store/modules/aps/run'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { useDomainStore } from '@/store/modules/aps/domain'
import type { DomainKey, RunStatus } from '@/api/aps-v1'
import {
  PLAN_VERSION_STATUS_LABELS,
  RUN_DOMAIN_STATUS_LABELS,
  RUN_DOMAIN_STATUS_TAG,
  type PlanVersionStatus,
  type RunDomainStatus
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElEmpty,
  ElPagination,
  ElTag,
  ElTable,
  ElTableColumn,
  ElTooltip
} from 'element-plus'

const runStore = useRunStore()
const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()
const router = useRouter()

function humanLabel(domainKey: string | undefined | null): string {
  if (!domainKey) return '—'
  return domainStore.label(domainKey)
}

const {
  loading,
  error,
  errors,
  visibleRuns,
  latestRun,
  runsTotal,
  runsPage,
  runsPageSize,
  runsPagedAvailable,
  activeVersions,
  latestDomainStatuses,
  domainStatuses,
  domainStatusRunId,
  domainStatusHasDenominator,
  selectedDomainRun,
  runStatusCounts,
  strategyProfileVersionCodes,
  strategyProfileVersionLookupOk,
  recovering
} = storeToRefs(runStore)

/* ===== 计划版本状态中文标签 ===== */
function planVersionStatusLabel(status: string): string {
  return PLAN_VERSION_STATUS_LABELS[status as PlanVersionStatus] ?? status
}

function runStatusLabel(status: string): string {
  return RUN_STATUS_LABEL[status as RunStatus] ?? status
}

function runStatusTag(status: string): 'success' | 'warning' | 'danger' | 'primary' {
  return RUN_STATUS_TAG[status as RunStatus] ?? 'info'
}

function domainStatusLabel(status: string): string {
  return RUN_DOMAIN_STATUS_LABELS[status as RunDomainStatus] ?? status
}

function domainStatusTag(status: string): 'success' | 'primary' | 'warning' | 'info' | 'danger' {
  return RUN_DOMAIN_STATUS_TAG[status as RunDomainStatus] ?? 'info'
}

/* ===== 域级失败原因分类（3号位 2026-11-12 R3 落码：reasonCode）=====
 * DOMAIN_CALC_FAILED = 该域计算失败（**根因**）
 * BLOCKED_BY_UPSTREAM_DOMAIN = 上游域失败，本次未生成计划版本（**被拖累**）
 * 未给码 / 未知码 ⇒ 不下结论（不猜根因），仍显示后端给的中文 reason 原文。
 */
const DOMAIN_REASON_KIND: Record<
  string,
  { label: string; hint: string; tag: 'danger' | 'warning' }
> = {
  DOMAIN_CALC_FAILED: {
    label: '根因',
    hint: '该排程域自身计算失败。',
    tag: 'danger'
  },
  BLOCKED_BY_UPSTREAM_DOMAIN: {
    label: '被拖累',
    hint: '上游排程域失败，本次没有生成计划版本。',
    tag: 'warning'
  }
}

function domainReasonKind(d: {
  reasonCode?: string | null
}): { label: string; hint: string; tag: 'danger' | 'warning' } | null {
  const code = d.reasonCode
  if (!code) return null
  return DOMAIN_REASON_KIND[code] ?? null
}

/* ===== 域级计划区间 / 任务数（3号位 2026-11-12 R1/R2 落码）=====
 * 这两个字段来自该域本次的 PlanVersion（未生成时为空）⇒ 取不到就显示「—」，不拿别处的值顶替。
 */
function domainPlanHorizonText(d: {
  planHorizonStart?: string | null
  planHorizonEnd?: string | null
}): string {
  if (!d.planHorizonStart || !d.planHorizonEnd) return '—'
  return `${dayjs(d.planHorizonStart).format('YYYY-MM-DD')} ~ ${dayjs(d.planHorizonEnd).format('YYYY-MM-DD')}`
}

/* ===== Run 持续时间（秒） ===== */
function runDuration(r: { startedAt: string; completedAt?: string | null }): string {
  const end = r.completedAt ? new Date(r.completedAt).getTime() : Date.now()
  const dur = Math.round((end - new Date(r.startedAt).getTime()) / 1000)
  if (dur < 60) return `${dur} 秒`
  if (dur < 3600) return `${Math.floor(dur / 60)} 分 ${dur % 60} 秒`
  return `${Math.floor(dur / 3600)} 时 ${Math.floor((dur % 3600) / 60)} 分`
}

/* ===== 触发来源：内部码翻中文 =====
 * 后端 `ScheduleRunGovDto.TriggeredBy` 声明域：'Hangfire' / UserId / 'API' / 'Agent'
 * ⇒ 除三个固定码外，其余取值都是**触发人标识**（自由文本，如 admin / 用户名 / 备注）。
 */
const TRIGGER_SOURCE_LABEL: Record<string, string> = {
  Hangfire: '系统定时触发',
  API: '接口触发',
  Agent: '智能体触发'
}

function triggerSourceLabel(raw?: string | null): string {
  const v = (raw ?? '').trim()
  if (!v) return '—'
  if (TRIGGER_SOURCE_LABEL[v]) return TRIGGER_SOURCE_LABEL[v]
  if (v === 'UserId' || v.startsWith('UserId')) return '人工触发'
  return `人工触发（${v}）`
}

/* ===== 策略包版本：Id → 版本码（数据面 ④） ===== */
function strategyProfileVersionCode(id?: number | null): string | null {
  if (!id) return null
  return strategyProfileVersionCodes.value[id] ?? null
}

function strategyProfileVersionMissingHint(id: number): string {
  return strategyProfileVersionLookupOk.value
    ? `找不到版本 #${id} 对应的版本码`
    : '策略包版本码取不到（接口失败或无权限），这里只能显示「—」'
}

/* ===== 主视图：最近一次排产 =====
 * 主视图取的是**最近一次「可见」排产**（`latestRun` 来自服务端过滤后的列表，
 * 3号位 2026-11-12 R4 追加要求：不取「全局最近批次」，受限账号不看无权看的批次）。
 *
 * 排程域数（F4，2026-11-12）：**只报「能看到进展的域数」，不回落到登记总数**。
 * 旧写法 `domainStatuses.length || expectedDomainKeys.length`：域状态取不到（无权 / 未取到）时
 * 会回落成登记总数 ⇒ 用户把「一个都看不到」误读成「就这 N 个域」。
 * 现在：可见数 > 0 → 显示可见数（不足登记数时写「可见 N 个」+ 悬停说明）；可见数 = 0 → 「—」。
 */
const domainCountVisible = computed(() => latestDomainStatuses.value.length)
/**
 * 登记的排程域数
 *
 * ⚠️ **null = 未取到**（没有最近一次排产 / 后端没给 `expectedDomainKeys`），**不是 0**：
 *   §0.2 ②「NULL=未知，不得转 0」——写 0 会说成「这次排产明确没登记排程域」。
 */
const domainCountRegistered = computed<number | null>(() => {
  const run = latestRun.value
  if (!run) return null
  const keys = run.expectedDomainKeys
  return Array.isArray(keys) ? keys.length : null
})

const domainCountText = computed(() => {
  const visible = domainCountVisible.value
  if (visible > 0) {
    const registered = domainCountRegistered.value
    return registered != null && registered > visible ? `可见 ${visible} 个` : `${visible} 个`
  }
  return '—'
})

const domainCountHint = computed(() => {
  const visible = domainCountVisible.value
  const registered = domainCountRegistered.value
  if (visible > 0) {
    return registered != null && registered > visible
      ? `这次排产共登记 ${registered} 个排程域，当前只能看到其中 ${visible} 个的进展。`
      : undefined
  }
  if (registered === null) return '最近一次排产的排程域登记情况取不到。'
  return registered > 0
    ? `这次排产登记了 ${registered} 个排程域，但当前看不到它们的进展。`
    : '这次排产没有登记排程域。'
})

/* ===== 业务范围标识（F2，2026-11-12）=====
 * Global 判定一律取后端 `/api/auth/me` 的 `isGlobal`（apsAuth.isGlobalScope），
 * 不再用「维度数组是否为空」推断 —— 那种推断把「全局」和「什么都没配」压成了同一种。
 * 标签说的是**账号被配置的范围**；数据是否已按此收窄由后端口径决定，前端不替后端承诺。
 */
const scopeLabelText = computed(() =>
  apsAuth.isGlobalScope ? '数据范围：全部' : '数据范围：我的范围'
)

const scopeLabelHint = computed(() => {
  if (apsAuth.isGlobalScope) return '当前账号可以查看全部排产记录与排程域。'
  const s = apsAuth.dataScope
  const parts: string[] = []
  if (s.domainKeys.length) {
    const known = s.domainKeys.filter((k) => domainStore.byKey.has(k))
    const unknown = s.domainKeys.length - known.length
    if (known.length) parts.push(`排程域 ${known.map(humanLabel).join(' / ')}`)
    if (unknown) parts.push(`另有 ${unknown} 个排程域未在排程域清单中登记`)
  }
  if (s.factoryCodes.length) parts.push(`工厂 ${s.factoryCodes.join(' / ')}`)
  if (s.productFamilyCodes.length) parts.push(`产品族 ${s.productFamilyCodes.join(' / ')}`)
  if (s.departmentCodes.length) parts.push(`部门 ${s.departmentCodes.length} 个`)
  if (!parts.length) return '当前账号没有配置业务范围，请联系管理员。'
  return `当前账号被限制在：${parts.join('；')}。`
})

/**
 * 可见批次为空时的文案：区分「真没有」与「不在你的范围内」。
 *
 * 服务端按业务范围过滤后列表为空，对受限账号说「还没有排产记录」是**不实**的
 * （记录存在，只是不在范围内）；说「当前业务范围内没有排产记录」既不误导、也不泄露范围外有多少。
 * Global 判定仍取后端 `/api/auth/me` 的 `isGlobal`（与 F2 同一口径）。
 */
const emptyRunsText = computed(() =>
  apsAuth.isGlobalScope ? '还没有排产记录' : '当前业务范围内没有排产记录'
)

interface HeroField {
  label: string
  value: string
  hint?: string
}

const heroFields = computed<HeroField[]>(() => {
  const r = latestRun.value
  if (!r) return []
  const spId = r.strategyProfileVersionId
  const spCode = spId ? strategyProfileVersionCode(spId) : null
  const spField: HeroField = !spId
    ? { label: '策略包版本', value: '未指定' }
    : spCode
      ? { label: '策略包版本', value: spCode }
      : { label: '策略包版本', value: '—', hint: strategyProfileVersionMissingHint(spId) }
  return [
    { label: '批次号', value: `#${r.id}` },
    { label: '开始时间', value: dayjs(r.startedAt).format('MM-DD HH:mm') },
    { label: '数据截止', value: dayjs(r.dataCutoffTime).format('MM-DD HH:mm') },
    { label: '批次类型', value: RUN_TYPE_LABEL[r.runType] ?? r.runType },
    { label: '触发来源', value: triggerSourceLabel(r.triggeredBy) },
    { label: '排程域', value: domainCountText.value, hint: domainCountHint.value },
    spField,
    { label: '耗时', value: runDuration(r) }
  ]
})

/* ===== 排程域状态分组（5号位 /api/domain-status/{runId}，6 值分五组展示） =====
 * 「待我确认」从「进行中 / 待确认」里单独拆出：它是**要用户点确认**的动作，
 * 混在「进行中」里用户就找不到该干什么。
 */
interface DomainStatusGroup {
  key: string
  label: string
  short: string
  cls: string
  chip: 'success' | 'primary' | 'warning' | 'info' | 'danger'
  statuses: readonly string[]
}

const DOMAIN_STATUS_GROUPS: DomainStatusGroup[] = [
  {
    key: 'issue',
    label: '异常（失败 / 被阻断）',
    short: '异常',
    cls: 'failed',
    chip: 'danger',
    statuses: ['FAILED', 'BLOCKED']
  },
  {
    key: 'candidate',
    label: '待我确认',
    short: '待我确认',
    cls: 'candidate',
    chip: 'warning',
    statuses: ['CANDIDATE']
  },
  {
    key: 'running',
    label: '进行中',
    short: '进行中',
    cls: 'active',
    chip: 'primary',
    statuses: ['RUNNING']
  },
  {
    key: 'done',
    label: '已完成',
    short: '已完成',
    cls: 'success',
    chip: 'success',
    statuses: ['COMPLETED']
  },
  {
    key: 'not-started',
    label: '未开始',
    short: '未开始',
    cls: 'not-started',
    chip: 'info',
    statuses: ['NOT_STARTED']
  }
]

const domainStatusGroups = computed(() =>
  DOMAIN_STATUS_GROUPS.map((g) => ({
    ...g,
    items: domainStatuses.value
      .filter((d) => g.statuses.includes(d.status))
      // 预挂失败原因分类（R3 的 reasonCode），模板里直接取，不必反复判断
      .map((d) => ({ ...d, reasonKind: domainReasonKind(d) }))
  }))
)

/** 主视图上的域状态分布（短名 + 计数）—— 恒取**最近一次批次**的域状态 */
const domainStatusSummary = computed(() =>
  DOMAIN_STATUS_GROUPS.map((g) => ({
    key: g.key,
    label: g.short,
    cls: g.chip,
    count: latestDomainStatuses.value.filter((d) => g.statuses.includes(d.status)).length
  }))
)

const hasDomainStatuses = computed(() => domainStatuses.value.length > 0)

/** 域级失败 / 被阻断（主视图警示用 ⇒ 最近一次批次） */
const failedDomains = computed(() =>
  latestDomainStatuses.value.filter((d) => d.status === 'FAILED' || d.status === 'BLOCKED')
)
const failedDomainNames = computed(() =>
  failedDomains.value.map((d) => humanLabel(d.domainKey)).join('、')
)

function isIssueStatus(status: string): boolean {
  return status === 'FAILED' || status === 'BLOCKED'
}

/**
 * 域状态空态文案：三态，**不许把取不到说成「没登记」**。
 * - 没取过数 → 还没有排产记录
 * - 取过数、这次 Run 确实没登记预期域 → 5号位 R3 裁定的终态，如实说
 * - 取过数、这次 Run 登记了预期域却没取到 → 取数出了问题，不冒充正常（错误横幅同时会亮）
 *
 * 拆解卡可跟随所选批次 ⇒ 空态文案要说清是**哪一批**（否则用户不知道在看谁）。
 */
const domainStatusEmptyText = computed(() => {
  if (domainStatusRunId.value === null) {
    return '还没有排产记录，排产之后这里会显示每个排程域的进展。'
  }
  const who = isShowingLatestRun.value ? '最近这次排产' : `批次 #${domainStatusRunId.value}`
  return domainStatusHasDenominator.value
    ? `${who}登记了要跑的排程域，但各域状态暂时取不到，请稍后再看。`
    : `${who}没有登记要跑哪些排程域，所以暂时看不出各域进展。`
})

/** 拆解卡当前显示的是否就是最近一次批次 */
const isShowingLatestRun = computed(
  () => domainStatusRunId.value !== null && domainStatusRunId.value === latestRun.value?.id
)

/** 表格里被选中（拆解卡正在看）的那一行 */
function runRowClassName({ row }: { row: RunListItem }): string {
  return selectedDomainRun.value?.id === row.id ? 'run-row-selected' : ''
}

/**
 * 点「最近排产记录」里的一行 ⇒ 拆解卡切到该批次。
 *
 * 点在按钮/链接上不切（否则「人工恢复」会顺带把拆解卡切走）；点了就等于
 * 「我现在要看这一批的域状态」，这是页面上唯一能查到**非最新批次**域状态的入口。
 */
function handleRunRowClick(row: RunListItem, _column: unknown, event: MouseEvent): void {
  const target = event.target as HTMLElement | null
  if (target?.closest('button, a, .el-tooltip__trigger, .el-switch, .el-select')) return
  void runStore.selectDomainRun(row)
}

/** 拆解卡回到最近一次批次 */
function resetDomainRun(): void {
  void runStore.selectDomainRun(null)
}

const recentRunsSorted = computed(() =>
  [...visibleRuns.value].sort(
    (a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()
  )
)

/* ===== 批次统计（降级为一行小字，不再做四张 KPI 卡） =====
 * 服务端分页后，这里的计数只覆盖**当前页** ⇒ 文案必须说清楚是「共 N 次」还是「本页 N 次」，
 * 不能拿本页计数冒充全量（3号位 2026-11-12 R4：total 是过滤后全量，列表是分页切片）。
 */
const batchSummaryText = computed(() => {
  const runs = recentRunsSorted.value
  if (!runs.length) return emptyRunsText.value
  const c = runStatusCounts.value
  const oldest = runs[runs.length - 1]
  const newest = runs[0]
  const range =
    oldest.startedAt === newest.startedAt
      ? dayjs(newest.startedAt).format('MM-DD')
      : `${dayjs(oldest.startedAt).format('MM-DD')} ~ ${dayjs(newest.startedAt).format('MM-DD')}`
  const head = runsPagedAvailable.value
    ? `共 ${runsTotal.value} 次排产，本页 ${runs.length} 次（${range}）`
    : `最近 ${runs.length} 次排产（${range}）`
  return (
    `${head}：` +
    `完成 ${c.COMPLETED} · 部分成功 ${c.PARTIAL_SUCCESS} · 失败 ${c.FAILED} · 运行中 ${c.RUNNING}`
  )
})

/** 最近一次失败（失败原因悬停看全文） */
const lastFailedRun = computed(
  () => recentRunsSorted.value.find((r) => r.status === 'FAILED') ?? null
)

/* ===== 加载 ===== */
function refresh(): void {
  runStore.load()
}
onMounted(refresh)

/* ===== 批次记录翻页（服务端分页，3号位 2026-11-12 R4）===== */
function handlePageChange(page: number): void {
  runStore.changePage(page)
}
function handlePageSizeChange(size: number): void {
  runStore.changePage(1, size)
}

/* ===== 出口按钮 =====
 * - 「看异常解释」→ /aps/explanation（只读诊断页，路由级无写权限码）
 * - 「去确认」→ /aps/candidate（需 aps.candidate.confirm，无权限则不显示按钮）
 */
const canConfirmCandidate = computed(() => apsAuth.has('aps.candidate.confirm'))

/**
 * 「看异常解释」→ /aps/explanation
 *
 * ⚠️ **必须带上 planVersionId**（2026-11-17）：不带的话解释页只能落回「当前生效版本」，
 *   用户点的域和看到的解释对不上（点 FAMILY_FRL 的失败，看到的却是别的域的版本）。
 *   解释页读 `?planVersionId=` 后自动选中该版本并置顶（@see views/Aps/Explanation.vue）。
 */
function goExplanation(planVersionId?: number | null): void {
  if (planVersionId) {
    router.push({
      path: '/aps/explanation',
      query: { planVersionId: String(planVersionId) }
    })
    return
  }
  router.push('/aps/explanation')
}

function goCandidate(planVersionId?: number | null): void {
  // 候选版本确认页已支持 `candidatePlanVersionId` 入参（Candidate.vue:85）⇒ 直接落到那一版的对比
  if (planVersionId) {
    router.push({
      path: '/aps/candidate',
      query: { candidatePlanVersionId: String(planVersionId) }
    })
    return
  }
  router.push('/aps/candidate')
}

/* ===== U19：FAILED Run 人工恢复 =====
 * 4号位文档第 15 节：必须调用「新建 ScheduleRun」，不能把 FAILED 改回 RUNNING
 * 权限：aps.plan.run（v1.2 §23.1 权限码门控）
 */
const canRecoverRun = computed(() => apsAuth.has('aps.plan.run'))

async function handleRecoverFailedRun(row: {
  id: number
  expectedDomainKeys: DomainKey[] | null
  errorMessage?: string | null
}): Promise<void> {
  if (!canRecoverRun.value) {
    ElMessage.warning('当前账号无排程执行权限，不可恢复失败批次')
    return
  }
  if (!row.expectedDomainKeys?.length) {
    ElMessage.warning('这条排产记录没有登记排程域，无法发起恢复')
    return
  }
  let reason = ''
  try {
    const res = await ElMessageBox.prompt(
      `将对失败的排产记录 #${row.id} 发起人工恢复。\n` +
        `排程域：${row.expectedDomainKeys.map(humanLabel).join(' / ')}\n` +
        `原错误：${row.errorMessage ?? '-'}\n\n` +
        `恢复会新建一次排产运行，原记录状态保持失败。\n` +
        `请填写恢复原因（写入审计）：`,
      '人工恢复失败的排产记录',
      {
        confirmButtonText: '确认恢复',
        cancelButtonText: '取消',
        inputType: 'textarea',
        inputPlaceholder: '例如：生产指示位置数据已补齐，重新触发排程',
        inputValidator: (val: string) => (val && val.trim().length >= 4 ? true : '至少 4 个字符'),
        inputErrorMessage: '至少 4 个字符'
      }
    )
    reason = (res.value ?? '').trim()
  } catch {
    return
  }
  const actor = apsAuth.userInfo?.userCode ?? 'unknown'
  try {
    const result = await runStore.recoverFailedRun({
      originalRunId: row.id,
      domainKeys: row.expectedDomainKeys,
      reason,
      actor
    })
    ElMessage.success(
      `已发起恢复 → 新记录 #${result.newRunId}（原记录 #${result.originalRunId} 仍为失败）。列表已刷新。`
    )
  } catch (err) {
    const msg = (err as Error)?.message ?? '未知错误'
    ElMessage.error(`恢复失败：${msg}`)
  }
}
</script>

<template>
  <div class="aps-run">
    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />

    <!-- 分块失败：每个数据面单独提示，不整页报错 -->
    <ElAlert
      v-for="e in errors"
      :key="e.section"
      type="warning"
      :closable="false"
      show-icon
      :title="`${e.section}加载失败：${e.message}`"
    />

    <!-- ===== 主视图：最近一次排产 ===== -->
    <ElCard class="hero-card">
      <template #header>
        <div class="panel-header">
          <span class="hero-title">
            最近一次排产
            <template v-if="latestRun">
              <span class="hero-runid">#{{ latestRun.id }}</span>
              <ElTag :type="runStatusTag(latestRun.status)" size="small">
                {{ runStatusLabel(latestRun.status) }}
              </ElTag>
            </template>
          </span>
          <div class="hero-actions">
            <ElTooltip :content="scopeLabelHint" placement="bottom">
              <span class="scope-label">{{ scopeLabelText }}</span>
            </ElTooltip>
            <ElButton :loading="loading" size="small" @click="refresh">
              <Icon icon="vi-ep:refresh" /> 刷新
            </ElButton>
          </div>
        </div>
      </template>

      <ElEmpty v-if="!latestRun" :description="emptyRunsText" />

      <template v-else>
        <div class="hero-fields">
          <div v-for="f in heroFields" :key="f.label" class="hero-field">
            <div class="hero-field-label">{{ f.label }}</div>
            <ElTooltip v-if="f.hint" :content="f.hint" placement="top">
              <div class="hero-field-value muted">{{ f.value }}</div>
            </ElTooltip>
            <div v-else class="hero-field-value">{{ f.value }}</div>
          </div>
        </div>

        <div class="hero-domains">
          <span class="hero-domains-label">排程域状态</span>
          <span v-for="c in domainStatusSummary" :key="c.key" class="hero-chip" :class="c.cls">
            <i class="chip-dot"></i>{{ c.label }} {{ c.count }}
          </span>
        </div>

        <!-- 域级失败警示：批次还是「运行中」但域已经失败时，这里是唯一的抓手 -->
        <div v-if="failedDomains.length" class="hero-warn">
          <Icon icon="vi-mdi:alert-circle" class="warn-icon" />
          <span class="warn-text">
            本次排产有 {{ failedDomains.length }} 个排程域失败或被阻断：{{ failedDomainNames }}
          </span>
          <ElButton type="danger" link size="small" @click="goExplanation()">
            看异常解释 <Icon icon="vi-ep:arrow-right" />
          </ElButton>
        </div>
      </template>
    </ElCard>

    <!-- ===== 批次统计（一行小字） ===== -->
    <div class="batch-line">
      <span class="batch-text">{{ batchSummaryText }}</span>
      <span v-if="lastFailedRun" class="batch-failed">
        最近一次失败
        <ElTooltip
          :content="lastFailedRun.errorMessage ?? '这条记录没有留下失败原因'"
          placement="top"
        >
          <code class="code-tag">#{{ lastFailedRun.id }}</code>
        </ElTooltip>
      </span>
    </div>

    <!-- ===== 排程域状态拆解（默认最近一次 Run，可点下方表格行切批次） ===== -->
    <ElCard class="partial-card">
      <template #header>
        <div class="panel-header">
          <span>
            排程域状态拆解
            <span v-if="domainStatusRunId !== null" class="muted">
              （<template v-if="isShowingLatestRun">最近一次排产</template
              ><template v-else>批次</template> #{{ domainStatusRunId }}）
            </span>
          </span>
          <span class="panel-header-actions">
            <span class="hint-text">点下方「最近排产记录」里的任意一行，可看那一批的各域进展</span>
            <ElButton
              v-if="domainStatusRunId !== null && !isShowingLatestRun"
              size="small"
              link
              @click="resetDomainRun"
            >
              回到最近一次
            </ElButton>
          </span>
        </div>
      </template>
      <div v-if="!hasDomainStatuses" class="col-empty">{{ domainStatusEmptyText }}</div>
      <div v-else class="partial-grid">
        <div v-for="g in domainStatusGroups" :key="g.key" class="partial-col" :class="g.cls">
          <div class="col-head">
            <span class="col-name">{{ g.label }}</span>
            <ElTag size="small" :type="g.chip">{{ g.items.length }}</ElTag>
          </div>
          <div v-for="d in g.items" :key="d.domainKey" class="col-item">
            <div class="col-item-line">
              <ElTag effect="light" :type="domainStatusTag(d.status)">
                {{ humanLabel(d.domainKey) }}
              </ElTag>
              <span class="muted small">{{ domainStatusLabel(d.status) }}</span>
              <span v-if="d.planVersionCode" class="muted small">{{ d.planVersionCode }}</span>
              <ElTooltip v-if="d.reasonKind" :content="d.reasonKind.hint" placement="top">
                <ElTag size="small" effect="plain" :type="d.reasonKind.tag">
                  {{ d.reasonKind.label }}
                </ElTag>
              </ElTooltip>
            </div>
            <div class="col-item-meta">
              <span>计划区间 {{ domainPlanHorizonText(d) }}</span>
              <span>任务数 {{ d.totalTasks ?? '—' }}</span>
            </div>
            <div v-if="d.reason" class="err-msg">{{ d.reason }}</div>
            <div v-if="isIssueStatus(d.status)" class="col-actions">
              <ElButton type="danger" link size="small" @click="goExplanation(d.planVersionId)">
                看异常解释 <Icon icon="vi-ep:arrow-right" />
              </ElButton>
            </div>
            <div v-else-if="d.status === 'CANDIDATE' && canConfirmCandidate" class="col-actions">
              <ElButton type="warning" link size="small" @click="goCandidate(d.planVersionId)">
                去确认 <Icon icon="vi-ep:arrow-right" />
              </ElButton>
            </div>
          </div>
          <div v-if="!g.items.length" class="col-empty">无</div>
        </div>
      </div>
    </ElCard>

    <!-- ===== Recent Runs ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span> 最近排产记录 </span>
          <span class="hint-text"
            >点一行即可在上方查看那一批的各域进展；人工恢复会新建一次运行，原失败运行状态保持失败</span
          >
        </div>
      </template>
      <ElTable
        v-if="recentRunsSorted.length"
        :data="recentRunsSorted"
        size="small"
        border
        stripe
        :row-class-name="runRowClassName"
        @row-click="handleRunRowClick"
      >
        <ElTableColumn prop="id" label="批次" width="64" fixed="left" />
        <ElTableColumn label="类型" width="92">
          <template #default="{ row }">{{ RUN_TYPE_LABEL[row.runType] }}</template>
        </ElTableColumn>
        <ElTableColumn label="状态" width="88">
          <template #default="{ row }">
            <ElTag :type="runStatusTag(row.status)" size="small">
              {{ runStatusLabel(row.status) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="排程域" width="160">
          <template #default="{ row }">
            <template v-if="row.expectedDomainKeys?.length">
              <span v-for="dk in row.expectedDomainKeys" :key="dk">
                <ElTag size="small" effect="plain" class="domain-tag">
                  {{ humanLabel(dk) }}
                </ElTag>
              </span>
            </template>
            <span v-else class="muted">未登记</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="触发来源" width="120">
          <template #default="{ row }">
            <ElTooltip v-if="row.triggeredBy" :content="row.triggeredBy" placement="top">
              <span class="muted">{{ triggerSourceLabel(row.triggeredBy) }}</span>
            </ElTooltip>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="数据截止" width="120">
          <template #default="{ row }">
            <span class="muted">{{ dayjs(row.dataCutoffTime).format('MM-DD HH:mm') }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="策略包版本" width="110">
          <template #default="{ row }">
            <template v-if="row.strategyProfileVersionId">
              <span v-if="strategyProfileVersionCode(row.strategyProfileVersionId)" class="sp-code">
                {{ strategyProfileVersionCode(row.strategyProfileVersionId) }}
              </span>
              <ElTooltip
                v-else
                :content="strategyProfileVersionMissingHint(row.strategyProfileVersionId)"
                placement="top"
              >
                <span class="muted">—</span>
              </ElTooltip>
            </template>
            <span v-else class="muted">未指定</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="耗时" width="80">
          <template #default="{ row }">
            <span class="muted">{{ runDuration(row) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="开始时间" width="120">
          <template #default="{ row }">{{ dayjs(row.startedAt).format('MM-DD HH:mm') }}</template>
        </ElTableColumn>
        <ElTableColumn label="完成时间" width="120">
          <template #default="{ row }">
            <span v-if="row.completedAt">{{ dayjs(row.completedAt).format('MM-DD HH:mm') }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="错误信息" min-width="180">
          <template #default="{ row }">
            <ElTooltip v-if="row.errorMessage" :content="row.errorMessage" placement="top">
              <span class="err-cell">{{ row.errorMessage }}</span>
            </ElTooltip>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="120" fixed="right">
          <template #default="{ row }">
            <template v-if="row.status === 'FAILED'">
              <!-- 冻结语义：ExpectedDomainKeysJson 是恢复的唯一权威依据，缺了就不能恢复。
                   直接禁用并在悬停时说明原因，避免用户点了才收到警告。 -->
              <ElTooltip
                :disabled="!!row.expectedDomainKeys?.length"
                content="这条记录没有登记排程域，暂不能恢复"
                placement="left"
              >
                <span class="recover-wrap">
                  <ElButton
                    size="small"
                    type="danger"
                    :loading="recovering"
                    :disabled="!canRecoverRun || !row.expectedDomainKeys?.length"
                    @click="handleRecoverFailedRun(row)"
                  >
                    <Icon icon="vi-mdi:restart-alert" />
                    人工恢复
                  </ElButton>
                </span>
              </ElTooltip>
            </template>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else-if="loading" class="panel-empty">
        <ElEmpty description="加载中" />
      </div>
      <ElEmpty v-else description="暂无排产记录" />
      <!-- 服务端分页（3号位 R4）；分页端点未部署时（降级到数组端点）不显示翻页控件 -->
      <div v-if="runsPagedAvailable && runsTotal > 0" class="pager">
        <ElPagination
          :current-page="runsPage"
          :page-size="runsPageSize"
          :total="runsTotal"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          background
          @current-change="handlePageChange"
          @size-change="handlePageSizeChange"
        />
      </div>
    </ElCard>

    <!-- ===== 当前生效版本 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span> 当前生效版本 </span>
          <ElTag v-if="activeVersions.length" type="primary" size="small">
            {{ activeVersions.length }} 排程域
          </ElTag>
        </div>
      </template>
      <div v-if="!activeVersions.length" class="panel-empty">
        <ElEmpty description="当前无生效版本" />
      </div>
      <div v-else class="version-list">
        <div
          v-for="v in activeVersions"
          :key="v.planVersionId"
          class="version-card"
          :class="v.status.toLowerCase()"
        >
          <div class="version-head">
            <span class="version-code">{{ v.versionCode }}</span>
            <ElTag
              :type="
                v.status === 'ACTIVE'
                  ? 'success'
                  : v.status === 'CANDIDATE'
                    ? 'warning'
                    : v.status === 'FAILED'
                      ? 'danger'
                      : 'info'
              "
              size="small"
            >
              {{ planVersionStatusLabel(v.status) }}
            </ElTag>
          </div>
          <ElDescriptions :column="2" size="small" class="version-desc">
            <ElDescriptionsItem label="排程域">{{ humanLabel(v.domainKey) }}</ElDescriptionsItem>
            <ElDescriptionsItem label="激活时间">
              {{ v.activatedAt ? dayjs(v.activatedAt).format('MM-DD HH:mm') : '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="计划区间">
              {{ dayjs(v.planHorizonStart).format('YYYY-MM-DD') }}
              ~
              {{ dayjs(v.planHorizonEnd).format('YYYY-MM-DD') }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="版本号">
              <span class="code-tag">#{{ v.planVersionId }}</span>
            </ElDescriptionsItem>
          </ElDescriptions>
        </div>
      </div>
    </ElCard>
  </div>
</template>

<style lang="less" scoped>
.aps-run {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 12px;
}

.panel {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

/* 卡片头右侧：提示语 + 复位按钮 */
.panel-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* 批次表：被选中（拆解卡正在看）的那一行 */
:deep(.el-table__body tr.run-row-selected > td.el-table__cell) {
  background-color: var(--el-color-primary-light-9);
}

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
}

/* 包一层 span：禁用态的 ElButton 不触发鼠标事件，Tooltip 需要可悬停的外层 */
.recover-wrap {
  display: inline-block;
}

.small {
  font-size: 11px;
}

/* 批次记录表分页条（服务端分页） */
.pager {
  display: flex;
  justify-content: flex-end;
  padding-top: 10px;
}

/* ===== 主视图卡 ===== */
.hero-card {
  border-radius: 8px;
}

.hero-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.scope-label {
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
  cursor: default;
  white-space: nowrap;
}

.hero-title {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.hero-runid {
  font-family: 'Courier New', monospace;
  font-size: 14px;
  font-weight: 700;
  color: #1e293b;
}

.hero-fields {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px 16px;
}

.hero-field-label {
  font-size: 12px;
  color: #94a3b8;
}

.hero-field-value {
  margin-top: 2px;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.hero-domains {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  padding-top: 12px;
  margin-top: 12px;
  border-top: 1px dashed #e2e8f0;
}

.hero-domains-label {
  font-size: 12px;
  color: #94a3b8;
}

.hero-chip {
  display: inline-flex;
  padding: 2px 10px;
  font-size: 12px;
  background: #f1f5f9;
  border-radius: 10px;
  align-items: center;
  gap: 6px;

  .chip-dot {
    width: 7px;
    height: 7px;
    background: #94a3b8;
    border-radius: 50%;
  }

  &.success {
    color: #047857;
    background: #ecfdf5;

    .chip-dot {
      background: #059669;
    }
  }

  &.warning {
    color: #b45309;
    background: #fff7ed;

    .chip-dot {
      background: #f59e0b;
    }
  }

  &.primary {
    color: #1d4ed8;
    background: #eff6ff;

    .chip-dot {
      background: #3b82f6;
    }
  }

  &.danger {
    color: #b91c1c;
    background: #fef2f2;

    .chip-dot {
      background: #ef4444;
    }
  }
}

.hero-warn {
  display: flex;
  padding: 8px 12px;
  margin-top: 10px;
  font-size: 13px;
  color: #b91c1c;
  background: #fef2f2;
  border: 1px solid #fecaca;
  border-radius: 6px;
  align-items: center;
  gap: 8px;

  .warn-icon {
    font-size: 16px;
  }

  .warn-text {
    flex: 1;
  }
}

/* ===== 批次统计小字行 ===== */
.batch-line {
  display: flex;
  padding: 8px 12px;
  font-size: 12px;
  color: #64748b;
  background: #fff;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.batch-failed {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  white-space: nowrap;
}

/* ===== 排程域状态拆解（自适应列，空列自动收窄） ===== */
.partial-card {
  border-radius: 8px;
}

.partial-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 14px;
}

.partial-col {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  &.success {
    background: #ecfdf5;
    border-color: #a7f3d0;
  }

  &.failed {
    background: #fef2f2;
    border-color: #fecaca;
  }

  &.candidate {
    background: #fff7ed;
    border-color: #fed7aa;
  }

  &.active {
    background: #eff6ff;
    border-color: #bfdbfe;
  }

  &.not-started {
    background: #f8fafc;
    border-color: #e2e8f0;
  }

  .col-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 10px;

    .col-name {
      font-size: 13px;
      font-weight: 600;
      color: #1e293b;
    }
  }

  .col-item {
    padding: 8px 10px;
    margin-bottom: 6px;
    background: rgb(255 255 255 / 70%);
    border-radius: 4px;
  }

  .col-item-line {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
  }

  /* 该域本次的计划区间 / 任务数（3号位 R1/R2） */
  .col-item-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    margin-top: 4px;
    font-size: 11px;
    color: #64748b;
  }

  .col-actions {
    margin-top: 4px;
  }

  .col-empty {
    padding: 8px 0;
    font-size: 12px;
    color: #94a3b8;
    text-align: center;
  }

  .err-msg {
    margin-top: 6px;
    font-size: 12px;
    color: #7f1d1d;
    /* 失败原因里带一长串 Domain 编码（FAMILY_CYL,FAMILY_FRL,...）⇒ 不换行会横向溢出、
       被列边界裁掉，用户读不到「到底是哪几个域失败」⇒ 强制按字符断行 */
    word-break: break-word;
    overflow-wrap: anywhere;
  }
}

/* 生效版本卡片 */
.version-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.version-card {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  &.active {
    background: #ecfdf5;
    border-color: #a7f3d0;
  }

  &.candidate {
    background: #fff7ed;
    border-color: #fed7aa;
  }

  &.failed {
    background: #fef2f2;
    border-color: #fecaca;
  }

  .version-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 8px;
  }

  .version-code {
    font-weight: 600;
    color: #1e293b;
  }

  .version-desc {
    :deep(.el-descriptions__label) {
      width: 60px;
      color: #94a3b8;
    }
  }
}

.domain-tag {
  margin-right: 4px;

  &:last-child {
    margin-right: 0;
  }
}

.err-cell {
  display: block;
  max-width: 220px;
  overflow: hidden;
  font-size: 12px;
  color: #b91c1c;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sp-code {
  font-size: 12px;
  font-weight: 600;
  color: #1e293b;
}

.code-tag {
  padding: 1px 6px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  background: #f1f5f9;
  border-radius: 3px;
}
</style>
