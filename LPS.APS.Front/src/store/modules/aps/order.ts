/**
 * APS V1 4号位 — 订单 / 需求计划 Pinia store（页面 2）
 *
 * 必填 `planVersionId`（5号位 2026-10-17 回执 §五）：
 *  - 三个端点全必填，漏传一律 `code=400 "planVersionId is required"`（实测）
 *  - 用户**显式选版本**，默认取 `GET /api/schedule/versions` 的**第一条**（最新），不自动猜 ACTIVE
 *    —— dev 当前无 ACTIVE 版本，猜 ACTIVE 会让页面恒空
 *
 * 服务端分页：列表 / 合计都取 `/api/order-query` 的 `PageResult{items,total,page,pageSize}`，
 * `listTotal` 用服务端 `total`，不用 `items.length`。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  orderApi,
  ApiError,
  scheduleApi,
  type OrderDetailDto,
  type OrderListItemDto,
  type OrderQuery,
  type OrderSummaryDto,
  type PlanVersionSummaryDto
} from '@/api/aps-v1'

/** 后端 `OrderQueryService` 上限 200（2026-10-17 起越界夹到 200，原来静默回落 50）⇒ 前端仍自夹作双保险 */
const PAGE_SIZE_CAP = 200
const DEFAULT_PAGE_SIZE = 20

export const useOrderStore = defineStore('aps.order', () => {
  // ===== state =====
  const list = ref<OrderListItemDto[]>([])
  const listTotal = ref(0)
  const pageIndex = ref(1)
  const pageSize = ref(DEFAULT_PAGE_SIZE)
  const listLoading = ref(false)
  const listError = ref<string | null>(null)

  const summary = ref<OrderSummaryDto | null>(null)
  const summaryLoading = ref(false)

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
  const filteredTotalLabel = computed(() => `共 ${listTotal.value} 条`)

  const currentVersion = computed<PlanVersionSummaryDto | null>(
    () => versions.value.find((v) => v.id === planVersionId.value) ?? null
  )

  const planVersionIdReady = computed<boolean>(
    () => planVersionId.value !== null && planVersionId.value > 0
  )

  /**
   * 未计算订单数 = 总数 − 已算出的（未延迟 + 延迟）。
   *
   * 冻结 `07:2666` 值域里没有 NULL，但 `DelayStatus` 列可空；NULL = **未计算**，不是「按期」。
   * v24 实测 13,577 + 7,941 = 21,518 = 总数，即该版本无未计算行；其它版本未必。
   */
  const uncalculatedCount = computed<number>(() => {
    const s = summary.value
    if (!s) return 0
    return Math.max(0, s.totalCount - s.onTimeCount - s.delayedCount)
  })

  // ===== actions =====
  async function loadVersions(): Promise<void> {
    versionsLoading.value = true
    try {
      versions.value = await scheduleApi.getVersions(100)
      if (planVersionId.value === null) {
        planVersionId.value = versions.value[0]?.id ?? null
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
      listTotal.value = 0
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
      listTotal.value = res.total ?? 0
    } catch (err) {
      list.value = []
      listTotal.value = 0
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
    await Promise.all([loadList(resetPage), loadSummary()])
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
    listTotal.value = 0
    currentDetail.value = null
    summary.value = null
    query.value = {}
    pageIndex.value = 1
    pageSize.value = DEFAULT_PAGE_SIZE
    listError.value = null
    detailError.value = null
  }

  function handleError(target: typeof listError, err: unknown): void {
    if (err instanceof ApiError) {
      target.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
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
    uncalculatedCount,
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
    loadDetail,
    refresh,
    setPlanVersion,
    setFilter,
    clearFilter,
    setPage,
    reset
  }
})
