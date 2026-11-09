/**
 * APS V1 4号位 — 执行批拆分规则（TaskSplitRuleConfig / Batch Policy 批量策略）DTO
 *
 * @owner 3号位（规则参数治理）
 * @see frontNew/docs/3号位致4号位_TaskSplitRuleConfig维护接口契约_v1.0_20261007.md
 * @see lps/LPS.APS.Core/DTOs/Governance/TaskSplitRuleConfigDtos.cs
 * @see lps/LPS.APS.Web/Controllers/TaskSplitRuleConfigController.cs
 *
 * 业务粒度：MaterialId + ProductionDepartmentId（NULL 部门 = Material 级默认）
 * 载体：两级承接 —— 主题表 CRUD 即最终态 + 发布时投影进 FrozenStrategySnapshot.BatchPolicy 子块
 * 权限：aps.rule.view / aps.rule.edit（复用 Rule 码，零权限种子变更）
 *
 * 兼容字段（ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity）不暴露：
 *  - 仅历史兼容，不进请求体、不进 DTO、不投快照
 *  - V1 正式匹配只用 Material + 部门（@see 冻结文档 v1.8 §1295 / v5.1.9 §468）
 */

import type { IsoDateTime } from './common'

/* =============== DTO =============== */

/**
 * 执行批拆分规则列表/详情 DTO（与后端 TaskSplitRuleConfigDto 对齐）
 *
 * ⚠️ 冻结 13 v1.8:97 / 12 v2.2:102：`MaxOptimizationSplitCount`、`MaxBatchCandidates`、
 * `BottleneckSplitStrategy`、`NonBottleneckStrategy` **不展示、不维护**。
 * 后端 v5.1.10 起已不再 SELECT / 不再接收（`TaskSplitRuleConfigGovernanceService.cs:36-40`、
 * `TaskSplitRuleConfigDtos.cs:33`）⇒ 前端类型同步移除，避免"显示了永远不会来的字段"。
 *
 * 兼容字段不暴露：ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity（同上）。
 */
export interface TaskSplitRuleConfigDto {
  id: number
  /** 物料 Id（业务键之一） */
  materialId: number
  /** 生产部门 Id（业务键之二；null = Material 级默认） */
  productionDepartmentId: number | null
  /** 经济最小批量（**软目标**；超出区间的可生产批量不是硬不可行，冻结 13 v1.8:31 / 01 B-011） */
  minExecutionBatchQty: number | null
  /** 经济最大批量（**软目标**；null = 未设） */
  maxExecutionBatchQty: number | null
  /** 经济软目标批量（Preferred；业务生效必填且 >0，须落经济区间内） */
  preferredBatchQty: number | null
  /** 是否允许拆分（必填，DDL DEFAULT 1） */
  allowSplit: boolean
  /** 是否允许合并（必填，DDL DEFAULT 0） */
  allowMerge: boolean
  /** 启用态（DELETE 软删 IsActive=0，保留历史与生效窗口） */
  isActive: boolean
  /** 生效起始（null = 不限） */
  effectiveFrom: IsoDateTime | null
  /** 生效截止（null = 永久有效） */
  effectiveTo: IsoDateTime | null
  createdAt: IsoDateTime
  updatedAt: IsoDateTime
}

/**
 * 新增/编辑请求体（与后端 SaveTaskSplitRuleConfigRequest 对齐）
 *  - **AllowSplit / AllowMerge 是 bool?**：须显式提供，无默认值（后端 Validate 422 强校验）
 *  - PUT 整对象替换，**不改 IsActive**（停用走独立 DELETE）
 *  - **不传** MaxOptimizationSplitCount / MaxBatchCandidates / BottleneckSplitStrategy /
 *    NonBottleneckStrategy（冻结 13 v1.8:97；后端 SaveRequest 已移除这四项）
 */
export interface SaveTaskSplitRuleConfigRequest {
  /** 物料 Id（业务键之一；后端 400 校验 MaterialId > 0） */
  materialId: number
  /** 生产部门 Id（业务键之二；null = Material 级默认；后端 400 校验 ≤ 0） */
  productionDepartmentId: number | null
  /** 经济最小批量（软目标） */
  minExecutionBatchQty: number | null
  /** 经济最大批量（软目标） */
  maxExecutionBatchQty: number | null
  /** 经济软目标批量 */
  preferredBatchQty: number | null
  /** 必须显式提供；省略 → 后端 422「须显式提供（bool? 不得省略）」 */
  allowSplit: boolean | null
  /** 必须显式提供；省略 → 后端 422 */
  allowMerge: boolean | null
  effectiveFrom: IsoDateTime | null
  effectiveTo: IsoDateTime | null
  /** 源系统部门码（仅透传，3号位 2026-10-08 知会；当前 SaveRequest 暂不持久化） */
  sourceDeptCode?: string | null
}

/* =============== 查询参数 =============== */

/**
 * 列表查询参数（@see 契约 §2.1 + 3号位 2026-10-08 列表分页改造回执）
 *  - 全部可选；排序：MaterialId, ProductionDepartmentId, Id
 *  - 分页（10-08 落地，与 Rbac / Audit / ManualEta 同轨）：
 *    - pageIndex：1 基页码，< 1 后端 Math.Max 归 1
 *    - pageSize：每页条数，> 200 后端 Math.Clamp 截断到 200（与 Rbac 硬上限一致）
 *  - 前端不传 pageIndex / pageSize 时使用默认值 1 / 20
 */
export interface TaskSplitRuleConfigListQuery {
  materialId?: number
  productionDepartmentId?: number
  isActive?: boolean
  /** 1 基页码；< 1 后端归 1 */
  pageIndex?: number
  /** 每页条数；> 200 后端静默截断到 200 */
  pageSize?: number
}

/**
 * 列表分页响应（10-08 落地，与 R2 既有的 PageResult<T> 同形态；零新概念）
 *  - items：当前页数据（@see 契约 §2.2）
 *  - total：满足筛选条件的总条数（COUNT）
 *  - page：实际生效的页码（与请求 pageIndex 一致；< 1 已归 1）
 *  - pageSize：实际生效的每页条数（> 200 已截断到 200）
 */
export interface PagedTaskSplitRuleConfig {
  items: TaskSplitRuleConfigDto[]
  total: number
  page: number
  pageSize: number
}
