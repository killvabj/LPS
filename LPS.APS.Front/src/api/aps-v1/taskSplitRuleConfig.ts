/**
 * APS V1 4号位 — 执行批拆分规则（TaskSplitRuleConfig / Batch Policy）治理 API（5 端点）
 *
 * @owner 3号位（规则参数治理；本端点属 3号位 治理 CRUD，不登记 ODS / 不 MERGE）
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig维护接口契约_v1.0_20261007.md
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1.1_20261009.md
 * @see lps/LPS.APS.Web/Controllers/TaskSplitRuleConfigController.cs
 * @see lps/LPS.APS.Application/Services/TaskSplitRuleConfigGovernanceService.cs
 * @see lps/LPS.APS.Application/Models/TaskSplitRuleConfigProjector.cs（两级承接：发布时投影进 FrozenStrategySnapshot.BatchPolicy 子块）
 *
 * 端点（5 条，前缀 /api/governance）：
 *   1. GET    /task-split-rule-configs?materialId=&productionDepartmentId=&isActive=    列表
 *   2. POST   /task-split-rule-configs                                                   新增
 *   3. PUT    /task-split-rule-configs/{id}                                              编辑（整对象替换，不改 IsActive）
 *   4. DELETE /task-split-rule-configs/{id}                                              停用（IsActive=0 软删）
 *   5. POST   /task-split-rule-configs/{id}/activate                                     恢复启用（IsActive 0→1）
 *
 * 权限（复用 Rule 码，零权限种子变更）：
 *   - GET    = aps.rule.view
 *   - POST / PUT / DELETE / POST-activate = aps.rule.edit
 *
 * 错误映射：
 *   - 422 数据红线（AllowSplit/AllowMerge 省略、Max < Min、EffectiveTo < EffectiveFrom、业务键重复、负数；
 *     恢复启用：NULL 部门历史行不可启用 / 与其它有效规则窗口重叠）
 *   - 404 id 不存在
 *   - 400 MaterialId ≤ 0 / ProductionDepartmentId ≤ 0 / 路径 id ≤ 0
 *   - 403 权限不足 / 业务范围越界（v1.1 新增 Department ScopeViolation；message 含「业务范围越界（Department）：xxx」）
 *
 * v1.1 增量（10-09 P0-01 接口签名 Scope 变更登记）：
 *   - List 服务层增 actorUserId（Controller 从登录身份取，HTTP 零变化）
 *   - 列表按 Department Scope 过滤：Global 不过滤 / 非 Global 按 Dept 授权 IN 约束 / 无授权 fail-closed 空页
 *   - 显式传入未授权部门 → 403 ScopeViolationException
 *   - 本文件：HTTP 请求参数零变化；仅错误信封新增 403「业务范围越界（Department）」
 *
 * 兼容字段（ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity）不暴露：
 *   - 仅历史兼容，不进请求体、不进 DTO、不投快照（@see 冻结文档 v5.1.9 §468 + 契约 §2.2）
 *
 * 冻结禁列（13 v1.8:97 / 12 v2.2:102）**不展示、不维护、不传**：
 *   MaxOptimizationSplitCount / MaxBatchCandidates / BottleneckSplitStrategy / NonBottleneckStrategy
 *   —— 后端 v5.1.10 起已不再 SELECT / 不再接收，前端同步移除。
 *
 * 设计要点：
 *   - mock 模式：返回 ~6 条 fixture（覆盖 Material+部门 + Material 默认 + 停用）；写端点抛 503 防伪成功（与 opm.ts 同范式）
 *   - 上层组件调 taskSplitRuleConfigApi.* 即可，store + page 不必关心 unwrap / mock fallback
 */

import { apsHttp, APS_USE_MOCK, ApiError } from './http'
import type {
  TaskSplitRuleConfigDto,
  SaveTaskSplitRuleConfigRequest,
  TaskSplitRuleConfigListQuery,
  PagedTaskSplitRuleConfig
} from './types/taskSplitRuleConfig'

