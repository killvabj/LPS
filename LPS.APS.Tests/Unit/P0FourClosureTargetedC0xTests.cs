using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-04 C01～C14 补齐件**（0号位 2026-11-11 §4，缺 C06/C08/C10/C12/C13/C14）。
///
/// <para>直调 <c>ValidateHardResultForTest</c>（P0-04 第 8 项测试入口），纯内存、零 DB。与
/// <c>P0FourQuantityClosureC0xTests</c>（C03/C04/C05/C07/C09/C11）、<c>P0FourMultiRoutingClosureTests</c>
/// （C01/C02）合起来构成 C01～C14 全套。</para>
///
/// <para>每条对应 v1.2 §三 B1-1 三列：**结构数量闭合**（<c>ValidateDemandStageClosure</c> 三层 L1/L2/L3）／
/// **可执行整数净量合法**（整数门禁 FRACTIONAL_EXECUTION_BATCH_QTY）／**完整 Solver 全硬约束最终结果**
/// （<c>ValidateHardResult</c> 全部 8 项硬校验＋整数门禁一次跑全）。</para>
/// </summary>
public sealed class P0FourClosureTargetedC0xTests
{
    private const int MaterialId = 1;
    private const string Stage = "STAGE1";
    private const string Route = "RT";

    private static LogicalProductionDemand Demand(
        string key, decimal qty, long alloc = 1L, bool continuation = false, string stage = "STAGE1")
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = alloc,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            DemandSequence = 1,
            StartStageCode = stage,
            StartOperationCode = null,
            RouteCode = null,     // 自由需求：Route/Path 由各执行批自选
            PathId = null,
            IsContinuation = continuation,
        };

    private static FinalTaskDraft Task(
        string id, string source, string op, decimal qty,
        string stage, string route, long? path, string seb)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = source,
            MaterialId = MaterialId,
            FactoryId = 1,
            StageCode = stage,
            OperationCode = op,
            RouteCode = route,
            PathId = path,
            StageExecutionBatchDraftKey = seb,
            ExecutionBatchDraftKey = seb,
            Quantity = qty,
            PlannedProcessQty = qty,
            SetupTime = 0m,
            PlannedStartTime = new DateTime(2026, 11, 10, 8, 0, 0),
            PlannedEndTime = new DateTime(2026, 11, 10, 9, 0, 0),
        };

    /// <summary>一条 Path 的线性格（(Stage, Operation) 依序、线性依赖），供 GetOperationsFromStage 裁剪。</summary>
    private static RoutingGraph PathGraph(int pathId, params (string Stage, string Op)[] ops)
    {
        var g = new RoutingGraph();
        foreach (var (stage, op) in ops)
        {
            g.Operations[OperationNodeKey.Of(stage, op)] = new OperationNode
            {
                OperationCode = op, StageCode = stage, StandardDuration = 1m,
                OperationPlanningMode = "FINITE_RESOURCE",
                RouteCode = Route, PathId = pathId, ProductionDepartmentId = 1,
            };
            g.RootOperations.Add(OperationNodeKey.Of(stage, op));
        }
        for (int i = 0; i + 1 < ops.Length; i++)
        {
            var from = OperationNodeKey.Of(ops[i].Stage, ops[i].Op);
            var to = OperationNodeKey.Of(ops[i + 1].Stage, ops[i + 1].Op);
            g.Dependencies[to] = new List<DependencyEdge> { new() { From = from, To = to } };
        }
        return g;
    }

    private static ConstraintContext Context(params (int PathId, RoutingGraph Graph)[] paths)
    {
        var c = new ConstraintContext();
        c.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>();
        foreach (var (pid, g) in paths)
        {
            c.RoutingGraphs[MaterialId][RoutePathKey.Of(Route, pid)] = g;
        }
        return c;
    }

    private static (bool Valid, string Error) Run(
        List<FinalTaskDraft> tasks,
        LogicalProductionDemand[] demands,
        ConstraintContext ctx,
        List<AllocationTaskShare>? shares = null,
        HashSet<string>? unscheduled = null,
        IReadOnlyList<UnscheduledTaskResult>? unscheduledTasks = null,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>>? mergeLineage = null)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            shares ?? new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = demands.ToList() },
            ctx,
            unscheduled ?? new HashSet<string>(),
            mergeLineage ?? new Dictionary<string, List<(string, decimal)>>(),
            unscheduledTasks);

    // ────────────────────────────────────────────────────────────────────
    // C06：Demand10，已排 6、真实未排 4，并留完整原因。
    //    ⇒ 按未排规则通过，不得伪造 10 件已排（对照 C05 少记未排的 DECLINED）。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C06 已排6真实未排4留原因 通过")]
    public void C06_PartialWithRealUnscheduledReason_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", "D1", "OP10", 6m, Stage, Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 6m, Stage, Route, 1, "EB1"),
        };
        var shares = new List<AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 6m },
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 10m) },
            Context((1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20")))),
            shares: shares,
            unscheduled: new HashSet<string> { "D1" },
            unscheduledTasks: new List<UnscheduledTaskResult>
            {
                new() { DraftId = "D1", Reason = "PHASE2_CAPACITY_INSUFFICIENT", UnscheduledQty = 4m },
            });

        Assert.True(valid, "已排 6 ＋ 真实未排 4（带原因）⇒ 三量等式 6+4=10 闭合、部分落定合法通过，不得伪造 10 件已排。" + err);
    }

    // ────────────────────────────────────────────────────────────────────
    // C08：同 Batch 完整链被复制两遍、各项数量重复 ⇒ 重复链/超量，必须拒。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C08 同批完整链复制两遍 拒")]
    public void C08_DuplicateChainInSameBatch_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", "D1", "OP10", 10m, Stage, Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 10m, Stage, Route, 1, "EB1"),
            Task("T3", "D1", "OP10", 10m, Stage, Route, 1, "EB1"),   // 同一批键 EB1 又一套完整链
            Task("T4", "D1", "OP20", 10m, Stage, Route, 1, "EB1"),
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 10m) },
            Context((1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20")))));

        Assert.False(valid, "同一批键 EB1 完整链被复制两遍 ⇒ 逐 Op Σ=20 > 需求 10 ⇒ 必须拒。");
        Assert.Contains("DEMAND_STAGE_QTY_OVER", err);
    }

    // ────────────────────────────────────────────────────────────────────
    // C10：同 OperationCode（OP10）出现在 Path1 与 Path2，但各自内链不同
    //    （Path1=OP10/OP20、Path2=OP10/OP30）⇒ 按 Path 隔离，不跨路径借用。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C10 同OperationCode跨Path按Path隔离 通过")]
    public void C10_SameOpCodeAcrossPaths_IsolatedPerPath_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", "D1", "OP10", 5m, Stage, Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 5m, Stage, Route, 1, "EB1"),
            Task("T3", "D1", "OP10", 5m, Stage, Route, 2, "EB2"),
            Task("T4", "D1", "OP30", 5m, Stage, Route, 2, "EB2"),
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 10m) },
            Context(
                (1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20"))),
                (2, PathGraph(2, (Stage, "OP10"), (Stage, "OP30")))));

        Assert.True(valid, "两个 Path 各含同码 OP10、内链不同（OP20/OP30），必须按 Path 独立闭合，不拿别的 Path 的 OP30 给 Path1 补位。" + err);
    }

    // ────────────────────────────────────────────────────────────────────
    // C12：多 Stage 串行：Stage1 完整批 EB1；Stage2 分两个不同 Path 的批 EB2/EB3。
    //    ⇒ 正确分 Stage 核证（Stage1=10、Stage2=5+5=10），不跨 Stage 重复加量。
    //    ★这是旧「按(需求,工序)聚合」会误杀的判别例：OP30/OP40/OP50/OP60 各只有 5，
    //    只按工序看会 5≠10 报错；按 Stage 看 Stage2 两批合 10 正确。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C12 多Stage串行+某Stage两Path 通过")]
    public void C12_MultiStageSerialWithTwoPathsInOneStage_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            // Stage1：单一执行批（EB1, Path1），完整完成 10 件。
            Task("T1", "D1", "OP10", 10m, "STAGE1", Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 10m, "STAGE1", Route, 1, "EB1"),
            // Stage2：两个不同 Path 的批（EB2 Path1=OP30/OP40、EB3 Path2=OP50/OP60），各 5 件。
            Task("T3", "D1", "OP30", 5m, "STAGE2", Route, 1, "EB2"),
            Task("T4", "D1", "OP40", 5m, "STAGE2", Route, 1, "EB2"),
            Task("T5", "D1", "OP50", 5m, "STAGE2", Route, 2, "EB3"),
            Task("T6", "D1", "OP60", 5m, "STAGE2", Route, 2, "EB3"),
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 10m, stage: "STAGE1") },
            Context(
                (1, PathGraph(1, ("STAGE1", "OP10"), ("STAGE1", "OP20"), ("STAGE2", "OP30"), ("STAGE2", "OP40"))),
                (2, PathGraph(2, ("STAGE1", "OP10"), ("STAGE1", "OP20"), ("STAGE2", "OP50"), ("STAGE2", "OP60")))));

        Assert.True(valid, "Stage1=10、Stage2=5+5=10 分 Stage 闭合（含跨 Stage 有向交接），不得按(需求,工序)聚合把 Stage2 的 5 误判为少 5。" + err);
    }

    // ────────────────────────────────────────────────────────────────────
    // C13：A/B 历史剩余是小数净量（13.5）、身份锁定（IsContinuation=true）。
    //    ⇒ 按既有 A/B 冻结事实放行：整数门禁不得把小数的冻结剩余硬拒（§二「锁定事实不得篡改」；
    //       §3.1 L3「A/B 逐 Operation 剩余不一致／已执行报工按冻结合同保留，不得为通过而强改每道同量」）。
    //    说明：本件钉「A/B 小数净量在硬校验里成立且不被整数门禁误杀」；「输出工序间数量本身不一致」的
    //    更激进变体需 0号位 在冻结文档给定 continuation 净量口径后再动 ValidateDemandStageClosure 的 L3。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C13 A/B历史小数剩余身份锁定 通过")]
    public void C13_ContinuationFractionalNet_IdentityLocked_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", "D1", "OP10", 13.5m, Stage, Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 13.5m, Stage, Route, 1, "EB1"),
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 13.5m, continuation: true) },
            Context((1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20")))));

        Assert.True(valid, "A/B 冻结剩余 13.5（小数）＋身份锁定 ⇒ 硬校验放行，不得被整数门禁/逐工序等量误杀。" + err);
    }

    // ────────────────────────────────────────────────────────────────────
    // C14：装配件 UOM，需求 1 拆为 0.5+0.5 ⇒ 与允许精度不符（MES 不容分数执行）。
    //    ⇒ 输出单位/批量合法性核查结论：FRACTIONAL_EXECUTION_BATCH_QTY 拒，不得无依据直接取整。
    // ────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "C14 装配UOM需求1拆0.5+0.5 拒")]
    public void C14_AssemblyUomOneSplitHalf_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", "D1", "OP10", 0.5m, Stage, Route, 1, "EB1"),
            Task("T2", "D1", "OP20", 0.5m, Stage, Route, 1, "EB1"),
            Task("T3", "D1", "OP10", 0.5m, Stage, Route, 1, "EB2"),
            Task("T4", "D1", "OP20", 0.5m, Stage, Route, 1, "EB2"),
        };
        var (valid, err) = Run(
            tasks,
            new[] { Demand("D1", 1m) },
            Context((1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20")))));

        Assert.False(valid, "需求 1 拆成 0.5+0.5 是非法小数可执行批（件/套不允许分数执行），必须被整数门禁拒，不得无依据取整。");
        Assert.Contains("FRACTIONAL_EXECUTION_BATCH_QTY", err);
    }
}