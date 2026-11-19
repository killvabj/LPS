/**
 * APS V1 4号位 — Supply/Pegging 追溯 Pinia store
 *
 * 数据面（全部来自 5号位 `PeggingTraceController`，无 mock、无前端二次聚合）：
 *  - `versions`  ← GET /api/schedule/versions（计划版本下拉）
 *  - `summary`   ← GET /api/pegging-trace/summary（总分配数 + 供给类型/承诺状态分布）
 *  - `list`      ← GET /api/pegging-trace（服务端分页，ORDER BY AllocationSequence）
 *  - `count`     ← GET /api/pegging-trace/count（筛选后总条数，2026-11-14 落码）
 *
 * ⚠️ 列表端点仍**不返回 total**（bare 数组）⇒ 分页底座仍是 hasMore
 *   （`hasMore = 本页返回条数 === pageSize`）；**总条数**另走 count 端点。
 *
 * ---- 2026-11-14 总条数/总页数口径（换源）----
 *
 * 5号位落了 count 端点（形状 B）并把 summary **收进数据范围**（R2.4 裁定）：
 *  - count 与列表**同一条 WHERE 文本单源**（六个筛选谓词 + 数据范围谓词）
 *    ⇒ ① 筛选后也有真值；② 受限账号读到的是**自己可见**的条数。
 *  - dev 2026-11-14 实测（计划版本 999512）：admin count 73,375 ＝ summary 73,375；
 *    pmc count 17,181 ＝ pmc summary 17,181；pmc count + supplyType=WIP = 4,469。
 *  ⇒ 本模块不再有「翻到末页才确知可见条数」那套口径（`reachableTotal` / `reachablePages`
 *    已撤）：count 给的是真值，翻不翻到末页都一样准。
 *  - count 取不到（端点不可用）⇒ `filteredTotal = null` ⇒ 页面只显示「本页 N 笔」，**不编总数**。
 *
 * ---- 2026-11-13 工厂视角改版 ----
 *
 * 页面改成两种「读法」，由「有没有指定订单」自动切换：
 *  ① **分配明细**（没指定订单）：一行 = 一笔分配（原始事实），分页浏览全量分配。
 *  ② **订单齐套**（指定了订单号 / 订单 ID）：一行 = 一种物料，给出「需求 / 已分配 / 缺口」。
 *
 * ② 为什么能算缺口：同一 (订单, 物料) 的多笔分配，`demandQty` 在组内**恒定**（dev 实测
 *    3000 行、64 个多笔组，组内需求量不一致 0 例）⇒ 需求量取组内值、已分配取组内求和，
 *    缺口 = 需求 − 已分配。（dev 实测缺口真实存在：跨厂订单 30.8%、在制 16.0%、在途 10.4%
 *    的物料组有缺口。）
 *
 * ② 为什么取数是完整的：订单口径（订单号模糊 / 根订单 ID 精确）都走列表端点一次 take=500，
 *    与其它筛选项同一条 WHERE ⇒ 一次请求取全该订单的行，且不分页，组不会被页边界切碎
 *    ⇒ 缺口可信。取到 500 行时（后端上限）页面提示「可能未取全」，不假装完整；
 *    **11-14 起由 count 精确判定**：真总数 > 取回行数 ⇒ 确实被截断（照样提示）；
 *    真总数 ≤ 500 ⇒ 已取全，缺口可信、不再提示。
 *
 * ① 为什么不算缺口：分页取的是**部分**分配行，跨页的订单会被切碎，缺口会算错 ⇒ 不给。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  peggingTraceApi,
  scheduleApi,
  type PeggingTraceDto,
  type PeggingTraceQuery,
  type PeggingTraceSummaryDto,
  type PlanVersionSummaryDto
} from '@/api/aps-v1'
import { pickDefaultVersion } from './order'

/** 单页条数上限：后端 `take > 500` 会静默回落 100 ⇒ 前端自夹 200 作双保险 */
const PAGE_SIZE_CAP = 200
const DEFAULT_PAGE_SIZE = 50
/** 订单口径一次取满的行数（后端 take 上限 500） */
const ORDER_SCOPE_TAKE = 500
/**
 * 跨厂供给的供给类型码（后端 `SupplySourceType.INTER_FACTORY_ORDER`）。
 * ⚠️ 跨厂判定**只能**看供给类型，不能比「需求工厂码 ≠ 供给工厂码」：
 *   后端落库时两列写的是同一个值（`PeggingOrchestrator` 里
 *   `DemandFactoryCode` 与 `SupplyFactoryCode` 都取 `a.FactoryCode`），
 *   而跨厂出荷指示（SH）入池时取的又是**目标厂**（= 需求厂）⇒ 两码恒等，
 *   dev 实测 PV 999512 前 500 行「两码不同」0 例 ⇒ 旧判定是死逻辑。
 */
