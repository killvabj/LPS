using System;
using System.IO;
using System.Threading.Tasks;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 手工只跑「ERP 订单**全量**同步」这一个 seam（运维入口，不是验收测试）。
///
/// 与既有的 <see cref="ManualOrderSyncRunTest"/> 的区别：那个跑的是 `IncrementalSyncAsync`
/// （增量，按 `UpdatedAt > 水位` 拉）；本用例跑 `FullSyncAsync`（`whereClause: ""` 拉全量）。
/// 两者不可互换——**订单生命周期收口只在全量后执行**（增量的「本轮未出现」没有意义）。
///
/// 用途（2026-11-05 起 `ERPOrderSyncService` 新增收口）：
///   · `FAILED` 行状态同步：验证失败不再阻断状态更新；
///   · 孤儿两轮关单：源头消失（ODS 视图 7 天关闭窗口已过）的单，连续两轮未出现才置 `CLOSED`。
///
/// ⚠ 两轮语义：第一次跑只登记候选（`MissCount=1`），**第二次跑才会真正关单**。验收请连跑两次。
/// ⚠ 该收口只改 `Order_Canonical`，不碰 `[Order]` 快照 ⇒ 可在求解进行中安全执行。
/// ⚠ 日志直写文件（xUnit 输出要等用例结束才落盘，中途崩会全丢）。
///
/// 开门：<code>APS_MANUAL_ORDER_FULL_SYNC=1 dotnet test --filter FullyQualifiedName~ManualOrderFullSyncRunTest</code>
/// </summary>
public class ManualOrderFullSyncRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_orderfullsync_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    [Fact(DisplayName = "只跑 ERP 订单全量同步（含订单生命周期收口）")]
    public async Task 跑订单全量同步()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_ORDER_FULL_SYNC") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_ORDER_FULL_SYNC=1（本用例会写生产库）");
            return;
        }

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
        services.AddLogging(b => b.AddProvider(new FileLoggerProvider(LogPath)));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var orderSync = scope.ServiceProvider.GetRequiredService<IERPOrderSyncService>();

        Log($"===== 订单全量同步开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await orderSync.FullSyncAsync();
        sw.Stop();
        Log($"===== 订单全量同步结束（{sw.Elapsed.TotalSeconds:F1}s）=====");
        Log($"日志：{LogPath}");
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
            { _path = path; _category = category; _gate = gate; }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null) line += Environment.NewLine + exception;
                lock (_gate) { try { File.AppendAllText(_path, line + Environment.NewLine); } catch { } }
            }
        }
    }
}