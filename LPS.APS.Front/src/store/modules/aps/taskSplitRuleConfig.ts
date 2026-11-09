/**
 * APS V1 4号位 — TaskSplitRuleConfig / Batch Policy 批量策略 Pinia store
 *
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig维护接口契约_v1.0_20261007.md
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1.1_20261009.md
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig列表分页改造回执_方案A_v1.0_20261008.md
 * @see lps/LPS.APS.Web/Controllers/TaskSplitRuleConfigController.cs
 * @see lps/LPS.APS.Application/Services/TaskSplitRuleConfigGovernanceService.cs
 * @see lps/LPS.APS.Application/Models/TaskSplitRuleConfigProjector.cs（两级承接）
 *
 * 业务粒度：MaterialId + ProductionDepartmentId（NULL 部门 = Material 级默认）
 * 权限码：aps.rule.view / aps.rule.edit（复用 Rule 码，零权限种子变更）
 *
 * 状态（10-08 列表分页改造落地后）：
 *  - list：当前页规则（items，pageSize 条）
 *  - total：满足筛选条件的总条数（来自后端 PageResult.total，KPI 用）
 *  - query / pageIndex / pageSize：当前列表筛选 + 分页条件
 *  - loading / saving / error：UI 态
 *
 * v1.1 增量（10-09 P0-01 Scope 变更登记）：
 *  - HTTP 调用零变化（actorUserId 由 Controller 从登录身份取，前端无感）
 *  - 错误信封新增 403「业务范围越界（Department）：xxx」 → handleError 已透传 ApiError.message
 *  - 非 Global 无 Department 授权 → 后端 fail-closed 空页（items=[], total=0）→ 本 store 已正确清空
 *  - 列表场景与本 store 无副作用；写路径 `create / update / deactivate / activate` 越权 403 由 canEdit 前置 + handleError 兜底
 *
 * 红线：
 *  - 仅 DML（POST / PUT / DELETE / GET），无 DDL（DDL 红线 #6 属 2号位 owner）
 *  - PUT 整对象替换，**不改 IsActive**（停用走独立 deactivate）
 *  - 兼容字段 ResourceGroupId / MOQ / EOQ 不进 store / 不进请求
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  taskSplitRuleConfigApi,
  type SaveTaskSplitRuleConfigRequest,
  type TaskSplitRuleConfigDto,
  type TaskSplitRuleConfigListQuery
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

/** 默认每页条数（与 R2 既有 Rbac / Audit / ManualEta 一致；后端硬上限 200） */
const DEFAULT_PAGE_SIZE = 20
/** 后端硬上限（Math.Clamp 静默截断；与 RbacController 一致） */
const MAX_PAGE_SIZE = 200

