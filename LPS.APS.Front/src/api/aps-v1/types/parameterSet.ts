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

/* ================= 一版一行列表源（2026-11-11，3号位 扩围加函） ================= */

/**
 * 参数集「主子表联查·一版一行」行（后端 `ParameterSetVersionRowDto`）
 *
 * ⚠️ 2026-11-11（3号位《三族列表改主子表联查一版一行·新增三族 versions 契约变更协调件·扩围加函》）：
 *   旧端点 `GET /parameter-sets` 用 `OUTER APPLY TOP1 ORDER BY Id DESC` 取状态 ⇒
 *   同族「已发布生效版 + 新建草稿」并存时，**状态被草稿覆盖**（与规则集同因）。
 *   新端点 `GET /parameter-sets/versions` 每主表 N 个版本 ⇒ N 行，各版本状态独立可读。
 */
export interface ParameterSetVersionRowDto {
  /** 主表 Id */
  parameterSetId: number
  /** 主表编码 */
  parameterSetCode: string
  /** 主表名称 */
  parameterSetName: string
  /** 主表说明 */
  description?: string | null
  /** 主表启停（与版本状态是两件事） */
  isActive?: boolean | null
  /** 主表建档时间 */
  parameterSetCreatedAt?: string | null
  /** 版本 Id（发布 / 停用 / 校验 / 保存端点的键） */
  versionId: number
  /** 版本码（如 `PS-PROD-V1.0`） */
  versionCode: string
  /** **版本真实状态**（DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED） */
  status: string
  /** 同族默认版本标记 */
  isDefault?: boolean
  /** 生效起（发布时后端写入的发布时间；未发布 = 空） */
  effectiveFrom?: string | null
  /** 生效止（停用时后端写入的停用时刻；已发布未停用 = 空） */
  effectiveTo?: string | null
  /** 发布时间 */
  publishedAt?: string | null
  /** 发布人 */
  publishedBy?: string | null
  /** 版本建档时间 */
  createdAt?: string | null
}

/**
 * 前端参数集列表行（**一版一行**）
 *
 * 行来源两处：① 新端点版本行（每版本一行，状态真实）② 旧端点里「一条版本都没有」的主表行。
 * ②不可省：新端点是 INNER JOIN，**无版本的主表不会出现在其中**
 * （dev 实测 892 个参数集主表里只有 16 个有版本）⇒ 纯切新端点会让这些主表
 * 连同「未配版本」筛选与「删除参数集」入口一起消失 ⇒ 功能回退。
 */
export interface ParameterSetListRowDto {
  /** 行唯一键：版本行 = `v{versionId}`；无版本主表行 = `n{parameterSetId}` */
  rowKey: string
  parameterSetId: number
  parameterSetCode: string
  parameterSetName: string
  /** 主表说明 */
  description?: string
  /** 主表启停 */
  isActive: boolean
  /** 建档时间（主表或版本，取可得的那个） */
  createdAt: string
  /** 该行是否有版本 */
  hasVersion: boolean
  /** 版本 Id（仅版本行） */
  versionId?: number
  /** 版本码（仅版本行） */
  versionCode?: string
  /** 版本真实状态（仅版本行） */
  versionStatus?: RuleStatus
  /** 本参数集「最新已发布」版本码（主表级副信息） */
  currentVersionCode?: string
  /** 本参数集当前草稿版本码（主表级副信息） */
  draftVersionCode?: string
  /** 最近变更（主表级；来自版本备注） */
  lastChangeReason?: string
  /** 发布时间（版本行 = 该版本发布时间；无版本行 = 主表最近一次发布时间） */
  publishedAt?: string
}
