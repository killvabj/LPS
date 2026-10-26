/**
 * 规则集（RuleSet）/ 参数集（ParameterSet）治理 DTO
 *
 * ⚠️ 2026-10-22 结构性更正（依据冻结 DDL + 后端实体 + dev 实测，三处一致）：
 *
 *   冻结模型是**三条平行链**，不是「规则集 1:1 挂参数集」：
 *
 *     RuleSet ──1:N──> RuleSetVersion          （内容：需求优先级 + 换型规则子块）
 *     ParameterSet ──1:N──> ParameterSetVersion （内容：5 主题 JSON）
 *     StrategyProfile ──1:N──> StrategyProfileVersion
 *
 *   两条链**只在 StrategyProfileVersion 上会合**（该表同时持 RuleSetVersionId
 *   与 ParameterSetVersionId 两个外键）。
 *
 *   ⇒ **RuleSetVersion 没有 ParameterSetVersionId 字段**
 *     （DDL `08:9042` / 实体 `RuleSetVersion.cs` / dev `GET /rule-set/version/{id}`
 *     响应键三处一致）——前端原先自造的 1:1 关联已全部撤除。
 *
 * 验收场景参考：
 *  - U21（Priority Segment：页面不出现全局 PriorityScore）
 *  - U22（Rule 发布：新 Version 生成，历史不覆盖）
 *
 * 注意：前端不能直接改 PUBLISHED 字段（4号位文档第 14 节）
 */

import type { DomainKey, IsoDateTime, RoleKey, RuleStatus } from './common'

/* ===== RuleSet ===== */

/** RuleSet 列表项（精简字段） */
export interface RuleSetSummaryDto {
  ruleSetId: number
  ruleSetCode: string
  ruleSetName: string
  /** 适用 Domain（ALL 表示跨域）
   *
   *  ⚠️ 后端 RuleSet 表**没有** Domain 外键（DDL 08:9025）⇒ 该值当前恒为 'ALL'，
   *  是「无域信息」的占位，不是真实归属。0号位 裁决未下（已致函 3号位 追问）。
   */
  domainKey: DomainKey
  /**
   * 当前已发布版本**版本码**（后端 `currentVersionCode`，字符串）
   *
   * ⚠️ 2026-10-22 更正：原先本字段是 `currentVersion: number`，映射时恒填 0，
   * 页面上「当前版本」永远是「—」。后端给的就是版本**码**（如 `RS-DEMO-V2`），
   * 不是自增号，故改成字符串并直接用后端值。
   */
  currentVersionCode?: string
  /** 待发布 DRAFT 版本码（后端 `draftVersionCode`；无草稿时 key 缺失） */
  draftVersionCode?: string
  status: RuleStatus
  /**
   * 是否存在版本行（10-14 增）
   *
   * 后端 `RuleSetListItemDto` 的 Status/CurrentVersionCode 等版本字段来自 RuleSetVersion 的
   * OUTER APPLY，且 `Program.cs:94` 设了 `WhenWritingNull` —— 该 RuleSet **一条版本都没有**时，
   * 这些 key 会整个从 JSON 里消失。故「status 缺失」= 「无任何版本」，是一个独立于 6 态的**正交布尔**，
   * 不新增第 7 个 RuleStatus（v1.4 §二十 6 态是 DDL 冻结契约，不因前端展示需要而扩张）。
   *
   * 用途：无版本的规则集点开必然 404（published-version 无行 → versions 空数组），
   * 列表要如实显示「未配版本」并让点击走空态，不能谎报「已发布」。
   */
  hasVersion: boolean
  /**
   * 主表是否启用（`RuleSet.IsActive`）
   *
   * ⚠️ 2026-10-22 新增：`POST/PUT /rule-set` 补全后，主表停用（IsActive=false）是**只关主表**、
   * 不级联版本（3号位 Q4 裁决）⇒ 列表上的「已发布/草稿」来自**版本**，与主表启停是两件事，
   * 必须单独显示，否则停用了也看不出来。
   *
   * ⚠️ 详情端点（`getRuleSet`）只读版本、拿不到主表 ⇒ 此处**可空**，
   *   由 store 用列表行回填；为 undefined 时页面显示「—」，不猜。
   */
  isActive?: boolean
  /** 主表说明（`RuleSet.Description`，编辑主表时回填用；与版本变更原因是两回事） */
  description?: string
  lastChangeReason?: string
  createdAt: IsoDateTime
  publishedAt?: IsoDateTime
  retiredAt?: IsoDateTime
}

