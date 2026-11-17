/**
 * 异常与原因解释 DTO（按 5号位 2026-10-09 函件 R1 回执整改）
 *
 * @see 4号位文档第 11-12 节（页面 6：异常与原因解释 + 设备故障）
 * @see 5号位-2026-10-09-Explanation页面一致性-回执-给4号位.md（§ 一 关键纠正 1/2/3 + § 五 R1 承诺）
 * @see 5号位 lps/LPS.APS.Web/Controllers/ExplanationController.cs（L41-94 list + summary 2 端点）
 * @see 5号位 lps/LPS.APS.Core/Dto/ExplanationDtos.cs（ExplanationDto + ExplanationSummaryDto + ReasonCodeCountDto）
 * @see 5号位 lps/LPS.APS.Core/Dto/BusinessFactIssueDto.cs（业务事实问题 DTO）
 *
 * 2026-10-09 改造（按 5号位 回执）：
 *  - R1 ReasonCode 权威源 = v5.1.10 §八.1 15 码（@see 冻结文档/07_APS_数据库字段说明文档_v5.1.10_20261008_BatchPolicy字段语义_PI数量候选版.md :4863）
 *    0号位《1号位代码第15轮审核报告》P1-03「6 码为正式冻结要求」属辅助，与最新冻结文档碰撞时**以最新冻结文档为准**
 *  - FROZEN_ZONE_LOCK：15 码唯一锁码；FIRM / FROZEN / 其它执行锁统一并入；细分走 EvidenceJson.evidenceType = LOCK
 *  - ObjectType 值域 = ORDER / TASK / RESOURCE / STAGE / DOMAIN（v5.1.10 :4858，5 码；前端原 PI/PO 占位已撤）
 *  - Severity = INFO / WARN / ERROR（v5.1.10 :4864；CRITICAL 仅 BOM Issues 表用；前端 WARNING → WARN）
 *  - 撤 5号位 关键纠正 3 占位 interface（后端无对应实体）
 *    改用 ExplanationSummaryDto.errorCount / warnCount / infoCount 三 KPI
 *  - 端点契约改造：summary 端点返 ExplanationSummaryDto（含 items[]）；前端 store 取 items 字段而非 data 字段
 *
 * R2 关键路径 / R3 failureSeverity 跨号位（1号位 求解 + 2号位 落盘 + 0号位 裁决 V1 范围）—— **前端不动**
 *  - 4号位 端不发函（@see feedback_no_redundant_ack_letters：仅"已知悉"不发；R2/R3 跨号位由 5号位 中转）
 *  - verify [E] 段新增 E11/E12 skip 占位（@see dp-freeze DP06/07 模式）
 *
 * 2026-10-10 收口：business-fact-issues 分页 + total + severity 排序（前端先上线）
 *  - ExplanationViewDto 增 businessFactIssuesTotal/PageIndex/PageSize 3 字段（@see frontNew/src/api/aps-v1/types/common.ts PageResult<T>）
 *  - store 层做缓存（版本切换保留，仅"刷新"按钮失效）+ 累积分页 + 本地 severity desc 排序兜底
 *  - 函件 4号位-2026-10-10-business-fact-issues分页severity排序total-给5号位.md 建议 ORDER BY 加 severity + DTO 改 PageResult
 */

import type { IsoDateTime } from './common'

/* ===== ReasonCode 权威枚举（v5.1.10 §八.1 :4863 15 码全集） ===== */
/** 15 码权威枚举（与后端 DTO ReasonCode 字段 1:1 对齐；前端字典改用全集，**勿按 §85 6 码白改**） */
export const REASON_CODES = [
  'RESOURCE_CAPACITY_WAIT',
  'MATERIAL_SHORTAGE',
  'PRECEDENCE_WAIT',
  'FROZEN_ZONE_LOCK',
  'ROUTING_FALLBACK',
  'STAGE_LEADTIME_FALLBACK',
  'BOM_DEGRADE',
  'CROSS_ORG_HANDOFF',
  'PRIORITY_LOWER_THAN_OTHERS',
  'DUE_DATE_RISK',
  'LOGISTICS_DELAY',
  'PRIORITY_INHERITANCE',
  'CROSS_DOMAIN_VERSION_MISMATCH_RISK',
  'MANUAL_COMPLETED_SHORT',
  'EQUIPMENT_BREAKDOWN_RISK'
] as const
export type ReasonCode = (typeof REASON_CODES)[number]

/** ReasonCode → UI 中文标签（前端字典，非后端契约） */
export const REASON_CODE_LABELS: Record<ReasonCode, string> = {
  RESOURCE_CAPACITY_WAIT: '资源产能等待',
  MATERIAL_SHORTAGE: '物料短缺',
  PRECEDENCE_WAIT: '前序等待',
  FROZEN_ZONE_LOCK: '冻结区锁定',
  ROUTING_FALLBACK: '工艺路线回退',
  STAGE_LEADTIME_FALLBACK: '阶段前置时间回退',
  BOM_DEGRADE: 'BOM 降级',
  CROSS_ORG_HANDOFF: '跨组织交接',
  PRIORITY_LOWER_THAN_OTHERS: '优先级低于其他',
  DUE_DATE_RISK: '交期风险',
  LOGISTICS_DELAY: '物流延期',
  PRIORITY_INHERITANCE: '优先级继承',
  CROSS_DOMAIN_VERSION_MISMATCH_RISK: '跨域版本不一致风险',
  MANUAL_COMPLETED_SHORT: '手工完成不足',
  EQUIPMENT_BREAKDOWN_RISK: '设备故障风险'
}

