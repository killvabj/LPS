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
/// **0号位 v1.7 §4 P0-3 + v1.8 §1**：找槽对「占用表未命中」的处置。
///
/// <para><b>v1.7 §4 P0-3（r14456，不抛异常）</b>逐字：「已知合格且合法资源无占用初始化空集合**可以**；
/// 缺资源定义/资格/日历**不能默认可用**；局部诊断/合法未排，**不让一个坏资源无证打挂整轮**」。</para>
///
/// <para><b>v1.8 §1（r14469，本次收紧）</b>逐字：「仅确证合法资源且占用状态已完整装载的空集合可视空；
/// **资源未知 / 引用坏 / 占用未装载不能以空字典放行**」。</para>
///
/// <para>⇒ 两条合起来的判定表（`ConstraintContext.DefinedResourceIds` 非空时启用）：</para>
/// <list type="number">
///   <item>**已定义 + 占用表有键 + 表空** ⇒ 合法空集，正常排下；</item>
///   <item>**不在 `Resources` 里**（引用坏/资源未知）⇒ **不按空字典放行**，合法未排，且不抛异常；</item>
///   <item>**有资格、无日历** ⇒ 不得当成可用，合法未排，且不抛异常。</item>
/// </list>
///
/// <para>⚠ 第 2 条在 r14456 时断言的是**相反**行为（当时按「合法资源」排下）。
///   0号位 v1.8 §1 明确后已就地改判 —— 原用例名「按合法资源排下」改为「不得按空字典放行」。</para>
///
/// ⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。
/// </summary>
public class PhaseTwoOccupancyGuardTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int MaterialId = 9701;
    private const int Dept = 1801;
    private const int GhostResourceId = 1000020210;   // 故意**不**放进 Resources
    private const int RealResourceId = 9901;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>
    /// **v1.8 §1 反例**：资源不在 `Resources` 里（引用坏），即便有日历 ⇒ **不得**以空占用放行。
    ///   要求同时满足：① 不抛异常（v1.7 P0-3 不让坏资源打挂整轮）；② 不产出 Task（不放行）。
    /// </summary>
    [Fact]
    public async Task 资源有日历但不在Resources里_不得按空字典放行_也不得抛异常()
    {
        var result = await _solver.SolveAsync(Build(withCalendarForGhost: true));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
    }

    [Fact]
    public async Task 资源有资格无日历_不得当成可用_合法未排()
    {
        var result = await _solver.SolveAsync(Build(withCalendarForGhost: false));

        // 不抛异常即可；不得凭空把这些量排到没有日历的资源上
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
    }

    /// <summary>
    /// **v1.8 §1 正例**：资源**已定义**、占用表有键且为空（合法空集）⇒ 必须正常排下。
    ///   防「把合法空集也一起拒掉」的过度收紧。
    /// </summary>
    [Fact]
    public async Task 资源已定义_占用为空集属合法_正常排下()
    {
        var result = await _solver.SolveAsync(Build(withCalendarForGhost: false, useRealResourceEligibility: true));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(RealResourceId, task.ResourceId);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(bool withCalendarForGhost, bool useRealResourceEligibility = false)
    {
        const string stage = "STAGE1";
        const string op = "OP10";

        var slots = new List<ResourceCalendarSlot>
        {
            new() { ResourceId = RealResourceId, Start = Day, End = Day.AddDays(30), IsAvailable = true }
        };
        if (withCalendarForGhost)
        {
            slots.Add(new ResourceCalendarSlot
            {
                ResourceId = GhostResourceId, Start = Day, End = Day.AddDays(30), IsAvailable = true
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "D1", DemandSequence = 1,
                    MaterialId = MaterialId, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = stage, NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = op, StageCode = stage, StandardDuration = 3600m, SetupTime = 0m
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            // 默认资格只指向「幽灵资源」：它没有 ResourceDefinition，但有/没有日历由用例控制。
            //   `useRealResourceEligibility=true` ⇒ 改指向**已定义且已在 Resources 里**的真实资源（正例）。
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = op,
                    ResourceId = useRealResourceEligibility ? RealResourceId : GhostResourceId,
                    Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = stage, ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(30) }
            },
            // ⚠ Resources 只给真实资源：**不含**幽灵资源 —— 承载 v1.8 §1 的「引用坏」判定
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = RealResourceId, ResourceCode = "R9901", FactoryCode = Factory, Capacity = 1m }
            },
            CalendarSlots = slots,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = true, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
