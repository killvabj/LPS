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
/// **0号位《六件审核与未排案例补证执行函》v1.0 §3 逐字**：
/// 「子件合法执行批确定、**回滚**、修复、压缩或可用份额/时间变化时，受影响父件应在**同次 Solver 内**
///   基于最新可信状态正确评价……**不要只清失败码或改 Completion**。保留失败历史与最终原因区分。
///   先小例覆盖足量部分、部分不足、两子件不同到达、**撤销/回滚**、后续修复、SH40+60，
///   断言实际父 Task 起点和剩余量。」
///
/// <para><b>本文件专门补「撤销 / 回滚」这一格</b>（其余几格见 `ParentReevalQuantityGateTests`
///   —— 足量、两子件不同到达；以及 `WorkHandoffWaitTests.E4_整单两条交接规则40与60_各应用一次_不与StageLT重复`
///   —— SH 40+60）。锁两件事：</para>
/// <list type="number">
///   <item>**子件能排** ⇒ 父件排在子件完成之后（下界来自真实 Task，不是声明值）；</item>
///   <item>**子件排不下（等价于「其执行批被撤销」）** ⇒ 父件**不得**沿用任何旧时间、
///         **不得**凭空排下 ⇒ 父件也 0 Task，且未排原因可追，**不是**悄悄成功。</item>
/// </list>
///
/// <para><b>为什么这算「回滚」的可测形态</b>：本 Solver 无跨 Run 状态，真正的「批被撤销」表现为
///   「该子件在本次求解内不产出任何 Task」。若父件仍按某个已失效的时间排下去，就是
///   「只清失败码 / 沿用旧 Completion」那一类错；反之父件跟着不排，才说明它按**最新状态**评价。</para>
///
/// <para>⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。</para>
/// </summary>
public class ParentReevalAfterChildRollbackTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 9401;
    private const int ChildMat = 9402;
    private const int Dept = 2401;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>子件能排 ⇒ 父件必须排在子件真实完成之后，且按自身净量排。</summary>
    [Fact]
    public async Task 子件已排_父件下界取子件真实完成时点()
    {
        var result = await _solver.SolveAsync(Build(childCanSchedule: true));

        Assert.True(result.Success, result.ErrorMessage);

        var childTasks = result.FinalTasks.Where(t => t.SourceDraftId == "C").ToList();
        Assert.NotEmpty(childTasks);

        var parentTasks = result.FinalTasks.Where(t => t.SourceDraftId == "P").ToList();
        Assert.NotEmpty(parentTasks);

        Assert.True(parentTasks.Min(t => t.PlannedStartTime) >= childTasks.Max(t => t.PlannedEndTime),
            "父件起点必须 ≥ 子件真实完成时点");
        Assert.Equal(5m, parentTasks.First().Quantity);
    }

    /// <summary>
    /// **撤销/回滚形态**：子件因本次载荷内无可用日历而排不下 ⇒ 父件**不得**按任何旧时间排下，
    /// 且结果里父件应被记为未排（有原因可追），而不是「静默成功」。
    /// </summary>
    [Fact]
    public async Task 子件排不下_父件不得沿用旧时间_应显式未排()
    {
        var result = await _solver.SolveAsync(Build(childCanSchedule: false));

        Assert.True(result.Success, result.ErrorMessage);

        Assert.Empty(result.FinalTasks.Where(t => t.SourceDraftId == "C"));
        Assert.Empty(result.FinalTasks.Where(t => t.SourceDraftId == "P"));

        var parentUnscheduled = result.UnscheduledTasks.FirstOrDefault(u => u.DraftId == "P");
        Assert.NotNull(parentUnscheduled);
        Assert.False(string.IsNullOrWhiteSpace(parentUnscheduled!.Reason),
            "父件未排必须有出口码，不得三无");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(bool childCanSchedule)
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
                StartStageCode = stage, NetOutputQty = 5m, PlannedProcessQty = 5m,
                RequiredAvailableTime = Day.AddDays(20)
            },
            new()
            {
                LogicalDemandKey = "C", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 2, DemandKey = "C", DemandSequence = 2,
                MaterialId = ChildMat, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = 5m, PlannedProcessQty = 5m,
                RequiredAvailableTime = Day.AddDays(20)
            }
        };

        var links = new List<MaterialRequirementLink>
        {
            new()
            {
                ConsumerLogicalDemandKey = "P", ProducerLogicalDemandKey = "C",
                ConsumerMaterialId = ParentMat, ProducerMaterialId = ChildMat,
                RequiredQty = 5m, ProducerAllocationSequence = 2
            }
        };

        var mats = new List<int> { ParentMat, ChildMat };

        // ⚠ `RoutingOperation.StandardDuration` 输入单位是**秒**（Phase1 `/60m` 归一）
        //   ⇒ 240 秒 = 4 分钟/件。
        var ops = mats.Select(m => new RoutingOperation
        {
            MaterialId = m, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
            OperationCode = op, StageCode = stage, StandardDuration = 240m, SetupTime = 0m
        }).ToList();

        var elig = mats.Select(m => new OperationResourceEligibility
        {
            MaterialId = m, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
            OperationCode = op, ResourceId = 25000 + m, Priority = 1, CapacityFactor = 1m
        }).ToList();

        var depts = mats.Select(m => new MaterialStageDepartmentContextDto
        {
            MaterialId = m, StageCode = stage, ProductionDepartmentId = Dept
        }).ToList();

        var resources = mats.Select(m => new ResourceDefinition
        {
            ResourceId = 25000 + m, ResourceCode = $"R{m}", FactoryCode = Factory, Capacity = 1m
        }).ToList();

        // 子件在「排不下」用例里**不给日历**：等价于它的执行批在本次求解内已不可用（被撤销）
        var slots = new List<ResourceCalendarSlot>
        {
            new() { ResourceId = 25000 + ParentMat, Start = Day, End = Day.AddDays(30), IsAvailable = true }
        };
        if (childCanSchedule)
        {
            slots.Add(new ResourceCalendarSlot
            {
                ResourceId = 25000 + ChildMat, Start = Day, End = Day.AddDays(30), IsAvailable = true
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = demands,
            MaterialRequirementLinks = links,
            RoutingOperations = ops,
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(30) }
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
