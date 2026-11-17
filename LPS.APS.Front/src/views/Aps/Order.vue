<script setup lang="ts">
/**
 * APS V1 4号位 — 订单/需求计划（页面 2）
 *
 * 冻结依据：
 *  - `06 v1.38 §7.2`：5号位向4号位提供「订单/需求计划」查询
 *  - `13 v1.8 §七`：用户输出展示含「订单计划」；`§十一` 禁止页面内计算排程结果
 *  - Order.Status 值域取「数据流向」口径：`07:3474` 的 `Order_Canonical.Status`
 *    = `OPEN/CLOSED/CANCELLED`，`sp_SyncOrdersToPartitionTable` 原样复制进 `[Order].Status`
 *    （`07:3611` 另有 6 值写法，与复制逻辑冲突，待 0号位 澄清；见 `types/common.ts`）
 *  - `07:2666` DelayStatus 值域 `ON_TIME/FIRST_DELAY/REPEATED_DELAY`
 *    （与 Status 是两个独立维度，禁止混用）
 *
 * 2026-10-17 按 5号位 回执整页重做（此前页面挂在一份自拟 DTO 上）：
 *  - 列表字段按真实 `OrderListItemDto`（`id` / `quantity` / `mtS_InstructionNo` / `priority` …）
 *  - 详情按真实 `{order, pegging, tasks}`（**没有 reasons 键**）
 *  - 硬编码的工厂 / 产品族 / 客户下拉（`F-SUZ-01`、`INJECTION`、`CUST-001` 等）全部删除 ——
 *    dev 实库是 `CN` / `CYL` / 中文客户名，与那份列表**零重合**，按它筛必然空
 *  - 「风险」卡下线（`riskCount` 查的是另一张表，实测恒 0，区分不了「无风险」与「无数据」）
 *
 * 2026-11-02 按 5号位 答复函（r14009 已部署）接入**计划派生交付判定**：
 *  - 交付状态改读后端算好的 `riskLevel`（`DELAYED` 红 / `ON_TRACK` 绿 / **缺键=未排产** 灰）；
 *    `Order.DelayStatus` 是 ERP/ODS 独立维度，冻结 `01 §64` 红线禁止拿它当本版本交付结果
 *  - KPI 四卡 = 总数 / 按期 / 延迟 / 未排产，全部取 summary 的**计划派生三桶**
 *    （恒等式：按期+延迟+未排产 = 总数）
 *  - 「未排」卡重新上线：后端 `unscheduledCount` 已从 `CAST(0 AS INT)` 字面量改为**实值**
 *    （本版本无 Task 行）；旧「未计算」卡（= 总数 − 按期 − 延迟）口径已废，删掉
 *
 * 2026-11-12 **工厂用户视角改版**（用户：站在工厂用户角度，页面怎么弄比较好用）：
 *
 *  ① 默认计划版本：旧规则取「最新一条」（`GET /api/schedule/versions` 最新优先）
 *     ⇒ dev 最新一条是 `FAILED` 的测试版本，用户一进页面看到的是「延迟 0 / 未排产 100%」的空版本，
 *     像是系统坏了。改由 store 的 `pickDefaultVersion()` 按「已生效(ACTIVE) 优先」挑
 *     （端点没给生效标识，仍是前端兜底 ⇒ 已出函 5号位要 `isDefault`，见函件 R5）。
 *  ② 交期快捷区间：后端本来就有 `dueDateFrom` / `dueDateTo`，页面却只有一个日期区间框，
 *     计划员每天要看的是「本周/近 4 周要交期的、已经逾期的」。现给 5 个一键区间
 *     （工作窗口 / 已逾期 / 近 4 周 / 近 13 周 / 全部交期），默认「工作窗口」。
 *  ③ 工厂 / 产品族：原来是**精确匹配的自由文本**（计划员不认编码，只认「北京工厂」），
 *     现改走主数据下拉（3号位 `GET /api/governance/factories` / `product-families`）：
 *     显示中文名、传编码，仍允许手输编码兜底。
 *  ④ 文本筛选 400ms 防抖：旧实现每敲一个字就打一次后端。
 *  ⑤ 表格瘦身：生产指示号并进订单号下方、延期原因并进「交付状态」列
 *     （原来 14 列，右边「操作」列常被横向滚动裁掉）。
 *  ⑥ KPI 口径标注：summary 端点只收 `planVersionId`（不收筛选条件）⇒ KPI 恒为**全版本**，
 *     页面必须写明，否则用户以为 KPI 跟着筛选变。
 *  ⑦ 版本状态大白话提示（失败 / 计算中 / 未发令 / 已被顶替 / 全版本都未排产）。
 *
 * 写接口：本页查询部分只读；**唯一写入口 = §10A.1「已有订单提前」**
 * （EXISTING_ORDER_ADVANCE → POST /api/governance/run/candidate，Policy aps.plan.run）
 * @see 4号位实施包 v1.4 §十A
 */

