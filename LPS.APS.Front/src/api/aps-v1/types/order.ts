/**
 * 订单 / 需求计划 DTO —— 与后端逐字段对齐（5号位 2026-10-17 回执后重做）
 *
 * 端点（`OrderQueryController.cs:28-170`，`[Route("api/order-query")]`）：
 *  - `GET /api/order-query?planVersionId=&...&pageIndex=&pageSize=`
 *      → `ApiResponse<PageResult<OrderListItemDto>>`（**服务端分页**，body.data = `{items,total,page,pageSize}`）
 *  - `GET /api/order-query/summary?planVersionId=` → `ApiResponse<OrderSummaryDto>`
 *  - `GET /api/order-query/{orderId:long}?planVersionId=` → `ApiResponse<OrderDetailDto>`
 *
 * **三个端点的 `planVersionId` 全部必填**（实测漏传一律 `code=400 "planVersionId is required"`）。
 *
 * DTO 源文件：`lps/LPS.APS.Core/Dto/OrderQueryDtos.cs:6-84` + `OrderSummaryDto.cs:6-25`。
 *
 * 实测（dev，2026-10-17，planVersionId=24 实有 21,518 行；planVersionId=157 实有 1 行）：
 *  - 字段名 **`MTS_InstructionNo` → JSON `mtS_InstructionNo`**（System.Text.Json CamelCase 只压首字母大写段）
 *  - `status` 实库值随版本而异（dev 2026-10-19 实测）：**新夜跑版 `PLANNED`**（v190 22,017 行、v195 1 行，
 *    0号位 10-17 裁定后 SP 显式写 `N'PLANNED'`）／**老版本 `Open`**（v2/v5 22,163 行，历史遗留）
 *    ⇒ 按冻结 `07:3611` 六值词表渲染，`Open` 作为历史遗留单列（见 `common.ts` ORDER_STATUSES 注释）
 *  - `delayStatus` 实测 `ON_TIME` 13,577 + `FIRST_DELAY/REPEATED_DELAY` 7,941 = 全量 21,518（**无 NULL 行**）
 *  - `customerName` / `customerSegment` **v24 全量 12,880/21,518 = 59.9%** 有值（其余因 `WhenWritingNull`
 *    整键省略 ⇒ `undefined`）。缺失源于 **ERP 源头**（`ERP_Order_Staging` 仅 45.8% 带客户名），非本链丢弃。
 *    ⚠️ 别按「列表前 N 行」抽样估覆盖率：列表 `ORDER BY Priority DESC, CustomerDueDate`，
 *    前 1000 行只有 40.7%（高优先级子集），均匀抽样 12 页 × 200 行得 60.9% —— 以全量 60% 为准。
 *  - `demandMaturityStatus`：**V1 冻结严格 NULL**（`07:244`「禁止从任何字段临时推断」）⇒ 永远不会有值
 *  - `promisedDate`：冻结 `07:3608` 有定义（承诺交期，APS 计算产物），当前**无写入方** ⇒ 恒空
 *    ⇒ 前端不渲染这两项
 *  - 详情 `{order, pegging, tasks}` —— **没有 `reasons` 键**；v24 实测 pegging / tasks 均 0 行
 *  - 工厂/产品族/排程域在 v24 单一取值：`CN` / `CYL` / `FAMILY_X`
 *
 * 注意：`Program.cs` 的 `JsonIgnoreCondition.WhenWritingNull` 会把 null 字段**整个键省略**，
 * 所以前端拿到的是 `undefined` 而不是 `null`，判空一律用 `== null`。
 */

import type { IsoDateTime, OrderRiskLevel } from './common'

/**
 * 订单列表一行（= 后端 `OrderListItemDto`，`OrderQueryDtos.cs:6-35`）
 *
 * 一行 = 一张订单在某个 PlanVersion 下的快照，不是订单汇总。
 */
