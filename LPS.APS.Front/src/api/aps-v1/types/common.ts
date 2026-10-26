/**
 * APS V1 4号位前端 — 公共类型与枚举
 *
 * 依据：《APS_V1_4号位页面与业务操作开发实施包_v1.0_20260814.md》
 *  - 文档第 6 节：Task 正式状态仅 5 种，禁止 SCHEDULED/PAUSED/SUSPENDED/WAITING/RUNNING
 *  - 文档第 15 节：PlanVersion 状态机（ACTIVE/BUILDING/CANDIDATE/FAILED/ARCHIVED）
 *  - 文档第 19 节：DTO 设计原则（后端契约 → 前端 TS）
 *
 * 设计要点：
 *  - 后端 PascalCase 字段映射到前端 camelCase（API 层做映射）
 *  - 日期统一 ISO 字符串（前端不做时区运算，UI 层 dayjs 转换）
 *  - 枚举使用字符串联合类型，编译期校验，避免魔法字符串
 *  - 占位 DTO 字段标注 `@see 4号位文档第 X 节`，等 3 号位落地后对齐
 */

/**
 * 后端 ApiResponse 包装（与 LPS.APS.Shared/Models/ApiResponse.cs 一致）
 *
 * 成功示例：{ code: 200, message: 'success', data: {...}, traceId: '...', timestamp: '...' }
 * 失败示例：{ code: 400, message: '...', data: null,  traceId: '...', timestamp: '...' }
 *
 * 业务码：200/400/401/403/500；前端按 code === 200 判定成功
 */
export interface ApiResponse<T> {
  code: number
  message: string
  data: T | null
  traceId?: string
  timestamp: string
}

/** 与后端约定：HTTP 200 / 201 都表示业务成功
 *  - 200 OK（多数端点）
 *  - 201 Created（GovernanceController 的 POST CreatedAtAction 路径）
 *  - 3 号位是否统一包装见 [frontNew/docs/3号位契约-依赖与扩展项.md] §八.③
 */
export const APS_SUCCESS_CODE = 200
export const APS_SUCCESS_CODES = [200, 201] as const
export const isApsSuccessCode = (code: number): boolean =>
  (APS_SUCCESS_CODES as readonly number[]).includes(code)

/** 业务码常量 */
export const APS_ERROR_CODE = {
  BAD_REQUEST: 400,
  UNAUTHORIZED: 401,
  FORBIDDEN: 403,
  NOT_FOUND: 404,
  INTERNAL: 500
} as const

/** 分页请求
 *  - 10-12 收：PageQuery / PageResult 通用类型已撤
 *  - 各模块改用本地包装：ManualEtaPageResult / PagedTaskSplitRuleConfig / BusinessFactIssueResponsePage / 新增 ManualCapacitySlotPageResult
 *  - 字段名统一为 `page`（后端 LPS.APS.Core.Dto.PageResult<T>.Page 真值）
 *  - 此前 PageResult<T> 字段名 pageIndex 与后端 page 不一致，9 文件本地包装已隔离；删通用类型避免误用
 */
// （PageQuery / PageResult 已删，@see frontNew/docs/4号位-2026-10-12-三函闭环RC22-ME13-E24-给5号位.md）

/**
 * 4号位文档第 6 节：Task 正式状态仅 5 种
 * 禁止 SCHEDULED / PAUSED / SUSPENDED / WAITING / RUNNING
 *
 *  - PLANNED      已排产，等待释放
 *  - RELEASED     已释放给下游（MES 资格中）
 *  - IN_PROGRESS  执行中（实际开工）
 *  - COMPLETED    已完成
 *  - CANCELLED    已取消
 */
export const TASK_STATUSES = [
  'PLANNED',
  'RELEASED',
  'IN_PROGRESS',
  'COMPLETED',
  'CANCELLED'
] as const

export type TaskStatus = (typeof TASK_STATUSES)[number]

/** UI 层用：状态中文标签 */
export const TASK_STATUS_LABELS: Record<TaskStatus, string> = {
  PLANNED: '已排产',
  RELEASED: '已释放',
  IN_PROGRESS: '执行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消'
}

