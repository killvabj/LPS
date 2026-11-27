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
/// **A31③ 逐Demand Stage链 DTO 样本探针**（2号位，2026-11-25）。
///
/// <para><b>目的</b>：0号位 2026-11-25 要求「先提供一版实际可消费 DTO ＋ ROOT／EDGE／同物料不同合法链的最小样本，
/// 让 1号位 能够开始修改 Solver 消费代码」。本探针用<b>真实 PV 的真实订单</b>跑一次**只装载、不求解、不落库**，
/// 把 <see cref="DomainSolveRequest"/> 里三样新增/相关的载荷导出成 JSON，供 1号位 直接对着写消费代码。</para>
///
/// <para><b>为什么只装载就够</b>：<see cref="DomainSolveRequest"/> 是 Solver 的**输入**；
/// 生成它全在装载侧（<c>PeggingOrchestrator</c>）。1号位 拿到输入即可开始改消费代码，不必等求解结果。</para>
///
/// <para><b>只读性</b>：走 <c>RunPeggingOnlyAsync</c>，不调 Solver、不落库
/// （<c>persistPositionSnapshot=false</c> ＋ <c>writeAuditLog=false</c>）。
/// ⚠️ 2026-11-26 前该入口仍会写 <c>APS_ETL_Log</c>（<c>SnapshotBatchIdentity</c> 留痕，幂等）；
/// 0号位 v1.12 §十八 要求诊断路径零写库后，已加 <c>writeAuditLog</c> 开关，本探针显式关闭。</para>
///
/// <para><b>开关</b>（默认不跑，避免误伤常规测试）：</para>
/// <list type="bullet">
/// <item><c>APS_A31_PROBE_RUN=1</c> 必须显式设置</item>
/// <item><c>APS_A31_PROBE_PV</c> 计划版本 Id（默认 999634 / FAMILY_FRL）</item>
/// <item><c>APS_A31_PROBE_ORDERS</c> 指定 OrderId（逗号分隔）；不设则取前 N 条</item>
/// <item><c>APS_A31_PROBE_TAKE</c> 取前 N 条订单（默认 40）</item>
/// <item><c>APS_A31_PROBE_STRATEGY</c> StrategyProfileVersionId（默认 1970）</item>
/// <item><c>APS_A31_PROBE_OUT</c> 输出目录（默认 <c>D:/tmp</c>）</item>
/// <item><c>APS_A31_PROBE_TIMEOUT_MIN</c> 超时分钟（默认 30）</item>
/// </list>
/// </summary>
public class A31StageChainDtoSampleProbe
{
    private const string GateEnv = "APS_A31_PROBE_RUN";

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(GateEnv), "1", StringComparison.Ordinal);

    private static int PlanVersionId =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_A31_PROBE_PV"), out var v) && v > 0 ? v : 999634;

    private static long StrategyProfileVersionId =>
        long.TryParse(Environment.GetEnvironmentVariable("APS_A31_PROBE_STRATEGY"), out var v) && v > 0 ? v : 1970L;

    private static int Take =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_A31_PROBE_TAKE"), out var v) && v > 0 ? v : 40;

    private static int TimeoutMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("APS_A31_PROBE_TIMEOUT_MIN"), out var v) && v > 0 ? v : 30;

    private static string OutDir =>
        Environment.GetEnvironmentVariable("APS_A31_PROBE_OUT") is { Length: > 0 } d ? d : @"D:/tmp";

    /// <summary>
    /// 是否放开跨厂。`IsRootDemand = (bomLevel == 0)`（`PeggingOrchestrator:18395`），
    /// ⇒ **只有跨厂/子件成为独立需求时才会出现 EDGE 绑定**。默认关，与生产常跑口径一致。
    /// </summary>
    private static bool AllowCrossFactory =>
        string.Equals(Environment.GetEnvironmentVariable("APS_A31_PROBE_CROSS"), "1", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact(DisplayName = "A31③ 逐Demand链 DTO 样本：只装载不求解，导出 ROOT/EDGE/同物料多链")]
    public async Task DumpStageChainDtoSample()
    {
        if (!Enabled) return;

        Directory.CreateDirectory(OutDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outPath = Path.Combine(OutDir, $"A31_chain_dto_sample_{PlanVersionId}_{stamp}.json");

        var lines = new List<string>();
        void Log(string s) { lines.Add(s); Console.WriteLine(s); }

        Log($"=== A31③ DTO 样本探针 ===");
        Log($"PV={PlanVersionId} Strategy={StrategyProfileVersionId} 开始={DateTime.Now:O}");

        // ── DI：与其他探针同源装配（不挂 Fixture，走生产路径）──
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
        if (pv is null) { Log($"PV {PlanVersionId} 不存在，退出"); Write(outPath, lines, null); return; }

        var sourceRunId = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id=@p",
            new { p = PlanVersionId }, db: DatabaseId.APS) ?? 0;

        var allOrderIds = (await conn.QueryAsync<long>(
            "SELECT Id FROM [Order] WHERE PlanVersionId=@p ORDER BY Id",
            new { p = PlanVersionId }, db: DatabaseId.APS)).ToList();

        var orderIds = allOrderIds;
        var explicitIds = Environment.GetEnvironmentVariable("APS_A31_PROBE_ORDERS");
        if (!string.IsNullOrWhiteSpace(explicitIds))
        {
            var wanted = explicitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToHashSet();
            orderIds = allOrderIds.Where(wanted.Contains).ToList();
        }
        else
        {
            orderIds = orderIds.Take(Take).ToList();
        }

        // 与生产一致：SH 单不当普通 Demand（否则同一张 SH 会被算两遍）
        if (orderIds.Count > 0)
        {
            var shIds = (await conn.QueryAsync<long>(
                @"SELECT Id FROM [Order]
                  WHERE PlanVersionId=@p AND CustomerSegment=N'INTER_FACTORY' AND OrderType=N'SALES_ORDER'",
                new { p = PlanVersionId }, db: DatabaseId.APS)).ToHashSet();
            if (shIds.Count > 0) orderIds = orderIds.Where(id => !shIds.Contains(id)).ToList();
        }

        Log($"PV.DomainKey={pv.DomainKey} SourceRun={sourceRunId} 订单总数={allOrderIds.Count} 本次={orderIds.Count}");
        if (orderIds.Count == 0) { Log("无订单可跑，退出"); Write(outPath, lines, null); return; }

        // ⚠ 数据截止时刻**必须落回该 PV 自己的时间窗**，不能用 DateTime.Now。
        //   实测 2026-11-25：用 now 会把 cutoff 推到 11-25，而 PI 级 XC 快照最近刷新是 11-21 15:53，
        //   守卫「>24h 即拒绝本轮」直接抛 `[Pegging][PI-XC] PI 级 XC 快照过期` ⇒ 装载跑不完。
        //   这不是缺陷，是「过期切片不得混入本 Run」的正确拦截；探针应模拟该 PV 当时的口径。
        var cutoffEnv = Environment.GetEnvironmentVariable("APS_A31_PROBE_CUTOFF");
        var cutoff = DateTime.TryParse(cutoffEnv, out var cv) ? cv : pv.PlanHorizonStart;
        var snapshotAt = cutoff;
        Log($"数据截止 = {cutoff:O}（来源：{(string.IsNullOrWhiteSpace(cutoffEnv) ? "PlanHorizonStart" : "APS_A31_PROBE_CUTOFF")}）");

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
            SnapshotAt = snapshotAt,
            FrozenWindowStart = snapshotAt,
            FrozenWindowEnd = snapshotAt.AddHours(2),
            AllowCrossFactory = AllowCrossFactory,
            DefaultStrategy = PeggingStrategyType.FIFO,
            MaxBomDepth = 10,
            TimeoutSeconds = TimeoutMinutes * 60,
            ExecutionMode = "FULL_RUN",
            IsCandidate = false,
            BasePlanVersionId = null,
            SchedulingContext = schedulingContext
        };

        using var scope = sp.CreateScope();
        var schedulingOrchestrator = scope.ServiceProvider.GetRequiredService<SchedulingOrchestrator>();
        var peggingOrchestrator = (PeggingOrchestrator)scope.ServiceProvider.GetRequiredService<IPeggingOrchestrator>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));
        await schedulingOrchestrator.LoadResourcesAndCalendarAsync(schedulingContext, cts.Token);

        var payload = await peggingOrchestrator.RunPeggingOnlyAsync(
            request, cts.Token, scopeBomToOrders: true,
            writeAuditLog: false);   // 2026-11-26：诊断路径零写库（含 APS_ETL_Log）
        var req = payload.SolveRequest;

        var chains = req.StageSequenceChains ?? Array.Empty<StageSequenceChain>();
        var defs = req.StageChainDefinitions ?? Array.Empty<StageChainDefinition>();
        var binds = req.DemandStageChainBindings ?? Array.Empty<DemandStageChainBinding>();

        Log(string.Empty);
        Log($"StageSequenceChains       = {chains.Count}（物料级，兼容口径）");
        Log($"StageChainDefinitions     = {defs.Count}");
        Log($"DemandStageChainBindings  = {binds.Count}");

        var byStatus = binds.GroupBy(b => b.Status, StringComparer.Ordinal)
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .Select(g => $"{g.Key}={g.Count()}");
        Log($"绑定按状态：{string.Join("  ", byStatus)}");

        var byScope = defs.GroupBy(d => d.StageScopeType, StringComparer.Ordinal)
                          .Select(g => $"{g.Key}={g.Count()}");
        Log($"链目录按 scope：{string.Join("  ", byScope)}");

        var byMatch = defs.SelectMany(d => d.Stages)
                          .GroupBy(s => string.IsNullOrEmpty(s.FactoryMatchStatus) ? "(未投影)" : s.FactoryMatchStatus,
                                   StringComparer.Ordinal)
                          .Select(g => $"{g.Key}={g.Count()}");
        Log($"链目录按厂匹配状态：{string.Join("  ", byMatch)}");

        // ── 挑样本 ────────────────────────────────────────────────────
        var ro = binds.FirstOrDefault(b => b.StageScopeType == EffectiveStageChainResolver.ScopeRoot
                                        && b.ChainRefId is not null);
        var ed = binds.FirstOrDefault(b => b.StageScopeType == EffectiveStageChainResolver.ScopeEdge
                                        && b.ChainRefId is not null);
        var cf = binds.FirstOrDefault(b => b.Status == StageChainBindingStatus.Conflict);

        // ⚠ 2026-11-25 修正：先前按 `LogicalDemandKey` 分组找「同物料多链」是**错的**——
        //   一条需求只会有一条绑定，永远找不到。正确判据是 **按 MaterialId 分组、看绑到的不同 ChainRefId 个数**，
        //   而 MaterialId 要从 `LogicalProductionDemands` 反查（绑定本身不带 MaterialId）。
        var matByDemand = req.LogicalProductionDemands
            .GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().MaterialId, StringComparer.Ordinal);

        var withChain = binds
            .Where(b => b.ChainRefId is not null && matByDemand.ContainsKey(b.LogicalDemandKey))
            .Select(b => new
            {
                b.LogicalDemandKey,
                MaterialId = matByDemand[b.LogicalDemandKey],
                ChainRefId = b.ChainRefId!,
                b.StageScopeType,
                b.Status
            })
            .ToList();

        var multiChainMats = withChain
            .GroupBy(x => x.MaterialId)
            .Where(g => g.Select(x => x.ChainRefId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => new
            {
                MaterialId = g.Key,
                ChainRefIds = g.Select(x => x.ChainRefId).Distinct(StringComparer.Ordinal)
                               .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                DemandKeys = g.Select(x => x.LogicalDemandKey).Distinct(StringComparer.Ordinal)
                              .OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Scopes = g.Select(x => x.StageScopeType).Distinct(StringComparer.Ordinal)
                          .OrderBy(x => x, StringComparer.Ordinal).ToList()
            })
            .OrderBy(x => x.MaterialId)
            .ToList();

        // 关键对照：这些物料在**旧载体** `StageSequenceChains` 里还有没有（没有＝仍被隔离）
        var chainMatIds = chains.Select(c => c.MaterialId).ToHashSet();
        var isolatedButBound = multiChainMats.Where(m => !chainMatIds.Contains(m.MaterialId)).ToList();
        var boundMatIds = withChain.Select(x => x.MaterialId).Distinct().ToHashSet();

        Log($"有链物料数（绑定侧）={boundMatIds.Count}；旧载体覆盖={chains.Count}");
        Log($"同物料多链物料数={multiChainMats.Count}，其中**仍被隔离**（旧载体里没有）={isolatedButBound.Count}");
        Log($"EDGE 绑定数={binds.Count(b => b.StageScopeType == EffectiveStageChainResolver.ScopeEdge)}");

        var defByRef = defs.ToDictionary(d => d.ChainRefId, StringComparer.Ordinal);

        // ── 0号位 2026-11-25 核证一：EDGE 父子边能否靠**现有载体**关联 ──────────
        //   若 `MaterialRequirementLinks` 已能给出「父子需求＋份额＋时间差」，
        //   则 binding 上的 `ParentDemandKey` 属冗余，不必新增字段。
        var links = req.MaterialRequirementLinks ?? Array.Empty<MaterialRequirementLink>();

        // ⚠ 命名空间：`ChildDemandKey` 是 `DemandKey`（ORDER_{OrderId}_{MaterialCode}_{FactoryId}），
        //   **不是** `LogicalDemandKey`；真正的父子键是 `Consumer/ProducerLogicalDemandKey`。
        //   用错的键比对会得到「0 匹配」这种假结论（2026-11-25 首次即踩此坑）。
        var linkByProducer = new Dictionary<string, List<MaterialRequirementLink>>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            if (string.IsNullOrEmpty(l.ProducerLogicalDemandKey)) continue;
            if (!linkByProducer.TryGetValue(l.ProducerLogicalDemandKey!, out var list))
                linkByProducer[l.ProducerLogicalDemandKey!] = list = new List<MaterialRequirementLink>(1);
            list.Add(l);
        }

        var edgeBinds = binds.Where(b => b.StageScopeType == EffectiveStageChainResolver.ScopeEdge).ToList();
        var edgeWithLink = edgeBinds.Count(b => linkByProducer.ContainsKey(b.LogicalDemandKey));
        var edgeSample = edgeBinds.FirstOrDefault(b => linkByProducer.ContainsKey(b.LogicalDemandKey));

        Log($"需求链接 MaterialRequirementLinks = {links.Count}（含生产方 {links.Count(l => !string.IsNullOrEmpty(l.ProducerLogicalDemandKey))}）");
        Log($"EDGE 绑定 = {edgeBinds.Count}；能按 ProducerLogicalDemandKey 找到父边 = {edgeWithLink}");

        // ── 0号位 2026-11-25 核证二：三级日历「工厂级」这一层够不够 ────────────────
        //   投影口径＝厂级 scope = 部门所属工厂 ∪ 需求工厂。⇒ 凡有部门的阶段，其厂一定有日历；
        //   只有「无部门」的阶段，其 Stage 厂可能不在已投影集合里 ⇒ 第三级继承落空。
        var windows = req.DepartmentWorkWindows ?? Array.Empty<DepartmentWorkWindow>();
        var projectedFactoryIds = windows.Select(w => w.FactoryId).Where(f => f > 0).Distinct().ToHashSet();
        var deptScopeCount = windows.Where(w => w.DepartmentId.HasValue).Select(w => w.DepartmentId!.Value).Distinct().Count();
        var factoryScopeCount = projectedFactoryIds.Count;
        // ⚠ 必须取**物料级** `StageSequenceChains`：链目录（Definitions）是链级、按 r14417 口径
        //   一律记 `NotProjected`（不判定部门），从那里数 `DepartmentUnresolved` 恒为 0。
        var unresolvedSteps = chains.SelectMany(c => c.Stages)
            .Where(s => s.FactoryMatchStatus == StageFactoryMatchStatus.DepartmentUnresolved)
            .ToList();
        var unresolvedMissingFactory = unresolvedSteps
            .Count(s => s.StageFactoryId is int fid && !projectedFactoryIds.Contains(fid));
        var unresolvedFactoryCodes = unresolvedSteps
            .Where(s => s.StageFactoryId is int fid && !projectedFactoryIds.Contains(fid))
            .Select(s => (s.StageFactoryCode ?? "?") + ":" + s.StageCode)
            .Distinct(StringComparer.Ordinal).Take(10).ToList();

        // 2026-11-25 日历 scope 止血的效果度量：链上 Stage 执行厂里，有多少**不在需求厂集合**内
        //   （这些就是修复前会被漏装厂级窗口的厂）。
        var demandFacIds = (req.LogicalProductionDemands ?? Array.Empty<LogicalProductionDemand>())
            .Select(d => d.FactoryId).Where(f => f > 0).Distinct().ToHashSet();
        var stageFacIds = defs.SelectMany(d => d.Stages)
            .Select(s => s.StageFactoryId).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToList();
        var extraFactoryIds = stageFacIds.Where(f => !demandFacIds.Contains(f)).OrderBy(x => x).ToList();

        // 判别：这些厂是不是**只由 Stage 执行厂引入**（而非「部门所属工厂」或「需求工厂」带进来的）。
        //   只有「只由 Stage 厂引入」才证明旧 scope 真会漏装。
        var deptIntroducedFactoryIds = windows
            .Where(w => w.DepartmentId.HasValue)
            .Select(w => w.FactoryId).Distinct().ToHashSet();
        var onlyByStage = extraFactoryIds
            .Where(f => !deptIntroducedFactoryIds.Contains(f)).OrderBy(x => x).ToList();

        Log($"WORK窗口 {windows.Count} 片：厂级 scope {factoryScopeCount} 个 / 部门 scope {deptScopeCount} 个");
        Log($"需求厂 {demandFacIds.Count} 个；链上 Stage 执行厂 {stageFacIds.Count} 个；**其中不在需求厂集合的 {extraFactoryIds.Count} 个**（={string.Join(",", extraFactoryIds)}）");
        Log($"无部门步骤 {unresolvedSteps.Count} 条，其中**所属厂不在已投影集合** = {unresolvedMissingFactory}" +
            (unresolvedFactoryCodes.Count > 0 ? $"（样例：{string.Join(",", unresolvedFactoryCodes)}）" : ""));

        // ⚠ 自查用：核「需求总数 / 根需求 / 子件需求」「绑定是否覆盖全部需求」
        //   实测 2026-11-25 出现过 NO_CHAIN 在不同订单规模下几乎不变（2284/2284/2281），
        //   必须先确认绑定与需求是一一对应，否则对外报数会失真。
        var allDemands = req.LogicalProductionDemands ?? Array.Empty<LogicalProductionDemand>();
        var demandKeys = allDemands.Select(d => d.LogicalDemandKey)
                                   .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var bindingKeys = binds.Select(b => b.LogicalDemandKey)
                               .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var rootDemands = allDemands.Count(d => d.IsRootDemand);
        var nonRootDemands = allDemands.Count - rootDemands;
        var demandScopeByKey = allDemands.GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().IsRootDemand, StringComparer.Ordinal);
        var scopeMismatch = binds.Count(b =>
            demandScopeByKey.TryGetValue(b.LogicalDemandKey, out var isRoot)
            && (isRoot ? b.StageScopeType != EffectiveStageChainResolver.ScopeRoot
                       : b.StageScopeType != EffectiveStageChainResolver.ScopeEdge));

        Log($"需求总数={allDemands.Count}（根 {rootDemands} / 非根 {nonRootDemands}）；需求键={demandKeys.Count}；绑定键={bindingKeys.Count}");
        Log($"绑定与需求键一致={(demandKeys.SetEquals(bindingKeys) ? "是" : "否")}；绑定 scope 与需求角色不一致数={scopeMismatch}");

        var sample = new
        {
            生成时刻 = DateTime.Now.ToString("O"),
            PlanVersionId,
            DomainKey = pv.DomainKey,
            SourceScheduleRunId = sourceRunId,
            订单数 = orderIds.Count,
            数据截止 = cutoff.ToString("O"),
            AllowCrossFactory = AllowCrossFactory,
            统计 = new
            {
                StageSequenceChains = chains.Count,
                StageChainDefinitions = defs.Count,
                DemandStageChainBindings = binds.Count,
                绑定按scope = binds.GroupBy(b => b.StageScopeType).ToDictionary(g => g.Key, g => g.Count()),
                按状态 = binds.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count()),
                链目录按scope = defs.GroupBy(d => d.StageScopeType).ToDictionary(g => g.Key, g => g.Count()),
                按厂匹配 = defs.SelectMany(d => d.Stages)
                    .GroupBy(s => string.IsNullOrEmpty(s.FactoryMatchStatus) ? "(未投影)" : s.FactoryMatchStatus)
                    .ToDictionary(g => g.Key, g => g.Count()),
                同物料多链物料数 = multiChainMats.Count,
                其中仍被旧载体隔离 = isolatedButBound.Count,
                需求总数 = allDemands.Count,
                根需求数 = rootDemands,
                非根需求数 = nonRootDemands,
                需求键数 = demandKeys.Count,
                绑定键数 = bindingKeys.Count,
                绑定与需求键一致 = demandKeys.SetEquals(bindingKeys),
                绑定scope与需求角色不一致数 = scopeMismatch
            },
            样本_ROOT绑定 = ro,
            样本_EDGE绑定 = ed,
            样本_冲突绑定 = cf,
            样本_同物料多链 = isolatedButBound.FirstOrDefault() ?? multiChainMats.FirstOrDefault(),
            全部_同物料多链物料 = multiChainMats.Take(20).ToList(),
            需求链接 = new
            {
                总数 = links.Count,
                含生产方 = links.Count(l => !string.IsNullOrEmpty(l.ProducerLogicalDemandKey)),
                EDGE绑定数 = edgeBinds.Count,
                EDGE绑定能找到父边 = edgeWithLink,
                样本_EDGE绑定 = edgeSample,
                样本_该EDGE的父边链接 = edgeSample is null
                    ? null
                    : linkByProducer.GetValueOrDefault(edgeSample.LogicalDemandKey)
            },
            三级日历 = new
            {
                WORK窗口片数 = windows.Count,
                厂级scope数 = factoryScopeCount,
                部门scope数 = deptScopeCount,
                无部门步骤数 = unresolvedSteps.Count,
                其中所属厂未投影 = unresolvedMissingFactory,
                样例 = unresolvedFactoryCodes,
                需求厂数 = demandFacIds.Count,
                链上Stage执行厂数 = stageFacIds.Count,
                不在需求厂集合的执行厂 = extraFactoryIds,
                其中仅由Stage厂引入 = onlyByStage
            },
            样本_ROOT对应链 = ro is null ? null : defByRef.GetValueOrDefault(ro.ChainRefId!),
            样本_EDGE对应链 = ed is null ? null : defByRef.GetValueOrDefault(ed.ChainRefId!),
            // 1号位 2026-11-25 §五(a)：离线重放 `DemandChainResolver` 所需的最小四段。
            //   完整 `DomainSolveRequest` 实测 651 MB（600 单 659 MB），不适合传递 ⇒ 只给这四段，
            //   其余属性反序列化后为默认值，不影响「按绑定取链 / 无绑定回落」这条判定。
            全量_LogicalProductionDemands = req.LogicalProductionDemands,
            全量_StageSequenceChains = chains,
            全量_DemandStageChainBindings = binds,
            全量_StageChainDefinitions = defs,
            // 2026-11-27（A31 多父边取证）：父边身份存在 `MaterialRequirementLinks`（Consumer↔Producer），
            //   份额与供给来源存在 `Voucher.SupplyAllocations`。两者此前都未导出，导致"两条真实父边
            //   各承接 100/200"无法在事后核对。此处一并导出（只读，不落库）。
            全量_MaterialRequirementLinks = req.MaterialRequirementLinks,
            全量_SupplyAllocations = payload.Voucher.SupplyAllocations
        };

        Log(string.Empty);
        Log($"ROOT 样本：绑定 {(ro is null ? "(无)" : ro.LogicalDemandKey)}");
        Log($"EDGE 样本：绑定 {(ed is null ? "(无)" : ed.LogicalDemandKey)}");
        Log($"CONFLICT 样本：{(cf is null ? "(本次无冲突)" : cf.LogicalDemandKey)}");
        Log($"同物料多链样本：{(multiChainMats.Count == 0 ? "(本次无)" : $"物料 {multiChainMats[0].MaterialId} / {multiChainMats[0].ChainRefIds.Count} 条链")}");

        Write(outPath, lines, sample);
        Log($"已写出：{outPath}");

        // ── 整包 `DomainSolveRequest` 导出（1号位 2026-11-25 回执 §五(a) 点名要的离线重放输入）──
        //   其 11-23 捕获里 `StageChainDefinitions`/`DemandStageChainBindings` 均为 0，
        //   离线重放只会走 `LegacyMaterial` 分支、验不到新链路 ⇒ 由我方产出含新字段的捕获。
        if (string.Equals(Environment.GetEnvironmentVariable("APS_A31_PROBE_DUMP_DTO"), "1", StringComparison.Ordinal))
        {
            var dtoPath = Path.Combine(OutDir, $"A31_solve_request_{PlanVersionId}_{stamp}.json");
            var compact = new JsonSerializerOptions { WriteIndented = false };
            File.WriteAllText(dtoPath, JsonSerializer.Serialize(req, compact), new UTF8Encoding(false));
            Log($"整包 DTO 已写出：{dtoPath}（{new FileInfo(dtoPath).Length / 1024} KB）");
            Console.WriteLine($"整包 DTO 已写出：{dtoPath}");
        }
    }

    private static void Write(string path, List<string> lines, object? payload)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        if (payload is not null)
        {
            sb.AppendLine();
            sb.AppendLine("=== JSON ===");
            sb.AppendLine(JsonSerializer.Serialize(payload, JsonOpts));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private sealed class PlanVersionHorizon
    {
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
    }
}