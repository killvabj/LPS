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
/// **CF-T03（09 验收包 v2.3:16）的 Solver 消费侧**：
/// 「源工厂 SH100 拆 40/60 批，合法 LT 已知」⇒ 冻结要求
/// 「**最后必要批完工 + LT 一次才可用**；目标不可先排/拆成两笔到货」。
///
/// <para>我方验证两件事：</para>
/// <list type="number">
///   <item>源被拆成多批时，目标的下界必须取**最后完工的那一批**（`max`），不是第一批；</item>
///   <item>该 LT **只加一次**（不随批数倍乘），且目标不得早于 `最后必要批完工 + LT`。</item>
/// </list>
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class CfT03LastNecessaryBatchTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;
    private const string Stage = "STAGE1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "CF-T03：源 100 拆 40/60 两批，目标 ≥ 最后一批完工 + LT（LT 只加一次）")]
    public async Task CfT03_TargetWaitsForLastSourceBatchPlusSingleLt()
    {
        var lt = 120m;
        var request = Build(
            srcQty: 100m,
            srcBatchMax: 60m,          // 允许拆批且单批上限 60 ⇒ 应拆成多批
            tgtQty: 60m,
            lagMinutes: lt);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var srcTasks = result.FinalTasks.Where(t => t.SourceDraftId == "SHSRC").ToList();
        var tgtTask = result.FinalTasks.Single(t => t.SourceDraftId == "SHTGT");

        // 前提：源确实被拆成 ≥2 批（否则本用例没测到"最后必要批"）
        Assert.True(srcTasks.Count >= 2,
            $"源 100 在单批上限 60 下应被拆成多批，实际 {srcTasks.Count} 批");

        var lastSrcEnd = srcTasks.Max(t => t.PlannedEndTime);

        // ① 目标不得早于**最后**一批完工 + LT（不是第一批）。
        Assert.True(tgtTask.PlannedStartTime >= lastSrcEnd.AddMinutes((double)lt),
            $"目标 {tgtTask.PlannedStartTime:O} 必须 ≥ 最后一批完工 {lastSrcEnd:O} + LT {lt} 分钟；"
            + "若只等第一批，就是「拆成两笔到货」的错误口径");

        // ② LT 只加一次：目标开工与「最后批完工 + LT」的差不得再叠一个 LT 量级。
        Assert.True(tgtTask.PlannedStartTime < lastSrcEnd.AddMinutes((double)lt * 2),
            $"目标 {tgtTask.PlannedStartTime:O} 与最后批完工 {lastSrcEnd:O} 之间不得叠两次 LT（{lt * 2} 分钟）");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(
        decimal srcQty, decimal srcBatchMax, decimal tgtQty, decimal lagMinutes)
    {
        const int srcMat = 1;
        const int tgtMat = 2;

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
                    LogicalDemandKey = "SHSRC", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "SHSRC", MaterialId = srcMat, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = srcQty, PlannedProcessQty = srcQty,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
                new()
                {
                    LogicalDemandKey = "SHTGT", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 2, DemandKey = "SHTGT", MaterialId = tgtMat, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = tgtQty, PlannedProcessQty = tgtQty,
                    RequiredAvailableTime = PlanEnd, DemandSequence = 2,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = srcMat, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
                new() { MaterialId = tgtMat, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = srcMat, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = tgtMat, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 2, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = srcMat, StageCode = Stage, ProductionDepartmentId = DeptId },
                new() { MaterialId = tgtMat, StageCode = Stage, ProductionDepartmentId = DeptId },
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
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                new()
                {
                    ConsumerLogicalDemandKey = "SHTGT",
                    ProducerLogicalDemandKey = "SHSRC",
                    ConsumerMaterialId = tgtMat, ProducerMaterialId = srcMat,
                    RequiredQty = tgtQty, ProducerAllocationSequence = 1,
                    LagMinutes = lagMinutes,
                },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = true,          // CF-T03 前提：源允许拆批
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = srcMat, ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null,
                        MaxExecutionBatchQty = srcBatchMax,
                        AllowSplit = true, AllowMerge = false,
                    },
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = tgtMat, ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null,
                        MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = false,
                    },
                },
            },
        };
    }
}
