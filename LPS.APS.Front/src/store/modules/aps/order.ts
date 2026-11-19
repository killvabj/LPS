/**
 * APS V1 4号位 — 订单 / 需求计划 Pinia store（页面 2）
 *
 * 必填 `planVersionId`（5号位 2026-10-17 回执 §五）：
 *  - 三个端点全必填，漏传一律 `code=400 "planVersionId is required"`（实测）
 *  - 用户**显式选版本**，默认取 `GET /api/schedule/versions` 的**第一条**（最新），不自动猜 ACTIVE
 *    —— dev 当前无 ACTIVE 版本，猜 ACTIVE 会让页面恒空
 *
 * 服务端分页：列表 / 合计都取 `/api/order-query` 的 `PageResult{items,total,page,pageSize}`，
 * `listTotal` 用服务端 `total`，不用 `items.length`；取不到（失败 / 端点没给 total）时
 * 保持 **null**（未提供），不当 0（§0.2 ②「NULL=未知，不得转 0」）。
 *
 * summary 三桶（按期 / 延迟 / 未排产）为**计划派生**（5号位 2026-11-02 r14009 落码），
 * 与列表 `riskLevel` 同源（冻结 `01 §64`）⇒ 恒等式 `onTime+delayed+unscheduled = total`。
 *
 * 2026-11-17（5号位 B2，r14250～r14255）：**交付 KPI 改走 `GET /api/order-query/delivery-kpi`**——
 * 判定式与列表 `riskLevel` 逐字同文（后端类级常量 `RiskCaseExpr`），
 * 且**随筛选走**（分母 = 当前筛选命中的订单数，含未判定不剔除）。
 * summary 三桶不再当 KPI 展示（两者并排读会自相矛盾），summary 只留
 * 「整版订单总数」与「整版都还没排产」两个非 KPI 用途。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  orderApi,
  ApiError,
  scheduleApi,
  type DeliveryKpiResultDto,
  type OrderDetailDto,
  type OrderListItemDto,
  type OrderQuery,
  type OrderSummaryDto,
  type PlanVersionSummaryDto
} from '@/api/aps-v1'

/** 后端 `OrderQueryService` 上限 200（2026-10-17 起越界夹到 200，原来静默回落 50）⇒ 前端仍自夹作双保险 */
const PAGE_SIZE_CAP = 200
const DEFAULT_PAGE_SIZE = 20

/**
 * 默认计划版本挑选规则（2026-11-12 工厂视角改版；2026-11-17 按矩阵 §0.1 改走 ACTIVE 锚表）
 *
 * 旧规则 = `versions[0]`（`GET /api/schedule/versions` 按「最新优先」返回）
 * ⇒ dev 上最新一条是 `FAILED` 的测试版本 `SYN-PLAN-...`，用户一进页面看到的是
 *    「延迟 0 / 未排产 100%」的空版本，像是系统坏了。
 *
 * **现行规则（5号位 2026-11-16 矩阵 §0.1 给 4号位的硬条款）**：
 *   「任何页面禁按 MAX(PV)/MAX(Run) 选版，一律走 ACTIVE 六域锚表」
 *  1. `isDefault === true` 的行 = **域内当前生效版本**（后端 `PlanVersionSummaryDto.IsDefault`
 *     注释：= `Status='ACTIVE'`，DB 约束 `UQ_PlanVersion_OneActivePerDomain` 保证每域至多一条）
 *     ⇒ 跨域账号会拿到**多条** isDefault 行，按 **`activatedAt` 最新者** 取（后端 DTO 明确要求）
 *  2. 兜底：`ACTIVE` → `CANDIDATE` → 其它非 `FAILED` → 列表第一条
 *
 * ⚠️ 取不到 isDefault 行 ⇒ 该域「当前无正式计划版本」（**不是 0、不是失败**），
 *   页面照实显示，不拿 0 或失败冒充。
 */
export function pickDefaultVersion<
  T extends { status?: string | null; isDefault?: boolean; activatedAt?: string | null }
>(versions: T[]): T | null {
  if (!versions.length) return null
  const defaults = versions.filter((v) => v.isDefault === true)
  if (defaults.length) {
    return (
      [...defaults].sort((a, b) =>
        String(b.activatedAt ?? '').localeCompare(String(a.activatedAt ?? ''))
      )[0] ?? defaults[0]
    )
  }
  const byStatus = (status: string): T | undefined => versions.find((v) => v.status === status)
  return (
    byStatus('ACTIVE') ??
    byStatus('CANDIDATE') ??
    versions.find((v) => v.status !== 'FAILED') ??
    versions[0] ??
    null
  )
}

