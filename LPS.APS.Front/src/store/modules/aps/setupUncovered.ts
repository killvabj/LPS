/**
 * APS V1 4号位 — 0 分钟兜底统计 Pinia store（Setup 专项指导 v1.2 §五 第三优先 / §18）
 *
 * 状态：
 *  - stats：0 分钟兜底统计列表
 *  - query：查询参数（runId + 可选筛选条件）
 *  - loading 系列：UI 态
 *
 * 权限：
 *  - aps.setup.view → 查看（只读页面，无写操作）
 *
 * 统计口径约束：
 *  - 不在前端聚合（性能问题，调用后端聚合端点）
 *  - 不展示 Run 外的统计（避免与 RuleSetVersion 解耦）
 *
 * 出口操作：
 *  - 点击"去补默认换型规则" → 跳默认换型规则页，预填 4 字段（部门/大工艺/工序/设备）
 *  - 导出 CSV
 *
 * 10-27 变更（3号位 P1 落码后）：
 *  - 输出行改按后端 SetupUncoveredStatDto 12 字段；前产品/后产品/最近出现后端没有 ⇒ 不再产出
 *  - 「去补明确产品转换」出口撤除：EXACT 入参必须同时给前/后产品主键
 *    （SetupRuleExactInput.FromMaterialId/ToMaterialId，缺任一即 422），
 *    而 [Task] 无 FromMaterial ⇒ 统计行永远补不出明确转换。3号位 Q4 已裁定降级为只补 DEFAULT。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  setupApi,
  ApiError,
  type SetupUncoveredStatDto,
  type SetupUncoveredQuery
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

export const useSetupUncoveredStore = defineStore('aps.setupUncovered', () => {
  // ===== state =====
  const stats = ref<SetupUncoveredStatDto[]>([])
  const query = ref<SetupUncoveredQuery | null>(null)

  const listLoading = ref(false)
  const error = ref<string | null>(null)

  // ===== auth =====
  const apsAuth = useApsAuthStore()

  // ===== getters =====

  /** 查看权限 */
  const canView = computed<boolean>(() => apsAuth.has('aps.setup.view'))

  /** 统计总数 */
  const statCount = computed<number>(() => stats.value.length)

  /** 按部门分组统计 */
  const statsByDepartment = computed<Record<string, SetupUncoveredStatDto[]>>(() => {
    const groups: Record<string, SetupUncoveredStatDto[]> = {}
    stats.value.forEach((s) => {
      if (!groups[s.departmentCode]) groups[s.departmentCode] = []
      groups[s.departmentCode].push(s)
    })
    return groups
  })

  /**
   * 按大工艺分组统计
   *
   * 10-27：原口径是 `${departmentCode}/${stageCode}` 复合键 —— 那是因为后端当时
   * 不回带 stageCode，该列恒空，复合键让「涉及工艺」恒为 1。P1 落码后 stageCode
   * 已有值（dev 实测 CN_ASSY 等），复合键会把「部门+大工艺」组合数当成工艺数，口径失真。
   * 改为按大工艺单键分组；未回带的行归到「（未回带）」，不与真实大工艺混算。
   */
  const statsByStage = computed<Record<string, SetupUncoveredStatDto[]>>(() => {
    const groups: Record<string, SetupUncoveredStatDto[]> = {}
    stats.value.forEach((s) => {
      const key = s.stageCode?.trim() || '（未回带）'
      if (!groups[key]) groups[key] = []
      groups[key].push(s)
    })
    return groups
  })

  // ===== actions =====

  function setQuery(newQuery: SetupUncoveredQuery): void {
    query.value = newQuery
  }

  async function loadStats(newQuery: SetupUncoveredQuery): Promise<void> {
    listLoading.value = true
    error.value = null
    query.value = newQuery
    try {
      stats.value = await setupApi.getUncoveredStats(newQuery)
    } catch (err) {
      handleError(err)
      stats.value = []
    } finally {
      listLoading.value = false
    }
  }

  /** 导出 CSV（前端生成，不调后端）
   *  10-27：列对齐后端实返字段（去掉不存在的 前产品/后产品/最近出现），并补 Id 列便于回溯
   *  10-28：补 RuleSetVersionId（后端 P1 落码的归属维度） */
  function exportCsv(): string {
    const headers = [
      'ScheduleRunId',
      'RuleSetVersionId',
      'DepartmentId',
      'DepartmentCode',
      'StageCode',
      'OperationCode',
      'ResourceId',
      'ResourceCode',
      'MaterialId',
      'MaterialCode',
      'Count',
      'SampleTaskId'
    ]
    const rows = stats.value.map((s) => [
      String(s.scheduleRunId ?? ''),
      String(s.ruleSetVersionId ?? ''),
      String(s.departmentId),
      s.departmentCode,
      s.stageCode ?? '',
      s.operationCode,
      String(s.resourceId),
      s.resourceCode,
      String(s.materialId),
      s.materialCode,
      String(s.count),
      String(s.sampleTaskId ?? '')
    ])
    return [headers.join(','), ...rows.map((r) => r.join(','))].join('\n')
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
    stats.value = []
    query.value = null
    error.value = null
  }

  return {
    // state
    stats,
    query,
    listLoading,
    error,
    // getters
    canView,
    statCount,
    statsByDepartment,
    statsByStage,
    // actions
    setQuery,
    loadStats,
    exportCsv,
    handleError,
    reset
  }
})
