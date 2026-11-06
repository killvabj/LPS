using System;
using System.Linq;
using System.Reflection;
using Xunit;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-06：钉死「热点槽号」与 <c>SolverDiagnostics.Counters.HotspotCount</c> 必须对齐。
///
/// <para>【为什么要有这条】实测踩过：`Hotspot` 里槽位已编到 76，而 `SolverDiagnostics.Counters.HotspotCount` 还停在 68。
/// `AddHotspotTicks` 的 `&lt; HotspotCount` 护栏把这些槽**静默丢弃**——不抛异常、不写日志，
/// 读数恒为 0。表现是「埋点写了、编译过了、跑完一看全是 0」，很容易被误读成「这段不耗时」。
/// 而打印侧的 `HotspotMs` 当时没有护栏，直接抛 `IndexOutOfRangeException`，
/// 连带把后面所有分项读数一起吞掉（探针外层 `catch` 兜住，只留一行「已吞」）。</para>
///
/// <para>本测试用反射枚举 `Hotspot` 的 `public const int`，要求
/// `HotspotCount == 最大槽号 + 1`。以后加槽忘了改 `HotspotCount` 会立刻红。</para>
/// </summary>
public sealed class SolverDiagnosticsSlotTests
{
    private static int[] AllSlots()
        => typeof(SolverDiagnostics.Hotspot)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue()!)
            .ToArray();

    [Fact(DisplayName = "HotspotCount 必须等于最大槽号 + 1（加槽不同步会让读数静默丢失）")]
    public void HotspotCount_CoversAllSlots()
    {
        var slots = AllSlots();
        Assert.NotEmpty(slots);

        var max = slots.Max();
        Assert.True(
            SolverDiagnostics.Counters.HotspotCount == max + 1,
            $"SolverDiagnostics.Counters.HotspotCount={SolverDiagnostics.Counters.HotspotCount}，但 Hotspot 里最大槽号={max}。"
            + $" 应为 {max + 1}。差一截会让 {max + 1 - SolverDiagnostics.Counters.HotspotCount} 个槽静默丢失读数。");
    }

    [Fact(DisplayName = "槽号从 0 连续、无重复")]
    public void Slots_AreContiguousFromZero()
    {
        var slots = AllSlots().OrderBy(x => x).ToArray();
        for (var i = 0; i < slots.Length; i++)
        {
            Assert.True(slots[i] == i,
                $"槽号不连续：第 {i} 个位置上是 {slots[i]}。重复或跳号会让两个槽共用一格计数。");
        }
    }

    [Fact(DisplayName = "越界槽号：HotspotMs / HotspotCallCount 返回 0，不抛异常")]
    public void OutOfRangeSlots_ReturnZero()
    {
        using var scope = SolverDiagnostics.BeginScope();
        var c = scope.Counters;

        Assert.Equal(0d, c.HotspotMs(SolverDiagnostics.Counters.HotspotCount));
        Assert.Equal(0d, c.HotspotMs(SolverDiagnostics.Counters.HotspotCount + 1000));
        Assert.Equal(0L, c.HotspotCallCount(SolverDiagnostics.Counters.HotspotCount));
        Assert.Equal(0L, c.HotspotCallCount(SolverDiagnostics.Counters.HotspotCount + 1000));
    }

    [Fact(DisplayName = "槽内记录后 HotspotMs / HotspotCallCount 能读回（护栏没把合法槽挡掉）")]
    public void InRangeSlots_AreRecorded()
    {
        using var scope = SolverDiagnostics.BeginScope();
        var c = scope.Counters;

        var slot = SolverDiagnostics.Hotspot.Phase3UtilizationPerDemand;
        var sw = SolverDiagnostics.HotspotStart();
        SolverDiagnostics.HotspotEnd(sw, slot);

        Assert.True(c.HotspotCallCount(slot) >= 1,
            $"槽 {slot}（Phase3UtilizationPerDemand）记不上次数 —— 检查 HotspotCount 是否又落后了。");
    }
}