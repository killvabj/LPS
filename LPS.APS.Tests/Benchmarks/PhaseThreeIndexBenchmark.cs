using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Benchmarks;

/// <summary>
/// 2026-11-06 Phase3 首批整改 第1步 / 第2步的**纯内存**新旧耗时对照。
///
/// <para>【为什么不跑真实域】0号位 指令 §五 要 JS/QT 真实耗时，但真实域要连库装载、
/// 要把结果写回库。用户 2026-11-06 明确「不写库测试吧，只测试咱们的内容即可」
/// ⇒ 本文件只量**这两处扫描本身**：同一份内存数据，旧算法与新算法各跑一遍，给绝对毫秒与倍数。
/// 它**不含**装载、Pegging、落库，**不代表**整个 Run 的墙钟缩短比例。</para>
///
/// <para>【怎么读】倍数只在这两个方法内部成立。真实 Run 里的节省 =
/// 本表倍数对应的耗时差 × 该域的真实调用规模，两者相乘才是对 Phase3 的贡献。</para>
///
/// <para>【开门】默认跳过，避免拖慢日常测试：
/// <code>APS_P3_INDEX_BENCH=1 dotnet test LPS.APS.Tests -c Release --filter FullyQualifiedName~PhaseThreeIndexBenchmark</code>
/// </para>
/// </summary>
public sealed class PhaseThreeIndexBenchmark
{
    private const string EnvVar = "APS_P3_INDEX_BENCH";

    private readonly ITestOutputHelper _output;

    public PhaseThreeIndexBenchmark(ITestOutputHelper output) => _output = output;

    private static readonly DateTime Base = new DateTime(2026, 10, 1, 0, 0, 0);

