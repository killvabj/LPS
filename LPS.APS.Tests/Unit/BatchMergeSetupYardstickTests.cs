using System;
using Xunit;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **W3 · Setup 与评分同标尺**（0号位 v3.0 §2.3 必备反例 1 / 2 / 3）。
///
/// 直接喂 <see cref="PhaseTwoInitialScheduler.ResolveStageMerge"/> 的**完整比较元组**
/// （与 `PreferMergeByFrozenLayers` 同一个核，但额外返回「比较器落在哪一层」），
/// 逐条证明：
///   ① **Setup 节省与释放的窗口不得双重加分** —— 若合批同时「Setup 更小」与「完成更早」，
///      胜负必须在**更早完成（③）**那一层就判出，Setup 层不得再被用来「再赢一次」；
///   ② **合并省 Setup 但延误交期 ⇒ 必须被拒** —— 履约层（②）在 Setup 层（④）之前；
///   ③ **同样的完成时间、同样的 Setup 总额、但真实占用不同** ⇒ 必须被区别对待（④.6 层）。
///
/// ⚠ 本文件只主张**比较核**的层序与方向；不主张 Solver 级能构造出同一形态（那由
///   `StageBatchMergeTests` 的 M 系列覆盖）。
/// ⚠ 纯内存、无 `ConstraintContext`、无日历、不触库。
/// </summary>
public sealed class BatchMergeSetupYardstickTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 10, 8, 0, 0);

    /// <summary>默认「完全判平」：都可行、都不延期、同完工、同 Setup、经济偏离相同、占用相同。</summary>
    private static (PhaseTwoInitialScheduler.FrozenLayer Layer, bool MergeWins) Resolve(
        bool separateFeasible = true,
        bool mergeFeasible = true,
        long delaySeparate = 0,
        long delayMerge = 0,
        string? direction = null,
        DateTime? separateEnd = null,
        DateTime? mergeEnd = null,
        decimal separateSetup = 60m,
        decimal mergeSetup = 60m,
        (decimal Range, decimal Preferred) separateEconDev = default,
        (decimal Range, decimal Preferred) mergeEconDev = default,
        decimal? separateNonSetupOccupiedMinutes = null,
        decimal? mergeNonSetupOccupiedMinutes = null)
        => PhaseTwoInitialScheduler.ResolveStageMerge(
            separateFeasible,
            mergeFeasible,
            delaySeparate,
            delayMerge,
            direction ?? SchedulingDirectionResolver.Forward,
            separateEnd ?? T0.AddHours(6),
            mergeEnd ?? T0.AddHours(6),
            separateSetup,
            mergeSetup,
            separateEconDev,
            mergeEconDev,
            separateNonSetupOccupiedMinutes,
            mergeNonSetupOccupiedMinutes);

    // ══════════════════════════════════════════════════════════════════
    //  反例 1：同资源 8h 合法窗、真实加工 6h；甲 Setup 1.5h / 乙 Setup 0.5h
    //         —— 加工时长相同，乙**释放 1h**
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// **1a**：两方案**完成时间相同**、只有 Setup 不同（90 vs 30，差 60min = 1h 释放）
    /// ⇒ 必须由 **④ Setup 层**判出，合批（Setup 小者）胜。
    /// 这一条单独看是「正常」的：Setup 小确实应该赢。
    /// </summary>
    [Fact(DisplayName = "W3-1a · 完成时间相同、Setup 90 vs 30 ⇒ 由 Setup 层判定，合批胜")]
    public void SameCompletion_DifferentSetup_DecidedAtSetupLayer()
    {
        var r = Resolve(
            separateEnd: T0.AddHours(7),
            mergeEnd: T0.AddHours(7),
            separateSetup: 90m,
            mergeSetup: 30m);

        Assert.True(r.MergeWins, "Setup 更小的合批方案应当胜出");
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.Setup, r.Layer);
    }

    /// <summary>
    /// **1b（关键）**：合批**同时**「Setup 更小」与「完成更早」⇒ 必须在 **③ 交期层**就判出，
    /// **不得**落到 ④ Setup 层。
    ///
    /// 这就是「不得双重加分」的判据：如果实现先把「更早完成」算进 ③、又让 ④ 再赢一次，
    /// 那同一份 1 小时收益被记了两遍；而层序上 ③ 已经分出胜负，④ **根本不该被评价到**。
    /// </summary>
    [Fact(DisplayName = "W3-1b · Setup 更小且完成更早 ⇒ 由交期层判出，不得再落到 Setup 层（禁止双重加分）")]
    public void SetupGainAndEarlierCompletion_DecidedAtDueDateLayer_NotSetup()
    {
        var r = Resolve(
            separateEnd: T0.AddHours(7),      // 甲：更晚完成
            mergeEnd: T0.AddHours(6),         // 乙：早 1h
            separateSetup: 90m,
            mergeSetup: 30m);

        Assert.True(r.MergeWins, "既更早完成又更省 Setup 的合批方案应当胜出");
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.DueDate, r.Layer);

        // ★ 判别点：**不是** Setup 层 ⇒ 同一份收益没有被记两遍。
        Assert.NotEqual(PhaseTwoInitialScheduler.FrozenLayer.Setup, r.Layer);
    }

    // ══════════════════════════════════════════════════════════════════
    //  反例 2：多 Operation 因合并 Setup 下降，但会延误更重要交期 ⇒ 必须被拒
    // ══════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "W3-2 · 合并省 Setup 但延误交期 ⇒ 履约层直接拒，不得因省 Setup 而采纳")]
    public void MergeSavesSetupButDelaysDue_MustBeRejectedAtFulfillment()
    {
        var r = Resolve(
            delaySeparate: 0,          // 独立方案按期
            delayMerge: 120,           // 合批延误 120 分钟
            separateSetup: 90m,
            mergeSetup: 30m);          // 合批省 60min Setup，但**不能**因此翻盘

        Assert.False(r.MergeWins, "延误交期的合批方案必须被拒，哪怕它更省 Setup");
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.Fulfillment, r.Layer);
    }

    [Fact(DisplayName = "W3-2b · 履约层先于交期层：都延期时，延期短者优先，不看完成绝对时间")]
    public void FulfillmentPrecedesDueDate_ShorterDelayWins()
    {
        var r = Resolve(
            delaySeparate: 60,
            delayMerge: 30,
            separateEnd: T0.AddHours(5),   // 独立方案完成更早（绝对时间）
            mergeEnd: T0.AddHours(9));     // 合批完成更晚，但延期更短

        Assert.True(r.MergeWins, "都延期时延期短者优先 ⇒ 合批应胜");
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.Fulfillment, r.Layer);
    }

    // ══════════════════════════════════════════════════════════════════
    //  反例 3：同设备集合、同完成时间、同 Setup 总额，但**真实占用不同** ⇒ 必须被区别
    //
    //  ⚠ 诚实边界：本比较核拿不到「占用顺序 / 邻居是谁」，它只拿到
    //    「除 Setup 外的真实有效占用分钟数」。所以这里的判别力是：
    //    **只要占用总量不同就必须被区分**；占用总量相同而只是顺序不同，本核**分辨不出**
    //    （顺序影响会通过 Setup 与完成时间体现，那两层在前）。
    // ══════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "W3-3 · 同完成、同 Setup、同设备集合，但真实占用 240 vs 180 ⇒ ④.6 必须区分")]
    public void SameSetupAndCompletion_DifferentNonSetupOccupancy_DecidedAtEfficiency()
    {
        var r = Resolve(
            separateNonSetupOccupiedMinutes: 240m,   // 独立方案占 240min
            mergeNonSetupOccupiedMinutes: 180m);     // 合批占 180min

        Assert.True(r.MergeWins, "占用更少的合批方案应当在 ④.6 层胜出");
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.ResourceEfficiency, r.Layer);
    }

    [Fact(DisplayName = "W3-3b · 其他全同且占用也相同 ⇒ 判平取独立排程（合批不得靠模糊优势取胜）")]
    public void EverythingTies_MergeLoses()
    {
        var r = Resolve(
            separateNonSetupOccupiedMinutes: 240m,
            mergeNonSetupOccupiedMinutes: 240m);

        Assert.False(r.MergeWins, "完全判平时取独立排程 ⇒ 合批必须有可度量的改善才被采用");
    }

    [Fact(DisplayName = "W3-3c · Setup 只差 1 分钟也必须被区分（防止把邻居换型差异抹平）")]
    public void OneMinuteSetupDifference_IsDistinguishable()
    {
        var r = Resolve(separateSetup: 61m, mergeSetup: 60m);

        Assert.True(r.MergeWins);
        Assert.Equal(PhaseTwoInitialScheduler.FrozenLayer.Setup, r.Layer);
    }
}