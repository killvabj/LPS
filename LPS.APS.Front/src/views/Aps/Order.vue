<script setup lang="ts">
/**
 * APS V1 4号位 — 订单/需求计划（页面 2）
 *
 * 冻结依据：
 *  - `06 v1.38 §7.2`：5号位向4号位提供「订单/需求计划」查询
 *  - `13 v1.8 §七`：用户输出展示含「订单计划」；`§十一` 禁止页面内计算排程结果
 *  - Order.Status 值域取「数据流向」口径：`07:3474` 的 `Order_Canonical.Status`
 *    = `OPEN/CLOSED/CANCELLED`，`sp_SyncOrdersToPartitionTable` 原样复制进 `[Order].Status`
 *    （`07:3611` 另有 6 值写法，与复制逻辑冲突，待 0号位 澄清；见 `types/common.ts`）
 *  - `07:2666` DelayStatus 值域 `ON_TIME/FIRST_DELAY/REPEATED_DELAY`
 *    （与 Status 是两个独立维度，禁止混用）
 *
 * 2026-10-17 按 5号位 回执整页重做（此前页面挂在一份自拟 DTO 上）：
 *  - 列表字段按真实 `OrderListItemDto`（`id` / `quantity` / `mtS_InstructionNo` / `priority` …）
 *  - 详情按真实 `{order, pegging, tasks}`（**没有 reasons 键**）
 *  - 硬编码的工厂 / 产品族 / 客户下拉（`F-SUZ-01`、`INJECTION`、`CUST-001` 等）全部删除 ——
 *    dev 实库是 `CN` / `CYL` / 中文客户名，与那份列表**零重合**，按它筛必然空
 *  - 「风险」卡下线（`riskCount` 查的是另一张表，实测恒 0，区分不了「无风险」与「无数据」）
 *  - 「未排」卡下线（后端 SQL 写死 `CAST(0 AS INT)`，是字面量不是查出来的数）
 *
 * 写接口：本页查询部分只读；**唯一写入口 = §10A.1「已有订单提前」**
 * （EXISTING_ORDER_ADVANCE → POST /api/governance/run/candidate，Policy aps.plan.run）
 * @see 4号位实施包 v1.4 §十A
 */

import { computed, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useOrderStore } from '@/store/modules/aps/order'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { triggerBusinessEntry } from '@/api/aps-v1'
import { formatUtcDateTime, formatUtcMonthDay, formatUtcMonthDayHM } from '@/utils/datetime'
import type { BusinessEntrySubmission, OrderListItemDto } from '@/api/aps-v1'
import OrderAdvanceDialog from './components/OrderAdvanceDialog.vue'
import {
  ORDER_STATUS_LABELS,
  ORDER_DELAY_STATUSES,
  ORDER_DELAY_STATUS_LABELS,
  ORDER_STATUS_FILTER_OPTIONS,
  PLAN_VERSION_STATUS_LABELS,
  REASON_CODE_LABELS,
  normalizeOrderStatus,
  type DomainKey,
  type OrderDelayStatus,
  type OrderStatusDisplay,
  type ReasonCode,
  type SupplyType
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
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
  ElTag
} from 'element-plus'

const orderStore = useOrderStore()
const {
  list,
  listTotal,
  pageIndex,
  pageSize,
  listLoading,
  listError,
  summary,
  uncalculatedCount,
  currentDetail,
  detailLoading,
  detailError,
  versions,
  versionsLoading,
  planVersionId
} = storeToRefs(orderStore)

const drawerVisible = ref(false)

// ===== 延迟状态配色（冻结 07:2666 三值；未知值走 defaultColor 降级）=====
const DELAY_COLORS: Record<OrderDelayStatus, string> = {
  ON_TIME: '#059669',
  FIRST_DELAY: '#ef4444',
  REPEATED_DELAY: '#c2410c'
}
const DELAY_TAG_TYPE: Record<OrderDelayStatus, 'success' | 'danger' | 'warning'> = {
  ON_TIME: 'success',
  FIRST_DELAY: 'danger',
  REPEATED_DELAY: 'warning'
}

