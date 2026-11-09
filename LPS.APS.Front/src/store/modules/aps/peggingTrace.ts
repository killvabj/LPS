/**
 * APS V1 4号位 — Supply/Pegging 追溯 Pinia store
 *
 * 数据面（全部来自 5号位 `PeggingTraceController`，无 mock、无前端二次聚合）：
 *  - `versions`  ← GET /api/schedule/versions（计划版本下拉）
 *  - `summary`   ← GET /api/pegging-trace/summary（总分配数 + 供给类型/承诺状态分布）
 *  - `list`      ← GET /api/pegging-trace（服务端分页，ORDER BY AllocationSequence）
 *
 * ⚠️ 列表端点 **不返回 total**（bare 数组）⇒ 分页用「上一页 / 下一页 + hasMore」形态：
 *  `hasMore = 本页返回条数 === pageSize`。页面上展示的「总分配数」只能取 summary 的
 *  `totalAllocations`（全量口径），**不能**在筛选后继续拿它当筛选结果的条数 —— 那会说谎。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  peggingTraceApi,
  scheduleApi,
  type PeggingTraceDto,
  type PeggingTraceSummaryDto,
  type PlanVersionSummaryDto
} from '@/api/aps-v1'

/** 单页条数上限：后端 `take > 500` 会静默回落 100 ⇒ 前端自夹 200 作双保险 */
const PAGE_SIZE_CAP = 200
const DEFAULT_PAGE_SIZE = 50

export const usePeggingTraceStore = defineStore('aps.peggingTrace', () => {
  /* ===== state ===== */
  const versions = ref<PlanVersionSummaryDto[]>([])
  const planVersionId = ref<number | null>(null)
  const versionsLoading = ref(false)

  const summary = ref<PeggingTraceSummaryDto | null>(null)
  const summaryLoading = ref(false)

  const list = ref<PeggingTraceDto[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  const filter = ref({
    materialCode: '',
    supplyType: '',
    commitmentStatus: '',
    /**
     * 根订单 **Id**（数字）。不用「订单号」—— dev v2 实测 `RootOrderNo`/`CurrentOrderNo`
     * 在 200/200 行里**全为 NULL**（`WhenWritingNull` 整键省略），后端的 `orderNo LIKE` 过滤
     * 在真实数据上必然查空。改用后端已有的 `GET /api/pegging-trace/order/{orderId}`。
     */
    rootOrderId: '',
    supplyDocumentNo: ''
  })

  const pageIndex = ref(1)
  const pageSize = ref(DEFAULT_PAGE_SIZE)
  const hasMore = ref(false)

  /* ===== getters ===== */
  const currentVersion = computed(
    () => versions.value.find((v) => v.id === planVersionId.value) ?? null
  )
  /** 是否处于筛选态（用于决定「总数」能否按当前列表推断） */
  const hasFilter = computed(() => Object.values(filter.value).some((v) => v !== ''))
  /** 是否处于「按根订单查」模式（该模式走专用端点，不分页） */
  const orderScopeId = computed<number | null>(() => {
    const n = Number(filter.value.rootOrderId)
    return Number.isFinite(n) && n > 0 ? n : null
  })
  const totalAllocations = computed(() => summary.value?.totalAllocations ?? 0)

  /* ===== actions ===== */
  async function loadVersions(): Promise<void> {
    versionsLoading.value = true
    try {
      versions.value = await scheduleApi.getVersions(60)
      // 默认取第一条（`/api/schedule/versions` 已按时间倒序，最新在前）
      planVersionId.value = versions.value[0]?.id ?? null
    } catch (err) {
      handleError(err)
    } finally {
      versionsLoading.value = false
    }
  }

  async function loadSummary(): Promise<void> {
    const id = planVersionId.value
    if (id == null) {
      summary.value = null
      return
    }
    summaryLoading.value = true
    try {
      summary.value = await peggingTraceApi.summary(id)
    } catch (err) {
      // 汇总失败不阻断列表：分布条整块给空态，列表照常查
      summary.value = null
      handleError(err)
    } finally {
      summaryLoading.value = false
    }
  }

  async function loadList(resetPage = false): Promise<void> {
    const id = planVersionId.value
    if (id == null) {
      list.value = []
      hasMore.value = false
      return
    }
    if (resetPage) pageIndex.value = 1
    loading.value = true
    error.value = null
    try {
      // 按根订单查：走专用端点（后端按 OrderId 精确过滤，且不分页）
      if (orderScopeId.value != null) {
        list.value = await peggingTraceApi.listByOrder(orderScopeId.value, id)
        hasMore.value = false
        pageIndex.value = 1
        return
      }
      const take = Math.min(pageSize.value, PAGE_SIZE_CAP)
      const rows = await peggingTraceApi.list({
        planVersionId: id,
        materialCode: filter.value.materialCode.trim() || undefined,
        supplyType: filter.value.supplyType || undefined,
        commitmentStatus: filter.value.commitmentStatus || undefined,
        supplyDocumentNo: filter.value.supplyDocumentNo.trim() || undefined,
        skip: (pageIndex.value - 1) * take,
        take
      })
      list.value = rows
      hasMore.value = rows.length === take
    } catch (err) {
      list.value = []
      hasMore.value = false
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  /** 切版本：清筛选、回第 1 页、重取汇总与列表 */
  async function setPlanVersion(id: number): Promise<void> {
    if (!Number.isFinite(id) || id <= 0) return
    planVersionId.value = id
    filter.value = {
      materialCode: '',
      supplyType: '',
      commitmentStatus: '',
      rootOrderId: '',
      supplyDocumentNo: ''
    }
    pageIndex.value = 1
    await Promise.all([loadSummary(), loadList()])
  }

  async function applyFilter(): Promise<void> {
    await loadList(true)
  }

  async function resetFilter(): Promise<void> {
    filter.value = {
      materialCode: '',
      supplyType: '',
      commitmentStatus: '',
      rootOrderId: '',
      supplyDocumentNo: ''
    }
    await loadList(true)
  }

  async function nextPage(): Promise<void> {
    if (!hasMore.value) return
    pageIndex.value += 1
    await loadList()
  }

  async function prevPage(): Promise<void> {
    if (pageIndex.value <= 1) return
    pageIndex.value -= 1
    await loadList()
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
    versions.value = []
    planVersionId.value = null
    summary.value = null
    list.value = []
    error.value = null
    pageIndex.value = 1
    hasMore.value = false
  }

  return {
    // state
    versions,
    planVersionId,
    versionsLoading,
    summary,
    summaryLoading,
    list,
    loading,
    error,
    filter,
    pageIndex,
    pageSize,
    hasMore,
    // getters
    currentVersion,
    hasFilter,
    orderScopeId,
    totalAllocations,
    // actions
    loadVersions,
    loadSummary,
    loadList,
    setPlanVersion,
    applyFilter,
    resetFilter,
    nextPage,
    prevPage,
    reset
  }
})
