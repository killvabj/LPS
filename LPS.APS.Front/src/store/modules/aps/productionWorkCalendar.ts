/**
 * APS V1 4号位 — Production Work Calendar（工厂/部门上级工作日历）Pinia store
 *
 * 依据：
 *  - 冻结文档 v1.4《Resource Calendar资源日历能力补充冻结方案 v1.4（2026-10-12 三级继承最小增量候选）》
 *    §三/§九 三级继承 + 三态（MISSING/WORK/OFF）
 *  - 5号位 2026-10-13《ProductionWorkCalendar三级日历维护接口文档 — 给4号位》
 *
 * 治理：
 *  - 条款=G1 候选；G4 公告前本模块供联调/备料
 *  - 5号位 业务 Owner（数据/维护接口）；2号位 表模型/解析 Owner；4号位 页面
 *  - 本 store 走 PageResult 分页（字段 `page`），与 manual-capacity / ManualETA / business-fact-issues 同款
 *
 * 权限码：
 *  - view     aps.resource_calendar.view（5号位 函 §〇.2 明确：暂复用日历域 3 码；G4 前不新增）
 *  - edit     aps.resource_calendar.edit
 *  - delete   aps.resource_calendar.delete（仅未生效行物理删除）
 *
 * 三态派生（v1.4 §2）：
 *  - WORK/OFF 行存于 list；MISSING = 该 (scopeType, factoryId, productionDepartmentId, workDate/weekdayIso) 无行
 *  - 列表级不主动造 MISSING 行（v1.4 §2 + 5号位 函 §七：MISSING 不可造）
 *  - 资源级合并时由 1号位 ResourceCalendarResolver 处理
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  productionWorkCalendarApi,
  RESOURCE_CALENDAR_PERMISSIONS,
  type CalendarCloseOpenRequest,
  type ProductionWorkCalendarDto,
  type ProductionWorkCalendarPageResult,
  type ProductionWorkCalendarQuery,
  type WeeklyCalendarUpsertRequest,
  type WorkCalendarExceptionRequest
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

export const useProductionWorkCalendarStore = defineStore('aps.productionWorkCalendar', () => {
  /* ===== auth ===== */
  const apsAuth = useApsAuthStore()

  /* ===== state ===== */
  /** 列表默认排序：最新创建的排在最上面（5号位 2026-10-14 回执 R4）
   *  - 后端不传 sortBy 时走原 5 字段固定序，新行会落在中间——与「新增要立刻看到」的诉求不符
   *  - 5 个写 action 成功后均走 load({}, { resetPage: true })，故默认值必须在 load() 内兜底 */
  const DEFAULT_SORT: Partial<ProductionWorkCalendarQuery> = {
    sortBy: 'CreatedAt',
    sortDir: 'DESC'
  }

  const list = ref<ProductionWorkCalendarDto[]>([])
  const total = ref(0)
  const page = ref(1)
  const pageSize = ref(20)
  const query = ref<ProductionWorkCalendarQuery>(DEFAULT_SORT)
  const loading = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)

  /* ===== getters：权限（5号位 函 §〇.2：复用 aps.resource_calendar.* 三码）===== */
  const canView = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.view))
  const canEdit = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.edit))
  const canDelete = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.delete))

  /* ===== getters：派生数据 ===== */

  /** 是否还有更多页（v1.4 函 §〇.3：pageSize 默认 20、上限 200） */
  const hasMore = computed(() => page.value * pageSize.value < total.value)

  /** 按 (scopeType, factoryId, productionDepartmentId, weekdayIso/workDate) 索引
   *  - 用于"是否已存在行"快速判断（弹窗提交前的乐观查重） */
  const rowKeySet = computed(() => {
    const set = new Set<string>()
    for (const r of list.value) {
      set.add(rowKey(r))
    }
    return set
  })

  /* ===== actions ===== */

  /** 查询（带分页 + 后端回传覆盖 @see taskSplitRuleConfig.ts:111-119 范式） */
  async function load(
    nextQuery?: ProductionWorkCalendarQuery,
    options: { resetPage?: boolean; nextPageIndex?: number; nextPageSize?: number } = {}
  ): Promise<void> {
    if (nextQuery) query.value = { ...DEFAULT_SORT, ...nextQuery }
    if (options.resetPage) page.value = 1
    if (options.nextPageIndex != null) page.value = options.nextPageIndex
    if (options.nextPageSize != null) pageSize.value = options.nextPageSize
    loading.value = true
    error.value = null
    try {
      const res: ProductionWorkCalendarPageResult<ProductionWorkCalendarDto> =
        await productionWorkCalendarApi.query({
          ...query.value,
          pageIndex: page.value,
          pageSize: pageSize.value
        })
      list.value = res.items
      total.value = res.total
      page.value = res.page
      pageSize.value = res.pageSize
    } catch (err) {
      handleError(err)
      list.value = []
      total.value = 0
    } finally {
      loading.value = false
    }
  }

  /** 周基准铺设（@see 5号位 函 §二）
   *  - 成功后会刷新当前页（依赖 effectiveFrom 触发同范围同星期换版） */
  async function upsertWeekly(req: WeeklyCalendarUpsertRequest): Promise<number | null> {
    if (!canEdit.value) {
      handleError(new ApiError('无 PWC 编辑权限（需 aps.resource_calendar.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const inserted = await productionWorkCalendarApi.upsertWeekly(req)
      await load({}, { resetPage: true })
      return inserted
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 日期例外铺设（@see 5号位 函 §三） */
  async function createException(req: WorkCalendarExceptionRequest): Promise<number | null> {
    if (!canEdit.value) {
      handleError(new ApiError('无 PWC 编辑权限（需 aps.resource_calendar.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const id = await productionWorkCalendarApi.createException(req)
      await load({}, { resetPage: true })
      return id
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 批量闭区间换版（@see 5号位 函 §四） */
  async function closeOpen(req: CalendarCloseOpenRequest): Promise<number | null> {
    if (!canEdit.value) {
      handleError(new ApiError('无 PWC 编辑权限（需 aps.resource_calendar.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const n = await productionWorkCalendarApi.closeOpen(req)
      await load({}, { resetPage: true })
      return n
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 单行闭区间（@see 5号位 函 §五行级） */
  async function closeRow(id: number, effectiveTo: string): Promise<boolean> {
    if (!canEdit.value) {
      handleError(new ApiError('无 PWC 编辑权限（需 aps.resource_calendar.edit）', 403))
      return false
    }
    saving.value = true
    error.value = null
    try {
      const ok = await productionWorkCalendarApi.closeRow(id, effectiveTo)
      await load({}, { resetPage: true })
      return ok
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  /** 物理删除（仅未生效行，@see 5号位 函 §五行级） */
  async function deleteRow(id: number): Promise<boolean> {
    if (!canDelete.value) {
      handleError(new ApiError('无 PWC 删除权限（需 aps.resource_calendar.delete）', 403))
      return false
    }
    saving.value = true
    error.value = null
    try {
      const ok = await productionWorkCalendarApi.deleteRow(id)
      await load({}, { resetPage: true })
      return ok
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  /* ===== 生命周期 ===== */
  function reset(): void {
    list.value = []
    total.value = 0
    page.value = 1
    pageSize.value = 20
    query.value = { ...DEFAULT_SORT }
    error.value = null
  }

  function clearError(): void {
    error.value = null
  }

  /* ===== helpers ===== */
  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value = `${err.message}${err.traceId ? `（traceId=${err.traceId}）` : ''}`
    } else if (err instanceof Error) {
      error.value = err.message
    } else {
      error.value = String(err)
    }
  }

  return {
    // state
    list,
    total,
    page,
    pageSize,
    query,
    loading,
    saving,
    error,
    // getters：权限
    canView,
    canEdit,
    canDelete,
    // getters：数据
    // ⚠️ 不提供 workCount/offCount/overrideCount：这三者只按**当前页** list 统计，与 total 不同口径，
    //   挂成 KPI 卡就是假数（@see feedback_no_fake_data_in_pages）。需要总量请走服务端聚合端点。
    hasMore,
    rowKeySet,
    // actions
    load,
    upsertWeekly,
    createException,
    closeOpen,
    closeRow,
    deleteRow,
    reset,
    clearError,
    handleError
  }
})

/** 行级唯一键（用于查重 / 缓存命中） */
export function rowKey(r: ProductionWorkCalendarDto): string {
  return [
    r.scopeType,
    r.factoryId,
    r.productionDepartmentId ?? '_',
    r.workDate ?? `wd-${r.weekdayIso ?? '_'}`,
    r.startAt ?? '_',
    r.endAt ?? '_'
  ].join('|')
}
