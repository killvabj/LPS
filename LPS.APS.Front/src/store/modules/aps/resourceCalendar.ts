/**
 * APS V1 4号位 — 设备资源日历 + 人工能力槽 Pinia store
 *
 * 依据：
 *  - 冻结文档 v1.3《Resource Calendar 资源日历能力补充冻结方案 v1.3》§三/§五/§八/§九
 *  - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》
 *
 * 结构（单页 3 Tab）：
 *  - Tab1 设备资源日历：resources（资源主表 /api/resource） → 选中 resourceId → deviceWindowItems
 *  - Tab2 人工能力槽  ：slots（部门/工序过滤 + 含软删开关）
 *  - Tab3 人工槽日历  ：由 Tab2 选中行驱动 slotWindowItems
 *
 * 10-12 RC23：撤过渡实现，资源主表切正式端点 G1（ResourceController.cs:34 GET /api/resource）
 *  - 旧：listResourceCandidates 从 gantt.resources 派生 + closure 缓存；遗失未排资源
 *  - 新：listResources 直接查 dbo.Resource 镜像；含 hasCalendar（列表级判日历状态）
 *
 * 10-17：窗口列表改**服务端分页**（后端 10-14 已原地改 PageResult，前端当时未同步适配）
 *  - 每设备/每槽只留 `…WindowTotals[id]`（服务端 total）供「已配/未配」判定与进度总览
 *  - 已查证 = 该 id 的 total 已知；未查证 = total 为 undefined
 *    （v1.3 §九：无有效日历 = 不可用；G4 已闭环：后端 ResourceListItemDto.hasCalendar 列表级即可判定）
 *
 * 权限（按钮显隐，对齐后端 PermissionCodes.cs:82-96 六码）：
 *  - 设备：aps.resource_calendar.{view,edit,delete}
 *  - 人工：aps.manual_capacity.{view,edit,delete}
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  MANUAL_CAPACITY_PERMISSIONS,
  RESOURCE_CALENDAR_PERMISSIONS,
  resourceCalendarApi,
  type CalendarConfigStatus,
  type DepartmentCandidate,
  type ManualCapacitySlotDto,
  type ManualSlotCalendarBulkRequest,
  type ManualSlotCalendarDto,
  type ManualSlotQuery,
  type Resource,
  type ResourceCalendarBulkRequest,
  type ResourceCalendarEntryDto,
  type ResourceCalendarSingleRequest,
  type ResourceCalendarResolvePreviewDto,
  type ManualSlotCalendarSingleRequest
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

/** 窗口列表归属状态：未查证 / 已配 / 未配（按服务端 total 判定）
 *  resourceId 与 manualSlotId 共用（两者 id 空间独立，各自查各自的 totals 表） */
function statusOf(totals: Record<number, number>, key: number): CalendarConfigStatus {
  const total = totals[key]
  if (total === undefined) return 'UNKNOWN'
  return total > 0 ? 'CONFIGURED' : 'NOT_CONFIGURED'
}

