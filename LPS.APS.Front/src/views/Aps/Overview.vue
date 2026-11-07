<script setup lang="ts">
/**
 * APS V1 4号位 — 排产总览（页面 1 / U01 / U02）
 *
 * 数据源（5号位真实端点，5 个数据面各自独立调，无聚合端点）：
 *  - /api/overview/active-plan             各 Domain 生效版本（→ store.planVersionId）
 *  - /api/order-query/summary?planVersionId= 订单摘要 4 类计数
 *  - /api/overview/resource-bottleneck?planVersionId= 资源负荷 + 不可用窗口
 *  - /api/overview/candidate-summary       待确认候选版本数
 *  - /api/domain-status/{runId}            排程域状态（6 值）
 *
 * 保留的 4号位文档约束：
 *  - 只展示，不触发任何重算/触发接口（第 2.1 节）
 *  - 多 Domain 同时展示生效版本（第 4 节）
 *  - 排程域异常区分根因（FAILED）与非根因（BLOCKED），细分说明走 reason
 *
 * 已删：底部「仅估算订单」卡片 —— 冻结 07 §6.6a + 规则 P-009 规划性采购占位**仅内存不建表**，
 *       总览是事后读模型物理上给不出（5号位 2026-10-15 §十；4号位 10-16 回执选 (A) 删除）。
 *
 * 10-16 版式精简：页面大标题 + 副标题 + 刷新按钮整块下线（标题由面包屑/标签页承担，页面只留数据）；
 *   「无生效版本」说明从顶部横幅改为写进「当前生效版本」卡片的空状态，订单摘要取不到时给整块空态
 *   而不是摆 5 张含 0 的 KPI 卡。
 *
 * 2026-11-04 逐功能测试后改动（口径写清 + 有数据不展示的列换掉）：
 *  - KPI「订单合计」下方写明统计范围（哪个域的哪个版本）—— 三个数据面都按 active-plan 第一条取数，
 *    不写清会被误读成「全部域合计」。
 *  - 资源表：删「负荷率 / 状态」两列（后端 `NULL AS UtilizationRate` / `0 AS IsBottleneck` 是占位字面量
 *    ⇒ 恒为 `-`），换成后端真有的「任务数 / 计划工时（小时）」；面板口径提示负荷率与超载暂无口径。
 *  - 资源「编码 + 名称」合成一列，同名只留一段（与甘特页共用 `utils/apsResource` 同一套取值）。
 *  - 排程域状态面板头写明「共 N 个排程域 · 来源排程运行 X」，让「8 项异常 / 12 个域」看得懂。
 *  - 待确认候选版本的「去处理」由死文本改成真链接（无 `aps.candidate.confirm` 权限者不给入口）。
 *
 * 2026-11-04 任务数口径写明（用户反馈「两个页面任务数对不上」）：
 *  - 版本卡「任务数」= `PlanVersion.TotalTasks` 冗余列 = **整个计划区间**（2026-10-30 ~ 2027-01-28）
 *    内的任务总数（dev 实测 QT 版 13201）；故卡上写明「（整个计划区间内）」。
 *  - 甘特页那个「任务」是**时间窗内**的数（dev 实测同一版本 11-04 ~ 11-10 窗内 3334 条）
 *    —— 两个数不是同一口径，窗口版随日期滚、版本版不滚。
 *  - 对账：7990(10-30~11-04) + 3334(11-04~11-11) + 1877(11-11~01-28) = 13201 ⇒ 两页能对上。
 *
 *  ⚠️ 2026-11-04 后端缺口（已出函，页面零妥协）：
 *  - PlanVersion 冗余列 `totalOrders`（dev 实测 53）与 `/api/order-query/summary` 的实时 `totalCount`
 *    （55）不一致 ⇒ **页面不展示 totalOrders**，避免同页两个数字打架（见 types/overview.ts 注释）。
 *  - 负荷率（`utilizationRate`）与瓶颈判定（`isBottleneck`）后端仍是占位字面量 ⇒ 不展示、不臆断。
 *
 * 2026-11-04 加「查看版本切换」：
 *  - 原先订单摘要 / 资源负荷 / 排程域状态死钉在 `active-plan` 第一条 ⇒ 页面上列了 3 张生效版本卡，
 *    用户看得见另外两个版本、点不动也看不到（dev 实测 QT 版 951-3S 有 1857 个任务 / 116.91 小时，
 *    恰恰是用户想看的那份数据）。
 *  - 改成**点「当前生效版本」卡片切换**（不另加下拉控件 —— 同一路径只留一个入口）：
 *    选中卡片标「查看中」；订单摘要按选中版本取、资源负荷按选中版本取、
 *    域状态按选中版本的 sourceScheduleRunId 取（不同版本可能来自不同 Run）。
 *  - 候选数与版本无关（全局），不随切换变。
 */

