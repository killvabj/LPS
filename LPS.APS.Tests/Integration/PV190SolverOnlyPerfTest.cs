using System.Diagnostics;
using System.Globalization;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Models.Scheduling;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Scheduling.Solvers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 · PV190 只跑 Phase1~5】拿**真实 PV 的装载结果**喂求解器，只测求解那一段，**不落库**。
///
/// <para><b>与 <see cref="SolverReplayTest"/> 的分工</b>：
/// 那个吃的是**离线捕获的 JSON**（要先跑一轮全流程并开 <c>APS_CAPTURE_SOLVE_REQUEST=1</c> 才拿得到，
/// 而那一轮必然落库）；本入口**直接从库里装载**（走 2号位 的
/// <see cref="PeggingOrchestrator.RunPeggingOnlyAsync"/> —— 与生产共用同一段
/// <c>BuildSolveRequestAsync</c>，差异只有一条：组出请求就返回，**不调 Solver、不落库**），
/// 拿到 <see cref="DomainSolveRequest"/> 后**自己直调**求解器。
/// ⇒ 一轮改动验证成本 = 装载 + Phase1~5，**不含落库那一段**（PV190 落库曾占 470s，与本轮要优化的东西无关）。</para>
///
/// <para><b>为什么不改生产代码</b>：落库发生在 `PeggingOrchestrator`（`Application`），
/// 那是 2号位 的辖区，也没有「跳过落库」的开关。本入口绕开它 —— 只调装载、不调编排。</para>
///
/// <para><b>保真度</b>：`SchedulingContext` 的策略包版本与 `DataCutoffTime` 取自该 PV 的
/// `ScheduleRun` 冻结值（和生产 `LoadSchedulingContextAsync` 同源），`SnapshotAt` 用同一个
/// `DataCutoffTime`（对齐 `BuildPeggingRequest` 的 P0-03 口径），计划期取自 `PlanVersion`。
/// `IsCandidate=false` / `BasePlanVersionId=null` = 夜间 FULL 口径（`ExecutionConstraints` 恒空）。</para>
///
/// <para><b>开门</b>：
/// <code>APS_SOLVER_ONLY_RUN=1 APS_SOLVER_ONLY_PV_ID=190 dotnet test LPS.APS.Tests --filter FullyQualifiedName~PV190SolverOnlyPerfTest</code>
/// 默认跳过 —— 本入口会**读生产库**（只读，零写入），绝不能因为一次宽泛的 <c>dotnet test</c> 被误触发。</para>
///
/// <para><b>不改任何生产代码</b>：本文件只 <c>new FiniteCapacitySolver()</c> 并按接口调用。</para>
/// </summary>
public class PV190SolverOnlyPerfTest
{
    private const string GateEnvVar = "APS_SOLVER_ONLY_RUN";

    private readonly ITestOutputHelper _output;

    public PV190SolverOnlyPerfTest(ITestOutputHelper output) => _output = output;

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_SOLVER_ONLY_PV_ID"), out var v) && v > 0 ? v : 190;

