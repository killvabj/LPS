<script setup lang="ts">
/**
 * APS V1 4号位 — PI 位置（`/api/pi-position`）
 *
 * 2026-10-17 重做。原先本页整体建立在一份**手写 8 条 PI** 的假数据上（`PI-2026-0001`…`0008`），
 * 页面上 13 个字段（ERP 剩余 / 位置合计 / 差异 / 主位置 / 未分配数量 / 采购占位 / 供给数 / MES 资格 …）
 * 后端一个都没有，且 `mesEligible` 是前端按「无 UNLOCATED + 无占位 = 可下发」推断出来的。
 * 按 5号位 2026-10-17 回执整块重做：
 *  - 页面只回答一个问题：**这个 PI 的剩余量现在在哪些位置上**
 *  - 供给构成（9 类 SupplyType）整块下线 —— 冻结 `07:4123` 明令禁止把 INTERPLANT_TRANSIT 当 Supply 身份，
 *    且真实供给在 `SupplyFact_Pipeline`、不在本页
 *  - MES 资格只保留 U16（`positionType === 'UNLOCATED'` → 不可下发），判据取后端字段本身，不推断；
 *    U17（无 PI 规划任务）/ U18（采购占位）在本页**没有承载字段，不显示**
 *
 * 计划版本必填且由用户显式选择（5号位 2026-10-17 回执 §五）：默认取 `/api/schedule/versions` 第一条（最新）。
 */

