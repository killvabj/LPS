<!--
  APS V1 4号位 — 资源日历维护页（单页 3 Tab）

  依据：
    - 冻结文档 v1.3《APS_V1_Resource_Calendar资源日历能力补充冻结方案_v1.3_人工能力槽模型与职责边界修订版》
        §八 4号位 = 资源能力维护页面 Owner，维护 3 类对象：
          ① 设备资源日历（Resource + ResourceCalendarSlot）
          ② 人工能力槽（ManualCapacitySlot）
          ③ 人工能力槽日历（ManualCapacitySlotCalendar）
    - 冻结文档 v1.4（2026-10-12 三级继承）§17/§19/§23 **定向覆盖 v1.3 §九**：
        · 本页 = 三级继承的第 3 级（工厂 → 部门 → 设备/人工槽），v1.4 line 19「保留设备及人工槽已有页面资产」
        · 「本级无窗口 = 不可排」**已废止**（废止清单 O-024）：本级 MISSING 允许继承上级已认证窗口，
          仅当**三级计算后仍无权威合法窗口**才判不可排
        · 因此本页「未铺窗口」提示只能陈述本级事实，不得断言该资源不可排
        · 三级合并状态端点 **5号位 已于 2026-10-14 落地**
          （`GET /api/resource-calendar/resolve-preview`，dev 实测 200；RC25b 动态探针守门）
    - 冻结 13 v1.8:31：资源日历须明文展示「继承上级 / 本级明确工作 / 本级明确不工作」及**来源授权**
      → 本页两个 Tab 各挂一块「三级合并后可排时段」（只读，ResolvePreviewPanel 组件）
    - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》（9 端点 / 6 权限码）

  页面结构：
    Tab1 设备资源日历：资源选择（资源主表 /api/resource 正式端点）→ 窗口列表 → 新增一段 / 批量铺窗 / 删窗
                       → 三级合并后可排时段（设备侧 resolve-preview）
    Tab2 人工能力槽  ：部门/工序过滤 + 含软删开关 → 主档列表 → 新增 / 查看窗口 / 铺窗 / 软删
    Tab3 人工槽日历  ：由 Tab2 选中行驱动 → 窗口列表 → 新增一段 / 批量铺窗 / 删窗
                       → 三级合并后可排时段（人工槽侧 resolve-preview，5号位 10-23 落码 / 10-26 上 dev）

  2026-10-14「放开一天多条」方向2（5号位 回执《放开一天多条已落地》）：
    - 同一天可登记多个**不重叠**时段（如上午 08-12 + 下午 13-17）
    - 「新增一段」= 单条追加，只插不删，与当天已有段重叠 → 后端 409 拒绝；相切合法
    - 「批量铺窗」= 覆盖该日期区间（先删后建），用于整段重铺

  RBAC（v1.2 §23.1）：
    - 路由级 apsRequiredPermissions = [aps.resource_calendar.view, aps.manual_capacity.view]（OR 语义）
    - 按钮级：canEditDevice / canDeleteDevice / canEditManual / canDeleteManual

  10-12 RC23 收（G1 已闭环 + 撤过渡）：
    - 旧「计划版本甘特资源池」候选 → 撤；改走 GET /api/resource（ResourceController.cs:34）
    - 旧「手填资源 ID」 → 撤；el-select-v2 虚拟滚动满足 7773+ records 性能
    - 旧「过渡数据源」用户提示 → 撤；当前实现已无"过渡/兜底"概念

  2026-10-14 RC27 收（撤 RC19）：
   - 撤顶部版本选择器（version-bar）—— 原为绕过"资源池取自甘特图、ACTIVE 版无甘特数据"的排查障碍而加，
     同日 RC23 已把资源池改走 GET /api/resource（基础主数据，与计划版本无关），障碍不复存在
   - 且本页从不读 gantt / summary，切版本只是触发一次本页用不上的取数；留着会误导用户
     以为资源日历按计划版本分版本（上级工作日历同为基础数据，两页口径现已一致）
   - 冻结 v1.4 §「4号位：资源能力维护页面Owner」只列 3 项维护对象，未要求计划版本锚点

  2026-11-16 工厂视角改版（零后端改动）：
   - **加日期范围筛选**（设备侧 + 人工槽侧）：一台设备一年 360 段窗口，原来只能一页页翻。
     后端 `GET /api/resource-calendar/{resourceId}` 与 `GET /api/manual-capacity/calendar/{manualSlotId}`
     本来就支持 from/to（语义＝窗口完整落在区间内，ResourceCalendarService.cs:202-206），前端此前一直没传。
     快捷：本周 / 本月 / 近 30 天 / 全部；不选＝看全部（不藏数据）。
   - **表格去内部 Id / 去冗余列**：撤「窗口编号（Id）」「资源编码」「资源名称」（同一台设备每行都一样，顶部已有），
     换成「日期（今天带红标）」「时段（08:00 ~ 17:00）」两列；Tab2 撤「人工槽编号（Id）」，Tab3 同理。
   - **提示文案去内部 Id**：删除确认由「删除设备窗口 #1827430」改成「删除 2026-09-29（周二）08:00~17:00 这一段」；
     新增/铺窗成功提示不再回显行号。
   - **术语**：Tab2「软删」→「停用」（工厂用户不认识"软删"）；撤顶栏副标题（与页签重复）；灰按钮加悬停禁用原因。
   - **徽标防误报**：带日期筛选时 res.total 只是区间内条数 ⇒ 「日历 已配/未配」改用资源主表的 hasCalendar
     （全量判定），空态提示区分「本级没登记」与「这段日期里没有」。
   - 每页条数 20 → 50（360 段窗口翻页太碎）。
   - 已知缺口（未做，需后端）：窗口「可用/禁用」只能看不能单条切换 —— 后端只有批量覆盖/单条追加/物理删三个写端点，
     没有 update 端点；工厂最常见的「某天停机」目前只能删了重铺或用批量铺窗整段覆盖。
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { storeToRefs } from 'pinia'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElOption,
  ElPagination,
  ElSelect,
  ElSelectV2,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTabPane,
  ElTabs,
  ElTag,
  ElTooltip
} from 'element-plus'
import {
  CALENDAR_CONFIG_META,
  type BulkWindowDraft,
  type CalendarConfigStatus,
  type ManualCapacitySlotDto,
  type ManualSlotCalendarDto,
  type ManualSlotDraft,
  type Resource,
  type ResourceCalendarEntryDto
} from '@/api/aps-v1'
import type { RoleKey } from '@/api/aps-v1/types'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { useResourceCalendarStore } from '@/store/modules/aps/resourceCalendar'
import CalendarBulkDialog from './components/CalendarBulkDialog.vue'
import CalendarSingleWindowDialog from './components/CalendarSingleWindowDialog.vue'
import ManualSlotCreateDialog from './components/ManualSlotCreateDialog.vue'
import ResolvePreviewPanel from './components/ResolvePreviewPanel.vue'

