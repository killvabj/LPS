using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【2号位 · 真实域全量跑通留痕】—— 唯一一个**故意不清理、故意留真实结果**的集成测试。
///
/// 目的：主链在真实数据上完整跑一遍，把真实 Task / Pegging / PeggingSupplyAllocation /
///       AllocationTaskShare / PI Position 快照落进 APS_Production，供验收 SQL
///       （Database/Scripts/APS/03_SQL脚本/pegging-chain-verify-sql）读取。
///
/// 与 <see cref="RealSchedulingIntegrationTest"/> 的根本差别（**别混用**）：
/// 1. **生产口径 DI**：本类**不**用 <c>DemandPriorityFixtureProvider</c> /
///    <c>FrozenStrategySnapshotFixtureProvider</c> 覆盖 —— 订单优先级与供给排序全部走
///    3号位 冻结快照（<see cref="FrozenStrategySnapshotProvider"/>）+ 2号位 投影层
///    （<see cref="DemandPriorityProjector"/>）。真实跑得通 = 生产跑得通。
/// 2. **真实规模**：FAMILY_X 全量订单（经 sp_SyncOrdersToPartitionTable 从 Order_Canonical 灌），
///    不是合成单条 TEST-SO-001。
/// 3. **不回滚**：结果就是要留下的证据。重跑安全 —— ExecuteDomainAsync 自带结果四表幂等清理，
///    订单装载 SP 是 NOT EXISTS 纯增量。
/// 4. **Run 必须收口**：走 <see cref="SchedulingOrchestrator.RunSchedulingAndFinalizeAsync"/>
///    （带真实 ScheduleRunId，置 COMPLETED/FAILED），**绝不**留 RUNNING ——
///    否则 RunSchedulingAutoAsync 的「RUNNING + Created PV」领取条件会把本 Run 当夜间活领走。
///
/// 运行：dotnet test --filter FullyQualifiedName~RealDomainFullRunTest
///
/// **本类含两个门禁测试（都在 <c>APS_REAL_DOMAIN_RUN=1</c> 后才跑）**：
/// - <see cref="RunRealDomainAsync"/> —— 走 **候选单域 seam** <c>RunSchedulingAndFinalizeAsync</c>（原有）。
/// - <see cref="RunRealSeamAsync"/> —— 走 **夜间发令枪 seam** <c>ExecuteRunAsync</c>（P1-03 抽出，2026-09-29 新增）。
///   补这一条的原因：<c>RunSchedulingAutoAsync</c> **全仓无任何测试调用**，P1-03 把它的方法体搬进
///   <c>ExecuteRunAsync</c> 后，那条路径仍是零覆盖 —— 编译能过 ≠ 跑得起来。
/// 开发者：2号位
/// </summary>
public class RealDomainFullRunTest
{
    // ── 本次跑批的身份（改这里 = 换一次跑批）──────────────────────────────
    /// <summary>真实域（DomainDefinition.ScopeType=FAMILY, ScopeValue=FAMILY_X）。</summary>
    private const string DomainKey = "FAMILY_X";

    /// <summary>本夹具跑的 RunType（夜间全量）。默认策略包按 RunType 解析，见 ResolveDefaultStrategyVersionIdAsync。</summary>
    private const string HarnessRunType = "FULL_SCHEDULE";

    /// <summary>
    /// 生产默认策略包版本（本夹具跑 <c>FULL_SCHEDULE</c>，故断言对象＝该 RunType 下的唯一默认）。
    /// <para><b>2026-10-26 更新 811 → 1970</b>：`811`（SP-DEMO-V3.0）现已 `Status=DISABLED`，
    /// 不再是可解析默认；现行 `FULL_SCHEDULE` 默认＝`1970`（SP-PROD-V1.0，Profile 1303）。</para>
    /// 实际以库为准并断言，不盲信常量。
    /// </summary>
    private const long ExpectedStrategyProfileVersionId = 1970L;

    /// <summary>
    /// 本次跑批的 PlanVersion.VersionCode（**精确值，非 LIKE**，按日期一天一发）。
    ///
    /// <para><b>2026-10-14 增：可用环境变量 <c>APS_REAL_DOMAIN_VERSION_CODE</c> 指向**既有** PlanVersion</b>
    /// （例：<c>NIGHTLY_20261008_FAMILY_X</c>），从而**复用它的 Run**。这是唯一能读到真实 MES 快照的跑法：
    /// MES 三张快照（<c>MESWorkOrderSnapshot</c> / <c>OperationProgressSnapshot</c> / <c>StageProgressSnapshot</c>）
    /// 只挂在 <c>ScheduleRunId=1</c> 下，而 <see cref="SnapshotRunResolver"/> 要求该 Run 在
    /// <c>APS_ETL_Log</c> 上三个快照 Step 均有 <c>SUCCESS</c> 终态 —— **新建 Run 必然 fail-closed**
    /// （新 Run 没有 ETL 成功证据）。默认值（当天 <c>MANUAL_</c>）只适用于不看 MES 快照的场景。</para>
    /// </summary>
    private static string VersionCode =>
        Environment.GetEnvironmentVariable("APS_REAL_DOMAIN_VERSION_CODE") is { Length: > 0 } overridden
            ? overridden
            : $"MANUAL_{DateTime.Today:yyyyMMdd}_{DomainKey}";

    private const string TriggeredBy = "2号位-真实域全量跑通留痕";

    private readonly ITestOutputHelper _output;

    /// <summary>在测试方法体内装配（构造函数不碰 DI，见构造函数注释）。</summary>
    private DatabaseConnectionManager _connectionManager = null!;

    /// <summary><c>APS_RUN_TIMEOUT_MIN</c> 的实际取值（0＝不限时），由 <see cref="CreateRunCancellation"/> 写入，供日志引用。</summary>
    private double _runTimeoutMinutes;

