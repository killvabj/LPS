using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T06（0号位 2026-11-14 §七 清单）：`ImpactHours` 语义澄清与旧消费者兼容。**
///
/// <para>现状（改前）：`PhaseThreeDiagnostics.cs` 写的是 `ImpactHours = (decimal)delay.TotalHours`，
/// `delay = 末道 Task 完工 − 覆盖交期下界`，即**需求级整单延期小时**；它不是「某条原因造成的
/// 可归因影响小时」，多条解释事实的 `ImpactHours` **不能相加**。</para>
///
/// <para>本轮（不改物理列、不改旧 PV、等 0号位 裁定载体）：只在 `EvidenceJson.details` 内加
/// `impactType = ORDER_DELAY_NOT_ATTRIBUTABLE` 的可判标记。本文件锁三件事：
/// ① 标记存在；② 原两键 `RequiredTime/ActualTime` 仍在（旧消费者不受影响）；
/// ③ `ImpactHours` 仍逐字等于「完工 − 交期下界」的整单延期，不被偷换成别的东西。</para>
/// </summary>
public sealed class ExT06ImpactHoursCarrierTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);

    [Fact(DisplayName = "EX-T06：ImpactHours=整单延期小时，details 带 impactType 标记且旧两键仍在")]
    public async Task ExT06_ImpactHours_CarriesOrderDelayMarker()
    {
        var request = Build();
        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        var fact = result.ExplanationFacts.Single(f => f.ObjectType == "ORDER");

        Assert.NotNull(fact.ImpactHours);

        using var doc = JsonDocument.Parse(fact.EvidenceJson!);
        var details = doc.RootElement.GetProperty("details");

        // ③ 物理列 = 整单延期（完工 − 交期下界），不是别的口径。
        var bound = details.GetProperty("RequiredTime").GetDateTime();
        Assert.Equal((decimal)(task.PlannedEndTime - bound).TotalHours, fact.ImpactHours!.Value);

        // ① EX-T06 标记；② 旧两键仍在。
        Assert.Equal("ORDER_DELAY_NOT_ATTRIBUTABLE", details.GetProperty("impactType").GetString());
        Assert.Equal(task.PlannedEndTime.ToOADate(), details.GetProperty("ActualTime").GetDateTime().ToOADate());
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build()
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = "D1",
                    MaterialId = 1,
                    FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = 1m,
                    PlannedProcessQty = 1m,
                    // 交期已在计划期之前 ⇒ 必然延期（覆盖交期判定期上界会把 Day 整天算按期）。
                    RequiredAvailableTime = Day.AddDays(-1),
                    DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 60m, OperationPlanningMode = "FINITE_RESOURCE",
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m,
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
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, 100),
            },
        };
}