/* ===== 原因码 → 展示标签（全站唯一口径，2026-10-31 收口） ===== */
/**
 * 后端原因码字段的实库取值类型：**空串**（写方未填）+ 冻结 15 码。
 *
 * 后端两处均为 `public string ... = string.Empty`：
 *  - `lps/LPS.APS.Core/Dto/ExplanationDtos.cs` → `ScheduleExplanationFact.ReasonCode`
 *  - `lps/LPS.APS.Application/Services/Query/Dto/GanttDataDto.cs:310` → `GanttDelayReasonDto.ReasonCode`
 *
 * 5号位 2026-10-31 回执实查：ORDER 级 465,481 条事实中 **442,408 条（95%）ReasonCode 为空串**；
 * dev `GET /api/explanation/summary?planVersionId=999333` 实测 `reasonCodeCounts` 含
 * `{"reasonCode":"","count":516}`、`items` 50 条中 39 条为空串。
 * ⇒ **空串是常态，不是异常**：前端必须按「写方未填原因码」中性展示，不能留空白标签。
 */
export type ReasonCodeValue = ReasonCode | ''

/**
 * 原因码 → 展示标签。**全站唯一口径**（甘特页 / 排程解释页 / CTP 页共用，禁止各页自写查表）。
 *
 * 三档：
 *  - 空串 / null / undefined → 「未标注原因码」（中性；**不留空白标签**）
 *  - 冻结 15 码命中 → 冻结中文标签
 *  - 字典未命中（15 码之外的受控扩展值）→ 「待核对·<码值>」（原样露出码值，不臆造中文名）
 *
 * ⚠️ 查表**必须**走本函数：裸索引 `REASON_CODE_LABELS[code]` 在空串 / 未命中时得到 `undefined`
 *    ⇒ ElTag 渲染成**空白标签**（Ctp.vue 历史注释已记「甘特 GT02/GT10 踩过」）。
 */
export function reasonLabelOf(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '未标注原因码'
  return (REASON_CODE_LABELS as Record<string, string>)[raw] ?? `待核对·${raw}`
}

/* ===== 降级动作码（DegradeAction）→ 展示标签（2026-11-09 加） ===== */
/**
 * 权威来源 = 冻结文档 `07:857` 字段说明 + `07:868-885` 降级动作处置矩阵（**不臆造中文名**）：
 *  - `STAGE_NULL`：07:872/875「`ChildRequiredStageCode=NULL`；1号位按保守策略兜底」
 *  - `FACTORY_FALLBACK`：07:873/874「保留 BOM 原生链」
 *  - `QTY_DEFAULT_1`：07:877「按 Quantity=1 兜底写入 Workset」
 *  - `PRODUCE_DEFAULT_1`：07:878「按 Produce=1 兜底」
 *  - `CYCLE_SKIP`：07:879「首次访问保留 + 重复循环节点跳过」
 *  - `BOMNO_SKIP`：07:880「该 BOMNo 整棵树作废；其他 BOMNo 正常继续」
 *  - `USE_MAX_BOMNO`：不在 07:857 的 6 码清单里，但 08 DDL（`:5406`/`:6003`）实际写入该值 ⇒ 一并收
 *
 * ⚠️ **未知值一律原样显示**（沿用甘特 `setupSourceMetaOf` 口径，**不加**「待核对·」前缀）：
 *   dev 实测 `MATERIAL_STAGE_CONTEXT` 源 66,596 行的 `degradeAction` 是**中文自由文本**
 *   （「跳过此条，不写 Context」），加前缀会变成怪字符串。
 */
export const DEGRADE_ACTION_LABELS: Record<string, string> = {
  STAGE_NULL: '工段缺失兜底',
  FACTORY_FALLBACK: '工厂冲突兜底',
  QTY_DEFAULT_1: '数量按 1 兜底',
  PRODUCE_DEFAULT_1: '内制数量按 1 兜底',
  CYCLE_SKIP: 'BOM 环路跳过',
  BOMNO_SKIP: '整棵 BOM 作废跳过',
  USE_MAX_BOMNO: '取最大 BOM 号',
  /** 2026-11-10 dev 实测真值（抽样 3,139 条里 200 条带此码）⇒ 此前未收，页面显示裸英文码 */
  CROSS_FACTORY_EDGE_SKIP: '跨厂边跳过'
}

/**
 * 降级动作 → 展示标签。
 *
 *  - 空 / 未填 → **空串**（调用方按「无降级」显示 `—`；07:857「为 NULL 表示无降级（纯登记）」）
 *  - 命中码表 → 冻结中文标签
 *  - 未命中 → **原样返回原值**（含中文自由文本），不臆造中文名、不加前缀
 */
export function degradeActionLabelOf(action?: string | null): string {
  const raw = (action ?? '').trim()
  if (!raw) return ''
  return DEGRADE_ACTION_LABELS[raw] ?? raw
}

/* ===== 业务事实问题：来源 / 问题类型 → 中文（2026-11-10 按 dev 实测真值重做） ===== */
/**
 * 来源（Source）中文映射。
 *
 * 值域 = 后端 SQL 字面量（@see lps/LPS.APS.BusinessRules/Repositories/BusinessFactIssueRepository.cs
 *   :40 `'BOM_WORKSET' AS Source`、:114 `'MATERIAL_STAGE_CONTEXT' AS Source`）。
 * dev 2026-11-10 实测分布（合计 107,739 条）：BOM_WORKSET 41,143 / MATERIAL_STAGE_CONTEXT 66,596。
 */
