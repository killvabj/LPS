using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// A6 正交性能对照（0号位 2026-11-10《Batch_Code_Review_and_Action_Plan v1.0》§4-A6 / §5.1）：
/// **同一份输入、只切两个实验开关**，把「单 Op 无 PI 快路 + 来源 PI 守卫」的独立增量从
/// 「整组规则调整的总收益」里剥出来。
///
/// <para><b>为什么要另建</b>：`SolverReplayTest` 只跑单次重放，没有前缀限流、也不切实验开关；
///   而 `StageBatchMergeTests` 那些是夹具级，测不出吞吐。</para>
///
/// <para><b>档位</b>：</para>
/// <list type="bullet">
///   <item><b>S1</b> = `PI_ONLY` + `APS_P2_SINGLEOP_FASTPATH=0` + `APS_P2_SOURCE_PI_GUARD=0`
///         ⇒ 只有「旧的多 Op 同 PI 过滤」。</item>
///   <item><b>S2</b> = `PI_ONLY` + 两个开关都 =1 ⇒ 本次报告状态。</item>
/// </list>
/// <para>**S1→S2 才是本轮新改动的增量**；S0（LEGACY）→S2 是整组规则的总收益。</para>
///
/// <para><b>⚠ 保真度边界（与 `SolverReplayTest` 同）</b>：快照用 `ReferenceHandler.IgnoreCycles` 捕获，
///   环引用被写成 null ⇒ **只可用于吞吐对照与热点归因**，不得据此断言业务结果等价。</para>
///
/// <para><b>开门</b>：未设 `APS_REPLAY_SOLVE_REQUEST` ⇒ **跳过**（仓库里不硬编码本机路径）。</para>
/// </summary>
public class PhaseTwoOrthoReplayPerfTests
{
    private const string ReplayPathEnvVar = "APS_REPLAY_SOLVE_REQUEST";
    private const string LimitEnvVar = "APS_ORTHO_DEMAND_LIMIT";
    private const int DefaultLimit = 20000;

    private readonly ITestOutputHelper _output;

