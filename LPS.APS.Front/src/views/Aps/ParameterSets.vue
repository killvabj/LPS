<script setup lang="ts">
/**
 * APS V1 4号位 — 参数集（ParameterSet）维护
 *
 * ⚠️ 2026-10-22 新增：参数集从「规则与参数维护」页里**独立出来**。
 *
 * 冻结模型是三条**平行**链（DDL 08:9025 / 08:9042 / 08:9064 / 08:9081）：
 *
 *   RuleSet ──1:N──> RuleSetVersion            （需求优先级段位 + 换型规则子块）
 *   ParameterSet ──1:N──> ParameterSetVersion  （本页：5 主题 JSON）
 *   StrategyProfile ──1:N──> StrategyProfileVersion
 *
 * 两族**只在 StrategyProfileVersion 上会合**（08:9132-9133）。
 * 参数集版本里没有规则集外键，规则集版本里也没有参数集外键 —— 独立维护，各自发布。
 *
 * 内容 = 5 个主题 JSON（后端以**字符串**返回，前端反序列化成对象编辑、PUT 时序列化回去）：
 * lock / supply / procurement / solverStrategy / candidateGuardrail
 *
 * 权限码（⚠️ 与规则集**独立**，`aps.rule.publish` 不覆盖参数集发布）：
 *  aps.parameter.view / aps.parameter.edit / aps.parameter.publish
 */

import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useParameterSetsStore } from '@/store/modules/aps/parameterSets'
import { formatLocalDateTime, formatUtcDateTime } from '@/utils/datetime'
import {
  RULE_STATUS_LABELS,
  RULE_STATUS_TAG,
  NO_VERSION,
  NO_VERSION_LABEL,
  RULE_STATUSES_UI,
  BLOCK_KEYS,
  type BlockKey,
  type RuleStatus,
  type ParameterSetSummaryDto,
  type ParameterSetVersionHistoryDto
} from '@/api/aps-v1'
import type { ParameterRow, JsonValue } from '@/api/aps-v1/draftBuffer'
import {
  listAbsentFields,
  listBlockFields,
  findBlockField,
  isInertBlockField,
  isFullyInertBlock,
  inertAnnotationOf,
  type BlockFieldDef,
  type InertAnnotation
} from '@/api/aps-v1/parameterBlockFields'
import { lookupParameterEnum } from '@/api/aps-v1/parameterSetEnums'
import { translateValidationError } from '@/api/aps-v1/validationMessages'
import WarehouseRefEditor from './components/WarehouseRefEditor.vue'
import PlanningYieldEditor from './components/PlanningYieldEditor.vue'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElDialog,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElRadioButton,
  ElRadioGroup,
  ElSelect,
  ElSwitch,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTabs,
  ElTabPane,
  ElTooltip
} from 'element-plus'

const paramStore = useParameterSetsStore()
const {
  filteredParameterSets,
  statusCounts,
  noVersionCount,
  inactiveCount,
  currentDetail,
  currentDiff,
  paramDiffRows,
  paramDiffPair,
  lastValidation,
  statusFilter,
  showInactive,
  loading,
  listLoading,
  actionRunning,
  validating,
  error,
  canPublishAction,
  canWrite,
  actions,
  dirty,
  rowsGrouped,
  anchorHintMessage,
  anchorResourceCode,
  solverStrategyHasContent,
  buffer,
  warehouseDictError,
  currentUnknownWarehouseCodes,
  forkSourceUnknownCodes
} = storeToRefs(paramStore)

const detailNN = computed(() => currentDetail.value!)

/* ===== 字典 ===== */

/** 5 主题 JSON 块中文标签（沿用「规则与参数维护」页既有措辞） */
const BLOCK_LABELS: Record<BlockKey, string> = {
  lock: '锁定策略',
  supply: '供应参数',
  procurement: '采购参数',
  solverStrategy: '求解器策略',
  candidateGuardrail: '候选版本守门'
}

/** 只保留真正发生变化的字段 —— 后端 `fieldDiffs` 是全字段平铺 */
const changedFields = computed(() =>
  (currentDiff.value?.fieldDiffs ?? []).filter((f) => f.isChanged)
)

/**
 * 后端 diff 里的 5 个**块级**字段（内容 JSON）——参数内容由前端逐参数比，这几项不再展示
 *
 * 后端 `CompareParameterSetVersionsAsync` 的字段名是内容快照块名（Lock/Supply/…），
 * 与前端 5 块键名（lock/supply/…）大小写不同 ⇒ 这里按后端字段名剔除。
 */
const BACKEND_BLOCK_FIELD_NAMES = [
  'Lock',
  'Supply',
  'Procurement',
  'SolverStrategy',
  'CandidateGuardrail'
]

/** 版本元信息差异（版本码 / 状态 / 生效窗 / 发布时间 / 发布人 / 备注） */
const metaDiffFields = computed(() =>
  changedFields.value.filter((f) => !BACKEND_BLOCK_FIELD_NAMES.includes(f.fieldName))
)

/** 参数差异的变化类型中文 */
const DIFF_KIND_LABELS: Record<string, string> = {
  added: '新增',
  removed: '删除',
  changed: '修改'
}

/** 参数差异的变化类型标签色 */
const DIFF_KIND_TAG: Record<string, 'success' | 'danger' | 'warning'> = {
  added: 'success',
  removed: 'danger',
  changed: 'warning'
}

/** 对比对象说明（源 / 目标各是什么状态） */
const diffPairHint = computed(() => {
  const pair = paramDiffPair.value
  if (!pair) return ''
  const name = (s: string) => RULE_STATUS_LABELS[s as keyof typeof RULE_STATUS_LABELS] ?? s
  return `源：${pair.sourceCode}（${name(pair.sourceStatus)}） → 目标：${pair.targetCode}（${name(pair.targetStatus)}）`
})

/* ===== Tabs ===== */
type TabKey = 'edit' | 'diff' | 'history'
const activeTab = ref<TabKey>('edit')

/** 有仓库引用专属编辑器的块（通用表里这四处落点被剔除） */
const WAREHOUSE_EDITOR_BLOCKS: BlockKey[] = ['supply', 'procurement']

function hasWarehouseEditor(blockKey: BlockKey): boolean {
  return WAREHOUSE_EDITOR_BLOCKS.includes(blockKey)
}

/** 有专属编辑器的空块：给一句「能加什么」的示例（取字段目录前两项，不硬编码文案） */
function addFieldExamples(blockKey: BlockKey): string {
  return listBlockFields(blockKey)
    .slice(0, 2)
    .map((f) => f.label)
    .join('、')
}

/* ===== 选中 ===== */
const noVersionPicked = ref<ParameterSetSummaryDto | null>(null)

async function selectParameterSet(ps: ParameterSetSummaryDto): Promise<void> {
  noVersionPicked.value = null
  paramStore.reset()
  activeTab.value = 'edit'
  if (!ps.hasVersion) {
    noVersionPicked.value = ps
    return
  }
  await paramStore.loadDetail(ps.parameterSetId)
}

function setFilter(filter: RuleStatus | 'ALL'): void {
  paramStore.setStatusFilter(filter)
}

/* ===== [+ 新建参数集] Dialog（主表；POST /parameter-set） ===== */
const createParameterSetDialog = ref(false)
const createParameterSetForm = ref({
  parameterSetCode: '',
  parameterSetName: '',
  description: ''
})

function openCreateParameterSetDialog(): void {
  createParameterSetForm.value = { parameterSetCode: '', parameterSetName: '', description: '' }
  createParameterSetDialog.value = true
}

async function onCreateParameterSet(): Promise<void> {
  const id = await paramStore.createParameterSet({
    parameterSetCode: createParameterSetForm.value.parameterSetCode,
    parameterSetName: createParameterSetForm.value.parameterSetName,
    description: createParameterSetForm.value.description
  })
  if (id == null) return
  createParameterSetDialog.value = false
  ElMessage.success('参数集已创建，并自动建了一条空白草稿')
  const created = paramStore.parameterSets.find((p) => p.parameterSetId === id)
  if (created) await selectParameterSet(created)
}

/* ===== [编辑参数集] Dialog（主表；PUT /parameter-set/{id}） ===== */
const editParameterSetDialog = ref(false)
const editParameterSetForm = ref({
  parameterSetId: 0,
  parameterSetCode: '',
  parameterSetName: '',
  description: '',
  isActive: true
})

