using System.Collections.Generic;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-20（1号位）：**失败执行批 + 多路径物料 + 需求无固定 Route/Path** 的取图口径。
///
/// 背景（实测入参里的三条隔离样本 `999561_135704 / 165555 / 185584`）：
///   Phase2 只在**胜出**批登记 `ChosenBatchRoutePaths`；批落不下 ⇒ 一条都不登记 ⇒
///   `TryGetBatchRoutingGraph` 按 Q-3「多路径且未登记 ⇒ 不猜」返回 false ⇒ Phase4 连工艺图都取不到，
///   **修复永远 0 Task**（跨窗兜底根本没机会跑）。
///
/// 本组用例锁四件事：
///   1. 未登记时**仍然不猜**（Q-3 红线不动）；
///   2. 失败批登记了候选路径 ⇒ 按 **Phase2 候选顺序**取图（不是重选路径）；
///   3. 候选键在本物料下缺失/物料根本没有图 ⇒ 仍 Fail Closed；
///   4. 候选试排克隆**隔离**（多次试排不得互相污染）。
/// </summary>
public class MultiPathFailedBatchRepairTests
{
    private const int MaterialId = 424242;

    private static RoutePathKey P(string route, int path) => new RoutePathKey(route, path);

    private static RoutingGraph Graph()
    {
        var g = new RoutingGraph();
        g.Operations[OperationNodeKey.Of("S1", "OP10")] = new OperationNode
        {
            OperationCode = "OP10",
            StageCode = "S1",
            StandardDuration = 6m,
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "R",
            PathId = 1,
            ProductionDepartmentId = 1,
        };
        return g;
    }

    /// <summary>两路径物料：`(R,1)` 与 `(R,2)`。</summary>
    private static ConstraintContext TwoPathContext()
    {
        var c = new ConstraintContext();
        c.RoutingGraphs = new Dictionary<int, Dictionary<RoutePathKey, RoutingGraph>>
        {
            [MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
            {
                [P("R", 1)] = Graph(),
                [P("R", 2)] = Graph(),
            }
        };
        return c;
    }

    [Fact(DisplayName = "多路径 + 未登记选中路径 + 需求无固定路径 ⇒ 仍 Fail Closed（不猜）")]
    public void MultiPath_Unregistered_StaysFailClosed()
    {
        var c = TwoPathContext();

        Assert.False(c.TryGetBatchRoutingGraph("EB|D|001", MaterialId, null, null, out var graph));
        Assert.Null(graph);

        Assert.False(c.TryGetDemandRoutingGraph("D", MaterialId, null, null, out var demandGraph));
        Assert.Null(demandGraph);
    }

    [Fact(DisplayName = "失败批候选路径：按 Phase2 候选顺序取图（不重排、不重选）")]
    public void FailedBatchAttemptedPaths_ResolveInRecordedOrder()
    {
        var c = TwoPathContext();
        c.FailedBatchAttemptedRoutePaths["EB|D|001"] = new List<RoutePathKey> { P("R", 2), P("R", 1) };

        Assert.True(c.TryGetFailedBatchRoutingGraphs("EB|D|001", MaterialId, out var graphs));
        Assert.NotNull(graphs);
        Assert.Equal(2, graphs!.Count);

        // 顺序 = 登记顺序（先 R/2 再 R/1），不是按 RouteCode/PathId 排序。
        Assert.Same(c.RoutingGraphs[MaterialId][P("R", 2)], graphs[0]);
        Assert.Same(c.RoutingGraphs[MaterialId][P("R", 1)], graphs[1]);
    }

    [Fact(DisplayName = "失败批候选路径：未知路径键跳过；全未知 ⇒ Fail Closed")]
    public void FailedBatchAttemptedPaths_SkipsUnknownKeys()
    {
        var c = TwoPathContext();
        c.FailedBatchAttemptedRoutePaths["EB|D|001"] = new List<RoutePathKey> { P("R", 99), P("R", 1) };

        Assert.True(c.TryGetFailedBatchRoutingGraphs("EB|D|001", MaterialId, out var graphs));
        Assert.Single(graphs!);
        Assert.Same(c.RoutingGraphs[MaterialId][P("R", 1)], graphs![0]);

        c.FailedBatchAttemptedRoutePaths["EB|D|002"] = new List<RoutePathKey> { P("R", 99) };
        Assert.False(c.TryGetFailedBatchRoutingGraphs("EB|D|002", MaterialId, out _));
    }

    [Fact(DisplayName = "失败批候选路径：物料无图 / 批未登记 ⇒ Fail Closed")]
    public void FailedBatchAttemptedPaths_NoGraphOrNoEntry()
    {
        var c = TwoPathContext();
        c.FailedBatchAttemptedRoutePaths["EB|D|001"] = new List<RoutePathKey> { P("R", 1) };

        Assert.False(c.TryGetFailedBatchRoutingGraphs("EB|D|999", MaterialId, out _));
        Assert.False(c.TryGetFailedBatchRoutingGraphs("EB|D|001", MaterialId + 1, out _));
    }

    [Fact(DisplayName = "候选试排克隆：失败批候选路径表互相隔离")]
    public void ShallowCloneForTrial_IsolatesAttemptedPaths()
    {
        var c = TwoPathContext();
        c.FailedBatchAttemptedRoutePaths["EB|D|001"] = new List<RoutePathKey> { P("R", 1) };

        var clone = c.ShallowCloneForTrial();
        clone.FailedBatchAttemptedRoutePaths["EB|D|001"].Add(P("R", 2));

        Assert.Single(c.FailedBatchAttemptedRoutePaths["EB|D|001"]);
        Assert.Equal(2, clone.FailedBatchAttemptedRoutePaths["EB|D|001"].Count);
    }
}
