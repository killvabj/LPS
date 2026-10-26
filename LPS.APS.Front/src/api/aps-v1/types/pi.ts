/**
 * PI 位置 DTO —— 与后端 `PiPositionQueryDtos.cs` 逐字段对齐（5号位 2026-10-17 回执后重做）
 *
 * 端点（`PiPositionController`，`[Route("api/pi-position")]`）：
 *  - `GET /api/pi-position?planVersionId=&productionInstructionNo=&materialCode=&positionType=&stageCode=&skip=&take=`
 *      → `List<PiPositionDto>`（裸数组，**不返回 total**）
 *  - `GET /api/pi-position/summary?planVersionId=` → `PiPositionSummaryDto`
 *  - `GET /api/pi-position/{productionInstructionNo}?planVersionId=` → `List<PiPositionDto>`（该 PI 的全部位置行）
 *
 * 三个端点 `planVersionId` 均必填（缺则 HTTP 200 + code 400「planVersionId is required」）。
 *
 * 字段与冻结 `07 v5.1.13:4042-4062`（ProductionInstructionPositionSnapshot，PI位置最小快照表）一一对应。
 *
 * 实测（dev，4 个版本 × 4 段 skip 共 3500 行，2026-10-17）：
 *  - `nextStageCode` / `availableTime` / `confidence` **3500/3500 全 null** ⇒ 前端不渲染这三项
 *  - `sourceType` 全表 100% 缺键（`PositionSlice` DTO 无该属性）⇒ 不渲染
 *  - `sourceKey` 只有 `AUTO_GENERATED`（↔UNLOCATED）/ `FIRST_STAGE_FALLBACK`（↔FIRST_STAGE_PENDING）
 *    两个**兜底过程标记**，与冻结 `07:4061`「源工单/单据/位置追溯键」（例 `WO:9988`）不是一回事
 *    ⇒ 不渲染（5号位 2026-10-17 回执 §一 确认这是终态语义，不是临时占位）
 *  - `currentStageCode`、`issueCode` 有值 ⇒ 渲染；`issueCode` **原值展示不译**（冻结无码表）
 *
 * 注意：`Program.cs` 的 `JsonIgnoreCondition.WhenWritingNull` 会把 null 字段**整个键省略**，
 * 所以前端拿到的是 `undefined` 而不是 `null`，判空一律用 `== null`。
 */

import type { IsoDateTime } from './common'

/**
 * PI 位置快照一行（= 后端 `PiPositionDto`）
 *
 * 一行 = 「某个 PI 的一笔互斥剩余量份额当前位于某位置」，不是 PI 汇总。
 */