    /// <summary>
    /// 构造函数**只接输出**，不做任何 DI/DB 工作 —— 否则构造函数先于 <c>Skip.IfNot</c> 执行，
    /// 闸门关着也会因为构造失败报 FAIL（假红），把「默认跳过」这件事做废。
    /// </summary>
    public RealDomainFullRunTest(ITestOutputHelper output)
    {
        _output = output;
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
        // 严格对齐生产组合根 LPS.APS.Web/Program.cs 的注册顺序与集合 —— 少一行就不是生产口径了。
        // ⚠️ AddGovernanceRepositories 是**被 Fixture 长期遮住**的一行：历史所有集成测试都用
        //    FrozenStrategySnapshotFixtureProvider 顶掉 IFrozenStrategySnapshotProvider，
        //    于是 IStrategyProfileVersionRepository 从未被真正解析过，缺注册也从未暴露。
        services.AddGovernanceRepositories();
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        // 复用 ContinuityRedLine 的极简 Console Provider（本工程未引 Microsoft.Extensions.Logging.Console）。
        // 真实跑批必须看得到日志 —— 失败时要能从日志定位是装载、Pegging 还是求解。
        services.AddLogging(b => b.AddProvider(new ConsoleOutLoggerProvider()));

        // 具体类不在接口扫描范围，必须显式注册（与 RealSchedulingIntegrationTest 同口径）
        services.AddScoped<SchedulingOrchestrator>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 环境变量闸门：默认**跳过**。
    /// 本测试会往 APS_Production 写真实域的全量结果并跑满主链（分钟级、重资源），
    /// 绝不能因为一次宽泛的 <c>dotnet test</c> 被误触发。要跑必须显式开门：
    /// <code>APS_REAL_DOMAIN_RUN=1 dotnet test --filter FullyQualifiedName~RealDomainFullRunTest</code>
    ///
    /// ⚠️ 本入口建的 <c>MANUAL_*</c> 壳**没有** <c>OrderBomRequestLink</c>（OBRL 由夜间接货按 PV 生成）。
    ///   自 2026-10-17 起 Pegging 对「无关联批」**Fail-Closed**（不再回落 `APS_BOM_RAW` 最新批），
    ///   故直接跑本入口会在步骤 3 得到 <c>IsSuccess=false</c> / <c>ErrorMessage=BOM 输入不可用…</c>。
    ///   要跑**带 BOM 的真实口径**，请改用 <see cref="RunExistingPlanVersionAsync"/> 点名夜间 PV。
    /// </summary>
    private const string GateEnvVar = "APS_REAL_DOMAIN_RUN";

    [SkippableFact(DisplayName = "2号位: 真实域 FAMILY_X 全量跑通留痕（生产口径，结果不回滚）")]
    public async Task RunRealDomainAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"真实域全量跑批默认关闭（会写 APS_Production 并跑满主链）。设 {GateEnvVar}=1 显式开启。");

        using var runCts = CreateRunCancellation("RunRealDomain");
        var ct = runCts.Token;

        using var provider = BuildProductionProvider();
        var connectionManager = provider.GetRequiredService<DatabaseConnectionManager>();
        var schedulingOrchestrator = provider.GetRequiredService<SchedulingOrchestrator>();
        var orderLoadingService = provider.GetRequiredService<IOrderLoadingService>();
        _connectionManager = connectionManager;

        // ── 步骤 0：策略包版本 —— 以库为准，不盲信常量 ─────────────────────
        LogBuildProvenance("RunRealDomain");
        var strategyVersionId = await ResolveDefaultStrategyVersionIdAsync(ct);
        _output.WriteLine($"[0] 生产默认策略包版本 = {strategyVersionId}");

        // ── 步骤 1：Run + PlanVersion 壳（已存在则复用，保证可重跑）────────
        var (runId, planVersionId, reused) = await EnsureRunAndPlanVersionAsync(strategyVersionId, ct);
        _output.WriteLine($"[1] ScheduleRunId={runId}, PlanVersionId={planVersionId} ({(reused ? "复用既有" : "新建")})");

        // ── 步骤 2：灌订单（Order_Canonical → [Order]，纯增量幂等）─────────
        var orderCountBefore = await CountAsync("SELECT COUNT(*) FROM [Order] WHERE PlanVersionId = @Id", planVersionId, ct);
        var loaded = await orderLoadingService.LoadOrdersToPartitionTableAsync(planVersionId, ct);
        var orderCountAfter = await CountAsync("SELECT COUNT(*) FROM [Order] WHERE PlanVersionId = @Id", planVersionId, ct);
        _output.WriteLine($"[2] 订单装载: 本次新增 {loaded} 条，装载前 {orderCountBefore} → 装载后 {orderCountAfter}");

        Assert.True(orderCountAfter > 0,
            $"PlanVersionId={planVersionId} 的 [Order] 仍为 0 行 —— 订单装载未生效。"
            + " 前置条件：PlanVersion.DomainKey 非空 且 有 IsActive=1 的 DomainDefinition，否则 SP 会 RAISERROR。");

        // ── 步骤 3：跑主链（生产 DI：冻结快照 + 投影层，无 Fixture）────────
        var result = await schedulingOrchestrator.RunSchedulingAndFinalizeAsync(
            planVersionId, runId, strategyVersionId, ct);

        _output.WriteLine($"[3] RunSchedulingAndFinalizeAsync: IsSuccess={result.IsSuccess}, "
            + $"ErrorMessage={result.ErrorMessage ?? "(null)"}");

        // ── 步骤 4：落库证据（无论成败都打印，失败时正是要看的）───────────
        await DumpEvidenceAsync(runId, planVersionId, ct);

