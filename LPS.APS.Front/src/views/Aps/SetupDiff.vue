<!--
  APS V1 4号位 — 规则版本对比
  依据：frontNew/docs/APS_V1_Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916.md
   - §19.2 4号位「规则版本/Diff/发布状态」

  职责：对比两个规则集版本的换型规则差异（EXACT/DEFAULT 规则的 added/modified/removed）
  Diff 操作：选定两个规则集版本 → 调 GET /rule-set-versions/{id}/diff?otherVersionId=...
  边界：只读对比，不在此页直接编辑规则

  发布：本页**不再提供发布按钮**。整版规则集发布统一在「规则集维护」页
  （aps.rule.publish）。原按钮调 `POST /rule-set-versions/{id}/publish`，该端点
  仅凭 aps.setup.publish 就把含需求优先级的整版置 PUBLISHED —— 0号位 2026-10-26
  文档 §四 明确禁止；前端撤按钮不替代后端修复（已致函 3号位）。

  权限：aps.setup.view（查看 Diff）
-->

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElCol,
  ElEmpty,
  ElMessage,
  ElOption,
  ElRow,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus'
import { useSetupDiffStore } from '@/store/modules/aps/setupDiff'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { formatRuleSetVersionLabel } from './composables/ruleSetVersionLabels'
import {
  SETUP_RULE_STATUS_META,
  SETUP_VERSION_STATUS_META,
  type SetupRuleChangeDto
} from '@/api/aps-v1'
import { formatUtcDateTimeSec } from '@/utils/datetime'

defineOptions({ name: 'ApsSetupDiff' })

const router = useRouter()
const route = useRoute()
const diffStore = useSetupDiffStore()
const apsAuth = useApsAuthStore()

const {
  diff,
  selectedVersionId,
  otherVersionId,
  ruleSetVersions,
  diffLoading,
  versionsLoading,
  error,
  hasDiff,
  currentVersion,
  otherVersion,
  totalChanges
} = storeToRefs(diffStore)

/** 只有整版发布权（aps.rule.publish）才引导去规则集页发布 */
const canRulePublish = computed<boolean>(() => apsAuth.has('aps.rule.publish'))

// ===== 加载 =====
/**
 * 2026-10-28：本页已从菜单隐藏（meta.hidden），唯一入口是「规则集维护 → 版本对比」页签的
 * 「查看换型规则逐条差异 →」，那个按钮带 `?ruleSetId=&versionId=&otherVersionId=`。
 * ⇒ 带参进来时：版本下拉按规则集收窄（原来是不限规则集的全系统版本大杂烩，
 *    用户得自己从几十个版本里挑两个，还可能跨规则集乱比）→ 自动选中两端 → 直接出对比。
 */
async function loadAll() {
  const ruleSetId = Number(route.query.ruleSetId)
  const scoped = Number.isFinite(ruleSetId) && ruleSetId > 0
  await diffStore.loadRuleSetVersions(scoped ? { ruleSetId } : undefined)

  const versionId = Number(route.query.versionId)
  const otherId = Number(route.query.otherVersionId)
  const versions = ruleSetVersions.value
  const hasVersion = versions.some((v) => v.id === versionId)
  const hasOther = versions.some((v) => v.id === otherId)

  if (hasVersion) {
    diffStore.selectVersion(versionId)
  } else {
    // 默认选中第一个 DRAFT 版本
    const draftVersion = versions.find((v) => v.status === 'DRAFT')
    if (draftVersion) diffStore.selectVersion(draftVersion.id)
  }
  if (hasOther) {
    diffStore.selectOtherVersion(otherId)
    // 两端都在当前规则集里 ⇒ 直接出对比，不让用户再点一次「对比」
    if (hasVersion && versionId !== otherId) {
      await diffStore.loadDiff(versionId, otherId)
    }
  }
}

onMounted(() => {
  void loadAll()
})

// ===== Diff 操作 =====
async function applyDiff() {
  if (!selectedVersionId.value || !otherVersionId.value) {
    ElMessage.warning('请选择源版本和目标版本')
    return
  }
  if (selectedVersionId.value === otherVersionId.value) {
    ElMessage.warning('源版本和目标版本相同，无法对比')
    return
  }
  await diffStore.loadDiff(selectedVersionId.value, otherVersionId.value)
}

// ===== 发布：统一去规则集维护页（本页不再直接发布整版） =====
function goPublishInRules() {
  void router.push({ path: '/aps/rules' })
}