    public PhaseTwoOrthoReplayPerfTests(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "A6 正交对照：S1 vs S2（同一输入，只切单Op快路 + 来源PI守卫）")]
    public async Task S1_vs_S2_SameInput()
    {
        var jsonPath = Environment.GetEnvironmentVariable(ReplayPathEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath),
            $"未指定已存在的入参 JSON（{ReplayPathEnvVar}='{jsonPath ?? "(null)"}'）。");

        var limit = int.TryParse(Environment.GetEnvironmentVariable(LimitEnvVar), out var n) && n > 0
            ? n
            : DefaultLimit;

        // 全量 179k 单档约 70+ 分钟，两档合跑超过单次工具超时 ⇒ 支持分档单跑。
        // `APS_ORTHO_RUN=S1|S2|BOTH`（缺省 BOTH）。
        var which = (Environment.GetEnvironmentVariable("APS_ORTHO_RUN") ?? "BOTH").ToUpperInvariant();

        _output.WriteLine($"[A6] 入参：{jsonPath}（{new FileInfo(jsonPath!).Length / 1048576d:F0} MB）");
        _output.WriteLine($"[A6] 需求前缀上限：{limit}（{LimitEnvVar}）；档位={which}");

        var s1 = which is "S1" or "BOTH"
            ? await RunAsync(jsonPath!, limit, fastPath: "0", guard: "0", label: "S1")
            : null;
        var s2 = which is "S2" or "BOTH"
            ? await RunAsync(jsonPath!, limit, fastPath: "1", guard: "1", label: "S2")
            : null;

        if (s1 is null || s2 is null)
        {
            var only = s1 ?? s2!;
            _output.WriteLine($"[A6] 单档完成：{only.Label} 墙钟 {only.WallSeconds:F1}s，"
                              + $"Success={only.Success}，FinalTask={only.FinalTasks}，"
                              + $"P2={only.Phase2Ms}ms，单Op扫描 {only.ScanCalls}/{only.ScannedTasks}，候选 {only.Candidates}，"
                              + $"快路跳过 {only.SkippedNoPi}");
            Assert.True(only.Success, only.Error);
            return;
        }

        _output.WriteLine("");
        _output.WriteLine("── A6 正交对照 ─────────────────────────────────────────────");
        _output.WriteLine($"  S1（快路关/守卫关）：墙钟 {s1.WallSeconds,8:F1}s  P2 {s1.Phase2Ms,8}ms  "
                          + $"单Op扫描 {s1.ScanCalls,6} 次 / {s1.ScannedTasks,10} 元素  候选 {s1.Candidates,6}  "
                          + $"快路跳过 {s1.SkippedNoPi,6}  Merge {s1.MergeAttempts,6}");
        _output.WriteLine($"  S2（快路开/守卫开）：墙钟 {s2.WallSeconds,8:F1}s  P2 {s2.Phase2Ms,8}ms  "
                          + $"单Op扫描 {s2.ScanCalls,6} 次 / {s2.ScannedTasks,10} 元素  候选 {s2.Candidates,6}  "
                          + $"快路跳过 {s2.SkippedNoPi,6}  Merge {s2.MergeAttempts,6}");

        if (s2.WallSeconds > 0)
        {
            _output.WriteLine($"  ⇒ S1/S2 墙钟比 = {s1.WallSeconds / s2.WallSeconds:F2}× "
                              + "（S1→S2 的独立增量；**不是** S0→S2 的总收益）");
        }

        _output.WriteLine($"  FinalTask：S1={s1.FinalTasks}  S2={s2.FinalTasks}"
                          + "  （⚠ 两个数不同**不代表**业务等价性结论 —— 本快照只许做吞吐对照）");

        Assert.True(s1.Success, s1.Error);
        Assert.True(s2.Success, s2.Error);
    }

    private sealed record Measurement(
        string Label, bool Success, string? Error, double WallSeconds, long Phase2Ms,
        long ScanCalls, long ScannedTasks, long Candidates, long SkippedNoPi,
        long MergeAttempts, long PhysicalFeasible, long FinallyAdopted, int FinalTasks);

    private async Task<Measurement> RunAsync(string jsonPath, int limit, string fastPath, string guard, string label)
    {
        _output.WriteLine($"[A6] {label} 开始：{DateTime.Now:HH:mm:ss}");

        var request = Load(jsonPath);
        var originalCount = request.LogicalProductionDemands.Count;

        // `LogicalProductionDemands` 是 `init` 属性 —— `init` 在 IL 上就是普通 setter，反射可写。
        //   用反射而不是复制整个对象：`DomainSolveRequest` 有 30+ 个字段，手抄一遍必然漏一个。
        var limited = request.LogicalProductionDemands.Take(limit).ToList();
        typeof(DomainSolveRequest)
            .GetProperty(nameof(DomainSolveRequest.LogicalProductionDemands), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(request, limited);

        _output.WriteLine($"[A6] {label} 需求已截断：原始 {originalCount} → 本轮 {limited.Count}");

        PhaseTwoInitialScheduler.OverrideAnchorModeForTest("PI_ONLY");
        PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(fastPath, guard);
        try
        {
            using var scope = SolverDiagnostics.BeginScope();
            var c = scope.Counters;
            // 全量单档 70+ 分钟：挂上逐拍采样，日志里能看到「处理到第几条需求」这类推进读数。
            using var sampler = RealDomainFullRunTest.SolverLiveSampler.Start(
                c, request.PlanVersionId, _output);
            var sw = Stopwatch.StartNew();
            DomainSolveResult? result;
            try
            {
                result = await new FiniteCapacitySolver().SolveAsync(request, CancellationToken.None);
            }
            finally
            {
                sw.Stop();
            }

            var m = new Measurement(
                label,
                result?.Success ?? false,
                result?.ErrorMessage,
                sw.Elapsed.TotalSeconds,
                c.Phase2Ms,
                c.SingleOpScanCalls,
                c.SingleOpScannedTaskCount,
                c.SingleOpCandidateCount,
                c.SingleOpMergeSkippedNoPi,
                c.MergeAttempts,
                c.MergePhysicalFeasible,
                c.MergeFinallyAdopted,
                result?.FinalTasks?.Count ?? 0);

            _output.WriteLine($"[A6] {label} 结束：{DateTime.Now:HH:mm:ss}，墙钟 {m.WallSeconds:F1}s，"
                              + $"Success={m.Success}，FinalTask={m.FinalTasks}");
            return m;
        }
        finally
        {
            PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(null, null);
            PhaseTwoInitialScheduler.OverrideAnchorModeForTest(null);
        }
    }

    /// <summary>流式反序列化（与 `SolverReplayTest` 同一组选项，读写对称）。</summary>
    private static DomainSolveRequest Load(string jsonPath)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };

        using var fs = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        return System.Text.Json.JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
               ?? throw new InvalidOperationException($"入参 JSON 反序列化得到 null：{jsonPath}");
    }
}