/**
 * PlanVersion 生命周期状态（冻结文档 07 §4.2:3552 值域 + 2号位 2026-10-15 词表统一）
 *
 *  - BUILDING    待发令（排程启动建记录时的初始态）
 *  - CANDIDATE   算完待确认
 *  - ACTIVE      已生效 —— **「正式采用」直接看 Status = 'ACTIVE'**（07 §4.2:3573）
 *  - FAILED      失败
 *  - ARCHIVED    已被同域新版顶替
 *  - Computing   计算中（实现子状态：发令枪防重复领取，非冻结 DDL 值）
 *
 * 历史：2026-10-15 前代码曾偏离为 Created/Computed/ComputeFailed，
 *       2号位 已统一回冻结词表（Created→BUILDING、Computed→CANDIDATE、ComputeFailed→FAILED）
 *       ⇒ 后端**永不**返 Computed/ComputeFailed，这两个 key 是死分支，已删（5号位 10-15 §九）。
 * 注：CTP / INSERT_IMPACT_ANALYSIS 永远不能成为 PlanVersion（验收 U11/U12），
 *     因此枚举里也不包含这两种业务用途。
 */
export const PLAN_VERSION_STATUSES = [
  'BUILDING',
  'ACTIVE',
  'CANDIDATE',
  'FAILED',
  'ARCHIVED',
  'Computing'
] as const

export type PlanVersionStatus = (typeof PLAN_VERSION_STATUSES)[number]

/** UI 层用：PlanVersion 状态中文标签（5号位 2026-10-15 §一 指定渲染口径） */
export const PLAN_VERSION_STATUS_LABELS: Record<PlanVersionStatus, string> = {
  BUILDING: '待发令',
  ACTIVE: '已生效',
  CANDIDATE: '算完待确认',
  FAILED: '失败',
  ARCHIVED: '已被新版本顶替',
  Computing: '计算中'
}

/** UI 层用：PlanVersion 状态 → ElTag type（4号位 全前端 owner：版本切换条 / Overview 状态徽章共用） */
export const PLAN_VERSION_STATUS_TAG: Record<
  PlanVersionStatus,
  'success' | 'primary' | 'warning' | 'info' | 'danger'
> = {
  BUILDING: 'primary',
  ACTIVE: 'success',
  CANDIDATE: 'warning',
  FAILED: 'danger',
  ARCHIVED: 'info',
  Computing: 'primary'
}

/** 后端返未知状态（新词表 / 老 dev 库）时的兜底，禁止页面因未知值崩 */
export const planVersionStatusLabel = (raw: string | null | undefined): string =>
  raw ? (PLAN_VERSION_STATUS_LABELS[raw as PlanVersionStatus] ?? raw) : '-'

export const planVersionStatusTag = (
  raw: string | null | undefined
): 'success' | 'primary' | 'warning' | 'info' | 'danger' =>
  raw ? (PLAN_VERSION_STATUS_TAG[raw as PlanVersionStatus] ?? 'info') : 'info'

/** 4号位文档第 6 节：Task 不可移动标识（UI 显示，不允许拖动改库） */
export type TaskLockMarker = 'EXECUTION' | 'FIRM' | 'FROZEN'

/**
 * 订单状态（`[Order].Status`）—— 冻结 `07:3611`（`[Order]` 表 `Status` 行）权威**六值**
 *
 * ⚠️ 2026-10-19 方向已反转，勿再改回三值：
 *   本文件此前取「数据流向」一侧的 `07:3474` 三值（`Order_Canonical.Status` =
 *   `OPEN/CLOSED/CANCELLED`），理由是 `sp_SyncOrdersToPartitionTable` 原样复制 `oc.Status`。
 *   **该推理已被 0号位 第二十~二十三轮（10-17）裁定否掉**：两表是**不同语义层、本就不冲突**，
 *   且存储过程早已整改 —— SP `modify_date = 10-17 16:49` **显式写 `N'PLANNED'`**，不再复制
 *   Canonical 值（5号位 10-19 勘误段原文）。
 *   实库实测（dev 2026-10-19）：v190 = **22,017 行全部 PLANNED**、v195 = 1 行 PLANNED。
 *   ⇒ 现行写入值就是六值里的 `PLANNED`；继续按三值渲染会把 2 万余行渲染成「未知值原样英文」。
 *
 * ⇒ DTO 一律用开放 `string`；命中本词表按中文标签渲染，仍未认识的**原样显示**（不崩、不空标签）。
 */
export const ORDER_STATUSES = [
  'NEW',
  'PLANNED',
  'CONFIRMED',
  'IN_PROGRESS',
  'COMPLETED',
  'CANCELLED'
] as const
export type OrderStatus = (typeof ORDER_STATUSES)[number]

