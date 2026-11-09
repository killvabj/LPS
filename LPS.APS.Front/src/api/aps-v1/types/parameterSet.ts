/**
 * APS V1 4号位 — 参数集（ParameterSet）治理 DTO
 *
 * ⚠️ 2026-10-22 新增（拆分自 types/rule.ts）：
 *   参数集是**独立主表**（DDL `08:9064` ParameterSet / `08:9081` ParameterSetVersion），
 *   与规则集平行，**不是**规则集的子集。两者只在 StrategyProfileVersion 上会合。
 *
 * 端点（3号位 GovernanceController，权限码与规则集同构但独立）：
 *  - GET  /api/governance/parameter-sets                    aps.parameter.view
 *  - GET  /api/governance/parameter-set/{id}/versions         aps.parameter.view
 *  - GET  /api/governance/parameter-set/version/{id}         aps.parameter.view
 *  - POST /api/governance/parameter-set/version              aps.parameter.edit
 *  - PUT  /api/governance/parameter-set/version/{id}         aps.parameter.edit
 *  - POST /api/governance/parameter-set/version/{id}/publish aps.parameter.publish
 *  - POST /api/governance/parameter-set/version/{id}/disable aps.parameter.edit
 *  - GET  /api/governance/parameter-set/version/{id}/validate aps.parameter.view
 *  - GET  /api/governance/parameter-set/version/diff         aps.parameter.view
 */

import type { IsoDateTime, RuleStatus } from './common'
import type { ParameterSetVersion } from './rule'

/** 参数集列表项（结构与规则集列表项同构，便于同一套列表/过滤/KPI 组件复用） */
export interface ParameterSetSummaryDto {
  parameterSetId: number
  parameterSetCode: string
  parameterSetName: string
  description?: string
  /** 主表启用位（DDL `IsActive`；后端只提供停用语义，没有删除端点） */
  isActive: boolean
  /** 最新已发布版本码（后端 `currentVersionCode`，列表投影按 Id DESC 取；无版本行时整个 key 缺失）。
   *  ⚠️ 是「最新已发布」不是「当前有效」——后者按生效期窗口判定。 */
  currentVersionCode?: string
  /** 待发布 DRAFT 版本码（后端 `draftVersionCode`） */
  draftVersionCode?: string
  status: RuleStatus
  /**
   * 是否存在版本行
   *
   * 与规则集同源：`Program.cs:94` 的 `WhenWritingNull` ⇒ 无版本行时 status 等键整个消失，
   * 「status 缺失」= 「未配版本」。
   */
  hasVersion: boolean
  lastChangeReason?: string
  createdAt: IsoDateTime
  publishedAt?: IsoDateTime
}

/** 参数集单版本历史行 */
export interface ParameterSetVersionHistoryDto {
  parameterSetId: number
  /** 版本码（如 `PS-PROD-V1.0`） */
  versionCode: string
  /** ParameterSetVersion 主键（publish/disable/validate 端点键） */
  versionId?: number
  status: RuleStatus
  /** 变更原因（后端 `remarks`） */
  changeReason: string
  publishedAt?: IsoDateTime
  publishedBy: string
  /** 是否为「最新已发布」版本（Id 最大的那条 PUBLISHED）。
   *  ⚠️ 不等于「当前有效」——后者按生效期窗口判定，由 published-version 端点给出。 */
  isLatestPublished: boolean
  effectiveFrom?: IsoDateTime
  effectiveTo?: IsoDateTime
}

/** 参数集详情 */
export interface ParameterSetDetailDto {
  summary: ParameterSetSummaryDto
  /** 当前展示版本的内容（5 主题 JSON 字符串） */
  version: ParameterSetVersion | null
  history: ParameterSetVersionHistoryDto[]
}
