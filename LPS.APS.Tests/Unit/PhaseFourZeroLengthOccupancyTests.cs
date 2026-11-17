using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **2026-11-16｜零时长占用窗把「真实占用窗」从槽查找里藏掉**（1号位）。
///
/// <para><b>现场证据</b>：PV 999507 固定输入重放，资源 1000025577 上 Phase2 交付的 178 个 Task 里有两条
/// 起点完全相同：<c>999507_848726</c>（45 分钟，14:51:14→15:36:14）与 <c>999507_870296</c>
/// （<b>零时长</b>，14:51:14→14:51:14）。Phase4 为另一个需求找槽时，返回了
/// <b>14:52:36→14:59:46</b> —— 正落在 45 分钟窗内部 ⇒ Phase5 硬校验判同资源冲突、整份结果清空。</para>
///
/// <para><b>为什么会被藏掉</b>：<c>FindFirstAvailableSlot</c> 用「二分定位 + 靠 <c>End</c> 单调回退」
/// 确定扫描起点，前提是「窗互不重叠 ⇒ <c>End</c> 单调」。零时长窗 <c>[X, X]</c> 坐在
/// <c>[X, X+45min]</c> 里面时，<c>End</c> 序列变成 <c>… X, X+45m, X …</c>，<b>单调被打破</b>：
/// 从 <c>X+82s</c> 回退时先撞上零时长窗（<c>End = X</c> 不大于起点）⇒ 回退就地停止
/// ⇒ 前面那个真正挡住时间的 45 分钟窗被整个跳过 ⇒ 返回 <c>[X+82s, X+82s+7.17m)</c>。</para>
///
/// <para><b>鉴别性</b>：修复前本例必红（返回的槽与 45 分钟窗相交）；修复后必须把
/// <c>7.17</c> 分钟排在 45 分钟窗<b>之后</b>。</para>
/// </summary>
public class PhaseFourZeroLengthOccupancyTests
{
    private const int ResourceId = 777;

    // 与现场一致的基准时刻（PV 999507：2026-11-19 14:51:14）
    private static readonly DateTime Base = new(2026, 11, 19, 14, 51, 14);

    private static readonly TimeSpan RealOccupy = TimeSpan.FromMinutes(45);   // 48_726 的加工时长
    private static readonly TimeSpan Wanted = TimeSpan.FromMinutes(7.17);     // 待排任务的加工时长

    private static ConstraintContext CalendarAround()
    {
        var ctx = new ConstraintContext();
        ctx.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new(Base.AddHours(-8), Base.AddHours(8)),
        };
        return ctx;
    }

    [Fact(DisplayName = "FindForwardSlot：零时长窗与真实窗同起点时，不得把槽排进真实窗内")]
    public void FindForwardSlot_ZeroLengthWindowInsideRealWindow_DoesNotPlaceInside()
    {
        var constraints = CalendarAround();
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = new List<TimeWindow>
            {
                new(Base, Base + RealOccupy),   // 真实占用：45 分钟
                new(Base, Base),                // 零时长窗：与真实窗同起点（End 单调被打破）
            },
        };

        // 现场下界 = 14:52:36（= Base + 82 秒）
        var slot = PhaseFourLocalRepair.FindForwardSlot(
            Base.AddSeconds(82), Wanted, ResourceId, constraints, occupancy, Base.AddDays(30));

        Assert.True(slot.HasValue, "45 分钟窗之后有一整天日历，必须能找到槽");
        var real = new TimeWindow(Base, Base + RealOccupy);
        Assert.False(slot!.Value.Overlaps(real),
            $"槽 [{slot.Value.Start:O} .. {slot.Value.End:O}] 与真实占用 [{real.Start:O} .. {real.End:O}] 相交");
        Assert.True(slot.Value.Start >= Base + RealOccupy, "槽必须落在 45 分钟占用窗之后");
    }

    [Fact(DisplayName = "BuildResourceOccupancy：零时长 Task 不进占用表（它不占时间）")]
    public void BuildResourceOccupancy_SkipsZeroLengthTasks()
    {
        var tasks = new List<FinalTaskDraft>
        {
            new()
            {
                FinalDraftId = "A", SourceDraftId = "D1", MaterialId = 1, FactoryId = 1,
                StageCode = "TJ_MACH", OperationCode = "J106", Quantity = 9m, PlannedProcessQty = 9m,
                PlannedStartTime = Base, PlannedEndTime = Base + RealOccupy,
                SetupTime = 0m, ResourceId = ResourceId
            },
            new()
            {
                FinalDraftId = "Z", SourceDraftId = "D2", MaterialId = 1, FactoryId = 1,
                StageCode = "TJ_MACH", OperationCode = "J106", Quantity = 3m, PlannedProcessQty = 3m,
                PlannedStartTime = Base, PlannedEndTime = Base,          // 零时长
                SetupTime = 0m, ResourceId = ResourceId
            },
        };

        var occupancy = PhaseFourLocalRepair.BuildResourceOccupancy(tasks, new ConstraintContext());

        Assert.True(occupancy.ContainsKey(ResourceId));
        Assert.Single(occupancy[ResourceId]);
        Assert.Equal(Base + RealOccupy, occupancy[ResourceId][0].End);
    }
}