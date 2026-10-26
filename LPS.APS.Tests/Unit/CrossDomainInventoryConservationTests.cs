using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R03-02（Candidate 侧，「甲」口径）：跨域库存占用扣减的算术与键一致性。
///
/// 断言目的＝**它的声明用途**：
///   ① **总量守恒**：同键多批时，扣掉的合计 = min(外部占用, 本域同键可用量)。
///      这是本组最重要的一条 —— 若实现成「每批都扣一遍总量」，3 批各 100 遇外部占 150 会把 450 扣光，
///      候选凭空少 300，比不扣更危险（少排）。
///   ② **不超扣**：任一批扣后不为负，且扣减量不超过该批可用量。
///   ③ **扣不完要留痕**：外部占用大于本域可用量时，差额必须能被识别（`CrossDomainAggregateUnappliedQty`），
///      不能静默吞掉（那意味着供给快照与其它域分配不是同一时点）。
///   ④ **装载侧与入池侧键必须同源**：键格式一旦两边各写一份，扣减会静默失效（不报错、不扣量）。
///
/// 纯函数、零依赖、零真实库。
/// </summary>
public class CrossDomainInventoryConservationTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // ① 键一致性
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 聚合键含物料_工厂_类型()
    {
        var key = PeggingOrchestrator.BuildAggregateConsumptionKey(
            "RAW-001", "CN", SupplySourceType.INVENTORY);

        key.Should().Contain("RAW-001");
        key.Should().Contain("CN");
        key.Should().Contain(nameof(SupplySourceType.INVENTORY));
    }

    [Fact]
    public void 聚合键_类型不同即键不同()
    {
        var inventory = PeggingOrchestrator.BuildAggregateConsumptionKey(
            "RAW-001", "CN", SupplySourceType.INVENTORY);
        var pipeline = PeggingOrchestrator.BuildAggregateConsumptionKey(
            "RAW-001", "CN", SupplySourceType.PIPELINE);

        inventory.Should().NotBe(pipeline,
            "库存各批可用时间同为立即可用、采购逐批时点不同；类型混键会把采购占用挪到库存上");
    }

    [Fact]
    public void 聚合键_工厂为空时用占位符且稳定()
    {
        var a = PeggingOrchestrator.BuildAggregateConsumptionKey("RAW-001", null, SupplySourceType.INVENTORY);
        var b = PeggingOrchestrator.BuildAggregateConsumptionKey("RAW-001", null, SupplySourceType.INVENTORY);

        a.Should().Be(b);
        a.Should().NotBe(PeggingOrchestrator.BuildAggregateConsumptionKey(
            "RAW-001", "CN", SupplySourceType.INVENTORY));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ② 总量守恒（核心）
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 同键多批_外部占用只扣一次_不得每批都扣总量()
    {
        // 3 批各 100，外部已占用 150 ⇒ 本域应剩 150（100 + 50 + 0），不是 0。
        var deductions = PeggingOrchestrator.AllocateAggregateDeduction(
            150m, new[] { 100m, 100m, 100m });

        deductions.Should().Equal(new[] { 100m, 50m, 0m });
        deductions.Sum().Should().Be(150m, "扣掉的合计必须等于外部占用量，多扣就是凭空少排");
    }

    [Fact]
    public void 外部占用小于首批_只扣首批()
    {
        var deductions = PeggingOrchestrator.AllocateAggregateDeduction(
            40m, new[] { 100m, 100m });

        deductions.Should().Equal(new[] { 40m, 0m });
    }

    [Fact]
    public void 外部占用超过本域可用量_扣到零为止_差额可被识别()
    {
        var available = new[] { 100m, 100m, 100m };
        var deductions = PeggingOrchestrator.AllocateAggregateDeduction(350m, available);

        deductions.Should().Equal(new[] { 100m, 100m, 100m });
        deductions.Sum().Should().Be(300m, "本域只有 300，扣不出 350；差额须由调用侧计入 UnappliedQty 留痕");
        (350m - deductions.Sum()).Should().Be(50m);
    }

    [Fact]
    public void 单批部分占用()
    {
        PeggingOrchestrator.AllocateAggregateDeduction(12.5m, new[] { 100m })
            .Should().Equal(new[] { 12.5m });
    }

    [Fact]
    public void 跳过零或负可用量的批次_不把扣减堆到空批上()
    {
        var deductions = PeggingOrchestrator.AllocateAggregateDeduction(
            50m, new[] { 0m, 100m, 0m });

        deductions.Should().Equal(new[] { 0m, 50m, 0m });
    }

    [Fact]
    public void 任一批扣减不超过其可用量且扣后不为负()
    {
        var available = new[] { 30m, 0m, 70m, 5m };
        var deductions = PeggingOrchestrator.AllocateAggregateDeduction(90m, available);

        for (var i = 0; i < available.Length; i++)
        {
            deductions[i].Should().BeGreaterThanOrEqualTo(0m);
            deductions[i].Should().BeLessThanOrEqualTo(available[i]);
            (available[i] - deductions[i]).Should().BeGreaterThanOrEqualTo(0m);
        }
        deductions.Sum().Should().Be(90m);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ③ 退化输入
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void 无外部占用时全零()
    {
        PeggingOrchestrator.AllocateAggregateDeduction(0m, new[] { 100m, 100m })
            .Should().Equal(new[] { 0m, 0m });
    }

    [Fact]
    public void 外部占用为负时全零_不得反向加量()
    {
        PeggingOrchestrator.AllocateAggregateDeduction(-10m, new[] { 100m })
            .Should().Equal(new[] { 0m });
    }

    [Fact]
    public void 本域无同键条目时返回空数组_不抛异常()
    {
        PeggingOrchestrator.AllocateAggregateDeduction(500m, Array.Empty<decimal>())
            .Should().BeEmpty();
    }
}
