<!--
  APS V1 4号位 — TaskSplitRuleConfig / Batch Policy 批量策略治理独立维护页

  @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig维护接口契约_v1.0_20261007.md
  @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1.1_20261009.md
  @see lps/LPS.APS.Web/Controllers/TaskSplitRuleConfigController.cs
  @see lps/LPS.APS.Application/Services/TaskSplitRuleConfigGovernanceService.cs
  @see lps/LPS.APS.Application/Models/TaskSplitRuleConfigProjector.cs（两级承接：发布时投影进 FrozenStrategySnapshot.BatchPolicy）

  依据：
    - 红线 #5：接口即契约，本路由不嵌入 Rules.vue，独立维护页（避免混入规则参数主表）
    - 红线 #6：仅 DML（POST/PUT/DELETE/GET），无 DDL（列扩展由 2号位 owner）
    - 兼容字段（ResourceGroupId / MOQ / EOQ）不暴露：不进请求体、不进 DTO、不投快照
    - **冻结禁列（13 v1.8:97 / 12 v2.2:102）不展示、不维护、不传**：
      MaxOptimizationSplitCount / MaxBatchCandidates / BottleneckSplitStrategy / NonBottleneckStrategy
      —— 后端 v5.1.10 起已不再 SELECT/接收（TaskSplitRuleConfigGovernanceService.cs:36-40），2026-10-22 前端同步移除
    - v1.1 增量（10-09 P0-01）：HTTP 零变化；403「业务范围越界（Department）」友好提示 + 空态区分「无访问权」vs「真无数据」（@see errorEmptyDesc / scopeViolationHint）
    - 承接方式：主题表 CRUD 即最终态；发布时仍投影进 FrozenStrategySnapshot.BatchPolicy 块（供版本 diff），
      但**消费端已改为表直读**（PeggingOrchestrator.cs:920-954）⇒ 改动下次装载即生效，不需要重发策略包版本

  业务粒度：
    - 业务键：MaterialId + ProductionDepartmentId —— **部门必填**（后端 v5.1.10 收口③ 起写路径拒绝 NULL 部门，
      TaskSplitRuleConfigGovernanceService.cs:400）；NULL 部门仅历史数据兼容，编辑历史行须补选部门
    - 必填布尔：AllowSplit / AllowMerge（bool?，后端 422 强校验）
    - **PreferredBatchQty 必填且 >0，并须落在 Min/Max 区间内**（同文件 :427-446），前端需前置拦截
    - **Min/Max/Preferred 是经济软目标**（冻结 13 v1.8:31 / 01 B-011）：超出经济区间的可生产批量
      不是硬不可行；页面不得用「硬」措辞，也不得让用户以为超区间排不出来

  页面分区：
    - 顶部筛选（物料编码 + 生产部门 + 状态）
    - 主表格 + 行操作（编辑 / 停用 / 启用）
    - Drawer 复用于新增 + 编辑（含必填校验 + 必填布尔提示）
    - 红线 ElAlert：#5 接口即契约 / #6 仅 DML / 兼容字段与冻结禁列不暴露
