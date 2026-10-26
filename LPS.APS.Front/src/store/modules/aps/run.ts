/**
 * APS V1 4号位 — 运行治理 Pinia store（页面 9：运行与下发）
 *
 * 2026-10-16 重整（依据《4号位致3号位/5号位·运行页联调缺口回执》）：
 *  - 删掉自拟聚合体 `RunStatusDto`：后端 `GET /api/governance/runs` 只返 `ScheduleRunGov[]` 裸数组，
 *    端点说明已写明「只查 ScheduleRun 表、不做跨域综合统计」⇒ 页面按聚合体取值时
 *    `recentRuns` 恒 undefined，整页 KPI 恒 0（dev 实有 44 次排产）
 *  - 改为三个真实数据面各自独立调、各自降级：
 *      ① Run 列表   `GET /api/governance/runs`       （3号位）
 *      ② 生效版本   `GET /api/overview/active-plan`   （5号位）
 *      ③ 域状态拆解 `GET /api/domain-status/{runId}`  （5号位，取最近一次 Run）
 *  - 状态词表改为冻结 07 §4.5 权威四值 `RUNNING / COMPLETED / PARTIAL_SUCCESS / FAILED`
 *    （此前自拟的 `PENDING` / `SUCCESS` 冻结里不存在，且漏了 `COMPLETED`
 *      ⇒ 「成功」卡恒 0、后端真跑出 COMPLETED 时不计入任何卡片）
 *  - 删除 MES 全部状态（资格样本 / 下发 / 回执）：后端无资格查询端点，
 *    `POST /api/mes/dispatch` 只认 `factoryCode` 且是登记 stub，冻结 §十 要求的
 *    4号位 → 5号位 → 2号位 链路前端也接不上
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  runApi,
  overviewApi,
  ApiError,
  type ActiveVersionPanel,
  type RecoverFailedRunResult,
  type RunDomainStatusItem,
  type RunStatus,
  type RunType,
  type ScheduleRunGovDto,
  type DomainKey
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'
import { assertScope, ScopeViolationError } from './scope'

/** 冻结 07 §4.5 ScheduleRun.Status 权威四值 */
export const RUN_STATUS_LABEL: Record<RunStatus, string> = {
  RUNNING: '运行中',
  COMPLETED: '已完成',
  PARTIAL_SUCCESS: '部分成功',
  FAILED: '失败'
}

export const RUN_STATUS_TAG: Record<RunStatus, 'success' | 'warning' | 'danger' | 'primary'> = {
  RUNNING: 'primary',
  COMPLETED: 'success',
  PARTIAL_SUCCESS: 'warning',
  FAILED: 'danger'
}

/** 冻结 07 §4.5 运行类型五值（含 SIMULATION，后端 DTO 注释列明但此前前端缺） */
export const RUN_TYPE_LABEL: Record<RunType, string> = {
  FULL_SCHEDULE: '全量排产',
  MANUAL_RESCHEDULE: '手工重排',
  LOCAL_RESCHEDULE: '局部重排',
  SIMULATION: '仿真运行',
  INSERT_ORDER_WHATIF: '插单 WhatIf'
}

/** 单个数据面的失败信息（页面按块提示，而不是整页报错） */
export interface RunSectionError {
  section: string
  message: string
}

/** 列表卡片用（已归一，模板不必处理后端省略掉的 null 字段） */
export interface RunListItem extends ScheduleRunGovDto {
  /** 后端给的是 JSON 字符串（如 `["FAMILY_INJECTION"]`）；无值或解析失败为 null */
  expectedDomainKeys: DomainKey[] | null
}

/** 后端给的是 JSON 数组字符串；解析失败不抛，按 null 走「无域限定」放行 */
function parseDomainKeysJson(raw?: string | null): DomainKey[] | null {
  if (!raw) return null
  try {
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed) ? (parsed as DomainKey[]) : null
  } catch {
    return null
  }
}

