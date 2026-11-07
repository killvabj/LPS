/**
 * APS V1 4号位 — 排程查询 API
 *
 * 接口路径与 LPS.APS.Web/Controllers/ScheduleController.cs 一致：
 *  - GET /api/schedule/versions?take=30
 *  - GET /api/schedule/gantt/{planVersionId}
 *  - GET /api/schedule/gantt/{planVersionId}/buckets（资源×时间桶聚合；5号位 2026-10-30 r13972）
 *  - （`GET /api/schedule/summary/{planVersionId}` 本前端**仍不调用**。截至 2026-10-19 dev 实测
 *     端点健康（HTTP 200、`versionExists`/`scopeDenied` 随 5号位 §2.3 改名一并部署到位），两条不接的理由：
 *       ① 甘特页的 KPI 从甘特体现算，不缺数据源；
 *       ② 🔴 其 `delayedTasks` 的延期口径与甘特体不一致（裸 datetime 比较）：
 *          dev v2 实测 summary 191 / 甘特体 179，差的 12 条正是「交日当天 00:00 之后完工」。
 *          接进来会与页面上显示的延期数自相矛盾。详见 store/aps/schedule.ts 的 loadCurrent 注释
 *          与 types/schedule.ts 的 `ScheduleSummaryDto.delayedTasks`）
 *
 * mock 切换：VITE_USE_MOCK=true 时返回 fixture，否则走真实后端
 */

import { apsHttp, APS_USE_MOCK } from './http'
import { mockGantt, mockVersions } from './__mocks__/fixtures'
import type {
  GanttBucketUnit,
  GanttBucketsDto,
  GanttCursor,
  GanttDataDto,
  PlanVersionSummaryDto
} from './types'

export const scheduleApi = {
  /** 计划版本列表（默认 30 条） */
  async getVersions(take = 30): Promise<PlanVersionSummaryDto[]> {
    if (APS_USE_MOCK) return mockVersions.slice(0, take)
    return apsHttp.get<PlanVersionSummaryDto[]>({
      url: '/api/schedule/versions',
      params: { take }
    })
  },

  /** 指定版本的甘特图数据（@owner 5号位 中转 2号位 ScheduleResult）
   *
   * 5号位 2026-10-17 R1 新增可选入参：
   *  - `from` / `to`：按 `PlannedEndTime ≥ from` / `PlannedStartTime ≤ to` 过滤，无计划时间的任务保留；
   *  - `resourceIds`：按资源过滤，空数组 = 不过滤。
   *
   * 5号位 2026-10-29 复函 §二 新增：
   *  - `limit`：单次任务条上限（缺省 8000、上限 20000；**不传也不再返回全量**）；
   *  - 响应新增 `totalTasks`（过滤口径总数）/ `hasMore` / `limit` / `nextAfter` 游标。
   *
   * ⚠️ 甘特页自 10-29 起**一律窗口加载**（始终带 from/to + limit），不再整版本全量。
   *
   * ⚠️ `resourceIds` 必须用**重复键** `resourceIds=1&resourceIds=2`：
   *   dev 实测逗号分隔 422、`resourceIds[]=1` 被静默忽略（反而返回全量）。
   *
   * 2026-10-29 第 1 步：`after`（上一页 `nextAfter` 原样回填）→ 发三元组
   * `afterResourceId` / `afterStartAt` / `afterTaskId`，三参成套才翻页。
   */
  async getGantt(
    planVersionId: number,
    window?: {
      from?: string
      to?: string
      resourceIds?: number[]
      limit?: number
      /** 上一页 nextAfter 原样回填；缺省 = 第一页 */
      after?: GanttCursor
      /** 取消信号：切版本 / 切时间窗 / 离开页面时中止上一次请求 */
      signal?: AbortSignal
    }
  ): Promise<GanttDataDto> {
    if (APS_USE_MOCK) return mockGantt(planVersionId)
    // 10-12 RC21：30s 超时保护，dev 后端 gantt/143 第二次卡死不再拖死前端
    // - 兜底：store handleError 接 ApiError，ElMessage 提示；不静默吞
    let params: URLSearchParams | undefined
    if (window) {
      params = new URLSearchParams()
      if (window.from) params.append('from', window.from)
      if (window.to) params.append('to', window.to)
      if (window.limit) params.append('limit', String(window.limit))
      for (const id of window.resourceIds ?? []) params.append('resourceIds', String(id))
      if (window.after) {
        params.append('afterResourceId', String(window.after.resourceId))
        params.append('afterStartAt', window.after.plannedStartTime)
        params.append('afterTaskId', String(window.after.taskId))
      }
    }
    return apsHttp.get<GanttDataDto>({
      url: `/api/schedule/gantt/${planVersionId}`,
      params,
      timeout: 30_000,
      signal: window?.signal
    })
  },

  /**
   * 甘特「资源×时间桶」聚合（负荷带图层数据源；5号位 2026-10-30 r13972，dev 已部署）
   *
   * 路径：`GET /api/schedule/gantt/{planVersionId}/buckets?from=…&to=…&bucket=day|week|month[&resourceIds=…]`
   *
   * ⚠️ `from` / `to` **必填**（后端缺任一 ⇒ 真 HTTP 400）⇒ 这里在**类型上就是必填**，
   *   调用方漏传编译期即报错，不会发出无参请求。
   * ⚠️ 比较为 **naive**（服务端不换算时区）；带不带 `Z` 结果一致（dev 实测）。
   * ⚠️ `bucket` 缺省 `day`；其它值后端返 400。
   * ⚠️ `resourceIds` 只认**重复键** `resourceIds=1&resourceIds=2`；
   *   `resourceIds[]=1` 被后端 400 拦截、逗号写法 422（模型绑定）⇒ 前端只发重复键。
   *
   * 响应关键键（详见 types/schedule.ts 的 `GanttBucketsDto`）：
   *  - `bucketWidth` = **实际生效桶宽**（降级后可能 ≠ `bucket`）——图层按它画；
   *  - `totalTasks` 在版本不存在/越权时**整键省略**、空窗时**键在值 0**（两种形态）；
   *  - `items[].bucketStart` **缺键 = 未排桶行**；**可能早于 `from`**（周桶锚=周一）。
   */
  async getGanttBuckets(
    planVersionId: number,
    opts: {
      /** 必填 */
      from: string
      /** 必填 */
      to: string
      /** 缺省 day（后端缺省亦为 day） */
      bucket?: GanttBucketUnit
      /** 只用重复键下发 */
      resourceIds?: number[]
      /** 取消信号：切版本 / 切时间窗 / 离开页面时中止上一次请求 */
      signal?: AbortSignal
    }
  ): Promise<GanttBucketsDto> {
    const unit: GanttBucketUnit = opts.bucket ?? 'day'
    // mock 模式不造第二套假数据：负荷带在 mock 下没有数据源，如实返回空集
    if (APS_USE_MOCK) {
      return {
        planVersionId,
        versionCode: mockVersions.find((v) => v.id === planVersionId)?.versionCode ?? '',
        versionExists: true,
        isEmpty: true,
        bucket: unit,
        bucketWidth: unit,
        bucketDowngraded: false,
        items: []
      }
    }
    const params = new URLSearchParams()
    params.append('from', opts.from)
    params.append('to', opts.to)
    params.append('bucket', unit)
    for (const id of opts.resourceIds ?? []) params.append('resourceIds', String(id))
    return apsHttp.get<GanttBucketsDto>({
      url: `/api/schedule/gantt/${planVersionId}/buckets`,
      params,
      timeout: 30_000,
      signal: opts.signal
    })
  }
}
