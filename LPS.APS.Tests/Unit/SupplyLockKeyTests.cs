using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R02-O02 T01（2026-11-10）：供给物理锁键统一生成函数。
/// 被测对象 = <see cref="PeggingOrchestrator.BuildSupplyLockKey"/>（纯函数）。
///
/// 【为什么必须有这组用例】改动前 `SupplyLedgerEntry.SupplyKey` **全仓零赋值点**、恒为 `""`，
/// 而 `DemandSupplyHardLock` 今天 **0 行** ⇒ 键写错了在真实数据上**完全看不出来**。
/// 所以判定锚只能放在纯函数上，且必须逐条断言 0号位 在 T01 里点名的约束：
///   类型命名空间、`Material|Factory` 不得单独成键、`PHY:` 合法、`SH` 不得裸单号、
///   同键必须唯一指向同一物理供给及其物料/工厂、键长 ≤200 且超长不截断、空键不可作正式锁。
/// </summary>
public class SupplyLockKeyTests
{
    private static string? Key(SupplySourceType t, string? physical, int materialId = 1001, int factoryId = 3)
        => PeggingOrchestrator.BuildSupplyLockKey(t, physical, materialId, factoryId);

    // ── 命名空间 ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "T01 库存：沿用 PHY 前缀（0号位 明示不强制改 INV），并补物料")]
    public void Inventory_KeepsPhyNamespace_AndCarriesMaterial()
    {
        var phy = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 12345, null, "060594", 3);

        var key = Key(SupplySourceType.INVENTORY, phy, materialId: 777);

