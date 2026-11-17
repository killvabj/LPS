/**
 * APS V1 4号位 — 人工到货时间（Manual ETA）Pinia store（页面 8）
 *
 * @see 4号位文档第 11 节 / 审核报告 P1-7
 *
 * 状态：
 *  - list：ManualEtaDto[]
 *  - currentDetail：当前 Drawer 编辑对象（新增时为 null）
 *  - loading / saving / error：UI 态
 *  - lastFilter：最近一次查询的筛选条件
 *  - counts：指标条计数（**后端真实 COUNT**，与列表同口径）
 *  - poNoOptions / materialFilterOptions：筛选候选（来自**全量扫描**，非当前页）
 *
 * 4号位不计算 Effective ETA / AvailableTime
 *
 * ❌ 2026-09-15 撤销 DepartmentCode 维度（@see 4号位-2026-09-13-裁定回退清单.md）：
 *  - 9月13日 `未命名的Markdown文件.md` 实际是 0号位 出的业务裁决（程序有效）
 *  - DepartmentCode 整条撤销：store getter distinctDepartmentCodes 删除
 *  - 当前状态：与 9月13日 撤销状态一致
 *
 * 11-12 工厂视角改版（指标口径重做）
 *  - 旧口径：store 端「翻页累加」近似 totalActive / totalCancelled
 *    ⇒ 问题一：只累加**访问过的页**，没翻到的页数永远算不进来；
 *    ⇒ 问题二：翻回旧页不重复计，但**中途筛选变更**靠重置掩盖，仍与后端 total 对不上；
 *    ⇒ 问题三：三个数字（total / totalActive / totalCancelled）互相不闭合，
 *      用户看到的「总记录 970 / 启用 0 / 已取消 0」这类组合无法解释。
 *  - 新口径：指标条 5 个数字全部来自后端 COUNT（同一组筛选 + 同一 WHERE 语义）：
 *      全部 / 生效中 / 已取消 / 已过期 / 7 天内到期
 *
 * 11-13 接 5号位 2026-11-12 复函（R1～R5 全部落码）：
 *  - 指标条：1 个 `/summary` 请求给全五个数（旧口径是 1 列表 + 4 个 countOnly = 5 个请求）
 *    summary 恒跨两态统计 ⇒ 调用只传基础筛选，**不传** activeOnly / cancelledOnly
 *  - 「已取消」变可点分类（后端补了 `cancelledOnly`，与 activeOnly 互斥、cancelledOnly 优先）
 *  - 筛选候选改走 `/facets`（旧口径是拉 pageSize=200 全量扫描自己派生 ⇒ 超 200 条选不到）
 *  - 列表支持排序（`sortBy` 白名单 manualEta/updatedAt/poNo/materialCode；不传 = 后端原排序）
 *
 * 11-14 接 5号位 R6 时间桶参数（SVN r14208）
 *  - 「已过期」「7 天内到期」分类改传后端 `bucket=overdue|dueSoon`
 *  - 旧口径是前端用**本机墙钟**算日界（今天 00:00 / 今天+6 天 23:59:59）下发 etaAfter/etaBefore，
 *    与后端 GETDATE() 日界 + 比较符（严格小于 / 半开区间）不一致 ⇒ 边界行上「指标数」与「列表行数」差 1
 *  - 改传 bucket 后：日界与判定式都在后端一处 ⇒ 指标条五数与各分类列表行数逐位相等
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  manualEtaApi,
  type ManualEtaBucket,
  type ManualEtaCounts,
  type ManualEtaDto,
  type ManualEtaListFilter,
  type ManualEtaSortBy,
  type ManualEtaUpsertInput,
  type ManualEtaCancelInput
} from '@/api/aps-v1'

/** 基础筛选（不含状态 / 到货时间语义）——指标与列表共用 */
export interface ManualEtaBaseFilter {
  poNos?: string[]
  materialCodes?: string[]
  receivingWarehouses?: string[]
}