/**
 * RuleSet 详情
 *
 * ⚠️ 2026-10-22 更正：原先这里挂 `parameterSet` / `strategyProfile` 两个**前端自造**的占位对象
 * （后端根本没有「规则集 → 参数集」「规则集 → 策略包」的绑定关系，绑定只发生在
 * StrategyProfileVersion 上），恒为假值。现已整条删除。
 */
export interface RuleSetDetailDto {
  summary: RuleSetSummaryDto
  domainKey: DomainKey
  /** 需求优先级段位（自需求优先级块解析；后端不提供段码/说明，故这两项可空） */
  prioritySegments: PrioritySegmentDto[]
  /**
   * 本页展示的**内容所属版本码**
   *
   * ⚠️ 2026-10-22 新增（避免「看的到底是哪一版」说不清）：
   *   一个规则集可以同时存在「已发布版本」和「草稿版本」，
   *   而 `prioritySegments` 只来自**一个**版本 ⇒ 页面必须标出是哪一版，
   *   否则用户会把草稿内容当成线上生效内容。
   */
  contentVersionCode: string
  /**
   * 内容所属版本的**版本 Id**（保存段位时 PUT /rule-set/version/{id} 的键）
   *
   * ⚠️ 2026-10-22 新增：编辑器只能改「当前展示的那一版」，
   * 必须能唯一定位到版本 Id，不能拿版本码或规则集 Id 顶替。
   */
  contentVersionId: number
  /** 内容所属版本的备注（保存对话框回填，避免整条 PUT 把原备注清空） */
  contentVersionRemarks: string
  history: RuleVersionHistoryDto[]
}

/** RuleSet 单版本历史 */
export interface RuleVersionHistoryDto {
  ruleSetId: number
  /**
   * 版本**码**（后端 `versionCode`，如 `RS-DEMO-V2`）
   *
   * ⚠️ 2026-10-22 更正：原字段是 `version: number`（自增号），后端从未提供，
   * 映射时恒填 0。
   */
  versionCode: string
  /** v1.4 §二十：3 号位 后端 RuleSetVersion.VersionId（全局唯一；publish/disable/validate 端点键） */
  versionId?: number
  status: RuleStatus
  /** 变更原因（后端 `remarks`） */
  changeReason: string
  publishedAt?: IsoDateTime
  publishedBy: string
  /** 是否为当前生效版本 */
  isCurrent: boolean
  /** 生效起（后端 `effectiveFrom`） */
  effectiveFrom?: IsoDateTime
  /** 生效止（后端 `effectiveTo`；空 = 一直有效） */
  effectiveTo?: IsoDateTime
}

/**
 * Priority Segment（不暴露 PriorityScore）
 *
 * ⚠️ 2026-10-22 按后端真实 DTO 重写（`lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs:55` `PrioritySegment`）
 *  - 段内键**只有 6 个**：`SegmentOrder / SegmentName / IsEnabled / MatchConditions / SortFields / StableTieBreakFields`
 *    （该类带 `[JsonUnmappedMemberHandling(Disallow)]`，多写一个键后端直接反序列化失败）
 *  - `CompetitionRound` 是**可选**键（v1.2 B3 新增；缺省 = 未声明轮次，后端投影层 fail-closed）
 *  - **没有段码、没有段说明** ⇒ 这两列页面显示「—」，前端不自造
 */
export interface PrioritySegmentDto {
  /** 段内顺序（1 = 最高） */
  orderInSegment: number
  segmentName: string
  /** 是否启用该段 */
  isEnabled: boolean
  /** 竞争轮次（1/2/3）；未声明时为空（后端按「整块未声明 ⇒ 全按第 1 轮」处理） */
  competitionRound?: number
  /** 命中条件（AND 关系） */
  matchConditions: SegmentMatchConditionDto[]
  /** 段内排序键（依次生效） */
  sortFields: SegmentSortFieldDto[]
  /** 稳定排序键（并列时兜底，保证结果确定） */
  stableTieBreakFields: string[]
}

