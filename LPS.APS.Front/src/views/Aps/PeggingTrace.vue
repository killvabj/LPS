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
 * ---- 2026-11-13 工厂视角改版（这一版是给「计划员/物料员」用的）----
 *
 * 工厂用户在这一页只问两个问题：
 *  ① 「我这张订单的料，是从哪来的？齐了吗？还差多少？」
 *  ② 「这个物料 / 这张在制单，给了谁？」
 *  ⇒ 页面按这两个问题重排：
 *  - **来源指标条**：库存 / 在制 / 在途 / 跨厂订单四个可点卡片（条数取后端真分布），
 *    点一下就按来源筛；工厂用户第一眼要知道「我的料从哪来」。
 *  - **搜索条一行**：订单号、订单 ID、物料编码、来源单据号、承诺状态；**改了就生效**
 *    （文字去抖 400ms，下拉即时），不再让人记得点「筛选」。
 *  - **两种读法自动切换**：
 *      · 没指定订单 ⇒ 「分配明细」表，一行一笔分配（原始事实），分页浏览；
 *      · 指定了订单 ⇒ 「订单齐套」表，一行一种物料：需求 / 已分配 / **缺口** / 来源。
 *    缺口只在订单口径下给：那时取数是完整的（一次 take=500，不分页），缺口才算得对；
 *    分页浏览时跨页的订单会被切碎，缺口会算错 ⇒ 不给。
 *
 * 几条刻意的口径：
 *  - 列表端点**不返 total** ⇒ **无筛选时**总页数用 summary 的全版本条数换算；
 *    **有筛选时算不出来就不显示**（编总数＝说谎）
 *  - summary 的 COUNT **不过用户数据范围**（列表 SQL 带 `DemandFactoryCode IN @AllowedFactories`，
 *    summary 不带）⇒ 受限账号看到的总页数是**全版本页数**；翻到末页后前端改用
 *    **账号实际可见**的条数（那时才确知）
 *  - 「总分配数」是**当前选中的这个计划版本**的全部分配笔数（summary 只按 PlanVersionId 计数，
 *    不是所有版本相加），筛选后不再代表当前结果条数 —— 页面明确标注
 *  - `supplyType` / `commitmentStatus` 的筛选项**从 summary 的分布里取**，不套用别处的枚举：
 *    实测值域（INVENTORY / PIPELINE / WIP / INTER_FACTORY_ORDER）与文档第 13 节那套
 *    「供给来源分类」不是同一集合，硬套会筛出空结果
 *  - 未知值一律原样显示（中文标签查不到就展示原始码），不隐藏、不猜
 *  - **订单号**：后端 `orderNo` LIKE 命中 `RootOrderNo`/`CurrentOrderNo`。dev 2026-11-13 实测
 *    当前已生效版本的 `RootOrderNo` **已回填**（如 O20714667）⇒ 可按订单号直接查；
 *    较早版本仍全为空（写入侧历史遗留），那种版本下订单号查不到，属实情不是故障。
 *  - **订单 ID**：根订单 Id 精确匹配（后端 `RootOrderId = @RootOrderId`），老版本也能查。
 *  - 承诺状态的 `UNKNOWN` 是后端把 NULL 归并出来的名字；5号位 2026-10-19 已把汇总与列表
 *    统一到同一条口径（`@CommitmentStatus='UNKNOWN'` 特判命中 `CommitmentStatus IS NULL`），
 *    实测 `commitmentStatus=UNKNOWN` 能筛出数据 ⇒ 保留为筛选项（探针 PT03b 锁这一点）。
 *
 * 权限：`aps.plan.view`（后端 `PeggingTraceController` 同名 Policy），页面只读。
 *
 * ---- 2026-11-16 跨厂「供给工厂码」口径裁定后（3号位 回执 v1.0）----
 *
 * 3号位 裁定：`SupplyFactoryCode` ＝**供给方（发货厂）**；源厂未得时落 NULL，**不用目标厂顶替**；
 * 落码在 **2号位**（数量关系唯一修改者），查询侧在 5号位 ⇒ 本页改的是**展示**：
 *  - 对方厂码取到（`supplyFactoryCode ≠ demandFactoryCode`）⇒ 显示「需求厂 → 对方厂码」+「跨厂」标
 *  - 取不到（源厂未得 / 后端未落码）⇒ 如实写「跨厂（对方工厂未提供）」，不拿目标厂冒充
 *  - **跨厂判定仍只看供给类型**（INTER_FACTORY_ORDER），不改成比两个厂码
 *
 * ---- 2026-11-16 5号位《P01～P13＋采购协同 页面—接口实施矩阵 v0.1》A8 行 ----
 *
 * 直连端点不变（`/api/pegging-trace` + `/count` + `/summary`），把**空态**拆成矩阵要的三态：
 *  - total>0 但筛后空 → 「没有符合当前筛选条件的分配记录」
 *  - total=0 → 「这一版没有可见的分配记录」（还没发令 / 排产失败 / 不在数据范围）
 *  - **源批缺** → 这个版本没记录到来源排程批次（`sourceScheduleRunId` 为空）⇒
 *    分配按「当前批次」代理归集，源批没了就归集不到 ⇒ 空态单独说清，不混进「没数据」
 *
 * ---- 2026-11-17 5号位 r14240/r14241：供给批次**显式位**已落码（A8 换判据）----
 *
 * 后端在 `/api/pegging-trace/summary` 给了 `supplyBatchPresent` / `supplyBatchNo`：
 *  - `supplyBatchPresent=true` ⇒ 供给批次在 ⇒ 这一版 0 笔是**明确零**（供给在、只是还没归集到分配）
 *  - `supplyBatchPresent=false` ⇒ **源批缺**（供给侧缺源）⇒ 空态说清「没有可用的供给事实批次」
 *  - `supplyBatchPresent` 缺/null ⇒ **未判定** ⇒ 页面不替它下结论，**不落「源批缺」**
 *
 * 这层与「版本级 `sourceScheduleRunId` 为空」**并存、不合并**（两层说的不是一回事）：
 * 前者＝供给事实批次在不在；后者＝这个版本有没有记到来源排程批次。
 *
 * ---- 2026-11-20 5号位 复函 D1：仓库改走**供给事实行**（不再用 PSA 的 supplyWarehouseCode）----
 *
 * 依据 5号位-2026-11-20-复函-P01-P13收口v0.2清单十七项逐条裁决_三条可投影已落码B1F1J1_其余不可原因与后移给足-给4号位抄3号位.md §一 D1：
 *  - PSA 的 `supplyWarehouseCode` 实测 **99,564/99,564 为空（全 0 填充）** ⇒ **不得**当仓库显示
 *    （显示它等于凭空断言「这批货在 X 仓」）⇒ 抽屉里的「供给工厂 / 仓库」改走
 *    `composables/useSupplyWarehouse.ts`（按 PO × 物料到供给事实行查仓码/仓名）。
 *  - 查不到 / 同一 PO+物料命中**多个不同仓库** ⇒ 显「未提供」+ 说明，**不猜**。
 */

