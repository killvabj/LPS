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
/// **EX-T07（0号位 v1.8 §六）**：R1 工艺链 NeedBy 参考 ——
/// 要求「**仅改采购 ETA、工艺独立目标时间不变**的可复验例」。
///
/// <para>本文件用 A/B 对照：同一夹具、**只改一条需求的物料 ETA**，
/// 断言①该需求开工随之推后（ETA 真被消费）；②另一条**独立需求**的开工**纹丝不动**
/// （采购时间变化不得串改工艺目标时间）。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class ExT07ProcurementEtaIsolationTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "EX-T07：采购 ETA 推后 ⇒ 该需求开工推后，独立需求开工不变")]
    public async Task ProcurementEtaShift_OnlyAffectsOwnDemand()
    {
        var etaA = Day.AddDays(1);
        var etaA2 = Day.AddDays(10);

        var before = await _solver.SolveAsync(Build(etaA));
        var after = await _solver.SolveAsync(Build(etaA2));

        Assert.True(before.Success, before.ErrorMessage);
        Assert.True(after.Success, after.ErrorMessage);

        var aBefore = before.FinalTasks.Single(t => t.SourceDraftId == "A");
        var aAfter = after.FinalTasks.Single(t => t.SourceDraftId == "A");
        var bBefore = before.FinalTasks.Single(t => t.SourceDraftId == "B");
        var bAfter = after.FinalTasks.Single(t => t.SourceDraftId == "B");

        // ① ETA 被真实消费：A 开工随 ETA 推后。
        Assert.Equal(etaA, aBefore.PlannedStartTime);
        Assert.True(aAfter.PlannedStartTime >= etaA2,
            $"A 的 ETA 推到 {etaA2:O} 后，开工 {aAfter.PlannedStartTime:O} 不得早于它");

        // ② 独立需求 B 不受影响：开工逐位相同。
        Assert.Equal(bBefore.PlannedStartTime, bAfter.PlannedStartTime);
        Assert.Equal(bBefore.PlannedEndTime, bAfter.PlannedEndTime);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(DateTime etaA)
    {
        const int matA = 1;
        const int matB = 2;

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
                    LogicalDemandKey = "A", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "A", MaterialId = matA, FactoryId = 1,
                    StartStageCode = "S1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
                new()
                {
                    LogicalDemandKey = "B", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 2, DemandKey = "B", MaterialId = matB, FactoryId = 1,
                    StartStageCode = "S1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 2,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = matA, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", StageCode = "S1", StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
                new() { MaterialId = matB, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", StageCode = "S1", StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = matA, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = matB, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", ResourceId = 2, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = matA, StageCode = "S1", ProductionDepartmentId = DeptId },
                new() { MaterialId = matB, StageCode = "S1", ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
                new() { ResourceId = 2, ResourceCode = "R2", FactoryCode = "F1", Capacity = 1m },
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = PlanEnd, IsAvailable = true },
                new() { ResourceId = 2, Start = Day, End = PlanEnd, IsAvailable = true },
            },
            // **本用例唯一变量**：A 的采购 ETA（B 无物料约束）
            MaterialConstraints = new List<MaterialAvailabilitySlice>
            {
                new()
                {
                    AllocationSequence = 1, MaterialId = matA, FactoryId = 1,
                    Quantity = 10m, AvailableTime = etaA, TimeBasis = "KNOWN",
                },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(new[] { matA, matB }, DeptId),
            },
        };
    }
}
