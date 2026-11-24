using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Dto;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Models.Scheduling;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 · 只读】从库里重读 Pegging、**只落 SolveRequest JSON**，不调 Solver、不写库。
///
/// <para><b>为什么需要</b>：全流程入口 <c>RunSchedulingAndFinalizeAsync</c> / <c>RunSchedulingAsync</c>
/// 在跑之前会先 `DELETE` 本 PV 的 `[Task]` / `[Pegging]` / `AllocationTaskShare` /
/// `PeggingSupplyAllocation`（`SchedulingOrchestrator.cs:1300-1310`），而 0号位/2号位 正在分析
/// 上一轮落库数据 ⇒ **绝不能走那两个入口**。</para>
///
/// <para><b>本探针做什么</b>：复用生产的上下文装载（`LoadSchedulingContextAsync` /
/// `LoadStrategyConfigAsync` / `LoadPipelineSupplyAsync`，经反射避免改生产代码）与
/// <c>PeggingOrchestrator.RunPeggingOnlyAsync</c>（与生产共用同一段 <c>BuildSolveRequestAsync</c>，
/// 组出请求就返回），把 <c>DomainSolveRequest</c> 流式写到本地 JSON。</para>
///
/// <para><b>写入边界</b>：<c>persistPositionSnapshot:false</c>；不调用 `ExecutePeggingWorkflowAsync`
/// （Task/Pegging/ExplanationFact 的持久化都在那里）；不调 Solver。除本地 JSON 外**不写任何地方**。</para>
///
/// <para><b>开门</b>：
/// <code>APS_FROMDB_CAPTURE=1 APS_FROMDB_CAPTURE_PV_ID=999633 \
/// dotnet test LPS.APS.Tests --filter FullyQualifiedName~PeggingFromDbCaptureProbeTest</code>
/// 默认跳过（会读生产库）。</para>
/// </summary>
public class PeggingFromDbCaptureProbeTest
{
    private const string GateEnvVar = "APS_FROMDB_CAPTURE";

    private readonly ITestOutputHelper _output;

    public PeggingFromDbCaptureProbeTest(ITestOutputHelper output) => _output = output;

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_FROMDB_CAPTURE_PV_ID"), out var v) && v > 0
            ? v : 999633;

    private static string OutPath =>
        Environment.GetEnvironmentVariable("APS_FROMDB_CAPTURE_OUT")
        ?? $"D:/tmp/PV{PlanVersionId}_solverequest_fromdb_01.json";

