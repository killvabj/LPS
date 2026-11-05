using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 「部门自主模式」旗位常量校验（2026-11-04 Owner 裁决）。
/// 规则：两旗恒=1——缺省(null)按默认1放行；显式 false 拒绝；
/// 原"AllowLowerOverride 仅挂 OFF / IsAuthorizedOverride 仅挂 WORK / 凭证必填"三条废止。
/// </summary>
public class ProductionWorkCalendarFlagConstTests
{
    // ── 缺省与 true 全放行（含两旗同挂任意行型态——分挂规则已废） ──
    [Theory]
    [InlineData(null, null)]
    [InlineData(true, null)]
    [InlineData(null, true)]
    [InlineData(true, true)]
    public void 旗位缺省或为1_放行(bool? allow, bool? auth)
        => ProductionWorkCalendarService.ValidateFlags(allow, auth);   // 不抛即通过

    // ── 显式 false 一律拒（"默认填1，不能填别的"） ──
    [Theory]
    [InlineData(false, null)]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void 旗位显式为0_拒绝(bool? allow, bool? auth)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ProductionWorkCalendarService.ValidateFlags(allow, auth));
        Assert.Contains("固定为 1", ex.Message);
    }

    // ── 分挂规则废止回归：OFF 行携 Auth、WORK 行携 Allow 都不再拦 ──
    [Fact]
    public void 旗子分挂限制已废止_状态校验不再看旗()
    {
        // WORK 必须携时段（本职校验仍在）
        Assert.Throws<ArgumentException>(() =>
            ProductionWorkCalendarService.ValidateStateAndTimes("WORK", null, null));
        // 全天 OFF（双 NULL）合法，与旗无关
        ProductionWorkCalendarService.ValidateStateAndTimes("OFF", null, null);
        // OFF 时段行 Start>=End 仍拦
        Assert.Throws<ArgumentException>(() =>
            ProductionWorkCalendarService.ValidateStateAndTimes(
                "OFF", new TimeSpan(10, 0, 0), new TimeSpan(9, 0, 0)));
    }
}
