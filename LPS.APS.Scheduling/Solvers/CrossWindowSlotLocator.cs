using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **跨日历窗找槽**（PV195 跨窗裁决落地，2026-10-29）。
///
/// <para><b>为什么要有它</b>：单窗找槽要求「一个 Task 必须整体落在某一个日历窗内」
/// （`PhaseTwoInitialScheduler.FindForwardSlot` 的既有语义）。当单个 Task 的加工时长
/// **长于该资源最长的日历窗**时，这条路恒为 null ⇒ 需求永远排不下。PV195 正是这形态：
/// 2,898.56 分钟 vs 16 个合格资源最长窗 540 分钟，差 5.4 倍。</para>
///
/// <para><b>裁决口径</b>（1号位 2026-10-25 技术择定，抄送 0号位 备案；取「乙 跨窗」不取「甲 拆批」）：</para>
/// <list type="bullet">
///   <item>`Start`/`End` = 真实墙钟；`Duration` = **有效加工**（≠ `End − Start`）；</item>
///   <item>占用记为**连续一段** `[Start, End]`（窗间的非工作时间也占住，别的 Task 不得插进来）；</item>
///   <item>窗间中断由**评估层** `CrossCalendarInterrupt` 扣减，**不新增任何契约字段**；</item>
///   <item>**单窗优先** ⇒ 现在排得下的结果逐位不变。</item>
///   <item>**连续段内不得夹着任何已存在的占用块**（2026-10-29 修复）：整段是真实资源预约，
///     段里套住别人的占用块就是同资源同一时刻两个 Task ⇒ 碰到占用块**重置**，从块尾重新起算；
///     宁可如实排不下，也不产出重叠排程（详见 `Locate` 内该分支的注释与实测）。</item>
/// </list>
///
/// <para><b>两层闸门（刻意收窄，避免连带改变其它场景）</b>：</para>
/// <list type="number">
///   <item><b>外层（谁允许调它）</b>：只有 `ConstraintContext.AllowCrossWindowSlot == true` 时
///     `PhaseFourLocalRepair.FindForwardSlot` 才会走到这里；该开关**只在**
///     `RepairUnscheduledDemands` 里「常规修复对本执行批一个 Task 都没排出」的那次重试中临时置位。
///     ⇒ **今天能排下的需求一个都不受影响**（14:18 实测：无条件开跨窗会让 6 个既有用例变形）。</item>
///   <item><b>内层（本类自己的闸门）</b>：只有「时长**大于该资源任何一个日历窗**」才真跨。
///     单窗只是「没抢到空档 / 被占用挤满」的情形**不会**走这里 —— 那些保持原判定（排不下就排不下）。</item>
/// </list>
///
/// <para><b>纯函数</b>：不写占用表、不读库、不建虚拟 7×24 日历（§11.2）；调用方自己把返回的窗写进占用。</para>
/// </summary>
internal static class CrossWindowSlotLocator
{
    /// <summary>
    /// 在**同一资源**的日历上，从 <paramref name="earliestStart"/> 起向后累计**可用分钟**，
    /// 累计到 <paramref name="duration"/> 为止，返回整段墙钟 `[起点, 完成点]`。
    /// </summary>
    /// <param name="ascendingCalendar">按 Start 升序的资源日历窗（调用方保证有序）。</param>
    /// <param name="earliestStart">最早可开工时刻（正排下界）。</param>
    /// <param name="duration">**有效占用**时长（含 Setup 的加工占用；窗间空档**不算**）。</param>
    /// <param name="occupied">该资源的已占用窗；乱序时会先排一次**副本**，**不改调用方列表**。</param>
    /// <returns>
    /// 跨窗可行的占用窗；null 的三种情形：①日历为空/时长非正；②时长没有超过任何一个窗（交回单窗路径）；
    /// ③日历走到头仍未累计够（如实排不下，**不编造**超日期的完成点）。
    /// </returns>
    internal static TimeWindow? Locate(
        List<TimeWindow> ascendingCalendar,
        DateTime earliestStart,
        TimeSpan duration,
        List<TimeWindow>? occupied,
        int resourceId = -1)
    {
        // 2026-11-20（本号位）：**纯观测**探针 —— 未 `BeginProbe()` 时 `_probe` 为 null，
        //   本方法行为与旧实现逐位一致（只多一次 ThreadStatic 读）。
        var probe = _probe;

        if (ascendingCalendar.Count == 0 || duration <= TimeSpan.Zero)
        {
            probe?.Record(resourceId, CrossWindowLocateOutcome.NoCalendarOrZeroDuration,
                duration.TotalMinutes, 0d, 0, 0, 0, earliestStart, null, 0d);
            return null;
        }

        // 闸门：时长必须超过**每一个**窗，否则交回单窗路径（保证既有结果逐位不变）。
        var longest = TimeSpan.Zero;
        for (int i = 0; i < ascendingCalendar.Count; i++)
        {
            var len = ascendingCalendar[i].End - ascendingCalendar[i].Start;
            if (len > longest) longest = len;
        }
        if (duration <= longest)
        {
            probe?.Record(resourceId, CrossWindowLocateOutcome.DurationNotLongerThanLongestWindow,
                duration.TotalMinutes, 0d, 0, 0, 0, earliestStart, null, longest.TotalMinutes);
            return null;
        }

        var occ = occupied;
        if (occ is not null && occ.Count > 1 && !IsAscendingByStart(occ))
        {
            // 罕见路径（只在单窗结构性装不下时进来，不是热路径）⇒ 排一次副本可接受；
            // 绝不改调用方列表（Phase5 `CompactGaps` 依赖列表实例不变）。
            occ = occ.OrderBy(w => w.Start).ToList();
        }

        DateTime? start = null;
        var accumulated = TimeSpan.Zero;
        var cursor = earliestStart;
        var oi = 0;
        // 2026-11-20 探针计数（仅在 `probe` 非 null 时被写入并最终记录；纯观测，不参与判定）。
        var resets = 0;
        var usableWindows = 0;
        var skippedWindows = 0;

        for (int wi = 0; wi < ascendingCalendar.Count; wi++)
        {
            var window = ascendingCalendar[wi];
            if (window.End <= cursor) { skippedWindows++; continue; }
            usableWindows++;

            var pos = window.Start > cursor ? window.Start : cursor;

            while (pos < window.End)
            {
                // 跳过已经结束的占用块。
                while (occ is not null && oi < occ.Count && occ[oi].End <= pos) oi++;

                DateTime segEnd;
                if (occ is not null && oi < occ.Count && occ[oi].Start < window.End)
                {
                    if (occ[oi].Start > pos)
                    {
                        segEnd = occ[oi].Start;      // 占用开始前的空档
                    }
                    else
                    {
                        // ── 2026-10-29 修复：碰到已存在的占用块 ⇒ **已攒的进度全部作废**，从块尾重新起算 ──
                        //
                        //   缺陷（PV190 全量跑批实测）：旧实现在这里只 `pos = occ[oi].End; continue;`，
                        //   `start` 与 `accumulated` **都不重置** ⇒ 返回的连续段把这个占用块**套在里面**。
                        //   离线扫 Phase5 入口快照（`APS_PHASE5_SNAPSHOT_DIR`，1,212,673 个 Task）：
                        //   **39 对同资源时间冲突，39/39 全是这个形状** —— 跨窗块套住了另一条 Task。
                        //   例：Resource 13394 跨窗块 `[2027-02-10 16:59 → 2027-03-11 11:47]`（有效加工 752 分钟）
                        //       套住 `[2027-02-11 08:00 → 08:50]`，重叠 50.1 分钟；该批最终 Phase5 硬校验
                        //       第 4 项判失败，A 路把**整份 FinalTask 清空**（不是丢一条）。
                        //
                        //   为什么必须重置（不是口径问题，是物理不可能）：
                        //   本类返回的窗按裁决是**一整段连续占用**（"窗间的非工作时间也占住，别的 Task 不得插进来"）
                        //   ⇒ 段内夹着别人的占用块，等于同一资源同一时刻被两个 Task 占用。
                        //   宁可**排不下**（如实返回 null，走 `StillUnscheduledKeys`），也不许产出重叠排程。
                        //
                        //   等价性：已经攒进 `[start, pos]` 的那几段不再可能成为答案的起点
                        //   （任何以它们起算的段都必须跨过本块），丢掉它们不损失任何可行解。
                        start = null;
                        accumulated = TimeSpan.Zero;
                        resets++;
                        pos = occ[oi].End;           // 上面已保证 End > pos ⇒ pos 严格前移，不会原地打转
                        continue;
                    }
                }
                else
                {
                    segEnd = window.End;             // 本窗剩余全空
                }

                // 理论上 `segEnd > pos` 恒成立；写成 break 而不是 `pos = segEnd; continue`，
                // 是因为后者在 `segEnd == pos` 时会原地打转（死循环）。
                if (segEnd <= pos) break;

                start ??= pos;
                var need = duration - accumulated;
                var avail = segEnd - pos;
                if (avail >= need)
                {
                    // 完成点落在本段内 ⇒ 有效占用恰好等于 duration，`End − Start` 会更大（含窗间空档）。
                    // 这不是 bug —— 口径就是「Duration = 有效加工 ≠ End − Start」，消费端已按此改造。
                    probe?.Record(resourceId, CrossWindowLocateOutcome.Found,
                        duration.TotalMinutes, accumulated.TotalMinutes, resets,
                        usableWindows, skippedWindows, earliestStart,
                        ascendingCalendar[ascendingCalendar.Count - 1].End, longest.TotalMinutes);
                    return new TimeWindow(start.Value, pos + need);
                }

                accumulated += avail;
                pos = segEnd;
            }

            cursor = window.End;
        }

        // 日历走到头还没累计够 ⇒ 如实返回 null（不许编造一个超出日历的完成点）。
        probe?.Record(resourceId, CrossWindowLocateOutcome.CalendarExhausted,
            duration.TotalMinutes, accumulated.TotalMinutes, resets,
            usableWindows, skippedWindows, earliestStart,
            ascendingCalendar[ascendingCalendar.Count - 1].End, longest.TotalMinutes);
        return null;
    }