export interface PiPositionDto {
  id: number
  scheduleRunId: number
  planVersionId: number
  /** 生产指示号（PI 稳定业务键，也是详情端点的路由参数） */
  productionInstructionNo: string
  materialId: number
  materialCode: string
  /**
   * 位置类型。
   *
   * 值域未收敛（0号位 裁决中）：冻结 `07:4055` 为 `STAGE_WIP / XC / INTERPLANT_TRANSIT / WAITING_INBOUND / UNLOCATED`，
   * 后端实际落 `STAGE_WAITING / XC / INTERPLANT_TRANSIT / FIRST_STAGE_PENDING / UNLOCATED`。
   * `STAGE_WAITING` ≡ 冻结 `STAGE_WIP`（同概念、名称漂移）；`FIRST_STAGE_PENDING` 后端有冻结无；
   * `WAITING_INBOUND` 冻结有、后端未实现。过渡期 6 值全预留，未知值降级显示原值。
   *
   * 故此处用 `string` 而非窄联合类型：数据侧随时可能出现第 7 个值，窄类型会骗前端。
   */
  positionType: string
  /** 该 PI 当前位于此位置的互斥数量份额（冻结红线：Σ Position.Quantity = 本次 PI 可定位剩余边界） */
  quantity: number
  /** 当前阶段（PositionType 允许时填写；实测 62% 非空） */
  currentStageCode?: string
  /** 下一需执行阶段（实测恒 null，前端不渲染） */
  nextStageCode?: string
  /** 可用时间（在途/等待等事实真正可供下一阶段使用的时间；实测恒 null，前端不渲染） */
  availableTime?: IsoDateTime
  /**
   * 来源类型（`07:4061`：MES_STAGE / XC / INTERPLANT_TRANSIT / RECEIVED / DERIVED 等）。
   * 实测全表 100% 缺键（后端 `PositionSlice` DTO 无该属性）⇒ 前端不渲染。
   */
  sourceType?: string
  /**
   * 来源键 —— **前端不渲染**（5号位 2026-10-17 回执 §一 确认）。
   *
   * 全表只有两个取值，且与 PositionType 1:1：
   *   - `AUTO_GENERATED`         ↔ UNLOCATED              （数量兜底路径）
   *   - `FIRST_STAGE_FALLBACK`   ↔ FIRST_STAGE_PENDING    （首 Stage 兜底路径）
   *   - 缺键                     ↔ STAGE_WAITING / XC
   *
   * 冻结 `07:4061` 定义它是「源工单/单据/位置追溯键，例 `WO:9988`」——上面两个常量是**兜底过程标记**，
   * 不是源单据键（这两条路径本来就没有源单据，属终态语义，不是临时占位）。
   * 渲染它等于告诉用户「有溯源」，反而误导。
   * ⚠️ STAGE_WAITING / XC 本应有源单据键却全缺 —— 属上游事实缺口（2号位/1号位），不在前端。
   */
  sourceKey?: string
  /**
   * 位置问题码 —— **原值展示，不译中文**（冻结无码表，自造词典就是造假）。
   *
   * 5号位 2026-10-17 回执 §二澄清：**不是 5号位 自拟**，是 2号位
   * `PeggingOrchestrator.cs:5369` 按「Position 侧优先、ERP 源侧次之」写的一条码，
   * 受控枚举 5 值：`QUANTITY_GAP` / `POSITION_FAILED` / `ERP_FACT_MISSING` /
   * `ERP_SOURCE_MISMATCH` / `ERP_QUANTITY_INVALID`。
   * 冻结 `07:4062` 只有开放描述、无值域定义 ⇒ 属基线欠账（2号位/0号位）。
   * 实测：`QUANTITY_GAP` 只出现在 STAGE_WAITING / XC；UNLOCATED 行全缺键
   * （UNLOCATED 正是用来补平的差额，本身不代表异常）。
   */
  issueCode?: string
  /** 置信标记（`CONFIRMED / CONSERVATIVE / UNLOCATED` 等；实测恒 null，前端不渲染） */
  confidence?: string
  /** 快照时间 */
  createdAt: IsoDateTime
}

/** `/summary` 里的一行类型聚合（后端 `PiPositionSummaryDto.PositionTypeCounts[]`） */
export interface PiPositionTypeCountDto {
  positionType: string
  /** 该类型的**全部**位置条数（不是当前页条数） */
  count: number
  /** 该类型的全部位置数量合计 */
  totalQuantity: number
}

/** `GET /api/pi-position/summary` 响应 */
export interface PiPositionSummaryDto {
  planVersionId: number
  /** 该版本的 PI 数（按 ProductionInstructionNo 去重） */
  piCount: number
  /** 该版本的位置条数（= 明细端点的 total 口径；明细端点本身不返回 total） */
  totalPositions: number
  positionTypeCounts: PiPositionTypeCountDto[]
  /**
   * 后端**从不填充**（5号位 2026-10-17 回执 §二：KPI 走 summary、列表走 `/api/pi-position`，
   * `Items` 是历史结构、无填充计划）。前端不消费该字段。
   */
  items?: PiPositionDto[]
}

/** 列表查询条件 —— 五个查询参数全部对应 `PiPositionController.Query` 的真实签名 */
export interface PiPositionQuery {
  /** 必填 */
  planVersionId: number
  productionInstructionNo?: string
  materialCode?: string
  positionType?: string
  /** 后端按 `currentStageCode` 匹配（实测确认） */
  stageCode?: string
  /** 深页偏移 */
  skip?: number
  /**
   * 单页条数；后端上限 500。
   * 2026-10-17 已修（5号位 回执 §三）：原来 `>500` **静默回落 100**，现为 `>500` → **夹到 500**、
   * `<=0` → 100（默认值）。实测 take=501/1000 均返 500。前端仍自夹 ≤500（双保险）。
   */
  take?: number
}