export const useManualEtaStore = defineStore('aps.manualEta', () => {
  // ===== state =====
  const list = ref<ManualEtaDto[]>([])
  const currentDetail = ref<ManualEtaDto | null>(null)
  /** 首次进页面即为 true ⇒ 首帧渲染「正在加载…」而不是空态
   *  （onMounted 里的 load() 在首帧之后才跑 ⇒ loading 初值 false 会闪一下「还没有记录」） */
  const loading = ref(true)
  const saving = ref(false)
  const error = ref<string | null>(null)
  /** 最近一次查询的筛选条件（分页/筛选 UI 还原用） */
  const lastFilter = ref<ManualEtaListFilter>({})
  /** 分页：当前页（1 基） */
  const pageIndex = ref(1)
  /** 分页：每页条数（默认 20，与 5号位 端点一致） */
  const pageSize = ref(20)
  /** 分页：后端返回的 total（满足筛选条件全量条数） */
  const total = ref(0)

  /** 指标条计数（后端真实 COUNT；与列表同一组筛选） */
  const counts = ref<ManualEtaCounts>({
    all: 0,
    active: 0,
    cancelled: 0,
    overdue: 0,
    dueSoon: 0
  })

  /** 筛选候选：采购订单号（来自后端 facets） */
  const poNoOptions = ref<string[]>([])
  /** 筛选候选：物料编码（来自后端 facets） */
  const materialFilterOptions = ref<string[]>([])
  /** 候选被后端截断（每维 TOP 500）⇒ 界面上如实提示，不假装候选齐全 */
  const facetsTruncated = ref(false)

  // ===== getters =====
  /** 当前页生效中行数（仅供调试；指标条用 counts.active） */
  const activeCount = computed(() => list.value.filter((x) => x.isActive).length)
  /** 当前页已取消行数（仅供调试；指标条用 counts.cancelled） */
  const cancelledCount = computed(() => list.value.filter((x) => !x.isActive).length)

  // ===== 内部工具 =====
  /** 把基础筛选 + 分类拼成后端查询条件
   *  - all：activeOnly=false（全部，含已取消）
   *  - active：activeOnly=true
   *  - cancelled：cancelledOnly=true（5号位 2026-11-12 补的参数）
   *  - overdue / dueSoon：activeOnly=true + bucket=overdue|dueSoon
   *    （11-14：改传后端时间桶参数——**日界由服务器 GETDATE() 说了算**，与 /summary 的
   *      两个桶计数同一段 WHERE 文本 ⇒ 指标条数字与列表行数必然相等。
   *      旧口径是前端用本机墙钟算「今天 00:00 / 今天+6 天 23:59:59」，
   *      与后端的日界/比较符（严格小于、半开区间）不一致 ⇒ 边界行上两数会差 1）
   *  - ⚠️ **分类点击态不得叠时间窗**（5号位 11-14 复函 Q2 写进契约）：指标条的桶数出自
   *    /summary（其筛选集与桶是两个轴，不叠列表时间窗）⇒ 叠了列表就会与指标条对不上。
   *    本页筛选栏本来就没有时间窗（唯一的日期控件是抽屉里的「到货时间」行内编辑器），
   *    静态探针 ME25 也会在 buildQuery 里再出现 etaAfter/etaBefore 时转红。
   */
  function buildQuery(
    base: ManualEtaBaseFilter,
    bucket: ManualEtaBucket,
    page?: { pageIndex: number; pageSize: number }
  ): ManualEtaListFilter {
    const f: ManualEtaListFilter = {
      poNos: base.poNos?.length ? [...base.poNos] : undefined,
      materialCodes: base.materialCodes?.length ? [...base.materialCodes] : undefined,
      receivingWarehouses: base.receivingWarehouses?.length
        ? [...base.receivingWarehouses]
        : undefined,
      // 11-14：列表回带 isMatched 标注（5号位 v2.0 R2 取乙；后端默认 false ⇒ 须显式开）
      //  true = 会参与排产；false = 未命中当前供货事实（静默不生效）；null = 未判定
      withMatched: true
    }
    if (page) {
      f.pageIndex = page.pageIndex
      f.pageSize = page.pageSize
    }
    switch (bucket) {
      case 'active':
        f.activeOnly = true
        break
      case 'cancelled':
        f.activeOnly = false
        f.cancelledOnly = true
        break
      case 'overdue':
        f.activeOnly = true
        f.bucket = 'overdue'
        break
      case 'dueSoon':
        f.activeOnly = true
        f.bucket = 'dueSoon'
        break
      default:
        f.activeOnly = false
        break
    }
    return f
  }

  // ===== actions =====
  /**
   * 加载列表 + 指标
   *  - 11-13：指标 5 项一次拿全（后端 `/summary`），请求数 5 → 2（列表 1 + summary 1）
   *  - summary 用**基础筛选**（采购单/物料/仓库），不掺当前分类的到货时间区间
   *    ⇒ 指标条恒是「当前筛选下的全局分布」，点分类不会把别的数字顶掉
   */
  async function load(
    base: ManualEtaBaseFilter,
    bucket: ManualEtaBucket = 'all',
    page?: { pageIndex: number; pageSize: number },
    sort?: { sortBy?: ManualEtaSortBy; sortDir?: 'asc' | 'desc' }
  ): Promise<void> {
    loading.value = true
    error.value = null
    const pi = page?.pageIndex ?? pageIndex.value
    const ps = page?.pageSize ?? pageSize.value
    try {
      const listQuery = buildQuery(base, bucket, { pageIndex: pi, pageSize: ps })
      if (sort?.sortBy) {
        listQuery.sortBy = sort.sortBy
        listQuery.sortDir = sort.sortDir ?? 'asc'
      }
      const [resp, sum] = await Promise.all([
        manualEtaApi.list(listQuery),
        manualEtaApi.summary({
          poNos: base.poNos?.length ? [...base.poNos] : undefined,
          materialCodes: base.materialCodes?.length ? [...base.materialCodes] : undefined,
          receivingWarehouses: base.receivingWarehouses?.length
            ? [...base.receivingWarehouses]
            : undefined
        })
      ])

      let listTotal = 0
      if (Array.isArray(resp)) {
        list.value = resp
        listTotal = resp.length
      } else {
        list.value = resp?.items ?? []
        listTotal = Number(resp?.total ?? 0)
        if (typeof resp?.page === 'number') pageIndex.value = resp.page
        if (typeof resp?.pageSize === 'number') pageSize.value = resp.pageSize
      }
      total.value = listTotal

      counts.value = {
        all: sum?.allCount ?? 0,
        active: sum?.activeCount ?? 0,
        cancelled: sum?.cancelledCount ?? 0,
        overdue: sum?.overdueCount ?? 0,
        dueSoon: sum?.dueSoonCount ?? 0
      }
      lastFilter.value = listQuery
    } catch (err) {
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  /**
   * 加载筛选候选（采购订单 / 物料）
   *  - 11-13：改走后端 facets（本表 distinct，每维 TOP 500 + truncated 标志）
   *    旧口径是「拉 pageSize=200 全量扫描自己派生」⇒ 超过 200 条的采购单在下拉里选不到
   *  - 候选**不受当前筛选影响**（否则选了某个 PO 就再也选不到别的 PO）
   */
  async function loadCandidates(): Promise<void> {
    try {
      const f = await manualEtaApi.facets()
      poNoOptions.value = [...(f?.poNos ?? [])].sort()
      materialFilterOptions.value = [...(f?.materialCodes ?? [])].sort()
      facetsTruncated.value = f?.truncated === true
    } catch {
      poNoOptions.value = []
      materialFilterOptions.value = []
      facetsTruncated.value = false
    }
  }

  /**
   * Upsert：新增或更新
   *  - 成功后用 result 局部回写（避免无谓的全量 reload）
   *  - 列表内查找用 4 字段复合主键（与后端 MERGE ON 对齐；避免同 poNo+lineNo 多物料 / 多仓库时找错行）
   *  - 列表写回用新数组引用（map / spread），不直接下标赋值 / unshift —— ElTable :data
   *    依赖引用变化触发重渲，原地变更会出现"新增后不显示，刷新后显示"的问题
   */
  async function upsert(input: ManualEtaUpsertInput): Promise<ManualEtaDto> {
    saving.value = true
    error.value = null
    try {
      const result = await manualEtaApi.upsert(input)
      const idx = list.value.findIndex((x) => sameBusinessKey(x, result))
      if (idx >= 0) {
        list.value = list.value.map((x, i) => (i === idx ? result : x))
      } else {
        list.value = [result, ...list.value]
      }
      currentDetail.value = result
      return result
    } catch (err) {
      handleError(err)
      throw err
    } finally {
      saving.value = false
    }
  }

  /**
   * 取消（isActive=false）
   *  - 软删除，保留记录
   *  - 列表内查找 / currentDetail 同步均用 4 字段复合主键（同 upsert）
   *  - 列表写回用新数组引用（map），不直接下标赋值（ElTable :data 重渲依赖，@see upsert）
   */
  async function cancel(input: ManualEtaCancelInput): Promise<ManualEtaDto> {
    saving.value = true
    error.value = null
    try {
      const result = await manualEtaApi.cancel(input)
      const idx = list.value.findIndex((x) => sameBusinessKey(x, result))
      if (idx >= 0) {
        list.value = list.value.map((x, i) => (i === idx ? result : x))
      }
      if (currentDetail.value && sameBusinessKey(currentDetail.value as ManualEtaDto, result)) {
        currentDetail.value = result
      }
      return result
    } catch (err) {
      handleError(err)
      throw err
    } finally {
      saving.value = false
    }
  }

  /** 业务键（与 5号位 `ProcurementManualEtaRepository.MERGE ON` 一致：4 字段复合主键）
   *  - @see lps/LPS.APS.BusinessRules/Repositories/ProcurementManualEtaRepository.cs:202-205
   *  - receivingWarehouse 可选字段，统一按空串处理（避免 undefined/null/'' 三态比较漏匹配）
   */
  function sameBusinessKey(a: ManualEtaDto, b: ManualEtaDto): boolean {
    return (
      a.poNo === b.poNo &&
      a.lineNo === b.lineNo &&
      a.materialId === b.materialId &&
      (a.receivingWarehouse ?? '') === (b.receivingWarehouse ?? '')
    )
  }

  function openDetail(dto: ManualEtaDto | null): void {
    currentDetail.value = dto
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
    list.value = []
    currentDetail.value = null
    error.value = null
    lastFilter.value = {}
    total.value = 0
    counts.value = { all: 0, active: 0, cancelled: 0, overdue: 0, dueSoon: 0 }
    poNoOptions.value = []
    materialFilterOptions.value = []
  }

  return {
    list,
    currentDetail,
    loading,
    saving,
    error,
    lastFilter,
    pageIndex,
    pageSize,
    total,
    counts,
    poNoOptions,
    materialFilterOptions,
    facetsTruncated,
    activeCount,
    cancelledCount,
    load,
    loadCandidates,
    upsert,
    cancel,
    openDetail,
    reset
  }
})
