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
  ElCheckbox,
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
  if (row.effectiveFrom.slice(0, 10) > today) return 'NOT_YET'
  const to = row.effectiveTo ? row.effectiveTo.slice(0, 10) : null
  if (to && to < today) return 'EXPIRED'
  return 'ACTIVE'
}

/* ===== 筛选已选 label（@see feedback_list_id_to_name：展示名称不展示 ID）===== */
const filterFactoryLabel = computed(() => {
  const f = factoryOptions.value.find((x) => x.id === filterFactoryId.value)
  return f ? `${f.code}（${f.name}）` : ''
})
const filterDepartmentLabel = computed(() => {
  const d = departmentOptions.value.find((x) => x.masterId === filterDepartmentId.value)
  if (!d) return ''
  return d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')
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
async function loadFilterOptions(): Promise<void> {
  const results = await Promise.allSettled([
    domainApi.listFactories(),
    departmentLookupApi.listDepartments()
  ])
  factoryOptions.value = results[0].status === 'fulfilled' ? results[0].value : []
  departmentOptions.value = results[1].status === 'fulfilled' ? results[1].value : []
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
  factoryId: number
  productionDepartmentId: number | undefined
  effectiveFrom: string
  sourceSystem: string
  sourceReference: string
  entries: WeeklyCalendarEntry[]
}>({
  scopeType: 'FACTORY',
  factoryId: 2,
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
/** 同星期多窗口的「允许下级加班」勾选框只在 OFF 时可勾；状态切成「要工作」后勾选框被禁用但值残留
 *  ⇒ 提交时后端按 DDL CK 拒（AllowLowerOverride 仅 OFF 行）。这里随状态归零（探针 PWC34 锁）。 */
function normalizeWeeklyEntries(): void {
  for (const e of weeklyDraft.value.entries) {
    if (e.workState !== 'OFF') e.allowLowerOverride = false
  }
}
watch(() => weeklyDraft.value.entries.map((e) => e.workState).join(','), normalizeWeeklyEntries)
async function submitWeekly(): Promise<void> {
  if (!weeklyDraft.value.entries.length) {
    ElMessage.warning('请至少添加一条班次')
    return
  }
  if (weeklyDraft.value.scopeType === 'DEPARTMENT' && !weeklyDraft.value.productionDepartmentId) {
    ElMessage.warning('范围选「部门」时必须选择具体部门')
    return
  }
  if (weeklyDraft.value.scopeType === 'FACTORY') {
    weeklyDraft.value.productionDepartmentId = undefined
  }
  const req: WeeklyCalendarUpsertRequest = {
    scopeType: weeklyDraft.value.scopeType,
    factoryId: weeklyDraft.value.factoryId,
    productionDepartmentId: weeklyDraft.value.productionDepartmentId,
    effectiveFrom: weeklyDraft.value.effectiveFrom,
    sourceSystem: weeklyDraft.value.sourceSystem,
    sourceReference: weeklyDraft.value.sourceReference,
    // 提交前按状态清洗（不送后端必拒的组合）
    entries: weeklyDraft.value.entries.map((e) => ({
      ...e,
      allowLowerOverride: e.workState === 'OFF' && e.allowLowerOverride
    }))
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
  factoryId: number
  productionDepartmentId: number | undefined
  workDate: string
  workState: ProductionWorkState
  startAt: string
  endAt: string
  allowLowerOverride: boolean
  isAuthorizedOverride: boolean
  sourceSystem: string
  sourceReference: string
}>({
  scopeType: 'FACTORY',
  factoryId: 2,
  productionDepartmentId: undefined,
  workDate: todayLocal(),
  workState: 'OFF',
  startAt: '',
  endAt: '',
  allowLowerOverride: false,
  isAuthorizedOverride: false,
  sourceSystem: 'MANUAL',
  sourceReference: ''
})

/** 状态与两个互斥标记必须一致（DDL CK：AllowLowerOverride 仅 OFF 行、IsAuthorizedOverride 仅 WORK 行）
 *  - 弹窗里「允许下级加班」开关**只在状态=不工作时渲染**。用户先在「不工作」下打开开关、
 *    再把状态切到「要工作」⇒ 开关看不见了但值还是 true ⇒ 提交被自己的校验挡住，
 *    而用户既看不到也改不掉那个开关 ⇒ **无解报错**（10-23 用户实测踩坑）。
 *  - 切状态即归零；提交前再按状态清洗一次（双保险，探针 PWC34 锁）。 */
function normalizeExceptionDraft(): void {
  if (exceptionDraft.value.workState === 'WORK') {
    exceptionDraft.value.allowLowerOverride = false
  } else {
    exceptionDraft.value.isAuthorizedOverride = false
  }
}
watch(() => exceptionDraft.value.workState, normalizeExceptionDraft)

async function submitException(): Promise<void> {
  // 状态与互斥标记按状态清洗（切状态后隐藏的开关值可能残留 ⇒ 提交时归一，不做无解拦截）
  const allowLowerOverride =
    exceptionDraft.value.workState === 'OFF' && exceptionDraft.value.allowLowerOverride
  const isAuthorizedOverride =
    exceptionDraft.value.workState === 'WORK' && exceptionDraft.value.isAuthorizedOverride
  if (allowLowerOverride !== exceptionDraft.value.allowLowerOverride) {
    ElMessage.warning('「允许下级加班」只在「不工作」时可用，已自动取消')
  }
  if (isAuthorizedOverride !== exceptionDraft.value.isAuthorizedOverride) {
    ElMessage.warning('「本行是授权加班」只在「要工作」时可用，已自动取消')
  }
  if (
    exceptionDraft.value.workState === 'WORK' &&
    (!exceptionDraft.value.startAt || !exceptionDraft.value.endAt)
  ) {
    ElMessage.warning('状态选「要工作」时必须填写上班和下班时间')
    return
  }
  if (isAuthorizedOverride && !exceptionDraft.value.sourceReference) {
    ElMessage.warning('勾选「本行是授权加班」时必须填写加班依据（OA 加班单号等）')
    return
  }
  if (
    exceptionDraft.value.scopeType === 'DEPARTMENT' &&
    !exceptionDraft.value.productionDepartmentId
  ) {
    ElMessage.warning('范围选「部门」时必须选择具体部门')
    return
  }
  if (exceptionDraft.value.scopeType === 'FACTORY') {
    exceptionDraft.value.productionDepartmentId = undefined
  }
  const req: WorkCalendarExceptionRequest = {
    scopeType: exceptionDraft.value.scopeType,
    factoryId: exceptionDraft.value.factoryId,
    productionDepartmentId: exceptionDraft.value.productionDepartmentId,
    workDate: exceptionDraft.value.workDate,
    workState: exceptionDraft.value.workState,
    startAt: exceptionDraft.value.startAt || undefined,
    endAt: exceptionDraft.value.endAt || undefined,
    allowLowerOverride,
    isAuthorizedOverride,
    sourceSystem: exceptionDraft.value.sourceSystem,
    sourceReference: exceptionDraft.value.sourceReference || undefined,
    // 例外天然只管 workDate 这一天，生效起始日与 workDate 同值；结束日不传=长期
    // （后端 Service.cs:203 亦有 eff = EffectiveFrom == default ? workDate 兜底）
    effectiveFrom: exceptionDraft.value.workDate
  }
  const id = await store.createException(req)
  if (id != null) {
    ElMessage.success(`已创建日期例外行 id=${id}`)
    exceptionDialogOpen.value = false
  }
}

/* ===== 批量到期换版弹窗 ===== */
const closeOpenDraft = ref<{
  scopeType: ProductionWorkCalendarScope
  factoryId: number
  productionDepartmentId: number | undefined
  newEffectiveFrom: string
}>({
  scopeType: 'FACTORY',
  factoryId: 2,
  productionDepartmentId: undefined,
  newEffectiveFrom: shiftLocal(30)
})

async function submitCloseOpen(): Promise<void> {
  if (closeOpenDraft.value.scopeType === 'FACTORY') {
    closeOpenDraft.value.productionDepartmentId = undefined
  }
  const req: CalendarCloseOpenRequest = {
    scopeType: closeOpenDraft.value.scopeType,
    factoryId: closeOpenDraft.value.factoryId,
    productionDepartmentId: closeOpenDraft.value.productionDepartmentId,
    newEffectiveFrom: closeOpenDraft.value.newEffectiveFrom
  }
  const n = await store.closeOpen(req)
  if (n != null) {
    ElMessage.success(`已到期停用 ${n} 行（effectiveTo ← ${req.newEffectiveFrom} - 1）`)
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

/* ===== 行级 close/delete ===== */
async function onCloseRow(row: ProductionWorkCalendarDto): Promise<void> {
  if (!canEdit.value) return
  try {
    const { value } = await ElMessageBox.prompt(
      `到期停用后会保留审计。到期日早于该行生效起始日将被拒。`,
      `单行到期停用（id=${row.id}）`,
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
    ElMessage.warning('行已生效，请走"到期停用"通道（保留审计）')
    return
  }
  try {
    await ElMessageBox.confirm(
      `物理删除未生效行 id=${row.id}（仅纠错通道，无审计保留）。确认？`,
      '物理删除',
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

/* ===== 格式化辅助 ===== */
function fmtWindow(row: ProductionWorkCalendarDto): string {
  if (!row.startAt && !row.endAt) return '全天'
  return `${row.startAt ?? '??'} ~ ${row.endAt ?? '??'}`
}
function fmtWeekdayOrDate(row: ProductionWorkCalendarDto): string {
  if (row.workDate) return row.workDate.slice(0, 10)
  if (row.weekdayIso != null) {
    const meta = WEEKDAY_ISO_META[row.weekdayIso]
    return meta ? `周${meta.short}（周基准）` : `weekdayIso=${row.weekdayIso}`
  }
  return '—'
}
onMounted(async () => {
  if (!canView.value) {
    ElMessage.warning('无 PWC 查看权限（需 aps.resource_calendar.view）')
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
            :label="`${f.code}（${f.name}）`"
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
            :label="d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')"
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
        <ElButton type="primary" :disabled="!canWrite" @click="weeklyDialogOpen = true">
          工作周设置
        </ElButton>
        <ElButton type="primary" :disabled="!canWrite" @click="exceptionDialogOpen = true">
          日期例外
        </ElButton>
        <ElButton :disabled="!canWrite" @click="closeOpenDialogOpen = true">
          批量到期换版
        </ElButton>
      </div>
    </ElCard>

    <!-- ===== 主表 ===== -->
    <ElCard class="pwc-panel">
      <div v-if="!list.length" class="pwc-empty">
        <ElEmpty description="无生产工作日历数据" />
      </div>
      <ElTable v-else :data="list" :loading="loading" border stripe size="small">
        <ElTableColumn label="范围 / 厂 / 部门" min-width="220">
          <template #default="{ row }">
            <ElTag :type="PWC_SCOPE_META[row.scopeType].tagType" size="small">
              {{ PWC_SCOPE_META[row.scopeType].label }}
            </ElTag>
            <span class="pwc-cell-mono">{{ row.factoryCode ?? row.factoryId }}</span>
            <span v-if="row.productionDepartmentId" class="pwc-cell-sub">
              {{ row.departmentName ?? '—' }}（{{ row.productionDepartmentId }}）
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
        <ElTableColumn label="状态" width="100">
          <template #default="{ row }">
            <ElTag :type="PWC_WORK_STATE_META[row.workState].tagType" size="small">
              {{ PWC_WORK_STATE_META[row.workState].label }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="加班授权" width="180">
          <template #default="{ row }">
            <ElTag v-if="row.allowLowerOverride" type="warning" size="small" effect="plain">
              允许下级加班
            </ElTag>
            <ElTag v-if="row.isAuthorizedOverride" type="warning" size="small" effect="plain">
              本行是授权加班
            </ElTag>
            <span v-if="!row.allowLowerOverride && !row.isAuthorizedOverride" class="pwc-cell-sub"
              >—</span
            >
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
        <ElTableColumn label="有效期" min-width="180">
          <template #default="{ row }">
            <div class="pwc-cell-mono">
              {{ row.effectiveFrom.slice(0, 10) }}
              <span class="pwc-cell-sub">→</span>
              {{ row.effectiveTo ? row.effectiveTo.slice(0, 10) : '长期' }}
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="220" fixed="right">
          <template #default="{ row }">
            <ElButton
              size="small"
              :disabled="!canEdit || !!row.effectiveTo || !row.isActive"
              @click="onCloseRow(row)"
            >
              到期停用
            </ElButton>
            <ElButton
              size="small"
              type="danger"
              :disabled="!canRemove || row.effectiveFrom.slice(0, 10) <= todayLocal()"
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
              :label="`${f.code}（${f.name}）`"
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
              :label="
                d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')
              "
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
              <ElCheckbox v-model="e.allowLowerOverride" :disabled="e.workState !== 'OFF'">
                允许下级加班
              </ElCheckbox>
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
              :label="`${f.code}（${f.name}）`"
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
              :label="
                d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')
              "
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
        <ElFormItem v-if="exceptionDraft.workState === 'OFF'" label="允许下级加班">
          <ElSwitch v-model="exceptionDraft.allowLowerOverride" />
          <span class="pwc-hint-text">本行虽不工作，但下级可凭授权加班覆盖</span>
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.workState === 'WORK'" label="本行是授权加班">
          <ElSwitch v-model="exceptionDraft.isAuthorizedOverride" />
          <span class="pwc-hint-text">覆盖上级"不工作"的授权加班，需填加班依据</span>
        </ElFormItem>
        <ElFormItem v-if="exceptionDraft.isAuthorizedOverride" label="加班依据（OA 加班单号等）">
          <ElInput v-model="exceptionDraft.sourceReference" placeholder="OA 加班单号（必填）" />
          <span class="pwc-hint-text">授权加班时必填（缺依据 → 后端 400）</span>
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
              :label="`${f.code}（${f.name}）`"
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
              :label="
                d.code + (d.holon ? `（${d.holon}）` : d.stageCode ? `（${d.stageCode}）` : '')
              "
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