export const useTaskSplitRuleConfigStore = defineStore('aps.taskSplitRuleConfig', () => {
  // ===== state =====
  const list = ref<TaskSplitRuleConfigDto[]>([])
  /** 满足筛选条件的总条数（后端 COUNT；KPI 总数 / 启用 / 停用 / Material 级默认 改用 total 推算以避免分页误读） */
  const total = ref<number>(0)
  const query = ref<TaskSplitRuleConfigListQuery>({})
  /** 实际生效的页码（后端 Math.Max 归 1 后回传） */
  const page = ref<number>(1)
  /** 实际生效的每页条数（后端 Math.Clamp 截断到 200 后回传） */
  const pageSize = ref<number>(DEFAULT_PAGE_SIZE)
  const loading = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)

  // ===== auth =====
  const apsAuth = useApsAuthStore()

  // ===== getters =====

  /** 查看权限 */
  const canView = computed<boolean>(() => apsAuth.has('aps.rule.view'))

  /** 编辑权限（含新增 / 编辑 / 停用） */
  const canEdit = computed<boolean>(() => apsAuth.has('aps.rule.edit'))

  // ===== actions =====

  /**
   * 加载规则列表（默认按当前 query + page + pageSize）
   *  - 10-08：响应改 PageResult<T>，后端硬上限 pageSize=200 / pageIndex<1 归 1
   *  - 传 resetPage=true 时强制 pageIndex=1（用于筛选 / 刷新，避免空态错觉）
   *  - mock 模式：本地 MOCK_RULES 内存筛选 + 排序 + 分页（同后端口径）
   */
  async function load(
    nextQuery?: TaskSplitRuleConfigListQuery,
    options: { resetPage?: boolean; nextPageIndex?: number; nextPageSize?: number } = {}
  ): Promise<void> {
    if (nextQuery !== undefined) {
      query.value = { ...nextQuery }
    }
    if (options.resetPage === true) {
      page.value = 1
    }
    if (options.nextPageIndex !== undefined) {
      page.value = options.nextPageIndex
    }
    if (options.nextPageSize !== undefined) {
      pageSize.value = Math.min(MAX_PAGE_SIZE, Math.max(1, options.nextPageSize | 0))
    }
    loading.value = true
    error.value = null
    try {
      const result = await taskSplitRuleConfigApi.list({
        ...query.value,
        pageIndex: page.value,
        pageSize: pageSize.value
      })
      list.value = result.items
      total.value = result.total
      // 用后端回传的实际生效 page / pageSize 覆盖本地状态
      page.value = result.page
      pageSize.value = result.pageSize
    } catch (err) {
      handleError(err)
      list.value = []
      total.value = 0
    } finally {
      loading.value = false
    }
  }

  /**
   * 新增规则
   *  - smart-insert：按后端排序契约（MaterialId, ProductionDepartmentId, Id ASC）将新条目插到正确位置
   *  - 视觉与后端 100% 一致，刷新无跳变
   *  - 越界（按字典序应在本页之前 / 之后）不挤入 list（避免跨页条目），仅 total+=1
   *  - 同 material+dept 时新 Id 最大 → 插入该段末尾；跨 material+dept 时按字典序穿插
   */
  async function create(
    input: SaveTaskSplitRuleConfigRequest
  ): Promise<TaskSplitRuleConfigDto | null> {
    if (!canEdit.value) {
      handleError(new ApiError('当前操作者无 Batch Policy 编辑权限（需 aps.rule.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const result = await taskSplitRuleConfigApi.create(input)
      // total 必须 +1，否则 KPI 总数滞后
      total.value = total.value + 1
      // 仅当本页未满时本地 smart-insert（满页则不挤入，下次 load 自然拉回正确位置）
      if (list.value.length < pageSize.value) {
        const insertIdx = findSortedInsertIndex(list.value, result)
        list.value = [...list.value.slice(0, insertIdx), result, ...list.value.slice(insertIdx)]
      }
      return result
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 编辑规则（PUT 整对象替换，不改 IsActive） */
  async function update(
    id: number,
    input: SaveTaskSplitRuleConfigRequest
  ): Promise<TaskSplitRuleConfigDto | null> {
    if (!canEdit.value) {
      handleError(new ApiError('当前操作者无 Batch Policy 编辑权限（需 aps.rule.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const result = await taskSplitRuleConfigApi.update(id, input)
      const idx = list.value.findIndex((x) => x.id === id)
      if (idx >= 0) {
        list.value = list.value.map((x, i) => (i === idx ? result : x))
      } else {
        // 跨页编辑（极少见，本页未显示但 id 真实存在）→ 不挤进 list，下次 load 拉回
      }
      return result
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 停用规则（DELETE 软删 IsActive=0） */
  async function deactivate(id: number): Promise<TaskSplitRuleConfigDto | null> {
    if (!canEdit.value) {
      handleError(new ApiError('当前操作者无 Batch Policy 编辑权限（需 aps.rule.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const result = await taskSplitRuleConfigApi.deactivate(id)
      const idx = list.value.findIndex((x) => x.id === id)
      if (idx >= 0) {
        list.value = list.value.map((x, i) => (i === idx ? result : x))
      }
      return result
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 恢复启用规则（POST activate；IsActive 0→1；后端 422 红线由 handleError 透传） */
  async function activate(id: number): Promise<TaskSplitRuleConfigDto | null> {
    if (!canEdit.value) {
      handleError(new ApiError('当前操作者无 Batch Policy 编辑权限（需 aps.rule.edit）', 403))
      return null
    }
    saving.value = true
    error.value = null
    try {
      const result = await taskSplitRuleConfigApi.activate(id)
      const idx = list.value.findIndex((x) => x.id === id)
      if (idx >= 0) {
        list.value = list.value.map((x, i) => (i === idx ? result : x))
      }
      return result
    } catch (err) {
      handleError(err)
      return null
    } finally {
      saving.value = false
    }
  }

  /** 清空状态（页面切换时调用，避免跨用户残留） */
  function reset(): void {
    list.value = []
    total.value = 0
    query.value = {}
    page.value = 1
    pageSize.value = DEFAULT_PAGE_SIZE
    error.value = null
  }

  // ===== helpers =====

  /**
   * 按后端排序契约（MaterialId, ProductionDepartmentId, Id ASC）找新条目的插入位置
   *  - DepartmentId NULL 视为排在所有非 NULL 之后（NULL 部门 = Material 级默认 → 段末）
   *  - 返回 0..list.length 之间的 lowerBound 索引
   *  - 越界情况（按字典序应在本页之前 / 之后）由调用方决定是否挤入：
   *    - 我们选择"无脑挤入"——用户看到新条目出现在其排序位置；
   *    - 极小概率跨页场景（按字典序实际在另一页）由下次 load() 自然拉回正确位置
   */
  function findSortedInsertIndex(
    list: TaskSplitRuleConfigDto[],
    item: TaskSplitRuleConfigDto
  ): number {
    const cmp = (a: TaskSplitRuleConfigDto, b: TaskSplitRuleConfigDto): number => {
      if (a.materialId !== b.materialId) return a.materialId - b.materialId
      const aDept = a.productionDepartmentId ?? Number.MAX_SAFE_INTEGER
      const bDept = b.productionDepartmentId ?? Number.MAX_SAFE_INTEGER
      if (aDept !== bDept) return aDept - bDept
      return a.id - b.id
    }
    // 二分找 lowerBound（第一个 >= item 的索引）
    let lo = 0
    let hi = list.length
    while (lo < hi) {
      const mid = (lo + hi) >>> 1
      if (cmp(list[mid], item) < 0) lo = mid + 1
      else hi = mid
    }
    return lo
  }

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
    query,
    page,
    pageSize,
    loading,
    saving,
    error,
    // getters
    canView,
    canEdit,
    // actions
    load,
    create,
    update,
    deactivate,
    activate,
    reset,
    handleError
  }
})
