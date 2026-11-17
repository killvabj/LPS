using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **2026-11-16｜占用表「按 Start 升序」不变式回归**（1号位）。
///
/// <para><b>为什么要有这条</b>：`PhaseTwoInitialScheduler.FindFirstAvailableSlot` 的**二分跳段**
/// 依赖「资源占用列表按 `Start` 升序、窗互不重叠」。Phase2 全部写入经 `AddOccupancyWindow`（有序插入），
/// 但 Phase4 此前用**裸 `.Add()`** 追加 —— 任务列表顺序不保证按开工时间 ⇒ 表乱序 ⇒ 二分跳段跳过
/// 实际冲突的窗 ⇒ 排出同资源重叠（PV 999507 固定输入实测：Phase2 输出 0 重叠、Phase4 之后 1 重叠）。</para>
///
/// <para><b>鉴别性</b>：本用例**故意把任务按开工时间倒序喂进去**。修复前列表保持倒序（断言必红），
/// 修复后必须升序（断言绿）。</para>
/// </summary>
public class PhaseFourOccupancyOrderTests
{
    private static readonly DateTime Base = new(2026, 11, 19, 8, 0, 0);

    private static FinalTaskDraft Task(string id, int resourceId, double startHours, double durationHours, decimal setup = 0m) => new()
    {
        FinalDraftId = id,
        SourceDraftId = "D1",
        MaterialId = 1,
        FactoryId = 1,
        StageCode = "S1",
        OperationCode = "OP10",
        Quantity = 1m,
        PlannedProcessQty = 1m,
        PlannedStartTime = Base.AddHours(startHours),
        PlannedEndTime = Base.AddHours(startHours + durationHours),
        SetupTime = setup,
        ResourceId = resourceId
    };

    [Fact(DisplayName = "BuildResourceOccupancy：任务倒序喂入，输出仍按 Start 升序")]
    public void BuildResourceOccupancy_SortsWindowsByStart()
    {
        // 故意倒序：10h → 5h → 1h（同一资源 777）
        var tasks = new List<FinalTaskDraft>
        {
            Task("T3", 777, 10, 1),
            Task("T2", 777, 5, 1),
            Task("T1", 777, 1, 1),
        };

        var occupancy = PhaseFourLocalRepair.BuildResourceOccupancy(tasks, new ConstraintContext());

        Assert.True(occupancy.ContainsKey(777));
        var windows = occupancy[777];
        Assert.Equal(3, windows.Count);

        for (var i = 1; i < windows.Count; i++)
        {
            Assert.True(windows[i - 1].Start <= windows[i].Start,
                $"第 {i} 个窗的 Start={windows[i].Start:O} 早于前一个 {windows[i - 1].Start:O} ⇒ 表未按升序");
        }
    }

    [Fact(DisplayName = "BuildResourceOccupancy：含 Setup 的占用起点同样参与排序")]
    public void BuildResourceOccupancy_SortsBySetupAdjustedStart()
    {
        // 加工开始 5h、Setup 2h ⇒ 占用起点 3h；另一任务加工开始 4h、无 Setup ⇒ 占用起点 4h
        var tasks = new List<FinalTaskDraft>
        {
            Task("T2", 888, 4, 1, setup: 0m),      // 占用 [4h, 5h)
            Task("T1", 888, 5, 1, setup: 120m),    // 占用 [3h, 6h)
        };

        var occupancy = PhaseFourLocalRepair.BuildResourceOccupancy(tasks, new ConstraintContext());
        var windows = occupancy[888];

        Assert.Equal(2, windows.Count);
        Assert.True(windows[0].Start <= windows[1].Start, "含 Setup 的占用起点未参与排序");
        Assert.Equal(Base.AddHours(3), windows[0].Start);
    }
}