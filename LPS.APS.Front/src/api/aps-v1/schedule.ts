/**
 * APS V1 4号位 — 排程查询 API
 *
 * 接口路径与 LPS.APS.Web/Controllers/ScheduleController.cs 一致：
 *  - GET /api/schedule/versions?take=30
 *  - GET /api/schedule/gantt/{planVersionId}
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
import type { GanttDataDto, PlanVersionSummaryDto } from './types'

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
   * 5号位 2026-10-17 R1 新增可选入参（不传 = 全量，向后兼容）：
   *  - `from` / `to`：按 `PlannedEndTime ≥ from` / `PlannedStartTime ≤ to` 过滤，无计划时间的任务保留；
   *  - `resourceIds`：按资源过滤，空数组 = 不过滤。
   *
   * ⚠️ 甘特页当前仍取全量 —— 用 `planHorizonStart/End` 当窗口会**漏数据**：
   *   dev v2 实测有 3 个任务的计划完工超出 `planHorizonEnd`。等页面有真正的「加载窗口」
   *   控件（不是现在的时间轴缩放）再接。
   *
   * ⚠️ `resourceIds` 必须用**重复键** `resourceIds=1&resourceIds=2`：
   *   dev 实测逗号分隔 422、`resourceIds[]=1` 被静默忽略（反而返回全量）。
   */
  async getGantt(
    planVersionId: number,
    window?: { from?: string; to?: string; resourceIds?: number[] }
  ): Promise<GanttDataDto> {
    if (APS_USE_MOCK) return mockGantt(planVersionId)
    // 10-12 RC21：30s 超时保护，dev 后端 gantt/143 第二次卡死不再拖死前端
    // - 兜底：store handleError 接 ApiError，ElMessage 提示；不静默吞
    // - 与 loadCurrent 内 Promise.allSettled 不冲突（getGantt 自身先超时抛错）
    let params: URLSearchParams | undefined
    if (window) {
      params = new URLSearchParams()
      if (window.from) params.append('from', window.from)
      if (window.to) params.append('to', window.to)
      for (const id of window.resourceIds ?? []) params.append('resourceIds', String(id))
    }
    return apsHttp.get<GanttDataDto>({
      url: `/api/schedule/gantt/${planVersionId}`,
      params,
      timeout: 30_000
    })
  }
}