/**
 * 历史遗留值 `Open` —— **不在冻结六值域内**，故单列、不并进 `OrderStatus`。
 *
 * 它是 0号位 裁定前的老 SP 把 Canonical 值原样复制进 `[Order].Status` 的产物，裁定后不再新产生；
 * 但老计划版本仍是它（dev v2/v5 实测 22,163 行 `Open`）⇒ 仍需中文标签、仍需能筛。
 */
export type OrderStatusDisplay = OrderStatus | 'OPEN'

/** UI 层用：订单状态中文标签（未知值由调用方 `?? raw` 降级原样显示） */
export const ORDER_STATUS_LABELS: Record<OrderStatusDisplay, string> = {
  NEW: '新建',
  PLANNED: '计划中',
  CONFIRMED: '已确认',
  IN_PROGRESS: '执行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消',
  // 历史遗留 OPEN：老 SP 从 Order_Canonical 复制而来，非冻结 07:3611 六值
  OPEN: '进行中'
}

/**
 * 筛选下拉可用项 —— **只放实测真查得到的值**，否则就是「选了必空」的假筛选。
 *
 * dev 2026-10-19 逐值实测（`GET /api/order-query`）：
 *  - `PLANNED`：v190 total=22,017、v195 total=1 ⇒ 现行写入值
 *  - `OPEN`：v2/v5 total=22,163 ⇒ 历史遗留值（CI 等价，老 SP 产物）
 *  - 六值里其余五值 `NEW / CONFIRMED / IN_PROGRESS / COMPLETED / CANCELLED` 在域无产出
 *    （实测 total=0）⇒ **入词表、不入下拉**：字段真出现时能渲染，但不让用户去选一个空结果
 *    （⚠️ 5号位 10-19 勘误段此处写的是「其余四值」，按六值减一只有五个，此处按实测的五值列全）
 */
export const ORDER_STATUS_FILTER_OPTIONS = ['PLANNED', 'OPEN'] as const

/** 后端实返 `Open`（大写不敏感归一回 `OPEN`）；未进本表的原样交给调用方降级 */
export const normalizeOrderStatus = (raw: string | null | undefined): OrderStatusDisplay | null => {
  if (!raw) return null
  const upper = String(raw).toUpperCase()
  return upper in ORDER_STATUS_LABELS ? (upper as OrderStatusDisplay) : null
}

/**
 * 订单延迟状态（`Order.DelayStatus`）—— 冻结 `07:2666` / `07:277` / `07:3494` / `07:3625`
 *
 * 值域三种：`ON_TIME`（未延迟）/ `FIRST_DELAY`（首次延迟）/ `REPEATED_DELAY`（二次及以上延迟）。
 * 冻结 `07:277` 另注明 **V1 简化：FIRST_DELAY / REPEATED_DELAY 暂不区分，超期均记为 FIRST_DELAY**。
 *
 * 与 `Order.Status`（`OPEN/CLOSED/CANCELLED`）是**两个独立维度**，冻结明文禁止与 DemandMaturityStatus 混用。
 */
export const ORDER_DELAY_STATUSES = ['ON_TIME', 'FIRST_DELAY', 'REPEATED_DELAY'] as const
export type OrderDelayStatus = (typeof ORDER_DELAY_STATUSES)[number]

/** UI 层用：延迟状态中文标签（未知值原样降级显示） */
export const ORDER_DELAY_STATUS_LABELS: Record<OrderDelayStatus, string> = {
  ON_TIME: '未延迟',
  FIRST_DELAY: '首次延迟',
  REPEATED_DELAY: '重复延迟'
}

/** 4号位文档第 13 节：Supply 来源类型 */
export const SUPPLY_TYPES = [
  'INVENTORY',
  'PI',
  'PO',
  'VMI',
  'ARRIVED_NOT_INBOUND',
  'INTERPLANT_TRANSIT',
  'RECEIVED',
  'PLANNED_PRODUCTION',
  'PLANNING_PURCHASE_PLACEHOLDER'
] as const

export type SupplyType = (typeof SUPPLY_TYPES)[number]

/** 4号位文档第 2.2 节：页面必须区分四类信息标签 */
export type FactType = 'FACT' | 'RESULT' | 'ESTIMATED' | 'RECOMMENDATION'

