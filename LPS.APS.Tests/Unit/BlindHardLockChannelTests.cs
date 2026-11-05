using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R02/O02「硬锁通道失效」闸门的回归测试（2026-10-30）。
///
/// 【背景】`SupplyLedgerEntry.SupplyKey` 全仓无赋值点 ⇒ 恒为空串 ⇒ 锁装载只会发一次
///   `WHERE SupplyKey = ''`，永远匹配不到任何行。今天 `DemandSupplyHardLock` 为 0 行，
///   所以结果无害；但一旦锁表有数据，跑批会**当作没有锁**继续排 —— 保护失效却「一切正常」。
///
/// 【本闸门要守的三条】
///   ① 表里没有 ACTIVE 锁 ⇒ **必须放行**（否则每轮跑批都会被自己的闸门打死）；
///   ② 有锁且供给键全空 ⇒ **必须拦**（这正是静默失效的形态）；
///   ③ 有锁且至少一个真键 ⇒ **必须放行**（通道可用，不能误拦）。
/// </summary>
public class BlindHardLockChannelTests
{
    // ① 无锁 ⇒ 不拦。供给键是什么都无所谓。
    [Fact]
    public void 表内无ACTIVE锁_即使供给键全空也放行()
    {
        Assert.Null(PeggingOrchestrator.DetectBlindHardLockChannel(new[] { "" }, 0));
        Assert.Null(PeggingOrchestrator.DetectBlindHardLockChannel(Array.Empty<string>(), 0));
    }

    // ② 有锁 + 供给键全空 ⇒ 必须拦，且原因里要点名「锁表有数、键为空」。
    [Fact]
    public void 有ACTIVE锁但供给键全空_必须拦并点名()
    {
        var reason = PeggingOrchestrator.DetectBlindHardLockChannel(new[] { "" }, 12);

        Assert.NotNull(reason);
        Assert.Contains("12", reason!);
        Assert.Contains("SupplyKey", reason);
    }

    // ② 变体：供给池整个为空（连空键都没有）⇒ 同样要拦，且提示要指向供给装载而不是键口径。
    [Fact]
    public void 有ACTIVE锁但供给池为空_必须拦且提示供给装载()
    {
        var reason = PeggingOrchestrator.DetectBlindHardLockChannel(Array.Empty<string>(), 3);

        Assert.NotNull(reason);
        Assert.Contains("供给池为空", reason!);
    }

    // ③ 有锁 + 至少一个真键 ⇒ 放行（只有部分键为空不属于「全盲」）。
    [Fact]
    public void 有ACTIVE锁且存在真键_放行()
    {
        Assert.Null(PeggingOrchestrator.DetectBlindHardLockChannel(new[] { "", "INVENTORY_MAT001_F01_WH01" }, 5));
    }

    // 边界：多条目全空（不是单元素数组）也要判成盲。
    [Fact]
    public void 多条空键去重后仍为空串_判盲()
    {
        Assert.NotNull(PeggingOrchestrator.DetectBlindHardLockChannel(new[] { "", "", "" }, 1));
    }
}
