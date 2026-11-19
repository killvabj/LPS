<script setup lang="ts">
/**
 * APS V1 4号位 — 生产指示位置（`/api/pi-position`）
 *
 * 2026-11-12 工厂用户视角改版（菜单名同步改：PI位置 → 生产指示位置）
 *  - 菜单名去掉字母「PI」：中文名取冻结 01:567「生产指示位置计算」/ 01:786「生产指示位置可能事实包」
 *  - 版本默认值不再取 versions[0]（最新一条常常是失败测试版、位置数为 0 ⇒ 一进页面像系统坏了）
 *    → 复用订单页的 `pickDefaultVersion()`（已生效 → 算完待确认 → 其它非失败 → 兜底第一条）
 *  - 位置类型分布卡改成**可点分类**（点一下只看那一类，再点取消）；位置类型筛选下拉撤掉
 *    （两个入口说同一件事反而乱）
 *  - 当前阶段改下拉（中文名「BJ机加（BJ_MACH）」），工厂用户不用背阶段码
 *  - 筛选口径澄清：生产指示号 / 物料编码 后端是**包含匹配**（dev 实测），旧文案「精确匹配」是错的
 *  - 表格瘦身：去掉「快照时间」「操作」两列（点生产指示号即开明细）；明细抽屉补上快照时间
 *  - 抽屉里的「计划版本」不再显示内部 Id，改显示版本码
 *  - 空态分三态：未选版本 / 这个版本没有位置数据 / 当前筛选没有匹配
 *  - 指标条提到页面**最上面**（在筛选栏之上）：先看概况、再选版本看明细
 *  - 修时区错位：位置快照时间原用 `formatUtcDateTime`（把无时区字面值当 UTC ⇒ 整体 +8 小时，
 *    dev 上 13:25 落库会显示成 21:25 = 未来的时间）。`ProductionInstructionPositionSnapshot.CreatedAt`
 *    的 DDL 默认值是 `GETDATE()`（服务器本地墙钟）⇒ 改走 `formatLocalDateTime` 本地直显。
 *
 * 口径纪律（2026-10-17 重做时定的，勿推翻）：
 *  - 供给构成整块下线；MES 资格只用 U16（`positionType === 'UNLOCATED'`）数据驱动判据
 *  - 位置问题码**原值展示**（冻结无值域 ⇒ 不自造中文词典，已出函 5号位要词典）
 *  - `sourceKey` 不渲染（5号位 10-17 更正：它是兜底过程标记，不是源单据键）
 *
 * 11-16 接 5号位《P01～P13＋采购协同 页面—接口实施矩阵 v0.1》A10 行：
 *  - `INTERPLANT_TRANSIT`（跨厂在途）**整表 0 行**（D05-09：跨厂在途不走位置快照）
 *    ⇒ 这一类**查不到是正常的**，不是故障、不报错；
 *    页面如实写「不经位置快照承载」（在途事实由供给侧承载），并把这一类**不硬塞进分布卡**
 *    （分布卡只画后端给的那几类；补一个 0 会把「明确零」说成「有这一类但没数据」）。
 */

import { computed, onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import { usePiStore } from '@/store/modules/aps/pi'
import { formatLocalDateTime } from '@/utils/datetime'
import { PLAN_VERSION_STATUS_LABELS } from '@/api/aps-v1'
import { STAGE_DICT, stageLabelOf } from '@/api/aps-v1/types/stage'

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
  summaryFailed,
  totalPositions,
  unlocatedCount,
  unlocatedQty,
  pageIssueRows,
  hasFilter
} = storeToRefs(piStore)

/* ===== 位置类型字典 =====
 * 6 值全预留（0号位 裁决中）。未知值降级显示原值、不报错 —— 后端随时可能出现第 7 个值。
 * hint = 一句话大白话（工厂用户看标签未必懂），走悬停提示。
 */
const POSITION_META: Record<
  string,
  {
    label: string
    hint: string
    tag: 'success' | 'warning' | 'info' | 'primary' | 'danger'
  }
