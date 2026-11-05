using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 2026-11-05｜**夜间发令枪全量起算**（1号位 自建工装）。
///
/// 【为什么需要本工装】2号位 2026-11-05《夜间批已就绪 v2.0》要求按
///   `ISchedulingOrchestrator.RunSchedulingAutoAsync()` 起算 Run **999401**（11 个域）。
///   而 `RunSchedulingAutoAsync` **全仓无任何测试调用**（`RealDomainFullRunTest.cs:48` 自述），
///   生产侧由 Hangfire job `scheduling-trigger` 触发 —— 不便直接驱动生产 job，
///   故新建本工装：走**与生产完全相同的 DI 组合根**，直调同一方法。
///
/// 【会写库】本入口**全流程求解并入库**（Task / PeggingDraft / AllocationTaskShare /
///   ScheduleExplanationFact / PI Position 等）—— 与 `PV190SolverOnlyPerfTest` 的「只求解不落库」不同。
///   开门：`APS_FULL_AUTO_RUN=1`；不开门则 Skip，避免误跑。
///
/// 【⚠ 中途不要 kill】强杀会把正在算的 `PlanVersion` 停在 `Computing`（僵尸态，需手工复位）。
///   这是 2号位 v2.0 §五 与我方历年记录共同确认过的坑。
///
/// 【2026-11-05 全日志】用户要求「加全日志」，故本工装在**进程内把 `Console.Out` 重定向到文件**，
///   并另起一条 5 秒一次的进度线程。理由与边界见测试方法体里的注释。
/// </summary>
public class FullAutoRunTriggerTest
{
    private readonly ITestOutputHelper _output;

    public FullAutoRunTriggerTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task 夜间发令枪_全量起算_全流程求解并入库()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("APS_FULL_AUTO_RUN") == "1",
            "未设 APS_FULL_AUTO_RUN=1 —— 本工装会**写库**（全流程求解并入库），不默认执行。");

        using var provider = BuildProductionProvider();
        var orchestrator = provider.GetRequiredService<SchedulingOrchestrator>();

        // ── 2026-11-05 全日志重定向 ───────────────────────────────────────────────
        // 【为什么必须在这里做】Run 999401 r3 实测：`dotnet test` 默认 minimal 日志级别会把
        //   测试进程里的 `Console.WriteLine` **整段吞掉** —— 求解器的 `[SOLVER-BEACON]` 与
        //   `[SHAPE]` 全槽分项**一个字都没进日志文件**（r3 的 .log 只有 196 字节 = dotnet test
        //   自己的两行表头）。于是用户看不到任何百分比。
        // 【为什么不在求解器里写文件】`LPS.APS.Scheduling` 有**零 I/O** 约定（不读库、不写库、
        //   不开文件）。在这里重定向，求解器一行代码不用改，红线仍在。
        // 【路径】环境变量 `APS_SOLVER_LOG_FILE` 可覆盖；默认 `D:\tmp\solver_full.log`。
        //   每次 Run **覆盖写**（`FileMode.Create`），不做追加，避免跨 Run 混淆。
        var logPath = Environment.GetEnvironmentVariable("APS_SOLVER_LOG_FILE")
                      ?? @"D:\tmp\solver_full.log";
        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var logFile = new StreamWriter(
            new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        var log = TextWriter.Synchronized(logFile);

        var originalOut = Console.Out;
        Console.SetOut(log);

        using var cts = new CancellationTokenSource();
        Task? progressTask = null;

        log.WriteLine($"==== RUN START {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        log.WriteLine("[FULL] 调用 RunSchedulingAutoAsync（自动领取 RUNNING + 有 BUILDING PlanVersion 的 FULL_SCHEDULE Run）");

        var sw = Stopwatch.StartNew();
        SchedulingRunResult result;
        try
        {
            progressTask = Task.Run(() => ProgressLoop(log, cts.Token));
            result = await orchestrator.RunSchedulingAutoAsync(CancellationToken.None);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.WriteLine($"[FULL] 抛异常 {ex.GetType().Name}: {ex.Message}");
            log.WriteLine(ex.ToString());
            throw;
        }
        finally
        {
            cts.Cancel();
            try { progressTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* 收尾不抛 */ }

            var last = SolverDiagnostics.LastSnapshot;
            if (last is not null)
            {
                log.WriteLine(
                    $"[FULL] 求解末快照 phase={last.CurrentPhase}"
                    + $" p2={last.Phase2DemandsProcessed}/{last.Phase2DemandsTotal}"
                    + $" p4={last.Phase4UnitsProcessed}/{last.Phase4UnitsTotal}"
                    + $" trials={last.BatchPlanCandidateTrials}"
                    + $" seq={last.Sequence}");
            }

            log.WriteLine($"==== RUN END   {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
            log.Flush();

            Console.SetOut(originalOut);
            logFile.Dispose();
        }

        _output.WriteLine($"[FULL] 收口耗时 {sw.Elapsed.TotalMinutes:F1} 分钟（{sw.Elapsed.TotalSeconds:F0} s）");
        _output.WriteLine($"[FULL] IsSuccess={result.IsSuccess}");
        _output.WriteLine($"[FULL] ErrorMessage={result.ErrorMessage ?? "(null)"}");
    }

    /// <summary>
    /// 5 秒一次把求解器实时快照写进日志（含百分比）。
    ///
    /// 【为什么要这条线程】`SolverDiagnostics.ProgressBeacon` 自己 10 秒打一行，但它只在
    ///   `APS_SOLVER_BEACON=1` 时存在、且不带 GC/堆读数。用户要「看得见百分之多少」，
    ///   故在**测试侧**再补一条独立线程 —— 求解器仍是零 I/O，本线程只读公开的 `LiveSnapshot`。
    ///
    /// 【边界】纯观测：只读 `LiveSnapshot` / `GC` 静态读数，不碰求解状态；异常一律吞掉写一行。
    /// </summary>
    private static async Task ProgressLoop(TextWriter log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = SolverDiagnostics.LiveSnapshot;
                var body = s is null
                    ? "phase=(未开始或已结束)"
                    : $"phase={s.CurrentPhase}"
                      + $" p2={s.Phase2DemandsProcessed}/{s.Phase2DemandsTotal}"
                      + (s.Phase2DemandsTotal > 0
                          ? $"({100d * s.Phase2DemandsProcessed / s.Phase2DemandsTotal:F1}%)"
                          : "(?)")
                      + $" p4={s.Phase4UnitsProcessed}/{s.Phase4UnitsTotal}"
                      + $" trials={s.BatchPlanCandidateTrials}"
                      + $" slotSearch={s.SlotSearches}";

                log.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [PROGRESS] {body}"
                    + $" heap={GC.GetTotalMemory(false) / 1024d / 1024d:F0}MB"
                    + $" gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}");
            }
            catch (Exception ex)
            {
                try { log.WriteLine($"[PROGRESS] 采样异常（已吞）{ex.GetType().Name}: {ex.Message}"); }
                catch { /* 日志也写不了就只能放弃 */ }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 与生产组合根逐行对齐（复制自 `RealDomainFullRunTest.BuildProductionProvider`，
    /// 该方法为 `private`，按「不改别人的测试文件」约定不擅自放宽其可见性）。
    /// </summary>
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
        services.AddLogging(b => b.AddProvider(new ConsoleOutLoggerProvider()));
        services.AddScoped<SchedulingOrchestrator>();

        return services.BuildServiceProvider();
    }
}
