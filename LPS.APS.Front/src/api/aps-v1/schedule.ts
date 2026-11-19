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
  PlanVersionSummaryDto,
  ResourceLoadResultDto,
  TaskDetailResultDto,
  TaskListResultDto
} from './types'

/** 任务清单排序键白名单（后端非法值 ⇒ 真 400，禁静默回落） */
const TASK_SORT_KEYS: readonly string[] = [
  'plannedStart',
  'quantity',
  'materialCode',
  'taskNo',
  'id'
]

/** 任务清单页长上限（后端钳 200，前端同钳） */
const TASK_PAGE_SIZE_CAP = 200

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
  },

  /**
   * P12 资源负荷三率（5号位 2026-11-17 r14239；矩阵 A6/A7）
   *
   * `GET /api/schedule/resource-load/{planVersionId}?from=&to=[&resourceIds=…]`
   * 一行 = **设备 × 业务日**；①②两率 + ③Setup（③现恒 null）。
   *
   * ⚠️ `from` / `to` **必填**（缺任一 ⇒ 真 HTTP 400）⇒ 类型上就是必填。
   * ⚠️ **窗口上限 31 天**（后端 `spanDays > 30` ⇒ 抛错 ⇒ 真 400）⇒ 调用方自己夹。
   * ⚠️ **任务过滤是闭区间重叠**（`PlannedEndTime >= from AND PlannedStartTime <= to`）
   *   ⇒ 想覆盖某一天的任务，要传 `D ~ D+1`（只传 `D ~ D` ⇒ 命中不到当天白班的活）。
   *   而**返回的日期网格是 `from..to` 两端都含**（后端 `for (off = 0; off <= spanDays; off++)`）
   *   ⇒ 传 `D ~ D+1` 会多出 `D+1` 那一行（该行通常 0 工时），前端按自己要的末日裁掉。
   * ⚠️ `resourceIds` 只认**重复键**（`resourceIds=1&resourceIds=2`）；`resourceIds[]` 后端真 400。
   *   不传 = 窗内排产设备 TOP200（`resourcesTruncated=true` ⇒ 页面须提示收窄）。
   */
  async getResourceLoad(
    planVersionId: number,
    opts: {
      /** 必填 */
      from: string
      /** 必填 */
      to: string
      /** 只用重复键下发；≤200 个 */
      resourceIds?: number[]
      /** 取消信号：切版本 / 切时间窗 / 离开页面时中止上一次请求 */
      signal?: AbortSignal
    }
  ): Promise<ResourceLoadResultDto> {
    const params = new URLSearchParams()
    params.append('from', opts.from)
    params.append('to', opts.to)
    for (const id of opts.resourceIds ?? []) params.append('resourceIds', String(id))
    return apsHttp.get<ResourceLoadResultDto>({
      url: `/api/schedule/resource-load/${planVersionId}`,
      params,
      timeout: 30_000,
      signal: opts.signal
    })
  },

  /**
   * P05 任务清单（5号位 2026-11-16 矩阵 A4 / r14237；dev 11-17 已部署）
   *
   * `GET /api/schedule/tasks/{planVersionId}`
   *
   * ⚠️ **窗口语义 = 甘特同一件**（`GanttWindowPredicate`）：
   *   `PlannedEndTime >= from` 且 `PlannedStartTime <= to`，
   *   **计划时刻为 NULL 的任务恒保留**（不被静默丢弃）。
   *   ⇒ 这里的 from/to 必须传**与甘特体完全相同**的那一对，否则两个页面的行数会打架。
   * ⚠️ `resourceIds` 只认**重复键**（`resourceIds=1&resourceIds=2`）；
   *   `resourceIds[]` 后端真 400、逗号写法 422 ⇒ 前端只发重复键。
   * ⚠️ `stageCodes` / `departmentIds` 是**逗号串**（后端自己 split），不是重复键。
   * ⚠️ `hasWorkOrder` 是**工单号列过滤**三态（缺省=不过滤 / true=有工单号 / false=无工单号），
   *   **不是「无需 MES」分类**（权威分类标志位未建立）⇒ 调用方文案禁等同。
   * ⚠️ `sortBy` / `sortDir` 非法 ⇒ 后端真 400（参数即纪律）⇒ 这里先按白名单过滤再发。
   * ⚠️ `pageSize` 缺省 50、钳 ≤200；`total` = 服务端全量命中，`items` = 本页已加载。
   */
  async getTasks(
    planVersionId: number,
    opts: {
      /** 时间窗起（naive）；不传 = 不限 */
      from?: string
      /** 时间窗止（naive）；不传 = 不限 */
      to?: string
      /** 只用重复键下发 */
      resourceIds?: number[]
      /** 逗号分隔的阶段码 */
      stageCodes?: string[]
      /** 逗号分隔的部门 Id */
      departmentIds?: number[]
      /** 物料编码 / 物料名称 / 任务号 三路模糊 */
      keyword?: string
      /** APS 计划状态（原值等值） */
      status?: string
      /** 三态：undefined=不过滤 / true=有 MES 工单号 / false=无 MES 工单号 */
      hasWorkOrder?: boolean
      /** 白名单：plannedStart / quantity / materialCode / taskNo / id */
      sortBy?: string
      /** asc / desc */
      sortDir?: string
      pageIndex?: number
      pageSize?: number
      /** 取消信号：切版本 / 切时间窗 / 离开页面时中止上一次请求 */
      signal?: AbortSignal
    }
  ): Promise<TaskListResultDto> {
    const params = new URLSearchParams()
    if (opts.from) params.append('from', opts.from)
    if (opts.to) params.append('to', opts.to)
    for (const id of opts.resourceIds ?? []) params.append('resourceIds', String(id))
    if (opts.stageCodes?.length) params.append('stageCodes', opts.stageCodes.join(','))
    if (opts.departmentIds?.length) params.append('departmentIds', opts.departmentIds.join(','))
    if (opts.keyword) params.append('keyword', opts.keyword)
    if (opts.status) params.append('status', opts.status)
    if (opts.hasWorkOrder !== undefined) {
      params.append('hasWorkOrder', opts.hasWorkOrder ? 'true' : 'false')
    }
    if (opts.sortBy && TASK_SORT_KEYS.includes(opts.sortBy)) {
      params.append('sortBy', opts.sortBy)
    }
    if (opts.sortDir && (opts.sortDir === 'asc' || opts.sortDir === 'desc')) {
      params.append('sortDir', opts.sortDir)
    }
    if (opts.pageIndex && opts.pageIndex > 0) params.append('pageIndex', String(opts.pageIndex))
    if (opts.pageSize && opts.pageSize > 0) {
      params.append('pageSize', String(Math.min(opts.pageSize, TASK_PAGE_SIZE_CAP)))
    }
    return apsHttp.get<TaskListResultDto>({
      url: `/api/schedule/tasks/${planVersionId}`,
      params,
      timeout: 30_000,
      signal: opts.signal
    })
  },

  /**
   * P06 单任务详情（5号位 2026-11-16 矩阵 A5 / r14237）
   *
   * `GET /api/schedule/task/{taskId}`
   *
   * ⚠️ **三态分报**（禁把「不存在」说成「无权限」，反之亦然）：
   *  - `found=false` ⇒ 该 taskId **不存在**
   *  - `found=true && scopeDenied=true` ⇒ 任务**存在但越权**（域/部门范围不足）
   *  - `found=true && scopeDenied=false` ⇒ 可读
   * ⚠️ 响应里的 `shares[]` 是**逐行全量**（共享 Task 多根订单逐行给）；
   *   `sharesProvided=false` ⇒ 后端未下发份额；`sharesProvided=true` 且空数组 ⇒ **无份额记录**（≠ 0 需求）。
   * ⚠️ **前后依赖边与 MES 执行事实未提供**（后端不投影）⇒ 页面显「未提供」，不造。
   */
  async getTaskDetail(taskId: number, signal?: AbortSignal): Promise<TaskDetailResultDto> {
    return apsHttp.get<TaskDetailResultDto>({
      url: `/api/schedule/task/${taskId}`,
      timeout: 30_000,
      signal
    })
  }
}
