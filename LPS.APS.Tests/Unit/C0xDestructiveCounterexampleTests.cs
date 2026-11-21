using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-04 C01～C14 破坏性反例**（0号位 v1.8 §二「C01～C14 全套破坏性反例」）。
///
/// <para>既有三份文件已覆盖 C01～C14 的**正向**判别；本文件只补**必须被拒**的反向破坏，
/// 每条对应一个正向用例的"同一夹具、改坏一处"：</para>
/// <list type="bullet">
///   <item>C06 反向：已排 6、声称未排 4，却**没有注入未排记录/原因** ⇒ 三量不闭合，必须拒；</item>
///   <item>C09 反向：同 Op 拆两片合法，但两片总量**超过**需求 ⇒ 必须拒；</item>
///   <item>C10 反向：同一 Path 的内链**借用**另一 Path 的工序补位 ⇒ 必须拒（按 Path 隔离，不得串用）；</item>
///   <item>C11 反向：合批 Task 承载份额**超过** Task 执行量 ⇒ 被并需求少 5，必须拒；</item>
///   <item>C12 反向：多 Stage 串行里 Stage2 **漏掉一个批**（只剩 5）⇒ 分 Stage 闭合必须拒。</item>
/// </list>
///
/// <para>纯内存，直调 `PhaseFiveCompression.ValidateHardResultForTest`，不碰数据库、不跑完整求解。</para>
/// </summary>
public sealed class C0xDestructiveCounterexampleTests
{
    private const int MaterialId = 1;
    private const string Stage = "STAGE1";
    private const string Route = "RT";

    private static LogicalProductionDemand Demand(
        string key, decimal qty, string stage = Stage, bool continuation = false) => new()
    {
        LogicalDemandKey = key,
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = 1L,
        DemandKey = key,
        MaterialId = MaterialId,
        FactoryId = 1,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        DemandSequence = 1,
        StartStageCode = stage,
        RouteCode = Route,
        PathId = 1,
        IsContinuation = continuation,
    };

    private static FinalTaskDraft T(string id, string source, string op, decimal qty,
        string stage = Stage, long pathId = 1, string? seb = null) => new()
    {
        FinalDraftId = id,
        SourceDraftId = source,
        MaterialId = MaterialId,
        FactoryId = 1,
        StageCode = stage,
        OperationCode = op,
        RouteCode = Route,
        PathId = pathId,
        StageExecutionBatchDraftKey = seb,
        ExecutionBatchDraftKey = seb,
        Quantity = qty,
        PlannedProcessQty = qty,
        SetupTime = 0m,
        PlannedStartTime = new DateTime(2026, 11, 10, 8, 0, 0),
        PlannedEndTime = new DateTime(2026, 11, 10, 9, 0, 0),
    };

    private static RoutingGraph PathGraph(long pathId, params (string Stage, string Op)[] ops)
    {
        var g = new RoutingGraph();
        foreach (var (stage, op) in ops)
        {
            var key = OperationNodeKey.Of(stage, op);
            g.Operations[key] = new OperationNode
            {
                OperationCode = op, StageCode = stage, StandardDuration = 1m,
                OperationPlanningMode = "FINITE_RESOURCE", RouteCode = Route,
                PathId = (int)pathId, ProductionDepartmentId = 1,
            };
            g.RootOperations.Add(key);
        }
        return g;
    }

    private static ConstraintContext Context(params (long PathId, RoutingGraph Graph)[] paths)
    {
        var c = new ConstraintContext();
        var byPath = new Dictionary<RoutePathKey, RoutingGraph>();
        foreach (var (pathId, graph) in paths) byPath[RoutePathKey.Of(Route, (int)pathId)] = graph;
        c.RoutingGraphs[MaterialId] = byPath;
        return c;
    }