export const BIZ_SOURCE_LABELS: Record<string, string> = {
  BOM_WORKSET: 'BOM 维护',
  MATERIAL_STAGE_CONTEXT: '工段生产部门',
  PI_POSITION: 'PI Position',
  MANUAL_ETA: '手工 ETA'
}

/**
 * 来源 → 展示标签（空 → 「—」；未命中 → 「待核对·<码>」，**不裸奔英文码**）。
 */
export function bizSourceLabelOf(source?: string | null): string {
  const raw = (source ?? '').trim()
  if (!raw) return '—'
  return BIZ_SOURCE_LABELS[raw] ?? `待核对·${raw}`
}

/**
 * 问题类型（IssueType）中文映射。
 *
 * ⚠️ **此前 4号位 自拟的 6 分类（EQUIPMENT_FAILURE / MATERIAL_AVAILABILITY / PI_POSITION_MISMATCH /
 *    CONTRACT_VIOLATION / CAPACITY_SHORTAGE / OTHER）后端一个都不产出**
 *    ⇒ dev 上 100% 的行都在显示裸英文码。现按 2026-11-10 dev 实测的 **8 个真码**补齐。
 *
 * 实测依据：跨 16 个分页抽样 3,139 条（覆盖 ERROR/WARN/INFO 三档 + 两个来源），得到 8 个 IssueType：
 *   BOM_WORKSET：BOMNO_MISSING_PRODUCTION / BOM_ENTRY_NOT_FOUND / MULTI_BOMNO_UNRESOLVED
 *                / CROSS_FACTORY_STAGE_MISSING / FACTORY_MISMATCH / FACTORY_MISMATCH_MULTI / LEAF
 *   MATERIAL_STAGE_CONTEXT：MISSING_DEPT_FOR_BOM_STAGE
 *
 * 中文名按 dev 返回的 Detail 原义翻译（Detail 是后端原文，含具体单据/物料/工段）。
 */
export const BIZ_ISSUE_TYPE_LABELS: Record<string, string> = {
  BOMNO_MISSING_PRODUCTION: '生产指令没有 BOM 号',
  BOM_ENTRY_NOT_FOUND: 'BOM 展开找不到入口',
  MULTI_BOMNO_UNRESOLVED: '同一物料有多个默认 BOM 号',
  CROSS_FACTORY_STAGE_MISSING: '跨厂工段缺失',
  FACTORY_MISMATCH: '工厂归属不一致',
  FACTORY_MISMATCH_MULTI: '工厂归属不一致（涉及多厂）',
  LEAF: '内制物料没有下阶 BOM',
  MISSING_DEPT_FOR_BOM_STAGE: 'BOM 工段没配生产部门',
  // ↓ 以下两码为 2026-11-11 dev 全量扫描补齐（WARN 段 98,054 条全量遍历）
  MULTI_DEPT_CONFLICT_FOR_STAGE: '同阶段对出多个生产部门',
  MISSING_DEPT_IN_ERP: 'ERP 主档查不到生产部门'
}

/**
 * 问题类型 → 展示标签（空 → 「—」；未命中 → 「待核对·<码>」）。
 *
 * ⚠️ 查表**必须**走本函数：裸索引在未命中时得到 `undefined` ⇒ 渲染成空白格。
 */
export function bizIssueTypeLabelOf(issueType?: string | null): string {
  const raw = (issueType ?? '').trim()
  if (!raw) return '—'
  return BIZ_ISSUE_TYPE_LABELS[raw] ?? `待核对·${raw}`
}

/**
 * 业务事实问题「问题类型」值域（**封闭 10 码**）。
 *
 * 5号位 11-11 复函 §三：按**将要部署的 SP 本体**核（不是按当前数据）⇒ 新增码只可能出自我方
 * 那几支 SP（sp_ExpandBOMBatch_vNext / sp_GenerateBOMCrossFactoryEdge / sp_EnrichBOMWorkset /
 * MSC 重建路），且每次改动必书面知会。
 * ⚠️ 其中 BOM 号类 3 码当前 **0 行属正常**（豁免/精确匹配批次无此类）⇒ 字典**不许**按「有行才登记」。
 */
export const BIZ_ISSUE_TYPE_CODES = [
  'BOMNO_MISSING_PRODUCTION',
  'BOM_ENTRY_NOT_FOUND',
  'MULTI_BOMNO_UNRESOLVED',
  'CROSS_FACTORY_STAGE_MISSING',
  'FACTORY_MISMATCH',
  'FACTORY_MISMATCH_MULTI',
  'LEAF',
  'MISSING_DEPT_FOR_BOM_STAGE',
  'MULTI_DEPT_CONFLICT_FOR_STAGE',
  'MISSING_DEPT_IN_ERP'
] as const

/**
 * 真业务工厂码（5号位 11-11 复函 §二给的权威清单）。
 *
 * ⚠️ `Factory` 主数据表里另有 **44 条测试夹具**（`Test Factory…` / `S1~S4 Factory…`，且 IsActive=1）
 *   ⇒ 直接拉全表进下拉会把测试厂铺给用户；下拉只放这 5 个真业务码。
 * ⚠️ `CN6课` 就是**正式码**（正式名「中国特注品6课」）——按「CN6」筛 0 命中，别改写。
 */