    /// <summary>
    /// ⚠ **倒排没有跨窗**（2026-10-29 定稿）：倒排侧没有「最后兜底」的落点 ——
    /// 未排下的需求一律由 Phase4 以**正排**方式修复（`TryResourceSwitch` 只调 `FindForwardSlot`）。
    /// 若在倒排侧挂跨窗，它等不到 `ConstraintContext.AllowCrossWindowSlot` 被置位，
    /// 就是一个**永远不生效的死分支**；死分支比不做更坏（看着像支持，其实从不跑）。故只保留正排 `Locate`。
    /// </summary>

    private static bool IsAscendingByStart(List<TimeWindow> windows)
    {
        for (int i = 1; i < windows.Count; i++)
        {
            if (windows[i].Start < windows[i - 1].Start) return false;
        }
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2026-11-20（本号位）**纯观测探针**：回答「Phase4 跨窗兜底为什么没兜住」
    //   `ThreadStatic`：求解在本线程内跑，不跨线程；未 `BeginProbe()` 时 `_probe` 恒 null
    //   ⇒ `Locate` 只多一次字段读，行为与旧实现逐位一致（不参与任何业务判定）。
    // ────────────────────────────────────────────────────────────────────────
    [ThreadStatic]
    private static CrossWindowLocateProbe? _probe;

    /// <summary>
    /// 开启一次探针作用域（`using` 结束自动还原）。同线程嵌套时内层覆盖外层，外层 Dispose 后置 null。
    /// </summary>
    internal static CrossWindowLocateProbe BeginProbe()
    {
        var probe = new CrossWindowLocateProbe();
        _probe = probe;
        return probe;
    }

    /// <summary>仅当当前探针就是 <paramref name="probe"/> 时才清除（防嵌套 `using` 误清外层）。</summary>
    internal static void EndProbe(CrossWindowLocateProbe probe)
    {
        if (ReferenceEquals(_probe, probe)) _probe = null;
    }

    /// <summary>当前线程上的探针（null = 未开启，调用方据此零开销跳过）。</summary>
    internal static CrossWindowLocateProbe? ActiveProbe => _probe;
}

/// <summary>2026-11-20 跨窗定位的四种出口（纯诊断用）。</summary>
internal enum CrossWindowLocateOutcome
{
    /// <summary>日历为空 / 时长非正 ⇒ 连单窗都没法比。</summary>
    NoCalendarOrZeroDuration = 0,

