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
/// **M06（0号位 v1.5）：「混合 Task + Timing、父 Task 实际时点同次 Solver 端到端」**。
///
/// <para>场景：父件有真实 Routing Task；子件**只有 Stage 链、没有 Routing 工序**（纯 Timing）。
/// 子件的完成时间来自 `StageTimingNodeBuilder` 的提前期累计（经 `BuildLeadTimeOnlyCompletion`
/// 进入父件下界），**不是**任何 `FinalTask`。</para>
///
/// <para>必须成立的两件事：</para>
/// <list type="number">
///   <item>父件 Task 的实际开工时间（`PlannedStartTime`）不得早于子件的阈值段完成时间；
///     —— 这是「实际时点」断言，不是「完成时间后移」这类间接指标。</item>
///   <item>父件确实被排上了（`FinalTasks` 里有它），不是靠不排来规避。</item>
/// </list>
///
/// <para>⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。</para>
/// </summary>
public class MixedTaskTimingParentFloorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 9401;
    private const int ChildMat = 9402;
    private const int Dept = 1701;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 父件有Task子件纯Timing_父Task实际开工不得早于子件阈值完成()
    {
        var result = await _solver.SolveAsync(Build());

        Assert.True(result.Success, result.ErrorMessage);

        // 子件 CS1(2h) → CS2(2h, 阈值) ⇒ 阈值完成 = Day + 4h
        var child = result.DemandCompletions.Single(c => c.LogicalDemandKey == "C1");
        Assert.Equal(Day.AddHours(4), child.RequiredStageCompletionTime);

        // 父件必须真的排上（不能靠不排来「不违反」）
        var parentTask = result.FinalTasks.Single(t => t.SourceDraftId == "P1");

        Assert.True(
            parentTask.PlannedStartTime >= Day.AddHours(4),
            $"父件 Task 实际开工 {parentTask.PlannedStartTime:O} 早于子件阈值完成 {Day.AddHours(4):O} ⇒ 跨物料时序被绕开");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build()
    {
        const string parentStage = "STAGE1";
        const string parentOp = "OP10";

        var demands = new List<LogicalProductionDemand>
        {
            new()
            {
                LogicalDemandKey = "P1", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 1, DemandKey = "P1", DemandSequence = 1,
                MaterialId = ParentMat, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = parentStage,
                NetOutputQty = 1m, PlannedProcessQty = 1m,
                RequiredAvailableTime = Day.AddDays(20)
            },
            new()
            {
                LogicalDemandKey = "C1", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 2, DemandKey = "C1", DemandSequence = 2,
                MaterialId = ChildMat, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = "CS1",
                RequiredStageCode = "CS2",              // 供给阈值 = CS2
                NetOutputQty = 1m, PlannedProcessQty = 1m,
                RequiredAvailableTime = Day.AddDays(20)
            }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(60),

            LogicalProductionDemands = demands,

            // 父件 ← 子件（唯一 BOM 边）
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                new()
                {
                    ConsumerLogicalDemandKey = "P1",
                    ProducerLogicalDemandKey = "C1",
                    ConsumerMaterialId = ParentMat,
                    ProducerMaterialId = ChildMat,
                    RequiredQty = 1m,
                    ProducerAllocationSequence = 2
                }
            },

            // 父件有真实工序；子件**故意不给**任何 Routing 工序
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = ParentMat, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = parentOp, StageCode = parentStage, StandardDuration = 3600m, SetupTime = 0m
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = ParentMat, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = parentOp, ResourceId = ParentMat, Priority = 1, CapacityFactor = 1m
                }
            },

            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = ParentMat, StageCode = parentStage, ProductionDepartmentId = Dept },
                new() { MaterialId = ChildMat, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = ChildMat, StageCode = "CS2", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(10) }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = ChildMat, StageCode = "CS1", LeadTimeHours = 2m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = ChildMat, StageCode = "CS2", LeadTimeHours = 2m, MatchLevel = "DEPT_EXACT" }
            },
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT", ChainSignature = "1:CS1|2:CS2",
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                        new() { StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 }
                    }
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "C1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },

            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = ParentMat, ResourceCode = "RP", FactoryCode = Factory, Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ParentMat, Start = Day, End = Day.AddYears(10), IsAvailable = true }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = ParentMat, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = true, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
