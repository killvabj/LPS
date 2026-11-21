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
/// **EX-T08（0号位 v1.8 §六）**：「Stage 阈值 / 父件消费点 / 时间质量」。
///
/// <para>冻结依据（`LogicalProductionDemand.RequiredStageCode` 字段说明，源自 PM《BOM取用_Pegging_Stage_Routing
/// 完整链路说明》§八，2026-09-28）：</para>
/// <blockquote>
/// 「子件做到这个 Stage 以后，才成为当前父件可使用的 Supply。…
/// **本需求不必走完该物料全部 Stage，做到本阶段即为终点**。」
/// </blockquote>
///
/// <para>本文件验证 Solver 是否真的把 `RequiredStageCode` 当作**终点**裁剪工序链。
/// 若排到了该 Stage 之后，就是「越过供给阈值继续生产」——浪费产能，且与阈值语义冲突。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class ExT08SupplyThresholdTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "EX-T08：RequiredStageCode=STAGE2 ⇒ 工序只排到 STAGE2，不得越过供给阈值")]
    public async Task RequiredStage_StopsOperationChain()
    {
        var result = await _solver.SolveAsync(Build(requiredStageCode: "STAGE2"));

        Assert.True(result.Success, result.ErrorMessage);
        var stages = result.FinalTasks.Select(t => t.StageCode).Distinct().OrderBy(s => s).ToList();

        Assert.True(stages.All(s => s == "STAGE1" || s == "STAGE2"),
            "RequiredStageCode=STAGE2 时不得产出 STAGE3 的 Task（越过供给阈值）；实际产出阶段：" + string.Join(",", stages));
    }

    [Fact(DisplayName = "EX-T08 对照：RequiredStageCode 为空 ⇒ 走完整个工艺（保守口径）")]
    public async Task NoRequiredStage_SchedulesWholeChain()
    {
        var result = await _solver.SolveAsync(Build(requiredStageCode: null));

        Assert.True(result.Success, result.ErrorMessage);
        var stages = result.FinalTasks.Select(t => t.StageCode).Distinct().OrderBy(s => s).ToList();

        Assert.Equal(new[] { "STAGE1", "STAGE2", "STAGE3" }, stages);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(string? requiredStageCode)
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
                    StartStageCode = "STAGE1",
                    // 本用例的被测字段
                    RequiredStageCode = requiredStageCode,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            // 三段线性工艺：STAGE1 → STAGE2 → STAGE3
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", StageCode = "STAGE1", StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP20", StageCode = "STAGE2", StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP30", StageCode = "STAGE3", StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP20", ResourceId = 1, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = "OP30", ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = materialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
                new() { MaterialId = materialId, StageCode = "STAGE2", ProductionDepartmentId = DeptId },
                new() { MaterialId = materialId, StageCode = "STAGE3", ProductionDepartmentId = DeptId },
            },
            // 逐 Stage 计划加工量 + Stage 链：缺这两项时 Solver 只排起始 Stage（实测）。
            StageProcessQuantities = new List<DemandStageProcessQuantity>
            {
                new() { LogicalDemandKey = "D1", StageCode = "STAGE1", PlannedProcessQty = 10m },
                new() { LogicalDemandKey = "D1", StageCode = "STAGE2", PlannedProcessQty = 10m },
                new() { LogicalDemandKey = "D1", StageCode = "STAGE3", PlannedProcessQty = 10m },
            },
            // 工序依赖（缺它时 Routing 图内三条工序都成孤立根 ⇒ 只排起始 Stage）。
            RoutingDependencies = new List<LPS.APS.Core.Entities.APS.RoutingDependency>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT", PathId = 1,
                        FromOperationCode = "OP10", ToOperationCode = "OP20", DependencyType = "ES" },
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT", PathId = 1,
                        FromOperationCode = "OP20", ToOperationCode = "OP30", DependencyType = "ES" },
            },
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = materialId,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "STAGE1", StageSeq = 1, ProductionDepartmentId = DeptId },
                        new() { StageCode = "STAGE2", StageSeq = 2, ProductionDepartmentId = DeptId },
                        new() { StageCode = "STAGE3", StageSeq = 3, ProductionDepartmentId = DeptId },
                    },
                },
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
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(materialId, DeptId),
            },
        };
    }
}