const CROSS_SUPPLY_TYPE = 'INTER_FACTORY_ORDER'

/** 按 (订单, 物料) 归并后的一行，供「订单齐套」视图用 */
export interface PeggingGroupRow {
  key: string
  /** 根订单号（后端未回填订单号时回落订单 ID） */
  orderNo: string
  orderId: number | null
  materialCode: string
  /** 需求量（组内取最大；组内恒定故等于该物料在本订单的需求量） */
  demandQty: number
  /** 已分配量（组内各笔求和） */
  allocatedQty: number
  /** 缺口 = max(0, 需求量 − 已分配量) */
  shortage: number
  /** 供给来源及笔数 */
  supplyTypes: { supplyType: string; count: number }[]
  /** 来源单据号（去重后原样列出） */
  documentNos: string[]
  demandFactoryCode: string
  supplyFactoryCodes: string[]
  /** 供给工厂中**与需求工厂不同**的部分（后端两厂码同源时恒为空） */
  otherSupplyFactoryCodes: string[]
  /** 跨厂：组内有跨厂订单供给，或供给工厂码 ≠ 需求工厂码 */
  crossFactory: boolean
  /** 已知可用时间（在途/在制才有） */
  knownAvailableTime: string | null
  /** 该物料的各笔分配（抽屉里逐笔看） */
  allocations: PeggingTraceDto[]
}

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
  /**
   * 当前筛选条件下的总条数（`GET /api/pegging-trace/count`，5号位 2026-11-14 落码）。
   * 与列表**同一条 WHERE 文本**（六个筛选谓词 + 数据范围谓词）⇒ 筛选后也拿得到真值，
   * 且对受限账号也是真的。`null` = 计数没取到（端点不可用/失败）⇒ 页面只显示「本页 N 笔」，不编数。
   */
  const filteredTotal = ref<number | null>(null)
  /** 列表请求序号：并发时只认最后一次请求的结果 */
  let listReqSeq = 0

  const filter = ref({
    /** 订单号（模糊；后端 `orderNo` LIKE 命中 RootOrderNo / CurrentOrderNo） */
    orderNo: '',
    /** 根订单 ID（数字，精确；后端 `RootOrderId = @RootOrderId`） */
    rootOrderId: '',
    /** 物料编码（模糊） */
    materialCode: '',
    /** 来源单据号（模糊） */
    supplyDocumentNo: '',
    /** 供给来源（候选取 summary 的真实分布） */
    supplyType: '',
    /** 承诺状态（候选取 summary 的真实分布） */
    commitmentStatus: ''
  })

  const pageIndex = ref(1)
  const pageSize = ref(DEFAULT_PAGE_SIZE)
  const hasMore = ref(false)
  /** 订单口径取数是否被后端上限截断（取满 500 行） */
  const orderScopeTruncated = ref(false)

  /* ===== getters ===== */
  const currentVersion = computed(
    () => versions.value.find((v) => v.id === planVersionId.value) ?? null
  )
  /** 是否处于筛选态 */
  const hasFilter = computed(() => Object.values(filter.value).some((v) => v !== ''))
  /** 是否处于「按订单查」模式（该模式一次取全量，不分页） */
  const isOrderScope = computed(
    () => filter.value.orderNo.trim() !== '' || orderScopeId.value != null
  )
  /** 按订单 ID 查时用的内部 ID（数字） */
  const orderScopeId = computed<number | null>(() => {
    const n = Number(filter.value.rootOrderId)
    return Number.isFinite(n) && n > 0 ? n : null
  })
  /**
   * 分配总笔数（Summary 端点聚合）
   *
   * ⚠️ **null = 未取到**（Summary 失败/未加载），**不是 0**：
   *   §0.2 ②「NULL=未知，不得转 0」——同页下方另有「汇总暂不可用」的提示，
   *   若这里还显「共 0 笔分配」＝ 同时说「取不到」和「确定为零」，口径打架。
   */
  const totalAllocations = computed<number | null>(() => summary.value?.totalAllocations ?? null)

  /**
   * 明细视图的「总页数」——**算得准才显示**。
   *
   * 2026-11-14 起口径变了：5号位落了 `GET /api/pegging-trace/count`（形状 B），
   * 其 WHERE 与列表**同源单源**（`TraceWhereCore` 六个筛选谓词 + `TraceScopePredicate` 数据范围）
   * ⇒ ① 筛选后也能拿到真值；② 受限账号拿到的就是**自己可见**的条数，不再是全厂数。
   * dev 2026-11-14 实测（计划版本 999512）：
   *   admin count 73,375 ＝ summary 73,375；pmc count 17,181 ＝ pmc summary 17,181
   *   （pmc count + supplyType=WIP = 4,469 ＝ pmc 分布里 WIP 计数）。
   *
   * 拿不到计数（端点不可用）⇒ 返回 null ⇒ 页面只说「本页 N 笔」，不编总数。
   */
  const totalPages = computed<number | null>(() => {
    if (isOrderScope.value) return null
    const total = filteredTotal.value
    if (total == null || total <= 0) return null
    return Math.ceil(total / Math.min(pageSize.value, PAGE_SIZE_CAP))
  })
  /** 供给来源候选（来自 summary 的真实分布，带条数） */
  const supplyTypeOptions = computed(() => summary.value?.supplyTypeCounts ?? [])
  /** 承诺状态候选（来自 summary 的真实分布） */
  const commitmentOptions = computed(() => summary.value?.commitmentStatusCounts ?? [])

  /**
   * 按 (订单, 物料) 归并的齐套行。
   * 只在订单口径下使用 —— 那时取数是完整的，缺口才算得对。
   */
  const groupRows = computed<PeggingGroupRow[]>(() => {
    const map = new Map<string, PeggingGroupRow>()
    for (const row of list.value) {
      const orderNo = row.rootOrderNo ?? (row.rootOrderId != null ? String(row.rootOrderId) : '—')
      const key = `${orderNo}|${row.materialCode}`
      let g = map.get(key)
      if (!g) {
        g = {
          key,
          orderNo,
          orderId: row.rootOrderId ?? null,
          materialCode: row.materialCode,
          demandQty: 0,
          allocatedQty: 0,
          shortage: 0,
          supplyTypes: [],
          documentNos: [],
          demandFactoryCode: row.demandFactoryCode ?? '—',
          supplyFactoryCodes: [],
          otherSupplyFactoryCodes: [],
          crossFactory: false,
          knownAvailableTime: null,
          allocations: []
        }
        map.set(key, g)
      }
      // 组内需求量实测恒定，取最大只会更保守（不低报需求）
      g.demandQty = Math.max(g.demandQty, Number(row.demandQty ?? 0))
      g.allocatedQty += Number(row.allocatedQty ?? 0)
      const t = g.supplyTypes.find((s) => s.supplyType === row.supplyType)
      if (t) t.count += 1
      else g.supplyTypes.push({ supplyType: row.supplyType, count: 1 })
      if (row.supplyDocumentNo && !g.documentNos.includes(row.supplyDocumentNo)) {
        g.documentNos.push(row.supplyDocumentNo)
      }
      if (row.supplyFactoryCode && !g.supplyFactoryCodes.includes(row.supplyFactoryCode)) {
        g.supplyFactoryCodes.push(row.supplyFactoryCode)
      }
      if (row.knownAvailableTime) {
        if (!g.knownAvailableTime || row.knownAvailableTime < g.knownAvailableTime) {
          g.knownAvailableTime = row.knownAvailableTime
        }
      }
      g.allocations.push(row)
    }
    for (const g of map.values()) {
      g.shortage = Math.max(0, Number((g.demandQty - g.allocatedQty).toFixed(6)))
      // 跨厂判定：供给类型里有跨厂订单 **或** 供给厂码真的不同于需求厂码。
      // （后者留作后端修正后的兼容位；当前两列同源，恒为空。）
      g.otherSupplyFactoryCodes = g.supplyFactoryCodes.filter((f) => f !== g.demandFactoryCode)
      g.crossFactory =
        g.supplyTypes.some((s) => s.supplyType === CROSS_SUPPLY_TYPE) ||
        g.otherSupplyFactoryCodes.length > 0
    }
    return [...map.values()]
  })

  /* ===== actions ===== */
  async function loadVersions(): Promise<void> {
    versionsLoading.value = true
    try {
      versions.value = await scheduleApi.getVersions(100)
      // 默认取「域内当前生效版本」（复用订单页同一套挑选规则 = ACTIVE 锚表）：
      // 端点按时间倒序返回，最新一条常是 FAILED 的测试版 → 用户一进页面看到空版本，像系统坏了。
      // take 必须是 100：dev 实测最新 30 个版本里没有一条 ACTIVE（矩阵 §0.1：禁 MAX(PV)/MAX(Run) 选版）。
      planVersionId.value = pickDefaultVersion(versions.value)?.id ?? null
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

  /** 当前筛选条件（**不含翻页参数**）——列表端点与计数端点共用同一组参数 ⇒ 两者口径天然一致 */
  function currentFilterParams(id: number): PeggingTraceQuery {
    return {
      planVersionId: id,
      materialCode: filter.value.materialCode.trim() || undefined,
      supplyType: filter.value.supplyType || undefined,
      commitmentStatus: filter.value.commitmentStatus || undefined,
      supplyDocumentNo: filter.value.supplyDocumentNo.trim() || undefined
    }
  }

  async function loadList(resetPage = false): Promise<void> {
    const id = planVersionId.value
    if (id == null) {
      listReqSeq += 1
      list.value = []
      hasMore.value = false
      filteredTotal.value = null
      return
    }
    if (resetPage) pageIndex.value = 1
    loading.value = true
    error.value = null
    orderScopeTruncated.value = false
    // 请求序号：**只认最后一次请求的结果**。
    // 否则「刚打开页面还没加载完就点了来源卡片」时，先发的未筛选请求会后到，
    // 把后发的筛选结果盖掉 ⇒ 用户看到的是没筛选的行（2026-11-14 实看实测到）。
    const seq = ++listReqSeq
    try {
      const orderNo = filter.value.orderNo.trim()
      const orderId = orderScopeId.value
      const base = currentFilterParams(id)
      // 计数（5号位 2026-11-14 落码）：与列表同一条 WHERE ⇒ 筛选后的总条数拿得到真值。
      // 计数失败**不阻断列表**：拿不到就显示「本页 N 笔（总条数暂不可用）」，不编数。
      const countPromise = peggingTraceApi
        .count({ ...base, orderNo: orderNo || undefined, rootOrderId: orderId ?? undefined })
        .catch(() => null)
      // ① 订单口径（订单号模糊 / 根订单 ID 精确）：一次取满（后端上限 500），取满即提示可能未取全
      if (orderNo || orderId != null) {
        const [rows, total] = await Promise.all([
          peggingTraceApi.list({
            ...base,
            orderNo: orderNo || undefined,
            rootOrderId: orderId ?? undefined,
            skip: 0,
            take: ORDER_SCOPE_TAKE
          }),
          countPromise
        ])
        if (seq !== listReqSeq) return
        list.value = rows
        hasMore.value = false
        pageIndex.value = 1
        filteredTotal.value = total
        // 截断判定：有计数时按真值判（真总数 > 取回行数 ⇒ 真被截断）；没计数时退回「取满 500 条」
        orderScopeTruncated.value =
          total != null ? total > rows.length : rows.length >= ORDER_SCOPE_TAKE
        return
      }
      // ② 明细浏览：服务端分页
      const take = Math.min(pageSize.value, PAGE_SIZE_CAP)
      const fetchPage = (skip: number) => peggingTraceApi.list({ ...base, skip, take })
      const [rows, total] = await Promise.all([
        fetchPage((pageIndex.value - 1) * take),
        countPromise
      ])
      // 末页刚好满页时（总条数是每页条数的整数倍），再点「下一页」翻出来的是**空页** ——
      // 空页会被读成「没有符合筛选条件的分配记录」（不实情）⇒ 自动退回上一页，并标记已到末页。
      if (rows.length === 0 && pageIndex.value > 1) {
        pageIndex.value -= 1
        const retry = await fetchPage((pageIndex.value - 1) * take)
        if (seq !== listReqSeq) return
        list.value = retry
        hasMore.value = false
        filteredTotal.value = total
        return
      }
      if (seq !== listReqSeq) return
      list.value = rows
      hasMore.value = rows.length === take
      filteredTotal.value = total
    } catch (err) {
      if (seq !== listReqSeq) return
      list.value = []
      hasMore.value = false
      filteredTotal.value = null
      handleError(err)
    } finally {
      // 过期请求不改 loading：否则旧请求先结束会把 loading 提前清掉，
      // 新请求还在路上时页面已经显示「加载完成」。
      if (seq === listReqSeq) loading.value = false
    }
  }

  /** 切版本：清筛选、回第 1 页、重取汇总与列表 */
  async function setPlanVersion(id: number): Promise<void> {
    if (!Number.isFinite(id) || id <= 0) return
    planVersionId.value = id
    resetFilterState()
    pageIndex.value = 1
    await Promise.all([loadSummary(), loadList()])
  }

  function resetFilterState(): void {
    filter.value = {
      orderNo: '',
      rootOrderId: '',
      materialCode: '',
      supplyDocumentNo: '',
      supplyType: '',
      commitmentStatus: ''
    }
  }

  /** 筛选生效：回第 1 页重取 */
  async function applyFilter(): Promise<void> {
    await loadList(true)
  }

  /** 点供给来源卡片：同一个来源再点一次 = 取消该筛选 */
  async function toggleSupplyType(supplyType: string): Promise<void> {
    filter.value.supplyType = filter.value.supplyType === supplyType ? '' : supplyType
    await loadList(true)
  }

  async function resetFilter(): Promise<void> {
    resetFilterState()
    await loadList(true)
  }

  async function setPageSize(size: number): Promise<void> {
    pageSize.value = size
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
    listReqSeq += 1
    versions.value = []
    planVersionId.value = null
    summary.value = null
    list.value = []
    error.value = null
    pageIndex.value = 1
    hasMore.value = false
    filteredTotal.value = null
    orderScopeTruncated.value = false
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
    filteredTotal,
    orderScopeTruncated,
    // getters
    currentVersion,
    hasFilter,
    isOrderScope,
    orderScopeId,
    totalAllocations,
    totalPages,
    supplyTypeOptions,
    commitmentOptions,
    groupRows,
    // actions
    loadVersions,
    loadSummary,
    loadList,
    setPlanVersion,
    applyFilter,
    toggleSupplyType,
    resetFilter,
    setPageSize,
    nextPage,
    prevPage,
    reset
  }
})
