<script setup lang="ts">
/**
 * APS V1 4号位 — 排产总览 / 系统首页（页面 1 / U01 / U02）→ v0.2 稿 **P01**
 *
 * 2026-11-20：**快捷入口卡片改名**（依据 用户 2026-11-20 指示）：「运行治理」→「运行与版本管理」
 *   ⇒ 与菜单改名一致，用户在首页看到的名字和侧边栏对得上。
 * 2026-11-20：**提示文字同步改名**（依据 同上）：「需要重算或查看完整运行记录，请到「运行治理」页处理。」
 *   → 「运行与版本管理」；原先卡片改名时漏了正文提示，造成首页两处叫法不一致。
 *
 * 2026-11-20 **整块重做**（依据《APS_V1_P01-P13 全页面业务设计统一收口核对稿 v0.2
 * §4 P01「系统首页：最近FULL公告＋运行异常＋快捷入口」【确认】）：
 *   - 页面**只保留三块**：
 *     ① 最近一次 FULL 运行信息（Run 号 / 真实 RunType / 起止 / DataCutoffTime / 真实状态
 *        ＋ 各域「本次运行生成的版本」与「各域当前 ACTIVE 版本」两列）
 *     ② 「本次运行需关注」（**仅** FULL 失败 / 部分成功才出现）
 *     ③ 业务快捷入口（只显示可访问入口）
 *   - **撤除**：订单摘要 KPI 行（按期/延迟/未排产＝交付 KPI 驾驶舱）、资源负荷块、
 *     待确认候选版本窄条、「当前生效版本」卡片切换（版本卡切换随订单摘要/资源负荷一起撤）。
 *     依据：稿 §4 P01「不做订单延期数、设备负荷、采购缺口汇总卡；不设置主观重大异常阈值
 *     或风险等级；不构建首页筛选体系；不提供运行恢复/发布按钮」。
 *
 * ⚠️ 「最近一次 FULL」不依赖端点默认排序：数组端点 `/api/governance/runs` 排序未声明 ⇒
 *   前端**显式过滤 `runType === 'FULL_SCHEDULE'`** 并按 `startedAt` 倒序取第一条
 *   （手工重排 / 局部重排 / 仿真 / 插单 WhatIf 一律不算「正式夜间计算」）。
 *
 * ⚠️ 「本次生成的版本」与「当前生效版本」**必须分两列**（稿 P01 明文）：
 *   一个 Domain 本次失败时，它**旧的 ACTIVE 版本仍可能继续生效** ⇒ 把旧 ACTIVE 写成本次
 *   新计划＝造假。故本次列取 `GET /api/domain-status/{runId}` 的 `planVersionCode`，
 *   当前列取 `GET /api/overview/active-plan` 的 `versionCode`，两列不合并。
 *
 * ⚠️ 「可见 Domain 范围不能越权」：两个读端点都按用户业务范围过滤，前端**不再自行放宽**；
 *   补第三类（已启用但当前无正式计划版本的域）时，若 active-plan 取数失败则**不落第三态**
 *   （那会把「取不到」说成「没有」）。
 *
 * 取数（三个读面，各自独立降级，失败按块提示不清空其它块）：
 *   1. GET /api/overview/active-plan        → 各 Domain 当前 ACTIVE 版本
 *   2. GET /api/governance/runs[/paged]      → 最近一次 FULL 运行（3号位）
 *   3. GET /api/domain-status/{runId}       → 该 Run 的各域本次结果
 */