import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { usePeggingTraceStore, type PeggingGroupRow } from '@/store/modules/aps/peggingTrace'
import { useDomainStore } from '@/store/modules/aps/domain'
import { commitmentStatusLabel } from '@/api/aps-v1/codeLabels'
import { formatUtcDateTime } from '@/utils/datetime'
import { useSupplyWarehouse } from './composables/useSupplyWarehouse'
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
  ElTag,
  ElTooltip
} from 'element-plus'

const store = usePeggingTraceStore()
const domainStore = useDomainStore()
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
  pageSize,
  hasMore,
  orderScopeTruncated,
  currentVersion,
  isOrderScope,
  totalAllocations,
  totalPages,
  filteredTotal,
  supplyTypeOptions,
  commitmentOptions,
  groupRows
} = storeToRefs(store)

/* ===== 三态（矩阵 A8）：total>0 / total=0 / 源批缺 =====
 * 「源批缺」＝ 这个计划版本**没有记录到来源排程批次**（`sourceScheduleRunId` 为空）。
 * 判据取后端给的版本字段，不自算；追调用例里的 `IsActive=现批代理` 正是这个意思：
 * 分配行是按「当前批次」代理归集的，源批都没了 ⇒ 归集不到这一版的分配。
 */
const sourceBatchMissing = computed(() => {
  const v = currentVersion.value
  if (!v) return false
  return v.sourceScheduleRunId == null
})

/* ===== 供给批次三态（2026-11-17 5号位 r14240/r14241 落码，矩阵 A8）=====
 *
 * 供给侧给的是**显式位** `summary.supplyBatchPresent`（三态，不是布尔）：
 *   true ⇒ 供给批次在（有可归集的供给事实来源）；`supplyBatchNo` 给出批次号
 *   false ⇒ **源批缺**：没有可用的供给事实批次 ⇒ 这一版归集不到分配记录
 *   null / 键缺 ⇒ **未判定**：后端没给这一位 ⇒ 页面不得替它下结论（不落「源批缺」）
 *
 * ⚠️ 与上面的版本级 `sourceBatchMissing`（版本有没有记到来源排程批次）是**两层判据**：
 *   一层说「供给事实批次在不在」，一层说「这个版本有没有源 Run」⇒ 两层并存，不合并成一句话。
 */
