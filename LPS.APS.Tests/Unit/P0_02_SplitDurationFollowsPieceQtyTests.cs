using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-02 拆分工时按各份自己的量算**（0号位 v1.1 §四 P0-02，2026-11-21）：
/// 「`Phase4 TrySplitOperation` 仍按 `PlannedProcessQty/splitCount` 算**均长窗口**，却产**不等整数批 2+3**」
/// ⇒ 要求「先算逐片执行净量与投入/工时，再选资源槽；**5→2+3 反证**」。
///
/// <para>本文件钉住：5 拆 2 份 ⇒ 净量 3 + 2，投入量 6 / 4（10×3/5、10×2/5），
/// **两片工时按各自投入量成 3:2，不再等长**。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class P0_02_SplitDurationFollowsPieceQtyTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0);

    [Fact(DisplayName = "P0-02正例：5 拆 2 份 ⇒ 净量 3+2、投入 6/4、工时按各自量成 3:2")]
    public void SplitIntoTwo_UnequalPieces_DurationsFollowPieceQty()
    {
        var tasks = Split(net: 5m, proc: 10m, standardDuration: 60m);

        Assert.Equal(2, tasks.Count);
        Assert.Equal(new[] { 3m, 2m }, tasks.Select(t => t.Quantity).ToArray());          // 余数给前份

        var proc = tasks.Select(t => t.PlannedProcessQty).ToArray();
        Assert.Equal(6m, proc[0], 3);
        Assert.Equal(4m, proc[1], 3);

        // 工时长 = StandardDuration(60) × 本份投入 ÷ 容量系数(1) ⇒ 360 / 240 分钟
        var span = tasks.Select(t => (decimal)(t.PlannedEndTime - t.PlannedStartTime).TotalMinutes).ToArray();
        Assert.True(span[0] > span[1],
            $"3 件片必须比 2 件片占更长窗口：实际 [{span[0]}, {span[1]}]（均分时两者相等 = P0-02 缺陷形态）");
        Assert.Equal(360m, span[0], 1);
        Assert.Equal(240m, span[1], 1);
    }

    [Fact(DisplayName = "P0-02反证：5 拆 2 份时两片工时长不得相等（不得用均分 PlannedProcessQty/splitCount）")]
    public void SplitIntoTwo_DurationsMustNotBeFlat()
    {
        var tasks = Split(net: 5m, proc: 10m, standardDuration: 60m);

        var span = tasks.Select(t => (decimal)(t.PlannedEndTime - t.PlannedStartTime).TotalMinutes).ToArray();
        Assert.False(Math.Abs(span[0] - span[1]) < 0.5m,
            $"两片窗口一样长 [{span[0]}, {span[1]}] —— 这正是 P0-02 点名的均分缺陷");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static List<FinalTaskDraft> Split(decimal net, decimal proc, decimal standardDuration)
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "P002_1",
            PlanVersionId = 1,
            DomainKey = "FAMILY_TEST",
            AllocationSequence = 1,
            DemandKey = "D2",
            MaterialId = 1,
            FactoryId = 1,
            StartStageCode = "S1",
            NetOutputQty = net,
            PlannedProcessQty = proc,
            RequiredAvailableTime = T0.AddDays(30),
            DemandSequence = 1,
            IsContinuation = false,
            QuantityAuthority = QuantityAuthorityValues.None,
        };

        var op = new OperationNode
        {
            StageCode = "S1",
            OperationCode = "OP10",
            StandardDuration = standardDuration,
            ProductionDepartmentId = 1,
            RouteCode = "R",
            PathId = 1,
        };

        var constraints = new ConstraintContext();
        constraints.ResourceCapacityFactors[
            new EligibilityLookupKey(1, 1, "R", 1, "OP10")] = new Dictionary<int, decimal> { [1] = 1m };
        constraints.ResourceCalendars[1] = new List<TimeWindow> { new(T0, T0.AddDays(30)) };

        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var repair = new PhaseFourLocalRepair();
        return repair.TrySplitOperationForTest(
            demand, op, "EB|P002_1|001", new List<int> { 1 }, T0,
            constraints, occupancy, T0.AddDays(30), splitAlternatives: 2, minBatchQty: 0.1m);
    }
}
