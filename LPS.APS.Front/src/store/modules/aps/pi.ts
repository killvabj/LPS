/**
 * APS V1 4号位 — PI 位置 Pinia store
 *
 * 2026-10-17 重做：本 store 原先的 KPI / 分布全是从「手写 8 条 PI」在前端算出来的
 * （`totalCount = list.length`、`mesStats` 由推断规则派生、9 类 `supplyTypeCounts` 前端自造），
 * 三个端点一个都没调对。5号位 2026-10-17 回执确认：供给明细整块下线、KPI 走 `/summary`、明细走列表端点。
 *
 * 现口径（每个数字都能追到一个真实端点）：
 *  - `summary`      ← GET /api/pi-position/summary（服务端聚合，全版本口径，非当前页）
 *  - `list`         ← GET /api/pi-position（服务端分页；端点不返 total，「共 N 条」只能用 summary.totalPositions）
 *  - `detailRows`   ← GET /api/pi-position/{piNo}（单 PI 全部位置行）
 *
 * 计划版本必填（5号位 2026-10-17 回执 §五）：由用户在页面上显式选择，
 * 默认取 `GET /api/schedule/versions` 的第一条（最新），不自动猜 ACTIVE。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { ApiError, piApi, scheduleApi } from '@/api/aps-v1'
import type {
  PiPositionDto,
  PiPositionQuery,
  PiPositionSummaryDto,
  PlanVersionSummaryDto
} from '@/api/aps-v1'

/** 单页条数上限：后端 `take` 上限 500（2026-10-17 起越界夹到 500，原来静默回落 100）⇒ 前端仍自夹作双保险 */
const PAGE_SIZE_CAP = 500
const DEFAULT_PAGE_SIZE = 100

