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

        Skip.If(demandCount == 0, "入参 JSON 里一条需求都没有 —— 不是可重放的输入。");

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
            DumpPhaseAndHotspots(solverScope.Counters, sw.Elapsed);
        }

        if (cancelled)
        {
            _output.WriteLine($"[R] **超时取消**：{sw.Elapsed.TotalMinutes:0.#} 分钟未返回。"
                + " 卡在哪一步看上面的 CSV 逐拍读数（每拍都在涨的那个 hs_* 就是卡点）。");
            return;
        }

        var finalTaskCount = result?.FinalTasks?.Count ?? 0;
        _output.WriteLine($"[R] 求解返回：Success={result!.Success}, FinalTask={finalTaskCount}, "
            + $"求解耗时={sw.Elapsed.TotalSeconds:F1}s");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            _output.WriteLine($"[R] ErrorMessage={result.ErrorMessage}");
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
    }
}
