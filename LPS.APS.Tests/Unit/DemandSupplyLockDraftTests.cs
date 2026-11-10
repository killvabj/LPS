using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R02-O02 T03（2026-11-10）：受保护需求的锁草案构造。
/// 被测对象 = <see cref="PeggingOrchestrator.BuildDemandSupplyLockDrafts"/>（纯函数）。
///
/// 【为什么必须先有这组用例】锁表今天 **0 行**，落库后一旦算错，会把受保护订单预留放大、
/// 反过来压掉其它订单的可分配量（`ComputeDemandProtectionCap` 会真的扣）。改错了在真实数据上
/// **看不出来**，判定锚只能放在纯函数上。
///
/// 逐条对应 0号位 T03 原文，断言的是**声明目的**而非「函数被调用过」。
/// </summary>
public class DemandSupplyLockDraftTests
{
    private const string Protected = DemandProtectionTriggerEvaluator.ProtectedStatus;
    private const string Unprotected = DemandProtectionTriggerEvaluator.UnprotectedStatus;

    private static SupplyAllocationItem Alloc(
        long orderId,
        string supplyKey,
        decimal qty,
        int bomLevel = 0,
        string demandKey = "ORDER_1_MAT_3")
        => new()
        {
            AllocationSequence = 1,
            DemandKey = demandKey,
            OrderId = orderId,
            SupplyKey = supplyKey,
            AllocatedQuantity = qty,
            SupplyMaterialId = 1001,
            SourceType = SupplySourceType.WIP,
            FactoryCode = "BJ",
            FactoryId = 3,
            BomLevel = bomLevel,
            AvailableAt = new DateTime(2026, 11, 10, 8, 0, 0)
        };

    private static IReadOnlyDictionary<long, string> Status(params (long OrderId, string Status)[] rows)
        => rows.ToDictionary(r => r.OrderId, r => r.Status);

    // ── 基本：只给受保护订单建锁 ────────────────────────────────────────────