> = {
  STAGE_WAITING: { label: '在制中', hint: '已经在车间里做着的货', tag: 'success' },
  // 冻结名，与 STAGE_WAITING 同概念（纯名称漂移），后端改回冻结名后本行即生效
  STAGE_WIP: { label: '在制中（冻结名）', hint: '已经在车间里做着的货', tag: 'success' },
  FIRST_STAGE_PENDING: { label: '首工序待开始', hint: '还没开工，等着上线', tag: 'primary' },
  XC: { label: '线边仓', hint: '工位旁边的小仓', tag: 'info' },
  INTERPLANT_TRANSIT: {
    label: '跨厂在途',
    hint: '从一个厂运到另一个厂的路上的货；这类货由供给侧承载，不进位置快照',
    tag: 'primary'
  },
  UNLOCATED: { label: '无法定位', hint: '找不到具体位置，这批货不能下发车间', tag: 'danger' },
  // 冻结有、后端未实现；实库 0 行，V1 可能恒不出现
  WAITING_INBOUND: { label: '等待入库', hint: '货已经到了，还没入库', tag: 'warning' }
}

function positionLabel(t: string): string {
  return POSITION_META[t]?.label ?? t
}
function positionHint(t: string): string {
  return POSITION_META[t]?.hint ?? ''
}
function positionTag(t: string): 'success' | 'warning' | 'info' | 'primary' | 'danger' {
  return POSITION_META[t]?.tag ?? 'info'
}

const STATUS_LABEL = PLAN_VERSION_STATUS_LABELS
function versionLabel(id: number): string {
  const v = versions.value.find((x) => x.id === id)
  return v ? `${v.versionCode}（${STATUS_LABEL[v.status] ?? v.status}）` : String(id)
}

/* ===== U16：无法定位不可下发 MES =====
 * 判据 = 后端返回的 positionType 本身，不是前端推断（5号位 2026-10-17 回执 §三）。
 */
const UNLOCATED = 'UNLOCATED'
function isUnlocated(t: string): boolean {
  return t === UNLOCATED
}

/* ===== 跨厂在途：不经位置快照承载（矩阵 A10）=====
 * 这一类在位置快照里整表 0 行（D05-09）⇒ 筛它必然空，**不是故障**。
 * 页面据此说清「这一类查不到是正常的」，不报错、不显示成「没有匹配的数据」。
 */
const INTERPLANT_TRANSIT = 'INTERPLANT_TRANSIT'
const interplantSelected = computed(() => filter.value.positionType === INTERPLANT_TRANSIT)

/* ===== 展示口径 ===== */
/**
 * 这个版本到底有没有位置数据（0 条 = 选错了版本，不是页面坏了）
 *
 * ⚠️ 汇总取不到时（`summaryFailed`）**不能说成「没有位置数据」**：
 *   「取不到」是未知，「没有」是明确零（§0.2 ②）⇒ 空态分两句话说。
 */
const hasPositions = computed(() => (summary.value?.totalPositions ?? 0) > 0)
const piCount = computed(() => summary.value?.piCount ?? 0)
/** 指标条数字：汇总取不到时显「未提供」，不显 0（§0.2 ②） */
const piCountText = computed(() =>
  summary.value ? piCount.value.toLocaleString('zh-CN') : '未提供'
)
const unlocatedCountText = computed(() =>
  summary.value ? unlocatedCount.value.toLocaleString('zh-CN') : '未提供'
)
const unlocatedQtyText = computed(() =>
  summary.value ? unlocatedQty.value.toLocaleString('zh-CN') : '未提供'
)
/** 当前选中版本的版本码（抽屉里显示版本码，不显示内部 Id） */
const currentVersionLabel = computed(() => {
  const v = versions.value.find((x) => x.id === planVersionId.value)
  return v ? v.versionCode : '—'
})
/** 选中版本是不是失败版（失败版没有位置数据 ⇒ 空态要说清原因） */
const currentVersionFailed = computed(() => {
  const v = versions.value.find((x) => x.id === planVersionId.value)
  return v?.status === 'FAILED'
})
/** 指标条显示条件：已选版本，且（该版本有位置数据 或 还在加载）
 *  与原「指标条排在明细之前」时的渲染条件一致，只是位置提到了页面最上面。 */
const showKpi = computed(
  () => planVersionId.value != null && (hasPositions.value || listLoading.value)
)

/** 单 PI 位置数量合计（详情抽屉用） */
const detailTotalQty = computed(() => detailRows.value.reduce((s, r) => s + (r.quantity ?? 0), 0))

/* ===== 阶段下拉 =====
 * 权威字典 = 冻结 07 §1.9 阶段字典（前端内置 29 条，与后端 StageDict 同源），
 * 再并上**当前页真实出现**的阶段码（数据驱动，不臆造中文名）。
 */