/** 延迟状态未知值降级：原样显示、不做枚举校验（实库可能出现冻结词表外的值） */
function delayLabel(raw: string | undefined): string {
  if (!raw) return '—'
  return ORDER_DELAY_STATUS_LABELS[raw as OrderDelayStatus] ?? raw
}
function delayColor(raw: string | undefined): string {
  return DELAY_COLORS[raw as OrderDelayStatus] ?? '#94a3b8'
}
function delayTagType(raw: string | undefined): 'success' | 'danger' | 'warning' | 'info' {
  return DELAY_TAG_TYPE[raw as OrderDelayStatus] ?? 'info'
}

/**
 * 主因代码标签 —— 复用冻结 `07 v5.1.13 §八.1` 的 15 码字典，与甘特页 / CTP 页同一口径
 * （不维护第二套自拟码表）。
 *
 * ⚠️ dev 2026-10-19 实测：绝大多数返回值**不在**冻结 15 码内（`BATCH_POLICY_MISSING`、中文散文等），
 * 而冻结 07:4935 明文禁止这两类。故未入字典一律降级成「待核对·<原值>」——
 * 摆出原值是诚实，臆造中文名才是造假（该漂移已出函 5号位，前端不替它兜底成正式码）。
 */
function delayReasonLabel(code: string | undefined): string {
  if (!code) return '—'
  return REASON_CODE_LABELS[code as ReasonCode] ?? `待核对·${code}`
}
/** 延期时长 → 天/小时两态（后端给的是自然小时小数；`!= null` 而非真值判断，0 与「无值」是两态） */
function delayHoursText(hours: number | undefined): string {
  if (hours == null) return ''
  const abs = Math.abs(hours)
  if (abs < 24) return `超期 ${abs.toFixed(1)} 小时`
  return `超期 ${(abs / 24).toFixed(1)} 天`
}

/** 订单状态标签：按冻结 07:3611 六值词表渲染（实库 `Open` 归一回历史遗留 `OPEN`）；仍不认识则原样显示 */
function statusLabel(raw: string): string {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_LABELS[norm] : raw
}
const ORDER_STATUS_TAG: Record<OrderStatusDisplay, 'success' | 'info' | 'warning' | 'primary'> = {
  NEW: 'info',
  PLANNED: 'primary',
  CONFIRMED: 'success',
  IN_PROGRESS: 'success',
  COMPLETED: 'info',
  CANCELLED: 'warning',
  OPEN: 'success'
}
function statusTagType(raw: string): 'success' | 'info' | 'warning' | 'primary' {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_TAG[norm] : 'info'
}

// ===== Pegging 供应类型标签（冻结 07 的 9 类 SupplyType；未知值原样显示）=====
const SUPPLY_LABELS: Record<SupplyType, string> = {
  INVENTORY: '库存',
  PI: 'PI 在制',
  PO: 'PO 在途',
  VMI: 'VMI 寄售',
  ARRIVED_NOT_INBOUND: '到货未入库',
  INTERPLANT_TRANSIT: '跨厂在途',
  RECEIVED: '已收货',
  PLANNED_PRODUCTION: '计划生产',
  PLANNING_PURCHASE_PLACEHOLDER: '采购估算（占位）'
}
const supplyLabel = (raw: string | undefined): string =>
  raw ? (SUPPLY_LABELS[raw as SupplyType] ?? raw) : '—'

/** 任务状态：冻结 TASK_STATUSES 5 态（common.ts），未知值原样显示 */
const TASK_STATUS_LABELS: Record<string, string> = {
  PLANNED: '已排产',
  RELEASED: '已释放',
  IN_PROGRESS: '执行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消'
}

const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()

/** Domain 选项：从真实 domainStore 派生（非 mock 时即后端 Domain 列表），U41 按 dataScope 收敛 */
const DOMAIN_OPTIONS = computed<Array<{ value: DomainKey | 'ALL'; label: string }>>(() => {
  const all = domainStore.activeDomains.map((d) => ({ value: d.domainKey, label: d.domainName }))
  const allowed = apsAuth.dataScope.domainKeys as readonly string[]
  const list = allowed.length === 0 ? all : all.filter((o) => allowed.includes(o.value))
  return [{ value: 'ALL' as const, label: '全部' }, ...list]
})

