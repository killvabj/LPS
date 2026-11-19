using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **多工序 Stage 合批 × 静态物料可用时间**（R06 破坏性测试，2026-11-18）。
///
/// <para>背景（FAMILY_CYL / PV999549 整域归零真因）：<c>TryMergeDemandIntoStageBatch</c> 把新需求合进锚点批时，
/// 只按锚批身份排时间，**被合并需求自己的静态物料（MaterialConstraints 分段 AvailableTime）没再查**。
/// 于是「料晚到」的需求被合批提前排到料未到之前，Phase5 兜底整域清空。</para>
///
/// <para>本测试的判别点：被合并需求的静态物料最晚可用时刻晚于锚批起点时，「允许合批」分支必须被判失败（不许提前消耗料）；
/// 物料如期可用时合批应照常成功（不误杀）。</para>
///
/// <para>纯内存、直调内部方法 <see cref="PhaseTwoInitialScheduler.TryMergeDemandIntoStageBatch"/>，不碰数据库。</para>
/// </summary>
public sealed class StageMergeStaticMaterialFloorTests
{
    private static PhaseTwoInitialScheduler Scheduler => new PhaseTwoInitialScheduler();

    private static readonly DateTime T0 = new DateTime(2026, 11, 10, 8, 0, 0);
    private const int ResourceId = 1;
    private const int MaterialId = 100;
    private const string Stage = "S1";
    private static readonly string[] StageOps = { "OP10", "OP20", "OP30" };

    private static LogicalProductionDemand Demand(string key, decimal qty, long alloc = 1)
        => new LogicalProductionDemand
        {
            LogicalDemandKey = key,
            DemandKey = key,
            AllocationSequence = alloc,
            MaterialId = MaterialId,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = Stage,
            RouteCode = "DEFAULT",
            PathId = 1,
            RequiredAvailableTime = DateTime.MaxValue,
        };

    /// <summary>被合并需求 C 的静态物料：一段、数量恰 = 需求、但到货在 <paramref name="availableAt"/>。</summary>
    private static void WithMaterial(ConstraintContext c, long alloc, decimal qty, DateTime availableAt)
        => c.MaterialAvailability[alloc] = new List<MaterialAvailabilitySegment>
        {
            new MaterialAvailabilitySegment { Quantity = qty, AvailableTime = availableAt },
        };

    [Fact(DisplayName = "R06 多工序合批：被合并需求静态物料晚到 ⇒ 拒绝合批（不提前消耗料）")]
    public void StageMerge_Rejects_WhenMergedDemandStaticMaterialArrivesLater()
    {
        var constraints = ThreeOpConstraints();
        WithMaterial(constraints, 3L, 10m, T0.AddDays(10));   // C 物料 10 天才到

        var targetBatch = AnchorStageBatch();
        var occupancy = Occupancy(targetBatch);
        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = Shares(targetBatch);
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
            ["B"] = Demand("B", 20m),
        };

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m, alloc: 3), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        // 夹具自检：C 的物料确实晚于锚批起点，否则断言无判别力。
        Assert.True(T0.AddDays(10) > targetBatch[0].PlannedStartTime,
            "夹具前提失效：被合并需求 C 的物料必须晚于锚批起点");

        // 主断言（整改前红）：物料晚到的 C 不得被合批提前排。
        Assert.Null(merged);
    }

    [Fact(DisplayName = "R06 多工序合批：被合并需求静态物料如期可用 ⇒ 照常合批（不误杀）")]
    public void StageMerge_Allows_WhenMergedDemandStaticMaterialAvailableOnTime()
    {
        var constraints = ThreeOpConstraints();
        WithMaterial(constraints, 3L, 10m, T0);   // C 物料即到

        var targetBatch = AnchorStageBatch();
        var occupancy = Occupancy(targetBatch);
        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = Shares(targetBatch);
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
            ["B"] = Demand("B", 20m),
        };

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m, alloc: 3), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        Assert.NotNull(merged);
        Assert.Equal(3, combinedTasks.Count);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static List<OperationNode> ThreeOps()
        => StageOps.Select(op => new OperationNode
        {
            OperationCode = op,
            StageCode = Stage,
            StandardDuration = 1m,
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        }).ToList();

    private static RoutingGraph ThreeOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in ThreeOps()) g.Operations[OperationNodeKey.Of(Stage, op.OperationCode)] = op;
        g.Dependencies[OperationNodeKey.Of(Stage, "OP20")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP10"), To = OperationNodeKey.Of(Stage, "OP20") },
        };
        g.Dependencies[OperationNodeKey.Of(Stage, "OP30")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP20"), To = OperationNodeKey.Of(Stage, "OP30") },
        };
        g.RootOperations.Add(OperationNodeKey.Of(Stage, "OP10"));
        return g;
    }

    private static ConstraintContext ThreeOpConstraints()
    {
        var c = new ConstraintContext();
        foreach (var op in StageOps)
        {
            var key = new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, op);
            c.OperationResourceEligibility[key] = new List<int> { ResourceId };
            c.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [ResourceId] = 1m };
        }
        c.ResourceCalendars[ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(48)) };
        return c;
    }

    private static List<FinalTaskDraft> AnchorStageBatch()
    {
        var list = new List<FinalTaskDraft>();
        var cursor = T0;
        foreach (var op in StageOps)
        {
            list.Add(new FinalTaskDraft
            {
                FinalDraftId = "T-A-" + op,
                SourceDraftId = "A",
                MaterialId = MaterialId,
                ResourceId = ResourceId,
                StageCode = Stage,
                OperationCode = op,
                PlannedStartTime = cursor,
                PlannedEndTime = cursor.AddMinutes(50),
                SetupTime = 0m,
                Quantity = 50m,
                PlannedProcessQty = 50m,
                ExecutionBatchDraftKey = "EB|A|001",
            });
            cursor = cursor.AddMinutes(50);
        }
        return list;
    }

    private static Dictionary<int, List<TimeWindow>> Occupancy(List<FinalTaskDraft> targetBatch)
        => new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };

    private static Dictionary<string, List<(string DemandKey, decimal ShareQty)>> Shares(List<FinalTaskDraft> targetBatch)
    {
        var shares = new Dictionary<string, List<(string, decimal)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }
        return shares;
    }
}