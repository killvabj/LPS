<script setup lang="ts">
/**
 * APS V1 4号位 — 规则集（RuleSet）维护（页面 8 / U21 / U22）
 *
 * ⚠️ 2026-10-22 结构性重写：**本页只管规则集**。
 *
 * 冻结模型是三条**平行**链（DDL 08:9025 / 08:9042 / 08:9064 / 08:9081）：
 *
 *   RuleSet ──1:N──> RuleSetVersion          （内容：需求优先级段位 + 换型规则子块）
 *   ParameterSet ──1:N──> ParameterSetVersion （内容：5 主题 JSON）   ← 见「参数集维护」页
 *   StrategyProfile ──1:N──> StrategyProfileVersion
 *
 * 两族**只在 StrategyProfileVersion 上会合**（08:9132-9133 两个外键）。
 * ⇒ 规则集版本里**没有**参数集版本外键，前端原先自造的「规则集 1:1 参数集」模型全部撤除
 *   （原「参数维护」页签 + 双轨保存/双轨发布 + 求解器策略页签全部移除）。
 *
 * 本页内容只有两块，与后端 RuleSetVersion 实体一致：
 *  - 需求优先级段位（`demandPriorityJson`；**草稿可编辑**，其余版本只读 —— 2026-10-22 加编辑器）
 *  - 换型规则子块（`setupTransitionRulesJson`；2026-10-28 起在本页「换型规则」页签内直接增删改，
 *    复用 Setup 两个 Dialog + 两个 store；权限仍走 aps.setup.view / aps.setup.edit）
 *
 * 4号位文档第 14 节约束：
 *  - DRAFT 可编辑；PUBLISHED 不可直接改；ARCHIVED 只读
 *  - 优先级段位只展示段内顺序与名称，不暴露 PriorityScore（U21）
 *  - 发布产生新 Version，历史不覆盖（U22）
 *  - 前端不能直接 UPDATE Rule，所有写操作走 Controller（publish/disable/validate）
 *
 * 权限码：aps.rule.view / aps.rule.edit / aps.rule.publish
 */

import { computed, onActivated, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useRulesStore } from '@/store/modules/aps/rules'
import { useSetupExactStore } from '@/store/modules/aps/setupExact'
import { useSetupDefaultStore } from '@/store/modules/aps/setupDefault'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { formatLocalDateTime, formatUtcDateTime } from '@/utils/datetime'
import {
  ApiError,
  RULE_STATUS_LABELS,
  RULE_STATUS_TAG,
  NO_VERSION,
  NO_VERSION_LABEL,
  RULE_STATUSES_UI,
  setupApi,
  type FieldDiff,
  type RuleStatus,
  type RuleSetSummaryDto,
  type RuleVersionHistoryDto,
  type PrioritySegmentDto,
  type SetupRuleDto,
  type SetupDialogPrefill
} from '@/api/aps-v1'
import { competitionRoundLabel, conditionText, sortFieldText } from '@/api/aps-v1/prioritySegment'
import { toEditableSegments } from '@/api/aps-v1/prioritySegmentEditor'
import PrioritySegmentEditor from './components/PrioritySegmentEditor.vue'
import SetupExactDialog from './components/SetupExactDialog.vue'
import SetupDefaultDialog from './components/SetupDefaultDialog.vue'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElCheckbox,
  ElDescriptions,
  ElDescriptionsItem,
  ElDialog,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
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
  ElTooltip,
  ElMessage,
  ElMessageBox
} from 'element-plus'

const rulesStore = useRulesStore()
const {
  filteredRuleSets,
  statusCounts,
  noVersionCount,
  inactiveCount,
  currentDetail,
  currentDiff,
  diffBaseVersionId,
  diffTargetVersionId,
  lastValidation,
  statusFilter,
  showInactive,
  loading,
  listLoading,
  actionRunning,
  validating,
  saving,
  error,
  canWrite,
  canPublishAction,
  actions,
  forkPermission
} = storeToRefs(rulesStore)

/** Non-null 在 v-if 块内用 */
const detailNN = computed(() => currentDetail.value!)

/** 只保留真正发生变化的字段 —— 后端 `fieldDiffs` 是全字段平铺，未变字段 `isChanged=false` */
const changedFields = computed<FieldDiff[]>(() =>
  (currentDiff.value?.fieldDiffs ?? []).filter((f: FieldDiff) => f.isChanged)
)

/* ===== Tabs ===== */
type TabKey = 'segments' | 'setup' | 'diff' | 'history'
const activeTab = ref<TabKey>('segments')

// ===== ⑦ 换型规则（2026-10-28 用户拍板：并入本页直接增删改） =====
// 数据来自 Setup 两个正式端点（GET /setup-rules/exact|default?ruleSetVersionId=...），
// 不是从 ContentSnapshotJson 反推；写操作复用 Setup 两个 Dialog + 两个 store，
// 不在本页复制一套 CRUD。
// 权限仍走 Setup 三码：查看 aps.setup.view / 增删改 aps.setup.edit（**不是** aps.rule.*）。
// 编辑目标只能是「当前草稿版本」：没有草稿时整块只读，并提示先「新建草稿」。
const apsAuth = useApsAuthStore()
const router = useRouter()
const route = useRoute()

/** 需要有 aps.setup.view 才能读换型规则；无权限时整块降级为说明文字，绝不发请求 */
const canViewSetup = computed<boolean>(() => apsAuth.has('aps.setup.view'))

const setupSummaryLoading = ref(false)
const setupSummaryError = ref<string | null>(null)
/** 已加载的版本 Id（换版本 / 换规则集后重新拉取） */
const setupSummaryVersionId = ref<number | null>(null)
const setupExactRules = ref<SetupRuleDto[]>([])
const setupDefaultRules = ref<SetupRuleDto[]>([])

/** 当前展示的版本里最新的草稿（发布动作发布的就是它） */
const currentDraftVersionId = computed<number | null>(() => {
  const drafts = (detailNN.value?.history ?? []).filter(
    (h: RuleVersionHistoryDto) => h.status === 'DRAFT'
  )
  return drafts.length ? (drafts[drafts.length - 1].versionId ?? null) : null
})

/** 换型规则按「要发布的那一版」看：有草稿看草稿，没有草稿退回当前展示的版本 */
const setupTargetVersionId = computed<number | null>(
  () => currentDraftVersionId.value ?? detailNN.value?.contentVersionId ?? null
)

async function loadSetupSummary(versionId: number): Promise<void> {
  setupSummaryVersionId.value = versionId
  setupSummaryLoading.value = true
  setupSummaryError.value = null
  setupExactRules.value = []
  setupDefaultRules.value = []
  try {
    const [exact, defaults] = await Promise.all([
      setupApi.listExactRules(versionId),
      setupApi.listDefaultRules(versionId)
    ])
    setupExactRules.value = exact
    setupDefaultRules.value = defaults
  } catch (err) {
    setupSummaryError.value =
      err instanceof ApiError ? err.message : ((err as Error)?.message ?? '未知错误')
  } finally {
    setupSummaryLoading.value = false
  }
}

/** 按需加载：同版本且已成功加载过就不重复请求 */
async function ensureSetupSummary(): Promise<void> {
  if (!canViewSetup.value) return
  const versionId = setupTargetVersionId.value
  if (versionId == null) return
  if (setupSummaryVersionId.value === versionId && !setupSummaryError.value) return
  await loadSetupSummary(versionId)
}

/** 页签上的「刷新」：目标版本为空时不动 */
async function refreshSetupSummary(): Promise<void> {
  const versionId = setupTargetVersionId.value
  if (versionId == null) return
  await loadSetupSummary(versionId)
}

/**
 * 改完 ⑦ 后重跑字段级 diff
 *
 *  换型规则存在草稿版本自己的 ContentSnapshotJson 里（后端 SetupRuleService 写子块），
 *  所以增删改 ⑦ 会改变「已发布版 vs 草稿版」的字段 diff —— 但版本对比页签读的是
 *  loadDetail 时算好的旧快照 ⇒ 不重跑就看不到 SetupTransitionRules 这一行，
 *  也就看不到「查看换型规则逐条差异 →」入口。
 *  两端版本本身不会因改内容而变（改的是草稿内容，不是版本号）⇒ 重跑同一对即可。
 */
