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
/// **C07 回归**（0号位 P0「C07 不存在 Path 拒绝」）：需求声明了固定 `RouteCode/PathId`，
///   但该物料的图里**没有这条路径** ⇒ 必须 **Fail Closed**（不产 Task、进未排），
///   **不得**退回 `TryGetSingleRoutingGraph` 猜唯一 Path，**不得**跨 Path 找替代节点（Q-3 红线）。
///
/// <para><b>本用例在 2026-11-24 抓到过真缺陷</b>：Phase2 已正确 `P2_FIXED_PATH_MISSING`（不排），
/// 但 Phase4 局部修复经 `TryGetDemandRoutingGraph` 的**无条件单路径回退**把需求「救」了回来，
/// 最终仍产出 `RT/Path1` 的 Task —— 违反 Q-3。修复：需求声明固定路径但图缺失时，
/// `TryGetDemandRoutingGraph` / `TryGetBatchRoutingGraph` 一律返回 false，不再回退猜路径。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class C07FixedPathMissingTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 正例_声明的Path存在_应正常排下()
    {
        var result = await _solver.SolveAsync(Build(declaredPathId: 1));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal("RT", task.RouteCode);
        Assert.Equal(1L, task.PathId);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact]
    public async Task 反例_声明的Path不存在_必须未排_不得猜唯一Path回退()
    {
        // 图里只有 RT/Path1；需求却声明 PathId=99（不存在）。
        // 修复前：Phase4 局部修复靠单路径回退把它排成 RT/Path1（本用例会红）。
        // 修复后：Q-3 Fail Closed —— 不产 Task，且必须登记未排（不静默丢弃）。
        var req = Build(declaredPathId: 99);
        Assert.Equal(99, req.LogicalProductionDemands[0].PathId);   // 夹具自检

        var result = await _solver.SolveAsync(req);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    private static DomainSolveRequest Build(int declaredPathId)
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
                    StartStageCode = "STAGE1", StartOperationCode = null,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = "RT", PathId = declaredPathId
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = M, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", StageCode = "STAGE1", StandardDuration = 60m,
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = M, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "STAGE1", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = new List<StageLeadTimeFact>(),
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = M,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "STAGE1", StageSeq = 1 }
                    }
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
                        MaterialId = M, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
}
