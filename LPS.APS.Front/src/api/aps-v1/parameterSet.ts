/**
 * APS V1 4号位 — 参数集（ParameterSet）维护 API
 *
 * @owner 3号位（ParameterSet 治理）
 *
 * ⚠️ 2026-10-22 新增：参数集从规则集页面里**独立出来**。
 *   原先前端把参数集当成「规则集下的参数集」，在规则集页里维护 —— 该模型在冻结 DDL 里
 *   不存在（RuleSet 表无 ParameterSetVersionId；两族只在 StrategyProfileVersion 会合）。
 *
 * 关键契约事实（dev 实测）：
 *  - 5 主题 JSON（lock/supply/procurement/solverStrategy/candidateGuardrail）由
 *    `GET /parameter-set/version/{id}` 以**字符串**返回，前端反序列化成对象编辑、PUT 时序列化回去
 *  - publish / disable 返回**一句文本**，无结构化结果 ⇒ 前端不造结果对象
 *  - validate 返回 `PublishValidationResult`（isValid / errors / warnings / validatedAt）
 *  - 后端**没有** fork 端点 ⇒ fork = GET 源版本 → POST 新版本（5 主题 JSON 原样带走）
 *
 * 2026-11-18：跨厂LT 标准参数（`$.CrossFactoryLeadTimeRules`）改为**通用参数集版本的第 6 号内容子块**，
 *   与 5 主题**共用同一条** `GET /version/{id}` / `PUT /version/{id}`；
 *   原拟的专属端点（`…/cross-factory-lead-time-rules`）后端已删除（调用将 404）⇒ 本文件已移除该路径常量。
 *   —— 依据 3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》。
 */

import { apsHttp } from './http'
import type {
  ParameterSetVersion,
  CreateParameterSetDraftInput,
  UpdateParameterSetDraftInput,
  PublishGovernanceInput,
  VersionDiffResultDto
} from './types'
import type {
  ParameterSetSummaryDto,
  ParameterSetDetailDto,
  ParameterSetVersionHistoryDto,
  ParameterSetVersionRowDto,
  ParameterSetListRowDto
} from './types/parameterSet'
import { normalizeRuleStatus, pickLatestPublished } from './types/common'
import type { PublishValidationResultDto } from './types/strategyProfile'

/** 后端 ParameterSet 主表裸行（列表端点 LEFT JOIN 版本摘要；新建端点只回主表字段） */
export interface BackendParameterSet {
  id: number
  parameterSetCode: string
  parameterSetName: string
  description?: string | null
  isActive?: boolean
  createdAt?: string
  createdBy?: string | null
  updatedAt?: string | null
  updatedBy?: string | null
  status?: string | null
  currentVersionCode?: string | null
  draftVersionCode?: string | null
  lastChangeReason?: string | null
  publishedAt?: string | null
}

/** 新建参数集主表入参（Id/时间戳/IsActive 由服务端维护） */
export interface CreateParameterSetInput {
  parameterSetCode: string
  parameterSetName: string
  description?: string
}

/** 更新参数集主表入参（PUT /api/governance/parameter-set/{id}）
 *
 *  编码必须原样回传（后端校验不可变更，改码返 400）；`isActive=false` 只停用主表不级联版本。
 */
export interface UpdateParameterSetInput {
  id: number
  parameterSetCode: string
  parameterSetName: string
  description?: string | null
  isActive: boolean
}

/** 后端 ParameterSetVersion 裸形状（5 主题 JSON 字符串）
 *
 *  注：`status` 用基类类型（后端实际是裸字符串，消费处一律走 `normalizeRuleStatus` 归一）。
 */
export interface BackendParameterSetVersion extends ParameterSetVersion {
  id: number
  parameterSetId: number
  versionCode: string
  isDefault?: boolean
  effectiveFrom?: string | null
  effectiveTo?: string | null
  publishedAt?: string | null
  publishedBy?: string | null
  createdAt?: string | null
  createdBy?: string | null
  remarks?: string | null
  lockJson?: string | null
  supplyJson?: string | null
  procurementJson?: string | null
  solverStrategyJson?: string | null
  candidateGuardrailJson?: string | null
  /**
   * 跨厂LT 标准参数子块的原始 JSON（2026-11-18 通用参数接口；缺块 / 空 ⇒ `null`）
   *
   * 依据 3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》。
   */
  crossFactoryLeadTimeRulesJson?: string | null
}