/** 命中条件一项（后端 `SegmentMatchCondition`：Field/Operator 是**枚举数字**，Value 是标量或数组） */
export interface SegmentMatchConditionDto {
  /** DemandField 枚举值（0=剩余时间 1=延期状态 2=客户等级 3=订单类型 4=PMC保护 5=优先等级 6=交期 7=下单日期） */
  field: number
  /** ConditionOperator 枚举值（0=等于 1=不等于 2=小于 3=小于等于 4=大于 5=大于等于 6=属于） */
  operator: number
  /** 比较值；Operator=In 时是数组 */
  value: unknown
}

/** 段内排序键一项（后端 `SegmentSortField`：Field=DemandField 枚举，Direction=SortDirection 枚举） */
export interface SegmentSortFieldDto {
  field: number
  /** 0=升序 1=降序 */
  direction: number
}

/** 版本差异明细（后端 `LPS.APS.Core/DTOs/Governance/VersionDiffResult.cs` 的 `FieldDiff`）
 *
 * ⚠️ `sourceValue` / `targetValue` 后端声明为 `string?` ⇒ 值以字符串形式给出，
 *   数字/日期已在服务端格式化；`null`/缺键表示该字段在源或目标版本中不存在。
 */
export interface FieldDiff {
  /** 字段名（英文，如 `VersionCode`） */
  fieldName: string
  /** 字段显示名（中文，如「版本编码」）；后端已给，前端优先用它渲染 */
  fieldDisplayName: string
  sourceValue?: string | null
  targetValue?: string | null
  /** 是否真发生变化 —— **列表是全字段平铺，只有 true 的才算差异** */
  isChanged: boolean
}

/** 版本 Diff（`/rule-set/version/diff` 与 `/parameter-set/version/diff` 共用同一形状）
 *
 * 2026-10-19 按 dev 实测重写。此前本类型自拟 `baseVersion/targetVersion/changedParameters/summary`
 *   四组字段，与后端 `VersionDiffResult` **零字段重合** —— Rules.vue「版本对比」Tab 渲染
 *   `summary.addedCount` 会直接抛 TypeError。已按 r13755 + dev 实测对齐。
 *
 * ⚠️ 后端**不返回** added/modified/removed 三分类计数（它只给 `isChanged` 布尔位），
 *   所以「新增/删除」在当前契约下**无从判定**，前端不得自造 —— 只统计 `isChanged === true` 的条数。
 */
export interface VersionDiffResultDto {
  sourceVersionId: number
  targetVersionId: number
  sourceVersionCode: string
  targetVersionCode: string
  /** 实体类型：`RuleSetVersion` / `ParameterSetVersion` —— 前端据此标注这条 diff 说的是哪一族 */
  entityType: string
  fieldDiffs: FieldDiff[]
  comparedAt: IsoDateTime
}

/* ===== Inputs ===== */

/** 发布 Rule 输入
 *
 * ⚠️ 2026-10-22：后端 publish 端点只吃 `versionId` + `{changeReason}`，
 *   且**返回的是一句文本**（`ApiResponse.Ok("规则集版本 {id} 已发布")`），没有结构化结果。
 *   原先本 DTO 里的 `newVersion` / `historicalVersions` / `publishedAt` 全是前端自造的。
 */
export interface PublishRuleInput {
  ruleSetId: number
  /** 待发布版本码（后端 `draftVersionCode`；仅展示用） */
  draftVersionCode?: string
  /** v1.4 §二十：3 号位 后端 /rule-set/version/{versionId}/publish 端点使用 */
  versionId: number
  changeReason: string
  actor: string
  actorRoles: RoleKey[]
}

/** 停用 Rule 输入 */
export interface DisableRuleInput {
  ruleSetId: number
  /** 被停用版本码（后端 `currentVersionCode`；仅展示用） */
  versionCode?: string
  /** v1.4 §二十：3 号位 后端 /rule-set/version/{versionId}/disable 端点使用 */
  versionId: number
  reason: string
  actor: string
  actorRoles: RoleKey[]
}

/* ====================================================================== */
/* v1.4 §二十：ParameterSet / RuleSet 写维护 DTO（B 设计稿 Step 1）           */
/* ====================================================================== */

