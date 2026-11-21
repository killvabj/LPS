using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **C13（v1.8 §二 / v1.3 §2.1）**：A/B 续作的**真实载体形态**——一个 MES 工单一段剩余，
/// 由 `StartOperationCode + NetOutputQty` 表达（见
/// `项目文档/1号位的意见/…C13输入合同盘点…v1.0_20261120.md` §三：真实快照 5,833 条 A/B，
/// ContinuationKey 一 Key 一需求，5,085 条带 StartOperationCode，ExecutionConstraints 为空）。
///
/// v1.3 §2.1-3 逐字：「已完工且**不需未来 Task 的 OP 不得被当作缺 OP**」。
/// 本用例钉住这一点：A/B 需求从 OP20 起（Net=3）⇒ 只排 OP20；不得因图上还有 OP10 而报缺工序。
///
/// ⚠ 不作 C13 的假载体：此前「一条需求 + 两条锁定 5/3」的构造在真实合同里不存在
/// （逐 OP 剩余没有进场），已撤下，不冒充 C13；按 0号位 **v1.3 §2.1 第 3 条**（「不等任何号位」）应**自行构造** Task 级破坏性夹具；此前以"真实合同无逐 OP 载体"撤下属误判。
///
/// 纯内存、零 DB。
/// </summary>
public sealed class C13PerOperationRemainderTests
{
    private static readonly DateTime PlanningStart = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new(2026, 9, 30, 0, 0, 0);

    private const int MaterialId = 1;
    private const int DeptId = 100;

    private readonly FiniteCapacitySolver _solver = new();

    private sealed record Op(string Stage, string Code, int Resource, decimal LeadMinutes);

    private static DomainSolveRequest BuildRequest(
        IReadOnlyList<Op> ops,
        decimal demandNet,
        string startStage,
        string startOp)
    {
        var routingOps = ops.Select(op => new RoutingOperation
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "RTA",
            PathId = 1,
            OperationCode = op.Code,
            StageCode = op.Stage,
            StandardDuration = op.LeadMinutes * 60m,
            OperationPlanningMode = "FINITE_RESOURCE"
        }).ToList();

        var elig = ops.Select(op => new OperationResourceEligibility
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "RTA",
            PathId = 1,
            OperationCode = op.Code,
            ResourceId = op.Resource,
            Priority = 1,
            CapacityFactor = 1m
        }).ToList();

        var resources = ops.Select(op => op.Resource).Distinct()
            .Select(id => new ResourceDefinition
            {
                ResourceId = id,
                ResourceCode = $"R{id}",
                FactoryCode = "F1",
                Capacity = 1m
            }).ToList();

        var calendars = ops.Select(op => op.Resource).Distinct()
            .Select(id => new ResourceCalendarSlot
            {
                ResourceId = id,
                Start = PlanningStart,
                End = PlanningEnd,
                IsAvailable = true
            }).ToList();

        var stageDepts = ops.Select(op => op.Stage).Distinct()
            .Select(stage => new MaterialStageDepartmentContextDto
            {
                MaterialId = MaterialId,
                StageCode = stage,
                ProductionDepartmentId = DeptId
            }).ToList();

        var deps = new List<RoutingDependency>
        {
            new()
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = "RTA",
                PathId = 1,
                FromOperationCode = "OP10",
                ToOperationCode = "OP20",
                DependencyType = "ES",
                LagTime = 0m,
                IsActive = true
            }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = "D1",
                    MaterialId = MaterialId,
                    FactoryId = 1,
                    NetOutputQty = demandNet,
                    PlannedProcessQty = demandNet,
                    RequiredAvailableTime = PlanningStart.AddDays(20),
                    DemandSequence = 1,
                    RouteCode = "RTA",
                    PathId = 1,
                    // 真实载体：本段剩余从 OP20 起（上游 OP10 已完工，不需未来 Task）
                    StartStageCode = startStage,
                    StartOperationCode = startOp,
                    IsContinuation = true,
                    NoSplitMerge = true,
                    ContinuationKey = "CK-UT-1"
                }
            },
            RoutingOperations = routingOps,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            // 真实快照里 ExecutionConstraints 为空（见盘点 §三）⇒ 本用例同样不投射锁定事实
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = resources,
            CalendarSlots = calendars,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId, allowMerge: false)
            }
        };
    }

    /// <summary>
    /// **C13 正例（真实形态）**：A/B 续作从 OP20 起、剩 3 ⇒ 只排 OP20 的 3 件；
    /// 上游 OP10 已完工，**不得**被报成「缺 OP」；域成功、无未排、不产 OP10 的新 Task。
    /// </summary>
    [Fact]
    public async Task C13_AB续作_从OP20起剩3_只排剩余工序_上游已完工不报缺OP()
    {
        var ops = new[]
        {
            new Op("STAGE1", "OP10", 1, 30m),
            new Op("STAGE1", "OP20", 1, 30m),
        };

        var result = await _solver.SolveAsync(
            BuildRequest(ops, demandNet: 3m, startStage: "STAGE1", startOp: "OP20"));

        Assert.True(result.Success, result.ErrorMessage);

        var tasks = result.FinalTasks.Where(t => t.SourceDraftId == "D1").ToList();
        var op20 = Assert.Single(tasks);
        Assert.Equal("OP20", op20.OperationCode);
        Assert.Equal(3m, op20.Quantity);

        Assert.DoesNotContain("D1", result.UnscheduledTasks.Select(u => u.DraftId));
        // 反向：不得为已完工的 OP10 造新 Task
        Assert.DoesNotContain(tasks, t => t.OperationCode == "OP10");
    }
}