export interface OrderListItemDto {
  id: number
  planVersionId: number
  /** 订单规范化 Id（跨 PlanVersion / Domain 稳定；§10A.1「已有订单提前」用它做 payload） */
  orderCanonicalId?: number
  orderNo: string
  /** 实测取值 `PRODUCTION_INSTRUCTION`；值域未收敛，按开放字符串处理 */
  orderType: string
  materialCode: string
  materialId: number
  /** v24 全量 59.9% 有值（缺失源于 ERP 源头未带；未填的行整个键被省略） */
  customerName?: string
  /** 实测与 customerName 同批有/无（同一份来源） */
  customerSegment?: string
  factoryCode?: string
  factoryId: number
  productFamilyCode?: string
  domainKey?: string
  quantity: number
  uom: string
  customerDueDate: IsoDateTime
  /**
   * 承诺交期（`07:3608`，APS 计算产物）—— **当前无写入方，恒空** ⇒ 前端不渲染。
   * 勿与 `customerDueDate`（客户要求交期，实库 100% 有值）混用。
   */
  promisedDate?: IsoDateTime
  priority: number
  /**
   * 订单状态。
   *
   * 冻结 `07:3474` 值域 = `OPEN / CLOSED / CANCELLED`（默认 OPEN，准入条件 `WHERE Status = 'OPEN'`）；
   * 实库却是 `Open`（65,824 行）与 `CONFIRMED`（1 行）—— 大小写漂移 + 多出一个词。
   * 写入侧在 2号位（`ERPOrderSyncService.cs:279` 的 `order.Status ?? "Open"`），已归 2号位。
   *
   * 故此处用 `string` 而非窄联合：前端按冻结词表渲染、遇未知值原样降级，绝不做枚举校验（否则整表渲染空标签）。
   */
  status: string
  /**
   * 延迟状态（`Order.DelayStatus`）—— **ERP/ODS 独立维度**，冻结 `07:2688` / `07:2666`，
   * 值域 `ON_TIME / FIRST_DELAY / REPEATED_DELAY`（V1 简化超期均记 FIRST_DELAY）。
   *
   * ⚠️ **不得当「本版本是否延期」的标签用**：冻结 `01 §64` 红线明文禁止用 `Order.DelayStatus`
   *   替代当前 PlanVersion 的交付结果。页面上的交付判定一律读 `riskLevel`。
   *   本字段保留在 DTO 里（后端仍返、筛选仍可按它筛），但前端不再据此渲染状态标签。
   */
  delayStatus?: string
  /**
   * 交付风险等级 —— **计划派生**判定（5号位 2026-11-02 答复函 §一 Q1 选甲新增）。
   *
   * 判定 = 冻结 `01 §64` 唯一判定：`本版本有 Task 且 MAX(PlannedEndTime) > CustomerDueDate`
   * ⇒ `DELAYED`；有 Task 且未超交期 ⇒ `ON_TRACK`（按期与提前同归此值）。
   *
   * **缺键语义（关键）**：本版本**无 Task（未排产）或无交期** ⇒ 后端整键省略
   *   （`WhenWritingNull`）⇒ 前端渲染「未排产」，**绝不能落进「按期」**。
   *
   * dev 实测（2026-11-02，r14009 已部署）：v999333 共 310 单 —— `DELAYED` 1 单
   *   （O20630651，同时 `delayStatus=ON_TIME`，正是原「未延迟 + 超期 15.4 天」打架那一单）、
   *   其余 309 单缺键（未排产）；v999334 共 53 单 —— `ON_TRACK` 1 单、其余 52 单缺键。
   *
   * ⚠️ **只有列表端点带这个字段**：详情端点 `GET /api/order-query/{id}` 的 `order` 是另一条
   *   SQL，**不返 `riskLevel` / `delayHours` / `plannedCompletionTime`**（dev 实测）⇒
   *   抽屉要显示计划派生结论，只能取**打开抽屉的那一行列表数据**，不能对详情响应判空。
   */
  riskLevel?: OrderRiskLevel
  /**
   * 需求成熟度 —— **V1 冻结严格 NULL**（`07:244`「禁止从任何字段临时推断」），
   * 不是「还没填」而是「V1 不会有值」⇒ 前端不渲染，也不做任何推断。
   */
  demandMaturityStatus?: string
  /** MTS 指令号；后端属性名 `MTS_InstructionNo`，JSON 键为 `mtS_InstructionNo`（CamelCase 只压首段） */
  mtS_InstructionNo?: string
  bomno?: string
  /**
   * APS 预计完成时间 = 该订单在当前版本全部 Task 的 MAX(PlannedEndTime)（后端算，冻结 01:1908 第 2 项；13 §十一 禁前端自算）。
   * 未排程（无 Task 行）= null ⇒ WhenWritingNull 下**整个键被省略** ⇒ 判空用 `!= null`，禁 `=== undefined`。
   * dev 2026-10-19 实测 v190/v195/v2 各 200 行 **0 命中**（这三个版本订单都没有 Task 行）。
   */
  plannedCompletionTime?: IsoDateTime
  /**
   * 延期时长（自然小时）：仅当 预计完成日期 > 客户交期时有值；未延期 / 未排 = 键省略（两态不混，别用 0 顶替）。
   * 同上，dev 2026-10-19 实测 0 命中。
   */
  delayHours?: number
  /**
   * 主因代码（冻结 07:4966 列定义；取该订单 Explanation 事实中 ImpactHours 最大的非空 ReasonCode）。
   *
   * ⚠️ **值域不干净**：冻结 07:4937 只承认 15 码，07:4935「解释阶段门禁」明文「未新增 `UNKNOWN`、
   * `BATCH_POLICY_MISSING` 或中文Phase」——但 dev 实测（v2 前 200 行）79 行有值里 **多数不在 15 码内**
   * （`BATCH_POLICY_MISSING` 66、`MISSING_PRODUCTION_DEPARTMENT_CONTEXT` 5、中文散文 7，只有
   * `RESOURCE_CAPACITY_WAIT` 3 合规）。⇒ 渲染必须走 `REASON_CODE_LABELS` + 「待核对·」降级，**禁原样当权威码展示**。
   */
  primaryReasonCode?: string
}

