using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 探针】PV999507 的「437 自由份额零 Task」逐项对账。
///
/// 背景（0号位 T1）：`FAMILY_CYL / PV=999507` 历史上报「TaskShare 数量不闭合、437 自由份额零 Task」。
/// 修复 r14231/r14244 后重放 `Success=True`，但 0号位 要求「Success=True 不代表 437 自然归零」，
/// 要交「437 → 逐项去向（已排 / 合法未排 / 静默丢弃）」的直接证据，不能只看 Success。
///
/// 开门：`APS_REPLAY_SOLVE_REQUEST=D:\tmp\p5snap_overlap\request.json`；结果追加写
/// `APS_437_OUT`（默认 %TEMP%/famcyl437.txt）。约 40 分钟。
/// </summary>
public sealed class FamilyCyl437ReconcileTest
{
    private readonly ITestOutputHelper _output;
    public FamilyCyl437ReconcileTest(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "1号位: PV999507 437 自由份额零Task 逐项对账")]
    public async Task Reconcile437()
    {
        var jsonPath = Environment.GetEnvironmentVariable("APS_REPLAY_SOLVE_REQUEST");
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath), "未指定入参 JSON");

        var options = new System.Text.Json.JsonSerializerOptions
        { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles, MaxDepth = 256 };
        using var fs = new FileStream(jsonPath!, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        var request = System.Text.Json.JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
                      ?? throw new InvalidOperationException("反序列化 null");

        _output.WriteLine($"[437] 入参：需求 {request.LogicalProductionDemands?.Count}");

        // ── 运行中进度：挂与 SolverReplayTest 同款的采样器，边跑边把 phase / p2 百分比 / p4 / 内存
        //    逐拍写进 CSV（落点由 APS_SOLVER_SAMPLE_CSV 指定），跑多少分钟都不再是黑盒。──
        using var solverScope = SolverDiagnostics.BeginScope();
        using var sampler = RealDomainFullRunTest.SolverLiveSampler.Start(
            solverScope.Counters, request.PlanVersionId, _output);

        int timeoutMin = int.TryParse(Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var t) ? t : 150;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));
        var result = await new FiniteCapacitySolver().SolveAsync(request, cts.Token).ConfigureAwait(false);
        sampler.Stop();

        _output.WriteLine($"[437] 求解：Success={result.Success} FinalTasks={result.FinalTasks?.Count} Unscheduled={result.UnscheduledTasks?.Count}");

        // ── 逐项对账：自由份额（IsContinuation=false）需求里，谁零 Task 且未在未排出口 ──
        var scheduledKeys = new HashSet<string>(result.FinalTasks?.Select(t => t.SourceDraftId) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var unscheduledKeys = new HashSet<string>(result.UnscheduledTasks?.Select(u => u.DraftId) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var freeDemands = (request.LogicalProductionDemands ?? new List<LogicalProductionDemand>())
            .Where(d => d is not null && !d.IsContinuation).ToList();

        // ── 细分探针：按 AllocationSequence 汇总 AllocationTaskShare.ComponentQty ──
        //   V1 契约「一条 LogicalProductionDemand ↔ 一条 AllocationSequence」（2号位 实施包 :1414），
        //   故 `demand.AllocationSequence → Σ share.ComponentQty` 可判：量是否并入别的 Task（合批合法），
        //   还是根本没进任何份额（真静默丢）。⚠ `AllocationTaskShare` 无 DemandKey（D-01 载体缺口），
        //   只能按 AllocationSequence 对；1:1 时无歧义。
        var shareSumByAlloc = new Dictionary<long, decimal>();
        foreach (var s in result.AllocationShares)
        {
            shareSumByAlloc[s.AllocationSequence] =
                shareSumByAlloc.GetValueOrDefault(s.AllocationSequence) + s.ComponentQty;
        }

        var dropped = freeDemands
            .Where(d => !scheduledKeys.Contains(d.LogicalDemandKey) && !unscheduledKeys.Contains(d.LogicalDemandKey))
            .ToList();
        var droppedQty = dropped.Sum(d => d.NetOutputQty);
        var onlyUnsched = freeDemands
            .Where(d => !scheduledKeys.Contains(d.LogicalDemandKey) && unscheduledKeys.Contains(d.LogicalDemandKey))
            .ToList();

        // 三分桶：真静默丢 / 合批全量并入 / 部分并入（份额不足）
        var genuineDrop = new List<LogicalProductionDemand>();
        var mergedFull = new List<LogicalProductionDemand>();
        var mergedPartial = new List<LogicalProductionDemand>();
        foreach (var d in dropped)
        {
            if (!shareSumByAlloc.TryGetValue(d.AllocationSequence, out var shareSum) || shareSum <= 0m)
            {
                genuineDrop.Add(d);
            }
            else if (Math.Abs(shareSum - d.NetOutputQty) <= 0.001m)
            {
                mergedFull.Add(d);
            }
            else
            {
                mergedPartial.Add(d);
            }
        }
        var partialGapQty = mergedPartial.Sum(d =>
        {
            var s = shareSumByAlloc.GetValueOrDefault(d.AllocationSequence);
            return d.NetOutputQty - s > 0m ? d.NetOutputQty - s : 0m;
        });

        var lines = new List<string>
        {
            $"freeShareDemands={freeDemands.Count}",
            $"dropped(count)={dropped.Count}  droppedQty={droppedQty:0.###}",
            $"  genuineDrop(count)={genuineDrop.Count}  qty={genuineDrop.Sum(d => d.NetOutputQty):0.###}",
            $"  mergedFull(count)={mergedFull.Count}  qty={mergedFull.Sum(d => d.NetOutputQty):0.###}",
            $"  mergedPartial(count)={mergedPartial.Count}  qty={mergedPartial.Sum(d => d.NetOutputQty):0.###}  gapQty={partialGapQty:0.###}",
            $"unscheduledOnly(count)={onlyUnsched.Count}  unschedQty={onlyUnsched.Sum(d => d.NetOutputQty):0.###}",
            $"scheduled={scheduledKeys.Count} unscheduled={unscheduledKeys.Count}",
            $"allocationShares(rows)={result.AllocationShares.Count}  distinctAllocSeq={shareSumByAlloc.Count}",
        };
        foreach (var d in genuineDrop.Take(20)) lines.Add($"  GENUINE key={d.LogicalDemandKey} qty={d.NetOutputQty:0.###} alloc={d.AllocationSequence}");
        foreach (var d in mergedPartial.Take(20)) lines.Add($"  PARTIAL key={d.LogicalDemandKey} qty={d.NetOutputQty:0.###} share={shareSumByAlloc.GetValueOrDefault(d.AllocationSequence):0.###} alloc={d.AllocationSequence}");

        foreach (var l in lines) _output.WriteLine("[437] " + l);

        var outPath = Environment.GetEnvironmentVariable("APS_437_OUT");
        if (string.IsNullOrWhiteSpace(outPath)) outPath = Path.Combine(Path.GetTempPath(), "famcyl437_breakdown.txt");
        File.AppendAllLines(outPath, lines);

        // 对账目标：真静默丢应为 0（要么已排、要么合法未排、要么合批全量并入别的 Task）。
        Assert.True(genuineDrop.Count == 0,
            $"仍有 {genuineDrop.Count} 个自由份额需求零 Task、零份额、未进未排出口（真静默丢），合计 {genuineDrop.Sum(d => d.NetOutputQty):0.###} 单位。");
    }
}