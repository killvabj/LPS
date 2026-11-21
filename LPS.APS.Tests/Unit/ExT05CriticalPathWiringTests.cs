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
/// **EX-T05 接线**（2号位 r14344 已在 `FinalTaskDraft` 加 `IsCriticalPath`）：
/// 验证 Solver 真的把值填出来了，且**语义正确**。
///
/// <para>语义（我方定死、2号位 写进注释）：`false` = **算过且不在关键链上**，不是「未算」。</para>
///
/// <para>夹具：两条互不争资源的链 —— D1 短（10 分钟）、D2 长（100 分钟），
/// 项目终点由 D2 决定 ⇒ **D2 的 Task 全关键、D1 的全不关键**。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class ExT05CriticalPathWiringTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime Start = Day.AddHours(8);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "EX-T05接线：长链关键、短链不关键（false=算过且不关键）")]
    public async Task LongChainCritical_ShortChainNotCritical()
    {
        var result = await _solver.SolveAsync(Build());

        Assert.True(result.Success, result.ErrorMessage);

        var d1 = result.FinalTasks.Where(t => t.SourceDraftId == "D1").ToList();
        var d2 = result.FinalTasks.Where(t => t.SourceDraftId == "D2").ToList();

        Assert.NotEmpty(d1);
        Assert.NotEmpty(d2);

        // 长链决定项目终点 ⇒ 全部关键
        Assert.All(d2, t => Assert.True(t.IsCriticalPath,
            $"D2 的 Task {t.FinalDraftId} 决定了项目终点，却未被判为关键"));

        // 短链提前完工 ⇒ 有 slack ⇒ 全部不关键
        Assert.All(d1, t => Assert.False(t.IsCriticalPath,
            $"D1 的 Task {t.FinalDraftId} 提前完工，却被判为关键"));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build()
    {
        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Start,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Demand("D1", 1, materialId: 1, qty: 10m),    // 1 分钟/件 ⇒ 10 分钟
                Demand("D2", 2, materialId: 2, qty: 100m),   // 100 分钟 ⇒ 决定终点
            },
            RoutingOperations = new List<RoutingOperation>
            {
                Op_(1), Op_(2),
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                Eligibility(1, 1),   // D1 用资源 1
                Eligibility(2, 2),   // D2 用资源 2（互不争）
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = Stage, ProductionDepartmentId = DeptId },
                new() { MaterialId = 2, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
                new() { ResourceId = 2, ResourceCode = "R2", FactoryCode = "F1", Capacity = 1m },
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(30), IsAvailable = true },
                new() { ResourceId = 2, Start = Day, End = Day.AddDays(30), IsAvailable = true },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, DeptId)
                    .Concat(TestBatchPolicy.Permissive(2, DeptId))
                    .ToList(),
            },
        };
    }

    private static LogicalProductionDemand Demand(string key, int seq, int materialId, decimal qty) => new()
    {
        LogicalDemandKey = key, PlanVersionId = 1L, DomainKey = "DOMAIN",
        AllocationSequence = seq, DemandKey = key, MaterialId = materialId, FactoryId = 1,
        StartStageCode = Stage, NetOutputQty = qty, PlannedProcessQty = qty,
        RequiredAvailableTime = Day.AddDays(30), DemandSequence = seq,
        QuantityAuthority = QuantityAuthorityValues.None,
    };

    private static RoutingOperation Op_(int materialId) => new()
    {
        MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
        OperationPlanningMode = "FINITE_RESOURCE",
    };

    private static OperationResourceEligibility Eligibility(int materialId, int resourceId) => new()
    {
        MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
        OperationCode = Op, ResourceId = resourceId, Priority = 1, CapacityFactor = 1m,
    };
}