export const usePiStore = defineStore('aps.pi', () => {
  /* ===== state ===== */
  const versions = ref<PlanVersionSummaryDto[]>([])
  const planVersionId = ref<number | null>(null)

  const summary = ref<PiPositionSummaryDto | null>(null)
  const list = ref<PiPositionDto[]>([])
  const detailRows = ref<PiPositionDto[]>([])
  const detailOpen = ref(false)
  const currentPiNo = ref<string | null>(null)

  /** 四个筛选条件全部对应后端 `PiPositionController.Query` 的真实查询参数 */
  const filter = ref({
    productionInstructionNo: '',
    materialCode: '',
    positionType: '',
    stageCode: ''
  })
  const pageIndex = ref(1)
  const pageSize = ref(DEFAULT_PAGE_SIZE)

  const versionsLoading = ref(false)
  const listLoading = ref(false)
  const detailLoading = ref(false)
  const error = ref<string | null>(null)
  const detailError = ref<string | null>(null)

  /* ===== getters ===== */
  const currentVersion = computed(
    () => versions.value.find((v) => v.id === planVersionId.value) ?? null
  )

  /** 「共 N 条」只能用服务端聚合口径 —— 列表端点返裸数组、不提供 total */
  const totalPositions = computed(() => summary.value?.totalPositions ?? 0)

  /** 无法定位（U16 判据 = positionType === 'UNLOCATED'，数据驱动非推断） */
  const unlocatedCount = computed(
    () => summary.value?.positionTypeCounts.find((t) => t.positionType === 'UNLOCATED')?.count ?? 0
  )
  const unlocatedQty = computed(
    () =>
      summary.value?.positionTypeCounts.find((t) => t.positionType === 'UNLOCATED')
        ?.totalQuantity ?? 0
  )

  /** 当前页带位置问题码的行数（**仅本页**，不冒充全版本口径） */
  const pageIssueRows = computed(() => list.value.filter((r) => r.issueCode != null).length)

  const hasFilter = computed(
    () =>
      filter.value.productionInstructionNo !== '' ||
      filter.value.materialCode !== '' ||
      filter.value.positionType !== '' ||
      filter.value.stageCode !== ''
  )

  /* ===== actions ===== */
  async function loadVersions(): Promise<void> {
    versionsLoading.value = true
    try {
      versions.value = await scheduleApi.getVersions(30)
      if (planVersionId.value == null) {
        planVersionId.value = versions.value[0]?.id ?? null
      }
    } catch (err) {
      handleError(err)
      versions.value = []
      planVersionId.value = null
    } finally {
      versionsLoading.value = false
    }
  }

  function setPlanVersion(id: number): void {
    if (planVersionId.value === id) return
    planVersionId.value = id
    resetFilter()
    void refresh()
  }

  async function loadSummary(): Promise<void> {
    const id = planVersionId.value
    if (id == null) {
      summary.value = null
      return
    }
    try {
      summary.value = await piApi.summary(id)
    } catch (err) {
      summary.value = null
      handleError(err)
    }
  }

  async function loadList(): Promise<void> {
    const id = planVersionId.value
    if (id == null) {
      list.value = []
      return
    }
    listLoading.value = true
    error.value = null
    try {
      const query: PiPositionQuery = {
        planVersionId: id,
        skip: (pageIndex.value - 1) * pageSize.value,
        take: Math.min(pageSize.value, PAGE_SIZE_CAP)
      }
      if (filter.value.productionInstructionNo)
        query.productionInstructionNo = filter.value.productionInstructionNo
      if (filter.value.materialCode) query.materialCode = filter.value.materialCode
      if (filter.value.positionType) query.positionType = filter.value.positionType
      if (filter.value.stageCode) query.stageCode = filter.value.stageCode
      list.value = await piApi.list(query)
    } catch (err) {
      list.value = []
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  async function refresh(): Promise<void> {
    await Promise.all([loadSummary(), loadList()])
  }

  async function openDetail(productionInstructionNo: string): Promise<void> {
    const id = planVersionId.value
    currentPiNo.value = productionInstructionNo
    detailOpen.value = true
    detailRows.value = []
    detailError.value = null
    if (id == null) return
    detailLoading.value = true
    try {
      detailRows.value = await piApi.getByPi(productionInstructionNo, id)
      if (detailRows.value.length === 0) detailError.value = '该生产指示在当前版本下没有位置记录'
    } catch (err) {
      detailRows.value = []
      // 后端 `PiPositionController.GetByProductionInstruction` 在 `result.Count == 0` 时返
      // 404「PI Position not found」—— **这是空态不是故障**。5号位 2026-10-19 全局把信封码
      // 收口成 HTTP 状态后（`200 + code 404` → 真 404），若这里一律报「加载失败」，
      // 会把「没有记录」说成「系统坏了」。两种响应形态都按 404 归到空态文案。
      detailError.value =
        err instanceof ApiError && err.code === 404
          ? '该生产指示在当前版本下没有位置记录'
          : '位置明细加载失败，请稍后重试'
    } finally {
      detailLoading.value = false
    }
  }

  function closeDetail(): void {
    detailOpen.value = false
    currentPiNo.value = null
    detailRows.value = []
    detailError.value = null
  }

  /** 改任一筛选条件都回到第 1 页 —— 否则深页 + 新筛选会得到空列表且看不出原因 */
  function setFilter(patch: Partial<typeof filter.value>): void {
    filter.value = { ...filter.value, ...patch }
    pageIndex.value = 1
  }

  function resetFilter(): void {
    filter.value = {
      productionInstructionNo: '',
      materialCode: '',
      positionType: '',
      stageCode: ''
    }
    pageIndex.value = 1
  }

  function setPage(p: number, size?: number): void {
    if (size != null) pageSize.value = Math.min(size, PAGE_SIZE_CAP)
    pageIndex.value = p
    void loadList()
  }

  function handleError(err: unknown): void {
    error.value =
      err instanceof ApiError
        ? `[${err.code}] ${err.message}`
        : ((err as Error)?.message ?? '未知错误')
  }

  return {
    // state
    versions,
    planVersionId,
    summary,
    list,
    detailRows,
    detailOpen,
    currentPiNo,
    filter,
    pageIndex,
    pageSize,
    versionsLoading,
    listLoading,
    detailLoading,
    error,
    detailError,
    // getters
    currentVersion,
    totalPositions,
    unlocatedCount,
    unlocatedQty,
    pageIssueRows,
    hasFilter,
    // actions
    loadVersions,
    setPlanVersion,
    loadSummary,
    loadList,
    refresh,
    openDetail,
    closeDetail,
    setFilter,
    resetFilter,
    setPage,
    handleError
  }
})