    /// <summary>时长没有超过该资源**最长**窗 ⇒ 内层闸门交回单窗路径（本类不跨）。</summary>
    DurationNotLongerThanLongestWindow = 1,

    /// <summary>真跨窗且累计够了 ⇒ 返回一整段连续占用。</summary>
    Found = 2,

    /// <summary>日历走到头仍未累计够 ⇒ 如实排不下（`earliestStart` 之后没有足够可用分钟）。</summary>
    CalendarExhausted = 3
}

/// <summary>
/// 2026-11-20（本号位）跨窗定位探针：逐次记录「哪条分支退出 + 累计到多少 + 被占用块重置几次」。
/// **只读观测**，不改变 <see cref="CrossWindowSlotLocator"/> 的任何返回值。
/// </summary>
internal sealed class CrossWindowLocateProbe : IDisposable
{
    internal readonly List<CrossWindowLocateRecord> Records = new();

    /// <summary>2026-11-20：`TryResourceSwitch` 的**分阶段出口**计数（`步骤 → 次数`）。
/// 用来区分「根本没用跨窗」与「跨窗试了没成」—— 只记在探针开启期间。</summary>
    private readonly Dictionary<string, int> _steps = new(StringComparer.Ordinal);

    internal void RecordStep(string step)
    {
        _steps.TryGetValue(step, out var count);
        _steps[step] = count + 1;
    }