async function refreshDiff(): Promise<void> {
  const base = diffBaseVersionId.value
  const target = diffTargetVersionId.value
  if (base == null || target == null) return
  await rulesStore.loadDiff(base, target)
}

/** 清空汇总（切规则集 / 换内容版本时用，避免展示上一版的条数） */
function resetSetupSummary(): void {
  setupSummaryVersionId.value = null
  setupExactRules.value = []
  setupDefaultRules.value = []
  setupSummaryError.value = null
  setupSummaryLoading.value = false
}

watch(activeTab, (tab) => {
  if (tab === 'setup') void ensureSetupSummary()
})

const exactStore = useSetupExactStore()
const defaultStore = useSetupDefaultStore()

/** 增删改需要 aps.setup.edit；与 aps.setup.view 分开，跟本页的 aps.rule.* 无关 */
const canEditSetup = computed<boolean>(() => apsAuth.has('aps.setup.edit'))

/** 可写目标 = 当前草稿（历史里最新一条 DRAFT） */
const setupDraftVersionId = computed<number | null>(() => currentDraftVersionId.value)

/** 页签展示的就是草稿这一版、且有 edit 权限，才允许新增/编辑/删除 */
const setupEditable = computed<boolean>(
  () =>
    canEditSetup.value &&
    setupDraftVersionId.value !== null &&
    setupSummaryVersionId.value === setupDraftVersionId.value
)

/**
 * 写操作前把两个 Setup store 对齐到草稿版本：
 *  - Dialog 的 create 模式取 store.selectedRuleSetVersionId 填 ruleSetVersionId；
 *  - store.canEdit 依赖 ruleSetVersions 里能找到该版本且状态为 DRAFT。
 * 所以必须先按本规则集 loadRuleSetVersions 再 select，否则写入会被 store 自己拦下。
 */
async function syncSetupStores(versionId: number): Promise<boolean> {
  const ruleSetId = detailNN.value?.summary.ruleSetId
  const params = ruleSetId ? { ruleSetId } : undefined
  await Promise.all([
    exactStore.loadRuleSetVersions(params),
    defaultStore.loadRuleSetVersions(params)
  ])
  exactStore.selectRuleSetVersion(versionId)
  defaultStore.selectRuleSetVersion(versionId)
  if (!exactStore.canEdit || !defaultStore.canEdit) {
    ElMessage.warning('换型规则只能在草稿（DRAFT）版本上修改')
    return false
  }
  return true
}

/** 增删改的前置检查（权限 → 有草稿 → store 对齐），通过后返回草稿版本 Id */
async function startSetupWrite(): Promise<number | null> {
  if (!canEditSetup.value) {
    ElMessage.warning('当前操作者没有换型规则编辑权限（需权限码 aps.setup.edit）')
    return null
  }
  const versionId = setupDraftVersionId.value
  if (versionId === null) {
    ElMessage.warning('当前没有草稿版本，换型规则只能改草稿；请先点「新建草稿」')
    return null
  }
  if (!(await syncSetupStores(versionId))) return null
  return versionId
}

const exactDialogVisible = ref(false)
const exactDialogMode = ref<'create' | 'edit'>('create')
const editingExactRule = ref<SetupRuleDto | null>(null)

const defaultDialogVisible = ref(false)
const defaultDialogMode = ref<'create' | 'edit'>('create')
const editingDefaultRule = ref<SetupRuleDto | null>(null)
/** 「缺失规则查询」跳转带入的 4 字段预填（部门/大工艺/工序/设备） */
const defaultDialogPrefill = ref<SetupDialogPrefill | null>(null)

/**
 * 深链带回来的**来源版本**（缺失规则查询那一行所属的规则集版本）
 *
 *  2026-10-28（#4 绑定口径）：回填目标必须绑定到「不变 RuleSetVersionId」。
 *  缺失规则那一行指向的通常是**已发布**版本（排产实际生效的那版），
 *  而 ⑦ 只能改草稿 ⇒ 页面要明确说清「来源是哪个版本、保存写进哪个草稿」，
 *  不能让用户以为改的就是那一版。
 */
const deepLinkSourceVersion = ref<{ versionCode: string; status: string } | null>(null)

/** 当前草稿版本号（明示「保存写进哪一版」） */
const currentDraftVersionCode = computed<string>(() => {
  const id = currentDraftVersionId.value
  if (id == null) return ''
  const hit = (detailNN.value?.history ?? []).find((h) => h.versionId === id)
  return hit?.versionCode ?? ''
})

async function openExactCreate(): Promise<void> {
  if ((await startSetupWrite()) === null) return
  exactDialogMode.value = 'create'
  editingExactRule.value = null
  exactDialogVisible.value = true
}

async function openExactEdit(rule: SetupRuleDto): Promise<void> {
  if ((await startSetupWrite()) === null) return
  exactDialogMode.value = 'edit'
  editingExactRule.value = rule
  exactDialogVisible.value = true
}

async function openDefaultCreate(prefill?: SetupDialogPrefill | null): Promise<void> {
  if ((await startSetupWrite()) === null) return
  defaultDialogMode.value = 'create'
  editingDefaultRule.value = null
  defaultDialogPrefill.value = prefill ?? null
  defaultDialogVisible.value = true
}

async function openDefaultEdit(rule: SetupRuleDto): Promise<void> {
  if ((await startSetupWrite()) === null) return
  defaultDialogMode.value = 'edit'
  editingDefaultRule.value = rule
  defaultDialogPrefill.value = null
  defaultDialogVisible.value = true
}

