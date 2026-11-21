<script setup lang="ts">
/**
 * APS V1 4号位 — 人工到货时间（Manual ETA）页面（页面 8 / P1-7）
 *
 * 4号位文档第 11 节硬约束：
 *  - 必须包含：查询 / 新增 / 更新 / 取消 / Active 状态 / UpdatedBy / UpdatedAt / Remark
 *  - 接口：4号位 → 5号位（ProcurementManualEtaController）
 *  - 不计算：有效到货时间（Effective ETA）/ 最终可用时间（AvailableTime）
 *
 * 11-12 工厂视角改版（站在计划员角度：一眼看清「多少条 / 哪些晚了 / 这周哪些到」）
 *  - 撤页头大标题（2026-10-16 定调：页面不要大标题页头）⇒ 刷新/新增收进列表卡头部
 *  - 指标条改一行紧凑（沿用订单页 .kpi-row 样式）：全部记录 / 生效中 / 已取消 / 已过期 / 7 天内到期
 *    - 5 个数字全部来自后端 COUNT（同一组筛选 + 同一 WHERE 语义）⇒ 与列表永远对得上
 *    - 5 个可点分类（点数字即筛列表）；「已取消」在 11-13 接了后端 cancelledOnly 后也可点
 *  - 筛选即生效（不再有「筛选」按钮）：采购订单 / 物料 / 收货仓库
 *  - 物料显示中文名称（物料主数据解析），不再只给一串编码
 *  - 到货时间给相对口径：「剩 N 天 / 今天到货 / 已晚 N 天」（本地日历天比较）
 *  - 空态两态：一条都没有 vs 筛出来没有
 *  - 取消按钮按 aps.manual_eta.cancel 单独门控（此前误用 edit 码 ⇒ 有 edit 无 cancel 的账号会撞 403）
 *  - 修时区错位：Drawer 原 value-format 带字面量 [Z] ⇒ 存进去的是 UTC 串，
 *    回读按本地解析会整体偏 8 小时；改为不带 Z 的本地墙钟串
 *
 * 11-13 接 5号位 2026-11-12 复函（R1～R5 全部落码）：
 *  - 指标条五个数改 1 个 `/summary` 请求（旧口径 5 个请求凑数）
 *  - 「已取消」变可点分类（后端补了 `cancelledOnly`）
 *  - 筛选栏加「排序」下拉（后端补了 sortBy/sortDir 白名单；缺省不动后端原排序）
 *  - 下拉候选改走 `/facets`（旧口径靠 200 条全量扫描自己派生 ⇒ 超 200 条选不到）
 *  - 列表物料名改用**行内 materialName**（后端 LEFT JOIN Material 投影）⇒ 表格不再依赖物料主数据端点
 *  - 11-14 抽屉「选物料」改**远程搜索**（旧口径拉前 200 条再本地过滤 ⇒ 第 200 条之外的物料列不出来）；
 *    搜索口径由 3号位 11-13 书面确认（编码/名称/规格模糊、编码升序）。
 *    11-14 乙端点已落码（5号位 r14206～r14208，GET /api/procurement-manual-eta/materials，
 *    挂本域 aps.manual_eta.view）⇒ 抽屉改走该端点：**策略域权限依赖已解**
 *    （不再需要 aps.setup.view），并支持按采购订单号 / 行号收窄候选（附问一已闭环）。
 *
 * 11-14 接 5号位 v2.0 回执（R1 落码 r14214～r14218 / R2 取乙）：
 *  - 抽屉「按采购订单收窄」档改走 **`GET /api/procurement-manual-eta/supply-combos`**：
 *    一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，**成对**给出。
 *    Why 必须成对：人工到货时间是**对供给事实的覆盖**——覆盖键
 *    `(PONo, LineNo, MaterialId, ReceivingWarehouse)` ≡ 供给事实
 *    `(SourceDocumentNo, SourceDocumentLineNo, MaterialId, StorageCode)`
 *    （@see AvailableTimeCalculator.cs:63-73,105-107）；旧口径下「物料」与「仓库」
 *    是两个互不相干的池子（物料无仓库维度 / 仓库是 258 条全集）⇒ 拼出来的组合
 *    大概率命不中 ⇒ 这条到货时间**静默不生效**（不报错、不提示）。
 *    现在选中候选 ⇒ 物料与仓库一起带出（仓库按 storageCode 原样写入）。
 *  - 列表/详情接 `isMatched` 标注（5号位 v2.0 R2 取乙：允许保存 + 计算式标注，零 DDL）：
 *    true=会参与排产 / false=命不中（静默不生效）/ null=未判定（**不得当未命中显示**）。
 *
 * 11-16 接 5号位《P01～P13＋采购协同 页面—接口实施矩阵 v0.1》A13 行：
 *  - **撤掉「供货事实追溯」临时兜底档**（`GET /api/supply-fact-trace` 自行配对）：
 *    正式端点 `supply-combos` 已落码可用（r14214～r14218），矩阵明确「撤临时兜底档」；
 *    且该档跨域（挂 `aps.plan.view`）且按可见工厂范围裁剪 ⇒ 配出的仓库本来就可能不全。
 *    正式口径只有一条路：`supply-combos` → 取不到就如实说取不到（页面提示端点不可用）。
 *  - 未命中行文案统一为「未命中当前供货事实（暂不生效）」（列表标签与详情同口径）。
 *
 * 2026-11-20：**接采购协同页的定位入口**（依据《APS_V1_P01-P13_全页面业务设计统一收口核对稿_v0.2_20261114_四项审核修订版.md》§采购协同 6）。
 *   - 「采购协同」页 PO 行有「去维护」按钮 ⇒ 带 `?po=采购单号&line=行号` 跳本页。
 *   - 本页 onMounted 读到这两个参数 ⇒ **直接开新增抽屉**并把采购订单号/行号填好；
 *     物料候选按该 PO 行自动收窄（组合档自带仓库）⇒ 采购担当不用再从 258 个仓库里挑。
 *   - ⚠️ 权限不变：仍是 `aps.manual_eta.edit` 才能新增/编辑，`aps.manual_eta.cancel` 才能取消。
 *
 * RBAC（@see 审核报告 P1-14/15）：
 *  - aps.manual_eta.view：只读
 *  - aps.manual_eta.edit：新增 / 编辑
 *  - aps.manual_eta.cancel：取消
 */

import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import dayjs from 'dayjs'
import { ElMessage, ElMessageBox } from 'element-plus'
import { storeToRefs } from 'pinia'
import { useManualEtaStore } from '@/store/modules/aps/manualEta'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { manualEtaApi } from '@/api/aps-v1/manualEta'
import type {
  ManualEtaBucket,
  ManualEtaDto,
  ManualEtaSortBy,
  ManualEtaSupplyCombo,
  ManualEtaUpsertInput,
  MaterialLookupItem,
  WarehouseOption
} from '@/api/aps-v1'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElOption,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const store = useManualEtaStore()
const apsAuth = useApsAuthStore()
const route = useRoute()
const {
  list,
  currentDetail,
  loading,
  saving,
  error,
  counts,
  poNoOptions,
  materialFilterOptions,
  facetsTruncated,
  pageIndex,
  pageSize,
  total
} = storeToRefs(store)