    [SkippableFact(DisplayName = "1号位: PV190 只跑 Phase1~5（真实装载，不落库）")]
    public async Task RunSolverOnlyAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"求解器单跑默认关闭（会读生产库装载真实载荷）。设 {GateEnvVar}=1 显式开启。");

        var planVersionId = PlanVersionId;

        // ── 超时：与全流程同一套口径（APS_RUN_TIMEOUT_MIN，未设=不限时）────────────────
        using var runCts = RealDomainFullRunTest.CreateRunCancellationCore(
            Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var timeoutMinutes);
        if (timeoutMinutes > 0d)
        {
            _output.WriteLine($"[S] 总超时 = {timeoutMinutes:0.##} 分钟（APS_RUN_TIMEOUT_MIN，覆盖装载+求解）");
        }

        var ct = runCts.Token;

        using var provider = BuildProductionProvider();
        var conn = provider.GetRequiredService<DatabaseConnectionManager>();
        var schedulerOrchestrator = provider.GetRequiredService<SchedulingOrchestrator>();
        var peggingOrchestrator = (PeggingOrchestrator)provider.GetRequiredService<IPeggingOrchestrator>();

        // ── 1. PV 身份：计划期 / 域 / 归属 Run 的冻结策略包与数据截止 ───────────────────
        var pv = await conn.QueryFirstOrDefaultAsync<PvRow>(
            @"SELECT pv.DomainKey, pv.PlanHorizonStart, pv.PlanHorizonEnd, pv.SourceScheduleRunId,
                     sr.RunType, sr.StrategyProfileVersionId, sr.DataCutoffTime
              FROM PlanVersion pv
              LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
              WHERE pv.Id = @p",
            new { p = planVersionId }, db: DatabaseId.APS);

        Skip.If(pv is null, $"PlanVersion {planVersionId} 不存在。");

        var runType = string.IsNullOrWhiteSpace(pv!.RunType) ? "FULL_SCHEDULE" : pv.RunType!;
        var strategyVersionId = pv.StrategyProfileVersionId
            ?? await ResolveDefaultStrategyVersionIdAsync(conn, runType, ct);
        var dataCutoffTime = pv.DataCutoffTime ?? DateTime.Now;

        _output.WriteLine($"[S] PlanVersionId={planVersionId}, Domain='{pv.DomainKey}', "
            + $"SourceScheduleRunId={pv.SourceScheduleRunId}, RunType={runType}, "
            + $"策略包版本={strategyVersionId}, DataCutoff={dataCutoffTime:yyyy-MM-dd HH:mm:ss}");
        _output.WriteLine($"[S] 计划期 {pv.PlanHorizonStart:yyyy-MM-dd HH:mm} ~ {pv.PlanHorizonEnd:yyyy-MM-dd HH:mm}");

        var orderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = planVersionId }, db: DatabaseId.APS)).ToList();

        Skip.If(orderIds.Count == 0, $"PlanVersion {planVersionId} 下没有订单 —— 没有可装载的需求。");

        _output.WriteLine($"[S] 订单数 {orderIds.Count}（全量口径，不收窄 BOM 宇宙）");

        // ── 2. 装载（与生产同源，不落库、不调 Solver）────────────────────────────────
        //   `LoadResourcesAndCalendarAsync` 是生产内部实现，借用它把 Resources / CalendarSlots
        //   填成与生产同一份，而不是自己复刻 SQL（见 LoadToPosition1AuditTest 的同款说明）。
        var schedulingContext = new SchedulingContext
        {
            StrategyProfileVersionId = strategyVersionId,
            DataCutoffTime = dataCutoffTime,
            PlanHorizonStart = pv.PlanHorizonStart,
            PlanHorizonEnd = pv.PlanHorizonEnd
        };

        var loadSw = Stopwatch.StartNew();
        await schedulerOrchestrator.LoadResourcesAndCalendarAsync(schedulingContext, ct);

        var peggingRequest = new PeggingExecutionRequest
        {
            PlanVersionId = planVersionId,
            DomainKey = pv.DomainKey ?? string.Empty,
            OrderIds = orderIds,
            // P0-03 口径：供给快照基准 = Run 冻结的 DataCutoffTime
            SnapshotAt = dataCutoffTime,
            FrozenWindowStart = dataCutoffTime,
            FrozenWindowEnd = dataCutoffTime.AddHours(2),
            AllowCrossFactory = false,
            DefaultStrategy = PeggingStrategyType.FIFO,
            MaxBomDepth = 10,
            TimeoutSeconds = 300,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            SchedulingContext = schedulingContext
        };

        var payload = await peggingOrchestrator.RunPeggingOnlyAsync(peggingRequest, ct);
        loadSw.Stop();

        var request = payload.SolveRequest;
        var demandCount = request.LogicalProductionDemands?.Count ?? 0;

        _output.WriteLine("");
        _output.WriteLine($"[S] 装载完成：{loadSw.Elapsed.TotalSeconds:F1}s"
            + $"（Pegging 回路 {payload.PeggingMs}ms / 装载合计 {payload.TotalMs}ms）");
        _output.WriteLine($"[S] 载荷规模：需求 {demandCount} / 资源 {request.Resources?.Count ?? 0} / "
            + $"日历槽 {request.CalendarSlots?.Count ?? 0} / 路由工序 {request.RoutingOperations?.Count ?? 0} / "
            + $"执行约束 {request.ExecutionConstraints?.Count ?? 0}"
            // 2026-11-04：部门上下文条数 —— 判「全量缺部门」是数据空了还是代码问题。
            + $" / **部门上下文 {request.MaterialStageDepartmentContexts?.Count ?? 0}**"
            + $" / Stage链 {request.StageSequenceChains?.Count ?? 0}");

        Skip.If(demandCount == 0, "装载出来的载荷一条需求都没有 —— 不是可测的输入。");

        // 2026-11-02：**输入行序指纹**（纯观测，测试侧，不动生产代码）。
        //   目的：证明「同一份数据、两轮装载出来的行序是否相同」。
        //   查这件事的理由：`PeggingOrchestrator.cs:9414-9445` 那三条装载 SQL 都**没有 ORDER BY**，
        //   而求解器依赖输入行序（需求迭代按上游给定顺序、工序图按行序插入）。
        //   指纹按**位置**哈希 ⇒ 行序一变值就变；再落一份逐行序清单，可直接 diff 出哪几行挪了位。
        DumpInputFingerprint(planVersionId, request!);

        // ── 2.5 入参捕获（2026-11-02 增）：拿到 JSON 就不必每次再连库跑 30 分钟 ──────────
        //   开门：`APS_CAPTURE_SOLVE_REQUEST=1`；落点与逐拍 CSV 同目录（`APS_RUN_LOG_DIR` 可覆盖）。
        //   ⚠ 单域可达 1 GB+ ⇒ 跑批前务必把 `APS_RUN_LOG_DIR` 指到库外目录，别落进 `Database/Scripts`。
        using var solveRequestCapture = RealDomainFullRunTest.SolveRequestCapture.Start(planVersionId, _output);

        // ── 3. 只跑 Phase1~5：直调求解器，**不落库** ────────────────────────────────
        using var solverScope = SolverDiagnostics.BeginScope();
        using var sampler = RealDomainFullRunTest.SolverLiveSampler.Start(
            solverScope.Counters, planVersionId, _output);

        var solveSw = Stopwatch.StartNew();
        DomainSolveResult? result = null;
        var cancelled = false;

        try
        {
            result = await new FiniteCapacitySolver().SolveAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            solveSw.Stop();
            sampler.Stop();
            DumpPhases(solverScope.Counters, solveSw.Elapsed);
        }

        if (cancelled)
        {
            _output.WriteLine($"[S] **超时取消**：{solveSw.Elapsed.TotalMinutes:0.#} 分钟未返回。"
                + " 卡在哪一 Phase 看上面 CSV 逐拍读数（每拍都在涨的 hs_* 就是卡点）。");
            return;
        }

        _output.WriteLine("");
        _output.WriteLine($"[S] 求解返回：Success={result!.Success}, FinalTask={result.FinalTasks.Count}, "
            + $"未排需求={result.UnscheduledTasks.Count}, 墙钟={solveSw.Elapsed.TotalSeconds:F1}s");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            var firstLine = result.ErrorMessage.Split('\n')[0];
            _output.WriteLine($"[S] ErrorMessage 首行={firstLine}");
        }

        _output.WriteLine("");
        _output.WriteLine("[S] 本轮**未落库**：没有写 [Task] / PeggingDraft / AllocationTaskShare / "
            + "ScheduleExplanationFact / PI Position 任何一行。插库是 2号位 的段，不在本次测量内。");

        // ── 4. 未排原因分组（2026-11-02 增）────────────────────────────────────────
        //   2号位《FAMILY_X 未排 9835 条…请出计算结论 v1.0》§四 要甲/乙/丙。
        //   泛化原因「Phase 2 初始排程失败，Phase 4 修复未成功」本身不含信息；
        //   真正能分甲乙丙的是 Phase2 那 15 个 `P2_xxx` 出口码 —— 它们由
        //   `PhaseFiveCompression.ReportUnscheduled` 写进 `SolveTraceNotes.Message`（与 `DraftId` 同键域）。
        //   ⇒ 这里把「Reason × P2 码」逐组摊开，逐条明细另落 CSV。
        // 2026-11-04：把 STAGE_TIMING_PROBE 的**正文**打出来（此前只打计数，看不到数字）。
        foreach (var n in (result?.SolveTraceNotes ?? Array.Empty<SolveTraceNote>()))
        {
            if (n.ReasonCode == "STAGE_TIMING_PROBE")
            {
                _output.WriteLine($"[S] {n.Message}");
            }
        }

        DumpUnscheduledBreakdown(planVersionId, result!);

        // 2026-11-02：逐需求排程条数落盘（确定性排查用，纯观测）。
        DumpTaskCountsByDemand(planVersionId, result!);

        // 2026-11-03：交接规则 / PI Stage 上下文的**输入形状与命中情况**（纯观测，一次跑批答四个问题）。
        DumpHandoffAndPiStageProbe(request!, solverScope.Counters);

        // 不判成功/失败：本入口是**测速**工装，Success=false 时的耗时同样是有效读数。
    }

    /// <summary>
    /// 把未排原因按「`Reason` × Phase2 出口码 `P2_xxx`」拆开打印，并落逐条 CSV。
    ///
    /// <para><b>三个桶各代表什么</b>（判 甲/乙/丙 时就靠这一行行）：</para>
    /// <list type="bullet">
    ///   <item>`P2_NO_SLOT` —— 合格资源全排不进；其后的成因①/②/③由 `DescribeNoSlotCause` 写在正文里
    ///     （①日历窗不够 ②窗内无空档 ③缺 CapacityFactor）。②是**真产能不足（甲）**。</item>
    ///   <item>`P2_NO_ELIGIBLE_RESOURCE` —— 五元键 `(MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode)`
    ///     一条都没命中 ⇒ 输入/键域问题（乙），不是产能。</item>
    ///   <item>`P2_NO_ROUTING` / `P2_NO_OPERATIONS` / `P2_FIXED_PATH_*` —— 建不出图或裁不出工序（乙）。</item>
    /// </list>
    ///
    /// <para><b>`(无明细)` 桶</b>：走到泛化 `else` 却抽不到 `P2_` 码，说明该需求在 Phase2 出口没留痕
    ///   —— 这本身是**我方留痕缺口**，按丙处理，不往外推。</para>
    /// </summary>
    private void DumpUnscheduledBreakdown(long planVersionId, DomainSolveResult result)
    {
        var tasks = result.UnscheduledTasks;
        if (tasks is null || tasks.Count == 0)
        {
            _output.WriteLine("[S] 未排清单为空，无需分组。");
            return;
        }

        // ⚠ 索引键必须是 **(Key, ReasonCode) 二元组**，不能只用 Key。
        //   同一需求键下会挂**多条** `SolveTraceNote`：`ReportUnscheduled` 写的是「未排原因正文」（`P2_xxx`），
        //   而 `StageTimingNodeBuilder` 还会给同一需求写「完成时间可信度 G-3」的注记。
        //   2026-11-02 第一轮就是只按 Key 建索引 ⇒ 后写的 G-3 注记把 `P2_xxx` 覆盖掉，
        //   9,836 条全部误报 `(无明细)`。`ReportUnscheduled` 处 `ReasonCode` 与 `UnscheduledTaskResult.Reason`
        //   是同一个变量（`PhaseFiveCompression.cs:214-221`）⇒ 用二元组能**精确**取回。
        var messageByKey = new Dictionary<(string Key, string ReasonCode), string>();
        foreach (var note in result.SolveTraceNotes ?? Array.Empty<SolveTraceNote>())
        {
            if (string.IsNullOrEmpty(note.Key) || string.IsNullOrWhiteSpace(note.Message)) continue;
            messageByKey[(note.Key, note.ReasonCode ?? string.Empty)] = note.Message!;
        }

        // 自检：把 notes 按 ReasonCode 分个组打出来 —— 万一哪天键域又漂，这一行会立刻露出来。
        _output.WriteLine($"[S] SolveTraceNotes 共 {result.SolveTraceNotes?.Count ?? 0} 条，按 ReasonCode 计数：");
        foreach (var g in (result.SolveTraceNotes ?? Array.Empty<SolveTraceNote>())
                     .GroupBy(n => n.ReasonCode ?? "(null)", StringComparer.Ordinal)
                     .OrderByDescending(g => g.Count())
                     .Take(12))
        {
            _output.WriteLine($"[S]        {g.Key,-52} {g.Count(),8} 条");
        }

        static string P2CodeOf(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "(无明细)";
            var m = System.Text.RegularExpressions.Regex.Match(message, @"P2_[A-Z_]+");
            return m.Success ? m.Value : "(无明细)";
        }

        var rows = new List<(string Reason, string P2, string Key, decimal Qty, string Detail)>(tasks.Count);
        foreach (var t in tasks)
        {
            messageByKey.TryGetValue((t.DraftId, t.Reason ?? string.Empty), out var detail);
            rows.Add((t.Reason, P2CodeOf(detail), t.DraftId, t.UnscheduledQty, detail ?? string.Empty));
        }

        _output.WriteLine("");
        _output.WriteLine($"[S] ── 未排原因 × Phase2 出口码 分组（共 {rows.Count} 条，量 {rows.Sum(r => r.Qty):0.####}）──");
        foreach (var byReason in rows.GroupBy(r => r.Reason)
                                     .OrderByDescending(g => g.Sum(r => r.Qty))
                                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            _output.WriteLine($"[S]   「{byReason.Key}」｜{byReason.Count()} 条｜量 {byReason.Sum(r => r.Qty):0.####}");
            foreach (var byP2 in byReason.GroupBy(r => r.P2)
                                         .OrderByDescending(g => g.Sum(r => r.Qty))
                                         .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                _output.WriteLine($"[S]        {byP2.Key,-30} {byP2.Count(),7} 条   量 {byP2.Sum(r => r.Qty),16:0.####}");
            }
        }

        try
        {
            var dir = RealDomainFullRunTest.ResolveRunLogDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var csvPath = System.IO.Path.Combine(
                dir, $"PV{planVersionId}_unsched_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            static string Q(string? s) => "\"" + (s ?? string.Empty)
                .Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("DraftId,Reason,P2Code,UnscheduledQty,Detail");
            foreach (var r in rows)
            {
                sb.Append(Q(r.Key)).Append(',')
                  .Append(Q(r.Reason)).Append(',')
                  .Append(Q(r.P2)).Append(',')
                  .Append(r.Qty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                  .AppendLine(Q(r.Detail));
            }

            System.IO.File.WriteAllText(csvPath, sb.ToString(), System.Text.Encoding.UTF8);
            _output.WriteLine($"[S] 逐条明细已落盘：{csvPath}（{rows.Count} 行）");
        }
        catch (Exception ex)
        {
            // 落盘失败不影响读数：分组已在控制台打出来了。
            _output.WriteLine($"[S] 逐条明细落盘失败（已吞）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 2026-11-02｜**输入行序指纹**（纯观测，测试侧）。
    ///
    /// 要回答的问题：同一份 PV、同一份数据，两轮装载出来的行序是否相同？
    /// 背景：`PeggingOrchestrator.cs` 的三条装载 SQL（RoutingOperation / RoutingDependency /
    ///   OperationResourceEligibility）**均无 `ORDER BY`**；而求解器依赖输入行序 ——
    ///   需求迭代按上游给定顺序（`PhaseTwoInitialScheduler.cs:434`「不再做任何重排」）、
    ///   工序图按行序插入（`RoutingGraph.Operations` 的插入序 = 行序）。
    ///
    /// 指纹口径：对每个序列按**位置**做 FNV-1a —— 同一个元素换到别的位置，指纹立刻变。
    ///   `Xor` 才是顺序无关的累加，这里**故意用位置相关**，否则「行序变了」根本看不出来。
    /// 同时落一份逐行序清单（`Kind,Seq,Key`），两轮直接 diff 就能点名哪几行挪了。
    /// </summary>
    private void DumpInputFingerprint(long planVersionId, DomainSolveRequest request)
    {
        try
        {
            static ulong Mix(ulong h, string s)
            {
                foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
                return h;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Kind,Seq,Key");

            ulong hDemand = 1469598103934665603UL;
            var demands = request.LogicalProductionDemands ?? Array.Empty<LogicalProductionDemand>();
            for (int i = 0; i < demands.Count; i++)
            {
                var k = demands[i].LogicalDemandKey ?? string.Empty;
                hDemand = Mix(hDemand, k);
                sb.Append("DEMAND,").Append(i).Append(",\"")
                  .Append(k.Replace("\"", "\"\"")).AppendLine("\"");
            }

            ulong hOps = 1469598103934665603UL;
            var ops = request.RoutingOperations!;
            for (int i = 0; i < (ops?.Count ?? 0); i++)
            {
                var o = ops[i];
                var k = $"{o.MaterialId}|{o.ProductionDepartmentId}|{o.RouteCode}|{o.PathId}|{o.StageCode}|{o.OperationCode}";
                hOps = Mix(hOps, k);
                sb.Append("ROUTING_OP,").Append(i).Append(",\"")
                  .Append(k.Replace("\"", "\"\"")).AppendLine("\"");
            }

            ulong hElig = 1469598103934665603UL;
            var elig = request.OperationResourceEligibility!;
            for (int i = 0; i < (elig?.Count ?? 0); i++)
            {
                var e = elig[i];
                var k = $"{e.MaterialId}|{e.ProductionDepartmentId}|{e.RouteCode}|{e.PathId}|{e.OperationCode}|{e.ResourceId}";
                hElig = Mix(hElig, k);
                sb.Append("ELIGIBILITY,").Append(i).Append(",\"")
                  .Append(k.Replace("\"", "\"\"")).AppendLine("\"");
            }

            _output.WriteLine($"[S] 输入行序指纹：需求 {demands.Count} 行 / 工序 {ops.Count} 行 / 资格 {elig.Count} 行");
            _output.WriteLine($"[S]   DEMAND      顺序指纹 = 0x{hDemand:X16}");
            _output.WriteLine($"[S]   ROUTING_OP  顺序指纹 = 0x{hOps:X16}");
            _output.WriteLine($"[S]   ELIGIBILITY 顺序指纹 = 0x{hElig:X16}");
            _output.WriteLine("[S]   （同一份数据两轮此值不同 ⇒ 装载行序不稳；三者全同 ⇒ 行序稳定，漂移另有原因）");

            var dir = RealDomainFullRunTest.ResolveRunLogDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var csvPath = System.IO.Path.Combine(
                dir, $"PV{planVersionId}_inputseq_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(csvPath, sb.ToString(), System.Text.Encoding.UTF8);
            _output.WriteLine($"[S] 输入行序清单已落盘：{csvPath}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[S] 输入行序指纹失败（已吞）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 2026-11-02｜**逐需求排程条数**落盘（纯观测）。
    ///
    /// 起因：同一份输入、同一份代码，多轮跑出 `FinalTask = 1,084,606 / 1,084,326 / 1,085,066 / 1,114,448`，
    ///   而**未排侧（条数、量）逐位相同** ⇒ 差异只可能落在「某几条需求排出的 Task 条数」上。
    ///   只打总数看不出是哪几条，故按 `SourceDraftId`（= 需求键）聚合后整表落盘；
    ///   两轮同名文件按 `SourceDraftId` 逐行 diff，即可点名漂移的需求。
    /// 列：`SourceDraftId,TaskCount,Quantity,PlannedProcessQty,MinStart,MaxEnd`。
    /// </summary>
    private void DumpTaskCountsByDemand(long planVersionId, DomainSolveResult result)
    {
        try
        {
            var tasks = result.FinalTasks ?? Array.Empty<FinalTaskDraft>();
            var agg = tasks
                .GroupBy(t => t.SourceDraftId ?? string.Empty, StringComparer.Ordinal)
                .Select(g => new
                {
                    Key = g.Key,
                    Count = g.Count(),
                    Qty = g.Sum(t => t.Quantity),
                    ProcQty = g.Sum(t => t.PlannedProcessQty),
                    MinStart = g.Min(t => t.PlannedStartTime),
                    MaxEnd = g.Max(t => t.PlannedEndTime)
                })
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToList();

            var dir = RealDomainFullRunTest.ResolveRunLogDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var csvPath = System.IO.Path.Combine(
                dir, $"PV{planVersionId}_taskcount_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SourceDraftId,TaskCount,Quantity,PlannedProcessQty,MinStart,MaxEnd");
            foreach (var x in agg)
            {
                sb.Append('"').Append(x.Key.Replace("\"", "\"\"")).Append("\",")
                  .Append(x.Count).Append(',')
                  .Append(x.Qty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                  .Append(x.ProcQty.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                  .Append(x.MinStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(x.MaxEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .AppendLine();
            }

            System.IO.File.WriteAllText(csvPath, sb.ToString(), System.Text.Encoding.UTF8);
            _output.WriteLine($"[S] 逐需求条数已落盘：{csvPath}（{agg.Count} 个需求 / {tasks.Count} 条 Task）");
        }
        catch (Exception ex)
        {
            // 落盘失败不影响读数。
            _output.WriteLine($"[S] 逐需求条数落盘失败（已吞）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 2026-11-03｜**交接规则与 PI Stage 上下文探针**（纯观测，不写文件、不落库）。
    ///
    /// 一次跑批答四个问题（跑一轮要一小时，所以一次问全）：
    ///   ① 载荷里到底有没有 `$.StageHandoffWaitRules`？**规则本身长什么样**（全打，行数量级很小）；
    ///   ② 接线有没有被真触发（命中 / 应用 / WORK 跳过 / 累计分钟）；
    ///   ③ `PiStageQuantityBaselines` 输入规模多大、`ResolvePiStageContext` 四种 null 成因各多少
    ///      —— 直接定位 `PI_LEGAL_QTY_CONTRACT_PENDING` 是「输入为空」还是「键没命中」；
    ///   ④ 需求片数（与「订单数」不同口径，供双方对账）。
    /// </summary>
    private void DumpHandoffAndPiStageProbe(DomainSolveRequest request, SolverDiagnostics.Counters c)
    {
        _output.WriteLine("");
        _output.WriteLine("[S] ── 交接规则 / PI Stage 上下文 探针 ──────────────────────────────");

        var rules = request.StrategySnapshot?.StageHandoffWaitRules;
        var ruleList = rules ?? Array.Empty<StageHandoffWaitRule>();
        _output.WriteLine($"[S] 交接规则：载荷 {ruleList.Count} 条，其中 IsActive==true {ruleList.Count(r => r.IsActive == true)} 条");
        foreach (var r in ruleList.Take(60))
        {
            _output.WriteLine($"[S]   {r.FactoryCode} | {r.FromStageCode}→{r.ToStageCode} | "
                + $"{r.WaitMinutes} 分钟 | {r.TimeBasis} | IsActive={r.IsActive?.ToString() ?? "null"}");
        }
        if (ruleList.Count > 60) _output.WriteLine($"[S]   …其余 {ruleList.Count - 60} 条省略");

        _output.WriteLine($"[S] 交接执行：命中 {c.StageHandoffMatched} 次 / 应用 {c.StageHandoffApplied} 次"
            + $" / WORK 跳过 {c.StageHandoffSkippedWork} 次 / 累计加时 {c.StageHandoffAppliedMinutes} 分钟");

        var baselines = request.PiStageQuantityBaselines;
        _output.WriteLine($"[S] PiStageQuantityBaselines 原始条数 = {baselines?.Count ?? 0}"
            + $"；索引键数 = {c.PiStageIndexEntries}；索引内不同 PI = {c.PiStageIndexDistinctPi}");
        _output.WriteLine($"[S] ResolvePiStageContext 调用 {c.PiStageResolveCalls} 次："
            + $"命中 {c.PiStageResolveHit}"
            + $"｜索引空 {c.PiStageNull_IndexEmpty}"
            + $"｜PI 为空 {c.PiStageNull_PiMissing}"
            + $"｜Stage 为空 {c.PiStageNull_StageMissing}"
            + $"｜**键未命中 {c.PiStageNull_KeyNotFound}**");

        _output.WriteLine($"[S] 需求片数 = {request.LogicalProductionDemands?.Count ?? 0}（与「订单数」不同口径：BOM 展开＋连续性分桶后）");
    }

    private static ServiceProvider BuildProductionProvider()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(System.IO.Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddGovernanceRepositories();
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        // 生产组合根里 `AddApplicationServices` 依赖容器已有 `ILogger<T>`（`SnapshotService` 等按构造注入）。
        // 本工装**不挂任何 Provider**：装载段逐 PI 刷 Warning 是已知的 I/O 杀手，测速时不要它。
        services.AddLogging();

        // 具体类不在接口扫描范围，必须显式注册（与 RealDomainFullRunTest 同口径）
        services.AddScoped<DatabaseConnectionManager>();
        services.AddScoped<SchedulingOrchestrator>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 解析某 RunType 下的生产默认策略包版本：PUBLISHED + IsDefault=1 + 父 Profile IsActive=1 + 有效窗口内。
    /// 0 个 / 多个都**拒绝**（红线：禁止盲目取第一个），与 <c>RunLifecycleService</c> 同口径。
    /// </summary>
    private static async Task<long> ResolveDefaultStrategyVersionIdAsync(
        DatabaseConnectionManager conn, string runType, CancellationToken ct)
    {
        var pinned = Environment.GetEnvironmentVariable("APS_SOLVER_ONLY_STRATEGY_VERSION");
        if (long.TryParse(pinned, out var pinnedId) && pinnedId > 0)
        {
            return pinnedId;
        }

        var rows = (await conn.QueryAsync<long>(
            @"SELECT v.Id
              FROM StrategyProfileVersion v
              INNER JOIN StrategyProfile p ON p.Id = v.StrategyProfileId
              WHERE p.RunType = @RunType
                AND p.IsActive = 1
                AND v.IsDefault = 1
                AND v.Status = 'PUBLISHED'
                AND (v.EffectiveFrom IS NULL OR v.EffectiveFrom <= GETDATE())
                AND (v.EffectiveTo   IS NULL OR v.EffectiveTo   >= GETDATE())",
            new { RunType = runType }, db: DatabaseId.APS)).ToList();

        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"RunType={runType} 下有效默认策略包版本 {rows.Count} 个（{string.Join(",", rows)}）—— "
                + "不能盲目取，请用 APS_SOLVER_ONLY_STRATEGY_VERSION 显式点名。");
        }

        return rows[0];
    }

    /// <summary>分相耗时 + 热点。与 SolverReplayTest 同一套读数（同一 <c>Counters</c>），前后可对比。</summary>
    private void DumpPhases(SolverDiagnostics.Counters c, TimeSpan wall)
    {
        var inv = CultureInfo.InvariantCulture;
        long Total() => c.Phase1Ms + c.Phase2Ms + c.Phase3Ms + c.Phase4Ms + c.Phase5Ms;

        _output.WriteLine("");
        _output.WriteLine("── 分相（毫秒）──────────────────────────────────────────────");
        _output.WriteLine($"   P1 建约束 {c.Phase1Ms,10} | P2 排程 {c.Phase2Ms,10} | P3 诊断 {c.Phase3Ms,8} | "
            + $"P4 修复 {c.Phase4Ms,8} | P5 压缩 {c.Phase5Ms,10}");
        _output.WriteLine($"   合计 {Total(),10}（{Total() / 1000d / 60d:F1} 分），墙钟 {wall.TotalSeconds:F1}s ⇒ "
            + $"P2 占比 {(Total() == 0 ? 0d : c.Phase2Ms * 100d / Total()):F1}%");

        _output.WriteLine("");
        _output.WriteLine("── 热点（累计毫秒 / 调用次数 / 每次微秒）──────────────────────");
        var rows = new (string Name, int Slot, long Calls)[]
        {
            ("batch_trial",        SolverDiagnostics.Hotspot.BatchPlanTrial,             c.BatchPlanCandidateTrials),
            ("landing",            SolverDiagnostics.Hotspot.LandingRun,                 0),
            ("sched_ops",          SolverDiagnostics.Hotspot.ScheduleDemandOps,          c.Phase2DemandsProcessed),
            ("clone_task_list",    SolverDiagnostics.Hotspot.CloneTaskList,              c.CloneTaskList),
            ("clone_shares",       SolverDiagnostics.Hotspot.CloneShares,                c.CloneShares),
            ("clone_occ",          SolverDiagnostics.Hotspot.CloneOccupancy,             c.CloneOccupancy),
            ("slot_search",        SolverDiagnostics.Hotspot.SlotSearch,                 c.SlotSearches),
            ("find_fwd",           SolverDiagnostics.Hotspot.FindForwardSlot,            0),
            ("has_conflict",       SolverDiagnostics.Hotspot.HasConflict,                0),
            ("trial_prep",         SolverDiagnostics.Hotspot.TrialPrep,                  0),
            ("batch_adopt",        SolverDiagnostics.Hotspot.BatchAdopt,                 0),
            ("prep_direction",     SolverDiagnostics.Hotspot.PrepDirection,              0),
            ("prep_index",         SolverDiagnostics.Hotspot.PrepIndex,                  0),
            ("bg_rebuild",         SolverDiagnostics.Hotspot.BatchGroupFullRebuild,      0),
            ("bg_incr",            SolverDiagnostics.Hotspot.BatchGroupIncremental,      0),
            ("bg_adopt_reconcile", SolverDiagnostics.Hotspot.BatchGroupAdoptReconcile,   0),
            ("bg_trial_fresh",     SolverDiagnostics.Hotspot.BatchGroupTrialFresh,       0),
            ("bg_trial_overlay",   SolverDiagnostics.Hotspot.BatchGroupTrialOverlay,     0),
            ("ensure_own_delta",   SolverDiagnostics.Hotspot.EnsureOwnDelta,             0),
            ("occ_copy",           SolverDiagnostics.Hotspot.OccCopy,                    0),
            ("occ_copy_real",      SolverDiagnostics.Hotspot.OccCopyReal,                0),
            ("occ_insert",         SolverDiagnostics.Hotspot.OccInsert,                  0),
            ("timeline_place",     SolverDiagnostics.Hotspot.TimelinePlace,              0),
            ("timeline_read",      SolverDiagnostics.Hotspot.TimelineRead,               0),
            ("merge_anchor_scan",  SolverDiagnostics.Hotspot.MergeAnchorScan,            c.MergeScanCalls),
            ("merge_find_tasks",   SolverDiagnostics.Hotspot.MergeFindTasks,             0),
            ("merge_eff",          SolverDiagnostics.Hotspot.MergeEffEval,               0),
            ("detach_bookkeep",    SolverDiagnostics.Hotspot.DetachBookkeeping,          0),
            ("detach_occ",         SolverDiagnostics.Hotspot.DetachOccupancy,            0),
            ("detach_timeline",    SolverDiagnostics.Hotspot.DetachTimeline,             0),
            ("detach_copy",        SolverDiagnostics.Hotspot.DetachCopy,                 0),
            ("detach_tail",        SolverDiagnostics.Hotspot.DetachTail,                 0),
        };

        var ordered = new List<(string Name, double Ms, long Calls)>();
        foreach (var (name, slot, calls) in rows)
        {
            var ms = c.HotspotMs(slot);
            if (ms <= 0 && calls <= 0)
            {
                continue;
            }

            ordered.Add((name, ms, calls));
        }

        ordered.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        foreach (var (name, ms, calls) in ordered)
        {
            var perCall = calls > 0 ? $"{ms * 1000d / calls,9:F2} µs/次" : "        —";
            var callsText = calls > 0 ? calls.ToString(inv) : "—";
            _output.WriteLine($"   {name,-20} {ms,10:F0} ms  {callsText,12} 次  {perCall}");
        }

        _output.WriteLine("");
        _output.WriteLine("   ⚠ `Counters.HotspotMs` 的跨度**会嵌套**（试排包含排程，排程包含找槽），"
            + "所以各槽相加**大于** P2 是正常的；只有**逐拍做差**才能看出来这一段时间谁在涨。");
    }

    private sealed class PvRow
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
        public int? SourceScheduleRunId { get; set; }
        public string? RunType { get; set; }
        public long? StrategyProfileVersionId { get; set; }
        public DateTime? DataCutoffTime { get; set; }
    }
}
