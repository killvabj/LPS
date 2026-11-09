using System;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Engine.Extensions;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// MES 下发消费环手工入口（FULL-07，2号位）。
///
/// 做什么：把 `TaskDispatch` 的 `PENDING` 按 `(PI, Stage, SourcePlanVersionId)` **组合聚合**后
/// 交给 <see cref="LPS.APS.Core.Interfaces.IMesDispatchAdapter"/>，并按三态回写。
///
/// 契约：5号位 2026-11-09《FULL-07 出站通道答甲…》§四/§五/§六。
///   · 组合聚合、一组一次调用（逐行会造重复工单）；
///   · Accepted→CREATED／Rejected→FAILED+九码／Unknown→回 PENDING 保持可重试；
///   · 真实 MES 未接通（适配器未注册）时按 NOT_CONFIGURED 处理，**不外呼、不伪造成功**。
///
/// 用法：
/// <code>
/// APS_MES_DRAIN=1 APS_MES_DRAIN_RUN_ID=999423 dotnet test --filter "FullyQualifiedName~ManualMesDispatchDrainTest"
/// </code>
/// </summary>
public class ManualMesDispatchDrainTest
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

    [Fact(DisplayName = "MES 下发消费环：PENDING 组合 → 三态回写（APS_MES_DRAIN=1 显式开启）")]
    public async Task DrainPendingDispatchGroups()
    {
        if (Environment.GetEnvironmentVariable("APS_MES_DRAIN") != "1")
        {
            Console.WriteLine("已跳过：未设置 APS_MES_DRAIN=1（本用例会改 APS_Production 的 TaskDispatch 状态）");
            return;
        }

        var runRaw = Environment.GetEnvironmentVariable("APS_MES_DRAIN_RUN_ID");
        if (!int.TryParse(runRaw, out var scheduleRunId) || scheduleRunId <= 0)
        {
            Console.WriteLine($"已跳过：未指定 APS_MES_DRAIN_RUN_ID（当前值 '{runRaw ?? "(null)"}'）");
            return;
        }

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IMesDispatchDrainService>();

        var result = await svc.DrainAsync(scheduleRunId, maxGroups: 200);

        Console.WriteLine(
            $"===== 消费完成：组合 {result.GroupsSeen}（Accepted {result.GroupsAccepted} / Rejected {result.GroupsRejected} / Unknown {result.GroupsUnknown}）；" +
            $"行 →CREATED {result.RowsCreated} / →FAILED {result.RowsFailed} / 回PENDING {result.RowsBackToPending}；" +
            $"适配器{(result.AdapterMissing ? "未注册(NOT_CONFIGURED)" : "已注册")} =====");

        Assert.True(result.GroupsSeen >= 0);
    }
}