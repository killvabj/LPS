using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **父件下界取「供给阈值 Stage 完成时间」，不是整链完成时间**
/// （0号位 2026-11-24 §二.3：「最早开始时间不得早于上游**最后必要数量**的合法阶段完成时间」；
///   同 0号位 2026-10-23 §3 R09-T11「既不提前释放不合格供给，也不错误压到末端完工时点」）。
///
/// <para>场景：子件链 `CS1(7h) → CS2(3h) → CS3(5h)`，`RequiredStageCode = CS2`。
/// 父件只依赖做到 CS2 的合格供给 ⇒ 下界应为 `Day+10h`（7h+3h）；
/// **不得**用整链完成 `Day+15h`，否则父件被不必要地推后 5 小时。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class RoutelessRequiredStageFloorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 1;
    private const int ChildMat = 2;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();
    private readonly ITestOutputHelper _out;

    public RoutelessRequiredStageFloorTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task 子件有供给阈值CS2_父件下界应为CS2完成时间_不得用整链完成时间()
    {
        var result = await _solver.SolveAsync(Build(requiredStageCode: "CS2"));

        Assert.True(result.Success, result.ErrorMessage);

        // 子件仍只计时、不产 Task
        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == ChildMat);

        var childFact = Assert.Single(result.DemandCompletions, f => f.LogicalDemandKey == "C1");

        // 子件自身：阈值完成 Day+10h，整链完成 Day+15h
        Assert.Equal(Day.AddHours(10), childFact.RequiredStageCompletionTime);
        Assert.Equal(Day.AddHours(15), childFact.CompletionTime);

        // 父件必须排下，且下界取阈值完成 Day+10h
        var parentTask = Assert.Single(result.FinalTasks, t => t.MaterialId == ParentMat);
        Assert.True(parentTask.PlannedStartTime >= Day.AddHours(10),
            $"父件 {parentTask.PlannedStartTime:O} 早于供给阈值完成 {Day.AddHours(10):O}");
        Assert.True(parentTask.PlannedStartTime < Day.AddHours(15),
            $"父件 {parentTask.PlannedStartTime:O} 被推到整链完成 {Day.AddHours(15):O} 之后 —— 用了整链时间而非阈值时间");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 0号位 2026-11-24 C 项：必须证明「下界在调度前/调度中实际约束父件」，
    //   不是只在求解后调整 Completion 输出。以下三条用例构成前后对拍。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **C-1 下界跟着子件走**：同一夹具，只把 `RequiredStageCode` 从 CS2 改成 CS3
    /// （阈值完成时间 Day+10h → Day+15h），父件开排必须随之后移。
    /// 若父件两次相同，说明下界没有真正进入排程。
    /// </summary>
    [Fact]
    public async Task C1_下界随子件阈值变化_阈值CS3的父件必须比CS2更晚()
    {
        var r2 = await _solver.SolveAsync(Build(requiredStageCode: "CS2"));
        var r3 = await _solver.SolveAsync(Build(requiredStageCode: "CS3"));
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.True(r3.Success, r3.ErrorMessage);

        var p2 = Assert.Single(r2.FinalTasks, t => t.MaterialId == ParentMat);
        var p3 = Assert.Single(r3.FinalTasks, t => t.MaterialId == ParentMat);

        _out.WriteLine($"C1 阈值CS2：子件完成 {r2.DemandCompletions.Single(f => f.LogicalDemandKey == "C1").CompletionTime:O} ⇒ 父件开排 {p2.PlannedStartTime:O}");
        _out.WriteLine($"C1 阈值CS3：子件完成 {r3.DemandCompletions.Single(f => f.LogicalDemandKey == "C1").CompletionTime:O} ⇒ 父件开排 {p3.PlannedStartTime:O}");

        Assert.True(p2.PlannedStartTime >= Day.AddHours(10),
            $"CS2 父件 {p2.PlannedStartTime:O} 早于阈值完成 {Day.AddHours(10):O}");
        Assert.True(p3.PlannedStartTime >= Day.AddHours(15),
            $"CS3 父件 {p3.PlannedStartTime:O} 早于阈值完成 {Day.AddHours(15):O}");
        Assert.True(p3.PlannedStartTime > p2.PlannedStartTime,
            $"阈值后移 5 小时但父件未动：CS2={p2.PlannedStartTime:O}，CS3={p3.PlannedStartTime:O}");
    }

    /// <summary>
    /// **C-2 前后对拍（因果）**：同一夹具，只保留/删除父子边。
    /// 有边 ⇒ 父件被推到 Day+10h 之后；无边 ⇒ 父件从规划起点开排。
    /// 两者只差这一条边 ⇒ 证明推后是这条边造成的。
    /// </summary>
    [Fact]
    public async Task C2_前后对拍_有父子边父件被推开_无边父件从规划起点开排()
    {
        var withLink = await _solver.SolveAsync(Build(requiredStageCode: "CS2", withLink: true));
        var withoutLink = await _solver.SolveAsync(Build(requiredStageCode: "CS2", withLink: false));
        Assert.True(withLink.Success, withLink.ErrorMessage);
        Assert.True(withoutLink.Success, withoutLink.ErrorMessage);

        var pw = Assert.Single(withLink.FinalTasks, t => t.MaterialId == ParentMat);
        var pn = Assert.Single(withoutLink.FinalTasks, t => t.MaterialId == ParentMat);

        _out.WriteLine($"C2 有父子边：父件开排 {pw.PlannedStartTime:O}（子件阈值完成 {Day.AddHours(10):O}）");
        _out.WriteLine($"C2 无父子边：父件开排 {pn.PlannedStartTime:O}");

        Assert.True(pw.PlannedStartTime >= Day.AddHours(10),
            $"有边：父件 {pw.PlannedStartTime:O} 早于子件阈值完成 {Day.AddHours(10):O}");
        Assert.True(pn.PlannedStartTime < Day.AddHours(10),
            $"无边：父件仍被推到 {pn.PlannedStartTime:O} —— 说明推后另有原因，本用例不成立");
    }

    /// <summary>
    /// **C-3 不可证 ⇒ 不假齐料**：父子边在、链条在，但子件缺 LT（算不出可信时间）。
    /// 父件必须阻断/未排，不得凭「看起来齐料」放行。
    /// </summary>
    [Fact]
    public async Task C3_反例_子件缺LT算不出可信时间_父件必须阻断_不得假齐料()
    {
        var result = await _solver.SolveAsync(Build(requiredStageCode: "CS2", childHasLeadTime: false));
        Assert.True(result.Success, result.ErrorMessage);

        _out.WriteLine($"C3 子件缺LT：父件 Task 数 = {result.FinalTasks.Count(t => t.MaterialId == ParentMat)}；" +
                       $"P1 未排 = {string.Join(" | ", result.UnscheduledTasks.Where(u => u.DraftId == "P1").Select(u => u.Reason))}");

        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == ParentMat);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "P1");
    }

    private static DomainSolveRequest Build(
        string? requiredStageCode,
        bool withLink = true,
        bool childHasLeadTime = true)
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
                    LogicalDemandKey = "C1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "C1",
                    MaterialId = ChildMat, FactoryId = 1, FactoryCode = "F1",
                    StartStageCode = "CS1", StartOperationCode = null,
                    RequiredStageCode = requiredStageCode,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null, DemandSequence = 1
                },
                new()
                {
                    LogicalDemandKey = "P1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 2, DemandKey = "P1",
                    MaterialId = ParentMat, FactoryId = 1, FactoryCode = "F1",
                    StartStageCode = "PS1", StartOperationCode = null,
                    NetOutputQty = 5m, PlannedProcessQty = 5m,
                    RequiredAvailableTime = Day.AddDays(30),
                    RouteCode = "RT", PathId = 1, DemandSequence = 2
                }
            },
            MaterialRequirementLinks = withLink
                ? new List<MaterialRequirementLink>
                {
                    new()
                    {
                        ConsumerLogicalDemandKey = "P1",
                        ProducerLogicalDemandKey = "C1",
                        LagMinutes = 0
                    }
                }
                : new List<MaterialRequirementLink>(),
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = ParentMat, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", StageCode = "PS1", StandardDuration = 600m,
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = ParentMat, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = ParentMat, StageCode = "PS1", ProductionDepartmentId = Dept },
                new() { MaterialId = ChildMat, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = ChildMat, StageCode = "CS2", ProductionDepartmentId = Dept },
                new() { MaterialId = ChildMat, StageCode = "CS3", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = childHasLeadTime
                ? new List<StageLeadTimeFact>
                {
                    new() { MaterialId = ChildMat, StageCode = "CS1", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
                    new() { MaterialId = ChildMat, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" },
                    new() { MaterialId = ChildMat, StageCode = "CS3", LeadTimeHours = 5m, MatchLevel = "DEPT_EXACT" }
                }
                : new List<StageLeadTimeFact>(),
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = ChildMat,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 1 },
                        new() { StageCode = "CS2", StageSeq = 2 },
                        new() { StageCode = "CS3", StageSeq = 3 }
                    }
                },
                new()
                {
                    MaterialId = ParentMat,
                    Stages = new List<StageSequenceStep> { new() { StageCode = "PS1", StageSeq = 1 } }
                }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddYears(100), IsAvailable = true }
            },
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
                        MaterialId = ParentMat, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    },
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = ChildMat, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
}
