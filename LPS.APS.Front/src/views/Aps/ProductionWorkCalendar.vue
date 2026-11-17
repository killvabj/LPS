<script setup lang="ts">
/**
 * APS V1 4号位 — Production Work Calendar（工厂/部门上级工作日历）维护页
 *
 * 依据：
 *  - 冻结文档 v1.4《Resource Calendar资源日历能力补充冻结方案 v1.4（2026-10-12 三级继承最小增量候选）》
 *  - 5号位 2026-10-13《ProductionWorkCalendar三级日历维护接口文档 — 给4号位》
 *
 * 单页结构（与 v1.3 RC 页面 3 Tab 区分；PWC 暂为单页：
 *  - 筛选区（scopeType / factoryId / departmentId / 只看生效行）
 *    工厂→部门联动：先选工厂才可选部门（冻结 v5.1.12 line 2946 DEPARTMENT 行须校验 FactoryId 一致）
 *    不暴露 includeInactive：后端无路径产生 IsActive=0 行（CloseRow/CloseOpen 只写 EffectiveTo，
 *    INSERT 恒写 IsActive=1），开关无效会误导用户；待后端落地「整行作废」端点后再评估
 *    「只看生效行」（10-26 起）：后端已落 `onlyEffective` 查询参数（5号位，10-23 函 R2 选 (a)），
 *    按**生效日期**（EffectiveFrom/EffectiveTo）过滤，@Today = 服务器本地日；与 includeInactive 不同维度
 *  - ElTable（每行：scope/factory/dept + 周/日期 + 时间窗 + workState + 生效状态 + 加班授权 + 有效期 + 操作）
 *    来源字段（sourceSystem/sourceReference）不在列表展示——业务用户不接触，走审计日志
 *    （@see feedback_no_internal_notices_in_ui；探针 PWC15 + PWC17 锁）
 *    ⚠️ 本页**不做 KPI 卡**：行数统计只按当前页算，与分页 total 不同口径，做出来是假数
 *    ⇒ 相关 getter（workCount/offCount/overrideCount）已从 store 删除，勿再挂卡片
 *  - 「生效状态」列是**前端按 effectiveFrom/EffectiveTo 派生**的（生效中/未生效/已过期）：
 *    服务端已支持按生效日期过滤，但**行级状态仍要显示**（未生效/已过期行是维护对象），
 *    开关只控制「列表里看得到哪些行」，不替代行上的状态标注
 *  - ElPagination（与 manual-capacity / ManualETA / business-fact-issues 同款）
 *  - 弹窗区（UI 文案全中文，WORK/OFF 对用户显示「要工作/不工作」）：
 *    ① 工作周设置弹窗（每周固定班次，同星期多窗口并存=白班+小夜+大夜）
 *    ② 日期例外弹窗（放假OFF / 补班WORK / 授权加班，覆盖某一天的固定班次）
 *    ③ 批量到期换版弹窗
 *  - 治理注（v1.4 G1 候选 + G4 公告前供联调）
 */

