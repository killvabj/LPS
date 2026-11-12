using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// A6 正交性能对照（0号位 2026-11-10《Batch_Code_Review_and_Action_Plan v1.0》§4-A6 / §5.1）：
/// **同一份输入、只切两个实验开关**，把「单 Op 无 PI 快路 + 来源 PI 守卫」的独立增量从
/// 「整组规则调整的总收益」里剥出来。
///
/// <para><b>为什么要另建</b>：`SolverReplayTest` 只跑单次重放，没有前缀限流、也不切实验开关；
///   而 `StageBatchMergeTests` 那些是夹具级，测不出吞吐。</para>
///
/// <para><b>档位</b>：</para>
/// <list type="bullet">
///   <item><b>S1</b> = `PI_ONLY` + `APS_P2_SINGLEOP_FASTPATH=0` + `APS_P2_SOURCE_PI_GUARD=0`
///         ⇒ 只有「旧的多 Op 同 PI 过滤」。</item>
///   <item><b>S2</b> = `PI_ONLY` + 两个开关都 =1 ⇒ 本次报告状态。</item>
/// </list>
/// <para>**S1→S2 才是本轮新改动的增量**；S0（LEGACY）→S2 是整组规则的总收益。</para>
///
/// <para><b>⚠ 保真度边界（与 `SolverReplayTest` 同）</b>：快照用 `ReferenceHandler.IgnoreCycles` 捕获，
///   环引用被写成 null ⇒ **只可用于吞吐对照与热点归因**，不得据此断言业务结果等价。</para>
///
/// <para><b>开门</b>：未设 `APS_REPLAY_SOLVE_REQUEST` ⇒ **跳过**（仓库里不硬编码本机路径）。</para>
/// </summary>
public class PhaseTwoOrthoReplayPerfTests
{
    private const string ReplayPathEnvVar = "APS_REPLAY_SOLVE_REQUEST";
    private const string LimitEnvVar = "APS_ORTHO_DEMAND_LIMIT";
    private const int DefaultLimit = 20000;

    private readonly ITestOutputHelper _output;