export const BIZ_ISSUE_FACTORY_CODES = ['CN', 'BJ', 'TJ', 'CN6课', 'SH'] as const

/* ===== ObjectType 值域（v5.1.10 :4858 5 码） ===== */
export const EXPLANATION_OBJECT_TYPES = ['ORDER', 'TASK', 'RESOURCE', 'STAGE', 'DOMAIN'] as const
export type ExplanationObjectType = (typeof EXPLANATION_OBJECT_TYPES)[number]

/** ObjectType → UI 中文标签 */
export const OBJECT_TYPE_LABELS: Record<ExplanationObjectType, string> = {
  ORDER: '订单',
  TASK: '任务',
  RESOURCE: '资源',
  STAGE: '工段',
  DOMAIN: '分域'
}

/* ===== Severity 值域（v5.1.10 :4864 3 码） ===== */
export const EXPLANATION_SEVERITIES = ['INFO', 'WARN', 'ERROR'] as const
export type ExplanationSeverity = (typeof EXPLANATION_SEVERITIES)[number]

/** Severity → UI 中文标签（CRITICAL 仅 BOM Issues 表用，ScheduleExplanationFact 不含） */
export const EXPLANATION_SEVERITY_LABELS: Record<ExplanationSeverity, string> = {
  INFO: '提示',
  WARN: '警告',
  ERROR: '错误'
}

/** Severity → ElTag type */
export const EXPLANATION_SEVERITY_TAG: Record<ExplanationSeverity, 'info' | 'warning' | 'danger'> =
  {
    INFO: 'info',
    WARN: 'warning',
    ERROR: 'danger'
  }

/* ===== 排程解释事实（ScheduleExplanationFact —— 前端视图模型） ===== */
/**
 * ⚠️ **本类型是前端视图模型，不是后端 DTO 的原样拷贝**（2026-11-05 修）。
 *
 * 后端 `ExplanationDto`（lps/LPS.APS.Core/Dto/ExplanationDtos.cs :9-43）返的字段名是
 *   `id` / `createdAt` / `orderId` / `taskId` / `resourceId` / `stageCode`，
 * **没有** `factId` / `occurredAt` / `objectRef`。
 * 前端此前直接把它当本类型用 ⇒ 页面「对象」格恒为空、时间恒显示 `Invalid Date`、
 * `:key="f.factId"` 恒为 undefined。
 *
 * 现统一由 `api/aps-v1/explanation.ts` 的 `toScheduleExplanationFact()` 做一次映射：
 *   `id → factId`、`createdAt → occurredAt`、`objectRef` 由来源 Id / 证据 JSON 派生。
 */
export interface ScheduleExplanationFact {
  factId: number
  planVersionId: number
  scheduleRunId?: number
  objectType: ExplanationObjectType
  /**
   * 聚合展示用：**由来源 Id / 证据 JSON 派生**（不是后端字段）。
   * 派生优先级（见 api 层 `deriveObjectRef`）：
   *   ORDER → 证据里的 `LogicalDemandKey`（需求号，如 `999333_35`）→ 退到 `orderId`
   *   TASK → `taskId`；RESOURCE → `resourceId`；STAGE → `stageCode`；DOMAIN → 证据里的 `DomainKey`
   */
  objectRef: string
  /**
   * 原因码 —— 冻结权威枚举（v5.1.10 :4863 15 码全集）**或空串**。
   *
   * 2026-10-31：实库 **95% 为空串**（写方未填，见 `ReasonCodeValue` 注释）⇒ 类型必须能表达空串，
   * 否则前端按 15 码联合理解值，会把「写方未填」误判为「未知码」。
   */
  reasonCode: ReasonCodeValue
  severity: ExplanationSeverity
  /** 影响小时数（v5.1.10 :4865；可为 0） */
  impactHours?: number
  /** 证据 JSON（外壳 evidenceType/summary/details；v5.1.10 :4866；细分走 evidenceType） */
  evidenceJson?: string
  /**
   * 证据里的大白话摘要（写方给了才有；没有则不显示这一行）。
   * 实测形状不统一：ORDER 排产延误行是 `{evidenceType, summary, details}`（有 summary），
   * 需求分配行是 `{LogicalDemandKey, MaterialId, DomainKey, ...}`（无 summary），
   * 域级行是 PascalCase 的 `{EvidenceType, DomainKey, ...}` ⇒ 取值时大小写都认。
   */
  evidenceSummary?: string
  /** 发生时间（= 后端 `createdAt`） */
  occurredAt: IsoDateTime
  /* ---- 以下为后端原始列，保留以便派生 / 深钻 ---- */
  /** 订单 Id（ORDER 级事实；实库 525/527 行有值） */
  orderId?: number
  /** 任务 Id（TASK 级事实） */
  taskId?: number
  /** 资源 Id（RESOURCE 级事实） */
  resourceId?: number
  /** 工段代码（STAGE 级事实；实库多为 CN_ASSY / BJ_CAST / BJ_SURF） */
  stageCode?: string
  /**
   * 订单号（**后端列表端点直返**，2026-11-10 实测 996/998 条有值）。
   *
   * ⚠️ 此前前端靠逐条调 `GET /api/order-query/{id}` 解析订单号（@see 页面旧 `resolveOrderRef`）——
   *   那是**误判**：实测 `/api/explanation` 的响应里已经带 `orderNo` / `materialCode`，
   *   996 条事实里 996 条有值 ⇒ 无需任何额外请求，直接取。
   */
  orderNo?: string
  /** 物料编码（后端列表端点直返；实库为 `WIP-xxx` 半成品编码） */
  materialCode?: string
  /* ---- 证据 JSON 解析结果（2026-11-10 加：让页面说人话，而不是只给内部键） ---- */
  /** 证据里直接带的业务字段（大小写两种写法都已归一，取值见 `ExplanationEvidence`） */
  evidence?: ExplanationEvidence
  /** 排程域键（ORDER / DOMAIN 级事实都有；用于把 FAMILY_XC 翻成中文域名称） */
  domainKey?: string
  /**
   * 需求片序号（ORDER 级事实）。
   * 后端 `LogicalDemandKey` 的格式是 `{PlanVersionId}_{AllocationSequence}`
   * （证据 JSON 的 `LogicalDemandKey` = 示例 `999472_1`），
   * ⇒ 前半段是版本 Id（同一页所有行都一样、对用户无意义），有业务意义的是后半段的分配序号。
   */
  allocationSequence?: number
}

