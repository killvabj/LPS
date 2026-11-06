using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
/// BOM 夜间三段的手工执行入口（**不是验收测试**，是运维入口）。
///
/// 背景：生产 <c>Hangfire:CronEnabled=false</c> ⇒ 三个 RecurringJob 全部静音，
/// BOM 链只能手工补跑。夜间三段的真实顺序（见 <see cref="IBomBatchExpandService"/> 接口注释）：
/// <code>
///   ① 推送 bom-request-push  (00:00) → MES_API_BOM_Request(PENDING) + _Detail + APS_ETL_Log(BomRequestPush/SUCCESS)
///   ② 展开 sp_ExpandBOMBatch_vNext   → ODS 四表 truncate 重建、批次转 READY
///   ③ 接货 nightly-batch Step 5      → 按 READY 闸门取回 BOM 到各 PV
/// </code>
/// **② 未跑 ⇒ ③ 的 READY 闸门恒空**，夜间批只会记一条「无 BomRequestPush SUCCESS」然后跳过。
///
/// ⚠ <b>② 是破坏性的</b>：<c>sp_ExpandBOMBatch_vNext</c> 定义内四条 <c>truncate table</c>
///   （MES_APS_BOM_Workset / _StageDetail / _Issues / _CrossFactoryEdge）位于 <c>BEGIN TRAN;</c> **之外**，
///   清空立即落定、回滚不回来；<c>MES_APS_BOM_Workset_Archive</c> 实测 0 行，**无恢复路径**。
///
/// ⚠ <b>① 一天只能推一次</b>：<c>BomNightBatchResolver</c> 要求「当日恰好一条
///   <c>BomRequestPush/SUCCESS</c>」才算本次夜间；推第二次会让 ② 直接 SKIPPED（0 条或多条一律不认）。
///
/// 用法：
/// <code>
/// APS_MANUAL_BOM_CHAIN=1 APS_BOM_STEP=push    dotnet test --filter "FullyQualifiedName~ManualBomChainRunTest"
/// APS_MANUAL_BOM_CHAIN=1 APS_BOM_STEP=expand  dotnet test --filter "FullyQualifiedName~ManualBomChainRunTest"
/// APS_MANUAL_BOM_CHAIN=1 APS_BOM_STEP=pull    APS_BOM_PV_IDS=999437,999438,... dotnet test --filter "..."
/// APS_MANUAL_BOM_CHAIN=1 APS_BOM_STEP=all     dotnet test --filter "..."   -- 三段连跑（push+expand+pull）
/// </code>
/// </summary>
public class ManualBomChainRunTest
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"aps_bomchain_{DateTime.Now:yyyyMMdd_HHmmss}.log");

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

    [Fact(DisplayName = "BOM 夜间三段手工执行（push / expand / pull）")]
    public async Task 跑BOM链()
    {
        if (Environment.GetEnvironmentVariable("APS_MANUAL_BOM_CHAIN") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MANUAL_BOM_CHAIN=1（本用例会写 ODS 并可能 truncate 四张表）");
            return;
        }

        var step = (Environment.GetEnvironmentVariable("APS_BOM_STEP") ?? "all").Trim().ToLowerInvariant();

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var push   = sp.GetRequiredService<IBOMRequestService>();
        var expand = sp.GetRequiredService<IBomBatchExpandService>();
        var pull   = sp.GetRequiredService<IBOMResultPullService>();

        Log($"===== BOM 链开始 step={step} {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Log($"日志：{LogPath}");

        string? batchNo = null;

        // ── ① 推送 ────────────────────────────────────────────────────
        if (step is "push" or "all")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await push.PushBOMRequestToODSAsync(CancellationToken.None);
            sw.Stop();
            batchNo = r.BatchNo;
            Log($"① 推送完成：BatchNo={r.BatchNo}, Root={r.RootCount}, 订单={r.TotalOrderCount}, 耗时={sw.Elapsed.TotalSeconds:F1}s");
        }

        // ── ② 展开（破坏性）──────────────────────────────────────────
        if (step is "expand" or "all")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await expand.ExpandNightBatchAsync();
            sw.Stop();
            batchNo ??= r.BatchNo;
            Log($"② 展开完成：Expanded={r.Expanded}, BatchNo={r.BatchNo ?? "(null)"}, "
                + $"PENDING积压={r.PendingBacklogCount}, 耗时={sw.Elapsed.TotalSeconds:F1}s");
            foreach (var i in r.Issues) Log($"   issue: {i}");
        }

        // ── ③ 接货 ────────────────────────────────────────────────────
        if (step is "pull" or "all")
        {
            if (batchNo is null)
            {
                var raw = Environment.GetEnvironmentVariable("APS_BOM_BATCH_NO");
                if (string.IsNullOrWhiteSpace(raw))
                {
                    Log("③ 跳过：无 BatchNo（请设 APS_BOM_BATCH_NO）");
                    return;
                }
                batchNo = raw.Trim();
            }

            var pvRaw = Environment.GetEnvironmentVariable("APS_BOM_PV_IDS");
            if (string.IsNullOrWhiteSpace(pvRaw))
            {
                Log("③ 跳过：未设 APS_BOM_PV_IDS（逗号分隔的 PlanVersionId）");
                return;
            }

            var pvIds = pvRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(int.Parse).ToList();

            var ready = await pull.FindReadyBatchAsync(batchNo, CancellationToken.None);
            Log($"③ READY 闸门：expected={batchNo} ⇒ ready={ready ?? "(null)"}");
            if (ready is null) { Log("③ 跳过：批次未 READY"); return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = await pull.PullBOMResultFromODSAsync(ready, pvIds, CancellationToken.None);
            sw.Stop();
            Log($"③ 接货完成：BatchNo={ready}, PV数={pvIds.Count}, 行数={rows}, 耗时={sw.Elapsed.TotalSeconds:F1}s");
        }

        Log($"===== BOM 链结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
    }
}