/**
 * 治理状态别名：与 RuleStatus 同义，专用于治理类版本（RuleSetVersion / ParameterSetVersion）
 * - 6 态：v1.4 已冻结（3 号位 后端 GovernanceVersionStatus 对齐）
 * - 命名独立避免与 SetupRuleStatus 混淆
 */
export type GovernanceStatus = RuleStatus

/**
 * 5 主题 JSON 块 Key（B 设计稿 §1.1）
 *  - lock                锁定策略（Task 锁定/冻结约束）
 *  - supply              供应参数（PI/PO/库存等供应策略）
 *  - procurement         采购参数（前置期/占位等）
 *  - solverStrategy      Solver 策略参数（Split/Overlap/Priority 等）
 *  - candidateGuardrail  Candidate 守门参数（约束/红线）
 */
export type BlockKey = 'lock' | 'supply' | 'procurement' | 'solverStrategy' | 'candidateGuardrail'

export const BLOCK_KEYS: readonly BlockKey[] = [
  'lock',
  'supply',
  'procurement',
  'solverStrategy',
  'candidateGuardrail'
] as const

/** 5 主题 JSON 块容器（反序列化后的对象形态；PUT 时序列化回字符串） */
export type ParameterSetBlocks = Record<BlockKey, Record<string, unknown>>

/**
 * 治理公共字段（PUT 时一律冻结取自原记录，service 强制置 Status=DRAFT 入参忽略）
 * - 与 lps ParameterSetVersion.cs / RuleSetVersion.cs 治理字段对齐
 * - RuleSet / ParameterSet 各自的业务 ID 在子接口补
 */
export interface GovernanceVersionBase {
  /** 后端主键；POST 时可省（后端生成），PUT 时必填 */
  id?: number
  versionCode: string
  status: GovernanceStatus
  /** 以下治理字段后端列均可空 ⇒ GET 会返回 `null`（不是缺失） */
  effectiveFrom?: IsoDateTime | null
  effectiveTo?: IsoDateTime | null
  publishedAt?: IsoDateTime | null
  publishedBy?: string | null
  approvedAt?: IsoDateTime | null
  approvedBy?: string | null
  createdAt?: IsoDateTime | null
  createdBy?: string | null
  /** 变更原因 / 备注 */
  remarks?: string | null
}

/** ParameterSet 治理字段（parameterSetId 是业务外键） */
export interface ParameterSetVersionGovernance extends GovernanceVersionBase {
  parameterSetId: number
}

/**
 * RuleSet 治理字段（ruleSetId 是业务外键）
 *
 * ⚠️ **没有 ParameterSetVersionId**（2026-10-22 更正）
 * 冻结 DDL `08:9042` RuleSetVersion 的列只有 RuleSetId / VersionCode / Status / 生效窗 /
 * 审批发布戳 / 建档戳，外键 `FK_RuleSetVersion_RuleSet → RuleSet(Id)`；
 * 后端实体 `lps/LPS.APS.Core/Entities/Aps/RuleSetVersion.cs` 同样**没有**该字段；
 * dev 实测 `GET /api/governance/rule-set/version/478` 的响应键里也没有它。
 * 规则集与参数集**只在 StrategyProfileVersion 上会合**（`08:9132-9133` 两个 FK）。
 */
export interface RuleSetVersionGovernance extends GovernanceVersionBase {
  ruleSetId: number
}

/** ParameterSet 单版本实体（B 设计稿 §1.1 ParameterSetVersion.cs 对齐） */
export interface ParameterSetVersion extends ParameterSetVersionGovernance {
  /** 5 主题 JSON 字符串（前端取出来反序列化成 Record）；PUT 时序列化回字符串
   *  - 后端列可空 ⇒ GET 可能返回 `null`（空块），不要当 `undefined` 处理 */
  lockJson?: string | null
  supplyJson?: string | null
  procurementJson?: string | null
  solverStrategyJson?: string | null
  candidateGuardrailJson?: string | null
  /** 发版落库的全量快照（前端可读不写） */
  contentSnapshotJson?: string | null
}