/**
 * 证据 JSON 里对用户有意义的字段（PascalCase / camelCase 都接受，取值时统一）。
 * 形状不固定，按需读取，不做严格校验。
 */
export interface ExplanationEvidence {
  allocationSequence?: number
  domainKey?: string
  materialId?: number
  netOutputQty?: number
  scheduledShareQty?: number
  unscheduledQty?: number
  /** 上游给的原因：中文句子（如「Phase 2 初始排产失败，Phase 4 修复未成功」）或英文码 */
  upstreamReasonCode?: string
  unscheduledSource?: string
  /* ---- DOMAIN 级证据（域级健康度） ---- */
  /** 证据类型：DOMAIN_QUANTITY_RECONCILIATION（数量对账）/ DEMAND_COMPLETION_TIME（完工时间确定性） */
  evidenceType?: string
  demandCount?: number
  scheduledDemandCount?: number
  unscheduledDemandCount?: number
  missingDemandCount?: number
  underScheduledDemandCount?: number
  demandNetQtyTotal?: number
  unscheduledQtyTotal?: number
  planningYieldMissingCount?: number
  planningYieldOutOfRangeCount?: number
  planningYieldDuplicateKeyCount?: number
  unreportedRemainderQty?: number
  /** DEMAND_COMPLETION_TIME 形状：993 条需求里"完工时间确定/不确定"各多少 */
  factCount?: number
  certainTrueCount?: number
  certainFalseCount?: number
  certainNullCount?: number
  stageTimingSegmentCount?: number
  missingStageLeadTimeSegmentCount?: number
  /* ---- PI_FREE_OVER_COMMIT 形状（2026-11-10 实测 3 条 WARN） ---- */
  /** 生产指示号（PI_FREE_OVER_COMMIT 形状带） */
  productionInstructionNo?: string
  /** 生产指令数量 */
  quantity?: number
  /** 可承接量 */
  continuationQty?: number
  /** 超发量（= quantity − continuationQty） */
  excessQty?: number
  /** 事实种类（PI_FREE_OVER_COMMIT） */
  kind?: string
  /* ---- DOMAIN 数量对账：其余计数字段（2026-11-10 实测） ---- */
  scheduledQtyTotal?: number
  unscheduledQtyClosedTotal?: number
  deltaQty?: number
  unmatchedScheduledCount?: number
  planningYieldQueriedCount?: number
  /** 缺良率的物料编码样例（实测 10 个） */
  planningYieldMissingSamples?: string[]
  /** 完工时间不确定的需求键样例（实测 10 个，形如 `999472_1`） */
  uncertainSamples?: string[]
  /** 后端自带的判定口径说明（DOMAIN_COMPLETION_TIME 形状带，中文原文） */
  gate?: string
  /** 后端自带的红线说明（DOMAIN_COMPLETION_TIME 形状带，中文原文） */
  notATask?: string
  [k: string]: unknown
}

/**
 * 上游原因码 → 中文（**前端展示字典，不是后端契约**）。
 *
 * 背景（2026-11-10 实测 planVersionId=999472，998 条事实）：
 *   - 顶层 `reasonCode` 字段 **998/998 全为空串**（写方未填）
 *   - 真实原因在证据 JSON 的 `UpstreamReasonCode` 里，且**中英混用**：
 *       831 条「Phase 2 初始排产失败，Phase 4 修复未成功」（中文句子）
 *       155 条 `BATCH_POLICY_MISSING`
 *         5 条 `MISSING_PRODUCTION_DEPARTMENT_CONTEXT`
 *         2 条 `BATCH_POLICY_CONFLICT`
 * ⇒ 页面先按这张表翻成大白话；表里没有的值原样显示（不臆造中文）。
 *
 * 2026-11-10 5号位 R2 复函：全库 `UpstreamReasonCode` **恰 6 值**（已封闭）——
 *   276,173「Phase 2 初始排程失败，Phase 4 修复未成功」/ 49,685 `BATCH_POLICY_MISSING`
 *   / 19,730 `MISSING_PRODUCTION_DEPARTMENT_CONTEXT` / 13,654 `BATCH_POLICY_CONFLICT`
 *   / 1,967 `CALENDAR_COVERAGE_INSUFFICIENT` / 208「Phase 4 局部修复后仍有执行批未排下」。
 *   另：`reasonCode` 恒空是 0号位 2026-10-12 §六.3 的规范（域外码列置空串、原文搬进
 *   `UpstreamReasonCode` 留痕），**不是**漏写 ⇒ 前端不按「后端没填」问责。
 */
