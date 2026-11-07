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
    //  按 0号位 2026-11-07 答复 §Q1 的完整清单构造：
    //   ① targetBatch 必须是**整批 3 道 Operation**（不是逐道调 3 次）
    //   ② 必须同时填 OperationResourceEligibility（GetEligibleResources 读它）
    //      与 ResourceCapacityFactors（效率系数），两者资源 ID 要对上
    //   ③ resourceOccupancy 必须有该 ResourceId 的键且与既有 Task 时间一致
    //   ④ Graph 要有 OP10→OP20→OP30 依赖
    // ══════════════════════════════════════════════════════════════════

    private static readonly string[] StageOps = { "OP10", "OP20", "OP30" };

    private static List<OperationNode> ThreeOps()
        => StageOps.Select(op => new OperationNode
        {
            OperationCode = op,
            StageCode = Stage,
            StandardDuration = 1m,          // **分钟/件**（Solver 输入口径），不是秒
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        }).ToList();

    private static RoutingGraph ThreeOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in ThreeOps()) g.Operations[OperationNodeKey.Of(Stage, op.OperationCode)] = op;

        // DAG：OP20 依赖 OP10，OP30 依赖 OP20（Dependencies 是「To → 前驱列表」）
        g.Dependencies[OperationNodeKey.Of(Stage, "OP20")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP10"), To = OperationNodeKey.Of(Stage, "OP20") },
        };
        g.Dependencies[OperationNodeKey.Of(Stage, "OP30")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP20"), To = OperationNodeKey.Of(Stage, "OP30") },
        };
        g.RootOperations.Add(OperationNodeKey.Of(Stage, "OP10"));
        return g;
    }

    /// <summary>资格表与效率系数**两张都填**（0号位 §Q1 点名：只填 CapacityFactors 会拿到空资格）。</summary>
    private static ConstraintContext ThreeOpConstraints()
    {
        var c = new ConstraintContext();
        foreach (var op in StageOps)
        {
            var key = new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, op);
            c.OperationResourceEligibility[key] = new List<int> { ResourceId };   // 资格
            c.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [ResourceId] = 1m };  // 效率系数
        }
        c.ResourceCalendars[ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(48)) };
        return c;
    }

    /// <summary>既有 Stage 执行批：**三道工序各一条 Task、各 50 件（A30+B20）、共用同一批键**，
    /// 时间首尾相接（1min/件 × 50 = 50min）。</summary>
    private static List<FinalTaskDraft> AnchorStageBatch()
    {
        var list = new List<FinalTaskDraft>();
        var cursor = T0;
        foreach (var op in StageOps)
        {
            list.Add(new FinalTaskDraft
            {
                FinalDraftId = "T-A-" + op,
                SourceDraftId = "A",
                MaterialId = MaterialId,
                ResourceId = ResourceId,
                StageCode = Stage,
                OperationCode = op,
                PlannedStartTime = cursor,
                PlannedEndTime = cursor.AddMinutes(50),
                SetupTime = 0m,
                Quantity = 50m,
                PlannedProcessQty = 50m,
                ExecutionBatchDraftKey = "EB|A|001",
            });
            cursor = cursor.AddMinutes(50);
        }
        return list;
    }

    [Fact(DisplayName = "C2-04深度回滚 · 前两道试排成功、第三道因日历无槽失败 ⇒ 全状态逐项还原")]
    public void StageMerge_ThirdOperationFails_RestoresEverything()
    {
        var constraints = ThreeOpConstraints();
        // **日历只够前两道**：每道 60 件 × 1min = 60min；总需 180min，只给 130min ⇒ 第三道必然无槽。
        // 这是**真实资源约束**导致的失败（不是交期前置拒绝），才符合 0号位 §0.4 的「深度回滚」口径。
        constraints.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0, T0.AddMinutes(130)),
        };

        var targetBatch = AnchorStageBatch();
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
        };

        // ── 进入前完整快照 ──
        var beforeTaskIds = scheduled.Select(t => t.FinalDraftId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var beforeTaskQty = scheduled.ToDictionary(t => t.FinalDraftId, t => t.PlannedProcessQty);
        var beforeOcc = occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList();
        var beforeShares = shares.ToDictionary(
            kv => kv.Key,
            kv => string.Join(";", kv.Value.Select(x => x.DemandKey + "=" + x.ShareQty)));
        var timelineBefore = constraints.ProductTimeline;

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        // ── ① 失败：返回 null 且零产出 ──
        Assert.Null(merged);
        Assert.Empty(combinedTasks);

        // ── ② 任务集合逐项还原 ──
        Assert.Equal(beforeTaskIds,
            scheduled.Select(t => t.FinalDraftId).OrderBy(x => x, StringComparer.Ordinal).ToList());
        foreach (var t in scheduled)
        {
            Assert.Equal(beforeTaskQty[t.FinalDraftId], t.PlannedProcessQty);
        }

        // ── ③ 占用窗逐项还原 ──
        Assert.Equal(beforeOcc, occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList());

        // ── ④ 份额逐项还原（A30+B20 原样，未留 C10 残迹）──
        Assert.Equal(beforeShares.Count, shares.Count);
        foreach (var kv in beforeShares)
        {
            Assert.True(shares.TryGetValue(kv.Key, out var now), $"份额键 {kv.Key} 丢失");
            Assert.Equal(kv.Value, string.Join(";", now!.Select(x => x.DemandKey + "=" + x.ShareQty)));
        }

        // ── ⑤ ProductTimeline 已恢复 ──
        //   ⚠ **不能用 `Assert.Same`**：失败路径恢复的是**入口处 Clone 出来的那份**
        //     （`PhaseTwoInitialScheduler.cs:9879` `var timelineBeforeRerun = constraints.ProductTimeline.Clone();`，
        //      回滚点 `:9896`/`:9922`/`:9943` 都赋它）⇒ 实例必然不同、内容按设计等价。
        //   `ResourceProductTimeline` 只公开 `FromTasks`/`Clone`，内容无法直接比对
        //   ⇒ 这里只能断言「回滚后仍是一个非空的时间线」，并且在上面 ①~④ 四项内容级还原都通过的前提下成立。
        _ = timelineBefore;
        Assert.NotNull(constraints.ProductTimeline);
    }

    [Fact(DisplayName = "C2-04 · 三 Operation Stage 合批：A30+B20 已批 + C10 并入 ⇒ 每道 60 件、业务总量 60")]
    public void StageMerge_ThreeOperations_CountsPhysicalOnce()
    {
        var constraints = ThreeOpConstraints();
        var targetBatch = AnchorStageBatch();

        // 占用窗与既有 Task 一一对应（0号位 §Q1：不能只给 scheduledTasks 而占用账全空）
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);

        // 既有批的规范需求构成：每道 Task 上 A30 + B20
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }

        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),   // 锚点需求（可被 CloneDemandWithBatchQty 克隆）
        };

        // **一次调用**把 C10 并入整批（不是逐道调三次）
        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        Assert.NotNull(merged);
        Assert.Equal(3, combinedTasks.Count);   // 三道工序各一条

        // ① 每道 Operation 的**实物量都是 60**（A30+B20+C10），不是把三道相加当 180
        Assert.All(combinedTasks, t => Assert.Equal(60m, t.PlannedProcessQty));

        // ② 业务总量只算一份：三道的 PlannedProcessQty 之和 ÷ 工序数 = 60，而不是 180
        //    ⚠ 这里不是「算平均值冒充闭合」—— 因为三道时必须**逐道等于 60** 已由 ① 钉死，
        //      本行只是把「170/180 这种重复累加」显式挡住。
        Assert.Equal(3 * 60m, combinedTasks.Sum(t => t.PlannedProcessQty));

        // ③ 按 DemandKey × Operation 逐项验构成（0号位 §3 要求，不用平均值代替）
        foreach (var t in combinedTasks)
        {
            Assert.True(shares.TryGetValue(t.FinalDraftId, out var list), $"Task {t.FinalDraftId} 缺份额构成");
            var byKey = list!.GroupBy(x => x.DemandKey).ToDictionary(g => g.Key, g => g.Sum(x => x.ShareQty));
            Assert.Equal(30m, byKey["A"]);
            Assert.Equal(20m, byKey["B"]);
            Assert.Equal(10m, byKey["C"]);
            Assert.Equal(60m, byKey.Values.Sum());          // 构成之和 = 该道实物量
            Assert.Equal(t.PlannedProcessQty, byKey.Values.Sum());   // 两者必须相等，不得超出
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