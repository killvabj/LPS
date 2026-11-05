using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Fixtures;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **⑧ Batch Policy 覆盖度探针**（2号位，2026-10-31）。
///
/// 用途：1号位 2026-10-31 实测报出 `BATCH_POLICY_MISSING 1,676 / BATCH_POLICY_CONFLICT 311`，
/// 请求我方给出「逐条需求的 (MaterialId, ProductionDepartment) 键清单」。需求集**不落库**
/// （`LogicalProductionDemand` 只在内存里），纯 SQL 拿不到 ⇒ 必须走同一段装载再逐条判定。
///
/// 与生产同源：`PeggingOrchestrator.RunPeggingOnlyAsync`（与 `BuildSolveRequestAsync` 共用，
/// **不调 Solver、不落库**）；判定**直接调用 1号位 的公开/内部函数**，不在此复写一份规则：
///   · `PhaseTwoInitialScheduler.FormExecutionBatches`（public）—— 批形成 + B-010 兜底 + B-009 PI 门禁；
///   · `PhaseTwoInitialScheduler.BuildPiStageQuantityIndex` / `ResolvePiStageContext`（internal，经
///     `InternalsVisibleTo("LPS.APS.Tests")`）。
/// 唯一复写的是 `PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy` 的**载体查表**（私有方法，
/// 逻辑即「按 (MaterialId, dept) 取 BatchPolicyStates 一条」，逐行对照其 `:5157-5296` 抄口径）。
///
/// 产出（TSV，带时刻；写文件而不是 `ITestOutputHelper` —— 中途被掐断时输出缓冲看不到）：
///   1. `*_demands.tsv`：逐条需求一行（含分类与原因前缀）；
///   2. `*_missing_keys.tsv`：MISSING/CONFLICT 按 (MaterialId, Dept) 聚合的键清单 + 需求数。
///
/// 默认**不跑**（不设 `APS_BP_PROBE_RUN=1` 直接返回），防被宽泛 `dotnet test` 误触发。
/// 环境变量：
///   APS_BP_PROBE_RUN=1            启用
///   APS_BP_PROBE_PV               PlanVersionId，默认 999329
///   APS_BP_PROBE_STRATEGY         策略包版本，默认 1970
///   APS_BP_PROBE_CUTOFF           DataCutoff，默认 2026-10-30T11:01:16（对齐 1号位 本轮）
///   APS_BP_PROBE_SNAPSHOT         `PeggingExecutionRequest.SnapshotAt`，默认=跑批时刻。
///                                 ⚠ 它**同时是** PI 级 XC 24h 新鲜度闸的实际比较基准
///                                 （`PeggingOrchestrator` :4607 `cutoff = request.SnapshotAt`），
///                                 与 `APS_BP_PROBE_CUTOFF` **不是同一个值**。手工白天重跑跨过当日
///                                 XC 采集后 24h 会被整轮拒绝，用本变量把 SnapshotAt 退回到
///                                 闸门内的时刻即可，无需刷新 XC 快照、不改任何数据。
///   APS_BP_PROBE_TIMEOUT_MIN      超时分钟，默认 40
///   APS_BP_PROBE_OUT              输出目录，默认 D:/CODE/LPS/Database/Scripts/APS/04_测试与备份/运行日志
/// </summary>
public class BatchPolicyCoverageProbeTest
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("APS_BP_PROBE_RUN"), "1", StringComparison.Ordinal);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_BP_PROBE_PV"), out var v) ? v : 999329;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_BP_PROBE_STRATEGY"), out var v) ? v : 1970L;

    private static DateTime DataCutoff =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_BP_PROBE_CUTOFF"), out var v)
            ? v
            : new DateTime(2026, 10, 30, 11, 1, 16);

    /// <summary>
    /// `PeggingExecutionRequest.SnapshotAt`（默认=跑批时刻，保持生产同源行为）。
    /// 注意它同时是 PI 级 XC 24h 新鲜度闸的比较基准（见文件头说明），与 <see cref="DataCutoff"/> 不同源。
    /// </summary>
    private static DateTime SnapshotAt =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_BP_PROBE_SNAPSHOT"), out var v)
            ? v
            : DateTime.Now;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_BP_PROBE_TIMEOUT_MIN"), out var v) ? v : 40;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_BP_PROBE_OUT")
        ?? @"D:/CODE/LPS/Database/Scripts/APS/04_测试与备份/运行日志";

    [Fact(DisplayName = "⑧BatchPolicy 覆盖度：逐需求键与硬失败分类（只装载不求解）")]
    public async Task BatchPolicyCoverageProbe()
    {
        if (!Enabled) return;

        var runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(OutDir);
        var stageLogPath = Path.Combine(OutDir, $"BP探针_{PlanVersionId}_{runStamp}_stage.log");

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
        // 不挂 Fixture：⑧ 必须走生产路径（TaskSplitRuleConfig 表直读），否则规则是样例值，结论无意义。
        // 2026-10-31：只放行 PeggingOrchestrator 的日志并落文件 —— `ITestOutputHelper` 结束才刷盘，
        // 被掐断的跑批看不到任何诊断；阶段耗时（Mark）必须能事后回看。
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new OrchestratorFileLoggerProvider(stageLogPath));
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

        // SnapshotAt 同时是 XC 24h 闸的基准，默认=当前时刻；可用 APS_BP_PROBE_SNAPSHOT 退回闸门内。
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

        // 全量口径（不收窄 BOM 宇宙）——与 1号位 本轮一致：Orders=21,280。
        var payload = await orchestrator.RunPeggingOnlyAsync(request, cts.Token, scopeBomToOrders: false);
        var req = payload.SolveRequest;

        var stamp = runStamp;
        var demandPath = Path.Combine(OutDir, $"BP探针_{PlanVersionId}_{stamp}_demands.tsv");
        var keyPath = Path.Combine(OutDir, $"BP探针_{PlanVersionId}_{stamp}_missing_keys.tsv");
        var summaryPath = Path.Combine(OutDir, $"BP探针_{PlanVersionId}_{stamp}_summary.txt");

        // ── 载体查表（逐行对照 PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy :5130-5296）──
        var deptByMaterialStage = new Dictionary<(int MaterialId, string StageCode), int>();
        foreach (var c in req.MaterialStageDepartmentContexts)
        {
            deptByMaterialStage[(c.MaterialId, c.StageCode ?? string.Empty)] = c.ProductionDepartmentId;
        }

        var stateByKey = new Dictionary<(int MaterialId, int? Dept), BatchPolicyStateSnapshot>();
        foreach (var s in req.StrategySnapshot?.BatchPolicyStates ?? [])
        {
            stateByKey[(s.MaterialId, s.ProductionDepartmentId)] = s;
        }

        var policies = req.StrategySnapshot?.BatchPolicies ?? [];

        var piStageIndex = PhaseTwoInitialScheduler.BuildPiStageQuantityIndex(req.PiStageQuantityBaselines, out var ambiguous);
        var ledger = new PhaseTwoInitialScheduler.PiStageCommitLedger();

        var rows = new List<DemandRow>(req.LogicalProductionDemands.Count);
        foreach (var d in req.LogicalProductionDemands)
        {
            int? dept = deptByMaterialStage.TryGetValue((d.MaterialId, d.StartStageCode ?? string.Empty), out var dd)
                ? dd
                : null;

            var hasState = stateByKey.TryGetValue((d.MaterialId, dept), out var state);
            var carrierState = hasState ? state!.State.ToString() : "(键未覆盖)";
            var matchSource = hasState ? state!.MatchSource : "";

            BatchPolicyRuleSnapshot? policy = null;
            var cls = "";
            var reason = "";

            if (dept is null)
            {
                // BP-KEY-01 / 0号位 v2.0 §1.3：部门无法确定属技术 UNKNOWN，不得伪装成 MISSING。
                // 生产侧 PhaseOneConstraintBuilder.ApplyDepartmentLock(:416) 在 Phase 1 就先按
                // MISSING_PRODUCTION_DEPARTMENT_CONTEXT 拦掉，到不了 Phase 2 的 BatchPolicy 判定。
                // 探针跳过 Phase 0/1，此前会把这类需求误打成 MISSING，虚增缺配数。
                cls = "UNKNOWN(缺部门上下文)";
                reason = string.IsNullOrEmpty(d.StartStageCode)
                    ? "StartStageCode 为空，无法定位部门"
                    : "无 (MaterialId, StageCode) 部门上下文";
            }
            else if (!hasState)
            {
                // 1号位 C-2：载体未覆盖键 ⇒ Unknown ⇒ 硬失败（非 MISSING）。
                cls = "STATE_UNAVAILABLE";
                reason = "载体未覆盖业务键";
            }
            else if (state!.State == BatchPolicyState.Invalid)
            {
                // 1号位 :991-998：Invalid 复用 BATCH_POLICY_CONFLICT 出口码。
                cls = "CONFLICT(Invalid)";
                reason = "本键有规则但不在生效窗内";
            }
            else if (state.State == BatchPolicyState.Unavailable)
            {
                cls = "STATE_UNAVAILABLE";
                reason = state.MatchSource;
            }
            else
            {
                if (state.State == BatchPolicyState.Valid && dept is int dpt)
                {
                    policy = policies.FirstOrDefault(p =>
                        p.MaterialId == d.MaterialId && p.ProductionDepartmentId == dpt);
                    if (policy is null)
                    {
                        cls = "STATE_UNAVAILABLE";
                        reason = "State=Valid 但 BatchPolicies 无同键参数行";
                    }
                }

                if (cls.Length == 0)
                {
                    var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
                        d,
                        policy,
                        piFacts: req.PiRemainingFacts,
                        piStageContext: PhaseTwoInitialScheduler.ResolvePiStageContext(
                            piStageIndex, d.ProductionInstructionNo, d.MaterialId, d.StartStageCode),
                        piStageLedger: ledger);

                    if (formation.IsLegal)
                    {
                        cls = "LEGAL";
                        reason = formation.IsPiContractPending ? "PiContractPending" : "";
                    }
                    else
                    {
                        cls = formation.IsMissingPolicy ? "MISSING" : "CONFLICT";
                        reason = formation.ConflictReason ?? "";
                    }
                }
            }

            rows.Add(new DemandRow(
                d.LogicalDemandKey, d.MaterialId, d.StartStageCode ?? "", dept, d.ProductionInstructionNo,
                d.NetOutputQty, d.IsContinuation, d.NoSplitMerge, carrierState, matchSource, cls, reason));
        }

        // ── 明细 TSV ──
        var sb = new StringBuilder();
        sb.AppendLine("LogicalDemandKey\tMaterialId\tStartStageCode\tDept\tPI\tNetOutputQty\tIsContinuation\tNoSplitMerge\tCarrierState\tMatchSource\tClass\tReason");
        foreach (var r in rows)
        {
            sb.Append(r.LogicalDemandKey).Append('\t')
              .Append(r.MaterialId).Append('\t')
              .Append(r.StartStageCode).Append('\t')
              .Append(r.Dept?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
              .Append(r.Pi ?? "").Append('\t')
              .Append(r.Qty.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.IsContinuation).Append('\t')
              .Append(r.NoSplitMerge).Append('\t')
              .Append(r.CarrierState).Append('\t')
              .Append(r.MatchSource.Replace('\t', ' ')).Append('\t')
              .Append(r.Class).Append('\t')
              .Append(r.Reason.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '))
              .AppendLine();
        }

        File.WriteAllText(demandPath, sb.ToString(), new UTF8Encoding(false));

        // ── 键清单 TSV（只列硬失败）──
        var kb = new StringBuilder();
        kb.AppendLine("Class\tReasonPrefix\tMaterialId\tDept\tDemandCount\tDistinctPI\tSampleKey");
        foreach (var g in rows.Where(r => r.Class != "LEGAL")
                              .GroupBy(r => new
                              {
                                  r.Class,
                                  ReasonPrefix = Prefix(r.Reason),
                                  r.MaterialId,
                                  r.Dept
                              })
                              .OrderByDescending(g => g.Count()))
        {
            var distinctPi = g.Where(r => !string.IsNullOrWhiteSpace(r.Pi)).Select(r => r.Pi).Distinct().Count();
            kb.Append(g.Key.Class).Append('\t')
              .Append(g.Key.ReasonPrefix).Append('\t')
              .Append(g.Key.MaterialId).Append('\t')
              .Append(g.Key.Dept?.ToString(CultureInfo.InvariantCulture) ?? "NULL").Append('\t')
              .Append(g.Count()).Append('\t')
              .Append(distinctPi).Append('\t')
              .Append(g.First().LogicalDemandKey)
              .AppendLine();
        }

        File.WriteAllText(keyPath, kb.ToString(), new UTF8Encoding(false));

        // ── 摘要 ──
        var sum = new StringBuilder();
        sum.AppendLine($"PV={PlanVersionId} Strategy={StrategyProfileVersionId} DataCutoff={DataCutoff:yyyy-MM-dd HH:mm:ss} 跑批时刻={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sum.AppendLine($"Orders={orderIds.Count} Demands={rows.Count} BatchPolicies={policies.Count} BatchPolicyStates={(req.StrategySnapshot?.BatchPolicyStates?.Count ?? 0)} PiRemainingFacts={req.PiRemainingFacts?.Count ?? 0} PiStageQuantityBaselines={req.PiStageQuantityBaselines?.Count ?? 0} PiStageIndexKeys={piStageIndex.Count} PiStageAmbiguousKeys={ambiguous.Count}");
        sum.AppendLine($"MaterialStageDepartmentContexts={req.MaterialStageDepartmentContexts.Count}");
        sum.AppendLine();
        sum.AppendLine("[按分类]");
        foreach (var g in rows.GroupBy(r => r.Class).OrderByDescending(g => g.Count()))
        {
            sum.AppendLine($"  {g.Key}\t{g.Count()}");
        }

        sum.AppendLine();
        sum.AppendLine("[按 (分类 × 原因前缀)]");
        foreach (var g in rows.Where(r => r.Class != "LEGAL")
                              .GroupBy(r => (r.Class, Prefix(r.Reason)))
                              .OrderByDescending(g => g.Count()))
        {
            sum.AppendLine($"  {g.Key.Class}\t{g.Key.Item2}\t{g.Count()}");
        }

        sum.AppendLine();
        sum.AppendLine("[载体三态分布（逐需求）]");
        foreach (var g in rows.GroupBy(r => r.CarrierState).OrderByDescending(g => g.Count()))
        {
            sum.AppendLine($"  {g.Key}\t{g.Count()}");
        }

        sum.AppendLine();
        sum.AppendLine("[载体 MatchSource 分布（逐需求）]");
        foreach (var g in rows.GroupBy(r => r.MatchSource).OrderByDescending(g => g.Count()))
        {
            sum.AppendLine($"  {(string.IsNullOrEmpty(g.Key) ? "(空)" : g.Key)}\t{g.Count()}");
        }

        // 2026-10-31：PI Position 切片类型分布 —— 用来量「Stage 明细圈定口径」改动前后的位置质量
        // （UNLOCATED 对 → 落到具体 Stage 的对数）。
        sum.AppendLine();
        sum.AppendLine("[PI Position 切片类型分布（切片数 / 量 / 涉及PI数）]");
        var allSlices = payload.PiPositions
            .SelectMany(kv => kv.Value.Positions.Select(s => (Pi: kv.Key, s.PositionType, s.Quantity)))
            .ToList();
        foreach (var g in allSlices.GroupBy(x => x.PositionType.ToString()).OrderByDescending(g => g.Count()))
        {
            sum.AppendLine($"  {g.Key}\t切片={g.Count()}\t量={g.Sum(x => x.Quantity):0.####}\tPI数={g.Select(x => x.Pi).Distinct().Count()}");
        }
        sum.AppendLine($"  （PI 数={payload.PiPositions.Count}，切片合计={allSlices.Count}，量合计={allSlices.Sum(s => s.Quantity):0.####}）");

        File.WriteAllText(summaryPath, sum.ToString(), new UTF8Encoding(false));

        // 2026-10-31：逐 PI 逐切片明细 —— 5号位《首Stage残差项》§四.3 要求「按原分桶重跑，
        // 回报 310/268 对的落点」。只有逐对明细才能回答「这一对现在落在哪个位置类型」，
        // 聚合分布答不了。**零 DB 写入**（探针 persistPositionSnapshot:false）。
        var posPath = Path.Combine(OutDir, $"BP探针_{PlanVersionId}_{stamp}_positions.tsv");
        var pb = new StringBuilder();
        pb.AppendLine("PI\tPositionType\tQuantity\tStageCode\tIsUnlocated\tSourceKey");
        foreach (var kv in payload.PiPositions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var s in kv.Value.Positions)
            {
                pb.Append(kv.Key).Append('\t')
                  .Append(s.PositionType.ToString()).Append('\t')
                  .Append(s.Quantity.ToString("0.####", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(s.StageCode ?? string.Empty).Append('\t')
                  .Append(s.IsUnlocated ? "1" : "0").Append('\t')
                  .Append(s.SourceKey ?? string.Empty).Append('\n');
            }
        }
        File.WriteAllText(posPath, pb.ToString(), new UTF8Encoding(false));

        Assert.True(File.Exists(demandPath));
    }

    /// <summary>只落 `PeggingOrchestrator` 分类的日志到文件（阶段耗时 Mark / 供给池规模）。</summary>
    private sealed class OrchestratorFileLoggerProvider : ILoggerProvider
    {
        private readonly StreamWriter _writer;

        public OrchestratorFileLoggerProvider(string path)
        {
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public ILogger CreateLogger(string categoryName) => new OrchestratorFileLogger(_writer, categoryName);

        public void Dispose() => _writer.Dispose();

        private sealed class OrchestratorFileLogger : ILogger
        {
            private readonly StreamWriter _writer;
            private readonly string _category;
            private readonly bool _enabled;
            private readonly object _gate = new();

            public OrchestratorFileLogger(StreamWriter writer, string category)
            {
                _writer = writer;
                _category = category;
                _enabled = category.Contains("PeggingOrchestrator", StringComparison.Ordinal);
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => _enabled && logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (_gate)
                {
                    _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    _writer.Write('\t');
                    _writer.Write(logLevel);
                    _writer.Write('\t');
                    _writer.Write(formatter(state, exception));
                    if (exception is not null)
                    {
                        _writer.Write('\n');
                        _writer.Write(exception);
                    }
                    _writer.Write('\n');
                }
            }
        }
    }

    private static string Prefix(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "(none)";
        var idx = reason.IndexOf(':');
        return idx > 0 ? reason[..idx] : (reason.Length > 60 ? reason[..60] : reason);
    }

    private sealed record DemandRow(
        string LogicalDemandKey,
        int MaterialId,
        string StartStageCode,
        int? Dept,
        string? Pi,
        decimal Qty,
        bool IsContinuation,
        bool NoSplitMerge,
        string CarrierState,
        string MatchSource,
        string Class,
        string Reason);

    /// <summary>`PlanVersion` 计划期投影（与 `LoadToPosition1AuditTest` 同款）。</summary>
    private sealed class PlanVersionHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }
}
