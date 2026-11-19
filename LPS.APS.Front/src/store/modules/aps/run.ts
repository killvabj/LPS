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
  strategyProfileApi,
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

/** 后端给的是 JSON 数组字符串；解析失败不抛，按 null 走「这次排产没登记排程域」 */
function parseDomainKeysJson(raw?: string | null): DomainKey[] | null {
  if (!raw) return null
  try {
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed) ? (parsed as DomainKey[]) : null
  } catch {
    return null
  }
}

/** 后端行 → 列表项（补解析后的排程域数组） */
function toRunListItem(r: ScheduleRunGovDto): RunListItem {
  return {
    ...r,
    expectedDomainKeys: parseDomainKeysJson(r.expectedDomainKeysJson)
  }
}

/** 分页端点不可用时（后端未部署）从数组端点取的条数 */
const RUNS_FALLBACK_TAKE = 50

export const useRunStore = defineStore('aps.run', () => {
  // ===== state =====
  const runs = ref<RunListItem[]>([])
  /**
   * 分页状态（3号位 2026-11-12 R4：服务端分页端点 `GET /api/governance/runs/paged`）
   *  - `runsTotal` = **过滤后**全量条数（服务端给，与列表同 where）
   *  - `runsPagedAvailable` = 分页端点是否可用；false = 该端点尚未部署，已降级到数组端点
   */
  const runsTotal = ref(0)
  const runsPage = ref(1)
  const runsPageSize = ref(20)
  const runsPagedAvailable = ref(false)
  const activeVersions = ref<ActiveVersionPanel[]>([])
  /**
   * **最近一次批次**的域状态 —— 主视图（批次卡）用，恒跟最新批次，不受用户切换影响。
   *
   * ⚠️ 2026-11-17：拆解卡改成「可跟随用户选中的批次」后，这里必须与卡片那份分开 ——
   *    否则主卡写着 #999469、域状态分布却是 #999436 的数 ⇒ 用户会把两批的结论混成一批。
   */
  const latestDomainStatuses = ref<RunDomainStatusItem[]>([])
  /** 「排程域状态拆解」卡当前展示的域状态（默认 = 最近一次；用户点表格行可切到别的批次） */
  const domainStatuses = ref<RunDomainStatusItem[]>([])
  /** 拆解卡当前对应的批次；null = 尚无可取数的批次 */
  const domainStatusRunId = ref<number | null>(null)
  /**
   * 用户显式选中的批次（拆解卡跟随它）；null = 跟最近一次批次。
   *
   * 存整行而不是只存 Id：算「这次排产有没有登记排程域」（分母）要用行上的
   * `expectedDomainKeys`，翻页/刷新后列表里可能已没有这一行，只存 Id 会取不到分母。
   */
  const selectedDomainRun = ref<RunListItem | null>(null)
  /**
   * 取数的这次 Run 是否**登记过**预期域（分母）。
   *
   * ⚠️ 5号位 2026-10-19 R3 收口裁定：夹具 Run（TEST_*）的 `ExpectedDomainKeysJson`
   * **不回填**，`/api/domain-status/{runId}` 对其 404 是**正确终态**、不是故障；
   * 但「有真分母的 Run 出现 404」是**新缺陷**。两者在 HTTP 层同为 404，
   * 只有本标志分得开 —— 否则页面会把一次真故障写成「没登记要跑哪些排程域」。
   */
  const domainStatusHasDenominator = ref(false)

  /**
   * 策略包版本 Id → 版本码（仅用于把 Run 上的内部 Id 显示成人能读的版本码）。
   *
   * 数据面 ④ `GET /api/governance/strategy-profiles/versions`（3号位，权限 aps.strategy.view）。
   * 取不到时**不冒充「没有版本」**：置空对照表 + `strategyProfileVersionLookupOk=false`，
   * 页面显示「—」并在悬停时说明取数原因。
   */
  const strategyProfileVersionCodes = ref<Record<number, string>>({})
  const strategyProfileVersionLookupOk = ref(true)

  const loading = ref(false)
  const error = ref<string | null>(null)
  const errors = ref<RunSectionError[]>([])
  /** 最近一次 FAILED 恢复结果（U19 验收：原 Run 仍 FAILED + 新 Run 产生） */
  const lastRecover = ref<RecoverFailedRunResult | null>(null)
  const recovering = ref(false)

  // ===== getters =====
  /**
   * P1-15 业务范围：**只用于写操作的前端防御**（后端亦有二次校验）。
   *
   * 批次**可见性**不由前端判 —— 一律以服务端返回为准（3号位 2026-11-12 R4 铁律：
   * 「服务端是唯一裁决方，前端不再自判范围」）。
   */
  const apsAuth = useApsAuthStore()

  /**
   * 可见批次 = **服务端返回的批次**。
   *
   * ⚠️ 2026-11-12：旧写法在前端又按 `dataScope.domainKeys` 过滤一次，且「没登记预期域的批次」
   *    一律放行。3号位 R4 裁定「前端不再自判范围」+ Q2 裁定「没登记排程域的批次仅 Global 可见」
   *    ⇒ 这层前端判定已删除；范围收窄改由服务端负责：
   *    `GET /api/governance/runs` / `runs/paged` 已加 Domain 授权过滤（R1 已落码，
   *    `OPENJSON` EXISTS 精确过滤，total 与列表同 where）。
   */
  const visibleRuns = computed(() => runs.value)

  /**
   * 最近一次排产（= **最近一次「可见」排产**）。
   *
   * 2026-11-12 3号位 R4 追加要求：主视图按「最近可见批次」定位，不取「全局最近批次」——
   * 受限账号不该看到自己无权看的批次。可见性由服务端过滤决定（见 `visibleRuns`）。
   *
   * ⚠️ 2026-11-11：后端 `ScheduleRunRepository.cs:61` 是 `ORDER BY [Id] DESC`，
   *    **不是按启动时间**；Id 与启动时间不同序时（补录历史批次、并发写入）
   *    直接取 `[0]` 会把「最近一次」认错 ⇒ 此处显式按 `startedAt` 倒序。
   */
  const runsByStartedDesc = computed(() =>
    [...visibleRuns.value].sort(
      (a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()
    )
  )

  /**
   * 最近一次排产（= **最近一次「可见」排产**）—— 主视图用的那一条。
   *
   * ⚠️ 服务端分页后**不能**拿「当前页第一行」当主视图：翻到第 2 页时主视图会换成第 2 页的批次。
   *    ⇒ 主视图只跟第 1 页走（3号位 2026-11-12 R4 追加要求：按「最近可见批次」定位，
   *    不取「全局最近批次」）。可见性由服务端过滤决定（见 `visibleRuns`）。
   */
  const latestRun = ref<RunListItem | null>(null)

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
  /** 单个数据面的失败信息（页面按块提示，而不是整页报错） */
  const failed = (section: string, err: unknown, fallback: string) =>
    errors.value.push({
      section,
      message:
        err instanceof ApiError
          ? `[${err.code}] ${err.message}`
          : ((err as Error)?.message ?? fallback)
    })

  /**
   * 取一个批次的域状态。
   *
   * @param target 'latest' 写进 `latestDomainStatuses`（主视图用）；'card' 写进 `domainStatuses`（拆解卡用）
   *
   * 后端对「该 Run 没登记 ExpectedDomainKeysJson」的 run 返 404（5号位 R3 裁定：**终态**，不是故障）
   * ⇒ 只有「有分母还 404」才进 errors[]，否则页面会把一次真故障写成「没登记要跑哪些排程域」。
   */
  async function fetchDomainStatus(run: RunListItem, target: 'latest' | 'card'): Promise<void> {
    const sink = target === 'latest' ? latestDomainStatuses : domainStatuses
    const hasDenominator = Boolean(run.expectedDomainKeys?.length)
    try {
      sink.value = await overviewApi.getRunDomainStatus(run.id)
    } catch (err) {
      sink.value = []
      const terminalNoDenominator = err instanceof ApiError && err.code === 404 && !hasDenominator
      if (!terminalNoDenominator) {
        failed('排程域状态', err, `加载批次 #${run.id} 的排程域状态失败`)
      }
    }
  }

  /**
   * 装载「排程域状态拆解」卡当前该显示哪一批：用户显式选中优先，否则跟最近一次。
   *
   * 选中的就是最近一次 ⇒ 直接复用主视图已取过的那份，不再发第二次请求。
   */
  async function loadDomainStatusCard(): Promise<void> {
    const target = selectedDomainRun.value ?? latestRun.value
    if (!target) {
      domainStatuses.value = []
      domainStatusRunId.value = null
      domainStatusHasDenominator.value = false
      return
    }
    domainStatusRunId.value = target.id
    domainStatusHasDenominator.value = Boolean(target.expectedDomainKeys?.length)
    if (target.id === latestRun.value?.id) {
      domainStatuses.value = latestDomainStatuses.value
      return
    }
    await fetchDomainStatus(target, 'card')
  }

  /**
   * 点「最近排产记录」里的某一行 ⇒ 拆解卡切到该批次的域状态。
   *
   * @param run 选中的批次行；传 null = 回到最近一次
   */
  async function selectDomainRun(run: RunListItem | null): Promise<void> {
    selectedDomainRun.value = run
    await loadDomainStatusCard()
  }

  /**
   * 页面数据装载。
   *
   * @param page 批次记录表要显示的页（1 基）；不传 = 保持当前页（刷新不跳页）
   */
  async function load(page?: number): Promise<void> {
    loading.value = true
    error.value = null
    errors.value = []
    const targetPage = page ?? runsPage.value

    // ---- ① Run 列表（页面主体）----
    try {
      // 优先服务端分页端点（R4，3号位已落码但**尚未部署**）⇒ 取不到时降级到数组端点，
      //   页面照常有数据、只是不能翻页（runsPagedAvailable=false，页面不显示翻页控件）。
      const paged = await runApi
        .listRunsPaged({ page: targetPage, pageSize: runsPageSize.value })
        .catch(() => null)
      if (paged) {
        runs.value = paged.items.map(toRunListItem)
        runsTotal.value = paged.total ?? paged.items.length
        runsPage.value = paged.page ?? targetPage
        runsPagedAvailable.value = true
      } else {
        const rows = await runApi.listRuns(RUNS_FALLBACK_TAKE)
        runs.value = rows.map(toRunListItem)
        runsTotal.value = rows.length
        runsPage.value = 1
        runsPagedAvailable.value = false
      }
      // 主视图只跟第 1 页走（翻页不应把主视图换成第 2 页的批次）
      if (runsPage.value === 1) {
        latestRun.value = runsByStartedDesc.value[0] ?? null
      }
    } catch (err) {
      runs.value = []
      latestRun.value = null
      failed('排产记录', err, '加载排产记录失败')
    }

    // ---- ② 生效版本（5号位）----
    try {
      activeVersions.value = await overviewApi.getActivePlans()
    } catch (err) {
      activeVersions.value = []
      failed('生效版本', err, '加载生效版本失败')
    }

    // ---- ③ 域状态（5号位）----
    //  ③-1 最近一次批次的域状态 → 主视图「排程域状态」分布与失败警示（**恒跟最新批次**）
    //  ③-2 拆解卡当前批次 → 默认最近一次；用户点「最近排产记录」某一行可切到别的批次
    //  没有 runId 就不发请求（路由是 {scheduleRunId:int}）。
    //  后端对「该 Run 没登记 ExpectedDomainKeysJson」的 run 返 404（实测 run 160 如此，
    //  45 条里 37 条属此类）⇒ 走卡片里的空态，不进 errors[]、不弹全局 toast。
    const latest = latestRun.value
    if (!latest) {
      latestDomainStatuses.value = []
      domainStatuses.value = []
      domainStatusRunId.value = null
      domainStatusHasDenominator.value = false
    } else {
      // 选中的批次若还在当前列表里，用新行替换旧行（状态/登记域可能已变）
      if (selectedDomainRun.value) {
        const again = runs.value.find((r) => r.id === selectedDomainRun.value?.id)
        if (again) selectedDomainRun.value = again
      }
      await fetchDomainStatus(latest, 'latest')
      await loadDomainStatusCard()
    }

    // ---- ④ 策略包版本码对照（3号位）----
    // 只是把 Run 上的策略包版本 Id 翻成版本码；取不到不阻塞页面主体，
    // 页面显示「—」并在悬停时说明（不冒充「这一版没有策略包」）。
    try {
      const rows = await strategyProfileApi.listProfileVersionRows()
      const map: Record<number, string> = {}
      rows.forEach((r) => {
        map[r.versionId] = r.versionCode
      })
      strategyProfileVersionCodes.value = map
      strategyProfileVersionLookupOk.value = true
    } catch {
      strategyProfileVersionCodes.value = {}
      strategyProfileVersionLookupOk.value = false
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

  /** 翻页 / 改每页条数（页面只走这一个入口；改条数后回到第 1 页） */
  async function changePage(page: number, size?: number): Promise<void> {
    if (size && size !== runsPageSize.value) runsPageSize.value = size
    await load(page)
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
    runsTotal.value = 0
    runsPage.value = 1
    runsPagedAvailable.value = false
    latestRun.value = null
    activeVersions.value = []
    latestDomainStatuses.value = []
    domainStatuses.value = []
    domainStatusRunId.value = null
    domainStatusHasDenominator.value = false
    selectedDomainRun.value = null
    strategyProfileVersionCodes.value = {}
    strategyProfileVersionLookupOk.value = true
    error.value = null
    errors.value = []
    lastRecover.value = null
  }

  return {
    runs,
    runsTotal,
    runsPage,
    runsPageSize,
    runsPagedAvailable,
    visibleRuns,
    runsByStartedDesc,
    latestRun,
    activeVersions,
    latestDomainStatuses,
    domainStatuses,
    domainStatusRunId,
    domainStatusHasDenominator,
    selectedDomainRun,
    strategyProfileVersionCodes,
    strategyProfileVersionLookupOk,
    loading,
    error,
    errors,
    lastRecover,
    recovering,
    runStatusCounts,
    attentionRuns,
    load,
    changePage,
    selectDomainRun,
    recoverFailedRun,
    validateDomainKeys,
    reset,
    RUN_STATUS_LABEL,
    RUN_STATUS_TAG,
    RUN_TYPE_LABEL
  }
})