/**
 * RuleSet 单版本实体
 *
 * ⚠️ 2026-10-22 更正：原先这里声明了 5 个参数主题 JSON（lockJson/supplyJson/…），
 * 那是**参数集**的字段。规则集版本的内容只有两块（后端实体 `RuleSetVersion.cs`）：
 *  - 需求优先级（`DemandPriorityJson`）
 *  - 换型规则子块（`SetupTransitionRulesJson`，读路径从快照投影而来）
 * 5 块参数在 ParameterSetVersion 上，见「参数集维护」页。
 */
export interface RuleSetVersion extends RuleSetVersionGovernance {
  /** 需求优先级段位 JSON（读路径由 ContentSnapshotJson 投影而来） */
  demandPriorityJson?: string
  /** 换型规则子块 JSON（读路径由 ContentSnapshotJson 投影而来） */
  setupTransitionRulesJson?: string
  /** 发布内容快照载体（读路径一律被后端置空，前端不消费） */
  contentSnapshotJson?: string
  /** 变更原因 / 备注 */
  remarks?: string
}

/**
 * 规则集主表（`RuleSet` 实体，2026-10-22 主表写端点补全后前端开始消费）
 *
 * 后端 `POST /rule-set` / `PUT /rule-set/{id}` 直接收发该实体；Id/时间戳由服务端维护。
 */
export interface RuleSetMainDto {
  id: number
  ruleSetCode: string
  ruleSetName: string
  description?: string | null
  isActive: boolean
  createdAt?: string
  createdBy?: string
  updatedAt?: string
  updatedBy?: string
}

/** 新建规则集主表输入（POST /api/governance/rule-set，Q1/Q2/Q6） */
export interface CreateRuleSetInput {
  /** 用户填；全库唯一（大小写不敏感）、创建后不可变更 */
  ruleSetCode: string
  ruleSetName: string
  description?: string
}

/** 编辑规则集主表输入（PUT /api/governance/rule-set/{id}） */
export interface UpdateRuleSetInput {
  ruleSetId: number
  /** 必须原样回传既有编码（大小写差异也会被后端拒绝）；前端控件只读 */
  ruleSetCode: string
  ruleSetName: string
  description?: string
  /** Q4：IsActive 仅关主表、不级联版本 */
  isActive: boolean
}

/** 新建 RuleSet DRAFT 输入（POST /api/governance/rule-set/version） */
export interface CreateRuleSetDraftInput {
  ruleSetId: number
  versionCode: string
  /** 需求优先级段位 JSON（fork 时由前端复制源版本内容） */
  demandPriorityJson?: string
  /** 内容快照载体（fork 时用于保留换型规则子块） */
  contentSnapshotJson?: string
  /** 变更原因（写入 remarks） */
  remarks?: string
}

/** 更新 RuleSet DRAFT 输入（PUT /api/governance/rule-set/version/{id}） */
export interface UpdateRuleSetDraftInput {
  versionId: number
  body: RuleSetVersion
}

/** 新建 ParameterSet DRAFT 输入（POST /api/governance/parameter-set/version） */
export interface CreateParameterSetDraftInput {
  parameterSetId: number
  versionCode: string
  /** fork 来源（PUBLISHED / DISABLED 时的源 ParameterSetVersionId）；纯新建时为空 */
  sourceParameterSetVersionId?: number
  remarks?: string
}

/** 更新 ParameterSet DRAFT 输入（PUT /api/governance/parameter-set/version/{id}） */
export interface UpdateParameterSetDraftInput {
  versionId: number
  body: ParameterSetVersion
}

/** 通用 Governance Publish 输入 */
export interface PublishGovernanceInput {
  versionId: number
  changeReason: string
  actor: string
}

/** 通用 Governance Disable 输入 */
export interface DisableGovernanceInput {
  versionId: number
  reason: string
  actor: string
}

/**
 * Fork Draft 结果
 *
 * ⚠️ 2026-10-22 更正：原结构含 `parameterSetDraft`（「fork 一条规则集版本 ⇒ 同时
 * fork 一条参数集版本」）。**冻结模型不支持**：RuleSetVersion 无 ParameterSetVersionId
 * （DDL 08:9042 / 后端实体 / dev 实测三处一致），两族只在 StrategyProfileVersion 会合。
 */
export interface ForkDraftResult {
  /** 新建 RuleSet DRAFT */
  ruleSetDraft: RuleSetVersion
}
