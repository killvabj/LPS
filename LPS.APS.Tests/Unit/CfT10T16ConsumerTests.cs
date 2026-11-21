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
/// **CF-T10 / CF-T16 的 Solver 消费侧**（09 验收包 v2.3:24-30）。
///
/// <list type="bullet">
///   <item><b>CF-T10</b>「真实同厂 SH 源=目标 BJ，绑定/状态/Domain 合法」⇒ 冻结要求
///         「**不一刀切 MASKED**；SH 同号自承接仍被禁」。我方验证：同厂链接照常建边、不被遮蔽。</item>
///   <item><b>CF-T16</b>「真实 SH 自身 OrderNo 与供给单号相同」⇒ 冻结要求
///         「**自承接校验拒绝**；无循环/伪 Allocation/重复消耗」。
///         我方验证：自环不被静默吞掉、不造成整域技术失败，只让**该需求局部未排**，其余照排。</item>
/// </list>
///
/// <para>纯内存，不碰数据库。⚠ 这两条的策略主体在 2号位（SH 识别/绑定/状态），
/// 本文件只证 **1号位 Solver 拿到这样的输入后不会做错事**。</para>
/// </summary>
public sealed class CfT10T16ConsumerTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;
    private const string Stage = "STAGE1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    // ── CF-T16：自承接（consumer == producer）必须被拒，且不得拖垮整域 ──
    [Fact(DisplayName = "CF-T16：自身承接自身 ⇒ 该需求局部未排，独立需求照排，无技术失败")]
    public async Task CfT16_SelfConsumption_LocalUnscheduledOnly()
    {
        var request = Build(
            new[] { ("SELF", 1, 1L, 10m), ("OK", 2, 2L, 10m) },
            // 自环：Consumer == Producer == SELF
            new[] { new LinkSpec("SELF", "SELF", 0m) });

        var result = await _solver.SolveAsync(request);

        // 环被识别为**局部**问题，不是整域技术失败（2026-11-14 用户裁决）。
        Assert.True(result.Success, "自承接不得升级为整域技术失败：" + result.ErrorMessage);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "SELF");
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "OK");
        Assert.NotEmpty(result.FinalTasks.Where(t => t.SourceDraftId == "OK"));
        // 自承接需求不得被静默排下（那是"伪 Allocation/重复消耗"）。
        Assert.Empty(result.FinalTasks.Where(t => t.SourceDraftId == "SELF"));
    }

    // ── CF-T10：同厂 SH 合法 ⇒ 不得一刀切遮蔽 ──
    [Fact(DisplayName = "CF-T10：同厂 SH（源=目标工厂）合法 ⇒ 建边并约束时序，不遮蔽")]
    public async Task CfT10_SameFactorySh_NotMasked()
    {
        // 源与目标都是 FactoryId=1（同厂 BJ 场景），Domain 与状态合法 ⇒ 我方应照常建边。
        var request = Build(
            new[] { ("SHSRC", 1, 1L, 100m), ("SHTGT", 2, 2L, 60m) },
            new[] { new LinkSpec("SHTGT", "SHSRC", 60m) });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        var src = result.FinalTasks.Single(t => t.SourceDraftId == "SHSRC");
        var tgt = result.FinalTasks.Single(t => t.SourceDraftId == "SHTGT");

        // 未被遮蔽 = 真的有 Task；且时序约束真的生效（不是当现货）。
        Assert.True(tgt.PlannedStartTime >= src.PlannedEndTime.AddMinutes(60),
            $"同厂 SH 目标 {tgt.PlannedStartTime:O} 必须 ≥ 源完成 {src.PlannedEndTime:O} + LT 60 分钟"
            + "（不得因同厂而一刀切 MASKED / 当现货）");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private sealed record LinkSpec(string Parent, string Child, decimal Lag);

    private static DomainSolveRequest Build(
        IReadOnlyList<(string Key, int MaterialId, long Alloc, decimal Qty)> demands,
        IReadOnlyList<LinkSpec> links)
    {
        var materials = demands.Select(d => d.MaterialId).Distinct().ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = PlanEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key,
                PlanVersionId = 1L,
                DomainKey = "DOMAIN",
                AllocationSequence = d.Alloc,
                DemandKey = d.Key,
                MaterialId = d.MaterialId,
                FactoryId = 1,                      // 同一工厂：CF-T10 的"同厂"前提
                StartStageCode = Stage,
                NetOutputQty = d.Qty,
                PlannedProcessQty = d.Qty,
                RequiredAvailableTime = PlanEnd,
                DemandSequence = (int)d.Alloc,
                QuantityAuthority = QuantityAuthorityValues.None,
            }).ToList(),
            RoutingOperations = materials.Select(m => new RoutingOperation
            {
                MaterialId = m, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                OperationCode = Op, StageCode = Stage, StandardDuration = 60m, SetupTime = 0m,
                OperationPlanningMode = "FINITE_RESOURCE",
            }).ToList(),
            OperationResourceEligibility = materials.Select(m => new OperationResourceEligibility
            {
                MaterialId = m, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                OperationCode = Op, ResourceId = m, Priority = 1, CapacityFactor = 1m,
            }).ToList(),
            MaterialStageDepartmentContexts = materials.Select(m => new MaterialStageDepartmentContextDto
            {
                MaterialId = m, StageCode = Stage, ProductionDepartmentId = DeptId,
            }).ToList(),
            Resources = materials.Select(m => new ResourceDefinition
            {
                ResourceId = m, ResourceCode = $"R{m}", FactoryCode = "F1", Capacity = 1m,
            }).ToList(),
            CalendarSlots = materials.Select(m => new ResourceCalendarSlot
            {
                ResourceId = m, Start = Day, End = PlanEnd, IsAvailable = true,
            }).ToList(),
            MaterialRequirementLinks = links.Select(l =>
            {
                var parent = demands.First(d => d.Key == l.Parent);
                var child = demands.First(d => d.Key == l.Child);
                return new MaterialRequirementLink
                {
                    ConsumerLogicalDemandKey = parent.Key,
                    ProducerLogicalDemandKey = child.Key,
                    ConsumerMaterialId = parent.MaterialId,
                    ProducerMaterialId = child.MaterialId,
                    RequiredQty = child.Qty,
                    ProducerAllocationSequence = child.Alloc,
                    LagMinutes = l.Lag,
                };
            }).ToList(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(materials, DeptId),
            },
        };
    }
}
