using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R09-O01 验收**（1号位 2026-10-31，应 0号位《R01～R12 未闭环最小任务单》P0 第 3 条）。
///
/// <para>0号位 原文：**「无Routing Stage 的提前期要按 WORK 窗口逐小时累计、首日剩余 WORK 可计入、OFF 跳过」**。</para>
///
/// <para><b>为什么单开一个测试类</b>：实现早就落了（`StageTimingNodeBuilder.TryAdvanceByWorkHours`
/// `:573`／`TryRetreatByWorkHours` `:693`），但既有的
/// `RoutelessStageLeadTimeConstraintTests` 夹具里 WORK 窗口是**一整段 30 天连续窗**
/// （`Start=Day, End=Day.AddDays(30)`）。连续窗下「逐片累计」「首日剩余」「OFF 跳过」
/// 三条全都退化成同一件事：`AddHours`。⇒ 那三条**一条都没被证明过**，
/// 把实现改回 `cursor.AddHours(hours)` 也不会红。
/// 这三条只能靠**带 OFF 间隔的多段窗口**钉住，所以另开一类。</para>
///
/// <para><b>鉴别力</b>：每条正向断言都配一条「朴素实现必红」的对照 ——
/// 若把窗口全部当连续时间直接 `AddHours`，`W_02`／`W_03`／`W_11` 会立刻算出不同答案。</para>
///
/// <para><b>本类只测纯函数</b>，不起 Solver、不连库、不落库。</para>
/// </summary>
public class RoutelessLeadTimeWorkWindowTests
{
    private const int FactoryId = 7;
    private const int MaterialId = 555;
    private const int Dept = 901;
    private const int OtherDept = 902;
    private const string Stage = "STAGE2";

    private static readonly DateTime D1 = new(2026, 9, 1);
    private static readonly DateTime D2 = new(2026, 9, 2);
    private static readonly DateTime D3 = new(2026, 9, 3);

    // ════════════════════════════════════════════════════════════════════
    //  首日剩余 WORK
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void W_01_游标落在窗口中间_只用首日剩余工时()
    {
        // 窗 08:00–17:00；游标 13:00 ⇒ 当天还剩 4h。要 2h ⇒ 15:00 完工。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17)) },
            cursor: D1.AddHours(13),
            hours: 2m);

