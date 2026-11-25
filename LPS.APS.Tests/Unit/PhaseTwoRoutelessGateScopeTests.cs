using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **O-009 门禁范围核证**（0号位 2026-11-24 §A.3「门禁可能偏严的定向反例」）。
///
/// <para>0号位 提了两个疑点，本文件各用一条**正向**用例证伪/证实：</para>
/// <list type="number">
///   <item><b>门禁③（整物料一刀切？）</b>——0号位 疑：「同一物料 MACH 有 Routing、另一个合法 Stage
///     无 Routing 且有 LT」会不会被整物料否掉。<br/>
///     ✅ 实测：**不会**。门禁③ 只作用在「该物料**一条路由图都没有**」的分支
///     （`!TryGetRoutingGraphs` 命中处）；物料只要有任一 Path 图，就走正常排程，
///     无 Routing 的 Stage 由 `StageTimingNodeBuilder.ResolveRoutelessStages` 单独出计时。</item>
///   <item><b>门禁④（StartStageCode 必须非空？）</b>——0号位 引 2号位 DTO 合同「无 Routing 时
///     `StartStageCode` 可能为空」。<br/>
///     ✅ 实测：**空起点不拦**。门禁④ 只拦「**非空且不在链内**」的自相矛盾输入
///     （实测 FRL 280 条 `StartStageCode='TJ_MACH'` 而链只有 `TJ_OUTS`）。</item>
/// </list>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不改库。
/// </summary>
public class PhaseTwoRoutelessGateScopeTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 门禁3_同物料MACH有Routing_另一合法Stage无Routing且有LT_应仍为后者出计时()
    {
        // 物料只有 STAGE1（机加工）一条工序行 ⇒ 图存在 ⇒ 走正常排程；
        // STAGE2（如 FINAL/OUTS）无工序行但有 LT ⇒ 应由 StageTimingNodeBuilder 出计时。
        var req = Build(
            ops: new[]
            {
                new RoutingOperation
                {
                    MaterialId = M, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", StageCode = "STAGE1", StandardDuration = 600m,
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            leadTimes: new[] { ("STAGE1", 2m), ("STAGE2", 5m) },
            startStageCode: "STAGE1",
            withResource: true);

        var result = await _solver.SolveAsync(req);

        Assert.True(result.Success, result.ErrorMessage);

        // ① 有 Routing 的那段照常排下 Task —— 不因另一段无 Routing 而被整物料否掉
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal("STAGE1", task.StageCode);

        // ② 无 Routing 的那段仍有合法计时（不是被门禁③ 整物料拒掉）
        var fact = Assert.Single(result.DemandCompletions);
        Assert.Contains(fact.StageTimings, s => s.StageCode == "STAGE2");
        Assert.True(fact.CompletionTime > Day, "无 Routing 段的 LT 必须推进 CompletionTime，不得退化为 PlanningStart");
    }

    [Fact]
    public async Task 门禁4_StartStageCode为空_合法无Routing_应放行并只给计时()
    {
        // 2号位 DTO 合同：无 Routing 时 StartStageCode 可能为空。
        // 门禁④ 只拦「非空且不在链内」；空起点应可从链首定位 ⇒ 必须放行。
        var req = Build(
            ops: Array.Empty<RoutingOperation>(),
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            startStageCode: "",
            withResource: false);

        var result = await _solver.SolveAsync(req);

        Assert.True(result.Success, result.ErrorMessage);

        Assert.Empty(result.FinalTasks);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);
    }

    private static DomainSolveRequest Build(
        IReadOnlyList<RoutingOperation> ops,
        IReadOnlyList<(string Stage, int Seq)> chain,
        IReadOnlyList<(string Stage, decimal Hours)> leadTimes,
        string startStageCode,
        bool withResource)
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "D1",
                    MaterialId = M, FactoryId = 1,
                    StartStageCode = startStageCode, StartOperationCode = null,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null
                }
            },
            RoutingOperations = ops.ToList(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = withResource
                ? new List<OperationResourceEligibility>
                {
                    new()
                    {
                        MaterialId = M, ProductionDepartmentId = Dept,
                        RouteCode = "RT", PathId = 1, OperationCode = "OP10",
                        ResourceId = 1, Priority = 1, CapacityFactor = 1m
                    }
                }
                : new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = chain
                .Select(s => new MaterialStageDepartmentContextDto
                {
                    MaterialId = M, StageCode = s.Stage, ProductionDepartmentId = Dept
                })
                .ToList(),
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = leadTimes
                .Select(l => new StageLeadTimeFact
                {
                    MaterialId = M, StageCode = l.Stage, LeadTimeHours = l.Hours, MatchLevel = "DEPT_EXACT"
                })
                .ToList(),
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = M,
                    Stages = chain
                        .Select(s => new StageSequenceStep { StageCode = s.Stage, StageSeq = s.Seq })
                        .ToList()
                }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = withResource
                ? new List<ResourceDefinition>
                {
                    new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
                }
                : new List<ResourceDefinition>(),
            CalendarSlots = withResource
                ? new List<ResourceCalendarSlot>
                {
                    new() { ResourceId = 1, Start = Day, End = Day.AddYears(100), IsAvailable = true }
                }
                : new List<ResourceCalendarSlot>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD", AllowMerge = true, AllowSplit = false
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = M, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
}
