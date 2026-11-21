using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **W2 八反例之 ⑥⑦**（0号位 11-07 任务包 P1-E §154）：
/// <list type="bullet">
///   <item>**⑦**「全候选硬不可行，**业务未排数量闭合**」；</item>
///   <item>**⑥**「合法独立基线存在、**预算耗尽不得返回假技术失败**」。</item>
/// </list>
///
/// <para>两条的反面都是"把软/预算问题伪装成技术失败"——一旦伪装，下游会把可救的单当成不可救。
/// 本文件锁：`Success=true`（业务未排不是技术失败）+ 未排量 **U = 净量**（数量闭合，不丢数）。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W2CounterexampleInfeasibleAndBudgetTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    // ── ⑦ 全候选硬不可行：日历完全不可用 ⇒ 业务未排、U 闭合、非技术失败 ──
    [Fact(DisplayName = "W2⑦：日历零可用 ⇒ Success=true、U=净量、无伪 Task")]
    public async Task AllCandidatesHardInfeasible_BusinessUnscheduledQuantityClosed()
    {
        var request = Build(qty: 10m, calendarUsable: false);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, "全候选不可行是业务未排，不得升级为技术失败：" + result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        var u = result.UnscheduledTasks.Where(x => x.DraftId == "D1").Sum(x => x.UnscheduledQty);
        Assert.Equal(10m, u);   // 数量闭合：应排 10 / 已排 0 / 未排 10
    }

    // ── ⑥ 预算耗尽：**端到端不可测**（预算按设计不由输入注入）──
    //
    // 0号位 要求「合法独立基线存在、预算耗尽不得返回假技术失败」。
    // 实测约束：`SolverBatchBudget.MaxOptimizationSplitCount` / `MaxBatchCandidates`
    // 在 `FrozenStrategySnapshot` 里都标注「**1号位 Solver 技术预算，恒 null 不投**」，
    // 也不在 `FiniteCapacityParameters` / `SolverStrategyBlock` 上 ⇒ **无法从 DomainSolveRequest 注入**。
    //   ⇒ 端到端「把预算压到极小再跑」这条路在本设计下走不通；
    //     现值语义由 `SolverBatchBudgetSemanticsTests`（0/1/3/极大/负数 五档）直接钉住。
    //   ⇒ 本文件**不伪造**该用例，如实登记为「缺端到端注入通道」。

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(decimal qty, bool calendarUsable)
    {
        const int materialId = 1;

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = PlanEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = materialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = qty, PlannedProcessQty = qty,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = materialId, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            },
            // 日历可用性即"全候选是否硬不可行"的开关
            CalendarSlots = calendarUsable
                ? new List<ResourceCalendarSlot>
                {
                    new() { ResourceId = 1, Start = Day, End = PlanEnd, IsAvailable = true },
                }
                : new List<ResourceCalendarSlot>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(materialId, DeptId),
            },
        };
    }
}
