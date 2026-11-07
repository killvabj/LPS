/**
 * APS V1 4号位 — 排产总览 Pinia store
 *
 * 加载编排（5 个独立端点，无聚合端点）：
 *   1. active-plan          → 各 Domain 生效版本 + **planVersionId 唯一来源**
 *   2. order-query/summary  ┐
 *   3. resource-bottleneck  ┘ 依赖 1 的 planVersionId（无生效版本则跳过，不拿假数）
 *   4. candidate-summary    → 与 planVersionId 无关，始终调
 *   5. domain-status/{runId} → 依赖 1 的 sourceScheduleRunId（null 则**不发请求**）
 *
 * 各端点独立降级：单个失败只记录该端点错误，不清空其它已取到的数据
 * （避免「资源表挂了 ⇒ 订单 KPI 也变 0」这种假象）。
 *
 * 2026-11-04 加「查看版本切换」：原先三块版本相关数据死钉在 active-plan 第一条，
 *   用户在「当前生效版本」面板里看到 3 张卡，却只能看第 1 张那个版本的数据。
 *   现在可点卡片切换 ⇒ 订单摘要 / 资源负荷按**选中版本**取，域状态按**选中版本的 Run** 取。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  orderApi,
  overviewApi,
  type ActiveVersionPanel,
  type OrderSummaryCounts,
  type OverviewResourceBottleneck,
  type RunDomainStatusItem
} from '@/api/aps-v1'

/** 单个端点的失败信息（用于页面按块提示，而不是整页报错） */
export interface OverviewSectionError {
  /** 中文块名，直接可展示 */
  section: string
  message: string
}

const toMessage = (err: unknown, fallback: string): string => {
  if (err instanceof ApiError) return `[${err.code}] ${err.message}`
  return (err as Error)?.message ?? fallback
}

/** 受「切换查看版本」影响的三块（各自独立降级，切版本时逐块重取） */
const VERSION_SCOPED_SECTIONS = ['订单摘要', '资源负荷', '排程域状态']

