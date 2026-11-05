using LPS.APS.Engine.Extensions;
using LPS.APS.Tests.Integration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **手工触发一次 ERP 订单同步**（运维用途，不是验收测试）。
///
/// 背景：生产环境 <c>Hangfire:CronEnabled=false</c>，`erp-order-incremental-sync` 整点任务被静音，
/// 最后一次同步停在 2026-10-30 11:35。表 `ERP_Order_Staging` 的重复行收敛逻辑（同一 OrderNo 只保留
/// SyncedAt 最新的一条，PENDING 与 FAILED 同属收敛集）只有在**真的跑一次同步**时才会生效，
/// 所以必须有一个能手工拉起的入口。
///
/// ⚠ 闸门：默认**跳过**。本用例会往 `APS_Production` 发起真实同步（拉 ODS → 写 Staging → 提升 → 压订单池），
/// 绝不能因为一次宽泛的 <c>dotnet test</c> 被误触发。要跑必须显式开门：
/// <code>APS_MANUAL_ORDER_SYNC=1 dotnet test --filter "FullyQualifiedName~ManualOrderSyncRunTest"</code>
///
/// 输出写文件而不是只写 <see cref="ITestOutputHelper"/>：xUnit 的输出要等用例结束才落盘，
/// 跑挂了就什么都看不到。
/// </summary>
public class ManualOrderSyncRunTest
{
    private readonly ITestOutputHelper _output;
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "manual_order_sync_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

    public ManualOrderSyncRunTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task 手工跑一次订单同步()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_ORDER_SYNC") != "1")
        {
            _output.WriteLine("已跳过：未设置 APS_MANUAL_ORDER_SYNC=1（本用例会写生产库）");
            return;
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddLogging(b => b.AddProvider(new FileLoggerProvider(LogPath)));

        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<LPS.APS.Engine.Services.Sync.IERPOrderSyncService>();

        Log($"开始增量同步 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sync.IncrementalSyncAsync();
        sw.Stop();
        Log($"增量同步结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，墙钟 {sw.Elapsed.TotalSeconds:F1} s");
        Log($"日志文件：{LogPath}");
    }

    private void Log(string msg)
    {
        _output.WriteLine(msg);
        try { File.AppendAllText(LogPath, msg + Environment.NewLine); } catch { /* 落盘失败不影响同步 */ }
    }

    /// <summary>把 ILogger 输出直接写文件，避免 xUnit 输出缓冲到用例结束才落盘。</summary>
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