import { computed, onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import { usePiStore } from '@/store/modules/aps/pi'
import { formatUtcDateTime } from '@/utils/datetime'
import { PI_POSITION_TYPES, PLAN_VERSION_STATUS_LABELS } from '@/api/aps-v1'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElEmpty,
  ElInput,
  ElOption,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const piStore = usePiStore()
const {
  versions,
  planVersionId,
  summary,
  list,
  detailRows,
  detailOpen,
  currentPiNo,
  filter,
  pageIndex,
  pageSize,
  versionsLoading,
  listLoading,
  detailLoading,
  error,
  detailError,
  totalPositions,
  unlocatedCount,
  unlocatedQty,
  pageIssueRows,
  hasFilter
} = storeToRefs(piStore)

/* ===== 位置类型字典 =====
 * 6 值全预留（0号位 裁决中）。未知值降级显示原值、不报错 —— 后端随时可能出现第 7 个值。
 */
const POSITION_META: Record<
  string,
  { label: string; tag: 'success' | 'warning' | 'info' | 'primary' | 'danger' }
> = {
  STAGE_WAITING: { label: 'Stage 在制', tag: 'success' },
  // 冻结名，与 STAGE_WAITING 同概念（纯名称漂移），后端改回冻结名后本行即生效
  STAGE_WIP: { label: 'Stage 在制（冻结名）', tag: 'success' },
  FIRST_STAGE_PENDING: { label: '首工序待开始', tag: 'primary' },
  XC: { label: '线边仓', tag: 'info' },
  INTERPLANT_TRANSIT: { label: '跨厂在途', tag: 'primary' },
  UNLOCATED: { label: '无法定位', tag: 'danger' },
  // 冻结有、后端未实现；实库 0 行，V1 可能恒不出现
  WAITING_INBOUND: { label: '等待入库', tag: 'warning' }
}

function positionLabel(t: string): string {
  return POSITION_META[t]?.label ?? t
}
function positionTag(t: string): 'success' | 'warning' | 'info' | 'primary' | 'danger' {
  return POSITION_META[t]?.tag ?? 'info'
}

const POSITION_OPTIONS: Array<{ value: string; label: string }> = PI_POSITION_TYPES.map((v) => ({
  value: v,
  label: POSITION_META[v].label
}))

const STATUS_LABEL = PLAN_VERSION_STATUS_LABELS
function versionLabel(id: number): string {
  const v = versions.value.find((x) => x.id === id)
  return v ? `${v.versionCode}（${STATUS_LABEL[v.status]}）` : String(id)
}

/* ===== U16：无法定位不可下发 MES =====
 * 判据 = 后端返回的 positionType 本身，不是前端推断（5号位 2026-10-17 回执 §三）。
 */
const UNLOCATED = 'UNLOCATED'
function isUnlocated(t: string): boolean {
  return t === UNLOCATED
}

/* ===== 展示口径 ===== */
const hasPositions = computed(() => (summary.value?.positionTypeCounts?.length ?? 0) > 0)
const piCount = computed(() => summary.value?.piCount ?? 0)

/** 单 PI 位置数量合计（详情抽屉用） */
const detailTotalQty = computed(() => detailRows.value.reduce((s, r) => s + (r.quantity ?? 0), 0))

/* ===== 操作 ===== */
function onVersionChange(id: number): void {
  piStore.setPlanVersion(id)
}
function onFilterChange(
  patch: Partial<{
    productionInstructionNo: string
    materialCode: string
    positionType: string
    stageCode: string
  }>
): void {
  piStore.setFilter(patch)
  void piStore.loadList()
}
function resetFilters(): void {
  piStore.resetFilter()
  void piStore.refresh()
}
function refreshAll(): void {
  void piStore.refresh()
}

onMounted(async () => {
  await piStore.loadVersions()
  await piStore.refresh()
})
</script>

<template>
  <div class="aps-pi">
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="error" />

    <!-- ===== 版本与筛选 ===== -->
    <ElCard class="panel">
      <div class="filter-row">
        <div class="filter-item">
          <span class="filter-label">计划版本</span>
          <ElSelect
            :model-value="planVersionId ?? undefined"
            placeholder="选择版本"
            filterable
            style="width: 300px"
            :loading="versionsLoading"
            @change="onVersionChange"
          >
            <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v.id)" :value="v.id" />
          </ElSelect>
          <ElTooltip content="三个 PI 位置端点都按版本分区，切换版本会重新取数" placement="top">
            <span class="muted hint">按版本分区</span>
          </ElTooltip>
        </div>
        <div class="filter-item">
          <span class="filter-label">生产指示号</span>
          <ElInput
            :model-value="filter.productionInstructionNo"
            placeholder="精确匹配"
            clearable
            style="width: 150px"
            @change="onFilterChange({ productionInstructionNo: $event })"
          />
        </div>
        <div class="filter-item">
          <span class="filter-label">物料编码</span>
          <ElInput
            :model-value="filter.materialCode"
            placeholder="物料编码"
            clearable
            style="width: 190px"
            @change="onFilterChange({ materialCode: $event })"
          />
        </div>
        <div class="filter-item">
          <span class="filter-label">位置类型</span>
          <ElSelect
            :model-value="filter.positionType"
            placeholder="全部"
            clearable
            style="width: 170px"
            @change="onFilterChange({ positionType: $event })"
          >
            <ElOption
              v-for="opt in POSITION_OPTIONS"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <span class="filter-label">当前阶段</span>
          <ElInput
            :model-value="filter.stageCode"
            placeholder="如 BJ_MACH"
            clearable
            style="width: 140px"
            @change="onFilterChange({ stageCode: $event })"
          />
        </div>
        <ElButton @click="resetFilters"> <Icon icon="vi-ep:refresh-left" /> 重置 </ElButton>
        <ElButton :loading="listLoading" @click="refreshAll">
          <Icon icon="vi-ep:refresh" /> 刷新
        </ElButton>
      </div>
    </ElCard>

    <!-- ===== 未选版本 ===== -->
    <ElCard v-if="planVersionId == null" class="panel">
      <ElEmpty description="请先选择一个计划版本" />
    </ElCard>

    <template v-else>
      <!-- ===== KPI（全部来自 /summary 服务端聚合，不是当前页条数）===== -->
      <div class="kpi-strip">
        <div class="kpi-pill">
          <span class="pill-label">生产指示数</span>
          <span class="pill-val">{{ piCount.toLocaleString() }}</span>
        </div>
        <div class="kpi-pill">
          <span class="pill-label">位置条数</span>
          <span class="pill-val">{{ totalPositions.toLocaleString() }}</span>
        </div>
        <div class="kpi-pill danger">
          <span class="pill-label">无法定位（条 / 数量）</span>
          <span class="pill-val"
            >{{ unlocatedCount.toLocaleString() }} / {{ unlocatedQty.toLocaleString() }}</span
          >
        </div>
      </div>

      <!-- ===== 位置类型分布（服务端聚合）===== -->
      <ElCard v-if="hasPositions" class="panel">
        <template #header>
          <span class="panel-title">位置类型分布（全版本口径）</span>
        </template>
        <div class="position-grid">
          <div v-for="t in summary!.positionTypeCounts" :key="t.positionType" class="position-cell">
            <ElTag :type="positionTag(t.positionType)" effect="dark" size="small">
              {{ positionLabel(t.positionType) }}
            </ElTag>
            <span class="position-count">{{ t.count.toLocaleString() }}</span>
            <span class="position-qty">数量 {{ t.totalQuantity.toLocaleString() }}</span>
          </div>
        </div>
      </ElCard>

      <!-- ===== 位置明细 ===== -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>位置明细</span>
            <ElTag size="small" effect="plain">
              本页 {{ list.length }} 条 / 共 {{ totalPositions.toLocaleString() }} 条
            </ElTag>
            <ElTag v-if="hasFilter" size="small" type="warning" effect="plain"
              >筛选后「共 N 条」仍是全版本口径</ElTag
            >
            <ElTag v-if="pageIssueRows > 0" size="small" type="warning" effect="plain">
              本页带问题码 {{ pageIssueRows }} 条
            </ElTag>
          </div>
        </template>
        <ElTable v-loading="listLoading" :data="list" size="small" border stripe>
          <ElTableColumn label="生产指示号" width="150">
            <template #default="{ row }">
              <ElButton
                link
                type="primary"
                @click="piStore.openDetail(row.productionInstructionNo)"
              >
                <code class="code-tag">{{ row.productionInstructionNo }}</code>
              </ElButton>
            </template>
          </ElTableColumn>
          <ElTableColumn
            prop="materialCode"
            label="物料编码"
            min-width="200"
            show-overflow-tooltip
          />
          <ElTableColumn label="位置类型" width="180">
            <template #default="{ row }">
              <ElTag :type="positionTag(row.positionType)" effect="dark" size="small">
                {{ positionLabel(row.positionType) }}
              </ElTag>
              <ElTag v-if="isUnlocated(row.positionType)" type="danger" size="small" effect="plain">
                不可下发 MES
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn prop="quantity" label="位置数量" width="130" align="right" />
          <ElTableColumn label="当前阶段" width="120">
            <template #default="{ row }">
              <span v-if="row.currentStageCode" class="code-tag">{{ row.currentStageCode }}</span>
              <span v-else class="muted">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="位置问题码" width="140">
            <template #default="{ row }">
              <ElTag v-if="row.issueCode" type="warning" size="small" effect="plain">
                {{ row.issueCode }}
              </ElTag>
              <span v-else class="muted">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="快照时间" width="150">
            <template #default="{ row }">
              <span class="muted">{{ formatUtcDateTime(row.createdAt) }}</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="操作" width="90" align="center" fixed="right">
            <template #default="{ row }">
              <ElButton
                size="small"
                link
                type="primary"
                @click="piStore.openDetail(row.productionInstructionNo)"
              >
                <Icon icon="vi-ep:view" /> 位置明细
              </ElButton>
            </template>
          </ElTableColumn>
        </ElTable>

        <div class="pager">
          <!--
            列表端点不返回 total，「共 N 条」只能用 /summary 的全版本口径。
            筛选后真实命中数未知，故筛选态只给「上一页/下一页」，页数由满页判断 ——
            宁可少一个翻页入口，也不给一个算错的总页数。
          -->
          <template v-if="hasFilter">
            <ElButton
              size="small"
              :disabled="pageIndex <= 1 || listLoading"
              @click="piStore.setPage(pageIndex - 1)"
            >
              <Icon icon="vi-ep:arrow-left" /> 上一页
            </ElButton>
            <span class="pager-text">第 {{ pageIndex }} 页 · 本页 {{ list.length }} 条</span>
            <ElButton
              size="small"
              :disabled="list.length < pageSize || listLoading"
              @click="piStore.setPage(pageIndex + 1)"
            >
              下一页 <Icon icon="vi-ep:arrow-right" />
            </ElButton>
          </template>
          <ElPagination
            v-else
            :current-page="pageIndex"
            :page-size="pageSize"
            :total="totalPositions"
            :page-sizes="[50, 100, 200, 500]"
            layout="total, sizes, prev, pager, next, jumper"
            background
            @current-change="(p: number) => piStore.setPage(p)"
            @size-change="(s: number) => piStore.setPage(1, s)"
          />
        </div>
      </ElCard>
    </template>

    <!-- ===== 单 PI 位置明细 Drawer ===== -->
    <ElDrawer
      :model-value="detailOpen"
      direction="rtl"
      size="820px"
      :with-header="false"
      @update:model-value="piStore.closeDetail"
    >
      <div class="drawer-body">
        <div class="drawer-header">
          <div class="drawer-title">
            <code class="code-tag">{{ currentPiNo }}</code>
            <span class="muted">该生产指示的全部位置行</span>
          </div>
          <ElButton @click="piStore.closeDetail"> <Icon icon="vi-ep:close" /> 关闭 </ElButton>
        </div>

        <ElAlert
          v-if="detailError"
          type="warning"
          :closable="false"
          show-icon
          :title="detailError"
        />

        <template v-else>
          <ElDescriptions :column="3" size="small" border>
            <ElDescriptionsItem label="位置行数">{{ detailRows.length }}</ElDescriptionsItem>
            <ElDescriptionsItem label="数量合计">{{
              detailTotalQty.toLocaleString()
            }}</ElDescriptionsItem>
            <ElDescriptionsItem label="计划版本">{{ planVersionId }}</ElDescriptionsItem>
          </ElDescriptions>

          <ElTable v-loading="detailLoading" :data="detailRows" size="small" border>
            <ElTableColumn label="位置类型" width="170">
              <template #default="{ row }">
                <ElTag :type="positionTag(row.positionType)" effect="dark" size="small">
                  {{ positionLabel(row.positionType) }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn prop="quantity" label="位置数量" width="120" align="right" />
            <ElTableColumn label="当前阶段" width="110">
              <template #default="{ row }">
                <span v-if="row.currentStageCode" class="code-tag">{{ row.currentStageCode }}</span>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="位置问题码" width="130">
              <template #default="{ row }">
                <ElTag v-if="row.issueCode" type="warning" size="small" effect="plain">{{
                  row.issueCode
                }}</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="物料编码" min-width="180" show-overflow-tooltip>
              <template #default="{ row }">
                <span class="code-tag">{{ row.materialCode }}</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="快照时间" width="150">
              <template #default="{ row }">
                <span class="muted">{{ formatUtcDateTime(row.createdAt) }}</span>
              </template>
            </ElTableColumn>
          </ElTable>
        </template>
      </div>
    </ElDrawer>
  </div>
</template>

<style lang="less" scoped>
.aps-pi {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.panel {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  gap: 10px;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.panel-title {
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.muted {
  color: #94a3b8;
}

.hint {
  font-size: 12px;
}

.code-tag {
  padding: 2px 6px;
  font-family: 'Courier New', monospace;
  font-size: 12px;
  color: #475569;
  background: #f1f5f9;
  border-radius: 3px;
}

/* 筛选 */
.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  align-items: center;
}

.filter-item {
  display: flex;
  align-items: center;
  gap: 8px;

  .filter-label {
    min-width: 56px;
    font-size: 12px;
    color: #64748b;
  }
}

/* KPI */
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

  &.danger {
    background: #fee2e2;
  }

  .pill-label {
    color: #64748b;
  }

  .pill-val {
    font-weight: 600;
    color: #1e293b;
  }
}

/* 位置类型分布 */
.position-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 12px;
}

.position-cell {
  display: flex;
  padding: 10px 14px;
  background: #f8fafc;
  border-radius: 6px;
  align-items: center;
  gap: 10px;

  .position-count {
    margin-left: auto;
    font-family: 'Courier New', monospace;
    font-size: 18px;
    font-weight: 700;
    color: #1e293b;
  }

  .position-qty {
    font-size: 12px;
    color: #94a3b8;
  }
}

.pager {
  display: flex;
  padding-top: 12px;
  gap: 10px;
  align-items: center;
  justify-content: flex-end;
}

.pager-text {
  font-size: 12px;
  color: #64748b;
}

/* Drawer */
.drawer-body {
  display: flex;
  padding: 16px 24px 32px;
  flex-direction: column;
  gap: 16px;
}

.drawer-header {
  display: flex;
  padding-bottom: 12px;
  border-bottom: 2px solid #e2e8f0;
  justify-content: space-between;
  align-items: center;

  .drawer-title {
    display: flex;
    gap: 8px;
    align-items: center;
  }
}
</style>