/* ===== 权限门控（v1.2 §23.1 权限码门控）=====
 *  - aps.manual_eta.edit   → 新增 / 编辑（后端 POST 挂 ManualEtaMaintain）
 *  - aps.manual_eta.cancel → 取消（后端 DELETE 挂 ManualEtaCancel，是**另一个码**）
 *  - 不用角色判断的原因：与 3号位后端二次校验用同一码；前端显隐 = 后端能写
 */
const canWrite = computed(() => apsAuth.has('aps.manual_eta.edit'))
const canCancel = computed(() => apsAuth.has('aps.manual_eta.cancel'))

/* ===== 筛选（变更即生效，不再有「筛选」按钮）===== */
const selectedPoNos = ref<string[]>([])
const selectedMaterialCodes = ref<string[]>([])
const selectedWarehouses = ref<string[]>([])
/** 指标条当前分类（决定列表取哪一批） */
const bucket = ref<ManualEtaBucket>('all')

/* ===== 排序（11-13：5号位补 sortBy/sortDir）=====
 *  - 一个下拉直接给「排什么 + 往哪排」，用户不用理解两个参数
 *  - 缺省「默认排序」= 不传任何排序参数 ⇒ 后端保持原排序（UpdatedAt DESC, PONo, LineNo）逐字不变
 *  - ⚠️ 排序字段白名单外的值后端真 400 ⇒ 这里只允许白名单值
 */
const SORT_OPTIONS: {
  value: string
  label: string
  sortBy: ManualEtaSortBy
  sortDir: 'asc' | 'desc'
}[] = [
  { value: '', label: '默认排序', sortBy: 'manualEta', sortDir: 'asc' },
  { value: 'manualEta|asc', label: '到货时间：近的在前', sortBy: 'manualEta', sortDir: 'asc' },
  { value: 'manualEta|desc', label: '到货时间：远的在前', sortBy: 'manualEta', sortDir: 'desc' },
  { value: 'updatedAt|desc', label: '最近更新的在前', sortBy: 'updatedAt', sortDir: 'desc' },
  { value: 'poNo|asc', label: '采购订单号', sortBy: 'poNo', sortDir: 'asc' },
  { value: 'materialCode|asc', label: '物料编码', sortBy: 'materialCode', sortDir: 'asc' }
]
const sortKey = ref('')
/** 当前排序参数（空 = 不传，走后端默认排序） */
function currentSort(): { sortBy?: ManualEtaSortBy; sortDir?: 'asc' | 'desc' } {
  const hit = SORT_OPTIONS.find((o) => o.value === sortKey.value)
  if (!hit || !hit.value) return {}
  return { sortBy: hit.sortBy, sortDir: hit.sortDir }
}

/** 基础筛选（不含分类）→ 交给 store 拼后端条件 */
function baseFilter(): {
  poNos?: string[]
  materialCodes?: string[]
  receivingWarehouses?: string[]
} {
  return {
    poNos: selectedPoNos.value.length ? [...selectedPoNos.value] : undefined,
    materialCodes: selectedMaterialCodes.value.length
      ? [...selectedMaterialCodes.value]
      : undefined,
    receivingWarehouses: selectedWarehouses.value.length ? [...selectedWarehouses.value] : undefined
  }
}

/** 是否处在「筛过」的状态（用于空态两态区分） */
const hasFilter = computed(
  () =>
    selectedPoNos.value.length > 0 ||
    selectedMaterialCodes.value.length > 0 ||
    selectedWarehouses.value.length > 0 ||
    bucket.value !== 'all'
)

async function reload(resetPage = false): Promise<void> {
  await store.load(
    baseFilter(),
    bucket.value,
    {
      pageIndex: resetPage ? 1 : pageIndex.value,
      pageSize: pageSize.value
    },
    currentSort()
  )
}

/** 任一筛选变更 → 立即重查（回到第 1 页，避免停在旧页号出现空列表错觉） */
async function onFilterChange(): Promise<void> {
  await reload(true)
}

/** 排序变更 → 重查（同样回到第 1 页） */
async function onSortChange(): Promise<void> {
  await reload(true)
}

function resetFilters(): void {
  selectedPoNos.value = []
  selectedMaterialCodes.value = []
  selectedWarehouses.value = []
  bucket.value = 'all'
  sortKey.value = ''
  void reload(true)
}

/** 点指标条数字 → 切分类（同一分类再点一次＝回到全部） */
async function onBucketChange(key: ManualEtaBucket): Promise<void> {
  bucket.value = bucket.value === key ? 'all' : key
  await reload(true)
}

/** 指标条点击入口（模板里不做类型断言） */
async function onKpiClick(k: KpiItem): Promise<void> {
  if (!k.clickable) return
  await onBucketChange(k.key)
}

async function refresh(): Promise<void> {
  await reload()
  await store.loadCandidates()
}

/* ===== 指标条 ===== */
interface KpiItem {
  key: ManualEtaBucket
  label: string
  value: number
  color: string
  hint: string
  clickable: boolean
}

const kpiItems = computed<KpiItem[]>(() => [
  {
    key: 'all',
    label: '全部记录',
    value: counts.value.all,
    color: '#3b82f6',
    hint: '含已取消',
    clickable: true
  },
  {
    key: 'active',
    label: '生效中',
    value: counts.value.active,
    color: '#059669',
    hint: '当前仍在用',
    clickable: true
  },
  {
    key: 'cancelled',
    label: '已取消',
    value: counts.value.cancelled,
    color: '#94a3b8',
    hint: '保留备查',
    clickable: true
  },
  {
    key: 'overdue',
    label: '已过期',
    value: counts.value.overdue,
    color: '#dc2626',
    hint: '已过到货日',
    clickable: true
  },
  {
    key: 'dueSoon',
    label: '7 天内到期',
    value: counts.value.dueSoon,
    color: '#d97706',
    hint: '本周内到货',
    clickable: true
  }
])

/* ===== 分页：当前页切换 ===== */
async function onPageChange(p: number): Promise<void> {
  await store.load(
    baseFilter(),
    bucket.value,
    { pageIndex: p, pageSize: pageSize.value },
    currentSort()
  )
}

/** 分页：每页条数切换 → 回到第 1 页 */
async function onSizeChange(s: number): Promise<void> {
  await store.load(baseFilter(), bucket.value, { pageIndex: 1, pageSize: s }, currentSort())
}

/* ===== 状态映射 ===== */
const STATUS_LABEL: Record<'ACTIVE' | 'CANCELLED', string> = {
  ACTIVE: '生效中',
  CANCELLED: '已取消'
}
const STATUS_TAG: Record<'ACTIVE' | 'CANCELLED', 'success' | 'info'> = {
  ACTIVE: 'success',
  CANCELLED: 'info'
}

function statusOf(dto: ManualEtaDto): 'ACTIVE' | 'CANCELLED' {
  return dto.isActive ? 'ACTIVE' : 'CANCELLED'
}

/** 后端 ManualEta 落库是**本地墙钟**（无时区）
 *  - 串尾若带 Z（历史脏数据），按 UTC 解析会整体偏 8 小时 ⇒ 统一剥掉 Z 按本地解析
 */
function parseLocal(iso: string): Date {
  const s = iso && iso.endsWith('Z') ? iso.slice(0, -1) : iso
  return new Date(s)
}

function fmtDateTime(iso: string): string {
  if (!iso) return '-'
  return dayjs(parseLocal(iso)).format('YYYY-MM-DD HH:mm')
}

