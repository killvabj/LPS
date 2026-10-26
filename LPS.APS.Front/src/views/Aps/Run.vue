<script setup lang="ts">
/**
 * APS V1 4号位 — 运行与下发（页面 9 / U01 / U02 / U19）
 *
 * 2026-10-16 重整（依据《4号位致3号位/5号位·运行页联调缺口回执》）：
 *  - 数据源从自拟聚合体 `RunStatusDto` 换成三个真实数据面（见 store/run.ts）
 *  - 状态词表改冻结 07 §4.5 权威四值（此前自拟 PENDING / SUCCESS 冻结里不存在）
 *  - **MES 下发区块整块下线**：后端无资格查询端点；`POST /api/mes/dispatch` 只认
 *    `factoryCode` 且端点说明写明「不触发 2号位 排程」，是登记 stub；
 *    冻结 §十 要求的链路是 4号位 → 5号位 → 2号位，前端直调亦违反该链路
 *  - PARTIAL 三列拆解改为「排程域状态拆解」，数据来自 5号位 `GET /api/domain-status/{runId}`
 */

import { computed, onMounted } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  useRunStore,
  RUN_STATUS_LABEL,
  RUN_STATUS_TAG,
  RUN_TYPE_LABEL
} from '@/store/modules/aps/run'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { useDomainStore } from '@/store/modules/aps/domain'
import type { DomainKey, RunStatus } from '@/api/aps-v1'
import {
  PLAN_VERSION_STATUS_LABELS,
  RUN_DOMAIN_STATUS_LABELS,
  RUN_DOMAIN_STATUS_TAG,
  type PlanVersionStatus,
  type RunDomainStatus
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElEmpty,
  ElTag,
  ElTable,
  ElTableColumn,
  ElTooltip
} from 'element-plus'

const runStore = useRunStore()
const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()

function humanLabel(domainKey: string | undefined | null): string {
  if (!domainKey) return '—'
  return domainStore.label(domainKey)
}

const {
  loading,
  error,
  errors,
  visibleRuns,
  activeVersions,
  domainStatuses,
  domainStatusRunId,
  domainStatusHasDenominator,
  runStatusCounts,
  recovering
} = storeToRefs(runStore)

/* ===== 计划版本状态中文标签 ===== */
function planVersionStatusLabel(status: string): string {
  return PLAN_VERSION_STATUS_LABELS[status as PlanVersionStatus] ?? status
}

function runStatusLabel(status: string): string {
  return RUN_STATUS_LABEL[status as RunStatus] ?? status
}

function runStatusTag(status: string): 'success' | 'warning' | 'danger' | 'primary' {
  return RUN_STATUS_TAG[status as RunStatus] ?? 'info'
}

function domainStatusLabel(status: string): string {
  return RUN_DOMAIN_STATUS_LABELS[status as RunDomainStatus] ?? status
}

function domainStatusTag(status: string): 'success' | 'primary' | 'warning' | 'info' | 'danger' {
  return RUN_DOMAIN_STATUS_TAG[status as RunDomainStatus] ?? 'info'
}

/* ===== Run 持续时间（秒） ===== */
function runDuration(r: { startedAt: string; completedAt?: string | null }): string {
  const end = r.completedAt ? new Date(r.completedAt).getTime() : Date.now()
  const dur = Math.round((end - new Date(r.startedAt).getTime()) / 1000)
  if (dur < 60) return `${dur} 秒`
  if (dur < 3600) return `${Math.floor(dur / 60)} 分 ${dur % 60} 秒`
  return `${Math.floor(dur / 3600)} 时 ${Math.floor((dur % 3600) / 60)} 分`
}

/* ===== 顶部 KPI（冻结 07 §4.5 权威四值，不另造状态） ===== */
const kpiItems = computed(() => {
  const c = runStatusCounts.value
  return [
    { key: 'COMPLETED', label: '已完成', count: c.COMPLETED, color: '#059669' },
    { key: 'PARTIAL_SUCCESS', label: '部分成功', count: c.PARTIAL_SUCCESS, color: '#f59e0b' },
    { key: 'RUNNING', label: '运行中', count: c.RUNNING, color: '#3b82f6' },
    { key: 'FAILED', label: '失败', count: c.FAILED, color: '#ef4444' }
  ]
})

