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
/// **W2 八反例之①（0号位 11-07 任务包 P1-E §154 逐字）**：
/// 「**100 件经济 Max30 但物理 100 合法，整批必须仍是候选**」。
///
/// <para>依据：0号位《联合代码审核报告 v1.0》§4 **S1-01（P0 CONFIRMED）**：
/// 「经济 Max 仍参与 PI 批量硬数量校验……**在硬 PI 闸和合批合法性闸排除经济 Max**；
/// 软 Max 仅进入有界候选评分。」</para>
///
/// <para>本文件锁三件事：</para>
/// <list type="number">
///   <item>`MaxExecutionBatchQty=30` **不得**把 100 件需求硬拒 —— 整批照样排下；</item>
///   <item>也不得截断成 30（截断=把软偏好当硬上限）；</item>
///   <item>软 Max 不得制造未排量 —— U 必须为 0。</item>
/// </list>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W2CounterexampleSoftMaxTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "W2①：经济 Max=30 但物理可排 100 ⇒ 整批 100 必须排下（软 Max 不作硬拒）")]
    public async Task EconomicMax30_PhysicalCanRun100_WholeBatchStillCandidate()
    {
        var request = Build(qty: 100m, economicMax: 30m, allowSplit: false);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        var tasks = result.FinalTasks.ToList();
        Assert.NotEmpty(tasks);

        // ① 不得因经济 Max=30 把整批硬拒：总数必须仍是 100。
        var total = tasks.Sum(t => t.Quantity);
        Assert.Equal(100m, total);

        // ② 也不得截断成 30（截断=把软偏好当硬上限）。
        Assert.DoesNotContain(tasks, t => t.Quantity == 30m);

        // ③ 未排量必须为 0（软 Max 不得制造未排）。
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact(DisplayName = "W2①对照：经济 Max=30 且 AllowSplit=true ⇒ 仍须排满 100（可拆不可缺）")]
    public async Task EconomicMax30_WithSplit_StillProducesFullQuantity()
    {
        var request = Build(qty: 100m, economicMax: 30m, allowSplit: true);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        var total = result.FinalTasks.Sum(t => t.Quantity);
        Assert.Equal(100m, total);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(decimal qty, decimal economicMax, bool allowSplit)
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
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = PlanEnd, IsAvailable = true },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = allowSplit,
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = materialId,
                        ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null,
                        MaxExecutionBatchQty = economicMax,   // 经济偏好（软）
                        AllowSplit = allowSplit,
                        AllowMerge = false,
                    },
                },
            },
        };
    }
}