/** UI 层用：四类信息标签中文映射 */
export const FACT_TYPE_LABELS: Record<FactType, string> = {
  FACT: '事实',
  RESULT: '计算结果',
  ESTIMATED: '估算',
  RECOMMENDATION: '建议'
}

/**
 * PI 位置类型（PositionType）
 *
 * 值域未收敛，已上报 0号位 裁决（5号位 2026-10-17 回执 §1.2），过渡期 6 值全预留：
 *  - 冻结 `07 v5.1.13:4055`：`STAGE_WIP / XC / INTERPLANT_TRANSIT / WAITING_INBOUND / UNLOCATED`
 *  - 后端实际落库：`STAGE_WAITING / XC / INTERPLANT_TRANSIT / FIRST_STAGE_PENDING / UNLOCATED`
 *
 * 其中只有 `STAGE_WIP ↔ STAGE_WAITING` 是同概念改名；
 * `WAITING_INBOUND`（冻结有、后端未实现）与 `FIRST_STAGE_PENDING`（后端有、冻结无）**不可互映射**。
 *
 * 原先这里是自拟的 `ERP_REMAINING / PRODUCTION / TRANSIT / WAITING / UNLOCATED / CROSS_STAGE`，
 * 与冻结和后端**零重合**，按该值域筛选取不到任何行 —— 2026-10-17 整组替换。
 */
export const PI_POSITION_TYPES = [
  'STAGE_WAITING',
  'FIRST_STAGE_PENDING',
  'XC',
  'INTERPLANT_TRANSIT',
  'UNLOCATED',
  'WAITING_INBOUND'
] as const
export type PiPositionType = (typeof PI_POSITION_TYPES)[number]

/**
 * 4号位文档第 14 节 + v1.4 §二十：RuleSet / ParameterSet 6 态状态机
 *
 *  - DRAFT       草稿（可编辑）
 *  - SUBMITTED   已提交（待审批流转中间态，只读）
 *  - APPROVED    已审批（待发布流转中间态，只读）
 *  - PUBLISHED   已发布（生效中）
 *  - DISABLED    已停用（含原 RETIRED 归一；前端 DTO 兼容层处理）
 *  - ARCHIVED    已归档（历史快照，只读）
 *
 * 历史：v1.0/v1.1/v1.2/v1.3 仅 3 态（DRAFT/PUBLISHED/RETIRED）；
 *       v1.4 §二十扩 6 态（冻结契约，3 号位 后端 GovernanceVersionStatus 已对齐）。
 */
export const RULE_STATUSES = [
  'DRAFT',
  'SUBMITTED',
  'APPROVED',
  'PUBLISHED',
  'DISABLED',
  'ARCHIVED'
] as const
export type RuleStatus = (typeof RULE_STATUSES)[number]

/**
 * UI 流程实际触发的 3 态子集（v1.4 §二十 6 态冻结；前端无 submit / approve / archive 按钮）
 * 用途：Rules.vue KPI 横条 + 状态过滤 Tab 的 v-for（避免永远 0 的 SUBMITTED/APPROVED/ARCHIVED 噪声）
 * 不替代 RULE_STATUSES：类型、normalize、Record 键空间仍按 6 态对齐
 */
export const RULE_STATUSES_UI = ['DRAFT', 'PUBLISHED', 'DISABLED'] as const
export type RuleStatusUI = (typeof RULE_STATUSES_UI)[number]

/**
 * 「未配版本」—— **不属于 6 态的 UI 过滤/展示标记**（10-14 增）
 *
 * 为什么需要：后端 `RuleSetListItemDto` 的 status 来自 RuleSetVersion 的 OUTER APPLY，
 * 且 `Program.cs:94` 设了 `WhenWritingNull` —— RuleSet **一条版本都没有**时 status key 整个消失。
 * 这类规则集点开必然 404，但它在 6 态里找不到正确的落点（标 PUBLISHED 是撒谎，标 DRAFT 暗示有草稿可编辑）。
 *
 * 故单独用这个字符串做 UI 标签 + 过滤值，**不**扩充 RULE_STATUSES —— v1.4 §二十 的 6 态是
 * DDL 冻结契约，不因前端展示需要而扩张（@see RuleSetSummaryDto.hasVersion）。
 */
export const NO_VERSION = 'NO_VERSION' as const
export const NO_VERSION_LABEL = '未配版本'

