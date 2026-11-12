using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// O65（2026-11-10）：**子件按自己的应归属厂落需求**，不再一律继承父厂。
///
/// 断言的正是本改动声明的目的（不是「跑绿即有效」）：
///   ① 边上给了 `ChildRequiredFactory` 且能解析 ⇒ 节点工厂 = **该厂**，标记 `Rerouted`；
///   ② 该值与父厂相同 ⇒ 标记 `SameAsParent`（不算改厂）；
///   ③ 边上有值但 `Factory` 主档查无 ⇒ 保持父厂，标记 `UnknownFallback`（不静默丢弃）；
///   ④ 边上是空 ⇒ 保持父厂，标记 `MissingFallback`（走到展开说明要自制 ⇒ 空值即缺值）；
///   ⑤ 孙件**继承的是父件已解析的厂**，不是根订单厂；
///   ⑥ 多条父边给出不同应归属厂 ⇒ 标记 `ConflictFirstParentWins`（节点按物料唯一，只能取一个）。
///
/// ⚠ 这里是纯函数层（`BomExplosionService`）。主链 `RunBfsLoop` 还有一条同等重要的约束：
///   父件产出查找必须用**父件自己的厂**，否则子件改厂后会查不到父产出、需求静默归零 —— 那条在
///   `PeggingOrchestrator` 私有方法内，本文件不覆盖。
/// </summary>
public class BomChildFactoryResolutionTests
{
    private static readonly IBomExplosionService Service = new BomExplosionService();

