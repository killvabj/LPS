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
 *  - 换型规则子块（`setupTransitionRulesJson`；由「明确产品转换 / 默认换型规则」两页维护）
 *
 * 4号位文档第 14 节约束：
 *  - DRAFT 可编辑；PUBLISHED 不可直接改；ARCHIVED 只读
 *  - 优先级段位只展示段内顺序与名称，不暴露 PriorityScore（U21）
 *  - 发布产生新 Version，历史不覆盖（U22）
 *  - 前端不能直接 UPDATE Rule，所有写操作走 Controller（publish/disable/validate）
 *
 * 权限码：aps.rule.view / aps.rule.edit / aps.rule.publish
 */

import { computed, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRulesStore } from '@/store/modules/aps/rules'
import { formatUtcDateTime } from '@/utils/datetime'
import {
  RULE_STATUS_LABELS,
  RULE_STATUS_TAG,
  NO_VERSION,
  NO_VERSION_LABEL,
  RULE_STATUSES_UI,
  roleLabelOf,
  type RuleStatus,
  type RuleSetSummaryDto,
  type RuleVersionHistoryDto,
  type PrioritySegmentDto
} from '@/api/aps-v1'
import { competitionRoundLabel, conditionText, sortFieldText } from '@/api/aps-v1/prioritySegment'
import { toEditableSegments } from '@/api/aps-v1/prioritySegmentEditor'
import PrioritySegmentEditor from './components/PrioritySegmentEditor.vue'

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
  currentDetail,
  currentDiff,
  lastValidation,
  statusFilter,
  loading,
  listLoading,
  actionRunning,
  validating,
  saving,
  error,
  actor,
  actorRoles,
  canWrite,
  canPublishAction,
  actions,
  forkPermission
} = storeToRefs(rulesStore)

/** Non-null 在 v-if 块内用 */
const detailNN = computed(() => currentDetail.value!)

/** 只保留真正发生变化的字段 —— 后端 `fieldDiffs` 是全字段平铺，未变字段 `isChanged=false` */
const changedFields = computed(() =>
  (currentDiff.value?.fieldDiffs ?? []).filter((f) => f.isChanged)
)

/* ===== Tabs ===== */
type TabKey = 'segments' | 'diff' | 'history'
const activeTab = ref<TabKey>('segments')

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

