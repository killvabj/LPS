using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Scheduling.Solvers;
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

    /// <summary>计划期起点占位（仅 SH 时间待闭合分支使用；本组用例都带 AvailableAt，故不参与断言）。</summary>
    private static readonly DateTime PlanningStart = new(2026, 11, 13, 0, 0, 0);
    private static readonly DateTime PlanningEnd = PlanningStart.AddDays(90);

    [Theory(DisplayName = "O62 文本厂码不再被反解：BJ/CN6厂/SH 都带出真实工厂主键")]
    [InlineData("BJ", 3)]
    [InlineData("TJ", 6)]
    [InlineData("CN", 2)]
    [InlineData("SH", 5)]
    [InlineData("CN6厂", 4)]
    public void TextFactoryCode_DoesNotZeroOutFactoryId(string factoryCode, int factoryId)
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(Voucher(Alloc(factoryCode, factoryId)), PlanningStart);

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
            Voucher(Alloc("BJ", 3, 5m), Alloc("TJ", 6, 7m)), PlanningStart);

        Assert.Equal(2, slices.Count);
        Assert.Contains(slices, s => s.FactoryId == 3);
        Assert.Contains(slices, s => s.FactoryId == 6);
    }

    [Fact(DisplayName = "O62 缺口标记不进可用性（既有口径不受本次改动影响）")]
    public void NewRequirement_StillExcluded()
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(
            Voucher(Alloc("TJ", 6, 5m, SupplySourceType.NEW_REQUIREMENT)), PlanningStart);

        Assert.Empty(slices);
    }

    // ── 2026-11-13 新增：SH「数量已承接、时间待闭合」分支（0号位 实施回复 §4 / 1号位 五问答复 §一） ──

    /// <summary>SH 供给：`AvailableAt` 恒 null（Layer-2 未产时间），这是真实形态。</summary>
    private static SupplyAllocationItem ShAlloc(decimal qty = 80m, int factoryId = 6)
        => new()
        {
            AllocationSequence = 2,
            DemandKey = "ORDER_1_MAT_6",
            DemandQuantity = qty,
            SupplyMaterialId = 1001,
            AllocatedQuantity = qty,
            SourceType = SupplySourceType.INTER_FACTORY_ORDER,
            SourceReference = "O20593091",
            ShippingInstructionNo = "O20593091",
            FactoryCode = "TJ",
            FactoryId = factoryId,
            AvailableAt = null          // ← 关键：SH 没有时间
        };

    [Fact(DisplayName = "SH 有 MaterialRequirementLink：数量计入、时间给计划期起点，Lag 兜下界")]
    public void ShWithNullAvailableAt_WithLink_IsCounted()
    {
        var alloc = ShAlloc(80m);
        var v = Voucher(alloc);
        v.MaterialRequirementLinks.Add(new MaterialRequirementLink
        {
            ConsumerLogicalDemandKey = "CONSUMER",
            ProducerLogicalDemandKey = "PRODUCER",
            ChildDemandKey = alloc.DemandKey,
            ConsumerMaterialId = 1001,
            ProducerMaterialId = 1001,
            RequiredQty = 80m,
            ProducerAllocationSequence = 1,
            LagMinutes = 60m
        });

        var slices = PeggingOrchestrator.BuildMaterialConstraints(v, PlanningStart);

        var slice = Assert.Single(slices);
        Assert.Equal(80m, slice.Quantity);
        Assert.Equal(PlanningStart, slice.AvailableTime);
        Assert.Equal("INTER_FACTORY_ORDER", slice.SourceType);
        Assert.Equal("SH_LINKED", slice.TimeBasis);
    }

    [Fact(DisplayName = "SH 无 link：保留真实数量，时间未定用计划期终点，不得冒充当前")]
    public void ShWithoutLink_PreservesQuantityAndMarksUnresolved()
    {
        var slices = PeggingOrchestrator.BuildMaterialConstraints(
            Voucher(ShAlloc()), PlanningStart, PlanningEnd);

        var slice = Assert.Single(slices);
        Assert.Equal(80m, slice.Quantity);                 // 真实 SH 数量不丢
        Assert.Equal("INTER_FACTORY_ORDER", slice.SourceType);
        Assert.Equal(PlanningEnd, slice.AvailableTime);    // 不是当前
        Assert.Equal("SH_TIME_UNRESOLVED", slice.TimeBasis);
    }

    [Fact(DisplayName = "PIPELINE NULL：保留真实数量，时间未定，不得冒充当前")]
    public void PipelineWithNullAvailableAt_PreservesQuantityAndMarksUnresolved()
    {
        var alloc = Alloc("TJ", 6, 5m, SupplySourceType.PIPELINE);
        alloc.AvailableAt = null;

        var slices = PeggingOrchestrator.BuildMaterialConstraints(Voucher(alloc), PlanningStart, PlanningEnd);

        var slice = Assert.Single(slices);
        Assert.Equal(5m, slice.Quantity);                  // 真实数量不丢
        Assert.Equal("PIPELINE", slice.SourceType);
        Assert.Equal(PlanningEnd, slice.AvailableTime);
        Assert.Equal("UNRESOLVED", slice.TimeBasis);
    }

    [Fact(DisplayName = "O92 跨厂无合法SH：0量切片进父件约束，不被当『料够』")]
    public void ShFulfilmentShortage_AppendsZeroSliceToParentAllocation()
    {
        var v = Voucher();
        v.ShortageMaterialConstraints.Add(new MaterialAvailabilitySlice
        {
            AllocationSequence = 42,
            MaterialId = 1001,
            FactoryId = 3,
            Quantity = 0m,
            AvailableTime = new DateTime(2026, 11, 20),
            SourceType = "SH_MISSING",
            SourceKey = "UCFS_TEST"
        });

        var slices = PeggingOrchestrator.BuildMaterialConstraints(v, PlanningStart);

        var slice = Assert.Single(slices);
        Assert.Equal(42, slice.AllocationSequence);
        Assert.Equal(0m, slice.Quantity);                 // 明确不足，不是"没有约束"
        Assert.Equal("SH_MISSING", slice.SourceType);
    }

    [Fact(DisplayName = "O92 对照：0量切片确实让 1号位 GetMaterialEarliestTime 判 isSufficient=false")]
    public void ZeroQuantitySlice_MakesMaterialInsufficient()
    {
        var constraints = new ConstraintContext();
        constraints.MaterialAvailability[42] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 0m, AvailableTime = new DateTime(2026, 11, 20) }
        };

        var earliest = PhaseFourLocalRepair.GetMaterialEarliestTime(
            42, 10m, constraints, PlanningStart, out var isSufficient);

        Assert.False(isSufficient);                       // 关键：不是"没有约束 ⇒ 默认料够"
        Assert.Equal(new DateTime(2026, 11, 20), earliest);
    }

    [Fact(DisplayName = "S02 库存 AvailableAt=NULL 仍给计划期起点、数量照给")]
    public void NullAvailableInventory_IsKeptAtPlanningStart()
    {
        var alloc = Alloc("TJ", 6, 5m, SupplySourceType.INVENTORY);
        alloc.AvailableAt = null;
        alloc.Commitment = "COMMITTED";
        alloc.Confidence = "CONFIRMED";

        var slices = PeggingOrchestrator.BuildMaterialConstraints(Voucher(alloc), PlanningStart);

        var slice = Assert.Single(slices);
        Assert.Equal(5m, slice.Quantity);
        Assert.Equal(PlanningStart, slice.AvailableTime);
        Assert.Equal("INVENTORY", slice.SourceType);
        Assert.Equal("COMMITTED", slice.Commitment);
        Assert.Equal("CONFIRMED", slice.Confidence);
        Assert.Equal("CURRENT", slice.TimeBasis);
    }

    [Fact(DisplayName = "S02 WIP 不是库存：保留真实数量、时间未定，不得当当前现货")]
    public void NullAvailableWip_PreservesQuantityAndMarksUnresolved()
    {
        var alloc = Alloc("TJ", 6, 5m, SupplySourceType.WIP);
        alloc.AvailableAt = null;

        var slices = PeggingOrchestrator.BuildMaterialConstraints(Voucher(alloc), PlanningStart, PlanningEnd);

        var slice = Assert.Single(slices);
        Assert.Equal(5m, slice.Quantity);                  // 真实 WIP 数量不丢
        Assert.Equal("WIP", slice.SourceType);
        Assert.Equal(PlanningEnd, slice.AvailableTime);
        Assert.Equal("UNRESOLVED", slice.TimeBasis);
    }

    [Fact(DisplayName = "S02 承诺状态随分配透传到 MaterialAvailabilitySlice")]
    public void Commitment_IsPropagatedToSlice()
    {
        var alloc = Alloc("TJ", 6, 5m);
        alloc.Commitment = "NOT_COMMITTED";
        alloc.Confidence = "ESTIMATED";

        var slice = Assert.Single(PeggingOrchestrator.BuildMaterialConstraints(Voucher(alloc), PlanningStart));
        Assert.Equal("NOT_COMMITTED", slice.Commitment);
        Assert.Equal("ESTIMATED", slice.Confidence);
    }
}