// ===== 格式化 =====
function getStatusTag(status: string) {
  // 版本选择器/publishStatus 传治理六态、规则变更行传派生三态；两表 DRAFT 同义，合并回退安全
  const meta =
    SETUP_RULE_STATUS_META[status as keyof typeof SETUP_RULE_STATUS_META] ??
    SETUP_VERSION_STATUS_META[status as keyof typeof SETUP_VERSION_STATUS_META]
  return meta ?? { label: status, tag: 'info' as const }
}

/** 规则类型中文标签（枚举值 → 中文） */
function ruleTypeLabel(ruleType: string): string {
  return ruleType === 'EXACT' ? '明确产品转换' : '默认换型规则'
}

function formatRuleChange(change: SetupRuleChangeDto) {
  const parts = [
    change.departmentCode,
    change.stageCode,
    change.operationCode,
    change.resourceCode
  ].filter(Boolean)

  if (change.ruleType === 'EXACT' && change.fromMaterialCode && change.toMaterialCode) {
    parts.push(`${change.fromMaterialCode} → ${change.toMaterialCode}`)
  }

  if (change.setupMinutes !== undefined) {
    parts.push(`${change.setupMinutes} 分钟`)
  }

  return parts.join(' / ')
}
</script>

<template>
  <div class="setup-diff-page">
    <!-- 页面头部 -->
    <div class="page-header">
      <div class="header-left">
        <h2>规则版本对比</h2>
        <p class="subtitle">对比两个规则版本的换型规则差异</p>
      </div>
    </div>

    <!-- 错误提示 -->
    <ElAlert
      v-if="error"
      type="error"
      :title="error"
      :closable="false"
      show-icon
      class="error-alert"
    />

    <!-- 版本选择器 -->
    <ElCard class="selector-card" shadow="never">
      <ElRow :gutter="16" align="middle">
        <ElCol :span="8">
          <div class="selector-label">源版本（当前）</div>
          <ElSelect
            :model-value="selectedVersionId ?? undefined"
            placeholder="选择源版本"
            :loading="versionsLoading"
            style="width: 100%"
            @change="diffStore.selectVersion"
          >
            <ElOption
              v-for="v in ruleSetVersions"
              :key="v.id"
              :label="`${formatRuleSetVersionLabel(v)}（${getStatusTag(v.status).label}）`"
              :value="v.id"
            >
              <span>{{ formatRuleSetVersionLabel(v) }}</span>
              <ElTag :type="getStatusTag(v.status).tag" size="small" style="margin-left: 8px">
                {{ getStatusTag(v.status).label }}
              </ElTag>
            </ElOption>
          </ElSelect>
        </ElCol>
        <ElCol :span="8">
          <div class="selector-label">目标版本（对比）</div>
          <ElSelect
            :model-value="otherVersionId ?? undefined"
            placeholder="选择目标版本"
            :loading="versionsLoading"
            style="width: 100%"
            @change="diffStore.selectOtherVersion"
          >
            <ElOption
              v-for="v in ruleSetVersions"
              :key="v.id"
              :label="`${formatRuleSetVersionLabel(v)}（${getStatusTag(v.status).label}）`"
              :value="v.id"
            >
              <span>{{ formatRuleSetVersionLabel(v) }}</span>
              <ElTag :type="getStatusTag(v.status).tag" size="small" style="margin-left: 8px">
                {{ getStatusTag(v.status).label }}
              </ElTag>
            </ElOption>
          </ElSelect>
        </ElCol>
        <ElCol :span="8">
          <div class="selector-label">&nbsp;</div>
          <ElButton
            type="primary"
            :loading="diffLoading"
            :disabled="
              !selectedVersionId || !otherVersionId || selectedVersionId === otherVersionId
            "
            @click="applyDiff"
          >
            <Icon icon="vi-ep:switch" class="mr-4" />
            对比
          </ElButton>
          <!-- 整版发布统一在「规则集维护」页：本页只做导航，不再直接调发布端点 -->
          <ElButton v-if="canRulePublish" type="success" plain @click="goPublishInRules">
            <Icon icon="vi-ep:right" class="mr-4" />
            去规则集维护页发布
          </ElButton>
        </ElCol>
      </ElRow>
    </ElCard>

    <!-- Diff 结果 -->
    <ElCard v-if="hasDiff" class="diff-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span>对比结果：{{ currentVersion?.versionCode }} → {{ otherVersion?.versionCode }}</span>
          <div>
            <ElTag type="success" size="small">+{{ diff?.addedCount }} 新增</ElTag>
            <ElTag type="warning" size="small" style="margin-left: 8px"
              >~{{ diff?.modifiedCount }} 修改</ElTag
            >
            <ElTag type="danger" size="small" style="margin-left: 8px"
              >-{{ diff?.removedCount }} 删除</ElTag
            >
          </div>
        </div>
      </template>

      <!-- 新增规则 -->
      <div v-if="diff?.setupRuleChanges.added.length" class="change-section">
        <h4>
          <ElTag type="success" size="small">新增</ElTag>
          {{ diff.setupRuleChanges.added.length }} 条规则
        </h4>
        <ElTable :data="diff.setupRuleChanges.added" border size="small">
          <ElTableColumn prop="ruleType" label="类型" width="100">
            <template #default="{ row }">
              <ElTag :type="row.ruleType === 'EXACT' ? 'primary' : 'info'" size="small">
                {{ ruleTypeLabel(row.ruleType) }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="规则详情">
            <template #default="{ row }">
              {{ formatRuleChange(row) }}
            </template>
          </ElTableColumn>
        </ElTable>
      </div>

      <!-- 修改规则 -->
      <div v-if="diff?.setupRuleChanges.modified.length" class="change-section">
        <h4>
          <ElTag type="warning" size="small">修改</ElTag>
          {{ diff.setupRuleChanges.modified.length }} 条规则
        </h4>
        <ElTable :data="diff.setupRuleChanges.modified" border size="small">
          <ElTableColumn prop="ruleType" label="类型" width="100">
            <template #default="{ row }">
              <ElTag :type="row.ruleType === 'EXACT' ? 'primary' : 'info'" size="small">
                {{ ruleTypeLabel(row.ruleType) }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="规则详情">
            <template #default="{ row }">
              {{ formatRuleChange(row) }}
            </template>
          </ElTableColumn>
          <ElTableColumn label="变更" width="200">
            <template #default="{ row }">
              <span v-if="row.before?.setupMinutes !== undefined" class="diff-old">
                {{ row.before.setupMinutes }} 分钟
              </span>
              <span
                v-if="
                  row.before?.setupMinutes !== undefined && row.after?.setupMinutes !== undefined
                "
              >
                →
              </span>
              <span v-if="row.after?.setupMinutes !== undefined" class="diff-new">
                {{ row.after.setupMinutes }} 分钟
              </span>
            </template>
          </ElTableColumn>
        </ElTable>
      </div>

      <!-- 删除规则 -->
      <div v-if="diff?.setupRuleChanges.removed.length" class="change-section">
        <h4>
          <ElTag type="danger" size="small">删除</ElTag>
          {{ diff.setupRuleChanges.removed.length }} 条规则
        </h4>
        <ElTable :data="diff.setupRuleChanges.removed" border size="small">
          <ElTableColumn prop="ruleType" label="类型" width="100">
            <template #default="{ row }">
              <ElTag :type="row.ruleType === 'EXACT' ? 'primary' : 'info'" size="small">
                {{ ruleTypeLabel(row.ruleType) }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="规则详情">
            <template #default="{ row }">
              {{ formatRuleChange(row) }}
            </template>
          </ElTableColumn>
        </ElTable>
      </div>

      <!-- 无变更 -->
      <ElEmpty v-if="totalChanges === 0" description="无变更（源版本 = 目标版本）" />

      <div v-if="diff?.comparedAt" class="compared-at">
        对比时间：{{ formatUtcDateTimeSec(diff.comparedAt) }}
      </div>
    </ElCard>

    <ElEmpty v-else-if="!diffLoading" description="请选择源版本和目标版本进行对比" />
  </div>
</template>

<style scoped>
.setup-diff-page {
  padding: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
}

.header-left h2 {
  margin: 0 0 4px 0;
  font-size: 20px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.header-left .subtitle {
  margin: 0;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.error-alert {
  margin-bottom: 16px;
}

.selector-card {
  margin-bottom: 16px;
}

.selector-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.diff-card {
  margin-bottom: 16px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.change-section {
  margin-bottom: 20px;
}

.change-section:last-child {
  margin-bottom: 0;
}

.change-section h4 {
  margin: 0 0 12px 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  display: flex;
  align-items: center;
  gap: 8px;
}

.diff-old {
  color: var(--el-color-danger);
  text-decoration: line-through;
}

.diff-new {
  color: var(--el-color-success);
  font-weight: 600;
}

.compared-at {
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid var(--el-border-color-lighter);
  font-size: 12px;
  color: var(--el-text-color-secondary);
  text-align: right;
}

.mr-4 {
  margin-right: 4px;
}
</style>
