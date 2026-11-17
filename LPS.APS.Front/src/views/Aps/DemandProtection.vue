<script setup lang="ts">
/**
 * APS V1 4号位 — 手工释放需求保护（Demand Protection Release）页面（页面 9 / P1-11）
 *
 * 4号位文档第 13 节硬约束：
 *  - 链路：4号位 → 5号位（DemandProtectionController）→ 2号位 Service
 *  - 4号位只提交：Demand 标识 + 当前保护数量 + 释放范围 + Reason + 当前用户身份
 *  - 5号位不得 UPDATE 表 / 计算保护数量 / 复制规则
 *  - 4号位不计算：Effective ETA / 最终可用时间（AvailableTime）（此处无关）
 *
 * 本期范围（用户决策）：
 *  - 只支持 Full Release（与 2号位 IDemandSupplyHardLockRepository.ReleaseLocksAsync 1:1）
 *  - 列表接口返回详情（list = preview，Drawer 不发二次请求）
 *
 * 11-14 工厂用户视角改版：
 *  - 删页头大标题页头（2026-10-16 定调：页面不要大标题页头）；刷新收进列表卡片头
 *  - 删两列死数据（物料 / 工厂：后端 DTO 无此字段，store 恒置 undefined）
 *  - 删明细抽屉「锁 ID」列、删失败提示里的 LockId（列表不显示内部 ID）
 *  - 筛选改大白话（需求单号 / 供给单号 / 保护类别 / 状态）；「重置」改为清空后立即重查
 *  - 状态文案统一：生效中 / 已释放 / 部分释放 / 已失效
 *  - 空态给下一步（锁在排产时自动生成；可清空筛选重试）
 *  - 不可释放逐行给出原因（tooltip 说明，不让用户猜）
 *
 * RBAC（@see 审核报告 P1-14/15）：
 *  - aps.demand_protection.release 权限码持有者才可写
 *  - STRICT_BINDING：仅展示不允许释放（tooltip 说明）
 */

import { computed, nextTick, onMounted, ref } from 'vue'
import dayjs from 'dayjs'
import { ElMessage, ElMessageBox } from 'element-plus'
import { storeToRefs } from 'pinia'
import { useDemandProtectionStore } from '@/store/modules/aps/demandProtection'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import type {
  DemandProtectionDto,
  DemandProtectionListFilter,
  DemandProtectionReleaseResult,
  DemandProtectionStatus,
  HardLockStatus,
  HardLockType,
  ReleaseDemandProtectionInput
} from '@/api/aps-v1'
import {
  DEMAND_TYPE_LABELS,
  HARD_LOCK_SUPPLY_TYPE_LABELS,
  LOCK_TYPE_LABELS
} from '@/api/aps-v1'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElDialog,
  ElDrawer,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElOption,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const store = useDemandProtectionStore()
const apsAuth = useApsAuthStore()
const { list, currentDetail, loading, releasing, error, lastFilter } = storeToRefs(store)

/* ===== 权限门控（v1.2 §23.1 权限码门控）=====
 *  - aps.demand_protection.release：dev seed 下仅 aps.admin.system 持有
 *  - 不用角色判断的原因：与 3号位后端二次校验用同一码
 */
const canWrite = computed(() => apsAuth.has('aps.demand_protection.release'))

/* ===== 可释放判定（页面级单一真源） ===== */
function canRelease(row: DemandProtectionDto): boolean {
  if (!canWrite.value) return false
  if (row.status !== 'ACTIVE') return false
  if (row.lockType === 'STRICT_BINDING') return false
  if (row.lockIds.length === 0) return false
  return true
}

/** 不可释放的原因（tooltip 说清楚，不让用户猜） */
function releaseBlockedReason(row: DemandProtectionDto): string {
  if (!canWrite.value) return '当前账号没有释放权限'
  if (row.lockType === 'STRICT_BINDING') return '严格绑定的供给不能手工释放，请走重排流程'
  if (row.status === 'RELEASED') return '该需求已全部释放'
  if (row.status === 'PARTIALLY_RELEASED') return '该需求已有明细被释放，只剩部分可放'
  if (row.lockIds.length === 0) return '没有可释放的供给明细'
  return ''

}