export const useOverviewStore = defineStore('aps.overview', () => {
  const loading = ref(false)
  const errors = ref<OverviewSectionError[]>([])

  /** 各 Domain 生效版本（active-plan） */
  const activeVersions = ref<ActiveVersionPanel[]>([])
  /** 订单摘要（order-query/summary）—— null 表示「取不到」而非「为 0」 */
  const orderSummary = ref<OrderSummaryCounts | null>(null)
  /** 资源负荷（resource-bottleneck） */
  const resources = ref<OverviewResourceBottleneck[]>([])
  /** 待确认 Candidate 数（candidate-summary） */
  const pendingCandidateCount = ref<number | null>(null)
  /** 排程域状态（domain-status） */
  const domainStatuses = ref<RunDomainStatusItem[]>([])
  /** 是否存在 ACTIVE 版本 —— 决定 2/3/5 是否可调 */
  const hasActivePlan = computed(() => activeVersions.value.length > 0)
  /**
   * 汇总口径版本 = active-plan 数组的第一条。
   * load() 里订单摘要（planVersionId）、资源负荷（planVersionId）、排程域状态（sourceScheduleRunId）
   * 都按它取数 ⇒ 页面标注「这些数字属于哪个域的哪个版本」时用同一个值，别在页面另算一遍。
   */
  const primaryVersion = computed(() => activeVersions.value[0] ?? null)

  /**
   * 用户当前正在查看的生效版本 id（null = 未手动选 = 看第一条）。
   *
   * Why 2026-11-04：页面上「当前生效版本」列了 N 张卡，但下面三块数据永远只显示第一张卡那个版本 ⇒
   *   用户看得见另外两个版本、点不动也看不到。给它一个「正在查看」的语义即可，不必再加一个下拉控件
   *   （同一路径只留一个入口）。
   */
  const selectedPlanVersionId = ref<number | null>(null)

  /** 正在查看的版本对象（选中项不在生效列表里时回落第一条 —— 例如该版本被新版本顶替） */
  const selectedVersion = computed(
    () =>
      activeVersions.value.find((v) => v.planVersionId === selectedPlanVersionId.value) ??
      primaryVersion.value
  )

  async function load(): Promise<void> {
    loading.value = true
    errors.value = []

    const failed = (section: string, err: unknown, fallback: string) =>
      errors.value.push({ section, message: toMessage(err, fallback) })

    // ---- 1. active-plan：其余数据面的入参来源，必须先拿 ----
    let plans: ActiveVersionPanel[] = []
    try {
      plans = await overviewApi.getActivePlans()
      activeVersions.value = plans
    } catch (err) {
      activeVersions.value = []
      failed('生效版本', err, '加载生效版本失败')
    }

    // ---- 2/3/4/5 并行，各自降级 ----
    await Promise.all([
      loadOrderSummary(failed),
      loadResources(failed),
      loadCandidate(failed),
      loadDomainStatus(failed)
    ])

    loading.value = false
  }

  /**
   * 切换「正在查看的生效版本」：只重取受版本影响的三块，
   * 生效版本列表与待确认候选数（与版本无关）不动。
   */
  async function selectVersion(planVersionId: number): Promise<void> {
    if (selectedPlanVersionId.value === planVersionId) return
    selectedPlanVersionId.value = planVersionId
    loading.value = true
    // 切换时先清掉这三块上一次的错误（否则旧版本的报错会挂在新版本的数据旁边）
    errors.value = errors.value.filter((e) => !VERSION_SCOPED_SECTIONS.includes(e.section))
    const failed = (section: string, err: unknown, fallback: string) =>
      errors.value.push({ section, message: toMessage(err, fallback) })
    await Promise.all([loadOrderSummary(failed), loadResources(failed), loadDomainStatus(failed)])
    loading.value = false
  }

  /* ---- 三块「版本相关」数据的取数（load 与 selectVersion 共用 ⇒ 口径单点）---- */

  async function loadOrderSummary(
    failed: (s: string, e: unknown, f: string) => void
  ): Promise<void> {
    const planVersionId = selectedVersion.value?.planVersionId
    if (!planVersionId) {
      orderSummary.value = null
      return
    }
    try {
      orderSummary.value = await orderApi.getSummary(planVersionId)
    } catch (err) {
      orderSummary.value = null
      failed('订单摘要', err, '加载订单摘要失败')
    }
  }

  async function loadResources(failed: (s: string, e: unknown, f: string) => void): Promise<void> {
    const planVersionId = selectedVersion.value?.planVersionId
    if (!planVersionId) {
      resources.value = []
      return
    }
    try {
      resources.value = await overviewApi.getResourceBottleneck(planVersionId)
    } catch (err) {
      resources.value = []
      failed('资源负荷', err, '加载资源负荷失败')
    }
  }

  async function loadDomainStatus(
    failed: (s: string, e: unknown, f: string) => void
  ): Promise<void> {
    // §7.3：sourceScheduleRunId 为 null → 不发请求（路由是 {scheduleRunId:int}）
    const scheduleRunId = selectedVersion.value?.sourceScheduleRunId ?? null
    if (!scheduleRunId) {
      domainStatuses.value = []
      return
    }
    try {
      domainStatuses.value = await overviewApi.getRunDomainStatus(scheduleRunId)
    } catch (err) {
      domainStatuses.value = []
      // 404 = 该 Run 没登记要跑哪些排程域，不是服务故障（与运行页同口径）
      if (!(err instanceof ApiError && err.code === 404)) {
        failed('排程域状态', err, '加载排程域状态失败')
      }
    }
  }

  async function loadCandidate(failed: (s: string, e: unknown, f: string) => void): Promise<void> {
    try {
      pendingCandidateCount.value = await overviewApi
        .getCandidateSummary()
        .then((d) => d.pendingCount)
    } catch (err) {
      pendingCandidateCount.value = null
      failed('待确认候选', err, '加载待确认候选失败')
    }
  }

  return {
    loading,
    errors,
    activeVersions,
    orderSummary,
    resources,
    pendingCandidateCount,
    domainStatuses,
    hasActivePlan,
    primaryVersion,
    selectedVersion,
    selectedPlanVersionId,
    load,
    selectVersion
  }
})
