/**
 * APS V1 4号位 — Supply/Pegging 追溯 API（5号位 只读查询接入）
 *
 * 端点（`PeggingTraceController`）：
 *  - `GET /api/pegging-trace`               列表（bare 数组，**无 total**，skip/take）
 *  - `GET /api/pegging-trace/count`         **筛选后总条数**（5号位 2026-11-14 落码，形状 B）
 *  - `GET /api/pegging-trace/summary`       汇总（总数 + 两类分布；`items` 恒空）
 *
 * 职责边界：5号位只读 `PeggingSupplyAllocation`，不重算 Allocation。本页同样只读、零写调用。
 * 本模块**没有 mock 分支** —— 页面上的每个数字都必须来自真实端点。
 *
 * ⚠️ 订单查询为什么走**列表端点**而不是 `GET /api/pegging-trace/order/{orderId}`：
 *   专用端点只认订单 Id、不带其它筛选条件 ⇒ 用户「按订单查 + 按物料筛」要发两次请求、
 *   再在前端合并，口径容易打架。列表端点的 `orderNo`（LIKE 命中 RootOrderNo/CurrentOrderNo）
 *   与 `rootOrderId`（精确）与其它筛选项**同一条 WHERE**，一次请求即可取全该订单的行
 *   （take 上限 500，取满时页面提示可能未取全）。
 */

import { apsHttp } from './http'
import type {
  PeggingTraceCountDto,
  PeggingTraceDto,
  PeggingTraceQuery,
  PeggingTraceSummaryDto
} from './types'

export const peggingTraceApi = {
  /** 分配列表（服务端分页，后端 ORDER BY AllocationSequence） */
  async list(query: PeggingTraceQuery): Promise<PeggingTraceDto[]> {
    return apsHttp.get<PeggingTraceDto[]>({
      url: '/api/pegging-trace',
      params: { ...query },
      silent: true
    })
  },

  /**
   * 筛选后的总条数（5号位 2026-11-14 落码，形状 B）
   *
   * - 端点：`GET /api/pegging-trace/count?<与列表完全相同的筛选参数>` ⇒ `{ total }`
   * - WHERE 文本与列表**同源单源**（筛选谓词 + 数据范围谓词）⇒ 对受限账号也是真值
   *   （dev 2026-11-14 实测：pmc 计 17,181 ＝ 其列表真实行数；admin 73,375 ＝ summary）。
   * - 不带 skip/take（计数端点不翻页）。
   */
  async count(query: PeggingTraceQuery): Promise<number> {
    const filters: PeggingTraceQuery = { ...query }
    delete filters.skip
    delete filters.take
    const r = await apsHttp.get<PeggingTraceCountDto>({
      url: '/api/pegging-trace/count',
      params: { ...filters },
      silent: true
    })
    return r?.total ?? 0
  },

  /** 该版本的分配汇总（**本账号可见**口径，不随列表筛选变化） */
  async summary(planVersionId: number): Promise<PeggingTraceSummaryDto> {
    return apsHttp.get<PeggingTraceSummaryDto>({
      url: '/api/pegging-trace/summary',
      params: { planVersionId },
      silent: true
    })
  }
}