async function handleExactDelete(rule: SetupRuleDto): Promise<void> {
  if ((await startSetupWrite()) === null) return
  let confirmed = false
  try {
    await ElMessageBox.confirm(
      `即将删除明确产品转换规则：${rule.departmentCode} / ${rule.stageCode} / ${rule.operationCode} / ${rule.resourceCode} / ${rule.fromMaterialCode} → ${rule.toMaterialCode}（${rule.setupMinutes} 分钟）。删除后要在本页上方点「发布」重新发布整版才生效。是否继续？`,
      '删除确认',
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

  const ok = await exactStore.deleteRule(rule.id)
  if (ok) {
    ElMessage.success('明确产品转换规则删除成功')
    await refreshSetupSummary()
    await refreshDiff()
  } else if (exactStore.error) {
    ElMessage.error(exactStore.error)
  }
}

async function handleDefaultDelete(rule: SetupRuleDto): Promise<void> {
  if ((await startSetupWrite()) === null) return
  let confirmed = false
  try {
    await ElMessageBox.confirm(
      `即将删除默认换型规则：${rule.departmentCode} / ${rule.stageCode} / ${rule.operationCode} / ${rule.resourceCode}（${rule.setupMinutes} 分钟）。删除后要在本页上方点「发布」重新发布整版才生效。是否继续？`,
      '删除确认',
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

  const ok = await defaultStore.deleteRule(rule.id)
  if (ok) {
    ElMessage.success('默认换型规则删除成功')
    await refreshSetupSummary()
    await refreshDiff()
  } else if (defaultStore.error) {
    ElMessage.error(defaultStore.error)
  }
}

/** Dialog 保存成功：重取两个列表（发布弹窗里的条数也来自它们） */
async function handleSetupSaved(): Promise<void> {
  defaultDialogPrefill.value = null
  await refreshSetupSummary()
  await refreshDiff()
}

/**
 * 「查看换型规则逐条差异」下钻到换型规则级 Diff 页
 *
 * 2026-10-28：该页已从菜单隐藏（meta.hidden），唯一入口就是这个按钮
 * ⇒ 必须自带上下文：所属规则集（页面下拉按它收窄）+ 两端版本 Id（自动选中并直接出对比），
 *    否则用户落到一个"全系统版本大杂烩"的下拉页还要自己重新挑一遍。
 */
function goSetupDiffPage(): void {
  const query: Record<string, string> = {}
  const ruleSetId = currentDetail.value?.summary.ruleSetId
  if (ruleSetId != null) query.ruleSetId = String(ruleSetId)
  if (diffBaseVersionId.value != null) query.versionId = String(diffBaseVersionId.value)
  if (diffTargetVersionId.value != null) query.otherVersionId = String(diffTargetVersionId.value)
  void router.push({ path: '/aps/setup/diff', query })
}

/* ===== 操作 ===== */
/** 选中的规则集没有任何版本时的占位（右侧空态用；避免抛红条 + 外露内部 ID） */
const noVersionPicked = ref<RuleSetSummaryDto | null>(null)

/* ===== 段位编辑器（只对 DRAFT + 有写权限开放） ===== */
const editorSegments = ref<PrioritySegmentDto[]>([])
const segmentsDirty = ref(false)
const segmentsEditable = computed(
  () => !!currentDetail.value && currentDetail.value.summary.status === 'DRAFT' && canWrite.value
)

// 详情每次重取（切规则集 / 保存成功 / 发布后）都以服务端内容为准重建表单
watch(currentDetail, (d) => {
  editorSegments.value = toEditableSegments(d?.prioritySegments ?? [])
  segmentsDirty.value = false
  // 换规则集后换型规则条数属于上一版，必须清掉；当前就在 setup 页签时按新版本重拉
  resetSetupSummary()
  if (activeTab.value === 'setup') void ensureSetupSummary()
})

function onEditorUpdate(value: PrioritySegmentDto[]): void {
  editorSegments.value = value
  segmentsDirty.value = true
}

/** 有未保存修改时拦一下切换 / 校验 / 发布，避免用旧内容操作 */
async function blockIfDirty(action: string): Promise<boolean> {
  if (!segmentsDirty.value) return false
  try {
    await ElMessageBox.confirm(
      `段位还有未保存的修改，现在${action}会用已保存的旧内容。请先「保存段位」。`,
      '有未保存的修改',
      { type: 'warning', confirmButtonText: '去保存', cancelButtonText: '取消' }
    )
    openSaveDraftDialog()
  } catch {
    // 用户选择取消：留在当前页
  }
  return true
}

async function selectRuleSet(ruleSet: RuleSetSummaryDto): Promise<boolean> {
  if (await blockIfDirty('切换')) return false
  noVersionPicked.value = null
  rulesStore.reset()
  activeTab.value = 'segments'
  // 无版本的规则集必然加载不出内容（versions 返回空数组），
  // 与其让用户看一条报错红条，不如直接告诉他「这个还没配版本」
  if (!ruleSet.hasVersion) {
    noVersionPicked.value = ruleSet
    return false
  }
  await rulesStore.loadDetail(ruleSet.ruleSetId)
  return true
}

async function onValidate(): Promise<void> {
  if (await blockIfDirty('校验')) return
  await rulesStore.validateDraft()
}

/* ===== 保存段位 Dialog ===== */
const saveDraftDialog = ref(false)
const saveDraftForm = ref({ changeReason: '' })

function openSaveDraftDialog(): void {
  saveDraftForm.value = { changeReason: currentDetail.value?.contentVersionRemarks ?? '' }
  saveDraftDialog.value = true
}

async function onSaveDraft(): Promise<void> {
  const ok = await rulesStore.saveDraftSegments(
    editorSegments.value,
    saveDraftForm.value.changeReason
  )
  if (ok) {
    saveDraftDialog.value = false
    segmentsDirty.value = false
  }
}

function setFilter(filter: RuleStatus | 'ALL'): void {
  rulesStore.setStatusFilter(filter)
}

/* ===== [+ 新建草稿] Dialog ===== */
const createDraftDialog = ref(false)
const createDraftForm = ref({
  /** 源版本 id（从历史版本里选；后端无 fork 端点 ⇒ 前端 GET 源内容 → POST 新版本） */
  sourceRuleSetVersionId: 0,
  newVersionCode: '',
  changeReason: ''
})

/** 可作为 fork 起点的历史版本（已发布 / 已停用；必须带非空 versionId） */
type ForkableVersion = RuleVersionHistoryDto & { versionId: number }

const forkableVersions = computed<ForkableVersion[]>(() =>
  (detailNN.value?.history ?? []).filter(
    (h): h is ForkableVersion =>
      (h.status === 'PUBLISHED' || h.status === 'DISABLED') && h.versionId != null
  )
)

/**
 * 依据源版本码生成新版本码
 *  - `RS-DEMO-V2` → `RS-DEMO-V3`（末尾数字 +1）
 *  - 无末尾数字（`PS-PROD`）→ 追加 `-D1`
 */
function nextVersionCode(base: string): string {
  const m = /^(.*?)(\d+)$/.exec(base)
  if (m) return `${m[1]}${Number(m[2]) + 1}`
  return `${base}-D1`
}

/** Dialog 打开时初始化表单 */
function openCreateDraftDialog(): void {
  if (!detailNN.value) return
  const source = forkableVersions.value[0]
  const sourceId = source?.versionId ?? 0
  createDraftForm.value = {
    sourceRuleSetVersionId: sourceId,
    newVersionCode: nextVersionCode(source?.versionCode ?? 'RS-DRAFT'),
    changeReason: ''
  }
  createDraftDialog.value = true
}

/** 切换源版本时同步推荐新版本码（用户手改过就不覆盖） */
function onSourceVersionChange(id: number): void {
  createDraftForm.value.sourceRuleSetVersionId = id
  const src = forkableVersions.value.find((h) => h.versionId === id)
  if (src) createDraftForm.value.newVersionCode = nextVersionCode(src.versionCode)
}

async function onCreateDraft(): Promise<void> {
  const reason = createDraftForm.value.changeReason.trim()
  if (!reason) {
    rulesStore.handleError(new Error('请填写变更原因'))
    return
  }
  const result = await rulesStore.forkDraft(
    createDraftForm.value.sourceRuleSetVersionId,
    createDraftForm.value.newVersionCode,
    reason
  )
  if (result) {
    createDraftDialog.value = false
    activeTab.value = 'segments'
  }
}

/* ===== Publish Dialog ===== */
const publishDialog = ref(false)
const publishForm = ref({ changeReason: '' })

/**
 * 整版发布前的两个确认勾选（0号位 2026-10-26 文档要求）。
 * 只存在于本次弹窗内，不落库、不进快照 —— 纯「你看过这两块内容」的确认动作。
 */
const confirmPriority = ref(false)
const confirmSetup = ref(false)

const canConfirmPublish = computed<boolean>(
  () => canPublishAction.value && confirmPriority.value && confirmSetup.value
)

/** ① 需求优先级段位条数（发布弹窗内展示的核对依据） */
const prioritySegmentCount = computed<number>(() => detailNN.value?.prioritySegments.length ?? 0)

/* ===== ⑦ 每条规则的「最近修改信息」 =====
 * SetupTransitionRule 落库用 DateTime.Now（服务器本地墙钟）⇒ 按字面本地直显，
 * 不做 UTC→本地位移（0号位 2026-10-13「时间戳一律本地，禁 UTC」+ 3号位 10-28 回执正式口径）。 */
function setupRuleModifiedText(rule: SetupRuleDto): string {
  const t = rule.updatedAt ?? rule.createdAt
  if (!t) return '—'
  return `${rule.updatedAt ? '更新' : '创建'}于 ${formatLocalDateTime(t)}`
}

function setupRuleModifiedBy(rule: SetupRuleDto): string {
  return rule.updatedBy || rule.createdBy || '—'
}

async function openPublishDialog(): Promise<void> {
  if (await blockIfDirty('发布')) return
  publishForm.value = { changeReason: '' }
  confirmPriority.value = false
  confirmSetup.value = false
  publishDialog.value = true
  // 打开弹窗就取一次 ⑦ 的真实条数，勾选不是空勾
  await ensureSetupSummary()
}

async function onPublish(): Promise<void> {
  if (!detailNN.value) return
  if (!canConfirmPublish.value) return
  const reason = publishForm.value.changeReason.trim()
  if (!reason) {
    rulesStore.handleError(new Error('请填写变更原因'))
    return
  }
  const ok = await rulesStore.publish(detailNN.value.summary.ruleSetId, reason)
  if (ok) publishDialog.value = false
}

/* ===== ⑦ 换型规则子块的结构化摘要（版本对比页签用） ===== */
/**
 * 后端的 fieldDiff 只给 SetupTransitionRules 子块的整串 JSON（CompareField 原文），
 * 直接铺在表格里既不可读也容易把用户吓到。这里只做「数条数」的只读解析：
 *  - 空值 → 0 条
 *  - 不是数组 / 有未知 RuleType → 返回 null（如实显示「无法识别」，不回退整串 JSON）
 *  - IsActive=false 的行不计入（与运行时投影口径一致）
 */
interface SetupBlockCounts {
  exact: number
  default: number
  total: number
}

function parseSetupBlockCounts(raw: string | null | undefined): SetupBlockCounts | null {
  if (!raw || !raw.trim()) return { exact: 0, default: 0, total: 0 }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return null
    let exact = 0
    let def = 0
    for (const item of parsed) {
      if (!item || typeof item !== 'object') return null
      const rule = item as { RuleType?: unknown; IsActive?: unknown }
      if (rule.IsActive === false) continue
      const type = String(rule.RuleType ?? '').toUpperCase()
      if (type === 'EXACT') exact += 1
      else if (type === 'DEFAULT') def += 1
      else return null
    }
    return { exact, default: def, total: exact + def }
  } catch {
    return null
  }
}

/** 版本对比里是否包含 ⑦ 块的变更（决定要不要引导去逐条对比页） */
const hasSetupBlockChange = computed<boolean>(() =>
  changedFields.value.some((f: FieldDiff) => f.fieldName === 'SetupTransitionRules')
)

/** 对比表格行：⑦ 块附带「按类型计数」的解析结果，其余字段原样 */
type DiffRow = FieldDiff & {
  setupCounts?: { source: SetupBlockCounts | null; target: SetupBlockCounts | null }
}

const diffRows = computed<DiffRow[]>(() =>
  changedFields.value.map((f: FieldDiff) =>
    f.fieldName === 'SetupTransitionRules'
      ? {
          ...f,
          setupCounts: {
            source: parseSetupBlockCounts(f.sourceValue),
            target: parseSetupBlockCounts(f.targetValue)
          }
        }
      : f
  )
)

/** ⑦ 块计数文案；null（无法识别）由模板单独处理 */
function setupCountsText(counts: SetupBlockCounts | null | undefined): string {
  if (!counts) return ''
  return `明确产品转换 ${counts.exact} 条 / 默认换型 ${counts.default} 条（共 ${counts.total} 条）`
}

/* ===== [+ 新建规则集] Dialog（主表；2026-10-22 3号位 补 POST /rule-set 后才有入口） ===== */
const createRuleSetDialog = ref(false)
const createRuleSetForm = ref({ ruleSetCode: '', ruleSetName: '', description: '' })

function openCreateRuleSetDialog(): void {
  createRuleSetForm.value = { ruleSetCode: '', ruleSetName: '', description: '' }
  createRuleSetDialog.value = true
}

async function onCreateRuleSet(): Promise<void> {
  const id = await rulesStore.createRuleSet({
    ruleSetCode: createRuleSetForm.value.ruleSetCode,
    ruleSetName: createRuleSetForm.value.ruleSetName,
    description: createRuleSetForm.value.description
  })
  if (id == null) return
  createRuleSetDialog.value = false
  noVersionPicked.value = null
  activeTab.value = 'segments'
  await rulesStore.loadDetail(id)
}

/* ===== [编辑资料] Dialog（PUT /rule-set/{id}；编码只读、启停只关主表） ===== */
const editRuleSetDialog = ref(false)
const editRuleSetForm = ref({
  ruleSetCode: '',
  ruleSetName: '',
  description: '',
  isActive: true
})

function openEditRuleSetDialog(): void {
  const s = detailNN.value?.summary
  if (!s) return
  editRuleSetForm.value = {
    ruleSetCode: s.ruleSetCode,
    ruleSetName: s.ruleSetName,
    description: s.description ?? '',
    // 主表字段由列表行回填；极端情况下（列表未加载）按启用兜底，与后端默认一致
    isActive: s.isActive ?? true
  }
  editRuleSetDialog.value = true
}

async function onUpdateRuleSet(): Promise<void> {
  const s = detailNN.value?.summary
  if (!s) return
  const ok = await rulesStore.updateRuleSet({
    ruleSetId: s.ruleSetId,
    ruleSetCode: editRuleSetForm.value.ruleSetCode,
    ruleSetName: editRuleSetForm.value.ruleSetName,
    description: editRuleSetForm.value.description,
    isActive: editRuleSetForm.value.isActive
  })
  if (ok) editRuleSetDialog.value = false
}

/* ===== Disable Dialog ===== */
const disableDialog = ref(false)
const disableForm = ref({ reason: '' })

function openDisableDialog(): void {
  disableForm.value = { reason: '' }
  disableDialog.value = true
}

async function onDisable(): Promise<void> {
  if (!detailNN.value) return
  const reason = disableForm.value.reason.trim()
  if (!reason) {
    rulesStore.handleError(new Error('请填写停用原因'))
    return
  }
  const ok = await rulesStore.disable(detailNN.value.summary.ruleSetId, reason)
  if (ok) disableDialog.value = false
}

/* ===== 物理删除（最窄口径：无任何已发布版本才可删，连带删草稿） ===== */
/** 详情版本历史里是否存在 PUBLISHED —— 有则前端提前拦住，不把必然 400 的请求发出去 */
const detailHasPublished = computed<boolean>(() =>
  (detailNN.value?.history ?? []).some((h: RuleVersionHistoryDto) => h.status === 'PUBLISHED')
)

/**
 * 删除规则集整族（硬删）。
 * - 主表行 + 全部草稿一起删；含 PUBLISHED 版本后端 400，stub 里提前禁用（提示以后端原文为准）
 * - 被策略包/排产运行引用时后端也 400 —— 引用关系前端拿不到，不预判、不翻译
 */
async function onDeleteRuleSet(ruleSetId: number, ruleSetName: string): Promise<void> {
  let confirmed = false
  try {
    await ElMessageBox.confirm(
      `即将永久删除规则集「${ruleSetName}」及其全部未发布草稿版本，删除后无法恢复。已发布过版本或仍被策略包引用的规则集不能删除。是否继续？`,
      '删除规则集',
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
  if ((await rulesStore.removeRuleSet(ruleSetId)) === true) {
    ElMessage.success('规则集已删除')
    noVersionPicked.value = null
  } else if (rulesStore.error) {
    // 后端原文由 store 的 handleError 落进 error（含 400 原因：已发布/被引用）
    ElMessage.error(rulesStore.error)
  }
}

/* ===== 深链：「缺失规则查询 → 去补默认换型规则」 ===== */
/** 已处理过的深链 URL（onMounted / onActivated 会各跑一次，防重复弹窗） */
let lastDeepLinkKey = ''
/**
 * 2026-10-28：两个 Setup 维护页已从菜单隐藏（路由保留），「默认换型规则」的维护入口
 * 并入本页「换型规则」页签 ⇒ 缺失规则查询的出口改到这里：
 * 带 4 字段（部门/大工艺/工序/设备）预填，直接打开「默认换型规则」弹窗。
 * ⚠️ 本路由要 aps.rule.edit / aps.rule.publish（守卫 403），进不来的角色由
 *    缺失规则查询页改跳旧的 /aps/setup/default（那边只有 aps.setup.view）。
 */
async function applySetupDeepLink(): Promise<void> {
  const q = route.query
  if (q.tab !== 'setup' || !q.operationCode) {
    lastDeepLinkKey = ''
    return
  }
  // onMounted 与 onActivated 首次都会跑一遍 ⇒ 同一个深链只处理一次
  if (route.fullPath === lastDeepLinkKey) return
  lastDeepLinkKey = route.fullPath
  const prefill: SetupDialogPrefill = {
    departmentCode: String(q.departmentCode ?? ''),
    stageCode: String(q.stageCode ?? ''),
    operationCode: String(q.operationCode),
    resourceCode: String(q.resourceCode ?? '')
  }
  const ruleSetCode = typeof q.ruleSetCode === 'string' ? q.ruleSetCode : ''
  // 来源版本（缺失规则那一行所属的规则集版本）——用于明示「来源版本 vs 写入草稿」
  const sourceVersionId = Number(q.ruleSetVersionId ?? '')
  // ⚠️ **不要在这里清参**（router.replace 去 query）：layout 的 <component :key="route.fullPath" />
  //    (AppView.vue:30) 会因 fullPath 变化**重建整个页面组件** ⇒ 正在跑的深链连同弹窗一起被销毁。
  //    深链参数留在 URL 上（刷新后仍回到同一处，这是深链的正常行为）。
  if (rulesStore.ruleSets.length === 0) await rulesStore.loadList()
  const target = ruleSetCode
    ? rulesStore.ruleSets.find((r) => r.ruleSetCode === ruleSetCode)
    : undefined
  if (!target) return
  if (!(await selectRuleSet(target))) return
  activeTab.value = 'setup'
  await ensureSetupSummary()
  // 来源版本说明（#4）：找到就显示版本号与状态；找不到（历史数据/版本已删）就不显示，不编造
  const src = Number.isFinite(sourceVersionId)
    ? (detailNN.value?.history ?? []).find((h) => h.versionId === sourceVersionId)
    : undefined
  deepLinkSourceVersion.value = src ? { versionCode: src.versionCode, status: src.status } : null
  await openDefaultCreate(prefill)
}

onMounted(async () => {
  await rulesStore.loadList()
  await applySetupDeepLink()
})

// 本页被 keep-alive 缓存（无 noCache）⇒ 再次从别处带参进来时走 onActivated，不是 onMounted
onActivated(() => {
  void applySetupDeepLink()
})
</script>

<template>
  <div class="aps-rules">
    <!-- ===== 顶部（无大标题页头：页面名由菜单/面包屑给出） ===== -->
    <div class="page-header">
      <div class="header-actions">
        <!-- [+ 新建规则集]：建主表壳并自动跟建一个空白 DRAFT（3号位 Q2 两步） -->
        <ElTooltip
          :content="canWrite ? '新建规则集（编码创建后不可修改）' : '当前账号无编辑权限'"
          placement="bottom"
        >
          <ElButton :disabled="!canWrite" @click="openCreateRuleSetDialog">
            <Icon icon="vi-mdi:plus-box-outline" /> 新建规则集
          </ElButton>
        </ElTooltip>
        <!-- [+ 新建草稿]：基于已发布/已停用版本 fork 出新草稿 -->
        <ElTooltip
          v-if="currentDetail"
          :content="
            forkPermission.allowed ? '基于已有版本新建草稿版本' : (forkPermission.reason ?? '')
          "
          placement="bottom"
        >
          <ElButton
            type="primary"
            :disabled="!actions.canCreateDraft || !forkPermission.allowed"
            @click="openCreateDraftDialog"
          >
            <Icon icon="vi-mdi:file-plus-outline" /> 新建草稿
          </ElButton>
        </ElTooltip>
      </div>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="error" />

    <!-- ===== KPI 横条（UI 流程 3 态子集；后端 6 态为 DDL 冻结但前端无 submit/approve/archive 入口） ===== -->
    <div class="kpi-strip">
      <div class="kpi-pill">
        <span class="pill-label">规则集总数</span>
        <span class="pill-val">{{ rulesStore.ruleSets.length }}</span>
      </div>
      <div v-for="key in RULE_STATUSES_UI" :key="key" class="kpi-pill">
        <span class="pill-label">{{ RULE_STATUS_LABELS[key] }}</span>
        <span class="pill-val">{{ statusCounts[key] ?? 0 }}</span>
      </div>
      <!-- 「未配版本」是 6 态之外的正交计数：这些规则集一条版本都没有，点开必然加载不出内容 -->
      <div class="kpi-pill">
        <span class="pill-label">{{ NO_VERSION_LABEL }}</span>
        <span class="pill-val">{{ noVersionCount }}</span>
      </div>
    </div>

    <!-- ===== 状态过滤（UI 流程 3 态子集；与 KPI 一致） ===== -->
    <div class="filter-row">
      <ElRadioGroup v-model="statusFilter" @change="setFilter">
        <ElRadioButton label="ALL">全部</ElRadioButton>
        <ElRadioButton v-for="key in RULE_STATUSES_UI" :key="key" :label="key">
          {{ RULE_STATUS_LABELS[key] }}
        </ElRadioButton>
        <ElRadioButton :label="NO_VERSION">{{ NO_VERSION_LABEL }}</ElRadioButton>
      </ElRadioGroup>
      <ElTooltip content="停用的规则集默认不显示；勾选后才能看到并重新启用它们" placement="top">
        <label class="show-inactive-toggle">
          <ElSwitch v-model="showInactive" size="small" />
          <span>显示已停用（{{ inactiveCount }}）</span>
        </label>
      </ElTooltip>
    </div>

    <div class="content-row">
      <!-- ===== 左：规则集列表 ===== -->
      <ElCard class="panel left-panel">
        <template #header>
          <div class="panel-header">
            <span>规则集列表</span>
            <ElTag size="small" effect="plain">{{ filteredRuleSets.length }}</ElTag>
          </div>
        </template>
        <div v-if="!filteredRuleSets.length && !listLoading" class="panel-empty">
          <ElEmpty description="无符合条件的规则集" />
        </div>
        <div v-else class="ruleset-list">
          <div
            v-for="r in filteredRuleSets"
            :key="r.ruleSetId"
            class="ruleset-card"
            :class="{
              active: detailNN?.summary.ruleSetId === r.ruleSetId,
              draft: r.status === 'DRAFT',
              published: r.status === 'PUBLISHED',
              disabled: r.status === 'DISABLED'
            }"
            @click="selectRuleSet(r)"
          >
            <div class="rs-head">
              <span class="rs-code">{{ r.ruleSetCode }}</span>
              <span class="rs-tags">
                <!-- 主表停用（Q4：只关主表，不级联版本）——与右边的版本状态是两件事 -->
                <ElTag v-if="!r.isActive" size="small" effect="plain">主表停用</ElTag>
                <ElTag
                  :type="r.hasVersion ? RULE_STATUS_TAG[r.status] : 'info'"
                  size="small"
                  effect="dark"
                >
                  {{ r.hasVersion ? RULE_STATUS_LABELS[r.status] : NO_VERSION_LABEL }}
                </ElTag>
              </span>
            </div>
            <div class="rs-name">{{ r.ruleSetName }}</div>
            <div class="rs-meta">
              <span class="meta-key">最新已发布版本</span>
              <span class="meta-val">{{ r.currentVersionCode || '—' }}</span>
              <span v-if="r.draftVersionCode" class="meta-draft">
                / 草稿 {{ r.draftVersionCode }}
              </span>
            </div>
            <div v-if="r.lastChangeReason" class="rs-reason">
              {{ r.lastChangeReason }}
            </div>
          </div>
        </div>
      </ElCard>

      <!-- ===== 右：详情 ===== -->
      <ElCard class="panel right-panel">
        <template v-if="noVersionPicked">
          <ElEmpty
            :description="`「${noVersionPicked.ruleSetName}」还没有配任何版本，现在没有内容可看。`"
          >
            <ElButton
              v-if="canWrite"
              type="danger"
              size="small"
              plain
              @click="onDeleteRuleSet(noVersionPicked.ruleSetId, noVersionPicked.ruleSetName)"
            >
              <Icon icon="vi-mdi:trash-can-outline" /> 删除规则集
            </ElButton>
          </ElEmpty>
        </template>

        <template v-else-if="!currentDetail">
          <ElEmpty description="从左侧选择一个规则集" />
        </template>

        <div v-else>
          <!-- ===== Header 条 ===== -->
          <div class="detail-head-row">
            <div>
              <ElTag :type="RULE_STATUS_TAG[detailNN.summary.status]" effect="dark" size="small">
                {{ RULE_STATUS_LABELS[detailNN.summary.status] }}
              </ElTag>
              {{ detailNN.summary.ruleSetCode }}
              <span class="muted-tag">{{
                detailNN.summary.currentVersionCode || '（无已发布版本）'
              }}</span>
              <span v-if="detailNN.summary.draftVersionCode" class="muted-tag">
                → 草稿 {{ detailNN.summary.draftVersionCode }}
              </span>
            </div>
            <div class="head-actions">
              <ElButton v-if="canWrite" size="small" @click="openEditRuleSetDialog">
                <Icon icon="vi-mdi:pencil-outline" /> 编辑资料
              </ElButton>
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
                content="该规则集已有已发布版本，不能物理删除；如不再使用请「停用」已发布版本"
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
                      onDeleteRuleSet(detailNN.summary.ruleSetId, detailNN.summary.ruleSetName)
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
            <ElDescriptionsItem label="规则集">
              {{ detailNN.summary.ruleSetName }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="排程域">{{ detailNN.domainKey }}</ElDescriptionsItem>
            <ElDescriptionsItem label="状态">
              <ElTag :type="RULE_STATUS_TAG[detailNN.summary.status]" size="small">
                {{ RULE_STATUS_LABELS[detailNN.summary.status] }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="主表启用">
              <ElTag
                v-if="detailNN.summary.isActive != null"
                :type="detailNN.summary.isActive ? 'success' : 'info'"
                size="small"
              >
                {{ detailNN.summary.isActive ? '启用' : '已停用' }}
              </ElTag>
              <span v-else class="muted">—</span>
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
            <ElDescriptionsItem label="本页内容来自版本">
              <span class="code-tag">{{ detailNN.contentVersionCode || '—' }}</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="最近变更" :span="3">
              {{ detailNN.summary.lastChangeReason || '（无）' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem v-if="!canPublishAction && canWrite" label="权限" :span="3">
              <ElTag type="warning" size="small">当前账号无发布/停用权限</ElTag>
              <span class="msg-line">如需发布或停用，请联系系统管理员授予相应权限</span>
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
              <li v-for="(e, i) in lastValidation.errors" :key="`e${i}`">
                {{ e.message }}
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
            <!-- 1. 优先级段位（草稿可编辑；其余版本只读） -->
            <ElTabPane label="优先级段位" name="segments">
              <ElAlert
                v-if="segmentsEditable"
                type="info"
                :closable="false"
                show-icon
                class="tab-alert"
              >
                <template #title>改完要点「保存段位」才会生效</template>
                保存后仍是草稿，需要再走「校验草稿 → 发布」；未发布的修改不影响实际排产。
              </ElAlert>
              <ElAlert v-else type="info" :closable="false" show-icon class="tab-alert">
                <template #title>当前为只读</template>
                {{
                  detailNN.summary.status === 'DRAFT'
                    ? '当前账号没有编辑权限，只能查看。'
                    : '只有草稿版本可以修改段位；需要调整请先「新建草稿」。'
                }}
              </ElAlert>

              <div v-if="segmentsEditable" class="seg-toolbar">
                <ElButton
                  type="primary"
                  size="small"
                  :loading="saving"
                  :disabled="!segmentsDirty"
                  @click="openSaveDraftDialog"
                >
                  保存段位
                </ElButton>
                <span class="muted">{{ segmentsDirty ? '有未保存的修改' : '已保存' }}</span>
              </div>

              <PrioritySegmentEditor
                v-if="segmentsEditable"
                :model-value="editorSegments"
                @update:model-value="onEditorUpdate"
              />

              <template v-else>
                <ElEmpty
                  v-if="!detailNN.prioritySegments.length"
                  description="该版本没有配置需求优先级段位"
                  :image-size="70"
                />
                <ElTable v-else :data="detailNN.prioritySegments" size="small" border>
                  <ElTableColumn label="顺序" width="70" align="center">
                    <template #default="{ row }">
                      <span class="order-circle">{{ row.orderInSegment }}</span>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn prop="segmentName" label="段位名" min-width="160" />
                  <ElTableColumn label="启用" width="80" align="center">
                    <template #default="{ row }">
                      <ElTag v-if="row.isEnabled" size="small" type="success">启用</ElTag>
                      <ElTag v-else size="small" type="info">停用</ElTag>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="竞争轮次" width="150">
                    <template #default="{ row }">
                      {{ competitionRoundLabel(row.competitionRound) }}
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="命中条件" min-width="240">
                    <template #default="{ row }">
                      <span v-if="!row.matchConditions.length" class="muted"
                        >（无条件，命中全部）</span
                      >
                      <ul v-else class="cond-list">
                        <li v-for="(c, i) in row.matchConditions" :key="i">
                          {{ conditionText(c) }}
                        </li>
                      </ul>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="段内排序" min-width="180">
                    <template #default="{ row }">
                      <span v-if="!row.sortFields.length" class="muted">（无）</span>
                      <ul v-else class="cond-list">
                        <li v-for="(f, i) in row.sortFields" :key="i">{{ sortFieldText(f) }}</li>
                      </ul>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="稳定排序键" min-width="160">
                    <template #default="{ row }">
                      <span v-if="!row.stableTieBreakFields.length" class="muted">（无）</span>
                      <span v-else>
                        <ElTag
                          v-for="f in row.stableTieBreakFields"
                          :key="f"
                          size="small"
                          effect="plain"
                          style="margin-right: 4px"
                        >
                          {{ f }}
                        </ElTag>
                      </span>
                    </template>
                  </ElTableColumn>
                </ElTable>
              </template>
            </ElTabPane>

            <!-- 2. 换型规则（本页直接增删改；写操作复用 Setup 两个 Dialog + 两个 store） -->
            <ElTabPane label="换型规则" name="setup">
              <ElAlert
                v-if="!canViewSetup"
                type="info"
                :closable="false"
                show-icon
                class="tab-alert"
              >
                <template #title>当前账号看不到换型规则</template>
                查看换型规则需要「换型规则查看」权限（aps.setup.view）。如需查看请联系系统管理员授权。
              </ElAlert>

              <template v-else>
                <ElEmpty
                  v-if="setupTargetVersionId == null"
                  description="当前规则集还没有可查看的版本"
                  :image-size="70"
                />

                <template v-else>
                  <ElAlert
                    v-if="!setupEditable"
                    type="info"
                    :closable="false"
                    show-icon
                    class="tab-alert"
                  >
                    <template #title>当前只读</template>
                    <template v-if="setupDraftVersionId == null">
                      现在看到的是已发布版本，换型规则只能改草稿。要修改请先在页面上方「新建草稿」。
                    </template>
                    <template v-else>
                      当前账号没有换型规则编辑权限（需权限码 aps.setup.edit），只能查看。
                    </template>
                  </ElAlert>

                  <ElAlert type="info" :closable="false" show-icon class="tab-alert">
                    <template #title>换型规则与需求优先级段位一起整版生效</template>
                    这里改的是当前草稿，改完要回页面上方「发布」才生效。命中顺序：明确产品转换 →
                    当前工序+设备默认规则 → 0 分钟兜底；同一产品连续生产按 0 分钟算。
                  </ElAlert>

                  <!-- 来源版本说明（#4：从「缺失规则查询」回填时，明示来源版本与写入目标） -->
                  <ElAlert
                    v-if="deepLinkSourceVersion"
                    type="warning"
                    :closable="false"
                    show-icon
                    class="tab-alert"
                  >
                    <template #title>
                      这条缺失规则来自版本 {{ deepLinkSourceVersion.versionCode }}（{{
                        RULE_STATUS_LABELS[deepLinkSourceVersion.status] ??
                        deepLinkSourceVersion.status
                      }}）
                    </template>
                    <template v-if="currentDraftVersionCode">
                      保存会写进当前草稿 {{ currentDraftVersionCode }}，发布后才生效。
                    </template>
                    <template v-else>
                      这个规则集现在没有草稿版本，请先在页面上方「新建草稿」，否则保存不了。
                    </template>
                  </ElAlert>

                  <ElAlert
                    v-if="setupSummaryError"
                    type="error"
                    :closable="false"
                    show-icon
                    :title="`换型规则读取失败：${setupSummaryError}`"
                  />

                  <template v-else>
                    <!-- 明确产品转换（EXACT） -->
                    <div class="setup-block">
                      <div class="seg-toolbar">
                        <span class="setup-block-title">明确产品转换</span>
                        <span class="muted">共 {{ setupExactRules.length }} 条 · 优先级最高</span>
                        <span class="toolbar-spacer"></span>
                        <ElButton
                          size="small"
                          :loading="setupSummaryLoading"
                          @click="refreshSetupSummary"
                        >
                          <Icon icon="vi-mdi:refresh" /> 刷新
                        </ElButton>
                        <ElButton
                          size="small"
                          type="primary"
                          :disabled="!setupEditable"
                          @click="openExactCreate"
                        >
                          新增明确产品转换
                        </ElButton>
                      </div>
                      <ElTable
                        v-loading="setupSummaryLoading"
                        :data="setupExactRules"
                        size="small"
                        border
                        empty-text="这一版还没有明确产品转换规则"
                      >
                        <ElTableColumn prop="departmentCode" label="部门" width="110" />
                        <ElTableColumn prop="stageCode" label="大工艺" width="110" />
                        <ElTableColumn prop="operationCode" label="当前工序" width="120" />
                        <ElTableColumn prop="resourceCode" label="设备" width="130" />
                        <ElTableColumn label="前产品 → 后产品" min-width="240">
                          <template #default="{ row }">
                            {{ row.fromMaterialCode }} → {{ row.toMaterialCode }}
                          </template>
                        </ElTableColumn>
                        <ElTableColumn
                          prop="setupMinutes"
                          label="换型分钟"
                          width="100"
                          align="right"
                        />
                        <ElTableColumn label="最近修改" width="180">
                          <template #default="{ row }">
                            <div>{{ setupRuleModifiedText(row) }}</div>
                            <div class="muted">{{ setupRuleModifiedBy(row) }}</div>
                          </template>
                        </ElTableColumn>
                        <ElTableColumn v-if="canEditSetup" label="操作" width="130" fixed="right">
                          <template #default="{ row }">
                            <ElButton
                              type="primary"
                              size="small"
                              link
                              :disabled="!setupEditable"
                              @click="openExactEdit(row)"
                            >
                              编辑
                            </ElButton>
                            <ElButton
                              type="danger"
                              size="small"
                              link
                              :disabled="!setupEditable"
                              @click="handleExactDelete(row)"
                            >
                              删除
                            </ElButton>
                          </template>
                        </ElTableColumn>
                      </ElTable>
                    </div>

                    <!-- 默认换型规则（DEFAULT） -->
                    <div class="setup-block">
                      <div class="seg-toolbar">
                        <span class="setup-block-title">默认换型规则</span>
                        <span class="muted">
                          共 {{ setupDefaultRules.length }} 条 · 明确规则未命中时生效
                        </span>
                        <span class="toolbar-spacer"></span>
                        <ElButton
                          size="small"
                          type="primary"
                          :disabled="!setupEditable"
                          @click="openDefaultCreate()"
                        >
                          新增默认换型规则
                        </ElButton>
                      </div>
                      <ElTable
                        v-loading="setupSummaryLoading"
                        :data="setupDefaultRules"
                        size="small"
                        border
                        empty-text="这一版还没有默认换型规则（命中不到明确/默认规则时按 0 分钟算）"
                      >
                        <ElTableColumn prop="departmentCode" label="部门" width="110" />
                        <ElTableColumn prop="stageCode" label="大工艺" width="110" />
                        <ElTableColumn prop="operationCode" label="当前工序" width="120" />
                        <ElTableColumn prop="resourceCode" label="设备" width="130" />
                        <ElTableColumn
                          prop="setupMinutes"
                          label="换型分钟"
                          width="100"
                          align="right"
                        />
                        <ElTableColumn label="最近修改" width="180">
                          <template #default="{ row }">
                            <div>{{ setupRuleModifiedText(row) }}</div>
                            <div class="muted">{{ setupRuleModifiedBy(row) }}</div>
                          </template>
                        </ElTableColumn>
                        <ElTableColumn v-if="canEditSetup" label="操作" width="130" fixed="right">
                          <template #default="{ row }">
                            <ElButton
                              type="primary"
                              size="small"
                              link
                              :disabled="!setupEditable"
                              @click="openDefaultEdit(row)"
                            >
                              编辑
                            </ElButton>
                            <ElButton
                              type="danger"
                              size="small"
                              link
                              :disabled="!setupEditable"
                              @click="handleDefaultDelete(row)"
                            >
                              删除
                            </ElButton>
                          </template>
                        </ElTableColumn>
                      </ElTable>
                    </div>
                  </template>
                </template>
              </template>
            </ElTabPane>

            <!-- 3. Diff -->
            <ElTabPane label="版本对比" name="diff">
              <!-- `fieldDiffs` 是后端**全字段平铺**（未变字段 isChanged=false），
                   只有 isChanged 的行才是差异；后端不返 added/modified/removed 计数
                   （见 types/rule.ts VersionDiffResultDto 注释），故此处只数「变更字段数」，
                   不自造「新增/删除」分类。 -->
              <template v-if="!currentDiff || changedFields.length === 0">
                <ElEmpty
                  :description="
                    currentDiff
                      ? '两版本无字段差异'
                      : '暂无可对比的两个版本（需存在已发布版本 + 另一个版本）'
                  "
                />
              </template>
              <template v-else>
                <div class="diff-summary">
                  <ElTag type="primary" size="small"> {{ changedFields.length }} 个字段变更 </ElTag>
                  <span class="diff-meta">
                    {{ currentDiff.sourceVersionCode }} → {{ currentDiff.targetVersionCode }}
                  </span>
                  <ElButton
                    v-if="hasSetupBlockChange"
                    size="small"
                    type="primary"
                    link
                    @click="goSetupDiffPage"
                  >
                    查看换型规则逐条差异 →
                  </ElButton>
                </div>
                <ElTable :data="diffRows" size="small" border>
                  <ElTableColumn label="字段" min-width="200">
                    <template #default="{ row }">
                      <div class="diff-field">
                        <span class="diff-field-cn">{{ row.fieldDisplayName }}</span>
                        <code class="code-tag">{{ row.fieldName }}</code>
                      </div>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="源版本值" min-width="180">
                    <template #default="{ row }">
                      <template v-if="row.fieldName === 'SetupTransitionRules'">
                        <span v-if="row.setupCounts.source" class="diff-old">
                          {{ setupCountsText(row.setupCounts.source) }}
                        </span>
                        <span v-else class="muted">内容无法识别（请到「规则版本对比」页查看）</span>
                      </template>
                      <span v-else class="diff-old">{{ row.sourceValue ?? '（无）' }}</span>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="目标版本值" min-width="180">
                    <template #default="{ row }">
                      <template v-if="row.fieldName === 'SetupTransitionRules'">
                        <span v-if="row.setupCounts.target" class="diff-new">
                          {{ setupCountsText(row.setupCounts.target) }}
                        </span>
                        <span v-else class="muted">内容无法识别（请到「规则版本对比」页查看）</span>
                      </template>
                      <span v-else class="diff-new">{{ row.targetValue ?? '（无）' }}</span>
                    </template>
                  </ElTableColumn>
                </ElTable>
                <p class="diff-note">
                  对比对象：{{ currentDiff.entityType }} ·
                  {{ formatUtcDateTime(currentDiff.comparedAt) }}
                </p>
              </template>
            </ElTabPane>

            <!-- 4. History -->
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
                <ElTableColumn label="生效起" width="160">
                  <template #default="{ row }">
                    {{ formatLocalDateTime(row.effectiveFrom) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn label="生效止" width="160">
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
            </ElTabPane>
          </ElTabs>
        </div>
      </ElCard>
    </div>

    <!-- ===== 加载态 ===== -->
    <div v-if="loading" class="loading-tip">加载规则集详情中…</div>

    <!-- ===== Publish Dialog（整版发布：① + ⑦ 一起生效） ===== -->
    <ElDialog
      v-model="publishDialog"
      title="发布规则集版本（整版）"
      width="620px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="110px" size="default">
        <ElFormItem label="规则集">
          <span class="d-val">{{ detailNN?.summary.ruleSetCode }}</span>
        </ElFormItem>
        <ElFormItem label="待发布版本">
          <span class="d-val">{{ detailNN?.summary.draftVersionCode || '（无草稿）' }}</span>
        </ElFormItem>
        <ElFormItem label="本次发布内容">
          <div class="publish-blocks">
            <div class="publish-block">
              <span class="block-name">① 需求优先级段位</span>
              <ElTag size="small" effect="plain">{{ prioritySegmentCount }} 个段位</ElTag>
            </div>
            <div class="publish-block">
              <span class="block-name">⑦ 换型规则</span>
              <span v-if="!canViewSetup" class="muted">当前账号没有查看权限，条数不可见</span>
              <span v-else-if="setupSummaryLoading" class="muted">读取中…</span>
              <span v-else-if="setupSummaryError" class="muted">
                读取失败：{{ setupSummaryError }}
              </span>
              <ElTag v-else size="small" effect="plain">
                明确产品转换 {{ setupExactRules.length }} 条 / 默认换型
                {{ setupDefaultRules.length }} 条
              </ElTag>
            </div>
          </div>
        </ElFormItem>
        <ElFormItem label="发布确认 (必选)">
          <div class="publish-confirms">
            <ElCheckbox v-model="confirmPriority">
              我已核对「需求优先级段位」（{{ prioritySegmentCount }} 个段位）
            </ElCheckbox>
            <ElTooltip
              :disabled="canViewSetup"
              content="当前账号没有换型规则查看权限，无法核对 ⑦ 块；请联系系统管理员授予「换型规则查看」权限"
              placement="top"
            >
              <span>
                <ElCheckbox v-model="confirmSetup" :disabled="!canViewSetup">
                  我已核对「换型规则」（明确 {{ setupExactRules.length }} 条 + 默认
                  {{ setupDefaultRules.length }} 条）
                </ElCheckbox>
              </span>
            </ElTooltip>
          </div>
        </ElFormItem>
        <ElFormItem label="变更原因 (必填)">
          <ElInput
            v-model="publishForm.changeReason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert type="warning" :closable="false" show-icon class="d-alert">
          <template #title>整版发布：① 和 ⑦ 一起生效</template>
          本次发布会把「需求优先级段位」和「换型规则」一起发布成新版本；已发布的历史版本不受影响，可随时回看。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="publishDialog = false">取消</ElButton>
        <ElButton
          type="primary"
          :loading="actionRunning"
          :disabled="!canConfirmPublish"
          @click="onPublish"
        >
          确认发布
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== Disable Dialog ===== -->
    <ElDialog
      v-model="disableDialog"
      title="停用已发布规则集"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集">
          <span class="d-val">{{ detailNN?.summary.ruleSetCode }}</span>
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
          停用后该版本进入只读状态，排程引擎不再读取它；如需恢复，可基于此版本新建草稿后重新发布。
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
      title="新建规则集草稿"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集">
          <span class="d-val">{{ detailNN?.summary.ruleSetCode }}</span>
        </ElFormItem>
        <ElFormItem label="基于版本">
          <ElSelect
            v-model="createDraftForm.sourceRuleSetVersionId"
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
          <ElInput v-model="createDraftForm.newVersionCode" placeholder="如 RS-DEMO-V3" />
        </ElFormItem>
        <ElFormItem label="变更原因 (必填)">
          <ElInput
            v-model="createDraftForm.changeReason"
            type="textarea"
            :rows="3"
            placeholder="必须填写；将出现在审计记录"
          />
        </ElFormItem>
        <ElAlert
          :type="forkPermission.allowed ? 'info' : 'warning'"
          :closable="false"
          show-icon
          class="d-alert"
        >
          <template #title>
            {{
              forkPermission.allowed
                ? '新草稿会复制所选版本的内容'
                : (forkPermission.reason ?? '当前状态不允许新建草稿')
            }}
          </template>
          复制的内容只有规则集自己的两块：需求优先级段位与换型规则子块。
          参数集是独立的一族（见「参数集维护」页），不随规则集一起创建。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="createDraftDialog = false">取消</ElButton>
        <ElButton
          type="primary"
          :loading="actionRunning"
          :disabled="!forkPermission.allowed"
          @click="onCreateDraft"
        >
          确认新建
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [+ 新建规则集] Dialog ===== -->
    <ElDialog
      v-model="createRuleSetDialog"
      title="新建规则集"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集编码 (必填)">
          <ElInput
            v-model="createRuleSetForm.ruleSetCode"
            maxlength="50"
            show-word-limit
            placeholder="如 RS-PROD；全库唯一，创建后不能改"
          />
        </ElFormItem>
        <ElFormItem label="规则集名称 (必填)">
          <ElInput
            v-model="createRuleSetForm.ruleSetName"
            maxlength="200"
            placeholder="如 生产规则集"
          />
        </ElFormItem>
        <ElFormItem label="说明">
          <ElInput
            v-model="createRuleSetForm.description"
            type="textarea"
            :rows="2"
            placeholder="选填"
          />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>会同时建一个空白草稿版本</template>
          新建后系统自动建一条空白草稿版本（编码 = 规则集编码 + -V1）。规则集内容挂在版本上，
          没有版本时页面无可编辑内容。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="createRuleSetDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="actionRunning" @click="onCreateRuleSet">
          确认新建
        </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [编辑资料] Dialog ===== -->
    <ElDialog
      v-model="editRuleSetDialog"
      title="编辑规则集资料"
      width="520px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集编码">
          <ElInput :model-value="editRuleSetForm.ruleSetCode" disabled />
        </ElFormItem>
        <ElFormItem label="规则集名称 (必填)">
          <ElInput v-model="editRuleSetForm.ruleSetName" maxlength="200" />
        </ElFormItem>
        <ElFormItem label="说明">
          <ElInput v-model="editRuleSetForm.description" type="textarea" :rows="2" />
        </ElFormItem>
        <ElFormItem label="主表启用">
          <ElSwitch v-model="editRuleSetForm.isActive" />
        </ElFormItem>
        <ElAlert type="warning" :closable="false" show-icon class="d-alert">
          <template #title>停用只影响主表</template>
          停用规则集不会删除版本，也不会改变已发布版本的状态。编码创建后不能修改。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="editRuleSetDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="actionRunning" @click="onUpdateRuleSet"> 保存 </ElButton>
      </template>
    </ElDialog>

    <!-- ===== [保存段位] Dialog（PUT /rule-set/version/{id}，只改 demandPriorityJson） ===== -->
    <ElDialog
      v-model="saveDraftDialog"
      title="保存段位"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="90px">
        <ElFormItem label="变更说明">
          <ElInput
            v-model="saveDraftForm.changeReason"
            type="textarea"
            :rows="2"
            maxlength="200"
            placeholder="例如：把 VIP 订单提到最前"
          />
        </ElFormItem>
        <ElAlert type="info" :closable="false" show-icon class="d-alert">
          <template #title>保存只影响这一版草稿</template>
          保存后不会自动发布，需要在页面上继续「校验草稿 → 发布」。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="saveDraftDialog = false">取消</ElButton>
        <ElButton type="primary" :loading="saving" @click="onSaveDraft">保存</ElButton>
      </template>
    </ElDialog>

    <!-- ===== ⑦ 换型规则 新增/编辑 Dialog（与 Setup 两个维护页同一组件，不复制表单） ===== -->
    <SetupExactDialog
      v-model="exactDialogVisible"
      :mode="exactDialogMode"
      :rule="editingExactRule"
      @saved="handleSetupSaved"
    />
    <SetupDefaultDialog
      v-model="defaultDialogVisible"
      :mode="defaultDialogMode"
      :rule="editingDefaultRule"
      :prefill="defaultDialogPrefill"
      @saved="handleSetupSaved"
    />
  </div>
</template>

<style lang="less" scoped>
.aps-rules {
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

.seg-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.setup-block {
  margin-bottom: 20px;
}

.setup-block-title {
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.toolbar-spacer {
  flex: 1;
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

.loading-tip {
  padding: 30px;
  font-size: 14px;
  color: #94a3b8;
  text-align: center;
}

/* KPI 条 */
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

/* 过滤行 */
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

/* 主内容 */
.content-row {
  display: grid;
  grid-template-columns: 380px 1fr;
  gap: 16px;
}

/* 左：规则集列表 */
.ruleset-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 720px;
  overflow-y: auto;
}

.ruleset-card {
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

  .rs-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 6px;

    .rs-code {
      font-family: 'Courier New', monospace;
      font-weight: 600;
      color: #1e293b;
    }
  }

  .rs-name {
    margin-bottom: 8px;
    font-size: 13px;
    color: #334155;
  }

  .rs-meta {
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

  .rs-reason {
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

/* 优先级段位「段内第 N 位」顺序圆 */
.order-circle {
  display: inline-flex;
  width: 26px;
  height: 26px;
  font-size: 12px;
  font-weight: 600;
  color: #fff;
  background: #3b82f6;
  border-radius: 50%;
  align-items: center;
  justify-content: center;
}

.cond-list {
  padding-left: 16px;
  margin: 0;
  font-size: 12px;
  line-height: 1.6;
  color: #334155;
}

/* Diff */
.diff-summary {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-bottom: 12px;

  .diff-meta {
    margin-left: 8px;
    font-family: 'Courier New', monospace;
    font-size: 12px;
    color: #64748b;
  }
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

/* 整版发布弹窗：发布内容清单 + 两个确认勾选 */
.publish-blocks {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.publish-block {
  display: flex;
  align-items: center;
  gap: 8px;
}

.block-name {
  min-width: 130px;
  color: #475569;
}

.publish-confirms {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
</style>