/* ===== 状态 / 类型 映射 ===== */
const STATUS_TAG: Record<DemandProtectionStatus, 'success' | 'info' | 'warning'> = {
  ACTIVE: 'success',
  RELEASED: 'info',
  PARTIALLY_RELEASED: 'warning'
}

/** 聚合行状态文案（与表格状态列一致） */
const STATUS_LABEL: Record<DemandProtectionStatus, string> = {
  ACTIVE: '生效中',
  RELEASED: '已释放',
  PARTIALLY_RELEASED: '部分释放'
}

/** 供给明细状态文案（筛选下拉 + 明细表共用） */
const LOCK_STATUS_LABEL: Record<HardLockStatus, string> = {
  ACTIVE: '生效中',
  RELEASED: '已释放',
  BROKEN: '已失效'
}

const LOCK_TYPE_TAG: Record<'DEMAND_PROTECTION' | 'STRICT_BINDING', 'warning' | 'danger'> = {
  DEMAND_PROTECTION: 'warning',
  STRICT_BINDING: 'danger'
}

const SUPPLY_STATUS_TAG: Record<HardLockStatus, 'success' | 'info' | 'danger'> = {
  ACTIVE: 'success',
  RELEASED: 'info',
  BROKEN: 'danger'
}

/* ===== 筛选本地 state（变更后点 [查询] 才生效） =====
 * 形状对齐 5号位 GET /api/demand-protection QueryAsync 签名：
 *  - demandKey / supplyKey：substring LIKE
 *  - lockType / status：单值字符串（与 backend 枚举字面量一致）
 *  - skip / take：分页（UI 不暴露）
 */
const filterDraft = ref<DemandProtectionListFilter>({})
const filterDemandKey = ref('')
const filterSupplyKey = ref('')
const filterLockType = ref<HardLockType | ''>('')
const filterStatus = ref<HardLockStatus | ''>('')

function syncFilterDraft(): void {
  filterDraft.value = {
    demandKey: filterDemandKey.value.trim() || undefined,
    supplyKey: filterSupplyKey.value.trim() || undefined,
    lockType: filterLockType.value || undefined,
    status: filterStatus.value || undefined
  }
}

async function applyFilter(): Promise<void> {
  syncFilterDraft()
  selectedRows.value = []
  await store.load(filterDraft.value)
}

/** 重置 = 清空条件并立即重查（否则用户点了「重置」看不到变化） */
async function resetFilters(): Promise<void> {
  filterDemandKey.value = ''
  filterSupplyKey.value = ''
  filterLockType.value = ''
  filterStatus.value = ''
  await applyFilter()
}

async function refresh(): Promise<void> {
  selectedRows.value = []
  await store.load()
}

/** 当前已生效的筛选条件（大白话） */
const appliedFilterText = computed(() => {
  const f = lastFilter.value
  const parts: string[] = []
  if (f.demandKey) parts.push(`需求单号含「${f.demandKey}」`)
  if (f.supplyKey) parts.push(`供给单号含「${f.supplyKey}」`)
  if (f.lockType) parts.push(`保护类别 ${LOCK_TYPE_LABELS[f.lockType]}`)
  if (f.status) parts.push(`状态 ${LOCK_STATUS_LABEL[f.status]}`)
  return parts.join(' · ')
})

/* ===== 主表格多选 + 行点击 ===== */
const selectedRows = ref<DemandProtectionDto[]>([])

/** 行键（需求单号） */
function rowKeyOf(row: DemandProtectionDto): string {
  return row.demandKey
}

function onSelectionChange(rows: DemandProtectionDto[]): void {
  selectedRows.value = rows
}

/** 行是否可选：不可释放的行复选框直接不可勾 */
function selectable(row: DemandProtectionDto): boolean {
  return canRelease(row)
}

/** 可释放的选中行（防御性过滤，避免外部绕过 selectable 传入） */
const releasableSelection = computed(() => selectedRows.value.filter((r) => canRelease(r)))

/** 列表内可释放的行数（列表卡片头提示用） */
const releasableCount = computed(() => list.value.filter((r) => canRelease(r)).length)

