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
/// **0号位 v1.8 §2.1 要求的小例**（「先小例覆盖足量部分、部分不足、两子件不同到达……
///   断言实际父 Task 起点和剩余量」）。
///
/// <para>覆盖三件事：</para>
/// <list type="number">
///   <item>**足量**：子件已落定净产出 ≥ 本边需求量 ⇒ 父件**允许**排，且起点不得早于子件完成；</item>
///   <item>~~部分不足~~（**未覆盖**，见提交说明：载体语义待裁）；</item>
///   <item>**两子件不同到达**：父件起点取**较晚**那个子件的完成时间。</item>
/// </list>
///
/// <para>⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。</para>
/// </summary>
public class ParentReevalQuantityGateTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 9901;
    private const int ChildMatA = 9902;
    private const int ChildMatB = 9903;
    private const int Dept = 2001;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 足量_子件落定量够_父件排且起点不早于子件完成()
    {
        var result = await _solver.SolveAsync(Build(parentNeed: 10m, childAQty: 10m, childBQty: null, bLate: false));

        Assert.True(result.Success, result.ErrorMessage);

        var parentTasks = result.FinalTasks.Where(t => t.SourceDraftId == "P").ToList();
        Assert.NotEmpty(parentTasks);

        var childEnd = result.FinalTasks.Where(t => t.SourceDraftId == "CA").Max(t => t.PlannedEndTime);
        Assert.True(parentTasks.Min(t => t.PlannedStartTime) >= childEnd,
            "父件起点不得早于子件完成（跨物料时序）");
        Assert.Equal(10m, parentTasks.First().Quantity);   // 父件按自身净量排，不被子件量顶替
    }

    [Fact]
    public async Task 两子件不同到达_父件起点取较晚那个子件()
    {
        var result = await _solver.SolveAsync(Build(parentNeed: 10m, childAQty: 10m, childBQty: 10m, bLate: true));

        Assert.True(result.Success, result.ErrorMessage);

        var aEnd = result.FinalTasks.Where(t => t.SourceDraftId == "CA").Max(t => t.PlannedEndTime);
        var bEnd = result.FinalTasks.Where(t => t.SourceDraftId == "CB").Max(t => t.PlannedEndTime);
        var later = aEnd > bEnd ? aEnd : bEnd;

        var parentStart = result.FinalTasks.Where(t => t.SourceDraftId == "P").Min(t => t.PlannedStartTime);
        Assert.True(parentStart >= later,
            $"父件起点 {parentStart:O} 必须 ≥ 较晚子件完成 {later:O}（A={aEnd:O}, B={bEnd:O}）");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    /// <param name="parentNeed">父件从**每个**子件承接的需求量（写入 RequiredQty）</param>
    /// <param name="childAQty">子件 A 的净量</param>
    /// <param name="childBQty">子件 B 的净量；null ⇒ 不加第二个子件</param>
    /// <param name="bLate">子件 B 的日历从第 10 天才开始 ⇒ 完成明显晚于 A</param>
    private static DomainSolveRequest Build(decimal parentNeed, decimal childAQty, decimal? childBQty, bool bLate)
    {
        const string stage = "S1";
        const string op = "OP10";

        var demands = new List<LogicalProductionDemand>
        {
            new()
            {
                LogicalDemandKey = "P", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 1, DemandKey = "P", DemandSequence = 1,
                MaterialId = ParentMat, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = parentNeed, PlannedProcessQty = parentNeed,
                RequiredAvailableTime = Day.AddDays(40)
            },
            new()
            {
                LogicalDemandKey = "CA", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 2, DemandKey = "CA", DemandSequence = 2,
                MaterialId = ChildMatA, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = childAQty, PlannedProcessQty = childAQty,
                RequiredAvailableTime = Day.AddDays(40)
            }
        };
        if (childBQty is decimal bq)
        {
            demands.Add(new LogicalProductionDemand
            {
                LogicalDemandKey = "CB", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 3, DemandKey = "CB", DemandSequence = 3,
                MaterialId = ChildMatB, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = bq, PlannedProcessQty = bq,
                RequiredAvailableTime = Day.AddDays(40)
            });
        }

        var links = new List<MaterialRequirementLink>
        {
            new() { ConsumerLogicalDemandKey = "P", ProducerLogicalDemandKey = "CA",
                    ConsumerMaterialId = ParentMat, ProducerMaterialId = ChildMatA,
                    RequiredQty = parentNeed, ProducerAllocationSequence = 2 }
        };
        if (childBQty is not null)
        {
            links.Add(new MaterialRequirementLink
            {
                ConsumerLogicalDemandKey = "P", ProducerLogicalDemandKey = "CB",
                ConsumerMaterialId = ParentMat, ProducerMaterialId = ChildMatB,
                RequiredQty = parentNeed, ProducerAllocationSequence = 3
            });
        }

        var mats = new List<int> { ParentMat, ChildMatA };
        if (childBQty is not null) mats.Add(ChildMatB);

        var ops = mats.Select(m => new RoutingOperation
        {
            MaterialId = m, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
            OperationCode = op, StageCode = stage, StandardDuration = 60m, SetupTime = 0m
        }).ToList();

        var elig = mats.Select(m => new OperationResourceEligibility
        {
            MaterialId = m, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
            OperationCode = op, ResourceId = 20000 + m, Priority = 1, CapacityFactor = 1m
        }).ToList();

        var depts = mats.Select(m => new MaterialStageDepartmentContextDto
        {
            MaterialId = m, StageCode = stage, ProductionDepartmentId = Dept
        }).ToList();

        var resources = mats.Select(m => new ResourceDefinition
        {
            ResourceId = 20000 + m, ResourceCode = $"R{m}", FactoryCode = Factory, Capacity = 1m
        }).ToList();

        var slots = mats.Select(m => new ResourceCalendarSlot
        {
            ResourceId = 20000 + m,
            Start = (bLate && m == ChildMatB) ? Day.AddDays(10) : Day,
            End = Day.AddDays(40),
            IsAvailable = true
        }).ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(40),
            LogicalProductionDemands = demands,
            MaterialRequirementLinks = links,
            RoutingOperations = ops,
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(40) }
            },
            Resources = resources,
            CalendarSlots = slots,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = mats.Select(m => new BatchPolicyRuleSnapshot
                {
                    MaterialId = m, ProductionDepartmentId = Dept,
                    MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                    AllowSplit = false, AllowMerge = true
                }).ToList(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
