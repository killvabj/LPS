using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **手工整链路跑一次**（运维用途，不是验收测试）。
///
/// 生产链路是 Hangfire 13 个 RecurringJob 串起来的，但生产 `Hangfire:CronEnabled=false` ⇒ 全部静音，
/// 只能手工补跑。本用例按「今天的数据算今天的计划」的顺序把整条链跑一遍，
/// 并在最后**建 Run ＋ 每域一个 PlanVersion ＋ 装载 ＋ 接货 ＋ 起算**，让 1号位 拿到可直接求解的输入。
///
/// 【与线上顺序的两处差别（有意为之）】
///   1. **管道供给提前**到接货之前 —— 线上 `pipeline-supply-sync` 00:55 排在 `nightly-batch` 00:30 之后，
///      夜间批用的是**昨天**的管道供给。
///   2. **MSDC 重建提前**到接货之前 —— 线上 `material-stage-dept-context-rebuild` 01:55 排在
///      `nightly-batch` 00:30 之后，夜间批用的是**昨天**的部门上下文。
///
/// 【破坏性步骤（会写库，已确认无人在用）】
///   · BOM 展开（`sp_ExpandBOMBatch_vNext`）：ODS 四表 truncate 在 BEGIN TRAN **之外**，**无恢复路径**
///   · 本用例会 `TRUNCATE` 掉本 Run 之前残留的 PlanVersion？—— **不会**，只新增，不删历史
///
/// 用法：
/// <code>APS_MANUAL_FULL_CHAIN=1 dotnet test --filter "FullyQualifiedName~ManualFullChainRunTest"</code>
/// 日志直写文件（xUnit 输出要等测试结束才落盘，中途崩会全丢）。
/// </summary>
public class ManualFullChainRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_fullchain_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    private bool _criticalFailed;
    private readonly List<string> _failed = new();

    private async Task Step(string name, bool critical, Func<Task<string>> action)
    {
        Log($"── {name} 开始");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var detail = await action();
            sw.Stop();
            Log($"✅ {name} 完成（{sw.Elapsed.TotalSeconds:F1}s）{detail}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"❌ {name} 失败（{sw.Elapsed.TotalSeconds:F1}s）：{ex.GetType().Name}: {ex.Message}");
            _failed.Add(name);
            if (critical) _criticalFailed = true;
        }
    }

    [Fact(DisplayName = "手工整链路：同步 → 建 Run 与各域 PV → 接货 → 起算")]
    public async Task RunFullChainAsync()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_FULL_CHAIN") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_FULL_CHAIN=1（本用例会写生产库，并触发 ODS 破坏性展开）");
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
        services.AddScoped<SchedulingOrchestrator>();
        services.AddLogging(b => b.AddProvider(new FileLoggerProvider(LogPath)));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var orderSync    = sp.GetRequiredService<IERPOrderSyncService>();
        var bomPush      = sp.GetRequiredService<IBOMRequestService>();
        var bomExpand    = sp.GetRequiredService<IBomBatchExpandService>();
        var masterData   = sp.GetRequiredService<IMasterDataSyncService>();
        var resource     = sp.GetRequiredService<IResourceSyncService>();
        var routing      = sp.GetRequiredService<IRoutingSyncService>();
        var inventory    = sp.GetRequiredService<IInventorySyncService>();
        var piXc         = sp.GetRequiredService<IPiXcSnapshotSyncService>();
        var pipeline     = sp.GetRequiredService<IPipelineSupplySyncService>();
        var msdcRebuild  = sp.GetRequiredService<IMaterialStageDeptContextRebuildService>();
        var domainScan   = sp.GetRequiredService<IDomainDependencyScanService>();
        var nightly      = sp.GetRequiredService<INightlyBatchOrchestrator>();
        var mesSnapshot  = sp.GetRequiredService<IMESSnapshotSyncService>();
        var scheduler    = sp.GetRequiredService<ISchedulingOrchestrator>();

        Log($"===== 手工整链路开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log($"日志：{LogPath}");

        // ── 第一段：数据同步 ───────────────────────────────────────────
        await Step("① 订单全量同步", true, async () =>
        {
            await orderSync.FullSyncAsync();
            return "ERP_Order_Staging → Order_Canonical";
        });

        await Step("② BOM 请求推送（写 ODS）", true, async () =>
        {
            var r = await bomPush.PushBOMRequestToODSAsync(CancellationToken.None);
            return $"BatchNo={r.BatchNo}, Root={r.RootCount}, 订单={r.TotalOrderCount}";
        });

        await Step("③ BOM 批展开（ODS 四表 truncate，无恢复路径）", true, async () =>
        {
            var r = await bomExpand.ExpandNightBatchAsync();
            return $"Expanded={r.Expanded}, PENDING积压={r.PendingBacklogCount}";
        });

        await Step("④ 主数据 ERP", true, async () =>
        {
            var r = await masterData.SyncERPMasterDataAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        await Step("⑤ 资源", false, async () =>
        {
            var r = await resource.SyncAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        await Step("⑥ 主数据 MES（源已空，预期只关旧行）", false, async () =>
        {
            var r = await masterData.SyncMESMasterDataAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        await Step("⑦ 工艺路线", true, async () =>
        {
            await routing.SyncAllAsync();
            return "routing-sync 完成";
        });

        await Step("⑧ 库存", true, async () =>
        {
            var r = await inventory.SyncAsync();
            return $"Balance {r.BalanceRows} 行";
        });

        await Step("⑨ PI级XC 快照", true, async () =>
        {
            var r = await piXc.SyncAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        // ⚠ 与线上顺序不同：管道供给提前到接货之前（线上 00:55 晚于 nightly-batch 00:30）
        await Step("⑩ 管道供给【提前】", true, async () =>
        {
            var r = await pipeline.SyncAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        // ⚠ 与线上顺序不同：MSDC 重建提前到接货之前（线上 01:55 晚于 nightly-batch 00:30）
        await Step("⑪ MSDC 部门上下文重建【提前】", true, async () =>
        {
            var r = await msdcRebuild.RebuildAsync();
            return $"BatchNo={r.BatchNo}, 影响 {r.RowsAffected} 行";
        });

        await Step("⑫ 域依赖扫描", false, async () =>
        {
            var r = await domainScan.ScanAsync();
            return $"结果 {r}";
        });

        if (_criticalFailed)
        {
            Log($"⛔ 关键步骤失败（{string.Join("、", _failed)}），**不建 Run**——半套数据起算会污染结论。");
            Assert.Fail($"关键同步步骤失败：{string.Join("、", _failed)}");
            return;
        }

        // ── 第二段：建 Run 与各域 PV ────────────────────────────────────
        await Step("⑬ 夜间批：建 Run ＋ 每域 PV ＋ 装载 ＋ BOM 接货", true, async () =>
        {
            await nightly.RunAsync();
            return "ScheduleRun 与 PlanVersion 已建";
        });

        // ── 第三段：MES 三快照（需当日 RUNNING 的 ScheduleRun）────────────
        await Step("⑭ MES 工单快照", false, async () =>
        {
            var r = await mesSnapshot.SyncWorkOrderAsync();
            return $"影响 {r.RowsAffected} 行";
        });
        await Step("⑮ MES 工序进度快照", false, async () =>
        {
            var r = await mesSnapshot.SyncOperationProgressAsync();
            return $"影响 {r.RowsAffected} 行";
        });
        await Step("⑯ MES 阶段进度快照", false, async () =>
        {
            var r = await mesSnapshot.SyncStageProgressAsync();
            return $"影响 {r.RowsAffected} 行";
        });

        // ── 第四段：起算（1号位 求解器）─────────────────────────────────
        // 2026-11-05：求解除非显式开门，否则不跑。求解器归 1号位，整链用例只负责「把可求解的输入备齐」；
        // 顺手跑进来会把 Run 收口掉，反而不利于 1号位 按自己的节奏起算。
        // 开门：APS_MANUAL_TRIGGER=1
        if (Environment.GetEnvironmentVariable("APS_MANUAL_TRIGGER") == "1")
        {
            await Step("⑰ 排程发令枪（1号位 求解）", false, async () =>
            {
                var r = await scheduler.RunSchedulingAutoAsync();
                return $"结果 {r}";
            });
        }
        else
        {
            Log("⏭ 跳过 ⑰ 排程发令枪（未设置 APS_MANUAL_TRIGGER=1）：输入已备齐，求解由 1号位 启动");
        }

        Log($"===== 手工整链路结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        if (_failed.Count > 0)
            Log($"⚠ 失败步骤：{string.Join("、", _failed)}（详见上方日志）");
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