/** 批量释放总 lock 数 + 总 lockedQty（仅 releasable 选中部分） */
const batchSummary = computed(() => {
  const rows = releasableSelection.value
  return {
    demandCount: rows.length,
    lockCount: rows.reduce((sum, r) => sum + r.lockIds.length, 0),
    lockedQty: rows.reduce((sum, r) => sum + r.lockedQty, 0)
  }
})

const batchButtonTip = computed(() => {
  if (!canWrite.value) return '当前账号没有释放权限'
  if (!releasableSelection.value.length) return '请先勾选要释放的记录'
  return `将释放 ${batchSummary.value.demandCount} 条需求 / ${batchSummary.value.lockCount} 条供给明细`
})

/* ===== 详情 Drawer ===== */
const drawerVisible = ref(false)
const drawerFormRef = ref<{
  validate: () => Promise<boolean>
  resetFields: () => void
} | null>(null)
const drawerReason = ref('')

/**
 * 释放原因校验规则
 * ⚠️ 2026-09-14 治理红线复核说明（@see APS_V1_各号位AI冻结基线治理红线_v1.0_20260914.md §4.2）：
 *  - 「必填」= 冻结业务基线审计需求
 *  - 历史曾加 ≥5 字符硬约束，来自 9月13日 1号位解读，非 0 号位正式裁决 ⇒ 不落地
 *  - 当前仅保留「必填」+ 200 字符上限；批量释放 batchRules 同理
 */
const drawerRules = {
  reason: [{ required: true, message: '请输入释放原因', trigger: 'blur' }]
}

function openDetail(row: DemandProtectionDto): void {
  store.openDetail(row)
  drawerReason.value = ''
  drawerVisible.value = true
}

function openReleaseFromRow(row: DemandProtectionDto): void {
  // 行内 [释放] 直接打开 Drawer 并聚焦 reason（reason 是审计强需求，不做无 reason 快捷释放）
  openDetail(row)
  nextTick(() => {
    const el = document.querySelector(
      '.dp-drawer .el-textarea__inner'
    ) as HTMLTextAreaElement | null
    el?.focus()
  })
}

function closeDrawer(): void {
  drawerVisible.value = false
  drawerReason.value = ''
  store.openDetail(null)
}

async function confirmRelease(): Promise<void> {
  if (!currentDetail.value) return
  if (!drawerFormRef.value) return
  let valid = false
  try {
    valid = await drawerFormRef.value.validate()
  } catch {
    return
  }
  if (!valid) return

  try {
    await ElMessageBox.confirm(
      `将释放需求保护：${currentDetail.value.demandKey}\n\n` +
        `范围：该需求下全部 ${currentDetail.value.lockCount} 条供给明细（共 ${currentDetail.value.lockedQty} 数量）\n` +
        `原因：${drawerReason.value.trim()}\n\n` +
        `释放后对应供给回到可分配池，且不可撤销。`,
      '确认释放',
      {
        confirmButtonText: '确认释放',
        cancelButtonText: '不释放',
        type: 'warning'
      }
    )
  } catch {
    return
  }

  const releasedBy = apsAuth.userInfo?.userCode ?? 'mock-pmc'
  const input: ReleaseDemandProtectionInput = {
    lockIds: [...currentDetail.value.lockIds],
    releasedBy,
    releaseReason: drawerReason.value.trim()
  }
  try {
    const perLock = await store.release(input)
    showReleaseOutcome(perLock)
    closeDrawer()
  } catch (err) {
    ElMessage.error(`释放失败：${(err as Error)?.message ?? '未知错误'}`)
  }
}

/** 锁明细的可读标识（供给类型 + 供给单号；不给内部 ID） */
function lockLabelOf(lockId: number): string {
  for (const row of list.value) {
    const d = row.lockDetails.find((x) => x.lockId === lockId)
    if (!d) continue
    const type = HARD_LOCK_SUPPLY_TYPE_LABELS[d.supplyType] ?? d.supplyType
    return d.supplyKey ? `${type} / ${d.supplyKey}` : type
  }
  return '供给明细'
}

