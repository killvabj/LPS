using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 3号位 2026-11-16 准入的 `$.CrossFactoryLeadTimeRules` 消费匹配。
/// 只测纯解析：工厂对精确、产品族细化优先、IsActive、合法 0 与“无规则”分开。
/// </summary>
public class CrossFactoryLeadTimeRuleTests
{
    private static CrossFactoryLeadTimeRule Rule(
        string from, string to, int hours, string? family = null, bool? active = true)
        => new()
        {
            FromFactoryCode = from,
            ToFactoryCode = to,
            ProductFamilyCode = family,
            CrossFactoryLeadTimeHours = hours,
            IsActive = active
        };

    [Fact(DisplayName = "跨厂LT：工厂对精确命中，小时×60 成分钟")]
    public void ExactFactoryPair_Matches()
    {
        var rules = new[] { Rule("BJ", "CN", 24) };

        Assert.True(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", null, out var lag));
        Assert.Equal(1440m, lag);
    }

    [Fact(DisplayName = "跨厂LT：产品族精确规则优先于全族规则")]
    public void ExactFamily_WinsOverGeneric()
    {
        var rules = new[]
        {
            Rule("BJ", "CN", 24),
            Rule("BJ", "CN", 48, "PF-A")
        };

        Assert.True(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", "PF-A", out var lag));
        Assert.Equal(2880m, lag);
    }

    [Fact(DisplayName = "跨厂LT：无精确族规则时回落全族规则")]
    public void NoExactFamily_FallsBackToGeneric()
    {
        var rules = new[]
        {
            Rule("BJ", "CN", 24),
            Rule("BJ", "CN", 48, "PF-A")
        };

        Assert.True(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", "PF-B", out var lag));
        Assert.Equal(1440m, lag);
    }

    [Fact(DisplayName = "跨厂LT：IsActive 非 true 不参与匹配")]
    public void Inactive_IsIgnored()
    {
        var rules = new[] { Rule("BJ", "CN", 24, active: null) };

        Assert.False(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", null, out _));
    }

    [Fact(DisplayName = "跨厂LT：方向反了不命中")]
    public void ReversedDirection_DoesNotMatch()
    {
        var rules = new[] { Rule("BJ", "CN", 24) };

        Assert.False(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "CN", "BJ", null, out _));
    }

    [Fact(DisplayName = "跨厂LT：合法 0 返回 true、lag=0，与无规则严格分开")]
    public void LegalZero_ReturnsTrueZero()
    {
        var rules = new[] { Rule("BJ", "CN", 0) };

        Assert.True(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", null, out var lag));
        Assert.Equal(0m, lag);

        Assert.False(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(
            Array.Empty<CrossFactoryLeadTimeRule>(), "BJ", "CN", null, out _));
    }

    [Fact(DisplayName = "跨厂LT：只有族规则、但族码为空时不猜全族")]
    public void FamilyOnlyRule_WithNullFamily_DoesNotMatch()
    {
        var rules = new[] { Rule("BJ", "CN", 48, "PF-A") };

        Assert.False(PeggingOrchestrator.TryResolveCrossFactoryLagMinutes(rules, "BJ", "CN", null, out _));
    }
}