function openEditParameterSetDialog(p: ParameterSetSummaryDto): void {
  editParameterSetForm.value = {
    parameterSetId: p.parameterSetId,
    parameterSetCode: p.parameterSetCode,
    parameterSetName: p.parameterSetName,
    description: p.description ?? '',
    isActive: p.isActive !== false
  }
  editParameterSetDialog.value = true
}

async function onUpdateParameterSet(): Promise<void> {
  const ok = await paramStore.updateParameterSet({
    parameterSetId: editParameterSetForm.value.parameterSetId,
    parameterSetCode: editParameterSetForm.value.parameterSetCode,
    parameterSetName: editParameterSetForm.value.parameterSetName,
    description: editParameterSetForm.value.description,
    isActive: editParameterSetForm.value.isActive
  })
  if (!ok) return
  editParameterSetDialog.value = false
  ElMessage.success('参数集已保存')
}

/* ===== 参数行编辑 ===== */

/**
 * 数字输入框**不设 precision**（ElInputNumber 在 precision 缺省时按原值展示/回写）。
 *  - 设成 0 会把 `0.1` 显示成 `0` 并在失焦时写回 0 —— 真实数据被破坏
 *  - 只有 PERCENT 单独给 4 位小数 + 0.01 步长（百分比惯例）
 */
function onCellEdit(row: ParameterRow, newValue: JsonValue): void {
  paramStore.onCellEdit(row, newValue)
}

/** 枚举行只读时显示中文名（JSON 里存的是数字序号，直接展示用户看不懂） */
function enumLabelOf(row: ParameterRow): string {
  const hit = row.options?.find((o) => o.value === row.value)
  return hit ? hit.label : String(row.value ?? '—')
}

/** 用户看得懂的类型名（原样显示 BOOLEAN / NUMBER 对用户没有意义） */
const TYPE_LABELS: Record<ParameterRow['type'], string> = {
  BOOLEAN: '是 / 否',
  NUMBER: '数字',
  STRING: '文本',
  PERCENT: '百分比',
  DURATION: '时长',
  TEXT_LIST: '文本清单'
}

/** 只读态的值展示（数组不再直出 JSON 串） */
function displayValueOf(row: ParameterRow): string {
  if (row.options) return enumLabelOf(row)
  const v = row.value
  if (v === null || v === undefined) return '—'
  if (Array.isArray(v)) {
    return v.every((x) => typeof x === 'string') ? v.join('、') : JSON.stringify(v)
  }
  return String(v)
}

/** 文本清单 → 编辑框文本（每行一项） */
function listTextOf(row: ParameterRow): string {
  return Array.isArray(row.value) ? row.value.map(String).join('\n') : ''
}

/** 文本清单编辑：按行拆（空行丢弃）；整行删空 ⇒ 写成空数组（等价「一项都不限」） */
function onTextListEdit(row: ParameterRow, text: unknown): void {
  const items = String(text ?? '')
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
  paramStore.onCellEdit(row, items)
}

/** 锚点资源编码（自由文本；3号位 10-25 定调方案 (a)） */
function onAnchorCodeChange(value: string): void {
  paramStore.setAnchorResourceCode(value)
}

/**
 * 删除一个参数（把键从块 JSON 里拿掉）。
 *
 * 语义要说清楚：删掉 = 该参数**回到系统默认值**（不是「这条配置作废」），
 * 所以确认框里明写。数组行（editable=false）不给入口。
 */
