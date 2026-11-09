<script setup lang="ts">
/**
 * APS V1 4号位 — 供需追溯（Supply/Pegging 追溯）
 *
 * 冻结依据：
 *  - `06 v1.38 §7.2` 5号位向4号位提供「Supply/Pegging追溯」
 *  - `09 v2.0 §六` 用户输出验收「Supply/Pegging追溯」
 *  - `03 v2.0` 用户输出边界「Supply/Pegging追溯」
 *  - `13 v1.8 §七` 页面展示「Supply/Pegging追溯」
 *  - `01 v1.13:1913` 历史 PlanVersion 下对应 Pegging 关系
 *
 * 数据面（全部真实端点，本页**零写调用、零前端重算**）：
 *  - 版本下拉  GET /api/schedule/versions
 *  - 汇总      GET /api/pegging-trace/summary      （总分配数 + 供给类型/承诺状态分布）
 *  - 列表      GET /api/pegging-trace               （服务端分页，ORDER BY AllocationSequence）
 *
 * 几条刻意的口径：
 *  - 列表端点**不返 total** ⇒ 分页用「上一页/下一页 + 是否有下一页」，不显示假总页数
 *  - 「总分配数」是**全版本口径**（summary），筛选后不再代表当前结果条数 —— 页面明确标注
 *  - `supplyType` / `commitmentStatus` 的筛选项**从 summary 的分布里取**，不套用别处的枚举：
 *    实测值域（INVENTORY / PIPELINE / WIP / INTER_FACTORY_ORDER）与文档第 13 节那套
 *    「供给来源分类」不是同一集合，硬套会筛出空结果
 *  - 未知值一律原样显示（中文标签查不到就展示原始码），不隐藏、不猜
 *  - 「根订单 ID」走专用端点 `GET /api/pegging-trace/order/{orderId}`：dev v2 实测
 *    `RootOrderNo`/`CurrentOrderNo` 在 200/200 行全为 NULL ⇒ 后端的 `orderNo LIKE` 过滤
 *    在真实数据上必然查空，因此不提供「按订单号模糊查」这个假筛选项
 *  - 承诺状态的 `UNKNOWN` 是后端把 NULL 归并出来的名字（实测汇总 392,705 条），
 *    按它精确过滤会返回 0 条 ⇒ 只在分布条展示，不作为筛选项
 *
 * 权限：`aps.plan.view`（后端 `PeggingTraceController` 同名 Policy），页面只读。
 */

import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { storeToRefs } from 'pinia'
import { usePeggingTraceStore } from '@/store/modules/aps/peggingTrace'
import { formatUtcDateTime } from '@/utils/datetime'
import type { PeggingTraceDto } from '@/api/aps-v1'
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
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus'

const store = usePeggingTraceStore()
const {
  versions,
  planVersionId,
  versionsLoading,
  summary,
  summaryLoading,
  list,
  loading,
  error,
  filter,
  pageIndex,
  hasMore
} = storeToRefs(store)

/* ===== 字典（未知值原样显示）===== */
const SUPPLY_TYPE_LABELS: Record<string, string> = {
  INVENTORY: '库存',
  PIPELINE: '在途管道',
  WIP: '在制',
  INTER_FACTORY_ORDER: '跨厂订单',
  PRODUCTION_INSTRUCTION: '生产指示',
  SHIPPING_INSTRUCTION: '发运指示',
  ARRIVED_NOT_RECEIVED: '到厂未收货',
  PURCHASE_IN_TRANSIT: '采购在途',
  OPEN_PO_REMAINING: '未结 PO 余量',
  VMI_ONSITE: '现场 VMI'
}
const supplyTypeLabel = (v: string | null | undefined): string =>
  v ? (SUPPLY_TYPE_LABELS[v] ?? v) : '未标注'

const COMMITMENT_LABELS: Record<string, string> = {
  COMMITTED: '已承诺',
  CONFIRMED: '已确认',
  ESTIMATED: '估算',
  NOT_COMMITTED: '未承诺',
  UNKNOWN: '未标注'
}
const commitmentLabel = (v: string | null | undefined): string =>
  v ? (COMMITMENT_LABELS[v] ?? v) : '未标注'
