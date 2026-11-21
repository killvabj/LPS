using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **C13 逐 Operation 剩余量 —— 破坏性正反例**（0号位 v1.3 §2.1 第 3 条，标题即「不等任何号位」）：
/// 「按冻结合同**自己写**真正的 C13 破坏性测试：构造同一合法 A/B 批中两个 Operation 的
/// **不同、可证明的剩余量**，含历史已完成/锁定及当前待加工身份；正例必须保留这些量、合法通过；
/// 负例必须能拦过量、缺少未来应有 OP、混 Path、无来源份额、错误把 OP 净量统一。」
///
/// <para><b>为什么只能在 Task 级构造</b>：请求 DTO 的 `DemandStageProcessQuantity` 只到 **Stage 级**，
/// `LogicalProductionDemand` 只有 `StartOperationCode + NetOutputQty` 单值 ⇒ **逐 OP 剩余量无请求字段**。
/// 故本文件直接构造 Task 集合 + `ExecutionConstraint` 派生的 `LockedTasks`，
/// 驱动**生产同一个校验器**（`ValidateDemandStageClosure`，经 internal 钩子）。</para>
///
/// <para><b>实现分支</b>（0号位 v1.3 §2.1 第 4 条）：
/// <c>IsContinuation || NoSplitMerge</c> = A/B 既有执行批 ⇒ **逐工序对照该工序自己的执行事实**，
/// 不得强制等于单一 `BatchNetOutputQty`；缺可信份额 ⇒ 显式 <c>BATCH_OP_QTY_UNVERIFIED</c>；
/// C 新批 ⇒ 各工序等量是构造事实，照旧硬校验。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class C13PerOperationRemainderDestructiveTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0);
    private const int MaterialId = 1;
    private const string Stage = "S1";
    private const string DemandKey = "999597_1";
    private const string BatchKey = "EB|999597_1|001";

    // ─────────────────────── 正例（v1.3 §2.1-3 必须合法通过）───────────────────────

    [Fact(DisplayName = "C13正例：A/B 批 OP10 剩 5 / OP20 剩 3，各有执行事实 ⇒ 必须合法通过（不得强制等量）")]
    public void AbBatch_DifferentPerOpRemainder_WithExecutionFacts_Passes()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
            Task("t2", "M102", 3m, T0.AddMinutes(5), T0.AddMinutes(8)),
        };

        var runner = new Runner();
        var error = runner.Validate(tasks, net: 5m, isContinuation: true,
            facts: new Dictionary<string, decimal> { ["M101"] = 5m, ["M102"] = 3m });

        Assert.Null(error);   // 现实现曾在此给出 BATCH_OP_QTY_MISMATCH —— 已按 v1.3 修正
        Assert.Empty(runner.Constraints.TraceNotes.Where(n => n.ReasonCode == "BATCH_OP_QTY_UNVERIFIED"));
    }

    // ─────────────────────── 负例 ───────────────────────

    [Fact(DisplayName = "C13负例①过量：A/B 批 OP20 份额 7 ≠ 该工序执行事实 3 ⇒ 必须拒")]
    public void AbBatch_OpShareContradictsExecutionFact_IsRejected()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
            Task("t2", "M102", 7m, T0.AddMinutes(5), T0.AddMinutes(8)),
        };

        var error = new Runner().Validate(tasks, net: 5m, isContinuation: true,
            facts: new Dictionary<string, decimal> { ["M101"] = 5m, ["M102"] = 3m });

        Assert.NotNull(error);
        Assert.Contains("BATCH_OP_QTY_MISMATCH", error);
        Assert.Contains("M102", error);
    }

    [Fact(DisplayName = "C13负例②无来源份额：A/B 批缺逐工序执行事实 ⇒ 显式未验证（不伪造相等、不按 0 跳过）")]
    public void AbBatch_NoPerOpFacts_IsExplicitlyUnverified()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
            Task("t2", "M102", 3m, T0.AddMinutes(5), T0.AddMinutes(8)),
        };

        var runner = new Runner();
        var error = runner.Validate(tasks, net: 5m, isContinuation: true, facts: null);

        // 不整域失败（v1.3 §2.1-4「必要时使用已有 Issue/未排合同」），但必须留可定位的显式未验证痕迹。
        Assert.Null(error);
        var note = Assert.Single(runner.Constraints.TraceNotes.Where(n => n.ReasonCode == "BATCH_OP_QTY_UNVERIFIED"));
        Assert.Contains("M101", note.Message);
        Assert.Contains("M102", note.Message);
    }

    [Fact(DisplayName = "C13负例③混 Path：同批内混入两条 Path ⇒ 必须被拦（BATCH_ROUTE_MIXED）")]
    public void MixedPath_IsRejected()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
            Task("t2", "M102", 3m, T0.AddMinutes(5), T0.AddMinutes(8), pathId: 2),
        };

        var error = new Runner().Validate(tasks, net: 5m, isContinuation: true, facts: null);

        Assert.NotNull(error);
        Assert.Contains("BATCH_ROUTE_MIXED", error);
    }

    [Fact(DisplayName = "C13负例④缺未来应有 OP：A/B 批缺 M102 工序 ⇒ 必须被拦（BATCH_OP_MISSING）")]
    public void AbBatch_MissingRequiredOperation_IsRejected()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
        };

        var error = new Runner().Validate(tasks, net: 5m, isContinuation: true,
            facts: new Dictionary<string, decimal> { ["M101"] = 5m });

        Assert.NotNull(error);
        Assert.Contains("BATCH_OP_MISSING", error);
        Assert.Contains("M102", error);
    }

    [Fact(DisplayName = "C13负例⑤错误把 OP 净量统一：C 新批两工序不等量 ⇒ 仍必须拒（防止放宽过头）")]
    public void CBatch_UnequalPerOp_StillRejected()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("t1", "M101", 5m, T0, T0.AddMinutes(5)),
            Task("t2", "M102", 3m, T0.AddMinutes(5), T0.AddMinutes(8)),
        };

        var error = new Runner().Validate(tasks, net: 5m, isContinuation: false, facts: null);

        Assert.NotNull(error);
        Assert.Contains("BATCH_OP_QTY_MISMATCH", error);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private sealed class Runner
    {
        public ConstraintContext Constraints { get; } = new();

        public string? Validate(
            List<FinalTaskDraft> tasks, decimal net, bool isContinuation, Dictionary<string, decimal>? facts)
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
                        AllocationSequence = 1, DemandKey = "D1", MaterialId = MaterialId, FactoryId = 1,
                        StartStageCode = Stage, StartOperationCode = "M101",
                        NetOutputQty = net, PlannedProcessQty = net,
                        RequiredAvailableTime = T0.AddDays(30), DemandSequence = 1,
                        IsContinuation = isContinuation,
                        QuantityAuthority = QuantityAuthorityValues.None,
                        RouteCode = "R",
                        PathId = 1,
                    },
                },
                // A/B 既有执行批的逐工序执行事实载体（与 Phase2 锁定任务同一来源）。
                ExecutionConstraints = (facts ?? new Dictionary<string, decimal>())
                    .Select(kv => new ExecutionConstraint
                    {
                        DraftId = DemandKey, StageCode = Stage, OperationCode = kv.Key,
                        LockedNetOutputQty = kv.Value, LockedQuantity = kv.Value,
                        LockedStart = T0, LockedEnd = T0.AddMinutes(1),
                        ConstraintType = "LOCKED", ResourceId = 1,
                    })
                    .ToList(),
            };

            var pathKey = new RoutePathKey("R", 1);
            Constraints.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
            {
                [pathKey] = GraphWithOps(),
            };
            Constraints.ChosenBatchRoutePaths[BatchKey] = pathKey;

            // 与生产同一构建步骤：ExecutionConstraint ⇒ LockedTasks（键域 DraftId+Stage+Operation）。
            foreach (var ec in request.ExecutionConstraints)
            {
                Constraints.LockedTasks[(ec.DraftId, ec.StageCode ?? string.Empty, ec.OperationCode ?? string.Empty)] =
                    new LockedTaskConstraint
                    {
                        DraftId = ec.DraftId,
                        StageCode = ec.StageCode,
                        OperationCode = ec.OperationCode,
                        LockedQuantity = ec.LockedQuantity,
                        LockedNetOutputQty = ec.LockedNetOutputQty,
                    };
            }

            return new PhaseFiveCompression().ValidateDemandStageClosureForTest(
                tasks, request,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
                Constraints);
        }
    }

    private static RoutingGraph GraphWithOps()
    {
        var g = new RoutingGraph();
        foreach (var op in new[] { "M101", "M102" })
        {
            var key = OperationNodeKey.Of(Stage, op);
            g.Operations[key] = new OperationNode { StageCode = Stage, OperationCode = op };
        }

        var toKey = OperationNodeKey.Of(Stage, "M102");
        g.Dependencies[toKey] = new List<DependencyEdge>
        {
            new() { From = OperationNodeKey.Of(Stage, "M101"), To = toKey },
        };
        return g;
    }

    private static FinalTaskDraft Task(
        string id, string op, decimal qty, DateTime start, DateTime end, long pathId = 1) => new()
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
        PathId = pathId,
        Quantity = qty,
        PlannedProcessQty = qty,
        UOM = "PS",
        PlannedStartTime = start,
        PlannedEndTime = end,
        ExecutionBatchDraftKey = BatchKey,
    };
}
