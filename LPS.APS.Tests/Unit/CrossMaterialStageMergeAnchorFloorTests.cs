using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 跨物料时序 × Stage 合批重排（2026-11-16，FAMILY_CYL 整域归零真因）。
///
/// 【现场】PV999507 父件 999507_39077（CN_ASSY，锚点）首道 M102 落 09:32:20，
///   早于其子件 999507_39091（BJ_MACH）完工 09:33:59 ⇒ Phase5 `TaskDependency` 越序、整域 FinalTask=0。
///   trace 已坐实：`TryMergeDemandIntoStageBatch` 把兄弟需求合进锚点批时，重排用的 floor
///   是**被并入需求自己带进来的更早下界**，锚点自己的跨物料下界被丢弃。
///
/// 【本测试的判别点】锚点子件完工（下界）晚于被并入需求的下界时，合批重排必须取
///   `max(锚点下界, 被并入需求下界)`，锚点首道不得被拉到其子件完工之前。
///   整改前：重排只用被并入需求的下界 ⇒ 首道开工早于锚点下界 ⇒ 本条**红**。
///   纯内存、直调内部方法 <c>TryMergeDemandIntoStageBatch</c>，不碰数据库。
/// </summary>
public sealed class CrossMaterialStageMergeAnchorFloorTests
{
    private static PhaseTwoInitialScheduler Scheduler => new PhaseTwoInitialScheduler();

    private static readonly DateTime T0 = new DateTime(2026, 11, 10, 8, 0, 0);
    private const int ResourceId = 1;
    private const int MaterialId = 100;
    private const string Stage = "S1";
    private static readonly string[] StageOps = { "OP10", "OP20", "OP30" };

    /// <summary>锚点子件完工 = 很晚（T0+6h）。</summary>
    private static readonly DateTime AnchorFloor = T0.AddHours(6);

    /// <summary>被并入需求自己带进来的下界 = 很早（T0，几乎无约束）。</summary>
    private static readonly DateTime MergingFloor = T0;

    [Fact(DisplayName = "跨物料Stage合批：锚点子件完工晚于被并入需求 ⇒ 首道不得早于锚点下界")]
    public void StageMerge_RespectsAnchorFloor_WhenAnchorChildCompletesLater()
    {
        var constraints = ThreeOpConstraints();
        // 模拟锚点 A 有跨物料子件、且子件完工比被并入需求 C 更晚。
        constraints.DemandMaterialFloor["A"] = AnchorFloor;

        var targetBatch = AnchorStageBatch();
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
            ["B"] = Demand("B", 20m),
        };

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), MergingFloor, new StageOverlapParams(), null,
            out var combinedTasks);

        // 夹具自检：跨物料下界确实晚于被并入需求的下界，否则断言没有判别力。
        Assert.True(AnchorFloor > MergingFloor, "夹具前提失效：锚点下界必须晚于被并入需求的下界");
        Assert.NotNull(merged);
        Assert.Equal(3, combinedTasks.Count);

        // 主断言（整改前红）：合批首道开工必须 >= 锚点下界，而不是被并入需求的更早下界。
        var firstStart = combinedTasks.Min(t => t.PlannedStartTime);
        Assert.True(firstStart >= AnchorFloor,
            $"合批首道开工 {firstStart:O} 必须 >= 锚点跨物料下界 {AnchorFloor:O}"
            + "（合批重排不得用被并入需求的更早 floor 把锚点首道拉到其子件完工之前）");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static LogicalProductionDemand Demand(string key, decimal qty)
        => new LogicalProductionDemand
        {
            LogicalDemandKey = key,
            DemandKey = key,
            MaterialId = MaterialId,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = Stage,
            RouteCode = "DEFAULT",
            PathId = 1,
            RequiredAvailableTime = DateTime.MaxValue,
        };

    private static List<OperationNode> ThreeOps()
        => StageOps.Select(op => new OperationNode
        {
            OperationCode = op,
            StageCode = Stage,
            StandardDuration = 1m,          // 分钟/件
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
}