/** 释放结果展示：成功汇总 toast，失败明细 alert */
function showReleaseOutcome(perLock: DemandProtectionReleaseResult[]): void {
  const released = perLock.filter((r) => r.status === 'RELEASED')
  const failed = perLock.filter((r) => r.status === 'FAILED')
  const failedLines = failed
    .map((f) => `· ${lockLabelOf(f.lockId)}：${f.failureReason ?? '未知'}`)
    .join('\n')
  if (failed.length === 0) {
    ElMessage.success(`已释放 ${released.length} 条供给明细`)
    return
  }
  if (released.length === 0) {
    ElMessage.error(`全部 ${failed.length} 条供给明细释放失败`)
    ElMessageBox.alert(failedLines, '释放失败明细', { type: 'error' })
    return
  }
  ElMessageBox.alert(
    `成功 ${released.length} 条，失败 ${failed.length} 条\n\n失败明细：\n${failedLines}`,
    '部分失败',
    { type: 'warning' }
  )
}

/* ===== 批量释放 Modal ===== */
const batchModalVisible = ref(false)
const batchReason = ref('')
const batchFormRef = ref<{
  validate: () => Promise<boolean>
  resetFields: () => void
} | null>(null)

/**
 * 批量释放原因校验规则（@see drawerRules 注释 / 治理红线 §4.2）
 */
const batchRules = {
  reason: [{ required: true, message: '请输入批量释放原因', trigger: 'blur' }]
}

function openBatchModal(): void {
  if (!releasableSelection.value.length) return
  batchReason.value = ''
  batchModalVisible.value = true
}

function closeBatchModal(): void {
  batchModalVisible.value = false
  batchReason.value = ''
}

async function submitBatch(): Promise<void> {
  if (!batchFormRef.value) return
  let valid = false
  try {
    valid = await batchFormRef.value.validate()
  } catch {
    return
  }
  if (!valid) return

  try {
    await ElMessageBox.confirm(
      `将批量释放 ${batchSummary.value.demandCount} 条需求保护（共 ${batchSummary.value.lockCount} 条供给明细 / 锁定量 ${batchSummary.value.lockedQty}）\n\n` +
        `原因：${batchReason.value.trim()}\n\n` +
        `本操作不可撤销。`,
      '确认批量释放',
      {
        confirmButtonText: '确认批量释放',
        cancelButtonText: '取消',
        type: 'warning'
      }
    )
  } catch {
    return
  }

  const releasedBy = apsAuth.userInfo?.userCode ?? 'mock-pmc'
  const inputs: ReleaseDemandProtectionInput[] = releasableSelection.value.map((row) => ({
    lockIds: [...row.lockIds],
    releasedBy,
    releaseReason: batchReason.value.trim()
  }))
  const result = await store.releaseBatch(inputs)

  if (result.failed.length === 0) {
    const releasedCount = result.perLock.filter(
      (r: DemandProtectionReleaseResult) => r.status === 'RELEASED'
    ).length
    ElMessage.success(`已批量释放 ${result.succeeded.length} 条需求保护 / ${releasedCount} 条供给明细`)
    selectedRows.value = []
    closeBatchModal()
  } else if (result.succeeded.length === 0) {
    ElMessage.error(`批量释放失败：${result.failed[0]?.message ?? '未知错误'}`)
  } else {
    const releasedLockCount = result.perLock.filter(
      (r: DemandProtectionReleaseResult) => r.status === 'RELEASED'
    ).length
    ElMessageBox.alert(
      `成功 ${result.succeeded.length} 条需求保护 / ${releasedLockCount} 条供给明细；失败 ${result.failed.length} 条需求保护\n\n` +
        `失败明细：\n` +
        result.failed.map((f) => `· ${f.demandKey}：${f.message}`).join('\n'),
      '部分失败',
      { type: 'warning' }
    )
    closeBatchModal()
  }
}

onMounted(refresh)
</script>

