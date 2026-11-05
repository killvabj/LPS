using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Application.Services;
using static LPS.APS.Application.Services.PeggingOrchestrator;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **G-3 `CompletionTimeCertain` 接线验收**（0号位 2026-10-28《01_正式回复1号位_R04秒转分钟唯一装配
/// _WAIT_ONLY_AUTO与R06本地先修_v1.0》§二）：
/// 原文「认当前生产侧从未置值；同意先落 `false` 的已有**缺LT、无真实Task、路径未定**三种判据，
/// **不允许未证 WORK 就先填 `true`**」，要求交付「字段真实赋值点、**读闸生效负例**、null/false/true 守卫」。
///
/// 本文件钉死三件事：
///   ① **有赋值点**：`StageTimingNodeBuilder.BuildCore` 产出的 Fact 不再恒为 `null`；
///   ② **三态转满**：`false` 五条判据（缺 LT / 路径未定 / 时间无依据 / 无可靠 WORK / 链上无位置）
///      任一命中即 `false`；**全不命中**才是 `true`（R09-② 2026-10-29 落码，WORK 已可证）；
///   ③ **读闸真的通电**：`false` 的 Fact 被 2号位 既有闸门 `DemandCompletionDiagnostics.CertainOnly` 排除，
///      `true` 与历史 `null` 仍放行（向后兼容）。本文件**不修改** 2号位 任何代码，只验证其闸门对真实产出的行为。
/// </summary>
public class CompletionTimeCertainWiringTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime TerminalEnd = Start.AddDays(5).AddHours(8);

    // ── ① 判据一：缺 LT ⇒ false ────────────────────────────────────────

    /// <summary>
    /// 末端真实 Task 已排下，但其后的无 Routing Stage `CN_FINAL` **三级均未命中** StageLeadTime
    /// ⇒ 后续正式时间**无法证明**（该 Stage 不产段、不推进游标）⇒ `CompletionTimeCertain = false`。
    /// 这条同时锁住「缺 LT 不能因为前面有真实 Task 就当可信」。
    /// </summary>
    [Fact]
    public void 缺LT_末端有真实Task_置false()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });   // 无任何 StageLeadTime 事实 ⇒ CN_FINAL 缺参

        var missing = new List<(string DemandKey, string StageCode)>();
        var facts = StageTimingNodeBuilder.BuildCore(
            request,
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd)),
            null, null, missing);

        var fact = Assert.Single(facts);

        Assert.Empty(fact.StageTimings);                           // 缺参 Stage 不虚造时间（N8-01）
        Assert.Equal(TerminalEnd, fact.CompletionTime);             // 时间本身仍是 Task.End
        Assert.Equal(new[] { ("D1", "CN_FINAL") }, missing.ToArray());
        Assert.Equal<bool?>(false, fact.CompletionTimeCertain);      // ← 本用例唯一主张
    }

    // ── ② 判据二：无真实 Task（时间退化为 PlanningStart）⇒ false ──────

    /// <summary>
    /// 既无末端真实 Task、又无任何提前期段 ⇒ `CompletionTime` 退化为 `PlanningStart`，
    /// **不是**已排/预计完工时间 ⇒ `false`（与既有 `DEMAND_COMPLETION_TIME_UNAVAILABLE` 同一条判据）。
    /// </summary>
    [Fact]
    public void 无末端真实Task且无提前期段_置false()
    {
        var request = Req(new[] { Demand("D1", 1001) });

        var facts = StageTimingNodeBuilder.BuildCore(request, Stages(1001), Terminals());

        var fact = Assert.Single(facts);

        Assert.Equal(Start, fact.CompletionTime);
        Assert.Null(fact.TerminalFinalDraftId);
        Assert.Equal<bool?>(false, fact.CompletionTimeCertain);
    }

    // ── ③ 判据三：路径未唯一确定 ⇒ false ───────────────────────────────

    /// <summary>
    /// 纯无 Routing 但有提前期段：时间有依据（0号位 §10.3 认可），**但**无末端 Task ⇒ 无从确定唯一 Path
    /// ⇒ 提前期取自候选路径并集 = **候选口径** ⇒ 不得当权威承诺 ⇒ `false`。
    /// 与用例②只差「有没有提前期段」，用来证明 `false` 不是「没排下就一律 false」的粗暴判据。
    /// </summary>
    [Fact]
    public void 路径未唯一确定_置false()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            facts: new[]
            {
                Fact(1001, "CN_PRE", 10m, "DEPT_EXACT"),
                Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT")
            },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: Array.Empty<RoutingOperation>());

        var pathUndetermined = new List<string>();
        var facts = StageTimingNodeBuilder.BuildCore(
            request, Stages(1001, "CN_PRE", "CN_FINAL"), Terminals(),
            pathUndetermined, null);

        var fact = Assert.Single(facts);

        Assert.Equal(2, fact.StageTimings.Count);
        Assert.Equal(new[] { "D1" }, pathUndetermined.ToArray());
        Assert.Equal<bool?>(false, fact.CompletionTimeCertain);
    }

    // ── ④ 判据四：链上无位置的无 Routing Stage ⇒ false ─────────────────

    /// <summary>
    /// **R09-② 结构缺口**：范围内有无 Routing Stage、也有它的合法提前期，但**没有 `StageSequenceChain`**
    /// ⇒ 无法判定它在末端 Task 之前还是之后 ⇒ 提前期要么漏计、要么虚计。
    /// 0号位 2026-10-23《04》§1.5：「`StageDetail.StageSeq` 与真实 Routing、StageTiming 必须连续形成时间约束」
    /// ⇒ 无 seq 的 Stage 本身构不成合法时间约束 ⇒ **不得**声称确定完工。
    /// （整改前本用例断言 `null`；现在三态转满，必须落到 `false`。）
    /// </summary>
    [Fact]
    public void 链上无位置的无RoutingStage_置false()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
            ops: new[] { Op(1001, "M01", "CN_MACH") });   // 不传 chains

        var facts = StageTimingNodeBuilder.BuildCore(
            request,
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd, routeCode: "DEFAULT")));

        var fact = Assert.Single(facts);

        Assert.Equal("F2", fact.TerminalFinalDraftId);   // 有真实末端 Task
        Assert.Empty(fact.StageTimings);                  // 链上无位置 ⇒ 不产段
        Assert.Equal<bool?>(false, fact.CompletionTimeCertain);
    }

    // ── ⑤ 判据全不命中 ⇒ true（R09-② 2026-10-29 落码）────────────────

    /// <summary>
    /// **`true` 正例**：末端真实 Task + 唯一确定 Path + 链上可定位 + 合法 DEPT_EXACT 提前期 +
    /// 该 `(工厂, 部门)` 有连续合法 WORK 窗口 ⇒ 五条 `false` 判据一条都不命中。
    /// 0号位 的闸是「**不允许未证 WORK 就先填 `true`**」；R09-① 已按合法 WORK 窗口逐片推进 ⇒
    /// WORK 已证，本用例即该闸的**放行侧**证据。
    /// </summary>
    [Fact]
    public void 五条判据均未命中_置true()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });

        var facts = StageTimingNodeBuilder.BuildCore(
            request,
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd, routeCode: "DEFAULT")));

        var fact = Assert.Single(facts);

        Assert.Equal("F2", fact.TerminalFinalDraftId);
        Assert.Equal(TerminalEnd.AddHours(4), fact.CompletionTime);   // Task.End + 已证 WORK 4h
        Assert.Single(fact.StageTimings);
        Assert.Equal<bool?>(true, fact.CompletionTimeCertain);
    }

    // ── ⑥ 读闸生效 ────────────────────────────────────────────────────

    /// <summary>
    /// **读闸生效**（0号位 要求项）：把真实产出得到的 Fact 直接喂给 2号位 既有闸门
    /// `DemandCompletionDiagnostics.CertainOnly` ——
    ///   · `false`（缺 LT）**必须被排除**（此前它恒为 `null`，闸门整体空转）；
    ///   · `true`（证据齐全）**必须放行**；
    ///   · `null`（历史/未置值数据）**必须放行**（向后兼容，不得因未置值就全丢）。
    /// 这条证明的是「闸门通电了」，不是「我又写了一个 if」。
    /// </summary>
    [Fact]
    public void 读闸生效_false被排除_true与历史null放行()
    {
        var uncertain = BuildMissingLeadTimeFact();   // 真实产出：缺 LT ⇒ false
        var certain = BuildCertainFact();             // 真实产出：五判据全不命中 ⇒ true
        var legacyNull = BuildLegacyNullFact();       // 历史数据：未置值 ⇒ null

        var passed = DemandCompletionDiagnostics.CertainOnly(new[] { uncertain, certain, legacyNull });

        Assert.Equal(2, passed.Count);
        Assert.Same(certain, passed[0]);
        Assert.Same(legacyNull, passed[1]);

        var summary = DemandCompletionDiagnostics.Summarize(new[] { uncertain, certain, legacyNull });
        Assert.Equal(1, summary.CertainFalseCount);
        Assert.Equal(1, summary.CertainTrueCount);
        Assert.Equal(1, summary.CertainNullCount);
        Assert.True(summary.AnyUncertain);
    }

    // ─────────────────────────── 产出辅助 ───────────────────────────

    private static DemandCompletionFact BuildMissingLeadTimeFact()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });

        return Assert.Single(StageTimingNodeBuilder.BuildCore(
            request,
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd))));
    }

    private static DemandCompletionFact BuildCertainFact()
    {
        var request = Req(
            new[] { Demand("D1", 1001) },
            facts: new[] { Fact(1001, "CN_FINAL", 4m, "DEPT_EXACT") },
            chains: new[] { Chain(1001, ("CN_PRE", 1), ("CN_MACH", 2), ("CN_FINAL", 3)) },
            ops: new[] { Op(1001, "M01", "CN_MACH") });

        return Assert.Single(StageTimingNodeBuilder.BuildCore(
            request,
            Stages(1001, "CN_MACH", "CN_FINAL"),
            Terminals(Task("F2", "D1", 1001, "CN_MACH", "M01", TerminalEnd, routeCode: "DEFAULT"))));
    }

    /// <summary>
    /// 历史/未置值数据（`CompletionTimeCertain == null`）：闸门**必须放行**（向后兼容）。
    /// 本夹具是**手搓的读写兼容样本**，不是生产产出 —— 生产侧 2026-10-29 起不再产 `null`。
    /// </summary>
    private static DemandCompletionFact BuildLegacyNullFact()
        => new()
        {
            LogicalDemandKey = "D_LEGACY",
            MaterialId = 1002,
            CompletionTime = TerminalEnd,
            AvailableTime = TerminalEnd,
            CompletionTimeCertain = null,
            StageTimings = Array.Empty<StageTimingSegmentFact>()
        };

    // ─────────────────────────── 夹具 ───────────────────────────

    private const int FixtureDepartmentId = 9001;
    private const int FixtureFactoryId = 0;

    private static DomainSolveRequest Req(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<StageLeadTimeFact>? facts = null,
        IReadOnlyList<StageSequenceChain>? chains = null,
        IReadOnlyList<RoutingOperation>? ops = null)
    {
        var leadTimes = facts ?? Array.Empty<StageLeadTimeFact>();

        // R09-①（2026-10-29 落码）夹具补齐：无 Routing Stage 改按 `(FactoryId, DepartmentId)` 的
        // 合法 WORK 窗口逐片推进 ⇒ 给一条连续 24×7 窗口，使既有 `AddHours(n)` 期望值逐位不变。
        var contexts = leadTimes
            .Select(f => new MaterialStageDepartmentContextDto
            {
                MaterialId = f.MaterialId,
                StageCode = f.StageCode,
                ProductionDepartmentId = FixtureDepartmentId
            })
            .ToList();

        return new DomainSolveRequest
        {
            PlanningStart = Start,
            PlanningEnd = Start.AddYears(100),
            LogicalProductionDemands = demands,
            StageLeadTimes = leadTimes,
            StageSequenceChains = chains ?? Array.Empty<StageSequenceChain>(),
            RoutingOperations = ops ?? Array.Empty<RoutingOperation>(),
            MaterialStageDepartmentContexts = contexts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = FixtureFactoryId,
                    DepartmentId = FixtureDepartmentId,
                    Start = Start,
                    End = Start.AddYears(100)
                }
            }
        };
    }

    private static LogicalProductionDemand Demand(string key, int materialId, string? requiredStage = null)
        => new()
        {
            LogicalDemandKey = key,
            MaterialId = materialId,
            AllocationSequence = 1,
            RequiredStageCode = requiredStage
        };

    private static StageSequenceChain Chain(int materialId, params (string Stage, int Seq)[] steps)
        => new()
        {
            MaterialId = materialId,
            Stages = steps.Select(s => new StageSequenceStep { StageCode = s.Stage, StageSeq = s.Seq }).ToList()
        };

    private static StageLeadTimeFact Fact(int materialId, string stageCode, decimal hours, string level)
        => new()
        {
            MaterialId = materialId,
            StageCode = stageCode,
            LeadTimeHours = hours,
            MatchLevel = level
        };

    private static RoutingOperation Op(int materialId, string operationCode, string stageCode, string routeCode = "DEFAULT")
        => new()
        {
            MaterialId = materialId,
            OperationCode = operationCode,
            StageCode = stageCode,
            RouteCode = routeCode
        };

    private static FinalTaskDraft Task(
        string draftId, string demandKey, int materialId, string stageCode, string operationCode, DateTime end,
        string? routeCode = null)
        => new()
        {
            FinalDraftId = draftId,
            SourceDraftId = demandKey,
            MaterialId = materialId,
            StageCode = stageCode,
            OperationCode = operationCode,
            RouteCode = routeCode,
            PlannedStartTime = end.AddHours(-8),
            PlannedEndTime = end
        };

    private static IReadOnlyDictionary<int, HashSet<string>> Stages(int materialId, params string[] stageCodes)
        => new Dictionary<int, HashSet<string>>
        {
            [materialId] = new HashSet<string>(stageCodes, StringComparer.Ordinal)
        };

    private static IReadOnlyDictionary<string, FinalTaskDraft> Terminals(params FinalTaskDraft[] tasks)
        => tasks.ToDictionary(t => t.SourceDraftId, StringComparer.Ordinal);
}