    [SkippableFact(DisplayName = "1号位: Phase3 第1/2步 索引化纯内存新旧耗时对照")]
    public void CompareLegacyAndIndexed()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"默认跳过（{EnvVar} 未设为 1）。纯内存基准，不连库、不写库。");

        _output.WriteLine("Phase3 第1步（锁定表）/ 第2步（阻挡块）新旧耗时对照");
        _output.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核，.NET {Environment.Version}，Release，纯内存");
        _output.WriteLine("");

        _output.WriteLine("── 第1步：锁定表 ───────────────────────────────────────────────");
        _output.WriteLine("  锁定条目 × 延期需求                 旧·线性扫        新·索引        倍数");
        foreach (var (locked, delayed) in new[] { (1_000, 100), (10_000, 1_000), (50_000, 5_000) })
        {
            var lockedIds = BuildLockedIds(locked);
            var demandKeys = BuildDemandKeys(delayed);

            var oldMs = Best(() => LegacyLockHitCount(lockedIds, demandKeys), 3);
            var newMs = Best(() => IndexedLockHitCount(lockedIds, demandKeys), 3);

            _output.WriteLine(
                $"  {locked,7:N0} × {delayed,7:N0}                 {oldMs,9:F2} ms  {newMs,9:F2} ms  "
                + $"{Ratio(oldMs, newMs),9}");
        }

        _output.WriteLine("");
        _output.WriteLine("── 第2步：资源阻挡块 ───────────────────────────────────────────");
        _output.WriteLine("  块数 × 每条需求Task × 延期需求      场景        旧·全量扫     新·定向查     倍数");

        foreach (var (blockCount, tasksPerDemand, delayed) in new[]
                 {
                     (10_000, 5, 1_000),
                     (50_000, 5, 2_000),
                     (200_000, 5, 3_000),
                 })
        {
            foreach (var scenario in new[] { ScenarioMiss, ScenarioLastHit })
            {
                var (request, tasksPerDemandList) = BuildScenario(blockCount, tasksPerDemand, delayed, scenario);

                var oldMs = Best(() => LegacyBlockHitCount(tasksPerDemandList, request), 3);
                var newMs = Best(() => IndexedBlockHitCount(tasksPerDemandList, request), 3);

                _output.WriteLine(
                    $"  {blockCount,7:N0} × {tasksPerDemand} × {delayed,6:N0}          {scenario,-10} "
                    + $"{oldMs,9:F2} ms  {newMs,9:F2} ms  {Ratio(oldMs, newMs),9}");
            }
        }

        _output.WriteLine("");
        _output.WriteLine("说明：本表只含这两处扫描本身，不含装载/Pegging/落库；「新」侧已含一次建索引的成本。");
    }

    private const string ScenarioMiss = "全不命中";
    private const string ScenarioLastHit = "末位才命中";

    private static string Ratio(double oldMs, double newMs)
        => newMs <= 0.001 ? ">1000×" : $"{oldMs / newMs,7:F1}×";

    // ── 数据构造 ────────────────────────────────────────────────────────

    private static List<LockedTaskConstraint> BuildLockedIds(int count)
    {
        var list = new List<LockedTaskConstraint>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new LockedTaskConstraint { DraftId = $"LOCKED-{i:D7}" });
        }
        return list;
    }

    /// <summary>一半命中、一半不命中，避免只测「全命中」或「全未命中」。</summary>
    private static List<string> BuildDemandKeys(int count)
    {
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(i % 2 == 0 ? $"LOCKED-{i:D7}" : $"ABSENT-{i:D7}");
        }
        return list;
    }

    private static (DomainSolveRequest Request, List<List<FinalTaskDraft>> PerDemand) BuildScenario(
        int blockCount, int tasksPerDemand, int delayed, string scenario)
    {
        // 资源 0..9 循环；块按「外部在前、上游在后」两段构造（与原实现的拼接顺序一致）。
        var external = new List<ResourceBlock>(blockCount / 2);
        var upstream = new List<ResourceBlock>(blockCount / 2);

        for (var i = 0; i < blockCount; i++)
        {
            var start = Base.AddMinutes(i);
            var block = new ResourceBlock
            {
                ResourceId = i % 10,
                StartTime = start,
                EndTime = start.AddMinutes(30),
                SourceDomainKey = i % 4 == 0 ? "DOM_EXT" : null,
            };
            if (i % 2 == 0) external.Add(block);
            else upstream.Add(block);
        }

        // 「全不命中」：Task 排在所有块之后 ⇒ 两条路径都要走完全量（最坏情况）。
        // 「末位才命中」：Task 落在最后一个块的时间窗上 ⇒ 旧算法扫到最后才命中。
        var taskStart = scenario == ScenarioLastHit
            ? Base.AddMinutes(blockCount - 1)
            : Base.AddMinutes(blockCount).AddHours(1);

        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "BENCH",
            PlanningStart = Base,
            PlanningEnd = Base.AddYears(1),
            CandidateContext = new CandidateContext { ExternalDomainResourceBlocks = external },
            UpstreamDomainResourceBlocks = upstream,
        };

        var perDemand = new List<List<FinalTaskDraft>>(delayed);
        for (var d = 0; d < delayed; d++)
        {
            var list = new List<FinalTaskDraft>(tasksPerDemand);
            for (var t = 0; t < tasksPerDemand; t++)
            {
                list.Add(new FinalTaskDraft
                {
                    FinalDraftId = $"D{d}T{t}",
                    // 资源 0..9 循环，覆盖「桶里有很多块」与「桶很浅」两种
                    ResourceId = t % 10,
                    PlannedStartTime = taskStart,
                    PlannedEndTime = taskStart.AddMinutes(60),
                });
            }
            perDemand.Add(list);
        }

        return (request, perDemand);
    }

    // ── 旧算法 ──────────────────────────────────────────────────────────

    private static int LegacyLockHitCount(List<LockedTaskConstraint> locked, List<string> demandKeys)
    {
        var hits = 0;
        foreach (var key in demandKeys)
        {
            LockedTaskConstraint? found = null;
            foreach (var t in locked)
            {
                if (t.DraftId == key) { found = t; break; }
            }
            if (found != null) hits++;
        }
        return hits;
    }

    private static int LegacyBlockHitCount(List<List<FinalTaskDraft>> perDemand, DomainSolveRequest request)
    {
        var hits = 0;
        foreach (var demandTasks in perDemand)
        {
            if (LegacyBlockOverlap(demandTasks, request)) hits++;
        }
        return hits;
    }

    private static bool LegacyBlockOverlap(List<FinalTaskDraft> demandTasks, DomainSolveRequest request)
    {
        var blocks = new List<(ResourceBlock Block, bool Cross)>();
        if (request.CandidateContext?.ExternalDomainResourceBlocks != null)
        {
            foreach (var b in request.CandidateContext.ExternalDomainResourceBlocks)
                blocks.Add((b, !string.IsNullOrEmpty(b.SourceDomainKey)));
        }
        if (request.UpstreamDomainResourceBlocks != null)
        {
            foreach (var b in request.UpstreamDomainResourceBlocks)
                blocks.Add((b, !string.IsNullOrEmpty(b.SourceDomainKey)));
        }

        foreach (var task in demandTasks)
        {
            if (task.ResourceId is not int taskResourceId) continue;
            foreach (var (block, _) in blocks)
            {
                if (block.ResourceId == taskResourceId &&
                    task.PlannedStartTime < block.EndTime && block.StartTime < task.PlannedEndTime)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ── 新算法（直接调生产代码）──────────────────────────────────────────

    private static int IndexedLockHitCount(List<LockedTaskConstraint> locked, List<string> demandKeys)
    {
        var constraints = new ConstraintContext();
        for (var i = 0; i < locked.Count; i++)
        {
            constraints.LockedTasks[(locked[i].DraftId, "S", "OP")] = locked[i];
        }

        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(constraints);

        var hits = 0;
        foreach (var key in demandKeys)
        {
            if (index.Contains(key)) hits++;
        }
        return hits;
    }

    private static int IndexedBlockHitCount(List<List<FinalTaskDraft>> perDemand, DomainSolveRequest request)
    {
        var index = PhaseThreeDiagnostics.BuildResourceBlockIndex(request);

        var hits = 0;
        foreach (var demandTasks in perDemand)
        {
            if (PhaseThreeDiagnostics.HasResourceBlockOverlap(demandTasks, index, out _, out _))
            {
                hits++;
            }
        }
        return hits;
    }

    // ── 计时 ────────────────────────────────────────────────────────────

    private static double Best(Func<int> body, int runs)
    {
        body(); // 预热，避免把 JIT 算进去

        var best = double.MaxValue;
        for (var r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            var result = body();
            sw.Stop();
            GC.KeepAlive(result);
            if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
        }
        return best;
    }
}