import { computed, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDialog,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElPagination,
  ElRadioButton,
  ElRadioGroup,
  ElSelect,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus'
import {
  PWC_SCOPE_META,
  PWC_SHIFT_PRESETS,
  PWC_WORK_STATE_META,
  WEEKDAY_ISO_META,
  departmentLookupApi,
  domainApi,
  type DepartmentLookupItem,
  type FactorySummaryDto,
  type ProductionWorkCalendarDto,
  type ProductionWorkCalendarScope,
  type ProductionWorkState,
  type WeeklyCalendarEntry,
  type WeeklyCalendarUpsertRequest,
  type WorkCalendarExceptionRequest,
  type CalendarCloseOpenRequest
} from '@/api/aps-v1'
import { useProductionWorkCalendarStore } from '@/store/modules/aps/productionWorkCalendar'

const store = useProductionWorkCalendarStore()

const { list, total, page, pageSize, loading, saving, error, canView, canEdit, canDelete } =
  storeToRefs(store)

/* ===== 权限（@see 5号位 函 §〇.2：复用 aps.resource_calendar.* 三码）===== */
const canWrite = computed(() => canEdit.value)
const canRemove = computed(() => canDelete.value)

/* ===== 「今天」= 本地日期 =====
 *  后端判定生效用的是 DateTime.Today（服务器本地日，Service.cs:346）；
 *  Date.prototype.toISOString() 是 UTC 日 ⇒ 本地 00:00–08:00 会整整差一天，
 *  凌晨时段算出的「今天」与后端不一致（生效状态、删除按钮可用性都会判错）。 */
function todayLocal(): string {
  const d = new Date()
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}
/** 本地日期偏移（days 可负） */
function shiftLocal(days: number, base?: string): string {
  const d = base ? new Date(`${base}T00:00:00`) : new Date()
  d.setDate(d.getDate() + days)
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/* ===== 时分秒归一 =====
 *  后端 StartAt/EndAt 是 TimeSpan?，**收不了空字符串**：JSON 里给 "" ⇒ 整个请求体绑定失败
 *  ⇒ 422「The request field is required.」（那句英文指的是控制器那个叫 request 的参数，
 *  不是「少填了业务字段」，别照着去补字段）。OFF 全天必须**整对不传**，不能传 ""。
 *  这里顺带把 8:00 / 080000 / 08:00 统一成 08:00:00，免得用户手输格式又被拒。 */
function normalizeTimeInput(v?: string): string | undefined {
  const s = (v ?? '').trim()
  if (!s) return undefined
  const m = /^(\d{1,2})[:：]?(\d{2})(?:[:：]?(\d{2}))?$/.exec(s)
  if (!m) return undefined
  const hh = Number(m[1])
  const mm = Number(m[2])
  const ss = Number(m[3] ?? '0')
  if (hh > 23 || mm > 59 || ss > 59) return undefined
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${pad(hh)}:${pad(mm)}:${pad(ss)}`
}

/* ===== 行生效状态（前端派生）=====
 *  dev 实测 62 行里 10 行已过期、3 行未生效，此前列表无任何标识，用户会把过期行当现状。
 *  effectiveTo 含当日（后端换版闭到 eff.AddDays(-1)，即末日仍生效）⇒ 到期日当天仍是「生效中」。 */
type PwcEffectiveStatus = 'ACTIVE' | 'NOT_YET' | 'EXPIRED' | 'DISABLED'
const PWC_EFFECTIVE_STATUS_META: Record<
  PwcEffectiveStatus,
  { label: string; tagType: 'success' | 'info' | 'warning' | 'danger' }
> = {
  ACTIVE: { label: '生效中', tagType: 'success' },
  NOT_YET: { label: '未生效', tagType: 'info' },
  EXPIRED: { label: '已过期', tagType: 'warning' },
  DISABLED: { label: '已停用', tagType: 'danger' }
}
function effectiveStatus(row: ProductionWorkCalendarDto): PwcEffectiveStatus {
  if (!row.isActive) return 'DISABLED'
  const today = todayLocal()
  // 日期例外行：只管 workDate 那一天（EffectiveTo 恒空，拿有效期区间判会「永远生效中」）
  if (row.workDate) {
    const d = row.workDate.slice(0, 10)
    if (d > today) return 'NOT_YET'
    if (d < today) return 'EXPIRED'
    return 'ACTIVE'
  }
  if (row.effectiveFrom.slice(0, 10) > today) return 'NOT_YET'
  const to = row.effectiveTo ? row.effectiveTo.slice(0, 10) : null
  if (to && to < today) return 'EXPIRED'
  return 'ACTIVE'
}

/* ===== 筛选已选 label（@see feedback_list_id_to_name：展示名称不展示 ID）===== */
const filterFactoryLabel = computed(() => {
  const f = factoryOptions.value.find((x) => x.id === filterFactoryId.value)
  if (!f) return ''
  return f.name ? `${f.name}（${f.code}）` : f.code
})
const filterDepartmentLabel = computed(() => {
  const d = departmentOptions.value.find((x) => x.masterId === filterDepartmentId.value)
  if (!d) return ''
  return deptOptionLabel(d)
})

/* ===== 筛选本地 state ===== */
const filterScope = ref<ProductionWorkCalendarScope | ''>('')
const filterFactoryId = ref<number | undefined>(undefined)
const filterDepartmentId = ref<number | undefined>(undefined)
/** 「只看生效行」开关（10-26：5号位 已落 `onlyEffective` 查询参数）
 *  - 判据按**生效日期**（EffectiveFrom/EffectiveTo），与 `includeInactive`（IsActive）不是同一维度
 *  - 默认关：维护页要能看到未生效 / 已过期行；打开后只留今日生效中的行 */
const filterOnlyEffective = ref<boolean>(false)

/* ===== 筛选下拉数据源（@see feedback_list_id_to_name：列表/筛选展示名称）=====
 *  - 工厂：domainApi.listFactories → GET /api/governance/factories（Domain.vue 同源）
 *  - 部门：departmentLookupApi.listDepartments → GET /api/governance/lookups/departments（TSRC/RC 同源）
 *  - onMounted 预加载 + 失败兜底空数组（不让筛选区挂掉）
 */
const factoryOptions = ref<FactorySummaryDto[]>([])
const departmentOptions = ref<DepartmentLookupItem[]>([])
/** 部门名称表：Id → { code, name }
 *  - 数据源：GET /api/production-departments（5号位 ProductionDepartmentController，出参含 deptName）
 *  - 权限码与本页**完全一致**（PermissionCodes.ResourceCalendarView = aps.resource_calendar.view）
 *    ⇒ 只持日历域权限的角色也能取到，不会像 OPM 页那样跨域 403
 *  - 用途：把「部门 Id」翻译成「部门名称（编码）」；取不到时页面退化为显示编码，**不显示数字 Id**
 */
const departmentNameMap = ref<Record<number, { code: string; name: string }>>({})
async function loadFilterOptions(): Promise<void> {
  const results = await Promise.allSettled([
    domainApi.listFactories(),
    // limit=500：dev 实库 287 个部门，缺省 200 会截断（后面的部门在下拉里直接消失）
    departmentLookupApi.listDepartments(500),
    departmentLookupApi.listProductionDepartmentsOrThrow()
  ])
  factoryOptions.value = results[0].status === 'fulfilled' ? results[0].value : []
  departmentOptions.value = results[1].status === 'fulfilled' ? results[1].value : []
  const map: Record<number, { code: string; name: string }> = {}
  if (results[2].status === 'fulfilled') {
    for (const d of results[2].value) map[d.id] = { code: d.deptCode, name: d.deptName }
  }
  departmentNameMap.value = map
}

/** 部门下拉 / 筛选回显文案：名称（编码）；名称取不到时退回编码（holon / 阶段码） */
function deptOptionLabel(d: DepartmentLookupItem): string {
  const hit = departmentNameMap.value[d.masterId]
  if (hit?.name) return `${hit.name}（${hit.code}）`
  if (hit?.code) return hit.code
  return d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')
}

/* ===== 工厂→部门联动（冻结 v5.1.12 line 2946 / 3284）=====
 *  - 规则：DEPARTMENT 行所选部门必须归属所选工厂（后端写入时 400 拦截），故下拉必须先收敛
 *  - 数据源：DepartmentLookupItem.factoryId（3号位 A组 A1 已落地，dev 实测 200/200 条有值）
 *  - 降级：只给「归属未知」（factoryId=null）的部门，**绝不回退成全量**
 *    （10-23 用户实测踩坑：SH 工厂在字典里无部门 → 旧降级回退全量 → 选到 BJ 的部门 → 必被后端 400）
 */
const deptOptionsByFactory = computed(() => {
  const map = new Map<number, DepartmentLookupItem[]>()
  for (const d of departmentOptions.value) {
    if (d.factoryId == null) continue
    const bucket = map.get(d.factoryId)
    if (bucket) bucket.push(d)
    else map.set(d.factoryId, [d])
  }
  return map
})
function deptOptionsFor(factoryId: number | undefined): DepartmentLookupItem[] {
  if (factoryId == null) return []
  const mapped = deptOptionsByFactory.value.get(factoryId)
  if (mapped) return mapped
  // 该工厂在字典里没有归属明确的部门 ⇒ 只给「归属未知」的部门兜底
  // ⚠️ 不能回退成全量：跨厂部门后端一律 400（部门归属工厂与请求 FactoryId 不一致），
  //    回退全量只会让用户选到一个必然失败的选项（10-23 实测）
  return departmentOptions.value.filter((d) => d.factoryId == null)
}
/** 工厂/范围变更后，所选部门若已不在该工厂下则清空（防残留他厂部门被后端 400 拒） */
function deptAfterFactoryChange(
  factoryId: number | undefined,
  deptId: number | undefined
): number | undefined {
  if (deptId == null) return undefined
  return deptOptionsFor(factoryId).some((d) => d.masterId === deptId) ? deptId : undefined
}

/* ===== 弹窗态 ===== */
const weeklyDialogOpen = ref(false)
const exceptionDialogOpen = ref(false)
const closeOpenDialogOpen = ref(false)

/* ===== 周基准弹窗表单 ===== */
const weeklyDraft = ref<{
  scopeType: ProductionWorkCalendarScope
  factoryId: number | undefined
  productionDepartmentId: number | undefined
  effectiveFrom: string
  sourceSystem: string
  sourceReference: string
  entries: WeeklyCalendarEntry[]
}>({
  scopeType: 'FACTORY',
  factoryId: undefined,
  productionDepartmentId: undefined,
  effectiveFrom: shiftLocal(7),
  sourceSystem: 'MANUAL',
  sourceReference: '',
  entries: [
    { weekdayIso: 1, workState: 'WORK', startAt: '08:00:00', endAt: '17:00:00' },
    { weekdayIso: 7, workState: 'OFF' }
  ]
})

function addWeeklyEntry(): void {
  weeklyDraft.value.entries.push({
    weekdayIso: 1,
    workState: 'WORK',
    startAt: '08:00:00',
    endAt: '17:00:00'
  })
}
function removeWeeklyEntry(idx: number): void {
  weeklyDraft.value.entries.splice(idx, 1)
}
function applyShiftPreset(idx: number, preset: (typeof PWC_SHIFT_PRESETS)[number]): void {
  weeklyDraft.value.entries[idx] = {
    weekdayIso: preset.weekdayIso,
    workState: preset.workState,
    startAt: preset.startAt,
    endAt: preset.endAt
  }
}
function onEntryPresetChange(idx: number, v: unknown): void {
  if (v && typeof v === 'object' && 'label' in v) {
    applyShiftPreset(idx, v as (typeof PWC_SHIFT_PRESETS)[number])
  }
}
async function submitWeekly(): Promise<void> {
  if (!weeklyDraft.value.entries.length) {
    ElMessage.warning('请至少添加一条班次')
    return
  }
  const fid = weeklyDraft.value.factoryId
  if (!fid) {
    ElMessage.warning('请选择工厂')
    return
  }
  if (weeklyDraft.value.scopeType === 'DEPARTMENT' && !weeklyDraft.value.productionDepartmentId) {
    ElMessage.warning('范围选「部门」时必须选择具体部门')
    return
  }
  if (weeklyDraft.value.scopeType === 'FACTORY') {
    weeklyDraft.value.productionDepartmentId = undefined
  }
  // 提交前按状态清洗（不送后端必拒的组合）：
  //  ① 时间：OFF 全天**整对不传**（传空串 ⇒ 整包绑定失败 422）；WORK 必须成对且 start<end
  //  ② 旗位 AllowLowerOverride：后端「部门自主模式」恒 1（显式 false 拒 400）⇒ 页面无开关、也不发该字段
  const entries: WeeklyCalendarUpsertRequest['entries'] = []
  for (const [i, e] of weeklyDraft.value.entries.entries()) {
    const start = normalizeTimeInput(e.startAt)
    const end = normalizeTimeInput(e.endAt)
    const rowName = `第 ${i + 1} 行`
    if (e.workState === 'WORK') {
      if (!start || !end) {
        ElMessage.warning(`${rowName}选了「要工作」，必须填上班和下班时间（形如 08:00:00）`)
        return
      }
      if (start >= end) {
        ElMessage.warning(`${rowName}的下班时间要晚于上班时间`)
        return
      }
    } else if ((start && !end) || (!start && end)) {
      ElMessage.warning(`${rowName}选了「不工作」，时间要么都不填（全天），要么成对填`)
      return
    }
    entries.push({
      weekdayIso: e.weekdayIso,
      workState: e.workState,
      ...(start && end ? { startAt: start, endAt: end } : {})
    })
  }
  const req: WeeklyCalendarUpsertRequest = {
    scopeType: weeklyDraft.value.scopeType,
    factoryId: fid,
    productionDepartmentId: weeklyDraft.value.productionDepartmentId,
    effectiveFrom: weeklyDraft.value.effectiveFrom,
    sourceSystem: weeklyDraft.value.sourceSystem,
    sourceReference: weeklyDraft.value.sourceReference,
    entries
  }
  const n = await store.upsertWeekly(req)
  if (n != null) {
    ElMessage.success(`已铺设 ${n} 条周基准`)
    weeklyDialogOpen.value = false
  }
}

/* ===== 日期例外弹窗表单 ===== */
const exceptionDraft = ref<{
  scopeType: ProductionWorkCalendarScope
  factoryId: number | undefined
  productionDepartmentId: number | undefined
  workDate: string
  workState: ProductionWorkState
  startAt: string
  endAt: string
  sourceSystem: string
  sourceReference: string
}>({
  scopeType: 'FACTORY',
  factoryId: undefined,
  productionDepartmentId: undefined,
  workDate: todayLocal(),
  workState: 'OFF',
  startAt: '',
  endAt: '',
  sourceSystem: 'MANUAL',
  sourceReference: ''
})

async function submitException(): Promise<void> {
  // 旗位 AllowLowerOverride / IsAuthorizedOverride：后端「部门自主模式」**恒 1**
  //   （库约束 CK_PWCS_FlagConst 锁死 + Service INSERT 写死 alo=1/aao=1 + 显式 false 直接 400）
  //   ⇒ 页面上的开关是无效控件，已撤；这里也不发这两个字段（缺省 null ⇒ 后端按 1 落库）。
  // 「授权加班」的真凭据是**加班依据**（SourceReference，列表按有没有依据打标签）。
  const start = normalizeTimeInput(exceptionDraft.value.startAt)
  const end = normalizeTimeInput(exceptionDraft.value.endAt)
  if (exceptionDraft.value.workState === 'WORK' && (!start || !end)) {
    ElMessage.warning('状态选「要工作」时必须填写上班和下班时间（形如 08:00:00）')
    return
  }
  if (start && end && start >= end) {
    ElMessage.warning('下班时间要晚于上班时间')
    return
  }
  if (exceptionDraft.value.workState === 'OFF' && ((start && !end) || (!start && end))) {
    ElMessage.warning('「不工作」的时间要么都不填（全天），要么成对填')
    return
  }
  if (
    exceptionDraft.value.scopeType === 'DEPARTMENT' &&
    !exceptionDraft.value.productionDepartmentId
  ) {
    ElMessage.warning('范围选「部门」时必须选择具体部门')
    return
  }
  const fid = exceptionDraft.value.factoryId
  if (!fid) {
    ElMessage.warning('请选择工厂')
    return
  }
  if (exceptionDraft.value.scopeType === 'FACTORY') {
    exceptionDraft.value.productionDepartmentId = undefined
  }
  const req: WorkCalendarExceptionRequest = {
    scopeType: exceptionDraft.value.scopeType,
    factoryId: fid,
    productionDepartmentId: exceptionDraft.value.productionDepartmentId,
    workDate: exceptionDraft.value.workDate,
    workState: exceptionDraft.value.workState,
    startAt: start,
    endAt: end,
    sourceSystem: exceptionDraft.value.sourceSystem,
    sourceReference: exceptionDraft.value.sourceReference || undefined,
    // 例外天然只管 workDate 这一天，生效起始日与 workDate 同值；结束日不传=长期
    // （后端 Service.cs:203 亦有 eff = EffectiveFrom == default ? workDate 兜底）
    effectiveFrom: exceptionDraft.value.workDate
  }
  const id = await store.createException(req)
  if (id != null) {
    ElMessage.success('已创建日期例外')
    exceptionDialogOpen.value = false
  }
}

/* ===== 批量到期换版弹窗 ===== */
const closeOpenDraft = ref<{
  scopeType: ProductionWorkCalendarScope
  factoryId: number | undefined
  productionDepartmentId: number | undefined
  newEffectiveFrom: string
}>({
  scopeType: 'FACTORY',
  factoryId: undefined,
  productionDepartmentId: undefined,
  newEffectiveFrom: shiftLocal(30)
})

async function submitCloseOpen(): Promise<void> {
  const fid = closeOpenDraft.value.factoryId
  if (!fid) {
    ElMessage.warning('请选择工厂')
    return
  }
  if (closeOpenDraft.value.scopeType === 'FACTORY') {
    closeOpenDraft.value.productionDepartmentId = undefined
  }
  const req: CalendarCloseOpenRequest = {
    scopeType: closeOpenDraft.value.scopeType,
    factoryId: fid,
    productionDepartmentId: closeOpenDraft.value.productionDepartmentId,
    newEffectiveFrom: closeOpenDraft.value.newEffectiveFrom
  }
  const n = await store.closeOpen(req)
  if (n != null) {
    ElMessage.success(`已到期停用 ${n} 条旧班次（截止到 ${req.newEffectiveFrom} 前一天）`)
    closeOpenDialogOpen.value = false
  }
}

/* ===== 工厂→部门联动：切工厂后收敛已选部门 ===== */
watch(
  () => [weeklyDraft.value.factoryId, weeklyDraft.value.scopeType],
  ([fid]) => {
    weeklyDraft.value.productionDepartmentId = deptAfterFactoryChange(
      fid as number | undefined,
      weeklyDraft.value.productionDepartmentId
    )
  }
)
watch(
  () => [exceptionDraft.value.factoryId, exceptionDraft.value.scopeType],
  ([fid]) => {
    exceptionDraft.value.productionDepartmentId = deptAfterFactoryChange(
      fid as number | undefined,
      exceptionDraft.value.productionDepartmentId
    )
  }
)
watch(
  () => [closeOpenDraft.value.factoryId, closeOpenDraft.value.scopeType],
  ([fid]) => {
    closeOpenDraft.value.productionDepartmentId = deptAfterFactoryChange(
      fid as number | undefined,
      closeOpenDraft.value.productionDepartmentId
    )
  }
)
watch(filterFactoryId, (fid) => {
  filterDepartmentId.value = deptAfterFactoryChange(fid, filterDepartmentId.value)
})

/* ===== 打开弹窗：范围/工厂/部门跟随筛选区已选（不再硬编码某一家工厂）===== */
function syncScopeFromFilter(draft: {
  scopeType: ProductionWorkCalendarScope
  factoryId: number | undefined
  productionDepartmentId: number | undefined
}): void {
  if (filterScope.value) draft.scopeType = filterScope.value
  if (filterFactoryId.value) draft.factoryId = filterFactoryId.value
  if (filterDepartmentId.value) {
    draft.productionDepartmentId = deptAfterFactoryChange(draft.factoryId, filterDepartmentId.value)
  }
}
function openWeeklyDialog(): void {
  syncScopeFromFilter(weeklyDraft.value)
  weeklyDialogOpen.value = true
}
function openExceptionDialog(): void {
  syncScopeFromFilter(exceptionDraft.value)
  exceptionDialogOpen.value = true
}
function openCloseOpenDialog(): void {
  syncScopeFromFilter(closeOpenDraft.value)
  closeOpenDialogOpen.value = true
}

/* ===== 行级 close/delete ===== */
async function onCloseRow(row: ProductionWorkCalendarDto): Promise<void> {
  if (!canEdit.value) return
  try {
    const { value } = await ElMessageBox.prompt(
      `${rowTitle(row)}：填写这条设置从哪天起不再生效。到期停用后会保留历史记录；到期日早于该行生效起始日将被拒。`,
      '到期停用这一条',
      {
        inputPattern: /^\d{4}-\d{2}-\d{2}$/,
        inputErrorMessage: '格式 yyyy-MM-dd',
        inputValue: todayLocal()
      }
    )
    const ok = await store.closeRow(row.id, value as string)
    if (ok) ElMessage.success('已到期停用')
  } catch {
    // 用户取消或后端拒
  }
}

async function onDeleteRow(row: ProductionWorkCalendarDto): Promise<void> {
  if (!canDelete.value) return
  if (row.effectiveFrom.slice(0, 10) <= todayLocal()) {
    ElMessage.warning('这条设置已经生效了，请改用「到期停用」（保留历史记录）')
    return
  }
  try {
    await ElMessageBox.confirm(
      `${rowTitle(row)}：这条设置还没生效，删除后无法恢复（不保留历史记录）。确认删除吗？`,
      '删除这一条',
      { type: 'warning' }
    )
  } catch {
    return
  }
  const ok = await store.deleteRow(row.id)
  if (ok) ElMessage.success('已删除')
}

/* ===== 分页 / 筛选 ===== */
async function applyFilter(): Promise<void> {
  await store.load(
    {
      scopeType: filterScope.value || undefined,
      factoryId: filterFactoryId.value,
      departmentId: filterDepartmentId.value,
      onlyEffective: filterOnlyEffective.value
    },
    { resetPage: true }
  )
}
async function resetFilter(): Promise<void> {
  filterScope.value = ''
  filterFactoryId.value = undefined
  filterDepartmentId.value = undefined
  filterOnlyEffective.value = false
  await store.load({}, { resetPage: true })
}
async function onPageChange(p: number): Promise<void> {
  await store.load(undefined, { nextPageIndex: p })
}
async function onSizeChange(s: number): Promise<void> {
  await store.load(undefined, { resetPage: true, nextPageSize: s })
}

/* ===== 行内显示：一律用名称，不把内部 Id 甩给工厂用户 ===== */
function factoryText(row: ProductionWorkCalendarDto): string {
  const f = factoryOptions.value.find((x) => x.id === row.factoryId)
  const name = f?.name
  const code = f?.code || row.factoryCode
  if (name && code) return `${name}（${code}）`
  return name || code || '未指定工厂'
}
function deptText(row: ProductionWorkCalendarDto): string {
  if (!row.productionDepartmentId) return ''
  const hit = departmentNameMap.value[row.productionDepartmentId]
  const name = row.departmentName || hit?.name
  const code = hit?.code
  if (name && code) return `${name}（${code}）`
  return name || code || '未命名部门'
}
/** 行的业务标识（弹窗提示里说清是哪一行，不用 Id 指代） */
function rowTitle(row: ProductionWorkCalendarDto): string {
  const scope = PWC_SCOPE_META[row.scopeType].label
  const who = row.scopeType === 'DEPARTMENT' ? deptText(row) : factoryText(row)
  return `${scope} · ${who} · ${fmtWeekdayOrDate(row)}`
}

/* ===== 格式化辅助 ===== */
/** 时分秒 → HH:mm（工厂用户看班次不需要秒） */
function fmtTime(t?: string): string {
  return t ? t.slice(0, 5) : ''
}
function fmtWindow(row: ProductionWorkCalendarDto): string {
  if (!row.startAt && !row.endAt) return '全天'
  return `${fmtTime(row.startAt) || '??'} ~ ${fmtTime(row.endAt) || '??'}`
}
function fmtWeekdayOrDate(row: ProductionWorkCalendarDto): string {
  if (row.workDate) return row.workDate.slice(0, 10)
  if (row.weekdayIso != null) {
    const meta = WEEKDAY_ISO_META[row.weekdayIso]
    return meta ? `周${meta.short}（每周固定）` : '—'
  }
  return '—'
}
/** 有效期：起始日 ~ 截止日 / 长期
 *  - 日期例外行（workDate 有值）例外：它只管那一天（靠 workDate 定位，不靠有效期区间），
 *    显示「仅 2026-11-27」比「2026-11-27 起（长期）」贴近工厂用户的理解 */
function fmtEffective(row: ProductionWorkCalendarDto): string {
  if (row.workDate) return `仅 ${row.workDate.slice(0, 10)}`
  const from = row.effectiveFrom.slice(0, 10)
  return row.effectiveTo ? `${from} ~ ${row.effectiveTo.slice(0, 10)}` : `${from} 起（长期）`
}
onMounted(async () => {
  if (!canView.value) {
    ElMessage.warning('当前账号没有查看工作日历的权限，请联系系统管理员')
    return
  }
  await loadFilterOptions()
  await store.load({}, { resetPage: true })
})
</script>

<template>
  <div class="pwc-page">
    <!-- ===== 错误条 ===== -->
    <ElAlert
      v-if="error"
      type="error"
      :closable="false"
      show-icon
      :title="`加载/操作失败：${error}`"
      class="pwc-mb"
    />

    <!-- ===== 筛选 + 操作 ===== -->
    <ElCard class="pwc-panel">
      <template #header>
        <div class="pwc-panel-header">
          <span>筛选条件</span>
          <span class="pwc-hint-text">
            <span v-if="filterScope">范围：{{ PWC_SCOPE_META[filterScope].label }}</span>
            <span v-if="filterFactoryLabel"> · 厂：{{ filterFactoryLabel }}</span>
            <span v-if="filterDepartmentLabel"> · 部门：{{ filterDepartmentLabel }}</span>
          </span>
        </div>
      </template>
      <div class="pwc-filter-row">
        <ElSelect v-model="filterScope" placeholder="范围" clearable class="pwc-filter-item">
          <ElOption label="工厂" value="FACTORY" />
          <ElOption label="部门" value="DEPARTMENT" />
        </ElSelect>
        <ElSelect
          v-model="filterFactoryId"
          placeholder="工厂"
          clearable
          filterable
          class="pwc-filter-item"
        >
          <ElOption
            v-for="f in factoryOptions"
            :key="f.id"
            :label="f.name ? `${f.name}（${f.code}）` : f.code"
            :value="f.id"
          />
        </ElSelect>
        <ElSelect
          v-model="filterDepartmentId"
          placeholder="部门"
          clearable
          filterable
          :disabled="!filterFactoryId"
          class="pwc-filter-item"
        >
          <ElOption
            v-for="d in deptOptionsFor(filterFactoryId)"
            :key="d.masterId"
            :label="deptOptionLabel(d)"
            :value="d.masterId"
          />
        </ElSelect>
        <ElSwitch
          v-model="filterOnlyEffective"
          active-text="只看生效行"
          class="pwc-filter-item"
          @change="applyFilter"
        />
        <ElButton type="primary" :loading="loading" @click="applyFilter">查询</ElButton>
        <ElButton :disabled="loading" @click="resetFilter">重置</ElButton>
        <ElButton type="primary" :disabled="!canWrite" @click="openWeeklyDialog">
          工作周设置
        </ElButton>
        <ElButton type="primary" :disabled="!canWrite" @click="openExceptionDialog">
          日期例外
        </ElButton>
        <ElButton :disabled="!canWrite" @click="openCloseOpenDialog">批量到期换版</ElButton>
      </div>
    </ElCard>

    <!-- ===== 主表 ===== -->
    <ElCard class="pwc-panel">
      <div v-if="!list.length" class="pwc-empty">
        <ElEmpty description="没有符合条件的工作日历设置" />
      </div>
      <ElTable v-else :data="list" :loading="loading" border stripe size="small">
        <ElTableColumn label="范围 · 厂 · 部门" min-width="240">
          <template #default="{ row }">
            <ElTag :type="PWC_SCOPE_META[row.scopeType].tagType" size="small">
              {{ PWC_SCOPE_META[row.scopeType].label }}
            </ElTag>
            <span class="pwc-cell-main">{{ factoryText(row) }}</span>
            <span v-if="row.productionDepartmentId" class="pwc-cell-sub">
              {{ deptText(row) }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="周/日期" min-width="140">
          <template #default="{ row }">{{ fmtWeekdayOrDate(row) }}</template>
        </ElTableColumn>
        <ElTableColumn label="时间窗" min-width="140">
          <template #default="{ row }">
            <span class="pwc-cell-mono">{{ fmtWindow(row) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="状态" width="170">
          <template #default="{ row }">
            <ElTag :type="PWC_WORK_STATE_META[row.workState].tagType" size="small">
              {{ PWC_WORK_STATE_META[row.workState].label }}
            </ElTag>
            <!-- 日期例外的补班行：排产侧按「授权加班时间片」算（IsAuthorizedOverride=1 + WORK + WorkDate，
                 与解析侧 ResourceCalendarDtos.cs:167 同口径）⇒ 挂一个标识；其余行不挂。
                 （原「加班授权」整列已撤：两个旗位在库里被 CK_PWCS_FlagConst 锁死恒 1，
                   列内容 100% 可由「周/日期 + 状态」推出，横向复读还占 170px） -->
            <ElTag
              v-if="row.workState === 'WORK' && row.isAuthorizedOverride && !!row.workDate"
              type="warning"
              size="small"
              effect="plain"
              class="pwc-ml"
            >
              授权加班
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="生效状态" width="100" align="center">
          <template #default="{ row }">
            <ElTag
              :type="PWC_EFFECTIVE_STATUS_META[effectiveStatus(row)].tagType"
              size="small"
              effect="plain"
            >
              {{ PWC_EFFECTIVE_STATUS_META[effectiveStatus(row)].label }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="有效期" min-width="190">
          <template #default="{ row }">
            <span class="pwc-cell-main">{{ fmtEffective(row) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="220" fixed="right">
          <template #default="{ row }">
            <ElButton
              size="small"
              :disabled="!canEdit || !!row.effectiveTo || !row.isActive"
              title="填写截止日期：这条设置从该日起不再生效，历史记录保留"
              @click="onCloseRow(row)"
            >
              到期停用
            </ElButton>
            <ElButton
              size="small"
              type="danger"
              :disabled="!canRemove || row.effectiveFrom.slice(0, 10) <= todayLocal()"
              :title="
                row.effectiveFrom.slice(0, 10) <= todayLocal()
                  ? '已生效的设置不能删除，请改用「到期停用」'
                  : '删除这条还没生效的设置（不可恢复）'
              "
              @click="onDeleteRow(row)"
            >
              删除
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>

      <ElPagination
        v-if="total > 0"
        :current-page="page"
        :page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100, 200]"
        layout="total, sizes, prev, pager, next, jumper"
        background
        class="pwc-pagination"
        @current-change="onPageChange"
        @size-change="onSizeChange"
      />
    </ElCard>

    <!-- ===== 周基准弹窗 ===== -->
    <ElDialog
      v-model="weeklyDialogOpen"
      title="工作周设置——每周固定班次（同范围同星期整组替换）"
      width="800"
    >
      <ElForm label-width="120px" size="default">
        <ElFormItem label="范围">
          <ElRadioGroup v-model="weeklyDraft.scopeType">
            <ElRadioButton value="FACTORY">工厂</ElRadioButton>
            <ElRadioButton value="DEPARTMENT">部门</ElRadioButton>
          </ElRadioGroup>
        </ElFormItem>
        <ElFormItem label="工厂">
          <ElSelect
            v-model="weeklyDraft.factoryId"
            filterable
            placeholder="选择工厂"
            class="pwc-form-factory"
          >
            <ElOption
              v-for="f in factoryOptions"
              :key="f.id"
              :label="f.name ? `${f.name}（${f.code}）` : f.code"
              :value="f.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem v-if="weeklyDraft.scopeType === 'DEPARTMENT'" label="部门">
          <ElSelect
            v-model="weeklyDraft.productionDepartmentId"
            filterable
            placeholder="选择部门"
            class="pwc-form-dept"
          >
            <ElOption
              v-for="d in deptOptionsFor(weeklyDraft.factoryId)"
              :key="d.masterId"
              :label="deptOptionLabel(d)"
              :value="d.masterId"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="生效起始日">
          <ElDatePicker
            v-model="weeklyDraft.effectiveFrom"
            type="date"
            value-format="YYYY-MM-DD"
            format="YYYY-MM-DD"
            placeholder="选择日期"
            style="width: 200px"
          />
        </ElFormItem>
        <ElFormItem label="每周班次">
          <div class="pwc-entries">
            <div v-for="(e, idx) in weeklyDraft.entries" :key="idx" class="pwc-entry">
              <ElSelect v-model="e.weekdayIso" placeholder="星期" class="pwc-entry-wd">
                <ElOption
                  v-for="(meta, k) in WEEKDAY_ISO_META"
                  :key="k"
                  :label="meta.long"
                  :value="Number(k)"
                />
              </ElSelect>
              <ElSelect v-model="e.workState" placeholder="状态" class="pwc-entry-state">
                <ElOption label="要工作" value="WORK" />
                <ElOption label="不工作" value="OFF" />
              </ElSelect>
              <ElInput
                v-model="e.startAt"
                placeholder="HH:mm:ss"
                :disabled="e.workState === 'OFF' && !e.startAt"
                class="pwc-entry-time"
              />
              <span>~</span>
              <ElInput
                v-model="e.endAt"
                placeholder="HH:mm:ss"
                :disabled="e.workState === 'OFF' && !e.endAt"
                class="pwc-entry-time"
              />
              <ElSelect
                placeholder="常用班次"
                clearable
                class="pwc-entry-preset"
                @change="onEntryPresetChange(idx, $event)"
              >
                <ElOption
                  v-for="(p, i) in PWC_SHIFT_PRESETS"
                  :key="i"
                  :label="p.label"
                  :value="p"
                />
              </ElSelect>
              <ElButton size="small" type="danger" @click="removeWeeklyEntry(idx)"> 删 </ElButton>
            </div>
            <ElButton size="small" @click="addWeeklyEntry">+ 添加</ElButton>
          </div>
        </ElFormItem>
      </ElForm>
      <template #footer>
        <ElButton @click="weeklyDialogOpen = false">取消</ElButton>
        <ElButton type="primary" :loading="saving" @click="submitWeekly">铺设</ElButton>
      </template>
    </ElDialog>

    <!-- ===== 日期例外弹窗 ===== -->
    <ElDialog
      v-model="exceptionDialogOpen"
      title="日期例外（放假 / 补班 / 授权加班）——覆盖某一天的固定班次"
      width="600"
    >
      <ElForm label-width="120px" size="default">
        <ElFormItem label="范围">
          <ElRadioGroup v-model="exceptionDraft.scopeType">
            <ElRadioButton value="FACTORY">工厂</ElRadioButton>
            <ElRadioButton value="DEPARTMENT">部门</ElRadioButton>
          </ElRadioGroup>
        </ElFormItem>
        <ElFormItem label="工厂">
          <ElSelect
            v-model="exceptionDraft.factoryId"
            filterable
            placeholder="选择工厂"
            class="pwc-form-factory"
          >
            <ElOption
              v-for="f in factoryOptions"
              :key="f.id"
              :label="f.name ? `${f.name}（${f.code}）` : f.code"
              :value="f.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.scopeType === 'DEPARTMENT'" label="部门">
          <ElSelect
            v-model="exceptionDraft.productionDepartmentId"
            filterable
            placeholder="选择部门"
            class="pwc-form-dept"
          >
            <ElOption
              v-for="d in deptOptionsFor(exceptionDraft.factoryId)"
              :key="d.masterId"
              :label="deptOptionLabel(d)"
              :value="d.masterId"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="日期">
          <ElDatePicker
            v-model="exceptionDraft.workDate"
            type="date"
            value-format="YYYY-MM-DD"
            format="YYYY-MM-DD"
            placeholder="选择日期"
            style="width: 200px"
          />
          <span class="pwc-hint-text">本例外只针对这一天</span>
        </ElFormItem>
        <ElFormItem label="状态">
          <ElRadioGroup v-model="exceptionDraft.workState">
            <ElRadioButton value="WORK">要工作</ElRadioButton>
            <ElRadioButton value="OFF">不工作</ElRadioButton>
          </ElRadioGroup>
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.workState === 'WORK'" label="时段">
          <ElInput v-model="exceptionDraft.startAt" placeholder="HH:mm:ss" class="pwc-mr" />
          <span>~</span>
          <ElInput v-model="exceptionDraft.endAt" placeholder="HH:mm:ss" class="pwc-ml" />
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.workState === 'OFF'" label="时段">
          <span class="pwc-hint-text">全天休息可留空时间；如需带时段（如午休），请填下方时间</span>
          <div class="pwc-mt">
            <ElInput
              v-model="exceptionDraft.startAt"
              placeholder="HH:mm:ss（可空）"
              class="pwc-mr"
            />
            <span>~</span>
            <ElInput v-model="exceptionDraft.endAt" placeholder="HH:mm:ss（可空）" class="pwc-ml" />
          </div>
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.workState === 'WORK'" label="加班依据">
          <ElInput
            v-model="exceptionDraft.sourceReference"
            placeholder="OA 加班单号等（可留空）"
            class="pwc-form-wide"
          />
          <span class="pwc-hint-text">这次补班的加班凭据（可留空）；填了以后追责查得到依据</span>
        </ElFormItem>
      </ElForm>
      <template #footer>
        <ElButton @click="exceptionDialogOpen = false">取消</ElButton>
        <ElButton type="primary" :loading="saving" @click="submitException">创建</ElButton>
      </template>
    </ElDialog>

    <!-- ===== 批量到期换版弹窗 ===== -->
    <ElDialog
      v-model="closeOpenDialogOpen"
      title="批量到期换版——旧班次统一截止到新版本开始前一天"
      width="500"
    >
      <ElForm label-width="120px" size="default">
        <ElFormItem label="范围">
          <ElRadioGroup v-model="closeOpenDraft.scopeType">
            <ElRadioButton value="FACTORY">工厂</ElRadioButton>
            <ElRadioButton value="DEPARTMENT">部门</ElRadioButton>
          </ElRadioGroup>
        </ElFormItem>
        <ElFormItem label="工厂">
          <ElSelect
            v-model="closeOpenDraft.factoryId"
            filterable
            placeholder="选择工厂"
            class="pwc-form-factory"
          >
            <ElOption
              v-for="f in factoryOptions"
              :key="f.id"
              :label="f.name ? `${f.name}（${f.code}）` : f.code"
              :value="f.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem v-if="closeOpenDraft.scopeType === 'DEPARTMENT'" label="部门">
          <ElSelect
            v-model="closeOpenDraft.productionDepartmentId"
            filterable
            placeholder="选择部门"
            class="pwc-form-dept"
          >
            <ElOption
              v-for="d in deptOptionsFor(closeOpenDraft.factoryId)"
              :key="d.masterId"
              :label="deptOptionLabel(d)"
              :value="d.masterId"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="新版本起始日">
          <ElDatePicker
            v-model="closeOpenDraft.newEffectiveFrom"
            type="date"
            value-format="YYYY-MM-DD"
            format="YYYY-MM-DD"
            placeholder="选择日期"
            style="width: 200px"
          />
        </ElFormItem>
      </ElForm>
      <template #footer>
        <ElButton @click="closeOpenDialogOpen = false">取消</ElButton>
        <ElButton type="primary" :loading="saving" @click="submitCloseOpen">闭区间</ElButton>
      </template>
    </ElDialog>
  </div>
</template>

<style scoped>
.pwc-page {
  padding: 16px;
}
.pwc-mb {
  margin-bottom: 12px;
}
.pwc-mt {
  margin-top: 6px;
}
.pwc-mr {
  margin-right: 6px;
}
.pwc-ml {
  margin-left: 6px;
}
.pwc-panel {
  margin-bottom: 12px;
}
.pwc-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}
.pwc-hint-text {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
.pwc-filter-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.pwc-filter-item {
  width: 180px;
}
.pwc-form-factory {
  width: 260px;
}
.pwc-form-dept {
  width: 260px;
}
.pwc-form-wide {
  width: 320px;
}
.pwc-empty {
  padding: 24px;
  text-align: center;
}
.pwc-pagination {
  margin-top: 12px;
  justify-content: flex-end;
}
.pwc-cell-mono {
  font-family: var(--el-font-family-monospace, ui-monospace, monospace);
  font-size: 12px;
}
.pwc-cell-main {
  display: block;
  margin-top: 2px;
  font-size: 13px;
  color: var(--el-text-color-regular);
}
.pwc-cell-sub {
  display: block;
  font-size: 11px;
  color: var(--el-text-color-secondary);
  margin-top: 2px;
}
.pwc-entries {
  display: flex;
  flex-direction: column;
  gap: 6px;
  width: 100%;
}
.pwc-entry {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.pwc-entry-wd {
  width: 90px;
}
.pwc-entry-state {
  width: 90px;
}
.pwc-entry-time {
  width: 110px;
}
.pwc-entry-preset {
  width: 160px;
}
</style>
