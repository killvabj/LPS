using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 · 求解器离线重放】拿一份<b>捕获下来的 `DomainSolveRequest` JSON</b>，
/// <b>只跑 Phase1~Phase5</b>，不装载、不 Pegging、不落库。
///
/// <para><b>为什么需要它</b>：PV190 一次全流程要 <b>2 小时 45 分</b>，其中真正要优化的
/// （Phase1~5）只占 <b>44 分 47 秒</b>，剩下 <b>1 小时 56 分</b> 全在落库段
/// （已由 2号位 侧优化，见《2026-10-29 落库段性能实测》回执）。
/// 每改一次算法就跑一次全流程，等于 80% 的时间在等一件与本改动无关的事。
/// 本入口把求解这一段摘出来单独跑，一轮改动的验证成本从「小时级」降到「分钟级」。</para>
///
/// <para><b>入参从哪来</b>：`SolverDiagnostics.CaptureSolveRequest` —— `FiniteCapacitySolver.SolveCoreAsync`
/// 第一行回调，抓到的是<b>未被求解器改动过</b>的原始入参。宿主侧落盘由
/// `RealDomainFullRunTest.SolveRequestCapture` 负责（`APS_CAPTURE_SOLVE_REQUEST=1`）。
/// 也就是说：<b>全流程只需跑一次并开捕获，之后每次改动都只跑本入口。</b></para>
///
/// <para><b>开门</b>：
/// <code>APS_REPLAY_SOLVE_REQUEST=D:\tmp\PV190_solverequest_01.json dotnet test LPS.APS.Tests --filter FullyQualifiedName~SolverReplayTest</code>
/// 默认（不设该变量）<b>跳过</b> —— 仓库里不该硬编码任何本机路径。</para>
///
/// <para><b>⚠ 保真度边界（必须知道，别把重放结果当全流程结果）</b>：
/// 捕获用的序列化器是 <c>ReferenceHandler.IgnoreCycles</c>（图状 DTO 不设它会在环上抛异常）。
/// ⇒ JSON 里<b>被判定成环的那条引用被写成了 null</b>，反序列化后<b>环恢复不了</b>，
/// 共享对象会变成多份副本。所以：</para>
/// <list type="bullet">
///   <item>用来<b>比快慢、看热点分布、验证「这一刀削掉了多少」</b>：够用（同一份 JSON 前后自比）。</item>
///   <item>用来<b>断言业务结论、逐位比对全流程的 FinalTask 结果</b>：<b>不够</b> —— 那要真跑全流程。</item>
/// </list>
/// <para>本入口因此把「反序列化后的规模」原样打出来（需求数 / 资源数 / 日历槽数 / 路由工序数），
/// 并与 JSON 自带的 `PlanVersionId` 一起留痕 —— 规模对不上就说明重放输入本身已经走形，先别比耗时。</para>
///
/// <para><b>不改任何生产代码</b>：本文件只 `new FiniteCapacitySolver()` 并按接口调用，走的是
/// `IFiniteCapacityScheduler` 的公开入口，与 `SolverBenchmark` 同一手法。`LPS.APS.Scheduling` 一行未改。</para>
/// </summary>
public class SolverReplayTest
{
    private const string ReplayPathEnvVar = "APS_REPLAY_SOLVE_REQUEST";

    private readonly ITestOutputHelper _output;

