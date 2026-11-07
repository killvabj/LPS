using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-07 C2 数量闭合证据（0号位《54c687e 六项答复与继续整改指令》§四 第 1 步验收项：
/// 「同需求前批合并、后批失败、**同目标重复合并**；数量闭合」）。
///
/// <para>【测什么】直接调 <c>PhaseTwoInitialScheduler.TryMergeDemandIntoTask</c>（已由 private 改 internal），
/// 观察合并落定后 <c>allocationTaskShare</c> 里该需求的份额**是否只增加一次、金额等于本批净量**。</para>
///
/// <para>【为什么单独测这条】合并分支里那一句是无条件
/// <c>mergedShareNext.Add((demand.LogicalDemandKey, demand.NetOutputQty))</c> —— 没有去重检查。
/// 若同一需求能被合并进同一个目标 Task 两次，同一 `DemandKey` 就会在该 Task 上出现两条份额
/// ⇒ **数量闭合被破坏**（∑Share 超过需求净量）。代码里另有一道「目标必须是另一条需求」+
/// 「目标已属别的执行批则拒绝」的守卫，本用例就是把那道守卫**测出来**，而不是只靠读代码断言它存在。</para>
///
/// <para>【不碰生产库】纯内存夹具，零 DB。</para>
/// </summary>
public sealed class BatchMergeCommitTests
{
    private static PhaseTwoInitialScheduler Scheduler => new PhaseTwoInitialScheduler();

    private static readonly DateTime T0 = new DateTime(2026, 11, 10, 8, 0, 0);
    private const int ResourceId = 1;
    private const int MaterialId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private static FinalTaskDraft TargetTask() => new FinalTaskDraft
    {
        FinalDraftId = "T-D2",
        SourceDraftId = "D2",
        MaterialId = MaterialId,
        ResourceId = ResourceId,
        StageCode = Stage,
        OperationCode = Op,
        PlannedStartTime = T0,
        PlannedEndTime = T0.AddHours(1),
        SetupTime = 0m,
        Quantity = 10m,
        PlannedProcessQty = 10m,
        ExecutionBatchDraftKey = "EB|D2|001",
    };

    private static LogicalProductionDemand Demand(string key, decimal qty) => new LogicalProductionDemand
    {
        LogicalDemandKey = key,
        DemandKey = key,
        MaterialId = MaterialId,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        RequiredAvailableTime = DateTime.MaxValue,
    };

    private static RoutingGraph Graph()
    {
        var g = new RoutingGraph();
        g.Operations[OperationNodeKey.Of(Stage, Op)] = new OperationNode
        {
            OperationCode = Op,
            StageCode = Stage,
            StandardDuration = 6m,          // 分钟/件
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        };
        return g;
    }

    private static ConstraintContext Constraints()
    {
        var c = new ConstraintContext();
        c.ResourceCapacityFactors[new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, Op)]
            = new Dictionary<int, decimal> { [ResourceId] = 1m };
        c.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0.AddHours(-8), T0.AddHours(20)),
        };
        return c;
    }

    [Fact(DisplayName = "C2 · 首次合并：份额只增加一条，金额＝本批净量")]
    public void FirstMerge_AddsExactlyOneShare()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var d1 = Demand("D1", 5m);

        var merged = Scheduler.TryMergeDemandIntoTask(
            d1, target, Graph(), Constraints(), occupancy, shares, T0.AddDays(30));

        Assert.NotNull(merged);
        Assert.True(shares.TryGetValue(merged!.FinalDraftId, out var list), "合并后份额表里必须有该 Task 的条目");

        var d1Shares = list!.Where(s => s.DemandKey == "D1").ToList();
        Assert.Single(d1Shares);
        Assert.Equal(5m, d1Shares[0].ShareQty);
    }

    [Fact(DisplayName = "C2 · 同目标重复合并：份额不得被追加第二条（数量闭合）")]
    public void DuplicateMergeIntoSameTarget_DoesNotDoubleCount()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var constraints = Constraints();

        // 第一次合并：D1 的 5 件进目标 Task
        var first = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), target, Graph(), constraints, occupancy, shares, T0.AddDays(30));
        Assert.NotNull(first);

        // 第二次：同一需求再合并一次（模拟「同需求的两批先后命中同一目标」）
        var second = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), first!, Graph(), constraints, occupancy, shares, T0.AddDays(30));

        var all = shares.Values.SelectMany(v => v).Where(s => s.DemandKey == "D1").Sum(s => s.ShareQty);

        // 要么第二次被拒（second == null），要么份额仍是 5；**两者都不许出现「D1 份额合计 10」**
        Assert.True(second is null || all == 5m,
            $"同一需求被重复合并进同一目标：D1 份额合计={all}（应为 5）。"
            + $" 第二次合并返回={(second is null ? "null（已拒绝）" : "非 null（接受）")}");
    }
}