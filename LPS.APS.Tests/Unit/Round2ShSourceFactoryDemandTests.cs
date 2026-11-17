using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A（<c>AE-001</c> Round2，2026-11-13）：SH 整单源厂履约需求的判定口。
///
/// <para><b>断言的目的是这四条，不是「跑得过」：</b></para>
/// <list type="number">
///   <item><b>自消费不算被承接</b>：承接到该 SH 的根订单若**只有它自己**，不得判为 Round2 ——
///         这是 `CurrentOrderNo` 判据恒错的直接后果（该列对 `INTER_FACTORY_ORDER` 恒等于
///         `SupplyDocumentNo`，用它判会恒得「自消费」）。</item>
///   <item><b>整单唯一</b>：一张 SH 被多个上层承接时，只生成**一条**需求，数量取 `Order.Quantity`（整单），
///         不按上层分配比例拆成多条。</item>
///   <item><b>需用时间取 MIN</b>：上层交期早晚不一时取**最早**。取 MAX 会让最早那层必然迟到，
///         而一张 SH 只许一条源厂需求 ⇒ MIN 是唯一不新增违约的取值。</item>
///   <item><b>无上层取自身</b>：孤儿 SH（无任何该号的 Allocation）取自身交期，不因「孤儿」取消履约义务。</item>
/// </list>
///
/// <para>⚠ 本测试只覆盖**判定口**（<see cref="PeggingOrchestrator.ResolveShUpperRoots"/> /
/// <see cref="PeggingOrchestrator.ResolveShRequiredTime"/>）。「整单数量取 `OrderQuantity`」与
/// 「源厂需求不得消费自己那张 SH」两处由代码注释钉住，真实条数看运行期
/// <c>[Pegging][A-R2] SH 整单源厂需求</c> 日志（改前该日志根本不存在）。</para>
/// </summary>
public class Round2ShSourceFactoryDemandTests
{
    private const long ShOrderId = 7001;   // SH 自身那张订单
    private const long RootA = 8001;       // 上层需求 A
    private const long RootB = 8002;       // 上层需求 B

    [Fact]
    public void 只有自循环时_不得判为被真实上层承接()
    {
        // 承接它的根订单就是它自己 ⇒ Round2 不应生成（否则自消费被当成履约链）。
        var roots = PeggingOrchestrator.ResolveShUpperRoots(ShOrderId, new[] { ShOrderId });

        roots.Should().BeEmpty("自消费不构成「被合法上层履约链承接」");
    }

    [Fact]
    public void 被一张上层承接_只记该上层()
    {
        var roots = PeggingOrchestrator.ResolveShUpperRoots(ShOrderId, new[] { RootA });

        roots.Should().Equal(new[] { RootA });
    }

    [Fact]
    public void 被多张上层承接_整单只生成一条需求_不按比例拆分()
    {
        // 上层 A/B 各自扣了一部分，但 SH 的源厂义务是**整单一份**。
        // 判定口返回全部上层（供取 MIN），生成条数由调用方按 SH 号去重 —— 一处 SH 一行。
        var roots = PeggingOrchestrator.ResolveShUpperRoots(ShOrderId, new[] { RootA, RootB });

        roots.Should().Equal(new[] { RootA, RootB });
        roots.Should().HaveCount(2, "上层有几张都只影响需用时间，不影响生成条数");
    }

    [Fact]
    public void 自循环与真实承接混在一起时_只保留真实上层()
    {
        var roots = PeggingOrchestrator.ResolveShUpperRoots(ShOrderId, new[] { ShOrderId, RootA, RootB });

        roots.Should().Equal(new[] { RootA, RootB });
    }

    [Fact]
    public void 同一上层重复承接_去重后只算一张()
    {
        // 同一需求跨多条 SH 取满 / 同一 SH 被同一需求多次分配，都会产生重复根订单 Id。
        var roots = PeggingOrchestrator.ResolveShUpperRoots(ShOrderId, new[] { RootA, RootA, RootA });

        roots.Should().Equal(new[] { RootA });
    }

    [Fact]
    public void 需用时间取上层最早交期_不取最晚()
    {
        var due = new Dictionary<long, DateTime>
        {
            [RootA] = new(2026, 11, 20),
            [RootB] = new(2026, 11, 25),
        };

        var required = PeggingOrchestrator.ResolveShRequiredTime(
            new[] { RootA, RootB }, due, selfDue: new DateTime(2026, 12, 1));

        required.Should().Be(new DateTime(2026, 11, 20),
            "取 MAX 会让最早那层（11-20）必然迟到；MIN 只产生「早于需要」");
    }