// ===== 筛选输入（全部对应后端真实 Query 参数）=====
const orderNoInput = ref('')
const piInput = ref('')
const materialInput = ref('')
/** 客户：无 distinct 端点，且后端是 `CustomerName LIKE %x%` ⇒ 自由文本，不造下拉选项 */
const customerNameInput = ref('')
/** 工厂 / 产品族：后端是**精确匹配**（`f.Code =` / `pf.Code =`），无 distinct 端点 ⇒ 自由文本 */
const factoryCodeInput = ref('')
const productFamilyInput = ref('')
const dueDateRange = ref<[string, string] | undefined>()
const domainCode = ref<string | undefined>()
const delayStatusInput = ref<OrderDelayStatus | undefined>()
const statusInput = ref<OrderStatusDisplay | undefined>()

watch(
  [orderNoInput, piInput, materialInput, customerNameInput, factoryCodeInput, productFamilyInput],
  () => {
    orderStore.setFilter({
      orderNo: orderNoInput.value || undefined,
      productionInstructionNo: piInput.value || undefined,
      materialCode: materialInput.value || undefined,
      customerName: customerNameInput.value || undefined,
      factoryCode: factoryCodeInput.value || undefined,
      productFamilyCode: productFamilyInput.value || undefined
    })
    void orderStore.loadList(true)
  }
)

function onDueRangeChange(v: [string, string] | null | undefined): void {
  dueDateRange.value = v ?? undefined
  orderStore.setFilter({ dueDateFrom: v?.[0], dueDateTo: v?.[1] })
  void orderStore.loadList(true)
}

function onDomainChange(v: string): void {
  const val = v && v !== 'ALL' ? v : undefined
  domainCode.value = val
  orderStore.setFilter({ domainKey: val })
  void orderStore.loadList(true)
}

function onDelayStatusChange(v: OrderDelayStatus | undefined): void {
  delayStatusInput.value = v
  orderStore.setFilter({ delayStatus: v })
  void orderStore.loadList(true)
}

function onStatusChange(v: OrderStatusDisplay | undefined): void {
  statusInput.value = v
  orderStore.setFilter({ status: v })
  void orderStore.loadList(true)
}

function onVersionChange(v: number): void {
  void orderStore.setPlanVersion(v)
}

function onResetSearch(): void {
  orderNoInput.value = ''
  piInput.value = ''
  materialInput.value = ''
  customerNameInput.value = ''
  factoryCodeInput.value = ''
  productFamilyInput.value = ''
  dueDateRange.value = undefined
  domainCode.value = undefined
  delayStatusInput.value = undefined
  statusInput.value = undefined
  orderStore.clearFilter()
  void orderStore.loadList(true)
}

// ===== 版本下拉标签 =====
function versionLabel(id: number): string {
  const v = versions.value.find((x) => x.id === id)
  return v ? `${v.versionCode}（${PLAN_VERSION_STATUS_LABELS[v.status] ?? v.status}）` : String(id)
}

/** 顶部版本摘要（真实版本的时间窗 / 状态），别让用户对着一串裸 id 猜 */
const currentVersionLabel = computed(() => {
  const v = orderStore.currentVersion
  if (!v) return ''
  const s =
    v.planHorizonStart && v.planHorizonEnd
      ? ` ｜ 时间窗 ${formatUtcMonthDay(v.planHorizonStart)} ~ ${formatUtcMonthDay(v.planHorizonEnd)}`
      : ''
  return `${v.domainKey ?? ''} ｜ 版本 #${v.id}${s}`
})