    internal void Record(
        int resourceId,
        CrossWindowLocateOutcome outcome,
        double needMinutes,
        double accumulatedMinutes,
        int resets,
        int usableWindows,
        int skippedWindows,
        DateTime earliestStart,
        DateTime? lastCalendarEnd,
        double longestWindowMinutes)
    {
        Records.Add(new CrossWindowLocateRecord(
            resourceId, outcome, needMinutes, accumulatedMinutes, resets,
            usableWindows, skippedWindows, earliestStart, lastCalendarEnd, longestWindowMinutes));
    }

    /// <summary>
    /// 一行（含换行的多行）汇总：按出口分桶 + 「最接近成功的那次」明细（累计最多的一次）。
    /// 供 Phase4 写进 `TraceNotes`（`PHASE4_XW_PROBE`）。
    /// </summary>
    internal string Summary()
    {
        var stepText = _steps.Count == 0
            ? "阶段=（无）"
            : "阶段=" + string.Join(", ", _steps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        if (Records.Count == 0) return $"跨窗定位 0 次（未走到 CrossWindowSlotLocator）；{stepText}";

        int noCal = 0, notLonger = 0, found = 0, exhausted = 0;
        foreach (var r in Records)
        {
            switch (r.Outcome)
            {
                case CrossWindowLocateOutcome.NoCalendarOrZeroDuration: noCal++; break;
                case CrossWindowLocateOutcome.DurationNotLongerThanLongestWindow: notLonger++; break;
                case CrossWindowLocateOutcome.Found: found++; break;
                default: exhausted++; break;
            }
        }

        // 「最接近成功」＝ 日历走到头那一档里累计最多的一次（对定位最有信息量）。
        CrossWindowLocateRecord? best = null;
        foreach (var r in Records)
        {
            if (r.Outcome != CrossWindowLocateOutcome.CalendarExhausted) continue;
            if (best is null || r.AccumulatedMinutes > best.Value.AccumulatedMinutes) best = r;
        }

        var text = $"跨窗定位 {Records.Count} 次：命中={found}；日历走到头={exhausted}；"
                 + $"时长未超最长窗={notLonger}；无日历/时长非正={noCal}；{stepText}";
        if (best is not null)
        {
            var b = best.Value;
            text += $" || 最接近成功：资源={b.ResourceId} 需 {b.NeedMinutes:0.##} 分钟 / 已累计 {b.AccumulatedMinutes:0.##} 分钟"
                  + $"（缺口 {b.NeedMinutes - b.AccumulatedMinutes:0.##}）；"
                  + $"可用窗 {b.UsableWindows} 个、起点前已过期窗 {b.SkippedWindows} 个；"
                  + $"最长窗 {b.LongestWindowMinutes:0.##} 分钟；被已有占用重置 {b.Resets} 次；"
                  + $"起点={b.EarliestStart:yyyy-MM-dd HH:mm}，日历末日={b.LastCalendarEnd:yyyy-MM-dd HH:mm}";
        }
        return text;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CrossWindowSlotLocator.EndProbe(this);
    }
}

/// <summary>2026-11-20 跨窗定位的单次记录（纯诊断）。</summary>
internal readonly record struct CrossWindowLocateRecord(
    int ResourceId,
    CrossWindowLocateOutcome Outcome,
    double NeedMinutes,
    double AccumulatedMinutes,
    int Resets,
    int UsableWindows,
    int SkippedWindows,
    DateTime EarliestStart,
    DateTime? LastCalendarEnd,
    double LongestWindowMinutes);
