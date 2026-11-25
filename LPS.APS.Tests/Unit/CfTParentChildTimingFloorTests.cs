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
/// **CF-T 父子时间传播**（0号位 2026-11-24 §A.4：「对合法无 Routing 的 Stage 独立计时，
///   **可传播其合法时间**；对有生产来源未排／无可信 ETA 的父件，**不得假齐料**」）。
///
/// <para>背景：O-009 把合法无 Routing 的需求放行为「仅计时、不产 Task」后，其父件取
///   `demandCompletion` 永远没有条目 ⇒ 曾被判 `P2_CHILD_UNAVAILABLE`（FRL 实测 12,039 个父件）。
///   本文件锁定修复后的行为：**合法计时的子件可以给父件提供时间下界**，且父件 Task 不得早于该时间。</para>
///
/// <para>反例守住红线：子件**没有** Stage 链／LT（算不出可信时间）⇒ 父件仍必须被阻断，
///   **不得**凭"看起来齐料"放行。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class CfTParentChildTimingFloorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 1;
    private const int ChildMat = 2;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 正例_子件仅计时_其合法完成时间应成为父件下界_父件必须排出Task()
    {
        var result = await _solver.SolveAsync(Build(childHasTiming: true));

        Assert.True(result.Success, result.ErrorMessage);

        // 父件必须排下（此前会被判 P2_CHILD_UNAVAILABLE）
        var parentTask = Assert.Single(result.FinalTasks, t => t.MaterialId == ParentMat);

        // 子件只计时、不产 Task
        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == ChildMat);

        // 父件开排时间不得早于子件预计完成（Day + 7h + 3h = Day+10h）
        Assert.True(parentTask.PlannedStartTime >= Day.AddHours(10),
            $"父件开排时间 {parentTask.PlannedStartTime:O} 早于子件预计完成 {Day.AddHours(10):O}");

        // 子件的完成事实必须存在且为合法计时
        var childFact = Assert.Single(result.DemandCompletions, f => f.LogicalDemandKey == "C1");
        Assert.Null(childFact.TerminalFinalDraftId);
        Assert.True(childFact.StageTimings.Count > 0);
        Assert.Equal(Day.AddHours(10), childFact.CompletionTime);
    }

    [Fact]
    public async Task 反例_子件算不出可信时间_父件必须仍被阻断_不得假齐料()
    {
        var result = await _solver.SolveAsync(Build(childHasTiming: false));

        Assert.True(result.Success, result.ErrorMessage);

        // 子件无链无 LT ⇒ 无合法计时 ⇒ 父件不得开排
        Assert.DoesNotContain(result.FinalTasks, t => t.MaterialId == ParentMat);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "P1");
    }

    private static DomainSolveRequest Build(bool childHasTiming)
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
                    MaterialId = ChildMat, FactoryId = 1,
                    StartStageCode = childHasTiming ? "CS1" : "",
                    StartOperationCode = null,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null, DemandSequence = 1
                },
                new()
                {
                    LogicalDemandKey = "P1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 2, DemandKey = "P1",
                    MaterialId = ParentMat, FactoryId = 1,
                    StartStageCode = "PS1", StartOperationCode = null,
                    NetOutputQty = 5m, PlannedProcessQty = 5m,
                    RequiredAvailableTime = Day.AddDays(30),
                    RouteCode = "RT", PathId = 1, DemandSequence = 2
                }
            },
            // 父件依赖子件（子先排、父后排）
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                new()
                {
                    ConsumerLogicalDemandKey = "P1",
                    ProducerLogicalDemandKey = "C1",
                    LagMinutes = 0
                }
            },
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
            MaterialStageDepartmentContexts = childHasTiming
                ? new List<MaterialStageDepartmentContextDto>
                {
                    new() { MaterialId = ParentMat, StageCode = "PS1", ProductionDepartmentId = Dept },
                    new() { MaterialId = ChildMat, StageCode = "CS1", ProductionDepartmentId = Dept },
                    new() { MaterialId = ChildMat, StageCode = "CS2", ProductionDepartmentId = Dept }
                }
                : new List<MaterialStageDepartmentContextDto>
                {
                    new() { MaterialId = ParentMat, StageCode = "PS1", ProductionDepartmentId = Dept }
                },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = childHasTiming
                ? new List<StageLeadTimeFact>
                {
                    new() { MaterialId = ChildMat, StageCode = "CS1", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
                    new() { MaterialId = ChildMat, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" }
                }
                : new List<StageLeadTimeFact>(),
            StageSequenceChains = childHasTiming
                ? new List<StageSequenceChain>
                {
                    new()
                    {
                        MaterialId = ChildMat,
                        Stages = new List<StageSequenceStep>
                        {
                            new() { StageCode = "CS1", StageSeq = 1 },
                            new() { StageCode = "CS2", StageSeq = 2 }
                        }
                    },
                    new()
                    {
                        MaterialId = ParentMat,
                        Stages = new List<StageSequenceStep> { new() { StageCode = "PS1", StageSeq = 1 } }
                    }
                }
                : new List<StageSequenceChain>
                {
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
