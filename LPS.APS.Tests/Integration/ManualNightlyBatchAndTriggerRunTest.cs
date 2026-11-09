using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 手工触发「夜间批」与「排程发令枪」两个 seam（**不是**验收测试，是运维/交接入口）。
///
/// 背景：生产 `Hangfire:CronEnabled=false` ⇒ 13 个 RecurringJob 全部静音，只能手工补跑。
/// `RealDomainFullRunTest` 只覆盖**单域**的两个 seam（`RunSchedulingAndFinalizeAsync` / `ExecuteRunAsync`），
/// **`RunSchedulingAutoAsync` 全仓零测试调用** —— 而它才是线上 02:00 真正跑的那个（一个 Run 下**所有域**挨个跑）。
///
/// 两个方法各自独立门禁：
/// <code>
/// APS_MANUAL_NIGHTLY_BATCH=1  dotnet test --filter "FullyQualifiedName~ManualNightlyBatchAndTriggerRunTest.跑夜间批"
/// APS_MANUAL_TRIGGER=1        dotnet test --filter "FullyQualifiedName~ManualNightlyBatchAndTriggerRunTest.排程发令枪"
/// </code>
///
/// 用法场景：
///   ① **刷新 PV**：数据侧（BOM / 订单 / 主数据 / MSDC）都同步完之后，跑「跑夜间批」——
///      会复用当日 RUNNING 的 ScheduleRun、为每个有效域建/重建 PlanVersion（`Status='BUILDING'`）、
///      装载订单到分区表、接货最新 READY 的 BOM 批次。
///   ② **起算**：跑「排程发令枪」——领取「`RUNNING` + 有 `BUILDING` PV」的 FULL_SCHEDULE Run，
///      调 `ExecuteRunAsync` 把该 Run 下**所有域**挨个算完，收口为 COMPLETED/FAILED，最后跑保留期清理。
///
/// ⚠ 「排程发令枪」**会真的把 Run 算完并收口**，中途 kill 进程会把 PV 留在 `Computing`
///   （与 1号位 那边僵尸 Run 同性质）——要中止请让它跑完，不要 kill。
/// ⚠ 日志直写文件（xUnit 输出要等测试结束才落盘，中途崩会全丢）。
/// </summary>
public class ManualNightlyBatchAndTriggerRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_nightly_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddGovernanceRepositories();
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        services.AddScoped<SchedulingOrchestrator>();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(LogPath));
        });
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "跑夜间批：复用当日 Run + 每域重建 PV + 装载订单 + 接货最新 READY 的 BOM")]
    public async Task 跑夜间批()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_NIGHTLY_BATCH") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_NIGHTLY_BATCH=1（本用例会写生产库）");
            return;
        }

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var nightly = scope.ServiceProvider.GetRequiredService<INightlyBatchOrchestrator>();

        Log($"===== 夜间批开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await nightly.RunAsync();
        sw.Stop();
        Log($"===== 夜间批结束（{sw.Elapsed.TotalSeconds:F1}s）=====");
        Log($"日志：{LogPath}");
    }

    [Fact(DisplayName = "排程发令枪：领取 RUNNING + BUILDING 的 FULL_SCHEDULE Run，把该 Run 下所有域算完")]
    public async Task 排程发令枪()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_TRIGGER") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_TRIGGER=1（本用例会真实求解并收口 Run）");
            return;
        }

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<ISchedulingOrchestrator>();
        var cm = scope.ServiceProvider.GetRequiredService<DatabaseConnectionManager>();

        // 2026-11-07：本用例原先**只记日志、不断言** ⇒ 11/11 域全 `FAILED`、Run 落 `FAILED` 时，
        //   测试仍以退出码 0 通过（真跑 Run 999423 就是这样：全灭却报"测试通过"）。
        //   发令枪是上线验收的唯一执行入口，不能当睁眼瞎 —— 记起算时刻，跑完直接核库。
        var since = DateTime.Now;   // 本地时间（全链路禁 UTC，库里 GETDATE() 也是本地时钟）

        Log($"===== 排程发令枪开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await scheduler.RunSchedulingAutoAsync();
        sw.Stop();
        Log($"===== 发令枪结束（{sw.Elapsed.TotalSeconds:F1}s）IsSuccess={result.IsSuccess} Error={result.ErrorMessage} =====");
        Log($"日志：{LogPath}");

        // ── 收口核验：Run 必须收口、且没有任何域落 FAILED ──
        var run = await cm.QueryFirstOrDefaultAsync<RunStateRow>(
            @"SELECT TOP 1 Id, Status FROM ScheduleRun
              WHERE  RunType = 'FULL_SCHEDULE' AND StartedAt >= @Since
              ORDER  BY StartedAt DESC",
            new { Since = since },
            db: DatabaseId.APS);

        Assert.NotNull(run);
        Log($"Run {run!.Id} 收口状态 = {run.Status}");

        var domains = (await cm.QueryAsync<DomainStateRow>(
            @"SELECT DomainKey, Status, TotalOrders, TotalTasks
              FROM   PlanVersion WHERE SourceScheduleRunId = @Run ORDER BY DomainKey",
            new { Run = run.Id },
            db: DatabaseId.APS)).ToList();

        foreach (var d in domains)
        {
            Log($"  {d.DomainKey,-16} {d.Status,-10} 订单={d.TotalOrders} 任务={d.TotalTasks}");
        }

        Assert.False(string.IsNullOrEmpty(run.Status) || run.Status == "RUNNING",
            $"Run {run.Id} 未收口（Status={run.Status}）—— 发令枪没把 Run 算完");
        Assert.True(run.Status != "FAILED", $"Run {run.Id} 收口为 FAILED");

        var failedDomains = domains.Where(d => d.Status == "FAILED").Select(d => d.DomainKey).ToList();
        Assert.True(failedDomains.Count == 0,
            $"有 {failedDomains.Count} 个域落 FAILED：{string.Join(", ", failedDomains)}（明细见日志 {LogPath}）");
    }

    /// <summary>发令枪收口核验用。</summary>
    private sealed class RunStateRow { public int Id { get; set; } public string? Status { get; set; } }

    /// <summary>发令枪逐域核验用。</summary>
    private sealed class DomainStateRow
    {
        public string DomainKey { get; set; } = string.Empty;
        public string? Status { get; set; }
        public int? TotalOrders { get; set; }
        public int? TotalTasks { get; set; }
    }

    private sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        private readonly object _gate = new();

        public FileLoggerProvider(string path) => _path = path;

        public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _gate);

        public void Dispose() { }

        private sealed class FileLogger : ILogger
        {
            private readonly string _path;
            private readonly string _category;
            private readonly object _gate;

            public FileLogger(string path, string category, object gate)
            {
                _path = path;
                _category = category;
                _gate = gate;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null) line += Environment.NewLine + exception;
                lock (_gate)
                {
                    try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
                }
            }
        }
    }
}