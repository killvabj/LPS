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
 *
 * 10-29 变更（3号位 方案 A 服务端分页落码后）：
 *  - stats = **当前页**行；total = 后端返回的过滤后总行数（分页器用，不是 stats.length）
 *  - 后端分页对象另带 2 个 KPI 计数，本页按用户口径**只取 total**，其余不展示也不落 state
 *  - 列表走后端分页端点（传 pageIndex/pageSize）；CSV 导出改走全量端点重新拉一次，
 *    否则只会导出当前页（静默漏导）
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

/** 导出结果：CSV 文本 + 实际导出行数（服务端分页后导出走全量重拉，行数 ≠ 当前页行数） */
export interface SetupUncoveredCsvResult {
  csv: string
  count: number
}

export const useSetupUncoveredStore = defineStore('aps.setupUncovered', () => {
  // ===== state =====
  /** 当前页的聚合行（服务端分页后不再是全量） */
  const stats = ref<SetupUncoveredStatDto[]>([])
  /** 过滤后的总行数（后端返回，分页器 total 用；不是 stats.length） */
  const total = ref<number>(0)
  const query = ref<SetupUncoveredQuery | null>(null)

  const listLoading = ref(false)
  const error = ref<string | null>(null)

  // ===== auth =====
  const apsAuth = useApsAuthStore()

  // ===== getters =====

  /** 查看权限 */
  const canView = computed<boolean>(() => apsAuth.has('aps.setup.view'))

  // ===== actions =====

  function setQuery(newQuery: SetupUncoveredQuery): void {
    query.value = newQuery
  }

  /**
   * 加载当前页（服务端分页，方案 A）
   *  - 传了 pageIndex/pageSize ⇒ 后端返回分页对象（items/total/…）
   *  - 失败清空 stats 且 total 归 0（不留半截状态）
   */
  async function loadStats(
    newQuery: SetupUncoveredQuery,
    pageIndex = 1,
    pageSize = 20
  ): Promise<void> {
    listLoading.value = true
    error.value = null
    query.value = newQuery
    try {
      const page = await setupApi.getUncoveredStatsPage({ ...newQuery, pageIndex, pageSize })
      stats.value = page.items
      total.value = page.total
    } catch (err) {
      handleError(err)
      stats.value = []
      total.value = 0
    } finally {
      listLoading.value = false
    }
  }

  /**
   * 导出 CSV（前端拼装）
   *  10-27：列对齐后端实返字段（去掉不存在的 前产品/后产品/最近出现），并补 Id 列便于回溯
   *  10-28：补 RuleSetVersionId（后端 P1 落码的归属维度）
   *  10-29：列表改服务端分页后，stats 只有当前页 ⇒ 导出**重新拉一次全量**（不带分页参），
   *         避免只导出当前页静默漏导。返回 { csv, count }，count = 实际导出行数。
   */
  async function exportCsv(): Promise<SetupUncoveredCsvResult> {
    const current = query.value
    if (!current) return { csv: '', count: 0 }
    const all = await setupApi.getUncoveredStats(current)
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
    const rows = all.map((s) => [
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
    return {
      csv: [headers.join(','), ...rows.map((r) => r.join(','))].join('\n'),
      count: all.length
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
    stats.value = []
    total.value = 0
    query.value = null
    error.value = null
  }

  return {
    // state
    stats,
    total,
    query,
    listLoading,
    error,
    // getters
    canView,
    // actions
    setQuery,
    loadStats,
    exportCsv,
    handleError,
    reset
  }
})