    [Fact]
    public void 上层交期未装载到_回退自身交期_不返回默认时间()
    {
        // 上层根订单 Id 拿到了，但交期没查到（异常/跨 PV）。不得返回 DateTime.MinValue 把需求压到无穷早。
        var required = PeggingOrchestrator.ResolveShRequiredTime(
            new[] { RootA }, new Dictionary<long, DateTime>(), selfDue: new DateTime(2026, 12, 1));

        required.Should().Be(new DateTime(2026, 12, 1));
    }

    [Fact]
    public void 孤儿SH_无上层时取自身交期_不因孤儿取消履约义务()
    {
        var required = PeggingOrchestrator.ResolveShRequiredTime(
            Array.Empty<long>(), new Dictionary<long, DateTime>(), selfDue: new DateTime(2026, 12, 1));

        required.Should().Be(new DateTime(2026, 12, 1));
    }

    [Fact]
    public void 源厂不可信已知时_判UNKNOWN且不得用目标厂顶替()
    {
        // 冻结逐字：SourceFactoryId 未知必须 NULL，不得用目标厂/当前时点/订单交期顶替。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(sourceFactoryId: null, targetFactoryId: 2);

        category.Should().Be(UnresolvedCategoryCodes.Unknown);
    }

    [Fact]
    public void 源厂与目标厂不同厂且两侧Domain均未解析_判UNKNOWN不放行()
    {
        // CF-001 要求「由 DomainDefinition 证明同一有效 Domain」。
        // 查不出（无匹配或歧义）＝ 证明不了 ⇒ 不得当成同域放行，按 UNKNOWN 保守处理。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(
            sourceFactoryId: 1, targetFactoryId: 2,
            sourceDomainKey: null, targetDomainKey: null);

        category.Should().Be(UnresolvedCategoryCodes.Unknown);
    }

    [Fact]
    public void 源厂与目标厂不同厂但同属FAMILY域_判可履约()
    {
        // ScopeType='FAMILY' 的产品族域覆盖该族下所有工厂 ⇒ 跨厂仍同域，可正常生成源厂 Demand。
        // 这是「同厂==同域」近似之外的真正精确判据。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(
            sourceFactoryId: 1, targetFactoryId: 2,
            sourceDomainKey: "FAMILY_CYL", targetDomainKey: "FAMILY_CYL");

        category.Should().BeNull();
    }

    [Fact]
    public void 源厂与目标厂分部不同Domain_判跨域未来制造()
    {
        // 两侧均解析成功且不同 ⇒ 已证跨 Domain；V1 不展开未来制造，记未闭合、不造伪源厂 Demand。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(
            sourceFactoryId: 3, targetFactoryId: 6,
            sourceDomainKey: "BJ_FAMILY_FRL", targetDomainKey: "TJ_FAMILY_FRL");

        category.Should().Be(UnresolvedCategoryCodes.ShCrossDomainFuture);
    }

    [Fact]
    public void 只有一侧解析成功_仍判UNKNOWN不冒充同域()
    {
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(
            sourceFactoryId: 1, targetFactoryId: 2,
            sourceDomainKey: "FAMILY_CYL", targetDomainKey: null);

        category.Should().Be(UnresolvedCategoryCodes.Unknown);
    }

    [Fact]
    public void 同一工厂时_即使两侧Domain都解析不出也判可履约()
    {
        // 同一工厂不可能落在两个不同 Domain ⇒ 恒同域，无需查表。
        // 这条是今天的主路径（全库源厂 100% 等于目标厂），不能被 Domain 解析失败连坐。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(
            sourceFactoryId: 2, targetFactoryId: 2,
            sourceDomainKey: null, targetDomainKey: null);

        category.Should().BeNull();
    }

    [Fact]
    public void 源厂与目标厂相同_正常生成源厂需求不记未闭合()
    {
        // 今天全库源厂 100% 等于目标厂（ERP 事实、已裁）⇒ 该分支可履约。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(sourceFactoryId: 2, targetFactoryId: 2);

        category.Should().BeNull();
    }

    [Fact]
    public void 源厂为零与目标厂为零_仍视为可履约()
    {
        // 防御：0 不是「未知」的合法表达，只有 null 才是。
        var category = PeggingOrchestrator.ClassifyShUnresolvedCategory(sourceFactoryId: 0, targetFactoryId: 0);

        category.Should().BeNull();
    }
}
