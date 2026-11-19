using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;
using static LPS.APS.Application.Services.PeggingOrchestrator;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 跨厂分流判定（0号位 2026-11-13《跨厂BOM先行、M资格分型、SH池用途及三轮一次Solver统一回复》§一／§二）。
///
/// <para><b>断言的目的是这五条，不是「跑得过」：</b></para>
/// <list type="number">
///   <item><b>没有跨厂边就不是跨厂需求</b>——首要真值。不能因为 SH 池里有同物料 SH 就反推跨厂。</item>
///   <item><b>有合法 M ⇒ PI 接续</b>。</item>
///   <item><b>确认无 M ⇒ SH 履约</b>，且这是**正常业务结果**，不是错误。</item>
///   <item><b>M 数据不全 ⇒ UNKNOWN</b>，不得与「确认无 M」混同、不得默认 TRUE。</item>
///   <item><b>有 M 优先于数据不全</b>——已证有 M 是正证据，不该因别处数据缺失而被降级为 UNKNOWN。</item>
/// </list>
///
/// <para>⚠ 本测试只覆盖判定纯函数。目标厂取自跨厂边的 `ToFactoryCode`、
/// M 资格取自 `(MaterialCode, ToFactoryCode)`，这两点由调用方保证（见 `CrossFactoryFacts.Classify`）。</para>
/// </summary>
public class CrossFactoryFabricationTypeTests
{
    [Fact(DisplayName = "没有跨厂边 ⇒ NotCrossFactory（不得因 SH 池有同物料 SH 就反推跨厂）")]
    public void NoEdge_IsNotCrossFactory()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: false, hasM: false, mDataIncomplete: false)
            .Should().Be(PeggingOrchestrator.FabricationType.NotCrossFactory);
    }

    [Fact(DisplayName = "无边但库里有 M ⇒ 仍是 NotCrossFactory（边是首要真值）")]
    public void NoEdge_EvenWithM_IsStillNotCrossFactory()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: false, hasM: true, mDataIncomplete: false)
            .Should().Be(PeggingOrchestrator.FabricationType.NotCrossFactory);
    }

    [Fact(DisplayName = "有跨厂边 + 有合法 M ⇒ PI 接续")]
    public void EdgeWithM_IsPiContinuation()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: true, hasM: true, mDataIncomplete: false)
            .Should().Be(PeggingOrchestrator.FabricationType.PiContinuation);
    }

    [Fact(DisplayName = "有跨厂边 + 已证实无 M ⇒ SH 履约（正常业务结果，不是错误）")]
    public void EdgeWithoutM_IsShFulfilment()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: true, hasM: false, mDataIncomplete: false)
            .Should().Be(PeggingOrchestrator.FabricationType.ShFulfilment);
    }

    [Fact(DisplayName = "有跨厂边 + 无 M 但数据不全 ⇒ UNKNOWN（不得当『确认无 M』放行）")]
    public void EdgeWithoutMButIncompleteData_IsUnknown()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: true, hasM: false, mDataIncomplete: true)
            .Should().Be(PeggingOrchestrator.FabricationType.Unknown);
    }

    [Fact(DisplayName = "有 M 优先于数据不全：已证有 M 不被降级为 UNKNOWN")]
    public void HasM_WinsOverIncompleteData()
    {
        PeggingOrchestrator
            .ClassifyFabricationType(hasCrossFactoryEdge: true, hasM: true, mDataIncomplete: true)
            .Should().Be(PeggingOrchestrator.FabricationType.PiContinuation);
    }

    // ── 「没边 ≠ 同厂」（5号位 2026-11-14 §四）──────────────────────────────────

    private static PeggingOrchestrator.CrossFactoryFacts FactsWithSkip(
        (string, string, string)[]? edges, (string, string, string)[] skip, bool hasM = false)
    {
        var edgeMap = new Dictionary<(string, string, string), HashSet<(string, string)>>();
        foreach (var e in edges ?? Array.Empty<(string, string, string)>())
            edgeMap[e] = new HashSet<(string, string)> { ("BJ", "CN") };

        return new PeggingOrchestrator.CrossFactoryFacts
        {
            EdgesByBomParentChild = edgeMap,
            SkipKeys = new HashSet<(string, string, string)>(skip),
            MQualifiedPairs = hasM
                ? new HashSet<(string, string)> { ("MAT", "CN") }
                : new HashSet<(string, string)>()
        };
    }

    [Fact(DisplayName = "没边但命中 SKIP 清单 ⇒ UNKNOWN（不得回落为同厂）")]
    public void NoEdge_ButInSkipList_IsUnknown()
    {
        var facts = FactsWithSkip(
            edges: null,
            skip: new[] { ("BOM1", "PARENT1", "MAT") });

        facts.Classify("BOM1", new[] { "PARENT1" }, "MAT", "CN")
             .Should().Be(PeggingOrchestrator.FabricationType.Unknown);
    }

    [Fact(DisplayName = "没边且不在 SKIP 清单 ⇒ 仍是 NotCrossFactory（真的不跨厂）")]
    public void NoEdge_NotInSkipList_IsNotCrossFactory()
    {
        var facts = FactsWithSkip(
            edges: null,
            skip: new[] { ("BOM1", "OTHER_PARENT", "OTHER_MAT") });

        facts.Classify("BOM1", new[] { "PARENT1" }, "MAT", "CN")
             .Should().Be(PeggingOrchestrator.FabricationType.NotCrossFactory,
                 "SKIP 清单里是别的三元组，不能连坐");
    }

    [Fact(DisplayName = "目标厂取跨厂边 ToFactoryCode，不取 ChildRequiredFactory/节点继承厂")]
    public void TargetFactory_UsesEdgeTo_NotChildRequiredFactory()
    {
        var facts = FactsWithSkip(
            edges: new[] { ("BOM1", "PARENT1", "CHILD1") },
            skip: Array.Empty<(string, string, string)>());

        var target = facts.ResolveTargetFactoryCode(
            "BOM1", new[] { "PARENT1" }, "CHILD1", fallback: "BJ");

        target.Should().Be("CN", "边为 BJ→CN，目标厂必须是 To=CN；BJ 只是源厂/ChildRequiredFactory");
    }

    [Fact(DisplayName = "顶层 bomLevel=0 根需求不得判 ShFulfilment（CYL 根成品误判回归）")]
    public void RootLevelDemand_IsNotCrossFactory_NotShFulfilment()
    {
        var facts = FactsWithSkip(edges: null, skip: Array.Empty<(string, string, string)>());

        var (type, allowed, edgeId) = facts.ResolveNode(
            "BOM1", Array.Empty<string>(), "FINAL_FG-C85KN8-15S-Z1-DNE3964-YP0457", "CN", bomLevel: 0);

        type.Should().Be(PeggingOrchestrator.FabricationType.NotCrossFactory);
        allowed.Should().BeNull();
        edgeId.Should().BeNull();
    }

    [Fact(DisplayName = "有边时 SKIP 清单不影响判定（边证明跨厂优先）")]
    public void HasEdge_SkipListDoesNotOverride()
    {
        var facts = FactsWithSkip(
            edges: new[] { ("BOM1", "PARENT1", "MAT") },
            skip: new[] { ("BOM1", "PARENT1", "MAT") });

        facts.Classify("BOM1", new[] { "PARENT1" }, "MAT", "CN")
             .Should().Be(PeggingOrchestrator.FabricationType.ShFulfilment,
                 "即使该三元组在 SKIP 里，只要边真的存在就按边判");
    }
}