const apsAuth = useApsAuthStore()
const store = useResourceCalendarStore()
const {
  resources,
  resourcesLoading,
  currentResourceId,
  deviceWindowItems,
  deviceWindowTotal,
  deviceWindowPage,
  deviceWindowPageSize,
  deviceWindowFrom,
  deviceWindowTo,
  deviceCalendarStatus,
  resolvePreview,
  resolveLoading,
  manualResolvePreview,
  manualResolveLoading,
  slots,
  slotTotal,
  slotPage,
  slotPageSize,
  slotsLoading,
  departmentCandidates,
  currentSlotId,
  slotWindowItems,
  slotWindowTotal,
  slotWindowPage,
  slotWindowPageSize,
  slotWindowFrom,
  slotWindowTo,
  currentSlot,
  loading,
  saving,
  error,
  canViewDevice,
  canEditDevice,
  canDeleteDevice,
  canViewManual,
  canEditManual,
  canDeleteManual
} = storeToRefs(store)

/* ===== Tab 控制 ===== */
type TabName = 'device' | 'manual' | 'manual-calendar'
const activeTab = ref<TabName>('device')
const loadedTabs = ref<Set<TabName>>(new Set())

/* ===== 日期筛选 + 日期显示（设备侧 / 人工槽侧共用） =====
 *  工厂用户痛点：一台设备一年 360 段窗口，靠翻页找某天。
 *  后端 GET /api/resource-calendar/{id} 与 GET /api/manual-capacity/calendar/{id}
 *  本来就支持 from/to（语义＝**窗口完整落在区间内**），前端此前一直没传 ⇒ 本次接上。
 */
const WEEKDAY_CN = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']

/** 本地墙钟日期（治理域 DateTime.Now 口径，见 memory 后端两套时区） */
function pad2(n: number): string {
  return String(n).padStart(2, '0')
}
function ymd(d: Date): string {
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`
}
function todayYmd(): string {
  return ymd(new Date())
}
/** 本周一（周一为一周之始） */
function thisWeekStartYmd(): string {
  const d = new Date()
  const dow = d.getDay()
  d.setDate(d.getDate() - (dow === 0 ? 6 : dow - 1))
  return ymd(d)
}
function thisMonthStartYmd(): string {
  const d = new Date()
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-01`
}
function daysAgoYmd(n: number): string {
  const d = new Date()
  d.setDate(d.getDate() - n)
  return ymd(d)
}

/** ISO 时间 → 日期段（YYYY-MM-DD） */
function dateOf(iso?: string): string {
  return iso ? iso.slice(0, 10) : ''
}
/** ISO 时间 → 时段段（HH:mm） */
function timeOf(iso?: string): string {
  return iso ? iso.slice(11, 16) : ''
}
function weekdayOf(dateStr: string): string {
  if (!dateStr) return ''
  return WEEKDAY_CN[new Date(`${dateStr}T00:00:00`).getDay()] ?? ''
}
/** 表格日期列：`2026-09-29（周二）` */
function fmtDayLabel(iso?: string): string {
  const d = dateOf(iso)
  if (!d) return '—'
  return `${d}（${weekdayOf(d)}）`
}
/** 是否今天的行（工厂用户最关心今天） */
function isTodayRow(iso?: string): boolean {
  return dateOf(iso) === todayYmd()
}

/** 区间 → 后端 from/to（后端 to 是「窗口结束时间 ≤ to」，必须带当天 23:59:59） */
function rangeQuery(v: [string, string] | undefined): { from?: string; to?: string } {
  if (!v || v.length !== 2 || !v[0] || !v[1]) return {}
  return { from: `${v[0]}T00:00:00`, to: `${v[1]}T23:59:59` }
}

/** 区间文字（状态行用） */
function rangeText(v: [string, string] | undefined | null): string {
  if (!v || v.length !== 2 || !v[0] || !v[1]) return ''
  return `${v[0]} ~ ${v[1]}`
}

async function onTabChange(name: TabName): Promise<void> {
  if (loadedTabs.value.has(name)) return
  loadedTabs.value.add(name)
  // 10-12 RC20：fire-and-forget，不阻塞 UI 切 Tab
  //  - 原顶层 wait 在 onTabChange 内，会让 ElTabPane 切换视觉"卡"（慢响应端点会拖住 UI 切回）
  //  - store 内部已有 loading / error / ElMessage，错误由 store 处理
  //  - RC20 探针锁契约：onTabChange 内不在函数顶层 wait 数据加载
  void (async () => {
    try {
      if (name === 'device') {
        await store.loadResources()
      } else if (name === 'manual') {
        await Promise.all([store.loadSlots(), store.loadDepartmentCandidates()])
      }
    } catch {
      // 错误已由 store handleError 写入 error ref + ElMessage；此处不重复
    }
  })()
}

/* ===== Tab1：设备资源日历 ===== */
// 10-12 RC23：资源主表改走正式端点 GET /api/resource（含 hasCalendar，列表级判定日历状态）
//  - 旧「手填资源 ID」已撤（@see RC23 老实现 + 原 G1 过渡说明）
//  - dev 实测 7773 records → 用 el-select-v2 虚拟滚动渲染
const deviceDialogVisible = ref(false)

/** ElSelectV2 需要的 {value,label} 形态；按 resourceCode 升序便于定位 */
const resourceOptions = computed<Array<{ value: number; label: string; resource: Resource }>>(() =>
  [...resources.value]
    .sort((a, b) => (a.resourceCode ?? '').localeCompare(b.resourceCode ?? ''))
    .map((r) => ({
      value: r.resourceId,
      label: labelOfResource(r),
      resource: r
    }))
)

/** 冻结 v1.4 line 17：「资源归部门」是三级继承的前提 —— 用户须先知道设备属于哪个车间，
 *  才能去判断「上级（工厂/部门）开没开」。后端 ResourceListItemDto 已带 productionDepartmentName，
 *  10-14 起展示（此前 DTO 有值但页面未用，dev 7773 条同名设备无法区分）。
 *  10-23：5号位 R2 随业务范围收窄同批下发 factoryCode/factoryName，工厂一并带上
 *  （三级继承的上级就是工厂，列表不显示无法判断该去看哪一层）。 */
function labelOfResource(r: Resource): string {
  const dept = r.productionDepartmentName?.trim()
  const factory = (r.factoryName ?? r.factoryCode)?.trim()
  const owner = [factory, dept].filter(Boolean).join(' / ')
  return `${r.resourceCode ?? '-'} ${r.resourceName ?? ''}${owner ? `（${owner}）` : ''}`.trim()
}

async function pickResource(resourceId: number): Promise<void> {
  await store.loadDeviceWindows(resourceId)
  await loadResolvePreview()
}

/* ===== Tab1：设备窗口的日期筛选 =====
 *  一台设备一年 360 段窗口，不筛日期只能一页页翻 ⇒ 接后端 from/to（本来就支持）
 */
const deviceRange = ref<[string, string] | undefined>(undefined)

async function applyDeviceRange(v: [string, string] | undefined | null): Promise<void> {
  const id = currentResourceId.value
  if (id == null) return
  if (!v || v.length !== 2 || !v[0] || !v[1]) {
    await store.loadDeviceWindows(id, { clearRange: true, nextPageIndex: 1 })
    return
  }
  const q = rangeQuery(v)
  await store.loadDeviceWindows(id, { from: q.from, to: q.to, nextPageIndex: 1 })
}

