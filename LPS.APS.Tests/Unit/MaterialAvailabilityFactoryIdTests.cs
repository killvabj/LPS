using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// O62（2026-11-10）：`MaterialAvailabilitySlice.FactoryId` 必须取**工厂主键**，
/// 不得用 `int.TryParse(FactoryCode)` 反解文本厂码。
///
/// 【缺陷原形】`FactoryCode` 的值域是 `BJ`/`TJ`/`CN`/`CN6厂`/`SH`（`Factory.Code`），
///   `int.TryParse` 对这些文本**必然失败**并静默得 0 ⇒ 该字段 100% 为 0。
///   当时该字段无任何消费者（`MaterialAvailabilitySegment` 根本没有工厂列），
///   属「沉默列」缺陷：值确实错，但没人读，所以既没咬排程、也查不出来。
///
/// 【本用例断言的是声明目的】不是「函数被调用过」，而是：
///   ① 传入真实 `FactoryId` 后，切片带出的是它本身，**不是 0**；
///   ② 即使同时给了无法解析的文本厂码，也不影响结果（说明已经不再反解）。
/// </summary>
public class MaterialAvailabilityFactoryIdTests
{
    private static SupplyAllocationItem Alloc(
        string factoryCode, int factoryId, decimal qty = 10m, SupplySourceType type = SupplySourceType.INVENTORY)
        => new()
        {
            AllocationSequence = 1,
            DemandKey = "ORDER_1_MAT_3",
            DemandQuantity = qty,
            SupplyMaterialId = 1001,
            AllocatedQuantity = qty,
            SourceType = type,
            SourceReference = "REF",
            FactoryCode = factoryCode,
            FactoryId = factoryId,
            AvailableAt = new DateTime(2026, 11, 10, 8, 0, 0)
        };

    private static PeggingResultVoucher Voucher(params SupplyAllocationItem[] allocations)
    {
        var v = new PeggingResultVoucher();
        foreach (var a in allocations) v.SupplyAllocations.Add(a);
        return v;
    }

    [Theory(DisplayName = "O62 文本厂码不再被反解：BJ/CN6厂/SH 都带出真实工厂主键")]
    [InlineData("BJ", 3)]
    [InlineData("TJ", 6)]
    [InlineData("CN", 2)]
    [InlineData("SH", 5)]
    [InlineData("CN6厂", 4)]
    public void TextFactoryCode_DoesNotZeroOutFactoryId(string factoryCode, int factoryId)
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(Voucher(Alloc(factoryCode, factoryId)));

        var slice = Assert.Single(slices);
        Assert.Equal(factoryId, slice.FactoryId);
        Assert.NotEqual(0, slice.FactoryId);   // 缺陷原形：恒 0
    }

    [Fact(DisplayName = "O62 对照：若仍用 int.TryParse 反解文本厂码，必然得 0（证明缺陷确实存在过）")]
    public void Control_TryParseOnTextCode_YieldsZero()
    {
        Assert.False(int.TryParse("BJ", out var parsed));
        Assert.Equal(0, parsed);
    }

    [Fact(DisplayName = "O62 工厂主键逐条独立，不串厂")]
    public void DifferentFactories_KeepTheirOwnId()
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(
            Voucher(Alloc("BJ", 3, 5m), Alloc("TJ", 6, 7m)));

        Assert.Equal(2, slices.Count);
        Assert.Contains(slices, s => s.FactoryId == 3);
        Assert.Contains(slices, s => s.FactoryId == 6);
    }

    [Fact(DisplayName = "O62 缺口标记不进可用性（既有口径不受本次改动影响）")]
    public void NewRequirement_StillExcluded()
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(
            Voucher(Alloc("TJ", 6, 5m, SupplySourceType.NEW_REQUIREMENT)));

        Assert.Empty(slices);
    }
}