async function onRemoveField(row: ParameterRow): Promise<void> {
  const ok = await ElMessageBox.confirm(
    `将删除参数「${row.label ?? row.path.join('.')}」。\n` +
      '删除后这一项回到系统默认值，不再出现在参数表里。\n\n' +
      '删除只改当前草稿，保存草稿后才生效。',
    '删除参数',
    { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' }
  ).catch(() => false)
  if (!ok) return
  if (paramStore.removeBlockField(row.block, row.path)) {
    ElMessage.success('已删除，记得保存草稿')
  }
}

/* ===== [新增字段] Dialog（空块加第一个参数）=====
 * 3号位 2026-10-25 §C-AN-1/§C-AN-3 拍板：
 *   空块上开放「新增字段」；用户显式新增一个字段并填入真实值后，该字段才进入块 JSON；
 *   **不注入任何默认值** ⇒ 下面所有取值控件一律不预填。
 */

const addFieldDialog = ref(false)
const addFieldBlock = ref<BlockKey>('lock')
const addFieldPath = ref('')
/** 未选择 = undefined（不是 false / 0）——布尔必须显式选「是/否」，数字/枚举必须显式填 */
const addFieldBool = ref<boolean | undefined>(undefined)
const addFieldNumber = ref<number | undefined>(undefined)
const addFieldEnum = ref<number | undefined>(undefined)
const addFieldText = ref('')
const addFieldList = ref('')

/** 该块里**尚未配置**的字段（已配过的由通用参数表直接编辑） */
const addFieldCandidates = computed<BlockFieldDef[]>(() => {
  const blockObj = buffer.value.workingBlocks?.[addFieldBlock.value]
  return listAbsentFields(addFieldBlock.value, blockObj)
})

const addFieldDef = computed<BlockFieldDef | undefined>(() =>
  findBlockField(addFieldBlock.value, addFieldPath.value)
)

/**
 * 选中字段的「暂未生效」标注（弹窗里补一句大白话，免得「暂未生效」四个字看不懂）。
 * 逐字段文案在 `parameterBlockFields.ts` 的 `INERT_ANNOTATIONS`：
 * 「待接入 / 待开放 / 受控废弃 / V1 固定」四类不能共用一句话 —— 共用就是说假话。
 */
const addFieldInertAnnotation = computed<InertAnnotation | undefined>(() =>
  addFieldDef.value && isInertBlockField(addFieldBlock.value, addFieldDef.value.path)
    ? inertAnnotationOf(addFieldBlock.value, addFieldDef.value.path)
    : undefined
)

/** 行上「暂未生效」标注（徽标文案 + 悬停说明） */
const inertAnnotationOfRow = (row: { block: string; path: string[] }): InertAnnotation =>
  inertAnnotationOf(row.block as BlockKey, row.path.join('.'))

/** 枚举项来自 parameterSetEnums（唯一真源；值必须是数字序号） */
const addFieldEnumOptions = computed(() =>
  addFieldDef.value && addFieldDef.value.kind === 'ENUM'
    ? lookupParameterEnum(addFieldBlock.value, addFieldDef.value.path.split('.'))
    : undefined
)

function resetAddFieldValue(): void {
  addFieldBool.value = undefined
  addFieldNumber.value = undefined
  addFieldEnum.value = undefined
  addFieldText.value = ''
  addFieldList.value = ''
}

function openAddFieldDialog(blockKey: BlockKey): void {
  addFieldBlock.value = blockKey
  addFieldPath.value = ''
  resetAddFieldValue()
  addFieldDialog.value = true
}

/** 换字段 ⇒ 换控件，旧值必须清掉（否则会把上一个字段的值写进新字段） */
function onAddFieldPathChange(): void {
  resetAddFieldValue()
}

/** 取用户填的值；没填（undefined / 空串 / 空清单）⇒ 返回 null，不写 */
function collectAddFieldValue(def: BlockFieldDef): JsonValue | null {
  if (def.kind === 'BOOL') return addFieldBool.value === undefined ? null : addFieldBool.value
  if (def.kind === 'NUMBER') return addFieldNumber.value === undefined ? null : addFieldNumber.value
  if (def.kind === 'ENUM') return addFieldEnum.value === undefined ? null : addFieldEnum.value
  if (def.kind === 'TEXT') {
    const t = addFieldText.value.trim()
    return t ? t : null
  }
  const items = addFieldList.value
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
  return items.length ? items : null
}

function onAddField(): void {
  const def = addFieldDef.value
  if (!def) {
    ElMessage.warning('先选一个字段')
    return
  }
  const value = collectAddFieldValue(def)
  if (value === null) {
    ElMessage.warning('这个字段还没填值')
    return
  }
  const ok = paramStore.addBlockField(addFieldBlock.value, def.path.split('.'), value)
  if (!ok) return
  addFieldDialog.value = false
  ElMessage.success('已加上，记得保存草稿')
}

/* ===== [保存草稿] Dialog ===== */
const saveDraftDialog = ref(false)
const saveDraftForm = ref({ changeReason: '' })

function openSaveDraftDialog(): void {
  saveDraftForm.value = { changeReason: '' }
  saveDraftDialog.value = true
}

async function onSaveDraftConfirm(): Promise<void> {
  const ok = await paramStore.onSaveDraft(saveDraftForm.value.changeReason)
  if (ok) saveDraftDialog.value = false
}

/* ===== 校验 ===== */
async function onValidate(): Promise<void> {
  await paramStore.validateDraft()
}

/* ===== Publish Dialog ===== */
const publishDialog = ref(false)
const publishForm = ref({ changeReason: '' })

function openPublishDialog(): void {
  publishForm.value = { changeReason: '' }
  publishDialog.value = true
}

async function onPublish(): Promise<void> {
  const ok = await paramStore.publish(publishForm.value.changeReason)
  if (ok) publishDialog.value = false
}

/* ===== Disable Dialog ===== */
const disableDialog = ref(false)
const disableForm = ref({ reason: '' })

function openDisableDialog(): void {
  disableForm.value = { reason: '' }
  disableDialog.value = true
}

async function onDisable(): Promise<void> {
  const ok = await paramStore.disable(disableForm.value.reason)
  if (ok) disableDialog.value = false
}

/* ===== 物理删除（最窄口径：无任何已发布版本才可删，连带删草稿） ===== */
/** 详情版本历史里是否存在 PUBLISHED —— 有则前端提前拦住 */
const detailHasPublished = computed<boolean>(() =>
  (detailNN.value?.history ?? []).some(
    (h: ParameterSetVersionHistoryDto) => h.status === 'PUBLISHED'
  )
)

/**
 * 删除参数集整族（硬删）。
 * - 含 PUBLISHED 版本 ⇒ 后端 400（stub 里提前禁用）
 * - 版本被策略包版本引用 ⇒ 后端 400「存在被策略包引用的版本，不能删除」——引用关系前端拿不到，只回显原文
 */
async function onDeleteParameterSet(
  parameterSetId: number,
  parameterSetName: string
): Promise<void> {
  let confirmed = false
  try {
    await ElMessageBox.confirm(
      `即将永久删除参数集「${parameterSetName}」及其全部未发布草稿版本，删除后无法恢复。已发布过版本或仍被策略包引用的参数集不能删除。是否继续？`,
      '删除参数集',
      {
        type: 'warning',
        confirmButtonText: '确认删除',
        cancelButtonText: '取消',
        confirmButtonClass: 'el-button--danger'
      }
    )
    confirmed = true
  } catch {
    confirmed = false
  }
  if (!confirmed) return
  if ((await paramStore.removeParameterSet(parameterSetId)) === true) {
    ElMessage.success('参数集已删除')
    noVersionPicked.value = null
  } else if (paramStore.error) {
    ElMessage.error(paramStore.error)
  }
}

/* ===== [+ 新建草稿] Dialog ===== */
const createDraftDialog = ref(false)
const createDraftForm = ref({
  sourceParameterSetVersionId: 0,
  newVersionCode: '',
  changeReason: ''
})

/** 已带非空 versionId 的历史行（fork 端点必须拿到主键） */
type ForkableVersion = ParameterSetVersionHistoryDto & { versionId: number }

const forkableVersions = computed<ForkableVersion[]>(() =>
  (detailNN.value?.history ?? []).filter(
    (h): h is ForkableVersion =>
      (h.status === 'PUBLISHED' || h.status === 'DISABLED') && h.versionId != null
  )
)

function nextVersionCode(base: string): string {
  const m = /^(.*?)(\d+)$/.exec(base)
  if (m) return `${m[1]}${Number(m[2]) + 1}`
  return `${base}-D1`
}

function openCreateDraftDialog(): void {
  if (!detailNN.value) return
  const source = forkableVersions.value[0]
  createDraftForm.value = {
    sourceParameterSetVersionId: source?.versionId ?? 0,
    newVersionCode: nextVersionCode(source?.versionCode ?? 'PS-DRAFT'),
    changeReason: ''
  }
  createDraftDialog.value = true
  void paramStore.checkForkSourceWarehouseRefs(createDraftForm.value.sourceParameterSetVersionId)
}

function onSourceVersionChange(id: number): void {
  createDraftForm.value.sourceParameterSetVersionId = id
  const src = forkableVersions.value.find((h) => h.versionId === id)
  if (src) createDraftForm.value.newVersionCode = nextVersionCode(src.versionCode)
  void paramStore.checkForkSourceWarehouseRefs(id)
}

async function onCreateDraft(): Promise<void> {
  const reason = createDraftForm.value.changeReason.trim()
  if (!reason) {
    paramStore.handleError(new Error('请填写变更原因'))
    return
  }
  const ok = await paramStore.forkDraft(
    createDraftForm.value.sourceParameterSetVersionId,
    createDraftForm.value.newVersionCode,
    reason
  )
  if (ok) {
    createDraftDialog.value = false
    activeTab.value = 'edit'
  }
}

/* ===== dirty 拦截（路由切换 + 浏览器关闭） ===== */
async function confirmLeaveIfDirty(): Promise<boolean> {
  if (!dirty.value) return true
  try {
    await ElMessageBox.confirm(
      '当前参数集有未保存的修改，离开将丢失。\n请先点击「保存草稿」或「取消未保存」。',
      '未保存修改',
      {
        confirmButtonText: '确认离开',
        cancelButtonText: '留在页面',
        type: 'warning',
        closeOnClickModal: false,
        closeOnPressEscape: false
      }
    )
    return true
  } catch {
    return false
  }
}

function handleBeforeUnload(e: BeforeUnloadEvent): void {
  if (dirty.value) {
    e.preventDefault()
    e.returnValue = ''
  }
}

onMounted(() => {
  paramStore.loadList()
  // 仓库字典（主数据）：类级 aps.plan.view；拉不到时页面降级为「只能看已有编码」
  void paramStore.loadWarehouseDict()
  window.addEventListener('beforeunload', handleBeforeUnload)
})

onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', handleBeforeUnload)
})

onBeforeRouteLeave(async () => {
  const ok = await confirmLeaveIfDirty()
  if (!ok) return false
  return true
})
</script>