/* ==================== Mock Fixtures（仅离线 UI 演示） ====================
 * 覆盖：
 *  - 物料 + 部门组合（业务键完整）
 *  - 物料级默认（ProductionDepartmentId = null）
 *  - 允许拆分/允许合并四种组合
 *  - 含一条已停用（IsActive=false）
 *  - 含一条永久有效 + 含一条带生效窗口
 */
const MOCK_NOW = (offsetDays: number) => new Date(Date.now() + offsetDays * 86400_000).toISOString()

const MOCK_RULES: TaskSplitRuleConfigDto[] = [
  {
    id: 1,
    materialId: 5346846,
    productionDepartmentId: 1103,
    minExecutionBatchQty: 10,
    maxExecutionBatchQty: 200,
    preferredBatchQty: 50,
    allowSplit: true,
    allowMerge: true,
    isActive: true,
    effectiveFrom: MOCK_NOW(-30),
    effectiveTo: null,
    createdAt: MOCK_NOW(-30),
    updatedAt: MOCK_NOW(-2)
  },
  {
    id: 2,
    materialId: 5346846,
    productionDepartmentId: null,
    minExecutionBatchQty: 20,
    maxExecutionBatchQty: 500,
    preferredBatchQty: null,
    allowSplit: false,
    allowMerge: true,
    isActive: true,
    effectiveFrom: null,
    effectiveTo: null,
    createdAt: MOCK_NOW(-60),
    updatedAt: MOCK_NOW(-7)
  },
  {
    id: 3,
    materialId: 5346847,
    productionDepartmentId: 1114,
    minExecutionBatchQty: 5,
    maxExecutionBatchQty: 100,
    preferredBatchQty: 30,
    allowSplit: true,
    allowMerge: false,
    isActive: true,
    effectiveFrom: MOCK_NOW(-15),
    effectiveTo: MOCK_NOW(60),
    createdAt: MOCK_NOW(-15),
    updatedAt: MOCK_NOW(-1)
  },
  {
    id: 4,
    materialId: 5346848,
    productionDepartmentId: 1113,
    minExecutionBatchQty: null,
    maxExecutionBatchQty: null,
    preferredBatchQty: null,
    allowSplit: true,
    allowMerge: false,
    isActive: true,
    effectiveFrom: null,
    effectiveTo: null,
    createdAt: MOCK_NOW(-90),
    updatedAt: MOCK_NOW(-30)
  },
  {
    id: 5,
    materialId: 5346849,
    productionDepartmentId: null,
    minExecutionBatchQty: 1,
    maxExecutionBatchQty: 1000,
    preferredBatchQty: 100,
    allowSplit: true,
    allowMerge: true,
    isActive: true,
    effectiveFrom: null,
    effectiveTo: null,
    createdAt: MOCK_NOW(-180),
    updatedAt: MOCK_NOW(-90)
  },
  {
    id: 6,
    materialId: 5346850,
    productionDepartmentId: 1105,
    minExecutionBatchQty: 50,
    maxExecutionBatchQty: 300,
    preferredBatchQty: 150,
    allowSplit: false,
    allowMerge: true,
    isActive: false, // 已停用（演示 Deactivate 流）
    effectiveFrom: MOCK_NOW(-365),
    effectiveTo: MOCK_NOW(-30),
    createdAt: MOCK_NOW(-365),
    updatedAt: MOCK_NOW(-30)
  }
]

/* mock 辅助：筛选（与 3号位 后端 PageResult 口径一致，固定排序：MaterialId, ProductionDepartmentId, Id） */
function applyMockFilter(
  list: TaskSplitRuleConfigDto[],
  q?: TaskSplitRuleConfigListQuery
): TaskSplitRuleConfigDto[] {
  let result = [...list]
  if (!q) return result
  if (q.materialId !== undefined) result = result.filter((x) => x.materialId === q.materialId)
  if (q.productionDepartmentId !== undefined) {
    if (q.productionDepartmentId === null) {
      result = result.filter((x) => x.productionDepartmentId === null)
    } else {
      result = result.filter((x) => x.productionDepartmentId === q.productionDepartmentId)
    }
  }
  if (q.isActive !== undefined) result = result.filter((x) => x.isActive === q.isActive)
  return result.sort((a, b) => {
    if (a.materialId !== b.materialId) return a.materialId - b.materialId
    const aDept = a.productionDepartmentId ?? -1
    const bDept = b.productionDepartmentId ?? -1
    if (aDept !== bDept) return aDept - bDept
    return a.id - b.id
  })
}