/* ===== 排程域状态拆解（5号位 /api/domain-status/{runId}，6 值分四组展示） ===== */
const DOMAIN_STATUS_GROUPS: {
  label: string
  cls: string
  statuses: readonly string[]
}[] = [
  { label: '已完成', cls: 'success', statuses: ['COMPLETED'] },
  { label: '异常（失败 / 被阻断）', cls: 'failed', statuses: ['FAILED', 'BLOCKED'] },
  { label: '进行中 / 待确认', cls: 'active', statuses: ['RUNNING', 'CANDIDATE'] },
  { label: '未开始', cls: 'not-started', statuses: ['NOT_STARTED'] }
]

const domainStatusGroups = computed(() =>
  DOMAIN_STATUS_GROUPS.map((g) => ({
    ...g,
    items: domainStatuses.value.filter((d) => g.statuses.includes(d.status))
  }))
)

const hasDomainStatuses = computed(() => domainStatuses.value.length > 0)

/**
 * 域状态空态文案：三态，**不许把取不到说成「没登记」**。
 * - 没取过数 → 还没有排产记录
 * - 取过数、这次 Run 确实没登记预期域 → 5号位 R3 裁定的终态，如实说
 * - 取过数、这次 Run 登记了预期域却没取到 → 取数出了问题，不冒充正常（错误横幅同时会亮）
 */
const domainStatusEmptyText = computed(() => {
  if (domainStatusRunId.value === null) {
    return '还没有排产记录，排产之后这里会显示每个排程域的进展。'
  }
  return domainStatusHasDenominator.value
    ? '这次排产登记了要跑的排程域，但各域状态暂时取不到，请稍后再看。'
    : '最近这次排产没有登记要跑哪些排程域，所以暂时看不出各域进展。'
})

const recentRunsSorted = computed(() =>
  [...visibleRuns.value].sort(
    (a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()
  )
)

/* ===== 最近状态摘要 ===== */
function lastRunOf(status: RunStatus) {
  const hits = visibleRuns.value.filter((r) => r.status === status)
  return (
    hits.sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())[0] ??
    null
  )
}

const lastCompletedRun = computed(() => lastRunOf('COMPLETED'))
const lastFailedRun = computed(() => lastRunOf('FAILED'))
const lastPartialRun = computed(() => lastRunOf('PARTIAL_SUCCESS'))

/** 完成率 = COMPLETED / 总数（PARTIAL_SUCCESS 不计入完成） */
const completionRate = computed(() => {
  const total = visibleRuns.value.length
  const done = runStatusCounts.value.COMPLETED
  if (!total) return { pct: 0, label: '0%', count: 0, total: 0 }
  return {
    pct: Math.round((done / total) * 100),
    label: `${Math.round((done / total) * 100)}%`,
    count: done,
    total
  }
})

/* ===== 加载 ===== */
function refresh(): void {
  runStore.load()
}
onMounted(refresh)

/* ===== U19：FAILED Run 人工恢复 =====
 * 4号位文档第 15 节：必须调用「新建 ScheduleRun」，不能把 FAILED 改回 RUNNING
 * 权限：aps.plan.run（v1.2 §23.1 权限码门控）
 */
const canRecoverRun = computed(() => apsAuth.has('aps.plan.run'))

