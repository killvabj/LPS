using FluentAssertions;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// `Task.Duration` 回写公式（`Duration-2`）的回归锁 —— 应 1号位 2026-10-26 回执 §6.3 的请求补上。
///
/// 【为什么必须有这条锁】
/// 历史回写用的是**墙钟跨度** `PlannedEndTime − PlannedStartTime`。单窗（不跨日历中断）下，
/// 墙钟跨度 **恰好等于** `有效加工 + SetupTime` ⇒ **两种写法单测全绿，看不出差别**。
/// 只有 Task 跨了日历窗（含午休／夜休／周末），墙钟跨度才会大于有效加工，此时：
///   · 正确写法 → `Duration = 有效加工 + Setup`
///   · 错回墙钟跨度 → `Duration` 被**跨日历中断段虚增**，且不会报错。
/// 故本文件的锚点用例是 <see cref="跨日历窗_必须用有效加工而不是墙钟跨度"/>：
/// **任何把公式改回墙钟跨度的改动都会让它红，而单窗用例仍绿。**
///
/// 【口径来源】1号位 2026-10-26 回执 §一／§6.1 ＋ 冻结 `07 :33`「Setup 占设备但不属于有效加工」：
///   `Duration`（工时）＝ Setup ＋ 有效加工，**不含**跨日历中断。
///
/// 【回落分支的口径陷阱】`EffectiveProcessMinutes` 为 `null` 时回落**墙钟跨度本身**，
///   **不再额外加 `SetupTime`** —— 墙钟跨度本就含 Setup，加则重复计。
///   这一点由 <see cref="无有效加工_回落墙钟跨度且不重复计Setup"/> 锁住。
/// </summary>
public class TaskDurationWriteBackTests
{
    // 单窗样本：08:00 起，墙钟 510 分钟 = 有效加工 480 + Setup 30（三者自洽）
    private static readonly DateTime SingleStart = new(2026, 10, 26, 8, 0, 0);
    private static readonly DateTime SingleEnd   = new(2026, 10, 26, 16, 30, 0);

    // 跨窗样本：08:00 排到次日 08:00 —— 墙钟 1,440 分钟，含夜休；有效加工仍只有 480
    private static readonly DateTime CrossStart = new(2026, 10, 26, 8, 0, 0);
    private static readonly DateTime CrossEnd   = new(2026, 10, 27, 8, 0, 0);

    // ── 锚点用例：跨日历窗 ─────────────────────────────────────────────

    [Fact(DisplayName = "锚点_跨日历窗_必须用有效加工而不是墙钟跨度")]
    public void 跨日历窗_必须用有效加工而不是墙钟跨度()
    {
        var duration = PeggingOrchestrator.ComputeTaskDurationMinutes(
            CrossStart, CrossEnd, effectiveProcessMinutes: 480m, setupTime: 30m);

        duration.Should().Be(510m, "跨窗 Task 的 Duration 装的是「工时」= 有效加工 480 + Setup 30");
        duration.Should().NotBe(1440m,
            "墙钟跨度 1,440 含跨日历中断段，不能当作工时 —— 本断言把「错的理由」写在断言里");
    }

    // ── 反证：单窗下两种写法同值 ⇒ 单窗测不出问题 ──────────────────────

    [Fact(DisplayName = "单窗_两种写法同值_说明单窗用例测不出本缺陷")]
    public void 单窗_两种写法同值()
    {
        var withEffective = PeggingOrchestrator.ComputeTaskDurationMinutes(SingleStart, SingleEnd, 480m, 30m);
        var withFallback  = PeggingOrchestrator.ComputeTaskDurationMinutes(SingleStart, SingleEnd, null, 30m);

        withEffective.Should().Be(510m, "480 + 30");
        withFallback.Should().Be(510m, "墙钟跨度 510 —— 单窗下它恰好等于有效加工 + Setup");
        withEffective.Should().Be(withFallback,
            "两者相等正是缺陷藏得住的原因：单窗单测无法区分「用有效加工」与「用墙钟跨度」");
    }

    // ── 缺值回落（1号位 点名：锁定继承 Task 恒为 null，属预期不是漏拷）──────

    [Fact(DisplayName = "无有效加工_回落墙钟跨度且不重复计Setup")]
    public void 无有效加工_回落墙钟跨度且不重复计Setup()
    {
        var duration = PeggingOrchestrator.ComputeTaskDurationMinutes(
            CrossStart, CrossEnd, effectiveProcessMinutes: null, setupTime: 30m);

        duration.Should().Be(1440m, "回落＝墙钟跨度原值；Setup 已含在这个跨度里");
        duration.Should().NotBe(1470m, "若再叠一次 Setup 就是重复计 —— 这是回落分支最易犯的错");
    }

    // ── Setup 是否漏加（冻结 07 :33 的直读断言）────────────────────────

    [Fact(DisplayName = "Setup必须计入_不得只写有效加工")]
    public void Setup必须计入()
    {
        var duration = PeggingOrchestrator.ComputeTaskDurationMinutes(SingleStart, SingleEnd, 480m, 45m);

        duration.Should().Be(525m, "工时 = 有效加工 480 + Setup 45；漏加 Setup 会得到 480");
    }

    [Fact(DisplayName = "Setup为零_仅有效加工")]
    public void Setup为零_仅有效加工()
    {
        var start = new DateTime(2026, 10, 26, 8, 0, 0);
        var end   = new DateTime(2026, 10, 26, 9, 40, 0);   // 100 分钟

        PeggingOrchestrator.ComputeTaskDurationMinutes(start, end, 100m, 0m).Should().Be(100m);
    }

    // ── 精度：与落库列同口径（4 位小数）──────────────────────────────

    [Fact(DisplayName = "结果保留四位小数")]
    public void 结果保留四位小数()
    {
        PeggingOrchestrator.ComputeTaskDurationMinutes(SingleStart, SingleEnd, 1.23456m, 0m)
            .Should().Be(1.2346m, "落库口径与历史 `Math.Round(..., 4)` 一致");
    }
}
