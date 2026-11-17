using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 探针】只跑 Phase1（BuildConstraints），把跨物料 DAG 对**特定一对父←子**的建边结果落地。
///
/// <para>背景（PV 999507 / FAMILY_CYL）：Phase5 硬校验报 TaskDependency 越序 ——
/// 父件 <c>999507_39077</c>（CN_ASSY）首道 09:32:20 开工，早于子件 <c>999507_39091</c>（BJ_MACH）末道 09:33:59 完工。
/// 重放已证该越序**在 Phase2 就存在**；而 link（Consumer=39077, Producer=39091, Lag=0）在 request 里真实存在、
/// 无反向边、两需求都在本域。⇒ 疑点是 <c>PhaseOneConstraintBuilder.BuildCrossMaterialDag</c> 在运行时没有把这条边
/// 写进 <c>CrossMaterialParentToChildren</c>。本探针只跑 Phase1，几分钟即可定位，不跑 40 分钟的 Phase2。</para>
///
/// <para>开门：<c>APS_DAG_PROBE_REQUEST=&lt;request.json 路径&gt;</c>，输出 <c>APS_DAG_PROBE_OUT</c>（默认 /tmp/dagedge.txt）。</para>
/// </summary>
public class DagEdgeProbeTest
{
    private readonly ITestOutputHelper _output;
    public DagEdgeProbeTest(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "1号位: Phase1 跨物料 DAG 建边探针（只跑 Phase1）")]
    public void ProbeDagEdge()
    {
        var jsonPath = Environment.GetEnvironmentVariable("APS_DAG_PROBE_REQUEST");
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath),
            $"未指定入参 JSON（APS_DAG_PROBE_REQUEST='{jsonPath ?? "(null)"}'）");

        var options = new System.Text.Json.JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };
        using var fs = new FileStream(jsonPath!, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        var request = System.Text.Json.JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
                      ?? throw new InvalidOperationException("反序列化 null");

        _output.WriteLine($"[P1] 需求数={request.LogicalProductionDemands?.Count} link数={request.MaterialRequirementLinks?.Count}");

        var constraints = new PhaseOneConstraintBuilder().BuildConstraints(request);

        const string parentKey = "999507_39077";
        const string childKey = "999507_39091";

        var lines = new List<string>
        {
            $"parent={parentKey} hasChildren={constraints.CrossMaterialParentToChildren.ContainsKey(parentKey)}"
            + $" childrenCount={(constraints.CrossMaterialParentToChildren.TryGetValue(parentKey, out var kids) ? kids.Count : 0)}"
            + $" containsChild={constraints.CrossMaterialParentToChildren.GetValueOrDefault(parentKey)?.Contains(childKey) == true}",
            $"parentChildren = {string.Join(",", constraints.CrossMaterialParentToChildren.GetValueOrDefault(parentKey) ?? new List<string>())}",
            $"child={childKey} hasParents={constraints.CrossMaterialChildToParents.ContainsKey(childKey)}"
            + $" parents={(constraints.CrossMaterialChildToParents.TryGetValue(childKey, out var ps) ? string.Join(",", ps) : "∅")}",
            $"hasCycle={constraints.CrossMaterialHasCycle} cycleKeys={constraints.CrossMaterialCycleKeys.Count}"
            + $" parentInCycle={constraints.CrossMaterialCycleKeys.Contains(parentKey)} childInCycle={constraints.CrossMaterialCycleKeys.Contains(childKey)}",
            $"orderParentIdx={constraints.CrossMaterialOrder.IndexOf(parentKey)} orderChildIdx={constraints.CrossMaterialOrder.IndexOf(childKey)}",
            $"lag(39077,39091)={constraints.CrossMaterialLagMinutes.GetValueOrDefault((parentKey, childKey))}",
        };

        foreach (var l in lines) { _output.WriteLine("[DAG] " + l); }

        var outPath = Environment.GetEnvironmentVariable("APS_DAG_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(outPath)) outPath = Path.Combine(Path.GetTempPath(), "dagedge.txt");
        File.WriteAllLines(outPath, lines);
    }
}