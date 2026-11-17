using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **只重放 Phase5 的探针**（1号位，2026-11-14）。
///
/// <para><b>要回答的问题</b>：一次真实 Run 里 Phase2 明明排出了几十万 Task（且同资源零重叠），
/// 为什么 `DomainSolveResult.FinalTasks` 是空的？是哪一条 Phase5 校验把它整批判掉的、原话是什么？</para>
///
/// <para><b>为什么能只跑 Phase5</b>：`PhaseFiveCompression.SnapshotPhase5Inputs` 在
/// `APS_PHASE5_SNAPSHOT_DIR` 下已落了 `request.json`（Phase1 原始入参）与 `tasks.json`
/// （Phase2+Phase4 交付的全部 Task）。本文件读这两份，**用同一份生产代码**
/// `PhaseOneConstraintBuilder.BuildConstraints` 重建 `ConstraintContext`，再调
/// `PhaseFiveCompression.Compress` —— 与生产同一入口，**不重跑 Phase2**。</para>
///
/// <para><b>代价</b>：反序列化约 1~2 分钟，BuildConstraints 约 3~4 分钟（与本域 Phase1 同量级），
/// Phase5 本身几十秒 ⇒ 一轮 <b>约 5~8 分钟</b>，替代全流程 42 分钟。</para>
///
/// <para><b>默认不跑</b>：不设 <c>APS_P5_ONLY_DIR</c> 直接返回。</para>
///
/// 环境变量：
///   APS_P5_ONLY_DIR   快照目录（含 request.json / tasks.json）
///   APS_P5_ONLY_OUT   结论落盘文件，默认 &lt;快照目录&gt;/p5_only_result.txt
/// </summary>
public class Phase5OnlyReplayProbeTest
{
    private const string EnvDir = "APS_P5_ONLY_DIR";
    private const string EnvOut = "APS_P5_ONLY_OUT";

    private readonly ITestOutputHelper _output;

    public Phase5OnlyReplayProbeTest(ITestOutputHelper output) => _output = output;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true
    };

    private static T Deserialize<T>(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var v = JsonSerializer.Deserialize<T>(fs, JsonOpts);
        if (v is null) throw new InvalidOperationException($"反序列化得到 null: {path}");
        return v;
    }

    [Fact(DisplayName = "Phase5 单独重放：从快照跑 Compress，打印成功/失败原话")]
    public void ReplayPhase5Only()
    {
        var dir = Environment.GetEnvironmentVariable(EnvDir);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var outPath = Environment.GetEnvironmentVariable(EnvOut) is { Length: > 0 } p
            ? p
            : Path.Combine(dir, "p5_only_result.txt");

        var lines = new List<string>();
        void Log(string s)
        {
            lines.Add(s);
            _output.WriteLine(s);
        }

        var swAll = Stopwatch.StartNew();
        Log($"快照目录 = {dir}");
        Log($"开始时刻 = {DateTime.Now:O}");

        Log("[1/4] 反序列化 request.json ...");
        var sw = Stopwatch.StartNew();
        var request = Deserialize<DomainSolveRequest>(Path.Combine(dir, "request.json"));
        Log($"      完成 {sw.Elapsed.TotalSeconds:F1}s  需求={request.LogicalProductionDemands?.Count ?? 0}"
            + $"  资源={request.Resources?.Count ?? 0}  links={request.MaterialRequirementLinks?.Count ?? 0}");

        Log("[2/4] 反序列化 tasks.json ...");
        sw.Restart();
        var tasks = Deserialize<List<FinalTaskDraft>>(Path.Combine(dir, "tasks.json"));
        Log($"      完成 {sw.Elapsed.TotalSeconds:F1}s  Task={tasks.Count}");

        Log("[3/4] BuildConstraints（等价生产 Phase1）...");
        sw.Restart();
        var constraints = new PhaseOneConstraintBuilder().BuildConstraints(request);
        Log($"      完成 {sw.Elapsed.TotalSeconds:F1}s");

        Log("[4/4] 调 PhaseFiveCompression.Compress（与生产同一入口）...");
        var scheduleResult = new InitialScheduleResult();
        scheduleResult.ScheduledTasks.AddRange(tasks);
        var repairResult = new RepairResult();
        var diagnostics = new DiagnosticsResult();

        sw.Restart();
        DomainSolveResult result;
        try
        {
            result = new PhaseFiveCompression()
                .Compress(request, scheduleResult, repairResult, diagnostics, constraints);
        }
        catch (Exception ex)
        {
            Log($"      **抛出异常** {ex.GetType().Name}: {ex.Message}");
            Log($"      结束时刻 = {DateTime.Now:O}  总耗时 {swAll.Elapsed.TotalSeconds:F1}s");
            File.WriteAllLines(outPath, lines);
            return;
        }

        Log($"      Compress 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        Log(string.Empty);
        Log("=================== 结论 ===================");
        Log($"Success        = {result.Success}");
        Log($"ErrorMessage   = {result.ErrorMessage}");
        Log($"FinalTasks     = {result.FinalTasks?.Count ?? 0}");
        Log($"Unscheduled    = {result.UnscheduledTasks?.Count ?? 0}");
        Log($"ExplanationFacts = {result.ExplanationFacts?.Count ?? 0}");
        Log($"TraceNotes     = {result.SolveTraceNotes?.Count ?? 0}");

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            Log(string.Empty);
            Log("---- ErrorMessage 逐行 ----");
            foreach (var l in result.ErrorMessage.Split('\n').Take(40))
            {
                Log("  " + l.TrimEnd('\r'));
            }
        }

        Log(string.Empty);
        Log($"结束时刻 = {DateTime.Now:O}  总耗时 {swAll.Elapsed.TotalSeconds:F1}s");
        File.WriteAllLines(outPath, lines);

        Assert.True(File.Exists(outPath), $"结论未落盘: {outPath}");
    }
}