    [SkippableFact(DisplayName = "1号位: 从库只读重跑 Pegging 并落 SolveRequest JSON（不调 Solver、不写库）")]
    public async Task CaptureFromDbAsync()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"只读装载默认关闭（会读生产库）。设 {GateEnvVar}=1 显式开启。");

        var pvId = PlanVersionId;

        // ── 超时（只覆盖装载段；本探针不调 Solver）────────────────────────────
        var timeoutMin = 30d;
        if (double.TryParse(Environment.GetEnvironmentVariable("APS_FROMDB_CAPTURE_TIMEOUT_MIN"), out var tm) && tm > 0)
            timeoutMin = tm;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));
        var ct = cts.Token;

        using var provider = BuildProductionProvider();
        var conn = provider.GetRequiredService<DatabaseConnectionManager>();
        var scheduling = provider.GetRequiredService<SchedulingOrchestrator>();
        var pegging = (PeggingOrchestrator)provider.GetRequiredService<IPeggingOrchestrator>();

        // ── PV / Run 身份（与生产 ExecuteDomainAsync 同一查询口径）────────────
        var pv = await conn.QueryFirstOrDefaultAsync<PlanVersionInfoDto>(
            @"SELECT pv.Id, pv.VersionCode, pv.DomainKey, pv.PlanHorizonStart, pv.PlanHorizonEnd,
                     pv.SourceScheduleRunId, pv.VersionCategory, sr.RunType
              FROM PlanVersion pv
              LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
              WHERE pv.Id = @Id",
            new { Id = pvId }, db: DatabaseId.APS);
        Skip.If(pv is null, $"PlanVersion {pvId} 不存在。");

        var run = await conn.QueryFirstOrDefaultAsync<RunRow>(
            "SELECT Id, StrategyProfileVersionId, DataCutoffTime FROM ScheduleRun WHERE Id = @Id",
            new { Id = pv!.SourceScheduleRunId }, db: DatabaseId.APS);

        var strategyVersionId = run?.StrategyProfileVersionId
            ?? (long.TryParse(Environment.GetEnvironmentVariable("APS_FROMDB_CAPTURE_STRATEGY_VERSION"), out var pinned)
                ? pinned : 0);
        if (strategyVersionId <= 0)
            throw new InvalidOperationException(
                "无法确定策略包版本：ScheduleRun.StrategyProfileVersionId 为空，且未设 APS_FROMDB_CAPTURE_STRATEGY_VERSION。");

        var dataCutoffTime = run?.DataCutoffTime ?? DateTime.Now;

        _output.WriteLine($"[C] PV={pvId} 域={pv.DomainKey} Run={pv.SourceScheduleRunId} "
            + $"策略包={strategyVersionId} DataCutoff={dataCutoffTime:yyyy-MM-dd HH:mm:ss}");
        _output.WriteLine($"[C] 计划期 {pv.PlanHorizonStart:yyyy-MM-dd HH:mm} ~ {pv.PlanHorizonEnd:yyyy-MM-dd HH:mm}");
        _output.WriteLine("[C] 只读边界：不调 ExecutePeggingWorkflowAsync、不调 Solver、persistPositionSnapshot=false");

        // ── 1) 生产同一份上下文装载（反射调用 private，避免改生产代码）──────────────
        //   LoadSchedulingContextAsync = Resources/Calendar + Inventory + MESProgress
        var context = await InvokePrivateAsync<SchedulingContext>(
            scheduling, "LoadSchedulingContextAsync", pv, pv.SourceScheduleRunId, ct);
        context.StrategyProfileVersionId = strategyVersionId;
        context.DataCutoffTime = dataCutoffTime;
        await InvokePrivateVoidAsync(scheduling, "LoadStrategyConfigAsync", context, strategyVersionId, ct);
        await InvokePrivateVoidAsync(scheduling, "LoadPipelineSupplyAsync", context, ct);

        _output.WriteLine($"[C] 上下文：Resources={context.Resources.Count}, "
            + $"InventorySupplies={context.InventorySupplies.Count}, MESRemainingQty={context.MESRemainingQty.Count}, "
            + $"VirtualInventory={context.VirtualInventory.Count}");

        // ── 2) 需求根集合：本域订单 − 厂间出荷指示（与生产同一条 SQL）────────────
        var orderIds = (await conn.QueryAsync<long>(
            @"SELECT Id FROM [Order]
              WHERE PlanVersionId = @PlanVersionId
                AND NOT (ISNULL(OrderType, N'') = N'SALES_ORDER' AND ISNULL(CustomerSegment, N'') = N'INTER_FACTORY')
              ORDER BY Id",
            new { PlanVersionId = pvId }, db: DatabaseId.APS)).ToList();
        _output.WriteLine($"[C] 需求根订单数（已排除 SH）={orderIds.Count}");

        // ── 3) PeggingExecutionRequest：逐字段对齐生产 BuildPeggingRequest ─────────
        var now = DateTime.Now;
        var request = new PeggingExecutionRequest
        {
            PlanVersionId = pvId,
            DomainKey = pv.DomainKey ?? string.Empty,
            OrderIds = orderIds,
            SnapshotAt = context.DataCutoffTime ?? now,
            FrozenWindowStart = now,
            FrozenWindowEnd = now.AddHours(2),
            AllowCrossFactory = false,
            DefaultStrategy = PeggingStrategyType.FIFO,
            ProductFamilyIds = context.Tasks
                .Select(t => int.TryParse(t.MaterialId, out var mid) ? mid : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList(),
            UpstreamResourceBlocks = null,
            UpstreamPhysicalConsumption = null,
            UpstreamAggregateConsumption = null,
            MaxBomDepth = 10,
            TimeoutSeconds = 300,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            ExternalDomainResourceBlocks = null,
            SchedulingContext = context
        };

        // ── 4) 只到 SolveRequest 为止（与生产共用 BuildSolveRequestAsync）──────────
        var sw = Stopwatch.StartNew();
        var payload = await pegging.RunPeggingOnlyAsync(
            request, ct, scopeBomToOrders: false, persistPositionSnapshot: false);
        sw.Stop();

        var req = payload.SolveRequest;
        _output.WriteLine($"[C] Pegging 装载完成：{sw.Elapsed.TotalSeconds:F1}s");
        _output.WriteLine($"[C] Voucher: LPD={payload.Voucher.LogicalProductionDemands?.Count ?? 0}, "
            + $"RedLineErrors={payload.RedLineErrors.Count}, OverCommits={payload.OverCommits.Count}");
        _output.WriteLine($"[C] SolveRequest: Demands={req.LogicalProductionDemands?.Count ?? 0}, "
            + $"RoutingOperations={req.RoutingOperations?.Count ?? 0}, "
            + $"Resources={req.Resources?.Count ?? 0}, "
            + $"MaterialStageDepartmentContexts={req.MaterialStageDepartmentContexts?.Count ?? 0}");
        _output.WriteLine("[C] 对照上轮生产日志：CYL 期望 LPD=120,995 / 订单根≈116,615；"
            + "FRL 期望 LPD=98,855 / 订单根≈51,404");

        // ── 5) 流式落盘（与生产捕获同款：IgnoreCycles + 1MB 缓冲，避免 LOH）──────────
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };

        var dir = Path.GetDirectoryName(OutPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        await using (var fs = new FileStream(OutPath, FileMode.Create, FileAccess.Write,
                       FileShare.Read, bufferSize: 1 << 20))
        {
            await JsonSerializer.SerializeAsync(fs, req, options, ct);
        }

        var fi = new FileInfo(OutPath);
        _output.WriteLine($"[C] JSON 已写：{OutPath}（{fi.Length / 1024d / 1024d:F1} MB）");
        _output.WriteLine("[C] 下一步（离线、零读库）：APS_REPLAY_SOLVE_REQUEST=<上面的JSON> "
            + "APS_REPLAY_DUMP=... APS_REPLAY_TASK_DUMP=... dotnet test --filter ReplayCapturedSolveRequestAsync");
    }

    // ── 反射：只调 private 装载方法，不改生产代码 ────────────────────────────
    private static async Task<T> InvokePrivateAsync<T>(object target, string method, params object?[] args)
    {
        var mi = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到私有方法 {method}");
        var task = (Task)mi.Invoke(target, args)!;
        await task;
        var resultProp = task.GetType().GetProperty("Result")
            ?? throw new InvalidOperationException($"{method} 不是 Task<T>");
        return (T)resultProp.GetValue(task)!;
    }

    private static async Task InvokePrivateVoidAsync(object target, string method, params object?[] args)
    {
        var mi = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到私有方法 {method}");
        await (Task)mi.Invoke(target, args)!;
    }

    private sealed class RunRow
    {
        public int Id { get; set; }
        public long? StrategyProfileVersionId { get; set; }
        public DateTime? DataCutoffTime { get; set; }
    }

    private static ServiceProvider BuildProductionProvider()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
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
        services.AddLogging();
        services.AddScoped<DatabaseConnectionManager>();
        services.AddScoped<SchedulingOrchestrator>();
        return services.BuildServiceProvider();
    }
}