    public PhaseTwoOrthoReplayPerfTests(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "A6 正交对照：S1 vs S2（同一输入，只切单Op快路 + 来源PI守卫）")]
    public async Task S1_vs_S2_SameInput()
    {
        var jsonPath = Environment.GetEnvironmentVariable(ReplayPathEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath),
            $"未指定已存在的入参 JSON（{ReplayPathEnvVar}='{jsonPath ?? "(null)"}'）。");

        var limit = int.TryParse(Environment.GetEnvironmentVariable(LimitEnvVar), out var n) && n > 0
            ? n
            : DefaultLimit;

        // 全量 179k 单档约 70+ 分钟，两档合跑超过单次工具超时 ⇒ 支持分档单跑。
        // `APS_ORTHO_RUN=S1|S2|BOTH`（缺省 BOTH）。
        var which = (Environment.GetEnvironmentVariable("APS_ORTHO_RUN") ?? "BOTH").ToUpperInvariant();

        _output.WriteLine($"[A6] 入参：{jsonPath}（{new FileInfo(jsonPath!).Length / 1048576d:F0} MB）");
        _output.WriteLine($"[A6] 需求前缀上限：{limit}（{LimitEnvVar}）；档位={which}");

        var s1 = which is "S1" or "BOTH"
            ? await RunAsync(jsonPath!, limit, fastPath: "0", guard: "0", label: "S1")
            : null;
        var s2 = which is "S2" or "BOTH"
            ? await RunAsync(jsonPath!, limit, fastPath: "1", guard: "1", label: "S2")
            : null;

        if (s1 is null || s2 is null)
        {
            var only = s1 ?? s2!;
            _output.WriteLine($"[A6] 单档完成：{only.Label} 墙钟 {only.WallSeconds:F1}s，"
                              + $"Success={only.Success}，FinalTask={only.FinalTasks}，"
                              + $"P2={only.Phase2Ms}ms，单Op扫描 {only.ScanCalls}/{only.ScannedTasks}，候选 {only.Candidates}，"
                              + $"快路跳过 {only.SkippedNoPi}");
            Assert.True(only.Success, only.Error);
            return;
        }

        _output.WriteLine("");
        _output.WriteLine("── A6 正交对照 ─────────────────────────────────────────────");
        _output.WriteLine($"  S1（快路关/守卫关）：墙钟 {s1.WallSeconds,8:F1}s  P2 {s1.Phase2Ms,8}ms  "
                          + $"单Op扫描 {s1.ScanCalls,6} 次 / {s1.ScannedTasks,10} 元素  候选 {s1.Candidates,6}  "
                          + $"快路跳过 {s1.SkippedNoPi,6}  Merge {s1.MergeAttempts,6}");
        _output.WriteLine($"  S2（快路开/守卫开）：墙钟 {s2.WallSeconds,8:F1}s  P2 {s2.Phase2Ms,8}ms  "
                          + $"单Op扫描 {s2.ScanCalls,6} 次 / {s2.ScannedTasks,10} 元素  候选 {s2.Candidates,6}  "
                          + $"快路跳过 {s2.SkippedNoPi,6}  Merge {s2.MergeAttempts,6}");

        if (s2.WallSeconds > 0)
        {
            _output.WriteLine($"  ⇒ S1/S2 墙钟比 = {s1.WallSeconds / s2.WallSeconds:F2}× "
                              + "（S1→S2 的独立增量；**不是** S0→S2 的总收益）");
        }

        _output.WriteLine($"  FinalTask：S1={s1.FinalTasks}  S2={s2.FinalTasks}"
                          + "  （⚠ 两个数不同**不代表**业务等价性结论 —— 本快照只许做吞吐对照）");

        Assert.True(s1.Success, s1.Error);
        Assert.True(s2.Success, s2.Error);
    }

    private sealed record Measurement(
        string Label, bool Success, string? Error, double WallSeconds, long Phase2Ms,
        long ScanCalls, long ScannedTasks, long Candidates, long SkippedNoPi,
        long MergeAttempts, long PhysicalFeasible, long FinallyAdopted, int FinalTasks,
        // ── 2026-11-10 B-01：`LandingRun` 内部分账（纯观测）──
        //   `Counters.HotspotMs(...)` 返回 `double`（内部是 ticks→ms 的浮点换算）
        double LandingMs, double ShareRefillMs, double OccRefillMs,
        double OuterAdoptMs, double RunDirectMs, double PureAppendCheckMs, double IndexRebuildMs, double TraceRefillMs,
        double AdoptReconcileMs, double RouteRefillMs);

    private async Task<Measurement> RunAsync(string jsonPath, int limit, string fastPath, string guard, string label)
    {
        _output.WriteLine($"[A6] {label} 开始：{DateTime.Now:HH:mm:ss}");

        var request = Load(jsonPath);
        var originalCount = request.LogicalProductionDemands.Count;

        // `LogicalProductionDemands` 是 `init` 属性 —— `init` 在 IL 上就是普通 setter，反射可写。
        //   用反射而不是复制整个对象：`DomainSolveRequest` 有 30+ 个字段，手抄一遍必然漏一个。
        var limited = request.LogicalProductionDemands.Take(limit).ToList();
        typeof(DomainSolveRequest)
            .GetProperty(nameof(DomainSolveRequest.LogicalProductionDemands), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(request, limited);

        _output.WriteLine($"[A6] {label} 需求已截断：原始 {originalCount} → 本轮 {limited.Count}");

        PhaseTwoInitialScheduler.OverrideAnchorModeForTest("PI_ONLY");
        PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(fastPath, guard);
        try
        {
            using var scope = SolverDiagnostics.BeginScope();
            var c = scope.Counters;
            // 全量单档 70+ 分钟：挂上逐拍采样，日志里能看到「处理到第几条需求」这类推进读数。
            using var sampler = RealDomainFullRunTest.SolverLiveSampler.Start(
                c, request.PlanVersionId, _output);
            var sw = Stopwatch.StartNew();
            DomainSolveResult? result;
            try
            {
                result = await new FiniteCapacitySolver().SolveAsync(request, CancellationToken.None);
            }
            finally
            {
                sw.Stop();
            }

            var m = new Measurement(
                label,
                result?.Success ?? false,
                result?.ErrorMessage,
                sw.Elapsed.TotalSeconds,
                c.Phase2Ms,
                c.SingleOpScanCalls,
                c.SingleOpScannedTaskCount,
                c.SingleOpCandidateCount,
                c.SingleOpMergeSkippedNoPi,
                c.MergeAttempts,
                c.MergePhysicalFeasible,
                c.MergeFinallyAdopted,
                result?.FinalTasks?.Count ?? 0,
                // B-01 分账：LandingRun 总量 vs 其中两张表的整表回填
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingRun),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingShareRefill),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingOccRefill),
                c.HotspotMs(SolverDiagnostics.Hotspot.OuterAdopt),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingRunDirect),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingPureAppendCheck),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingIndexRebuild),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingTraceRefill),
                c.HotspotMs(SolverDiagnostics.Hotspot.BatchGroupAdoptReconcile),
                c.HotspotMs(SolverDiagnostics.Hotspot.LandingRouteRefill));

            _output.WriteLine($"[A6] {label} 结束：{DateTime.Now:HH:mm:ss}，墙钟 {m.WallSeconds:F1}s，"
                              + $"Success={m.Success}，FinalTask={m.FinalTasks}");

            // ── 2026-11-10 B-01（0号位 §四 B-01）：把 LandingRun 这个 71.6% 的黑盒切开 ──
            _output.WriteLine($"[B-01] {label} LandingRun 分账：");
            _output.WriteLine($"        LandingRun 合计   = {m.LandingMs,10:F0} ms  （占 P2 {Pct(m.LandingMs, m.Phase2Ms)}）");
            _output.WriteLine($"        ├ 份额整表回填   = {m.ShareRefillMs,10:F0} ms  （占 LandingRun {Pct(m.ShareRefillMs, m.LandingMs)}）");
            _output.WriteLine($"        └ 占用整表回填   = {m.OccRefillMs,10:F0} ms  （占 LandingRun {Pct(m.OccRefillMs, m.LandingMs)}）");
            var bothRefill = m.ShareRefillMs + m.OccRefillMs;
            _output.WriteLine($"        两张表回填合计   = {bothRefill,10:F0} ms  （占 LandingRun {Pct(bothRefill, m.LandingMs)}）");
            _output.WriteLine($"        分支A 采纳(OuterAdopt) = {m.OuterAdoptMs,10:F0} ms  （占 LandingRun {Pct(m.OuterAdoptMs, m.LandingMs)}）");
            _output.WriteLine($"        分支B 真跑(RunBatchPlan) = {m.RunDirectMs,8:F0} ms  （占 LandingRun {Pct(m.RunDirectMs, m.LandingMs)}）");
            _output.WriteLine($"        前缀比对(PureAppendCheck) = {m.PureAppendCheckMs,8:F0} ms  （占 LandingRun {Pct(m.PureAppendCheckMs, m.LandingMs)}）");
            _output.WriteLine($"        索引重建(IndexRebuild)   = {m.IndexRebuildMs,8:F0} ms  （占 LandingRun {Pct(m.IndexRebuildMs, m.LandingMs)}）");
            _output.WriteLine($"        TraceNotes 回填          = {m.TraceRefillMs,8:F0} ms  （占 LandingRun {Pct(m.TraceRefillMs, m.LandingMs)}）");
            _output.WriteLine($"        索引对齐(AdoptReconcile) = {m.AdoptReconcileMs,8:F0} ms  （占 LandingRun {Pct(m.AdoptReconcileMs, m.LandingMs)}）");
            _output.WriteLine($"        选路回填(RouteRefill)    = {m.RouteRefillMs,8:F0} ms  （占 LandingRun {Pct(m.RouteRefillMs, m.LandingMs)}）");
            // ── 2026-11-10 B-02（0号位 §四 B-02）：Phase5 分账 ──
            //   0号位 原话：「不要将 472.3 秒一概称为『压缩空隙』耗时」。
            //   全量复测里 Phase5 已从 472 s 涨到 923 s，与 Phase2 并列最大 ⇒ 必须先拆开看。
            //   这些槽**早就存在**（`SolverDiagnostics.Hotspot.Phase5*`），此前只是没人输出。
            var p5Items = new (string Name, int Slot, bool Child)[]
            {
                ("CompactGaps（压缩空隙）",         SolverDiagnostics.Hotspot.Phase5CompactGaps, false),
                ("  +- CompactPrep",               SolverDiagnostics.Hotspot.Phase5CompactPrep, true),
                ("  +- CompactIndex",              SolverDiagnostics.Hotspot.Phase5CompactIndex, true),
                ("  +- CompactLoop",               SolverDiagnostics.Hotspot.Phase5CompactLoop, true),
                ("OptimizeSetupSequences（换型）",  SolverDiagnostics.Hotspot.Phase5SetupSeq, false),
                ("BuildTerminalContributions",     SolverDiagnostics.Hotspot.Phase5Terminal, false),
                ("GenerateDependencies（依赖）",    SolverDiagnostics.Hotspot.Phase5Deps, false),
                ("Unsched 未排量（合计）",          SolverDiagnostics.Hotspot.Phase5Unsched, false),
                ("  +- 扫 Phase2",                 SolverDiagnostics.Hotspot.Phase5UnschedP2, true),
                ("  +- 扫 Phase4",                 SolverDiagnostics.Hotspot.Phase5UnschedP4, true),
                ("  +- 聚合",                      SolverDiagnostics.Hotspot.Phase5UnschedAgg, true),
                ("AllocationShares（份额）",        SolverDiagnostics.Hotspot.Phase5Shares, false),
                ("Validate（硬校验）",              SolverDiagnostics.Hotspot.Phase5Validate, false),
                ("CalendarScan（日历扫描）",        SolverDiagnostics.Hotspot.Phase5CalendarScan, false),
                ("FinalDiagnose（最终诊断）",       SolverDiagnostics.Hotspot.Phase5FinalDiagnose, false),
                ("ResultAssembly（结果组装）",      SolverDiagnostics.Hotspot.Phase5ResultAssembly, false),
                // ⚠ 2026-11-11 更正：`StageTimingBuild` **嵌套在** `ResultAssembly` 内
                //   （实测两者数值几乎相等：29,137 vs 29,136）⇒ 属**子项**，
                //   此前误标为顶层项，导致「顶层合计」多算一遍（80.2 s vs 段时长 51.1 s）。
                ("  +- StageTimingBuild",          SolverDiagnostics.Hotspot.Phase5StageTimingBuild, true),
            };
            _output.WriteLine($"[P5] {label} Phase5 分账：");
            double p5Top = 0;
            foreach (var (nm, slot, child) in p5Items)
            {
                var v = c.HotspotMs(slot);
                if (!child) p5Top += v;
                _output.WriteLine($"        {nm,-34} = {v,10:F0} ms");
            }
            _output.WriteLine($"        == 顶层项合计（不含子项）   = {p5Top,10:F0} ms");

            // ── 2026-11-11 FULL-02 真实路径验证（0号位《夜间FULL计算整改与联合回归任务书 v1.0》§2.B）──
            //   任务书要求「最终 Task 及 MES 执行身份保留实际 RouteCode/PathId，不得回退 DEFAULT/1」。
            //   这里只**观测**产出里的真实取值分布，不改任何判定。
            if (result?.FinalTasks is { Count: > 0 } ft)
            {
                var byRoute = ft
                    .GroupBy(t => t.RouteCode ?? "(null)")
                    .OrderByDescending(g => g.Count())
                    .Take(8)
                    .Select(g => $"{g.Key}={g.Count()}");
                var defaultRoute = ft.Count(t => string.Equals(t.RouteCode, "DEFAULT", StringComparison.Ordinal));
                var nonOnePath = ft.Count(t => t.PathId != 1);
                _output.WriteLine($"[FULL-02] {label} FinalTask RouteCode 分布（Top8）：{string.Join(" | ", byRoute)}");
                _output.WriteLine($"[FULL-02] {label} RouteCode==DEFAULT 的 Task：{defaultRoute} / {ft.Count}；PathId != 1 的 Task：{nonOnePath}");
            }

            // ── 2026-11-11 任务书 §3「联合 FULL Run 状态」：未排量 + 原因码缺口 ──
            //   任务书 FULL-04 关心「未排是否有法定原因码」，这里一并量出来（只观测）。
            if (result is not null)
            {
                var un = result.UnscheduledTasks ?? Array.Empty<UnscheduledTaskResult>();
                var atQuery = request.LogicalProductionDemands.Count;
                var unQty = 0m;
                for (var ui = 0; ui < un.Count; ui++) unQty += un[ui].UnscheduledQty;
                var withReason = 0;
                for (var ui = 0; ui < un.Count; ui++)
                {
                    if (!string.IsNullOrWhiteSpace(un[ui].Reason)) withReason++;
                }
                _output.WriteLine($"[RUN] {label} 未排需求 {un.Count} / {atQuery}，未排量合计 {unQty:F3}");
                _output.WriteLine($"[RUN] {label} 其中带原因码 {withReason}，**空原因 {un.Count - withReason}**");

                // FULL-04：不仅要「非空」，还要看填的是不是法定码。给出取值分布（Top10）。
                var reasonTop = un
                    .Where(x => !string.IsNullOrWhiteSpace(x.Reason))
                    .GroupBy(x => x.Reason)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Key}={g.Count()}");
                _output.WriteLine($"[RUN] {label} Reason 取值分布（Top10）：{string.Join(" | ", reasonTop)}");

                // ── 2026-11-11 需求级数量闭合诊断（对应 2号位 FAMILY_CYL「25.5 vs 51」）──
                //   目的：验证「部分排程」机制是否在别的域也存在 —— 即同一条需求只排出一部分 Task。
                //   判据与 Phase5 硬校验同源：Σ(Task.Quantity by SourceDraftId) vs demand.NetOutputQty。
                if (result.FinalTasks is { Count: > 0 } allTasks)
                {
                    var qtyByDemand = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    for (var ti = 0; ti < allTasks.Count; ti++)
                    {
                        var t = allTasks[ti];
                        var dk = t.SourceDraftId;
                        if (string.IsNullOrEmpty(dk)) continue;
                        qtyByDemand.TryGetValue(dk, out var acc);
                        qtyByDemand[dk] = acc + t.Quantity;
                    }

                    var partial = 0;
                    var exact = 0;
                    var over = 0;
                    var missingDemand = 0;
                    foreach (var kv in qtyByDemand)
                    {
                        var dm = request.LogicalProductionDemands.FirstOrDefault(d => d.LogicalDemandKey == kv.Key);
                        if (dm is null) { missingDemand++; continue; }
                        var diff = kv.Value - dm.NetOutputQty;
                        if (Math.Abs(diff) <= 0.001m) exact++;
                        else if (diff > 0) over++;
                        else partial++;
                    }
                    _output.WriteLine($"[CLOSURE] {label} 有 Task 的需求 {qtyByDemand.Count} 个："
                        + $"完全闭合 {exact}｜**部分排程 {partial}**｜超额 {over}｜查不到需求 {missingDemand}");
                }
            }

            var accounted = bothRefill + m.OuterAdoptMs + m.RunDirectMs
                            + m.PureAppendCheckMs + m.IndexRebuildMs
                            + m.TraceRefillMs + m.AdoptReconcileMs + m.RouteRefillMs;
            _output.WriteLine($"        ── 已归因合计     = {accounted,10:F0} ms  （占 LandingRun {Pct(accounted, m.LandingMs)}）");
            _output.WriteLine($"        ── 仍未归因       = {m.LandingMs - accounted,10:F0} ms  （占 LandingRun {Pct(m.LandingMs - accounted, m.LandingMs)}）");
            return m;
        }
        finally
        {
            PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(null, null);
            PhaseTwoInitialScheduler.OverrideAnchorModeForTest(null);
        }
    }

    /// <summary>占比文本；分母非正时给 <c>—</c>，避免出现 Infinity/NaN 干扰读数。</summary>
    private static string Pct(double part, double whole)
        => whole > 0 ? $"{part * 100.0 / whole:F1}%" : "—";

    /// <summary>流式反序列化（与 `SolverReplayTest` 同一组选项，读写对称）。</summary>
    private static DomainSolveRequest Load(string jsonPath)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };

        using var fs = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        return System.Text.Json.JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
               ?? throw new InvalidOperationException($"入参 JSON 反序列化得到 null：{jsonPath}");
    }
}