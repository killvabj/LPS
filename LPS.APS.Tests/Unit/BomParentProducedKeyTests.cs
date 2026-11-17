using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// C01 整改测试（2026-11-13）：BFS 展开里「父件产出」的查询键必须按<b>父件自己的制造厂</b>，
/// 不能按子件厂（<c>node.FactoryId</c>）。
///
/// <para><b>本测试断言的是「声明的目的」，不是「跑得过」</b>：</para>
/// <list type="number">
///   <item><b>核心回归</b>：子件被 <c>ChildRequiredFactory</c> 改厂后，父产出键仍落父厂 ——
///         这正是原缺陷（PV 999487 实测 8,174 个改厂节点，跨厂父边一律 <c>continue</c> ⇒ 血缘静默丢失）。</item>
///   <item><b>旧写法确实会漏</b>：用子件厂查同一份 <c>produced</c> 必须查不到（证明用例有回归意义，
///         否则说明场景没构造出跨厂）。</item>
///   <item><b>同厂不变</b>：父子同厂时行为与原口径逐字相同（不得把同厂也改坏）。</item>
///   <item><b>不新造兜底</b>：父件码不在展开结果里时沿用原毛量口径（子件厂兜底），不抛、不返回 <c>0</c>。</item>
///   <item><b>接错父任务</b>：同一父件码在子件厂另有一份产出时，新口径取父厂那份 ——
///         否则 <c>RequiredQty</c> 会取错量、<c>ConsumerLogicalDemandKey</c> 会指错父任务。</item>
/// </list>
///
/// <para>⚠ 本测试覆盖的是<b>解析口</b>（<see cref="PeggingOrchestrator.ResolveParentFactoryId"/>）。
/// 「毛量」与「建边」两个调用点是否都走该解析口，代码注释已钉住；真实条数由运行期
/// <c>[Pegging][C01] 跨厂血缘边</c> 日志给出（改前恒 0）。</para>
/// </summary>
public class BomParentProducedKeyTests
{
    /// <summary>父件在 TJ（Id=1）；子件被改到 BJ（Id=2）。这是 O65 之后的典型跨厂形态。</summary>
    private static readonly IReadOnlyDictionary<string, int> ReroutedMap =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["P-PARENT"] = 1,   // 父件 TJ
            ["C-CHILD"]  = 2    // 子件改到 BJ
        };

    private const int Tj = 1;
    private const int Bj = 2;

    [Fact(DisplayName = "核心回归：子件改厂后，父件产出键仍按父件自己的厂（TJ），不是子件厂（BJ）")]
    public void ResolveParentFactoryId_ReroutedChild_UsesParentFactory()
    {
        var parentFactoryId = PeggingOrchestrator.ResolveParentFactoryId(ReroutedMap, "P-PARENT", fallbackFactoryId: Bj);

        parentFactoryId.Should().Be(Tj,
            "父件产出键必须是 父件|父厂；用子件厂查不到父产出 ⇒ 血缘静默丢失（1号位 少一条 TaskDependency ⇒ 子件可排到父件之前）");
    }

    [Fact(DisplayName = "回归基线：旧写法（子件厂 BJ）在同一份 produced 上查不到，新写法查得到")]
    public void OldChildFactoryKey_MissesWhereNewParentFactoryKeyHits()
    {
        // 模拟 RunBfsLoop 的 produced：只有父件在它自己的厂（TJ）产出了。
        var produced = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [$"P-PARENT|{Tj}"] = 100
        };

        var oldKey = $"P-PARENT|{Bj}";   // 旧写法：SupplyPool.BuildKey(edge.ParentMaterialCode, node.FactoryId)
        var newKey = $"P-PARENT|{PeggingOrchestrator.ResolveParentFactoryId(ReroutedMap, "P-PARENT", Bj)}";

        produced.ContainsKey(oldKey).Should().BeFalse("若这里为 true，说明用例没构造出真正的跨厂场景，回归失效");
        produced.ContainsKey(newKey).Should().BeTrue("新口径必须命中父件在父厂的产出");
        newKey.Should().Be($"P-PARENT|{Tj}");
    }

    [Fact(DisplayName = "同厂不变：父子同厂时仍取该厂，不引入任何变化")]
    public void SameFactory_Unchanged()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["P-PARENT"] = Tj,
            ["C-CHILD"]  = Tj
        };

        PeggingOrchestrator.ResolveParentFactoryId(map, "P-PARENT", Tj).Should().Be(Tj);
    }

    [Fact(DisplayName = "父件码不在展开结果里 ⇒ 回落调用方给的兜底厂（沿用原毛量口径），不抛也不返回 0")]
    public void UnknownParentCode_FallsBackToCallerFactory()
    {
        PeggingOrchestrator.ResolveParentFactoryId(ReroutedMap, "P-NOT-IN-EXPLOSION", fallbackFactoryId: Bj)
            .Should().Be(Bj, "父件码查不到时沿用原行为（子件厂兜底）；若返回 0 会去查一个不存在的工厂键");

        PeggingOrchestrator.ResolveParentFactoryId(ReroutedMap, "P-NOT-IN-EXPLOSION", fallbackFactoryId: 0)
            .Should().Be(0);
    }

    [Fact(DisplayName = "接错父任务：同一父件码在子件厂另有一份产出时，必须取父厂那份（数量与依赖才对）")]
    public void DuplicateProducedAtChildFactory_TakesParentFactoryInstance()
    {
        // 病态但真实存在：同一个父件码在本订单内既在 TJ 产出（100），又在 BJ 产出了另一份（999）。
        var produced = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [$"P-PARENT|{Tj}"] = 100,
            [$"P-PARENT|{Bj}"] = 999
        };

        var key = $"P-PARENT|{PeggingOrchestrator.ResolveParentFactoryId(ReroutedMap, "P-PARENT", Bj)}";

        produced[key].Should().Be(100,
            "旧写法会命中 BJ 那份（999）⇒ RequiredQty 取错量、ConsumerLogicalDemandKey 指向别的父任务");
    }
}
