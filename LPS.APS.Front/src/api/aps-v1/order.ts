/**
 * APS V1 4号位 — 订单 / 需求计划查询 API
 *
 * @owner 5号位（Pegging / Task 等运行结果的真 Owner 是 2号位，5号位只做查询接入与 DTO 封装）
 * @see 冻结 `06 v1.38 §7.2`「5号位向4号位提供：订单/需求计划」
 *
 * 端点（`OrderQueryController.cs`）：
 *  - `GET /api/order-query`                  列表（**服务端分页**，返 `PageResult<OrderListItemDto>`）
 *  - `GET /api/order-query/summary`          订单状态聚合
 *  - `GET /api/order-query/{orderId:long}`   详情（order / pegging / tasks）
 *
 * 三个端点的 `planVersionId` 全部必填 —— 漏传一律 `code=400 "planVersionId is required"`（实测）。
 *
 * 本模块**不再有 mock 分支**：原订单 mock 桩建立在一份自拟 DTO 上（工厂 `F-SUZ-01`、产品族
 * `INJECTION`、客户 `CUST-001` 全是编的），与 dev 实库零重合；mock 模式下本页直接空态。
 *
 * 2026-11-18：新增 `getPool()`（P09 选单池）——依据 5号位 2026-11-18《知会C2-P09选单池pool已落码》
 *   （r14268～r14270）；新老判据=ACTIVE PV 参与关系，**禁按创建时间/单号前缀猜**。
 */

import { apsHttp } from './http'
import type {
  DeliveryKpiResultDto,
  OrderDetailDto,
  OrderListItemDto,
  OrderListPageResult,
  OrderPoolQuery,
  OrderPoolResultDto,
  OrderQuery,
  OrderSummaryDto,
  OrderTreeResultDto
} from './types'

