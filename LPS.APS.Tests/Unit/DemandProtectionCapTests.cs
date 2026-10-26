using FluentAssertions;
using Xunit;

using static LPS.APS.Application.Services.PeggingOrchestrator;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Demand Protection **数量预留闸门**测试
/// （0号位 2026-10-25《四份回执联合核证》§二-3：`DEMAND_PROTECTION` 不得当成整条供给的身份阻断，
/// 且不得机械 `RemainingQty − LockedQty` 造成「预留量」与「已实扣量」双减）。
///
/// 断言的正是本改动**声明的目的**：
///   ① 无保护锁 ⇒ 不设限（今天 `DemandSupplyHardLock` 为 0 行，此支必须完全惰性、零行为变化）；
///   ② 受保护需求本身 ⇒ 不设限（它可取到预留量，也可继续取剩余份额）；
///   ③ 非受保护需求 ⇒ 上限 = 剩余 − **仍未被取走的**预留量（PM 例：保护 60、其余 40 可竞争）；
///   ④ 预留已被受保护需求取够 ⇒ 不再设限（避免双减）；
///   ⑤ 剩余不足预留 ⇒ 上限 0（Fail Closed，不得「看起来锁了却照用」）。
///
/// ⚠ 只覆盖纯函数，不触库、不跑 Run。
/// </summary>
public class DemandProtectionCapTests
{
    private static (string? LockedToDemandKey, long? LockedToOrderId, decimal LockedQty) ProtectToKey(
        string key, decimal qty) => (key, null, qty);

    private static (string? LockedToDemandKey, long? LockedToOrderId, decimal LockedQty) ProtectToOrder(
        long orderId, decimal qty) => (null, orderId, qty);

    private static (string? DemandKey, decimal AllocatedQty) Taken(string key, decimal qty) => (key, qty);

    // ── ① 惰性：没有保护锁时必须完全放行 ────────────────────────────────

    [Fact]
    public void 无保护锁_不设限()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: Array.Empty<(string?, long?, decimal)>(),
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 1,
            demandKey: "ORDER_1_A");

        cap.Should().Be(decimal.MaxValue, "无保护锁 ⇒ 今日 0 行锁的现网必须零行为变化");
    }

    [Fact]
    public void 空保护列表同样不设限()
    {
        ComputeDemandProtectionCap(0m, new List<(string?, long?, decimal)>(), null!, null, "D")
            .Should().Be(decimal.MaxValue);
    }

    // ── ② 受保护需求本身不设限 ──────────────────────────────────────────

    [Fact]
    public void 当前需求按DemandKey受保护_不设限()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m) },
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 1,
            demandKey: "ORDER_1_A");

        cap.Should().Be(decimal.MaxValue, "受保护需求就是那 60，不能被自己的预留挡住");
    }

    [Fact]
    public void 当前需求按OrderId受保护_不设限()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: new[] { ProtectToOrder(1, 60m) },
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 1,
            demandKey: "ORDER_1_A");

        cap.Should().Be(decimal.MaxValue, "OrderId 命中同样是受保护方");
    }

    // ── ③ 非受保护需求：只看得到「剩余 − 未取走的预留」 ─────────────────

    [Fact]
    public void 非受保护需求_保护60时其余可竞争940()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m) },
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 2,
            demandKey: "ORDER_2_B");

        cap.Should().Be(940m, "PM 口径：保护 60、其余 40 可竞争（千件场景即 940）");
    }

    [Fact]
    public void 多个保护锁_预留量累加()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m), ProtectToOrder(3, 40m) },
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 9,
            demandKey: "ORDER_9_X");

        cap.Should().Be(900m, "两条预留言 60+40 一并扣除");
    }

    // ── ④ 避免双减 ──────────────────────────────────────────────────────

    [Fact]
    public void 受保护需求已取走20_只按剩余预留40扣()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 980m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m) },
            allocations: new[] { Taken("ORDER_1_A", 20m) },
            demandOrderId: 2,
            demandKey: "ORDER_2_B");

        cap.Should().Be(940m,
            "剩余 980 中包含受保护需求已实扣的 20；仍待预留只有 40 ⇒ 980−40=940，不能再按 60 扣一次");
    }

    [Fact]
    public void 预留已被受保护需求取够_不再设限()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 940m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m) },
            allocations: new[] { Taken("ORDER_1_A", 60m) },
            demandOrderId: 2,
            demandKey: "ORDER_2_B");

        cap.Should().Be(decimal.MaxValue, "预留 60 已全部取走 ⇒ 剩余 940 全部可竞争，不得再挡");
    }

    // ── ⑤ Fail Closed ───────────────────────────────────────────────────

    [Fact]
    public void 剩余不足预留_上限为0()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 50m,
            protections: new[] { ProtectToKey("ORDER_1_A", 60m) },
            allocations: Array.Empty<(string?, decimal)>(),
            demandOrderId: 2,
            demandKey: "ORDER_2_B");

        cap.Should().Be(0m, "只剩 50 却要预留 60 ⇒ 其它需求不得再取（不得「看起来锁了却照用」）");
    }

    [Fact]
    public void 剩余为0_上限为0()
    {
        ComputeDemandProtectionCap(0m, new[] { ProtectToKey("ORDER_1_A", 60m) },
                Array.Empty<(string?, decimal)>(), 2, "ORDER_2_B")
            .Should().Be(0m);
    }

    // ── 边界：空键的锁不参与「已取走」匹配 ──────────────────────────────

    [Fact]
    public void 空DemandKey的锁不计入已取走()
    {
        var cap = ComputeDemandProtectionCap(
            remainingQty: 1000m,
            protections: new[] { ProtectToOrder(1, 60m) },
            allocations: new[] { Taken(null, 20m), Taken("", 20m) },
            demandOrderId: 2,
            demandKey: "ORDER_2_B");

        cap.Should().Be(940m, "键为空的分配无法归属到受保护方，不得冲减其预留量");
    }
}
