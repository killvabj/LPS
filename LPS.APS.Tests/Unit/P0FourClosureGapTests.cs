using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-04-A/B/C 定向破坏反例**（0号位 2026-11-17 v1.1 §3.1）。
///
/// <para>0号位 静态审核指出 `ValidateDemandStageClosure` 的三组放行风险：</para>
/// <list type="bullet">
/// <item><b>P0-04-A</b>：应有 Operation 完全缺失 —— 只聚合「出现的工序」，不比对选定 Path 的
///   全部必需 Operation。反例：Path 必含 OP10→OP20，删掉 OP20 只剩 OP10=10，应被拒。</item>
/// <item><b>P0-04-B</b>：必需 Stage 完全缺失 —— `domainSet` 只取已有 Task 的 Demand+Stage，
///   不比对选定 Path 的必需 Stage 全集。</item>
/// <item><b>P0-04-C</b>：份额血缘/执行批身份丢失 —— 未知 SourceDraftId / 未知 mergeLineage
///   不得静默放行或混批。</item>
/// </list>
///
/// <para>这三个用例直调 <c>ValidateHardResultForTest</c>（P0-04 测试入口），纯内存，不连库。
/// 当前预期 **A/B 红**（三层闭合不拦截缺工序/缺 Stage 的缺漏），C 由份额/其它门禁决定。</para>
/// </summary>
public sealed class P0FourClosureGapTests
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

    private static FinalTaskDraft TaskTask(string id, string source, string op, decimal qty, string route = Route, string? seb = "SEB1")
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = source,
            MaterialId = MaterialId,
            FactoryId = 1,
            StageCode = Stage,
            OperationCode = op,
            RouteCode = route,
            PathId = 1,
            StageExecutionBatchDraftKey = seb,
            ExecutionBatchDraftKey = seb,
            Quantity = qty,
            PlannedProcessQty = qty,
            SetupTime = 0m,
            PlannedStartTime = new DateTime(2026, 11, 10, 8, 0, 0),
            PlannedEndTime = new DateTime(2026, 11, 10, 9, 0, 0),
        };

    /// <summary>Path = OP10 → OP20（同 Stage），Route=RT Path=1。</summary>
    private static RoutingGraph TwoOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in new[] { "OP10", "OP20" })
        {
            g.Operations[OperationNodeKey.Of(Stage, op)] = new OperationNode
            {
                OperationCode = op,
                StageCode = Stage,
                StandardDuration = 1m,
                OperationPlanningMode = "FINITE_RESOURCE",
                RouteCode = Route,
                PathId = 1,
                ProductionDepartmentId = 1,
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
        RoutingGraph graph,
        List<AllocationTaskShare>? shares = null,
        Dictionary<string, List<(string, decimal)>>? mergeLineage = null,
        string demandKey = "D1")
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            shares ?? new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest
            {
                LogicalProductionDemands = new List<LogicalProductionDemand> { Demand(demandKey, 10m) },
            },
            Context(graph),
            new HashSet<string>(),
            mergeLineage ?? new Dictionary<string, List<(string, decimal)>>());

    [Fact(DisplayName = "P0-04-A 缺必需Operation(OP20) 应拒")]
    public void A_MissingRequiredOperation_ShouldReject()
    {
        // 路径要求 OP10→OP20，但只排了 OP10=10；OP20 缺失。
        var (valid, _) = Run(
            new List<FinalTaskDraft> { TaskTask("T1", "D1", "OP10", 10m) },
            TwoOpGraph(),
            shares: new List<AllocationTaskShare> { new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 10m } });

        Assert.False(valid, "缺必需 Operation（OP20）却只按出现工序聚合 ⇒ 必须被最终硬门禁拒绝。");
    }

    [Fact(DisplayName = "P0-04-B 缺必需Stage 应拒")]
    public void B_MissingRequiredStage_ShouldReject()
    {
        // 路径跨 STAGE1(OP10)→STAGE2(OP20)，只排了 STAGE1 的 OP10；STAGE2 缺失。
        var g = new RoutingGraph();
        g.Operations[OperationNodeKey.Of("STAGE1", "OP10")] = new OperationNode
        {
            OperationCode = "OP10", StageCode = "STAGE1", StandardDuration = 1m,
            OperationPlanningMode = "FINITE_RESOURCE", RouteCode = Route, PathId = 1, ProductionDepartmentId = 1,
        };
        g.Operations[OperationNodeKey.Of("STAGE2", "OP20")] = new OperationNode
        {
            OperationCode = "OP20", StageCode = "STAGE2", StandardDuration = 1m,
            OperationPlanningMode = "FINITE_RESOURCE", RouteCode = Route, PathId = 1, ProductionDepartmentId = 1,
        };
        g.Dependencies[OperationNodeKey.Of("STAGE2", "OP20")] = new List<DependencyEdge>
        {
            new() { From = OperationNodeKey.Of("STAGE1", "OP10"), To = OperationNodeKey.Of("STAGE2", "OP20") },
        };
        g.RootOperations.Add(OperationNodeKey.Of("STAGE1", "OP10"));

        var t1 = TaskTask("T1", "D1", "OP10", 10m);

        var (valid, _) = Run(
            new List<FinalTaskDraft> { t1 },
            g,
            shares: new List<AllocationTaskShare> { new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 10m } });

        Assert.False(valid, "缺必需 Stage（STAGE2）却只按出现 Stage 核 ⇒ 必须被最终硬门禁拒绝。");
    }

    [Fact(DisplayName = "P0-04-C 未知SourceDraftId血缘丢失 不得静默放行")]
    public void C_UnknownSourceDraftId_ShouldNotSilentlyPass()
    {
        // 一条 Task 的 SourceDraftId 指向不存在的需求，其数量不得从闭合里静默消失。
        var (valid, error) = Run(
            new List<FinalTaskDraft> { TaskTask("T1", "GHOST", "OP10", 10m) },
            TwoOpGraph());

        // 现状：GetTaskDemandComposition 对未知 SourceDraftId 返回空构成，该 10 件可能不进入任何核证域。
        // 期望：由份额闭合或其它 Hard Result 拦截（不得整条静默放行）。
        Assert.True(!valid || !string.IsNullOrEmpty(error),
            "未知血缘的 Task 数量不得静默消失；应该 IsValid=false 或至少产出失败原因。");
    }
}