export const orderApi = {
  /** 订单列表（服务端分页）
   *
   * 分页参数用 `pageIndex`(1 起) / `pageSize`；后端 `pageSize>0` 时优先于 `skip/take`。
   * 2026-10-17 起：`>200` **夹到 200**、`<=0` → 50（原来是越界静默回落 50，5号位 已修）。
   * 前端仍自夹 ≤200 作双保险，见 store。
   */
  async list(query: OrderQuery): Promise<OrderListPageResult<OrderListItemDto>> {
    return apsHttp.get<OrderListPageResult<OrderListItemDto>>({
      url: '/api/order-query',
      params: { ...query },
      // 查不动时**留在页内**（页面已有「加载失败：…」错误条）——
      // 整页跳 /500 会把用户已填的筛选、勾选、页码全丢掉。
      // 5号位 2026-10-31 回执 §六-2 支持前端这么改。
      noErrorRedirect: true
    })
  },

  /** 订单详情（含 Pegging 承接 + 生产任务）
   *
   * ⚠️ `planVersionId` 必填 —— 后端 `[FromQuery] int planVersionId` 无默认值，
   * 漏传得到 400，页面 Drawer 会整块空。
   */
  async getDetail(orderId: number, planVersionId: number): Promise<OrderDetailDto> {
    return apsHttp.get<OrderDetailDto>({
      url: `/api/order-query/${orderId}`,
      params: { planVersionId },
      silent: true,
      // 详情失败在抽屉里显示（`detailError`），不整页跳 /500
      noErrorRedirect: true
    })
  },

  /** 订单状态聚合（顶部 KPI）
   *
   * ⚠️ `planVersionId` 必填 —— `GetSummaryAsync` 首行即 `throw new ArgumentException("planVersionId is required")`。
   *
   * 返回**原始计数**，不做状态名映射：`delayedCount` 是 `FIRST_DELAY + REPEATED_DELAY` 合计，
   * 不是一个叫「延期」的状态；`riskCount` / `unscheduledCount` 前端不消费（见 `OrderSummaryDto` 注释）。
   */
  async getSummary(planVersionId: number): Promise<OrderSummaryDto> {
    return apsHttp.get<OrderSummaryDto>({
      url: '/api/order-query/summary',
      params: { planVersionId },
      silent: true,
      // 聚合失败只让 KPI 整块空态（store 侧处理），不整页跳 /500
      noErrorRedirect: true
    })
  },

  /**
   * 交付 KPI 分区聚合（5号位 2026-11-17 B2，r14250～r14255 已落码；dev 实测 11-17）
   *
   * `GET /api/order-query/delivery-kpi?planVersionId=…&<与列表同名同义的筛选参数>`
   *
   * ⚠️ **筛选参数与列表端点同名同义**（多了个 `riskLevel`，本页不用）⇒
   *   「统计随筛选走」是端点自带的语义，前端把同一套 `query` 原样传下去即可。
   * ⚠️ **不支持按订单类型筛选**（实测传 `orderType` 被静默忽略）⇒
   *   SO / PI 两个分区只是**读数**，不是可点回列表的筛选项（列表端点没有该筛选维度）。
   * ⚠️ 越权校验同规：`factoryCode` / `domainKey` 越界 ⇒ 403（与列表一致）。
   */
  async getDeliveryKpi(query: OrderQuery): Promise<DeliveryKpiResultDto> {
    return apsHttp.get<DeliveryKpiResultDto>({
      url: '/api/order-query/delivery-kpi',
      params: { ...query },
      silent: true,
      // 聚合失败只让 KPI 整块空态（store 侧处理），不整页跳 /500
      noErrorRedirect: true
    })
  },

  /**
   * 订单完整树（**5号位 2026-11-17 B3/B4**，r14260～r14262 已落码并上 dev）
   *
   * `GET /api/order-query/{orderId}/tree?planVersionId=…`
   *
   * ⚠️ **P02 行展开与 P03 深链共用本端点**（不建第二套）：同一树、同一数据、同一正式 PV。
   * ⚠️ `planVersionId` 必填（漏传 ⇒ 真 400「planVersionId 必填」）。
   * ⚠️ 200 体内有**两个分态**，别当错误处理：
   *   `found=false`（订单不在该 PV）／`scopeDenied=true`（域外/工厂外，越权 fail-closed）。
   */
  async getTree(orderId: number, planVersionId: number): Promise<OrderTreeResultDto> {
    return apsHttp.get<OrderTreeResultDto>({
      url: `/api/order-query/${orderId}/tree`,
      params: { planVersionId },
      silent: true,
      // 取不到在面板里就地说明（分态/截断/空树各有各的说法），不整页跳 /500
      noErrorRedirect: true
    })
  },

  /**
   * P09 选单池（**5号位 2026-11-18 知会C2**，r14268～r14270 已落码）
   *
   * `GET /api/order-query/pool?planVersionId=&participation=&keyword=&orderType=&pageIndex=&pageSize=`
   *
   * 判据 = v0.2 §P09 原文「当前真实 FULL/ACTIVE ＋ 合法根订单参与关系」：
   *   `scheduled`（在 ACTIVE PV 装载集＝老订单，场景 5/6）／`fresh`（不在＝新订单，场景 1/2）／`all`。
   *   **禁 CreatedAt / 单号前缀猜新老** ⇒ 前端不做本地判新老，只翻后端给的参与位。
   *
   * ⚠️ `planVersionId` 必填（≤0 ⇒ 真 400「planVersionId 必填」）。
   * ⚠️ `participation` 非法值 ⇒ 真 400（后端 `ArgumentException`）⇒ 前端只发三个合法值。
   * ⚠️ 200 体内有**三个分态**，别当错误处理：
   *   `versionExists=false`（版本不存在）／`scopeDenied=true`（域越权）／
   *   `noActiveNote`（PV 非 ACTIVE 或域字典缺族归属 ⇒ **显示注记，不是空列表**）。
   */
  async getPool(query: OrderPoolQuery): Promise<OrderPoolResultDto> {
    return apsHttp.get<OrderPoolResultDto>({
      url: '/api/order-query/pool',
      params: { ...query },
      silent: true,
      // 取不到在面板里就地说明（分态各有各的说法），不整页跳 /500
      noErrorRedirect: true
    })
  }
}
