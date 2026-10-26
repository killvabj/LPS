using System;
using System.IO;
using System.Threading;
using LPS.APS.Tests.Integration;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 【2026-10-26 增】跑批超时令牌与证据落点的单测。
///
/// <para><b>为什么这些看着「太简单」的断言必须写</b>：这两处工装的全部价值都在
/// <b>坏结局</b>里（求解器不返回、进程被掐）。它们平时永远静默正常，
/// 一旦写错也<b>不会报错</b>，只会让下一次卡死的时候又一次什么都留不下 ——
/// 那正是 10-25、10-26 连撞三次的形态。见 memory <c>self-check-must-assert-its-stated-purpose</c>。</para>
/// </summary>
public class RunCancellationTests
{
    [Fact(DisplayName = "跑批超时：设了分钟数，令牌到点真的取消")]
    public void 设了分钟数_令牌到点真的取消()
    {
        // 0.02 分钟 = 1.2 秒。
        using var cts = RealDomainFullRunTest.CreateRunCancellationCore("0.02", out var minutes);

        Assert.Equal(0.02d, minutes, 3);
        Assert.False(cts.IsCancellationRequested, "刚创建就已被取消 —— 定时器时长算错了");

        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)),
            "等了 10 秒令牌仍未取消 ⇒ APS_RUN_TIMEOUT_MIN 只是个摆设，求解器照样会无限挂住。");
        Assert.True(cts.IsCancellationRequested);
    }

    [Theory(DisplayName = "跑批超时：未设 / 空 / 非法 / 0 / 负数 ⇒ 一律不限时")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void 未设或非法_一律不限时(string? raw)
    {
        using var cts = RealDomainFullRunTest.CreateRunCancellationCore(raw, out var minutes);

        Assert.Equal(0d, minutes);
        // 令牌不存在任何定时器：等 300ms 必须仍未取消。
        // 反过来说，若这里红了，说明「解析失败」被当成了某个默认分钟数 —— 那是拿笔误去掐正常跑批。
        Assert.False(cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)),
            $"输入 '{raw}' 被当成了有效超时（minutes={minutes}）⇒ 笔误会掐掉正常跑批。");
    }

    [Fact(DisplayName = "证据落点：解析到仓库内『运行日志』目录，而不是退化成当前目录")]
    public void 证据落点_在仓库内运行日志目录()
    {
        var overridden = Environment.GetEnvironmentVariable("APS_RUN_LOG_DIR");
        if (overridden is { Length: > 0 })
        {
            // 覆盖值是使用者自己指定的，本断言不适用（但别静默跳过 —— 打印出来）。
            Assert.Equal(overridden, RealDomainFullRunTest.ResolveRunLogDirectory());
            return;
        }

        var dir = RealDomainFullRunTest.ResolveRunLogDirectory();

        // 退化成 cwd 时的样子是 .../LPS.APS.Tests/bin/Debug/net8.0 —— 收尾判断就靠这个后缀。
        Assert.EndsWith("运行日志", dir,
            StringComparison.Ordinal);

        // 运行日志 ← 04_测试与备份 ← APS ← Scripts ← Database ← 仓库根
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(repoRoot, "LPS.APS.sln")),
            $"上溯 5 级没有得到仓库根（算出来是 {repoRoot}）⇒ 目录结构变了，落点会飘。");
    }
}