/** Pegging 承接一行（`OrderQueryDtos.cs:57-70`） */
export interface OrderPeggingDto {
  allocationSequence: number
  materialCode: string
  allocatedQty: number
  supplyType: string
  supplyFactoryCode?: string
  supplyWarehouseCode?: string
  erpProperty?: string
  supplyDocumentNo?: string
  supplyDocumentType?: string
  eta?: IsoDateTime
  knownAvailableTime?: IsoDateTime
  commitmentStatus?: string
  supplyMode?: string
}

/** 生产任务一行（`OrderQueryDtos.cs:73-90`） */
export interface OrderTaskDto {
  taskId: number
  taskNo: string
  operationCode?: string
  operationSeq: number
  resourceCode?: string
  resourceName?: string
  quantity: number
  /** 计划加工数量（含良率损耗）；U05：与 `quantity`（净产出）不混淆 */
  plannedProcessQty?: number
  plannedStartTime?: IsoDateTime
  plannedEndTime?: IsoDateTime
  duration?: number
  status: string
  isCriticalPath: boolean
  isLocked: boolean
  mtS_InstructionNo?: string
}

/**
 * 订单详情（`OrderQueryDtos.cs:42-54`）
 *
 * 只有三块 —— **后端没有 `reasons` 键**（原前端 DTO 的 `reasons` 是自拟的，见文件头实测口径）。
 */
export interface OrderDetailDto {
  order: OrderListItemDto
  pegging: OrderPeggingDto[]
  tasks: OrderTaskDto[]
}

/**
 * 订单状态聚合（`OrderSummaryDto.cs:6-25`）
 *
 * ⚠️ **2026-11-02 口径全换（5号位 r14009 答复函 §二）**：三桶（`onTimeCount` /
 * `delayedCount` / `unscheduledCount`）**全部改为计划派生** —— 判据与列表 `riskLevel` 同源
 * （冻结 `01 §64`：`本版本有 Task 且 MAX(PlannedEndTime) > CustomerDueDate` 即为延期）。
 * 原口径（按 `o.DelayStatus` 聚合 = ERP 维度）已废除：那是「两个世界」，
 * 会让 KPI 与格子各说各话（v999333 曾出现「格子未延迟 / KPI 延迟 0」）。
 *
 * **恒等式**：`onTimeCount + delayedCount + unscheduledCount === totalCount`（三桶互斥且穷尽）
 * ⇒ 可直接作探针断言。dev 实测：v999333 `310 = 0 + 1 + 309`、v999334 `53 = 1 + 0 + 52`。
 *
 * `riskCount` 仍是另一张表（`OrderScheduleSummary`，dev 0 行、当前无写入方）⇒ 前端**不渲染**。
 */