    public SolverReplayTest(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "1号位: 求解器离线重放（只跑 Phase1~5，不落库）")]
    public async Task ReplayCapturedSolveRequestAsync()
    {
        var jsonPath = Environment.GetEnvironmentVariable(ReplayPathEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath),
            $"未指定已存在的入参 JSON（{ReplayPathEnvVar}='{jsonPath ?? "(null)"}'）。"
            + " 先用 APS_CAPTURE_SOLVE_REQUEST=1 跑一次全流程把它捕获下来，再用本入口重放。");

        var swLoad = Stopwatch.StartNew();
        var request = Load(jsonPath!);
        swLoad.Stop();

        // ── 反序列化后的规模：这一步就是「重放输入有没有走形」的唯一凭据 ──────────────
        var demandCount = request.LogicalProductionDemands?.Count ?? 0;
        var resourceCount = request.Resources?.Count ?? 0;
        var calendarCount = request.CalendarSlots?.Count ?? 0;
        var routingOpCount = request.RoutingOperations?.Count ?? 0;

        _output.WriteLine($"[R] 入参：{jsonPath}（{new FileInfo(jsonPath!).Length / 1048576d:F1} MB，反序列化 {swLoad.ElapsedMilliseconds} ms）");
        _output.WriteLine($"[R] PlanVersionId={request.PlanVersionId}, DomainKey='{request.DomainKey}', "
            + $"规划窗 {request.PlanningStart:yyyy-MM-dd HH:mm} ~ {request.PlanningEnd:yyyy-MM-dd HH:mm}");
        _output.WriteLine($"[R] 规模：需求 {demandCount} / 资源 {resourceCount} / 日历槽 {calendarCount} / 路由工序 {routingOpCount}");
        _output.WriteLine("[R] 规模若与捕获那一轮的日志不符，说明 JSON 走形（环被 IgnoreCycles 打断）⇒ 先别比耗时。");

        // ── 2026-11-21（0号位 v1.8 §七 / W6）：`APS_REPLAY_DIGEST_DUMP=<path>` 落**输入指纹** ──
        //   为什么要有它：W5/W6 要求「同输入」可证。光有文件路径不够——文件被替换过也看不出来。
        //   这里落 sha256 + 逐集合条数；两次跑的数一对比，就能回答"是不是同一份输入"。
        var digestPath = Environment.GetEnvironmentVariable("APS_REPLAY_DIGEST_DUMP");
        if (!string.IsNullOrWhiteSpace(digestPath))
        {
            try
            {
                using var digestWriter = new System.IO.StreamWriter(digestPath!, false, System.Text.Encoding.UTF8);
                var fi = new FileInfo(jsonPath!);
                using (var hashStream = File.OpenRead(jsonPath!))
                {
                    var sha = System.Security.Cryptography.SHA256.HashData(hashStream);
                    digestWriter.WriteLine($"sha256={Convert.ToHexString(sha).ToLowerInvariant()}");
                }
                digestWriter.WriteLine($"file={jsonPath}");
                digestWriter.WriteLine($"sizeBytes={fi.Length}");
                digestWriter.WriteLine($"planVersionId={request.PlanVersionId}");
                digestWriter.WriteLine($"domainKey={request.DomainKey}");
                digestWriter.WriteLine($"planningStart={request.PlanningStart:O}");
                digestWriter.WriteLine($"planningEnd={request.PlanningEnd:O}");
                digestWriter.WriteLine($"demands={demandCount}");
                digestWriter.WriteLine($"resources={resourceCount}");
                digestWriter.WriteLine($"calendarSlots={calendarCount}");
                digestWriter.WriteLine($"routingOperations={routingOpCount}");
                digestWriter.WriteLine($"routingDependencies={request.RoutingDependencies?.Count ?? 0}");
                digestWriter.WriteLine($"operationResourceEligibility={request.OperationResourceEligibility?.Count ?? 0}");
                digestWriter.WriteLine($"materialRequirementLinks={request.MaterialRequirementLinks?.Count ?? 0}");
                digestWriter.WriteLine($"materialConstraints={request.MaterialConstraints?.Count ?? 0}");
                digestWriter.WriteLine($"stageLeadTimes={request.StageLeadTimes?.Count ?? 0}");
                digestWriter.WriteLine($"stageSequenceChains={request.StageSequenceChains?.Count ?? 0}");
                digestWriter.WriteLine($"stageProcessQuantities={request.StageProcessQuantities?.Count ?? 0}");
                digestWriter.WriteLine($"materialStageDepartmentContexts={request.MaterialStageDepartmentContexts?.Count ?? 0}");
                digestWriter.WriteLine($"batchPolicies={request.StrategySnapshot?.BatchPolicies?.Count ?? 0}");
                digestWriter.WriteLine($"piRemainingFacts={request.PiRemainingFacts?.Count ?? 0}");
                digestWriter.WriteLine($"piStageQuantityBaselines={request.PiStageQuantityBaselines?.Count ?? 0}");
                digestWriter.WriteLine($"executionConstraints={request.ExecutionConstraints?.Count ?? 0}");
                _output.WriteLine($"[R] 输入指纹已写：{digestPath}");
            }
            catch (Exception ex)
            {
                // 指纹失败不得影响求解
                _output.WriteLine($"[R] 输入指纹写入失败（不影响求解）：{ex.Message}");
            }
        }

        Skip.If(demandCount == 0, "入参 JSON 里一条需求都没有 —— 不是可重放的输入。");

        // ── 2026-11-19 只读分析（1-CF-A）：`APS_REPLAY_ANALYZE_ONLY=1` ⇒ 加载后即输出 TimeBasis 账，不求解 ──
        if (Environment.GetEnvironmentVariable("APS_REPLAY_ANALYZE_ONLY") == "1")
        {
            var slices = request.MaterialConstraints ?? Array.Empty<MaterialAvailabilitySlice>();
            var byBasis = slices.GroupBy(s => s.TimeBasis ?? "(null)")
                                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var unknownAllocs = slices
                .Where(s => s.TimeBasis is "UNRESOLVED" or "SH_TIME_UNRESOLVED")
                .Select(s => s.AllocationSequence)
                .ToHashSet();
            var demandAllocs = request.LogicalProductionDemands
                .Select(d => d.AllocationSequence)
                .ToHashSet();
            var coveredDemands = request.LogicalProductionDemands
                .Count(d => unknownAllocs.Contains(d.AllocationSequence));
            _output.WriteLine($"[ANA] MaterialConstraints 总 {slices.Count}；"
                + string.Join(" / ", byBasis.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
            _output.WriteLine($"[ANA] 时间未定切片覆盖 Allocation={unknownAllocs.Count}；"
                + $"命中本请求需求 {coveredDemands}/{demandCount}（需求 Allocation 去重 {demandAllocs.Count}）");
            _output.WriteLine($"[ANA] 示例需求：{string.Join("、", request.LogicalProductionDemands
                .Where(d => unknownAllocs.Contains(d.AllocationSequence))
                .Take(5).Select(d => $"{d.LogicalDemandKey}(alloc={d.AllocationSequence},net={d.NetOutputQty:G29})"))}");
            _output.WriteLine($"[ANA] 切片 alloc 样例={string.Join(",", unknownAllocs.Take(5))}"
                + $"；需求 alloc 样例={string.Join(",", demandAllocs.Take(5))}"
                + $"；切片 alloc 范围={unknownAllocs.Min()}~{unknownAllocs.Max()}"
                + $"；需求 alloc 范围={demandAllocs.Min()}~{demandAllocs.Max()}");

            // 2026-11-20：D-04 分类只依赖 request DTO（与求解无关）⇒ 只读分析模式也顺手导出，
            //   不必为了分类再跑一次 40 分钟全量。
            DumpBatchPolicyClassification(request);
            return;
        }

        // ── 计时与采样工装：与全流程同一套（列的语义一致，才能前后对比）──────────────
        using var solverScope = SolverDiagnostics.BeginScope();
        using var sampler = RealDomainFullRunTest.SolverLiveSampler.Start(
            solverScope.Counters, request.PlanVersionId, _output);

        using var runCts = RealDomainFullRunTest.CreateRunCancellationCore(
            Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var timeoutMinutes);
        if (timeoutMinutes > 0d)
        {
            _output.WriteLine($"[R] 重放超时 = {timeoutMinutes:0.##} 分钟（APS_RUN_TIMEOUT_MIN）");
        }

        var ct = runCts.Token;
        var sw = Stopwatch.StartNew();
        DomainSolveResult? result = null;
        var cancelled = false;

        // ── 2026-11-20 进度信标（`APS_REPLAY_PROGRESS=<path>`）：每 10 秒把运行中进度写**文件**并落盘 ──
        //   为什么要有它：`SolverLiveSampler` 只写 xUnit 输出，而命令行常带管道/重定向 ⇒ 中途看不到。
        //   数据源就是既有的 public `SolverDiagnostics.LiveSnapshot`（Phase + Phase2 需求已处理/总数 +
        //   Phase4 修复单元 + 关键计数器），**不新增任何生产打点、不改求解行为**。
        var progressPath = Environment.GetEnvironmentVariable("APS_REPLAY_PROGRESS");
        using var progressCts = new CancellationTokenSource();
        Task? progressTask = null;
        if (!string.IsNullOrWhiteSpace(progressPath))
        {
            progressTask = Task.Run(async () =>
            {
                var swp = Stopwatch.StartNew();
                while (!progressCts.IsCancellationRequested)
                {
                    try
                    {
                        var s = SolverDiagnostics.LiveSnapshot;
                        var p2 = s is null ? "-" : $"{s.Phase2DemandsProcessed}/{s.Phase2DemandsTotal}";
                        var p4 = s is null ? "-" : $"{s.Phase4UnitsProcessed}/{s.Phase4UnitsTotal}";
                        var pct2 = s is not null && s.Phase2DemandsTotal > 0
                            ? $" ({100.0 * s.Phase2DemandsProcessed / s.Phase2DemandsTotal:0.0}%)"
                            : string.Empty;
                        var line =
                            $"{DateTime.Now:HH:mm:ss} | elapsed {swp.Elapsed:hh\\:mm\\:ss}"
                            + $" | phase={s?.CurrentPhase ?? "(null)"}"
                            + $" | P2 {p2}{pct2} | P4 {p4}"
                            + $" | routingTrials={s?.BatchPlanCandidateTrials ?? -1}"
                            + $" | runBatchPlan={s?.RunBatchPlanInvocations ?? -1}"
                            + $" | slots={s?.SlotSearchAttempts ?? -1}";
                        File.AppendAllText(progressPath!, line + Environment.NewLine);
                    }
                    catch
                    {
                        // 埋点失败绝不影响求解
                    }

                    try { await Task.Delay(TimeSpan.FromSeconds(10), progressCts.Token).ConfigureAwait(false); }
                    catch { }
                }
            });
        }

        try
        {
            // 直调接口实现：与 DI 注册的 Singleton 是同一个类型，构造无参（见 SolverBenchmark 同款用法）。
            result = await new FiniteCapacitySolver().SolveAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            sw.Stop();
            sampler.Stop();
            progressCts.Cancel();
            if (progressTask is not null)
            {
                try { await progressTask.ConfigureAwait(false); } catch { }
            }
            DumpPhaseAndHotspots(solverScope.Counters, sw.Elapsed);
        }

        if (cancelled)
        {
            _output.WriteLine($"[R] **超时取消**：{sw.Elapsed.TotalMinutes:0.#} 分钟未返回。"
                + " 卡在哪一步看上面的 CSV 逐拍读数（每拍都在涨的那个 hs_* 就是卡点）。");
            return;
        }

        var finalTaskCount = result?.FinalTasks?.Count ?? 0;

        // 2026-11-19 诊断（新增可执行批整数门禁落地后）：小数 Quantity 的实测条数与 Top5。
        //   纯输出，不改求解逻辑、不做断言；UOM=PS 是离散件单位。
        var fracTasks = result!.FinalTasks.Where(t => t.Quantity != decimal.Truncate(t.Quantity)).ToList();
        var fracPs = fracTasks.Count(t => string.Equals(t.UOM, "PS", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"[INT] FinalTask={result.FinalTasks.Count}；小数 Quantity={fracTasks.Count}（其中 UOM=PS {fracPs}）；"
            + $"Top5={string.Join("、", fracTasks.GroupBy(t => t.Quantity).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key:G29}×{g.Count()}"))}");

        // 2026-11-19 诊断（乙：查整数门禁带来的 FinalTask 变化去向）：
        //   `APS_REPLAY_DUMP=<path>` ⇒ 逐需求导出 TSV，供两次重放（revert 前 / 打回 patch 后）按 LogicalDemandKey 对拍。
        var dumpPath = Environment.GetEnvironmentVariable("APS_REPLAY_DUMP");
        if (!string.IsNullOrWhiteSpace(dumpPath))
        {
            var unschedByDemand = result.UnscheduledTasks
                .GroupBy(u => u.DraftId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key,
                    g => (Qty: g.Sum(x => x.UnscheduledQty), Reason: g.First().Reason),
                    StringComparer.Ordinal);
            var taskByDemand = result.FinalTasks
                .GroupBy(t => t.SourceDraftId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key,
                    g => (Cnt: g.Count(), Qty: g.Sum(t => t.Quantity)),
                    StringComparer.Ordinal);

            using var dumpWriter = new System.IO.StreamWriter(dumpPath!, false, System.Text.Encoding.UTF8);
            dumpWriter.WriteLine("demand\tnet\tisCont\tuom\ttaskCnt\ttaskQty\tuQty\tuReason");
            foreach (var d in request.LogicalProductionDemands)
            {
                taskByDemand.TryGetValue(d.LogicalDemandKey, out var ta);
                unschedByDemand.TryGetValue(d.LogicalDemandKey, out var ua);
                dumpWriter.WriteLine(
                    $"{d.LogicalDemandKey}\t{d.NetOutputQty:G29}\t{d.IsContinuation}\t{d.UOM ?? ""}\t{ta.Cnt}\t{ta.Qty:G29}\t{ua.Qty:G29}\t{ua.Reason}");
            }
            _output.WriteLine($"[DUMP] 逐需求去向已写：{dumpPath}（{request.LogicalProductionDemands.Count} 行）");

            // ── 2026-11-20：未排**明细**导出（`<dump>.notes.tsv`）──
            //   用途：`PHASE2_INITIAL_SCHEDULE_FAILED` 这类**泛化标签**无法回答「算法问题还是数据问题」；
            //   Phase2 的 `Phase2BlockedDetails` / 日历逐资源诊断 / 部门缺失明细都写在 `SolveTraceNotes`，
            //   这里原样导出（脱敏：制表符/换行替换为空格），供按需求归因分类。纯导出，不改判定。
            var notesPath = dumpPath + ".notes.tsv";
            using var notesWriter = new System.IO.StreamWriter(notesPath, false, System.Text.Encoding.UTF8);
            notesWriter.WriteLine("demand\treason\tlevel\tmessage");
            foreach (var note in result.SolveTraceNotes)
            {
                var msg = (note.Message ?? string.Empty)
                    .Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                notesWriter.WriteLine($"{note.Key}\t{note.ReasonCode}\t{note.Level}\t{msg}");
            }
            _output.WriteLine($"[DUMP] Trace 明细已写：{notesPath}（{result.SolveTraceNotes.Count} 行）");
        }

        // ── 2026-11-21（0号位 v1.8 §三「独立校验」）：`APS_REPLAY_TASK_DUMP=<path>` 导出**逐 Task 时间窗** ──
        //   用途：用**测试侧独立实现的**校验器（`IndependentScheduleAuditTests` 的同一套判据）在真实结果上
        //   复查「同资源重叠 / Setup 侵入 / 依赖逆序 / 锁漂移」，而不是只让 `ValidateHardResult` 自己证自己。
        //   纯导出，不改判定、不写库。
        var taskDumpPath = Environment.GetEnvironmentVariable("APS_REPLAY_TASK_DUMP");
        if (!string.IsNullOrWhiteSpace(taskDumpPath))
        {
            using var tw = new System.IO.StreamWriter(taskDumpPath!, false, System.Text.Encoding.UTF8);
            // 2026-11-21（EX-T05）：末列补 `isCritical` —— 2号位 r14344 已把字段加进 DTO，
            //   本列用于「真实 PV 上关键路径是否合理」的核对（false = 算过且不关键，非未算）。
            tw.WriteLine("taskId\tresourceId\tstart\tend\tsetupMinutes\tdemandKey\tstage\top\tisCritical");
            foreach (var t in result.FinalTasks)
            {
                tw.WriteLine(string.Join('\t',
                    t.FinalDraftId,
                    t.ResourceId?.ToString() ?? "null",
                    t.PlannedStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    t.PlannedEndTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    t.SetupTime.ToString("G29"),
                    t.SourceDraftId,
                    t.StageCode ?? string.Empty,
                    t.OperationCode ?? string.Empty,
                    t.IsCriticalPath ? "1" : "0"));
            }
            _output.WriteLine($"[DUMP] 逐 Task 时间窗已写：{taskDumpPath}（{result.FinalTasks.Count} 行）");
        }

        // ── 2026-11-21（EX-T01 / EX-T02 真实 PV 绑定）：`APS_REPLAY_FACTS_DUMP=<path>` 导出**解释事实** ──
        //   用途：把 Phase3 的 `ScheduleExplanationFact`（原因码/证据类别/ImpactHours/EvidenceJson）
        //   摊平成 TSV，供逐条核对「五类最终真紧约束」与「物料绑定证据（bindingState/materialReadyAt/slack）」。
        //   纯导出，不改判定、不写库。
        var factsDumpPath = Environment.GetEnvironmentVariable("APS_REPLAY_FACTS_DUMP");
        if (!string.IsNullOrWhiteSpace(factsDumpPath))
        {
            using var fw = new System.IO.StreamWriter(factsDumpPath!, false, System.Text.Encoding.UTF8);
            fw.WriteLine("finalDraftId\tobjectType\torderId\tresourceId\tstageCode\treasonCode\tseverity\timpactHours\tevidenceJson");
            foreach (var f in result.ExplanationFacts)
            {
                var evidence = (f.EvidenceJson ?? string.Empty)
                    .Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                fw.WriteLine(string.Join('\t',
                    f.FinalDraftId ?? string.Empty,
                    f.ObjectType ?? string.Empty,
                    f.OrderId?.ToString() ?? string.Empty,
                    f.ResourceId?.ToString() ?? string.Empty,
                    f.StageCode ?? string.Empty,
                    f.ReasonCode ?? string.Empty,
                    f.Severity ?? string.Empty,
                    f.ImpactHours?.ToString("G29") ?? string.Empty,
                    evidence));
            }
            _output.WriteLine($"[DUMP] 解释事实已写：{factsDumpPath}（{result.ExplanationFacts.Count} 行）");
        }

        // ── 2026-11-20 D-04 诊断：⑧块 Batch Policy **逐需求分类**（`APS_REPLAY_BATCHPOLICY=<path>`）──
        //   0号位 v1.8 §四 要求把 VALID / MISSING / INVALID / UNKNOWN / UNAVAILABLE 与
        //   「合法 B-010 Preferred 兜底」分开，并给出可定位样本与总数。
        //   判定**复用生产同一函数** `ResolveExecutionBatchPolicyCore`（DTO 版）⇒ 不建第二套口径；
        //   本块只读 request DTO：不建 ConstraintContext、不改求解、不写库。
        // 2026-11-20 D-04 逐需求分类（`APS_REPLAY_BATCHPOLICY=<path>`）：参见 helper 注释；
        //   只读 request DTO，与求解无关 ⇒ 也放进 `APS_REPLAY_ANALYZE_ONLY=1` 分支（不用跑全量）。
        DumpBatchPolicyClassification(request);

        _output.WriteLine($"[R] 求解返回：Success={result!.Success}, FinalTask={finalTaskCount}, "
            + $"求解耗时={sw.Elapsed.TotalSeconds:F1}s");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            _output.WriteLine($"[R] ErrorMessage={result.ErrorMessage}");
        }

        // 2026-11-19 诊断（开门 APS_DEBUG_UNSCHED_DUP=1）：自查落库 Inv2「U 超额」是**我方产出重复**
        //   还是**落库底座按 base-key（'/' 前）合并放大**。不 gate 时不打、不改判定。
        if (Environment.GetEnvironmentVariable("APS_DEBUG_UNSCHED_DUP") == "1")
        {
            static string BaseKeyOf(string k)
            {
                if (string.IsNullOrEmpty(k)) return string.Empty;
                var s = k.IndexOf('/');
                return s > 0 ? k[..s] : k;
            }

            var un = result.UnscheduledTasks ?? Array.Empty<UnscheduledTaskResult>();
            _output.WriteLine($"[DBG] UnscheduledTasks 总 {un.Count} 条；按 DraftId 去重 {un.Select(u => u.DraftId).Distinct().Count()} 个；"
                + $"按 base-key('/'前)去重 {un.Select(u => BaseKeyOf(u.DraftId)).Distinct().Count()} 个");

            var dupId = un.GroupBy(u => u.DraftId).Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count()).Take(10).ToList();
            _output.WriteLine($"[DBG] 同 DraftId 多条的键数={dupId.Count}");
            foreach (var g in dupId)
            {
                _output.WriteLine($"[DBG-dupId] {g.Key} 条数={g.Count()} qtys=[{string.Join(",", g.Select(x => x.UnscheduledQty.ToString("G29")))}]");
            }

            var net = request.LogicalProductionDemands
                .GroupBy(d => d.LogicalDemandKey)
                .ToDictionary(g => g.Key!, g => g.First().NetOutputQty);
            var over = un.GroupBy(u => BaseKeyOf(u.DraftId))
                .Select(g => (Base: g.Key, Cnt: g.Count(), SumU: g.Sum(x => x.UnscheduledQty),
                              Net: net.TryGetValue(g.Key, out var q) ? q : -1m))
                .Where(x => x.Cnt > 1 || (x.Net >= 0m && x.SumU > x.Net + 0.001m))
                .OrderByDescending(x => x.SumU - (x.Net < 0m ? 0m : x.Net)).Take(12).ToList();
            _output.WriteLine($"[DBG] base-key 多条 或 ΣU>Net 的键数(取样12)={over.Count}");
            foreach (var x in over)
            {
                _output.WriteLine($"[DBG-over] base={x.Base} 条数={x.Cnt} ΣU={x.SumU:G29} Net={x.Net:G29}");
            }
        }

        // 2026-11-19：**精确复刻 2号位 内存 Inv2**（PeggingOrchestrator:12212-12286）——
        //   逐 AllocationSequence：ΣShare(AllocationShares.ComponentQty) + ΣU(UnscheduledTasks 按需求→seq) vs ΣNetOutput。
        //   验证「缺料 U 按被删 share 对平」修复后账是否平。开门 APS_DEBUG_INV2=1。
        if (Environment.GetEnvironmentVariable("APS_DEBUG_INV2") == "1")
        {
            var allocNet = request.LogicalProductionDemands
                .GroupBy(d => d.AllocationSequence).ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty));
            var allocShare = (result.AllocationShares ?? Array.Empty<AllocationTaskShare>())
                .GroupBy(s => s.AllocationSequence).ToDictionary(g => g.Key, g => g.Sum(s => s.ComponentQty));
            var keyToSeq = request.LogicalProductionDemands
                .GroupBy(d => d.LogicalDemandKey!).ToDictionary(g => g.Key, g => g.First().AllocationSequence);
            var allocU = new Dictionary<long, decimal>();
            foreach (var u in result.UnscheduledTasks ?? Array.Empty<UnscheduledTaskResult>())
            {
                if (u.UnscheduledQty <= 0m) continue;
                if (keyToSeq.TryGetValue(u.DraftId, out var sq))
                    allocU[sq] = allocU.GetValueOrDefault(sq) + u.UnscheduledQty;
            }

            var viol = allocNet
                .Select(kv => (Seq: kv.Key, Q: kv.Value,
                               S: allocShare.GetValueOrDefault(kv.Key), U: allocU.GetValueOrDefault(kv.Key)))
                .Where(x => Math.Abs(x.S + x.U - x.Q) > 0.001m)
                .OrderByDescending(x => Math.Abs(x.S + x.U - x.Q)).ToList();
            _output.WriteLine($"[INV2] seq 总数={allocNet.Count}；ΣShare+ΣU≠ΣNetOutput 的 seq 数={viol.Count}");
            foreach (var x in viol.Take(10))
            {
                _output.WriteLine($"[INV2-VIOL] seq={x.Seq} ΣShare={x.S:G29} + ΣU={x.U:G29} = {x.S + x.U:G29} vs Net={x.Q:G29}（差 {x.S + x.U - x.Q:G29}）");
            }

            // 逐条明细：对前 5 个违规 seq，打出其 demand / share / 未排 entry 的真实行——直接看 3 倍/漏排怎么凑出来。
            foreach (var x in viol.Take(5))
            {
                var seq = x.Seq;
                _output.WriteLine($"[DETAIL] ===== seq={seq}（S={x.S:G29} U={x.U:G29} Net={x.Q:G29}）=====");
                foreach (var d in request.LogicalProductionDemands.Where(d => d.AllocationSequence == seq))
                    _output.WriteLine($"[DETAIL-DEMAND] key={d.LogicalDemandKey} NetOutput={d.NetOutputQty:G29}");
                foreach (var sh in (result.AllocationShares ?? Array.Empty<AllocationTaskShare>()).Where(s => s.AllocationSequence == seq))
                    _output.WriteLine($"[DETAIL-SHARE] FinalDraftId={sh.FinalDraftId} ComponentQty={sh.ComponentQty:G29}");
                foreach (var u in (result.UnscheduledTasks ?? Array.Empty<UnscheduledTaskResult>())
                            .Where(u => keyToSeq.TryGetValue(u.DraftId, out var q) && q == seq))
                    _output.WriteLine($"[DETAIL-U] DraftId={u.DraftId} qty={u.UnscheduledQty:G29} reason={u.Reason}");
            }
        }

        Assert.True(result.Success, "重放求解未成功：" + (result.ErrorMessage ?? "(无错误信息)"));
    }

    /// <summary>
    /// 流式反序列化。**不能用 <c>JsonSerializer.Deserialize&lt;T&gt;(string)</c> 那条重载** ——
    /// 它会先把整份文件读成一个 string，PV190 量级是数百 MB 的 LOH 常驻，重放还没开始就先顶一次内存。
    /// </summary>
    private static DomainSolveRequest Load(string jsonPath)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            // 与捕获侧同一组选项：读写对称，避免「捕获能写、重放读不回」。
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };

        using var fs = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        return System.Text.Json.JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
               ?? throw new InvalidOperationException($"入参 JSON 反序列化得到 null：{jsonPath}");
    }

    /// <summary>
    /// **D-04：⑧块 Batch Policy 逐需求分类**（`APS_REPLAY_BATCHPOLICY=&lt;path&gt;`）。
    /// 0号位 v1.8 §四 要求把 VALID / MISSING / INVALID / UNKNOWN / UNAVAILABLE 与「合法 B-010 Preferred 兜底」分开。
    /// 判定**复用生产同一函数** <see cref="PhaseTwoInitialScheduler.ResolveExecutionBatchPolicyCore"/>（不建第二套口径）；
    /// 只读 request DTO：不建 ConstraintContext、不改求解、不写库。
    /// 未设环境变量 ⇒ 直接返回（零开销）。
    /// </summary>
    private void DumpBatchPolicyClassification(DomainSolveRequest request)
    {
        var path = Environment.GetEnvironmentVariable("APS_REPLAY_BATCHPOLICY");
        if (string.IsNullOrWhiteSpace(path)) return;

        // 部门反查表（与 `ResolveExecutionBatchPolicyCore` 同源：MaterialStageDepartmentContexts）。
        var deptByMaterialStage = new Dictionary<(int, string), int?>();
        foreach (var ctx in request.MaterialStageDepartmentContexts)
        {
            var key = (ctx.MaterialId, ctx.StageCode ?? string.Empty);
            if (!deptByMaterialStage.ContainsKey(key)) deptByMaterialStage[key] = ctx.ProductionDepartmentId;
        }

        var carrierByKey = new Dictionary<(int, int?), BatchPolicyStateSnapshot>();
        if (request.StrategySnapshot?.BatchPolicyStates is { } states)
        {
            foreach (var s in states)
            {
                var key = (s.MaterialId, s.ProductionDepartmentId);
                if (!carrierByKey.ContainsKey(key)) carrierByKey[key] = s;
            }
        }

        var policies = request.StrategySnapshot?.BatchPolicies
            ?? (IReadOnlyList<BatchPolicyRuleSnapshot>)Array.Empty<BatchPolicyRuleSnapshot>();

        var carrierCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var effectiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int b010Eligible = 0;

        using var writer = new StreamWriter(path!, false, System.Text.Encoding.UTF8);
        writer.WriteLine("demand\tmaterial\tstartStage\tdept\tcarrierState\teffectiveState\tb010Eligible\tmatchSource");
        foreach (var d in request.LogicalProductionDemands)
        {
            int? dept = null;
            if (!string.IsNullOrEmpty(d.StartStageCode))
            {
                var key = (d.MaterialId, d.StartStageCode!);
                if (deptByMaterialStage.TryGetValue(key, out var resolved)) dept = resolved;
            }

            string carrierState;
            if (request.StrategySnapshot?.BatchPolicyStates is null)
            {
                carrierState = "NO_CARRIER";
            }
            else if (carrierByKey.TryGetValue((d.MaterialId, dept), out var entry))
            {
                carrierState = entry.State.ToString();
            }
            else
            {
                carrierState = "NO_ENTRY";
            }

            var resolution = PhaseTwoInitialScheduler.ResolveExecutionBatchPolicyCore(
                d.MaterialId, d.StartStageCode, request.MaterialStageDepartmentContexts,
                request.StrategySnapshot, policies);

            var effective = resolution.State.ToString();
            // B-010 合法兜底只在「可信 Missing」时成立（Invalid/Unknown 均 Fail Closed，不启 Q_C）。
            var eligible = resolution.State == PhaseTwoInitialScheduler.BatchPolicyMatchState.Missing;

            carrierCounts[carrierState] = carrierCounts.TryGetValue(carrierState, out var c1) ? c1 + 1 : 1;
            effectiveCounts[effective] = effectiveCounts.TryGetValue(effective, out var c2) ? c2 + 1 : 1;
            if (eligible) b010Eligible++;

            writer.WriteLine(
                $"{d.LogicalDemandKey}\t{d.MaterialId}\t{d.StartStageCode ?? ""}\t{dept?.ToString() ?? "null"}"
                + $"\t{carrierState}\t{effective}\t{eligible}\t"
                + (resolution.MatchSource ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
        }

        _output.WriteLine($"[BP] Batch Policy 分类已写：{path}（{request.LogicalProductionDemands.Count} 行）；"
            + $"载体状态={string.Join("、", carrierCounts.Select(kv => $"{kv.Key}={kv.Value}"))}；"
            + $"生效判定={string.Join("、", effectiveCounts.Select(kv => $"{kv.Key}={kv.Value}"))}；"
            + $"B-010合法兜底={b010Eligible}");
    }

    /// <summary>
    /// 打印分相耗时与本轮优化要盯的热点。**取的是同一个 `Counters`**，
    /// 所以与 `SolverLiveSampler` 的 CSV 末行应当能对上（对不上说明末拍之后又有读数，不是矛盾）。
    /// </summary>
    private void DumpPhaseAndHotspots(SolverDiagnostics.Counters c, TimeSpan wall)
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
            + "所以各槽相加**大于** P2 是正常的；只有**逐拍做差**才能看出这一段时间谁在涨。");

        // ── 合批采纳链（纯观测读数，用来判定「锚点擂台是不是纯空转」）──────────────
        //   判据链：AnchorFound → TrialStarted → (HardRejectedCalendar | ComparedRejected | Adopted)
        //   采纳数取差值 —— 计数器里没有独立的 Adopted 字段。
        var adopted = c.MergeTrialStarted - c.MergeHardRejectedCalendar - c.MergeComparedRejected;
        _output.WriteLine("");
        _output.WriteLine("── 合批采纳链 ──────────────────────────────────────────────");
        _output.WriteLine($"   锚点找到 {c.MergeAnchorFound,12:N0} | 开始试排 {c.MergeTrialStarted,12:N0} | "
            + $"硬拒(日历) {c.MergeHardRejectedCalendar,10:N0} | 比较落败 {c.MergeComparedRejected,10:N0} | "
            + $"⇒ 采纳 {adopted,10:N0}");
        _output.WriteLine($"   单工序路径：尝试 {c.MergeAttempts,12:N0} | 命中 {c.MergeHits,12:N0} | "
            + $"未命中 {c.MergeMisses,12:N0}");
        _output.WriteLine($"   无合法锚点 {c.MergeNotEligible,12:N0} | 被执行批身份守卫挡 {c.MergeIdentityUnrepresentable,12:N0}");

        WritePhaseCsv(c, wall);
    }

    /// <summary>
    /// 2026-11-21（0号位 v1.8 §七 / W5）：`APS_REPLAY_PHASE_DUMP=<path>` 把**分相耗时与热点**落成 CSV。
    ///
    /// <para><b>为什么要单独落文件</b>：`dotnet test` 默认**不把 <c>ITestOutputHelper</c> 写进日志**，
    /// 所以在此之前，分相计时只在控制台一闪而过 ⇒ W5 的「P2/P3/P4/P5 分段」拿不到证据。
    /// 本方法只把**已有计数器**写成文件，**不新增任何埋点、不改求解行为**。</para>
    ///
    /// <para>格式：两段，`[phase]` 与 `[hotspot]`，每行逗号分隔。跨两次跑对拍时直接按 name 对齐。</para>
    /// </summary>
    private static void WritePhaseCsv(SolverDiagnostics.Counters c, TimeSpan wall)
    {
        var path = Environment.GetEnvironmentVariable("APS_REPLAY_PHASE_DUMP");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var inv = CultureInfo.InvariantCulture;
            using var w = new System.IO.StreamWriter(path!, false, System.Text.Encoding.UTF8);
            w.WriteLine("kind,name,value");
            w.WriteLine($"phase,wallSeconds,{wall.TotalSeconds.ToString("F3", inv)}");
            w.WriteLine($"phase,P1Ms,{c.Phase1Ms.ToString(inv)}");
            w.WriteLine($"phase,P2Ms,{c.Phase2Ms.ToString(inv)}");
            w.WriteLine($"phase,P3Ms,{c.Phase3Ms.ToString(inv)}");
            w.WriteLine($"phase,P4Ms,{c.Phase4Ms.ToString(inv)}");
            w.WriteLine($"phase,P5Ms,{c.Phase5Ms.ToString(inv)}");

            var rows = new (string Name, int Slot, long Calls)[]
            {
                ("batch_trial", SolverDiagnostics.Hotspot.BatchPlanTrial, c.BatchPlanCandidateTrials),
                ("clone_task_list", SolverDiagnostics.Hotspot.CloneTaskList, c.CloneTaskList),
                ("clone_shares", SolverDiagnostics.Hotspot.CloneShares, c.CloneShares),
                ("clone_occ", SolverDiagnostics.Hotspot.CloneOccupancy, c.CloneOccupancy),
                ("slot_search", SolverDiagnostics.Hotspot.SlotSearch, c.SlotSearches),
                ("trial_prep", SolverDiagnostics.Hotspot.TrialPrep, 0),
                ("prep_index", SolverDiagnostics.Hotspot.PrepIndex, 0),
                ("occ_copy_real", SolverDiagnostics.Hotspot.OccCopyReal, 0),
                ("batch_adopt", SolverDiagnostics.Hotspot.BatchAdopt, 0),
                ("merge_anchor_scan", SolverDiagnostics.Hotspot.MergeAnchorScan, c.MergeScanCalls),
                ("detach_tail", SolverDiagnostics.Hotspot.DetachTail, 0),
            };
            foreach (var (name, slot, calls) in rows)
            {
                var ms = c.HotspotMs(slot);
                if (ms <= 0 && calls <= 0)
                {
                    continue;
                }
                w.WriteLine($"hotspot,{name}_ms,{ms.ToString("F0", inv)}");
                if (calls > 0)
                {
                    w.WriteLine($"hotspot,{name}_calls,{calls.ToString(inv)}");
                }
            }
        }
        catch
        {
            // 埋点失败绝不影响求解
        }
    }
}