/** 快捷区间：本周 / 本月 / 近 30 天 / 全部 */
async function applyDevicePreset(kind: 'week' | 'month' | 'd30' | 'all'): Promise<void> {
  const today = todayYmd()
  let v: [string, string] | undefined
  if (kind === 'week') v = [thisWeekStartYmd(), today]
  else if (kind === 'month') v = [thisMonthStartYmd(), today]
  else if (kind === 'd30') v = [daysAgoYmd(29), today]
  else v = undefined
  deviceRange.value = v
  await applyDeviceRange(v)
}

/** 是否正在按日期筛选 */
const deviceRangeActive = computed<boolean>(
  () => !!deviceWindowFrom.value || !!deviceWindowTo.value
)

/** 按钮禁用原因（悬停可见，别让用户对着灰按钮猜） */
const deviceActionHint = computed<string>(() => {
  if (currentResourceId.value == null) return '请先在上面选择一台设备'
  if (!canEditDevice.value) return '当前账号没有设备日历编辑权限'
  return ''
})

/* ===== 三级合并后可排时段（5号位 resolve-preview，只读） =====
 *  冻结 13 v1.8:31 要求「明确显示『继承上级 / 本级明确工作 / 本级明确不工作』及来源授权」。
 *  本级窗口表看不到继承来的时段，故单独一块展示合并结果：
 *    status=WORK    → 看每片的 source（Factory/Department=继承上级，Resource=本级）
 *    status=OFF     → 本级明确停机（显式关闭/维修，不会被上级复活）
 *    status=MISSING → 本级没登记，且没从上级解析出窗口
 *  设备侧端点 GET /api/resource-calendar/resolve-preview；
 *  人工槽侧端点 GET /api/manual-capacity/resolve-preview（5号位 10-23 落码 / 10-26 上 dev）
 *  —— 两者后端 DTO 同构，页面共用 ResolvePreviewPanel 组件（口径不许两边各写一套）。
 */
const resolveRange = ref<[string, string] | undefined>(undefined)
/** 人工槽侧的查询区间（与设备侧独立，切槽不互相影响） */
const manualResolveRange = ref<[string, string] | undefined>(undefined)

/** 默认区间：今天 ~ 今天+30 天（后端必填；endDate 含末日） */
function defaultResolveRange(): [string, string] {
  const pad = (n: number): string => String(n).padStart(2, '0')
  const fmtDay = (d: Date): string =>
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
  const start = new Date()
  const end = new Date(start.getTime())
  end.setDate(end.getDate() + 30)
  return [fmtDay(start), fmtDay(end)]
}

async function loadResolvePreview(): Promise<void> {
  const id = currentResourceId.value
  if (id == null) return
  if (!resolveRange.value) resolveRange.value = defaultResolveRange()
  const [startDate, endDate] = resolveRange.value
  await store.loadResolvePreview(id, startDate, endDate)
}

function onResolveRangeChange(v: [string, string] | undefined): void {
  resolveRange.value = v
  if (v) void loadResolvePreview()
}

/** 人工槽版三级合并预览（只读；越界槽后端 fail-closed 剔除 ⇒ 查不到即空态） */
async function loadManualResolvePreview(): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  if (!manualResolveRange.value) manualResolveRange.value = defaultResolveRange()
  const [startDate, endDate] = manualResolveRange.value
  await store.loadManualResolvePreview(id, startDate, endDate)
}

function onManualResolveRangeChange(v: [string, string] | undefined): void {
  manualResolveRange.value = v
  if (v) void loadManualResolvePreview()
}

function openDeviceDialog(): void {
  if (currentResourceId.value == null) {
    ElMessage.warning('请先选择资源并加载窗口')
    return
  }
  deviceDialogVisible.value = true
}

async function submitDeviceBulk(draft: BulkWindowDraft): Promise<void> {
  if (currentResourceId.value == null) return
  const inserted = await store.createDeviceWindows({
    resourceId: currentResourceId.value,
    startDate: `${draft.startDate}T00:00:00`,
    days: draft.days,
    startTime: draft.startTime,
    endTime: draft.endTime,
    availableFlag: draft.availableFlag,
    remark: draft.remark
  })
  if (inserted != null) {
    ElMessage.success(
      `已按 ${draft.startTime.slice(0, 5)}~${draft.endTime.slice(0, 5)} 生成 ${inserted} 天窗口（${draft.startDate} 起 ${draft.days} 天）`
    )
    deviceDialogVisible.value = false
    await loadResolvePreview()
  }
}

/* ===== 单条追加（2026-10-14「放开一天多条」方向2） ===== */
const deviceSingleDialogVisible = ref(false)

function openDeviceSingleDialog(): void {
  if (currentResourceId.value == null) {
    ElMessage.warning('请先选择资源并加载窗口')
    return
  }
  deviceSingleDialogVisible.value = true
}

async function submitDeviceSingle(payload: {
  startTime: string
  endTime: string
  availableFlag: boolean
  remark?: string
}): Promise<void> {
  if (currentResourceId.value == null) return
  const id = await store.appendDeviceWindow({
    resourceId: currentResourceId.value,
    ...payload
  })
  if (id != null) {
    ElMessage.success(
      `已新增 ${fmtDayLabel(payload.startTime)} ${timeOf(payload.startTime)}~${timeOf(payload.endTime)} 这一段`
    )
    deviceSingleDialogVisible.value = false
    await loadResolvePreview()
  }
}

async function removeDeviceWindow(row: ResourceCalendarEntryDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将删除 ${fmtDayLabel(row.startTime)} ${timeOf(row.startTime)}~${timeOf(row.endTime)} 这一段窗口。\n` +
      '删除后不可恢复，排产追溯将不再含该窗口。确认删除？',
    '删除这一段窗口',
    { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.removeDeviceWindow(row.id, row.resourceId)
  if (done) {
    ElMessage.success('设备窗口已删除')
    await loadResolvePreview()
  }
}

/** 当前资源的展示名 */
const currentResourceLabel = computed<string>(() => {
  const id = currentResourceId.value
  if (id == null) return ''
  const hit = resources.value.find((r) => r.resourceId === id)
  if (!hit) return `资源 #${id}`
  return labelOfResource(hit)
})

/** 当前资源的归属部门（冻结 v1.4 line 17 资源归部门是三级继承前提；status 行展示供用户核对上级） */
const currentResourceDept = computed<string>(
  () =>
    resources.value.find((r) => r.resourceId === currentResourceId.value)
      ?.productionDepartmentName ?? ''
)

/* 10-17：设备窗口改服务端分页 ⇒ 翻页/改每页条数都要**重新取数**（不再是本地切片）
 *  - 改每页条数时回第 1 页，避免停在越界页
 */
function onDeviceWindowPageChange(nextPage: number): void {
  const id = store.currentResourceId
  if (id == null) return
  void store.loadDeviceWindows(id, { nextPageIndex: nextPage })
}
function onDeviceWindowSizeChange(nextSize: number): void {
  const id = store.currentResourceId
  if (id == null) return
  void store.loadDeviceWindows(id, { nextPageSize: nextSize, nextPageIndex: 1 })
}