/** mock 辅助：分页（与后端 Math.Max(pageIndex,1) + Math.Clamp(pageSize,1,200) 同口径） */
function paginateMock(
  filtered: TaskSplitRuleConfigDto[],
  pageIndex: number,
  pageSize: number
): PagedTaskSplitRuleConfig {
  const idx = Math.max(1, pageIndex | 0)
  const size = Math.min(200, Math.max(1, pageSize | 0))
  const start = (idx - 1) * size
  return {
    items: filtered.slice(start, start + size),
    total: filtered.length,
    page: idx,
    pageSize: size
  }
}

/* ==================== API ==================== */

export const taskSplitRuleConfigApi = {
  /**
   * 列表（GET /api/governance/task-split-rule-configs）
   *  - 全部参数可选；后端排序固定：MaterialId, ProductionDepartmentId, Id
   *  - 10-08 落地：响应改用 PageResult<T>（与 R2 既有 Rbac/Audit/ManualEta 同形态）
   *  - 默认 pageIndex=1 / pageSize=20（避免后端默认 100 拉全量）
   *  - 后端硬上限 pageSize=200（Math.Clamp 静默截断，零报错）
   *  - mock 模式：本地 MOCK_RULES 内存筛选 + 排序 + 分页
   */
  async list(query?: TaskSplitRuleConfigListQuery): Promise<PagedTaskSplitRuleConfig> {
    const pageIndex = query?.pageIndex ?? 1
    const pageSize = query?.pageSize ?? 20
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 250))
      const filtered = applyMockFilter(MOCK_RULES, query)
      return paginateMock(filtered, pageIndex, pageSize)
    }
    const params: Record<string, unknown> = {
      pageIndex,
      pageSize
    }
    if (query?.materialId !== undefined) params.materialId = query.materialId
    if (query?.productionDepartmentId !== undefined) {
      // null → 后端要传 "null" 字符串吗？契约 §2.1 productionDepartmentId (int?) → null = 全部
      // 真实模式：直接不传 productionDepartmentId（后端 int? null = 无过滤）
      if (query.productionDepartmentId !== null)
        params.productionDepartmentId = query.productionDepartmentId
    }
    if (query?.isActive !== undefined) params.isActive = query.isActive
    return apsHttp.get<PagedTaskSplitRuleConfig>({
      url: '/api/governance/task-split-rule-configs',
      params
    })
  },

  /**
   * 新增（POST /api/governance/task-split-rule-configs）
   *  - 后端会校验：MaterialId > 0、AllowSplit/AllowMerge 显式提供、Max ≥ Min、EffectiveTo ≥ EffectiveFrom、业务键不重复
   *  - 422 / 400 / 403 由后端抛 ApiError
   *  - mock 模式：本地追加 MOCK_RULES（id = max+1），回包
   */
  async create(input: SaveTaskSplitRuleConfigRequest): Promise<TaskSplitRuleConfigDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 200))
      const nextId = MOCK_RULES.reduce((max, x) => Math.max(max, x.id), 0) + 1
      const now = new Date().toISOString()
      const next: TaskSplitRuleConfigDto = {
        id: nextId,
        materialId: input.materialId,
        productionDepartmentId: input.productionDepartmentId,
        minExecutionBatchQty: input.minExecutionBatchQty,
        maxExecutionBatchQty: input.maxExecutionBatchQty,
        preferredBatchQty: input.preferredBatchQty,
        allowSplit: input.allowSplit ?? false,
        allowMerge: input.allowMerge ?? false,
        isActive: true,
        effectiveFrom: input.effectiveFrom,
        effectiveTo: input.effectiveTo,
        createdAt: now,
        updatedAt: now
      }
      MOCK_RULES.push(next)
      return next
    }
    return apsHttp.post<TaskSplitRuleConfigDto>({
      url: '/api/governance/task-split-rule-configs',
      data: input
    })
  },

  /**
   * 编辑（PUT /api/governance/task-split-rule-configs/{id}）
   *  - 整对象替换，**不改 IsActive**（停用走 deactivate）
   *  - 路径 id 与请求体 materialId 无强约束（后端校验只查 Id 是否存在 + 业务键冲突）
   *  - mock 模式：本地替换 MOCK_RULES[idx]
   */
  async update(id: number, input: SaveTaskSplitRuleConfigRequest): Promise<TaskSplitRuleConfigDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 200))
      const idx = MOCK_RULES.findIndex((x) => x.id === id)
      if (idx < 0) {
        throw new ApiError(`mock 无 TaskSplitRuleConfig id=${id}`, 404)
      }
      const now = new Date().toISOString()
      const next: TaskSplitRuleConfigDto = {
        ...MOCK_RULES[idx],
        materialId: input.materialId,
        productionDepartmentId: input.productionDepartmentId,
        minExecutionBatchQty: input.minExecutionBatchQty,
        maxExecutionBatchQty: input.maxExecutionBatchQty,
        preferredBatchQty: input.preferredBatchQty,
        allowSplit: input.allowSplit ?? MOCK_RULES[idx].allowSplit,
        allowMerge: input.allowMerge ?? MOCK_RULES[idx].allowMerge,
        effectiveFrom: input.effectiveFrom,
        effectiveTo: input.effectiveTo,
        updatedAt: now
        // IsActive 保留原值（PUT 不改 IsActive；停用走 deactivate）
      }
      MOCK_RULES[idx] = next
      return next
    }
    return apsHttp.put<TaskSplitRuleConfigDto>({
      url: `/api/governance/task-split-rule-configs/${id}`,
      data: input
    })
  },

  /**
   * 停用（DELETE /api/governance/task-split-rule-configs/{id}）
   *  - 软删 IsActive=0，保留历史与生效窗口（4号位 文档硬约束：不物理删）
   *  - 后端同时写 AuditLog（ActionCode=Deactivate）
   *  - mock 模式：本地置 IsActive=false
   */
  async deactivate(id: number): Promise<TaskSplitRuleConfigDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 200))
      const idx = MOCK_RULES.findIndex((x) => x.id === id)
      if (idx < 0) {
        throw new ApiError(`mock 无 TaskSplitRuleConfig id=${id}`, 404)
      }
      const now = new Date().toISOString()
      const next: TaskSplitRuleConfigDto = {
        ...MOCK_RULES[idx],
        isActive: false,
        updatedAt: now
      }
      MOCK_RULES[idx] = next
      return next
    }
    return apsHttp.delete<TaskSplitRuleConfigDto>({
      url: `/api/governance/task-split-rule-configs/${id}`
    })
  },

  /**
   * 恢复启用（POST /api/governance/task-split-rule-configs/{id}/activate）
   *  - IsActive 0→1，保留历史与生效窗口（不物理删、不清窗口）
   *  - 后端同时写 AuditLog（ActionCode=Activate）
   *  - 后端红线（422）：NULL 部门历史行不可恢复启用；与其它有效规则窗口重叠
   *  - 幂等：已启用时后端原样返回、不追加第二条审计
   *  - mock 模式：本地置 IsActive=true
   */
  async activate(id: number): Promise<TaskSplitRuleConfigDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 200))
      const idx = MOCK_RULES.findIndex((x) => x.id === id)
      if (idx < 0) {
        throw new ApiError(`mock 无 TaskSplitRuleConfig id=${id}`, 404)
      }
      const now = new Date().toISOString()
      const next: TaskSplitRuleConfigDto = {
        ...MOCK_RULES[idx],
        isActive: true,
        updatedAt: now
      }
      MOCK_RULES[idx] = next
      return next
    }
    return apsHttp.post<TaskSplitRuleConfigDto>({
      url: `/api/governance/task-split-rule-configs/${id}/activate`,
      data: {}
    })
  }
}