export const UPSTREAM_REASON_LABELS: Record<string, string> = {
  BATCH_POLICY_MISSING: '未配置批策略（该物料+部门缺拆合批规则）',
  BATCH_POLICY_CONFLICT: '批策略规则冲突（同一物料+部门存在多条冲突规则）',
  MISSING_PRODUCTION_DEPARTMENT_CONTEXT: '生产部门信息缺失（物料未关联可用的生产部门）',
  // 2026-11-10 5号位 R2 复函补：全库 1,967 行，此前前端字典漏收 ⇒ 这些行落进「未归类」
  CALENDAR_COVERAGE_INSUFFICIENT: '资源日历覆盖不足（该时段没有可用的资源日历或产能时段）'
}

/**
 * 已知**配置缺口码**（前端业务字典，非后端契约）。
 *
 * 5号位 2026-11-10 复函 R2：全库 `UpstreamReasonCode` 恰 6 值（4 个英文码 + 2 句中文），
 * 其中 4 个英文码都是「用户在系统里改配置就能修」的缺口：
 *   BATCH_POLICY_MISSING / BATCH_POLICY_CONFLICT / MISSING_PRODUCTION_DEPARTMENT_CONTEXT
 *   / CALENDAR_COVERAGE_INSUFFICIENT
 *
 * ⚠️ 判定必须**先于**冻结 15 码：0号位 若把这几个码回写进冻结码表，2号位 写入点就会把它们
 *    写进顶层 `reasonCode`；此时若先按 15 码判「排产决策」，「没配批策略」会被误显示成
 *    「排产决策取舍」，且「处理入口」消失 ⇒ 前端按本表优先判定。
 */
export const CONFIG_GAP_REASON_CODES = [
  'BATCH_POLICY_MISSING',
  'BATCH_POLICY_CONFLICT',
  'MISSING_PRODUCTION_DEPARTMENT_CONTEXT',
  'CALENDAR_COVERAGE_INSUFFICIENT'
] as const

/** 该码是否已知配置缺口码（顶层 `reasonCode` 与证据 `UpstreamReasonCode` 共用同一判定） */
export function isConfigGapReasonCode(code?: string | null): boolean {
  const v = (code ?? '').trim()
  return (CONFIG_GAP_REASON_CODES as readonly string[]).includes(v)
}

/** 上游原因 → 展示文案。空 → ''；已知码 → 中文；中文句子 / 未知码 → 原样 */
export function upstreamReasonLabel(raw?: string | null): string {
  const v = (raw ?? '').trim()
  if (!v) return ''
  const known = UPSTREAM_REASON_LABELS[v]
  if (known) return known
  // 中文句子里夹着 Phase 2 / Phase 4 这类阶段术语 ⇒ 翻成"第 N 步"，工厂用户更好懂
  if (/[一-龥]/.test(v) && /Phase\s*\d/i.test(v)) {
    return v.replace(/Phase\s*(\d+)/gi, '第 $1 步')
  }
  return v
}

/** 上游原因是否为可动作的配置缺口（用于提示"去哪个页面补"） */
export function isUpstreamConfigGap(raw?: string | null): boolean {
  return isConfigGapReasonCode(raw)
}

/* ===== 原因归类（2026-11-10 工厂视角） =====
 *
 * 为什么要有这一层：实测 998 条事实的原因是**四种互不相干的形态**混在一起 ——
 *   ① 英文配置码（BATCH_POLICY_MISSING 等）→ 用户改配置就能修
 *   ② 中文句子（Phase 2 初始排程失败…）→ 引擎内部阶段术语，用户看不懂也修不了
 *   ③ 冻结 15 码（顶层 reasonCode 非空时）→ 排产决策取舍
 *   ④ 证据里的 evidenceType（PI_FREE_OVER_COMMIT）→ 数据问题
 * 直接把原文摆出来等于让用户自己分类 ⇒ 归成固定的 8 类，
 * 页面按类聚合成"要人处理的几张卡"，每类给一句人话 + 一条去处。 */
export type ExplanationReasonClassKey =
  | 'BATCH_POLICY_MISSING'
  | 'BATCH_POLICY_CONFLICT'
  | 'MISSING_PRODUCTION_DEPARTMENT_CONTEXT'
  | 'CALENDAR_COVERAGE_INSUFFICIENT'
  | 'ENGINE'
  | 'PI_OVER_COMMIT'
  | 'DECISION'
  | 'OTHER'

export interface ExplanationReasonMeta {
  key: ExplanationReasonClassKey
  /** 短标签（表格「问题」列用） */
  label: string
  /** 卡片标题（分类卡用，比 label 更完整） */
  title: string
  /** 要人做什么（卡片副标题） */
  hint: string
  /** 是不是「用户在系统里改配置就能解决」的缺口 */
  configGap: boolean
  /** 是不是要人去系统外补（主数据 / 找算法方） */
  external: boolean
}