import { computed, onMounted } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { useRouter } from 'vue-router'
import { useOverviewStore } from '@/store/modules/aps/overview'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { resourceCodeNameParts } from '@/utils/apsResource'
import {
  RUN_DOMAIN_ISSUE_STATUSES,
  RUN_DOMAIN_STATUS_LABELS,
  RUN_DOMAIN_STATUS_TAG,
  planVersionStatusLabel,
  planVersionStatusTag,
  type ActiveVersionPanel,
  type RunDomainStatus
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElEmpty,
  ElIcon,
  ElTag
} from 'element-plus'

const router = useRouter()
const overviewStore = useOverviewStore()
const { errors, activeVersions, orderSummary, resources, pendingCandidateCount, domainStatuses } =
  storeToRefs(overviewStore)
const apsAuth = useApsAuthStore()

const domainStore = useDomainStore()
function humanLabel(domainKey: string | undefined | null): string {
  return domainStore.label(domainKey)
}

/** 时间容缺：后端 WhenWritingNull 会整段省略字段 */
function fmtTime(v: string | null | undefined, pattern = 'YYYY-MM-DD HH:mm'): string {
  return v ? dayjs(v).format(pattern) : '-'
}

/* ===== 订单摘要（计划派生三桶；理由见 types/order.ts OrderSummaryDto）=====
 *
 * 2026-11-02 口径全换（5号位 r14009 答复函 §二）：三桶改为**计划派生**，与订单页格子的
 * `riskLevel` 同源（冻结 `01 §64`）⇒ 总览与订单页不再各说各话。
 * 恒等式：按期 + 延迟 + 未排产 = 合计（v999333 实测 0 + 1 + 309 = 310）。
 *
 * 「风险」卡仍下线：`riskCount` 查的是 `OrderScheduleSummary`（冻结 07:4965 RiskLevel），
 *   dev 实测恒 0，**区分不了「无风险」与「该表无数据」** —— 显示它等于宣称零风险。
 * 「未排」卡重新上线：后端 `unscheduledCount` 已从 `CAST(0 AS INT)` 字面量改为**实值**
 *   （本版本无 Task 行 ⇒ 未排产/不可判定）。旧「未计算」卡（= 总数 − 按期 − 延迟）
 *   在新口径下算出来的其实是「未排产」，标签是错的 ⇒ 删掉，改用后端这一桶。
 */
const orderKpis = computed(() => {
  const s = orderSummary.value
  if (!s) return []
  return [
    { key: 'ontime', label: '按期', count: s.onTimeCount, color: '#059669' },
    { key: 'delayed', label: '延迟', count: s.delayedCount, color: '#ef4444' },
    { key: 'unscheduled', label: '未排产', count: s.unscheduledCount, color: '#94a3b8' }
  ]
})

const totalOrders = computed(() => orderSummary.value?.totalCount ?? null)

const orderShare = (count: number): number =>
  totalOrders.value && totalOrders.value > 0 ? Math.round((count / totalOrders.value) * 100) : 0

/* ===== 统计口径 =====
 *
 * 订单摘要 / 资源负荷 / 排程域状态三个数据面**都按「正在查看的版本」取数**
 * （store 的 `selectedVersion`）⇒ 页面必须写清「这些数字属于哪个域的哪个版本」，
 * 否则与下方 3 张生效版本卡对不上号（用户会以为 KPI 是全部域的合计）。
 *
 * 2026-11-04：原先死钉在 active-plan 第一条，用户看得见另外两个版本却点不动 ⇒
 *   改成可点卡片切换（selectedVersion），下面三块随之切换。
 */
const selectedVersion = computed(() => overviewStore.selectedVersion)

/** 正在查看的版本 id（用于卡片高亮） */
const selectedVersionId = computed(() => selectedVersion.value?.planVersionId ?? null)

/** 订单摘要的统计范围文案（域中文名 + 版本号） */
const summaryScope = computed(() => {
  const v = selectedVersion.value
  if (!v) return '统计范围未知'
  return `${humanLabel(v.domainKey)} · ${v.versionCode}`
})