const COMMITMENT_TAG_TYPE: Record<string, 'success' | 'warning' | 'danger' | 'info'> = {
  COMMITTED: 'success',
  CONFIRMED: 'success',
  ESTIMATED: 'warning',
  NOT_COMMITTED: 'danger',
  UNKNOWN: 'info'
}
const commitmentTagType = (
  v: string | null | undefined
): 'success' | 'warning' | 'danger' | 'info' => (v ? (COMMITMENT_TAG_TYPE[v] ?? 'info') : 'info')

/** 筛选下拉的选项直接取 summary 的真实分布（无 summary 时只有「全部」） */
const supplyTypeOptions = computed(() => summary.value?.supplyTypeCounts ?? [])
/**
 * 承诺状态筛选项**排除 `UNKNOWN`**：它是后端汇总把数据库 NULL 归并出来的名字，
 * 而列表端点按 `CommitmentStatus = @x` 精确过滤 —— 选 `UNKNOWN` 会查回 0 条
 * （dev v2 实测：汇总 392,705 条 UNKNOWN / 按 UNKNOWN 过滤 0 条）。
 * 分布条里仍照样展示这个计数，只是不让它当筛选项。
 */
/**
 * 承诺状态下拉 —— 选项直接来自汇总返回的 `commitmentStatusCounts`（真实分布，非自拟词表）。
 *
 * ⚠️ 2026-10-19：此前这里对 UNKNOWN 做过剔除（当时后端「可看不可筛」——汇总报 UNKNOWN
 * 392,705 条、列表却永远筛不出来，挂上去就是「选了必空」的假筛选，故先摘掉）。
 * 5号位 2026-10-19 已把汇总与列表统一到同一条 SQL 口径（`@CommitmentStatus='UNKNOWN'`
 * 特判命中 `CommitmentStatus IS NULL`），dev 实测 `commitmentStatus=UNKNOWN` 命中 100 行、
 * `RECEIVED` 命中 0 行 ⇒ 筛选已真生效，**剔除逻辑删回、不要再加回**。
 * 探针 PT03b 锁这一点（断言「无剔除」，不是断言某句正向写法——见脚本注释）。
 */
const commitmentOptions = computed(() => summary.value?.commitmentStatusCounts ?? [])

const num = (v: number | null | undefined): string =>
  v == null ? '-' : Number(v).toLocaleString('zh-CN', { maximumFractionDigits: 3 })

/* ===== 详情 Drawer ===== */
const selected = ref<PeggingTraceDto | null>(null)
const drawerOpen = ref(false)
function openDetail(row: PeggingTraceDto): void {
  selected.value = row
  drawerOpen.value = true
}

/* ===== 加载 ===== */
async function refresh(): Promise<void> {
  await store.loadVersions()
  if (planVersionId.value != null) {
    await Promise.all([store.loadSummary(), store.loadList()])
  }
}

async function onVersionChange(id: number): Promise<void> {
  await store.setPlanVersion(id)
}

onMounted(refresh)
onBeforeUnmount(() => store.reset())
</script>

