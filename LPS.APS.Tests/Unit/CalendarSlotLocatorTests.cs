using FluentAssertions;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-10-26 第十三刀（PV190 找槽次数）的**鉴别性**反证。
///
/// <para><b>要证两件事，缺一不可</b>：
/// ① <b>结果逐值相同</b> —— 新写法返回的槽与旧写法（逐窗试）完全一致；
/// ② <b>工作量真降了</b> —— 排满这种「注定失败」形状下，找缝调用次数从 W 掉到 1。
/// 只证①是护栏（改坏了会红），证不了这一刀有没有用；只证②可能是个**算得更快的错答案**。</para>
///
/// <para><b>暴力参考实现的来源</b>：逐行照 2026-10-26 之前的
/// <c>PhaseTwoInitialScheduler.FindForwardSlot</c> 写 —— 按 <c>Start</c> 升序遍历日历窗，
/// 跳过 <c>End &lt;= earliestStart</c> 与「窗长不够 duration」的，每个窗调一次找缝，
/// 结果 <c>End &lt;= 窗 End</c> 即返回。</para>
/// </summary>
public class CalendarSlotLocatorTests
{
    private static readonly DateTime Origin = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Unspecified);

    /// <summary>暴力找缝：与 `FindFirstAvailableSlot`（占用窗按 Start 升序、互不重叠）逐行对应。</summary>
    private static TimeWindow Gap(DateTime from, TimeSpan duration, List<TimeWindow> occupied)
    {
        var cursor = from;
        foreach (var occ in occupied)
        {
            if (occ.End <= cursor) continue;
            if (occ.Start >= cursor + duration) break;
            cursor = occ.End > cursor ? occ.End : cursor;
        }
        return new TimeWindow(cursor, cursor + duration);
    }

    /// <summary>旧写法原样复刻：逐窗试，每个窗一次找缝。</summary>
    private static TimeWindow? Legacy(
        List<TimeWindow> calendar, DateTime earliestStart, TimeSpan duration, List<TimeWindow> occupied,
        out int gapCalls)
    {
        gapCalls = 0;
        foreach (var w in calendar)
        {
            if (w.End <= earliestStart) continue;

            var windowStart = w.Start > earliestStart ? w.Start : earliestStart;
            if (w.End - windowStart < duration) continue;

            gapCalls++;
            var slot = Gap(windowStart, duration, occupied);
            if (slot.End <= w.End) return slot;
        }
        return null;
    }

    /// <summary>新写法 + 计数包装（用来断言「找缝调用次数」这个工作量指标本身）。</summary>
    private static TimeWindow? Fast(
        List<TimeWindow> calendar, DateTime earliestStart, TimeSpan duration, List<TimeWindow> occupied,
        out int gapCalls)
    {
        var calls = 0;
        var result = CalendarSlotLocator.Locate(calendar, earliestStart, start =>
        {
            calls++;
            return Gap(start, duration, occupied);
        });
        gapCalls = calls;
        return result;
    }

    /// <summary>
    /// 造**按 Start 严格升序**的日历窗。`allowOverlap` 时用超长窗自然重叠 ——
    /// ⚠ 不允许「起点回退」：`Locate` 的契约（与真实调用点 `GetAscendingCalendar` 一致）是升序表。
    /// </summary>
    private static List<TimeWindow> Calendar(DateTime origin, Random rng, int count, bool allowOverlap)
    {
        var list = new List<TimeWindow>(count);
        var t = origin.AddMinutes(rng.Next(0, 120));
        for (var i = 0; i < count; i++)
        {
            var start = t.AddMinutes(rng.Next(0, 60));
            var maxLen = allowOverlap ? 900 : 300;
            list.Add(new TimeWindow(start, start.AddMinutes(rng.Next(5, maxLen))));
            t = start.AddMinutes(rng.Next(0, 240));
        }
        return list;
    }

    /// <summary>造按 Start 升序、互不重叠的占用窗（`AddOccupancyWindow` 维护的不变式）。</summary>
    private static List<TimeWindow> Occupancy(DateTime origin, Random rng, int count)
    {
        var list = new List<TimeWindow>(count);
        var t = origin.AddMinutes(rng.Next(0, 240));
        for (var i = 0; i < count; i++)
        {
            var len = rng.Next(1, 300);
            list.Add(new TimeWindow(t, t.AddMinutes(len)));
            t = t.AddMinutes(len + rng.Next(0, 180));     // 0 间隙 ⇒ 首尾相接
        }
        return list;
    }

    [Fact(DisplayName = "CAL-01 空日历 ⇒ null，且一次找缝都不调")]
    public void CAL01_EmptyCalendar_ReturnsNullWithoutSearch()
    {
        var result = CalendarSlotLocator.Locate(
            new List<TimeWindow>(), Origin, _ => throw new InvalidOperationException("空日历不该调找缝"));

        result.Should().BeNull();
    }

    [Fact(DisplayName = "CAL-02 单窗 + 无占用 ⇒ 取 earliestStart，与旧写法同值")]
    public void CAL02_SingleWindow_NoOccupancy()
    {
        var cal = new List<TimeWindow> { new(Origin, Origin.AddHours(8)) };
        var occ = new List<TimeWindow>();
        var duration = TimeSpan.FromMinutes(60);

        var result = Fast(cal, Origin.AddHours(1), duration, occ, out var fastCalls);
        var expected = Legacy(cal, Origin.AddHours(1), duration, occ, out var legacyCalls);

        result.Should().Be(expected);
        result!.Value.Start.Should().Be(Origin.AddHours(1), "earliestStart 落在窗内且无占用 ⇒ 就地");
        fastCalls.Should().BeLessThanOrEqualTo(legacyCalls);
    }

    [Fact(DisplayName = "CAL-03 资源排满 ⇒ 只调 1 次找缝（旧写法要调 W 次），结果同为 null")]
    public void CAL03_FullyBooked_OneSearchInsteadOfMany()
    {
        // 670 个日历窗（PV190 实测量级），占用把整段日历全部压满 ⇒ 旧写法每个窗都白跑一次。
        const int windowCount = 670;
        var cal = new List<TimeWindow>(windowCount);
        for (var i = 0; i < windowCount; i++)
        {
            var s = Origin.AddDays(i);
            cal.Add(new TimeWindow(s, s.AddHours(20)));
        }

        var occ = new List<TimeWindow>
        {
            new(Origin.AddDays(-1), Origin.AddDays(windowCount + 5)),   // 一整段压死，日历末端都装不下
        };
        var duration = TimeSpan.FromHours(4);

        var result = Fast(cal, Origin, duration, occ, out var fastCalls);
        var expected = Legacy(cal, Origin, duration, occ, out var legacyCalls);

        result.Should().Be(expected);
        result.Should().BeNull();

        legacyCalls.Should().Be(windowCount, "旧写法逐窗失败，每个窗一次");
        fastCalls.Should().Be(1, "新写法一跳到底：最早可行起点已越过日历末端 ⇒ 一次即知放不下");
    }

    [Fact(DisplayName = "CAL-04 前段排满、后段有空 ⇒ 跳到对的窗，次数远少于窗数")]
    public void CAL04_BookedPrefix_RealGapLater()
    {
        const int windowCount = 200;
        var cal = new List<TimeWindow>(windowCount);
        for (var i = 0; i < windowCount; i++)
        {
            var s = Origin.AddDays(i);
            cal.Add(new TimeWindow(s, s.AddDays(5)));                  // 每窗 5 天、逐日排布（互相重叠，且都比 duration 长）
        }

        // 占用压满前 150 天，第 150 天之后空；duration 2 天 ⇒ 最早可行起点落在第 150 天
        var occ = new List<TimeWindow> { new(Origin, Origin.AddDays(150)) };
        var duration = TimeSpan.FromDays(2);

        var result = Fast(cal, Origin, duration, occ, out var fastCalls);
        var expected = Legacy(cal, Origin, duration, occ, out var legacyCalls);

        result.Should().Be(expected);
        result!.Value.Start.Should().Be(Origin.AddDays(150));
        fastCalls.Should().BeLessThan(legacyCalls, "跳越必须真的少调用");
    }

    [Fact(DisplayName = "CAL-05 随机 2,000 组（含重叠日历 / 首尾相接占用）对拍 ⇒ 逐值相同")]
    public void CAL05_Randomized_MatchesLegacy()
    {
        var rng = new Random(20261026);

        for (var iter = 0; iter < 2000; iter++)
        {
            var calCount = rng.Next(0, 30);
            var cal = Calendar(Origin, rng, calCount, allowOverlap: rng.Next(0, 2) == 0);
            var occ = Occupancy(Origin.AddDays(-2), rng, rng.Next(0, 40));

            var earliestStart = Origin.AddMinutes(rng.Next(-600, 5000));
            var duration = TimeSpan.FromMinutes(rng.Next(1, 600));

            var fast = Fast(cal, earliestStart, duration, occ, out _);
            var legacy = Legacy(cal, earliestStart, duration, occ, out _);

            fast.Should().Be(legacy,
                $"iter={iter} start={earliestStart:O} duration={duration}\n"
                + $"cal=[{string.Join(" | ", cal.Select(w => $"{w.Start:MM-dd HH:mm}~{w.End:MM-dd HH:mm}"))}]\n"
                + $"occ=[{string.Join(" | ", occ.Select(w => $"{w.Start:MM-dd HH:mm}~{w.End:MM-dd HH:mm}"))}]");
        }
    }

    [Fact(DisplayName = "CAL-06 随机 500 组「窗数 ≥ 20」大规模对拍 ⇒ 逐值相同（覆盖跳越路径）")]
    public void CAL06_RandomizedLarge_MatchesLegacy()
    {
        var rng = new Random(1314);

        for (var iter = 0; iter < 500; iter++)
        {
            var cal = Calendar(Origin, rng, rng.Next(20, 200), allowOverlap: rng.Next(0, 3) == 0);
            var occ = Occupancy(Origin.AddDays(-2), rng, rng.Next(0, 120));

            var earliestStart = Origin.AddMinutes(rng.Next(-600, 20000));
            var duration = TimeSpan.FromMinutes(rng.Next(1, 2000));

            var fast = Fast(cal, earliestStart, duration, occ, out _);
            var legacy = Legacy(cal, earliestStart, duration, occ, out _);

            fast.Should().Be(legacy, $"iter={iter} cal={cal.Count} occ={occ.Count}");
        }
    }
}
