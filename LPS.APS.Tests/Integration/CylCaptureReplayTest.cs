using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位】带 MES 快照的 FAMILY_CYL 失败入参 JSON 捕获入口（2026-11-18）。
///
/// <para><b>为什么新建这个文件、不去改 2号位 的 <c>RunExistingPvWithRunAsync</c></b>：
/// 捕获钩子 <c>SolveRequestCapture.Start</c> 在 2号位 harness 里只接在
/// <c>RunExistingPlanVersionAsync</c>（scheduleRunId=0，<b>不装 MES 快照</b>）那条上；
/// 能复现 Phase5 越序的是 <c>RunExistingPvWithRunAsync</c>（带 MES 快照 999473），但它没接捕获。
/// 1号位 不动别人的测试文件 ⇒ 本文件 verbatim 复刻它的 DI 组合根（生产口径），
/// 只把「带 MES 的那条」与「捕获」接起来。</para>
///
/// <para><b>产物</b>：<c>PV999561_solverequest_01.json</c>。此后用 <c>SolverReplayTest</c>
/// （<c>APS_REPLAY_SOLVE_REQUEST=&lt;json&gt;</c>）离线纯内存重放 Phase1~5，彻底脱离真库跑批。</para>
///
/// <para><b>开门</b>：
/// <code>APS_REAL_DOMAIN_RUN=1 APS_REAL_DOMAIN_PV_ID=999561 APS_REAL_RUN_ID=999473
/// APS_CAPTURE_SOLVE_REQUEST=1 APS_RUN_LOG_DIR=D:/tmp
/// dotnet test --no-build -c Release --filter FullyQualifiedName~CylCaptureReplayTest</code></para>
/// </summary>
public sealed class CylCaptureReplayTest
{
    private const string GateEnvVar = "APS_REAL_DOMAIN_RUN";
    private const string HarnessRunType = "FULL_SCHEDULE";

    private readonly ITestOutputHelper _output;

    public CylCaptureReplayTest(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "1号位: 带MES快照捕获 DomainSolveRequest JSON（离线重放入参）")]
    public async Task CaptureWithMesAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            "真实域跑批默认关闭（会写 APS_Production 并跑满主链）。设 APS_REAL_DOMAIN_RUN=1 显式开启。");

        var pvRaw = Environment.GetEnvironmentVariable("APS_REAL_DOMAIN_PV_ID");
        Skip.IfNot(int.TryParse(pvRaw, out var planVersionId) && planVersionId > 0,
            $"未指定 APS_REAL_DOMAIN_PV_ID（当前值 '{pvRaw ?? "(null)"}'）。");

        var runRaw = Environment.GetEnvironmentVariable("APS_REAL_RUN_ID");
        Skip.IfNot(int.TryParse(runRaw, out var scheduleRunId) && scheduleRunId > 0,
            $"未指定 APS_REAL_RUN_ID（当前值 '{runRaw ?? "(null)"}'）—— 本入口必须点名既有 ScheduleRunId 才能读到 MES 快照。");

        using var runCts = RealDomainFullRunTest.CreateRunCancellationCore(
            Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var timeoutMinutes);
        var ct = runCts.Token;
        if (timeoutMinutes > 0d)
        {
            _output.WriteLine($"[C] 跑批总超时 = {timeoutMinutes:0.##} 分钟（APS_RUN_TIMEOUT_MIN）。");
        }

        using var provider = BuildProductionProvider();
        var connectionManager = provider.GetRequiredService<DatabaseConnectionManager>();
        var orchestrator = provider.GetRequiredService<SchedulingOrchestrator>();

        var strategyVersionId = await ResolveDefaultStrategyVersionIdAsync(connectionManager, ct);
        _output.WriteLine($"[C] PlanVersionId={planVersionId}, ScheduleRunId={scheduleRunId}, 策略包版本={strategyVersionId}");

        // 关键：接上捕获 —— APS_CAPTURE_SOLVE_REQUEST=1 时才写盘，否则只打一行提示（复用 2号位 已落码的 internal 钩子）。
        using var capture = RealDomainFullRunTest.SolveRequestCapture.Start(planVersionId, _output);

        var sw = Stopwatch.StartNew();
        SchedulingRunResult result;
        try
        {
            result = await orchestrator.RunSchedulingAndFinalizeAsync(
                planVersionId, scheduleRunId, strategyVersionId, ct);
        }
        finally
        {
            sw.Stop();
        }

        _output.WriteLine($"[C] IsSuccess={result.IsSuccess}, Scheduled={result.ScheduledCount}, "
            + $"Unscheduled={result.UnscheduledCount}, 耗时={sw.Elapsed.TotalSeconds:F1}s");
        _output.WriteLine($"[C] Error={(result.ErrorMessage ?? "(null)")}");

        // 诊断入口：不把「0 任务 / 未排下」判成测试失败。捕获到的 JSON 才是目的。
    }

    /// <summary>verbatim 复刻 <c>RealDomainFullRunTest.BuildProductionProvider</c> 的生产组合根（少一行就不是生产口径）。</summary>
    private static ServiceProvider BuildProductionProvider()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(System.IO.Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddGovernanceRepositories();
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        services.AddLogging(b => b.AddProvider(new ConsoleOutLoggerProvider()));
        services.AddScoped<SchedulingOrchestrator>();

        return services.BuildServiceProvider();
    }

    /// <summary>verbatim 复刻 <c>RealDomainFullRunTest.ResolveDefaultStrategyVersionIdAsync</c> 的查询与唯一性红线。</summary>
    private static async Task<long> ResolveDefaultStrategyVersionIdAsync(
        DatabaseConnectionManager connectionManager, CancellationToken ct)
    {
        var pinned = Environment.GetEnvironmentVariable("APS_REAL_DOMAIN_STRATEGY_VERSION_ID");
        if (long.TryParse(pinned, out var pinnedId) && pinnedId > 0)
        {
            return pinnedId;
        }

        var rows = (await connectionManager.QueryAsync<long>(
            @"SELECT v.Id
              FROM StrategyProfileVersion v
              INNER JOIN StrategyProfile p ON p.Id = v.StrategyProfileId
              WHERE p.RunType = @RunType
                AND p.IsActive = 1
                AND v.IsDefault = 1
                AND v.Status = 'PUBLISHED'
                AND (v.EffectiveFrom IS NULL OR v.EffectiveFrom <= GETDATE())
                AND (v.EffectiveTo   IS NULL OR v.EffectiveTo   >= GETDATE())",
            new { RunType = HarnessRunType }, db: DatabaseId.APS)).ToList();

        Assert.False(rows.Count == 0,
            $"RunType={HarnessRunType} 下无有效的默认 PUBLISHED 策略包版本 —— 跑批无策略可依，拒绝执行。");
        Assert.False(rows.Count > 1,
            $"RunType={HarnessRunType} 下存在 {rows.Count} 个有效默认 PUBLISHED 策略包版本（{string.Join(",", rows)}）—— 歧义，拒绝执行。");

        return rows[0];
    }
}