/** 8 类原因的中文口径（前端业务字典，非后端契约）
 *
 * 命名口径（2026-11-10 用户口径：名字要正式，但用户能看懂）：
 *  - 卡片标题 = 问题类别的**正式名称**（名词短语，如「未配置批策略」「批策略规则冲突」）
 *  - 行内短标签 = 同一名称的缩写（表格「问题」列空间小）
 *  - 提示 hint = 大白话「你要做什么」（这是给用户看的说明，不参与命名正式化）
 */
export const EXPLANATION_REASON_META: Record<ExplanationReasonClassKey, ExplanationReasonMeta> = {
  BATCH_POLICY_MISSING: {
    key: 'BATCH_POLICY_MISSING',
    label: '未配批策略',
    title: '未配置批策略',
    hint: '这个物料+生产部门缺拆合批规则，去「批策略」补一条',
    configGap: true,
    external: false
  },
  BATCH_POLICY_CONFLICT: {
    key: 'BATCH_POLICY_CONFLICT',
    label: '规则冲突',
    title: '批策略规则冲突',
    hint: '同一物料+部门有多条冲突规则，去「批策略」改掉一条',
    configGap: true,
    external: false
  },
  MISSING_PRODUCTION_DEPARTMENT_CONTEXT: {
    key: 'MISSING_PRODUCTION_DEPARTMENT_CONTEXT',
    label: '缺生产部门',
    title: '生产部门信息缺失',
    hint: '主数据里查不到这个物料的生产部门，要找工艺补上',
    configGap: true,
    external: true
  },
  CALENDAR_COVERAGE_INSUFFICIENT: {
    key: 'CALENDAR_COVERAGE_INSUFFICIENT',
    label: '日历覆盖不足',
    title: '资源日历覆盖不足',
    hint: '这个时段没有可用的资源日历或产能时段，去「资源日历维护」补上',
    configGap: true,
    external: false
  },
  ENGINE: {
    key: 'ENGINE',
    label: '引擎未排出',
    title: '排程引擎未产出结果',
    hint: '不是配置问题，要找排产算法方查这一批的运行记录',
    configGap: false,
    external: true
  },
  PI_OVER_COMMIT: {
    key: 'PI_OVER_COMMIT',
    label: '生产指令超发',
    title: '生产指令超发',
    hint: '同一张生产指令的数量超出可承接量，要跟生产核对',
    configGap: false,
    external: true
  },
  DECISION: {
    key: 'DECISION',
    label: '排产决策',
    title: '排产决策取舍',
    // 2026-11-10：页面下方的块已由「原因代码分布」改名「排产决策原因」（小表格，只列真原因码）
    hint: '排产时按规则做的取舍，看页面下方「排产决策原因」',
    configGap: false,
    external: false
  },
  OTHER: {
    key: 'OTHER',
    label: '未归类',
    title: '未归类原因',
    hint: '后端这条事实没写原因码，要找排产算法方补',
    configGap: false,
    external: true
  }
}

/**
 * 事实 → 原因分类。
 *
 * 判定顺序（**先权威、后兜底**，不臆造）：
 *  1. **已知配置缺口码**（顶层 `reasonCode` 或证据 `UpstreamReasonCode` 都认）→ 对应配置缺口类
 *     ⚠️ 必须排在冻结 15 码之前：0号位 若把这几个码回写进冻结码表，2号位 写入点就会把它们
 *        写进顶层 `reasonCode`；此时若先按 15 码判 DECISION，「没配批策略」会被误显示成
 *        「排产决策取舍」，且「处理入口」按钮消失 ⇒ 用户点不到该去补的地方。
 *  2. 顶层 `reasonCode` 命中冻结 15 码 → DECISION（权威）
 *  3. 证据 `UpstreamReasonCode` 含 `Phase N` → ENGINE（引擎内部阶段术语）
 *  4. 证据 `evidenceType` = PI_FREE_OVER_COMMIT → PI_OVER_COMMIT
 *  5. 其它 → OTHER
 */
export function classifyExplanationReason(f: {
  reasonCode?: string
  evidence?: ExplanationEvidence
}): ExplanationReasonMeta {
  const code = (f.reasonCode ?? '').trim()
  const raw = (f.evidence?.upstreamReasonCode ?? '').trim()
  const gap = CONFIG_GAP_REASON_CODES.find((c) => c === code || c === raw)
  if (gap) return EXPLANATION_REASON_META[gap]
  if (code && (REASON_CODE_LABELS as Record<string, string>)[code]) {
    return EXPLANATION_REASON_META.DECISION
  }
  if (raw && /Phase\s*\d/i.test(raw)) return EXPLANATION_REASON_META.ENGINE
  const et = (f.evidence?.evidenceType ?? '').trim()
  if (et === 'PI_FREE_OVER_COMMIT') return EXPLANATION_REASON_META.PI_OVER_COMMIT
  return EXPLANATION_REASON_META.OTHER
}

/* ===== 业务事实问题（BusinessFactIssue，对齐 5号位 lps/LPS.APS.Core/Dto/BusinessFactIssueDto.cs :9-80）
 *  数据源：5号位 `GET /api/business-fact-issues`（独立于 Summary 端点聚合）
 *  @see lps/LPS.APS.Web/Controllers/BusinessFactIssueController.cs :60-95
 *  冻结基线对齐：4号位 v1.6 § 八 + 01 基线 v1.10 §85 ReasonCode 隐含「业务事实层」===== */