/* 人工槽窗口分页（同设备侧，服务端分页） */
function onSlotWindowPageChange(nextPage: number): void {
  const id = store.currentSlotId
  if (id == null) return
  void store.loadSlotWindows(id, { nextPageIndex: nextPage })
}
function onSlotWindowSizeChange(nextSize: number): void {
  const id = store.currentSlotId
  if (id == null) return
  void store.loadSlotWindows(id, { nextPageSize: nextSize, nextPageIndex: 1 })
}

/* ===== Tab2：人工能力槽 ===== */
const slotDialogVisible = ref(false)
const filterDepartmentId = ref<number | undefined>(undefined)
const filterOperationName = ref<string>('')
const filterIncludeInactive = ref<boolean>(false)

async function searchSlots(): Promise<void> {
  await store.loadSlots(
    {
      departmentId: filterDepartmentId.value ?? undefined,
      operationName: filterOperationName.value.trim() || undefined,
      includeInactive: filterIncludeInactive.value
    },
    { resetPage: true }
  )
}

function resetSlotFilter(): void {
  filterDepartmentId.value = undefined
  filterOperationName.value = ''
  filterIncludeInactive.value = false
  void store.loadSlots({}, { resetPage: true })
}

/* 10-12 RC22 收：人工槽 Tab2 接入分页（@see taskSplitRuleConfig.vue:162-170 范式）
 *  - 切页码 → loadSlots(undefined, { nextPageIndex })
 *  - 切每页条数 → loadSlots(undefined, { resetPage: true, nextPageSize }) 防越界
 */
async function onSlotPageChange(nextPage: number): Promise<void> {
  await store.loadSlots(undefined, { nextPageIndex: nextPage })
}
async function onSlotSizeChange(nextSize: number): Promise<void> {
  await store.loadSlots(undefined, { resetPage: true, nextPageSize: nextSize })
}

async function submitCreateSlot(draft: ManualSlotDraft): Promise<void> {
  const created = await store.createSlot({
    manualSlotId: 0,
    productionDepartmentId: draft.productionDepartmentId,
    operationName: draft.operationName,
    slotCode: draft.slotCode,
    isActive: true
  })
  if (created) {
    ElMessage.success(`人工能力槽「${created.slotCode}」已新增`)
    slotDialogVisible.value = false
  }
}

/** 查看某人工槽窗口 → 载入并跳 Tab3 */
async function viewSlotWindows(row: ManualCapacitySlotDto): Promise<void> {
  await store.loadSlotWindows(row.manualSlotId)
  await loadManualResolvePreview()
  activeTab.value = 'manual-calendar'
  loadedTabs.value.add('manual-calendar')
}