    [Fact(DisplayName = "T03 受保护订单的根需求分配 ⇒ 产一条锁，量＝真实分到的量")]
    public void ProtectedRootAllocation_ProducesLock()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[] { Alloc(orderId: 100, supplyKey: "PI:17226504:1001:3", qty: 60m) },
            Status((100, Protected)));

        var d = Assert.Single(drafts);
        Assert.Equal("PI:17226504:1001:3", d.SupplyKey);
        Assert.Equal("ORDER_1_MAT_3", d.DemandKey);
        Assert.Equal(60m, d.LockedQty);
        Assert.Equal(PeggingOrchestrator.DemandSupplyLockDraft.DemandProtection, d.LockType);
    }

    [Fact(DisplayName = "T03 未受保护订单 ⇒ 一条锁都不产")]
    public void UnprotectedOrder_ProducesNothing()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[] { Alloc(orderId: 100, supplyKey: "PI:1:1001:3", qty: 60m) },
            Status((100, Unprotected)));

        Assert.Empty(drafts);
    }

    [Fact(DisplayName = "T03 订单不在资格表里 ⇒ 按未受保护处理，不产锁")]
    public void OrderMissingFromStatusMap_ProducesNothing()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[] { Alloc(orderId: 999, supplyKey: "PI:1:1001:3", qty: 60m) },
            Status((100, Protected)));

        Assert.Empty(drafts);
    }

    // ── 「不得叠加成两份占用」────────────────────────────────────────────────

    [Fact(DisplayName = "T03 只锁根需求：BOM 下阶分配不产锁（防一份保护放大成多份）")]
    public void ChildLevelAllocations_DoNotProduceLocks()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[]
            {
                Alloc(orderId: 100, supplyKey: "PI:1:1001:3", qty: 60m, bomLevel: 0),
                Alloc(orderId: 100, supplyKey: "PI:2:2002:3", qty: 999m, bomLevel: 1),
                Alloc(orderId: 100, supplyKey: "PHY:ERP:9:-:W1:3:3003", qty: 888m, bomLevel: 2)
            },
            Status((100, Protected)));

        var d = Assert.Single(drafts);
        Assert.Equal(60m, d.LockedQty);            // 只有根需求那 60
        Assert.Equal("PI:1:1001:3", d.SupplyKey);
    }

    [Fact(DisplayName = "T03 同 (SupplyKey, DemandKey) 多笔分配 ⇒ 合并为一条，量相加")]
    public void SameSupplyAndDemand_MergedIntoOneDraft()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[]
            {
                Alloc(orderId: 100, supplyKey: "PI:1:1001:3", qty: 20m),
                Alloc(orderId: 100, supplyKey: "PI:1:1001:3", qty: 30m)
            },
            Status((100, Protected)));

        var d = Assert.Single(drafts);
        Assert.Equal(50m, d.LockedQty);
    }

    [Fact(DisplayName = "T03 同订单不同物理供给 ⇒ 各出一条，互不合并")]
    public void SameOrderDifferentSupplies_ProduceSeparateDrafts()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[]
            {
                Alloc(orderId: 100, supplyKey: "PI:1:1001:3", qty: 20m),
                Alloc(orderId: 100, supplyKey: "PI:2:1001:3", qty: 30m)
            },
            Status((100, Protected)));

        Assert.Equal(2, drafts.Count);
        Assert.Equal(50m, drafts.Sum(d => d.LockedQty));
    }

    // ── 「空键不可作正式持久锁」────────────────────────────────────────────

    [Fact(DisplayName = "T03 无物理锁键（虚拟供给／无单号在途）⇒ 不建锁")]
    public void EmptySupplyKey_ProducesNothing()
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[]
            {
                Alloc(orderId: 100, supplyKey: "", qty: 60m),
                Alloc(orderId: 100, supplyKey: "   ", qty: 60m)
            },
            Status((100, Protected)));

        Assert.Empty(drafts);
    }

    [Theory(DisplayName = "T03 锁量必须 >0、订单 Id 必须 >0，否则不建锁")]
    [InlineData(0, 60)]     // OrderId 缺失
    [InlineData(100, 0)]    // 分配量 0
    [InlineData(100, -5)]   // 异常负值
    public void InvalidNumbers_ProduceNothing(long orderId, decimal qty)
    {
        var drafts = PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            new[] { Alloc(orderId: orderId, supplyKey: "PI:1:1001:3", qty: qty) },
            Status((orderId, Protected)));

        Assert.Empty(drafts);
    }

    // ── 稳定性 ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "T03 输出顺序确定（先按 SupplyKey 再按 DemandKey），便于落库与对账")]
    public void OutputIsDeterministic()
    {
        var input = new[]
        {
            Alloc(orderId: 100, supplyKey: "PI:B:1:3", qty: 1m, demandKey: "ORDER_1_M_3"),
            Alloc(orderId: 100, supplyKey: "PI:A:1:3", qty: 1m, demandKey: "ORDER_1_M_3"),
            Alloc(orderId: 101, supplyKey: "PI:A:1:3", qty: 1m, demandKey: "ORDER_2_M_3")
        };
        var map = Status((100, Protected), (101, Protected));

        var first = PeggingOrchestrator.BuildDemandSupplyLockDrafts(input, map);
        var second = PeggingOrchestrator.BuildDemandSupplyLockDrafts(input, map);

        Assert.Equal(first.Select(d => (d.SupplyKey, d.DemandKey)), second.Select(d => (d.SupplyKey, d.DemandKey)));
        Assert.Equal("PI:A:1:3", first[0].SupplyKey);
        Assert.Equal("PI:B:1:3", first[2].SupplyKey);
    }

    [Fact(DisplayName = "T03 空输入不抛错")]
    public void EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(PeggingOrchestrator.BuildDemandSupplyLockDrafts(
            Array.Empty<SupplyAllocationItem>(), Status((100, Protected))));
    }
}