/** 排程域状态来自哪个 Run（与 store 请求的入参同源 = 正在查看版本的 sourceScheduleRunId） */
const statusRunId = computed(() => selectedVersion.value?.sourceScheduleRunId ?? null)

/** 点版本卡 ⇒ 切到该版本（订单摘要 / 资源负荷 / 域状态三块随之重取） */
function onSelectVersion(v: ActiveVersionPanel): void {
  void overviewStore.selectVersion(v.planVersionId)
}

/* ===== 排程域状态 ===== */
const issueStatuses = new Set<string>(RUN_DOMAIN_ISSUE_STATUSES)
const issueCount = computed(
  () => domainStatuses.value.filter((d) => issueStatuses.has(d.status)).length
)

const domainLabel = (status: string): string =>
  RUN_DOMAIN_STATUS_LABELS[status as RunDomainStatus] ?? status
const domainTag = (status: string) => RUN_DOMAIN_STATUS_TAG[status as RunDomainStatus] ?? 'info'

/* ===== 资源 ===== */
const bottleneckResources = computed(() => resources.value.filter((r) => r.isBottleneck))

/** 资源显示名：编码 + 名称（同名只留一段，与甘特页同一套取值助手） */
const resourceLabel = (r: { resourceCode?: string | null; resourceName?: string | null }): string =>
  resourceCodeNameParts(r).join(' | ')

/** 计划工时：后端给的是小时小数，页面保留 2 位（7.062 → 7.06） */
const fmtHours = (v: number | null | undefined): string =>
  v === null || v === undefined ? '-' : String(Number(v.toFixed(2)))

/* ===== 待确认候选版本 ===== */
/** 候选确认页需 `aps.candidate.confirm`（路由 RBAC）⇒ 无权者不给入口，避免点了跳 403 */
const canConfirmCandidate = computed(() => apsAuth.has('aps.candidate.confirm'))

function goCandidate(): void {
  void router.push({ path: '/aps/candidate' })
}

function refresh() {
  overviewStore.load()
}

onMounted(refresh)
</script>

