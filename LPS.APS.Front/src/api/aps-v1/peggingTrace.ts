/**
 * APS V1 4号位 — Supply/Pegging 追溯 API（5号位 只读查询接入）
 *
 * 端点（`PeggingTraceController`）：
 *  - `GET /api/pegging-trace`               列表（bare 数组，**无 total**，skip/take）
 *  - `GET /api/pegging-trace/order/{id}`    按订单查（bare 数组）
 *  - `GET /api/pegging-trace/summary`       汇总（总数 + 两类分布；`items` 恒空）
 *
 * 职责边界：5号位只读 `PeggingSupplyAllocation`，不重算 Allocation。本页同样只读、零写调用。
 * 本模块**没有 mock 分支** —— 页面上的每个数字都必须来自真实端点。
 */

import { apsHttp } from './http'
import type { PeggingTraceDto, PeggingTraceQuery, PeggingTraceSummaryDto } from './types'

export const peggingTraceApi = {
  /** 分配列表（服务端分页，后端 ORDER BY AllocationSequence） */
  async list(query: PeggingTraceQuery): Promise<PeggingTraceDto[]> {
    return apsHttp.get<PeggingTraceDto[]>({
      url: '/api/pegging-trace',
      params: { ...query },
      silent: true
    })
  },

  /** 单个订单的分配明细（跨工序/下阶可能多行） */
  async listByOrder(orderId: number, planVersionId: number): Promise<PeggingTraceDto[]> {
    return apsHttp.get<PeggingTraceDto[]>({
      url: `/api/pegging-trace/order/${orderId}`,
      params: { planVersionId },
      silent: true
    })
  },

  /** 该版本的分配汇总（全量口径，不随列表筛选变化） */
  async summary(planVersionId: number): Promise<PeggingTraceSummaryDto> {
    return apsHttp.get<PeggingTraceSummaryDto>({
      url: '/api/pegging-trace/summary',
      params: { planVersionId },
      silent: true
    })
  }
}