        Assert.True(result.IsSuccess,
            $"主链求解未成功：PlanVersionId={planVersionId}, RunId={runId}, Error={result.ErrorMessage}");
    }

    /// <summary>
    /// 【2号位 · 既有 PlanVersion 单域重跑（诊断入口）】对**已存在**的 PlanVersion 走手动入口
    /// <see cref="SchedulingOrchestrator.RunSchedulingAsync(int, long, CancellationToken)"/> 重跑。
    ///
    /// **为什么需要它**：2026-10-08 真实域跑批出现「22,135 条需求进求解器 → 722s → FinalTask=0」，
    ///   而 <see cref="RunRealDomainAsync"/> 走的是 <c>MANUAL_*</c> 新壳 —— 该壳**没有**
    ///   <c>OrderBomRequestLink</c>（OBRL 由 bom-request-push 按 PV 生成，新壳拿不到），
    ///   于是 Pegging 回退「按 MaterialId 全量装载」、BOM 快照为空。
    ///   夜间壳（<c>NIGHTLY_20261008_FAMILY_X</c>）才带 OBRL 与已落库的 <c>APS_BOM_RAW</c>。
    ///   要复现**带 BOM 的真实口径**，必须能点名既有 PV 重跑，而不是每次新建壳。
    ///
    /// 与 <see cref="RunRealDomainAsync"/> 的差别：本入口 <c>scheduleRunId=0</c> ⇒
    ///   <c>LoadSchedulingContextAsync</c> 的 <c>if (scheduleRunId &gt; 0)</c> 守卫使 **MES 进度快照不装载**。
    ///   诊断「求解器为什么丢弃需求」不受影响；但**连续性切片**依赖 MES 进度，
    ///   在本入口下恒为空 —— **不要用本入口验连续性**。
    ///
    /// 开门：<code>APS_REAL_DOMAIN_RUN=1 APS_REAL_DOMAIN_PV_ID=2 dotnet test --filter FullyQualifiedName~RunExistingPlanVersionAsync</code>
    /// </summary>
    [SkippableFact(DisplayName = "2号位: 既有 PlanVersion 单域重跑（诊断入口，生产口径）")]
    public async Task RunExistingPlanVersionAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"真实域跑批默认关闭（会写 APS_Production 并跑满主链）。设 {GateEnvVar}=1 显式开启。");

        var pvIdRaw = Environment.GetEnvironmentVariable("APS_REAL_DOMAIN_PV_ID");
        Skip.IfNot(int.TryParse(pvIdRaw, out var planVersionId) && planVersionId > 0,
            $"未指定 APS_REAL_DOMAIN_PV_ID（当前值 '{pvIdRaw ?? "(null)"}'）—— 本入口必须显式点名既有 PlanVersionId。");

        using var runCts = CreateRunCancellation("RunExistingPlanVersion");
        var ct = runCts.Token;

        using var provider = BuildProductionProvider();
        _connectionManager = provider.GetRequiredService<DatabaseConnectionManager>();
        var schedulingOrchestrator = provider.GetRequiredService<SchedulingOrchestrator>();

        LogBuildProvenance("RunExistingPlanVersion");
        var strategyVersionId = await ResolveDefaultStrategyVersionIdAsync(ct);

        // 反查既有 PV 的归属 Run（只为 DumpEvidence 打印终态，不参与执行）
        var sourceRunId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @Id",
            new { Id = planVersionId }, db: DatabaseId.APS) ?? 0;

        _output.WriteLine($"[0] PlanVersionId={planVersionId}, SourceScheduleRunId={sourceRunId}, 策略包版本={strategyVersionId}");

        // ── 求解器内部计数器采集 ────────────────────────────────────────────────
        //   `SolverDiagnostics` 是 1号位 侧的 `internal` 类型，本程序集经
        //   `InternalsVisibleTo("LPS.APS.Tests")`（`LPS.APS.Scheduling.csproj:23`）可见。
        //   生产 Run 只留进程内预算快照、**不打印**这些数 ⇒ 由本诊断工装显式开作用域采集，
        //   一次跑批即可拿到 Phase1..5 分段耗时与热点累计（谁最慢、被调多少次）。
        //   作用域走 `AsyncLocal`，`RunSchedulingAsync` 内部的 await 链沿用同一上下文。
        using var solverScope = LPS.APS.Scheduling.Solvers.SolverDiagnostics.BeginScope();

        // ── 运行中采样（2026-10-26 增）───────────────────────────────────────────
        //   `DumpSolverCounters` 写在 `RunSchedulingAsync` **之后** —— 求解器不返回就永远执行不到。
        //   10-25（31分12秒）／10-26 上午／10-26 中午（≈57分11秒）三次求解器零返回，
        //   那几行一次都没跑到。采样器把「跑完之后才看」改成「每 5 秒落一行 CSV」，
        //   就算再被掐掉，卡在哪一 Phase、内存在不在涨也都留得下来。见 SolverLiveSampler 注释。
        using var sampler = SolverLiveSampler.Start(solverScope.Counters, planVersionId, _output);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SchedulingRunResult? result = null;
        var cancelled = false;
        try
        {
            result = await schedulingOrchestrator.RunSchedulingAsync(planVersionId, strategyVersionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 超时取消走这里：1号位 r13908 的协作式取消在阶段边界/每需求/每修复单元检查令牌，
            // 所以栈是**正常退的**，不是被 kill —— 下面的 finally 因此能真的执行到。
            cancelled = true;
        }
        finally
        {
            sw.Stop();

            // 先停采样再 Dump：保证 CSV 的最后一拍与下面的汇总取自同一终态。（Stop 幂等，Dispose 会再调一次也不重复落行。）
            sampler.Stop();

            // ⚠ 必须在 finally 里 Dump：取消/异常路径下，这一行是**唯一**能拿到计数器的机会。
            DumpSolverCounters(solverScope.Counters);
        }

        if (cancelled)
        {
            _output.WriteLine($"[1] RunSchedulingAsync 在 {sw.Elapsed.TotalMinutes:0.#} 分钟后**超时取消**"
                + $"（APS_RUN_TIMEOUT_MIN={_runTimeoutMinutes:0.##}）—— 求解器未返回，本入口不判失败。"
                + " 卡在哪一步看上面的 [SD] 分段计数与同名 CSV 的逐拍读数。");

            // 证据用**不受取消影响**的新令牌：跑到这里就是为了看现场，不能因为令牌已取消而丢。
            await DumpEvidenceAsync(sourceRunId, planVersionId, CancellationToken.None);
            return;
        }

        _output.WriteLine($"[1] RunSchedulingAsync: IsSuccess={result!.IsSuccess}, Error={result.ErrorMessage ?? "(null)"}, "
            + $"Scheduled={result.ScheduledCount}, Unscheduled={result.UnscheduledCount}, 耗时={sw.ElapsedMilliseconds}ms");

        if (result!.Metrics is { } m)
        {
            _output.WriteLine($"     Metrics: Demand={m.DemandCount}, LPD={m.LogicalProductionDemandCount}, "
                + $"FinalTask={m.FinalTaskCount} | ContextBuild={m.ContextBuildMs}ms, Pegging={m.PeggingMs}ms, "
                + $"Solver={m.SolverMs}ms, Persist={m.PersistMs}ms, Total={m.TotalMs}ms");
        }
        else
        {
            _output.WriteLine("     Metrics=(null)");
        }

        // 落库证据：无论成败都摊开（0 任务时正是要看现场）。
        // ⚠ 用 CancellationToken.None：跑了半小时才等到结果，不能因为超时令牌刚好响在收尾阶段就把证据丢掉。
        await DumpEvidenceAsync(sourceRunId, planVersionId, CancellationToken.None);

        // 本入口是**诊断**入口，不把「0 任务」判成测试失败 —— 求解器的丢弃原因在日志里，
        // 断言在此只会把「拿到了证据」误报成红。
    }

    /// <summary>
    /// 摊开求解器内部计数器（1号位 <c>SolverDiagnostics</c>，internal + InternalsVisibleTo）。
    ///
    /// 目的：一次真实域跑批拿到「Phase1..5 各多少 ms」「哪个热点最贵、被调多少次」。
    /// 判定「慢」必须先分清**单次开销大**（µs/次高）还是**调用次数多**（次数高）——
    /// 两者的整改方向完全相反。纯观测，不参与任何业务判定、不改任何取值。
    /// 热点耗时含嵌套，各项之和会大于 Phase 耗时，用途是排序而非拆分。
    /// </summary>
    private void DumpSolverCounters(LPS.APS.Scheduling.Solvers.SolverDiagnostics.Counters c)
    {
        _output.WriteLine($"[SD] Phase: P1={c.Phase1Ms}ms P2={c.Phase2Ms}ms P3={c.Phase3Ms}ms "
            + $"P4={c.Phase4Ms}ms P5={c.Phase5Ms}ms");

        _output.WriteLine($"[SD] 计数: 批方案候选试跑={c.BatchPlanCandidateTrials:N0} "
            + $"RunBatchPlan={c.RunBatchPlanInvocations:N0} 合批扫描={c.MergeScanCalls:N0} "
            + $"找槽={c.SlotSearches:N0} 占用整表拷贝={c.CloneOccupancy:N0} "
            + $"任务表拷贝={c.CloneTaskList:N0} 份额拷贝={c.CloneShares:N0} "
            + $"回滚未命中={c.OccupancyRollbackUnmatched:N0}");

        // 槽位名以 1号位 `SolverDiagnostics.Hotspot` 的常量名为准（索引不可猜，逐项引用）。
        var hotspots = new (string Name, int Slot)[]
        {
            ("TimelineClone(时间线克隆)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.TimelineClone),
            ("CloneOccupancy(占用整表拷贝)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.CloneOccupancy),
            ("CloneTaskList(任务表拷贝)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.CloneTaskList),
            ("CloneShares(份额拷贝)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.CloneShares),
            ("SlotSearch(找槽)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.SlotSearch),
            ("BatchPlanTrial(含嵌套)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.BatchPlanTrial),
            ("DemandSchedule(含嵌套)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.DemandSchedule),
            ("ScheduleDemandOps", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.ScheduleDemandOps),
            ("TryMergeStageBatch(含嵌套)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.TryMergeStageBatch),
            ("MergeBatchDetach(试排摘除)", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.MergeBatchDetach),
            ("DetachOccupancy", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.DetachOccupancy),
            ("DetachTimeline", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.DetachTimeline),
            ("MergeAnchorScan", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.MergeAnchorScan),
            ("Phase4Propagation", LPS.APS.Scheduling.Solvers.SolverDiagnostics.Hotspot.Phase4Propagation),
        };

        var rows = hotspots
            .Select(h => (h.Name, Ms: c.HotspotMs(h.Slot), Calls: c.HotspotCallCount(h.Slot)))
            .Where(h => h.Ms > 0)
            .OrderByDescending(h => h.Ms)
            .Take(12);

        foreach (var (name, ms, calls) in rows)
        {
            var perCall = calls > 0 ? ms * 1000d / calls : 0d;
            _output.WriteLine($"[SD] 热点 {name}: {ms:N0}ms / {calls:N0} 次 = {perCall:N1}µs/次");
        }
    }

    /// <summary>
    /// 【2026-10-26 增】跑批总超时令牌。环境变量 <c>APS_RUN_TIMEOUT_MIN</c>（分钟，可带小数）。
    /// 未设 / 非法 / ≤0 ⇒ **不限时**（与历史行为完全一致，老命令照旧可用）。
    ///
    /// <para><b>为什么需要它</b>：1号位 r13908 落的是<b>协作式取消</b> —— 阶段边界 ＋ Phase2 每需求 ＋
    /// Phase4 每修复单元各查一次令牌。但我方夹具一直传 <c>CancellationToken.None</c> ⇒
    /// <b>他那套能力对我们是空转</b>。结果是求解器不返回时只能 <c>taskkill /F</c>：
    /// 进程被杀 ⇒ <see cref="DumpSolverCounters"/> 的 <c>finally</c> 不执行、整轮一个数都留不下。
    /// 给它一个真令牌，到点就在协作点<b>正常退栈</b>，计数器与落库证据都还在。</para>
    ///
    /// <para><b>作用域</b>：令牌覆盖整个测试体（含装载与求解）。用途是「不要无限等人」，不是掐某一段。</para>
    /// </summary>
    private CancellationTokenSource CreateRunCancellation(string entry)
    {
        var cts = CreateRunCancellationCore(
            Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var minutes);
        _runTimeoutMinutes = minutes;

        if (minutes > 0d)
        {
            _output.WriteLine($"[T] {entry}: 跑批总超时 = {minutes:0.##} 分钟（APS_RUN_TIMEOUT_MIN）。"
                + " 到点在协作取消点正常退栈，计数器和落库证据仍会落。");
        }
        else
        {
            _output.WriteLine($"[T] {entry}: 未设 APS_RUN_TIMEOUT_MIN ⇒ **不限时**。"
                + " 求解器不返回时只能人肉掐断，而 kill 掉的进程 DumpSolverCounters 一行都不会执行 —— 建议设一个。");
        }

        return cts;
    }

    /// <summary>
    /// <see cref="CreateRunCancellation"/> 的纯逻辑部分（不读环境变量、不写输出 ⇒ 可直接单测）。
    /// 解析失败、为空、≤0 一律**不限时**：宁可等，也不许因为一个笔误把正常跑批掐掉。
    /// </summary>
    internal static CancellationTokenSource CreateRunCancellationCore(string? rawMinutes, out double minutes)
    {
        minutes = 0d;

        var cts = new CancellationTokenSource();
        if (double.TryParse(rawMinutes, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0d)
        {
            minutes = parsed;
            cts.CancelAfter(TimeSpan.FromMinutes(parsed));
        }

        return cts;
    }

    /// <summary>
    /// 运行日志目录：优先环境变量 <c>APS_RUN_LOG_DIR</c>；否则从当前目录上溯 8 级找
    /// <c>LPS.APS.sln</c>，落到 <c>Database/Scripts/APS/04_测试与备份/运行日志/</c>；找不到仓库根就落当前目录。
    ///
    /// <para>抽出来是为了让「求解器逐拍 CSV」与「二进制溯源」落在<b>同一处</b> ——
    /// 两者都是<b>跑批挂了才要看</b>的证据，出事时还要满仓找两个地方，正是最不该花时间的动作。</para>
    /// </summary>
    internal static string ResolveRunLogDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("APS_RUN_LOG_DIR");
        if (overridden is { Length: > 0 })
        {
            return overridden;
        }

        var cwd = System.IO.Directory.GetCurrentDirectory();
        var probe = new System.IO.DirectoryInfo(cwd);
        for (var i = 0; i < 8 && probe is not null; i++, probe = probe.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(probe.FullName, "LPS.APS.sln")))
            {
                return System.IO.Path.Combine(
                    probe.FullName, "Database", "Scripts", "APS", "04_测试与备份", "运行日志");
            }
        }

        return cwd;
    }

    /// <summary>
    /// 【2026-10-26 增】把本次**实际加载的二进制**构建时刻打进日志最前面。
    ///
    /// <para><b>由来</b>：10-26 中午那一轮用 <c>--no-build</c> 跑，真正执行的是
    /// <c>LPS.APS.Tests/bin/Debug/net8.0/LPS.APS.Application.dll</c>（11:52:28），
    /// 而我方源码已改到 12:15:27 —— <b>④ 侧改动一行都没跑</b>，事后靠手比 mtime 才发现，白等一小时。
    /// 判据从此前置：日志开头就有 mtime，跑完第一件事是对它对不对得上源码。</para>
    ///
    /// <para><b>为什么取 <c>Assembly.Location</c> 而不是各项目的 <c>bin/</c></b>：两者可以不同，
    /// 当初踩的就是这个坑 —— 单独 build <c>LPS.APS.Application</c> 只会更新它自己的 <c>bin/</c>，
    /// 而测试宿主加载的是被复制到 <c>LPS.APS.Tests/bin/</c> 的那一份。</para>
    /// </summary>
    private void LogBuildProvenance(string tag = "run")
    {
        var items = new (string Name, System.Reflection.Assembly Asm)[]
        {
            ("Application ", typeof(SchedulingOrchestrator).Assembly),
            ("Scheduling  ", typeof(LPS.APS.Scheduling.Solvers.SolverDiagnostics).Assembly),
            ("Engine      ", typeof(DatabaseConnectionManager).Assembly),
            ("Tests(宿主) ", typeof(RealDomainFullRunTest).Assembly),
        };

        var lines = new List<string>
        {
            $"[BIN] 本次实际加载的二进制（跑完先比对这里与源码改动时刻，别再空跑一轮）"
            + $"  entry={tag}  at={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        };

        foreach (var (name, asm) in items)
        {
            var loc = asm.Location;
            if (string.IsNullOrEmpty(loc))
            {
                lines.Add($"[BIN] {name}: (无文件，动态程序集) {asm.GetName().Version}");
                continue;
            }

            var mtime = System.IO.File.Exists(loc)
                ? System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss")
                : "(文件不存在)";
            lines.Add($"[BIN] {name}: {mtime}  {loc}");
        }

        foreach (var line in lines)
        {
            _output.WriteLine(line);
        }

        // ⚠ **必须同时落盘**：`_output` 只在测试方法正常结束时才交给 runner。
        //   实测 2026-10-26 那一轮被掐断，这几行 `grep '^\[BIN\]'` = 0 命中 ——
        //   而它的唯一用途就是「跑挂了要查」，等于在最需要它的一次静默失效。
        //   见 memory `xunit-output-buffered-until-test-end`。
        try
        {
            var dir = ResolveRunLogDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(
                dir, $"build-provenance_{DateTime.Now:yyyyMMdd_HHmmss}_{tag}.txt");
            System.IO.File.WriteAllLines(path, lines);
            _output.WriteLine($"[BIN] 已落盘（即使进程被 kill 也还在）：{path}");
        }
        catch (Exception ex)
        {
            // 观测失败绝不能影响跑批。
            _output.WriteLine($"[BIN] 落盘失败（不影响跑批）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 【为什么有这个东西】2026-10-26 一天之内第三次撞上「⑤ 求解器零返回」
    ///    （10-25 31分12秒 / 10-26 上午未记 / 10-26 中午 ≈57分11秒），三次都由我方人肉掐掉。
    ///    本类原有的 <see cref="DumpSolverCounters"/> 写在 <c>RunSchedulingAsync</c> <b>之后</b> ——
    ///    求解器不返回，那一行就永远不执行。<b>计数器明明拿得到，偏偏在最需要的时候一个数都没留下。</b>
    ///
    /// <para><b>【为什么拿得到】</b><c>SolverDiagnostics</c> 确实是 1号位 的 <c>internal</c>，但本程序集经
    /// <c>InternalsVisibleTo("LPS.APS.Tests")</c>（<c>LPS.APS.Scheduling.csproj:23</c>）可见；
    /// <c>BeginScope()</c> 返回的 <c>Counters</c> 是<b>活对象</b>，后台线程直接读即可，
    /// <b>不需要 1号位 再开放任何出口</b>。他 <c>SolverDiagnostics.cs:770</c> 的原话：
    /// 「后台线程可以直接轮询 …<b>不必等求解返回</b>」。</para>
    ///
    /// <para><b>【采什么】</b>两栏合一，正好能把内存问题拆开：</para>
    /// <list type="bullet">
    /// <item>求解器侧：<c>CurrentPhase</c> ＋ Phase1..5 已完成耗时 ＋ Phase2/4 处理进度 ＋ 合批/找槽/对象拷贝计数
    ///       ⇒ 卡在<b>哪一步</b>、哪一步在<b>原地打转</b>（计数涨但不推进）。</item>
    /// <item>进程侧：RSS(<c>WorkingSet64</c>) ＋ 托管堆(<c>GC.GetTotalMemory(false)</c>) ＋
    ///       <c>TotalProcessorTime</c> ＋ 线程数 ⇒ 分开「回滚没干净（对象泄漏）/ 活对象真高 / 工作集没归还 OS」，
    ///       并区分「<b>在算</b>」（cpu_ms 随墙钟线性涨）与「<b>在停</b>」（cpu_ms 不动）。</item>
    /// </list>
    ///
    /// <para><b>【落盘】</b>CSV 一行一拍，<b>周期写盘</b>（不是进程退出才写）⇒ 进程被 kill 也不丢已采到的拍。</para>
    ///
    /// <para><b>【边界】</b>纯观测：不写求解器状态、不改任何取值、不参与任何判定。
    /// 每拍都是标量读 ＋ <c>Process.Refresh()</c>，默认 5 秒一拍，对热路径影响可忽略。
    /// 采样线程内异常全部吞掉 —— <b>观测失败绝不能把跑批搞挂</b>。</para>
    /// </summary>
    internal sealed class SolverLiveSampler : IDisposable
    {
        internal const string Header =
            "elapsed_s,phase,p1_ms,p2_ms,p3_ms,p4_ms,p5_ms,"
            + "p2_done,p2_total,p4_done,p4_total,"
            + "batch_trials,run_batch_plan,merge_scan,slot_search,"
            + "clone_occ,rollback_unmatched,clone_task,clone_shares,"
            + "rss_mb,heap_mb,cpu_ms,threads";

        private readonly LPS.APS.Scheduling.Solvers.SolverDiagnostics.Counters _counters;
        private readonly string _path;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Diagnostics.Process _proc = System.Diagnostics.Process.GetCurrentProcess();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private Thread? _thread;
        private int _stopped;

        internal SolverLiveSampler(
            LPS.APS.Scheduling.Solvers.SolverDiagnostics.Counters counters, string path, TimeSpan interval)
        {
            _counters = counters;
            _path = path;
            _interval = interval;
        }

        /// <summary>CSV 落点。单测要用它读回断言（表头与列数是最容易漂的东西）。</summary>
        internal string CsvPath => _path;

        public static SolverLiveSampler Start(
            LPS.APS.Scheduling.Solvers.SolverDiagnostics.Counters counters,
            long planVersionId,
            ITestOutputHelper output)
        {
            var intervalSec = double.TryParse(
                Environment.GetEnvironmentVariable("APS_SOLVER_SAMPLE_INTERVAL_S"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 1d
                ? s
                : 5d;

            var sampler = new SolverLiveSampler(counters, ResolveCsvPath(planVersionId), TimeSpan.FromSeconds(intervalSec));
            sampler.StartCore();

            output.WriteLine($"[SD-LIVE] 运行中采样已启动：每 {intervalSec:0.#}s 一拍 → {sampler._path}");
            output.WriteLine("[SD-LIVE] 求解器要是又卡住，这张 CSV 每 5 秒会多 1 行："
                + "看 phase 停在哪一步、p2_done/p2_total 还推不推进、"
                + "rss_mb / heap_mb 是否单调上行、cpu_ms 是否随 elapsed_s 线性增长（涨＝在算，不涨＝在停）。");
            return sampler;
        }

        /// <summary>
        /// CSV 落点：环境变量 <c>APS_SOLVER_SAMPLE_CSV</c> 优先；否则落 <see cref="ResolveRunLogDirectory"/>
        /// （默认＝仓库内 <c>Database/Scripts/APS/04_测试与备份/运行日志/</c>）。
        /// 文件名带 PV 与时刻，不与历史轮次混。
        /// </summary>
        private static string ResolveCsvPath(long planVersionId)
        {
            var overridden = Environment.GetEnvironmentVariable("APS_SOLVER_SAMPLE_CSV");
            if (overridden is { Length: > 0 })
            {
                return overridden;
            }

            return System.IO.Path.Combine(
                ResolveRunLogDirectory(),
                $"PV{planVersionId}_solver-live_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        }

        internal void StartCore()
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            System.IO.File.WriteAllText(_path, Header + Environment.NewLine);

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "aps-solver-live-sampler",
            };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_cts.Token.WaitHandle.WaitOne(_interval))
            {
                try
                {
                    SampleOnce(_clock.Elapsed.TotalSeconds);
                }
                catch
                {
                    // 观测失败绝不能影响跑批：磁盘满、文件被占、计数器读不到 —— 一律吞掉，下一拍再来。
                }
            }
        }

        private void SampleOnce(double elapsedSeconds)
        {
            _proc.Refresh();
            var c = _counters;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var line = string.Concat(
                elapsedSeconds.ToString("F1", inv), ",",
                CsvEscape(c.CurrentPhase), ",",
                c.Phase1Ms.ToString(inv), ",", c.Phase2Ms.ToString(inv), ",",
                c.Phase3Ms.ToString(inv), ",", c.Phase4Ms.ToString(inv), ",",
                c.Phase5Ms.ToString(inv), ",",
                c.Phase2DemandsProcessed.ToString(inv), ",", c.Phase2DemandsTotal.ToString(inv), ",",
                c.Phase4UnitsProcessed.ToString(inv), ",", c.Phase4UnitsTotal.ToString(inv), ",",
                c.BatchPlanCandidateTrials.ToString(inv), ",", c.RunBatchPlanInvocations.ToString(inv), ",",
                c.MergeScanCalls.ToString(inv), ",", c.SlotSearches.ToString(inv), ",",
                c.CloneOccupancy.ToString(inv), ",", c.OccupancyRollbackUnmatched.ToString(inv), ",",
                c.CloneTaskList.ToString(inv), ",", c.CloneShares.ToString(inv), ",",
                (_proc.WorkingSet64 / 1048576d).ToString("F1", inv), ",",
                (GC.GetTotalMemory(false) / 1048576d).ToString("F1", inv), ",",
                ((long)_proc.TotalProcessorTime.TotalMilliseconds).ToString(inv), ",",
                _proc.Threads.Count.ToString(inv));

            System.IO.File.AppendAllText(_path, line + Environment.NewLine);
        }

        private static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        /// <summary>停止采样并补最后一拍。<b>幂等</b>：先手动 Stop 一次、Dispose 再调一次也不会重复落行。</summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // ignore
            }

            try
            {
                _thread?.Join(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignore
            }

            // 末尾一拍：让 CSV 的末行就是「求解返回/被取消」那一刻的状态，与 DumpSolverCounters 对齐。
            try
            {
                SampleOnce(_clock.Elapsed.TotalSeconds);
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            Stop();

            try
            {
                _cts.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _proc.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// 【2号位 · 夜间发令枪 seam 真跑】验 P1-03 抽出的 <c>ExecuteRunAsync</c> 在真实库上的行为：
    ///   ① 首次调用**真的跑完**整个域 —— 即重入守卫**不得误伤首跑**（本次改动最需证伪的风险点）；
    ///   ② 第二次调用**命中重入守卫、不重跑**。
    ///
    /// **为什么必须真跑、单测为什么不够**：`RunSchedulingAutoAsync`（本次被抽走方法体的那个）
    ///   **全仓没有任何测试调用它** —— 唯一调用方是 Hangfire 定时任务
    ///   （<c>HangfireServiceExtensions</c> 的 <c>RecurringJob</c>）。P1-03 把它的方法体搬进
    ///   <c>ExecuteRunAsync</c>，编译能过、单测全绿，但**那条路径此前一行都没被覆盖过**。
    ///   （同源教训：Fixture 顶掉生产 Provider ⇒ 生产能不能跑，集成测试证明不了。）
    ///
    /// 运行：<c>APS_REAL_DOMAIN_RUN=1 dotnet test --filter FullyQualifiedName~RunRealSeamAsync</c>
    /// </summary>
    [SkippableFact(DisplayName = "2号位: ExecuteRunAsync 夜间 seam 真跑 + 重入守卫生效（生产口径，结果不回滚）")]
    public async Task RunRealSeamAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"真实域全量跑批默认关闭（会写 APS_Production 并跑满主链）。设 {GateEnvVar}=1 显式开启。");

        using var runCts = CreateRunCancellation("RunRealSeam");
        var ct = runCts.Token;
        var versionCode = $"SEAM_{DateTime.Today:yyyyMMdd}_{DomainKey}";

        using var provider = BuildProductionProvider();
        var connectionManager = provider.GetRequiredService<DatabaseConnectionManager>();
        var orchestrator = provider.GetRequiredService<SchedulingOrchestrator>();
        var orderLoadingService = provider.GetRequiredService<IOrderLoadingService>();
        _connectionManager = connectionManager;

        LogBuildProvenance("RunRealSeam");
        var strategyVersionId = await ResolveDefaultStrategyVersionIdAsync(ct);
        _output.WriteLine($"[0] 生产默认策略包版本 = {strategyVersionId}");

        var (runId, planVersionId, reused) = await EnsureRunAndPlanVersionAsync(strategyVersionId, ct, versionCode);
        _output.WriteLine($"[1] ScheduleRunId={runId}, PlanVersionId={planVersionId} ({(reused ? "复用既有" : "新建")})");

        // 复用既有 Run/PV 时把 PV 打回 'BUILDING' —— 否则上一次留下的终态会让**第 1 次**调用
        // 直接命中重入守卫（守卫行为正确，但本测试要验的是「首跑真的跑」，会被自己绊倒）。
        // Run 侧由 EnsureRunAndPlanVersionAsync 的复用分支打回 RUNNING。
        await _connectionManager.ExecuteAsync(
            "UPDATE PlanVersion SET Status = 'BUILDING', ComputedAt = NULL WHERE Id = @Id",
            new { Id = planVersionId }, db: DatabaseId.APS);

        var loaded = await orderLoadingService.LoadOrdersToPartitionTableAsync(planVersionId, ct);
        var orderCount = await CountAsync("SELECT COUNT(*) FROM [Order] WHERE PlanVersionId = @Id", planVersionId, ct);
        _output.WriteLine($"[2] 订单装载: 本次新增 {loaded} 条，现 {orderCount} 行");
        Assert.True(orderCount > 0,
            $"PlanVersionId={planVersionId} 的 [Order] 为 0 行 —— 装载未生效，前置不满足。");

        // ── 调用 1：首次执行 ──────────────────────────────────────────────
        var first = await orchestrator.ExecuteRunAsync(runId, ct);
        _output.WriteLine($"[3] 第 1 次 ExecuteRunAsync: IsSuccess={first.IsSuccess}, "
            + $"ScheduledCount={first.ScheduledCount}, Error={first.ErrorMessage ?? "(null)"}");

        Assert.True(first.IsSuccess, $"首次 ExecuteRunAsync 未成功：{first.ErrorMessage}");
        Assert.True(first.ScheduledCount > 0,
            "首次 ExecuteRunAsync 的 ScheduledCount=0 —— 重入守卫**误伤首跑**，或求解未产出任务。"
            + " 这正是本次改动最需要证伪的风险点。");

        var pvStatusAfterFirst = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM PlanVersion WHERE Id = @Id", new { Id = planVersionId }, db: DatabaseId.APS);
        var runStatusAfterFirst = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM ScheduleRun WHERE Id = @Id", new { Id = runId }, db: DatabaseId.APS);
        _output.WriteLine($"[4] 首跑终态: PlanVersion.Status={pvStatusAfterFirst}, ScheduleRun.Status={runStatusAfterFirst}");
        // 本测试用的是 FULL_SCHEDULE（夜间全量）Run ⇒ 域成功即**自动正式采用**（Status=ACTIVE）。
        // 白天候选（MANUAL_RESCHEDULE 等）才停在 CANDIDATE 等人工确认。
        Assert.Equal("ACTIVE", pvStatusAfterFirst);
        Assert.Equal("COMPLETED", runStatusAfterFirst);

        // ── 调用 2：重入 —— 必须命中守卫、不重跑 ──────────────────────────
        var second = await orchestrator.ExecuteRunAsync(runId, ct);
        _output.WriteLine($"[5] 第 2 次 ExecuteRunAsync: IsSuccess={second.IsSuccess}, "
            + $"ScheduledCount={second.ScheduledCount}, Error={second.ErrorMessage ?? "(null)"}");

        Assert.Equal(0, second.ScheduledCount);
        Assert.NotNull(second.ErrorMessage);
        Assert.Contains("跳过重跑", second.ErrorMessage!);

        var runStatusAfterSecond = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM ScheduleRun WHERE Id = @Id", new { Id = runId }, db: DatabaseId.APS);
        _output.WriteLine($"[6] 重入后 ScheduleRun.Status={runStatusAfterSecond}（应仍为 COMPLETED）");
        Assert.Equal("COMPLETED", runStatusAfterSecond);

        await DumpEvidenceAsync(runId, planVersionId, ct);
    }

    // ══════════════════════════════════════════════════════════════════
    // 辅助
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析当前生产默认策略包版本：**本 RunType 下** PUBLISHED + IsDefault=1 + 父 Profile IsActive=1 + 有效窗口内。
    /// 0 个 / 多个都**拒绝**（红线 #4：禁止盲目取第一个），与 <c>RunLifecycleService</c> 同口径。
    /// </summary>
    private async Task<long> ResolveDefaultStrategyVersionIdAsync(CancellationToken ct)
    {
        // 【2026-10-26 修正·2号位】原先本查询**漏了 RunType 收窄**，直接扫全库 IsDefault=1 的 PUBLISHED 版本，
        //   于是 1969（Profile 1302 / MANUAL_RESCHEDULE）与 1970（Profile 1303 / FULL_SCHEDULE）同时命中，
        //   被判「歧义」拒绝执行。**这不是数据问题、也不是治理缺陷**：`IsDefault` 本就是「按 Profile（进而按
        //   RunType）各自的默认」，生产侧 `GetDefaultByRunTypeAsync` 恒按 `p.[RunType]=@RunType AND p.[IsActive]=1`
        //   收窄 ⇒ `FULL_SCHEDULE` 下只有 1970 一个，`MANUAL_RESCHEDULE` 下只有 1969 一个，**生产路径从不歧义**。
        //   修法＝把夹具查询对齐生产口径（补 JOIN StrategyProfile + RunType 收窄），而不是放宽唯一性红线。
        // 【2026-10-14 增】环境变量仍可显式点名策略包版本（保留为旁路，不再需要用于绕歧义）。
        var pinned = Environment.GetEnvironmentVariable("APS_REAL_DOMAIN_STRATEGY_VERSION_ID");
        if (long.TryParse(pinned, out var pinnedId) && pinnedId > 0)
        {
            return pinnedId;
        }

        var rows = (await _connectionManager.QueryAsync<long>(
            @"SELECT v.Id
              FROM StrategyProfileVersion v
              INNER JOIN StrategyProfile p ON p.Id = v.StrategyProfileId
              WHERE p.RunType = @RunType
                AND p.IsActive = 1
                AND v.IsDefault = 1
                AND v.Status = 'PUBLISHED'
                AND (v.EffectiveFrom IS NULL OR v.EffectiveFrom <= GETDATE())
                AND (v.EffectiveTo   IS NULL OR v.EffectiveTo   >= GETDATE())",
            new { RunType = HarnessRunType }, db: DatabaseId.APS)).ToList();

        Assert.False(rows.Count == 0,
            $"RunType={HarnessRunType} 下无有效的默认 PUBLISHED 策略包版本（IsDefault=1 且窗口内）—— 跑批无策略可依，拒绝执行。");
        Assert.False(rows.Count > 1,
            $"RunType={HarnessRunType} 下存在 {rows.Count} 个有效默认 PUBLISHED 策略包版本（{string.Join(",", rows)}）—— 歧义，拒绝执行。");

        var resolved = rows[0];
        Assert.True(resolved == ExpectedStrategyProfileVersionId,
            $"生产默认策略包版本已漂移：库里 IsDefault=1 的是 {resolved}，本夹具常量写的 {ExpectedStrategyProfileVersionId}。"
            + " 请确认新版本内容后再改本夹具常量（别跟着改，先看差异）。");

        return resolved;
    }

    /// <summary>
    /// 保证存在本次跑批的 Run + PlanVersion。已存在则复用（同一天重跑不新建，避免 PV 堆积）。
    /// 两个 INSERT 的列集严格对齐 ScheduleRunRepository.CreateCandidateRunAsync。
    /// VersionCategory 用 'DAILY_BASELINE'（与夜间批同身份词表，**不用 TEST** —— 这不是测试残留，
    /// 是真实基线结果，别被残留清理脚本扫掉）。
    /// </summary>
    private async Task<(int RunId, int PlanVersionId, bool Reused)> EnsureRunAndPlanVersionAsync(
        long strategyVersionId, CancellationToken ct, string? versionCode = null)
    {
        // versionCode 可覆盖：夜间 seam 真跑测试用独立 VersionCode，避免与本测试的 MANUAL_ 批互相复用同一条 Run/PV。
        var code = versionCode ?? VersionCode;
        var existing = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            "SELECT Id FROM PlanVersion WHERE VersionCode = @Code",
            new { Code = code }, db: DatabaseId.APS);

        if (existing.HasValue)
        {
            var existingRunId = await _connectionManager.QueryFirstOrDefaultAsync<int?>(
                "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @Id",
                new { Id = existing.Value }, db: DatabaseId.APS);

            Assert.True(existingRunId.HasValue,
                $"既有 PlanVersionId={existing.Value} 的 SourceScheduleRunId 为空 —— 无法收口 Run，拒绝复用。");

            // 复用前把 Run 打回 RUNNING（上次跑完是 COMPLETED；重跑要重新计时）
            // ⚠️ 时间戳一律**本地**（`GETDATE()`）—— 2026-10-14 订正：原为 `GETUTCDATE()`，与
            //    《APS V1》统一本地时间口径冲突，且会污染 ScheduleRun.CreatedAt 一列两种时区的既有毛病。
            await _connectionManager.ExecuteAsync(
                "UPDATE ScheduleRun SET Status = 'RUNNING', StartedAt = GETDATE(), CompletedAt = NULL, DurationSeconds = NULL WHERE Id = @Id",
                new { Id = existingRunId.Value }, db: DatabaseId.APS);

            return (existingRunId.Value, existing.Value, true);
        }

        var now = DateTime.Now;
        var expectedDomainKeysJson = System.Text.Json.JsonSerializer.Serialize(new[] { DomainKey });

        var runId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"INSERT INTO [dbo].[ScheduleRun]
                  ([RunType], [Status], [TriggeredBy], [DataCutoffTime],
                   [BasePlanVersionId], [StrategyProfileVersionId], [ExpectedDomainKeysJson], [ScopeJson],
                   [StartedAt], [CreatedAt])
              OUTPUT INSERTED.[Id]
              VALUES ('FULL_SCHEDULE', 'RUNNING', @TriggeredBy, @DataCutoffTime,
                      NULL, @StrategyProfileVersionId, @ExpectedDomainKeysJson, NULL,
                      @StartedAt, @StartedAt)",
            new
            {
                TriggeredBy = TriggeredBy,
                DataCutoffTime = now,
                StrategyProfileVersionId = strategyVersionId,
                ExpectedDomainKeysJson = expectedDomainKeysJson,
                StartedAt = now,
            }, db: DatabaseId.APS);

        Assert.True(runId > 0, "ScheduleRun 写入未返回 Id");

        var planVersionId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"INSERT INTO [dbo].[PlanVersion]
                  ([VersionCode], [VersionCategory], [DomainKey],
                   [PlanHorizonStart], [PlanHorizonEnd], [ComputeMode], [Status],
                   [SourceScheduleRunId], [CreatedBy], [CreatedAt])
              OUTPUT INSERTED.[Id]
              VALUES (@VersionCode, 'DAILY_BASELINE', @DomainKey,
                      @PlanHorizonStart, @PlanHorizonEnd, 'FULL', 'BUILDING',
                      @SourceScheduleRunId, @CreatedBy, @StartedAt)",
            new
            {
                VersionCode = code,
                DomainKey = DomainKey,
                PlanHorizonStart = DateTime.Today,
                PlanHorizonEnd = DateTime.Today.AddDays(90),
                SourceScheduleRunId = runId,
                CreatedBy = TriggeredBy,
                StartedAt = now,
            }, db: DatabaseId.APS);

        Assert.True(planVersionId > 0, "PlanVersion 写入未返回 Id");
        return (runId, planVersionId, false);
    }

    /// <summary>把证据摊开打印 —— 验收 SQL 之前的现场快照。</summary>
    private async Task DumpEvidenceAsync(int runId, int planVersionId, CancellationToken ct)
    {
        var pv = new { Id = planVersionId };

        var pvStatus = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM PlanVersion WHERE Id = @Id", pv, db: DatabaseId.APS);
        var runStatus = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM ScheduleRun WHERE Id = @Id", new { Id = runId }, db: DatabaseId.APS);

        _output.WriteLine($"[4] 终态: PlanVersion.Status={pvStatus}, ScheduleRun.Status={runStatus}");

        // 【2026-10-26 增·2号位】1号位 回执 §六 的分界数：跑完后「托管堆 vs RSS」。
        //   托管堆 ≈ RSS ⇒ 内存是真占用（设计问题，要看 CloneOccupancy 嵌套深度）；
        //   托管堆 ≪ RSS ⇒ 只是工作集没归还 OS（假象，不用管）。
        //   `forceFullCollection: true` 会阻塞做一次 Full GC —— 只在**跑完之后**调用，不影响任何计时。
        var gcHeapBytes = GC.GetTotalMemory(forceFullCollection: true);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        var rssBytes = proc.WorkingSet64;
        var peakRssBytes = proc.PeakWorkingSet64;
        _output.WriteLine(
            $"[GC] 托管堆={gcHeapBytes / 1024d / 1024d:F0}MB  RSS={rssBytes / 1024d / 1024d:F0}MB  " +
            $"峰值RSS={peakRssBytes / 1024d / 1024d:F0}MB  托管堆占RSS={100d * gcHeapBytes / Math.Max(rssBytes, 1):F1}%  " +
            $"(托管堆≈RSS⇒真占用；托管堆≪RSS⇒工作集未归还OS)");

        foreach (var table in new[]
                 {
                     "[Order]", "[Task]", "[Pegging]", "PeggingSupplyAllocation",
                     "AllocationTaskShare", "ProductionInstructionPositionSnapshot",
                     "OrderScheduleSummary", "ExplainTrace", "FrozenZoneSnapshot",
                 })
        {
            var n = await CountAsync($"SELECT COUNT(*) FROM {table} WHERE PlanVersionId = @Id", planVersionId, ct);
            _output.WriteLine($"      {table,-38} = {n}");
        }

        // 排不下 / 失败原因分布 —— 「产能不足 ≠ 排不下」，Unscheduled 只兜真无解
        var taskStatus = (await _connectionManager.QueryAsync<(string? Status, int N)>(
            "SELECT Status, COUNT(*) AS N FROM [Task] WHERE PlanVersionId = @Id GROUP BY Status ORDER BY N DESC",
            pv, db: DatabaseId.APS)).ToList();
        foreach (var (status, n) in taskStatus)
        {
            _output.WriteLine($"      Task.Status={status ?? "(null)"} → {n}");
        }
    }

    private Task<int> CountAsync(string sql, int planVersionId, CancellationToken ct)
        => _connectionManager.QueryFirstOrDefaultAsync<int>(sql, new { Id = planVersionId }, db: DatabaseId.APS);
}