    /// <summary>工厂主档：BJ=2、TJ=6（与实库 `Factory` 生产厂口径一致）。</summary>
    private static readonly IReadOnlyDictionary<string, int> FactoryMap =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["BJ"] = 2, ["TJ"] = 6 };

    private static BomStructure Structure(
        IReadOnlyDictionary<string, int>? factoryMap,
        params (string Parent, string Child, int ChildId, decimal Qty, string? RequiredFactory)[] edges)
    {
        var byParent = edges
            .GroupBy(e => e.Parent, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<BomComponent>)g
                    .Select(e => new BomComponent(e.Child, e.ChildId, e.Qty)
                    {
                        ChildRequiredFactoryCode = e.RequiredFactory,
                    })
                    .ToList(),
                StringComparer.Ordinal);

        return new BomStructure(byParent, new Dictionary<string, bool>(StringComparer.Ordinal))
        {
            FactoryIdByCode = factoryMap ?? new Dictionary<string, int>(StringComparer.Ordinal),
        };
    }

    /// <summary>根订单厂 = TJ(6)。</summary>
    private static BomOrderDemand Demand(string rootCode) =>
        new(RootOrderId: 101, RootMaterialId: 1, RootMaterialCode: rootCode, FactoryId: 6, RootQty: 10m);

    private static BomLevelNode NodeOf(string code, IReadOnlyList<BomLevelNode> nodes)
        => nodes.Single(n => n.MaterialCode == code);

    [Fact(DisplayName = "O65-① 子件按边上给的应归属厂改厂")]
    public void 子件按应归属厂改厂()
    {
        var s = Structure(FactoryMap, ("A", "B", 2, 1m, "BJ"));

        var nodes = Service.ExplodeOrderStructure(s, Demand("A"));

        var b = NodeOf("B", nodes);
        b.FactoryId.Should().Be(2, "边上写 BJ，就该在 BJ 建需求（此前会继承根订单的 6）");
        b.FactorySource.Should().Be(BomFactorySource.Rerouted);
        NodeOf("A", nodes).FactoryId.Should().Be(6, "根节点始终用订单厂");
    }

    [Fact(DisplayName = "O65-② 应归属厂与父厂相同 ⇒ 不算改厂")]
    public void 应归属厂等于父厂时不标记改厂()
    {
        var s = Structure(FactoryMap, ("A", "B", 2, 1m, "TJ"));

        var b = NodeOf("B", Service.ExplodeOrderStructure(s, Demand("A")));

        b.FactoryId.Should().Be(6);
        b.FactorySource.Should().Be(BomFactorySource.SameAsParent, "同厂不是「改厂」，不能虚报");
    }

    [Fact(DisplayName = "O65-③ 厂码在主档查无 ⇒ 保持父厂并标记兜底")]
    public void 厂码查无时保持父厂并标记()
    {
        var s = Structure(FactoryMap, ("A", "B", 2, 1m, "XX"));

        var b = NodeOf("B", Service.ExplodeOrderStructure(s, Demand("A")));

        b.FactoryId.Should().Be(6, "解析不出就保持父厂，不凭空造厂");
        b.FactorySource.Should().Be(BomFactorySource.UnknownFallback, "查无必须被标记出来，不能与「同厂」混淆");
    }

    [Fact(DisplayName = "O65-④ 空值保持父厂并标记缺值")]
    public void 空值保持父厂并标记缺值()
    {
        var s = Structure(FactoryMap, ("A", "B", 2, 1m, null));

        var b = NodeOf("B", Service.ExplodeOrderStructure(s, Demand("A")));

        b.FactoryId.Should().Be(6);
        b.FactorySource.Should().Be(BomFactorySource.MissingFallback, "走到展开说明该子件要自制，空值即缺值");
    }

    [Fact(DisplayName = "O65-⑤ 孙件继承父件已解析的厂，不是根订单厂")]
    public void 孙件继承父件已解析的厂()
    {
        // A(订单厂 TJ=6) → B(BJ=2) → C(边上没写)
        var s = Structure(FactoryMap,
            ("A", "B", 2, 1m, "BJ"),
            ("B", "C", 3, 1m, null));

        var nodes = Service.ExplodeOrderStructure(s, Demand("A"));

        NodeOf("B", nodes).FactoryId.Should().Be(2);
        NodeOf("C", nodes).FactoryId.Should().Be(2, "继承的是 B 的厂，不是根订单的 6");
        NodeOf("C", nodes).FactorySource.Should().Be(BomFactorySource.MissingFallback);
    }

    [Fact(DisplayName = "O65-⑥ 多条父边给出不同应归属厂 ⇒ 标记冲突")]
    public void 多父边给出不同厂时标记冲突()
    {
        // A → B(BJ=2)、A → C(TJ=6)，B 与 C 都指向 D，但各自要求不同的厂。
        var s = Structure(FactoryMap,
            ("A", "B", 2, 1m, "BJ"),
            ("A", "C", 3, 1m, "TJ"),
            ("B", "D", 4, 1m, "BJ"),
            ("C", "D", 4, 1m, "TJ"));

        var d = NodeOf("D", Service.ExplodeOrderStructure(s, Demand("A")));

        d.FactorySource.Should().Be(BomFactorySource.ConflictFirstParentWins,
            "节点按物料唯一，两厂各建一份需求本结构表达不了 —— 必须被标记，不能静默取一个");
    }

    [Fact(DisplayName = "O65-⑧ 采购件空厂是正常态，不得计入缺值")]
    public void 采购件空厂不计缺值()
    {
        // B 在 ERP 里是采购件（SupplyMode=PURCHASE），边上自然没有制造厂。
        var s = StructureWithPurchased(
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["B"] = true },
            ("A", "B", 2, 1m, null));

        var b = NodeOf("B", Service.ExplodeOrderStructure(s, Demand("A")));

        b.FactorySource.Should().Be(BomFactorySource.PurchasedNoFactory,
            "采购件不制造、无制造厂可言 —— 若记成缺值，39.8 万采购件会把告警淹没");
        b.FactorySource.Should().NotBe(BomFactorySource.MissingFallback);
    }

    private static BomStructure StructureWithPurchased(
        IReadOnlyDictionary<string, bool> purchased,
        params (string Parent, string Child, int ChildId, decimal Qty, string? RequiredFactory)[] edges)
    {
        var byParent = edges
            .GroupBy(e => e.Parent, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<BomComponent>)g
                    .Select(e => new BomComponent(e.Child, e.ChildId, e.Qty)
                    {
                        ChildRequiredFactoryCode = e.RequiredFactory,
                    })
                    .ToList(),
                StringComparer.Ordinal);

        return new BomStructure(byParent, purchased) { FactoryIdByCode = FactoryMap };
    }

    [Fact(DisplayName = "O65-⑦ 未给工厂主档 ⇒ 一律兜底且不改厂（回归保护）")]
    public void 无工厂主档时全部保持父厂()
    {
        var s = Structure(null, ("A", "B", 2, 1m, "BJ"), ("B", "C", 3, 1m, "TJ"));

        var nodes = Service.ExplodeOrderStructure(s, Demand("A"));

        nodes.Should().OnlyContain(n => n.FactoryId == 6, "主档缺失时不得臆造工厂");
        NodeOf("B", nodes).FactorySource.Should().Be(BomFactorySource.UnknownFallback);
    }
}