function fmtDate(iso: string): string {
  if (!iso) return '-'
  return dayjs(parseLocal(iso)).format('YYYY-MM-DD')
}

/** 到货时间的相对口径（本地日历天比较；工厂用户最关心「还剩几天 / 已经晚了几天」） */
function etaRelative(iso: string): { text: string; overdue: boolean } {
  const t = parseLocal(iso)
  if (Number.isNaN(t.getTime())) return { text: '', overdue: false }
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const eta = new Date(t)
  eta.setHours(0, 0, 0, 0)
  const days = Math.round((eta.getTime() - today.getTime()) / 86400000)
  if (days < 0) return { text: `已晚 ${-days} 天`, overdue: true }
  if (days === 0) return { text: '今天到货', overdue: false }
  return { text: `剩 ${days} 天`, overdue: false }
}

/* ===== Drawer 状态 ===== */
const drawerVisible = ref(false)
const drawerMode = ref<'new' | 'edit'>('new')
const formRef = ref<{
  validate: () => Promise<boolean>
  resetFields: () => void
} | null>(null)

/** 默认到货时间：今天 +7 天（本地墙钟串，**不带 Z**）
 *  - 后端 ManualEta 落库即本地墙钟 ⇒ 前端也必须送本地墙钟串；
 *    带 Z 会被 ASP.NET 当 UTC，整体偏 8 小时（11-12 已修此坑）。
 */