// ===== KPI（全部取 /api/order-query/summary 的服务端聚合）=====
const totalCount = computed(() => summary.value?.totalCount ?? null)
const kpis = computed(() => {
  const s = summary.value
  if (!s) return []
  const items = [
    { key: 'total', label: '订单总数', value: s.totalCount, color: '#334155' },
    { key: 'ontime', label: '未延迟', value: s.onTimeCount, color: DELAY_COLORS.ON_TIME },
    { key: 'delayed', label: '延迟', value: s.delayedCount, color: DELAY_COLORS.FIRST_DELAY }
  ]
  // 「未计算」= DelayStatus 为 NULL 的行。2026-10-17 起全表已回填（NULL=0），
  // 常驻一张 0 卡片只是噪音 ⇒ 只有真的存在未计算行时才显示（既暴露问题，也不天天摆 0）。
  if (uncalculatedCount.value > 0) {
    items.push({
      key: 'unknown',
      label: '未计算',
      value: uncalculatedCount.value,
      color: '#94a3b8'
    })
  }
  return items
})
const kpiRatio = (v: number): number =>
  totalCount.value && totalCount.value > 0 ? Math.round((v / totalCount.value) * 100) : 0

// ===== 页码 =====
function onPageChange(p: number): void {
  orderStore.setPage({ pageIndex: p })
  void orderStore.loadList()
}
function onSizeChange(s: number): void {
  orderStore.setPage({ pageIndex: 1, pageSize: s })
  void orderStore.loadList()
}

// ===== 行点击：打开抽屉 =====
async function openDetail(orderId: number): Promise<void> {
  drawerVisible.value = true
  await orderStore.loadDetail(orderId)
}
function closeDetail(): void {
  drawerVisible.value = false
}
function onRowClick(row: OrderListItemDto, column?: { type?: string }): void {
  if (column?.type === 'selection') return
  void openDetail(row.id)
}

// ===== §10A.1 已有订单提前（EXISTING_ORDER_ADVANCE）=====
const router = useRouter()
const canPlanRun = computed(() => apsAuth.has('aps.plan.run'))

const advanceVisible = ref(false)
const advanceSubmitting = ref(false)
const selectedOrders = ref<OrderListItemDto[]>([])
const tableRef = ref<InstanceType<typeof ElTable> | null>(null)

/** Dialog 候选行（仅取所需字段；orderCanonicalId 自动带入由 Dialog watch 处理） */
const advanceCandidates = computed(() =>
  list.value.map((o) => ({
    orderId: o.id,
    orderNo: o.orderNo,
    orderCanonicalId: o.orderCanonicalId,
    materialCode: o.materialCode,
    customerName: o.customerName,
    customerDueDate: o.customerDueDate
  }))
)

const advanceDomainOptions = computed<DomainKey[]>(() =>
  DOMAIN_OPTIONS.value.filter((o) => o.value !== 'ALL').map((o) => o.value as DomainKey)
)

const advanceDefaultDomain = computed<DomainKey | undefined>(() => {
  if (domainCode.value) return domainCode.value
  return advanceDomainOptions.value[0]
})

function onOrderSelectionChange(rows: OrderListItemDto[]): void {
  selectedOrders.value = rows
}

function openAdvanceDialog(): void {
  if (selectedOrders.value.length === 0) {
    ElMessage.warning('请先在列表中勾选要提前的订单')
    return
  }
  advanceVisible.value = true
}

async function submitOrderAdvance(payload: BusinessEntrySubmission): Promise<void> {
  advanceSubmitting.value = true
  try {
    const result = await triggerBusinessEntry({
      trigger: payload.trigger,
      domainKey: payload.domainKey,
      draft: payload.draft,
      actor: apsAuth.userInfo?.userCode ?? 'unknown'
    })
    ElMessage.success(
      `已发起：Run#${result.newRunId}，Candidate PlanVersion#${result.candidatePlanVersionId}。跳转对比页…`
    )
    advanceVisible.value = false
    selectedOrders.value = []
    tableRef.value?.clearSelection()
    router.push({
      name: 'ApsCandidate',
      query: {
        candidatePlanVersionId: String(result.candidatePlanVersionId),
        basePlanVersionId: String(result.basePlanVersionId),
        source: 'order-advance'
      }
    })
  } catch (err) {
    ElMessage.error(`发起订单提前失败：${(err as Error)?.message ?? '未知错误'}`)
  } finally {
    advanceSubmitting.value = false
  }
}