-->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { storeToRefs } from 'pinia'
import { formatLocalDate, formatLocalDateTime } from '@/utils/datetime'
import {
  ElAlert,
  ElAutocomplete,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInputNumber,
  ElOption,
  ElPagination,
  ElSelect,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'
import {
  materialLookupApi,
  departmentLookupApi,
  type DepartmentLookupItem,
  type MaterialLookupItem,
  type SaveTaskSplitRuleConfigRequest,
  type TaskSplitRuleConfigDto,
  type TaskSplitRuleConfigListQuery
} from '@/api/aps-v1'
import { useTaskSplitRuleConfigStore } from '@/store/modules/aps/taskSplitRuleConfig'

const store = useTaskSplitRuleConfigStore()
const { list, total, loading, saving, error, canView, canEdit, page, pageSize } = storeToRefs(store)

/* ===== 筛选本地 state（变更后点 [筛选] 才生效，避免每次下拉都重查） =====
 * 物料 ID 改用 ElAutocomplete（与 Drawer 新增 / 编辑同源），用户输编码片段 → 自动补全 → 选中后取 masterId
 */
const filterMaterialKey = ref<number | null>(null)
const filterMaterialLabel = ref<string>('')
/** 生产部门筛参（后端 List 支持 productionDepartmentId；83 万行量级下按部门收窄是刚需） */
const filterDepartmentId = ref<number | undefined>(undefined)
const filterIsActive = ref<boolean | undefined>(undefined)

/** 筛选物料下拉数据源（real API；fetchFilterMaterialSuggestions 内懒加载） */
const filterMaterialOptions = ref<MaterialLookupItem[]>([])

/** 筛选物料 fetch-suggestions（Element Plus 协议：返回 AutocompleteData 让 EP 自行处理） */
async function fetchFilterMaterialSuggestions(
  queryString: string,
  callback: (items: MaterialLookupItem[]) => void
): Promise<MaterialLookupItem[]> {
  // 懒加载：首次触发时拉取全量活跃物料
  if (filterMaterialOptions.value.length === 0) {
    try {
      filterMaterialOptions.value = await materialLookupApi.listActiveMaterials()
    } catch {
      filterMaterialOptions.value = []
    }
  }
  const q = queryString.trim()
  let result: MaterialLookupItem[]
  if (!q) {
    result = filterMaterialOptions.value
  } else {
    const lower = q.toLowerCase()
    result = filterMaterialOptions.value.filter(
      (m) =>
        m.code.toLowerCase().includes(lower) ||
        (m.name ?? '').toLowerCase().includes(lower) ||
        (m.spec ?? '').toLowerCase().includes(lower)
    )
  }
  callback(result)
  return result
}

/** 筛选物料选中 → 写 filterMaterialKey（masterId） */
function onFilterMaterialSelect(item: MaterialLookupItem): void {
  filterMaterialKey.value = item.masterId
  filterMaterialLabel.value = item.code
}

/**
 * label 与 key 同步：清空 label（× 按钮 / 手动删空）→ key 同步置 null
 *  - 否则残留的 masterId 会让下一次 [筛选] 仍按旧 materialId 过滤，与「留空 = 全部」语义不一致
 *  - 用户手动输入未选中的文本时 key 维持不变：applyFilter 仍按上次 key 过滤（非本修复范围）
 */
watch(filterMaterialLabel, (next) => {
  if (!next) {
    filterMaterialKey.value = null
  }
})

function applyFilter(): void {
  const q: TaskSplitRuleConfigListQuery = {}
  if (filterMaterialKey.value != null && filterMaterialKey.value > 0) {
    q.materialId = filterMaterialKey.value
  }
  if (filterDepartmentId.value != null && filterDepartmentId.value > 0) {
    q.productionDepartmentId = filterDepartmentId.value
  }
  if (filterIsActive.value !== undefined) {
    q.isActive = filterIsActive.value
  }
  // 筛选条件变 → 强制回到 pageIndex=1（避免筛选无交集时停留在空页造成「空态错觉」）
  store.load(q, { resetPage: true })
}

function resetFilters(): void {
  filterMaterialKey.value = null
  filterMaterialLabel.value = ''
  filterDepartmentId.value = undefined
  filterIsActive.value = undefined
}

/** ElPagination 翻页 */
function onPageChange(nextPage: number): void {
  store.load(undefined, { nextPageIndex: nextPage })
}

/** ElPagination 切每页大小 */
function onSizeChange(nextSize: number): void {
  // 切 size 时回到 pageIndex=1（避免新 pageSize × 旧 pageIndex 越界）
  store.load(undefined, { resetPage: true, nextPageSize: nextSize })
}

async function refresh(): Promise<void> {
  // 刷新保留当前分页位置（用户体验：刷新是「再查一次当前页」，不是「回到第一页」）
  await store.load()
}

/* ===== 状态映射 ===== */
function statusTagOf(isActive: boolean): 'success' | 'info' {
  return isActive ? 'success' : 'info'
}

/* ===== v1.1 友好提示（10-09 P0-01 Department Scope 变更登记）
 *  - 区分「业务范围越界」vs「权限不足 / 数据不存在」等其他错误
 *  - 空态分两支：真无数据 vs 无访问权（前者保留原 [新增批策略] 引导）
 *  - 实现：仅按 store.error 文本匹配（v1.1 后端 message 已包含「业务范围越界」字样）
 */
const SCOPE_VIOLATION_PATTERN = /业务范围越界/

/** ElAlert title 友好提示：命中「业务范围越界」时改文案，保留 traceId 后缀 */
const errorAlertTitle = computed<string>(() => {
  const msg = error.value ?? ''
  if (SCOPE_VIOLATION_PATTERN.test(msg)) {
    // 把后端 message 中的英文细节保留，但 prefix 换成业务中文
    return `无该部门业务范围授权：${msg}（请联系管理员配置 Department Scope，或筛选其它部门）`
  }
  return `操作失败：${msg}`
})

/** ElEmpty description 分支：列表空 + 无 error → 真无数据；列表空 + scope 错误 → 无访问权 */
const emptyDescription = computed<string>(() => {
  if (SCOPE_VIOLATION_PATTERN.test(error.value ?? '')) {
    return '无 Department 业务范围授权（v1.1 Scope 约束）；如需操作请联系管理员配置 Department Scope'
  }
  return '尚无批策略；点击右上 [新增批策略] 创建'
})

/* ===== 主表格 ID → 名称 渲染（业务可读，不显示内部数字键） =====
 *  - 表格列：物料ID 列展示「编码 / 名称」、生产部门ID 列展示「部门编码 / 阶段编码」
 *  - options 缓存在 onMounted 预加载；列表渲染先于 options 就绪时回退到「ID={n}」兜底
 *  - 与冻结文档 v5.1.9 §7.3「业务键」字段对应：materialCode / productionDepartmentCode 是用户视觉唯一标识
 */
const materialOptions = ref<MaterialLookupItem[]>([])
const departmentOptions = ref<DepartmentLookupItem[]>([])

/** 主表格物料渲染：masterId → "code / name"；回退 "物料 ID={n}" */
function materialLabel(masterId: number): string {
  const hit = materialOptions.value.find((m) => m.masterId === masterId)
  if (hit) {
    const namePart = hit.name ? ` / ${hit.name}` : ''
    return `${hit.code}${namePart}`
  }
  return `物料 ID=${masterId}`
}

/** 主表格生产部门渲染：masterId → code（不再拼接 stageCode，保持简洁） */
function departmentLabel(masterId: number): string {
  const hit = departmentOptions.value.find((d) => d.masterId === masterId)
  if (hit) {
    return hit.code
  }
  return `部门 ID=${masterId}`
}

/* ===== Drawer 业务键下拉数据源 ===== */

/** 物料下拉（Drawer 新增 / 编辑 + Toolbar 筛选复用同源）
 *  - 端点：GET /api/governance/lookups/materials?activeOnly=true
 *  - 复用 materialLookupApi.listActiveMaterials
 *  - 失败兜底：空数组（下拉空态提示「暂无物料主数据」）
 */
async function loadMaterialOptions(): Promise<void> {
  if (materialOptions.value.length > 0) return // 已加载则跳过
  try {
    materialOptions.value = await materialLookupApi.listActiveMaterials()
  } catch {
    materialOptions.value = []
  }
}

/** 生产部门下拉（来自正式 lookup 端点 GET /api/governance/lookups/departments）
 *  - 端点 owner：3 号位（SetupRuleController.cs:299-313），直接读源表 ProductionDepartment
 *  - 替换 2026-04 G2 未决时 4 号位 /api/schedule/gantt/{id} 资源池去重的过渡实现
 *  - 失败兜底：空数组（部门为必填项，空数组时无法新增，用户需先补主数据）
 *  - ⚠️ 2026-10-28 撤除「Material 级默认」哨兵：后端 v5.1.10 收口③ 起写路径拒绝 NULL 部门
 *    （TaskSplitRuleConfigGovernanceService.cs:400，实测 422）。保留哨兵会让默认表单必然保存失败。
 */
async function loadDepartmentOptions(): Promise<void> {
  try {
    departmentOptions.value = await departmentLookupApi.listDepartments()
  } catch {
    departmentOptions.value = []
  }
}

/* ===== Drawer 状态 ===== */
const drawerVisible = ref(false)
const drawerMode = ref<'new' | 'edit'>('new')
const editingId = ref<number | null>(null)
/** 编辑行的**原始**部门（null = 历史 Material 级默认行）。此类行必须补选部门才能保存，
 *  故此时解锁部门选择器（其余编辑场景业务键仍锁定）。 */
const editingDeptOriginal = ref<number | null>(null)
const formRef = ref<{
  validate: () => Promise<boolean>
  resetFields: () => void
} | null>(null)

/** Drawer 表单（新增 + 编辑 共用）
 *  - 业务键 Material 用 ElAutocomplete 双轨：materialKey (masterId) + materialCode (显示文本)
 *  - 业务键 Department 用 ElSelect：DEPARTMENT_SENTINEL_DEFAULT (0) = Material 级默认（提交映射回 null）
 *  - 表单层用 `undefined`（Element Plus v-model 不接受 null）
 *  - 提交时统一映射回 `null`（与 DTO 对齐）
 *  - allowSplit / allowMerge 新增默认禁止拆批 / 合批；编辑按原记录回填
 *  - 数值字段统一用 undefined（Element Plus InputNumber 输入空 = undefined）
 *  - 时间字段：undefined = 不限；编辑时回填 ISO
 */
interface DrawerForm {
  materialKey: number | undefined
  materialCode: string | undefined
  productionDepartmentId: number | undefined
  minExecutionBatchQty: number | undefined
  maxExecutionBatchQty: number | undefined
  preferredBatchQty: number | undefined
  allowSplit: boolean | undefined
  allowMerge: boolean | undefined
  effectiveFrom: string | undefined
  effectiveTo: string | undefined
  /** 源系统部门码（部门 lookup 透传；提交时随请求体送出，后端当前不持久化） */
  sourceDeptCode: string | undefined
}

const form = ref<DrawerForm>({
  materialKey: undefined,
  materialCode: undefined,
  productionDepartmentId: undefined,
  minExecutionBatchQty: undefined,
  maxExecutionBatchQty: undefined,
  preferredBatchQty: undefined,
  allowSplit: false,
  allowMerge: false,
  effectiveFrom: undefined,
  effectiveTo: undefined,
  sourceDeptCode: undefined
})

/** 字段级校验规则
 *  - 与后端 TaskSplitRuleConfigGovernanceService.Validate 对齐：
 *    ① MaterialId > 0（业务键必填；ElAutocomplete 选中后 materialKey 才会有值）
 *    ② AllowSplit / AllowMerge 显式提供（不能省略）
 *    ③ Max ≥ Min（Max 不填 = 未设经济最大，放行）
 *    ④ EffectiveTo ≥ EffectiveFrom（任一为空 = 不限，放行）
 *    ⑤ 数值字段 ≥ 0
 *    ⑥ 业务键不重复（MaterialId + ProductionDepartmentId）— 后端 422，前端不做（避免双查）
 */
const formRules = {
  materialKey: [
    {
      validator: (_rule: unknown, value: unknown, callback: (err?: Error) => void) => {
        if (value == null || value === '' || Number(value) <= 0) {
          callback(new Error('请先在物料下拉中选中物料（按编码 / 名称 / 规格搜索）'))
        } else {
          callback()
        }
      },
      trigger: 'change'
    }
  ],
  productionDepartmentId: [
    {
      validator: (_rule: unknown, value: unknown, callback: (err?: Error) => void) => {
        // 后端 v5.1.10 收口③：写路径拒绝 NULL 部门（实测 422）
        if (value == null || value === '' || Number(value) <= 0) {
          callback(new Error('请选择生产部门（新写入规则必须明确部门）'))
        } else {
          callback()
        }
      },
      trigger: 'change'
    }
  ],
  allowSplit: [
    {
      validator: (_rule: unknown, value: unknown, callback: (err?: Error) => void) => {
        if (value !== true && value !== false) {
          callback(new Error('是否允许拆分必须显式提供（true / false）'))
        } else {
          callback()
        }
      },
      trigger: 'change'
    }
  ],
  allowMerge: [
    {
      validator: (_rule: unknown, value: unknown, callback: (err?: Error) => void) => {
        if (value !== true && value !== false) {
          callback(new Error('是否允许合并必须显式提供（true / false）'))
        } else {
          callback()
        }
      },
      trigger: 'change'
    }
  ],
  preferredBatchQty: [
    {
      validator: (_rule: unknown, value: unknown, callback: (err?: Error) => void) => {
        // 后端必填且 >0，并须落在 Min/Max 区间内（TaskSplitRuleConfigGovernanceService.cs:427-446）
        if (value == null || Number(value) <= 0) {
          callback(new Error('经济软目标批量必填，且须大于 0'))
          return
        }
        const min = form.value.minExecutionBatchQty
        const max = form.value.maxExecutionBatchQty
        if (min != null && Number(value) < min) {
          callback(new Error('经济软目标批量不能小于经济最小批量'))
          return
        }
        if (max != null && Number(value) > max) {
          callback(new Error('经济软目标批量不能大于经济最大批量'))
          return
        }
        callback()
      },
      trigger: 'blur'
    }
  ],
  maxExecutionBatchQty: [
    {
      validator: (_rule: unknown, _value: unknown, callback: (err?: Error) => void) => {
        const max = form.value.maxExecutionBatchQty
        const min = form.value.minExecutionBatchQty
        if (max != null && min != null && max < min) {
          callback(new Error('最大执行批量不能小于最小执行批量'))
        } else {
          callback()
        }
      },
      trigger: 'blur'
    }
  ],
  effectiveTo: [
    {
      validator: (_rule: unknown, _value: unknown, callback: (err?: Error) => void) => {
        const from = form.value.effectiveFrom
        const to = form.value.effectiveTo
        if (from && to && new Date(to).getTime() < new Date(from).getTime()) {
          callback(new Error('生效截止不能早于生效起始'))
        } else {
          callback()
        }
      },
      trigger: 'change'
    }
  ]
}

function openCreate(): void {
  drawerMode.value = 'new'
  editingId.value = null
  editingDeptOriginal.value = null
  // 预填过滤栏已选物料 / 部门（用户点 [新增] 时少两步操作）
  form.value = {
    materialKey: filterMaterialKey.value ?? undefined,
    materialCode: filterMaterialLabel.value || undefined,
    productionDepartmentId: filterDepartmentId.value, // 未选则为空 → 必填校验拦截
    minExecutionBatchQty: undefined,
    maxExecutionBatchQty: undefined,
    preferredBatchQty: undefined,
    allowSplit: false,
    allowMerge: false,
    effectiveFrom: undefined,
    effectiveTo: undefined,
    sourceDeptCode: undefined
  }
  // 抽屉打开时确保两个下拉都有数据
  void loadMaterialOptions()
  void loadDepartmentOptions()
  drawerVisible.value = true
}

function openEdit(dto: TaskSplitRuleConfigDto): void {
  drawerMode.value = 'edit'
  editingId.value = dto.id
  // 反查物料编码（materialOptions 已加载则直接取；未加载完则 placeholder + 后置 watch 兜底）
  const foundMaterial = materialOptions.value.find((m) => m.masterId === dto.materialId)
  form.value = {
    materialKey: dto.materialId,
    materialCode: foundMaterial?.code ?? '',
    productionDepartmentId: dto.productionDepartmentId ?? undefined,
    minExecutionBatchQty: dto.minExecutionBatchQty ?? undefined,
    maxExecutionBatchQty: dto.maxExecutionBatchQty ?? undefined,
    preferredBatchQty: dto.preferredBatchQty ?? undefined,
    allowSplit: dto.allowSplit,
    allowMerge: dto.allowMerge,
    effectiveFrom: dto.effectiveFrom ?? undefined,
    effectiveTo: dto.effectiveTo ?? undefined,
    sourceDeptCode: undefined
  }
  // 编辑时反查部门源系统码（options 已就绪则直接写回；未就绪由 sourceDeptCode watch 兜底）
  editingDeptOriginal.value = dto.productionDepartmentId ?? null
  const deptId = form.value.productionDepartmentId
  if (deptId != null) {
    const foundDept = departmentOptions.value.find((d) => d.masterId === deptId)
    form.value.sourceDeptCode = foundDept?.sourceDeptCode ?? undefined
  }
  // 编辑时也确保两个下拉都有数据（options 异步加载）
  void loadMaterialOptions()
  void loadDepartmentOptions()
  drawerVisible.value = true
}

/** 兜底 watch：materialOptions 异步加载完后，若 form.materialKey 已有但 materialCode 空（编辑态 / 预填态），反查填回 */
watch(
  () => materialOptions.value,
  (opts) => {
    if (form.value.materialKey && !form.value.materialCode) {
      const found = opts.find((m) => m.masterId === form.value.materialKey)
      if (found) form.value.materialCode = found.code
    }
  }
)

/** 同步 sourceDeptCode：生产部门下拉变化时反查 lookup 写回表单
 *  - 部门 = null → 清空（未选部门时无源部门码）
 *  - lookup 未加载完成时保持 undefined，等 watch 二次回填
 */
watch(
  () => form.value.productionDepartmentId,
  (id) => {
    if (id == null) {
      form.value.sourceDeptCode = undefined
      return
    }
    const hit = departmentOptions.value.find((d) => d.masterId === id)
    form.value.sourceDeptCode = hit?.sourceDeptCode ?? undefined
  }
)

/** Drawer 内物料 ElAutocomplete 选中回调（脚本命名函数，避开模板 attribute TS 注解解析陷阱） */
function onDrawerMaterialSelect(item: MaterialLookupItem): void {
  form.value.materialKey = item.masterId
  form.value.materialCode = item.code
}

function closeDrawer(): void {
  drawerVisible.value = false
  editingId.value = null
}

/** 提交（新增 / 编辑 共用）—— 表单层 undefined → DTO 层 null 映射 */
async function submitDrawer(): Promise<void> {
  if (!formRef.value) return
  let valid = false
  try {
    valid = await formRef.value.validate()
  } catch {
    return
  }
  if (!valid) return

  const input: SaveTaskSplitRuleConfigRequest = {
    materialId: Number(form.value.materialKey),
    // 部门必填由表单校验前置拦截；此处仅做 undefined → null 的类型映射
    productionDepartmentId: form.value.productionDepartmentId ?? null,
    minExecutionBatchQty: form.value.minExecutionBatchQty ?? null,
    maxExecutionBatchQty: form.value.maxExecutionBatchQty ?? null,
    preferredBatchQty: form.value.preferredBatchQty ?? null,
    // 必须显式提供（后端 422 强校验）；form.value 已是 boolean | undefined
    allowSplit: form.value.allowSplit ?? null,
    allowMerge: form.value.allowMerge ?? null,
    effectiveFrom: form.value.effectiveFrom ?? null,
    effectiveTo: form.value.effectiveTo ?? null,
    sourceDeptCode: form.value.sourceDeptCode ?? null
  }

  // 保存结果只展示业务键（冻结 13 v1.8:11「显示保存结果、适用业务键、时间窗及审计」），不露内部 Id
  const keyLabel = `${materialLabel(input.materialId)} / ${
    input.productionDepartmentId == null
      ? '未指定部门'
      : departmentLabel(input.productionDepartmentId)
  }`

  try {
    if (drawerMode.value === 'new') {
      const result = await store.create(input)
      if (result) {
        ElMessage.success(`已新增批策略：${keyLabel}`)
        closeDrawer()
      }
    } else {
      if (editingId.value == null) return
      const result = await store.update(editingId.value, input)
      if (result) {
        ElMessage.success(`已更新批策略：${keyLabel}`)
        closeDrawer()
      }
    }
  } catch {
    // store 已 handleError；UI 仅展示错误条
  }
}

/** 停用规则（DELETE 软删 IsActive=0） */
async function handleDeactivate(dto: TaskSplitRuleConfigDto): Promise<void> {
  try {
    await ElMessageBox.confirm('是否确认停用？', '停用批策略', {
      confirmButtonText: '确认停用',
      cancelButtonText: '不操作',
      type: 'warning'
    })
  } catch {
    return
  }
  const result = await store.deactivate(dto.id)
  if (result) {
    ElMessage.success('批策略已停用')
  }
}

/** 恢复启用规则（POST activate；IsActive 0→1；后端 422 红线由 store.error 展示） */
async function handleActivate(dto: TaskSplitRuleConfigDto): Promise<void> {
  try {
    await ElMessageBox.confirm('是否确认恢复启用该批策略？', '启用批策略', {
      confirmButtonText: '确认启用',
      cancelButtonText: '不操作',
      type: 'warning'
    })
  } catch {
    return
  }
  const result = await store.activate(dto.id)
  if (result) {
    ElMessage.success('批策略已启用')
  }
}

/* ===== Drawer 副作用：visible → 编辑/新建切换时重置表单 ===== */
watch(drawerVisible, (v) => {
  if (!v) {
    editingId.value = null
  }
})

/* ===== 生命周期 ===== */
onMounted(() => {
  // 主表格 ID → 名称 渲染：与 refresh() 并行预加载，不阻塞首屏
  void loadMaterialOptions()
  void loadDepartmentOptions()
  refresh()
})

onBeforeUnmount(() => {
  store.reset()
})
</script>

<template>
  <div class="tsrc-page">
    <!-- ===== 红线 ElAlert ===== -->
    <ElAlert
      v-if="!canView"
      type="warning"
      :closable="false"
      show-icon
      title="当前账号无批策略查看权限（需 aps.rule.view）"
      class="red-line-alert"
    />

    <!-- ===== 错误条 ===== -->
    <ElAlert
      v-if="error"
      type="error"
      :closable="true"
      show-icon
      :title="errorAlertTitle"
      class="error-alert"
      @close="store.handleError(new Error(''))"
    />

    <!-- ===== 筛选 ===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <span>筛选条件</span>
          <span class="hint-text">点 [筛选] 才生效</span>
        </div>
      </template>
      <div class="filter-row">
        <div class="filter-item">
          <label>物料</label>
          <ElAutocomplete
            v-model="filterMaterialLabel"
            :fetch-suggestions="fetchFilterMaterialSuggestions"
            value-key="code"
            placeholder="留空 = 全部物料（按编码 / 名称 / 规格筛选）"
            style="width: 280px"
            clearable
            :trigger-on-focus="true"
            highlight-first-item
            @select="onFilterMaterialSelect"
          >
            <template #default="{ item }">
              <span class="material-suggestion">
                <span class="material-code">{{ item.code }}</span>
                <span class="material-name">{{ item.name }}</span>
                <span class="material-spec">{{ item.spec }} / {{ item.uom }}</span>
              </span>
            </template>
          </ElAutocomplete>
        </div>
        <div class="filter-item">
          <label>生产部门</label>
          <ElSelect
            v-model="filterDepartmentId"
            placeholder="留空 = 全部部门"
            style="width: 200px"
            filterable
            clearable
          >
            <ElOption
              v-for="d in departmentOptions"
              :key="d.masterId"
              :value="d.masterId"
              :label="d.code"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <label>是否启用</label>
          <ElSelect v-model="filterIsActive" placeholder="全部状态" style="width: 160px" clearable>
            <ElOption label="启用" :value="true" />
            <ElOption label="已停用" :value="false" />
          </ElSelect>
        </div>
        <div class="filter-actions">
          <ElButton @click="resetFilters">重置</ElButton>
          <ElButton type="primary" :loading="loading" @click="applyFilter">筛选</ElButton>
        </div>
      </div>
    </ElCard>

    <!-- ===== 主表格 ===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header panel-header--end">
          <ElButton type="success" plain :disabled="!canEdit" @click="openCreate">
            <Icon icon="vi-mdi:plus" />
            新增批策略
          </ElButton>
        </div>
      </template>

      <ElEmpty v-if="!list.length && !loading" :description="emptyDescription" />

      <ElTable
        v-else
        :data="list"
        :loading="loading"
        border
        stripe
        size="small"
        row-key="id"
        style="width: 100%"
      >
        <ElTableColumn label="物料" min-width="220">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span class="material-cell">
              <span class="material-code">{{ materialLabel(row.materialId) }}</span>
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="生产部门" min-width="180" align="center">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span v-if="row.productionDepartmentId != null" class="dept-cell">
              <span>{{ departmentLabel(row.productionDepartmentId) }}</span>
            </span>
            <ElTag v-else type="warning" size="small">物料级默认</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="经济最小批量（软目标）" width="150" align="right">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span v-if="row.minExecutionBatchQty != null">{{ row.minExecutionBatchQty }}</span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="经济最大批量（软目标）" width="150" align="right">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span v-if="row.maxExecutionBatchQty != null">{{ row.maxExecutionBatchQty }}</span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="经济软目标批量" width="130" align="right">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span v-if="row.preferredBatchQty != null">{{ row.preferredBatchQty }}</span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="是否允许拆批 / 合批" width="160" align="center">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <ElTag :type="row.allowSplit ? 'success' : 'info'" size="small">
              {{ row.allowSplit ? '允许拆批' : '禁止拆批' }}
            </ElTag>
            <ElTag
              :type="row.allowMerge ? 'success' : 'info'"
              size="small"
              style="margin-left: 4px"
            >
              {{ row.allowMerge ? '允许合批' : '禁止合批' }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="生效起止" min-width="180">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span class="muted small">
              {{ row.effectiveFrom ? formatLocalDate(row.effectiveFrom) : '不限' }}
              ~
              {{ row.effectiveTo ? formatLocalDate(row.effectiveTo) : '永久' }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="是否启用" width="80" align="center">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <ElTag :type="statusTagOf(row.isActive)" size="small">
              {{ row.isActive ? '启用' : '停用' }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="更新时间" width="140" align="center">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <span class="muted small">{{ formatLocalDateTime(row.updatedAt) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="220" fixed="right" align="center">
          <template #default="{ row }: { row: TaskSplitRuleConfigDto }">
            <ElButton size="small" plain :disabled="!canEdit" @click="openEdit(row)">
              <Icon icon="vi-mdi:pencil" />
              编辑
            </ElButton>
            <ElButton
              v-if="row.isActive"
              size="small"
              type="danger"
              plain
              :disabled="!canEdit"
              @click="handleDeactivate(row)"
            >
              <Icon icon="vi-mdi:cancel" />
              停用
            </ElButton>
            <ElButton
              v-else
              size="small"
              type="success"
              plain
              :disabled="!canEdit"
              @click="handleActivate(row)"
            >
              <Icon icon="vi-mdi:play" />
              启用
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>

      <!-- 分页（10-08 列表分页改造落地） -->
      <ElPagination
        :current-page="page"
        :page-size="pageSize"
        :total="total"
        :page-sizes="[10, 20, 50, 100, 200]"
        layout="total, sizes, prev, pager, next, jumper"
        background
        class="tsrc-pagination"
        @current-change="onPageChange"
        @size-change="onSizeChange"
      />
    </ElCard>

    <!-- ===== 新增 / 编辑 Drawer ===== -->
    <ElDrawer
      v-model="drawerVisible"
      :title="drawerMode === 'new' ? '新增批策略' : '编辑批策略'"
      size="640px"
      direction="rtl"
      :close-on-click-modal="false"
      @close="closeDrawer"
    >
      <div class="tsrc-drawer">
        <ElForm
          ref="formRef"
          :model="form"
          :rules="formRules"
          label-width="120px"
          label-position="right"
          class="tsrc-form"
        >
          <div class="form-section">
            <div class="form-section-title">业务键（必填）</div>
            <ElFormItem label="物料" prop="materialKey" required>
              <ElAutocomplete
                v-model="form.materialCode"
                :fetch-suggestions="fetchFilterMaterialSuggestions"
                value-key="code"
                placeholder="按编码 / 名称 / 规格搜索物料"
                style="width: 100%"
                clearable
                :trigger-on-focus="true"
                highlight-first-item
                :disabled="drawerMode === 'edit'"
                @select="onDrawerMaterialSelect"
              >
                <template #default="{ item }">
                  <span class="material-suggestion">
                    <span class="material-code">{{ item.code }}</span>
                    <span class="material-name">{{ item.name }}</span>
                    <span class="material-spec">{{ item.spec }} / {{ item.uom }}</span>
                  </span>
                </template>
              </ElAutocomplete>
            </ElFormItem>
            <ElFormItem label="生产部门" prop="productionDepartmentId" required>
              <ElSelect
                v-model="form.productionDepartmentId"
                placeholder="请选择生产部门（必填）"
                style="width: 100%"
                filterable
                clearable
                :disabled="drawerMode === 'edit' && editingDeptOriginal !== null"
              >
                <ElOption
                  v-for="d in departmentOptions"
                  :key="d.masterId"
                  :value="d.masterId"
                  :label="d.code"
                />
              </ElSelect>
              <div v-if="drawerMode === 'edit' && editingDeptOriginal === null" class="field-hint">
                该行原为「Material
                级默认」（无部门）。后端已不接受无部门规则，请补选生产部门后再保存。
              </div>
            </ElFormItem>
          </div>

          <div class="form-section">
            <div class="form-section-title">经济批量区间（软目标；可空 = 未设）</div>
            <ElAlert
              type="info"
              :closable="false"
              show-icon
              class="soft-hint"
              title="最小 / 最大批量是经济软目标，不是硬约束：超出区间的可生产批量仍然可排，排程会按软目标择优。"
            />
            <ElFormItem label="经济最小批量（软目标）" prop="minExecutionBatchQty">
              <ElInputNumber
                v-model="form.minExecutionBatchQty"
                :min="0"
                :precision="4"
                placeholder="留空 = 未设"
                style="width: 100%"
              />
            </ElFormItem>
            <ElFormItem label="经济最大批量（软目标）" prop="maxExecutionBatchQty">
              <ElInputNumber
                v-model="form.maxExecutionBatchQty"
                :min="0"
                :precision="4"
                placeholder="留空 = 未设"
                style="width: 100%"
              />
            </ElFormItem>
            <ElFormItem label="经济软目标批量" prop="preferredBatchQty" required>
              <ElInputNumber
                v-model="form.preferredBatchQty"
                :min="0"
                :precision="4"
                placeholder="必填，且须落在经济区间内"
                style="width: 100%"
              />
            </ElFormItem>
          </div>

          <div class="form-section">
            <div class="form-section-title">是否允许拆批 / 合批（必显式提供）</div>
            <ElFormItem label="是否允许拆批" prop="allowSplit" required>
              <ElSwitch
                v-model="form.allowSplit"
                active-text="允许"
                inactive-text="禁止"
                inline-prompt
              />
            </ElFormItem>
            <ElFormItem label="是否允许合批" prop="allowMerge" required>
              <ElSwitch
                v-model="form.allowMerge"
                active-text="允许"
                inactive-text="禁止"
                inline-prompt
              />
            </ElFormItem>
          </div>

          <div class="form-section">
            <div class="form-section-title">生效起止（可空 = 不限）</div>
            <ElFormItem label="生效起始" prop="effectiveFrom">
              <ElDatePicker
                v-model="form.effectiveFrom"
                type="datetime"
                value-format="YYYY-MM-DDTHH:mm:ss[Z]"
                placeholder="留空 = 不限"
                style="width: 100%"
                clearable
              />
            </ElFormItem>
            <ElFormItem label="生效截止" prop="effectiveTo">
              <ElDatePicker
                v-model="form.effectiveTo"
                type="datetime"
                value-format="YYYY-MM-DDTHH:mm:ss[Z]"
                placeholder="留空 = 永久"
                style="width: 100%"
                clearable
              />
            </ElFormItem>
          </div>
        </ElForm>

        <div v-if="drawerMode === 'edit' && editingId !== null" class="audit-block">
          <ElDescriptions :column="1" size="small" title="审计字段（只读）" border>
            <ElDescriptionsItem label="创建时间">
              <span class="muted small">
                {{ formatLocalDateTime(list.find((x) => x.id === editingId)?.createdAt) }}
              </span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="最后更新">
              <span class="muted small">
                {{ formatLocalDateTime(list.find((x) => x.id === editingId)?.updatedAt) }}
              </span>
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

    <ElTooltip v-if="saving" content="正在保存修改" placement="top">
      <div class="tsrc-saving-tip">保存中…</div>
    </ElTooltip>
  </div>
</template>

<style lang="less" scoped>
.tsrc-page {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.error-alert,
.red-line-alert {
  margin-bottom: 0;
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

  /* 表格卡头部只剩一个按钮时用（右对齐到卡片右上角） */
  &--end {
    justify-content: flex-end;
  }

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.muted {
  color: #94a3b8;
}

.small {
  font-size: 11px;
}

.field-hint {
  margin-top: 4px;
  font-size: 12px;
  line-height: 1.5;
  color: #e6a23c;
}

.material-cell,
.dept-cell {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
}

.material-cell .material-code,
.dept-cell > span:first-child {
  font-weight: 500;
  color: #1e293b;
}

.strategy-tag {
  font-size: 12px;
  color: #1e293b;
}

.filter-row {
  display: flex;
  align-items: flex-end;
  flex-wrap: wrap;
  gap: 14px;
}

.filter-item {
  display: flex;
  align-items: center;
  flex-direction: column;
  gap: 4px;

  label {
    font-size: 12px;
    font-weight: 600;
    color: #64748b;
  }
}

.filter-actions {
  display: flex;
  margin-left: auto;
  gap: 8px;
}

.tsrc-drawer {
  display: flex;
  padding: 0 4px;
  flex-direction: column;
  gap: 12px;
}

.tsrc-form {
  :deep(.el-form-item) {
    margin-bottom: 14px;
  }
}

.form-section {
  padding: 12px 14px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  margin-bottom: 12px;
}

.form-section-title {
  margin-bottom: 12px;
  font-size: 13px;
  font-weight: 600;
  color: #475569;
  border-left: 3px solid var(--el-color-primary);
  padding-left: 8px;
}

/* 经济批量软目标说明（不是硬约束） */
.soft-hint {
  margin-bottom: 12px;
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

.tsrc-saving-tip {
  position: fixed;
  bottom: 24px;
  right: 32px;
  z-index: 99;
  padding: 6px 14px;
  font-size: 13px;
  color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
  border-radius: 16px;
  box-shadow: 0 2px 8px rgb(0 0 0 / 8%);
}
</style>