/** fork 时要原样带走的 5 个主题 JSON */
const THEME_JSON_KEYS = [
  'lockJson',
  'supplyJson',
  'procurementJson',
  'solverStrategyJson',
  'candidateGuardrailJson'
] as const

/**
 * 把「新端点版本行」与「旧端点无版本主表行」合并成前端列表行（**一版一行**）
 *
 * ⚠️ 2026-11-11（3号位 扩围加函）：
 *   新端点 `GET /parameter-sets/versions` 是主表 INNER JOIN 版本 ⇒ **一条版本都没有的主表
 *   不会出现在它里面**（dev 实测 892 个参数集主表里只有 16 个有版本）。纯切新端点会让这些主表
 *   连同页面的「未配版本」筛选与「删除参数集」入口一起消失 ⇒ 功能回退。
 *   故列表 = 新端点版本行（主表有版本时全部版本各占一行）+ 旧端点的「无版本主表」行。
 *
 *  - 版本行：`versionStatus` = 该版本**真实状态**（不再被同族新草稿覆盖）
 *  - 主表级副信息（最新已发布版本码 / 草稿版本码）从版本行派生，版本 Id 大者为准
 */
export function mergeParameterSetListRows(
  versionRows: ParameterSetVersionRowDto[],
  mains: ParameterSetSummaryDto[]
): ParameterSetListRowDto[] {
  const latestPublished = new Map<number, { id: number; code: string }>()
  const latestDraft = new Map<number, { id: number; code: string }>()
  for (const r of versionRows) {
    const st = normalizeRuleStatus(r.status)
    const bucket = st === 'PUBLISHED' ? latestPublished : st === 'DRAFT' ? latestDraft : null
    if (!bucket) continue
    const cur = bucket.get(r.parameterSetId)
    if (!cur || r.versionId > cur.id)
      bucket.set(r.parameterSetId, { id: r.versionId, code: r.versionCode })
  }

  const rows: ParameterSetListRowDto[] = versionRows.map((r) => {
    const versionStatus = normalizeRuleStatus(r.status)
    return {
      rowKey: `v${r.versionId}`,
      parameterSetId: r.parameterSetId,
      parameterSetCode: r.parameterSetCode,
      parameterSetName: r.parameterSetName,
      description: r.description ?? undefined,
      isActive: r.isActive !== false,
      createdAt: r.createdAt ?? r.parameterSetCreatedAt ?? '',
      hasVersion: true,
      versionId: r.versionId,
      versionCode: r.versionCode,
      versionStatus,
      currentVersionCode: latestPublished.get(r.parameterSetId)?.code,
      draftVersionCode: latestDraft.get(r.parameterSetId)?.code,
      publishedAt: r.publishedAt ?? undefined
    }
  })

  // 旧端点里「一条版本都没有」的主表 ⇒ 补一行「未配版本」（主表自身信息取旧端点）
  const withVersion = new Set(versionRows.map((r) => r.parameterSetId))
  for (const m of mains) {
    if (m.hasVersion || withVersion.has(m.parameterSetId)) continue
    rows.push({
      rowKey: `n${m.parameterSetId}`,
      parameterSetId: m.parameterSetId,
      parameterSetCode: m.parameterSetCode,
      parameterSetName: m.parameterSetName,
      description: m.description,
      isActive: m.isActive !== false,
      createdAt: m.createdAt ?? '',
      hasVersion: false,
      lastChangeReason: m.lastChangeReason,
      publishedAt: m.publishedAt
    })
  }
  return rows
}

