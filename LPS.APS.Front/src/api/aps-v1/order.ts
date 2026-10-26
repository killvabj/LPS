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
 */

import { apsHttp } from './http'
import type {
  OrderDetailDto,
  OrderListItemDto,
  OrderListPageResult,
  OrderQuery,
  OrderSummaryDto
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
      params: { ...query }
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
      silent: true
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
      silent: true
    })
  }
}
