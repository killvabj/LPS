/**
 * APS V1 4号位 — 规则集（RuleSet）维护 API
 *
 * @owner 3号位（RuleSet 治理：版本增改 / Diff / 发布 / 停用 / 校验）
 * @see 4号位文档第 14 节（页面 8）
 *
 * ⚠️ 2026-10-22 结构性更正（用户提问触发，DDL + 后端实体 + dev 实测三处一致）：
 *   **规则集与参数集是两条平行链，不是父子关系。**
 *     RuleSet ──1:N──> RuleSetVersion       （内容：需求优先级 + 换型规则子块）
 *     ParameterSet ──1:N──> ParameterSetVersion（内容：5 主题 JSON）
 *   两条链只在 StrategyProfileVersion 上会合。
 *   ⇒ 本模块**只管规则集**；参数集在 `./parameterSet.ts`。
 *
 * 关键契约事实（都经过 dev 实测，不是猜的）：
 *  - publish / disable 端点返回的是**一句文本**（`ApiResponse.Ok("...已发布")`），
 *    没有 newVersion / historicalVersions 这类结构化结果 ⇒ 前端不自造，改用「刷新后读真状态」
 *  - validate 端点返回 `PublishValidationResult`（isValid / errors / warnings / validatedAt），
 *    **不是** `{status:'PASSED'|'FAILED'}` ⇒ 原前端的 status 字段是自造的
 *  - 后端**没有** fork 端点，也没有 SourceRuleSetVersionId 字段 ⇒ fork 是前端职责：
 *    GET 源版本内容 → POST 新版本
 */

import { apsHttp } from './http'
import type {
  DisableRuleInput,
  VersionDiffResultDto,
  RuleSetDetailDto,
  RuleSetSummaryDto,
  RuleVersionHistoryDto,
  RoleKey,
  RuleSetVersion,
  RuleSetMainDto,
  CreateRuleSetInput,
  UpdateRuleSetInput,
  CreateRuleSetDraftInput,
  UpdateRuleSetDraftInput,
  PublishGovernanceInput,
  ForkDraftResult
} from './types'
import { normalizeRuleStatus, pickCurrentPublished } from './types/common'
import { parsePrioritySegments } from './prioritySegment'
import type { PublishValidationResultDto } from './types/strategyProfile'

/* ===== 后端原始形状 ===== */

/**
 * 后端 RuleSetVersion 实体（`lps/LPS.APS.Core/Entities/Aps/RuleSetVersion.cs`）
 *
 * ⚠️ 没有 ParameterSetVersionId（2026-10-22 核实：DDL 08:9042 / 实体 / dev 实测三处一致）
 */
export interface BackendRuleSetVersion {
  id: number
  ruleSetId: number
  versionCode: string
  status: string
  isDefault?: boolean
  effectiveFrom?: string | null
  effectiveTo?: string | null
  publishedAt?: string | null
  publishedBy?: string | null
  approvedAt?: string | null
  approvedBy?: string | null
  createdAt?: string | null
  createdBy?: string | null
  remarks?: string | null
  /** 需求优先级段位 JSON（读路径由 ContentSnapshotJson 投影而来） */
  demandPriorityJson?: string | null
  /** 换型规则子块 JSON（读路径由 ContentSnapshotJson 投影而来） */
  setupTransitionRulesJson?: string | null
}

/**
 * 构造 fork 时随新版本提交的换型规则子块载体
 *
 * 后端 `EnsureRuleSetNormalized`（GovernanceVersionService.cs:1353-1376）：
 *   - `DemandPriorityJson` 非空 ⇒ **全量重建** ContentSnapshotJson
 *   - 重建时从入参 `contentSnapshotJson` 里提取 `SetupTransitionRules` 子块一并保留
 *   - 读路径 `ProjectRuleSetContent`（:1453）又把两个子块投影回主题 JSON 字段
 * ⇒ fork 时必须把换型规则子块包进快照载体一起带走，否则新建的草稿会丢掉它。
 */
export function buildForkContentSnapshot(
  setupTransitionRulesJson?: string | null
): string | undefined {
  if (!setupTransitionRulesJson) return undefined
  try {
    return JSON.stringify({ SetupTransitionRules: JSON.parse(setupTransitionRulesJson) })
  } catch {
    return undefined
  }
}