<template>
  <div class="aps-dp">
    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />

    <!-- ===== 筛选 ===== -->
    <ElCard class="panel" shadow="never">
      <div class="filter-row">
        <ElInput
          v-model="filterDemandKey"
          class="filter-input"
          placeholder="需求单号：订单号 / 工作集号 / PI 需求号"
          clearable
          @keyup.enter="applyFilter"
        >
          <template #prefix>
            <Icon icon="vi-ep:search" />
          </template>
        </ElInput>
        <ElInput
          v-model="filterSupplyKey"
          class="filter-input"
          placeholder="供给单号：库存批次 / 采购单 / 在制工单"
          clearable
          @keyup.enter="applyFilter"
        />
        <ElSelect
          v-model="filterLockType"
          class="filter-select"
          placeholder="保护类别"
          clearable
        >
          <ElOption label="需求保护" value="DEMAND_PROTECTION" />
          <ElOption label="严格绑定" value="STRICT_BINDING" />
        </ElSelect>
        <ElSelect v-model="filterStatus" class="filter-select" placeholder="状态" clearable>
          <ElOption label="生效中" value="ACTIVE" />
          <ElOption label="已释放" value="RELEASED" />
          <ElOption label="已失效" value="BROKEN" />
        </ElSelect>
        <ElButton type="primary" :loading="loading" @click="applyFilter">
          <Icon icon="vi-ep:search" />
          查询
        </ElButton>
        <ElButton @click="resetFilters">重置</ElButton>
      </div>
      <div v-if="appliedFilterText" class="filter-applied">
        当前筛选：{{ appliedFilterText }}
      </div>
    </ElCard>

    <!-- ===== 列表 ===== -->
    <ElCard class="panel" shadow="never">
      <template #header>
        <div class="panel-header">
          <div class="panel-title">
            <span>需求保护</span>
            <span class="muted small">{{ list.length }} 条</span>
            <span v-if="releasableCount" class="muted small">· 其中 {{ releasableCount }} 条可释放</span>
          </div>
          <div class="panel-actions">
            <ElButton size="small" plain :loading="loading" @click="refresh">
              <Icon icon="vi-ep:refresh" />
              刷新
            </ElButton>
            <ElTooltip :content="batchButtonTip" placement="top">
              <span>
                <ElButton
                  size="small"
                  type="danger"
                  plain
                  :disabled="!canWrite || releasableSelection.length === 0"
                  @click="openBatchModal"
                >
                  <Icon icon="vi-mdi:shield-off-outline" />
                  批量释放{{ releasableSelection.length ? ` (${releasableSelection.length})` : '' }}
                </ElButton>
              </span>
            </ElTooltip>
          </div>
        </div>
      </template>

      <div v-if="loading && !list.length" class="panel-loading">加载中…</div>
      <div v-else-if="!list.length" class="panel-empty">
        <ElEmpty description="没有需求保护记录">
          <p class="empty-hint">
            需求保护锁在排产时自动生成：需求与可用供给冲突时，系统会先锁住这部分供给。
            当前没有记录，可换个需求单号重试，或清空筛选条件查看全部。
          </p>
        </ElEmpty>
      </div>
      <ElTable
        v-else
        :data="list"
        :row-key="rowKeyOf"
        size="small"
        border
        stripe
        row-class-name="row-clickable"
        style="width: 100%"
        @selection-change="onSelectionChange"
        @row-click="openDetail"
      >
        <ElTableColumn type="selection" width="46" :selectable="selectable" />
        <ElTableColumn label="需求" min-width="240">
          <template #default="{ row }">
            <ElTag size="small" type="info" effect="plain">
              {{ DEMAND_TYPE_LABELS[row.demandType] }}
            </ElTag>
            <ElTooltip :content="row.demandKey" placement="top">
              <span class="code-tag">{{ row.demandKey }}</span>
            </ElTooltip>
          </template>
        </ElTableColumn>
        <ElTableColumn label="保护类别" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="LOCK_TYPE_TAG[row.lockType]" size="small">
              {{ LOCK_TYPE_LABELS[row.lockType] }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="锁条数" prop="lockCount" width="80" align="center" />
        <ElTableColumn label="锁定量" prop="lockedQty" width="90" align="right">
          <template #default="{ row }">
            <span v-if="row.status === 'ACTIVE'" class="qty-active">{{ row.lockedQty }}</span>
            <span v-else class="muted">{{ row.lockedQty }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="状态" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="STATUS_TAG[row.status]" size="small">
              {{ STATUS_LABEL[row.status] }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="锁定时间" width="120" align="center">
          <template #default="{ row }">
            <ElTooltip
              :content="`锁定来源：${row.createdBy} · ${dayjs(row.createdAt).format('YYYY-MM-DD HH:mm')}`"
              placement="top"
            >
              <span class="muted small">{{ dayjs(row.createdAt).format('MM-DD HH:mm') }}</span>
            </ElTooltip>
          </template>
        </ElTableColumn>
        <ElTableColumn label="释放" width="140" align="center">
          <template #default="{ row }">
            <template v-if="row.releasedAt">
              <span class="muted small">{{ dayjs(row.releasedAt).format('MM-DD HH:mm') }}</span>
              <span class="muted small"> · {{ row.releasedBy ?? '-' }}</span>
            </template>
            <span v-else class="muted">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="160" fixed="right" align="center">
          <template #default="{ row }">
            <ElButton size="small" plain @click.stop="openDetail(row)">
              <Icon icon="vi-mdi:eye-outline" />
              查看
            </ElButton>
            <ElButton
              v-if="canRelease(row)"
              size="small"
              type="danger"
              plain
              @click.stop="openReleaseFromRow(row)"
            >
              <Icon icon="vi-mdi:shield-off-outline" />
              释放
            </ElButton>
            <ElTooltip v-else :content="releaseBlockedReason(row)" placement="top">
              <span>
                <ElButton size="small" plain disabled @click.stop>
                  <Icon icon="vi-mdi:lock-outline" />
                  释放
                </ElButton>
              </span>
            </ElTooltip>
          </template>
        </ElTableColumn>
      </ElTable>
    </ElCard>

    <!-- ===== 详情 / 释放 Drawer ===== -->
    <ElDrawer
      v-model="drawerVisible"
      :title="currentDetail?.demandKey ?? '需求保护详情'"
      size="720px"
      direction="rtl"
      :close-on-click-modal="false"
      @close="closeDrawer"
    >
      <div v-if="currentDetail" class="dp-drawer">
        <!-- 状态徽：已释放 -->
        <ElAlert
          v-if="currentDetail.status === 'RELEASED'"
          type="success"
          :closable="false"
          show-icon
          class="status-banner"
        >
          <template #title>已释放</template>
          <span class="hint-text">
            释放人 {{ currentDetail.releasedBy ?? '-' }} · 释放时间
            {{
              currentDetail.releasedAt
                ? dayjs(currentDetail.releasedAt).format('YYYY-MM-DD HH:mm')
                : '-'
            }}
          </span>
          <div v-if="currentDetail.releaseReason" class="release-reason">
            原因：{{ currentDetail.releaseReason }}
          </div>
        </ElAlert>
        <!-- 状态徽：部分释放 -->
        <ElAlert
          v-else-if="currentDetail.status === 'PARTIALLY_RELEASED'"
          type="warning"
          :closable="false"
          show-icon
          class="status-banner"
        >
          <template #title>部分释放</template>
          <span class="hint-text">历史混合态（前端不生产该状态）</span>
        </ElAlert>

        <!-- 严格绑定提示 -->
        <ElAlert
          v-if="currentDetail.lockType === 'STRICT_BINDING'"
          type="warning"
          :closable="false"
          show-icon
          class="status-banner"
        >
          <template #title>严格绑定锁</template>
          <span class="hint-text">不支持手工释放。如需调整，请走重排流程。</span>
        </ElAlert>

        <!-- 基本信息 -->
        <ElDescriptions :column="2" border size="small" title="基本信息">
          <ElDescriptionsItem label="需求类型">{{
            DEMAND_TYPE_LABELS[currentDetail.demandType]
          }}</ElDescriptionsItem>
          <ElDescriptionsItem label="保护类别">
            <ElTag :type="LOCK_TYPE_TAG[currentDetail.lockType]" size="small">
              {{ LOCK_TYPE_LABELS[currentDetail.lockType] }}
            </ElTag>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="需求单号" :span="2">
            <span class="code-tag">{{ currentDetail.demandKey }}</span>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="锁条数">{{ currentDetail.lockCount }}</ElDescriptionsItem>
          <ElDescriptionsItem label="锁定量">
            <span v-if="currentDetail.status === 'ACTIVE'" class="qty-active">
              {{ currentDetail.lockedQty }}
            </span>
            <span v-else>{{ currentDetail.lockedQty }}</span>
          </ElDescriptionsItem>
          <ElDescriptionsItem label="锁定时间">
            {{ dayjs(currentDetail.createdAt).format('YYYY-MM-DD HH:mm') }}
          </ElDescriptionsItem>
          <ElDescriptionsItem label="锁定来源">{{ currentDetail.createdBy }}</ElDescriptionsItem>
        </ElDescriptions>

        <!-- 锁明细（只读） -->
        <div class="lock-details">
          <h4 class="section-title">供给明细（{{ currentDetail.lockDetails.length }} 条）</h4>
          <ElTable :data="currentDetail.lockDetails" size="small" border stripe style="width: 100%">
            <ElTableColumn label="供给类型" width="110" align="center">
              <template #default="{ row }">
                <ElTag size="small">
                  {{ HARD_LOCK_SUPPLY_TYPE_LABELS[row.supplyType] ?? row.supplyType }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="供给单号" min-width="240">
              <template #default="{ row }">
                <ElTooltip :content="row.supplyKey" placement="top">
                  <span class="code-tag">{{ row.supplyKey }}</span>
                </ElTooltip>
              </template>
            </ElTableColumn>
            <ElTableColumn label="锁定量" prop="lockedQty" width="90" align="right" />
            <ElTableColumn label="状态" width="90" align="center">
              <template #default="{ row }">
                <ElTag :type="SUPPLY_STATUS_TAG[row.status]" size="small">
                  {{ LOCK_STATUS_LABEL[row.status] }}
                </ElTag>
              </template>
            </ElTableColumn>
          </ElTable>
        </div>

        <!-- 释放表单（仅 ACTIVE + DEMAND_PROTECTION + canWrite 显示） -->
        <div v-if="canRelease(currentDetail)" class="release-form">
          <h4 class="section-title">释放操作</h4>
          <ElAlert type="warning" :closable="false" show-icon class="confirm-hint">
            <template #title>整条释放</template>
            <span>
              将释放该需求下的全部
              <strong>{{ currentDetail.lockCount }}</strong> 条供给明细（共
              <strong>{{ currentDetail.lockedQty }}</strong>
              数量），释放后对应供给回到可分配池，且不可撤销。
            </span>
          </ElAlert>
          <ElForm
            ref="drawerFormRef"
            :model="{ reason: drawerReason }"
            :rules="drawerRules"
            label-width="90px"
            label-position="right"
            class="dp-form"
          >
            <ElFormItem label="释放原因" prop="reason">
              <ElInput
                v-model="drawerReason"
                type="textarea"
                :rows="3"
                placeholder="请输入释放原因（必填，便于事后追溯）"
                maxlength="200"
                show-word-limit
              />
            </ElFormItem>
          </ElForm>
        </div>

        <div class="drawer-actions">
          <ElButton @click="closeDrawer">关闭</ElButton>
          <ElButton
            v-if="canRelease(currentDetail)"
            type="danger"
            :loading="releasing"
            @click="confirmRelease"
          >
            <Icon icon="vi-mdi:shield-off-outline" />
            确认释放
          </ElButton>
        </div>
      </div>
    </ElDrawer>

    <!-- ===== 批量释放 Modal ===== -->
    <ElDialog
      v-model="batchModalVisible"
      title="批量释放需求保护"
      width="560px"
      :close-on-click-modal="false"
      @close="closeBatchModal"
    >
      <div class="batch-modal">
        <ElAlert type="info" :closable="false" show-icon class="batch-summary">
          <template #title>释放范围</template>
          <div class="batch-summary-grid">
            <div class="batch-summary-item">
              <div class="batch-summary-label">需求条数</div>
              <div class="batch-summary-value">{{ batchSummary.demandCount }}</div>
            </div>
            <div class="batch-summary-item">
              <div class="batch-summary-label">锁条数</div>
              <div class="batch-summary-value">{{ batchSummary.lockCount }}</div>
            </div>
            <div class="batch-summary-item">
              <div class="batch-summary-label">锁定总量</div>
              <div class="batch-summary-value">{{ batchSummary.lockedQty }}</div>
            </div>
          </div>
        </ElAlert>

        <div class="batch-list">
          <h4 class="section-title">选中明细</h4>
          <ul class="batch-list-ul">
            <li v-for="row in releasableSelection" :key="row.demandKey">
              <span class="code-tag">{{ row.demandKey }}</span>
              <span class="muted small">
                · {{ row.lockCount }} 条锁 · 锁定量 {{ row.lockedQty }}
              </span>
            </li>
          </ul>
        </div>

        <ElForm
          ref="batchFormRef"
          :model="{ reason: batchReason }"
          :rules="batchRules"
          label-width="90px"
          label-position="right"
          class="batch-form"
        >
          <ElFormItem label="共享原因" prop="reason">
            <ElInput
              v-model="batchReason"
              type="textarea"
              :rows="3"
              placeholder="请输入批量释放原因（必填，便于事后追溯）"
              maxlength="200"
              show-word-limit
            />
          </ElFormItem>
        </ElForm>
      </div>
      <template #footer>
        <ElButton @click="closeBatchModal">取消</ElButton>
        <ElButton type="danger" :loading="releasing" @click="submitBatch">
          <Icon icon="vi-mdi:shield-off-outline" />
          确认批量释放
        </ElButton>
      </template>
    </ElDialog>
  </div>
</template>

<style lang="less" scoped>
.aps-dp {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 12px;
}

.panel {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.panel-title {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.panel-actions {
  display: flex;
  gap: 8px;
}

.panel-empty {
  padding: 20px 0;
}

.panel-loading {
  padding: 32px 0;
  font-size: 13px;
  color: #94a3b8;
  text-align: center;
}

.empty-hint {
  max-width: 520px;
  margin: 0 auto;
  font-size: 12px;
  line-height: 1.7;
  color: #94a3b8;
}

.muted {
  color: #94a3b8;
}

.small {
  font-size: 11px;
}

.code-tag {
  padding: 1px 6px;
  margin-left: 4px;
  font-family: 'Courier New', monospace;
  font-size: 11px;
  background: #f1f5f9;
  border-radius: 3px;
}

.qty-active {
  font-weight: 600;
  color: #d97706;
}

/* 筛选条 */
.filter-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}

.filter-input {
  width: 260px;
}

.filter-select {
  width: 150px;
}

.filter-applied {
  margin-top: 10px;
  font-size: 12px;
  color: #64748b;
}

/* Drawer */
.dp-drawer {
  display: flex;
  padding: 0 4px;
  flex-direction: column;
  gap: 16px;
}

.status-banner {
  margin: 0;

  .release-reason {
    margin-top: 6px;
    font-size: 12px;
    color: #475569;
  }
}

.section-title {
  margin: 0 0 10px;
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
}

.release-form {
  .confirm-hint {
    margin-bottom: 12px;
  }
}

.dp-form,
.batch-form {
  :deep(.el-form-item) {
    margin-bottom: 12px;
  }
}

.batch-modal {
  display: flex;
  flex-direction: column;
  gap: 14px;

  .batch-summary {
    .batch-summary-grid {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 12px;
      margin-top: 8px;
    }

    .batch-summary-item {
      padding: 8px 10px;
      text-align: center;
      background: #fff;
      border: 1px solid #e2e8f0;
      border-radius: 4px;
    }

    .batch-summary-label {
      font-size: 11px;
      color: #94a3b8;
    }

    .batch-summary-value {
      margin-top: 2px;
      font-size: 18px;
      font-weight: 600;
      color: #1e293b;
    }
  }

  .batch-list {
    .batch-list-ul {
      max-height: 160px;
      padding: 8px 12px;
      margin: 0;
      overflow-y: auto;
      list-style: none;
      background: #f8fafc;
      border: 1px solid #e2e8f0;
      border-radius: 4px;

      li {
        padding: 4px 0;
        font-size: 12px;
      }
    }
  }
}

.drawer-actions {
  display: flex;
  padding: 12px 0;
  border-top: 1px solid #e2e8f0;
  justify-content: flex-end;
  gap: 8px;
}

:deep(.row-clickable) {
  cursor: pointer;
}
</style>