import { computed, onMounted } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { useRouter } from 'vue-router'
import { useOverviewStore } from '@/store/modules/aps/overview'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { RUN_STATUS_LABEL, RUN_STATUS_TAG, RUN_TYPE_LABEL } from '@/store/modules/aps/run'
import {
  RUN_DOMAIN_ISSUE_STATUSES,
  RUN_DOMAIN_STATUS_LABELS,
  RUN_DOMAIN_STATUS_TAG,
  type RunDomainStatus,
  type RunDomainStatusItem,
  type RunStatus,
  type RunType
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
const { errors, activeVersions, latestFullRun, runDomainStatuses } = storeToRefs(overviewStore)
const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()

/** 时间容缺：后端 WhenWritingNull 会整段省略字段 */
function fmtTime(v: string | null | undefined, pattern = 'YYYY-MM-DD HH:mm'): string {
  return v ? dayjs(v).format(pattern) : '—'
}

function humanLabel(domainKey: string | undefined | null): string {
  return domainStore.label(domainKey)
}

/* ===== 块① 最近一次 FULL 运行 ===== */
const run = computed(() => latestFullRun.value)

const runTypeLabel = computed(() =>
  run.value ? (RUN_TYPE_LABEL[run.value.runType as RunType] ?? run.value.runType) : '—'
)
const runStatusLabel = computed(() =>
  run.value ? (RUN_STATUS_LABEL[run.value.status as RunStatus] ?? run.value.status) : '—'
)
const runStatusTag = computed(() =>
  run.value ? (RUN_STATUS_TAG[run.value.status as RunStatus] ?? 'info') : 'info'
)

/** 各域本次结果 vs 各域当前 ACTIVE（两列分列，见文件头警示） */
interface DomainVersionRow {
  domainKey: string
  name: string
  /** 本次运行生成的版本号；本次未生成（失败/被阻断/未参与）时为 null */
  thisRunVersionCode: string | null
  /** 本次该域状态 */
  thisRunStatus: string | null
  /** 该域当前生效版本号；无 ACTIVE 时为 null */
  activeVersionCode: string | null
}

/** active-plan 取数失败 ⇒ 不落「无正式计划版本」第三态（那会把「取不到」说成「没有」） */
const activePlanFailed = computed(() => errors.value.some((e) => e.section === '当前生效版本'))

const domainRows = computed<DomainVersionRow[]>(() => {
  const runByKey = new Map<string, RunDomainStatusItem>()
  for (const d of runDomainStatuses.value) runByKey.set(d.domainKey, d)

  const activeByKey = new Map<string, string>()
  for (const v of activeVersions.value) {
    if (v.domainKey) activeByKey.set(v.domainKey, v.versionCode)
  }

  const keys = new Set<string>([...runByKey.keys(), ...activeByKey.keys()])
  // 第三类：已启用但当前无正式计划版本的域（5号位 2026-11-17 复函B6 §三：既非失败也非「没有任务」）
  if (!activePlanFailed.value) {
    for (const d of domainStore.activeDomains) {
      if (!activeByKey.has(d.domainKey)) keys.add(d.domainKey)
    }
  }

  return [...keys].map((key) => {
    const r = runByKey.get(key)
    return {
      domainKey: key,
      name: humanLabel(key),
      thisRunVersionCode: r?.planVersionCode ?? null,
      thisRunStatus: r?.status ?? null,
      activeVersionCode: activeByKey.get(key) ?? null
    }
  })
})

const domainStatusLabel = (status: string | null): string =>
  status ? (RUN_DOMAIN_STATUS_LABELS[status as RunDomainStatus] ?? status) : '—'
const domainStatusTag = (status: string | null) =>
  status ? (RUN_DOMAIN_STATUS_TAG[status as RunDomainStatus] ?? 'info') : 'info'

/* ===== 块② 本次运行需关注（仅失败 / 部分成功） ===== */
const issueStatuses = new Set<string>(RUN_DOMAIN_ISSUE_STATUSES)

/** 稿 P01：只有 FAILED / PARTIAL_SUCCESS 才出现「本次运行需关注」 */
const needsAttention = computed(() => {
  const s = run.value?.status
  return s === 'FAILED' || s === 'PARTIAL_SUCCESS'
})

/** 实际受影响的 Domain（FAILED=根因失败，BLOCKED=被上游拖累） */
const affectedDomains = computed(() =>
  runDomainStatuses.value.filter((d) => issueStatuses.has(d.status))
)

/* ===== 块③ 业务快捷入口（只显示可访问入口） ===== */
interface QuickEntry {
  key: string
  title: string
  desc: string
  path: string
  icon: string
  /** 访问该页所需的功能权限码（与路由 apsRequiredPermissions 对齐） */
  permission: string
}

const QUICK_ENTRIES: QuickEntry[] = [
  {
    key: 'order',
    title: '订单交付总览',
    desc: '按排程域看订单交期、延期与未排产情况',
    path: '/aps/order',
    icon: 'vi-mdi:package-variant-closed',
    permission: 'aps.plan.view'
  },
  {
    key: 'gantt',
    title: '生产任务与甘特',
    desc: '看排产任务的时间安排与资源占用',
    path: '/aps/gantt',
    icon: 'vi-mdi:chart-gantt',
    permission: 'aps.plan.view'
  },
  {
    key: 'procurement',
    title: '采购协同',
    desc: '看采购与供给的承接、缺口情况',
    path: '/aps/procurement',
    icon: 'vi-mdi:truck-delivery-outline',
    permission: 'aps.plan.view'
  },
  {
    key: 'pegging',
    title: '供需追溯',
    desc: '从订单往下追到每一条供给来源',
    path: '/aps/pegging-trace',
    icon: 'vi-mdi:source-branch',
    permission: 'aps.plan.view'
  },
  {
    key: 'pi',
    title: '生产指示位置',
    desc: '看生产指示在各个工序的分布与位置',
    path: '/aps/pi',
    icon: 'vi-mdi:map-marker-path',
    permission: 'aps.plan.view'
  },
  {
    key: 'explanation',
    title: '异常与原因解释',
    desc: '查排产异常的业务事实与原因',
    path: '/aps/explanation',
    icon: 'vi-mdi:alert-circle-outline',
    permission: 'aps.plan.view'
  },
  {
    key: 'protection',
    title: '需求保护',
    desc: '维护需求优先级与保护规则',
    path: '/aps/demand-protection',
    icon: 'vi-mdi:shield-check-outline',
    permission: 'aps.plan.view'
  },
  {
    key: 'ctp',
    title: '场景评估',
    desc: '评估新订单、插单或资源变化的影响',
    path: '/aps/ctp',
    icon: 'vi-mdi:vector-triangle',
    permission: 'aps.ctp.evaluate'
  },
  {
    key: 'candidate',
    title: '候选版本确认',
    desc: '确认或采用评估产生的候选计划版本',
    path: '/aps/candidate',
    icon: 'vi-mdi:file-check-outline',
    permission: 'aps.candidate.confirm'
  },
  {
    key: 'run',
    title: '运行与版本管理',
    desc: '查看排产运行记录与各域执行结果',
    path: '/aps/run',
    icon: 'vi-mdi:play-box-outline',
    permission: 'aps.plan.view'
  }
]

/** 只显示可访问入口（无权者点了会跳 403 / 被守卫拦下） */
const quickEntries = computed(() => QUICK_ENTRIES.filter((e) => apsAuth.has(e.permission)))

function go(path: string): void {
  void router.push(path)
}

function refresh(): void {
  void overviewStore.load()
}

onMounted(() => {
  if (!domainStore.loadedAt) void domainStore.load()
  refresh()
})
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

    <!-- ===== 块① 最近一次 FULL 运行信息 ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>最近一次全量排产（FULL）运行</span>
          <div class="panel-header-right">
            <ElTag v-if="run" :type="runStatusTag" size="small">{{ runStatusLabel }}</ElTag>
            <button type="button" class="refresh-btn" @click="refresh">刷新</button>
          </div>
        </div>
      </template>

      <ElEmpty v-if="!run" description="尚无 FULL 记录" :image-size="70">
        <p class="empty-hint">
          系统还没有完成过一次全量排产。完成夜间全量计算后，这里会显示最近一次的运行结果。
        </p>
      </ElEmpty>

      <template v-else>
        <ElDescriptions :column="4" size="small" border class="run-desc">
          <ElDescriptionsItem label="运行编号">{{ run.id }}</ElDescriptionsItem>
          <ElDescriptionsItem label="运行类型">{{ runTypeLabel }}</ElDescriptionsItem>
          <ElDescriptionsItem label="开始时间">{{ fmtTime(run.startedAt) }}</ElDescriptionsItem>
          <ElDescriptionsItem label="结束时间">{{ fmtTime(run.completedAt) }}</ElDescriptionsItem>
          <ElDescriptionsItem label="数据截至时间">
            {{ fmtTime(run.dataCutoffTime) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="运行状态">
            <ElTag :type="runStatusTag" size="small">{{ runStatusLabel }}</ElTag>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="触发来源">{{ run.triggeredBy ?? '—' }}</ElDescriptionsItem>
          <ElDescriptionsItem label="运行状态说明">
            {{ run.errorMessage ?? '—' }}
          </ElDescriptionsItem>
        </ElDescriptions>

        <p class="panel-hint">
          下表按排程域分列「本次运行生成的版本」与「当前生效版本」：本次失败或被阻断的排程域，
          其较早的生效版本仍可能继续生效，两者不是同一回事。
        </p>

        <div v-if="!domainRows.length" class="panel-empty">
          <ElEmpty description="本次运行没有排程域结果" :image-size="70" />
        </div>
        <table v-else class="domain-table">
          <thead>
            <tr>
              <th>排程域</th>
              <th>本次运行状态</th>
              <th>本次运行生成的版本</th>
              <th>当前生效版本</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in domainRows" :key="row.domainKey">
              <td>{{ row.name }}</td>
              <td>
                <ElTag :type="domainStatusTag(row.thisRunStatus)" size="small">
                  {{ domainStatusLabel(row.thisRunStatus) }}
                </ElTag>
              </td>
              <td>
                <span v-if="row.thisRunVersionCode">{{ row.thisRunVersionCode }}</span>
                <span v-else class="muted">本次未生成</span>
              </td>
              <td>
                <span v-if="row.activeVersionCode">{{ row.activeVersionCode }}</span>
                <span v-else class="muted">无正式计划版本</span>
              </td>
            </tr>
          </tbody>
        </table>
      </template>
    </ElCard>

    <!-- ===== 块② 本次运行需关注（仅失败 / 部分成功） ===== -->
    <ElCard v-if="needsAttention" class="panel">
      <template #header>
        <div class="panel-header">
          <span>本次运行需关注</span>
          <ElTag :type="runStatusTag" size="small">
            {{ run?.status === 'FAILED' ? '本次 FULL 失败' : '本次 FULL 部分成功' }}
          </ElTag>
        </div>
      </template>

      <div v-if="run?.errorMessage" class="attention-msg">{{ run.errorMessage }}</div>

      <p class="panel-hint">实际受影响的排程域：</p>
      <div v-if="!affectedDomains.length" class="panel-empty">
        <ElEmpty description="后端未登记具体受影响的排程域" :image-size="60" />
      </div>
      <table v-else class="domain-table">
        <thead>
          <tr>
            <th>排程域</th>
            <th>结果</th>
            <th>说明</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in affectedDomains" :key="d.domainKey">
            <td>{{ humanLabel(d.domainKey) }}</td>
            <td>
              <ElTag :type="domainStatusTag(d.status)" size="small">
                {{ domainStatusLabel(d.status) }}
              </ElTag>
            </td>
            <td>
              <span v-if="d.reason">{{ d.reason }}</span>
              <span v-else class="muted">后端未提供失败说明</span>
            </td>
          </tr>
        </tbody>
      </table>

      <p class="panel-hint attention-foot">
        需要重算或查看完整运行记录，请到「运行与版本管理」页处理。
      </p>
    </ElCard>

    <!-- ===== 块③ 业务快捷入口（只显示可访问入口） ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>业务快捷入口</span>
        </div>
      </template>
      <div v-if="!quickEntries.length" class="panel-empty">
        <ElEmpty description="当前账号没有可访问的业务页面" :image-size="70" />
      </div>
      <div v-else class="entry-grid">
        <button
          v-for="e in quickEntries"
          :key="e.key"
          type="button"
          class="entry-card"
          @click="go(e.path)"
        >
          <span class="entry-icon"><Icon :icon="e.icon" /></span>
          <span class="entry-body">
            <span class="entry-title">{{ e.title }}</span>
            <span class="entry-desc">{{ e.desc }}</span>
          </span>
        </button>
      </div>
    </ElCard>
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
  padding: 16px 0;
}

.panel-hint {
  margin: 12px 0 8px;
  font-size: 12px;
  color: #94a3b8;
  line-height: 1.7;
}

.empty-hint {
  margin: 0;
  font-size: 13px;
  line-height: 1.7;
  color: #94a3b8;
}

.refresh-btn {
  padding: 3px 10px;
  font-size: 12px;
  color: #475569;
  cursor: pointer;
  background: #fff;
  border: 1px solid #e2e8f0;
  border-radius: 4px;

  &:hover {
    color: #2563eb;
    border-color: #93c5fd;
  }
}

/* 运行概要 */
.run-desc {
  :deep(.el-descriptions__label) {
    width: 96px;
    color: #94a3b8;
  }

  :deep(.el-descriptions__content) {
    color: #475569;
  }
}

/* 排程域表 */
.domain-table {
  width: 100%;
  border-collapse: collapse;

  th,
  td {
    padding: 9px 12px;
    font-size: 13px;
    text-align: left;
    border-bottom: 1px solid #f1f5f9;
  }

  th {
    font-weight: 500;
    color: #64748b;
    background: #f8fafc;
  }

  tbody tr:hover {
    background: #f8fafc;
  }
}

.muted {
  color: #94a3b8;
}

/* 需关注 */
.attention-msg {
  padding: 10px 12px;
  font-size: 13px;
  line-height: 1.7;
  color: #b91c1c;
  background: #fef2f2;
  border-left: 3px solid #ef4444;
  border-radius: 4px;
}

.attention-foot {
  margin-top: 10px;
}

/* 快捷入口 */
.entry-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 12px;
}

.entry-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  text-align: left;
  cursor: pointer;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  transition:
    border-color 0.15s,
    box-shadow 0.15s;

  &:hover {
    border-color: #93c5fd;
    box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
  }

  .entry-icon {
    font-size: 22px;
    color: #3b82f6;
  }

  .entry-body {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .entry-title {
    font-size: 14px;
    font-weight: 600;
    color: #1e293b;
  }

  .entry-desc {
    font-size: 12px;
    color: #94a3b8;
  }
}
</style>
