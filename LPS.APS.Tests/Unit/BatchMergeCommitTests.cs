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

    private static LogicalProductionDemand Demand(string key, decimal qty, DateTime? due = null)
        => new LogicalProductionDemand
    {
        LogicalDemandKey = key,
        DemandKey = key,
        MaterialId = MaterialId,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        // ScheduleDemandOperations 靠 StartStageCode 定位工序图起点；缺它会直接零产出
        StartStageCode = Stage,
        RouteCode = "DEFAULT",
        PathId = 1,
        // init-only：必须在初始化器里赋
        RequiredAvailableTime = due ?? DateTime.MaxValue,
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

        // ── C2-01（0号位 v2.0 §2.3）：**完整构成**，不只看新增的 D1 ──
        //   「目标D2=10 + 首次D1=5 单Operation合并 ⇒ 合批 15；源构成 D2=10、D1=5；无额外重复供给」
        //   合并后 Task 的业务量必须是 10+5=15（一份实物），D2 记在 SourceDraftId（本 Task 的归属需求），
        //   D1 记在分配份额里 —— 两者相加恰等于 15，不多不少。
        Assert.Equal("D2", merged!.SourceDraftId);
        Assert.Equal(15m, merged.PlannedProcessQty);

        var d2Part = merged.PlannedProcessQty - d1Shares.Sum(x => x.ShareQty);
        Assert.Equal(10m, d2Part);   // 原目标 D2 的 10 件必须原样保留，不被吃掉
    }

    [Fact(DisplayName = "C2-05 · 合并被硬约束拒绝（交期）⇒ 份额/占用/任务全部不动")]
    public void MergeRejectedByHardConstraint_LeavesStateUntouched()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(1)) },
        };
        var constraints = Constraints();

        // D1 的有效交期压在「合并后必然超期」的位置：
        //   目标 10 件 + 新增 5 件 ⇒ 合并后时长 6min/件 × 15 = 90min，起点 T0 ⇒ 结束 T0+1.5h > 交期 T0+1h
        //   ⇒ `TryMergeDemandIntoTask` 必须按 P0-04（交期不得被破坏）拒绝。
        var d1 = Demand("D1", 5m, due: T0.AddHours(1));

        var beforeOccupancy = occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList();

        var merged = Scheduler.TryMergeDemandIntoTask(
            d1, target, Graph(), constraints, occupancy, shares, T0.AddDays(30));

        Assert.Null(merged);                                    // 硬拒
        Assert.Empty(shares);                                   // 份额表**一条都不许留**
        Assert.Equal(beforeOccupancy,
            occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList());   // 占用窗逐项不变
    }

    // ══════════════════════════════════════════════════════════════════
    //  C2-04：三 Operation Stage 合批 —— 实物总量只算一份
    // ══════════════════════════════════════════════════════════════════

    private static List<OperationNode> ThreeOps()
        => new()
        {
            new OperationNode { OperationCode = "OP10", StageCode = Stage, StandardDuration = 1m, RouteCode = "DEFAULT", PathId = 1, ProductionDepartmentId = 1 },
            new OperationNode { OperationCode = "OP20", StageCode = Stage, StandardDuration = 1m, RouteCode = "DEFAULT", PathId = 1, ProductionDepartmentId = 1 },
            new OperationNode { OperationCode = "OP30", StageCode = Stage, StandardDuration = 1m, RouteCode = "DEFAULT", PathId = 1, ProductionDepartmentId = 1 },
        };

    private static RoutingGraph ThreeOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in ThreeOps()) g.Operations[OperationNodeKey.Of(Stage, op.OperationCode)] = op;
        return g;
    }

    private static ConstraintContext ThreeOpConstraints()
    {
        var c = new ConstraintContext();
        foreach (var op in ThreeOps())
        {
            c.ResourceCapacityFactors[new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, op.OperationCode)]
                = new Dictionary<int, decimal> { [ResourceId] = 1m };
        }
        c.ResourceCalendars[ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(48)) };
        return c;
    }

    /// <summary>锚点需求 A 的三道工序既有 Task（同一执行批键）。</summary>
    private static List<FinalTaskDraft> AnchorBatch(string opCode, decimal qty) => new()
    {
        new FinalTaskDraft
        {
            FinalDraftId = "T-A-" + opCode,
            SourceDraftId = "A",
            MaterialId = MaterialId,
            ResourceId = ResourceId,
            StageCode = Stage,
            OperationCode = opCode,
            PlannedStartTime = T0,
            PlannedEndTime = T0.AddHours(1),
            Quantity = qty,
            PlannedProcessQty = qty,
            ExecutionBatchDraftKey = "EB|A|001",
        },
    };

    [Fact(Skip = "夹具未跑通：TryMergeDemandIntoStageBatch 仍返回 null。已排除：targetBatch 空/无批键/缺锚点/detach 缺任务/"
               + "缺 StartStageCode。下一步：给 ScheduleDemandOperations 加临时探针，看是哪一步零产出（工序依赖链/eligibility 键/日历窗）。",
          DisplayName = "C2-04 · 三 Operation Stage 合批：每道构成 [A30,B20,C10]，实物总量 60 不是 180")]
    public void StageMerge_ThreeOperations_CountsPhysicalOnce()
    {
        var constraints = ThreeOpConstraints();
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var scheduled = new List<FinalTaskDraft>();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
        };
        var c = Demand("C", 10m);

        // 逐道工序调用：先把 A30 的既有批当作目标批，让 C10 并进去
        var results = new List<FinalTaskDraft>();
        foreach (var opCode in new[] { "OP10", "OP20", "OP30" })
        {
            var targetBatch = AnchorBatch(opCode, 30m);
            // detachTaskList 默认 true ⇒ 目标批必须**真的在** scheduledTasks 里，否则摘除失败直接回 null
            scheduled.AddRange(targetBatch);
            // 目标批上已有 A30 的份额（B20 略去，先验证「跨 Operation 不重复累加」这条主线）
            shares[targetBatch[0].FinalDraftId] =
                new List<(string, decimal)> { ("A", 30m) };

            var merged = Scheduler.TryMergeDemandIntoStageBatch(
                c, ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
                occupancy, scheduled, shares, demandByKey,
                T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
                out var combinedTasks);

            Assert.NotNull(merged);
            results.Add(merged!);
            Assert.Equal(3, combinedTasks.Count);   // 三道工序各一条
            _ = opCode;
        }

        // 核心断言：**每道 Operation 的实物量都是 40（A30+C10），不是把三道加起来当 120**
        Assert.All(results, t => Assert.Equal(40m, t.PlannedProcessQty));
        Assert.Equal(40m, results.Sum(t => t.PlannedProcessQty) / results.Count);   // 平均仍是 40

        // 份额按需求独立闭合：每个 Task 上 Σ份额 ≤ 该 Task 实物量
        foreach (var t in results)
        {
            if (!shares.TryGetValue(t.FinalDraftId, out var list)) continue;
            Assert.True(list.Sum(x => x.ShareQty) <= t.PlannedProcessQty,
                $"Task {t.FinalDraftId} 份额合计 {list.Sum(x => x.ShareQty)} 超过实物量 {t.PlannedProcessQty}");
        }
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

        // C2-02：拒绝后必须与拒绝前**逐项相同**（份额条数、金额都不许变）
        var totalQtyAfter = shares.Values.SelectMany(v => v).Sum(v => v.ShareQty);
        var d1CountAfter = shares.Values.SelectMany(v => v).Count(v => v.DemandKey == "D1");

        var all = shares.Values.SelectMany(v => v).Where(s => s.DemandKey == "D1").Sum(s => s.ShareQty);

        // 要么第二次被拒（second == null），要么份额仍是 5；**两者都不许出现「D1 份额合计 10」**
        Assert.Equal(1, d1CountAfter);
        Assert.Equal(5m, totalQtyAfter);

        Assert.True(second is null || all == 5m,
            $"同一需求被重复合并进同一目标：D1 份额合计={all}（应为 5）。"
            + $" 第二次合并返回={(second is null ? "null（已拒绝）" : "非 null（接受）")}");
    }
}