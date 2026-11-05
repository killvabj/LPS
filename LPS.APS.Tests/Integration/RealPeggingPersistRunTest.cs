using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Application.Services;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **单域「Pegging 真实落库、不求解」跑批**（2号位，2026-10-31）。
///
/// 与探针的差别只有一处：把 PI Position 快照**真写进** `ProductionInstructionPositionSnapshot`，
/// 走的是生产同一段 `SavePiPositionSnapshotsAsync`（先按 (ScheduleRunId, PlanVersionId) DELETE 再逐行 INSERT，
/// 天然幂等）。**仍然不调 Solver**。
///
/// 用途：验证 2026-10-31 的装载侧四项整改在生产表上的真实效果——
/// 位置总量是否收敛进 ERP 剩余、PositionType 分布、`UNLOCATED` 是否下降。
///
/// 默认**不跑**（必须显式 `APS_REALRUN=1`），防被宽泛 `dotnet test` 误触发。
/// 环境变量：
///   APS_REALRUN=1            启用（缺省直接返回，不写库）
///   APS_REALRUN_PV           PlanVersionId，默认 999329
///   APS_REALRUN_STRATEGY     策略包版本，默认 1970
///   APS_REALRUN_CUTOFF       DataCutoff，默认 2026-10-30T11:01:16
///   APS_REALRUN_SNAPSHOT     `SnapshotAt`（同时是 XC 24h 新鲜度闸的基准），默认=跑批时刻
///   APS_REALRUN_TIMEOUT_MIN  超时分钟，默认 45
///   APS_REALRUN_OUT          输出目录
/// </summary>
public class RealPeggingPersistRunTest
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("APS_REALRUN"), "1", StringComparison.Ordinal);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_REALRUN_PV"), out var v) ? v : 999329;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_REALRUN_STRATEGY"), out var v) ? v : 1970L;

    private static DateTime DataCutoff =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_REALRUN_CUTOFF"), out var v)
            ? v
            : new DateTime(2026, 10, 30, 11, 1, 16);

    private static DateTime SnapshotAt =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_REALRUN_SNAPSHOT"), out var v)
            ? v
            : DateTime.Now;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_REALRUN_TIMEOUT_MIN"), out var v) ? v : 45;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_REALRUN_OUT")
        ?? @"D:/CODE/LPS/Database/Scripts/APS/04_测试与备份/运行日志";

    [Fact(DisplayName = "真实落库：单域 Pegging 写 PI Position 快照（不求解）")]
    public async Task RealPeggingPersistRun()
    {
        if (!Enabled) return;

        var runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(OutDir);
        var stageLogPath = Path.Combine(OutDir, $"实跑落库_{PlanVersionId}_{runStamp}_stage.log");

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddSchedulingServices();
        services.AddBusinessRuleServices();
        services.AddApplicationServices();
        services.AddGovernanceRepositories();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(stageLogPath));
        });
        services.AddScoped<DatabaseConnectionManager>();
        services.AddScoped<SchedulingOrchestrator>();

        var sp = services.BuildServiceProvider();
        var orchestrator = (PeggingOrchestrator)sp.GetRequiredService<IPeggingOrchestrator>();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();
        var schedulingOrchestrator = sp.GetRequiredService<SchedulingOrchestrator>();

        var pv = await conn.QueryFirstOrDefaultAsync<PlanVersionHorizon>(
            "SELECT DomainKey, PlanHorizonStart, PlanHorizonEnd FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS);
        var orderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        var now = SnapshotAt;
        var schedulingContext = new SchedulingContext
        {
            StrategyProfileVersionId = StrategyProfileVersionId,
            DataCutoffTime = DataCutoff,
            PlanHorizonStart = pv?.PlanHorizonStart ?? now,
            PlanHorizonEnd = pv?.PlanHorizonEnd ?? now.AddDays(90)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));
        await schedulingOrchestrator.LoadResourcesAndCalendarAsync(schedulingContext, cts.Token);

        var request = new PeggingExecutionRequest
        {
            PlanVersionId = PlanVersionId,
            DomainKey = pv?.DomainKey ?? string.Empty,
            OrderIds = orderIds,
            SnapshotAt = now,
            FrozenWindowStart = now,
            FrozenWindowEnd = now.AddHours(2),
            AllowCrossFactory = false,
            DefaultStrategy = PeggingStrategyType.FIFO,
            MaxBomDepth = 10,
            TimeoutSeconds = 900,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            SchedulingContext = schedulingContext
        };

        // ← 与探针唯一差异：persistPositionSnapshot:true（真写库），仍不调 Solver。
        var payload = await orchestrator.RunPeggingOnlyAsync(
            request, cts.Token, scopeBomToOrders: false, persistPositionSnapshot: true);

        var lines = new List<string>
        {
            $"PV={PlanVersionId} Domain={pv?.DomainKey} Strategy={StrategyProfileVersionId} " +
            $"DataCutoff={DataCutoff:yyyy-MM-dd HH:mm:ss} 跑批时刻={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Orders={request.OrderIds.Count} Demands={payload.SolveRequest.LogicalProductionDemands.Count} " +
            $"PI位置事实={payload.PiPositions.Count}",
            $"红线条数={payload.RedLineErrors.Count} 超承诺事件={payload.OverCommits.Count}",
            $"PeggingMs={payload.PeggingMs} TotalMs={payload.TotalMs}",
            "=== 红线明细 ==="
        };
        lines.AddRange(payload.RedLineErrors.Take(200).Select(e => "  " + e));

        var summaryPath = Path.Combine(OutDir, $"实跑落库_{PlanVersionId}_{runStamp}_summary.txt");
        await File.WriteAllLinesAsync(summaryPath, lines);

        Assert.True(true);
    }

    private sealed class PlanVersionHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }

    private sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly string _path;

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
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null) line += Environment.NewLine + exception;
                lock (_gate) File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
    }
}