export const useOrderStore = defineStore('aps.order', () => {
  // ===== state =====
  const list = ref<OrderListItemDto[]>([])
  /**
   * 服务端返回的命中总数
   *
   * ⚠️ **null = 未取到**（请求失败 / 端点没给 total），**不是 0**：
   *   §0.2 ②「NULL=未知，不得转 0」——失败时写 0 等于说「这一版明确没有订单」。
   */
  const listTotal = ref<number | null>(null)
  const pageIndex = ref(1)
  const pageSize = ref(DEFAULT_PAGE_SIZE)
  const listLoading = ref(false)
  const listError = ref<string | null>(null)

  const summary = ref<OrderSummaryDto | null>(null)
  const summaryLoading = ref(false)

  /**
   * 交付 KPI（5号位 2026-11-17 B2：新增 `GET /api/order-query/delivery-kpi`）
   *
   * ⚠️ 与 `summary` 是**两套读法**：本端点 = 交付 KPI 唯一口径（计划派生、日期级、
   *   当天完成=按期、**随筛选走**）；summary 三桶不再当 KPI 展示
   *   （5号位 11-17 复函 §五：别把两者并排读）。
   *   `summary` 只留两个非 KPI 用途：整版订单总数、整版「都还没排产」提示。
   */
  const deliveryKpi = ref<DeliveryKpiResultDto | null>(null)
  const deliveryKpiLoading = ref(false)
  const deliveryKpiError = ref<string | null>(null)

  const currentDetail = ref<OrderDetailDto | null>(null)
  const detailLoading = ref(false)
  const detailError = ref<string | null>(null)

  /** 候选版本（`/api/schedule/versions`），供版本下拉 */
  const versions = ref<PlanVersionSummaryDto[]>([])
  const versionsLoading = ref(false)
  const planVersionId = ref<number | null>(null)

  /** 当前过滤条件（全部对应后端真实 Query 参数） */
  const query = ref<Partial<Omit<OrderQuery, 'planVersionId' | 'pageIndex' | 'pageSize'>>>({})

  // ===== getters =====
  const filteredTotalLabel = computed(() =>
    listTotal.value === null ? '总条数未提供' : `共 ${listTotal.value} 条`
  )

  const currentVersion = computed<PlanVersionSummaryDto | null>(
    () => versions.value.find((v) => v.id === planVersionId.value) ?? null
  )

  const planVersionIdReady = computed<boolean>(
    () => planVersionId.value !== null && planVersionId.value > 0
  )

  // ===== actions =====
  async function loadVersions(): Promise<void> {
    versionsLoading.value = true
    try {
      versions.value = await scheduleApi.getVersions(100)
      if (planVersionId.value === null) {
        planVersionId.value = pickDefaultVersion(versions.value)?.id ?? null
      }
    } catch {
      versions.value = []
    } finally {
      versionsLoading.value = false
    }
  }

  async function loadList(resetPage = false): Promise<void> {
    if (resetPage) pageIndex.value = 1
    if (!planVersionIdReady.value) {
      listError.value =
        '请先选择计划版本（订单按 PlanVersion 分区，后端三个端点均必填 planVersionId）'
      list.value = []
      listTotal.value = null
      return
    }
    listLoading.value = true
    listError.value = null
    try {
      const res = await orderApi.list({
        ...query.value,
        planVersionId: planVersionId.value as number,
        pageIndex: pageIndex.value,
        pageSize: Math.min(pageSize.value, PAGE_SIZE_CAP)
      })
      list.value = res.items ?? []
      listTotal.value = res.total ?? null
    } catch (err) {
      list.value = []
      listTotal.value = null
      handleError(listError, err)
    } finally {
      listLoading.value = false
    }
  }

  async function loadSummary(): Promise<void> {
    if (!planVersionIdReady.value) {
      summary.value = null
      return
    }
    summaryLoading.value = true
    try {
      summary.value = await orderApi.getSummary(planVersionId.value as number)
    } catch (err) {
      // 聚合失败不阻断列表：KPI 整块给空态即可
      summary.value = null
      console.warn('[order.store] loadSummary failed', err)
    } finally {
      summaryLoading.value = false
    }
  }

  /**
   * 取交付 KPI（分区分母随筛选走 ⇒ 传与列表**同一套** `query`）。
   *
   * ⚠️ 后端 `delivery-kpi` 与列表端点**同名同义**筛选参数 ⇒ 直接原样转发，
   *   不做二次加工（前端自算＝两套口径，禁）。
   */
  async function loadDeliveryKpi(): Promise<void> {
    if (!planVersionIdReady.value) {
      deliveryKpi.value = null
      return
    }
    deliveryKpiLoading.value = true
    deliveryKpiError.value = null
    // 旧值先清掉：筛选一变，列表已经是新集合，卡片还挂着旧分母会与列表自相矛盾
    deliveryKpi.value = null
    try {
      deliveryKpi.value = await orderApi.getDeliveryKpi({
        ...query.value,
        planVersionId: planVersionId.value as number
      })
    } catch (err) {
      deliveryKpi.value = null
      handleError(deliveryKpiError, err)
    } finally {
      deliveryKpiLoading.value = false
    }
  }

  async function loadDetail(orderId: number): Promise<void> {
    if (!planVersionIdReady.value) return
    detailLoading.value = true
    detailError.value = null
    currentDetail.value = null
    try {
      currentDetail.value = await orderApi.getDetail(orderId, planVersionId.value as number)
    } catch (err) {
      // 后端 `OrderQueryController.GetOrderDetail` 在 `result == null` 时返 404「Order not found」。
      // 5号位 2026-10-19 全局收口后这会变成真 HTTP 404（原为 `200 + code 404`）。两种形态都
      // 归一到大白话空态，不把后端英文原文 `[404] Order not found` 直接摆给用户。
      if (err instanceof ApiError && err.code === 404) {
        detailError.value = '该订单在当前版本下不存在或已被清理'
      } else {
        handleError(detailError, err)
      }
    } finally {
      detailLoading.value = false
    }
  }

  /** 重取列表 + 聚合（切版本 / 改筛选后调用） */
  async function refresh(resetPage = true): Promise<void> {
    await Promise.all([loadList(resetPage), loadSummary(), loadDeliveryKpi()])
  }

  /**
   * 改筛选条件并刷新（列表 + 交付 KPI 一起）。
   *
   * ⚠️ 交付 KPI 的分母**随筛选走**（5号位 B2 契约）⇒ 筛选一变，卡片必须跟着变，
   *   否则用户会拿「全版 KPI」去对「筛后列表」。
   */
  async function applyFilter(next: Partial<typeof query.value>): Promise<void> {
    query.value = { ...query.value, ...next }
    pageIndex.value = 1
    await Promise.all([loadList(), loadDeliveryKpi()])
  }

  /** 切换计划版本：清空筛选与详情，回到第 1 页 */
  async function setPlanVersion(id: number): Promise<void> {
    if (!Number.isFinite(id) || id <= 0) return
    planVersionId.value = id
    query.value = {}
    currentDetail.value = null
    pageIndex.value = 1
    await refresh(true)
  }

  function setFilter(next: Partial<typeof query.value>): void {
    query.value = { ...query.value, ...next }
  }

  function clearFilter(): void {
    query.value = {}
  }

  function setPage(p: { pageIndex?: number; pageSize?: number }): void {
    if (p.pageIndex !== undefined) pageIndex.value = p.pageIndex
    // 上限夹住：后端 2026-10-17 起也会夹到 200，但前端夹住能保证 UI 显示与实际请求一致
    if (p.pageSize !== undefined) pageSize.value = Math.min(p.pageSize, PAGE_SIZE_CAP)
  }

  function reset(): void {
    list.value = []
    listTotal.value = null
    currentDetail.value = null
    summary.value = null
    deliveryKpi.value = null
    query.value = {}
    pageIndex.value = 1
    pageSize.value = DEFAULT_PAGE_SIZE
    listError.value = null
    detailError.value = null
    deliveryKpiError.value = null
  }

  function handleError(target: typeof listError, err: unknown): void {
    if (err instanceof ApiError) {
      // code 0 = 网络层错误（断网 / 超时），不是 HTTP 状态 ⇒ 不显示 "[0]" 这种没人看得懂的编号
      const prefix = err.code > 0 ? `[${err.code}] ` : ''
      target.value = prefix + err.message + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      target.value = (err as Error)?.message ?? '未知错误'
    }
  }

  return {
    list,
    listTotal,
    pageIndex,
    pageSize,
    listLoading,
    listError,
    summary,
    summaryLoading,
    deliveryKpi,
    deliveryKpiLoading,
    deliveryKpiError,
    currentDetail,
    detailLoading,
    detailError,
    query,
    versions,
    versionsLoading,
    planVersionId,
    currentVersion,
    planVersionIdReady,
    filteredTotalLabel,
    loadVersions,
    loadList,
    loadSummary,
    loadDeliveryKpi,
    loadDetail,
    refresh,
    applyFilter,
    setPlanVersion,
    setFilter,
    clearFilter,
    setPage,
    reset
  }
})