<template>
  <div class="aps-overview">
    <!-- ===== 分块错误（单端点失败不清空其它数据） ===== -->
    <ElAlert
      v-for="e in errors"
      :key="e.section"
      type="error"
      :closable="false"
      show-icon
      :title="`${e.section}加载失败：${e.message}`"
    />

    <!-- ===== 订单摘要：合计 + 按期/延迟/未排产（计划派生三桶）。取不到时给整块空态，不摆 0 ===== -->
    <div v-if="orderSummary" class="kpi-row">
      <div class="kpi-card total">
        <div class="kpi-label">订单合计</div>
        <div class="kpi-value">{{ totalOrders ?? '-' }}</div>
        <div class="kpi-ratio">统计范围：{{ summaryScope }}</div>
      </div>
      <div
        v-for="item in orderKpis"
        :key="item.key"
        class="kpi-card"
        :style="{ borderTopColor: item.color }"
      >
        <div class="kpi-label">{{ item.label }}</div>
        <div class="kpi-value" :style="{ color: item.color }">{{ item.count }}</div>
        <div class="kpi-ratio">占 {{ orderShare(item.count) }}%</div>
      </div>
    </div>
    <ElCard v-else class="panel">
      <ElEmpty description="暂无订单摘要，需先完成一次排产运行并正式采用生效版本" />
    </ElCard>

    <!-- ===== 中部两列 ===== -->
    <div class="content-row">
      <!-- 当前生效版本（多排程域） -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>当前生效版本</span>
            <ElTag v-if="activeVersions.length" type="success" size="small">
              {{ activeVersions.length }} 排程域
            </ElTag>
          </div>
        </template>
        <div v-if="!activeVersions.length" class="panel-empty">
          <ElEmpty description="当前没有排程域处于生效状态">
            <p class="empty-hint">
              订单摘要、资源负荷、排程域状态都以生效版本为入参。<br />
              需先完成一次排产运行并被正式采用。
            </p>
          </ElEmpty>
        </div>
        <div v-else class="version-list">
          <p v-if="activeVersions.length > 1" class="panel-hint">
            点版本卡可切换下面各块看的版本（订单摘要 / 资源负荷 / 排程域状态）
          </p>
          <div
            v-for="v in activeVersions"
            :key="v.planVersionId"
            class="version-card"
            :class="{ selected: v.planVersionId === selectedVersionId }"
            role="button"
            tabindex="0"
            title="点击查看该版本的数据"
            @click="onSelectVersion(v)"
            @keydown.enter="onSelectVersion(v)"
            @keydown.space.prevent="onSelectVersion(v)"
          >
            <div class="version-card-head">
              <div class="version-code">{{ v.versionCode }}</div>
              <div class="version-card-tags">
                <ElTag v-if="v.planVersionId === selectedVersionId" type="primary" size="small">
                  查看中
                </ElTag>
                <ElTag :type="planVersionStatusTag(v.status)" size="small">
                  {{ planVersionStatusLabel(v.status) }}
                </ElTag>
              </div>
            </div>
            <ElDescriptions :column="2" size="small" class="version-desc">
              <ElDescriptionsItem label="排程域">{{ humanLabel(v.domainKey) }}</ElDescriptionsItem>
              <ElDescriptionsItem label="激活时间">
                {{ fmtTime(v.activatedAt) }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="计划区间">
                {{ fmtTime(v.planHorizonStart, 'YYYY-MM-DD') }}
                ~
                {{ fmtTime(v.planHorizonEnd, 'YYYY-MM-DD') }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="来源排程运行">
                {{ v.sourceScheduleRunId ?? '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem label="任务数">
                {{ v.totalTasks ?? '-' }}
                <span class="field-scope">（整个计划区间内）</span>
              </ElDescriptionsItem>
            </ElDescriptions>
          </div>
        </div>
      </ElCard>

      <!-- 排程域状态 -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>排程域状态</span>
            <div class="panel-header-right">
              <span class="hint-text">
                共 {{ domainStatuses.length }} 个排程域<template v-if="statusRunId">
                  · 来源排程运行 {{ statusRunId }}</template
                >
              </span>
              <ElTag v-if="issueCount" type="danger" size="small"> {{ issueCount }} 项异常 </ElTag>
            </div>
          </div>
        </template>
        <div v-if="!domainStatuses.length" class="panel-empty">
          <ElEmpty description="暂无排程域状态" />
        </div>
        <div v-else class="issue-list">
          <div
            v-for="d in domainStatuses"
            :key="d.domainKey"
            class="issue-item"
            :class="{ abnormal: issueStatuses.has(d.status) }"
          >
            <ElTag :type="domainTag(d.status)" size="small">{{ domainLabel(d.status) }}</ElTag>
            <div class="issue-body">
              <div class="issue-domain">{{ humanLabel(d.domainKey) }}</div>
              <div v-if="d.reason" class="issue-msg">{{ d.reason }}</div>
              <div class="issue-time">{{ fmtTime(d.startedAt) }}</div>
            </div>
          </div>
        </div>
      </ElCard>
    </div>

    <!-- ===== 资源负荷 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>资源负荷</span>
          <div class="panel-header-right">
            <span v-if="bottleneckResources.length" class="hint-text">
              <ElIcon><Icon icon="vi-mdi:alert" /></ElIcon>
              <strong>{{ bottleneckResources.length }} 个瓶颈资源</strong>
            </span>
            <span class="hint-text">负荷率与超载判定暂无口径，暂不显示</span>
          </div>
        </div>
      </template>

      <div v-if="!resources.length" class="panel-empty">
        <ElEmpty description="暂无资源数据" />
      </div>
      <table v-else class="resource-table">
        <thead>
          <tr>
            <th>资源</th>
            <th>任务数</th>
            <th>计划工时（小时）</th>
            <th>不可用窗口</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in resources" :key="r.resourceId" :class="{ bottleneck: r.isBottleneck }">
            <td>{{ resourceLabel(r) }}</td>
            <td>{{ r.taskCount }}</td>
            <td>{{ fmtHours(r.totalPlannedHours) }}</td>
            <td>
              <span v-if="!r.unavailableWindows.length" class="muted">无</span>
              <div v-else class="window-list">
                <div v-for="(w, wi) in r.unavailableWindows" :key="wi" class="window-item">
                  {{ fmtTime(w.from) }} ~ {{ fmtTime(w.to) }}
                  <span v-if="w.reason" class="window-reason">{{ w.reason }}</span>
                </div>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </ElCard>

    <!-- ===== 底部：待确认候选（整行窄条，不占半屏留白） ===== -->
    <div class="candidate-bar">
      <ElIcon class="candidate-icon"><Icon icon="vi-mdi:clock-alert-outline" /></ElIcon>
      <span class="candidate-label">待确认候选版本</span>
      <span class="candidate-value">{{ pendingCandidateCount ?? '-' }}</span>
      <button
        v-if="pendingCandidateCount && canConfirmCandidate"
        type="button"
        class="candidate-link"
        @click="goCandidate"
      >
        去「候选版本确认」页处理 →
      </button>
      <span v-else-if="pendingCandidateCount === 0" class="candidate-sub">暂无待确认候选</span>
    </div>
  </div>
</template>

<style lang="less" scoped>
.aps-overview {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

/* 顶部（页面大标题区已按 10-16 要求下线，标题由面包屑与标签页承担） */
.hint-text {
  font-size: 13px;
  color: #475569;
}

.empty-hint {
  margin: 0;
  font-size: 13px;
  line-height: 1.7;
  color: #94a3b8;
}

/* KPI 卡片 */
.kpi-row {
  display: grid;
  /* 订单合计 + 2~3 张状态卡，卡片数会变，用 auto-fit 不留空格 */
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
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
    font-size: 24px;
    font-weight: 600;
    color: #1e293b;
  }

  .kpi-ratio {
    margin-top: 2px;
    font-size: 11px;
    color: #cbd5e1;
  }

  &.total {
    background: #f1f5f9;

    .kpi-value {
      color: #475569;
    }
  }
}

/* 中部两列 */
.content-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

/* 卡片 */
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

.panel-header-right {
  display: flex;
  align-items: center;
  gap: 10px;
  font-weight: 400;
}

.panel-empty {
  padding: 20px 0;
}

/* ACTIVE 版本卡片 */
.version-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.panel-hint {
  margin: 0;
  font-size: 12px;
  color: #94a3b8;
}

.version-card {
  padding: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  cursor: pointer;
  transition:
    border-color 0.15s,
    box-shadow 0.15s;

  &:hover {
    border-color: #93c5fd;
  }

  &.selected {
    border-color: #409eff;
    box-shadow: 0 0 0 1px #409eff inset;
    background: #f0f7ff;
  }

  &-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 8px;
  }

  &-tags {
    display: flex;
    gap: 6px;
    align-items: center;
  }

  .version-code {
    font-weight: 600;
    color: #1e293b;
  }

  .version-desc {
    :deep(.el-descriptions__label) {
      width: 80px;
      color: #94a3b8;
    }

    :deep(.el-descriptions__content) {
      color: #475569;
    }
  }

  /* 口径标注（跟数值贴在一起，避免读者把它当成另一个字段名） */
  .field-scope {
    font-size: 12px;
    color: #94a3b8;
  }
}

/* 排程域状态 */
.issue-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.issue-item {
  display: flex;
  gap: 12px;
  padding: 10px 12px;
  background: #f8fafc;
  border-left: 3px solid #cbd5e1;
  border-radius: 4px;

  &.abnormal {
    background: #fef2f2;
    border-left-color: #ef4444;
  }
}

.issue-body {
  flex: 1;
}

.issue-domain {
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
}

.issue-msg {
  margin-top: 2px;
  font-size: 13px;
  color: #475569;
}

.issue-time {
  margin-top: 4px;
  font-size: 11px;
  color: #94a3b8;
}

/* 资源表 */
.resource-table {
  width: 100%;
  border-collapse: collapse;

  th,
  td {
    padding: 10px 12px;
    font-size: 13px;
    text-align: left;
    border-bottom: 1px solid #f1f5f9;
  }

  th {
    font-weight: 500;
    color: #64748b;
    background: #f8fafc;
  }

  tbody tr.bottleneck {
    background: #fef2f2;
  }

  tbody tr:hover {
    background: #f8fafc;
  }
}

.window-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.window-item {
  font-size: 12px;
  color: #475569;
}

.window-reason {
  margin-left: 6px;
  color: #94a3b8;
}

.muted {
  color: #cbd5e1;
}

/* 底部：待确认候选窄条 */
.candidate-bar {
  display: flex;
  align-items: center;
  padding: 12px 18px;
  background: #fff;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
  gap: 12px;

  .candidate-icon {
    font-size: 20px;
    color: #3b82f6;
  }

  .candidate-label {
    font-size: 14px;
    color: #475569;
  }

  .candidate-value {
    font-size: 20px;
    font-weight: 600;
    color: #1e293b;
  }

  .candidate-sub {
    margin-left: auto;
    font-size: 12px;
    color: #94a3b8;
  }

  .candidate-link {
    margin-left: auto;
    padding: 0;
    font-size: 12px;
    color: #2563eb;
    cursor: pointer;
    background: none;
    border: 0;

    &:hover {
      text-decoration: underline;
    }
  }
}
</style>