type SupplyBatchState = 'present' | 'absent' | 'unknown'

const supplyBatchState = computed<SupplyBatchState>(() => {
  const s = summary.value
  if (!s) return 'unknown'
  const p = s.supplyBatchPresent
  if (p === true) return 'present'
  if (p === false) return 'absent'
  return 'unknown'
})

/** 供给批次号（后端给了才显示，不自己编） */
const supplyBatchNo = computed<string | null>(() => summary.value?.supplyBatchNo ?? null)

/** 供给批次这一行怎么说（三态各有各的话，不共用一句） */
const supplyBatchText = computed(() => {
  switch (supplyBatchState.value) {
    case 'present':
      return supplyBatchNo.value ? `供给批次 ${supplyBatchNo.value}` : '供给批次在'
    case 'absent':
      return '供给批次缺失（没有可用的供给事实批次）'
    default:
      return summary.value ? '供给批次未判定（后端未给这一位）' : '供给批次未判定（汇总暂不可用）'
  }
})

/* ===== 字典（未知值原样显示）===== */
const SUPPLY_TYPE_LABELS: Record<string, string> = {
  INVENTORY: '库存',
  PIPELINE: '在途',
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

/**
 * 跨厂供给判定：**看供给类型**，不看两个厂码是否不同。
 * 后端 pegging 落库时 `DemandFactoryCode` 与 `SupplyFactoryCode` 写的是同一个值
 * （`PeggingOrchestrator` 两处都取 `a.FactoryCode`），跨厂出荷指示入池时取的又是
 * **目标厂**（= 需求厂）⇒ 两码恒等，比不出跨厂（dev 实测 500 行 0 例不同）。
 *
 * 2026-11-16 3号位 裁定（口径已定，落码在 2号位）：
 *  - `SupplyFactoryCode` ＝**供给方（发货厂）**；源厂未得时落 NULL，**不用目标厂顶替**
 *  - ⇒ 落码后跨厂行的供给厂码会变成真值（≠ 需求厂）；落码前仍是老样（两码相等）
 *  - **跨厂判定口径不变**：仍只看供给类型，不改成比两个厂码
 *    （源厂未得时两个码都可能取不到，比不出来）
 */
const isCrossSupply = (row: PeggingTraceDto): boolean => row.supplyType === 'INTER_FACTORY_ORDER'

/**
 * 对方工厂码（跨厂供给的发货厂）：取不到就返回 null
 *  - 行内 `supplyFactoryCode` 与 `demandFactoryCode` 不同 ⇒ 认定是对方厂
 *  - 源厂未得（后端落 NULL）或仍是老样（两码相等）⇒ null ⇒ 页面如实写「对方工厂未提供」
 */
const otherFactoryCode = (row: PeggingTraceDto): string | null => {
  const s = row.supplyFactoryCode
  const d = row.demandFactoryCode
  return s && s !== d ? s : null
}

/** 履约承诺文案走全站共用词典（订单页 / 采购协同页同源，不各造一份） */
const commitmentLabel = (v: string | null | undefined): string => commitmentStatusLabel(v)
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

/** 版本状态的大白话（与订单页同一套说法） */
const VERSION_STATUS_LABELS: Record<string, string> = {
  ACTIVE: '已生效',
  CANDIDATE: '算完待确认',
  BUILDING: '还没发令',
  Computing: '计算中',
  FAILED: '排产失败',
  ARCHIVED: '已被顶替'
}
const versionStatusLabel = (v: string | null | undefined): string =>
  v ? (VERSION_STATUS_LABELS[v] ?? v) : '状态未知'

/**
 * 版本下拉文案：版本号 · 状态 · 域（工厂用户靠域区分不同产品线）
 *
 * 域显示中文名（内部 domainKey 不给用户看）；字典取不到时回退原码，
 * 那是「后端没给中文名」的实情，不猜。
 */
function versionLabel(v: {
  versionCode: string
  status?: string | null
  domainKey?: string | null
}): string {
  const parts = [v.versionCode, versionStatusLabel(v.status)]
  if (v.domainKey) parts.push(domainStore.label(v.domainKey))
  return parts.join(' · ')
}

const num = (v: number | null | undefined): string =>
  v == null ? '-' : Number(v).toLocaleString('zh-CN', { maximumFractionDigits: 3 })

/**
 * 总分配笔数的显示文本
 *
 * ⚠️ null = **汇总取不到**（不是 0）：§0.2 ②「NULL=未知，不得转 0」。
 *   汇总接口失败时写「未提供」，不写 0 笔（那会说成「这一版明确没有分配」）。
 */
const totalAllocationsText = computed(() =>
  totalAllocations.value === null ? '未提供' : num(totalAllocations.value)
)

/** 数量（可能带小数，去掉无意义的尾零） */
const qty = (v: number | null | undefined): string =>
  v == null ? '-' : Number(v).toLocaleString('zh-CN', { maximumFractionDigits: 6 })

/** 分配记录写入时间（同一版本内所有行相同 ⇒ 放版本条里说明，不占表格列） */
const allocWriteTime = computed(() =>
  list.value[0] ? formatUtcDateTime(list.value[0].createdAt) : ''
)

/** 版本状态提示：用户看到空页面时，第一反应是「系统坏了」，其实是这一版没发令/失败 */
const versionNotice = computed<{ type: 'error' | 'warning' | 'info'; text: string } | null>(() => {
  const v = currentVersion.value
  if (!v) return null
  switch (v.status) {
    case 'FAILED':
      return {
        type: 'error',
        text: '这一版排产失败，没有分配结果。订单本身还在，换一版看。'
      }
    case 'Computing':
      return { type: 'warning', text: '这一版还在计算中，分配结果可能不完整，过一会儿再看。' }
    case 'BUILDING':
      return { type: 'warning', text: '这一版还没发令，所以没有供需分配记录。换一版看。' }
    case 'ARCHIVED':
      return { type: 'info', text: '这一版已经被更新的版本顶替，看到的是当时的分配结果。' }
    default:
      return null
  }
})

/* ===== 筛选：改了就生效（文字去抖，下拉即时）===== */
let debounceTimer: number | undefined
function scheduleApply(delay = 400): void {
  if (debounceTimer !== undefined) window.clearTimeout(debounceTimer)
  debounceTimer = window.setTimeout(() => {
    debounceTimer = undefined
    void store.applyFilter()
  }, delay)
}
watch(
  () =>
    `${filter.value.orderNo}\u0001${filter.value.rootOrderId}\u0001${filter.value.materialCode}\u0001${filter.value.supplyDocumentNo}`,
  () => scheduleApply()
)
onBeforeUnmount(() => {
  if (debounceTimer !== undefined) window.clearTimeout(debounceTimer)
})

/** 下拉类筛选：立即生效 */
function applyNow(): void {
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer)
    debounceTimer = undefined
  }
  void store.applyFilter()
}

