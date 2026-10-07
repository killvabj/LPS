using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// TaskSplitRuleConfigProjector 单元测试（Batch Policy 投影/编解码，0号位 2026-10-07 裁决本轮落码）。
/// 无状态纯函数，直接钉死：IsActive/生效区间筛除、去审计字段投影、ContentSnapshotJson 子块编解码 + fail-open。
/// </summary>
public class TaskSplitRuleConfigProjectorTests
{
    private static TaskSplitRuleConfig Rule(
        int id = 1, int materialId = 100, int? deptId = null, bool isActive = true,
        DateTime? effectiveFrom = null, DateTime? effectiveTo = null,
        decimal? min = null, decimal? max = null, bool allowSplit = true)
        => new()
        {
            Id = id,
            MaterialId = materialId,
            ProductionDepartmentId = deptId,
            IsActive = isActive,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            AllowSplit = allowSplit,
        };

    [Fact]
    public void Project_筛除IsActivefalse()
    {
        var rules = new[]
        {
            Rule(id: 1, materialId: 100, isActive: true),
            Rule(id: 2, materialId: 200, isActive: false),
        };

        var result = TaskSplitRuleConfigProjector.Project(rules, asOf: DateTime.UtcNow);

        result.Should().ContainSingle().Which.MaterialId.Should().Be(100);
    }

    [Fact]
    public void Project_筛除生效区间外()
    {
        var now = DateTime.UtcNow;
        var rules = new[]
        {
            Rule(id: 1, materialId: 100, effectiveFrom: now.AddDays(-1), effectiveTo: now.AddDays(1)),
            Rule(id: 2, materialId: 200, effectiveFrom: now.AddDays(1)),
            Rule(id: 3, materialId: 300, effectiveTo: now.AddDays(-1)),
        };

        var result = TaskSplitRuleConfigProjector.Project(rules, asOf: now);

        result.Should().ContainSingle().Which.MaterialId.Should().Be(100);
    }

    [Fact]
    public void Project_投影业务键与参数_去审计兼容字段()
    {
        var rule = Rule(id: 7, materialId: 200, deptId: 300, min: 10m, max: 50m, allowSplit: false);
        rule.PreferredBatchQty = 25m;
        rule.AllowMerge = true;
        rule.MaxOptimizationSplitCount = 3;
        rule.MaxBatchCandidates = 5;
        rule.BottleneckSplitStrategy = "PREFER_SPLIT";
        rule.NonBottleneckStrategy = "PREFER_LARGE_BATCH";

        var result = TaskSplitRuleConfigProjector.Project(new[] { rule }).Single();

        result.MaterialId.Should().Be(200);
        result.ProductionDepartmentId.Should().Be(300);
        result.MinExecutionBatchQty.Should().Be(10m);
        result.MaxExecutionBatchQty.Should().Be(50m);
        result.PreferredBatchQty.Should().Be(25m);
        result.AllowSplit.Should().BeFalse();
        result.AllowMerge.Should().BeTrue();
        result.MaxOptimizationSplitCount.Should().Be(3);
        result.MaxBatchCandidates.Should().Be(5);
        result.BottleneckSplitStrategy.Should().Be("PREFER_SPLIT");
        result.NonBottleneckStrategy.Should().Be("PREFER_LARGE_BATCH");
    }

    [Fact]
    public void SetRules_保留其它子块_原子替换BatchPolicy()
    {
        var original = "{\"Lock\":{\"x\":1},\"BatchPolicy\":[{\"MaterialId\":1}]}";
        var snapshots = new List<BatchPolicyRuleSnapshot> { new() { MaterialId = 200, AllowSplit = false } };

        var merged = TaskSplitRuleConfigProjector.SetRules(original, snapshots);

        TaskSplitRuleConfigProjector.ExtractRules(merged).Should().ContainSingle().Which.MaterialId.Should().Be(200);
        merged.Should().Contain("\"Lock\"");
    }

    [Fact]
    public void ExtractRules_缺失或空或损坏_返回空列表()
    {
        TaskSplitRuleConfigProjector.ExtractRules(null).Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules(string.Empty).Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules("{\"Lock\":{}}").Should().BeEmpty();
        TaskSplitRuleConfigProjector.ExtractRules("{{{bad json").Should().BeEmpty();
    }
}