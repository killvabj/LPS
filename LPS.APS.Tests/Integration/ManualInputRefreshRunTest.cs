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
/// 「求解前输入链」的手工补跑入口（**运维入口，不是验收测试**）。
///
/// 【为什么分两段】生产 cron 顺序（`LPS.APS.Web/Extensions/HangfireServiceExtensions.cs:86-250`）是：
/// <code>
///   00:00 bom-request-push        00:05 bom-batch-expand
///   00:10 master-data-sync-erp    00:15 resource-sync      00:20 master-data-sync-mes
///   00:25 routing-sync            00:26 inventory-sync     00:28 pi-xc-snapshot-sync
///   00:30 nightly-batch   ← [Order] 装载必须晚于上面六个（装载 SP 用 Material.ProductFamilyId 判域）
///   00:40/45/50 mes-snapshot×3    00:55 pipeline-supply-sync
///   01:50 domain-dependency-scan  01:55 material-stage-dept-context-rebuild
///   02:00 scheduling-trigger
/// </code>
/// ⇒ 本入口分 <c>pre</c>（③–⑧，nightly 之前）与 <c>post</c>（⑩–⑮，nightly 之后）两段，
///   **中间那一步（nightly-batch + `[Order]` 重装）由 <see cref="ManualNightlyBatchAndTriggerRunTest"/> 承担**。
///
/// 【为什么不用 ManualFullChainRunTest】它含 ② BOM 推送，而推送**一天只能推一次**
/// （`BomNightBatchResolver` 要求当日恰好一条 `BomRequestPush/SUCCESS`）；已经推过再跑整链会把当日记录弄成两条。
///
/// 用法：
/// <code>
/// APS_MANUAL_INPUT_REFRESH=1 APS_INPUT_PHASE=pre   dotnet test --filter "FullyQualifiedName~ManualInputRefreshRunTest"
/// APS_MANUAL_INPUT_REFRESH=1 APS_INPUT_PHASE=post  dotnet test --filter "FullyQualifiedName~ManualInputRefreshRunTest"
/// </code>
/// ⚠ 本入口**不建 Run、不装订单、不接 BOM、不起求解**，只刷数据面。
/// ⚠ <c>post</c> 段的 MES 三快照要求当日已有 `RUNNING` 的 `ScheduleRun`，否则 `MESSnapshotSyncService` 会**抛异常**。
/// </summary>
public class ManualInputRefreshRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_inputrefresh_{DateTime.Now:yyyyMMdd_HHmmss}.log");

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
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "求解前输入链刷新（pre = ③–⑧ nightly 之前 / post = ⑩–⑮ nightly 之后）")]
    public async Task 刷新输入链()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_INPUT_REFRESH") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_INPUT_REFRESH=1（本用例会写生产库）");
            return;
        }

        var phase = (Environment.GetEnvironmentVariable("APS_INPUT_PHASE") ?? "pre").Trim().ToLowerInvariant();
        if (phase is not ("pre" or "post"))
            throw new InvalidOperationException($"APS_INPUT_PHASE 只能是 pre 或 post，当前 '{phase}'");

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Log($"===== 输入链刷新开始 phase={phase} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log($"日志：{LogPath}");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (phase == "pre")
        {
            // 生产 00:10 – 00:28，必须全部早于 nightly-batch
            var masterData = sp.GetRequiredService<IMasterDataSyncService>();
            var resource   = sp.GetRequiredService<IResourceSyncService>();
            var routing    = sp.GetRequiredService<IRoutingSyncService>();
            var inventory  = sp.GetRequiredService<IInventorySyncService>();
            var piXc       = sp.GetRequiredService<IPiXcSnapshotSyncService>();

            var m1 = await masterData.SyncERPMasterDataAsync();
            Log($"③ 主数据 ERP 完成：{m1.RowsAffected} 行");

            var rs = await resource.SyncAsync();
            Log($"④ 资源 完成：{rs.RowsAffected} 行");

            var m2 = await masterData.SyncMESMasterDataAsync();
            Log($"⑤ 主数据 MES 完成：{m2.RowsAffected} 行");

            await routing.SyncAllAsync();
            Log("⑥ 工艺路线 完成");

            var inv = await inventory.SyncAsync();
            Log($"⑦ 库存 完成：Balance {inv.BalanceRows} 行");

            var xc = await piXc.SyncAsync();
            Log($"⑧ PI级XC 快照 完成：{xc.RowsAffected} 行");
        }
        else
        {
            // 生产 00:40 – 01:55，必须全部晚于 nightly-batch
            var mesSnapshot = sp.GetRequiredService<IMESSnapshotSyncService>();
            var pipeline    = sp.GetRequiredService<IPipelineSupplySyncService>();
            var domainScan  = sp.GetRequiredService<IDomainDependencyScanService>();
            var msdcRebuild = sp.GetRequiredService<IMaterialStageDeptContextRebuildService>();

            var s1 = await mesSnapshot.SyncWorkOrderAsync();
            Log($"⑩ MES 工单快照 完成：{s1.RowsAffected} 行");

            var s2 = await mesSnapshot.SyncOperationProgressAsync();
            Log($"⑪ MES 工序进度快照 完成：{s2.RowsAffected} 行");

            var s3 = await mesSnapshot.SyncStageProgressAsync();
            Log($"⑫ MES 阶段进度快照 完成：{s3.RowsAffected} 行");

            var pl = await pipeline.SyncAsync();
            Log($"⑬ 管道供给 完成：{pl.RowsAffected} 行");

            var scan = await domainScan.ScanAsync();
            Log($"⑭ 域依赖扫描 完成：{scan}");

            var msdc = await msdcRebuild.RebuildAsync();
            Log($"⑮ MSDC 部门上下文重建 完成：BatchNo={msdc.BatchNo}, {msdc.RowsAffected} 行");
        }

        sw.Stop();
        Log($"===== 输入链刷新结束 phase={phase}，耗时 {sw.Elapsed.TotalSeconds:F1}s =====");
        Log($"日志：{LogPath}");
    }
}