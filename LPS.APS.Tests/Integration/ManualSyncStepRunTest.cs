using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 「单步同步」手工执行入口（**运维入口，不是验收测试**，2026-11-07）。
/// </summary>
/// <remarks>
/// 【为什么要有这个件】<see cref="ManualInputRefreshRunTest"/> 是**阶段式**（`pre` 一次跑 ③–⑧ 六步、
///   `post` 一次跑 ⑩–⑮ 四步）。阶段式的问题是：**第 3 步把源刷坏了，第 4–8 步照样在坏数据上继续跑**，
///   事后只能从日志里倒推是哪一步出的错。物料源出过事（`O44`）之后，逐步执行 + 每步单独核数
///   是唯一能把「哪一步、改了什么、改了多少」钉死的做法。
///
/// 【与生产 cron 的关系】步名与 `LPS.APS.Web/Extensions/HangfireServiceExtensions.cs` 的 job id 一一对应，
///   走的也是同一批服务实现 —— **不是模拟，是真的那套码**。区别只在宿主：本入口在测试进程内跑，
///   因此代码版本、日志、耗时全部可归因（Hangfire 的 `APS_Hangfire` 是**多实例共享**的，
///   投进队列后由哪台机器执行不可控）。
///
/// 【顺序约束（改不得）】
///   · `master-erp` / `resource` / `master-mes` / `routing` / `inventory` / `pixc` **必须早于** `nightly-batch`
///     —— `[Order]` 装载 SP 用 `Material.ProductFamilyId` 判域，主数据没刷完就装单＝按旧家族分域。
///   · MES 三快照（`mes-workorder` / `mes-operation` / `mes-stage`）**必须晚于** `nightly-batch`
///     —— `MESSnapshotSyncService` 找不到当日 `RUNNING` 的 `ScheduleRun` 会直接抛。
///   · `bom-request-push` **一天只能一次**（`BomNightBatchResolver` 要求当日恰好一条 `BomRequestPush/SUCCESS`），
///     故本件**故意不提供 push 步**；push 已推过的日子从 `bom-expand` 起。
///
/// 用法：
/// <code>
/// APS_SYNC_STEP=master-erp dotnet test --filter "FullyQualifiedName~ManualSyncStepRunTest"
/// </code>
/// 可用步名：<c>erp-order</c> / <c>master-erp</c> / <c>resource</c> / <c>master-mes</c> / <c>routing</c> / <c>inventory</c> /
///   <c>pixc</c> / <c>bom-expand</c> / <c>mes-workorder</c> / <c>mes-operation</c> / <c>mes-stage</c> /
///   <c>pipeline</c> / <c>domain-scan</c> / <c>msdc</c>
/// ⚠ 本入口会**写生产库**（含 `TRUNCATE`+重载类步骤）。
/// </remarks>
public class ManualSyncStepRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_syncstep_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    private static void Log(string msg)
    {
        // 故意同时写 Console 与文件：xUnit 的 ITestOutputHelper 要到用例结束才落盘，
        //   长跑中途崩掉时 Console/文件是唯一能证明「跑到哪一步」的证据。
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* 日志失败不影响主流程 */ }
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

    [Fact(DisplayName = "单步同步（APS_SYNC_STEP 指定一步；逐步核数用）")]
    public async Task 执行单步同步()
    {
        var step = (Environment.GetEnvironmentVariable("APS_SYNC_STEP") ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(step))
        {
            Console.WriteLine("已跳过：未设置 APS_SYNC_STEP（本用例会写生产库）");
            return;
        }

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        // 每个步骤都返回结果 DTO；统一 JSON 打出来，避免逐类型写格式化代码后又漂移。
        object? result;

        Log($"===== 单步同步 step={step} 开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log($"日志：{LogPath}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            switch (step)
            {
                // ── ③–⑧ nightly-batch 之前 ──────────────────────────────────
                // 第 1 步（Hangfire `erp-order-incremental-sync`，每小时）。
                //   返回 void，结果只能从 APS_ETL_Log / ERP_Order_Staging 看。
                //   2026-11-07 补：此前本件缺这一步，导致「主数据全刷了、订单还是昨天的」却看不出来
                //   （实测 ERP_Order_Staging 停在 2026-11-06 14:22，[Order] 10:08 装的仍是昨日单）。
                case "erp-order":
                    await sp.GetRequiredService<IERPOrderSyncService>()
                        .IncrementalSyncAsync(CancellationToken.None);
                    result = new
                    {
                        Step = "erp-order",
                        Note = "IncrementalSyncAsync 无返回 DTO；核对点＝ERP_Order_Staging.MAX(SyncedAt) 与 APS_ETL_Log"
                    };
                    break;

                case "master-erp":
                    result = await sp.GetRequiredService<IMasterDataSyncService>()
                        .SyncERPMasterDataAsync(CancellationToken.None);
                    break;

                case "resource":
                    result = await sp.GetRequiredService<IResourceSyncService>()
                        .SyncAsync(CancellationToken.None);
                    break;

                case "master-mes":
                    result = await sp.GetRequiredService<IMasterDataSyncService>()
                        .SyncMESMasterDataAsync(CancellationToken.None);
                    break;

                case "routing":
                    result = await sp.GetRequiredService<IRoutingSyncService>()
                        .SyncAllAsync(CancellationToken.None);
                    break;

                case "inventory":
                    result = await sp.GetRequiredService<IInventorySyncService>()
                        .SyncAsync(CancellationToken.None);
                    break;

                case "pixc":
                    result = await sp.GetRequiredService<IPiXcSnapshotSyncService>()
                        .SyncAsync(CancellationToken.None);
                    break;

                // ── BOM 扩批（push 不在此件：一天只能推一次）─────────────────
                case "bom-expand":
                    result = await sp.GetRequiredService<IBomBatchExpandService>()
                        .ExpandNightBatchAsync(CancellationToken.None);
                    break;

                // ── ⑩–⑮ nightly-batch 之后 ─────────────────────────────────
                case "mes-workorder":
                    result = await sp.GetRequiredService<IMESSnapshotSyncService>()
                        .SyncWorkOrderAsync(CancellationToken.None);
                    break;

                case "mes-operation":
                    result = await sp.GetRequiredService<IMESSnapshotSyncService>()
                        .SyncOperationProgressAsync(CancellationToken.None);
                    break;

                case "mes-stage":
                    result = await sp.GetRequiredService<IMESSnapshotSyncService>()
                        .SyncStageProgressAsync(CancellationToken.None);
                    break;

                case "pipeline":
                    result = await sp.GetRequiredService<IPipelineSupplySyncService>()
                        .SyncAsync(CancellationToken.None);
                    break;

                case "domain-scan":
                    result = await sp.GetRequiredService<IDomainDependencyScanService>()
                        .ScanAsync(CancellationToken.None);
                    break;

                case "msdc":
                    result = await sp.GetRequiredService<IMaterialStageDeptContextRebuildService>()
                        .RebuildAsync(CancellationToken.None);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未知步名 '{step}'。可用：erp-order/master-erp/resource/master-mes/routing/inventory/pixc/" +
                        "bom-expand/mes-workorder/mes-operation/mes-stage/pipeline/domain-scan/msdc");
            }

            sw.Stop();
            Log($"[OK] step={step} 耗时 {sw.Elapsed.TotalSeconds:F1}s");
            try
            {
                Log("结果：" + JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = false, MaxDepth = 4 }));
            }
            catch (Exception ex)
            {
                // 序列化失败不是业务失败，不要让日志把「成功」变成「异常」。
                Log($"结果序列化失败（业务本身已成功）：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"[FAIL] step={step} 耗时 {sw.Elapsed.TotalSeconds:F1}s，异常：{ex}");
            throw;
        }
    }
}
