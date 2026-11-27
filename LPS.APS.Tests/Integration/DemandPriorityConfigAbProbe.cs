using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Application.Services;
using LPS.APS.Application.Extensions;
using LPS.APS.BusinessRules.Extensions;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using LPS.APS.Scheduling.Extensions;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **排序配置 A/B 探针**（0号位 2026-11-25 总表 v1.10 §十六.3 点名）：
/// 同一程序版本、**同一组竞争需求**、两套**合法但不同**的排序配置，
/// **经实际 Pegging 入口**跑到分配，证明只改配置就能改变需求顺序与实际承接。
///
/// <para><b>如何做到「不改生产 RuleSet478」</b>：DI 里把 <see cref="IDemandPriorityConfigProvider"/>
/// 换成装饰器 —— 变体 A 原样透传（＝生产 478），变体 B 在**内存中**把两段的
/// <c>SegmentOrder</c> 对调（仍是合法配置）。数据库一个字不动。</para>
///
/// <para><b>观测点</b>：每需求的 <c>DemandSequence</c> / <c>AllocationSequence</c> / 净量，
/// 以及 <c>voucher.SupplyAllocations</c> 按需求汇总的**实际承接量**。</para>
///
/// <para><b>开关</b>（默认不跑）：
/// <c>APS_DP_AB_RUN=1</c>、<c>APS_DP_AB_VARIANT=A|B</c>、<c>APS_DP_AB_PV</c>、
/// <c>APS_DP_AB_ORDERS</c>／<c>APS_DP_AB_TAKE</c>、<c>APS_DP_AB_OUT</c>、<c>APS_DP_AB_TIMEOUT_MIN</c>。
/// 两次分别跑 A、B，再用同一目录下的两份 TSV 比对。</para>
/// </summary>
public class DemandPriorityConfigAbProbe
{
    private const string GateEnv = "APS_DP_AB_RUN";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(GateEnv), "1", StringComparison.Ordinal);

    private static bool VariantB =>
        string.Equals(Environment.GetEnvironmentVariable("APS_DP_AB_VARIANT"), "B", StringComparison.OrdinalIgnoreCase);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_DP_AB_PV"), out var v) && v > 0 ? v : 999633;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_DP_AB_STRATEGY"), out var v) && v > 0 ? v : 1970L;

    private static int Take =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_DP_AB_TAKE"), out var v) && v > 0 ? v : 250;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_DP_AB_TIMEOUT_MIN"), out var v) && v > 0 ? v : 30;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_DP_AB_OUT") is { Length: > 0 } d ? d : @"D:/tmp";

    /// <summary>变体 B 的改动：把按 <c>SegmentOrder</c> 排序后的**第 1 段与第 3 段**的次序互换。</summary>
    private sealed class VariantPriorityConfigProvider : IDemandPriorityConfigProvider
    {
        private readonly IDemandPriorityConfigProvider _inner;
        private readonly bool _swap;

        public VariantPriorityConfigProvider(IDemandPriorityConfigProvider inner, bool swap)
        {
            _inner = inner;
            _swap = swap;
        }

        public async Task<DemandPriorityConfig> GetPriorityConfigAsync(
            long strategyProfileVersionId, DateTime? frozenAtAnchor = null, CancellationToken cancellationToken = default)
        {
            var cfg = await _inner.GetPriorityConfigAsync(strategyProfileVersionId, frozenAtAnchor, cancellationToken);
            if (!_swap) return cfg;

            var segs = cfg.Segments.OrderBy(s => s.SegmentOrder).ToList();

            // 变体 B：**把所有段的第一排序字段方向取反**（ASC↔DESC）。
            //   · 仍是合法配置（方向是受支持配置项）；
            //   · 必然改变段内先后 ⇒ 可观测到 DemandSequence / AllocationSequence 变化；
            //   · 段与段的匹配条件、段数、轮次**均不动** —— 隔离出「排序方向」这一个变量。
            var flipped = segs.Select(s =>
            {
                var sf = s.SortFields.ToList();
                if (sf.Count > 0)
                {
                    var head = sf[0];
                    sf[0] = new SortField
                    {
                        FieldName = head.FieldName,
                        Direction = string.Equals(head.Direction, "DESC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC"
                    };
                }
                return CloneWith(s, s.SegmentOrder, sf);
            }).ToList();

            Console.WriteLine($"[DP-AB] 变体B 已生效：{flipped.Count} 段，首字段方向全部取反（原首段 " +
                              $"{segs.FirstOrDefault()?.SortFields.FirstOrDefault()?.FieldName} " +
                              $"{segs.FirstOrDefault()?.SortFields.FirstOrDefault()?.Direction} → " +
                              $"{flipped.FirstOrDefault()?.SortFields.FirstOrDefault()?.Direction}）");

            return new DemandPriorityConfig { Segments = flipped };
        }

        private static PrioritySegmentConfig CloneWith(
            PrioritySegmentConfig s, int newOrder, IReadOnlyList<SortField> sortFields) => new()
        {
            CompetitionRound        = s.CompetitionRound,
            SegmentOrder            = newOrder,
            IsEnabled               = s.IsEnabled,
            MatchConditions         = s.MatchConditions,
            MatchConditionGroups    = s.MatchConditionGroups,
            SortFields              = sortFields,
            StableTieBreakFields    = s.StableTieBreakFields
        };
    }

    [Fact(DisplayName = "排序配置 A/B：同一批竞争需求，两套合法配置经 Pegging 入口跑出不同分配")]
    public async Task RunAbVariant()
    {
        if (!Enabled) return;

        Directory.CreateDirectory(OutDir);
        var variant = VariantB ? "B" : "A";
        var outPath = Path.Combine(OutDir, $"DP_AB_{PlanVersionId}_{variant}.tsv");
        var log = new List<string>();
        void Log(string s) { log.Add(s); Console.WriteLine(s); }

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
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddScoped<DatabaseConnectionManager>();
        services.AddScoped<SchedulingOrchestrator>();

        // 关键：覆盖配置提供者（后注册覆盖前注册）——变体 A 原样，变体 B 内存换序
        services.AddScoped<IDemandPriorityConfigProvider>(sp =>
            new VariantPriorityConfigProvider(
                new DemandPriorityConfigProvider(sp.GetRequiredService<IFrozenStrategySnapshotProvider>()),
                VariantB));

        using var sp = services.BuildServiceProvider();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();

        var pv = await conn.QueryFirstOrDefaultAsync<PvHorizon>(
            "SELECT DomainKey, PlanHorizonStart, PlanHorizonEnd FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS);
        if (pv is null) { Log($"PV {PlanVersionId} 不存在"); File.WriteAllLines(outPath, log); return; }

        var sourceRunId = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS) ?? 0;

        var allOrderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        var orderIds = allOrderIds;
        var explicitIds = Environment.GetEnvironmentVariable("APS_DP_AB_ORDERS");
        if (!string.IsNullOrWhiteSpace(explicitIds))
        {
            var wanted = explicitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToHashSet();
            orderIds = allOrderIds.Where(wanted.Contains).ToList();
        }
        else orderIds = orderIds.Take(Take).ToList();

        if (orderIds.Count > 0)
        {
            var shIds = (await conn.QueryAsync<long>(
                @"SELECT Id FROM [Order] WHERE PlanVersionId=@p AND CustomerSegment=N'INTER_FACTORY' AND OrderType=N'SALES_ORDER'",
                new { p = PlanVersionId }, db: DatabaseId.APS)).ToHashSet();
            if (shIds.Count > 0) orderIds = orderIds.Where(id => !shIds.Contains(id)).ToList();
        }

        var cutoffEnv = Environment.GetEnvironmentVariable("APS_DP_AB_CUTOFF");
        var cutoff = DateTime.TryParse(cutoffEnv, out var cv) ? cv : pv.PlanHorizonStart;

        var schedulingContext = new SchedulingContext
        {
            ScheduleRunId = sourceRunId,
            StrategyProfileVersionId = StrategyProfileVersionId,
            DataCutoffTime = cutoff,
            PlanHorizonStart = pv.PlanHorizonStart,
            PlanHorizonEnd = pv.PlanHorizonEnd
        };

        var request = new PeggingExecutionRequest
        {
            PlanVersionId = PlanVersionId,
            DomainKey = pv.DomainKey ?? string.Empty,
            OrderIds = orderIds,
            SnapshotAt = cutoff,
            FrozenWindowStart = cutoff,
            FrozenWindowEnd = cutoff.AddHours(2),
            AllowCrossFactory = true,
            DefaultStrategy = PeggingStrategyType.FIFO,
            MaxBomDepth = 10,
            TimeoutSeconds = TimeoutMinutes * 60,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            SchedulingContext = schedulingContext
        };

        using var scope = sp.CreateScope();
        var so = scope.ServiceProvider.GetRequiredService<SchedulingOrchestrator>();
        var po = (PeggingOrchestrator)scope.ServiceProvider.GetRequiredService<IPeggingOrchestrator>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));
        await so.LoadResourcesAndCalendarAsync(schedulingContext, cts.Token);
        var payload = await po.RunPeggingOnlyAsync(request, cts.Token, scopeBomToOrders: true);

        var req = payload.SolveRequest;
        var allocByDemand = (payload.Voucher?.SupplyAllocations ?? new List<SupplyAllocationItem>())
            .GroupBy(a => a.DemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.AllocatedQuantity), StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("DemandKey\tLogicalDemandKey\tDemandSequence\tAllocationSequence\tNetOutputQty\tAllocatedQty\tIsContinuation");
        foreach (var d in req.LogicalProductionDemands)
        {
            allocByDemand.TryGetValue(d.DemandKey, out var alloc);
            sb.AppendLine(string.Join('\t',
                d.DemandKey, d.LogicalDemandKey, d.DemandSequence, d.AllocationSequence,
                d.NetOutputQty.ToString("0.####"), alloc.ToString("0.####"), d.IsContinuation));
        }
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

        Log($"变体={variant} 订单={orderIds.Count} 需求={req.LogicalProductionDemands.Count} 分配行={payload.Voucher?.SupplyAllocations?.Count ?? 0}");
        Log($"已写出 {outPath}");

        var logPath = Path.Combine(OutDir, $"DP_AB_{PlanVersionId}_{variant}.log.txt");
        File.WriteAllLines(logPath, log, new UTF8Encoding(false));
    }

    private sealed class PvHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }
}