import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useOrderStore } from '@/store/modules/aps/order'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { domainApi, triggerBusinessEntry } from '@/api/aps-v1'
import {
  formatUtcDate,
  formatUtcDateTime,
  formatUtcMonthDay,
  formatUtcMonthDayHM
} from '@/utils/datetime'
import type { BusinessEntrySubmission, OrderListItemDto } from '@/api/aps-v1'
import OrderAdvanceDialog from './components/OrderAdvanceDialog.vue'
import {
  ORDER_STATUS_LABELS,
  ORDER_DELAY_STATUSES,
  ORDER_DELAY_STATUS_LABELS,
  ORDER_RISK_LEVEL_LABELS,
  ORDER_RISK_MISSING_LABEL,
  ORDER_STATUS_FILTER_OPTIONS,
  PLAN_VERSION_STATUS_LABELS,
  normalizeOrderStatus,
  planVersionStatusTag,
  reasonLabelOf,
  type DomainKey,
  type OrderDelayStatus,
  type OrderRiskLevel,
  type OrderStatusDisplay,
  type SupplyType
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElEmpty,
  ElInput,
  ElOption,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const orderStore = useOrderStore()
const {
  list,
  listTotal,
  pageIndex,
  pageSize,
  listLoading,
  listError,
  summary,
  currentDetail,
  detailLoading,
  detailError,
  versions,
  versionsLoading,
  planVersionId
} = storeToRefs(orderStore)

const drawerVisible = ref(false)

// ===== 交付状态配色（计划派生 `riskLevel`；冻结 07 §八.2 三值 + 缺键态）=====
//
// ⚠️ 这里**不再**用 `Order.DelayStatus`（ERP/ODS 独立维度）渲染状态标签：
//   冻结 `01 §64` 红线 —— 不得用 `Order.DelayStatus` 替代当前 PlanVersion 的交付结果；
//   实测 v999333 的 O20630651 就是 `delayStatus=ON_TIME` + 计划超期 15.4 天，
//   两个结论同屏打架。现在交付判定一律读后端算好的 `riskLevel`。
const RISK_COLORS: Record<OrderRiskLevel, string> = {
  ON_TRACK: '#059669',
  AT_RISK: '#d97706',
  DELAYED: '#ef4444'
}
const RISK_TAG_TYPE: Record<OrderRiskLevel, 'success' | 'danger' | 'warning'> = {
  ON_TRACK: 'success',
  AT_RISK: 'warning',
  DELAYED: 'danger'
}
/** 缺键（未排产 / 无交期）的灰 —— 不用红色，避免把「没排」说成「出事」 */
const RISK_MISSING_COLOR = '#94a3b8'

/** 交付状态中文标签：缺键 ⇒「未排产」（**不得**落成「按期」/「未延迟」） */
function riskLabel(raw: string | undefined): string {
  if (!raw) return ORDER_RISK_MISSING_LABEL
  return ORDER_RISK_LEVEL_LABELS[raw as OrderRiskLevel] ?? raw
}
function riskColor(raw: string | undefined): string {
  if (!raw) return RISK_MISSING_COLOR
  return RISK_COLORS[raw as OrderRiskLevel] ?? RISK_MISSING_COLOR
}
function riskTagType(raw: string | undefined): 'success' | 'danger' | 'warning' | 'info' {
  if (!raw) return 'info'
  return RISK_TAG_TYPE[raw as OrderRiskLevel] ?? 'info'
}

/**
 * 主因代码标签 —— 复用冻结 `07 v5.1.13 §八.1` 的 15 码字典，与甘特页 / CTP 页同一口径
 * （不维护第二套自拟码表）。
 *
 * ⚠️ dev 2026-10-19 实测：绝大多数返回值**不在**冻结 15 码内（`BATCH_POLICY_MISSING`、中文散文等），
 * 而冻结 07:4935 明文禁止这两类。故未入字典一律降级成「待核对·<原值>」——
 * 摆出原值是诚实，臆造中文名才是造假（该漂移已出函 5号位，前端不替它兜底成正式码）。
 */
function delayReasonLabel(code: string | undefined): string {
  // 无延期原因 ⇒ '—'（该字段是**可空可选**字段，键不出现即「没有延期原因」）；
  // 有值 ⇒ 走全站唯一口径 `reasonLabelOf`（冻结 15 码 / 未入字典 →「待核对·<原值>」）。
  // 2026-10-31：原为本地查表 `REASON_CODE_LABELS[code as ReasonCode] ?? 待核对·${code}`，
  // 与甘特 / 排程解释 / CTP 页各写各的 ⇒ 收敛到共享函数，避免再次分叉。
  if (!code) return '—'
  return reasonLabelOf(code)
}
/** 延期时长 → 天/小时两态（后端给的是自然小时小数；`!= null` 而非真值判断，0 与「无值」是两态）
 *
 *  口径（5号位 2026-11-02 答复函 §三 Q3 定稿）：**按期不给 `delayHours`**（缺键 = 诚实，
 *  按期/提前无「延期时长」可言）。负值若出现属「提前」，按展示换算显示成「提前 N 天」——
 *  取绝对值会把「提前 20 天」写成「超期 20 天」，方向就反了。
 *  ⚠️ 提前与否的**判定**看 `plannedCompletionTime` 与交期之差，这里只做展示换算，不参与判定。 */
function delayHoursText(hours: number | undefined): string {
  if (hours == null) return ''
  const abs = Math.abs(hours)
  const label = hours < 0 ? '提前' : '超期'
  if (abs < 24) return `${label} ${abs.toFixed(1)} 小时`
  return `${label} ${(abs / 24).toFixed(1)} 天`
}

/** 订单状态标签：按冻结 07:3611 六值词表渲染（实库 `Open` 归一回历史遗留 `OPEN`）；仍不认识则原样显示 */
function statusLabel(raw: string): string {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_LABELS[norm] : raw
}
const ORDER_STATUS_TAG: Record<OrderStatusDisplay, 'success' | 'info' | 'warning' | 'primary'> = {
  NEW: 'info',
  PLANNED: 'primary',
  CONFIRMED: 'success',
  IN_PROGRESS: 'success',
  COMPLETED: 'info',
  CANCELLED: 'warning',
  OPEN: 'success'
}
function statusTagType(raw: string): 'success' | 'info' | 'warning' | 'primary' {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_TAG[norm] : 'info'
}

// ===== Pegging 供应类型标签（冻结 07 的 9 类 SupplyType；未知值原样显示）=====
const SUPPLY_LABELS: Record<SupplyType, string> = {
  INVENTORY: '库存',
  PI: 'PI 在制',
  PO: 'PO 在途',
  VMI: 'VMI 寄售',
  ARRIVED_NOT_INBOUND: '到货未入库',
  INTERPLANT_TRANSIT: '跨厂在途',
  RECEIVED: '已收货',
  PLANNED_PRODUCTION: '计划生产',
  PLANNING_PURCHASE_PLACEHOLDER: '采购估算（占位）'
}
const supplyLabel = (raw: string | undefined): string =>
  raw ? (SUPPLY_LABELS[raw as SupplyType] ?? raw) : '—'

/** 任务状态：冻结 TASK_STATUSES 5 态（common.ts），未知值原样显示 */
const TASK_STATUS_LABELS: Record<string, string> = {
  PLANNED: '已排产',
  RELEASED: '已释放',
  IN_PROGRESS: '执行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消'
}

const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()

/** Domain 选项：从真实 domainStore 派生（非 mock 时即后端 Domain 列表），U41 按 dataScope 收敛 */
const DOMAIN_OPTIONS = computed<Array<{ value: DomainKey | 'ALL'; label: string }>>(() => {
  const all = domainStore.activeDomains.map((d) => ({ value: d.domainKey, label: d.domainName }))
  const allowed = apsAuth.dataScope.domainKeys as readonly string[]
  const list = allowed.length === 0 ? all : all.filter((o) => allowed.includes(o.value))
  return [{ value: 'ALL' as const, label: '全部' }, ...list]
})

// ===== 交期快捷区间（后端既有参数 dueDateFrom / dueDateTo，页面此前没给入口）=====
//
// 计划员每天要回答的问题是「这周/近 4 周有哪些订单要交、哪些已经逾期」，
// 而不是「按 2016 年的交期顺序翻 200 行」。故给 5 个一键区间。
//
// ⚠️ 日期基准用 **UTC 今天**：后端 `customerDueDate` 是 UTC 无时区字符串，
//    而「剩余 N 天」的显示（fmtDue）也是拿 UTC 零点与当前时刻比 —— 两处必须同基准，
//    否则芯片选中的区间和格子里的天数会对不上。
type RangeKey = 'work' | 'overdue' | 'w4' | 'w13' | 'all' | 'custom'

const RANGE_PRESETS: Array<{ key: RangeKey; label: string; desc: string }> = [
  {
    key: 'work',
    label: '近期订单',
    desc: '这批最该动手：近 28 天内要交货的，加上近 30 天内已经拖期的'
  },
  { key: 'overdue', label: '已逾期', desc: '交货日期已经过了今天的订单' },
  { key: 'w4', label: '4 周内到期', desc: '从今天起 4 周内要交货的订单' },
  { key: 'w13', label: '13 周内到期', desc: '从今天起 13 周内要交货的订单' },
  { key: 'all', label: '全部', desc: '不按交货日期挑单（含很早以前的历史积压单）' }
]

/** 相对今天偏移 n 天的 UTC 日期（YYYY-MM-DD） */
function dayOffset(offset: number): string {
  const d = new Date()
  d.setUTCDate(d.getUTCDate() + offset)
  return d.toISOString().slice(0, 10)
}

/** 区间 → 后端真实参数（dueDateFrom / dueDateTo） */
function rangeBounds(key: RangeKey): { from?: string; to?: string } {
  switch (key) {
    case 'work':
      return { from: dayOffset(-30), to: dayOffset(28) }
    case 'overdue':
      return { to: dayOffset(-1) }
    case 'w4':
      return { from: dayOffset(0), to: dayOffset(28) }
    case 'w13':
      return { from: dayOffset(0), to: dayOffset(91) }
    case 'all':
    case 'custom':
    default:
      return {}
  }
}

const activeRange = ref<RangeKey>('work')
const dueFrom = ref<string | undefined>()
const dueTo = ref<string | undefined>()
const rangeDesc = computed(
  () => RANGE_PRESETS.find((r) => r.key === activeRange.value)?.desc ?? '按下面自己填的日期过滤'
)

/** 当前生效的交货日期区间（写成真实日期，比写「4 周内」更好懂） */
const rangeDateText = computed(() => {
  if (activeRange.value === 'all') return '交货日期不限'
  const from = dueFrom.value
  const to = dueTo.value
  if (from && to) return `交货日期 ${from} ~ ${to}`
  if (to) return `交货日期早于 ${to}`
  if (from) return `交货日期 ${from} 之后`
  return '交货日期不限'
})

/** 落区间到后端参数（不重查 —— 供「先设好再统一刷一次」的场景用） */
function setRangeFilter(key: RangeKey): void {
  activeRange.value = key
  const { from, to } = rangeBounds(key)
  dueFrom.value = from
  dueTo.value = to
  orderStore.setFilter({ dueDateFrom: from, dueDateTo: to })
}

/** 落区间 + 重查列表 */
async function applyRange(key: RangeKey): Promise<void> {
  setRangeFilter(key)
  await orderStore.loadList(true)
}

// ===== 筛选输入（全部对应后端真实 Query 参数）=====
const orderNoInput = ref('')
const piInput = ref('')
const materialInput = ref('')
/** 客户：无 distinct 端点，且后端是 `CustomerName LIKE %x%` ⇒ 自由文本，不造下拉选项 */
const customerNameInput = ref('')
/** 工厂 / 产品族：后端是**精确匹配**（`f.Code =` / `pf.Code =`）⇒ 走主数据下拉（显示中文名、传编码） */
const factoryCodeInput = ref<string | undefined>()
const productFamilyInput = ref<string | undefined>()
const delayStatusInput = ref<OrderDelayStatus | undefined>()
const statusInput = ref<OrderStatusDisplay | undefined>()

// ===== 主数据下拉（工厂 / 产品族）=====
//
// 来源：3号位 `GET /api/governance/factories` / `GET /api/governance/product-families`
//  （Domain 维护页同一份主数据，非本页自造 ⇒ 与「域定义」里选的工厂/产品族永远对得上）
const factoryOptions = ref<Array<{ code: string; name: string }>>([])
const familyOptions = ref<Array<{ code: string; name: string }>>([])
const dictError = ref<string>('')

/**
 * 工厂下拉按**业务范围**收窄。
 *
 * 后端 `OrderQueryController.QueryOrders` 对入参做范围校验：
 * `factoryCode` 不在授权范围内 ⇒ 直接 403「Factory 范围越界」。
 * ⇒ 下拉若把范围外的工厂摆出来，用户点一下就撞 403（自己找的坑）。
 * 全局范围（isGlobal）⇒ 全部可选；受限范围 ⇒ 只列授权内的。
 */
const visibleFactories = computed(() => {
  const allowed = apsAuth.isGlobalScope ? [] : (apsAuth.dataScope.factoryCodes ?? [])
  if (allowed.length === 0) return factoryOptions.value
  return factoryOptions.value.filter((f) => allowed.includes(f.code))
})

async function loadMasterData(): Promise<void> {
  try {
    const [factories, families] = await Promise.all([
      domainApi.listFactories(),
      domainApi.listProductFamilies()
    ])
    factoryOptions.value = factories.map((f) => ({ code: f.code, name: f.name }))
    familyOptions.value = families.map((f) => ({ code: f.code, name: f.name }))
    dictError.value = ''
  } catch (err) {
    factoryOptions.value = []
    familyOptions.value = []
    dictError.value = (err as Error)?.message ?? '加载工厂/产品族字典失败'
  }
}

/** 编码 → 中文名（字典里没有就原样显示编码，绝不臆造名字） */
function factoryLabel(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '—'
  return factoryOptions.value.find((f) => f.code === raw)?.name ?? raw
}
function familyLabel(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '—'
  return familyOptions.value.find((f) => f.code === raw)?.name ?? raw
}

// ===== 文本筛选：400ms 防抖 =====
//
// 旧实现 watch 六个输入框，每敲一个字就 `loadList` 一次（每个键一次后端请求）。
// 现在：文本框 400ms 防抖；下拉/日期即刻生效（选择本身就是一次明确动作）。
const TEXT_FILTER_DEBOUNCE_MS = 400
let textTimer: ReturnType<typeof setTimeout> | null = null
/** 重置时抑制一次 watch（否则重置后 400ms 又补打一次请求） */
const suppressTextWatch = ref(false)

watch([orderNoInput, piInput, materialInput, customerNameInput], () => {
  if (suppressTextWatch.value) {
    suppressTextWatch.value = false
    return
  }
  if (textTimer) clearTimeout(textTimer)
  textTimer = setTimeout(() => {
    orderStore.setFilter({
      orderNo: orderNoInput.value || undefined,
      productionInstructionNo: piInput.value || undefined,
      materialCode: materialInput.value || undefined,
      customerName: customerNameInput.value || undefined
    })
    void orderStore.loadList(true)
  }, TEXT_FILTER_DEBOUNCE_MS)
})

onUnmounted(() => {
  if (textTimer) clearTimeout(textTimer)
})

function onDueFromChange(v: string | null | undefined): void {
  dueFrom.value = v || undefined
  activeRange.value = 'custom'
  orderStore.setFilter({ dueDateFrom: dueFrom.value })
  void orderStore.loadList(true)
}

function onDueToChange(v: string | null | undefined): void {
  dueTo.value = v || undefined
  activeRange.value = 'custom'
  orderStore.setFilter({ dueDateTo: dueTo.value })
  void orderStore.loadList(true)
}

function onDelayStatusChange(v: OrderDelayStatus | undefined): void {
  delayStatusInput.value = v
  orderStore.setFilter({ delayStatus: v })
  void orderStore.loadList(true)
}

function onStatusChange(v: OrderStatusDisplay | undefined): void {
  statusInput.value = v
  orderStore.setFilter({ status: v })
  void orderStore.loadList(true)
}

function onFactoryChange(v: string | undefined): void {
  factoryCodeInput.value = v
  orderStore.setFilter({ factoryCode: v })
  void orderStore.loadList(true)
}

function onFamilyChange(v: string | undefined): void {
  productFamilyInput.value = v
  orderStore.setFilter({ productFamilyCode: v })
  void orderStore.loadList(true)
}

async function onVersionChange(v: number): Promise<void> {
  // store 切版本会清空全部筛选并重查（既有语义）；交期区间跟着版本走 ⇒ 再落一次区间并重查。
  await orderStore.setPlanVersion(v)
  await applyRange(activeRange.value)
}

async function onResetSearch(): Promise<void> {
  suppressTextWatch.value = true
  orderNoInput.value = ''
  piInput.value = ''
  materialInput.value = ''
  customerNameInput.value = ''
  factoryCodeInput.value = undefined
  productFamilyInput.value = undefined
  delayStatusInput.value = undefined
  statusInput.value = undefined
  orderStore.clearFilter()
  await applyRange('work')
  // watch 若这次没被触发（输入框本来就是空的），标记要在这里回收，否则下一次真输入会被吞掉
  suppressTextWatch.value = false
}

// ===== 版本下拉标签 =====
function versionLabel(id: number): string {
  const v = versions.value.find((x) => x.id === id)
  return v ? `${v.versionCode}（${PLAN_VERSION_STATUS_LABELS[v.status] ?? v.status}）` : String(id)
}

/** 顶部版本摘要（真实版本的时间窗 / 状态），别让用户对着一串裸 id 猜 */
const currentVersionLabel = computed(() => {
  const v = orderStore.currentVersion
  if (!v) return ''
  const s =
    v.planHorizonStart && v.planHorizonEnd
      ? ` ｜ 时间窗 ${formatUtcMonthDay(v.planHorizonStart)} ~ ${formatUtcMonthDay(v.planHorizonEnd)}`
      : ''
  return `${v.domainKey ?? ''} ｜ 版本 #${v.id}${s}`
})

/**
 * 版本状态的大白话提示（2026-11-12）
 *
 * 工厂用户看到一版「全是未排产」的订单时，第一反应是「系统坏了」。
 * 其实多数情况是**这一版还没发令 / 算失败 / 已被顶替**——把状态翻译成人话即可。
 */
const versionNotice = computed<{ type: 'error' | 'warning' | 'info'; text: string } | null>(() => {
  const v = orderStore.currentVersion
  if (!v) return null
  switch (v.status) {
    case 'FAILED':
      return {
        type: 'error',
        text: '这一版排产失败，没有排产结果。订单本身还在，但看不到排产任务；换一版看。'
      }
    case 'Computing':
      return { type: 'warning', text: '这一版还在计算中，排产结果可能不完整，过一会儿再看。' }
    case 'BUILDING':
      return {
        type: 'warning',
        text: '这一版还没发令，订单暂时没有排产任务，所以都显示成「未排产」。'
      }
    case 'ARCHIVED':
      return { type: 'info', text: '这一版已经被更新的版本顶替，看到的是当时的排产结果。' }
    default:
      return null
  }
})

/** 全版本都未排产时的补充说明（ACTIVE 版也可能没排产任务） */
const allUnscheduledNotice = computed<string | null>(() => {
  const s = summary.value
  if (!s || !s.totalCount) return null
  if (s.unscheduledCount === s.totalCount) {
    return '这一版的订单都还没有排产任务（未排产），所以看不到「APS 预计完成时间」。'
  }
  return null
})

// ===== KPI（全部取 /api/order-query/summary 的服务端聚合）=====
//
// ⚠️ 2026-11-02 口径全换（5号位 r14009 答复函 §二）：三桶改为**计划派生**，
//   与列表 `riskLevel` 同源（冻结 `01 §64`）⇒ 格子与 KPI 不再各说各话。
//   三桶互斥且穷尽：按期 + 延迟 + 未排产 = 总数（可作探针断言）。
//   旧「未延迟」卡文案已换「按期」（同一判定，但「按期」不与 ERP 维度混读）。
//
// ⚠️ 2026-11-12 补一句口径说明：summary 端点**只收 planVersionId**（不收筛选条件）
//   ⇒ 这四个数字恒为「该版本全部订单」，不随下面的筛选变。不写清楚用户会误以为 KPI 跟着筛选走。
const totalCount = computed(() => summary.value?.totalCount ?? null)
const kpis = computed(() => {
  const s = summary.value
  if (!s) return []
  return [
    { key: 'total', label: '订单总数', value: s.totalCount, color: '#334155' },
    { key: 'ontime', label: '按期', value: s.onTimeCount, color: RISK_COLORS.ON_TRACK },
    { key: 'delayed', label: '延迟', value: s.delayedCount, color: RISK_COLORS.DELAYED },
    { key: 'unscheduled', label: '未排产', value: s.unscheduledCount, color: RISK_MISSING_COLOR }
  ]
})
const kpiRatio = (v: number): number =>
  totalCount.value && totalCount.value > 0 ? Math.round((v / totalCount.value) * 100) : 0

// ===== 页码 =====
function onPageChange(p: number): void {
  orderStore.setPage({ pageIndex: p })
  void orderStore.loadList()
}
function onSizeChange(s: number): void {
  orderStore.setPage({ pageIndex: 1, pageSize: s })
  void orderStore.loadList()
}

// ===== 行点击：打开抽屉 =====
/**
 * 抽屉里的「交付状态 / 延期时长 / APS 预计完成」取**打开抽屉的那一行列表数据**。
 *
 * ⚠️ 原因（dev 实测 2026-11-02）：详情端点 `GET /api/order-query/{id}` 的 `order` 是**另一条 SQL**，
 *   **不返计划派生字段**（`riskLevel` / `delayHours` / `plannedCompletionTime` 三个键全缺）⇒
 *   若拿详情响应判空，一条「已超期 15.4 天」的订单会被显示成「未排产」。
 *   列表行是同一 PlanVersion 下的同一条数据（只有列表端点带富化字段），用它才是真的。
 */
const detailRow = ref<OrderListItemDto | null>(null)

async function openDetail(row: OrderListItemDto): Promise<void> {
  drawerVisible.value = true
  detailRow.value = row
  await orderStore.loadDetail(row.id)
}
function closeDetail(): void {
  drawerVisible.value = false
}
function onRowClick(row: OrderListItemDto, column?: { type?: string }): void {
  if (column?.type === 'selection') return
  void openDetail(row)
}

// ===== §10A.1 已有订单提前（EXISTING_ORDER_ADVANCE）=====
const router = useRouter()
const canPlanRun = computed(() => apsAuth.has('aps.plan.run'))

const advanceVisible = ref(false)
const advanceSubmitting = ref(false)
const selectedOrders = ref<OrderListItemDto[]>([])
const tableRef = ref<InstanceType<typeof ElTable> | null>(null)

/** Dialog 候选行（仅取所需字段；orderCanonicalId 自动带入由 Dialog watch 处理）
 *  domainKey 一并带出：弹窗按排程域收敛候选集（一次只处理一个域） */
const advanceCandidates = computed(() =>
  list.value.map((o) => ({
    orderId: o.id,
    orderNo: o.orderNo,
    orderCanonicalId: o.orderCanonicalId,
    domainKey: o.domainKey,
    materialCode: o.materialCode,
    customerName: o.customerName,
    customerDueDate: o.customerDueDate
  }))
)

const advanceDomainOptions = computed<DomainKey[]>(() =>
  DOMAIN_OPTIONS.value.filter((o) => o.value !== 'ALL').map((o) => o.value as DomainKey)
)

/** 候选推进弹窗的默认域：取**选中订单自己的域**（一版一域 ⇒ 行内 domainKey 即该版所属域）；
 *  没勾选时退回第一个可选域。此前默认域来自「排程域」筛选（该筛选已撤），语义不变。 */
const advanceDefaultDomain = computed<DomainKey | undefined>(() => {
  const picked = selectedOrders.value.find((o) => o.domainKey)
  if (picked?.domainKey) return picked.domainKey
  return advanceDomainOptions.value[0]
})

function onOrderSelectionChange(rows: OrderListItemDto[]): void {
  selectedOrders.value = rows
}

function openAdvanceDialog(): void {
  if (selectedOrders.value.length === 0) {
    ElMessage.warning('请先在列表中勾选要提前的订单')
    return
  }
  advanceVisible.value = true
}

async function submitOrderAdvance(payload: BusinessEntrySubmission): Promise<void> {
  advanceSubmitting.value = true
  try {
    const result = await triggerBusinessEntry({
      trigger: payload.trigger,
      domainKey: payload.domainKey,
      draft: payload.draft,
      actor: apsAuth.userInfo?.userCode ?? 'unknown'
    })
    ElMessage.success(
      `已发起：排程运行 #${result.newRunId}，候选版本 #${result.candidatePlanVersionId}。跳转对比页…`
    )
    advanceVisible.value = false
    selectedOrders.value = []
    tableRef.value?.clearSelection()
    router.push({
      name: 'ApsCandidate',
      query: {
        candidatePlanVersionId: String(result.candidatePlanVersionId),
        basePlanVersionId: String(result.basePlanVersionId),
        source: 'order-advance'
      }
    })
  } catch (err) {
    ElMessage.error(`发起订单提前失败：${(err as Error)?.message ?? '未知错误'}`)
  } finally {
    advanceSubmitting.value = false
  }
}

/** 交期显示（后端 DateTime 无时区后缀 → 按 UTC 解析，见 utils/datetime） */
function fmtDue(due: string): string {
  const dueMs = new Date(`${due}Z`).getTime()
  if (Number.isNaN(dueMs)) return due
  const days = Math.round((dueMs - Date.now()) / 86400000)
  // 年月日（原来只显示月日 ⇒ 跨年订单看不出是哪一年，2024-03-14 和 2026-03-14 长得一样）
  const text = formatUtcDate(due)
  return days >= 0 ? `${text}（剩 ${days} 天）` : `${text}（逾期 ${-days} 天）`
}
const isOverdue = (due: string): boolean => new Date(`${due}Z`).getTime() < Date.now()

/** 表头计数：当前筛选命中多少 + 该版本一共多少（两个口径别混） */
const listCountLabel = computed(() => {
  const total = totalCount.value
  const filtered = listTotal.value
  return total === null
    ? `当前筛选 ${filtered} 条`
    : `当前筛选 ${filtered} 条 · 该版本共 ${total} 单`
})

/** 空态文案：区分「这版没订单」和「筛选太窄」 */
const emptyText = computed(() => {
  if (totalCount.value === 0) return '这一版没有订单'
  return '当前筛选条件下没有订单，换个交期区间或放宽条件再看看'
})

const detailOrder = computed(() => currentDetail.value?.order ?? null)
const detailPegging = computed(() => currentDetail.value?.pegging ?? [])
const detailTasks = computed(() => currentDetail.value?.tasks ?? [])

// ===== 加载入口 =====
onMounted(async () => {
  // 排程域字典：**「已有订单提前」候选弹窗**要用域下拉，而全项目只有异常解释页加载过
  // ⇒ 直接进本页时下拉是空的。这里补一次加载（store 内有并发守卫）。
  if (!domainStore.loadedAt) void domainStore.load()
  void loadMasterData()
  await orderStore.loadVersions()
  // 默认交期区间随版本一起落上（否则第一屏是 2016 年的历史订单）；
  // 区间先落进 store，再统一刷列表 + KPI，避免连打两次列表。
  setRangeFilter(activeRange.value)
  await orderStore.refresh(true)
})
</script>

<template>
  <div class="aps-order">
    <!-- ===== 版本选择 + 写入口 ===== -->
    <div class="toolbar">
      <ElSelect
        :model-value="planVersionId ?? undefined"
        placeholder="选择计划版本"
        class="version-select"
        filterable
        :loading="versionsLoading"
        @change="onVersionChange"
      >
        <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v.id)" :value="v.id">
          <div class="version-option">
            <span class="version-code">{{ v.versionCode }}</span>
            <ElTag size="small" :type="planVersionStatusTag(v.status)">
              {{ PLAN_VERSION_STATUS_LABELS[v.status] ?? v.status }}
            </ElTag>
            <span class="muted version-meta">
              {{ v.domainKey }} · {{ formatUtcMonthDay(v.planHorizonStart) }} ~
              {{ formatUtcMonthDay(v.planHorizonEnd) }}
            </span>
          </div>
        </ElOption>
      </ElSelect>
      <div class="toolbar-meta">
        <span v-if="currentVersionLabel" class="muted">{{ currentVersionLabel }}</span>
      </div>
      <!-- §10A.1 已有订单提前：5 个独立中文业务入口中的本页唯一入口 -->
      <ElButton
        type="warning"
        plain
        :disabled="!canPlanRun || selectedOrders.length === 0"
        :title="canPlanRun ? '已有订单提前 — 先在列表勾选订单' : '当前账号无排程执行权限'"
        @click="openAdvanceDialog"
      >
        <Icon icon="vi-mdi:calendar-arrow-right" />
        已有订单提前（{{ selectedOrders.length }}）
      </ElButton>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert
      v-if="listError"
      type="error"
      :closable="false"
      show-icon
      :title="`加载失败：${listError}`"
    />

    <!-- ===== 版本状态大白话提示（失败 / 计算中 / 未发令 / 已被顶替）===== -->
    <ElAlert
      v-if="versionNotice"
      :type="versionNotice.type"
      :closable="false"
      show-icon
      :title="versionNotice.text"
    />
    <ElAlert
      v-if="allUnscheduledNotice"
      type="info"
      :closable="false"
      show-icon
      :title="allUnscheduledNotice"
    />

    <!-- ===== KPI（取 /summary 服务端聚合；取不到整块空态）===== -->
    <!-- 2026-11-12 用户反馈：原先四张大卡片占掉大半屏首屏 ⇒ 改一行紧凑指标条，
         口径说明也并到同一行右端（不必为说明单占一行）。 -->
    <div v-if="kpis.length" class="kpi-row">
      <div v-for="k in kpis" :key="k.key" class="kpi-item">
        <span class="kpi-bar" :style="{ background: k.color }"></span>
        <span class="kpi-label">{{ k.label }}</span>
        <span class="kpi-value" :style="{ color: k.color }">{{ k.value }}</span>
        <span class="kpi-ratio">{{ kpiRatio(k.value) }}%</span>
      </div>
      <span class="kpi-caption">
        这四个数字是「该版本全部订单」的结果（按计划算的），不随下面的筛选变化。
      </span>
    </div>

    <!-- ===== 过滤栏 ===== -->
    <ElCard class="filter-bar">
      <!-- 交货日期快捷区间：计划员每天最常用的一刀 -->
      <div class="range-row">
        <span class="range-label">按交货日期挑单</span>
        <ElTooltip v-for="r in RANGE_PRESETS" :key="r.key" :content="r.desc" placement="top">
          <ElButton
            size="small"
            :type="activeRange === r.key ? 'primary' : 'default'"
            :plain="activeRange !== r.key"
            @click="applyRange(r.key)"
          >
            {{ r.label }}
          </ElButton>
        </ElTooltip>
        <span class="range-desc">{{ rangeDateText }} · {{ rangeDesc }}</span>
      </div>

      <div class="filter-row">
        <ElInput v-model="orderNoInput" placeholder="订单号" clearable class="filter-sm" />
        <ElInput v-model="piInput" placeholder="生产指示号" clearable class="filter-sm" />
        <ElInput v-model="materialInput" placeholder="物料编码" clearable class="filter-sm" />
        <ElInput
          v-model="customerNameInput"
          placeholder="客户名称（模糊）"
          clearable
          class="filter-sm"
        />
      </div>

      <div class="filter-row">
        <ElSelect
          :model-value="factoryCodeInput"
          placeholder="工厂"
          clearable
          filterable
          allow-create
          default-first-option
          class="filter-select-md filter-factory"
          @change="onFactoryChange"
        >
          <ElOption
            v-for="f in visibleFactories"
            :key="f.code"
            :label="`${f.name}（${f.code}）`"
            :value="f.code"
          />
        </ElSelect>
        <ElSelect
          :model-value="productFamilyInput"
          placeholder="产品族"
          clearable
          filterable
          allow-create
          default-first-option
          class="filter-select-md filter-family"
          @change="onFamilyChange"
        >
          <ElOption
            v-for="f in familyOptions"
            :key="f.code"
            :label="`${f.name}（${f.code}）`"
            :value="f.code"
          />
        </ElSelect>
        <ElSelect
          :model-value="statusInput"
          placeholder="订单状态"
          clearable
          class="filter-select-sm"
          @change="onStatusChange"
        >
          <ElOption
            v-for="s in ORDER_STATUS_FILTER_OPTIONS"
            :key="s"
            :label="ORDER_STATUS_LABELS[s]"
            :value="s"
          />
        </ElSelect>
        <ElSelect
          :model-value="delayStatusInput"
          placeholder="ERP 延期标记"
          clearable
          class="filter-select-sm"
          @change="onDelayStatusChange"
        >
          <ElOption
            v-for="d in ORDER_DELAY_STATUSES"
            :key="d"
            :label="ORDER_DELAY_STATUS_LABELS[d]"
            :value="d"
          />
        </ElSelect>
        <ElDatePicker
          :model-value="dueFrom"
          type="date"
          placeholder="交期起"
          value-format="YYYY-MM-DD"
          class="filter-date"
          @change="onDueFromChange"
        />
        <ElDatePicker
          :model-value="dueTo"
          type="date"
          placeholder="交期止"
          value-format="YYYY-MM-DD"
          class="filter-date"
          @change="onDueToChange"
        />
        <ElButton @click="onResetSearch">重置</ElButton>
      </div>
      <div v-if="dictError" class="filter-error">
        工厂 / 产品族字典加载失败，暂时只能按编码手输：{{ dictError }}
      </div>
    </ElCard>

    <!-- ===== 订单表 ===== -->
    <ElCard class="table-card">
      <template #header>
        <div class="panel-header">
          <span>订单列表</span>
          <span class="header-total">{{ listCountLabel }}</span>
        </div>
      </template>
      <ElTable
        ref="tableRef"
        v-loading="listLoading"
        :data="list"
        stripe
        size="small"
        row-key="id"
        empty-text="暂无订单"
        @row-click="onRowClick"
        @selection-change="onOrderSelectionChange"
      >
        <ElTableColumn type="selection" width="44" :selectable="() => canPlanRun" />
        <!-- 订单号 + 生产指示号（生产指示号并进订单号下方，省一列；原来 14 列把右边的操作列挤到横向滚动外） -->
        <ElTableColumn prop="orderNo" label="订单号" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <div class="cell-order-no">{{ row.orderNo }}</div>
            <div v-if="row.mtS_InstructionNo" class="muted cell-sub">
              指示号 {{ row.mtS_InstructionNo }}
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="materialCode" label="物料编码" min-width="140" show-overflow-tooltip />
        <ElTableColumn label="客户" min-width="110" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.customerName">{{ row.customerName }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="数量" width="84" align="right">
          <template #default="{ row }">
            <span>{{ row.quantity }}</span>
            <span class="muted"> {{ row.uom }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="客户交期" width="176">
          <template #default="{ row }">
            <span :class="{ overdue: isOverdue(row.customerDueDate) }">
              {{ fmtDue(row.customerDueDate) }}
            </span>
          </template>
        </ElTableColumn>
        <!-- APS 预计完成时间（审核 C-01：原先只在详情抽屉里有，业务用户得逐行点开才能核对两个日期）
             后端口径 = 该订单在当前版本全部 Task 的 MAX(PlannedEndTime)（后端算，前端不自算）；
             未排产（无 Task 行）⇒ 整键省略 ⇒ 显示「未排产」，与「交付状态」列同词。 -->
        <ElTableColumn label="APS 预计完成时间" width="150">
          <template #default="{ row }">
            <span v-if="row.plannedCompletionTime">
              {{ formatUtcDateTime(row.plannedCompletionTime) }}
            </span>
            <span v-else class="muted" title="该订单在当前版本没有排产任务">未排产</span>
          </template>
        </ElTableColumn>
        <!-- 交付状态 = 计划派生判定 + 延期时长 + 主因（原来「延期原因」单列常年空，占 180px） -->
        <ElTableColumn label="交付状态" width="150">
          <template #default="{ row }">
            <ElTag
              size="small"
              effect="light"
              :type="riskTagType(row.riskLevel)"
              :style="{
                background: riskColor(row.riskLevel),
                color: '#fff',
                borderColor: riskColor(row.riskLevel)
              }"
            >
              {{ riskLabel(row.riskLevel) }}
            </ElTag>
            <div v-if="delayHoursText(row.delayHours)" class="muted delay-hours">
              {{ delayHoursText(row.delayHours) }}
            </div>
            <div
              v-if="row.primaryReasonCode"
              class="reason-unchecked"
              :title="row.primaryReasonCode"
            >
              {{ delayReasonLabel(row.primaryReasonCode) }}
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn label="订单状态" width="88" align="center">
          <template #default="{ row }">
            <ElTag size="small" :type="statusTagType(row.status)">
              {{ statusLabel(row.status) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="工厂" width="96">
          <template #default="{ row }">{{ factoryLabel(row.factoryCode) }}</template>
        </ElTableColumn>
        <ElTableColumn label="产品族" width="96">
          <template #default="{ row }">{{ familyLabel(row.productFamilyCode) }}</template>
        </ElTableColumn>
        <ElTableColumn prop="priority" label="优先级" width="68" align="right" />
        <ElTableColumn label="操作" width="62" align="center" fixed="right">
          <template #default="{ row }">
            <a href="javascript:;" @click.stop="openDetail(row)">详情</a>
          </template>
        </ElTableColumn>
        <template #empty>
          <ElEmpty :description="emptyText" />
        </template>
      </ElTable>
      <div class="pager-row">
        <ElPagination
          v-model:current-page="pageIndex"
          v-model:page-size="pageSize"
          :total="listTotal"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          background
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </ElCard>

    <!-- ===== 详情 Drawer ===== -->
    <ElDrawer v-model="drawerVisible" size="620px" direction="rtl" @close="closeDetail">
      <template #header>
        <div class="drawer-head">
          <span class="drawer-title">订单详情</span>
          <span v-if="detailOrder" class="drawer-sub">{{ detailOrder.orderNo }}</span>
        </div>
      </template>

      <div v-if="detailLoading" class="drawer-loading"></div>

      <template v-else-if="detailOrder">
        <!-- ===== 订单基本信息 ===== -->
        <div class="detail-section">
          <h4 class="section-title">基本信息</h4>
          <ElDescriptions :column="2" size="small" border>
            <ElDescriptionsItem label="订单号">{{ detailOrder.orderNo }}</ElDescriptionsItem>
            <ElDescriptionsItem label="生产指示号">
              {{ detailOrder.mtS_InstructionNo ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="物料编码">
              {{ detailOrder.materialCode }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户">
              {{ detailOrder.customerName ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户类别">
              {{ detailOrder.customerSegment ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="工厂">
              {{ factoryLabel(detailOrder.factoryCode) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="产品族">
              {{ familyLabel(detailOrder.productFamilyCode) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="排程域">
              {{ detailOrder.domainKey ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单数量">
              {{ detailOrder.quantity }} {{ detailOrder.uom }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="优先级">
              {{ detailOrder.priority }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户交期">
              {{ formatUtcDateTime(detailOrder.customerDueDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="承诺交期">
              {{ formatUtcDateTime(detailOrder.promisedDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单状态">
              <ElTag size="small" :type="statusTagType(detailOrder.status)">
                {{ statusLabel(detailOrder.status) }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="交付状态">
              <ElTag v-if="detailRow" size="small" :type="riskTagType(detailRow.riskLevel)">
                {{ riskLabel(detailRow.riskLevel) }}
              </ElTag>
              <span v-else>—</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="APS 预计完成">
              {{ formatUtcDateTime(detailRow?.plannedCompletionTime) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期时长">
              {{ delayHoursText(detailRow?.delayHours) || '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期原因" :span="2">
              <span
                :class="{
                  'reason-unchecked': !!(
                    detailRow?.primaryReasonCode ?? detailOrder.primaryReasonCode
                  )
                }"
                >{{
                  delayReasonLabel(detailRow?.primaryReasonCode ?? detailOrder.primaryReasonCode)
                }}</span
              >
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单类型">
              {{ detailOrder.orderType }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="BOM 版本">
              {{ detailOrder.bomno ?? '—' }}
            </ElDescriptionsItem>
          </ElDescriptions>
        </div>

        <!-- ===== Pegging 供应承接 ===== -->
        <div class="detail-section">
          <h4 class="section-title">Pegging 供应承接</h4>
          <ElTable :data="detailPegging" size="small" border>
            <ElTableColumn prop="allocationSequence" label="序号" width="60" align="right" />
            <ElTableColumn label="来源类型" min-width="110">
              <template #default="{ row }">{{ supplyLabel(row.supplyType) }}</template>
            </ElTableColumn>
            <ElTableColumn prop="materialCode" label="物料" min-width="140" show-overflow-tooltip />
            <ElTableColumn prop="allocatedQty" label="承接数量" width="90" align="right" />
            <ElTableColumn prop="supplyFactoryCode" label="来源工厂" width="90" />
            <ElTableColumn prop="supplyWarehouseCode" label="来源仓库" width="100" />
            <ElTableColumn label="单据" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="row.supplyDocumentNo">
                  {{ row.supplyDocumentNo }}
                  <span v-if="row.supplyDocumentType" class="muted">
                    / {{ row.supplyDocumentType }}</span
                  >
                </span>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="ETA" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.eta) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="可用时间" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.knownAvailableTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="commitmentStatus" label="履约承诺" width="90" />
            <ElTableColumn prop="supplyMode" label="供应方式" width="90" />
            <template #empty>
              <ElEmpty description="该订单暂无 Pegging 承接记录" :image-size="60" />
            </template>
          </ElTable>
        </div>

        <!-- ===== 生产任务 ===== -->
        <div class="detail-section">
          <h4 class="section-title">生产任务</h4>
          <ElTable :data="detailTasks" size="small" border>
            <ElTableColumn prop="taskNo" label="任务号" width="110" show-overflow-tooltip />
            <ElTableColumn label="工序" width="120">
              <template #default="{ row }">
                {{ row.operationCode ?? '—' }} (Seq {{ row.operationSeq }})
              </template>
            </ElTableColumn>
            <ElTableColumn label="资源" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.resourceName ?? row.resourceCode ?? '—' }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="quantity" label="净产出" width="80" align="right" />
            <ElTableColumn label="计划加工" width="90" align="right">
              <template #default="{ row }">
                <span :class="{ 'hl-warn': row.plannedProcessQty !== row.quantity }">
                  {{ row.plannedProcessQty ?? '—' }}
                </span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="开始" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedStartTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="结束" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedEndTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="状态" width="90">
              <template #default="{ row }">
                <ElTag size="small">{{ TASK_STATUS_LABELS[row.status] ?? row.status }}</ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="关键路径" width="80" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isCriticalPath" size="small" type="danger">关键</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="锁定" width="60" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isLocked" size="small" type="warning">锁</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <template #empty>
              <ElEmpty description="该订单暂无生产任务" :image-size="60" />
            </template>
          </ElTable>
          <p class="u05-hint">
            净产出与计划加工是两个不同口径：计划加工含良率损耗，净产出为最终交付数量。
          </p>
        </div>
      </template>

      <ElEmpty v-else :description="detailError || '请选择订单查看详情'" />
    </ElDrawer>

    <!-- ===== §10A.1 已有订单提前 Dialog（EXISTING_ORDER_ADVANCE）===== -->
    <OrderAdvanceDialog
      :visible="advanceVisible"
      :submitting="advanceSubmitting"
      :domain-options="advanceDomainOptions"
      :orders="advanceCandidates"
      :preselected-order-ids="selectedOrders.map((o) => o.id)"
      :default-domain-key="advanceDefaultDomain"
      @update:visible="(v) => (advanceVisible = v)"
      @submit="submitOrderAdvance"
    />
  </div>
</template>

<style lang="less" scoped>
.aps-order {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;

  .version-select {
    width: 380px;
  }

  .toolbar-meta {
    flex: 1;
    font-size: 12px;
  }
}

.version-option {
  display: flex;
  gap: 8px;
  align-items: center;

  .version-code {
    font-weight: 500;
    color: #1e293b;
  }

  .version-meta {
    font-size: 12px;
  }
}

.muted {
  color: #94a3b8;
}

.cell-order-no {
  font-weight: 500;
  color: #1e293b;
}

.cell-sub {
  font-size: 11px;
  line-height: 1.3;
}

.delay-hours {
  font-size: 12px;
  line-height: 1.4;
}

/* 未入冻结 15 码字典的主因码：中性灰，不与正式码同色，避免被当成已确认原因 */
.reason-unchecked {
  font-size: 12px;
  line-height: 1.4;
  color: #94a3b8;
  font-style: italic;
}

/* KPI：一行紧凑指标条（四项 + 口径说明同一行），不占首屏高度 */
.kpi-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  padding: 6px 14px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 8px;
}

.kpi-item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 16px;
  border-right: 1px solid #e2e8f0;

  &:last-of-type {
    border-right: none;
  }
}

.kpi-bar {
  width: 3px;
  height: 12px;
  border-radius: 2px;
}

.kpi-label {
  font-size: 12px;
  color: #64748b;
}

.kpi-value {
  font-size: 17px;
  font-weight: 600;
  line-height: 1.2;
}

.kpi-ratio {
  font-size: 11px;
  color: #94a3b8;
}

.kpi-caption {
  margin-left: auto;
  padding-left: 16px;
  font-size: 12px;
  color: #94a3b8;
}

.filter-bar {
  border-radius: 8px;
}

.range-row {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  padding-bottom: 12px;
  margin-bottom: 12px;
  border-bottom: 1px dashed #e2e8f0;

  .range-label {
    font-size: 13px;
    color: #475569;
  }

  .range-desc {
    /* 说明整句另起一行（挤在按钮后面会被右边缘截断，看不全） */
    flex: 1 1 100%;
    font-size: 12px;
    color: #94a3b8;
  }
}

.filter-row {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;

  & + .filter-row {
    margin-top: 12px;
  }
}

.filter-sm {
  width: 175px;
}

.filter-select-sm {
  width: 145px;
}

.filter-select-md {
  width: 190px;
}

.filter-date {
  width: 140px;
}

.filter-error {
  margin-top: 10px;
  font-size: 12px;
  color: #d97706;
}

.table-card {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.header-total {
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
}

.pager-row {
  display: flex;
  margin-top: 12px;
  justify-content: flex-end;
}

.overdue {
  font-weight: 600;
  color: #ef4444;
}

.hl-warn {
  font-weight: 600;
  color: #f59e0b;
}

.u05-hint {
  padding: 8px 10px;
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #64748b;
  background: #f8fafc;
  border-left: 3px solid #3b82f6;
  border-radius: 4px;
}

.drawer-head {
  display: flex;
  gap: 12px;
  align-items: baseline;

  .drawer-title {
    font-size: 16px;
    font-weight: 600;
    color: #1e293b;
  }

  .drawer-sub {
    font-size: 12px;
    color: #94a3b8;
  }
}

.detail-section {
  margin-bottom: 18px;

  .section-title {
    display: flex;
    margin: 0 0 8px;
    font-size: 13px;
    font-weight: 600;
    color: #475569;
    align-items: center;
    gap: 8px;
  }
}

.drawer-loading {
  height: 200px;
}
</style>