<template>
  <div class="pt-page">
    <ElAlert
      v-if="error"
      type="error"
      :closable="false"
      show-icon
      :title="error"
      class="error-alert"
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
          <label>计划版本</label>
          <ElSelect
            :model-value="planVersionId ?? undefined"
            :loading="versionsLoading"
            placeholder="选择计划版本"
            style="width: 260px"
            @change="onVersionChange"
          >
            <ElOption
              v-for="v in versions"
              :key="v.id"
              :label="`${v.versionCode}（${v.status}）`"
              :value="v.id"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <label>物料编码</label>
          <ElInput
            v-model="filter.materialCode"
            placeholder="模糊匹配"
            clearable
            style="width: 180px"
          />
        </div>
        <div class="filter-item">
          <label>供给类型</label>
          <ElSelect
            v-model="filter.supplyType"
            placeholder="全部类型"
            clearable
            style="width: 160px"
          >
            <ElOption
              v-for="s in supplyTypeOptions"
              :key="s.supplyType"
              :label="supplyTypeLabel(s.supplyType)"
              :value="s.supplyType"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <label>承诺状态</label>
          <ElSelect
            v-model="filter.commitmentStatus"
            placeholder="全部状态"
            clearable
            style="width: 150px"
          >
            <ElOption
              v-for="s in commitmentOptions"
              :key="s.commitmentStatus"
              :label="commitmentLabel(s.commitmentStatus)"
              :value="s.commitmentStatus"
            />
          </ElSelect>
        </div>
        <div class="filter-item">
          <label>根订单 ID</label>
          <ElInput
            v-model="filter.rootOrderId"
            placeholder="数字，精确匹配"
            clearable
            style="width: 160px"
          />
        </div>
        <div class="filter-item">
          <label>供给单据号</label>
          <ElInput
            v-model="filter.supplyDocumentNo"
            placeholder="模糊匹配"
            clearable
            style="width: 170px"
          />
        </div>
        <div class="filter-actions">
          <ElButton @click="store.resetFilter()">重置</ElButton>
          <ElButton type="primary" :loading="loading" @click="store.applyFilter()">筛选</ElButton>
        </div>
      </div>
    </ElCard>

    <!-- ===== 分配分布（全版本口径）===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <span>分配分布</span>
          <span class="hint-text">全版本口径，不随下方筛选变化</span>
        </div>
      </template>

      <div v-if="summaryLoading" class="muted">加载中…</div>
      <div v-else-if="!summary" class="muted">该版本的分配汇总暂不可用（列表仍可查询）</div>
      <div v-else class="dist">
        <div class="dist-total">
          <span class="dist-label">分配总条数</span>
          <strong class="dist-value">{{ num(summary.totalAllocations) }}</strong>
        </div>
        <div class="dist-group">
          <span class="dist-label">按供给类型</span>
          <ElTag
            v-for="s in summary.supplyTypeCounts"
            :key="s.supplyType"
            class="dist-tag"
            type="info"
            effect="plain"
          >
            {{ supplyTypeLabel(s.supplyType) }} {{ num(s.count) }} 条 / 数量
            {{ num(s.totalAllocatedQty) }}
          </ElTag>
          <span v-if="!summary.supplyTypeCounts.length" class="muted">无</span>
        </div>
        <div class="dist-group">
          <span class="dist-label">按承诺状态</span>
          <ElTag
            v-for="s in summary.commitmentStatusCounts"
            :key="s.commitmentStatus"
            class="dist-tag"
            :type="commitmentTagType(s.commitmentStatus)"
            effect="plain"
          >
            {{ commitmentLabel(s.commitmentStatus) }} {{ num(s.count) }} 条
          </ElTag>
          <span v-if="!summary.commitmentStatusCounts.length" class="muted">无</span>
        </div>
      </div>
    </ElCard>

    <!-- ===== 分配明细 ===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <span>分配明细</span>
          <span class="hint-text">
            第 {{ pageIndex }} 页 · 本页 {{ list.length }} 条
            <template v-if="store.hasFilter">（已筛选）</template>
            <template v-else>（未筛选）</template>
          </span>
        </div>
      </template>

      <ElEmpty v-if="!list.length && !loading" description="当前条件下没有 Pegging 分配记录" />
      <ElTable
        v-else
        :data="list"
        :loading="loading"
        border
        stripe
        size="small"
        row-key="id"
        class="pt-table"
        @row-click="openDetail"
      >
        <ElTableColumn prop="allocationSequence" label="分配序号" width="90" align="right" />
        <ElTableColumn label="根订单" min-width="130">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.rootOrderNo ?? row.rootOrderId ?? '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="当前需求" min-width="130">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.currentOrderNo ?? row.currentOrderId ?? '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="materialCode" label="物料编码" min-width="190" />
        <ElTableColumn prop="demandFactoryCode" label="需求工厂" width="90" />
        <ElTableColumn label="需求数量" width="110" align="right">
          <template #default="{ row }: { row: PeggingTraceDto }">{{ num(row.demandQty) }}</template>
        </ElTableColumn>
        <ElTableColumn label="分配数量" width="110" align="right">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ num(row.allocatedQty) }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="供给类型" width="120">
          <template #default="{ row }: { row: PeggingTraceDto }">
            <ElTag size="small" effect="plain">{{ supplyTypeLabel(row.supplyType) }}</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="supplyFactoryCode" label="供给工厂" width="90" />
        <ElTableColumn label="供给单据" min-width="150">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.supplyDocumentNo ?? '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="承诺状态" width="110">
          <template #default="{ row }: { row: PeggingTraceDto }">
            <ElTag size="small" :type="commitmentTagType(row.commitmentStatus)" effect="plain">
              {{ commitmentLabel(row.commitmentStatus) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="ETA" width="160">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ formatUtcDateTime(row.eta) || '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="可用时间" width="160">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ formatUtcDateTime(row.knownAvailableTime) || '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="分配时间" width="160">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ formatUtcDateTime(row.createdAt) || '-' }}
          </template>
        </ElTableColumn>
      </ElTable>

      <div class="pager">
        <ElButton :disabled="pageIndex <= 1 || loading" @click="store.prevPage()">上一页</ElButton>
        <span class="pager-info">
          第 {{ pageIndex }} 页
          <span v-if="!hasMore" class="muted">（已到末页）</span>
        </span>
        <ElButton :disabled="!hasMore || loading" @click="store.nextPage()">下一页</ElButton>
      </div>
    </ElCard>

    <!-- ===== 明细 Drawer ===== -->
    <ElDrawer v-model="drawerOpen" title="分配明细" size="560px">
      <template v-if="selected">
        <ElDescriptions :column="1" size="small" border>
          <ElDescriptionsItem label="分配 ID">{{ selected.id }}</ElDescriptionsItem>
          <ElDescriptionsItem label="分配序号">{{
            selected.allocationSequence
          }}</ElDescriptionsItem>
          <ElDescriptionsItem label="计划版本">{{ selected.planVersionId }}</ElDescriptionsItem>
          <ElDescriptionsItem label="排程运行">{{ selected.scheduleRunId }}</ElDescriptionsItem>
          <ElDescriptionsItem label="根订单">
            {{ selected.rootOrderNo ?? selected.rootOrderId ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="当前订单">
            {{ selected.currentOrderNo ?? selected.currentOrderId ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="订单类型">{{ selected.orderType ?? '-' }}</ElDescriptionsItem>
          <ElDescriptionsItem label="物料">
            {{ selected.materialCode }}（ID {{ selected.materialId }}）
          </ElDescriptionsItem>
          <ElDescriptionsItem label="需求工厂 / 阶段">
            {{ selected.demandFactoryCode ?? '-' }} / {{ selected.demandStageCode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="需求数量">{{ num(selected.demandQty) }}</ElDescriptionsItem>
          <ElDescriptionsItem label="分配数量">{{ num(selected.allocatedQty) }}</ElDescriptionsItem>
          <ElDescriptionsItem label="供给类型">
            {{ supplyTypeLabel(selected.supplyType) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="供给工厂 / 仓库">
            {{ selected.supplyFactoryCode ?? '-' }} / {{ selected.supplyWarehouseCode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="供给方式">
            {{ selected.supplyMode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="供给单据">
            {{ selected.supplyDocumentNo ?? '-' }}
            <span class="muted">（{{ selected.supplyDocumentType ?? '无类型' }}）</span>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="ERP 属性">{{
            selected.eRPProperty ?? '-'
          }}</ElDescriptionsItem>
          <ElDescriptionsItem label="承诺状态">
            {{ commitmentLabel(selected.commitmentStatus) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="ETA">
            {{ formatUtcDateTime(selected.eta) || '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="已知可用时间">
            {{ formatUtcDateTime(selected.knownAvailableTime) || '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="跨厂边 / 运输提前期">
            {{ selected.crossFactoryEdgeId ?? '-' }} /
            {{ selected.transportLeadTimeHours ?? '-' }} 小时
          </ElDescriptionsItem>
          <ElDescriptionsItem label="Stage 路径">
            承接 {{ selected.attachStageCode ?? '-' }} · 已完成
            {{ selected.completedStageCode ?? '-' }} · 下一需
            {{ selected.nextRequiredStageCode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="分配时间">
            {{ formatUtcDateTime(selected.createdAt) || '-' }}
          </ElDescriptionsItem>
        </ElDescriptions>
      </template>
      <ElEmpty v-else description="请选择一条分配记录" />
    </ElDrawer>
  </div>
</template>

<style lang="less" scoped>
.pt-page {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.error-alert {
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

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 16px;
  align-items: flex-end;
}

.filter-item {
  display: flex;
  flex-direction: column;
  gap: 6px;

  label {
    font-size: 12px;
    color: #64748b;
  }
}

.filter-actions {
  display: flex;
  gap: 8px;
  margin-left: auto;
}

.dist {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 28px;
  align-items: center;
}

.dist-total,
.dist-group {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.dist-label {
  font-size: 12px;
  color: #64748b;
}

.dist-value {
  font-size: 20px;
  color: #1e293b;
}

.dist-tag {
  margin: 0;
}

.muted {
  color: #94a3b8;
  font-size: 12px;
}

.pt-table {
  :deep(.el-table__row) {
    cursor: pointer;
  }
}

.pager {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}

.pager-info {
  font-size: 12px;
  color: #475569;
}
</style>