/** 交期显示（后端 DateTime 无时区后缀 → 按 UTC 解析，见 utils/datetime） */
function fmtDue(due: string): string {
  const dueMs = new Date(`${due}Z`).getTime()
  if (Number.isNaN(dueMs)) return due
  const days = Math.round((dueMs - Date.now()) / 86400000)
  const text = formatUtcMonthDay(due)
  return days >= 0 ? `${text}（剩 ${days} 天）` : `${text}（已过 ${-days} 天）`
}
const isOverdue = (due: string): boolean => new Date(`${due}Z`).getTime() < Date.now()

const detailOrder = computed(() => currentDetail.value?.order ?? null)
const detailPegging = computed(() => currentDetail.value?.pegging ?? [])
const detailTasks = computed(() => currentDetail.value?.tasks ?? [])

// ===== 加载入口 =====
onMounted(async () => {
  await orderStore.loadVersions()
  await orderStore.refresh(true)
})
</script>

<template>
  <div class="aps-order">
    <!-- ===== 版本选择 + 写入口 ===== -->
    <div class="toolbar">
      <ElSelect
        :model-value="planVersionId ?? undefined"
        placeholder="选择计划版本"
        class="version-select"
        filterable
        :loading="versionsLoading"
        @change="onVersionChange"
      >
        <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v.id)" :value="v.id" />
      </ElSelect>
      <div class="toolbar-meta">
        <span v-if="currentVersionLabel" class="muted">{{ currentVersionLabel }}</span>
      </div>
      <!-- §10A.1 已有订单提前：5 个独立中文业务入口中的本页唯一入口 -->
      <ElButton
        type="warning"
        plain
        :disabled="!canPlanRun || selectedOrders.length === 0"
        :title="canPlanRun ? '已有订单提前 — 先在列表勾选订单' : '当前账号无排程执行权限'"
        @click="openAdvanceDialog"
      >
        <Icon icon="vi-mdi:calendar-arrow-right" />
        已有订单提前（{{ selectedOrders.length }}）
      </ElButton>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert
      v-if="listError"
      type="error"
      :closable="false"
      show-icon
      :title="`加载失败：${listError}`"
    />

    <!-- ===== KPI（取 /summary 服务端聚合；取不到整块空态）===== -->
    <div v-if="kpis.length" class="kpi-row">
      <div v-for="k in kpis" :key="k.key" class="kpi-card" :style="{ borderTopColor: k.color }">
        <div class="kpi-label">{{ k.label }}</div>
        <div class="kpi-value" :style="{ color: k.color }">{{ k.value }}</div>
        <div class="kpi-ratio">{{ kpiRatio(k.value) }}%</div>
      </div>
    </div>

    <!-- ===== 过滤栏 ===== -->
    <ElCard class="filter-bar">
      <div class="filter-row">
        <ElInput v-model="orderNoInput" placeholder="订单号" clearable class="filter-sm" />
        <ElInput v-model="piInput" placeholder="MTS 生产指令号" clearable class="filter-sm" />
        <ElInput v-model="materialInput" placeholder="物料编码" clearable class="filter-sm" />
        <ElInput
          v-model="customerNameInput"
          placeholder="客户名称（模糊）"
          clearable
          class="filter-sm"
        />
        <ElInput
          v-model="factoryCodeInput"
          placeholder="工厂编码（精确）"
          clearable
          class="filter-sm"
        />
        <ElInput
          v-model="productFamilyInput"
          placeholder="产品族编码（精确）"
          clearable
          class="filter-sm"
        />
        <ElDatePicker
          v-model="dueDateRange"
          type="daterange"
          range-separator="~"
          start-placeholder="交期起"
          end-placeholder="交期止"
          value-format="YYYY-MM-DD"
          class="filter-date"
          @change="onDueRangeChange"
        />
        <ElSelect
          :model-value="delayStatusInput"
          placeholder="延迟状态"
          clearable
          class="filter-select-sm"
          @change="onDelayStatusChange"
        >
          <ElOption
            v-for="d in ORDER_DELAY_STATUSES"
            :key="d"
            :label="ORDER_DELAY_STATUS_LABELS[d]"
            :value="d"
          />
        </ElSelect>
        <ElSelect
          :model-value="statusInput"
          placeholder="订单状态"
          clearable
          class="filter-select-sm"
          @change="onStatusChange"
        >
          <ElOption
            v-for="s in ORDER_STATUS_FILTER_OPTIONS"
            :key="s"
            :label="ORDER_STATUS_LABELS[s]"
            :value="s"
          />
        </ElSelect>
        <ElSelect
          :model-value="domainCode ?? 'ALL'"
          placeholder="排程域"
          class="filter-select-sm"
          @change="onDomainChange"
        >
          <ElOption v-for="d in DOMAIN_OPTIONS" :key="d.value" :label="d.label" :value="d.value" />
        </ElSelect>
        <ElButton @click="onResetSearch">重置</ElButton>
      </div>
    </ElCard>

    <!-- ===== 订单表 ===== -->
    <ElCard class="table-card">
      <template #header>
        <div class="panel-header">
          <span>订单列表</span>
          <span class="header-total">{{ orderStore.filteredTotalLabel }}</span>
        </div>
      </template>
      <ElTable
        ref="tableRef"
        v-loading="listLoading"
        :data="list"
        stripe
        size="small"
        row-key="id"
        empty-text="暂无订单"
        @row-click="onRowClick"
        @selection-change="onOrderSelectionChange"
      >
        <ElTableColumn type="selection" width="44" :selectable="() => canPlanRun" />
        <ElTableColumn prop="orderNo" label="订单号" min-width="130" show-overflow-tooltip />
        <ElTableColumn prop="mtS_InstructionNo" label="MTS 指令号" width="110" />
        <ElTableColumn prop="materialCode" label="物料编码" min-width="160" show-overflow-tooltip />
        <ElTableColumn label="客户" min-width="130" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.customerName">{{ row.customerName }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="productFamilyCode" label="产品族" width="90" />
        <ElTableColumn prop="factoryCode" label="工厂" width="80" />
        <ElTableColumn prop="quantity" label="数量" width="100" align="right">
          <template #default="{ row }">
            <span>{{ row.quantity }}</span>
            <span class="muted"> {{ row.uom }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="客户交期" width="150">
          <template #default="{ row }">
            <span :class="{ overdue: isOverdue(row.customerDueDate) }">
              {{ fmtDue(row.customerDueDate) }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="priority" label="优先级" width="80" align="right" />
        <ElTableColumn label="订单状态" width="90" align="center">
          <template #default="{ row }">
            <ElTag size="small" :type="statusTagType(row.status)">{{
              statusLabel(row.status)
            }}</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="延迟状态" width="100" align="center">
          <template #default="{ row }">
            <ElTag
              size="small"
              effect="light"
              :type="delayTagType(row.delayStatus)"
              :style="{
                background: delayColor(row.delayStatus),
                color: '#fff',
                borderColor: delayColor(row.delayStatus)
              }"
            >
              {{ delayLabel(row.delayStatus) }}
            </ElTag>
            <div v-if="delayHoursText(row.delayHours)" class="muted delay-hours">
              {{ delayHoursText(row.delayHours) }}
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn label="延期原因" width="180" show-overflow-tooltip>
          <template #default="{ row }">
            <span :class="{ 'reason-unchecked': !!row.primaryReasonCode }">
              {{ delayReasonLabel(row.primaryReasonCode) }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="70" align="center" fixed="right">
          <template #default="{ row }">
            <a href="javascript:;" @click.stop="openDetail(row.id)">详情</a>
          </template>
        </ElTableColumn>
        <template #empty>
          <ElEmpty description="该计划版本下没有订单" />
        </template>
      </ElTable>
      <div class="pager-row">
        <ElPagination
          v-model:current-page="pageIndex"
          v-model:page-size="pageSize"
          :total="listTotal"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          background
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </ElCard>

    <!-- ===== 详情 Drawer ===== -->
    <ElDrawer v-model="drawerVisible" size="620px" direction="rtl" @close="closeDetail">
      <template #header>
        <div class="drawer-head">
          <span class="drawer-title">订单详情</span>
          <span v-if="detailOrder" class="drawer-sub">{{ detailOrder.orderNo }}</span>
        </div>
      </template>

      <div v-if="detailLoading" class="drawer-loading"></div>

      <template v-else-if="detailOrder">
        <!-- ===== 订单基本信息 ===== -->
        <div class="detail-section">
          <h4 class="section-title">基本信息</h4>
          <ElDescriptions :column="2" size="small" border>
            <ElDescriptionsItem label="订单号">{{ detailOrder.orderNo }}</ElDescriptionsItem>
            <ElDescriptionsItem label="MTS 指令号">
              {{ detailOrder.mtS_InstructionNo ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="物料编码">
              {{ detailOrder.materialCode }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户">
              {{ detailOrder.customerName ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户类别">
              {{ detailOrder.customerSegment ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="工厂">
              {{ detailOrder.factoryCode ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="产品族">
              {{ detailOrder.productFamilyCode ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="排程域">
              {{ detailOrder.domainKey ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单数量">
              {{ detailOrder.quantity }} {{ detailOrder.uom }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="优先级">
              {{ detailOrder.priority }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户交期">
              {{ formatUtcDateTime(detailOrder.customerDueDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="承诺交期">
              {{ formatUtcDateTime(detailOrder.promisedDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单状态">
              <ElTag size="small" :type="statusTagType(detailOrder.status)">
                {{ statusLabel(detailOrder.status) }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延迟状态">
              <ElTag size="small" :type="delayTagType(detailOrder.delayStatus)">
                {{ delayLabel(detailOrder.delayStatus) }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="APS 预计完成">
              {{ formatUtcDateTime(detailOrder.plannedCompletionTime) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期时长">
              {{ delayHoursText(detailOrder.delayHours) || '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期原因" :span="2">
              <span :class="{ 'reason-unchecked': !!detailOrder.primaryReasonCode }">{{
                delayReasonLabel(detailOrder.primaryReasonCode)
              }}</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单类型">
              {{ detailOrder.orderType }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="BOM 版本">
              {{ detailOrder.bomno ?? '—' }}
            </ElDescriptionsItem>
          </ElDescriptions>
        </div>

        <!-- ===== Pegging 供应承接 ===== -->
        <div class="detail-section">
          <h4 class="section-title">Pegging 供应承接</h4>
          <ElTable :data="detailPegging" size="small" border>
            <ElTableColumn prop="allocationSequence" label="序号" width="60" align="right" />
            <ElTableColumn label="来源类型" min-width="110">
              <template #default="{ row }">{{ supplyLabel(row.supplyType) }}</template>
            </ElTableColumn>
            <ElTableColumn prop="materialCode" label="物料" min-width="140" show-overflow-tooltip />
            <ElTableColumn prop="allocatedQty" label="承接数量" width="90" align="right" />
            <ElTableColumn prop="supplyFactoryCode" label="来源工厂" width="90" />
            <ElTableColumn prop="supplyWarehouseCode" label="来源仓库" width="100" />
            <ElTableColumn label="单据" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="row.supplyDocumentNo">
                  {{ row.supplyDocumentNo }}
                  <span v-if="row.supplyDocumentType" class="muted">
                    / {{ row.supplyDocumentType }}</span
                  >
                </span>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="ETA" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.eta) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="可用时间" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.knownAvailableTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="commitmentStatus" label="履约承诺" width="90" />
            <ElTableColumn prop="supplyMode" label="供应方式" width="90" />
            <template #empty>
              <ElEmpty description="该订单暂无 Pegging 承接记录" :image-size="60" />
            </template>
          </ElTable>
        </div>

        <!-- ===== 生产任务 ===== -->
        <div class="detail-section">
          <h4 class="section-title">生产任务</h4>
          <ElTable :data="detailTasks" size="small" border>
            <ElTableColumn prop="taskNo" label="任务号" width="110" show-overflow-tooltip />
            <ElTableColumn label="工序" width="120">
              <template #default="{ row }">
                {{ row.operationCode ?? '—' }} (Seq {{ row.operationSeq }})
              </template>
            </ElTableColumn>
            <ElTableColumn label="资源" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.resourceName ?? row.resourceCode ?? '—' }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="quantity" label="净产出" width="80" align="right" />
            <ElTableColumn label="计划加工" width="90" align="right">
              <template #default="{ row }">
                <span :class="{ 'hl-warn': row.plannedProcessQty !== row.quantity }">
                  {{ row.plannedProcessQty ?? '—' }}
                </span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="开始" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedStartTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="结束" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedEndTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="状态" width="90">
              <template #default="{ row }">
                <ElTag size="small">{{ TASK_STATUS_LABELS[row.status] ?? row.status }}</ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="关键路径" width="80" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isCriticalPath" size="small" type="danger">关键</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="锁定" width="60" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isLocked" size="small" type="warning">锁</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <template #empty>
              <ElEmpty description="该订单暂无生产任务" :image-size="60" />
            </template>
          </ElTable>
          <p class="u05-hint">
            净产出与计划加工是两个不同口径：计划加工含良率损耗，净产出为最终交付数量。
          </p>
        </div>
      </template>

      <ElEmpty v-else :description="detailError || '请选择订单查看详情'" />
    </ElDrawer>

    <!-- ===== §10A.1 已有订单提前 Dialog（EXISTING_ORDER_ADVANCE）===== -->
    <OrderAdvanceDialog
      :visible="advanceVisible"
      :submitting="advanceSubmitting"
      :domain-options="advanceDomainOptions"
      :orders="advanceCandidates"
      :preselected-order-ids="selectedOrders.map((o) => o.id)"
      :default-domain-key="advanceDefaultDomain"
      @update:visible="(v) => (advanceVisible = v)"
      @submit="submitOrderAdvance"
    />
  </div>
</template>

<style lang="less" scoped>
.aps-order {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;

  .version-select {
    width: 380px;
  }

  .toolbar-meta {
    flex: 1;
    font-size: 12px;
  }
}

.muted {
  color: #94a3b8;
}

.delay-hours {
  font-size: 12px;
  line-height: 1.4;
}

/* 未入冻结 15 码字典的主因码：中性灰，不与正式码同色，避免被当成已确认原因 */
.reason-unchecked {
  color: #94a3b8;
  font-style: italic;
}

.kpi-row {
  display: grid;
  /* 卡片数会随「未计算」一项增减，用 auto-fit 避免留空格 */
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

.filter-bar {
  border-radius: 8px;
}

.filter-row {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
}

.filter-sm {
  width: 175px;
}

.filter-select-sm {
  width: 145px;
}

.filter-date {
  width: 250px;
}

.table-card {
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

.header-total {
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
}

.pager-row {
  display: flex;
  margin-top: 12px;
  justify-content: flex-end;
}

.overdue {
  font-weight: 600;
  color: #ef4444;
}

.hl-warn {
  font-weight: 600;
  color: #f59e0b;
}

.u05-hint {
  padding: 8px 10px;
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #64748b;
  background: #f8fafc;
  border-left: 3px solid #3b82f6;
  border-radius: 4px;
}

.drawer-head {
  display: flex;
  gap: 12px;
  align-items: baseline;

  .drawer-title {
    font-size: 16px;
    font-weight: 600;
    color: #1e293b;
  }

  .drawer-sub {
    font-size: 12px;
    color: #94a3b8;
  }
}

.detail-section {
  margin-bottom: 18px;

  .section-title {
    display: flex;
    margin: 0 0 8px;
    font-size: 13px;
    font-weight: 600;
    color: #475569;
    align-items: center;
    gap: 8px;
  }
}

.drawer-loading {
  height: 200px;
}
</style>
