/**
 * APS V1 4号位 — 规则版本 Diff Pinia store（Setup 专项指导 v1.2 §19.2「规则版本/Diff/发布状态」）
 *
 * 状态：
 *  - diff：版本 Diff 结果（含 setupRuleChanges）
 *  - selectedVersionId：当前版本
 *  - otherVersionId：对比版本
 *  - ruleSetVersions：RuleSetVersion 下拉源
 *  - loading 系列：UI 态
 *
 * 权限：
 *  - aps.setup.view → 查看 Diff
 *
 * 发布：本 store **不提供发布动作**。整版规则集发布统一在「规则集维护」页
 * （aps.rule.publish → POST /rule-set/version/{id}/publish）。
 * 依据 0号位 2026-10-26《规则集多主题协作与统一发布_V1最小实现建议》§四：
 * `POST /rule-set-versions/{id}/publish` 仅凭 aps.setup.publish 就能把含
 * 需求优先级的整版置 PUBLISHED，属整版发布权越界（已致函 3号位 收口）。
 *
 * Diff 边界：
 *  - 不展示非 Setup 维度的 Diff（ParameterSet/StrategyProfile 仍走原 v1.4 §十八 规则参数维护页）
 *  - 不允许跨 RuleSet 直接编辑（必须发布 DRAFT 后再编辑）
 *
 * 复用：
 *  - RuleSetVersion Diff 机制（v1.4 §十八）
 *  - 3号位 需补 setupRuleChanges 字段
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { setupApi, ApiError, type SetupDiffDto, type SetupRuleSetVersionDto } from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

export const useSetupDiffStore = defineStore('aps.setupDiff', () => {
  // ===== state =====
  const diff = ref<SetupDiffDto | null>(null)
  const selectedVersionId = ref<number | null>(null)
  const otherVersionId = ref<number | null>(null)
  const ruleSetVersions = ref<SetupRuleSetVersionDto[]>([])

  const diffLoading = ref(false)
  const versionsLoading = ref(false)
  const error = ref<string | null>(null)

  // ===== auth =====
  const apsAuth = useApsAuthStore()

  // ===== getters =====

  /** 是否有 Diff 结果 */
  const hasDiff = computed<boolean>(() => diff.value !== null)

  /** 查看权限 */
  const canView = computed<boolean>(() => apsAuth.has('aps.setup.view'))

  /** 当前选中的 RuleSetVersion */
  const currentVersion = computed<SetupRuleSetVersionDto | null>(() => {
    if (selectedVersionId.value === null) return null
    return ruleSetVersions.value.find((v) => v.id === selectedVersionId.value) ?? null
  })

  /** 对比的 RuleSetVersion */
  const otherVersion = computed<SetupRuleSetVersionDto | null>(() => {
    if (otherVersionId.value === null) return null
    return ruleSetVersions.value.find((v) => v.id === otherVersionId.value) ?? null
  })

  /** 变更总数 */
  const totalChanges = computed<number>(() => {
    if (!diff.value) return 0
    return diff.value.addedCount + diff.value.modifiedCount + diff.value.removedCount
  })

  // ===== actions =====

  function selectVersion(versionId: number | null): void {
    selectedVersionId.value = versionId
    diff.value = null
  }

  function selectOtherVersion(versionId: number | null): void {
    otherVersionId.value = versionId
    diff.value = null
  }

  async function loadRuleSetVersions(params?: { ruleSetId?: number }): Promise<void> {
    versionsLoading.value = true
    error.value = null
    try {
      ruleSetVersions.value = await setupApi.listRuleSetVersions(params)
    } catch (err) {
      handleError(err)
      ruleSetVersions.value = []
    } finally {
      versionsLoading.value = false
    }
  }

  async function loadDiff(versionId: number, otherId: number): Promise<void> {
    if (versionId === otherId) {
      diff.value = null
      error.value = '源版本和目标版本相同，无法对比'
      return
    }
    diffLoading.value = true
    error.value = null
    try {
      diff.value = await setupApi.diffRuleSetVersions(versionId, otherId)
    } catch (err) {
      handleError(err)
      diff.value = null
    } finally {
      diffLoading.value = false
    }
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    diff.value = null
    selectedVersionId.value = null
    otherVersionId.value = null
    ruleSetVersions.value = []
    error.value = null
  }

  return {
    // state
    diff,
    selectedVersionId,
    otherVersionId,
    ruleSetVersions,
    diffLoading,
    versionsLoading,
    error,
    // getters
    hasDiff,
    canView,
    currentVersion,
    otherVersion,
    totalChanges,
    // actions
    selectVersion,
    selectOtherVersion,
    loadRuleSetVersions,
    loadDiff,
    handleError,
    reset
  }
})