        Assert.True(ok, reason);
        Assert.Equal(D1.AddHours(15), end);
    }

    [Fact]
    public void W_02_首日剩余不够时_必须跨到下一个工作窗而不是直接加小时()
    {
        // 窗 08:00–12:00 与 13:00–17:00（中间 1h OFF）；游标 11:00，要 2h。
        // 正解：11:00→12:00（1h），OFF 跳过，13:00→14:00（1h）⇒ 14:00。
        // 朴素 AddHours(2) 会得到 13:00 —— 正好落在 OFF 里，必红。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(12)), Win(D1.AddHours(13), D1.AddHours(17)) },
            cursor: D1.AddHours(11),
            hours: 2m);

        Assert.True(ok, reason);
        Assert.Equal(D1.AddHours(14), end);
        Assert.NotEqual(D1.AddHours(13), end);
    }

    [Fact]
    public void W_12_首日剩余用完才跨日_三种错误实现答案互不相同()
    {
        // 两天都是 08:00–17:00 的 9h 窗；游标 09-01 13:00；要 5h。
        //
        //   正确（首日剩余 4h 计入，再跨到次日补 1h）      ⇒ 09-02 09:00
        //   「首日剩余不计入」错法（直接跳到次日 08:00 起算 5h）⇒ 09-02 13:00
        //   朴素 AddHours(5)（把 OFF 也当工时）              ⇒ 09-01 18:00
        //
        // 三个答案互不相同 ⇒ 这条断言能把两种错法都打红。W_01 那个 2h 的用例
        // 恰好与朴素答案撞成同一个数，单独留着只证明「没跳到次日」，证明不了「不计 OFF」。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17)), Win(D2.AddHours(8), D2.AddHours(17)) },
            cursor: D1.AddHours(13),
            hours: 5m);

        Assert.True(ok, reason);
        Assert.Equal(D2.AddHours(9), end);
        Assert.NotEqual(D2.AddHours(13), end);   // 打「首日剩余不计入」
        Assert.NotEqual(D1.AddHours(18), end);   // 打朴素 AddHours
    }

    // ════════════════════════════════════════════════════════════════════
    //  OFF 跳过 / 逐片累计
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void W_03_提前期跨三天_必须逐片累计且整段OFF不计()
    {
        // 每天 08:00–12:00 一个 4h 窗（其余全是 OFF），要 10h。
        // 09-01 用 4h（到 12:00）→ 09-02 用 4h（累计 8h）→ 09-03 再要 2h ⇒ 09-03 10:00。
        // 朴素 AddHours(10) 从 09-01 00:00 起算会得到 09-01 10:00 —— 与正解差两天，必红。
        //
        // 顺带把窗口**乱序**喂进去：实现必须自己排序，不能依赖调用方传入顺序。
        var (ok, end, reason) = Advance(
            windows: new[]
            {
                Win(D3.AddHours(8), D3.AddHours(12)),
                Win(D1.AddHours(8), D1.AddHours(12)),
                Win(D2.AddHours(8), D2.AddHours(12)),
            },
            cursor: D1,
            hours: 10m);

        Assert.True(ok, reason);
        Assert.Equal(D3.AddHours(10), end);
    }

    [Fact]
    public void W_04_游标之后已经结束的窗口不得再计入()
    {
        // 09-01 的窗早就过了（游标 09-02 00:00）⇒ 只能用 09-02 的 4h 窗。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(12)), Win(D2.AddHours(8), D2.AddHours(12)) },
            cursor: D2,
            hours: 3m);

        Assert.True(ok, reason);
        Assert.Equal(D2.AddHours(11), end);
    }

    // ════════════════════════════════════════════════════════════════════
    //  证明不了 ⇒ 不许回落 8h / 7×24
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void W_05_窗口工时不够时_必须判INSUFFICIENT_WORK_不许回落8h或7x24()
    {
        // 只有一个 4h 窗，却要 5h。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(12)) },
            cursor: D1.AddHours(8),
            hours: 5m);

        Assert.False(ok);
        Assert.Equal("INSUFFICIENT_WORK", reason);
        Assert.Equal(D1.AddHours(8), end);   // 不推进，不编时间

        // 反向也一样：不许悄悄按 7×24 算出来。
        var (okBack, start, reasonBack) = Retreat(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(12)) },
            cursor: D1.AddHours(12),
            hours: 5m);

        Assert.False(okBack);
        Assert.Equal("INSUFFICIENT_WORK", reasonBack);
        Assert.Equal(D1.AddHours(12), start);
    }

    [Fact]
    public void W_06_取不到部门上下文时_必须判MISSING_DEPARTMENT_CONTEXT且不猜()
    {
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17)) },
            cursor: D1.AddHours(8),
            hours: 2m,
            stageHasDept: false);

        Assert.False(ok);
        Assert.Equal("MISSING_DEPARTMENT_CONTEXT", reason);
        Assert.Equal(D1.AddHours(8), end);
    }

    [Fact]
    public void W_07_该部门没有窗口时_必须判NO_WORK_WINDOW()
    {
        // 有别的部门的窗，但本 Stage 的部门一个窗都没有 ⇒ 不许借用别人的。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17), dept: OtherDept) },
            cursor: D1.AddHours(8),
            hours: 2m);

        Assert.False(ok);
        Assert.Equal("NO_WORK_WINDOW", reason);
        Assert.Equal(D1.AddHours(8), end);
    }

    [Fact]
    public void W_08_厂级行DepartmentId为null不得参与部门匹配()
    {
        // 只有厂级行（DepartmentId=null）。厂级事实不属于任何部门 ⇒ 不得顶替。
        var (ok, end, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17), dept: null) },
            cursor: D1.AddHours(8),
            hours: 2m);

        Assert.False(ok);
        Assert.Equal("NO_WORK_WINDOW", reason);
        Assert.Equal(D1.AddHours(8), end);
    }

    [Fact]
    public void W_09_别的工厂的窗口不得串用()
    {
        var (ok, _, reason) = Advance(
            windows: new[] { Win(D1.AddHours(8), D1.AddHours(17), factory: FactoryId + 1) },
            cursor: D1.AddHours(8),
            hours: 2m);

        Assert.False(ok);
        Assert.Equal("NO_WORK_WINDOW", reason);
    }

    // ════════════════════════════════════════════════════════════════════
    //  合法边界值：0 小时不需要任何窗口也确定
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void W_10_零小时是合法命中_无窗口也必须判确定()
    {
        // 0 小时不消耗工时 ⇒ 不需要窗口，也必须返回「时间确定」。
        var (ok, end, reason) = Advance(
            windows: Array.Empty<DepartmentWorkWindow>(),
            cursor: D1.AddHours(9),
            hours: 0m);

        Assert.True(ok, reason);
        Assert.Equal(D1.AddHours(9), end);
    }

    // ════════════════════════════════════════════════════════════════════
    //  倒排必须是正排的严格镜像
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void W_11_倒排是正排的严格镜像_含OFF()
    {
        var wins = new[] { Win(D1.AddHours(8), D1.AddHours(12)), Win(D1.AddHours(13), D1.AddHours(17)) };

        // 正排：11:00 + 2h ⇒ 14:00（W_02 已单测）
        var (okF, forward, _) = Advance(wins, D1.AddHours(11), 2m);
        Assert.True(okF);
        Assert.Equal(D1.AddHours(14), forward);

        // 倒排：14:00 − 2h ⇒ 11:00。两段窗各耗 1h，OFF 不计。
        var (okB, backward, reasonB) = Retreat(wins, D1.AddHours(14), 2m);
        Assert.True(okB, reasonB);
        Assert.Equal(D1.AddHours(11), backward);

        // 严格镜像：正排的结果拿去做倒排起点，必须回到原游标。
        var (okRound, roundTrip, _) = Retreat(wins, forward, 2m);
        Assert.True(okRound);
        Assert.Equal(D1.AddHours(11), roundTrip);
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    private static DepartmentWorkWindow Win(
        DateTime start, DateTime end, int? dept = Dept, int factory = FactoryId)
        => new() { FactoryId = factory, DepartmentId = dept, Start = start, End = end };

    private static DomainSolveRequest Request(
        IEnumerable<DepartmentWorkWindow> windows, bool stageHasDept) => new()
    {
        MaterialStageDepartmentContexts = stageHasDept
            ? new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = Dept },
            }
            : new List<MaterialStageDepartmentContextDto>(),
        DepartmentWorkWindows = new List<DepartmentWorkWindow>(windows),
    };

    private static LogicalProductionDemand Demand() => new()
    {
        LogicalDemandKey = "D1",
        MaterialId = MaterialId,
        FactoryId = FactoryId,
    };

    private static (bool Ok, DateTime End, string Reason) Advance(
        IEnumerable<DepartmentWorkWindow> windows,
        DateTime cursor,
        decimal hours,
        bool stageHasDept = true)
    {
        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            Request(windows, stageHasDept), Demand(), Stage, cursor, hours, out var end, out var reason);
        return (ok, end, reason);
    }

    private static (bool Ok, DateTime Start, string Reason) Retreat(
        IEnumerable<DepartmentWorkWindow> windows,
        DateTime cursor,
        decimal hours,
        bool stageHasDept = true)
    {
        var ok = StageTimingNodeBuilder.TryRetreatByWorkHours(
            Request(windows, stageHasDept), Demand(), Stage, cursor, hours, out var start, out var reason);
        return (ok, start, reason);
    }
}