    private static (bool Valid, string Error) Run(
        List<FinalTaskDraft> tasks,
        ConstraintContext ctx,
        LogicalProductionDemand[] demands,
        List<AllocationTaskShare>? shares = null,
        HashSet<string>? unscheduled = null,
        IReadOnlyList<UnscheduledTaskResult>? unscheduledTasks = null,
        Dictionary<string, List<(string, decimal)>>? mergeLineage = null)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            shares ?? new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = demands.ToList() },
            ctx,
            unscheduled ?? new HashSet<string>(),
            mergeLineage ?? new Dictionary<string, List<(string, decimal)>>(),
            unscheduledTasks);

    private static ConstraintContext SinglePathTwoOp()
        => Context((1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20"))));

    // ── C06 反向：已排 6、声称未排 4，但**未注入任何未排记录/原因** ⇒ 必须拒 ──
    [Fact(DisplayName = "C06反例 已排6声称未排4但无未排记录/原因 拒")]
    public void C06_Inverse_UnderProducedWithoutUnscheduledEvidence_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 6m, seb: "EB1"),
            T("T2", "D1", "OP20", 6m, seb: "EB1"),
        };
        var shares = new List<AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 6m },
        };

        // 与 C06 正例同一夹具，唯一差别：**不给** UnscheduledTaskResult（= 未排量无原因、无凭证）。
        var (valid, err) = Run(
            tasks, SinglePathTwoOp(), new[] { Demand("D1", 10m) },
            shares: shares,
            unscheduled: new HashSet<string> { "D1" });

        Assert.False(valid,
            "已排 6 却声称未排 4、又拿不出未排记录/原因 ⇒ 不算真实未排，必须拒（不得把缺量当成合法未排）。" + err);
    }

    // ── C09 反向：同 Op 拆 4+8（两片总和 12 > 需求 10）⇒ 必须拒 ──
    [Fact(DisplayName = "C09反例 同Op拆4+8超需求 拒")]
    public void C09_Inverse_PartialSplitOverTotal_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 4m, seb: "EB1"),
            T("T2", "D1", "OP10", 8m, seb: "EB1"),   // 4+8=12 > 10
            T("T3", "D1", "OP20", 10m, seb: "EB1"),
        };
        var (valid, err) = Run(tasks, SinglePathTwoOp(), new[] { Demand("D1", 10m) });

        Assert.False(valid, "同 Op 合法拆分的**总量**不得超过需求（4+8=12 > 10），必须拒。" + err);
    }

    // ── C10 反向：Path1 的内链借用 Path2 的工序（Path1 缺 OP20、混入 Path2 的 OP30）⇒ 必须拒 ──
    [Fact(DisplayName = "C10反例 同Op码跨Path串用补位 拒")]
    public void C10_Inverse_BorrowOpFromOtherPath_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            // Path1 本应 = {OP10, OP20}，这里缺 OP20、拿 Path2 的 OP30 来"补位"。
            T("T1", "D1", "OP10", 5m, pathId: 1, seb: "EB1"),
            T("T2", "D1", "OP30", 5m, pathId: 1, seb: "EB1"),
            T("T3", "D1", "OP10", 5m, pathId: 2, seb: "EB2"),
            T("T4", "D1", "OP30", 5m, pathId: 2, seb: "EB2"),
        };
        var ctx = Context(
            (1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20"))),
            (2, PathGraph(2, (Stage, "OP10"), (Stage, "OP30"))));

        var (valid, err) = Run(tasks, ctx, new[] { Demand("D1", 10m) });

        Assert.False(valid,
            "Path1 的批缺本 Path 必需工序 OP20、混入别 Path 的 OP30 ⇒ 必须拒（各 Path 按自身工序集合独立闭合，不得串用补位）。" + err);
    }

    // ── C11 反向：合批 Task 30，血缘声明 D2=25 ⇒ D2 实际只得到 20 ⇒ 少 5，必须拒 ──
    [Fact(DisplayName = "C11反例 合批血缘份额超过Task执行量 拒")]
    public void C11_Inverse_MergeLineageExceedsTaskQty_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 30m, seb: "EB1"),
            T("T2", "D1", "OP20", 30m, seb: "EB1"),
        };
        // 锚 D1 占 10，Task 总 30 ⇒ D2 最多只能拿到 20；血缘却声明 25 ⇒ 超 5。
        var lineage = new Dictionary<string, List<(string, decimal)>>(StringComparer.Ordinal)
        {
            ["T1"] = new List<(string, decimal)> { ("D2", 25m) },
            ["T2"] = new List<(string, decimal)> { ("D2", 25m) },
        };
        var (valid, err) = Run(tasks, SinglePathTwoOp(),
            new[] { Demand("D1", 10m), Demand("D2", 25m) },
            mergeLineage: lineage);

        Assert.False(valid,
            "合批 Task 执行量 30、锚 10 ⇒ 被并 D2 最多 20；血缘声明 25 ⇒ D2 少 5，必须拒（份额不得超过执行量）。" + err);
    }

    // ── C04 反向变体：重复生产不许靠"换 Path"规避（两批各 10，需求 10）⇒ 必须拒 ──
    [Fact(DisplayName = "C04反例变体 两批换Path重复生产20 拒")]
    public void C04_InverseVariant_DuplicateAcrossPaths_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 10m, pathId: 1, seb: "EB1"),
            T("T2", "D1", "OP20", 10m, pathId: 1, seb: "EB1"),
            T("T3", "D1", "OP10", 10m, pathId: 2, seb: "EB2"),
            T("T4", "D1", "OP30", 10m, pathId: 2, seb: "EB2"),
        };
        var ctx = Context(
            (1, PathGraph(1, (Stage, "OP10"), (Stage, "OP20"))),
            (2, PathGraph(2, (Stage, "OP10"), (Stage, "OP30"))));

        var (valid, err) = Run(tasks, ctx, new[] { Demand("D1", 10m) });

        Assert.False(valid,
            "需求 10，两条 Path 各排 10 = 20 ⇒ 换 Path 不得规避重复生产判据，必须拒。" + err);
    }

    // ── C14 反向变体：小数执行批不止 0.5 —— 1.5+1.5 同样必须拒 ──
    [Fact(DisplayName = "C14反例变体 需求3拆1.5+1.5 拒")]
    public void C14_InverseVariant_OnePointFiveSplit_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 1.5m, seb: "EB1"),
            T("T2", "D1", "OP20", 1.5m, seb: "EB1"),
            T("T3", "D1", "OP10", 1.5m, seb: "EB2"),
            T("T4", "D1", "OP20", 1.5m, seb: "EB2"),
        };
        var (valid, err) = Run(tasks, SinglePathTwoOp(), new[] { Demand("D1", 3m) });

        Assert.False(valid, "需求 3 拆成 1.5+1.5 仍是非整数执行批（件/套不允许分数执行），必须拒。" + err);
        Assert.Contains("FRACTIONAL_EXECUTION_BATCH_QTY", err);
    }

    // ── C07 反向变体：同一批声称走了**不存在的 Path**（本批实测：硬校验**放行**）──
    //
    // 2026-11-20 实测：`ValidateHardResultForTest` **不检查** Task 的 `(RouteCode, PathId)` 是否真在该物料的
    // `RoutingGraphs` 里 ⇒ 身份不可证的 Path 会被放行。这是**新发现的口径缺口**，不在本轮擅自改判定
    // （改它要 0号位 先定「Path 身份不存在时是拒整单、还是判技术失败」），故按现行为立例并 Skip，
    // 与 `SH_LINKED` 待裁例同一处理方式。
    [Fact(
        DisplayName = "C07反例变体（待0号位裁）：同批引用不存在的Path 当前被放行",
        Skip = "实测硬校验不检查 Path 身份存在性（放行）；待 0号位 裁决应拒还是应判技术失败后再改。")]
    public void C07_InverseVariant_UnknownPathInBatch_CurrentBehavior_Accepted()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 10m, pathId: 9, seb: "EB1"),   // Path 9 不在 RoutingGraphs 里
            T("T2", "D1", "OP20", 10m, pathId: 9, seb: "EB1"),
        };
        var (valid, _) = Run(tasks, SinglePathTwoOp(), new[] { Demand("D1", 10m) });

        Assert.True(valid, "现行为：路径身份不可证也放行（缺口，待裁）。");
    }

    // ── C01 反向：同批内两工序份额打架（26 vs 25）⇒ 批净产出自相矛盾，必须拒 ──
    [Fact(DisplayName = "C01反例 同批两工序份额不等 拒")]
    public void C01_Inverse_OpsDisagreeInsideBatch_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "M202", 26m, pathId: 2, seb: "EB001"),
            T("T2", "D1", "M203", 25m, pathId: 2, seb: "EB001"),   // 同批内 26 ≠ 25
            T("T3", "D1", "M302", 26m, pathId: 3, seb: "EB002"),
            T("T4", "D1", "M303", 26m, pathId: 3, seb: "EB002"),
        };
        var ctx = Context(
            (2, PathGraph(2, (Stage, "M202"), (Stage, "M203"))),
            (3, PathGraph(3, (Stage, "M302"), (Stage, "M303"))));

        var (valid, err) = Run(tasks, ctx, new[] { Demand("D1", 51m) });

        Assert.False(valid,
            "同批内 M202=26、M203=25 ⇒ 该批净产出无法自洽，必须拒（不得因两条 Path 合计看似 51 就放行）。" + err);
    }

    // ── C02 反向：两批各 3 走不同 Path，但需求只有 5 ⇒ 跨 Path 合计超量，必须拒 ──
    [Fact(DisplayName = "C02反例 两批3走不同Path合计6超需求5 拒")]
    public void C02_Inverse_TwoBatchesOverDemandAcrossPaths_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "M202", 3m, pathId: 2, seb: "EB001"),
            T("T2", "D1", "M203", 3m, pathId: 2, seb: "EB001"),
            T("T3", "D1", "M302", 3m, pathId: 3, seb: "EB002"),
            T("T4", "D1", "M303", 3m, pathId: 3, seb: "EB002"),
        };
        var ctx = Context(
            (2, PathGraph(2, (Stage, "M202"), (Stage, "M203"))),
            (3, PathGraph(3, (Stage, "M302"), (Stage, "M303"))));

        var (valid, err) = Run(tasks, ctx, new[] { Demand("D1", 5m) });

        Assert.False(valid, "需求 5，两批各 3 = 6 ⇒ 跨 Path 合计超量，必须拒。" + err);
    }

    // ── C03 反向：同 Path 5+5 合法，但其中一批**重复**插了一条同工序 Task ⇒ 必须拒 ──
    [Fact(DisplayName = "C03反例 同Path批内重复工序Task 拒")]
    public void C03_Inverse_DuplicateOpTaskInsideBatch_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 5m, seb: "EB1"),
            T("T2", "D1", "OP20", 5m, seb: "EB1"),
            T("T3", "D1", "OP10", 5m, seb: "EB2"),
            T("T4", "D1", "OP20", 5m, seb: "EB2"),
            T("T5", "D1", "OP10", 5m, seb: "EB1"),   // EB1 内 OP10 重复 ⇒ 批内 10 ≠ 5
        };
        var (valid, err) = Run(tasks, SinglePathTwoOp(), new[] { Demand("D1", 10m) });

        Assert.False(valid, "EB1 内 OP10 被重复记一条 ⇒ 批内工序份额打架、Stage 合计 15 > 10，必须拒。" + err);
    }

    // ── C12 反向：多 Stage 串行，Stage2 只剩一个批（5 < 10）⇒ 分 Stage 闭合必须拒 ──
    [Fact(DisplayName = "C12反例 多Stage串行漏Stage2一个批 拒")]
    public void C12_Inverse_MissingOneStageBatch_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "D1", "OP10", 10m, stage: "STAGE1", seb: "EB1"),
            T("T2", "D1", "OP20", 10m, stage: "STAGE1", seb: "EB1"),
            // Stage2 本应 EB2(5)+EB3(5)=10，这里只有 EB2 ⇒ 5。
            T("T3", "D1", "OP30", 5m, stage: "STAGE2", seb: "EB2"),
            T("T4", "D1", "OP40", 5m, stage: "STAGE2", seb: "EB2"),
        };
        var ctx = Context(
            (1, PathGraph(1, ("STAGE1", "OP10"), ("STAGE1", "OP20"), ("STAGE2", "OP30"), ("STAGE2", "OP40"))),
            (2, PathGraph(2, ("STAGE1", "OP10"), ("STAGE1", "OP20"), ("STAGE2", "OP50"), ("STAGE2", "OP60"))));

        var (valid, err) = Run(tasks, ctx, new[] { Demand("D1", 10m, stage: "STAGE1") });

        Assert.False(valid,
            "Stage1=10 但 Stage2 只有 5（漏了一个批）⇒ 分 Stage 闭合少 5，必须拒（不得只凭 Stage1 就判整单成立）。" + err);
    }
}