/* ===== 详情 Drawer ===== */
const selected = ref<PeggingTraceDto | null>(null)
const drawerOpen = ref(false)
/**
 * 供给侧仓库解析（2026-11-20；D1）。
 *
 * ⚠️ **PSA 的 `supplyWarehouseCode` 全 0 填充**（5号位 11-20 复函 D1）⇒ 不再拿它当仓库显示；
 *   仓库改由**供给事实行**（采购在途档案的存储地点）解析 ⇒ 查不到显「未提供」。
 */
const { resolve: resolveWarehouses, warehouseText, warehouseHint } = useSupplyWarehouse()
function openDetail(row: PeggingTraceDto): void {
  selected.value = row
  drawerOpen.value = true
  void resolveWarehouses([
    { poNo: row.supplyDocumentNo, materialCode: row.materialCode, materialId: row.materialId }
  ])
}

/** 齐套视图里点某一种物料 ⇒ 打开该物料第一笔分配的明细 */
function openGroupDetail(row: { allocations: PeggingTraceDto[] }): void {
  if (row.allocations.length) openDetail(row.allocations[0])
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

async function onPageSizeChange(size: number): Promise<void> {
  await store.setPageSize(size)
}

onMounted(() => {
  // 域字典：版本下拉里显示中文域名；字典取不到时回退原码（实情，不猜）
  if (!domainStore.loadedAt) void domainStore.load()
  void refresh()
})
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
    <ElAlert
      v-if="versionNotice"
      :type="versionNotice.type"
      :closable="false"
      show-icon
      :title="versionNotice.text"
      class="error-alert"
    />

    <!-- ===== 追溯对象（版本 + 一句话规模）===== -->
    <ElCard class="panel" shadow="never">
      <div class="target-row">
        <div class="target-item">
          <label>计划版本</label>
          <ElSelect
            :model-value="planVersionId ?? undefined"
            :loading="versionsLoading"
            placeholder="选择计划版本"
            style="width: 420px"
            @change="onVersionChange"
          >
            <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v)" :value="v.id" />
          </ElSelect>
        </div>
        <div class="target-facts">
          <span class="fact">
            共 <strong>{{ totalAllocationsText }}</strong> 笔分配
          </span>
          <span
            class="fact"
            :class="{ muted: supplyBatchState !== 'present', warn: supplyBatchState === 'absent' }"
          >
            {{ supplyBatchText }}
          </span>
          <span v-if="allocWriteTime" class="fact">分配记录写入于 {{ allocWriteTime }}</span>
          <span class="fact muted">当前计划版本、当前账号可见的笔数，不随下面筛选变化</span>
        </div>
      </div>
    </ElCard>

    <!-- ===== 供给来源（可点分类）===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <span>供给来源</span>
          <span class="hint-text">点一个来源即筛选，再点取消</span>
        </div>
      </template>

      <div v-if="summaryLoading" class="muted">加载中…</div>
      <div v-else-if="!summary" class="muted">该版本的分配汇总暂不可用（下面仍可查明细）</div>
      <div v-else>
        <div class="source-cards">
          <button
            v-for="s in supplyTypeOptions"
            :key="s.supplyType"
            type="button"
            class="source-card"
            :class="{ active: filter.supplyType === s.supplyType }"
            @click="store.toggleSupplyType(s.supplyType)"
          >
            <span class="source-name">{{ supplyTypeLabel(s.supplyType) }}</span>
            <span class="source-count">{{ num(s.count) }}</span>
            <span class="source-qty">分配数量 {{ num(s.totalAllocatedQty) }}</span>
          </button>
          <span v-if="!supplyTypeOptions.length" class="muted">这一版没有分配记录</span>
        </div>
        <div class="commitment-line">
          <span class="muted">承诺状态：</span>
          <ElTag
            v-for="s in commitmentOptions"
            :key="s.commitmentStatus"
            size="small"
            :type="commitmentTagType(s.commitmentStatus)"
            effect="plain"
            class="commitment-tag"
          >
            {{ commitmentLabel(s.commitmentStatus) }} {{ num(s.count) }} 笔
          </ElTag>
          <span v-if="!commitmentOptions.length" class="muted">无</span>
        </div>
      </div>
    </ElCard>

    <!-- ===== 搜索条 ===== -->
    <!-- 2026-11-14 用户要求：去掉筛选栏上方那行字段名标签（订单号 / 订单 ID / 物料编码 /
         来源单据号 / 承诺状态），只留输入框 —— 字段名改由各自 placeholder 自带说明
         （同人工到货时间页筛选栏的做法：标签与表格列名重复、把筛选条撑成两行）。 -->
    <ElCard class="panel" shadow="never">
      <div class="filter-row">
        <div class="filter-item">
          <ElInput
            v-model="filter.orderNo"
            placeholder="订单号，如 O20714667"
            clearable
            style="width: 220px"
          />
        </div>
        <div class="filter-item">
          <ElInput
            v-model="filter.materialCode"
            placeholder="物料编码"
            clearable
            style="width: 180px"
          />
        </div>
        <div class="filter-item">
          <ElInput
            v-model="filter.supplyDocumentNo"
            placeholder="来源单据号"
            clearable
            style="width: 180px"
          />
        </div>
        <div class="filter-item">
          <ElSelect
            v-model="filter.commitmentStatus"
            placeholder="全部承诺状态"
            clearable
            style="width: 170px"
            @change="applyNow"
          >
            <ElOption
              v-for="s in commitmentOptions"
              :key="s.commitmentStatus"
              :label="`${commitmentLabel(s.commitmentStatus)}（${num(s.count)}）`"
              :value="s.commitmentStatus"
            />
          </ElSelect>
        </div>
        <div class="filter-actions">
          <ElButton @click="store.resetFilter()">重置</ElButton>
        </div>
      </div>
    </ElCard>

    <!-- ===== 结果：按订单 = 齐套视图；不按订单 = 分配明细 ===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <span>{{ isOrderScope ? '订单齐套' : '分配明细' }}</span>
          <span class="hint-text">
            <template v-if="isOrderScope">
              <template v-if="filteredTotal !== null">共 {{ num(filteredTotal) }}</template>
              <template v-else>{{ list.length }}</template>
              笔分配 → {{ groupRows.length }} 种物料 · 需求量按同一物料多笔取同一值
            </template>
            <template v-else> 第 {{ pageIndex }} 页 · 本页 {{ list.length }} 笔 </template>
          </span>
        </div>
      </template>

      <ElAlert
        v-if="orderScopeTruncated"
        type="warning"
        :closable="false"
        show-icon
        title="这笔订单的分配记录超过 500 条，只取到了前 500 条，下面的缺口可能算大了。"
        class="trunc-alert"
      />

      <!-- 订单齐套 -->
      <ElTable
        v-if="isOrderScope"
        :data="groupRows"
        :loading="loading"
        border
        stripe
        size="small"
        row-key="key"
        class="pt-table"
        @row-click="openGroupDetail"
      >
        <ElTableColumn label="订单号" min-width="130">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ row.orderNo }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="materialCode" label="物料编码" min-width="190" />
        <ElTableColumn label="需求量" width="110" align="right">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ qty(row.demandQty) }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="已分配" width="110" align="right">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ qty(row.allocatedQty) }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="缺口" width="110" align="right">
          <template #default="{ row }: { row: PeggingGroupRow }">
            <span v-if="row.shortage > 0" class="shortage">{{ qty(row.shortage) }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="供给来源" width="170">
          <template #default="{ row }: { row: PeggingGroupRow }">
            <ElTag
              v-for="s in row.supplyTypes"
              :key="s.supplyType"
              size="small"
              effect="plain"
              class="type-tag"
            >
              {{ supplyTypeLabel(s.supplyType) }}
              <template v-if="s.count > 1">×{{ s.count }}</template>
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="来源单据" min-width="150">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ row.documentNos.length ? row.documentNos.join('、') : '—' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="需求工厂 → 供给工厂" min-width="150">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ row.demandFactoryCode }}
            <template v-if="row.crossFactory">
              <span class="arrow">→</span>
              <template v-if="row.otherSupplyFactoryCodes.length">
                {{ row.otherSupplyFactoryCodes.join('、') }}
              </template>
              <ElTag
                size="small"
                type="warning"
                effect="plain"
                class="cross-tag"
                :title="
                  row.otherSupplyFactoryCodes.length
                    ? '供给来自其它工厂的跨厂订单'
                    : '供给来自其它工厂的跨厂订单；对方工厂代码暂未提供'
                "
              >
                {{ row.otherSupplyFactoryCodes.length ? '跨厂' : '跨厂（对方工厂未提供）' }}
              </ElTag>
            </template>
          </template>
        </ElTableColumn>
        <ElTableColumn label="可用时间" width="160">
          <template #default="{ row }: { row: PeggingGroupRow }">
            {{ (row.knownAvailableTime && formatUtcDateTime(row.knownAvailableTime)) || '—' }}
          </template>
        </ElTableColumn>
      </ElTable>

      <!-- 分配明细 -->
      <ElTable
        v-else
        :data="list"
        :loading="loading"
        border
        stripe
        size="small"
        row-key="id"
        show-overflow-tooltip
        class="pt-table"
        @row-click="openDetail"
      >
        <ElTableColumn label="订单号" min-width="140">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.rootOrderNo ?? row.rootOrderId ?? '—' }}
            <div v-if="row.currentOrderNo" class="sub">承接 {{ row.currentOrderNo }}</div>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="materialCode" label="物料编码" min-width="190" show-overflow-tooltip />
        <ElTableColumn label="分配数量" width="110" align="right">
          <template #default="{ row }: { row: PeggingTraceDto }">{{
            qty(row.allocatedQty)
          }}</template>
        </ElTableColumn>
        <ElTableColumn label="供给来源" width="120">
          <template #default="{ row }: { row: PeggingTraceDto }">
            <ElTag size="small" effect="plain">{{ supplyTypeLabel(row.supplyType) }}</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="来源单据" min-width="150">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.supplyDocumentNo ?? '—' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="需求工厂 → 供给工厂" min-width="150">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ row.demandFactoryCode ?? '—' }}
            <template v-if="isCrossSupply(row) || otherFactoryCode(row)">
              <span class="arrow">→</span>
              <span v-if="otherFactoryCode(row)" class="other-factory">{{
                otherFactoryCode(row)
              }}</span>
              <ElTag
                v-if="isCrossSupply(row)"
                size="small"
                type="warning"
                effect="plain"
                class="cross-tag"
                :title="
                  otherFactoryCode(row)
                    ? '供给来自其它工厂的跨厂订单'
                    : '供给来自其它工厂的跨厂订单；对方工厂代码暂未提供'
                "
              >
                {{ otherFactoryCode(row) ? '跨厂' : '跨厂（对方工厂未提供）' }}
              </ElTag>
            </template>
          </template>
        </ElTableColumn>
        <ElTableColumn label="可用时间" width="160">
          <template #default="{ row }: { row: PeggingTraceDto }">
            {{ (row.knownAvailableTime && formatUtcDateTime(row.knownAvailableTime)) || '—' }}
          </template>
        </ElTableColumn>
      </ElTable>

      <!-- 空态：说清「为什么空」（矩阵 A8 三态 + 2026-11-17 供给批次显式位层） -->
      <ElEmpty
        v-if="!loading && !list.length"
        :description="
          isOrderScope
            ? '这张订单没有供需分配记录。可换订单号，或改用物料编码 / 来源单据号查。'
            : totalAllocations === null
              ? '这一版的分配汇总取不到，所以不知道它一共该有多少笔；下面查明细的结果是空的。换一版看看。'
              : totalAllocations
                ? '没有符合当前筛选条件的分配记录。换个条件或点「重置」。'
                : supplyBatchState === 'absent'
                  ? '这一版没有可用的供给事实批次（源批缺失），归集不到分配记录；也可能这些分配不在你的数据范围内。换一版看看。'
                  : supplyBatchState === 'unknown'
                    ? '这一版的供给批次未判定，无法确定是否有分配记录；也可能这些分配不在你的数据范围内。换一版看看。'
                    : sourceBatchMissing
                      ? '这个计划版本没有记录到来源排程批次（版本无源 Run），也就归集不到这一版的分配记录；也可能这些分配不在你的数据范围内。换一版看看。'
                      : '当前账号在这一版没有可见的分配记录。可能是这一版还没发令、排产失败，或这些分配不在你的数据范围内，换一版看看。'
        "
      />

      <!-- 分页（只有明细视图有；订单口径一次取全量） -->
      <div v-if="!isOrderScope" class="pager">
        <span class="pager-total">
          <template v-if="filteredTotal !== null">
            服务端命中 {{ num(filteredTotal) }} 笔 · 本页已显示 {{ list.length }} 笔
            <template v-if="totalPages !== null"> · 共 {{ totalPages }} 页</template>
          </template>
          <template v-else>
            本页 {{ list.length }} 笔
            <span class="muted">（总条数暂不可用）</span>
          </template>
        </span>
        <div class="pager-nav">
          <ElSelect :model-value="pageSize" style="width: 110px" @change="onPageSizeChange">
            <ElOption :value="50" label="每页 50 条" />
            <ElOption :value="100" label="每页 100 条" />
            <ElOption :value="200" label="每页 200 条" />
          </ElSelect>
          <ElButton :disabled="pageIndex <= 1 || loading" @click="store.prevPage()"
            >上一页</ElButton
          >
          <span class="pager-info">
            第 {{ pageIndex }} 页
            <span v-if="!hasMore" class="muted">（已到末页）</span>
          </span>
          <ElButton :disabled="!hasMore || loading" @click="store.nextPage()">下一页</ElButton>
        </div>
      </div>
    </ElCard>

    <!-- ===== 明细 Drawer ===== -->
    <ElDrawer v-model="drawerOpen" title="分配明细" size="560px">
      <template v-if="selected">
        <div class="drawer-block-title">需求侧</div>
        <ElDescriptions :column="1" size="small" border>
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
          <ElDescriptionsItem label="需求量">{{ qty(selected.demandQty) }}</ElDescriptionsItem>
          <ElDescriptionsItem label="分配量">{{ qty(selected.allocatedQty) }}</ElDescriptionsItem>
        </ElDescriptions>

        <div class="drawer-block-title">供给侧</div>
        <ElDescriptions :column="1" size="small" border>
          <ElDescriptionsItem label="供给类型">
            {{ supplyTypeLabel(selected.supplyType) }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="供给工厂 / 仓库">
            <template v-if="otherFactoryCode(selected)">{{ otherFactoryCode(selected) }}</template>
            <template v-else-if="isCrossSupply(selected)">跨厂（对方工厂未提供）</template>
            <template v-else>{{ selected.supplyFactoryCode ?? '-' }}</template>
            /
            <!-- 仓库（2026-11-20；D1）：改走供给事实行，**不再拿 PSA 的 supplyWarehouseCode**（全 0 填充） -->
            <ElTooltip
              :content="
                warehouseHint({
                  poNo: selected.supplyDocumentNo,
                  materialCode: selected.materialCode,
                  materialId: selected.materialId
                })
              "
              placement="top"
            >
              <span>
                {{
                  warehouseText({
                    poNo: selected.supplyDocumentNo,
                    materialCode: selected.materialCode,
                    materialId: selected.materialId
                  })
                }}
              </span>
            </ElTooltip>
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
          <ElDescriptionsItem label="已知可用时间">
            {{ formatUtcDateTime(selected.knownAvailableTime) || '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="运输提前期">
            {{ selected.transportLeadTimeHours ?? '-' }} 小时
          </ElDescriptionsItem>
        </ElDescriptions>

        <div class="drawer-block-title">工序路径</div>
        <ElDescriptions :column="1" size="small" border>
          <ElDescriptionsItem label="承接工序">
            {{ selected.attachStageCode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="已完成工序">
            {{ selected.completedStageCode ?? '-' }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="下一需求工序">
            {{ selected.nextRequiredStageCode ?? '-' }}
          </ElDescriptionsItem>
        </ElDescriptions>

        <div class="drawer-block-title">内部标识（排查用）</div>
        <ElDescriptions :column="1" size="small" border>
          <ElDescriptionsItem label="分配 ID">{{ selected.id }}</ElDescriptionsItem>
          <ElDescriptionsItem label="分配序号">{{
            selected.allocationSequence
          }}</ElDescriptionsItem>
          <ElDescriptionsItem label="计划版本">{{ selected.planVersionId }}</ElDescriptionsItem>
          <ElDescriptionsItem label="排程运行">{{ selected.scheduleRunId }}</ElDescriptionsItem>
          <ElDescriptionsItem label="跨厂边">{{
            selected.crossFactoryEdgeId ?? '-'
          }}</ElDescriptionsItem>
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
  gap: 12px;
}

.error-alert {
  margin-bottom: 0;
}

.trunc-alert {
  margin-bottom: 8px;
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

/* ===== 追溯对象条 ===== */
.target-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px 24px;
}

.target-item {
  display: flex;
  align-items: center;
  gap: 8px;

  label {
    font-size: 12px;
    color: #64748b;
  }
}

.target-facts {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 20px;
  font-size: 13px;
  color: #475569;

  .fact strong {
    font-size: 16px;
    color: #1e293b;
  }

  /* 源批缺失＝要提醒的异常态；未判定＝中性（不替后端下结论） */
  .fact.warn {
    color: #b45309;
  }
}

/* ===== 来源卡片 ===== */
.source-cards {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.source-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  min-width: 132px;
  padding: 8px 14px;
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  background: #fff;
  cursor: pointer;
  font: inherit;
  text-align: left;

  &:hover {
    border-color: #94a3b8;
  }

  &.active {
    border-color: #409eff;
    background: #ecf5ff;
  }

  .source-name {
    font-size: 13px;
    color: #475569;
  }

  .source-count {
    font-size: 20px;
    font-weight: 600;
    color: #1e293b;
  }

  .source-qty {
    font-size: 12px;
    color: #94a3b8;
  }
}

.commitment-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 10px;
}

.commitment-tag {
  margin: 0;
}

/* ===== 搜索条 ===== */
.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 10px 16px;
  align-items: flex-end;
}

.filter-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.filter-actions {
  display: flex;
  gap: 8px;
  margin-left: auto;
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

.sub {
  font-size: 12px;
  color: #94a3b8;
}

.arrow {
  margin: 0 4px;
  color: #f59e0b;
}

.cross-tag {
  margin-left: 4px;
}

/* 对方工厂码（跨厂供给的发货厂） */
.other-factory {
  font-weight: 600;
  color: var(--el-color-warning-dark-2);
}

.type-tag {
  margin: 0 4px 2px 0;
}

.shortage {
  color: #f56c6c;
  font-weight: 600;
}

.pager {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}

.pager-nav {
  display: flex;
  align-items: center;
  gap: 12px;
}

.pager-total {
  font-size: 12px;
  color: #475569;
}

.pager-info {
  font-size: 12px;
  color: #475569;
}

.drawer-block-title {
  margin: 14px 0 6px;
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;

  &:first-child {
    margin-top: 0;
  }
}
</style>
