using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R03-01/02：同一物理供给跨条目、跨 Domain 不得被重复消费。
/// 被测对象 = <see cref="PeggingOrchestrator.FindPhysicalOverConsumption"/>（纯函数）。
///
/// 断言的是**声明目的**：合计量超物理量必须报；不是「函数被调用过」。
/// </summary>
public class PhysicalSupplyOverConsumptionTests
{
    private static List<string> Check(params (string? Key, decimal Original, decimal Allocated)[] rows)
        => PeggingOrchestrator.FindPhysicalOverConsumption(rows);

    [Fact(DisplayName = "R03-01 同键多条目：各自未超但合计超 ⇒ 必须报")]
    public void SameKey_TwoEntries_EachWithinOwnQty_TotalExceeds_Reports()
    {
        // 同一批物理库存 100，按两个产品族各装载一次（各 100），两个族各消耗 80
        var errors = Check(
            ("INV:1001", 100m, 80m),
            ("INV:1001", 100m, 80m));

        Assert.Single(errors);
        Assert.Contains("INV:1001", errors[0]);
        Assert.Contains("160", errors[0]);   // 合计已分配
        Assert.Contains("100", errors[0]);   // 物理量
    }

    [Fact(DisplayName = "R03-01 逐条自比可通过、合计超 ⇒ 旧口径漏报（回归护栏）")]
    public void PerEntrySelfCheck_WouldPass_GroupCheckStillReports()
    {
        var rows = new (string? Key, decimal Original, decimal Allocated)[]
            { ("PI:17013705", 100m, 60m), ("PI:17013705", 100m, 60m) };

        // 旧口径：逐条 consumed(60) > OriginalQty(100) 为假 ⇒ 0 条错误
        var perEntry = rows.Count(r => r.Item3 > r.Item2);
        Assert.Equal(0, perEntry);

        // 新口径：合计 120 > 100 ⇒ 必须报
        Assert.Single(Check(rows));
    }

    [Fact(DisplayName = "R03-02 跨域守恒：前域消耗后，本域只剩剩余量 ⇒ 合计不超，不报")]
    public void CrossDomain_UpstreamConsumed_RemainderOnly_NoReport()
    {
        // 物理量 100；前域已消耗 70 ⇒ 本域条目被压到 30（SupplyPool.Add 行为），本域再消耗 30
        var errors = Check(("INV:2002", 100m, 30m));
        Assert.Empty(errors);
    }

    [Fact(DisplayName = "R03-02 跨域守恒被突破（未扣减上游消耗）⇒ 合计 130 > 100 必须报")]
    public void CrossDomain_UpstreamNotDeducted_Reports()
    {
        // 前域 70 + 本域 60 = 130，超过物理量 100
        var errors = Check(("INV:2002", 100m, 130m));
        Assert.Single(errors);
    }

    [Fact(DisplayName = "无 PhysicalSourceKey 的虚拟供给不参与物理守恒")]
    public void EntriesWithoutPhysicalKey_AreIgnored()
    {
        var errors = Check(
            (null, 100m, 500m),
            ("", 100m, 500m),
            ("   ", 100m, 500m));
        Assert.Empty(errors);
    }

    [Fact(DisplayName = "不同物理键各自闭合，互不影响")]
    public void DistinctKeys_AreIndependent()
    {
        var errors = Check(
            ("PI:A", 100m, 100m),
            ("PI:B", 100m, 100m));
        Assert.Empty(errors);
    }

    [Fact(DisplayName = "R03-01 物理键＝(来源系统, ERP主档, MES主档, 仓, 厂)，不含产品族与明细Id")]
    public void InventoryPhysicalKey_ContractShape()
    {
        var key = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 12345, null, "060594", 3);

        Assert.Equal("PHY:ERP:12345:-:060594:3", key);
        // 同一物理库存被复制到两个产品族 ⇒ 明细行 Id 不同，但物理键必须相同（这正是原实现漏掉的）
        Assert.Equal(key, PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 12345, null, "060594", 3));
    }

    [Fact(DisplayName = "R03-01 仓码或厂不同 ⇒ 物理键不同，不得合并")]
    public void InventoryPhysicalKey_WarehouseOrFactoryDistinguishes()
    {
        var a = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 1, null, "020498", 3);
        var b = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 1, null, "020799", 3);
        var c = PeggingOrchestrator.BuildInventoryPhysicalKey("ERP", 1, null, "020498", 4);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact(DisplayName = "物理量取组内最大值，不取合计（重复装载不放大容量）")]
    public void PhysicalQty_IsMaxNotSum()
    {
        // 三条同键各 100（重复装载），合计已分配 250
        // 若按 Sum(OriginalQty)=300 判定 ⇒ 250 < 300 漏报；按 Max=100 ⇒ 必须报
        var errors = Check(
            ("SH:S001", 100m, 100m),
            ("SH:S001", 100m, 100m),
            ("SH:S001", 100m, 50m));

        Assert.Single(errors);
    }
}