export interface OrderSummaryDto {
  planVersionId: number
  /** 该版本订单总数（`COUNT(*)`，与列表 `total` 同 WHERE 口径） */
  totalCount: number
  /**
   * 按期单数（**计划派生**）：本版本**有 Task** 且未超交期。
   *
   * ⚠️ 不含「无交期」与「未排产」—— 那两类归 `unscheduledCount`（缺键 ⇒ 未排产/不可判定）。
   */
  onTimeCount: number
  /** 延期单数（**计划派生**）：本版本有 Task 且超交期（`MAX(PlannedEndTime) > CustomerDueDate`） */
  delayedCount: number
  /**
   * `OrderScheduleSummary.RiskLevel IN ('AT_RISK','DELAYED')`（冻结 `07:4965` RiskLevel 值域）。
   *
   * ⚠️ 前端**不渲染**：查的是另一张表 `OrderScheduleSummary`，dev 实测 v24 = 0，
   * **无法区分「无风险」与「该表无数据」** —— 两者返回的都是 0。渲染它等于告诉用户「零风险」，
   * 而事实是「不知道」。要展示需后端补一个「该表行数」或风险卡下线。
   */
  riskCount: number
  /**
   * 未排产 / 不可判定单数（**计划派生**，2026-11-02 起为**实值**，不再是 `CAST(0 AS INT)` 字面量）：
   * 本版本**无 Task 行**（未排产）**或无交期**的订单数。
   *
   * 与 `onTimeCount + delayedCount` 互斥且穷尽 ⇒ 三桶之和恒等于 `totalCount`。
   * dev 实测：v999333 = 309、v999334 = 52（这两个真实小版本「绝大多数未排产」是正常形态，勿当异常）。
   */
  unscheduledCount: number
}

/** 列表查询条件 —— 全部对应 `OrderQueryController.QueryOrders` 的真实签名（`OrderQueryController.cs:55-72`） */
export interface OrderQuery {
  /** 必填（漏传 code=400） */
  planVersionId: number
  orderNo?: string
  materialCode?: string
  /**
   * 客户名称（`o.CustomerName` 模糊匹配）。
   *
   * 注意**没有 `customerCode`** —— 后端 `[Order]` 表无该列（`OrderListItemDto` 也无该属性）。
   * 实库 `CustomerName` 全量约 60% 有值（ERP 源头约 46%），故只能做「有值才筛得到」的名称筛，
   * 不能做编码筛（`[Order]` 无 `CustomerCode` 列）。
   */
  customerName?: string
  factoryCode?: string
  productFamilyCode?: string
  domainKey?: string
  delayStatus?: string
  status?: string
  /** 映射 `o.MTS_InstructionNo` 模糊匹配；实库形如 `17092207`（**无 `PI` 前缀**） */
  productionInstructionNo?: string
  dueDateFrom?: string
  dueDateTo?: string
  /** 1 起（后端 `pageIndex>0` 时按 `(pageIndex-1)*take` 换算 skip） */
  pageIndex?: number
  /**
   * 单页条数；后端 `OrderQueryService` 上限 **200**。
   *
   * 2026-10-17 已修（5号位 回执 §三.3）：原来 `>200` 静默回落 **50**，
   * 现为 `>200` → **夹到 200**、`<=0` → 50（默认值）。实测 pageSize=201/500 均回显 200。
   * 前端仍自夹 ≤200（双保险）。
   */
  pageSize?: number
}

/** 订单列表分页响应（`PageResult<T>`：字段名是 `page` 不是 `pageIndex`，与 TaskSplitRuleConfig 等同款） */
export interface OrderListPageResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}