async function disableSlot(row: ManualCapacitySlotDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将停用人工能力槽「${row.slotCode}」（${row.operationName}）：停用后行保留、查询默认不含。\n\n` +
      '影响：该能力槽不再参与排产；已有日历窗口不会自动清除，如需清理请到「人工槽日历」页签删除。',
    '停用人工能力槽',
    { type: 'warning', confirmButtonText: '确认停用', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.disableSlot(row.manualSlotId)
  if (done) ElMessage.success(`人工能力槽 ${row.slotCode} 已停用`)
}

/** 从 Tab2 直接对该行发起铺窗（切换到 Tab3 后打开 Dialog） */
const pendingSlotBulkId = ref<number | null>(null)
async function bulkForSlot(row: ManualCapacitySlotDto): Promise<void> {
  if (!row.isActive) {
    ElMessage.warning('已停用的人工槽不可铺窗')
    return
  }
  await viewSlotWindows(row)
  pendingSlotBulkId.value = row.manualSlotId
  slotBulkDialogVisible.value = true
}

/* ===== Tab3：人工槽日历 ===== */
const slotBulkDialogVisible = ref(false)

/* 人工槽窗口的日期筛选（与设备侧同一套：后端 from/to 早就支持） */
const slotRange = ref<[string, string] | undefined>(undefined)

async function applySlotRange(v: [string, string] | undefined | null): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  if (!v || v.length !== 2 || !v[0] || !v[1]) {
    await store.loadSlotWindows(id, { clearRange: true, nextPageIndex: 1 })
    return
  }
  const q = rangeQuery(v)
  await store.loadSlotWindows(id, { from: q.from, to: q.to, nextPageIndex: 1 })
}

/** 快捷区间：本周 / 本月 / 近 30 天 / 全部 */
async function applySlotPreset(kind: 'week' | 'month' | 'd30' | 'all'): Promise<void> {
  const today = todayYmd()
  let v: [string, string] | undefined
  if (kind === 'week') v = [thisWeekStartYmd(), today]
  else if (kind === 'month') v = [thisMonthStartYmd(), today]
  else if (kind === 'd30') v = [daysAgoYmd(29), today]
  else v = undefined
  slotRange.value = v
  await applySlotRange(v)
}

/** 是否正在按日期筛选 */
const slotRangeActive = computed<boolean>(() => !!slotWindowFrom.value || !!slotWindowTo.value)

/** 按钮禁用原因（悬停可见） */
const slotActionHint = computed<string>(() => {
  if (currentSlotId.value == null) return '请先在「人工能力槽」里选一个工位'
  if (currentSlot.value && !currentSlot.value.isActive) return '该工位已停用，不能铺窗'
  if (!canEditManual.value) return '当前账号没有人工能力槽编辑权限'
  return ''
})

/** Tab3 顶部「刷新」：本级窗口 + 三级合成结果一起重取（两者口径必须同步） */
async function refreshSlotWindows(): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  await store.loadSlotWindows(id)
  await loadManualResolvePreview()
}

function openSlotBulkDialog(): void {
  if (currentSlotId.value == null) return
  if (currentSlot.value && !currentSlot.value.isActive) {
    ElMessage.warning('已停用的人工槽不可铺窗')
    return
  }
  slotBulkDialogVisible.value = true
}

async function submitSlotBulk(draft: BulkWindowDraft): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  const inserted = await store.createSlotWindows({
    manualSlotId: id,
    startDate: `${draft.startDate}T00:00:00`,
    days: draft.days,
    startTime: draft.startTime,
    endTime: draft.endTime,
    availableFlag: draft.availableFlag,
    remark: draft.remark
  })
  if (inserted != null) {
    ElMessage.success(
      `已按 ${draft.startTime.slice(0, 5)}~${draft.endTime.slice(0, 5)} 生成 ${inserted} 天窗口（${draft.startDate} 起 ${draft.days} 天）`
    )
    slotBulkDialogVisible.value = false
    await loadManualResolvePreview()
  }
}

/* ===== 单条追加（人工槽，同设备侧） ===== */
const slotSingleDialogVisible = ref(false)

function openSlotSingleDialog(): void {
  if (currentSlotId.value == null) return
  if (currentSlot.value && !currentSlot.value.isActive) {
    ElMessage.warning('已停用的人工槽不可铺窗')
    return
  }
  slotSingleDialogVisible.value = true
}

async function submitSlotSingle(payload: {
  startTime: string
  endTime: string
  availableFlag: boolean
  remark?: string
}): Promise<void> {
  const id = currentSlotId.value
  if (id == null) return
  const newId = await store.appendSlotWindow({ manualSlotId: id, ...payload })
  if (newId != null) {
    ElMessage.success(
      `已新增 ${fmtDayLabel(payload.startTime)} ${timeOf(payload.startTime)}~${timeOf(payload.endTime)} 这一段`
    )
    slotSingleDialogVisible.value = false
    await loadManualResolvePreview()
  }
}

async function removeSlotWindow(row: ManualSlotCalendarDto): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将删除 ${fmtDayLabel(row.startTime)} ${timeOf(row.startTime)}~${timeOf(row.endTime)} 这一段窗口。\n` +
      '删除后不可恢复。确认删除？',
    '删除这一段窗口',
    { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  const done = await store.removeSlotWindow(row.id, row.manualSlotId)
  if (done) {
    ElMessage.success('人工槽窗口已删除')
    await loadManualResolvePreview()
  }
}

const currentSlotLabel = computed<string>(() => {
  const s = currentSlot.value
  if (!s) return ''
  return `${s.slotCode}（${s.operationName}｜${s.departmentName ?? '未知部门'}）`
})

/** 人工槽「日历」徽标状态：实时查过的（totals 已知）优先，否则用列表项自带的 hasCalendar 打底。
 *  后端 ManualCapacitySlotDto.HasCalendar 是列表级快照 ⇒ 以前整列显示「未查证」，要点开每个槽才知道。 */
function slotCalendarStatusOf(row: ManualCapacitySlotDto): CalendarConfigStatus {
  const live = store.slotCalendarStatus(row.manualSlotId)
  if (live !== 'UNKNOWN') return live
  if (row.hasCalendar === true) return 'CONFIGURED'
  if (row.hasCalendar === false) return 'NOT_CONFIGURED'
  return 'UNKNOWN'
}

const currentSlotWindowStatus = computed(() =>
  currentSlotId.value == null ? 'UNKNOWN' : store.slotCalendarStatus(currentSlotId.value)
)

/* ===== 公共 ===== */

/** 设备侧状态行：窗口条数（有筛选 → 明说"本区间"） */
const deviceWindowCountText = computed<string>(() => {
  if (deviceRangeActive.value) {
    return `本区间（${rangeText(deviceRange.value)}）${deviceWindowTotal.value} 条`
  }
  return `窗口：${deviceWindowTotal.value} 条`
})

/** 人工槽侧状态行：同上 */
const slotWindowCountText = computed<string>(() => {
  if (slotRangeActive.value) {
    return `本区间（${rangeText(slotRange.value)}）${slotWindowTotal.value} 条`
  }
  return `窗口：${slotWindowTotal.value} 条`
})

/* ===== mock 角色切换器（仅 mock 模式显示；与甘特图页同一范式） =====
 * 真实绑定已落地（3号位 脚本 APS_Auth_resource_calendar_role_binding_20260924.sql）：
 *   viewer = view；planner = view + edit（不含 delete）；admin.aps / admin.system = 全 6 码。
 * mock 下用本切换器验证按钮级门控（planner 无可删按钮 / admin 可删）。
 */
const ROLE_TAG: Record<RoleKey, 'success' | 'warning' | 'info' | 'primary' | 'danger'> = {
  'aps.viewer.management': 'info',
  'aps.planner': 'success',
  'aps.admin.aps': 'warning',
  'aps.supervisor.workshop': 'primary',
  'aps.coordinator.material': 'primary',
  'aps.service.api': 'primary',
  'aps.admin.system': 'danger'
}

function switchMockRole(role: RoleKey): void {
  apsAuth.mockSwitchRole(role)
  ElMessage.success(`已切换角色：${role}（仅演示模式）`)
}

/* ===== 10-14 RC27 收：撤版本选择器（RC19 撤回）=====
 *  - 冻结 v1.4 §「4号位：资源能力维护页面Owner」只列 3 项维护对象（设备日历 / 人工能力槽 / 人工槽日历），
 *    **未要求计划版本锚点**；上级工作日历（同为基础数据）同样无版本条，两页口径现已一致
 *  - 原 RC19 加它是为绕过"资源池取自甘特图、ACTIVE 版本无甘特数据 → 资源池空"的排查障碍；
 *    同日 RC23 已把资源池改走 GET /api/resource（与版本无关的基础主数据），**该障碍不复存在**
 *  - 且本页从不读取 gantt / summary：切版本只触发一次本页用不上的取数，纯装饰且误导
 *    （让用户误以为资源日历按计划版本分版本）
 */

onMounted(async () => {
  if (!canViewDevice.value && !canViewManual.value) {
    ElMessage.warning('当前操作者无资源日历查看权限，请联系系统管理员授予')
  }
  loadedTabs.value.add('device')
  // 10-12 RC23：资源主表走正式端点 /api/resource（基础数据，与计划版本无关）
  await store.loadResources()
})

onBeforeUnmount(() => {
  store.reset()
})
</script>

<template>
  <div class="rc-page">
    <!-- 页面说明（10-12 收：原 ElCard 仅有 header 无 body，空 body 占行高；改用 div 紧凑布局） -->
    <div class="rc-intro">
      <div class="intro-header">
        <span class="intro-title">资源日历维护</span>
        <ElTooltip placement="bottom-start" effect="light" :show-after="150">
          <span class="intro-help" tabindex="0">?</span>
          <template #content>
            <div class="help-pop">
              <p class="help-pop-title">能不能排产，看厂和部门开没开</p>
              <p>排产时间从上往下继承：工厂 → 部门 → 设备 / 工位。</p>
              <p
                >本级没铺窗口，只说明<strong>这台还没单独登记</strong>，不代表一定不能排 ——
                厂里或部门开放了，它照样能排。</p
              >
              <p>只有三级都没开放，才是真的排不了；系统也不会默认按 7×24 小时当成一直可排。</p>
            </div>
          </template>
        </ElTooltip>
        <!-- mock 角色切换器（仅 mock 模式显示；生产模式自动隐藏） -->
        <ElDropdown
          v-if="apsAuth.mockAvailableRoles.length > 0"
          trigger="click"
          class="role-switcher"
          @command="switchMockRole"
        >
          <ElTag
            :type="ROLE_TAG[apsAuth.mockActiveRole]"
            effect="dark"
            class="role-badge"
            title="点击切换角色（仅演示模式可用）"
          >
            {{ apsAuth.mockActiveRole }}
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
                      ? '仅 view（可看不可改）'
                      : r === 'aps.planner'
                        ? 'view + edit（不含 delete）'
                        : r === 'aps.admin.aps' || r === 'aps.admin.system'
                          ? '全 6 码（含 delete）'
                          : ''
                  }}
                </span>
              </ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>
      </div>
    </div>

    <!-- 错误条 -->
    <ElAlert
      v-if="error"
      type="error"
      :closable="true"
      show-icon
      class="rc-alert rc-error"
      title="操作失败"
      @close="store.clearError()"
    >
      {{ error }}
    </ElAlert>

    <ElCard shadow="never">
      <ElTabs v-model="activeTab" @tab-change="(n) => onTabChange(n as TabName)">
        <!-- ============ Tab1 设备资源日历 ============ -->
        <ElTabPane label="设备资源日历" name="device" lazy>
          <ElForm inline label-position="right" label-width="80px" class="rc-toolbar">
            <ElFormItem v-if="resources.length > 0" label="资源">
              <!-- 10-12 RC23：el-select-v2 虚拟滚动（dev 7773+ records → el-select 会卡） -->
              <ElSelectV2
                :model-value="currentResourceId ?? undefined"
                :options="resourceOptions"
                :loading="resourcesLoading"
                filterable
                clearable
                placeholder="从资源主表选择（输入编码/名称/部门搜索）"
                style="width: 360px"
                @change="(v: number | undefined) => v && pickResource(v)"
              />
            </ElFormItem>
            <ElFormItem v-else label="资源">
              <span class="rc-hint">{{
                resourcesLoading ? '资源主表加载中…' : '资源主表为空'
              }}</span>
            </ElFormItem>
            <ElFormItem>
              <ElButton
                :disabled="currentResourceId == null || !canEditDevice"
                :title="deviceActionHint"
                @click="openDeviceSingleDialog"
              >
                新增一段
              </ElButton>
              <ElButton
                type="primary"
                :disabled="currentResourceId == null || !canEditDevice"
                :title="deviceActionHint"
                @click="openDeviceDialog"
              >
                批量铺窗
              </ElButton>
              <span class="rc-hint">
                新增一段＝只加这一段，不动当天其他时段；批量铺窗＝该区间整段重铺
              </span>
            </ElFormItem>
          </ElForm>

          <div v-if="currentResourceId != null" class="rc-status-row">
            <span class="rc-status-label">当前资源：</span>
            <strong>{{ currentResourceLabel }}</strong>
            <ElTag v-if="currentResourceDept" type="info" class="rc-btn-gap" effect="plain">
              所属部门：{{ currentResourceDept }}
            </ElTag>
            <ElTag
              :type="CALENDAR_CONFIG_META[deviceCalendarStatus].tagType"
              class="rc-btn-gap"
              effect="dark"
            >
              日历 {{ CALENDAR_CONFIG_META[deviceCalendarStatus].label }}
            </ElTag>
            <span class="rc-hint">{{
              CALENDAR_CONFIG_META[deviceCalendarStatus].description
            }}</span>
            <span class="rc-hint">{{ deviceWindowCountText }}</span>
          </div>

          <ElEmpty
            v-if="currentResourceId == null"
            description="请先在上方资源下拉中选中目标设备"
          />
          <template v-else>
            <!-- 日期筛选：一台设备一年几百段窗口，靠翻页找某天不现实 -->
            <div class="rc-filter-row">
              <span class="rc-status-label">日期范围：</span>
              <ElDatePicker
                v-model="deviceRange"
                type="daterange"
                value-format="YYYY-MM-DD"
                range-separator="至"
                start-placeholder="开始日期"
                end-placeholder="结束日期"
                style="width: 250px"
                @change="applyDeviceRange"
              />
              <ElButton size="small" plain @click="applyDevicePreset('week')">本周</ElButton>
              <ElButton size="small" plain @click="applyDevicePreset('month')">本月</ElButton>
              <ElButton size="small" plain @click="applyDevicePreset('d30')">近 30 天</ElButton>
              <ElButton size="small" plain @click="applyDevicePreset('all')">全部</ElButton>
              <span class="rc-hint">不选＝看全部；选了就只看这段日期</span>
            </div>

            <ElAlert
              v-if="deviceRangeActive && deviceWindowTotal === 0"
              type="info"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这段日期里没有登记时段"
            >
              {{
                deviceCalendarStatus === 'NOT_CONFIGURED'
                  ? '这台设备本级就没有登记过任何时段。'
                  : '这台设备在别的日期可能还有窗口。点「全部」看全部，或换一个日期范围。'
              }}
            </ElAlert>
            <ElAlert
              v-else-if="deviceCalendarStatus === 'NOT_CONFIGURED'"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这台设备本级还没登记"
            >
              只说明没有单独设过可用时段，<strong>不代表它一定不能排产</strong> ——
              所属工厂或部门开放了，它照样能排。需要单独调整它的可用时段时，用「新增一段」或「批量铺窗」。
            </ElAlert>

            <ElTable
              v-if="deviceWindowTotal > 0"
              :data="deviceWindowItems"
              :loading="loading"
              border
              stripe
              row-key="id"
              size="small"
              style="width: 100%"
            >
              <ElTableColumn label="日期" min-width="160" fixed="left">
                <template #default="{ row }">
                  <span>{{ fmtDayLabel(row.startTime) }}</span>
                  <ElTag
                    v-if="isTodayRow(row.startTime)"
                    size="small"
                    type="danger"
                    effect="plain"
                    class="today-tag"
                  >
                    今天
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn label="时段" width="150">
                <template #default="{ row }">
                  {{ timeOf(row.startTime) }} ~ {{ timeOf(row.endTime) }}
                </template>
              </ElTableColumn>
              <ElTableColumn label="可用" width="90" align="center">
                <template #default="{ row }">
                  <ElTag :type="row.availableFlag ? 'success' : 'danger'" size="small">
                    {{ row.availableFlag ? '可用' : '禁用' }}
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn prop="remark" label="备注" min-width="160" show-overflow-tooltip />
              <ElTableColumn label="操作" width="100" fixed="right" v-if="canDeleteDevice">
                <template #default="{ row }">
                  <ElButton
                    type="danger"
                    size="small"
                    :loading="saving"
                    @click="removeDeviceWindow(row)"
                  >
                    删除
                  </ElButton>
                </template>
              </ElTableColumn>
            </ElTable>

            <!-- 10-17：设备窗口服务端分页（后端 PageResult 已上线，翻页触发重新取数） -->
            <ElPagination
              v-if="deviceWindowTotal > 0"
              :current-page="deviceWindowPage"
              :page-size="deviceWindowPageSize"
              :total="deviceWindowTotal"
              :page-sizes="[10, 20, 50, 100, 200]"
              layout="total, sizes, prev, pager, next, jumper"
              background
              class="rc-pagination"
              @current-change="onDeviceWindowPageChange"
              @size-change="onDeviceWindowSizeChange"
            />

            <!-- ===== 三级合并后可排时段（本级表看不到继承来的时段，故单独展示） ===== -->
            <ResolvePreviewPanel
              :preview="resolvePreview"
              :loading="resolveLoading"
              :range="resolveRange"
              empty-text="选择设备后展示工厂 / 部门 / 本级合并后的可排时段"
              @update:range="onResolveRangeChange"
              @refresh="loadResolvePreview"
            />
          </template>
        </ElTabPane>

        <!-- ============ Tab2 人工能力槽 ============ -->
        <ElTabPane label="人工能力槽" name="manual" lazy>
          <ElForm inline label-position="right" label-width="80px" class="rc-toolbar">
            <ElFormItem label="部门">
              <ElSelect
                v-if="departmentCandidates.length > 0"
                v-model="filterDepartmentId"
                filterable
                clearable
                placeholder="全部部门"
                style="width: 220px"
              >
                <ElOption
                  v-for="d in departmentCandidates"
                  :key="d.productionDepartmentId"
                  :label="d.productionDepartmentName ?? '未知部门'"
                  :value="d.productionDepartmentId"
                />
              </ElSelect>
              <ElInputNumber
                v-else
                v-model="filterDepartmentId"
                :min="1"
                :step="1"
                style="width: 140px"
              />
            </ElFormItem>
            <ElFormItem label="小工序">
              <ElInput
                v-model="filterOperationName"
                placeholder="完整工序名（精确匹配）"
                clearable
                style="width: 180px"
                @keyup.enter="searchSlots"
              />
            </ElFormItem>
            <ElFormItem label="含已停用">
              <ElSwitch v-model="filterIncludeInactive" />
            </ElFormItem>
            <ElFormItem>
              <ElButton type="primary" :loading="slotsLoading" @click="searchSlots">查询</ElButton>
              <ElButton :disabled="slotsLoading" @click="resetSlotFilter">重置</ElButton>
            </ElFormItem>
            <ElFormItem>
              <ElButton type="primary" :disabled="!canEditManual" @click="slotDialogVisible = true">
                新增人工能力槽
              </ElButton>
            </ElFormItem>
          </ElForm>

          <ElEmpty
            v-if="slots.length === 0 && !slotsLoading"
            description="无人工能力槽；可点击「新增人工能力槽」创建"
          />

          <ElTable
            v-else
            :data="slots"
            :loading="slotsLoading"
            border
            stripe
            row-key="manualSlotId"
            size="small"
            :row-class-name="({ row }) => (!row.isActive ? 'rc-row-disabled' : '')"
            style="width: 100%"
          >
            <ElTableColumn prop="departmentName" label="生产部门" width="150" fixed="left">
              <template #default="{ row }">
                {{ row.departmentName ?? '未知部门' }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="operationName" label="小工序" width="140" />
            <ElTableColumn prop="slotCode" label="能力槽编码" width="150" />
            <ElTableColumn label="状态" width="100" align="center">
              <template #default="{ row }">
                <ElTag :type="row.isActive ? 'success' : 'info'" size="small">
                  {{ row.isActive ? '启用' : '已停用' }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="日历" width="120" align="center">
              <template #default="{ row }">
                <ElTag
                  :type="CALENDAR_CONFIG_META[slotCalendarStatusOf(row)].tagType"
                  size="small"
                  effect="plain"
                >
                  {{ CALENDAR_CONFIG_META[slotCalendarStatusOf(row)].label }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="操作" width="260" fixed="right">
              <template #default="{ row }">
                <ElButton size="small" @click="viewSlotWindows(row)">查看窗口</ElButton>
                <ElButton
                  size="small"
                  type="primary"
                  :disabled="!canEditManual || !row.isActive"
                  @click="bulkForSlot(row)"
                >
                  铺窗
                </ElButton>
                <ElButton
                  size="small"
                  type="danger"
                  :disabled="!canDeleteManual || !row.isActive"
                  :loading="saving"
                  @click="disableSlot(row)"
                >
                  停用
                </ElButton>
              </template>
            </ElTableColumn>
          </ElTable>

          <!-- 10-12 RC22 收：分页 ElPagination（@see TaskSplitRuleConfig.vue:842-852 范式） -->
          <ElPagination
            v-if="slotTotal > 0"
            :current-page="slotPage"
            :page-size="slotPageSize"
            :total="slotTotal"
            :page-sizes="[10, 20, 50, 100, 200]"
            layout="total, sizes, prev, pager, next, jumper"
            background
            class="rc-pagination"
            @current-change="onSlotPageChange"
            @size-change="onSlotSizeChange"
          />
        </ElTabPane>

        <!-- ============ Tab3 人工槽日历 ============ -->
        <ElTabPane label="人工槽日历" name="manual-calendar" lazy>
          <ElEmpty
            v-if="currentSlotId == null"
            description="请先在「人工能力槽」页签中点击某行的「查看窗口」或「铺窗」"
          />
          <template v-else>
            <div class="rc-status-row">
              <span class="rc-status-label">当前人工槽：</span>
              <strong>{{ currentSlotLabel }}</strong>
              <ElTag
                :type="CALENDAR_CONFIG_META[currentSlotWindowStatus].tagType"
                class="rc-btn-gap"
                effect="dark"
              >
                日历 {{ CALENDAR_CONFIG_META[currentSlotWindowStatus].label }}
              </ElTag>
              <span class="rc-hint">{{
                CALENDAR_CONFIG_META[currentSlotWindowStatus].description
              }}</span>
              <span class="rc-hint">{{ slotWindowCountText }}</span>
              <ElButton
                class="rc-btn-gap"
                size="small"
                :loading="loading"
                @click="refreshSlotWindows"
              >
                刷新
              </ElButton>
              <ElButton
                size="small"
                :disabled="!canEditManual || (currentSlot ? !currentSlot.isActive : true)"
                :title="slotActionHint"
                @click="openSlotSingleDialog"
              >
                新增一段
              </ElButton>
              <ElButton
                type="primary"
                size="small"
                :disabled="!canEditManual || (currentSlot ? !currentSlot.isActive : true)"
                :title="slotActionHint"
                @click="openSlotBulkDialog"
              >
                批量铺窗
              </ElButton>
            </div>

            <ElAlert
              v-if="currentSlot && !currentSlot.isActive"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="该人工槽已停用"
            >
              停用后不再参与排产；窗口保留，不会被自动清理。
            </ElAlert>

            <!-- 日期筛选（同设备侧：后端 from/to 早就支持） -->
            <div class="rc-filter-row">
              <span class="rc-status-label">日期范围：</span>
              <ElDatePicker
                v-model="slotRange"
                type="daterange"
                value-format="YYYY-MM-DD"
                range-separator="至"
                start-placeholder="开始日期"
                end-placeholder="结束日期"
                style="width: 250px"
                @change="applySlotRange"
              />
              <ElButton size="small" plain @click="applySlotPreset('week')">本周</ElButton>
              <ElButton size="small" plain @click="applySlotPreset('month')">本月</ElButton>
              <ElButton size="small" plain @click="applySlotPreset('d30')">近 30 天</ElButton>
              <ElButton size="small" plain @click="applySlotPreset('all')">全部</ElButton>
              <span class="rc-hint">不选＝看全部；选了就只看这段日期</span>
            </div>

            <ElAlert
              v-if="slotRangeActive && slotWindowTotal === 0"
              type="info"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这段日期里没有登记时段"
            >
              {{
                currentSlotWindowStatus === 'NOT_CONFIGURED'
                  ? '这个工位本级就没有登记过任何时段。'
                  : '这个工位在别的日期可能还有窗口。点「全部」看全部，或换一个日期范围。'
              }}
            </ElAlert>
            <ElAlert
              v-else-if="currentSlotWindowStatus === 'NOT_CONFIGURED'"
              type="warning"
              :closable="false"
              show-icon
              class="rc-alert"
              title="这个工位本级还没登记"
            >
              只说明没有单独设过可用时段，<strong>不代表它一定不能排产</strong> ——
              所属工厂或部门开放了，它照样能排。需要单独调整它的可用时段时，用「新增一段」或「批量铺窗」。
            </ElAlert>

            <ElTable
              v-if="slotWindowTotal > 0"
              :data="slotWindowItems"
              :loading="loading"
              border
              stripe
              row-key="id"
              size="small"
              style="width: 100%"
            >
              <ElTableColumn label="日期" min-width="160" fixed="left">
                <template #default="{ row }">
                  <span>{{ fmtDayLabel(row.startTime) }}</span>
                  <ElTag
                    v-if="isTodayRow(row.startTime)"
                    size="small"
                    type="danger"
                    effect="plain"
                    class="today-tag"
                  >
                    今天
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn label="时段" width="150">
                <template #default="{ row }">
                  {{ timeOf(row.startTime) }} ~ {{ timeOf(row.endTime) }}
                </template>
              </ElTableColumn>
              <ElTableColumn label="可用" width="90" align="center">
                <template #default="{ row }">
                  <ElTag :type="row.availableFlag ? 'success' : 'danger'" size="small">
                    {{ row.availableFlag ? '可用' : '禁用' }}
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn prop="remark" label="备注" min-width="160" show-overflow-tooltip />
              <ElTableColumn label="操作" width="100" fixed="right" v-if="canDeleteManual">
                <template #default="{ row }">
                  <ElButton
                    type="danger"
                    size="small"
                    :loading="saving"
                    @click="removeSlotWindow(row)"
                  >
                    删除
                  </ElButton>
                </template>
              </ElTableColumn>
            </ElTable>

            <!-- 10-17：人工槽窗口服务端分页（与设备窗口同款范式） -->
            <ElPagination
              v-if="slotWindowTotal > 0"
              :current-page="slotWindowPage"
              :page-size="slotWindowPageSize"
              :total="slotWindowTotal"
              :page-sizes="[10, 20, 50, 100, 200]"
              layout="total, sizes, prev, pager, next, jumper"
              background
              class="rc-pagination"
              @current-change="onSlotWindowPageChange"
              @size-change="onSlotWindowSizeChange"
            />

            <!-- ===== 三级合并后可排时段（人工槽侧，与设备侧同一 Resolver 口径） ===== -->
            <ResolvePreviewPanel
              :preview="manualResolvePreview"
              :loading="manualResolveLoading"
              :range="manualResolveRange"
              empty-text="选择人工槽后展示工厂 / 部门 / 本级合并后的可排时段"
              @update:range="onManualResolveRangeChange"
              @refresh="loadManualResolvePreview"
            />
          </template>
        </ElTabPane>
      </ElTabs>
    </ElCard>

    <!-- ===== Dialog：设备新增一段（只追加，不动其他时段） ===== -->
    <CalendarSingleWindowDialog
      :visible="deviceSingleDialogVisible"
      :submitting="saving"
      mode="DEVICE"
      :target-id="currentResourceId"
      :target-label="currentResourceLabel"
      :existing-total="deviceWindowTotal"
      @update:visible="(v) => (deviceSingleDialogVisible = v)"
      @submit="submitDeviceSingle"
    />

    <!-- ===== Dialog：设备铺窗（覆盖该区间） ===== -->
    <CalendarBulkDialog
      :visible="deviceDialogVisible"
      :submitting="saving"
      mode="DEVICE"
      :target-id="currentResourceId"
      :target-label="currentResourceLabel"
      @update:visible="(v) => (deviceDialogVisible = v)"
      @submit="submitDeviceBulk"
    />

    <!-- ===== Dialog：新增人工能力槽 ===== -->
    <ManualSlotCreateDialog
      :visible="slotDialogVisible"
      :submitting="saving"
      :department-candidates="departmentCandidates"
      :is-fallback-source="true"
      @update:visible="(v) => (slotDialogVisible = v)"
      @submit="submitCreateSlot"
    />

    <!-- ===== Dialog：人工槽新增一段（只追加） ===== -->
    <CalendarSingleWindowDialog
      :visible="slotSingleDialogVisible"
      :submitting="saving"
      mode="MANUAL_SLOT"
      :target-id="currentSlotId"
      :target-label="currentSlotLabel"
      :existing-total="slotWindowTotal"
      @update:visible="(v) => (slotSingleDialogVisible = v)"
      @submit="submitSlotSingle"
    />

    <!-- ===== Dialog：人工槽铺窗（覆盖该区间） ===== -->
    <CalendarBulkDialog
      :visible="slotBulkDialogVisible"
      :submitting="saving"
      mode="MANUAL_SLOT"
      :target-id="pendingSlotBulkId ?? currentSlotId"
      :target-label="currentSlotLabel"
      @update:visible="(v) => (slotBulkDialogVisible = v)"
      @submit="submitSlotBulk"
    />
  </div>
</template>

<style scoped>
.rc-page {
  padding: 16px;
}

/* 三级合并后可排时段的样式随 ResolvePreviewPanel 组件一起搬走（10-26） */
.rc-intro {
  margin-bottom: 12px;
}
.intro-header {
  display: flex;
  align-items: center;
  gap: 16px;
}
.intro-title {
  font-weight: 600;
  font-size: 15px;
}
/* 日期筛选行（设备侧 / 人工槽侧各一行） */
.rc-filter-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin: 0 0 10px;
  font-size: 13px;
}
.today-tag {
  margin-left: 6px;
}
.intro-help {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  border: 1px solid var(--el-border-color);
  color: var(--el-text-color-secondary);
  font-size: 11px;
  cursor: help;
  user-select: none;
}
.intro-help:hover {
  border-color: var(--el-color-primary);
  color: var(--el-color-primary);
}
.help-pop {
  max-width: 300px;
  font-size: 12px;
  line-height: 1.7;
}
.help-pop p {
  margin: 0 0 4px;
}
.help-pop-title {
  font-weight: 600;
}
.role-switcher {
  margin-left: auto;
}
.role-badge {
  cursor: pointer;
  font-size: 12px;
  letter-spacing: 0.5px;
  transition: transform 0.15s ease;
}
.role-badge:hover {
  transform: translateY(-1px);
}
.role-hint {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.rc-alert {
  margin-top: 10px;
}
.rc-error {
  margin-bottom: 12px;
}
.rc-alert :deep(code) {
  background: rgba(0, 0, 0, 0.05);
  padding: 1px 6px;
  border-radius: 3px;
  font-size: 12px;
}
.rc-toolbar {
  margin-bottom: 6px;
}
.rc-btn-gap {
  margin-left: 10px;
}
.rc-status-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin: 10px 0 12px;
  font-size: 13px;
}
.rc-status-label {
  color: var(--el-text-color-secondary);
}
.rc-hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  margin-left: 6px;
}
.rc-source-hint {
  margin-left: 0;
  margin-bottom: 8px;
}

/* 分页（10-12 RC22 收：人工槽 Tab2 接入 ElPagination） */
.rc-pagination {
  margin-top: 12px;
  justify-content: flex-end;
}

:deep(.rc-row-disabled) {
  opacity: 0.6;
}
</style>