        Assert.NotNull(key);
        Assert.StartsWith("PHY:", key!);
        Assert.EndsWith(":777", key);          // 物料必须进键，否则同仓同厂不同物料会串锁
        Assert.Contains(phy, key);
    }

    [Fact(DisplayName = "T01 生产指示：PI 命名空间")]
    public void Wip_UsesPiNamespace()
    {
        var key = Key(SupplySourceType.WIP, "PI:17226504", materialId: 1001, factoryId: 3);

        Assert.NotNull(key);
        Assert.StartsWith("PI:17226504", key!);
    }

    [Fact(DisplayName = "T01 采购在途：PO 命名空间")]
    public void Pipeline_UsesPoNamespace()
    {
        var key = Key(SupplySourceType.PIPELINE, "PO:4500001234:1", materialId: 1001, factoryId: 3);

        Assert.NotNull(key);
        Assert.StartsWith("PO:4500001234:1", key!);
    }

    [Fact(DisplayName = "T01 厂间调拨：SH 不得裸单号（0号位 明令）")]
    public void InterFactory_ShIsNamespaced_NotBareNumber()
    {
        const string shipmentNo = "SH20261108001";

        var key = Key(SupplySourceType.INTER_FACTORY_ORDER, shipmentNo, materialId: 1001, factoryId: 6);

        Assert.NotNull(key);
        Assert.NotEqual(shipmentNo, key);                 // 裸单号就是错的
        Assert.StartsWith("SH:", key!);                   // 必须带类型命名空间
        Assert.Contains(shipmentNo, key);                 // 单号本身仍要保留以便追溯
    }

    [Fact(DisplayName = "T01 厂间调拨：重复加前缀不叠加（幂等）")]
    public void InterFactory_AlreadyPrefixed_NotDoublePrefixed()
    {
        var key = Key(SupplySourceType.INTER_FACTORY_ORDER, "SH:SH20261108001", materialId: 1001, factoryId: 6);

        Assert.NotNull(key);
        Assert.StartsWith("SH:SH20261108001", key!);
        Assert.DoesNotContain("SH:SH:", key);
    }

    // ── 空键 / 类型错 ────────────────────────────────────────────────────────

    [Fact(DisplayName = "T01 虚拟供给无物理身份 ⇒ 不开锁（0号位 Q2：不建跨轮持久锁）")]
    public void VirtualSupply_WithoutPhysicalIdentity_ReturnsNull()
    {
        Assert.Null(Key(SupplySourceType.UPSTREAM_DOMAIN_PRODUCTION, "UPSTREAM_999473_1001_20261110093000"));
        Assert.Null(Key(SupplySourceType.NEW_REQUIREMENT, "ANY"));
        Assert.Null(Key(SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER, "ANY"));
    }

    [Fact(DisplayName = "T01 空物理来源 ⇒ 返回 null，但不得连坐其它供给（由调用侧只留计数）")]
    public void EmptyPhysicalKey_ReturnsNull()
    {
        Assert.Null(Key(SupplySourceType.INVENTORY, null));
        Assert.Null(Key(SupplySourceType.INVENTORY, "   "));
        Assert.Null(Key(SupplySourceType.PIPELINE, ""));   // 无采购单号的在途
    }

    [Fact(DisplayName = "T01 前缀对、类型错 ⇒ 返回 null，绝不猜一个键出来")]
    public void NamespaceTypeMismatch_ReturnsNull()
    {
        // 库存却给了 PI 前缀 / 生产指示却给了 PHY 前缀 / 在途却给了 PI 前缀
        Assert.Null(Key(SupplySourceType.INVENTORY, "PI:17226504"));
        Assert.Null(Key(SupplySourceType.WIP, "PHY:ERP:1:-:W1:3"));
        Assert.Null(Key(SupplySourceType.PIPELINE, "PI:17226504"));
    }

    // ── 结构唯一性（0号位：不得凭「当前样本 0 冲突」认定结构唯一）────────────────

    [Fact(DisplayName = "T01 同物理来源、不同物料 ⇒ 键必须不同（否则同源两物料串锁）")]
    public void SamePhysical_DifferentMaterial_KeysDiffer()
    {
        var a = Key(SupplySourceType.WIP, "PI:17226504", materialId: 1001, factoryId: 3);
        var b = Key(SupplySourceType.WIP, "PI:17226504", materialId: 2002, factoryId: 3);

        Assert.NotEqual(a, b);
    }

    [Fact(DisplayName = "T01 同物理来源、不同工厂 ⇒ 键必须不同（否则同号跨厂串锁）")]
    public void SamePhysical_DifferentFactory_KeysDiffer()
    {
        var a = Key(SupplySourceType.INTER_FACTORY_ORDER, "SH001", materialId: 1001, factoryId: 3);
        var b = Key(SupplySourceType.INTER_FACTORY_ORDER, "SH001", materialId: 1001, factoryId: 6);

        Assert.NotEqual(a, b);
    }

    [Fact(DisplayName = "T01 同输入重复调用 ⇒ 同一键（写/读/释放须可比对）")]
    public void SameInput_IsDeterministic()
    {
        var a = Key(SupplySourceType.INVENTORY, "PHY:ERP:12345:-:060594:3", materialId: 777, factoryId: 3);
        var b = Key(SupplySourceType.INVENTORY, "PHY:ERP:12345:-:060594:3", materialId: 777, factoryId: 3);

        Assert.Equal(a, b);
    }

    [Fact(DisplayName = "T01 Material|Factory 本身不得作 SupplyKey（0号位 明令）")]
    public void Key_IsNotBareMaterialOrFactory()
    {
        var key = Key(SupplySourceType.WIP, "PI:17226504", materialId: 1001, factoryId: 3);

        Assert.NotEqual("1001|3", key);
        Assert.DoesNotContain("|", key!);   // 池分桶键才用 `|`，锁键不用
    }

    // ── 键长 ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "T01 键长上限 200")]
    public void MaxLength_Is200()
    {
        Assert.Equal(200, PeggingOrchestrator.SupplyKeyMaxLength);
    }

    [Fact(DisplayName = "T01 正常长度键 ≤200")]
    public void NormalKey_WithinLimit()
    {
        var phy = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 2786602, 1992192, "990106-ABCDEF", 6);
        var key = Key(SupplySourceType.INVENTORY, phy, materialId: 675823, factoryId: 6);

        Assert.NotNull(key);
        Assert.True(key!.Length <= PeggingOrchestrator.SupplyKeyMaxLength);
    }

    [Fact(DisplayName = "T01 超长键必须抛错，不得截断（截断会让两条不同供给撞成一把锁）")]
    public void OversizedKey_Throws_NotTruncated()
    {
        var huge = "PI:" + new string('9', PeggingOrchestrator.SupplyKeyMaxLength);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Key(SupplySourceType.WIP, huge, materialId: 1001, factoryId: 3));

        Assert.Contains("200", ex.Message);
    }
}