const stageOptions = computed(() => {
  const codes = new Set(Object.keys(STAGE_DICT))
  for (const r of list.value) {
    if (r.currentStageCode) codes.add(r.currentStageCode)
  }
  return Array.from(codes)
    .map((code) => ({ value: code, label: stageOptionLabel(code) }))
    .sort((a, b) => a.label.localeCompare(b.label, 'zh-CN'))
})
function stageOptionLabel(code: string): string {
  const name = stageLabelOf(code)
  return name === code ? code : `${name}（${code}）`
}

/* ===== 操作 ===== */
function onVersionChange(id: number): void {
  piStore.setPlanVersion(id)
}
function onPiNoChange(v: string | undefined): void {
  piStore.setFilter({ productionInstructionNo: v ?? '' })
  void piStore.loadList()
}
function onMaterialChange(v: string | undefined): void {
  piStore.setFilter({ materialCode: v ?? '' })
  void piStore.loadList()
}
function onStageChange(v: string | undefined): void {
  piStore.setFilter({ stageCode: v ?? '' })
  void piStore.loadList()
}
/** 点位置类型分布卡：只看那一类；再点同一张卡 = 取消该分类 */
function onPositionTypeClick(positionType: string): void {
  piStore.setFilter({
    positionType: filter.value.positionType === positionType ? '' : positionType
  })
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

    <!-- ===== 指标条（全部来自 /summary 服务端聚合，不是当前页条数）===== -->
    <div v-if="showKpi" class="kpi-strip">
      <div class="kpi-pill">
        <span class="pill-label">生产指示</span>
        <span class="pill-val">{{ piCountText }}</span>
      </div>
      <button
        type="button"
        class="kpi-pill clickable"
        :class="{ active: filter.positionType === UNLOCATED }"
        @click="onPositionTypeClick(UNLOCATED)"
      >
        <span class="pill-label">无法定位（条 / 数量）</span>
        <span class="pill-val">{{ unlocatedCountText }} / {{ unlocatedQtyText }}</span>
      </button>
      <span class="kpi-caption">数字按所选版本统计；点「无法定位」只看定位不到的那批。</span>
    </div>

    <!-- ===== 版本与筛选（变更即生效）===== -->
    <ElCard class="panel">
      <div class="filter-row">
        <div class="filter-item">
          <span class="filter-label">计划版本</span>
          <ElSelect
            :model-value="planVersionId ?? undefined"
            placeholder="选择版本"
            filterable
            style="width: 320px"
            :loading="versionsLoading"
            @change="onVersionChange"
          >
            <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v.id)" :value="v.id" />
          </ElSelect>
        </div>
        <ElInput
          :model-value="filter.productionInstructionNo"
          placeholder="生产指示号（支持部分匹配）"
          clearable
          style="width: 200px"
          @change="onPiNoChange"
        />
        <ElInput
          :model-value="filter.materialCode"
          placeholder="物料编码（支持部分匹配）"
          clearable
          style="width: 210px"
          @change="onMaterialChange"
        />
        <ElSelect
          :model-value="filter.stageCode"
          placeholder="全部阶段"
          clearable
          filterable
          style="width: 190px"
          @change="onStageChange"
        >
          <ElOption v-for="s in stageOptions" :key="s.value" :label="s.label" :value="s.value" />
        </ElSelect>
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

    <!-- ===== 这个版本没有位置数据（汇总取不到时另说，见 summaryFailed）===== -->
    <ElCard v-else-if="!hasPositions && !listLoading" class="panel">
      <ElEmpty
        :description="
          summaryFailed
            ? '这个计划版本的位置汇总没取到（上面有错误提示），所以不知道有没有位置数据；过一会儿再试，或换个版本看看'
            : currentVersionFailed
              ? '这个计划版本排产失败，没有位置数据，换个版本看看'
              : '这个计划版本还没有位置数据，换个版本看看'
        "
      />
    </ElCard>

    <template v-else>
      <!-- ===== 位置类型分布（服务端聚合 · 可点分类）===== -->
      <ElCard v-if="hasPositions" class="panel">
        <div class="position-grid">
          <button
            v-for="t in summary!.positionTypeCounts"
            :key="t.positionType"
            type="button"
            class="position-cell"
            :class="{ active: filter.positionType === t.positionType }"
            @click="onPositionTypeClick(t.positionType)"
          >
            <ElTooltip :content="positionHint(t.positionType)" placement="top">
              <ElTag :type="positionTag(t.positionType)" effect="dark" size="small">
                {{ positionLabel(t.positionType) }}
              </ElTag>
            </ElTooltip>
            <span class="position-count">{{ t.count.toLocaleString() }}</span>
            <span class="position-qty">数量 {{ t.totalQuantity.toLocaleString() }}</span>
          </button>
        </div>
        <!-- 跨厂在途不在位置快照里（矩阵 A10）：明说「查不到是正常的」，不补 0 也不报错 -->
        <p class="position-note">
          跨厂在途不经位置快照承载（在途货由供给侧承载），所以上面没有「跨厂在途」这一类。
        </p>
      </ElCard>

      <!-- ===== 位置明细 ===== -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span>位置明细</span>
            <ElTag v-if="hasFilter" size="small" type="warning" effect="plain"
              >筛选后「共 N 条」仍是全版本口径</ElTag
            >
            <ElTag v-if="pageIssueRows > 0" size="small" type="warning" effect="plain">
              本页带问题码 {{ pageIssueRows }} 条
            </ElTag>
          </div>
        </template>

        <div v-if="!list.length && !listLoading" class="panel-empty">
          <ElEmpty
            :description="
              interplantSelected
                ? '跨厂在途不经位置快照承载（在途货由供给侧承载），这一类在位置快照里查不到行'
                : hasFilter
                  ? '当前筛选没有匹配的位置行'
                  : '这个版本没有位置行'
            "
          >
            <ElButton v-if="hasFilter" @click="resetFilters">清除筛选</ElButton>
          </ElEmpty>
        </div>

        <ElTable v-else v-loading="listLoading" :data="list" size="small" border stripe>
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
          <ElTableColumn label="位置类型" width="170">
            <template #default="{ row }">
              <ElTag :type="positionTag(row.positionType)" effect="dark" size="small">
                {{ positionLabel(row.positionType) }}
              </ElTag>
              <ElTag v-if="isUnlocated(row.positionType)" type="danger" size="small" effect="plain">
                不可下发 MES
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="位置数量" width="120" align="right">
            <template #default="{ row }">{{ (row.quantity ?? 0).toLocaleString() }}</template>
          </ElTableColumn>
          <ElTableColumn label="当前阶段" width="140">
            <template #default="{ row }">
              <span v-if="row.currentStageCode">{{ stageLabelOf(row.currentStageCode) }}</span>
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
        </ElTable>

        <div class="pager">
          <!--
            列表端点不返回 total，「共 N 条」只能用 /summary 的全版本口径。
            筛选后真实命中数未知，故筛选态只给「上一页/下一页」，页数由满页判断 ——
            宁可少一个翻页入口，也不给一个算错的总页数。
            汇总取不到时同理（总条数未知），不拿 0 当总条数。
          -->
          <template v-if="hasFilter || summaryFailed">
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

    <!-- ===== 单个生产指示的全部位置行 Drawer ===== -->
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
            <ElDescriptionsItem label="计划版本">{{ currentVersionLabel }}</ElDescriptionsItem>
          </ElDescriptions>

          <ElTable v-loading="detailLoading" :data="detailRows" size="small" border>
            <ElTableColumn label="位置类型" width="170">
              <template #default="{ row }">
                <ElTag :type="positionTag(row.positionType)" effect="dark" size="small">
                  {{ positionLabel(row.positionType) }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="位置数量" width="120" align="right">
              <template #default="{ row }">{{ (row.quantity ?? 0).toLocaleString() }}</template>
            </ElTableColumn>
            <ElTableColumn label="当前阶段" width="130">
              <template #default="{ row }">
                <span v-if="row.currentStageCode">{{ stageLabelOf(row.currentStageCode) }}</span>
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
            <ElTableColumn label="位置快照时间" width="150">
              <template #default="{ row }">
                <span class="muted">{{ formatLocalDateTime(row.createdAt) }}</span>
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

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
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
  align-items: center;
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

  /* 可点的分类入口（无法定位） */
  &.clickable {
    cursor: pointer;
    border: 1px solid transparent;

    &:hover {
      border-color: #cbd5e1;
    }

    &.active {
      background: #fee2e2;
      border-color: #f87171;
    }
  }
}

.kpi-caption {
  font-size: 12px;
  color: #94a3b8;
}

/* 位置类型分布 */
.position-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 12px;
}

.position-cell {
  display: flex;
  padding: 10px 14px;
  font-size: 12px;
  text-align: left;
  background: #f8fafc;
  cursor: pointer;
  border: 1px solid transparent;
  border-radius: 6px;
  align-items: center;
  gap: 10px;

  &:hover {
    border-color: #cbd5e1;
  }

  &.active {
    background: #eff6ff;
    border-color: #3b82f6;
  }

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

/* 跨厂在途说明（矩阵 A10） */
.position-note {
  margin: 10px 0 0;
  font-size: 12px;
  color: #64748b;
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