function defaultManualEta(): string {
  const d = new Date()
  d.setDate(d.getDate() + 7)
  const p = (n: number) => String(n).padStart(2, '0')
  return (
    `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}` +
    `T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
  )
}

/** Drawer 表单（新增 + 编辑 共用）
 *  - materialId / receivingWarehouse 后端必填（@see lps/LPS.APS.Core/Dto/ProcurementManualEtaOverride.cs）
 *  - materialKey：物料下拉选中的 masterId（number）；watch 同步 → materialId + materialCode
 */
const form = ref({
  poNo: '',
  lineNo: 1,
  materialId: 0,
  materialCode: '',
  materialKey: null as number | null,
  receivingWarehouse: '',
  manualEta: defaultManualEta(),
  remark: ''
})

/** 抽屉「选物料」候选：**远程搜索 + 按采购订单收窄**
 *  - 收窄档（订单+行号 / 订单）：`GET /api/procurement-manual-eta/supply-combos?poNo=&lineNo=&limit=`
 *    （5号位 11-14 落码 r14214～r14218，挂**本域** aps.manual_eta.view）
 *    ⇒ 一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，**成对**给出
 *  - 兜底档（只按关键字）：`GET /api/procurement-manual-eta/materials?search=&limit=`
 *    ⇒ 只有物料、**没有仓库依据** ⇒ 选它时仓库仍要手工选（页面会如实提示可能不生效）
 *
 * ⚠️ 旧口径（11-14 上午）两个池子各选各的：物料候选无仓库维度（/materials 按 materialId 去重）、
 *   仓库候选是 258 条全集（/warehouses）⇒ 用户能拼出永远匹配不上的 (物料, 仓库)
 *   ⇒ 这条到货时间**静默不生效**（不报错、不提示）。故收窄档改走 supply-combos。
 */
/** 每次搜索最多取多少条（端点 limit；命中数等于它就说明可能被截断，页面要如实说） */
const MATERIAL_SEARCH_LIMIT = 50

/**
 * 抽屉候选（两种来源统一成一个形状）
 *  - 有 `storageCode` = 来自 supply-combos（物料与仓库**成对**，来自同一条供给事实）
 *  - 无 `storageCode` = 来自 /materials 关键字档（只有物料，没有仓库依据）
 */
interface MaterialCandidate {
  masterId: number
  code: string
  name: string
  spec: string
  uom: string
  /** 存储地点（= 供给事实 StorageCode，须原样写入表单「收货仓库」） */
  storageCode?: string
  /** 存储地点名（视图无此码 ⇒ 空） */
  storageName?: string
  /**
   * 该组合所属的订单行（供给事实 SourceDocumentLineNo）
   *  - 组合是**某一行的**事实 ⇒ 选中它就等于说「我要覆盖这一行」
   *  - ⇒ 选中时把表单行号对齐过去（否则行号与组合对不上 ⇒ 覆盖键命不中 ⇒ 静默不生效）
   */
  lineNo?: number
}

const materialOptions = ref<MaterialCandidate[]>([])
/** 正在搜索（ElSelect 的 loading 态） */
const materialSearching = ref(false)
/** 最近一次已发起搜索的关键字（空 = 还没搜过）⇒ 用于区分「没搜」与「搜了没结果」 */
const materialQuery = ref('')
/** 取候选失败（端点 403 / 网络）⇒ 必须说话，否则用户只看到空下拉 */
const materialSearchError = ref('')
/** 当前选中的候选项（选中后锁定，不受后续搜索替换候选列表影响） */
const materialSelected = ref<MaterialCandidate | null>(null)
/**
 * 候选口径的大白话说明（空 = 无需说明）。
 * 说明「你现在看到的是不是本订单的物料」——不说明就会让用户选错料。
 */
const materialScopeNote = ref('')
/** 仓库是否已由「物料 × 存储地点」组合带出（成对口径 ⇒ 不让用户再另选一个不配套的仓库） */
const warehouseLockedByCombo = ref(false)
/** 请求序号：只认最后一次搜索结果（先发的慢请求后到会盖掉新结果） */
let materialSearchSeq = 0
let materialSearchTimer: ReturnType<typeof setTimeout> | null = null

/** supply-combos 一行 → 候选 */
function comboToCandidate(c: ManualEtaSupplyCombo): MaterialCandidate {
  return {
    masterId: c.materialId,
    code: c.materialCode ?? '',
    name: c.materialName ?? '',
    spec: c.spec ?? '',
    uom: c.uom ?? '',
    storageCode: c.storageCode,
    storageName: c.storageName ?? undefined,
    lineNo: Number(c.lineNo) || undefined
  }
}

/** /materials 一行 → 候选（无仓库依据） */
function lookupToCandidate(m: MaterialLookupItem): MaterialCandidate {
  return { masterId: m.masterId, code: m.code, name: m.name, spec: m.spec, uom: m.uom }
}

/** 组合档没有关键字参数 ⇒ 本地按 编码/名称/规格 过滤（候选 ≤ limit 条，本地过滤足够） */
function comboMatchesKeyword(c: MaterialCandidate, kw: string): boolean {
  if (!kw) return true
  const k = kw.toLowerCase()
  return (
    c.code.toLowerCase().includes(k) ||
    c.name.toLowerCase().includes(k) ||
    c.spec.toLowerCase().includes(k)
  )
}

/**
 * 取物料候选：**逐级放宽**，收窄不阻断录入。
 *
 * 顺序：① 订单 + 行号 → ② 只按订单 → ③ 只按关键字（全库）。
 * Why 逐级放宽：端点查无返空集合（200 + 0 行）——PO 事实未同步、或用户行号填错时
 *   收窄结果就是空；若就此卡住，用户录不进去还以为是系统坏了。
 *   每退一级都在提示里说明口径（不能让人把全库物料误当成本订单的物料）。
 *
 * ❌ 11-14 撤掉的第四档「本表已登记」（曾按本表真实行给候选，已回退）：
 *   人工到货时间是**对供给事实的覆盖**——后端按 (PONo, LineNo, MaterialId, ReceivingWarehouse)
 *   精确匹配供给事实的 (SourceDocumentNo, SourceDocumentLineNo, MaterialId, StorageCode)
 *   （@see lps/LPS.APS.Application/Services/AvailableTimeCalculator.cs:63-73,105-107），
 *   匹配不上 ⇒ 这条到货时间**静默不生效**（不报错、不提示）。
 *   dev 实测：本表 10 个 (物料, 仓库) 组合在供给事实里**一个都不存在** ⇒ 兜底档给的候选
 *   同样命不中 ⇒ 用户录进去的还是不生效的记录，只是「看起来能录」⇒ 撤掉更诚实。
 */
/** 收窄档（supply-combos）是否取不到（端点不可用 / 权限 / 网络）——用于如实提示，不静默降级 */
const comboTierFailed = ref(false)
/** 本次候选是否**成对**（带真实存储地点）
 *  false = 退到了「无仓库依据」档：物料是真的，但**取不到存储地点**
 *          ⇒ 仓库仍要用户自己选（选错 ⇒ 这条到货时间静默不生效） */
const comboPaired = ref(false)

/**
 * 该订单行的**成对**候选（物料 × 存储地点）
 *  - 正式口径只有一条路：`supply-combos`（矩阵 A13「撤临时兜底档」）
 *  - 取不到 ⇒ 返 null（调用方退到「无仓库依据」档），并记一笔端点不可用如实提示
 */
async function fetchPairedCandidates(
  q: string,
  t: { poNo?: string; lineNo?: number }
): Promise<MaterialCandidate[] | null> {
  try {
    const combos = await manualEtaApi.searchSupplyCombos(
      t.poNo ?? '',
      t.lineNo,
      MATERIAL_SEARCH_LIMIT
    )
    const rows = combos.map(comboToCandidate).filter((c) => comboMatchesKeyword(c, q))
    if (rows.length) {
      comboPaired.value = true
      return rows
    }
    return null
  } catch {
    // 正式端点取不到 ⇒ 记一笔，页面如实提示（不静默换口径）
    comboTierFailed.value = true
    return null
  }
}

async function fetchMaterialCandidates(
  q: string,
  poNo: string,
  lineNo: number
): Promise<{ rows: MaterialCandidate[]; scope: 'line' | 'po' | 'all' }> {
  const tries: Array<{ scope: 'line' | 'po' | 'all'; poNo?: string; lineNo?: number }> = []
  if (poNo) {
    tries.push({ scope: 'line', poNo, lineNo })
    tries.push({ scope: 'po', poNo })
  }
  tries.push({ scope: 'all' })
  for (const t of tries) {
    let rows: MaterialCandidate[] = []
    if (t.scope === 'all') {
      rows = (await manualEtaApi.searchMaterials(q, MATERIAL_SEARCH_LIMIT)).map(lookupToCandidate)
    } else {
      const paired = await fetchPairedCandidates(q, t)
      if (paired && paired.length) {
        rows = paired
      } else {
        // 无仓库依据档：物料是真的，但取不到存储地点 ⇒ 仓库要用户自己选
        try {
          rows = (
            await manualEtaApi.searchMaterials(q, MATERIAL_SEARCH_LIMIT, {
              poNo: t.poNo,
              lineNo: t.lineNo
            })
          ).map(lookupToCandidate)
        } catch {
          continue
        }
      }
    }
    if (rows.length) return { rows, scope: t.scope }
  }
  return { rows: [], scope: 'all' }
}

async function searchMaterialOptions(query: string): Promise<void> {
  const q = (query ?? '').trim()
  const seq = ++materialSearchSeq
  const poNo = form.value.poNo.trim()
  const lineNo = Number(form.value.lineNo)
  if (!q && !poNo) {
    // 既没关键字也没采购订单号 ⇒ 不去要全库物料（那会把几百上千条物料拉回来列全）
    materialOptions.value = []
    materialQuery.value = ''
    materialSearchError.value = ''
    materialScopeNote.value = ''
    materialSearching.value = false
    return
  }
  materialSearching.value = true
  comboTierFailed.value = false
  comboPaired.value = false
  try {
    const { rows, scope } = await fetchMaterialCandidates(q, poNo, lineNo)
    if (seq !== materialSearchSeq) return
    // 已选中的那一项始终留在候选里（否则新结果把它顶掉 ⇒ 下拉框显示成一把裸键码）
    const picked = materialSelected.value
    const merged =
      picked && !rows.some((r) => pickKeyOf(r) === pickKeyOf(picked)) ? [picked, ...rows] : rows
    materialOptions.value = merged
    materialQuery.value = q
    materialSearchError.value = ''
    materialScopeNote.value = materialScopeNoteOf(
      scope,
      poNo,
      lineNo,
      rows.length,
      comboPaired.value,
      comboTierFailed.value
    )
  } catch {
    if (seq !== materialSearchSeq) return
    materialOptions.value = []
    materialQuery.value = q
    materialSearchError.value =
      '取不到物料候选：请确认账号有「人工到货时间」查看权限，或联系管理员。'
  } finally {
    if (seq === materialSearchSeq) materialSearching.value = false
  }
}

/** 候选口径说明（大白话，一行说完；不编造任何端点没给的事实） */
function materialScopeNoteOf(
  scope: 'line' | 'po' | 'all',
  poNo: string,
  lineNo: number,
  count: number,
  paired = false,
  comboFailed = false
): string {
  const pairedTail = '仓库取自该订单行的实际存储地点。'
  if (scope === 'line') {
    return paired
      ? `已按采购订单 ${poNo} 第 ${lineNo} 行收窄，共 ${count} 种物料（选中后仓库自动带出）。${pairedTail}`
      : `已按采购订单 ${poNo} 第 ${lineNo} 行收窄，共 ${count} 种物料；取不到实际存储地点，仓库要自己选（要与实际存储地点一致，否则不生效）。`
  }
  if (scope === 'po') {
    return paired
      ? `采购订单 ${poNo} 共 ${count} 种物料（选中后仓库自动带出）。${pairedTail}`
      : `采购订单 ${poNo} 共 ${count} 种物料；取不到实际存储地点，仓库要自己选（要与实际存储地点一致，否则不生效）。`
  }
  const failed = comboFailed ? '（按订单收窄的供货组合接口暂不可用，后端还没部署新版本）' : ''
  if (poNo)
    return `采购订单 ${poNo} 没取到物料，下面是按关键字搜到的 ${count} 种，请确认是本订单的物料；这类候选没有仓库依据，仓库要自己选，可能不生效。${failed}`
  return '按关键字搜到的候选没有该订单的供货依据，仓库要自己选，可能不生效。'
}

/** ElSelect 远程搜索入口（防抖 300ms，避免每敲一个键就打一次后端） */
function onMaterialSearch(query: string): void {
  if (materialSearchTimer) clearTimeout(materialSearchTimer)
  materialSearchTimer = setTimeout(() => {
    void searchMaterialOptions(query)
  }, 300)
}

/**
 * 采购订单号 / 行号改了 ⇒ 重新取候选（收窄口径变了，旧的候选已经不对）
 *  - 新增态才需要（编辑态物料不可改，下拉是禁用的）
 *  - 沿用最近一次关键字（用户搜过就保留），没有关键字就按订单直接列
 *  - ⚠️ 顺带**清掉已选物料**：组合档的物料+仓库是**按该订单行**带出来的，
 *    换了订单号还留着上一单的组合 ⇒ 存进去必然命不中（静默不生效）。
 */
watch(
  () => [form.value.poNo, form.value.lineNo] as const,
  () => {
    if (drawerMode.value === 'edit') return
    materialSelected.value = null
    form.value.materialKey = null
    form.value.materialId = 0
    form.value.materialCode = ''
    if (warehouseLockedByCombo.value) {
      form.value.receivingWarehouse = ''
      warehouseLockedByCombo.value = false
    }
    if (materialSearchTimer) clearTimeout(materialSearchTimer)
    materialSearchTimer = setTimeout(() => {
      void searchMaterialOptions(materialQuery.value)
    }, 300)
  }
)

/** 下拉项文案：名称（编码）· 规格；没有名称就只给编码（不编造名称）
 *  ⚠️ 规格要露出来（3号位 2026-11-13 附问二：search 命中「编码 / 名称 / 规格」三路）：
 *  用户按规格搜到的候选若只显示「名称（编码）」，看不出自己命中的是哪条规格 ⇒ 会以为搜不准。
 *  - 有存储地点的候选（来自 supply-combos）把**仓库也写进选项**：用户一眼能看出
 *    「这个物料配这个仓库」，也才知道选它之后仓库会被带成什么。
 */
function materialOptionText(m: MaterialCandidate): string {
  const base = m.name ? `${m.name}（${m.code}）` : m.code
  const spec = m.spec ? ` · ${m.spec}` : ''
  // 行号与当前表单不一致时才显示（否则用户会把别的行的组合当本行用）
  const line = m.lineNo && m.lineNo !== form.value.lineNo ? ` · 第 ${m.lineNo} 行` : ''
  if (!m.storageCode) return `${base}${spec}${line}`
  const wh = m.storageName ? `${m.storageName}（${m.storageCode}）` : m.storageCode
  return `${base}${spec}${line} · 仓库 ${wh}`
}

/** 选中候选的复合键：物料 ID + 存储地点 + 行号
 *  - 同一物料可能对应多个存储地点、同一物料可能在多行出现 ⇒ 三者齐全才唯一 */
function pickKeyOf(c: MaterialCandidate): string {
  return `${c.masterId}@${c.storageCode ?? ''}@${c.lineNo ?? ''}`
}

/** 收货仓库下拉数据源（5号位 2026-10-08 落地）
 *  - 端点：GET /api/procurement-manual-eta/warehouses?search=（非 /api/governance/lookups/warehouses）
 *  - 数据源 ext_MES_ProcessCode_View；返回 [{ code, name }]
 *  - 失败兜底：空数组（提示「暂无仓库数据」）
 */
const warehouseOptions = ref<WarehouseOption[]>([])
async function loadWarehouseOptions(): Promise<void> {
  try {
    warehouseOptions.value = await manualEtaApi.listWarehouses()
  } catch {
    warehouseOptions.value = []
  }
}

/** 仓库编码 → name 映射（用于表格列展示）
 *  - 数据源 warehouseOptions（已通过 listWarehouses 异步加载）
 *  - 兜底：name 找不到时只显示 code（脏数据 / 测试输入 / view 未覆盖）
 */
const warehouseNameMap = computed(() => {
  const m = new Map<string, string>()
  for (const w of warehouseOptions.value) m.set(w.code, w.name)
  return m
})

/** 物料 ID → 中文名称（物料主数据解析）
 *  - 11-13：列表行的物料名改由后端行内 `materialName` 直接给（5号位 R5 落码）
 *  - 11-14：抽屉下拉改远程搜索 ⇒ 这份映射只剩「行内没带 materialName 时」的兜底作用
 */
const materialNameMap = computed(() => {
  const m = new Map<number, string>()
  for (const x of materialOptions.value) m.set(x.masterId, x.name)
  return m
})

/** 物料编码 → 中文名称（从**列表行**带回来的 materialName 归集；解析不到就只给编码）
 *  - 数据源 = 后端行内字段（不额外请求、不依赖物料主数据端点）
 */
const materialCodeNameMap = computed(() => {
  const m = new Map<string, string>()
  for (const row of list.value) {
    if (row.materialCode && row.materialName && !m.has(row.materialCode)) {
      m.set(row.materialCode, row.materialName)
    }
  }
  return m
})

/** 列表行物料展示：名称（编码）
 *  - 优先用后端行内 materialName（R5）；行内没有时回退主数据映射；再没有就只给编码（不编造名称）
 */
function materialText(row: ManualEtaDto): string {
  const name = row.materialName ?? materialNameMap.value.get(row.materialId)
  return name ? `${name}（${row.materialCode}）` : row.materialCode
}

/** 筛选下拉物料展示：名称（编码）；解析不到只给编码 */
function materialOptionLabel(code: string): string {
  const name = materialCodeNameMap.value.get(code)
  return name ? `${name}（${code}）` : code
}

/** 物料下拉 watch：选中 masterId → 同步 materialId + materialCode
 *  - 远程搜索后候选列表会被新的搜索结果替换 ⇒ 选中项另存一份（materialSelected），
 *    免得「选完又搜一次」把选中项冲掉、提交时 materialId 变成 0
 */
watch(
  () => form.value.materialKey,
  (key) => {
    if (key == null) {
      materialSelected.value = null
      form.value.materialId = 0
      form.value.materialCode = ''
      return
    }
    const found =
      materialSelected.value?.masterId === key
        ? materialSelected.value
        : materialOptions.value.find((m) => m.masterId === key)
    if (found) {
      form.value.materialId = found.masterId
      form.value.materialCode = found.code
    }
  }
)

/** 物料下拉 v-model 包装（**复合键 = 物料 ID + 存储地点**）
 *  - getter：当前选中候选的复合键（null = 未选）
 *  - setter：选中即同步 materialId / materialCode；候选带存储地点时，
 *    **仓库按供给事实原样带出**（成对口径 ⇒ 选完就能命中，不给用户拼错的机会）
 */
const materialKeyModel = computed<string | undefined>({
  get: () => (materialSelected.value ? pickKeyOf(materialSelected.value) : undefined),
  set: (v) => {
    if (v == null) {
      materialSelected.value = null
      form.value.materialKey = null
      form.value.materialId = 0
      form.value.materialCode = ''
      if (warehouseLockedByCombo.value) {
        form.value.receivingWarehouse = ''
        warehouseLockedByCombo.value = false
      }
      return
    }
    const found = materialOptions.value.find((m) => pickKeyOf(m) === v) ?? null
    materialSelected.value = found
    if (!found) return
    form.value.materialKey = found.masterId
    form.value.materialId = found.masterId
    form.value.materialCode = found.code
    // 组合是**某一行的**事实 ⇒ 行号对齐过去（行号对不上 ⇒ 覆盖键命不中 ⇒ 静默不生效）
    if (found.lineNo && found.lineNo !== form.value.lineNo) {
      form.value.lineNo = found.lineNo
    }
    if (found.storageCode) {
      form.value.receivingWarehouse = found.storageCode
      warehouseLockedByCombo.value = true
    }
  }
})

/** 字段级错误 */
const formRules = {
  poNo: [
    { required: true, message: '请输入采购订单', trigger: 'blur' },
    { min: 3, message: '采购订单至少 3 个字符', trigger: 'blur' }
  ],
  lineNo: [
    { required: true, type: 'number' as const, min: 1, message: '行号 ≥ 1', trigger: 'blur' }
  ],
  materialId: [
    { required: true, type: 'number' as const, min: 1, message: '物料 ID ≥ 1', trigger: 'blur' }
  ],
  materialKey: [{ required: true, message: '请选择物料', trigger: 'change' }],
  receivingWarehouse: [
    { required: true, message: '请选择收货仓库', trigger: 'change' },
    { min: 2, message: '仓库编码至少 2 个字符', trigger: 'blur' }
  ],
  manualEta: [{ required: true, message: '请选择到货时间', trigger: 'change' }]
}

/**
 * 新增抽屉。
 *
 * `prefill` 用来接住「采购协同」页带来的定位参数（PO 号 + 行号）：
 * 人工到货时间是**对供给事实的覆盖**，覆盖键 = PO＋行号＋物料＋收货仓库，
 * 采购协同那一行已经带了 PO 号与行号 ⇒ 直接带过来，物料候选也会按这行自动收窄。
 */
function openCreate(prefill?: { poNo?: string; lineNo?: number }): void {
  drawerMode.value = 'new'
  // 物料候选不再预拉（远程搜索）⇒ 新增态从空开始，等用户输入关键字
  materialOptions.value = []
  materialQuery.value = ''
  materialSearchError.value = ''
  materialSelected.value = null
  warehouseLockedByCombo.value = false
  form.value = {
    poNo: prefill?.poNo ?? '',
    lineNo: prefill?.lineNo ?? 1,
    materialId: 0,
    materialCode: '',
    materialKey: null,
    receivingWarehouse: '',
    manualEta: defaultManualEta(),
    remark: ''
  }
  store.openDetail(null)
  drawerVisible.value = true
}

function openEdit(dto: ManualEtaDto): void {
  drawerMode.value = 'edit'
  // 编辑态的物料**不可改**（MaterialId 是复合主键的一部分）⇒ 候选用该行自带的物料信息占位，
  // 不再靠「先拉 200 条主数据再反查」；materialName 由后端行内给出（5号位 R5 投影），
  // 解析不到就只显示编码（不编造名称）。
  materialOptions.value = dto.materialId
    ? [
        {
          masterId: dto.materialId,
          code: dto.materialCode ?? '',
          name: dto.materialName ?? '',
          spec: '',
          uom: ''
        }
      ]
    : []
  materialSelected.value = materialOptions.value[0] ?? null
  materialQuery.value = ''
  materialSearchError.value = ''
  // 编辑态仓库照抄该行原值（编辑态只能改到货时间/备注；物料与仓库是复合主键的一部分）
  warehouseLockedByCombo.value = false
  form.value = {
    poNo: dto.poNo,
    lineNo: dto.lineNo,
    materialId: dto.materialId,
    materialCode: dto.materialCode ?? '',
    materialKey: dto.materialId || null,
    receivingWarehouse: dto.receivingWarehouse ?? '',
    manualEta: dto.manualEta,
    remark: dto.remark ?? ''
  }
  store.openDetail(dto)
  drawerVisible.value = true
}

function closeDrawer(): void {
  drawerVisible.value = false
  store.openDetail(null)
}

/** 提交（新增 / 编辑 共用 upsert） */
async function submitDrawer(): Promise<void> {
  if (!formRef.value) return
  let valid = false
  try {
    valid = await formRef.value.validate()
  } catch {
    return
  }
  if (!valid) return

  const actor = apsAuth.userInfo?.userCode ?? 'mock-pmc'
  const input: ManualEtaUpsertInput = {
    poNo: form.value.poNo.trim(),
    lineNo: Number(form.value.lineNo),
    materialId: Number(form.value.materialId),
    materialCode: form.value.materialCode.trim() || undefined,
    receivingWarehouse: form.value.receivingWarehouse.trim(),
    manualEta: form.value.manualEta,
    isActive: true, // 新增/编辑都强制 active；取消走独立按钮
    remark: form.value.remark.trim() || undefined,
    actor
  }
  try {
    await store.upsert(input)
    ElMessage.success(
      `${drawerMode.value === 'new' ? '新增' : '更新'}人工到货时间成功：${input.poNo} / Line ${input.lineNo}`
    )
    closeDrawer()
    // 指标条数字来自后端 COUNT ⇒ 写完必须重查，否则指标与列表对不上
    await reload()
  } catch {
    ElMessage.error('保存失败，请检查表单或网络')
  }
}

/** 取消 ETA（软删除：isActive=false） */
async function handleCancel(dto: ManualEtaDto): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `将取消人工到货时间：${dto.poNo} / Line ${dto.lineNo}\n\n` +
        `操作：保留记录并标记为已取消（便于事后审计）\n` +
        `请确认。`,
      '取消人工到货时间',
      {
        confirmButtonText: '确认取消',
        cancelButtonText: '不取消',
        type: 'warning'
      }
    )
  } catch {
    return
  }
  const actor = apsAuth.userInfo?.userCode ?? 'mock-pmc'
  try {
    await store.cancel({
      poNo: dto.poNo,
      lineNo: dto.lineNo,
      materialId: dto.materialId,
      receivingWarehouse: dto.receivingWarehouse ?? '',
      actor
    })
    ElMessage.success(`已取消 ${dto.poNo} / Line ${dto.lineNo}`)
    await reload()
  } catch {
    ElMessage.error('取消失败')
  }
}

onMounted(() => {
  void reload()
  void store.loadCandidates()
  void loadWarehouseOptions()
  // 2026-11-20：从「采购协同」页带定位参数进来 ⇒ 直接开新增抽屉，采购订单号/行号已填好
  //   （v0.2 稿 §采购协同 6：人工 ETA 按 PO＋行＋物料＋收货仓库 粒度维护）
  const q = route.query
  const po = typeof q.po === 'string' ? q.po.trim() : ''
  if (po) {
    const rawLine = Number(q.line)
    const line = Number.isFinite(rawLine) && rawLine > 0 ? Math.floor(rawLine) : 1
    openCreate({ poNo: po, lineNo: line })
  }
})
</script>

<template>
  <div class="aps-manual-eta">
    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />

    <!-- ===== 指标条（一行紧凑；数字即分类入口）===== -->
    <div class="kpi-row">
      <button
        v-for="k in kpiItems"
        :key="k.key"
        type="button"
        class="kpi-item"
        :class="{
          'is-active': k.clickable && bucket === k.key,
          'is-static': !k.clickable
        }"
        :disabled="!k.clickable"
        @click="onKpiClick(k)"
      >
        <span class="kpi-bar" :style="{ background: k.color }"></span>
        <span class="kpi-label">{{ k.label }}</span>
        <span class="kpi-value" :style="{ color: k.color }">{{ k.value }}</span>
        <span class="kpi-ratio">{{ k.hint }}</span>
      </button>
      <span class="kpi-caption">数字与下方列表同一口径（当前筛选）；点一下可只看那一类。</span>
    </div>

    <!-- ===== 筛选（变更即生效）===== -->
    <ElCard class="filter-bar">
      <div class="filter-row">
        <div class="filter-item">
          <ElSelect
            v-model="selectedPoNos"
            multiple
            filterable
            clearable
            collapse-tags
            placeholder="全部采购订单"
            style="width: 220px"
            @change="onFilterChange"
          >
            <ElOption v-for="p in poNoOptions" :key="p" :label="p" :value="p" />
          </ElSelect>
        </div>
        <div class="filter-item">
          <ElSelect
            v-model="selectedMaterialCodes"
            multiple
            filterable
            clearable
            collapse-tags
            placeholder="全部物料"
            style="width: 240px"
            @change="onFilterChange"
          >
            <ElOption
              v-for="m in materialFilterOptions"
              :key="m"
              :label="materialOptionLabel(m)"
              :value="m"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <ElSelect
            v-model="selectedWarehouses"
            multiple
            filterable
            clearable
            collapse-tags
            placeholder="全部收货仓库"
            style="width: 240px"
            @change="onFilterChange"
          >
            <ElOption
              v-for="w in warehouseOptions"
              :key="w.code"
              :label="`${w.name}（${w.code}）`"
              :value="w.code"
            />
          </ElSelect>
        </div>
        <div class="filter-actions">
          <ElSelect
            v-model="sortKey"
            style="width: 190px"
            placeholder="默认排序"
            @change="onSortChange"
          >
            <ElOption
              v-for="o in SORT_OPTIONS"
              :key="o.value || 'default'"
              :label="o.label"
              :value="o.value"
            />
          </ElSelect>
          <ElButton @click="resetFilters">重置</ElButton>
        </div>
      </div>
      <span v-if="facetsTruncated" class="muted small">
        候选太多，采购订单 / 物料下拉只列了前 500 条（可输入关键字在其中筛选）。
      </span>
    </ElCard>

    <!-- ===== 主表格 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span
            >人工到货时间<span class="muted">（共 {{ total }} 条）</span></span
          >
          <div class="panel-actions">
            <ElButton size="small" :loading="loading" @click="refresh">
              <Icon icon="vi-ep:refresh" />
              刷新
            </ElButton>
            <ElButton size="small" type="primary" :disabled="!canWrite" @click="openCreate">
              <Icon icon="vi-mdi:plus" />
              新增
            </ElButton>
          </div>
        </div>
      </template>
      <!-- ⚠️ 加载中不要显示空态：初次进页面时 list 还是空数组，
             若直接渲染「还没有人工到货时间记录」⇒ 用户会以为库里真的一条都没有（假空态） -->
      <div v-if="loading && !list.length" class="panel-loading">
        <span class="muted">正在加载人工到货时间…</span>
      </div>
      <div v-else-if="!list.length" class="panel-empty">
        <ElEmpty v-if="hasFilter" description="当前筛选没有匹配的记录">
          <ElButton @click="resetFilters">清除筛选</ElButton>
        </ElEmpty>
        <ElEmpty v-else description="还没有人工到货时间记录，点右上角「新增」录入" />
      </div>
      <ElTable
        v-else
        :data="list"
        size="small"
        border
        stripe
        row-class-name="row-clickable"
        style="width: 100%"
      >
        <ElTableColumn label="采购订单" prop="poNo" width="130" />
        <ElTableColumn label="行号" prop="lineNo" width="64" align="center" />
        <ElTableColumn label="物料" min-width="200">
          <template #default="{ row }">
            <span>{{ materialText(row) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="收货仓库" min-width="180">
          <template #default="{ row }">
            <span v-if="row?.receivingWarehouse">{{ row.receivingWarehouse }}</span>
            <span v-else class="muted">-</span>
            <span
              v-if="row?.receivingWarehouse && warehouseNameMap.get(row.receivingWarehouse)"
              class="muted small"
            >
              · {{ warehouseNameMap.get(row.receivingWarehouse) }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="到货时间" width="176">
          <template #default="{ row }">
            <span :class="{ expired: row.isActive && etaRelative(row.manualEta).overdue }">
              {{ fmtDate(row.manualEta) }}
            </span>
            <span
              v-if="etaRelative(row.manualEta).text"
              class="muted small"
              :class="{ expired: row.isActive && etaRelative(row.manualEta).overdue }"
            >
              · {{ etaRelative(row.manualEta).text }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="状态" width="84" align="center">
          <template #default="{ row }">
            <ElTag :type="STATUS_TAG[statusOf(row)]" size="small">
              {{ STATUS_LABEL[statusOf(row)] }}
            </ElTag>
          </template>
        </ElTableColumn>
        <!-- 是否参与排产（5号位 v2.0 R2 取乙：计算式标注，true/false/null 三态）
             ⚠️ null = 未判定（后端取数失败），**不得显示成「未命中」** -->
        <ElTableColumn label="是否参与排产" width="168" align="center">
          <template #default="{ row }">
            <ElTooltip
              v-if="row.isMatched === false"
              content="这条到货时间没命中该订单行当前的供货事实（物料或仓库对不上），不会参与排产。"
              placement="top"
            >
              <ElTag type="danger" size="small">未命中当前供货事实（暂不生效）</ElTag>
            </ElTooltip>
            <ElTag v-else-if="row.isMatched === true" type="success" size="small">
              会参与排产
            </ElTag>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="备注" min-width="150">
          <template #default="{ row }">
            <ElTooltip v-if="row.remark" :content="row.remark" placement="top">
              <span class="remark-cell">{{ row.remark }}</span>
            </ElTooltip>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="更新人 / 更新时间" min-width="170">
          <template #default="{ row }">
            <span class="muted code-tag">{{ row.updatedBy }}</span>
            <span class="muted small"> · {{ fmtDateTime(row.updatedAt) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="170" fixed="right" align="center">
          <template #default="{ row }">
            <ElButton size="small" plain :disabled="!canWrite" @click="openEdit(row)">
              <Icon icon="vi-mdi:pencil" />
              编辑
            </ElButton>
            <ElButton
              size="small"
              type="danger"
              plain
              :disabled="!canCancel || !row.isActive"
              @click="handleCancel(row)"
            >
              <Icon icon="vi-mdi:cancel" />
              取消
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-if="total > 0" class="pagination-bar">
        <ElPagination
          :current-page="pageIndex"
          :page-size="pageSize"
          :total="total"
          :page-sizes="[10, 20, 50, 100]"
          layout="total, sizes, prev, pager, next, jumper"
          background
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </ElCard>

    <!-- ===== 新增 / 编辑 Drawer ===== -->
    <ElDrawer
      v-model="drawerVisible"
      :title="drawerMode === 'new' ? '新增人工到货时间' : '编辑人工到货时间'"
      size="540px"
      direction="rtl"
      :close-on-click-modal="false"
      @close="closeDrawer"
    >
      <div class="manual-eta-drawer">
        <ElForm
          ref="formRef"
          :model="form"
          :rules="formRules"
          label-width="100px"
          label-position="right"
          class="manual-eta-form"
        >
          <ElFormItem label="采购订单" prop="poNo">
            <ElInput
              v-model="form.poNo"
              :disabled="drawerMode === 'edit'"
              placeholder="例如：PO-2026-1001"
            />
          </ElFormItem>
          <ElFormItem label="行号" prop="lineNo">
            <ElInput
              v-model.number="form.lineNo"
              :disabled="drawerMode === 'edit'"
              type="number"
              :min="1"
              placeholder="≥ 1"
            />
          </ElFormItem>
          <ElFormItem label="物料" prop="materialKey">
            <ElSelect
              v-model="materialKeyModel"
              :disabled="drawerMode === 'edit'"
              placeholder="输入物料编码、名称或规格搜索"
              filterable
              remote
              :remote-method="onMaterialSearch"
              :loading="materialSearching"
              clearable
              style="width: 100%"
            >
              <ElOption
                v-for="m in materialOptions"
                :key="pickKeyOf(m)"
                :label="materialOptionText(m)"
                :value="pickKeyOf(m)"
              />
            </ElSelect>
            <!-- 空态要分得开：还没搜 / 搜了没结果 / 取不到（权限或网络）——用户分不出来就会以为系统坏了 -->
            <span v-if="materialSearchError" class="muted small">{{ materialSearchError }}</span>
            <span
              v-else-if="drawerMode === 'new' && !materialQuery && !form.poNo.trim()"
              class="muted small"
            >
              输入物料编码、名称或规格的关键字即可搜索；填了采购订单号会直接列出该订单实际供货的物料和仓库。
            </span>
            <span v-else-if="!materialOptions.length" class="muted small">
              <template v-if="comboTierFailed">
                按订单收窄的供货组合接口暂时取不到（后端可能还没部署新版本）；请核对采购订单号与行号，或清空采购订单号按关键字搜索。
              </template>
              <template v-else>
                没查到物料：该采购订单在供货数据里没有对应的物料，请核对采购订单号与行号；也可清空采购订单号，按关键字搜索全部物料。
              </template>
            </span>
            <span
              v-else-if="materialScopeNote || materialOptions.length >= MATERIAL_SEARCH_LIMIT"
              class="muted small"
            >
              {{ materialScopeNote }}
              <template v-if="materialOptions.length >= MATERIAL_SEARCH_LIMIT">
                只列了前 {{ MATERIAL_SEARCH_LIMIT }} 条，继续输入关键字可缩小范围。
              </template>
            </span>
          </ElFormItem>
          <ElFormItem label="收货仓库" prop="receivingWarehouse">
            <!-- 成对口径（11-14）：选了带存储地点的候选 ⇒ 仓库由该供货事实带出并锁定，
                 用户要换仓库只能换物料（换回不配套的组合，这条到货时间就静默不生效） -->
            <div v-if="warehouseLockedByCombo" class="warehouse-locked">
              <span class="code-tag">{{ form.receivingWarehouse }}</span>
              <span v-if="materialSelected?.storageName" class="muted small">
                · {{ materialSelected.storageName }}
              </span>
              <span class="muted small"
                >已按该订单行的实际存储地点带出（来自同一条供货事实）。</span
              >
            </div>
            <ElSelect
              v-else
              v-model="form.receivingWarehouse"
              placeholder="请选择收货仓库"
              filterable
              clearable
              style="width: 100%"
            >
              <ElOption
                v-for="w in warehouseOptions"
                :key="w.code"
                :label="`${w.name}（${w.code}）`"
                :value="w.code"
              />
            </ElSelect>
            <!-- 仓库是覆盖键的一半：必须与该订单行的实际存储地点一致，否则这条到货时间不参与排产
                 （后端按 (PONo, LineNo, MaterialId, ReceivingWarehouse) 精确匹配供给事实的 StorageCode） -->
            <span v-if="!warehouseLockedByCombo" class="muted small">
              仓库须与该订单行的实际存储地点一致，否则这条到货时间不会参与排产。
            </span>
          </ElFormItem>
          <ElFormItem label="到货时间" prop="manualEta">
            <!-- 11-12 修：原 value-format 带字面量 [Z] ⇒ 存的是 UTC 串、回读按本地解析偏 8 小时 -->
            <ElDatePicker
              v-model="form.manualEta"
              type="datetime"
              value-format="YYYY-MM-DDTHH:mm:ss"
              placeholder="选择到货预期时间"
              style="width: 100%"
            />
          </ElFormItem>
          <ElFormItem label="备注">
            <ElInput
              v-model="form.remark"
              type="textarea"
              :rows="3"
              placeholder="可选"
              maxlength="200"
              show-word-limit
            />
          </ElFormItem>
        </ElForm>

        <div v-if="currentDetail" class="audit-block">
          <ElDescriptions :column="1" size="small" title="审计字段（只读）" border>
            <ElDescriptionsItem label="创建人">
              {{ currentDetail.createdBy ?? '-' }} ·
              {{ currentDetail.createdAt ? fmtDateTime(currentDetail.createdAt) : '-' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="最后更新">
              {{ currentDetail.updatedBy }} · {{ fmtDateTime(currentDetail.updatedAt) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="是否参与排产">
              <span v-if="currentDetail.isMatched === true">会参与排产</span>
              <span v-else-if="currentDetail.isMatched === false" class="expired">
                未命中当前供货事实（暂不生效）
              </span>
              <span v-else class="muted">未判定</span>
            </ElDescriptionsItem>
          </ElDescriptions>
        </div>

        <div class="drawer-actions">
          <ElButton @click="closeDrawer">取消</ElButton>
          <ElButton type="primary" :loading="saving" @click="submitDrawer">
            <Icon icon="vi-mdi:content-save" />
            {{ drawerMode === 'new' ? '新增' : '保存' }}
          </ElButton>
        </div>
      </div>
    </ElDrawer>
  </div>
</template>

<style lang="less" scoped>
.aps-manual-eta {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 12px;
}

/* KPI：一行紧凑指标条（沿用订单页样式；数字即分类入口） */
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
  border: none;
  border-right: 1px solid #e2e8f0;
  background: none;
  font: inherit;
  color: inherit;
  text-align: left;

  &:last-of-type {
    border-right: none;
  }

  &:not(.is-static) {
    cursor: pointer;
    border-radius: 4px;

    &:hover {
      background: #eef2f7;
    }
  }

  &.is-static {
    cursor: default;
    opacity: 1;
  }

  &.is-active {
    background: #e6f0ff;
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
}

.panel-actions {
  display: flex;
  gap: 8px;
}

.panel-empty {
  padding: 20px 0;
}

.panel-loading {
  padding: 24px 0;
  text-align: center;
}

.pagination-bar {
  display: flex;
  padding: 16px 0 4px;
  justify-content: flex-end;
}

.muted {
  color: #94a3b8;
}

.small {
  font-size: 11px;
}

.code-tag {
  padding: 1px 6px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  background: #f1f5f9;
  border-radius: 3px;
}

.expired {
  font-weight: 600;
  color: #b91c1c;
}

.remark-cell {
  display: block;
  max-width: 150px;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 筛选条 */
.filter-row {
  display: flex;
  align-items: flex-end;
  flex-wrap: wrap;
  gap: 14px;
}

.filter-item {
  display: flex;
  align-items: center;
  gap: 4px;
}

.filter-actions {
  display: flex;
  margin-left: auto;
  gap: 8px;
}

/* Drawer */
.manual-eta-drawer {
  display: flex;
  padding: 0 4px;
  flex-direction: column;
  gap: 12px;
}

.manual-eta-form {
  :deep(.el-form-item) {
    margin-bottom: 14px;
  }
}

.audit-block {
  padding: 10px 12px;
  background: #f8fafc;
  border: 1px dashed #e2e8f0;
  border-radius: 6px;
}

.drawer-actions {
  display: flex;
  padding: 12px 0;
  border-top: 1px solid #e2e8f0;
  justify-content: flex-end;
  gap: 8px;
}

:deep(.row-clickable) {
  cursor: default; /* 当前行不点开 drawer，drawer 由 [编辑] 按钮触发；避免与 Gantt 点击冲突 */
}
</style>