export const useRunStore = defineStore('aps.run', () => {
  // ===== state =====
  const runs = ref<RunListItem[]>([])
  const activeVersions = ref<ActiveVersionPanel[]>([])
  const domainStatuses = ref<RunDomainStatusItem[]>([])
  /** 域状态对应的 Run（取最近一次）；null = 尚无可取数的 Run */
  const domainStatusRunId = ref<number | null>(null)
  /**
   * 取数的这次 Run 是否**登记过**预期域（分母）。
   *
   * ⚠️ 5号位 2026-10-19 R3 收口裁定：夹具 Run（TEST_*）的 `ExpectedDomainKeysJson`
   * **不回填**，`/api/domain-status/{runId}` 对其 404 是**正确终态**、不是故障；
   * 但「有真分母的 Run 出现 404」是**新缺陷**。两者在 HTTP 层同为 404，
   * 只有本标志分得开 —— 否则页面会把一次真故障写成「没登记要跑哪些排程域」。
   */
  const domainStatusHasDenominator = ref(false)

  const loading = ref(false)
  const error = ref<string | null>(null)
  const errors = ref<RunSectionError[]>([])
  /** 最近一次 FAILED 恢复结果（U19 验收：原 Run 仍 FAILED + 新 Run 产生） */
  const lastRecover = ref<RecoverFailedRunResult | null>(null)
  const recovering = ref(false)

  // ===== getters =====
  /** P1-15：业务范围（前端防御；后端亦有二次校验） */
  const apsAuth = useApsAuthStore()

  /**
   * Run 是否落在当前用户业务范围内。
   *
   * ⚠️ 2026-10-16：后端 `ScheduleRunGov` 只给 `ExpectedDomainKeysJson` **JSON 字符串**，
   *    且 dev 实测 44 条里只有 7 条有值（其余被 WhenWritingNull 省略）。
   *    缺该字段时**按放行处理**（无域限定 = 全员可见），不猜、不拿别的字段顶替 ——
   *    否则 37 条会被静默隐藏、页面又变成「空」。已随函请后端确认该字段是否应补齐。
   */
  function runInScope(run: RunListItem): boolean {
    const keys = apsAuth.dataScope.domainKeys
    if (keys.length === 0) return true
    if (!run.expectedDomainKeys || run.expectedDomainKeys.length === 0) return true
    return run.expectedDomainKeys.some((k) => keys.includes(k))
  }

  const visibleRuns = computed(() => runs.value.filter(runInScope))
  const latestRun = computed<RunListItem | null>(() => visibleRuns.value[0] ?? null)

  /** 各状态计数（四值齐全，未出现的状态为 0，不开新键） */
  const runStatusCounts = computed(() => {
    const counts: Record<RunStatus, number> = {
      RUNNING: 0,
      COMPLETED: 0,
      PARTIAL_SUCCESS: 0,
      FAILED: 0
    }
    visibleRuns.value.forEach((r) => {
      counts[r.status] = (counts[r.status] ?? 0) + 1
    })
    return counts
  })

  /** 有失败 / 部分成功需要用户关注的 Run */
  const attentionRuns = computed(() =>
    visibleRuns.value.filter((r) => r.status === 'FAILED' || r.status === 'PARTIAL_SUCCESS')
  )

  // ===== actions =====
  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    errors.value = []
    const failed = (section: string, err: unknown, fallback: string) =>
      errors.value.push({
        section,
        message:
          err instanceof ApiError
            ? `[${err.code}] ${err.message}`
            : ((err as Error)?.message ?? fallback)
      })

    // ---- ① Run 列表（页面主体）----
    try {
      const rows = await runApi.listRuns(50)
      runs.value = rows.map((r) => ({
        ...r,
        expectedDomainKeys: parseDomainKeysJson(r.expectedDomainKeysJson)
      }))
    } catch (err) {
      runs.value = []
      failed('排产记录', err, '加载排产记录失败')
    }

    // ---- ② 生效版本（5号位）----
    try {
      activeVersions.value = await overviewApi.getActivePlans()
    } catch (err) {
      activeVersions.value = []
      failed('生效版本', err, '加载生效版本失败')
    }

    // ---- ③ 域状态拆解（5号位，入参取最近一次 Run）----
    //  没有 runId 就不发请求（路由是 {scheduleRunId:int}）。
    //  后端对「该 Run 没登记 ExpectedDomainKeysJson」的 run 返 404（实测 run 160 如此，
    //  45 条里 37 条属此类）⇒ 走卡片里的空态，不进 errors[]、不弹全局 toast。
    const latest = visibleRuns.value[0]
    if (!latest) {
      domainStatuses.value = []
      domainStatusRunId.value = null
      domainStatusHasDenominator.value = false
    } else {
      domainStatusRunId.value = latest.id
      domainStatusHasDenominator.value = Boolean(latest.expectedDomainKeys?.length)
      try {
        domainStatuses.value = await overviewApi.getRunDomainStatus(latest.id)
      } catch (err) {
        domainStatuses.value = []
        // 只豁免「本来就没分母」的 404（5号位 R3 裁定：夹具 Run 不回填，404 即终态）。
        // 有分母还 404 = 新缺陷，必须进 errors[]，不能被空态文案盖成正常。
        const terminalNoDenominator =
          err instanceof ApiError && err.code === 404 && !domainStatusHasDenominator.value
        if (!terminalNoDenominator) {
          failed('排程域状态', err, '加载排程域状态失败')
        }
      }
    }

    loading.value = false
  }

  /**
   * 校验 ScheduleRun.ExpectedDomainKeysJson 冻结规则（v1.4 §十九.2）
   *  - 在 recoverFailedRun 之前调用，避免"空恢复"撞 400
   *  - 返回 { valid, message }；UI 层据此判断是否继续走恢复流程
   *  - v1.4 §十九.2：FULL ≥ 1 Domain / RESCHEDULE 恰 1 Domain
   */
  async function validateDomainKeys(
    scheduleRunId: number
  ): Promise<{ valid: boolean; message: string }> {
    try {
      const res = await runApi.validateDomainKeys(scheduleRunId)
      if (!res.valid) {
        errors.value = [{ section: 'DomainKey 校验', message: res.message }]
      }
      return res
    } catch (err) {
      handleError(err)
      return { valid: false, message: (err as Error)?.message ?? '校验异常' }
    }
  }

  /**
   * FAILED Run 人工恢复（U19：新建 Run，原始 Run 仍 FAILED）
   *  - 4 号位文档第 15 节：FAILED 人工恢复按钮必须调用"新建 ScheduleRun"，不能把 FAILED 直接改回 RUNNING
   *  - 恢复完成后 refresh，新 Run 应出现在 runs 列表顶部，原 Run 仍为 FAILED
   *  - P1-15：payload.domainKeys 中每个 Domain 都必须在当前用户业务范围内
   */
  async function recoverFailedRun(payload: {
    originalRunId: number
    domainKeys: DomainKey[]
    reason: string
    actor: string
  }): Promise<RecoverFailedRunResult> {
    // P1-15：业务范围断言（防止"指定 Domain 确认人恢复其它 Domain"—— §二十五.7）
    for (const dk of payload.domainKeys) {
      try {
        assertScope(apsAuth.dataScope, { domainKey: dk })
      } catch (err) {
        if (err instanceof ScopeViolationError) {
          handleError(err)
          throw err
        }
        throw err
      }
    }
    // v1.4 §十九.2：预校验（避免空恢复直接 400）
    const check = await runApi.validateDomainKeys(payload.originalRunId)
    if (!check.valid) {
      const msg = `DomainKey 冻结规则校验失败：${check.message}（请先修正再恢复）`
      handleError(new Error(msg))
      throw new Error(msg)
    }
    recovering.value = true
    error.value = null
    try {
      lastRecover.value = await runApi.recoverFailedRun(payload)
      // U19 验收：刷新后原 Run 仍 FAILED + 新 Run 出现在列表
      await load()
      return lastRecover.value
    } catch (err) {
      handleError(err)
      throw err
    } finally {
      recovering.value = false
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
    runs.value = []
    activeVersions.value = []
    domainStatuses.value = []
    domainStatusRunId.value = null
    domainStatusHasDenominator.value = false
    error.value = null
    errors.value = []
    lastRecover.value = null
  }

  return {
    runs,
    visibleRuns,
    latestRun,
    activeVersions,
    domainStatuses,
    domainStatusRunId,
    domainStatusHasDenominator,
    loading,
    error,
    errors,
    lastRecover,
    recovering,
    runStatusCounts,
    attentionRuns,
    runInScope,
    load,
    recoverFailedRun,
    validateDomainKeys,
    reset,
    RUN_STATUS_LABEL,
    RUN_STATUS_TAG,
    RUN_TYPE_LABEL
  }
})