export const useResourceCalendarStore = defineStore('aps.resourceCalendar', () => {
  /* ===== auth ===== */
  const apsAuth = useApsAuthStore()

  /* ===== Tab1：设备资源日历 ===== */
  // 10-12 RC23 收：资源主表走正式端点 GET /api/resource（@see resourceCalendarApi.listResources）
  //  - 旧过渡实现（listResourceCandidates）从 gantt.resources 派生 → 已撤
  //  - 旧 candidateNote / candidateIsFallback 标记源提示 → 已撤（不再有"过渡/兜底"概念）
  const resources = ref<Resource[]>([])
  const resourcesLoading = ref(false)
  const currentResourceId = ref<number | null>(null)
  /* 2026-10-17：设备窗口改**服务端分页**（后端 10-14 已原地改 PageResult）。
   *  - 旧实现缓存「该设备全量窗口」再前端切片 ⇒ 后端改 PageResult 后，
   *    不带分页参数只拿第 1 页 20 条，且拿到的是对象不是数组 ⇒ 列表整块不显示。
   *  - 现只留**当前页** + **每设备窗口总数**（总数供状态徽标/进度总览判断"有无窗口"）。 */
  const deviceWindowItems = ref<ResourceCalendarEntryDto[]>([])
  const deviceWindowTotal = ref<number>(0)
  const deviceWindowTotals = ref<Record<number, number>>({})
  /* 2026-10-17：三级合并解析预览（5号位 GET /api/resource-calendar/resolve-preview）
   *  - 只读；给页面一个"实际能不能排、这片是谁给的"的真话口
   *  - 只接受设备 id，无人工槽版本 */
  const resolvePreview = ref<ResourceCalendarResolvePreviewDto | null>(null)
  const resolveLoading = ref(false)

  /* ===== Tab2/3：人工能力槽 + 人工槽日历 ===== */
  // 10-12 RC22 收：人工槽 Tab2 接入分页（5号位 落地 PageResult，dev 15360 records）
  //  - 5 ref + 1 action 范式（@see taskSplitRuleConfig.ts:92-128）：list/total/page/pageSize/loading + loadSlots options
  const slots = ref<ManualCapacitySlotDto[]>([])
  const slotQuery = ref<ManualSlotQuery>({})
  const slotTotal = ref<number>(0)
  const slotPage = ref<number>(1)
  const slotPageSize = ref<number>(20)
  const slotsLoading = ref(false)
  const departmentCandidates = ref<DepartmentCandidate[]>([])
  const currentSlotId = ref<number | null>(null)
  /* 2026-10-17：人工槽窗口同设备侧，改服务端分页（后端 10-14 同批改 PageResult，并顺带补齐 from/to） */
  const slotWindowItems = ref<ManualSlotCalendarDto[]>([])
  const slotWindowTotal = ref<number>(0)
  const slotWindowPage = ref(1)
  const slotWindowPageSize = ref(20)
  const slotWindowTotals = ref<Record<number, number>>({})

  /* ===== 公共 UI 态 ===== */
  const loading = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)

  /* ===== getters：权限（按钮显隐） ===== */
  const canViewDevice = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.view))
  const canEditDevice = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.edit))
  const canDeleteDevice = computed(() => apsAuth.has(RESOURCE_CALENDAR_PERMISSIONS.delete))
  const canViewManual = computed(() => apsAuth.has(MANUAL_CAPACITY_PERMISSIONS.view))
  const canEditManual = computed(() => apsAuth.has(MANUAL_CAPACITY_PERMISSIONS.edit))
  const canDeleteManual = computed(() => apsAuth.has(MANUAL_CAPACITY_PERMISSIONS.delete))

  /* ===== getters：数据 ===== */

  /** 当前设备窗口的**当前页行**（分页由服务端给，前端不再切片） */
  const deviceWindowPage = ref(1)
  const deviceWindowPageSize = ref(20)

  /** 当前设备窗口是否已查证（该设备的总数已知即算查过） */
  const deviceWindowsLoaded = computed<boolean>(() => {
    const id = currentResourceId.value
    return id != null && deviceWindowTotals.value[id] !== undefined
  })

  /** 当前设备的 Calendar 状态（v1.3 §九）—— 按服务端 total 判有无窗口 */
  const deviceCalendarStatus = computed<CalendarConfigStatus>(() => {
    const id = currentResourceId.value
    if (id == null) return 'UNKNOWN'
    return statusOf(deviceWindowTotals.value, id)
  })

  /** 某人工槽的 Calendar 状态（列表行徽章用；UNKNOWN = 未查证）—— 按服务端 total 判有无窗口 */
  function slotCalendarStatus(manualSlotId: number): CalendarConfigStatus {
    return statusOf(slotWindowTotals.value, manualSlotId)
  }

  /** 当前人工槽对象（Tab3 头部展示） */
  const currentSlot = computed<ManualCapacitySlotDto | null>(() => {
    const id = currentSlotId.value
    if (id == null) return null
    return slots.value.find((s) => s.manualSlotId === id) ?? null
  })

  /* ===== actions：Tab1 ===== */

  /** 加载资源主表（GET /api/resource，10-12 RC23：撤过渡实现切正式端点） */
  async function loadResources(
    opts: { departmentId?: number; includeInactive?: boolean } = {}
  ): Promise<void> {
    resourcesLoading.value = true
    error.value = null
    try {
      resources.value = await resourceCalendarApi.listResources(opts)
    } catch (err) {
      handleError(err)
      resources.value = []
    } finally {
      resourcesLoading.value = false
    }
  }

  /** 加载生产部门候选（过渡数据源；G2 正式端点缺失） */
  async function loadDepartmentCandidates(): Promise<void> {
    try {
      departmentCandidates.value = await resourceCalendarApi.listDepartmentCandidates()
    } catch (err) {
      handleError(err)
      departmentCandidates.value = []
    }
  }

  /** 查询某设备的窗口（写入缓存；重复查询覆盖） */
  async function loadDeviceWindows(
    resourceId: number,
    options: { nextPageIndex?: number; nextPageSize?: number } = {}
  ): Promise<void> {
    if (!Number.isFinite(resourceId) || resourceId <= 0) {
      handleError(new ApiError('资源 Id 必须为正整数', 400))
      return
    }
    loading.value = true
    error.value = null
    if (currentResourceId.value !== resourceId) deviceWindowPage.value = 1
    currentResourceId.value = resourceId
    if (options.nextPageSize != null) deviceWindowPageSize.value = options.nextPageSize
    if (options.nextPageIndex != null) deviceWindowPage.value = options.nextPageIndex
    try {
      const res = await resourceCalendarApi.getWindows(resourceId, {
        pageIndex: deviceWindowPage.value,
        pageSize: deviceWindowPageSize.value
      })
      const items = res.items ?? []
      deviceWindowItems.value = items
      deviceWindowTotal.value = res.total
      deviceWindowPage.value = res.page
      deviceWindowPageSize.value = res.pageSize
      deviceWindowTotals.value = { ...deviceWindowTotals.value, [resourceId]: res.total }
      // 删掉本页最后一条后当前页会越界 → 停在前一页（total>0 兜底，避免全删时空转）
      if (!items.length && res.page > 1 && res.total > 0) {
        return loadDeviceWindows(resourceId, { nextPageIndex: res.page - 1 })
      }
    } catch (err) {
      handleError(err)
      deviceWindowItems.value = []
      deviceWindowTotal.value = 0
      const next = { ...deviceWindowTotals.value }
      delete next[resourceId]
      deviceWindowTotals.value = next
    } finally {
      loading.value = false
    }
  }

  /** 批量铺设备窗口（**覆盖式**：后端先删该区间旧窗再逐天 INSERT，幂等且无重叠） */
  async function createDeviceWindows(req: ResourceCalendarBulkRequest): Promise<number | null> {
    if (!canEditDevice.value) {
      handleError(new ApiError('无设备日历编辑权限（需 aps.resource_calendar.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const inserted = await resourceCalendarApi.bulkCreateWindows(req)
      await loadDeviceWindows(req.resourceId)
      return inserted
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 单条追加设备窗口（2026-10-14「放开一天多条」方向2；只插不删，重叠 409） */
  async function appendDeviceWindow(req: ResourceCalendarSingleRequest): Promise<number | null> {
    if (!canEditDevice.value) {
      handleError(new ApiError('无设备日历编辑权限（需 aps.resource_calendar.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const id = await resourceCalendarApi.appendWindow(req)
      await loadDeviceWindows(req.resourceId)
      return id
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 三级合并解析预览（设备，单资源）
   *  - 日期区间由调用方给（后端必填）；结果按 resourceId 取首项 */
  async function loadResolvePreview(
    resourceId: number,
    startDate: string,
    endDate: string
  ): Promise<void> {
    if (!Number.isFinite(resourceId) || resourceId <= 0) {
      handleError(new ApiError('资源 Id 必须为正整数', 400))
      return
    }
    resolveLoading.value = true
    error.value = null
    try {
      const rows = await resourceCalendarApi.getResolvePreview([resourceId], startDate, endDate)
      resolvePreview.value = rows.find((r) => r.resourceId === resourceId) ?? null
    } catch (err) {
      handleError(err)
      resolvePreview.value = null
    } finally {
      resolveLoading.value = false
    }
  }

  /** 物理删除设备窗口 */
  async function removeDeviceWindow(id: number, resourceId: number): Promise<boolean> {
    if (!canDeleteDevice.value) {
      handleError(new ApiError('无设备日历删除权限（需 aps.resource_calendar.delete）', 403))
      return false
    }
    saving.value = true
    error.value = null
    try {
      await resourceCalendarApi.deleteWindow(id)
      await loadDeviceWindows(resourceId)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  /* ===== actions：Tab2 / Tab3 ===== */

  /** 查询人工槽主档（默认不含软删，10-12 RC22 收：接 pageIndex/pageSize + 后端回传覆盖） */
  async function loadSlots(
    query?: ManualSlotQuery,
    options: { resetPage?: boolean; nextPageIndex?: number; nextPageSize?: number } = {}
  ): Promise<void> {
    if (query) slotQuery.value = query
    if (options.resetPage) slotPage.value = 1
    if (options.nextPageIndex != null) slotPage.value = options.nextPageIndex
    if (options.nextPageSize != null) slotPageSize.value = options.nextPageSize
    slotsLoading.value = true
    error.value = null
    try {
      const res = await resourceCalendarApi.getSlots({
        ...slotQuery.value,
        pageIndex: slotPage.value,
        pageSize: slotPageSize.value
      })
      slots.value = res.items
      slotTotal.value = res.total
      slotPage.value = res.page
      slotPageSize.value = res.pageSize
      // 当前选中槽若已不在列表（被软删 / 被过滤 / 翻页不见）→ 清空 Tab3
      const id = currentSlotId.value
      if (id != null && !slots.value.some((s) => s.manualSlotId === id)) {
        slotWindowItems.value = []
        slotWindowTotal.value = 0
        currentSlotId.value = null
      }
    } catch (err) {
      handleError(err)
      slots.value = []
      slotTotal.value = 0
    } finally {
      slotsLoading.value = false
    }
  }

  /** 新增人工槽主档 */
  async function createSlot(slot: ManualCapacitySlotDto): Promise<ManualCapacitySlotDto | null> {
    if (!canEditManual.value) {
      handleError(new ApiError('无人工能力槽编辑权限（需 aps.manual_capacity.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const created = await resourceCalendarApi.createSlot(slot)
      await loadSlots()
      return created
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 软删人工槽主档（isActive=0；已有窗口不自动清 —— 对接函 §三.4） */
  async function disableSlot(manualSlotId: number): Promise<boolean> {
    if (!canDeleteManual.value) {
      handleError(new ApiError('无人工能力槽删除权限（需 aps.manual_capacity.delete）', 403))
      return false
    }
    saving.value = true
    error.value = null
    try {
      await resourceCalendarApi.softDeleteSlot(manualSlotId)
      await loadSlots()
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  /** 查询某人工槽的窗口页 */
  async function loadSlotWindows(
    manualSlotId: number,
    options: { nextPageIndex?: number; nextPageSize?: number } = {}
  ): Promise<void> {
    if (!Number.isFinite(manualSlotId) || manualSlotId <= 0) {
      handleError(new ApiError('人工槽 Id 必须为正整数', 400))
      return
    }
    loading.value = true
    error.value = null
    if (currentSlotId.value !== manualSlotId) slotWindowPage.value = 1
    currentSlotId.value = manualSlotId
    if (options.nextPageSize != null) slotWindowPageSize.value = options.nextPageSize
    if (options.nextPageIndex != null) slotWindowPage.value = options.nextPageIndex
    try {
      const res = await resourceCalendarApi.getSlotWindows(manualSlotId, {
        pageIndex: slotWindowPage.value,
        pageSize: slotWindowPageSize.value
      })
      const items = res.items ?? []
      slotWindowItems.value = items
      slotWindowTotal.value = res.total
      slotWindowPage.value = res.page
      slotWindowPageSize.value = res.pageSize
      slotWindowTotals.value = { ...slotWindowTotals.value, [manualSlotId]: res.total }
      // 同设备侧：删掉本页最后一条后回退一页
      if (!items.length && res.page > 1 && res.total > 0) {
        return loadSlotWindows(manualSlotId, { nextPageIndex: res.page - 1 })
      }
    } catch (err) {
      handleError(err)
      slotWindowItems.value = []
      slotWindowTotal.value = 0
      const next = { ...slotWindowTotals.value }
      delete next[manualSlotId]
      slotWindowTotals.value = next
    } finally {
      loading.value = false
    }
  }

  /** 批量铺人工槽窗口（**覆盖式**，与设备侧同语义） */
  async function createSlotWindows(req: ManualSlotCalendarBulkRequest): Promise<number | null> {
    if (!canEditManual.value) {
      handleError(new ApiError('无人工能力槽编辑权限（需 aps.manual_capacity.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const inserted = await resourceCalendarApi.bulkCreateSlotWindows(req)
      await loadSlotWindows(req.manualSlotId)
      return inserted
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 单条追加人工槽窗口（同设备侧；只插不删，重叠 409） */
  async function appendSlotWindow(req: ManualSlotCalendarSingleRequest): Promise<number | null> {
    if (!canEditManual.value) {
      handleError(new ApiError('无人工能力槽编辑权限（需 aps.manual_capacity.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const id = await resourceCalendarApi.appendSlotWindow(req)
      await loadSlotWindows(req.manualSlotId)
      return id
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 物理删除人工槽窗口 */
  async function removeSlotWindow(id: number, manualSlotId: number): Promise<boolean> {
    if (!canDeleteManual.value) {
      handleError(new ApiError('无人工能力槽删除权限（需 aps.manual_capacity.delete）', 403))
      return false
    }
    saving.value = true
    error.value = null
    try {
      await resourceCalendarApi.deleteSlotWindow(id)
      await loadSlotWindows(manualSlotId)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  /* ===== 生命周期 ===== */

  function reset(): void {
    currentResourceId.value = null
    deviceWindowItems.value = []
    deviceWindowTotal.value = 0
    deviceWindowTotals.value = {}
    resolvePreview.value = null
    slots.value = []
    slotQuery.value = {}
    currentSlotId.value = null
    slotWindowItems.value = []
    slotWindowTotal.value = 0
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
    resources,
    resourcesLoading,
    currentResourceId,
    deviceWindowItems,
    deviceWindowTotal,
    deviceWindowPage,
    deviceWindowPageSize,
    deviceWindowTotals,
    resolvePreview,
    resolveLoading,
    slots,
    slotQuery,
    slotTotal,
    slotPage,
    slotPageSize,
    slotsLoading,
    departmentCandidates,
    currentSlotId,
    slotWindowItems,
    slotWindowTotal,
    slotWindowPage,
    slotWindowPageSize,
    slotWindowTotals,
    loading,
    saving,
    error,
    // getters：权限
    canViewDevice,
    canEditDevice,
    canDeleteDevice,
    canViewManual,
    canEditManual,
    canDeleteManual,
    // getters：数据
    deviceWindowsLoaded,
    deviceCalendarStatus,
    slotCalendarStatus,
    currentSlot,
    // actions
    loadResources,
    loadDepartmentCandidates,
    loadDeviceWindows,
    createDeviceWindows,
    appendDeviceWindow,
    loadResolvePreview,
    removeDeviceWindow,
    loadSlots,
    createSlot,
    disableSlot,
    loadSlotWindows,
    createSlotWindows,
    appendSlotWindow,
    removeSlotWindow,
    reset,
    clearError,
    handleError
  }
})