export const parameterSetApi = {
  /** 参数集列表（GET /api/governance/parameter-sets；权限 aps.parameter.view）
   *
   *  后端已从裸实体换型为 ParameterSetListItemDto（与 RuleSetListItemDto 同构），
   *  版本摘要字段来自 ParameterSetVersion 的 OUTER APPLY；无版本行时整个 key 缺失。
   */
  async listParameterSets(): Promise<ParameterSetSummaryDto[]> {
    const raw = await apsHttp.get<BackendParameterSet[]>({
      url: '/api/governance/parameter-sets'
    })
    return raw.map((p) => {
      const hasVersion = p.status != null && String(p.status).trim() !== ''
      return {
        parameterSetId: p.id,
        parameterSetCode: p.parameterSetCode,
        parameterSetName: p.parameterSetName,
        description: p.description ?? undefined,
        isActive: p.isActive ?? true,
        createdAt: (p.createdAt ?? '') as ParameterSetSummaryDto['createdAt'],
        currentVersionCode: p.currentVersionCode ?? undefined,
        draftVersionCode: p.draftVersionCode ?? undefined,
        hasVersion,
        status: hasVersion
          ? normalizeRuleStatus(p.status)
          : ((p.isActive === false ? 'DISABLED' : 'DRAFT') as ParameterSetSummaryDto['status']),
        lastChangeReason: p.lastChangeReason ?? p.description ?? undefined,
        publishedAt: p.publishedAt
          ? (p.publishedAt as ParameterSetSummaryDto['publishedAt'])
          : undefined
      }
    })
  },

  /**
   * 新建参数集主表（POST /api/governance/parameter-set；权限 aps.parameter.edit）
   *
   * 3号位 2026-10-25 已落码（含 `PUT /parameter-set/{id}`）：编码唯一冲突 400（大小写不敏感）、
   * 编码一经创建不可变更；建壳后由 store 自动跟建 `<编码>-V1` 空白 DRAFT。
   */
  async createParameterSet(input: CreateParameterSetInput): Promise<BackendParameterSet> {
    return apsHttp.post<BackendParameterSet>({
      url: '/api/governance/parameter-set',
      data: input
    })
  },

  /**
   * 参数集列表（新端点·**主子表联查一版一行**）
   *
   * ⚠️ 2026-11-11（3号位 扩围加函）：`GET /api/governance/parameter-sets/versions`
   *   - 权限 aps.parameter.view；入参 activeOnly（三态）/ keyword / status / name / page / pageSize
   *     —— 本方法**不传 page/pageSize** ⇒ 后端 `ResolvePaging` 返回 (null,null) ⇒ 全量
   *   - 每主表 N 个版本 ⇒ N 行，`status` 是**版本真实状态**，不再被同族新草稿覆盖
   *   - 排序：主表 Id DESC；每主表内版本 Id ASC
   */
  async listParameterSetVersionRows(): Promise<ParameterSetVersionRowDto[]> {
    return apsHttp.get<ParameterSetVersionRowDto[]>({
      url: '/api/governance/parameter-sets/versions'
    })
  },

  /**
   * 参数集列表（前端列表源）：新端点版本行 + 旧端点「无版本主表」行
   *
   * 见 `mergeParameterSetListRows` 的注释（为什么不能纯切新端点）。
   * 旧端点 `GET /parameter-sets` 保留用途。
   */
  async listParameterSetRows(): Promise<ParameterSetListRowDto[]> {
    const [versionRows, mains] = await Promise.all([
      this.listParameterSetVersionRows(),
      this.listParameterSets()
    ])
    return mergeParameterSetListRows(versionRows, mains)
  },

  /**
   * 更新参数集主表（PUT /api/governance/parameter-set/{id}；权限 aps.parameter.edit）
   *
   * 编码不可变（改码 400）；`isActive=false` 只停用主表，不级联已有版本状态。
   */
  async updateParameterSet(input: UpdateParameterSetInput): Promise<BackendParameterSet> {
    return apsHttp.put<BackendParameterSet>({
      url: `/api/governance/parameter-set/${input.id}`,
      data: {
        parameterSetCode: input.parameterSetCode,
        parameterSetName: input.parameterSetName,
        description: input.description,
        isActive: input.isActive
      }
    })
  },

  /**
   * 物理删除参数集整族（DELETE /api/governance/parameter-set/{id}）
   *
   * 最窄口径（2026-10-30 恢复）：无任何 PUBLISHED 版本才可删，连带删 DRAFT。
   * 含 PUBLISHED ⇒ 400；版本被策略包版本引用 ⇒ 400「存在被策略包引用的版本，不能删除」。
   * 硬删（单事务）+ 审计前置。权限 aps.parameter.edit
   */
  async deleteParameterSet(parameterSetId: number): Promise<void> {
    await apsHttp.delete<boolean>({ url: `/api/governance/parameter-set/${parameterSetId}` })
  },

  /** 参数集版本列表（GET /api/governance/parameter-set/{id}/versions） */
  async listParameterSetVersions(parameterSetId: number): Promise<BackendParameterSetVersion[]> {
    return apsHttp.get<BackendParameterSetVersion[]>({
      url: `/api/governance/parameter-set/${parameterSetId}/versions`
    })
  },

  /** 取单条 ParameterSetVersion（GET /api/governance/parameter-set/version/{id}） */
  async getParameterSetVersion(versionId: number): Promise<BackendParameterSetVersion> {
    return apsHttp.get<BackendParameterSetVersion>({
      url: `/api/governance/parameter-set/version/${versionId}`
    })
  },

  /** 参数集详情（版本摘要 + 当前版本内容 + 历史） */
  /**
   * 参数集详情（版本摘要 + 5 主题内容 + 历史版本）
   *
   * ⚠️ 2026-11-11（列表改一版一行）：新增 `versionId` 参数 ——
   *   列表点哪一行就展示哪一版的内容，`summary.status` 取**该版真实状态**。
   *   不传 versionId 时保持旧默认（草稿优先 → 已发布 → 第一条），供内部调用使用。
   */
  async getParameterSet(
    parameterSetId: number,
    versionId?: number
  ): Promise<ParameterSetDetailDto> {
    const versions = await this.listParameterSetVersions(parameterSetId)
    if (!Array.isArray(versions) || versions.length === 0) {
      throw new Error('该参数集没有可用版本')
    }
    // 多条 PUBLISHED 并存时取**最新已发布**的一条（`find` 会命中已到期的旧版）
    const published = pickLatestPublished(versions)
    const draft = versions.find((v) => normalizeRuleStatus(v.status) === 'DRAFT')
    const history: ParameterSetVersionHistoryDto[] = versions.map((v) => ({
      parameterSetId,
      versionCode: v.versionCode ?? '',
      versionId: v.id,
      status: normalizeRuleStatus(v.status),
      changeReason: v.remarks ?? '',
      publishedAt: v.publishedAt ?? undefined,
      publishedBy: v.publishedBy ?? '',
      // 「最新已发布」只认 Id 最大的那一条 PUBLISHED（后端允许多条 PUBLISHED 并存）。
      // ⚠️ 术语：这是「最新已发布」不是「当前有效」（后者按生效期窗口，由 published-version 端点判定）
      isLatestPublished: !!published && v.id === published.id,
      effectiveFrom: v.effectiveFrom ?? undefined,
      effectiveTo: v.effectiveTo ?? undefined
    }))
    /**
     * 内容版本：
     *  - 传了 versionId（列表点某一行的版本）⇒ 就展示那一版；找不到（版本刚被删）才回落默认
     *  - 没传 ⇒ 草稿优先 → 已发布 → 第一条（保持旧默认，供内部调用）
     */
    const contentSource =
      (versionId != null ? versions.find((v) => v.id === versionId) : undefined) ??
      draft ??
      published ??
      versions[0]
    const full = await this.getParameterSetVersion(contentSource.id)
    return {
      summary: {
        parameterSetId,
        parameterSetCode: '',
        parameterSetName: '',
        isActive: true,
        createdAt: contentSource.createdAt ?? '',
        currentVersionCode: published?.versionCode ?? undefined,
        draftVersionCode: draft?.versionCode ?? undefined,
        // 本页展示的那一版的**真实状态**（不再「有草稿就一律 DRAFT」——
        // 那样会把已发布生效的那一版错标成草稿，用户点发布时才发现版本不对）
        status: normalizeRuleStatus(contentSource.status),
        hasVersion: true,
        lastChangeReason: contentSource.remarks ?? undefined,
        publishedAt: published?.publishedAt ?? undefined
      },
      version: full,
      history
    }
  },

  /** 参数集版本 Diff（GET /api/governance/parameter-set/version/diff） */
  async diffVersions(base: number, target: number): Promise<VersionDiffResultDto> {
    return apsHttp.get<VersionDiffResultDto>({
      url: '/api/governance/parameter-set/version/diff',
      params: { sourceVersionId: base, targetVersionId: target }
    })
  },

  /** 发布前校验（GET /api/governance/parameter-set/version/{id}/validate）
   *  返回 `PublishValidationResult`（isValid / errors / warnings / validatedAt）
   */
  async validateDraft(versionId: number): Promise<PublishValidationResultDto> {
    return apsHttp.get<PublishValidationResultDto>({
      url: `/api/governance/parameter-set/version/${versionId}/validate`
    })
  },

  /** 发布参数集版本（POST /api/governance/parameter-set/version/{id}/publish）
   *  ⚠️ 返回一句文本；权限 aps.parameter.publish
   */
  async publishParameterSet(input: PublishGovernanceInput): Promise<void> {
    await apsHttp.post<null>({
      url: `/api/governance/parameter-set/version/${input.versionId}/publish`,
      data: { changeReason: input.changeReason }
    })
  },

  /** 停用参数集版本（POST /api/governance/parameter-set/version/{id}/disable）
   *  ⚠️ 返回一句文本；权限 aps.parameter.edit
   */
  async disableParameterSet(input: { versionId: number; reason: string }): Promise<void> {
    await apsHttp.post<null>({
      url: `/api/governance/parameter-set/version/${input.versionId}/disable`,
      data: { reason: input.reason }
    })
  },

  /** 新建 ParameterSet DRAFT（POST /api/governance/parameter-set/version）
   *  后端强制置 Status=DRAFT、治理字段清空（CreateParameterSetVersionAsync :1886）
   */
  async createParameterSetDraft(input: CreateParameterSetDraftInput): Promise<ParameterSetVersion> {
    return apsHttp.post<ParameterSetVersion>({
      url: '/api/governance/parameter-set/version',
      data: input
    })
  },

  /** 更新 ParameterSet DRAFT（PUT /api/governance/parameter-set/version/{id}）
   *  full-object PUT：5 JSON 字符串必须全量回传（后端五主题归一化会重建快照）
   */
  async updateParameterSetDraft(input: UpdateParameterSetDraftInput): Promise<ParameterSetVersion> {
    return apsHttp.put<ParameterSetVersion>({
      url: `/api/governance/parameter-set/version/${input.versionId}`,
      data: input.body
    })
  },

  /**
   * Fork 一条参数集版本（基于已发布/已停用版本新建 DRAFT）
   *
   * 后端无 fork 端点 ⇒ 前端 GET 源版本 → POST 新版本，**5 个主题 JSON 原样带走**
   * （`EnsureParameterSetNormalized` 会把五主题重建进 ContentSnapshotJson；
   *  BatchPolicy 子块由后端从既有快照回填保留，前端不用管）。
   *
   * ⚠️ 2026-11-18：跨厂LT 子块（`crossFactoryLeadTimeRulesJson`）**一并带走**——
   *   后端 Create 支持带该子块（3号位 v1.1 V1）；不带的话新草稿的快照里就没有这一块
   *   ⇒ 跨厂LT 规则在 fork 出的新版本里凭空消失。
   */
  async forkDraft(
    sourceParameterSetVersionId: number,
    newVersionCode: string,
    remarks?: string
  ): Promise<ParameterSetVersion> {
    const source = await this.getParameterSetVersion(sourceParameterSetVersionId)
    const body: Record<string, unknown> = {
      parameterSetId: source.parameterSetId,
      versionCode: newVersionCode,
      remarks
    }
    for (const key of THEME_JSON_KEYS) {
      const v = source[key]
      if (v) body[key] = v
    }
    if (source.crossFactoryLeadTimeRulesJson) {
      body.crossFactoryLeadTimeRulesJson = source.crossFactoryLeadTimeRulesJson
    }
    return this.createParameterSetDraft(body as unknown as CreateParameterSetDraftInput)
  }
}
