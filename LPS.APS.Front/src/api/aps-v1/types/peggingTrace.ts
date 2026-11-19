/**
 * APS V1 4号位 — Supply/Pegging 追溯 DTO
 *
 * 契约源（逐字段回读，不是自拟）：
 *  - `lps/LPS.APS.Core/Dto/PeggingTraceDtos.cs`（`PeggingTraceDto` / `PeggingTraceSummaryDto`）
 *  - `lps/LPS.APS.Web/Controllers/PeggingTraceController.cs`
 *      GET /api/pegging-trace?planVersionId=&materialCode=&supplyType=&commitmentStatus=
 *                             &orderNo=&supplyDocumentNo=&skip=&take=
 *      GET /api/pegging-trace/order/{orderId}?planVersionId=
 *      GET /api/pegging-trace/summary?planVersionId=
 *    权限 `aps.plan.view`（`PermissionCodes.PlanView`）
 *
 * 冻结依据：
 *  - `06 v1.38 §7.2` 5号位向4号位提供「Supply/Pegging追溯」
 *  - `09 v2.0 §六` 用户输出验收「Supply/Pegging追溯」
 *  - `03 v2.0` 用户输出边界「Supply/Pegging追溯」
 *  - `13 v1.8 §七` 页面展示「Supply/Pegging追溯」
 *  - `01 v1.13:1913` 历史 PlanVersion 下对应 Pegging 关系
 *
 * ⚠️ 后端 `Program.cs` 注册了 `WhenWritingNull` ⇒ **null 字段整个键不出现**（不是 null）。
 *   实测 v2 首行只出现 13 个键，`rootOrderNo` / `commitmentStatus` / `supplyDocumentNo` 等
 *   全部缺键 ⇒ 判空一律用 `== null` / `field ?? '默认'`，**禁止 `=== null`**。
 */

import type { IsoDateTime } from './common'

/** 一条 Pegging 分配（PeggingSupplyAllocation 的只读投影） */
export interface PeggingTraceDto {
  id: number
  planVersionId: number
  scheduleRunId: number
  /** 分配顺序（后端 `ORDER BY AllocationSequence` 升序） */
  allocationSequence: number

  // ===== 需求侧 =====
  rootOrderId?: number | null
  rootOrderNo?: string | null
  /** 当前需求所属订单（下阶展开时可能不同于根订单） */
  currentOrderId?: number | null
  currentOrderNo?: string | null
  orderType?: string | null
  materialCode: string
  materialId: number
  demandFactoryCode?: string | null
  demandStageCode?: string | null
  demandQty?: number | null

  // ===== 分配结果 =====
  allocatedQty: number

  // ===== 供给侧 =====
  /**
   * 供给类型。**值域以本次响应为准**，不要套用页面上别处的 9 类枚举：
   * dev v2 实测为 `INVENTORY / PIPELINE / WIP / INTER_FACTORY_ORDER`，
   * 与 `types/common.ts` 的 `SUPPLY_TYPES`（文档第 13 节来源分类）**不是同一集合**。
   */
  supplyType: string
  supplyFactoryCode?: string | null
  supplyWarehouseCode?: string | null
  /** ⚠️ System.Text.Json CamelCase 只压首段 ⇒ 该键名是 `eRPProperty`，不是 `erpProperty` */
  eRPProperty?: string | null
  supplyDocumentType?: string | null
  supplyDocumentNo?: string | null
  supplyMode?: string | null
  crossFactoryEdgeId?: number | null
  transportLeadTimeHours?: number | null

  // ===== 时间 =====
  eta?: IsoDateTime | null
  knownAvailableTime?: IsoDateTime | null
  /** `COMMITTED / CONFIRMED / ESTIMATED / NOT_COMMITTED` 等；dev v2 实测大量缺键（汇总计为 UNKNOWN） */
  commitmentStatus?: string | null

  // ===== Stage 路径 =====
  attachStageCode?: string | null
  completedStageCode?: string | null
  nextRequiredStageCode?: string | null

  createdAt: IsoDateTime
}

/** 按 SupplyType 的汇总行 */
export interface SupplyTypeCountDto {
  supplyType: string
  count: number
  totalAllocatedQty: number
}

/** 按 CommitmentStatus 的汇总行 */
export interface CommitmentStatusCountDto {
  commitmentStatus: string
  count: number
  totalAllocatedQty: number
}

/** `GET /api/pegging-trace/summary` */
export interface PeggingTraceSummaryDto {
  planVersionId: number
  /**
   * 该版本 Pegging 分配总条数。
   *
   * ⚠️ 口径（2026-11-14 5号位 R2.4 裁定）：**收进用户数据范围**（同列表的
   *   `TraceScopePredicate`）⇒ 受限账号读到的是**本账号可见**的笔数，不再是全厂数。
   *   但**仍不随列表筛选变化**（summary 不带六个筛选谓词）⇒ 它是「本版本、本账号可见」的总数。
   */
  totalAllocations: number
  supplyTypeCounts: SupplyTypeCountDto[]
  commitmentStatusCounts: CommitmentStatusCountDto[]
  /**
   * 后端 DTO 里有该字段，但当前实现恒为空数组（dev v2 实测）。
   * ⇒ 列表数据一律走 `GET /api/pegging-trace`，不要指望这里。
   */
  items: PeggingTraceDto[]
  /**
   * 供给事实批次**是否在**（2026-11-17 5号位 r14240/r14241 落码，dev 实测键存在）。
   *
   * 三态，**不是布尔**：
   *  - `true`  ⇒ 供给批次在（本版有可归集的供给事实来源）
   *  - `false` ⇒ **源批缺**：没有可用的供给事实批次 ⇒ 归集不到分配记录
   *  - `null` / 键缺 ⇒ **未判定**：后端没给这一位 ⇒ 页面不得替它下结论
   *
   * ⚠️ 与「版本级 `sourceScheduleRunId` 为空」是**两层判据**，不合并：
   *   前者说「供给事实批次在不在」，后者说「这个计划版本有没有记到来源排程批次」。
   */
  supplyBatchPresent?: boolean | null
  /** 供给事实批次号（`supplyBatchPresent=true` 时给出，如 `PIPELINE_20261117_122517`） */
  supplyBatchNo?: string | null
}

/**
 * `GET /api/pegging-trace/count`（5号位 2026-11-14 落码，形状 B）
 *
 * - 参数与列表端点**完全相同**（planVersionId + 六个筛选谓词，不带 skip/take）。
 * - WHERE 文本与列表**同源单源**（`TraceWhereCore` + `TraceScopePredicate`）
 *   ⇒ total 对受限账号也是真的（dev 实测 pmc 17,181 ＝ 其列表真实行数）。
 * - 沿用统一 `ApiResponse` 信封，`data` 只有 `total` 一个字段。
 */
export interface PeggingTraceCountDto {
  total: number
}

/** 列表查询（`skip` 从 0 起；`take` 后端 >500 或 <=0 时静默回落 100 ⇒ 前端自夹 ≤200） */
export interface PeggingTraceQuery {
  planVersionId: number
  materialCode?: string
  supplyType?: string
  commitmentStatus?: string
  /** 订单号模糊（后端 LIKE 命中 `RootOrderNo` / `CurrentOrderNo`） */
  orderNo?: string
  /** 根订单 Id 精确（后端 `RootOrderId = @RootOrderId`；实测 100% 非空） */
  rootOrderId?: number
  supplyDocumentNo?: string
  skip?: number
  take?: number
}