async function selectRuleSet(ruleSet: RuleSetSummaryDto): Promise<void> {
  if (await blockIfDirty('切换')) return
  noVersionPicked.value = null
  rulesStore.reset()
  activeTab.value = 'segments'
  // 无版本的规则集必然加载不出内容（versions 返回空数组），
  // 与其让用户看一条报错红条，不如直接告诉他「这个还没配版本」
  if (!ruleSet.hasVersion) {
    noVersionPicked.value = ruleSet
    return
  }
  await rulesStore.loadDetail(ruleSet.ruleSetId)
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

async function openPublishDialog(): Promise<void> {
  if (await blockIfDirty('发布')) return
  publishForm.value = { changeReason: '' }
  publishDialog.value = true
}

async function onPublish(): Promise<void> {
  if (!detailNN.value) return
  const reason = publishForm.value.changeReason.trim()
  if (!reason) {
    rulesStore.handleError(new Error('请填写变更原因'))
    return
  }
  const ok = await rulesStore.publish(detailNN.value.summary.ruleSetId, reason)
  if (ok) publishDialog.value = false
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

/* ===== 删除规则集 Dialog（DELETE /rule-set/{id}，3号位 R1）===== */
const deleteRuleSetDialog = ref(false)

/** 删除目标：详情区选中项优先；「未配版本」的空态走列表行 */
const deleteTarget = computed<{ ruleSetId: number; code: string; name: string } | null>(() => {
  const d = detailNN.value
  if (d) {
    return {
      ruleSetId: d.summary.ruleSetId,
      code: d.summary.ruleSetCode,
      name: d.summary.ruleSetName
    }
  }
  const n = noVersionPicked.value
  if (n) return { ruleSetId: n.ruleSetId, code: n.ruleSetCode, name: n.ruleSetName }
  return null
})

/** 删除按钮的禁用原因（破坏性操作：先说清为什么不让删） */
const deleteDisabledReason = computed(() => {
  if (!rulesStore.canWrite) return '当前账号无删除权限'
  if (detailNN.value && !rulesStore.currentDeletable) {
    return '该规则集已发布过，不能删除（请先停用版本）'
  }
  return ''
})

function openDeleteRuleSetDialog(): void {
  if (deleteDisabledReason.value) {
    rulesStore.handleError(new Error(deleteDisabledReason.value))
    return
  }
  deleteRuleSetDialog.value = true
}

async function onDeleteRuleSet(): Promise<void> {
  const target = deleteTarget.value
  if (!target) return
  const ok = await rulesStore.removeRuleSet(target.ruleSetId)
  if (ok) {
    deleteRuleSetDialog.value = false
    noVersionPicked.value = null
    ElMessage.success('规则集已删除')
  }
}

onMounted(() => {
  rulesStore.loadList()
})
</script>

<template>
  <div class="aps-rules">
    <!-- ===== 顶部（无大标题页头：页面名由菜单/面包屑给出） ===== -->
    <div class="page-header">
      <div class="header-actions">
        <ElTooltip
          :content="`当前操作者：${actor}（${actorRoles.map(roleLabelOf).join('、')}）`"
          placement="bottom"
        >
          <ElTag effect="plain" type="primary">
            <Icon icon="vi-mdi:account-circle" /> {{ actor }}
          </ElTag>
        </ElTooltip>
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
              <span class="meta-key">当前版本</span>
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
              plain
              size="small"
              @click="openDeleteRuleSetDialog"
            >
              <Icon icon="vi-mdi:delete-outline" /> 删除这个规则集
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
              <!-- [删除]：破坏性操作，破坏性前先说清后果（3号位 R1） -->
              <ElTooltip
                v-if="canWrite"
                :content="deleteDisabledReason || '删除该规则集（会连带删除它的草稿版本）'"
                placement="bottom"
              >
                <ElButton
                  type="danger"
                  size="small"
                  :disabled="!!deleteDisabledReason"
                  @click="openDeleteRuleSetDialog"
                >
                  <Icon icon="vi-mdi:delete-outline" /> 删除
                </ElButton>
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
            <ElDescriptionsItem label="当前已发布版本">
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

            <!-- 2. Diff -->
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
                </div>
                <ElTable :data="changedFields" size="small" border>
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
                      <span class="diff-old">{{ row.sourceValue ?? '（无）' }}</span>
                    </template>
                  </ElTableColumn>
                  <ElTableColumn label="目标版本值" min-width="180">
                    <template #default="{ row }">
                      <span class="diff-new">{{ row.targetValue ?? '（无）' }}</span>
                    </template>
                  </ElTableColumn>
                </ElTable>
                <p class="diff-note">
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
                    <span :class="{ 'history-current': row.isCurrent }">{{ row.versionCode }}</span>
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
                    {{ formatUtcDateTime(row.publishedAt) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn label="生效起" width="160">
                  <template #default="{ row }">
                    {{ formatUtcDateTime(row.effectiveFrom) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn label="生效止" width="160">
                  <template #default="{ row }">
                    {{ formatUtcDateTime(row.effectiveTo) }}
                  </template>
                </ElTableColumn>
                <ElTableColumn label="当前生效" width="100" align="center">
                  <template #default="{ row }">
                    <ElTag v-if="row.isCurrent" size="small" type="success" effect="dark">是</ElTag>
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

    <!-- ===== Publish Dialog ===== -->
    <ElDialog v-model="publishDialog" title="发布草稿" width="520px" :close-on-click-modal="false">
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集">
          <span class="d-val">{{ detailNN?.summary.ruleSetCode }}</span>
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
          提交后此规则集版本进入已发布状态；原已发布版本成为历史版本，可随时回看。
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

    <!-- ===== [删除规则集] Dialog（DELETE /rule-set/{id}，3号位 R1）===== -->
    <ElDialog
      v-model="deleteRuleSetDialog"
      title="删除规则集"
      width="480px"
      :close-on-click-modal="false"
    >
      <ElForm label-width="100px" size="default">
        <ElFormItem label="规则集">
          <span class="d-val">{{ deleteTarget?.code }}</span>
        </ElFormItem>
        <ElFormItem label="名称">
          <span class="d-val">{{ deleteTarget?.name }}</span>
        </ElFormItem>
        <ElAlert type="error" :closable="false" show-icon class="d-alert">
          <template #title>删除后不可恢复</template>
          这条规则集和它的草稿版本会一起被删掉，删了就找不回来了。已发布过的规则集不能删（只能停用）；
          如果这个规则集已经被策略包引用，也删不掉。
        </ElAlert>
      </ElForm>
      <template #footer>
        <ElButton @click="deleteRuleSetDialog = false">取消</ElButton>
        <ElButton type="danger" :loading="actionRunning" @click="onDeleteRuleSet">
          确认删除
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
</style>
