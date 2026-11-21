using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **Phase5 末端构成判定 —— 两处"静默丢量"缺陷的回归**（2026-11-21 修复，r14348 / r14349）。
///
/// <para>缺陷①（r14348）：同一 (Stage, Operation) **被拆成多个 Task** 时，
///   `BuildTerminalContributions` 用 `FirstOrDefault` 只标第一个为非末端
///   ⇒ 被标中的那道**永远不计入构成** ⇒ 该需求的份额凭空少一块。</para>
///
/// <para>缺陷②（r14349）：需求 `RouteCode/PathId` 为 **null**（RT-003 多路径待选）时，
///   按需求取到的路由图**零依赖边** ⇒ 全部 Task 被当末端 ⇒ 构成远大于声明量
///   ⇒ `声明量 − 已落定` 夹到 0 ⇒ 未排量消失。</para>
///
/// <para>两例都通过 `GenerateAllocationSharesForTest`（internal，与生产同一函数）验证
/// **份额合计 == 净量**，不启动求解器、不碰数据库。</para>
/// </summary>
public sealed class Phase5TerminalContributionDefectTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0);
    private const int MaterialId = 1;
    private const string Stage = "S1";
    private const long AllocSeq = 1;
    private const string DemandKey = "999597_1";
    private const string BatchKey = "EB|999597_1|001";

    private static PhaseFiveCompression Phase5 => new();

    [Fact(DisplayName = "缺陷①回归：同一工序拆 3 个 Task（77+77+76）⇒ 份额仍须 = 净量 230")]
    public void SplitOperation_ShareStillEqualsNet()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 77m, T0, T0.AddMinutes(77)),
            Task("t2", "M101", 77m, T0.AddMinutes(100), T0.AddMinutes(177)),
            Task("t3", "M101", 76m, T0.AddMinutes(200), T0.AddMinutes(276)),
            Task("t4", "M102", 230m, T0.AddMinutes(300), T0.AddMinutes(400)),
            Task("t5", "M103", 230m, T0.AddMinutes(400), T0.AddMinutes(500)),
        };

        var shares = Share(tasks, GraphWithFullChain(), registerBatchPath: true);

        var total = shares.Where(s => s.AllocationSequence == AllocSeq).Sum(s => s.ComponentQty);
        Assert.Equal(230m, total);
    }

    [Fact(DisplayName = "缺陷②回归：需求 Route/Path 为 null（零依赖边图）⇒ 用 Task 真实路径重取后份额仍须 = 230")]
    public void NullDemandRouteAndPath_ShareStillEqualsNet()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 230m, T0, T0.AddMinutes(100)),
            Task("t2", "M102", 230m, T0.AddMinutes(100), T0.AddMinutes(200)),
            Task("t3", "M103", 230m, T0.AddMinutes(200), T0.AddMinutes(300)),
        };

        // 关键：**不登记**批的选中路径（模拟 `/FREE` 那种批键未登记），且需求 Route/Path 为 null；
        //   图只挂在真实路径键下 —— 修复后应靠 Task 回传的 Route/Path 找到它。
        var shares = Share(tasks, GraphWithFullChain(), registerBatchPath: false);

        var total = shares.Where(s => s.AllocationSequence == AllocSeq).Sum(s => s.ComponentQty);
        Assert.Equal(230m, total);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static List<AllocationTaskShare> Share(
        List<FinalTaskDraft> tasks, RoutingGraph graph, bool registerBatchPath)
    {
        var request = new DomainSolveRequest
        {
            PlanVersionId = 999597,
            DomainKey = "FAMILY_LGJ",
            PlanningStart = T0,
            PlanningEnd = T0.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = DemandKey, PlanVersionId = 999597, DomainKey = "FAMILY_LGJ",
                    AllocationSequence = AllocSeq, DemandKey = "D1", MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = 230m, PlannedProcessQty = 230m,
                    RequiredAvailableTime = T0.AddDays(30), DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                    // 缺陷②的核心：需求自己不持有 Route/Path（RT-003 多路径待选）
                    RouteCode = null,
                    PathId = null,
                },
            },
        };

        var constraints = new ConstraintContext();
        var pathKey = new RoutePathKey("R", 1);
        constraints.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph> { [pathKey] = graph };
        if (registerBatchPath)
        {
            constraints.ChosenBatchRoutePaths[BatchKey] = pathKey;
        }

        return Phase5.GenerateAllocationSharesForTest(
            tasks, request, constraints,
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal));
    }

    private static RoutingGraph GraphWithFullChain()
    {
        var g = new RoutingGraph();
        foreach (var op in new[] { "M101", "M102", "M103" })
        {
            var key = OperationNodeKey.Of(Stage, op);
            g.Operations[key] = new OperationNode { StageCode = Stage, OperationCode = op };
        }

        void Edge(string from, string to)
        {
            var toKey = OperationNodeKey.Of(Stage, to);
            if (!g.Dependencies.TryGetValue(toKey, out var list))
            {
                list = new List<DependencyEdge>();
                g.Dependencies[toKey] = list;
            }
            list.Add(new DependencyEdge { From = OperationNodeKey.Of(Stage, from), To = toKey });
        }

        Edge("M101", "M102");
        Edge("M102", "M103");
        return g;
    }

    private static FinalTaskDraft Task(string id, string op, decimal qty, DateTime start, DateTime end) => new()
    {
        FinalDraftId = id,
        SourceDraftId = DemandKey,
        MaterialId = MaterialId,
        FactoryId = 1,
        StageCode = Stage,
        OperationCode = op,
        OperationSeq = 0,
        TaskType = "PRODUCTION",
        ResourceId = 1,
        ResourceCode = "R1",
        RouteCode = "R",
        PathId = 1,
        Quantity = qty,
        PlannedProcessQty = qty,
        UOM = "PS",
        PlannedStartTime = start,
        PlannedEndTime = end,
        ExecutionBatchDraftKey = BatchKey,
    };
}
