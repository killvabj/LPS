using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **手工按顺序补跑主数据与供给类同步**（运维用途，不是验收测试）。
///
/// 背景（2026-11-03 实测 `APS_ETL_Log`）：除 routing（当日手工补跑）与订单同步（11-02）外，
/// 其余同步**最后一次都停在 2026-10-30**，已过期 4 天：
///   `sp_SyncMasterData[ERP]` 10:40 / `[MES]` 10:44 / `ResourceSync` 10:50 /
///   `InventorySync` 10:56 / `PipelineSupply` 10:50 / `PiXcSnapshot` 10:50 / `sp_SyncMES*Snapshot` 13:13。
///
/// 顺序：主数据（ERP→MES）→ 资源 → 库存 → 管道供给 → PI级XC。
/// **MES 三个快照（工单/工序进度/阶段进度）不在本入口**：它们要求存在当日 RUNNING 的 `ScheduleRun`
/// （`MESSnapshotSyncService.SyncSnapshotAsync` 里 `GetCurrentRunAsync()` 为空直接抛），
/// 需要先起一个 Run，另行处理。
///
/// ⚠ 闸门：默认**跳过**。要跑必须显式开门：
/// <code>APS_MANUAL_FULL_SYNC=1 dotnet test --filter "FullyQualifiedName~ManualFullSyncRunTest"</code>
///
/// 每一步独立 try/catch：**一步失败不阻断后续**，跑完整份报告一次给。
/// 日志直写文件（xUnit 输出要等用例结束才落盘，跑挂了看不到）。
/// </summary>
public class ManualFullSyncRunTest
{
    private readonly ITestOutputHelper _output;
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "manual_full_sync_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

    public ManualFullSyncRunTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task 按顺序补跑主数据与供给同步()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_FULL_SYNC") != "1")
        {
            _output.WriteLine("已跳过：未设置 APS_MANUAL_FULL_SYNC=1（本用例会写生产库）");
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
        // ⚠ 必须补这一行：`ScheduleRunService` 依赖 `IStrategyProfileVersionRepository`，它由
        //   `AddGovernanceRepositories()` 注册。集成测试历史上用 Fixture 顶掉过这个解析，所以缺注册
        //   一直没暴露；这里是真实构造，缺了就 `Unable to resolve service`（2026-11-03 首次跑即撞上）。
        services.AddGovernanceRepositories();
        services.AddLogging(b => b.AddProvider(new FileLoggerProvider(LogPath)));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var masterData  = sp.GetRequiredService<IMasterDataSyncService>();
        var resource    = sp.GetRequiredService<IResourceSyncService>();
        var inventory   = sp.GetRequiredService<IInventorySyncService>();
        var pipeline    = sp.GetRequiredService<IPipelineSupplySyncService>();
        var piXc        = sp.GetRequiredService<IPiXcSnapshotSyncService>();
        var mesSnapshot = sp.GetRequiredService<IMESSnapshotSyncService>();

        Log($"===== 补跑开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

        await Step("① 主数据 ERP", async () =>
        {
            var r = await masterData.SyncERPMasterDataAsync();
            return $"SourceType={r.SourceType} BatchNo={r.BatchNo} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        await Step("② 主数据 MES", async () =>
        {
            var r = await masterData.SyncMESMasterDataAsync();
            return $"SourceType={r.SourceType} BatchNo={r.BatchNo} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        await Step("③ 资源", async () =>
        {
            var r = await resource.SyncAsync();
            return $"BatchNo={r.BatchNo} Rows={r.RowsAffected} Skipped={r.Skipped} Err={r.ErrorMessage}";
        });

        await Step("④ 库存", async () =>
        {
            var r = await inventory.SyncAsync();
            return $"BatchNo={r.BatchNo} BalanceRows={r.BalanceRows} Err={r.ErrorMessage}";
        });

        await Step("⑤ 管道供给", async () =>
        {
            var r = await pipeline.SyncAsync();
            return $"BatchNo={r.BatchNo} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        await Step("⑥ PI级XC快照", async () =>
        {
            var r = await piXc.SyncAsync();
            return $"BatchNo={r.BatchNo} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        // ⑦~⑨ 需要当日 RUNNING 的 ScheduleRun —— 预期失败，跑一遍把真实的失败信息记下来
        await Step("⑦ MES 工单快照（需 RUNNING Run）", async () =>
        {
            var r = await mesSnapshot.SyncWorkOrderAsync();
            return $"ScheduleRunId={r.ScheduleRunId} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        await Step("⑧ MES 工序进度快照（需 RUNNING Run）", async () =>
        {
            var r = await mesSnapshot.SyncOperationProgressAsync();
            return $"ScheduleRunId={r.ScheduleRunId} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        await Step("⑨ MES 阶段进度快照（需 RUNNING Run）", async () =>
        {
            var r = await mesSnapshot.SyncStageProgressAsync();
            return $"ScheduleRunId={r.ScheduleRunId} Rows={r.RowsAffected} Err={r.ErrorMessage}";
        });

        Log($"===== 补跑结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log($"日志文件：{LogPath}");
    }

    private async Task Step(string name, Func<Task<string>> action)
    {
        Log($"--- {name} 开始 {DateTime.Now:HH:mm:ss}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var detail = await action();
            sw.Stop();
            Log($"--- {name} 成功 {sw.Elapsed.TotalSeconds:F1}s | {detail}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"--- {name} 失败 {sw.Elapsed.TotalSeconds:F1}s | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Log(string msg)
    {
        _output.WriteLine(msg);
        try { File.AppendAllText(LogPath, msg + Environment.NewLine); } catch { }
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