<template>
  <div class="aps-paramsets">
    <!-- ===== 顶部（无大标题页头：页面名由菜单/面包屑给出） ===== -->
    <div class="page-header">
      <div class="header-actions">
        <ElTooltip
          :content="canWrite ? '新建一个参数集（编码创建后不可修改）' : '当前账号无编辑权限'"
          placement="bottom"
        >
          <ElButton :disabled="!canWrite" @click="openCreateParameterSetDialog">
            <Icon icon="vi-mdi:plus-box-outline" /> 新建参数集
          </ElButton>
        </ElTooltip>
        <ElTooltip
          v-if="currentDetail"
          :content="
            forkableVersions.length ? '基于已发布/已停用版本新建草稿' : '没有可作为起点的已发布版本'
          "
          placement="bottom"
        >
          <ElButton
            type="primary"
            :disabled="!forkableVersions.length"
            @click="openCreateDraftDialog"
          >
            <Icon icon="vi-mdi:file-plus-outline" /> 新建草稿
          </ElButton>
        </ElTooltip>
      </div>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="error" />

    <!-- ===== KPI 横条 ===== -->
    <div class="kpi-strip">
      <div class="kpi-pill">
        <span class="pill-label">参数集总数</span>
        <span class="pill-val">{{ paramStore.parameterSets.length }}</span>
      </div>
      <div v-for="key in RULE_STATUSES_UI" :key="key" class="kpi-pill">
        <span class="pill-label">{{ RULE_STATUS_LABELS[key] }}</span>
        <span class="pill-val">{{ statusCounts[key] ?? 0 }}</span>
      </div>
      <div class="kpi-pill">
        <span class="pill-label">{{ NO_VERSION_LABEL }}</span>
        <span class="pill-val">{{ noVersionCount }}</span>
      </div>
    </div>

    <!-- ===== 状态过滤 ===== -->
    <div class="filter-row">
      <ElRadioGroup v-model="statusFilter" @change="setFilter">
        <ElRadioButton label="ALL">全部</ElRadioButton>
        <ElRadioButton v-for="key in RULE_STATUSES_UI" :key="key" :label="key">
          {{ RULE_STATUS_LABELS[key] }}
        </ElRadioButton>
        <ElRadioButton :label="NO_VERSION">{{ NO_VERSION_LABEL }}</ElRadioButton>
      </ElRadioGroup>
      <ElTooltip content="停用的参数集默认不显示；勾选后才能看到并重新启用它们" placement="top">
        <label class="show-inactive-toggle">
          <ElSwitch v-model="showInactive" size="small" />
          <span>显示已停用（{{ inactiveCount }}）</span>
        </label>
      </ElTooltip>
    </div>

    <div class="content-row">
      <!-- ===== 左：参数集列表 ===== -->
      <ElCard class="panel left-panel">
        <template #header>
          <div class="panel-header">
            <span>参数集列表</span>
            <ElTag size="small" effect="plain">{{ filteredParameterSets.length }}</ElTag>
          </div>
        </template>
        <div v-if="!filteredParameterSets.length && !listLoading" class="panel-empty">
          <ElEmpty description="无符合条件的参数集" />
        </div>
        <div v-else class="ps-list">
          <div
            v-for="p in filteredParameterSets"
            :key="p.parameterSetId"
            class="ps-card"
            :class="{
              active: detailNN?.summary.parameterSetId === p.parameterSetId,
              draft: p.status === 'DRAFT',
              published: p.status === 'PUBLISHED',
              disabled: p.status === 'DISABLED'
            }"
            @click="selectParameterSet(p)"
          >
            <div class="ps-head">
              <span class="ps-code">{{ p.parameterSetCode }}</span>
              <span class="ps-head-tags">
                <ElTag v-if="p.isActive === false" size="small" type="info">主表停用</ElTag>
                <ElTag
                  :type="p.hasVersion ? RULE_STATUS_TAG[p.status] : 'info'"
                  size="small"
                  effect="dark"
                >
                  {{ p.hasVersion ? RULE_STATUS_LABELS[p.status] : NO_VERSION_LABEL }}
                </ElTag>
                <ElTooltip
                  :content="
                    canWrite ? '修改名称 / 说明 / 启停（编码不可改）' : '当前账号无编辑权限'
                  "
                  placement="top"
                >
                  <ElButton
                    text
                    size="small"
                    class="ps-edit-btn"
                    :disabled="!canWrite"
                    @click.stop="openEditParameterSetDialog(p)"
                  >
                    <Icon icon="vi-mdi:pencil" /> 编辑
                  </ElButton>
                </ElTooltip>
              </span>
            </div>
            <div class="ps-name">{{ p.parameterSetName }}</div>
            <div class="ps-meta">
              <span class="meta-key">最新已发布版本</span>
              <span class="meta-val">{{ p.currentVersionCode || '—' }}</span>
              <span v-if="p.draftVersionCode" class="meta-draft"
                >/ 草稿 {{ p.draftVersionCode }}</span
              >
            </div>
            <div v-if="p.lastChangeReason" class="ps-reason">{{ p.lastChangeReason }}</div>
          </div>
        </div>
      </ElCard>

      <!-- ===== 右：详情 ===== -->
      <ElCard class="panel right-panel">
        <template v-if="noVersionPicked">
          <ElEmpty
            :description="`「${noVersionPicked.parameterSetName}」还没有配任何版本，现在没有内容可看。`"
          >
            <ElButton
              v-if="canWrite"
              type="danger"
              size="small"
              plain
              @click="
                onDeleteParameterSet(
                  noVersionPicked.parameterSetId,
                  noVersionPicked.parameterSetName
                )
              "
            >
              <Icon icon="vi-mdi:trash-can-outline" /> 删除参数集
            </ElButton>
          </ElEmpty>
        </template>

        <template v-else-if="!currentDetail">
          <ElEmpty description="从左侧选择一个参数集" />
        </template>

        <div v-else>
          <!-- ===== Header 条 ===== -->
          <div class="detail-head-row">
            <div>
              <ElTag :type="RULE_STATUS_TAG[detailNN.summary.status]" effect="dark" size="small">
                {{ RULE_STATUS_LABELS[detailNN.summary.status] }}
              </ElTag>
              {{ detailNN.summary.parameterSetCode }}
              <span class="muted-tag">
                {{ detailNN.summary.currentVersionCode || '（无已发布版本）' }}
              </span>
              <span v-if="detailNN.summary.draftVersionCode" class="muted-tag">
                → 草稿 {{ detailNN.summary.draftVersionCode }}
              </span>
            </div>
            <div class="head-actions">
              <ElButton
                v-if="actions.canValidate"
                size="small"
                :loading="validating"
                @click="onValidate"
              >
                <Icon icon="vi-mdi:check-decagram" /> 校验草稿
              </ElButton>
              <ElButton
                v-if="actions.canPublish"
                type="primary"
                size="small"
                :disabled="!canPublishAction"
                @click="openPublishDialog"
              >
                <Icon icon="vi-mdi:rocket-launch" /> 发布
              </ElButton>
              <ElButton
                v-if="actions.canDisable"
                type="danger"
                size="small"
                plain
                :disabled="!canPublishAction"
                @click="openDisableDialog"
              >
                <Icon icon="vi-mdi:archive" /> 停用
              </ElButton>
              <ElTooltip
                v-if="canWrite"
                :disabled="!detailHasPublished"
                content="该参数集已有已发布版本，不能物理删除；如不再使用请「停用」已发布版本"
                placement="top"
              >
                <span>
                  <ElButton
                    type="danger"
                    size="small"
                    plain
                    :disabled="detailHasPublished"
                    :loading="actionRunning"
                    @click="
                      onDeleteParameterSet(
                        detailNN.summary.parameterSetId,
                        detailNN.summary.parameterSetName
                      )
                    "
                  >
                    <Icon icon="vi-mdi:trash-can-outline" /> 删除
                  </ElButton>
                </span>
              </ElTooltip>
            </div>
          </div>

          <!-- ===== Summary 描述 ===== -->
          <ElDescriptions :column="3" size="small" border>
            <ElDescriptionsItem label="参数集">
              {{ detailNN.summary.parameterSetName }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="状态">
              <ElTag :type="RULE_STATUS_TAG[detailNN.summary.status]" size="small">
                {{ RULE_STATUS_LABELS[detailNN.summary.status] }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="版本">
              <span class="code-tag">{{ buffer.governance?.versionCode || '—' }}</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="最新已发布版本">
              {{ detailNN.summary.currentVersionCode || '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="草稿版本">
              <span v-if="detailNN.summary.draftVersionCode">
                {{ detailNN.summary.draftVersionCode }}
              </span>
              <span v-else class="muted">—</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="最近变更">
              {{ detailNN.summary.lastChangeReason || '（无）' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem v-if="!canPublishAction" label="权限" :span="3">
              <ElTag type="warning" size="small">当前账号无参数集发布权限</ElTag>
              <span class="msg-line">
                参数集发布使用独立权限码 aps.parameter.publish；如需发布，请联系系统管理员
              </span>
            </ElDescriptionsItem>
          </ElDescriptions>

          <!-- ===== 校验结果（后端 PublishValidationResult 真值） ===== -->
          <ElAlert
            v-if="lastValidation"
            :type="lastValidation.isValid ? 'success' : 'error'"
            :closable="false"
            show-icon
            class="validation-alert"
          >
            <template #title>
              {{
                lastValidation.isValid
                  ? '校验通过：没有发现阻断发布的问题'
                  : `校验未通过：${lastValidation.errors.length} 项错误`
              }}
            </template>
            <ul v-if="lastValidation.errors.length" class="v-list">
              <li v-for="(e, i) in lastValidation.errors" :key="`e${i}`" :title="e.message">
                {{ translateValidationError(e.code, e.message) }}
                <span v-if="e.fieldName" class="v-field">（{{ e.fieldName }}）</span>
              </li>
            </ul>
            <ul v-if="lastValidation.warnings.length" class="v-list">
              <li v-for="(w, i) in lastValidation.warnings" :key="`w${i}`">
                提示：{{ w.message }}
              </li>
            </ul>
            <div class="v-time">校验时间：{{ formatUtcDateTime(lastValidation.validatedAt) }}</div>
          </ElAlert>

          <!-- ===== Tabs ===== -->
          <ElTabs v-model="activeTab" class="detail-tabs">
            <!-- 1. 参数编辑（5 主题 JSON 块） -->
            <ElTabPane label="参数编辑" name="edit">
              <ElAlert
                v-if="!actions.canEdit"
                type="info"
                :closable="false"
                show-icon
                class="tab-alert"
              >
                <template #title>
                  {{ `${RULE_STATUS_LABELS[detailNN.summary.status]} 状态：参数只读` }}
                </template>
                只有草稿版本可以修改。如需变更，请点右上角「新建草稿」→ 编辑 → 校验 → 发布。
              </ElAlert>

              <template v-if="buffer.workingBlocks">
                <!-- 仓库字典拉取失败（类级 aps.plan.view）：如实提示，页面降级为「只能看已有编码」 -->
                <ElAlert
                  v-if="warehouseDictError"
                  type="warning"
                  :closable="false"
                  show-icon
                  class="tab-alert"
                >
                  <template #title>{{ warehouseDictError }}</template>
                </ElAlert>

                <!-- 非阻断提示：引用了仓库字典里不存在的编码（发布时后端会报 INVALID_WAREHOUSE_REF） -->
                <ElAlert
                  v-if="currentUnknownWarehouseCodes.length"
                  type="warning"
                  :closable="false"
                  show-icon
                  class="tab-alert"
                >
                  <template #title>
                    有 {{ currentUnknownWarehouseCodes.length }} 个仓库编码在仓库字典里找不到
                  </template>
                  {{ currentUnknownWarehouseCodes.join('、') }} ——
                  发布时会报错，建议改成字典里的真实仓库。
                </ElAlert>

                <div v-for="blockKey in BLOCK_KEYS" :key="blockKey" class="param-block-section">
                  <div class="block-header">
                    <h4 class="block-title">{{ BLOCK_LABELS[blockKey] }}</h4>
                    <ElTag v-if="buffer.dirty[blockKey]" type="warning" size="small" effect="dark">
                      ● 未保存
                    </ElTag>
                    <ElTag v-else size="small" effect="plain">
                      {{ rowsGrouped[blockKey].length }} 项
                    </ElTag>
                    <ElTag
                      v-if="isFullyInertBlock(blockKey)"
                      size="small"
                      type="info"
                      effect="plain"
                    >
                      暂未生效
                    </ElTag>
                    <ElButton
                      plain
                      type="primary"
                      size="small"
                      class="block-add-btn"
                      :disabled="!actions.canEdit"
                      @click="openAddFieldDialog(blockKey)"
                    >
                      <Icon icon="vi-mdi:plus" /> 新增字段
                    </ElButton>
                  </div>
                  <!-- 有专属编辑器的块（供应 / 采购）不显示空态插图，但**必须**留一句引导：
                       否则用户只看到「采购仓库优先级」，不知道上面的普通参数从哪加。 -->
                  <div
                    v-if="rowsGrouped[blockKey].length === 0 && hasWarehouseEditor(blockKey)"
                    class="add-field-hintline"
                  >
                    这块还没有参数。点右上角「新增字段」添加，例如：{{ addFieldExamples(blockKey) }}
                    。
                  </div>
                  <ElEmpty
                    v-if="rowsGrouped[blockKey].length === 0 && !hasWarehouseEditor(blockKey)"
                    :image-size="60"
                  >
                    <template #description>
                      <div class="add-field-empty">
                        <span>{{ BLOCK_LABELS[blockKey] }} 暂无参数</span>
                        <ElButton
                          link
                          type="primary"
                          size="small"
                          :disabled="!actions.canEdit"
                          @click="openAddFieldDialog(blockKey)"
                        >
                          点这里加第一个字段
                        </ElButton>
                      </div>
                    </template>
                  </ElEmpty>
                  <ElTable
                    v-else-if="rowsGrouped[blockKey].length > 0"
                    :data="rowsGrouped[blockKey]"
                    size="small"
                    border
                  >
                    <ElTableColumn label="参数名" min-width="280">
                      <template #default="{ row }">
                        <div class="param-name">
                          <ElTooltip v-if="row.hint" :content="row.hint" placement="top">
                            <span class="param-label param-label-hint">{{
                              row.label ?? row.path.join('.')
                            }}</span>
                          </ElTooltip>
                          <span v-else class="param-label">{{
                            row.label ?? row.path.join('.')
                          }}</span>
                          <code v-if="row.label" class="code-tag param-key">{{
                            row.path.join('.')
                          }}</code>
                          <ElTooltip
                            v-if="row.inert"
                            :content="inertAnnotationOfRow(row).note"
                            placement="top"
                          >
                            <ElTag size="small" type="info" effect="plain">
                              {{ inertAnnotationOfRow(row).tag }}
                            </ElTag>
                          </ElTooltip>
                          <ElTag v-if="row.fixed" size="small" type="warning" effect="plain">
                            V1 固定
                          </ElTag>
                        </div>
                      </template>
                    </ElTableColumn>
                    <ElTableColumn label="类型" width="100" align="center">
                      <template #default="{ row }">
                        <ElTag size="small" effect="plain">{{
                          row.options ? '枚举' : TYPE_LABELS[row.type]
                        }}</ElTag>
                      </template>
                    </ElTableColumn>
                    <ElTableColumn label="当前值" min-width="260">
                      <template #default="{ row }">
                        <!-- 只读：非草稿态 / 不可编辑行（数组整体不展开，避免误改） -->
                        <span v-if="!actions.canEdit || !row.editable" class="readonly-val">
                          {{ displayValueOf(row) }}
                        </span>
                        <ElSelect
                          v-else-if="row.options"
                          :model-value="typeof row.value === 'number' ? row.value : undefined"
                          size="small"
                          style="width: 100%"
                          @change="(v) => onCellEdit(row, typeof v === 'number' ? v : 0)"
                        >
                          <ElOption
                            v-for="opt in row.options"
                            :key="opt.value"
                            :label="opt.label"
                            :value="opt.value"
                          />
                        </ElSelect>
                        <ElInputNumber
                          v-else-if="row.type === 'PERCENT'"
                          :model-value="typeof row.value === 'number' ? row.value : 0"
                          :step="0.01"
                          :precision="4"
                          :min="row.min"
                          :max="row.max"
                          size="small"
                          style="width: 100%"
                          @change="(v) => onCellEdit(row, typeof v === 'number' ? v : 0)"
                        />
                        <ElInputNumber
                          v-else-if="row.type === 'NUMBER'"
                          :model-value="typeof row.value === 'number' ? row.value : 0"
                          :step="row.step ?? 1"
                          :min="row.min"
                          :max="row.max"
                          size="small"
                          style="width: 100%"
                          @change="(v) => onCellEdit(row, typeof v === 'number' ? v : 0)"
                        />
                        <ElInput
                          v-else-if="row.type === 'TEXT_LIST'"
                          :model-value="listTextOf(row)"
                          type="textarea"
                          :rows="3"
                          size="small"
                          placeholder="每行一项"
                          @change="(v) => onTextListEdit(row, v)"
                        />
                        <ElSwitch
                          v-else-if="row.type === 'BOOLEAN'"
                          :model-value="row.value === true"
                          size="small"
                          @change="(v) => onCellEdit(row, Boolean(v))"
                        />
                        <ElInput
                          v-else
                          :model-value="String(row.value ?? '')"
                          size="small"
                          @change="(v) => onCellEdit(row, v)"
                        />
                      </template>
                    </ElTableColumn>
                    <ElTableColumn label="操作" width="70" align="center">
                      <template #default="{ row }">
                        <ElTooltip v-if="row.editable" content="删除这个参数" placement="top">
                          <ElButton
                            size="small"
                            text
                            type="danger"
                            :disabled="!actions.canEdit"
                            @click="onRemoveField(row)"
                          >
                            <Icon icon="vi-mdi:delete-outline" />
                          </ElButton>
                        </ElTooltip>
                      </template>
                    </ElTableColumn>
                  </ElTable>

                  <!-- 仓库引用落点（专属编辑器：有序码列表 / 对象数组，通用单值控件表达不了）。
                       供应 Inventory.WarehousePriority 按 3号位 2026-11-06 v1.1 改判隐藏；
                       值仍留在块 JSON 中并原样回写，载荷边界不变。 -->
                  <template v-if="blockKey === 'procurement'">
                    <WarehouseRefEditor
                      kind="procurement-priority"
                      title="采购仓库优先级"
                      :disabled="!actions.canEdit"
                    />
                    <WarehouseRefEditor
                      kind="purchase-lt"
                      title="默认采购提前期"
                      :disabled="!actions.canEdit"
                    />
                    <WarehouseRefEditor
                      kind="arrival-offset"
                      title="到货可用偏移"
                      :disabled="!actions.canEdit"
                    />
                    <!-- 采购良率（专属编辑器：物料 + 工序 + 良率，通用单值控件表达不了） -->
                    <PlanningYieldEditor :disabled="!actions.canEdit" />
                  </template>

                  <!-- 锚点资源编码（专属输入项：3号位 10-25 定调「自由文本补录入」，后端零改动） -->
                  <div
                    v-if="blockKey === 'solverStrategy' && solverStrategyHasContent"
                    class="anchor-field"
                  >
                    <div class="anchor-field-label">锚点资源编码</div>
                    <ElInput
                      :model-value="anchorResourceCode"
                      :disabled="!actions.canEdit"
                      placeholder="填资源的业务编码（不是资源 ID）"
                      size="small"
                      @change="(v) => onAnchorCodeChange(v)"
                    />
                    <p class="field-note">
                      填资源的业务编码。只有把「瓶颈锚点方式」选成非自动时它才生效，选「自动判定」时填了也不起作用。
                      编码写错不会报错，运行时按自动处理。
                    </p>
                  </div>

                  <!-- 非阻断提示：选了非自动锚点方式却没填锚点资源编码（3号位 10-25 §五；不拦截、不阻断发布） -->
                  <ElAlert
                    v-if="blockKey === 'solverStrategy' && anchorHintMessage"
                    type="warning"
                    :closable="false"
                    show-icon
                    class="tab-alert"
                  >
                    <template #title>{{ anchorHintMessage }}</template>
                  </ElAlert>
                </div>

                <!-- 保存操作条（仅草稿态可见） -->
                <div v-if="actions.canEdit" class="save-bar">
                  <ElTag :type="dirty ? 'warning' : 'info'" size="small">
                    参数集 {{ dirty ? '未保存' : '已保存' }}
                  </ElTag>
                  <ElButton :disabled="!dirty" @click="paramStore.onCancelDirty">
                    <Icon icon="vi-mdi:undo" /> 取消未保存
                  </ElButton>
                  <ElButton
                    type="primary"
                    :loading="buffer.saving"
                    :disabled="!dirty"
                    @click="openSaveDraftDialog"
                  >
                    <Icon icon="vi-mdi:content-save" /> 保存草稿
                  </ElButton>
                </div>
              </template>
              <ElEmpty v-else description="该版本没有参数内容" />
            </ElTabPane>

            <!-- 2. Diff -->
            <ElTabPane label="版本对比" name="diff">
              <template v-if="!paramDiffPair">
                <ElEmpty
                  description="暂无可对比的两个版本（需要至少两个版本：一个已发布版本 + 另一个版本）"
                />
              </template>
              <template v-else>
                <div class="diff-summary">
                  <ElTag type="primary" size="small">{{ paramDiffRows.length }} 处参数变更</ElTag>
                  <span class="diff-meta">{{ diffPairHint }}</span>
                </div>

                <!-- 参数内容差异（前端逐参数比） -->
                <ElTable v-if="paramDiffRows.length" :data="paramDiffRows" size="small" border>
                  <ElTableColumn label="块" width="120">
                    <template #default="{ row }">{{ BLOCK_LABELS[row.block] }}</template>
                  </ElTableColumn>
                  <ElTableColumn label="参数" min-width="200">
                    <template #default="{ row }">
                      <div class="diff-field">
                        <span class="diff-field-cn">{{ row.label }}</span>
                        <code class="code-tag">{{ row.path }}</code>
                      </div>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="变化" width="90" align="center">
                    <template #default="{ row }">
                      <ElTag :type="DIFF_KIND_TAG[row.kind]" size="small" effect="plain">
                        {{ DIFF_KIND_LABELS[row.kind] }}
                      </ElTag>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="原值" min-width="160">
                    <template #default="{ row }">
                      <span class="diff-old">{{ row.oldValue || '—' }}</span>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="新值" min-width="160">
                    <template #default="{ row }">
                      <span class="diff-new">{{ row.newValue || '—' }}</span>
                    </template>
                  </ElTableColumn>
                </ElTable>
                <ElEmpty v-else description="两个版本的参数内容完全一致" :image-size="60" />

                <!-- 版本信息差异（后端 diff 的元信息项） -->
                <template v-if="metaDiffFields.length">
                  <div class="diff-summary meta">
                    <ElTag type="info" size="small">
                      版本信息 {{ metaDiffFields.length }} 项变更
                    </ElTag>
                  </div>
                  <ElTable :data="metaDiffFields" size="small" border>
                    <ElTableColumn label="字段" min-width="200">
                      <template #default="{ row }">
                        <div class="diff-field">
                          <span class="diff-field-cn">{{ row.fieldDisplayName }}</span>
                          <code class="code-tag">{{ row.fieldName }}</code>
                        </div>
                      </template>
                    </ElTableColumn>
                    <ElTableColumn label="原值" min-width="180">
                      <template #default="{ row }">
                        <span class="diff-old">{{ row.sourceValue ?? '（无）' }}</span>
                      </template>
                    </ElTableColumn>
                    <ElTableColumn label="新值" min-width="180">
                      <template #default="{ row }">
                        <span class="diff-new">{{ row.targetValue ?? '（无）' }}</span>
                      </template>
                    </ElTableColumn>
                  </ElTable>
                </template>

                <p v-if="currentDiff" class="diff-note">
                  对比对象：{{ currentDiff.entityType }} ·
                  {{ formatUtcDateTime(currentDiff.comparedAt) }}
                </p>
              </template>
            </ElTabPane>

            <!-- 3. History -->
            <ElTabPane label="历史版本" name="history">
              <ElTable :data="detailNN.history" size="small" border>
                <ElTableColumn label="版本码" min-width="150">
                  <template #default="{ row }">
                    <span :class="{ 'history-current': row.isLatestPublished }">{{
                      row.versionCode
                    }}</span>
                  </template>
                </ElTableColumn>
                <ElTableColumn label="状态" width="110" align="center">
                  <template #default="{ row }">
                    <ElTag :type="RULE_STATUS_TAG[row.status]" size="small">
                      {{ RULE_STATUS_LABELS[row.status] }}
                    </ElTag>
                  </template>
                </ElTableColumn>
                <ElTableColumn prop="changeReason" label="变更原因" min-width="200" />
                <ElTableColumn label="发布者" width="140">
                  <template #default="{ row }">
                    <code class="code-tag">{{ row.publishedBy || '—' }}</code>
                  </template>
                </ElTableColumn>
                <ElTableColumn label="发布时间" width="160">
                  <template #default="{ row }">
                    {{ formatLocalDateTime(row.publishedAt) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn width="160">
                  <template #header>
                    <ElTooltip
                      content="为空 = 不限有效期（发布即生效，直到停用）。参数集/规则集发布时后端不写这列。"
                      placement="top"
                    >
                      <span class="th-hint">生效起</span>
                    </ElTooltip>
                  </template>
                  <template #default="{ row }">
                    {{ formatLocalDateTime(row.effectiveFrom) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn width="160">
                  <template #header>
                    <ElTooltip
                      content="为空 = 一直有效（直到这个版本被停用）。参数集/规则集发布时后端不写这列。"
                      placement="top"
                    >
                      <span class="th-hint">生效止</span>
                    </ElTooltip>
                  </template>
                  <template #default="{ row }">
                    {{ formatLocalDateTime(row.effectiveTo) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn width="120" align="center">
                  <template #header>
                    <ElTooltip
                      content="标的是最近一次发布的那一版。「当前有效」另按生效期窗口判定，见「生效起 / 生效止」两列。"
                      placement="top"
                    >
                      <span class="th-hint">最新已发布</span>
                    </ElTooltip>
                  </template>
                  <template #default="{ row }">
                    <ElTag v-if="row.isLatestPublished" size="small" type="success" effect="dark"
                      >是</ElTag
                    >
                    <span v-else class="muted">否</span>
                  </template>
                </ElTableColumn>
              </ElTable>
              <ElAlert type="info" :closable="false" show-icon class="u22-alert">
                <template #title>发布产生新版本，历史不覆盖</template>
                每次发布产生新的版本，历史版本完整保留，可随时回看变更轨迹。
              </ElAlert>
              <ElAlert type="info" :closable="false" show-icon class="u22-alert">
                <template #title>「生效起 / 生效止」留空是正常的</template>
                参数集与规则集发布时不写有效期；留空 = 这个版本一直有效，直到它被停用。
              </ElAlert>
            </ElTabPane>
          </ElTabs>
        </div>
      </ElCard>
    </div>

    <!-- ===== 加载态 ===== -->
    <div v-if="loading" class="loading-tip">加载参数集详情中…</div>

    <!-- ===== [+ 新建参数集] Dialog（主表） ===== -->
    <ElDialog
      v-model="createParameterSetDialog"
      title="新建参数集"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集编码 (必填)">
          <ElInput
            v-model="createParameterSetForm.parameterSetCode"
            maxlength="50"
            show-word-limit
            placeholder="如 PS-PROD；全库唯一，创建后不能改"
          />
        </ElFormItem>
        <ElFormItem label="参数集名称 (必填)">
          <ElInput
            v-model="createParameterSetForm.parameterSetName"
            maxlength="200"
            placeholder="如 生产参数集"
          />
        </ElFormItem>
        <ElFormItem label="说明">
          <ElInput
            v-model="createParameterSetForm.description"
            type="textarea"
            :rows="2"
            placeholder="选填"
          />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>会同时建一个空白草稿版本</template>
          新建后系统自动建一条空白草稿版本（编码 = 参数集编码 + -V1）。参数内容挂在版本上，
          没有版本时页面无可编辑内容。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="createParameterSetDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="actionRunning" @click="onCreateParameterSet">
          确认新建
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [编辑参数集] Dialog（主表） ===== -->
    <ElDialog
      v-model="editParameterSetDialog"
      title="编辑参数集"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集编码">
          <ElInput :model-value="editParameterSetForm.parameterSetCode" disabled />
        </ElFormItem>
        <ElFormItem label="参数集名称 (必填)">
          <ElInput
            v-model="editParameterSetForm.parameterSetName"
            maxlength="200"
            placeholder="如 生产参数集"
          />
        </ElFormItem>
        <ElFormItem label="说明">
          <ElInput
            v-model="editParameterSetForm.description"
            type="textarea"
            :rows="2"
            placeholder="选填"
          />
        </ElFormItem>
        <ElFormItem label="主表启用">
          <ElSwitch v-model="editParameterSetForm.isActive" />
          <span class="edit-hint">
            {{ editParameterSetForm.isActive ? '启用中' : '已停用' }}
          </span>
        </ElFormItem>
        <ElAlert
          v-if="!editParameterSetForm.isActive"
          type="warning"
          :closable="false"
          show-icon
          class="d-alert"
        >
          <template #title>停用只关主表</template>
          不会改动已有版本的状态；是否继续用于排产由装载侧判定。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="editParameterSetDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="actionRunning" @click="onUpdateParameterSet">
          保存
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [新增字段] Dialog（空块加第一个参数） ===== -->
    <ElDialog v-model="addFieldDialog" title="新增字段" width="520px" :close-on-click-modal="false">
      <ElForm label-width="90px" size="default">
        <ElFormItem label="所属块">
          <span>{{ BLOCK_LABELS[addFieldBlock] }}</span>
        </ElFormItem>
        <ElFormItem label="字段">
          <ElSelect
            v-model="addFieldPath"
            placeholder="选择要新增的字段"
            style="width: 100%"
            @change="onAddFieldPathChange"
          >
            <ElOption
              v-for="f in addFieldCandidates"
              :key="f.path"
              :label="f.label"
              :value="f.path"
            >
              <div class="add-field-option">
                <span>{{ f.label }}</span>
                <code class="code-tag">{{ f.path }}</code>
                <ElTag
                  v-if="isInertBlockField(addFieldBlock, f.path)"
                  size="small"
                  type="info"
                  effect="plain"
                >
                  {{ inertAnnotationOf(addFieldBlock, f.path).tag }}
                </ElTag>
              </div>
            </ElOption>
          </ElSelect>
        </ElFormItem>
        <ElFormItem v-if="addFieldDef" label="取值">
          <ElRadioGroup v-if="addFieldDef.kind === 'BOOL'" v-model="addFieldBool">
            <ElRadioButton :value="true">是</ElRadioButton>
            <ElRadioButton :value="false">否</ElRadioButton>
          </ElRadioGroup>
          <ElInputNumber
            v-else-if="addFieldDef.kind === 'NUMBER'"
            v-model="addFieldNumber"
            :min="addFieldDef.min"
            :max="addFieldDef.max"
            :step="addFieldDef.step ?? 1"
            style="width: 100%"
          />
          <ElSelect
            v-else-if="addFieldDef.kind === 'ENUM'"
            v-model="addFieldEnum"
            placeholder="选择一项"
            style="width: 100%"
          >
            <ElOption
              v-for="opt in addFieldEnumOptions ?? []"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
          <ElInput
            v-else-if="addFieldDef.kind === 'TEXT'"
            v-model="addFieldText"
            placeholder="填写内容"
          />
          <ElInput
            v-else-if="addFieldDef.kind === 'TEXT_LIST'"
            v-model="addFieldList"
            type="textarea"
            :rows="3"
            placeholder="每行一项"
          />
        </ElFormItem>
        <div v-if="addFieldDef" class="add-field-hint">
          <code class="code-tag">{{ addFieldDef.path }}</code>
          <span v-if="addFieldDef.hint">{{ addFieldDef.hint }}</span>
        </div>
        <p v-if="addFieldInertAnnotation" class="inert-note">
          {{ addFieldInertAnnotation.note }}
        </p>
      </ElForm>
      <template #footer>
        <ElButton @click="addFieldDialog = false">取消</ElButton>
        <ElButton type="primary" @click="onAddField">加上</ElButton>
      </template>
    </ElDialog>

    <!-- ===== [保存草稿] Dialog ===== -->
    <ElDialog
      v-model="saveDraftDialog"
      title="保存参数集草稿"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集">
          <span class="d-val">
            {{ detailNN?.summary.parameterSetCode }} / {{ buffer.governance?.versionCode }}
          </span>
        </ElFormItem>
        <ElFormItem label="变更原因 (必填)">
          <ElInput
            v-model="saveDraftForm.changeReason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert type="warning" :closable="false" show-icon class="d-alert">
          <template #title>草稿保存说明</template>
          保存会用整份草稿覆盖该版本内容（5 个主题全部回传），不保留逐项修改历史。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="saveDraftDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="buffer.saving" @click="onSaveDraftConfirm">
          确认保存
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== Publish Dialog ===== -->
    <ElDialog
      v-model="publishDialog"
      title="发布参数集草稿"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集">
          <span class="d-val">{{ detailNN?.summary.parameterSetCode }}</span>
        </ElFormItem>
        <ElFormItem label="待发布版本">
          <span class="d-val">{{ detailNN?.summary.draftVersionCode || '（无草稿）' }}</span>
        </ElFormItem>
        <ElFormItem label="变更原因 (必填)">
          <ElInput
            v-model="publishForm.changeReason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>发布产生新版本，历史不覆盖</template>
          发布后该版本进入已发布状态。参数集发布是独立权限（aps.parameter.publish），与规则集发布互不代偿。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="publishDialog = false">取消</ElButton>
        <ElButton
          type="primary"
          :loading="actionRunning"
          :disabled="!canPublishAction"
          @click="onPublish"
        >
          确认发布
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== Disable Dialog ===== -->
    <ElDialog
      v-model="disableDialog"
      title="停用已发布参数集"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集">
          <span class="d-val">{{ detailNN?.summary.parameterSetCode }}</span>
        </ElFormItem>
        <ElFormItem label="停用版本">
          {{ detailNN?.summary.currentVersionCode || '（无已发布版本）' }}
        </ElFormItem>
        <ElFormItem label="停用原因 (必填)">
          <ElInput
            v-model="disableForm.reason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert type="warning" :closable="false" show-icon class="d-alert">
          <template #title>停用前请确认</template>
          停用后该版本进入只读状态；如需恢复，可基于此版本新建草稿后重新发布。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="disableDialog = false">取消</ElButton>
        <ElButton
          type="danger"
          :loading="actionRunning"
          :disabled="!canPublishAction"
          @click="onDisable"
        >
          确认停用
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [+ 新建草稿] Dialog ===== -->
    <ElDialog
      v-model="createDraftDialog"
      title="新建参数集草稿"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="参数集">
          <span class="d-val">{{ detailNN?.summary.parameterSetCode }}</span>
        </ElFormItem>
        <ElFormItem label="基于版本">
          <ElSelect
            v-model="createDraftForm.sourceParameterSetVersionId"
            style="width: 100%"
            @change="onSourceVersionChange"
          >
            <ElOption
              v-for="h in forkableVersions"
              :key="h.versionId"
              :label="`${h.versionCode}（${RULE_STATUS_LABELS[h.status]}）`"
              :value="h.versionId"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="新版本码">
          <ElInput v-model="createDraftForm.newVersionCode" placeholder="如 PS-PROD-V2.0" />
        </ElFormItem>
        <ElFormItem label="变更原因 (必填)">
          <ElInput
            v-model="createDraftForm.changeReason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>新草稿会复制所选版本的 5 个主题</template>
          参数集只带走它自己的内容（锁定策略 / 供应参数 / 采购参数 / 求解器策略 / 候选版本守门），
          不涉及规则集 —— 规则集请到「规则集维护」页单独建草稿。
        </ElAlert>
        <!-- fork 前置提示：源版本里的仓库编码在字典里不存在 ⇒ 会被原样继承，发布时才报错 -->
        <ElAlert
          v-if="forkSourceUnknownCodes.length"
          type="warning"
          :closable="false"
          show-icon
          class="d-alert"
        >
          <template #title>
            这个版本里有 {{ forkSourceUnknownCodes.length }} 个仓库编码在仓库字典里找不到
          </template>
          {{ forkSourceUnknownCodes.join('、') }} —— 复制过来的草稿发布时会报「仓库编码不存在」，
          建议先改成字典里的真实仓库编码。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="createDraftDialog = false">取消</ElButton>
        <ElButton
          type="primary"
          :loading="actionRunning"
          :disabled="!forkableVersions.length"
          @click="onCreateDraft"
        >
          确认新建
        </ElButton>
      </template>
    </ElDialog>
  </div>
</template>

<style lang="less" scoped>
.aps-paramsets {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-end;

  .header-actions {
    display: flex;
    gap: 8px;
    align-items: center;
  }
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

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
}

.muted-tag {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
}

.msg-line {
  margin-left: 8px;
  font-size: 12px;
  color: #64748b;
}

.code-tag {
  padding: 2px 6px;
  font-family: 'Courier New', monospace;
  font-size: 12px;
  color: #475569;
  background: #f1f5f9;
  border-radius: 3px;
}

/* 参数名：中文大字 + 原始键小灰字（原始键只做对照，不抢视线） */
.param-name {
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.35;
}

.param-label {
  font-size: 13px;
  color: #1e293b;
}

/* 有说明的参数名：虚线下划线提示可悬停看取值域 */
.param-label-hint {
  border-bottom: 1px dashed #94a3b8;
  cursor: help;
}

.param-key {
  align-self: flex-start;
  padding: 0 4px;
  font-size: 11px;
  color: #94a3b8;
  background: transparent;
}

.loading-tip {
  padding: 30px;
  font-size: 14px;
  color: #94a3b8;
  text-align: center;
}

.kpi-strip {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.kpi-pill {
  display: inline-flex;
  padding: 6px 14px;
  font-size: 12px;
  background: #f8fafc;
  border-radius: 16px;
  align-items: center;
  gap: 6px;

  .pill-label {
    color: #64748b;
  }

  .pill-val {
    font-weight: 600;
    color: #1e293b;
  }
}

.filter-row {
  padding: 8px 12px;
  background: #fff;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.show-inactive-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #4b5563;
  cursor: pointer;
  user-select: none;
}

.content-row {
  display: grid;
  grid-template-columns: 380px 1fr;
  gap: 16px;
}

/* 左：列表 */
.ps-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 720px;
  overflow-y: auto;
}

.ps-card {
  padding: 12px 14px;
  cursor: pointer;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-left: 3px solid #94a3b8;
  border-radius: 4px;
  transition: all 0.2s;

  &:hover {
    border-color: #3b82f6;
  }

  &.active {
    background: #eff6ff;
    border-color: #3b82f6;
    box-shadow: 0 0 0 2px rgb(59 130 246 / 12%);
  }

  &.draft {
    border-left-color: #f59e0b;
  }

  &.published {
    border-left-color: #10b981;
  }

  &.disabled {
    border-left-color: #94a3b8;
  }

  .ps-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 6px;

    .ps-code {
      font-family: 'Courier New', monospace;
      font-weight: 600;
      color: #1e293b;
    }

    .ps-head-tags {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }

    .ps-edit-btn {
      padding: 0 4px;
      height: auto;
    }
  }

  .ps-name {
    margin-bottom: 8px;
    font-size: 13px;
    color: #334155;
  }

  .ps-meta {
    display: flex;
    margin-bottom: 4px;
    font-size: 12px;
    gap: 6px;
    align-items: center;

    .meta-key {
      min-width: 60px;
      color: #94a3b8;
    }

    .meta-val {
      font-weight: 600;
      color: #1e293b;
    }

    .meta-draft {
      margin-left: 4px;
      font-weight: 600;
      color: #f59e0b;
    }
  }

  .ps-reason {
    padding-top: 6px;
    margin-top: 6px;
    font-size: 11px;
    line-height: 1.4;
    color: #64748b;
    border-top: 1px dashed #e2e8f0;
  }
}

/* 右：详情 */
.detail-head-row {
  display: flex;
  padding: 14px 18px;
  margin: -1px -1px 12px;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
  background: #f8fafc;
  border-bottom: 1px solid #e2e8f0;
  border-radius: 4px 4px 0 0;
  justify-content: space-between;
  align-items: center;

  .head-actions {
    display: flex;
    gap: 8px;
  }
}

.detail-tabs {
  margin-top: 12px;
}

.tab-alert {
  margin-bottom: 12px;
}

/* 锚点资源编码（专属录入项） */
.anchor-field {
  margin-top: 12px;
  max-width: 520px;

  .anchor-field-label {
    margin-bottom: 6px;
    font-size: 13px;
    color: #475569;
  }

  .field-note {
    margin: 6px 0 0;
    font-size: 12px;
    line-height: 18px;
    color: #94a3b8;
  }
}

.u22-alert {
  margin-top: 12px;
}

/* 校验结果 */
.validation-alert {
  margin-top: 12px;

  .v-list {
    padding-left: 18px;
    margin: 6px 0 0;
    font-size: 12px;
    color: #475569;
  }

  .v-field {
    color: #94a3b8;
  }

  .v-time {
    margin-top: 6px;
    font-size: 11px;
    color: #94a3b8;
  }
}

/* 参数块 */
.param-block-section {
  margin-bottom: 20px;
  padding: 12px 14px;
  background: #fff;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  .block-header {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 10px;
    padding-bottom: 8px;
    border-bottom: 1px dashed #e2e8f0;

    .block-title {
      margin: 0;
      font-size: 14px;
      font-weight: 600;
      color: #1e293b;
    }

    .block-add-btn {
      margin-left: auto;
    }
  }

  .readonly-val {
    font-family: 'Courier New', monospace;
    color: #64748b;
  }
}

/* 新增字段（空块加第一个参数） */
.add-field-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}

/* 有专属编辑器的空块：一行文字引导（不占插图位） */
.add-field-hintline {
  padding: 10px 12px;
  margin-bottom: 10px;
  font-size: 12px;
  color: #64748b;
  background: #f8fafc;
  border: 1px dashed #e2e8f0;
  border-radius: 6px;
}

.add-field-option {
  display: flex;
  align-items: center;
  gap: 8px;
}

.add-field-hint {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: -6px;
  padding-left: 90px;
  font-size: 12px;
  color: #64748b;
}

.inert-note {
  margin: 8px 0 0;
  padding-left: 90px;
  font-size: 12px;
  line-height: 1.6;
  color: #b45309;
}

.save-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 16px;
  padding: 10px 14px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
}

/* Diff */
.diff-summary {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-bottom: 12px;

  &.meta {
    margin-top: 16px;
  }

  .diff-meta {
    margin-left: 8px;
    font-family: 'Courier New', monospace;
    font-size: 12px;
    color: #64748b;
  }
}

/* 表头提示（生效起 / 生效止 的语义说明） */
.th-hint {
  border-bottom: 1px dashed #94a3b8;
  cursor: help;
}

.diff-old {
  color: #94a3b8;
  text-decoration: line-through;
}

.diff-field {
  display: flex;
  gap: 8px;
  align-items: baseline;
}

.diff-field-cn {
  font-size: 13px;
  color: #1e293b;
}

.diff-note {
  margin: 8px 0 0;
  font-size: 12px;
  color: #94a3b8;
}

.diff-new {
  font-weight: 600;
  color: #059669;
}

/* History */
.history-current {
  font-weight: 700;
  color: #059669;
}

/* Dialog */
.d-val {
  font-family: 'Courier New', monospace;
  color: #1e293b;
}

.d-alert {
  margin-bottom: -10px;
}

.edit-hint {
  margin-left: 8px;
  font-size: 12px;
  color: #64748b;
}
</style>
