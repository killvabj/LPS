using LPS.APS.Engine.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **手工触发一次工艺路线同步**（运维用途，不是验收测试）。
///
/// 背景：生产环境 <c>Hangfire:CronEnabled=false</c>，每日 00:25 的 routing-sync 被静音，
/// 最后一次成功是 2026-10-31 09:29:57（当次 `Op(I/U/D)=0/258/1`，零插入）。
/// 而 <c>MaterialMapping</c> 在 10-30 已更新过 ⇒ 之后新出现的工序行**根本没有被灌进 APS**，
/// 表现为「物料在 APS 里零 RoutingOperation」，容易被误判成源缺。
/// （2026-11-03 实测：`MGP32AAAZ825-*` 四个物料就是这种情形。）
///
/// ⚠ 闸门：默认**跳过**。本用例会往 `APS_Production` 执行 <c>sp_SyncRoutingData</c>（写
/// RoutingOperation／RoutingDependency／RoutingStage／OperationResourceEligibility），
/// 绝不能因为一次宽泛的 <c>dotnet test</c> 被误触发。要跑必须显式开门：
/// <code>APS_MANUAL_ROUTING_SYNC=1 dotnet test --filter "FullyQualifiedName~ManualRoutingSyncRunTest"</code>
///
/// 输出写文件而不是只写 <see cref="ITestOutputHelper"/>：xUnit 的输出要等用例结束才落盘，跑挂了就什么都看不到。
/// </summary>
public class ManualRoutingSyncRunTest
{
    private readonly ITestOutputHelper _output;
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "manual_routing_sync_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

    public ManualRoutingSyncRunTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task 手工跑一次工艺路线同步()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_ROUTING_SYNC") != "1")
        {
            _output.WriteLine("已跳过：未设置 APS_MANUAL_ROUTING_SYNC=1（本用例会写生产库）");
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
        var sync = scope.ServiceProvider.GetRequiredService<LPS.APS.Engine.Services.Sync.IRoutingSyncService>();

        Log($"开始工艺路线同步 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await sync.SyncAllAsync();
        sw.Stop();

        Log($"工艺路线同步结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，墙钟 {sw.Elapsed.TotalSeconds:F1} s");
        Log($"结果：IsSuccess={result.IsSuccess}，BatchNo={result.BatchNo}");
        Log($"  Operation(I/U/D)      = {result.OperationInserted}/{result.OperationUpdated}/{result.OperationDeactivated}");
        Log($"  Dependency(I/U/D)     = {result.DependencyInserted}/{result.DependencyUpdated}/{result.DependencyDeactivated}");
        Log($"  Stage(I/U/D)          = {result.StageInserted}/{result.StageUpdated}/{result.StageDeactivated}");
        Log($"  Eligibility(I/U/D)    = {result.EligibilityInserted}/{result.EligibilityUpdated}/{result.EligibilityDeactivated}");
        Log($"  未映射跳过(物料)      = {result.UnmappedSkipped}");
        Log($"  未映射跳过(资源)      = {result.ResourceUnmappedSkipped}");
        Log($"  未映射跳过(部门)      = {result.DeptUnmappedSkipped}");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            Log($"  ErrorMessage = {result.ErrorMessage}");
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