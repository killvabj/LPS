using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Models.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **CF-T18 输入一致性探针**（1号位，2026-11-13）。
///
/// <para><b>要回答的问题</b>：同一 PlanVersion 连续多轮，2号位交给 1号位 的
/// <c>DomainSolveRequest</c> 是否完全一致？不一致时差在哪几条需求、差在哪个字段？</para>
///
/// <para><b>为什么能只读</b>：走 <see cref="PeggingOrchestrator.RunPeggingOnlyAsync"/>，
/// 与生产共用同一段装载与 <c>BuildSolveRequestAsync</c>，**不调 Solver、默认不落库**
/// （<c>persistPositionSnapshot: false</c>）。返回值 <c>PeggingPayload.SolveRequest</c> 即
/// <c>DomainSolveRequest</c>，无需再跑完整 Run、无需捕获 1.4 GB JSON。</para>
///
/// <para><b>注意</b>：本探针只证「1号位收到的输入是否一致」。输入为何不一致（快照/排序/并发/
/// 分配顺序）属 2号位 辖区，不在本探针结论内。</para>
///
/// <para><b>默认不跑</b>：不设 <c>APS_CF18_PROBE_RUN=1</c> 直接返回。</para>
///
/// 环境变量：
///   APS_CF18_PROBE_RUN=1        启用
///   APS_CF18_PROBE_PV           PlanVersionId，默认 999487（FAMILY_CYL）
///   APS_CF18_PROBE_STRATEGY     策略包版本，默认 1970（SP-PROD-V1.0 / FULL_SCHEDULE 默认）
///   APS_CF18_PROBE_ROUNDS       轮数，默认 2
///   APS_CF18_PROBE_SNAPSHOT     PeggingExecutionRequest.SnapshotAt，默认=跑批时刻
///                               （同时是 PI 级 XC 24h 新鲜度闸基准，跨日会被拒）
///   APS_CF18_PROBE_CUTOFF       SchedulingContext.DataCutoffTime，默认=跑批时刻
///   APS_CF18_PROBE_TIMEOUT_MIN  单轮超时分钟，默认 40
///   APS_CF18_PROBE_OUT          输出目录，默认 D:/tmp
/// </summary>
public class CrossFactoryShInputDigestTest
{
    private const string GateEnv = "APS_CF18_PROBE_RUN";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(GateEnv), "1", StringComparison.Ordinal);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_PV"), out var v) && v > 0 ? v : 999487;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_STRATEGY"), out var v) && v > 0 ? v : 1970L;

    private static int Rounds =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_ROUNDS"), out var v) && v > 1 ? v : 2;

    private static DateTime SnapshotAt =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_SNAPSHOT"), out var v) ? v : DateTime.Now;

    private static DateTime DataCutoff =>
        DateTime.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_CUTOFF"), out var v) ? v : DateTime.Now;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_CF18_PROBE_TIMEOUT_MIN"), out var v) && v > 0 ? v : 40;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_CF18_PROBE_OUT") is { Length: > 0 } d ? d : @"D:/tmp";

    /// <summary>参与对比的 LPD 字段（顺序即 TSV 列序）。</summary>
    private static readonly string[] FieldNames =
    [
        "LogicalDemandKey", "NetOutputQty", "DemandSequence", "RequiredAvailableTime",
        "MaterialId", "FactoryId", "AllocationSequence", "RequiredStageCode", "StartStageCode"
    ];

    private static string[] Row(LogicalProductionDemand d) =>
    [
        d.LogicalDemandKey ?? string.Empty,
        // G29 去尾零：`3.0000m` 与 `3m` 必须等值，否则会造出大量假差异
        d.NetOutputQty.ToString("G29", CultureInfo.InvariantCulture),
        d.DemandSequence.ToString(CultureInfo.InvariantCulture),
        d.RequiredAvailableTime.ToString("O", CultureInfo.InvariantCulture),
        d.MaterialId.ToString(CultureInfo.InvariantCulture),
        d.FactoryId.ToString(CultureInfo.InvariantCulture),
        d.AllocationSequence.ToString(CultureInfo.InvariantCulture),
        d.RequiredStageCode ?? string.Empty,
        d.StartStageCode ?? string.Empty
    ];

    /// <summary>LPD 全部公开属性，按名称序固定（反射序不稳定，不能直接依赖）。</summary>
    private static readonly PropertyInfo[] LpdProps =
        typeof(LogicalProductionDemand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// **全字段**签名（覆盖 <see cref="LogicalProductionDemand"/> 全部公开属性）。
    /// 9 列 TSV 只用于人工看差异；一致性判定以本签名 + 全量哈希为准。
    /// </summary>
    private static string FullSignature(LogicalProductionDemand d)
    {
        var sb = new StringBuilder();
        foreach (var p in LpdProps)
            sb.Append(p.Name).Append('=').Append(FieldValue(d, p)).Append('\u0001');
        return sb.ToString();
    }

    /// <summary>单属性归一化取值：decimal 用 G29 去尾零，DateTime 用 O 往返格式。</summary>
    private static string FieldValue(LogicalProductionDemand d, PropertyInfo p)
    {
        object? v;
        try { v = p.GetValue(d); }
        catch { return "<ERR>"; }

        return v switch
        {
            null => string.Empty,
            decimal m => m.ToString("G29", CultureInfo.InvariantCulture),
            DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? string.Empty
        };
    }

    /// <summary>属性缓存（按运行时类型），避免 190 万行逐条反射取 PropertyInfo。</summary>
    private static readonly Dictionary<Type, PropertyInfo[]> PropCache = new();

    /// <summary>
    /// **整表内容聚合**（与顺序无关）：每元素按「全部公开属性 × 归一化值」算 FNV-1a 64 位，
    /// 再取**和**与**异或**两个可交换聚合量。
    /// 用途：除 LPD 外的 7 张表只比条目数是**不够的**（条目数相同、数量可以不同）。
    /// 选聚合而不是排序落盘：`OperationResourceEligibility` 190 万行，排序会吃掉数 GB。
    /// 任一元素不同 ⇒ 和/异或两值同时不变的概率可忽略。
    /// </summary>
    private static (ulong Sum, ulong Xor) TableAggregate(System.Collections.IEnumerable? items)
    {
        ulong sum = 0UL, xor = 0UL;
        if (items is null)
        {
            return (0UL, 0UL);
        }

        foreach (var it in items)
        {
            if (it is null)
            {
                continue;
            }

            var t = it.GetType();
            if (!PropCache.TryGetValue(t, out var props))
            {
                props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(p => p.Name, StringComparer.Ordinal)
                         .ToArray();
                PropCache[t] = props;
            }

            var sb = new StringBuilder();
            foreach (var p in props)
            {
                object? v;
                try { v = p.GetValue(it); }
                catch { v = "<ERR>"; }

                sb.Append(p.Name).Append('=').Append(v switch
                {
                    null => string.Empty,
                    decimal m => m.ToString("G29", CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString()
                }).Append('\u0001');
            }

            var h = Fnv1a64(sb.ToString());
            sum += h;
            xor ^= h;
        }

        return (sum, xor);
    }

    private static string Agg(System.Collections.IEnumerable? items)
    {
        var (s, x) = TableAggregate(items);
        return $"sum=0x{s:x16} xor=0x{x:x16}";
    }

    private static ulong Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var h = offset;
        foreach (var ch in s)
        {
            h ^= (byte)(ch & 0xFF);
            h *= prime;
            h ^= (byte)(ch >> 8);
            h *= prime;
        }
        return h;
    }

    private static string DigestStrings(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (var s in items.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(s).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string DigestRows(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows.OrderBy(x => string.Join("\u0001", x), StringComparer.Ordinal))
            sb.Append(string.Join("\u0001", r)).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact(DisplayName = "CF-T18 输入一致性：多轮 Pegging 的 DomainSolveRequest 摘要对比（不求解、不落库）")]
    public async Task DigestRounds()
    {
        if (!Enabled)
        {
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(OutDir);
        var summaryPath = Path.Combine(OutDir, $"CF18_digest_{PlanVersionId}_{stamp}_summary.txt");

        // ── DI：与 2号位 探针同源装配（不挂 Fixture，走生产路径）──
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

        using var sp = services.BuildServiceProvider();
        var conn = sp.GetRequiredService<DatabaseConnectionManager>();

        var pv = await conn.QueryFirstOrDefaultAsync<PlanVersionHorizon>(
            "SELECT DomainKey, PlanHorizonStart, PlanHorizonEnd FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS);

        var orderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        var lines = new List<string>
        {
            "CF-T18 输入一致性探针",
            $"PlanVersionId = {PlanVersionId}",
            $"DomainKey     = {pv?.DomainKey}",
            $"StrategyVer   = {StrategyProfileVersionId}",
            $"Order 行数    = {orderIds.Count}",
            $"Rounds        = {Rounds}",
            $"SnapshotAt    = {SnapshotAt:O}",
            $"DataCutoff    = {DataCutoff:O}",
            $"开始时刻      = {DateTime.Now:O}",
            string.Empty
        };

        var digests = new List<string>();
        var fullDigests = new List<string>();
        var sigSets = new List<List<string>>();
        var rowSets = new List<List<string[]>>();
        var tableCounts = new List<Dictionary<string, int>>();
        var tableAggs = new List<Dictionary<string, string>>();

        for (var round = 1; round <= Rounds; round++)
        {
            var now = SnapshotAt;
            var schedulingContext = new SchedulingContext
            {
                StrategyProfileVersionId = StrategyProfileVersionId,
                DataCutoffTime = DataCutoff,
                PlanHorizonStart = pv?.PlanHorizonStart ?? now,
                PlanHorizonEnd = pv?.PlanHorizonEnd ?? now.AddDays(90)
            };

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

            using var scope = sp.CreateScope();
            var schedulingOrchestrator = scope.ServiceProvider.GetRequiredService<SchedulingOrchestrator>();
            var peggingOrchestrator = (PeggingOrchestrator)scope.ServiceProvider.GetRequiredService<IPeggingOrchestrator>();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            lines.Add($"[轮 {round}] 开始 {DateTime.Now:HH:mm:ss}");

            await schedulingOrchestrator.LoadResourcesAndCalendarAsync(schedulingContext, cts.Token);

            // persistPositionSnapshot 默认 false ⇒ 不落库；不调 Solver。
            var payload = await peggingOrchestrator.RunPeggingOnlyAsync(request, cts.Token, scopeBomToOrders: false);
            var req = payload.SolveRequest;

            var rows = req.LogicalProductionDemands.Select(Row).ToList();
            var digest = DigestRows(rows);

            // 全字段签名（33 属性）—— 一致性判定以它为准，9 列 TSV 仅供人工定位
            var sigs = req.LogicalProductionDemands.Select(FullSignature).ToList();
            var fullDigest = DigestStrings(sigs);
            sigSets.Add(sigs);
            var counts = new Dictionary<string, int>
            {
                ["LogicalProductionDemands"] = rows.Count,
                ["MaterialConstraints"] = req.MaterialConstraints?.Count ?? 0,
                ["MaterialRequirementLinks"] = req.MaterialRequirementLinks?.Count ?? 0,
                ["RoutingOperations"] = req.RoutingOperations?.Count ?? 0,
                ["RoutingDependencies"] = req.RoutingDependencies?.Count ?? 0,
                ["OperationResourceEligibility"] = req.OperationResourceEligibility?.Count ?? 0,
                ["PiRemainingFacts"] = req.PiRemainingFacts?.Count ?? 0,
                ["StageLeadTimes"] = req.StageLeadTimes?.Count ?? 0
            };

            digests.Add(digest);
            fullDigests.Add(fullDigest);
            rowSets.Add(rows);
            tableCounts.Add(counts);

            // 除 LPD 外 7 张表的**整表内容**聚合（和 + 异或，与顺序无关）
            var aggs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaterialConstraints"] = Agg(req.MaterialConstraints),
                ["MaterialRequirementLinks"] = Agg(req.MaterialRequirementLinks),
                ["RoutingOperations"] = Agg(req.RoutingOperations),
                ["RoutingDependencies"] = Agg(req.RoutingDependencies),
                ["OperationResourceEligibility"] = Agg(req.OperationResourceEligibility),
                ["PiRemainingFacts"] = Agg(req.PiRemainingFacts),
                ["StageLeadTimes"] = Agg(req.StageLeadTimes)
            };
            tableAggs.Add(aggs);

            lines.Add($"[轮 {round}] 完成 {DateTime.Now:HH:mm:ss}  耗时 {sw.Elapsed.TotalSeconds:F1}s");
            lines.Add($"[轮 {round}] LPD={rows.Count}  sha256(9列)={digest}");
            lines.Add($"[轮 {round}] 全字段({LpdProps.Length}属性) sha256={fullDigest}");
            lines.AddRange(counts.Select(kv => $"[轮 {round}]   {kv.Key}={kv.Value}"));
            lines.AddRange(aggs.Select(kv => $"[轮 {round}]   内容哈希 {kv.Key}: {kv.Value}"));
            lines.Add(string.Empty);

            // 每轮落一份 TSV（中途被掐也能看）
            var tsvPath = Path.Combine(OutDir, $"CF18_digest_{PlanVersionId}_{stamp}_round{round}.tsv");
            using (var w = new StreamWriter(tsvPath, false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join('\t', FieldNames));
                foreach (var r in rows) w.WriteLine(string.Join('\t', r));
            }
            lines.Add($"[轮 {round}] TSV → {tsvPath}");

            // 全字段 TSV（33 列）：供离行核对 RouteCode / PathId 等红线字段
            var fullTsvPath = Path.Combine(OutDir, $"CF18_digest_{PlanVersionId}_{stamp}_round{round}_full.tsv");
            using (var w = new StreamWriter(fullTsvPath, false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join('\t', LpdProps.Select(p => p.Name)));
                foreach (var d in req.LogicalProductionDemands)
                    w.WriteLine(string.Join('\t', LpdProps.Select(p => FieldValue(d, p))));
            }
            lines.Add($"[轮 {round}] 全字段TSV → {fullTsvPath}");
            lines.Add(string.Empty);
        }

        // ── 对比 ──
        lines.Add("================ 对比 ================");
        for (var i = 1; i < Rounds; i++)
        {
            var same9 = string.Equals(digests[0], digests[i], StringComparison.Ordinal);
            var sameFull = string.Equals(fullDigests[0], fullDigests[i], StringComparison.Ordinal);
            lines.Add($"轮1 vs 轮{i + 1}: 9列={(same9 ? "一致" : "不同")}  全字段({LpdProps.Length}属性)={(sameFull ? "一致" : "不同")}");

            var (onlySigA, onlySigB) = DiffSigs(sigSets[0], sigSets[i]);
            lines.Add($"  全字段签名 A-only={onlySigA.Count}  B-only={onlySigB.Count}");
            foreach (var s in onlySigA.Take(10)) lines.Add($"  - {Trunc(s)}");
            foreach (var s in onlySigB.Take(10)) lines.Add($"  + {Trunc(s)}");

            // ⚠ 这里**不能** `continue`：下面还有表规模与 7 张表内容哈希的比对。
            //   2026-11-13 踩过：全字段一致时提前 continue，表级比对被整段跳过，
            //   汇总里看不到「7 张表内容是否一致」那一行（差点又只凭条目数下结论）。
            if (!sameFull)
            {
            var a = Count(rowSets[0]);
            var b = Count(rowSets[i]);
            var onlyA = a.Where(kv => !b.TryGetValue(kv.Key, out var n) || n < kv.Value)
                         .Select(kv => (Row: kv.Key, N: kv.Value - (b.TryGetValue(kv.Key, out var m) ? m : 0)));
            var onlyB = b.Where(kv => !a.TryGetValue(kv.Key, out var n) || n < kv.Value)
                         .Select(kv => (Row: kv.Key, N: kv.Value - (a.TryGetValue(kv.Key, out var m) ? m : 0)));

            foreach (var (row, n) in onlyA.Take(50))
                lines.Add($"  - x{n}  {Describe(row)}");
            foreach (var (row, n) in onlyB.Take(50))
                lines.Add($"  + x{n}  {Describe(row)}");
            }

            foreach (var key in tableCounts[0].Keys)
            {
                var v1 = tableCounts[0][key];
                var v2 = tableCounts[i][key];
                if (v1 != v2) lines.Add($"  表规模不同: {key}  轮1={v1}  轮{i + 1}={v2}");
            }

            // 7 张表**内容**是否逐条一致（只比条目数会漏掉「数量变了但条数不变」）
            var aggDiff = tableAggs[0]
                .Where(kv => !string.Equals(kv.Value, tableAggs[i][kv.Key], StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();

            if (aggDiff.Count == 0)
            {
                lines.Add($"  7 张表内容哈希：轮1 vs 轮{i + 1} **全部一致**");
            }
            else
            {
                foreach (var k in aggDiff)
                {
                    lines.Add($"  表内容不同: {k}");
                    lines.Add($"    轮1  {tableAggs[0][k]}");
                    lines.Add($"    轮{i + 1}  {tableAggs[i][k]}");
                }
            }
        }

        lines.Add(string.Empty);
        lines.Add($"结束时刻 = {DateTime.Now:O}");
        File.WriteAllLines(summaryPath, lines, new UTF8Encoding(false));

        // 让失败信息带着结论跑出去（不 throw：探针结论要看数据，不靠断言红绿）
        Assert.True(File.Exists(summaryPath), $"汇总未生成: {summaryPath}");
    }

    private static (List<string> OnlyA, List<string> OnlyB) DiffSigs(List<string> a, List<string> b)
    {
        var ca = new Dictionary<string, int>(StringComparer.Ordinal);
        var cb = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in a) ca[s] = ca.TryGetValue(s, out var n) ? n + 1 : 1;
        foreach (var s in b) cb[s] = cb.TryGetValue(s, out var n) ? n + 1 : 1;

        var oa = new List<string>();
        var ob = new List<string>();
        foreach (var kv in ca)
        {
            cb.TryGetValue(kv.Key, out var other);
            for (var k = 0; k < kv.Value - other; k++) oa.Add(kv.Key);
        }
        foreach (var kv in cb)
        {
            ca.TryGetValue(kv.Key, out var other);
            for (var k = 0; k < kv.Value - other; k++) ob.Add(kv.Key);
        }
        return (oa, ob);
    }

    private static string Trunc(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "…";

    private static Dictionary<string[], int> Count(List<string[]> rows)
    {
        var cmp = new RowComparer();
        var d = new Dictionary<string[], int>(cmp);
        foreach (var r in rows)
        {
            if (d.TryGetValue(r, out var n)) d[r] = n + 1;
            else d[r] = 1;
        }
        return d;
    }

    private static string Describe(string[] row) =>
        string.Join(", ", FieldNames.Zip(row, (k, v) => $"{k}={v}"));

    private sealed class RowComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y, StringComparer.Ordinal));

        public int GetHashCode(string[] obj)
        {
            var h = new HashCode();
            foreach (var s in obj) h.Add(s, StringComparer.Ordinal);
            return h.ToHashCode();
        }
    }

    /// <summary>`PlanVersion` 计划期投影（与 2号位 探针同款，Dapper 需可写属性）。</summary>
    private sealed class PlanVersionHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }
}