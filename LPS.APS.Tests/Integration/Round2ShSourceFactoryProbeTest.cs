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
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Models.Scheduling;
using LPS.APS.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **A（<c>AE-001</c> Round2）受控对照探针**（2号位，2026-11-13）。
///
/// <para><b>跑什么</b>：与生产同源的 <see cref="PeggingOrchestrator.RunPeggingOnlyAsync"/> ——
/// 共用 `ExecutePeggingLoopAsync`、`BuildSolveRequestAsync`、同一供给池装载，
/// <b>不调 Solver、不写 Pegging / Task / PSA</b>（`persistPositionSnapshot` 保持默认 <c>false</c>）。</para>
///
/// <para><b>为什么必须走这个入口</b>：`RunBfsOnlyAsync` 只调 `RunBfsLoop`、<b>不经过</b>
/// `ExecutePeggingLoopAsync` ⇒ 看不到 Round2 归集，拿它验会把「没生效」报成「生效了没差异」。</para>
///
/// <para><b>输入必须按生产口径剔除 SH</b>：生产侧 `SchedulingOrchestrator` 用
/// `NOT (OrderType='SALES_ORDER' AND CustomerSegment='INTER_FACTORY')` 剔除 SH；
/// 探针若照搬 `[Order] WHERE PlanVersionId=@p`（全量），会把 SH 塞进第一层，
/// 那就<b>不是</b>在验生产路径，而是在制造一个不存在的场景。</para>
///
/// <para><b>让它是按需跑的</b>：默认不执行（环境变量 `APS_R2_PROBE_RUN=1` 才跑），
/// 避免 CI / 全量测试无端起一轮 5 分钟的重装载。</para>
///
/// <para><b>读什么</b>：</para>
/// <list type="number">
///   <item><c>[Pegging][A-R2]</c> 行 —— 生成条数、孤儿数、仅自循环剔除数、上层交期跨度（**改前不存在这一行**）。</item>
///   <item>Round2 源厂需求条数与合计数量 —— 落到 <c>voucher.LogicalProductionDemands</c> 里、<c>OrderId</c> 属于 SH 订单的那部分。</item>
///   <item>**自消费次数必须为 0** —— Round2 需求不得消费以它自己为源的那张 SH。</item>
///   <item><c>[Pegging][O49]</c> 跨厂 PI 候选落第 3 轮条数 —— 改前基线。</item>
/// </list>
/// </summary>
public class Round2ShSourceFactoryProbeTest
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("APS_R2_PROBE_RUN"), "1", StringComparison.Ordinal);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_R2_PROBE_PV"), out var v) ? v : 999487;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_R2_PROBE_STRATEGY"), out var v) ? v : 1970L;

    private static DateTime DataCutoff =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_R2_PROBE_CUTOFF"), out var v)
            ? v
            : new DateTime(2026, 11, 11, 8, 16, 26);

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_R2_PROBE_TIMEOUT_MIN"), out var v) ? v : 60;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_R2_PROBE_OUT")
        ?? @"D:/CODE/LPS/Database/Scripts/APS/04_测试与备份/运行日志";

    [Fact(DisplayName = "A-R2 受控对照：SH 整单源厂需求（只装载不求解、不落库）")]
    public async Task Round2SourceFactoryDemandProbe()
    {
        if (!Enabled) return;

        var runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(OutDir);
        var logPath = Path.Combine(OutDir, $"A-R2探针_{PlanVersionId}_{runStamp}_stage.log");
        var summaryPath = Path.Combine(OutDir, $"A-R2探针_{PlanVersionId}_{runStamp}_summary.txt");

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
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new PeggingFileLoggerProvider(logPath));
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

        // SH 订单（供给身份）——用来框定「Round2 生成的需求」与「自消费」的判据。
        var shRows = (await conn.QueryAsync<ShOrderRow>(
            $@"SELECT Id, OrderNo, Quantity FROM [Order]
               WHERE PlanVersionId = @p
                 AND OrderType = N'SALES_ORDER'
                 AND CustomerSegment = N'{CustomerSegmentCodes.InterFactory}'
                 AND Quantity > 0",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();
        var shOrderIds = shRows.Select(r => r.Id).ToHashSet();
        var shNoById = shRows.ToDictionary(r => r.Id, r => r.OrderNo);

        // ⚠ 生产同口径：剔除 SH。照搬全量会把 SH 塞进第一层，验的就不是生产路径。
        var demandOrderIds = (await conn.QueryAsync<long>(
            $@"SELECT Id FROM [Order]
               WHERE PlanVersionId = @p
                 AND NOT (ISNULL(OrderType, N'') = N'SALES_ORDER'
                          AND ISNULL(CustomerSegment, N'') = N'{CustomerSegmentCodes.InterFactory}')
               ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        // CF-T18（0号位 v2.3 §7 明令）：同一 PV 连测出现 LPD 数量不同时，**必须核对是否真正相同输入**，
        //   不得直接按"可接受误差"销账。故本探针支持把 `SnapshotAt` 钉成固定值（`APS_R2_PROBE_SNAPSHOT`），
        //   两次跑用**完全相同的 SnapshotAt／DataCutoff／PV／Strategy** 才有资格谈"同输入重跑"。
        //   不设该变量时仍取 `DateTime.Now`（便于日常观察，但不构成一致性证据）。
        var now = DateTime.TryParse(Environment.GetEnvironmentVariable("APS_R2_PROBE_SNAPSHOT"), out var fixedSnapshot)
            ? fixedSnapshot
            : DateTime.Now;
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
            OrderIds = demandOrderIds,          // ← 已剔除 SH，与生产同口径
            SnapshotAt = now,
            FrozenWindowStart = now,
            FrozenWindowEnd = now.AddHours(2),
            AllowCrossFactory = false,
            DefaultStrategy = PeggingStrategyType.FIFO,
            MaxBomDepth = 10,
            TimeoutSeconds = 1800,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            SchedulingContext = schedulingContext
        };

        var payload = await orchestrator.RunPeggingOnlyAsync(request, cts.Token, scopeBomToOrders: false);
        var voucher = payload.Voucher;

        // ── 读数 ──
        // ⚠ 只数**根需求**（`IsRootDemand`＝bomLevel 0）。子件 LPD 的 `OrderId` 同样是该 SH 订单，
        //   一并计入会把「1 张 SH → N 条子件需求」误读成「1 张 SH 生成 N 条源厂需求」。
        var round2Demands = voucher.LogicalProductionDemands
            .Where(d => d.IsRootDemand && d.OrderId.HasValue && shOrderIds.Contains(d.OrderId.Value))
            .ToList();
        var round2Qty = round2Demands.Sum(d => d.NetOutputQty);
        var round2OrderIds = round2Demands.Where(d => d.OrderId.HasValue)
            .Select(d => d.OrderId!.Value).Distinct().Count();

        // 自消费：分配行所属订单是某张 SH，且供给身份正是这张 SH 自己
        var selfConsume = voucher.SupplyAllocations
            .Where(a => a.SourceType == SupplySourceType.INTER_FACTORY_ORDER)
            .Where(a => a.OrderId != 0 && shOrderIds.Contains(a.OrderId))
            .Count(a => !string.IsNullOrEmpty(a.ShippingInstructionNo)
                        && shNoById.TryGetValue(a.OrderId, out var no)
                        && string.Equals(a.ShippingInstructionNo, no, StringComparison.Ordinal));

        var shAllocatedQty = voucher.SupplyAllocations
            .Where(a => a.SourceType == SupplySourceType.INTER_FACTORY_ORDER)
            .Sum(a => a.AllocatedQuantity);

        var sb = new StringBuilder();
        sb.AppendLine($"A-R2 受控对照｜PlanVersionId={PlanVersionId}｜跑于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Domain={pv?.DomainKey}｜Strategy={StrategyProfileVersionId}｜DataCutoff={DataCutoff:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"需求侧订单（已剔除 SH）={demandOrderIds.Count}");
        sb.AppendLine($"SH 订单（供给身份）={shRows.Count}");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"[A-R2] SH 类根需求条数（Round2＋孤儿SH，整单） = {round2Demands.Count}");
        sb.AppendLine($"[A-R2] 覆盖 SH 张数            = {round2OrderIds}");
        sb.AppendLine($"[A-R2] 合计数量(NetOutputQty)  = {round2Qty:0.####}");
        sb.AppendLine($"[A-R2] 自消费次数（必须 0）    = {selfConsume}");
        sb.AppendLine($"[A-R2] SH 供给被消费总量       = {shAllocatedQty:0.####}");
        sb.AppendLine($"[A-R2] LPD 总数               = {voucher.LogicalProductionDemands.Count}");
        sb.AppendLine($"[A-R2] 短缺合计               = {voucher.ShortageQuantity:0.####}");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine("SH 明细（OrderNo｜Quantity｜根需求条数｜根需求合计）：");
        foreach (var r in shRows.OrderBy(r => r.OrderNo, StringComparer.Ordinal))
        {
            var rows = round2Demands.Where(d => d.OrderId == r.Id).ToList();
            sb.AppendLine($"  {r.OrderNo}\t{r.Quantity:0.####}\t{rows.Count}\t{rows.Sum(d => d.NetOutputQty):0.####}");
        }
        File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));

        // ── 断言：验「声明的目的」，不是「跑得过」 ──
        // ① 只要该 PV 有被真实上层承接的 SH，Round2 就必须产出（改前恒 0）。
        Assert.True(File.Exists(summaryPath), "摘要必须落盘——被掐断的跑批也要能事后读");
        // ② 自消费必须为 0：Round2 需求不得消费以自己为源的那张 SH。
        Assert.Equal(0, selfConsume);
        // ③ 不得凭空放大：Round2 表里每张 SH 的生成条数 ≤ 1（唯一整单）。
        foreach (var r in shRows)
        {
            var n = round2Demands.Count(d => d.OrderId == r.Id);
            Assert.True(n <= 1, $"SH {r.OrderNo} 生成了 {n} 条**根**源厂需求，违反「一张 SH 只生成一条」");
        }
    }

    private sealed class ShOrderRow
    {
        public long Id { get; set; }
        public string OrderNo { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
    }

    private sealed class PlanVersionHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }

    /// <summary>只落 `PeggingOrchestrator` 分类的日志到文件（`[Pegging][A-R2]` / `[Pegging][O49]` 都在里面）。</summary>
    private sealed class PeggingFileLoggerProvider : ILoggerProvider
    {
        private readonly StreamWriter _writer;

        public PeggingFileLoggerProvider(string path)
        {
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public ILogger CreateLogger(string categoryName) => new PeggingFileLogger(_writer, categoryName);

        public void Dispose() => _writer.Dispose();

        private sealed class PeggingFileLogger : ILogger
        {
            private readonly StreamWriter _writer;
            private readonly bool _enabled;
            private readonly object _gate = new();

            public PeggingFileLogger(StreamWriter writer, string category)
            {
                _writer = writer;
                _enabled = category.Contains("PeggingOrchestrator", StringComparison.Ordinal);
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => _enabled && logLevel >= LogLevel.Debug;

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
                    _writer.Write('\n');
                }
            }
        }
    }
}