async function handleRecoverFailedRun(row: {
  id: number
  expectedDomainKeys: DomainKey[] | null
  errorMessage?: string | null
}): Promise<void> {
  if (!canRecoverRun.value) {
    ElMessage.warning('当前账号无排程执行权限，不可恢复失败批次')
    return
  }
  if (!row.expectedDomainKeys?.length) {
    ElMessage.warning('这条排产记录没有登记排程域，无法发起恢复')
    return
  }
  let reason = ''
  try {
    const res = await ElMessageBox.prompt(
      `将对失败的排产记录 #${row.id} 发起人工恢复。\n` +
        `排程域：${row.expectedDomainKeys.map(humanLabel).join(' / ')}\n` +
        `原错误：${row.errorMessage ?? '-'}\n\n` +
        `恢复会新建一次排产运行，原记录状态保持失败。\n` +
        `请填写恢复原因（写入审计）：`,
      '人工恢复失败的排产记录',
      {
        confirmButtonText: '确认恢复',
        cancelButtonText: '取消',
        inputType: 'textarea',
        inputPlaceholder: '例如：PI 位置数据已补齐，重新触发排程',
        inputValidator: (val: string) => (val && val.trim().length >= 4 ? true : '至少 4 个字符'),
        inputErrorMessage: '至少 4 个字符'
      }
    )
    reason = (res.value ?? '').trim()
  } catch {
    return
  }
  const actor = apsAuth.userInfo?.userCode ?? 'unknown'
  try {
    const result = await runStore.recoverFailedRun({
      originalRunId: row.id,
      domainKeys: row.expectedDomainKeys,
      reason,
      actor
    })
    ElMessage.success(
      `已发起恢复 → 新记录 #${result.newRunId}（原记录 #${result.originalRunId} 仍为失败）。列表已刷新。`
    )
  } catch (err) {
    const msg = (err as Error)?.message ?? '未知错误'
    ElMessage.error(`恢复失败：${msg}`)
  }
}
</script>

