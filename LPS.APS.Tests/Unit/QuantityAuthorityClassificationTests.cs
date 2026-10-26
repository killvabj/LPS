using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 数量权威分类（`QuantityAuthority`，1号位 2026-10-22 回执 §四点名落地）。
///
/// 要保住的是**一条区分**：「有 PI 号」≠「PI 是数量权威」。
/// 客户订单行同样携带 `MTS_InstructionNo`（ODS 视图 `v_APS_SalesOrder` 分支2 的刻意设计），
/// 但其数量边界取 `[Order].Quantity` ⇒ 必须落 `ORDER_ONLY`，不能按「有 PI 号」进 PI 法定量门禁。
/// 判据＝本 Run 是否载入该 PI 号的权威事实（`PiRemainingFacts`，与 1号位 门禁消费的同一份）。
/// </summary>
public class QuantityAuthorityClassificationTests
{
    private static LogicalProductionDemand Demand(string? pi)
        => new() { ProductionInstructionNo = pi };

    private static PiRemainingFact Fact(string pi)
        => new() { ProductionInstructionNo = pi };

    [Fact]
    public void 挂PI号且命中权威事实_MTS_PI()
    {
        var demands = new[] { Demand("PI-1") };

        var (mtsPi, orderOnly, none, piFacts) =
            PeggingOrchestrator.ClassifyQuantityAuthority(demands, new[] { Fact("PI-1") });

        demands[0].QuantityAuthority.Should().Be(QuantityAuthorityValues.MtsPi);
        (mtsPi, orderOnly, none, piFacts).Should().Be((1, 0, 0, 1));
    }

    [Fact]
    public void 挂PI号但无权威事实_ORDER_ONLY且保留PI号()
    {
        // 典型：B-01 身份闸门剔掉的客户订单行 —— 需求侧仍带 PI 号，事实侧不认。
        var demands = new[] { Demand("PI-SO") };

        var (mtsPi, orderOnly, none, _) =
            PeggingOrchestrator.ClassifyQuantityAuthority(demands, new[] { Fact("PI-1") });

        demands[0].QuantityAuthority.Should().Be(QuantityAuthorityValues.OrderOnly);
        demands[0].ProductionInstructionNo.Should().Be("PI-SO", "ORDER_ONLY 只去掉数量权威语义，PI 号保留");
        (mtsPi, orderOnly, none).Should().Be((0, 1, 0));
    }

    [Fact]
    public void 无PI号_NONE()
    {
        var demands = new[] { Demand(null) };

        var (mtsPi, orderOnly, none, _) =
            PeggingOrchestrator.ClassifyQuantityAuthority(demands, new[] { Fact("PI-1") });

        demands[0].QuantityAuthority.Should().Be(QuantityAuthorityValues.None);
        (mtsPi, orderOnly, none).Should().Be((0, 0, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白PI号算无PI_NONE而不是ORDER_ONLY(string pi)
    {
        var demands = new[] { Demand(pi) };

        PeggingOrchestrator.ClassifyQuantityAuthority(demands, Array.Empty<PiRemainingFact>());

        demands[0].QuantityAuthority.Should().Be(QuantityAuthorityValues.None);
    }

    [Fact]
    public void 同一PI多条事实_PiFactCount按号去重且不影响分档()
    {
        var demands = new[] { Demand("PI-1") };

        var (_, _, _, piFacts) = PeggingOrchestrator.ClassifyQuantityAuthority(
            demands, new[] { Fact("PI-1"), Fact("PI-1"), Fact("PI-2") });

        piFacts.Should().Be(2);
    }

    [Fact]
    public void PI号比对大小写敏感_不做归一()
    {
        // 契约：逐字大写、不归一（1号位 侧 StringComparison.Ordinal）。
        var demands = new[] { Demand("pi-1") };

        PeggingOrchestrator.ClassifyQuantityAuthority(demands, new[] { Fact("PI-1") });

        demands[0].QuantityAuthority.Should().Be(QuantityAuthorityValues.OrderOnly);
    }

    [Fact]
    public void 取值与一号位契约逐字一致()
    {
        QuantityAuthorityValues.MtsPi.Should().Be("MTS_PI");
        QuantityAuthorityValues.OrderOnly.Should().Be("ORDER_ONLY");
        QuantityAuthorityValues.None.Should().Be("NONE");
    }

    [Fact]
    public void 空需求集_不抛且各档为0()
    {
        var (mtsPi, orderOnly, none, piFacts) = PeggingOrchestrator.ClassifyQuantityAuthority(
            Array.Empty<LogicalProductionDemand>(), new[] { Fact("PI-1") });

        (mtsPi, orderOnly, none, piFacts).Should().Be((0, 0, 0, 1));
    }
}
