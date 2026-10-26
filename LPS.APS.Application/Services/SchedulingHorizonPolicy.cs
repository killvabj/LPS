namespace LPS.APS.Application.Services;

/// <summary>
/// 计划期 / 日历上界的**公共常量**（正排软边界口径）。
///
/// 【为什么必须只有一处】1号位 2026-10-25 G-2 回执 §四：资源日历与 G-2（无 Routing 的 Stage）两条路的
///   **上界必须同值**。此前资源日历用 `PlanHorizonEnd + 180`、G-2 用裸 `PlanHorizonEnd` ⇒ 同一条需求里，
///   前段有 Routing 能排到 +180 天、后段无 Routing 到 +90 天就「无工时」；出口会在第 90 天附近集中出一批
///   「排不下」，而根因不是产能、不是算法，是**投影截断**。两处各写一个数就会再次漂移，故只留这一个定义。
///
/// 【口径】S4 取消 730 天合成后，真实日历覆盖 = **正排物理上限**；正排不受 <c>PlanningEnd</c>（90 天有限产能
///   求解窗口）约束，日历须铺到「兜底日期」= <c>PlanHorizonEnd + 本值</c>。
/// </summary>
public static class SchedulingHorizonPolicy
{
    /// <summary>
    /// 正排软边界缓冲天数（= 180）。用途：把日历类投影（资源三级日历 `CalendarSlots`、
    /// 工厂/部门 WORK 窗口 `DepartmentWorkWindows`）的上界统一抬到 <c>PlanHorizonEnd + 180 天</c>。
    /// ⚠ 该值**只影响日历/工时投影的上界**，不改变 `DomainSolveRequest.PlanningStart/PlanningEnd` 的求解窗口语义。
    /// </summary>
    public const int SoftBoundaryCalendarBufferDays = 180;
}