/** UI 层用：RuleSet / ParameterSet 状态中文标签（6 态） */
export const RULE_STATUS_LABELS: Record<RuleStatus, string> = {
  DRAFT: '草稿',
  SUBMITTED: '已提交',
  APPROVED: '已审批',
  PUBLISHED: '已发布',
  DISABLED: '已停用',
  ARCHIVED: '已归档'
}

/** UI 层用：RuleSet / ParameterSet 状态 Tag 颜色（Element Plus Tag type） */
export const RULE_STATUS_TAG: Record<
  RuleStatus,
  'success' | 'warning' | 'info' | 'primary' | 'danger'
> = {
  DRAFT: 'warning', // 黄 — 待编辑
  SUBMITTED: 'primary', // 蓝 — 中转
  APPROVED: 'info', // 灰蓝 — 中转
  PUBLISHED: 'success', // 绿 — 生效
  DISABLED: 'info', // 灰 — 失效
  ARCHIVED: 'info' // 灰 — 历史
}

/**
 * 兼容层：RETIRED → DISABLED 归一
 *
 * 后端 v1.4 已冻结 6 态不再返 RETIRED，但前端可能从：
 *  - mock fixture（v1.0-v1.3 数据）
 *  - 老 dev 环境（SQL 未迁移）
 *  - 后端字段返回大小写不一致
 * 拿到 RETIRED / retired / 未知态字符串。在 store receive 处 normalize 后再使用。
 */
export const normalizeRuleStatus = (raw: string | null | undefined): RuleStatus => {
  if (!raw) return 'DRAFT'
  const upper = String(raw).toUpperCase()
  if (upper === 'RETIRED') return 'DISABLED'
  if ((RULE_STATUSES as readonly string[]).includes(upper)) return upper as RuleStatus
  // 未知态 → DISABLED 兜底（不抛错，避免页面打不开）

  console.warn(`[RuleStatus] Unknown status from backend: ${raw}, falling back to DISABLED`)
  return 'DISABLED'
}

/**
 * 从版本列表里挑出「当前生效的已发布版本」
 *
 * ⚠️ 后端允许**多条 PUBLISHED 并存**（旧版本不会自动降级），所以不能
 *   `find(status === 'PUBLISHED')` —— 那会命中**最旧**的一版。dev 实测：
 *   RS_DEMO 的 RS-DEMO-V1（2026-07-04 生效 ~ 08-03 到期）与 RS-DEMO-V2
 *   （08-03 生效 ~ 一直）**都是** PUBLISHED，`find` 拿到 V1，
 *   页面就把已到期的旧版标成「当前生效」，而列表摘要明明写着 V2。
 *
 * 选取口径 = **Id 最大**，与后端列表 `OUTER APPLY (... ORDER BY [Id] DESC)`
 * （`RuleSetRepository.cs:95-102` / `ParameterSetRepository.cs:93-100`）完全一致
 * ⇒ 列表里的「当前版本」和详情页标「当前生效」的那一条不会打架。
 */
export function pickCurrentPublished<T extends { id: number; status?: string | null }>(
  versions: readonly T[]
): T | undefined {
  const published = versions.filter((v) => normalizeRuleStatus(v.status) === 'PUBLISHED')
  if (published.length === 0) return undefined
  return published.reduce((best, cur) => (cur.id > best.id ? cur : best))
}

/**
 * 4号位 B 设计稿 §3.4 fork 状态矩阵
 * 决定源 RuleSet 处于某状态时，[+ 新建草稿] 按钮是否点亮
 *
 *  - PUBLISHED  ✅ 主用例 + v1.4 §10 文档要求
 *  - DISABLED   ✅ 重启治理中（已退役但未归档）
 *  - DRAFT      ❌ 已有"当前编辑"，再建会冲突（提示去编辑现有草稿）
 *  - SUBMITTED  ❌ 流程中，改需先撤回
 *  - APPROVED   ❌ 已审批就等发布，不应分叉
 *  - ARCHIVED   ❌ 归档文件不修改
 *  - 未知态      ❌ 兜底禁掉
 */
