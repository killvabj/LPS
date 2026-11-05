using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 正排「在资源日历里找第一个能装下任务的窗口」的定位器。纯函数、无状态、零分配。
///
/// <para><b>为什么单独抽出来</b>：2026-10-26 PV190 实测，卡顿期间 3.697 亿次找槽调用烧掉 726 秒
/// （单次 1.96 µs），账**全部**落在「调用次数」上 —— 单次成本已无油水（同一段卡顿把找缝改成二分跳窗后，
/// 单次反而从 1.96 µs 涨到 2.76 µs，已回滚，见 `PhaseTwoInitialScheduler.FindFirstAvailableSlot` 注释）。</para>
///
/// <para><b>病在哪</b>：旧写法把该资源的**全部日历窗**逐个试 ——
/// 每个窗都调一次「在占用表里找最早可行起点」（下称 <c>findFirstGap</c>，O(log W + 扫窗)）。
/// 资源排满时**每个窗都会失败**，于是 W 次全量找缝；而 W 在 PV190 量级约 670。
/// 「排满 = 注定失败」这种形状下，99% 的调用是白跑的。</para>
///
/// <para><b>怎么跳</b>：注意到 <c>findFirstGap(x)</c> **与日历无关**，只取决于起点 <c>x</c>；
/// 且它随 <c>x</c> 单调不减。于是：
/// ① 先取<b>全局最早</b>可行起点 <c>s = findFirstGap(probe)</c>；
/// ② 只要存在<b>任何一个</b>窗满足 <c>Start ≤ s.Start</c> 且 <c>End ≥ s.End</c>，答案就是 <c>s</c>
///    （该窗里的 <c>findFirstGap</c> 起点必 ≤ <c>s.Start</c>，按单调性仍得 <c>s</c>）；
/// ③ 否则把 <c>probe</c> 推到「第一个 <c>Start &gt; s.Start</c> 的窗」再算一次。</para>
///
/// <para><b>②的判定怎么做到 O(1)</b>：窗按 <c>Start</c> 升序，<c>s.Start</c> 逐轮单调不减
/// ⇒ 只需维护一个「已计入前缀的最大 <c>End</c>」游标，整趟调用里总共前进 W 次、分摊到每轮 O(1)。
/// <b>不依赖「日历窗互不重叠」</b>——判据是「前缀最大 End」，重叠日历同样成立。</para>
///
/// <para><b>等价性</b>：结果与旧写法逐值相同（见 <c>CalendarSlotLocatorTests</c>：暴力复刻旧写法 +
/// 2,000 组随机日历/占用对拍，含重叠日历；另有用例专门断言「排满时找缝只调 1 次」这个**工作量下降**本身 ——
/// 「结果一致」护栏 ≠ 鉴别性反证）。</para>
///
/// <para><b>终止性</b>：每轮 <c>probe</c> 严格增大（新 probe 是某个窗的 <c>Start</c>，且 &gt; 上一轮 <c>s.Start</c> ≥ 上一轮 probe）；
/// 窗数有限 ⇒ 必然终止。</para>
/// </summary>
internal static class CalendarSlotLocator
{
    /// <summary>
    /// 在 <paramref name="ascendingCalendar"/>（按 <c>Start</c> 升序）里找第一个能完整容纳任务的时间槽。
    /// </summary>
    /// <param name="findFirstGap">
    /// 给一个起点，返回占用表里最早可行的槽（不看日历、无末期上界）。**不得返回 null** ——
    /// 与旧写法一致：正排只受日历窗约束，「最后一个占用之后」恒有空间。
    /// </param>
    /// <returns>装得进某个日历窗的槽；日历里放不下 ⇒ null。</returns>
    internal static TimeWindow? Locate(
        List<TimeWindow> ascendingCalendar,
        DateTime earliestStart,
        Func<DateTime, TimeWindow> findFirstGap)
    {
        // 空日历 ⇒ 直接放不下，且**一次找缝都不调**（与旧写法一致：旧写法 `foreach` 零次迭代，本来就不调）。
        if (ascendingCalendar.Count == 0) return null;

        var probe = earliestStart;

        // 前缀游标：`prefixEnd` = 第一个 `Start > slot.Start` 的下标；
        // `prefixMaxEnd` = `[0, prefixEnd)` 里最大的 `End`（按 Start 升序 ⇒ 只需一次前扫）。
        var prefixEnd = 0;
        var prefixMaxEnd = DateTime.MinValue;

        while (true)
        {
            var slot = findFirstGap(probe);

            while (prefixEnd < ascendingCalendar.Count && ascendingCalendar[prefixEnd].Start <= slot.Start)
            {
                if (ascendingCalendar[prefixEnd].End > prefixMaxEnd) prefixMaxEnd = ascendingCalendar[prefixEnd].End;
                prefixEnd++;
            }

            // 已有某个窗 `Start <= slot.Start` 且 `End >= slot.End` ⇒ 该窗完整装得下，答案就是它。
            if (prefixMaxEnd >= slot.End) return slot;

            // 推进到「第一个 `Start > slot.Start` 的窗」—— 起点 ≤ slot.Start 的窗全部已判失败。
            if (prefixEnd >= ascendingCalendar.Count) return null;
            probe = ascendingCalendar[prefixEnd].Start;
        }
    }
}
