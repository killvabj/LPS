using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **Phase4 供给不足不再静默**（0号位 2026-10-26 四份整改单 P0 ③
/// 「Phase4／5 供给不足补原因码（`P4:1139`、`P5:907`、`P5:1254`）」）。
///
/// 断言的正是改动的目的：
///   · 缺料时 Phase4 `TryResourceSwitch` 在物料下界处返回空表，旧实现**不留一个字** ⇒
///     结果与「无可用时间槽」「无合格资源」完全同形，而三者该找的人不同（采购／产能日历／工艺主数据）；
///   · 现在必须留下 `PHASE4_MATERIAL_INSUFFICIENT`；
///   · **供给充足时不得误报** —— 否则下游会把正常单当成缺料单。
/// </summary>
public class MaterialInsufficientTraceNoteTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 供给不足_不静默_留下PHASE4_MATERIAL_INSUFFICIENT()
    {
        // 需求 10 件，物料只到 1 件 ⇒ 累计量恒不足
        var result = await _solver.SolveAsync(Build(materialQuantity: 1m, netQty: 10m));

        Assert.Empty(result.FinalTasks);   // 缺料 ⇒ 一个 Task 也排不出（业务未排，不是技术失败）
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_MATERIAL_INSUFFICIENT");
    }

    [Fact]
    public async Task 供给充足_不得误报缺料原因码()
    {
        var result = await _solver.SolveAsync(Build(materialQuantity: 10m, netQty: 10m));

        Assert.Single(result.FinalTasks);
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_MATERIAL_INSUFFICIENT");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(decimal materialQuantity, decimal netQty)
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = 1, FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = netQty, PlannedProcessQty = netQty,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 60m,                    // 源事实：秒 = 1 分钟/件
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(29), IsAvailable = true }
            },
            // 物料多段可用性：单段，数量由参数控制（`PhaseOneConstraintBuilder` 按 AllocationSequence 聚合
            // 后交给 `GetMaterialEarliestTime` 做「累计够 / 不够」判定）。
            MaterialConstraints = new List<MaterialAvailabilitySlice>
            {
                new()
                {
                    AllocationSequence = 1, MaterialId = 1, FactoryId = 1,
                    Quantity = materialQuantity, AvailableTime = Day
                }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, 100)
            }
        };
}
