using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-04 C01～C14 数量闭合正反例**（0号位 2026-11-11 §4）。
///
/// <para>直调 <c>ValidateHardResultForTest</c>（P0-04 第 8 项测试入口），纯内存。
/// 本文件先覆盖**单路径/单 Stage** 的干净判别对：C03（同 Path 5+5 通过）、C04（重复生产 20 拒）、
/// C05（少 1 拒）、C07（同批混 Path 拒）、C09（同 Op 拆两个部分 Task 通过）、C11（合批多需求通过/缺键拒）。
/// 多路径（C01/C02/C10/C12）、A-B 逐工序（C13）、UOM 精度（C14）需更富夹具，另补。</para>
/// </summary>
public sealed class P0FourQuantityClosureC0xTests
{
    private const int MaterialId = 1;
    private const string Stage = "STAGE1";
    private const string Route = "RT";

    private static LogicalProductionDemand Demand(string key, decimal qty) => new()
    {
        LogicalDemandKey = key,
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = 1L,
        DemandKey = key,
        MaterialId = MaterialId,
        FactoryId = 1,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        DemandSequence = 1,
        StartStageCode = Stage,
        RouteCode = Route,
        PathId = 1,
    };

    private static FinalTaskDraft TaskTask(
        string id, string source, string op, decimal qty,
        string route = Route, long? path = 1, string? seb = null)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = source,
            MaterialId = MaterialId,
            FactoryId = 1,
            StageCode = Stage,
            OperationCode = op,
            RouteCode = route,
            PathId = path,
            StageExecutionBatchDraftKey = seb,
            ExecutionBatchDraftKey = seb,
            Quantity = qty,
            PlannedProcessQty = qty,
            SetupTime = 0m,
            PlannedStartTime = new DateTime(2026, 11, 10, 8, 0, 0),
            PlannedEndTime = new DateTime(2026, 11, 10, 9, 0, 0),
        };

    private static RoutingGraph TwoOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in new[] { "OP10", "OP20" })
        {
            g.Operations[OperationNodeKey.Of(Stage, op)] = new OperationNode
            {
                OperationCode = op, StageCode = Stage, StandardDuration = 1m,
                OperationPlanningMode = "FINITE_RESOURCE", RouteCode = Route, PathId = 1, ProductionDepartmentId = 1,
            };
        }
        g.Dependencies[OperationNodeKey.Of(Stage, "OP20")] = new List<DependencyEdge>
        {
            new() { From = OperationNodeKey.Of(Stage, "OP10"), To = OperationNodeKey.Of(Stage, "OP20") },
        };
        g.RootOperations.Add(OperationNodeKey.Of(Stage, "OP10"));
        return g;
    }

    private static ConstraintContext Context(RoutingGraph graph)
    {
        var c = new ConstraintContext();
        c.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
        {
            [RoutePathKey.Of(Route, 1)] = graph,
        };
        return c;
    }

    private static (bool Valid, string Error) Run(
        List<FinalTaskDraft> tasks,
        LogicalProductionDemand[] demands,
        List<AllocationTaskShare>? shares = null,
        Dictionary<string, List<(string, decimal)>>? mergeLineage = null,
        HashSet<string>? unscheduled = null)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            shares ?? new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = demands.ToList() },
            Context(TwoOpGraph()),
            unscheduled ?? new HashSet<string>(),
            mergeLineage ?? new Dictionary<string, List<(string, decimal)>>());

    // C03：两批同 Path 5+5，数量闭合通过。
    [Fact(DisplayName = "C03 两批同Path 5+5 通过")]
    public void C03_TwoBatchesSamePath_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 5m, seb: "EB1"), TaskTask("T2", "D1", "OP20", 5m, seb: "EB1"),
            TaskTask("T3", "D1", "OP10", 5m, seb: "EB2"), TaskTask("T4", "D1", "OP20", 5m, seb: "EB2"),
        };
        var (valid, err) = Run(tasks, new[] { Demand("D1", 10m) },
            shares: new List<AllocationTaskShare>
            {
                new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 5m },
                new() { FinalDraftId = "T3", AllocationSequence = 1L, ComponentQty = 5m },
            });
        Assert.True(valid, err);
    }

    // C04：Demand10，两批各10（不同 Path 亦同判），总产20 ⇒ 重复生产必须拒。
    [Fact(DisplayName = "C04 重复生产20 拒")]
    public void C04_DuplicateProduction_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 10m, seb: "EB1"), TaskTask("T2", "D1", "OP20", 10m, seb: "EB1"),
            TaskTask("T3", "D1", "OP10", 10m, seb: "EB2"), TaskTask("T4", "D1", "OP20", 10m, seb: "EB2"),
        };
        var (valid, err) = Run(tasks, new[] { Demand("D1", 10m) });
        Assert.False(valid, "两批各 10 = 20 > 需求 10，必须被数量闭合拒绝。");
    }

    // C05：Demand10，只排了 6 却声明全部已排 ⇒ 少 4 必须拒。
    [Fact(DisplayName = "C05 只排6声明已排 拒")]
    public void C05_UnderProduced_DeclaredFull_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 6m, seb: "EB1"), TaskTask("T2", "D1", "OP20", 6m, seb: "EB1"),
        };
        var (valid, err) = Run(tasks, new[] { Demand("D1", 10m) });
        Assert.False(valid, "已排需求却少 4，必须被 DEMAND_STAGE_QTY_GAP 拒绝。");
    }

    // C07：同一批（同 SEB）混入两条不同 Path ⇒ 非法混 Path 必须拒。
    [Fact(DisplayName = "C07 同批混两条Path 拒")]
    public void C07_MixedPathInSameBatch_ShouldReject()
    {
        var g2 = new RoutingGraph();
        g2.RootOperations.Add(OperationNodeKey.Of(Stage, "MIX"));
        var ctx = Context(TwoOpGraph());
        ctx.RoutingGraphs[MaterialId][RoutePathKey.Of("RT2", 2)] = g2;

        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 10m, route: Route, path: 1, seb: "X"),
            TaskTask("T2", "D1", "OP20", 10m, route: "RT2", path: 2, seb: "X"),
        };
        var (valid, err) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = new List<LogicalProductionDemand> { Demand("D1", 10m) } },
            ctx,
            new HashSet<string>(),
            new Dictionary<string, List<(string, decimal)>>());
        Assert.False(valid, "同一批混两条 Path 必须被 BATCH_ROUTE_MIXED 拒绝。");
    }

    // C09：Phase4 让同一批同一 Operation 合法拆成两个部分 Task，总量闭合 ⇒ 通过（不得用唯一 Task 限制误杀）。
    [Fact(DisplayName = "C09 同Op拆两个部分Task 通过")]
    public void C09_PartialSplitSameOp_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 4m, seb: "EB1"), TaskTask("T2", "D1", "OP10", 6m, seb: "EB1"),
            TaskTask("T3", "D1", "OP20", 10m, seb: "EB1"),
        };
        var (valid, err) = Run(tasks, new[] { Demand("D1", 10m) });
        Assert.True(valid, "同 Op 拆 4+6，总量仍 10 ⇒ 合法拆分不得被唯一 Task 限制误杀。" + err);
    }

    // C11：合批 Task 承载多需求份额，各需求分别守恒 ⇒ 通过。
    [Fact(DisplayName = "C11 合批多需求 通过")]
    public void C11_MergedTaskMultiDemand_Pass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskTask("T1", "D1", "OP10", 30m, seb: "EB1"), TaskTask("T2", "D1", "OP20", 30m, seb: "EB1"),
        };
        var lineage = new Dictionary<string, List<(string, decimal)>>(StringComparer.Ordinal)
        {
            ["T1"] = new List<(string, decimal)> { ("D2", 20m) },
            ["T2"] = new List<(string, decimal)> { ("D2", 20m) },
        };
        var (valid, err) = Run(tasks,
            new[] { Demand("D1", 10m), Demand("D2", 20m) },
            mergeLineage: lineage);
        Assert.True(valid, "合批 Task 30 = D1(10 锚) + D2(20 份额)，各需求守恒 ⇒ 通过。" + err);
    }
}