<template>
  <div class="aps-run">
    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />

    <!-- 分块失败：每个数据面单独提示，不整页报错 -->
    <ElAlert
      v-for="e in errors"
      :key="e.section"
      type="warning"
      :closable="false"
      show-icon
      :title="`${e.section}加载失败：${e.message}`"
    />

    <!-- ===== Run 状态 KPI ===== -->
    <div class="kpi-row">
      <div
        v-for="item in kpiItems"
        :key="item.key"
        class="kpi-card"
        :style="{ borderTopColor: item.color }"
      >
        <div class="kpi-label">{{ item.label }}</div>
        <div class="kpi-value" :style="{ color: item.color }">{{ item.count }}</div>
        <div class="kpi-ratio">最近 {{ visibleRuns.length }} 次排产</div>
      </div>
    </div>

    <!-- ===== 排程域状态拆解（最近一次 Run） ===== -->
    <ElCard class="partial-card">
      <template #header>
        <div class="panel-header">
          <span>
            排程域状态拆解
            <span v-if="domainStatusRunId !== null" class="muted">
              （最近一次排产 #{{ domainStatusRunId }}）
            </span>
          </span>
          <span class="hint-text">各排程域状态由后端按计划版本派生，此处只做展示</span>
        </div>
      </template>
      <div v-if="!hasDomainStatuses" class="col-empty">{{ domainStatusEmptyText }}</div>
      <div v-else class="partial-grid">
        <div v-for="g in domainStatusGroups" :key="g.label" class="partial-col" :class="g.cls">
          <div class="col-head">
            <span class="col-name">{{ g.label }}</span>
            <ElTag
              size="small"
              :type="g.items.length ? domainStatusTag(g.items[0].status) : 'info'"
            >
              {{ g.items.length }}
            </ElTag>
          </div>
          <div v-for="d in g.items" :key="d.domainKey" class="col-item">
            <ElTag effect="light" :type="domainStatusTag(d.status)">
              {{ humanLabel(d.domainKey) }}
            </ElTag>
            <span class="muted small">{{ domainStatusLabel(d.status) }}</span>
            <span v-if="d.planVersionCode" class="muted small">{{ d.planVersionCode }}</span>
            <div v-if="d.reason" class="err-msg">{{ d.reason }}</div>
          </div>
          <div v-if="!g.items.length" class="col-empty">无</div>
        </div>
      </div>
    </ElCard>

    <!-- ===== Recent Runs ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span> 最近排产记录 </span>
          <span class="hint-text">人工恢复会新建一次运行，原失败运行状态保持失败</span>
        </div>
      </template>
      <ElTable v-if="recentRunsSorted.length" :data="recentRunsSorted" size="small" border stripe>
        <ElTableColumn prop="id" label="批次" width="70" fixed="left" />
        <ElTableColumn label="类型" width="100">
          <template #default="{ row }">{{ RUN_TYPE_LABEL[row.runType] }}</template>
        </ElTableColumn>
        <ElTableColumn label="状态" width="100">
          <template #default="{ row }">
            <ElTag :type="runStatusTag(row.status)" size="small">
              {{ runStatusLabel(row.status) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="排程域" width="170">
          <template #default="{ row }">
            <template v-if="row.expectedDomainKeys?.length">
              <span v-for="dk in row.expectedDomainKeys" :key="dk">
                <ElTag size="small" effect="plain" class="domain-tag">
                  {{ humanLabel(dk) }}
                </ElTag>
              </span>
            </template>
            <span v-else class="muted">未登记</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="触发来源" width="130">
          <template #default="{ row }">
            <span class="muted">{{ row.triggeredBy ?? '-' }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="数据截止" width="140">
          <template #default="{ row }">
            <span class="muted">{{ dayjs(row.dataCutoffTime).format('MM-DD HH:mm') }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="策略包版本" width="120">
          <template #default="{ row }">
            <span v-if="row.strategyProfileVersionId" class="muted code-tag">
              {{ row.strategyProfileVersionId }}
            </span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="耗时" width="80">
          <template #default="{ row }">
            <span class="muted">{{ runDuration(row) }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="开始时间" width="140">
          <template #default="{ row }">{{ dayjs(row.startedAt).format('MM-DD HH:mm') }}</template>
        </ElTableColumn>
        <ElTableColumn label="完成时间" width="140">
          <template #default="{ row }">
            <span v-if="row.completedAt">{{ dayjs(row.completedAt).format('MM-DD HH:mm') }}</span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="错误信息" min-width="180">
          <template #default="{ row }">
            <span v-if="row.errorMessage" class="err-cell">{{ row.errorMessage }}</span>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="130" fixed="right">
          <template #default="{ row }">
            <template v-if="row.status === 'FAILED'">
              <!-- 冻结语义：ExpectedDomainKeysJson 是恢复的唯一权威依据，缺了就不能恢复。
                   直接禁用并在悬停时说明原因，避免用户点了才收到警告。 -->
              <ElTooltip
                :disabled="!!row.expectedDomainKeys?.length"
                content="这条记录没有登记排程域，暂不能恢复"
                placement="left"
              >
                <span class="recover-wrap">
                  <ElButton
                    size="small"
                    type="danger"
                    :loading="recovering"
                    :disabled="!canRecoverRun || !row.expectedDomainKeys?.length"
                    @click="handleRecoverFailedRun(row)"
                  >
                    <Icon icon="vi-mdi:restart-alert" />
                    人工恢复
                  </ElButton>
                </span>
              </ElTooltip>
            </template>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else-if="loading" class="panel-empty">
        <ElEmpty description="加载中" />
      </div>
      <ElEmpty v-else description="暂无排产记录" />
    </ElCard>

    <!-- ===== 两列：当前生效版本 + 最近状态摘要 ===== -->
    <div class="content-row">
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span> 当前生效版本 </span>
            <ElTag v-if="activeVersions.length" type="primary" size="small">
              {{ activeVersions.length }} 排程域
            </ElTag>
          </div>
        </template>
        <div v-if="!activeVersions.length" class="panel-empty">
          <ElEmpty description="当前无生效版本" />
        </div>
        <div v-else class="version-list">
          <div
            v-for="v in activeVersions"
            :key="v.planVersionId"
            class="version-card"
            :class="v.status.toLowerCase()"
          >
            <div class="version-head">
              <span class="version-code">{{ v.versionCode }}</span>
              <ElTag
                :type="
                  v.status === 'ACTIVE'
                    ? 'success'
                    : v.status === 'CANDIDATE'
                      ? 'warning'
                      : v.status === 'FAILED'
                        ? 'danger'
                        : 'info'
                "
                size="small"
              >
                {{ planVersionStatusLabel(v.status) }}
              </ElTag>
            </div>
            <ElDescriptions :column="2" size="small" class="version-desc">
              <ElDescriptionsItem label="排程域">{{ humanLabel(v.domainKey) }}</ElDescriptionsItem>
              <ElDescriptionsItem label="激活时间">
                {{ v.activatedAt ? dayjs(v.activatedAt).format('MM-DD HH:mm') : '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="计划区间">
                {{ dayjs(v.planHorizonStart).format('YYYY-MM-DD') }}
                ~
                {{ dayjs(v.planHorizonEnd).format('YYYY-MM-DD') }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="版本号">
                <span class="code-tag">#{{ v.planVersionId }}</span>
              </ElDescriptionsItem>
            </ElDescriptions>
          </div>
        </div>
      </ElCard>

      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span> 最近状态摘要 </span>
            <span class="hint-text">关键排产记录时间线</span>
          </div>
        </template>

        <div
          class="summary-rate"
          :class="completionRate.pct >= 80 ? 'good' : completionRate.pct >= 50 ? 'mid' : 'bad'"
        >
          <div class="rate-num">{{ completionRate.label }}</div>
          <div class="rate-sub">
            完成率 · {{ completionRate.count }} / {{ completionRate.total }} 次排产
          </div>
        </div>

        <ul class="summary-list">
          <li class="summary-row">
            <span class="row-label">
              <Icon icon="vi-mdi:check-circle" class="row-icon good" />
              最近完成
            </span>
            <span v-if="lastCompletedRun" class="row-val">
              <code class="code-tag">#{{ lastCompletedRun.id }}</code>
              <span class="muted">
                {{
                  dayjs(lastCompletedRun.completedAt ?? lastCompletedRun.startedAt).format(
                    'MM-DD HH:mm'
                  )
                }}
              </span>
            </span>
            <span v-else class="muted">无</span>
          </li>

          <li class="summary-row">
            <span class="row-label">
              <Icon icon="vi-mdi:alert-circle" class="row-icon bad" />
              最近失败
            </span>
            <span v-if="lastFailedRun" class="row-val">
              <code class="code-tag">#{{ lastFailedRun.id }}</code>
              <ElTag size="small" type="danger" effect="light" class="err-mini">
                {{ lastFailedRun.errorMessage?.slice(0, 24) ?? '-' }}
              </ElTag>
            </span>
            <span v-else class="muted">无</span>
          </li>

          <li class="summary-row">
            <span class="row-label">
              <Icon icon="vi-mdi:alert" class="row-icon warn" />
              最近部分成功
            </span>
            <span v-if="lastPartialRun" class="row-val">
              <code class="code-tag">#{{ lastPartialRun.id }}</code>
              <span class="muted">{{ dayjs(lastPartialRun.startedAt).format('MM-DD HH:mm') }}</span>
            </span>
            <span v-else class="muted">无</span>
          </li>

          <li class="summary-row">
            <span class="row-label">
              <Icon icon="vi-mdi:layers-triple" class="row-icon" />
              生效版本
            </span>
            <span class="row-val">
              <strong>{{ activeVersions.length }}</strong>
              <span class="muted">排程域</span>
            </span>
          </li>

          <li class="summary-row">
            <span class="row-label">
              <Icon icon="vi-mdi:refresh" class="row-icon" />
              数据刷新
            </span>
            <span class="row-val">
              <ElButton size="small" :loading="loading" @click="refresh">
                <Icon icon="vi-ep:refresh" /> 刷新
              </ElButton>
            </span>
          </li>
        </ul>
      </ElCard>
    </div>
  </div>
</template>

<style lang="less" scoped>
.aps-run {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.kpi-row {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}

.kpi-card {
  padding: 14px 16px;
  background: #fff;
  border-top: 3px solid #94a3b8;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);

  .kpi-label {
    font-size: 12px;
    color: #94a3b8;
  }

  .kpi-value {
    margin-top: 4px;
    font-size: 22px;
    font-weight: 600;
    color: #1e293b;
  }

  .kpi-ratio {
    margin-top: 2px;
    font-size: 11px;
    color: #cbd5e1;
  }
}

.content-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
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

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
}

/* 包一层 span：禁用态的 ElButton 不触发鼠标事件，Tooltip 需要可悬停的外层 */
.recover-wrap {
  display: inline-block;
}

.small {
  font-size: 11px;
}

/* 排程域状态拆解四列 */
.partial-card {
  border-radius: 8px;
}

.partial-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 14px;
}

.partial-col {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  &.success {
    background: #ecfdf5;
    border-color: #a7f3d0;
  }

  &.failed {
    background: #fef2f2;
    border-color: #fecaca;
  }

  &.active {
    background: #eff6ff;
    border-color: #bfdbfe;
  }

  &.not-started {
    background: #f8fafc;
    border-color: #e2e8f0;
  }

  .col-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 10px;

    .col-name {
      font-size: 13px;
      font-weight: 600;
      color: #1e293b;
    }
  }

  .col-item {
    padding: 8px 10px;
    margin-bottom: 6px;
    background: rgb(255 255 255 / 70%);
    border-radius: 4px;
  }

  .col-empty {
    padding: 8px 0;
    font-size: 12px;
    color: #94a3b8;
    text-align: center;
  }

  .err-msg {
    margin-top: 6px;
    font-size: 12px;
    color: #7f1d1d;
  }
}

/* 生效版本卡片 */
.version-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.version-card {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;

  &.active {
    background: #ecfdf5;
    border-color: #a7f3d0;
  }

  &.candidate {
    background: #fff7ed;
    border-color: #fed7aa;
  }

  &.failed {
    background: #fef2f2;
    border-color: #fecaca;
  }

  .version-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 8px;
  }

  .version-code {
    font-weight: 600;
    color: #1e293b;
  }

  .version-desc {
    :deep(.el-descriptions__label) {
      width: 60px;
      color: #94a3b8;
    }
  }
}

.domain-tag {
  margin-right: 4px;

  &:last-child {
    margin-right: 0;
  }
}

.err-cell {
  display: block;
  max-width: 220px;
  overflow: hidden;
  font-size: 12px;
  color: #b91c1c;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.code-tag {
  padding: 1px 6px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  background: #f1f5f9;
  border-radius: 3px;
}

/* 最近状态摘要 */
.summary-rate {
  padding: 12px 16px;
  margin-bottom: 12px;
  border-radius: 6px;

  &.good {
    background: #ecfdf5;
    border: 1px solid #a7f3d0;
  }

  &.mid {
    background: #fff7ed;
    border: 1px solid #fed7aa;
  }

  &.bad {
    background: #fef2f2;
    border: 1px solid #fecaca;
  }

  .rate-num {
    font-family: 'Courier New', monospace;
    font-size: 26px;
    font-weight: 700;
    color: #1e293b;
  }

  .rate-sub {
    margin-top: 2px;
    font-size: 12px;
    color: #64748b;
  }
}

.summary-list {
  padding: 0;
  margin: 0 0 12px;
  list-style: none;
}

.summary-row {
  display: flex;
  padding: 6px 4px;
  font-size: 13px;
  border-bottom: 1px dashed #e2e8f0;
  align-items: center;
  justify-content: space-between;

  &:last-child {
    border-bottom: none;
  }

  .row-label {
    display: inline-flex;
    font-size: 12px;
    color: #64748b;
    align-items: center;
    gap: 6px;
  }

  .row-val {
    display: inline-flex;
    font-size: 12px;
    color: #1e293b;
    align-items: center;
    gap: 6px;
  }

  .row-icon {
    font-size: 14px;

    &.good {
      color: #059669;
    }

    &.bad {
      color: #ef4444;
    }

    &.warn {
      color: #f59e0b;
    }
  }

  .err-mini {
    max-width: 130px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
}
</style>
