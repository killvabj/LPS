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
   * 最新已发布版本**版本码**（后端 `currentVersionCode`，字符串，列表投影按 Id DESC 取）
   *
   * ⚠️ 2026-10-22 更正：原先本字段是 `currentVersion: number`，映射时恒填 0，
   * 页面上「当前版本」永远是「—」。后端给的就是版本**码**（如 `RS-DEMO-V2`），
   * 不是自增号，故改成字符串并直接用后端值。
   *
   * ⚠️ 术语：是「**最新已发布**」不是「当前有效」——后者按生效期窗口判定（published-version 端点）。
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
 * 后端 `RuleSetVersionRowDto`（`GET /api/governance/rule-sets/versions` 的一行）
 *
 * ⚠️ 2026-11-11（3号位 契约变更协调件 v1.0）新增端点，**主子表联查·一版一行**：
 *   每主表 N 个 RuleSetVersion ⇒ N 行，各版本状态独立可读、互不覆盖。
 *   旧 `GET /rule-sets`（一主表带最新概要）把 `Status` 取「最新创建版本」
 *   （`OUTER APPLY TOP1 ORDER BY Id DESC`）⇒ 同族有生效 PUBLISHED 又新加 DRAFT 时
 *   整个列表被标成「草稿」，且详情默认展示草稿内容 ⇒ **看不到当前生效版本的内容**。
 *   本 DTO 每行自带版本真实状态与版本 Id，页面一行即可渲染、可直接按 versionId 进详情。
 *
 * 排序：主表 Id DESC；每主表内版本 Id ASC。
 * ⚠️ 可空字段（description / effectiveFrom / effectiveTo / publishedAt / publishedBy /
 *   createdAt / ruleSetCreatedAt）在后端 `Program.cs:94` 设了 `WhenWritingNull`
 *   ⇒ 为空时**整个 key 消失**（不是 null），前端按「缺失即空」处理。
 */
export interface RuleSetVersionRowDto {
  /** 主表 Id */
  ruleSetId: number
  /** 主表编码 */
  ruleSetCode: string
  /** 主表名称 */
  ruleSetName: string
  /** 主表说明 */
  description?: string | null
  /** 主表启停（与版本状态是两件事） */
  isActive: boolean
  /** 主表建档时间 */
  ruleSetCreatedAt?: string | null
  /** 版本 Id（发布 / 停用 / 校验 / 保存段位端点的键） */
  versionId: number
  /** 版本码（如 `DOMAIN001-V1`） */
  versionCode: string
  /** **版本真实状态**（DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED） */
  status: string
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
 * 前端规则集**列表行**（一版一行）
 *
 * 行有两种：
 *  - **版本行**（来自新端点 `GET /rule-sets/versions`）：`hasVersion=true`，
 *    带 `versionId` / `versionCode` / `versionStatus`（该版本真实状态）。
 *  - **无版本主表行**（来自旧端点 `GET /rule-sets` 里一条版本都没有的主表）：
 *    `hasVersion=false`，无版本字段 ⇒ 页面上显示「未配版本」并给「删除规则集」入口。
 *
 * ⚠️ 为什么要保留第二种行：新端点是主表 INNER JOIN 版本，**一条版本都没有的主表
 *   不会出现在它里面**（dev 实测 905 个主表里 894 个无版本）。若纯切新端点，
 *   这些主表连同「未配版本」筛选与删除入口会一起从列表消失 ⇒ 功能回退。
 *   故列表 = 新端点版本行 + 旧端点「无版本主表」行。
 */
export interface RuleSetListRowDto {
  /** 行唯一键：版本行 = `v{versionId}`；无版本主表行 = `n{ruleSetId}` */
  rowKey: string
  ruleSetId: number
  ruleSetCode: string
  ruleSetName: string
  /** 主表说明 */
  description?: string
  /** 主表启停 */
  isActive: boolean
  /** 建档时间（主表或版本，取可得的那个） */
  createdAt: string
  /** 适用 Domain（恒为 'ALL'：后端 RuleSet 表无 Domain 外键，不编造） */
  domainKey: DomainKey
  /** 该行是否有版本 */
  hasVersion: boolean
  /** 版本 Id（仅版本行） */
  versionId?: number
  /** 版本码（仅版本行） */
  versionCode?: string
  /** 版本真实状态（仅版本行） */
  versionStatus?: RuleStatus
  /** 本规则集「最新已发布」版本码（主表级副信息） */
  currentVersionCode?: string
  /** 本规则集当前草稿版本码（主表级副信息） */
  draftVersionCode?: string
  /** 最近变更（主表级；来自版本备注） */
  lastChangeReason?: string
  /** 发布时间（版本行 = 该版本发布时间；无版本行 = 主表最近一次发布时间） */
  publishedAt?: string
}

/**
 * RuleSet 详情
 *
 * ⚠️ 2026-10-22 更正：原先这里挂 `parameterSet` / `strategyProfile` 两个**前端自造**的占位对象
 * （后端根本没有「规则集 → 参数集」「规则集 → 策略包」的绑定关系，绑定只发生在
 * StrategyProfileVersion 上），恒为假值。现已整条删除。
 */
export interface RuleSetDetailDto {
  /**
   * 摘要
   *
   * ⚠️ 2026-11-11：`summary.status` = **本页展示的那一版的真实状态**
   *   （不再「有草稿就一律 DRAFT」）。列表改一版一行后，用户点哪一行就看到哪一版的状态，
   *   页面上的「发布 / 停用 / 校验」按钮据此决定作用在哪个版本上。
   */
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
  /** 是否为「最新已发布」版本（Id 最大的那条 PUBLISHED）。
   *  ⚠️ 不等于「当前有效」——后者按生效期窗口判定，由 published-version 端点给出。 */
  isLatestPublished: boolean
  /** 生效起（后端 `effectiveFrom`；发布时写入的发布时间，未发布为空） */
  effectiveFrom?: IsoDateTime
  /** 生效止（后端 `effectiveTo`；停用时写入的停用时刻，已发布未停用为空） */
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
  /**
   * 命中条件组（DNF：**组内 AND、组间 OR**）
   *
   * ⚠️ 与 `matchConditions` **互斥**（后端 `DemandPriorityValidator` 对「同一段两者都填」直接报错）：
   *   非空即以组为准、平铺列表被忽略 ⇒ 属歧义配置。本前端两种模式互斥产出：
   *   有组 ⇒ 只写组；无组 ⇒ 只写平铺。
   * 空数组 / 未定义 = 存量平铺模式。
   */
  matchConditionGroups?: SegmentConditionGroupDto[]
  /** 段内排序键（依次生效） */
  sortFields: SegmentSortFieldDto[]
  /** 稳定排序键（并列时兜底，保证结果确定） */
  stableTieBreakFields: string[]
}

/**
 * 一条 AND 条件组（后端 `SegmentConditionGroup` / `PriorityConditionGroup`）
 *
 * 语义：**组内条件全部满足**才本组成立，**组与组之间取或**。
 * 例「1 并 2 并（3 或 4）」＝ 组A{1,2,3}、组B{1,2,4}。
 */
export interface SegmentConditionGroupDto {
  /** 组内条件（AND 关系） */
  conditions: SegmentMatchConditionDto[]
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