/* ===== Rule Set List ===== */

export const ruleApi = {
  /** RuleSet 列表（页面 8 入口）
   *  v1.4 §二十：3号位 GovernanceController 列表走 /api/governance/rule-sets
   *
   *  真实模式字段映射：
   *  - 后端返回精简主表 + 版本摘要（3号位 R2 `OUTER APPLY`）
   *  - ⚠️ `Program.cs:94` 设了 `JsonIgnoreCondition.WhenWritingNull`：RuleSetVersion 表里
   *    **一条版本都没有**时，版本摘要那几个 key 会整个从 JSON 里消失（不是 null，是没有）。
   *    ⇒ `status` 缺失 = 无任何版本（hasVersion=false），绝不能退回 isActive 派生
   *  - 前端 RuleSetSummaryDto 的 domainKey 恒为 'ALL'（线上无规则集↔域关联，
   *    RuleSet 表无 Domain 外键，0号位 裁决未下 ⇒ 不编造真值）
   */
  async listRuleSets(): Promise<RuleSetSummaryDto[]> {
    type BackendRuleSet = {
      id: number
      ruleSetCode: string
      ruleSetName: string
      description?: string
      isActive?: boolean
      createdAt?: string
      createdBy?: string
      /** 3号位 R2：有版本行时才有；无版本则整个 key 缺失（WhenWritingNull） */
      status?: string | null
      currentVersionCode?: string | null
      draftVersionCode?: string | null
      lastChangeReason?: string | null
      publishedAt?: string | null
    }
    const raw = await apsHttp.get<BackendRuleSet[]>({ url: '/api/governance/rule-sets' })
    return raw.map((r) => {
      const hasVersion = r.status != null && String(r.status).trim() !== ''
      return {
        ruleSetId: r.id,
        ruleSetCode: r.ruleSetCode,
        ruleSetName: r.ruleSetName,
        createdAt: (r.createdAt ?? '') as RuleSetSummaryDto['createdAt'],
        // ⚠️ 占位字段：后端没有规则集↔域关联（DDL 08:9025 无 Domain 外键），
        //    0号位 裁决未下 ⇒ 统一按「无域信息」处理，不编造真实域。
        domainKey: 'ALL' as RuleSetSummaryDto['domainKey'],
        // 版本码直连后端（原先恒填 0 ⇒ 页面「当前版本」永远是「—」）
        currentVersionCode: r.currentVersionCode ?? undefined,
        draftVersionCode: r.draftVersionCode ?? undefined,
        hasVersion,
        // 主表启停（Q4：只关主表、不级联版本）——与版本状态是两件事，单独保留
        isActive: r.isActive !== false,
        description: r.description ?? undefined,
        // 有版本 → 用后端真状态；无版本 → 不谎报 PUBLISHED（列表按 hasVersion 统一显示「未配版本」）
        status: hasVersion
          ? normalizeRuleStatus(r.status)
          : ((r.isActive === false ? 'DISABLED' : 'DRAFT') as RuleSetSummaryDto['status']),
        lastChangeReason: r.lastChangeReason ?? r.description,
        publishedAt: r.publishedAt
          ? (r.publishedAt as RuleSetSummaryDto['publishedAt'])
          : undefined,
        retiredAt: undefined
      }
    })
  },

  /** 规则集版本列表（GET /api/governance/rule-set/{ruleSetId}/versions）
   *  权限 aps.rule.view；返回裸 RuleSetVersion[]（含 demandPriorityJson 投影）
   */
  async listRuleSetVersions(ruleSetId: number): Promise<BackendRuleSetVersion[]> {
    return apsHttp.get<BackendRuleSetVersion[]>({
      url: `/api/governance/rule-set/${ruleSetId}/versions`
    })
  },

  /** 取单条 RuleSetVersion（GET /api/governance/rule-set/version/{versionId}） */
  async getRuleSetVersion(versionId: number): Promise<BackendRuleSetVersion> {
    return apsHttp.get<BackendRuleSetVersion>({
      url: `/api/governance/rule-set/version/${versionId}`
    })
  },

  /**
   * RuleSet 详情（版本摘要 + 需求优先级段位 + 历史版本）
   *
   * ⚠️ 2026-10-22 重写：**去掉全部自造占位对象**
   *  - 原实现返回 `parameterSet: {...}`、`strategyProfile: {...}`、`prioritySegments: []`、
   *    `history: []`（首选路径）、`summary.currentVersion = 1`、
   *    `summary.validationStatus = 'PASSED'`、`summary.createdAt = now`
   *    —— 这些后端**根本没有**，页面显示的是编的值。
   *  - 现在只发两个真实请求：
   *      GET /api/governance/rule-set/{id}/versions   → 版本摘要列表（历史 + 当前/草稿版本）
   *      GET /api/governance/rule-set/version/{vid}   → 版本内容（需求优先级 / 换型规则）
   *    规则集**代码/名称**只存在于主表列表，故 summary 留空，由 store 用列表行回填。
   */
  async getRuleSet(ruleSetId: number): Promise<RuleSetDetailDto> {
    const versions = await this.listRuleSetVersions(ruleSetId)
    if (!Array.isArray(versions) || versions.length === 0) {
      // 列表说有版本、点进来却一条都没有（两次请求之间数据被改）。
      // 不外露内部 ID —— 用户看到的应该是「这个规则集没有可用版本」
      throw new Error('该规则集没有可用版本')
    }
    // 多条 PUBLISHED 并存时取**最新生效**的一条（`find` 会命中已到期的旧版）
    const published = pickCurrentPublished(versions)
    const draft = versions.find((v) => normalizeRuleStatus(v.status) === 'DRAFT')
    const history: RuleVersionHistoryDto[] = versions.map((v) => ({
      ruleSetId,
      versionCode: v.versionCode ?? '',
      versionId: v.id,
      status: normalizeRuleStatus(v.status),
      changeReason: v.remarks ?? '',
      publishedAt: v.publishedAt ?? undefined,
      publishedBy: v.publishedBy ?? '',
      // 「当前生效」只认**唯一一条** PUBLISHED（后端允许多条 PUBLISHED 并存 ⇒ 不能按状态一刀切）
      isCurrent: !!published && v.id === published.id,
      effectiveFrom: v.effectiveFrom ?? undefined,
      effectiveTo: v.effectiveTo ?? undefined
    }))
    /**
     * 内容取「草稿版本」优先，没有草稿才取已发布版本。
     *
     * ⚠️ 2026-10-22 更正：原先恒取已发布版本 ⇒ 一个规则集同时有「已发布 + 草稿」时，
     *   页面显示的是**旧内容**、状态却是草稿（可点发布）—— 会出现「发布的不是我看到的」。
     *   现按「待发布的草稿优先」取，并回传 `contentVersionCode` 让页面标明是哪一版。
     */
    const contentSource = draft ?? published ?? versions[0]
    const full = await this.getRuleSetVersion(contentSource.id)
    return {
      summary: {
        ruleSetId,
        ruleSetCode: '',
        ruleSetName: '',
        domainKey: 'ALL' as RuleSetSummaryDto['domainKey'],
        currentVersionCode: published?.versionCode ?? undefined,
        draftVersionCode: draft?.versionCode ?? undefined,
        // 有草稿 ⇒ 页面处在「草稿待发布」状态（发布按钮据此点亮）
        status: draft ? 'DRAFT' : normalizeRuleStatus(contentSource.status),
        hasVersion: true,
        createdAt: contentSource.createdAt ?? '',
        publishedAt: published?.publishedAt ?? undefined
      },
      domainKey: 'ALL' as RuleSetSummaryDto['domainKey'],
      // 需求优先级段位：后端只给 demandPriorityJson（段顺序/名称/条件/排序键）
      prioritySegments: parsePrioritySegments(full.demandPriorityJson),
      contentVersionCode: contentSource.versionCode ?? '',
      contentVersionId: contentSource.id,
      contentVersionRemarks: full.remarks ?? '',
      history
    }
  },

  /** Rule Diff（发布前预览）
   *  v1.4 §二十：3号位 后端走 /api/governance/rule-set/version/diff?sourceVersionId=&targetVersionId=
   *  不需要 ruleSetId（versionId 全局唯一）
   */
  async diffVersions(base: number, target: number): Promise<VersionDiffResultDto> {
    return apsHttp.get<VersionDiffResultDto>({
      url: '/api/governance/rule-set/version/diff',
      params: { sourceVersionId: base, targetVersionId: target }
    })
  },

  /** 提交 DRAFT 校验（U22：发布前必走）
   *  后端 GET /api/governance/rule-set/version/{versionId}/validate
   *
   *  ⚠️ 返回的是 `PublishValidationResult`（isValid / errors / warnings / validatedAt），
   *    **不是** `{status:'PASSED'|'FAILED', message}`。
   */
  async validateDraft(versionId: number): Promise<PublishValidationResultDto> {
    return apsHttp.get<PublishValidationResultDto>({
      url: `/api/governance/rule-set/version/${versionId}/validate`
    })
  },

  /** 发布规则集版本（POST /api/governance/rule-set/version/{versionId}/publish）
   *
   *  ⚠️ 后端返回**一句文本**（无结构化结果）⇒ 本方法不返回结果对象，
   *    调用方发布后应重新拉取版本列表读真状态。
   *  权限 aps.rule.publish
   */
  async publishRuleSet(input: PublishGovernanceInput): Promise<void> {
    await apsHttp.post<null>({
      url: `/api/governance/rule-set/version/${input.versionId}/publish`,
      data: { changeReason: input.changeReason }
    })
  },

  /** 停用规则集版本（POST /api/governance/rule-set/version/{versionId}/disable）
   *  ⚠️ 后端返回**一句文本**；权限 aps.rule.edit
   */
  async disableRuleSet(input: DisableRuleInput): Promise<void> {
    await apsHttp.post<null>({
      url: `/api/governance/rule-set/version/${input.versionId}/disable`,
      data: { reason: input.reason }
    })
  },

  /* ====================================================================== */
  /* v1.4 §二十 写维护：RuleSet DRAFT 全套端点                                   */
  /* ====================================================================== */

  /**
   * 新建规则集主表（POST /api/governance/rule-set）
   *
   * 2026-10-22 3号位 补齐：此前规则集**没有任何主表写端点**，页面上无法新建规则集。
   * 契约（3号位 回执 §四）：
   *  - 入参只认 RuleSetCode / RuleSetName / Description；IsActive 由服务端强制 true
   *  - Code 全库唯一（大小写不敏感），冲突返回 400
   *  - 返回创建后的实体（含 Id）
   * 权限 aps.rule.edit
   */
  async createRuleSet(input: CreateRuleSetInput): Promise<RuleSetMainDto> {
    return apsHttp.post<RuleSetMainDto>({
      url: '/api/governance/rule-set',
      data: input
    })
  },

  /**
   * 删除规则集（DELETE /api/governance/rule-set/{id}）
   *
   * ⚠️ 2026-10-25 3号位 R1：端点已落码，**dev 已实测生效**
   *   （`DELETE /api/governance/rule-set/999999` → 400「规则集不存在：999999」，路由已注册）。
   *   但 R1 是对冻结口径 Q5「不给删除、一律停用」的**例外翻案**，治理上仍待 0号位 放行。
   *
   * 后端拒绝条件（`RuleSetGovernanceService.DeleteRuleSetAsync:106-132`）：
   *  - 该族存在任何 PUBLISHED 版本 ⇒ 400「该规则集已发布过，不能删除（请先停用版本）」
   *  - 任一版本被策略包引用 ⇒ 400「该规则集存在被策略包引用的版本，不能删除」
   * 成功 ⇒ 200 + `ApiResponse<bool>`（事务内连带删全部 DRAFT 版本 + 主表行，并写审计 Delete）
   * 权限 aps.rule.edit
   */
  async deleteRuleSet(ruleSetId: number): Promise<void> {
    await apsHttp.delete<boolean>({
      url: `/api/governance/rule-set/${ruleSetId}`
    })
  },

  /**
   * 编辑规则集主表（PUT /api/governance/rule-set/{id}）
   *
   *  - RuleSetCode **必须原样回传**（大小写差异也会被后端判 400「一经创建不可变更」）
   *  - IsActive 由请求体决定 ⇒ 前端每次都要显式传，否则开关状态会被误写
   *  - Q4：IsActive=false 只关主表，不级联版本
   * 权限 aps.rule.edit
   */
  async updateRuleSet(input: UpdateRuleSetInput): Promise<RuleSetMainDto> {
    return apsHttp.put<RuleSetMainDto>({
      url: `/api/governance/rule-set/${input.ruleSetId}`,
      data: {
        ruleSetCode: input.ruleSetCode,
        ruleSetName: input.ruleSetName,
        description: input.description,
        isActive: input.isActive
      }
    })
  },

  /** 新建 RuleSet DRAFT（POST /api/governance/rule-set/version）
   *  后端强制置 Status=DRAFT、治理字段清空（CreateRuleSetVersionAsync :1788）
   *  权限 aps.rule.edit
   */
  async createRuleSetDraft(input: CreateRuleSetDraftInput): Promise<RuleSetVersion> {
    return apsHttp.post<RuleSetVersion>({
      url: '/api/governance/rule-set/version',
      data: input
    })
  },

  /** 更新 RuleSet DRAFT（PUT /api/governance/rule-set/version/{versionId}）
   *  后端实现：冻结治理字段 + 既有 ContentSnapshotJson 回填，其余字段整对象落库
   *  （UpdateRuleSetVersionAsync :1828）
   */
  async updateRuleSetDraft(input: UpdateRuleSetDraftInput): Promise<RuleSetVersion> {
    return apsHttp.put<RuleSetVersion>({
      url: `/api/governance/rule-set/version/${input.versionId}`,
      data: input.body
    })
  },

  /**
   * Fork 一条规则集版本（基于已发布/已停用版本新建 DRAFT）
   *
   * ⚠️ 2026-10-22 重写（这是原实现 100% 失败的 P0）：
   *  - 原实现读 `sourceRs.parameterSetVersionId` 再 GET `/parameter-set/version/{id}`。
   *    **该字段在 DDL 08:9042、后端实体、dev 实测响应里都不存在** ⇒ 拼出
   *    `/api/governance/parameter-set/version/undefined` ⇒ dev 实测 HTTP 422
   *    ⇒ 【+ 新建草稿】在真实模式下 100% 失败（不是偶发）。
   *  - 原实现还「双发」（POST 规则集版本 + POST 参数集版本），
   *    凭空造出「规则集版本 1:1 参数集版本」的模型 —— 冻结模型不支持：
   *    两族**只在 StrategyProfileVersion 上会合**（DDL 08:9132-9133）。
   *
   * 正确语义（冻结红线「已发布版本不可原地修改」）：
   *  - 后端**没有** fork 端点，也没有 SourceRuleSetVersionId 字段 ⇒ fork 是前端职责
   *  - 内容复制：需求优先级（demandPriorityJson）+ 换型规则子块（SetupTransitionRules）
   */
  async forkDraft(
    sourceRuleSetVersionId: number,
    newVersionCode: string,
    remarks?: string
  ): Promise<ForkDraftResult> {
    // 1. GET 源版本（读路径已把两个内容子块投影出来）
    const sourceRs = await this.getRuleSetVersion(sourceRuleSetVersionId)
    // 2. 组装内容快照：后端 Create 会用 demandPriorityJson 重建快照，
    //    并从 contentSnapshotJson 里取 SetupTransitionRules 子块保留
    const contentSnapshotJson = buildForkContentSnapshot(sourceRs.setupTransitionRulesJson)
    // 3. POST 新版本（后端强制 Status=DRAFT、治理字段清空）
    const ruleSetDraft = await this.createRuleSetDraft({
      ruleSetId: sourceRs.ruleSetId,
      versionCode: newVersionCode,
      demandPriorityJson: sourceRs.demandPriorityJson ?? undefined,
      contentSnapshotJson,
      remarks
    })
    return { ruleSetDraft }
  }
}

/** 客户端判断：当前 actor 是否拥有某角色（mock 模式用固定 actor，真实模式由 JWT 注入）
 *  v1.2 DDL：RULE_ADMIN + RULE_PUBLISHER 合并为单一 aps.admin.aps 角色
 */
export const mockActor: { name: string; roles: RoleKey[] } = {
  name: 'rule-admin',
  roles: ['aps.admin.aps']
}