export const canCreateDraftForStatus = (
  sourceStatus: RuleStatus
): { allowed: boolean; reason?: string } => {
  if (sourceStatus === 'DRAFT') {
    return { allowed: false, reason: '当前 RuleSet 已有 DRAFT；请直接编辑现有草稿' }
  }
  if (sourceStatus === 'PUBLISHED') {
    return { allowed: true }
  }
  if (sourceStatus === 'DISABLED') {
    return {
      allowed: true,
      reason: '基于已停用 RuleSet fork；新 DRAFT 将重新进入 PUBLISHED 流程'
    }
  }
  return {
    allowed: false,
    reason: `当前状态 ${RULE_STATUS_LABELS[sourceStatus] ?? sourceStatus} 不允许 fork`
  }
}

/** 4号位文档第 17 节：人工操作审计 ObjectType */
export type AuditObjectType =
  | 'CANDIDATE'
  | 'DEMAND_PROTECTION'
  | 'RULE_VERSION'
  | 'PARAMETER_VERSION'
  | 'MANUAL_RESCHEDULE'
  | 'MES_DISPATCH'

export type AuditAction = 'CONFIRM' | 'RELEASE' | 'PUBLISH' | 'RETIRE' | 'TRIGGER' | 'DISPATCH'

/**
 * 4号位 RBAC — 系统角色码（与后端 DDL v1.3 严格一致，lps/LPS.APS.Engine/Repositories/Auth/RoleRepository.cs）
 *
 * 历史：v1.0/v1.1 用 VIEWER/PMC/RULE_ADMIN/RULE_PUBLISHER/SYSTEM_ADMIN 5 个 mock 别名；
 *       v1.2 §23.1 后端落地 7 个 DDL 系统角色码（aps.* 前缀），前端对齐切换。
 *
 * 7 角色分布（前端可见的 7 个 DDL 角色码，dev seed 已写 4 个）：
 *  - aps.admin.system            系统管理员（持全部 34 权限码 + Global scope）
 *  - aps.admin.aps               规则/参数/策略管理员（v1.3 合并 RULE_ADMIN + RULE_PUBLISHER —
 *                                §二十五.3「RULE_ADMIN 不能自动拥有发布权」不再由角色码表达，
 *                                改由权限码 aps.rule.publish 区分，后端二次校验同一码）
 *  - aps.planner                 PMC 计划员（17 码：plan/ctp/candidate/manual_eta/demand_protection.view 等）
 *  - aps.viewer.management       查看员（10 码：仅 *.view）
 *  - aps.supervisor.workshop     车间主管（dev 未 seed，mock 模式按 §二十五 场景分配）
 *  - aps.coordinator.material    物料协调（dev 未 seed，mock 模式按 §二十五 场景分配）
 *  - aps.service.api             API 服务账号（dev 未 seed，无前端用户）
 */
export const ROLE_KEYS = [
  'aps.admin.system',
  'aps.admin.aps',
  'aps.planner',
  'aps.viewer.management',
  'aps.supervisor.workshop',
  'aps.coordinator.material',
  'aps.service.api'
] as const
export type RoleKey = (typeof ROLE_KEYS)[number]

/** 角色中文标签（前端 UI 用；非安全相关，纯展示） */
export const ROLE_LABELS: Record<RoleKey, string> = {
  'aps.admin.system': '系统管理员',
  'aps.admin.aps': '规则/参数管理员',
  'aps.planner': 'PMC 计划员',
  'aps.viewer.management': '查看员',
  'aps.supervisor.workshop': '车间主管',
  'aps.coordinator.material': '物料协调',
  'aps.service.api': 'API 服务账号'
}

/** 角色码 → 中文标签（未知角色码原样返回，供「当前操作者」等展示处统一使用） */
export const roleLabelOf = (code: string): string => ROLE_LABELS[code as RoleKey] ?? code

/** Domain 标识（每个产品族对应一个 Domain） */
export type DomainKey = string

/** ISO8601 字符串（前端不解析，统一 dayjs 处理） */
export type IsoDateTime = string
export type IsoDate = string

/**
 * 4号位文档第 7 节：CTP 必须明确区分 Estimated 与确定
 * 凡是依赖 PLANNING_PURCHASE_PLACEHOLDER 的供应，UI 必须显示该徽章
 */
export const ESTIMATED_BADGE_TEXT = '估算日期，采购尚无正式承诺'

/**
 * 4号位文档第 16 节：MES 下发资格判定结果
 *  UNKNOWN 后端未判定 / ELIGIBLE 可下发 / INELIGIBLE 不可下发
 */
export type MesEligibility = 'UNKNOWN' | 'ELIGIBLE' | 'INELIGIBLE'
