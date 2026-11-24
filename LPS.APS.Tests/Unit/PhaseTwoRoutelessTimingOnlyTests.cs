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
/// **O-009 回归**（0号位 2026-11-23《最新分桶回执审核与可立即整改》§二A）：
///   「合法无 Routing 的 Stage 应形成仅计时的 StageTiming 约束节点，**不伪造制造 Task、
///    不丢依赖、不默认 24h/7×24、不直接视为 `P2_NO_ROUTING`**；
///    对没有合法 Stage 链或 LT 的需求继续 Fail-Closed。」
///
/// 本文件给**正例 + 两个反例**（反例是"不能放行"的鉴别性证据）：
///   · 正例：无工序行 + 有 Stage 链 + 链上每段都有 LT ⇒ **不进 `UnscheduledTasks`**，且产出 StageTimings；
///   · 反例 ①（缺 LT）：无工序行 + 有链但链上一段无 LT ⇒ **仍进 `UnscheduledTasks`**；
///   · 反例 ②（有工序行）：输入含工序行 ⇒ **不得**按"合法无 Routing"放行，仍进 `UnscheduledTasks`。
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class PhaseTwoRoutelessTimingOnlyTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 正例_无工序行_有链且每段有LT_应只形成StageTiming_不计未排()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            eligibility: Array.Empty<(string, int, string, int)>(),
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) }));

        Assert.True(result.Success, result.ErrorMessage);

        // ① 不生成 Task（"不伪造制造 Task"）
        Assert.Empty(result.FinalTasks);

        // ② 不再被登记为未排（这正是 17,962 条错停的根因）
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");

        // ③ 但 Demand 级仍有**合法计时**：无末端 Task + StageTimings 非空 = 0号位 三态中的 (ii)
        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal("D1", fact.LogicalDemandKey);
        Assert.Null(fact.TerminalFinalDraftId);
        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal("STAGE1", fact.StageTimings[0].StageCode);
        Assert.Equal(Day, fact.StageTimings[0].StartTime);
        Assert.Equal(Day.AddHours(7), fact.StageTimings[1].StartTime);
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);
    }

    [Fact]
    public async Task 反例1_无工序行_有链但缺一段LT_仍应未排_不得局部编时间()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            eligibility: Array.Empty<(string, int, string, int)>(),
            leadTimes: new[] { ("STAGE1", 7m) },   // ← STAGE2 缺 LT
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        // 缺 LT ⇒ 按冻结 `STAGE_LEADTIME_MISSING`，本需求**局部未排**
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact]
    public async Task 反例2_输入含工序行_即使有链有LT_也不得按合法无Routing放行()
    {
        var result = await _solver.SolveAsync(Build(
            ops: new[]
            {
                new RoutingOperation
                {
                    MaterialId = M, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", StageCode = "STAGE1", StandardDuration = 60m,
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            eligibility: Array.Empty<(string, int, string, int)>(),   // 无资格行 ⇒ 排不下
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        // 输入**含**工序行 ⇒ 不是"合法无 Routing"，必须维持未排（否则会掩盖 BuildRoutingGraphs 缺陷）
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact]
    public async Task 反例5_起点Stage不在链内_即使有链有LT_仍应未排_不得静默丢失()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            eligibility: Array.Empty<(string, int, string, int)>(),
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            startStageCode: "STAGE9"));   // ← 链里没有 STAGE9 ⇒ Phase1 可达集为空

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        // 放行会造成"既不排、也无时间"的静默丢失 ⇒ 必须留在未排
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact]
    public async Task 反例3_无WORK窗口_即使有链有LT_仍应未排_不得假造时间()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            eligibility: Array.Empty<(string, int, string, int)>(),
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            withWorkWindow: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        // 0号位 §二：缺日历只让受影响 Demand **正常未排**，禁止假时间；也不得"既不排也无时间"地静默丢失。
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact]
    public async Task 反例4_缺部门上下文_即使有链有LT_仍应未排()
    {
        var result = await _solver.SolveAsync(Build(
            ops: Array.Empty<RoutingOperation>(),
            eligibility: Array.Empty<(string, int, string, int)>(),
            leadTimes: new[] { ("STAGE1", 7m), ("STAGE2", 3m) },
            chain: new[] { ("STAGE1", 1), ("STAGE2", 2) },
            withDeptContext: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    private static DomainSolveRequest Build(
        IReadOnlyList<RoutingOperation> ops,
        IReadOnlyList<(string Route, int PathId, string Op, int ResourceId)> eligibility,
        IReadOnlyList<(string Stage, decimal Hours)> leadTimes,
        IReadOnlyList<(string Stage, int Seq)> chain,
        bool withWorkWindow = true,
        bool withDeptContext = true,
        string? startStageCode = "STAGE1")
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
            OperationResourceEligibility = eligibility
                .Select(e => new OperationResourceEligibility
                {
                    MaterialId = M, ProductionDepartmentId = Dept,
                    RouteCode = e.Route, PathId = e.PathId, OperationCode = e.Op,
                    ResourceId = e.ResourceId, Priority = 1, CapacityFactor = 1m
                })
                .ToList(),
            MaterialStageDepartmentContexts = withDeptContext
                ? chain
                    .Select(s => new MaterialStageDepartmentContextDto
                    {
                        MaterialId = M, StageCode = s.Stage, ProductionDepartmentId = Dept
                    })
                    .ToList()
                : new List<MaterialStageDepartmentContextDto>(),
            // 24×7 连续窗口 ⇒ 「有效生产小时」与「墙钟小时」逐位相同，便于断言
            DepartmentWorkWindows = withWorkWindow
                ? new[]
                {
                    new DepartmentWorkWindow
                    {
                        FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                    }
                }
                : Array.Empty<DepartmentWorkWindow>(),
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
            Resources = new List<ResourceDefinition>(),
            CalendarSlots = new List<ResourceCalendarSlot>(),
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
