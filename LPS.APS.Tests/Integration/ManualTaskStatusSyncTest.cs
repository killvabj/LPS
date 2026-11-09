using System;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// `[Task].Status` MES 报工回写手工入口（W4，2号位；**运维入口，不是验收测试**）。
/// </summary>
/// <remarks>
/// 【裁定依据】我方 2026-11-05《回两事裁定》：「归我方。**V1 做 MES 报工回写段
///   （`IN_PROGRESS` / `COMPLETED`）**；`PLANNED → RELEASED` 段待下发链（`O8` / `TaskDispatch`）跑通后一并做。」
///
/// 【本入口做什么】按 `MESWorkOrderSnapshot` 回写指定 Run 下 `[Task].Status`：
///   · MES `IN_PROGRESS` ⇒ `PLANNED → IN_PROGRESS`
///   · MES `CLOSED`      ⇒ `PLANNED|IN_PROGRESS → COMPLETED`
///   · 其它 MES 状态**不动**（`RELEASED` 段不在 V1 范围；`DELETED` 也不动 —— 裁定只点了两个段）
///   **只前进不回退，只改状态，不碰数量/时间/资源。**
///
/// 【验收信号（4号位 定义、5号位 背书）】dev 任一本 ACTIVE 版本出现 **≥1 条非 `PLANNED`**。
///   本入口跑完会打印该 Run 各 PV 的状态分布，直接可读。
///
/// 【为什么是运维入口而不是 cron】当前运行方式为本地手工跑（用户 2026-11-09 澄清：本地跑即可、不发服务器），
///   `Hangfire:CronEnabled=false` ⇒ 定时任务不会触发。故先提供显式入口；将来若要日更，再挂 recurring job。
///
/// 用法：
/// <code>
/// APS_TASK_STATUS_SYNC=1 APS_TASK_STATUS_RUN_ID=999423 dotnet test --filter "FullyQualifiedName~ManualTaskStatusSyncTest"
/// </code>
/// </remarks>
public class ManualTaskStatusSyncTest
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "Task.Status MES 报工回写（APS_TASK_STATUS_SYNC=1 显式开启）")]
    public async Task SyncTaskStatusFromMes()
    {
        if (Environment.GetEnvironmentVariable("APS_TASK_STATUS_SYNC") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_TASK_STATUS_SYNC=1（本用例会写 APS_Production 的 [Task].Status）");
            return;
        }

        var runRaw = Environment.GetEnvironmentVariable("APS_TASK_STATUS_RUN_ID");
        if (!int.TryParse(runRaw, out var scheduleRunId) || scheduleRunId <= 0)
        {
            Console.WriteLine($"已跳过：未指定 APS_TASK_STATUS_RUN_ID（当前值 '{runRaw ?? "(null)"}'）");
            return;
        }

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITaskExecutionStatusSyncService>();
        var conn = scope.ServiceProvider.GetRequiredService<DatabaseConnectionManager>();

        Console.WriteLine($"===== 改前 Status 分布（ScheduleRunId={scheduleRunId}）=====");
        await DumpStatusAsync(conn, scheduleRunId);

        var result = await svc.SyncByRunAsync(scheduleRunId, CancellationToken.None);
        Console.WriteLine($"===== 回写完成：→IN_PROGRESS={result.ToInProgress}，→COMPLETED={result.ToCompleted}，合计={result.TotalChanged} =====");

        Console.WriteLine($"===== 改后 Status 分布 =====");
        await DumpStatusAsync(conn, scheduleRunId);

        Assert.True(result.TotalChanged >= 0);   // 入口不判业务对错，只保证跑通；验收看分布
    }

    private static async Task DumpStatusAsync(DatabaseConnectionManager conn, int scheduleRunId)
    {
        var rows = await conn.QueryAsync<StatusCountRow>(
            @"SELECT ISNULL(t.Status, N'NULL') AS Status, COUNT(*) AS Cnt
              FROM   [Task] t
              INNER JOIN PlanVersion pv ON pv.Id = t.PlanVersionId
              WHERE  pv.SourceScheduleRunId = @RunId
              GROUP BY t.Status
              ORDER BY t.Status",
            new { RunId = scheduleRunId },
            db: DatabaseId.APS,
            commandTimeout: 300);

        foreach (var r in rows)
            Console.WriteLine($"    {r.Status} x {r.Cnt}");
    }

    private sealed class StatusCountRow
    {
        public string Status { get; set; } = string.Empty;
        public long Cnt { get; set; }
    }
}