/** Issue 来源（@see BusinessFactIssueDto.cs:12「BOM_WORKSET / MATERIAL_STAGE_CONTEXT / PI_POSITION / MANUAL_ETA 等」） */
export type BusinessFactSource =
  | 'BOM_WORKSET'
  | 'MATERIAL_STAGE_CONTEXT'
  | 'PI_POSITION'
  | 'MANUAL_ETA'
  | string

export interface BusinessFactIssue {
  issueId: number
  source: BusinessFactSource
  issueType: string
  /** 严重级别（后端 4 码 INFO/WARN/ERROR/CRITICAL，宽 string 接收；view 无 severity 列渲染，无视觉影响） */
  severity: string
  detail: string
  materialCode?: string
  factoryCode?: string
  documentNo?: string
  stageCode?: string
  affectedQuantity?: number
  degradeAction?: string
  reviewStatus: 'PENDING' | 'CONFIRMED' | 'IGNORED' | 'FIXED'
  reviewedBy?: string
  reviewedAt?: IsoDateTime
  occurredAt: IsoDateTime
}

/* ===== 重排建议 / 设备故障（10-10 冻结基线收口：4号位 v1.6 未列出，撤 UI + DTO）
 *  历史：v1.0/v1.1 前端 DTO 自扩（RescheduleRecommendation / EquipmentFailure）+ 模板 mock 占位
 *  - 4号位 v1.6 § 八「4号位 不重新计算原因」无 UI 块规格
 *  - 01 基线 v1.10 §85 ReasonCode 无 UI 块匹配（只有 ReasonCode 通用枚举）
 *  - 14 5号位 v1.7 § 八输出清单也不含此两类
 *  - lps 整目录无 RescheduleRecommendationDto / EquipmentFailureDto 对应实体
 *  结论：撤掉，等冻结文档 v1.7 增补后再启
 *  为避免影响外部 import 占位，类型保留空壳导出（理由同 # frozen-baseline-rebase-10-10）
 */
// 撤：RescheduleRecommendation
// 撤：EquipmentFailure

/* ===== 按 ReasonCode 分组统计（对齐 5号位 lps/LPS.APS.Core/Dto/ExplanationDtos.cs :71-76 ReasonCodeCountDto） ===== */
export interface ReasonCodeCount {
  /**
   * 原因码 —— 冻结 15 码**或空串**（实库 95% 空串，见 `ReasonCodeValue`）。
   * 空串会作为一个独立分组出现在 `reasonCodeCounts` 里（dev 实测 `{"reasonCode":"","count":516}`）。
   */
  reasonCode: ReasonCodeValue
  count: number
  totalImpactHours?: number
}

/* ===== ExplanationViewDto — 异常与原因解释（页面 6） ===== */
/**
 * 5号位 2026-10-09 函件关键纠正 3：后端无对应实体字段；
 *  改用 Summary 端点的 `errorCount / warnCount / infoCount` 三计数。
 *
 *  说明：本 DTO 是 4号位 视图聚合层（与 5号位 lps/LPS.APS.Core/Dto/ExplanationDtos.cs :48 ExplanationSummaryDto 字段对齐 + 增 4号位 业务字段）：
 *  - top 3 KPI 来自 `errorCount / warnCount / infoCount`（Summary 端点权威数），**百分比分母用 `totalCount`**
 *  - 排程解释事实卡片来自 `scheduleExplanationFacts[]`（**列表端点 `/api/explanation` 分页加载**，不是 Summary.items）
 *  - reasonCodeCounts 来自 Summary 端点直接透传
 */
export interface ExplanationViewDto {
  planVersionId: number
  /**
   * **后端权威总数**（Summary 端点 `TotalCount`，= errorCount + warnCount + infoCount）。
   *
   * 2026-11-05 修：此前页面拿「已加载条数」当分母算 KPI 百分比
   *   ⇒ dev 上出现「错误 538% / 警告 516%」（分母 50 条已加载，分子 269/258 全量计数）。
   * 百分比分母必须用这个字段（总数），不能用 `scheduleExplanationFacts.length`（已加载数）。
   */
  totalCount: number
  scheduleExplanationFacts: ScheduleExplanationFact[]
  businessFactIssues: BusinessFactIssue[]
  /** 业务事实问题总数（来自后端 PageResult.total；前端视图层"共 M 条"展示） */
  businessFactIssuesTotal: number
  /** 业务事实问题当前页码（首屏 1；翻页累加）
   *  字段名 page 来自后端真值（5号位 2026-10-10 函件已落地 PageResult）
   *  与 LPS.APS.Core.Dto.PageResult<T>.page 一致（RBAC/TaskSplitRuleConfig/ManualETA 同源）
   *  注：本仓通用类型 PageResult<T> 仍用 pageIndex（@see types/common.ts:62-67），
   *     仅 explanation 模块用此 page 字段以对齐后端真值
   */
  businessFactIssuesPage: number
  /** 业务事实问题每页条数（默认 20，对齐 Audit.vue） */
  businessFactIssuesPageSize: number
  /** Summary 端点 errorCount（v5.1.10 字段映射，@see lps/LPS.APS.Core/Dto/ExplanationDtos.cs :57） */
  errorCount: number
  /** Summary 端点 warnCount */
  warnCount: number
  /** Summary 端点 infoCount */
  infoCount: number
  /** Summary 端点 reasonCodeCounts（按 15 码分布） */
  reasonCodeCounts: ReasonCodeCount[]
}
