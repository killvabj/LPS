using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// G-2「工厂 / 生产部门级生效 WORK 窗口」投影语义钉死测试（1号位 2026-10-25 核证回执 §五 点名）。
///
/// 【为什么必须有这组测试】
///   投影结果直接决定「无 Routing 的 Stage 一天能推进多少小时」，错一条就会把交付期算偏。
///   本组逐条钉住四类口径：
///     ① **继承已物化** —— 部门自身无事实时，部门行＝工厂行（消费侧不做二级回落）；
///     ② **部门显式 OFF 不被工厂 WORK 复活**（RC-006 反方向）；
///     ③ **工厂整日 OFF 压住部门 WORK**（RC-006 正方向：明确关闭不可由上级/下级复活）；
///     ④ **两层都有 WORK 时取交集，绝不并集相加**（RC-001 / 日历专项 v1.4 §2）。
///   另有计划期裁剪、部门主档缺工厂两类边界。
/// </summary>
public class DepartmentWorkWindowProjectorTests
{
    /// <summary>2026-10-12 是**周一**（ISO = 1）；本组测试用 周一…周五 一段。</summary>
    private static readonly DateTime Mon = new(2026, 10, 12);
    private static readonly DateTime Fri = Mon.AddDays(4);
    private static readonly DateTime Wed = Mon.AddDays(2);

    private const int FactoryId = 3;
    private const int DeptId = 963;

    // ── 构造器 ──────────────────────────────────────────────────────────

    private static ProductionWorkCalendarSlot Row(
        string scopeType, int? deptId, byte? weekdayIso, DateTime? workDate,
        string state, TimeSpan? startAt, TimeSpan? endAt)
        => new()
        {
            ScopeType = scopeType,
            FactoryId = FactoryId,
            ProductionDepartmentId = deptId,
            WeekdayIso = weekdayIso,
            WorkDate = workDate,
            WorkState = state,
            StartAt = startAt,
            EndAt = endAt,
            EffectiveFrom = new DateTime(2020, 1, 1),
            EffectiveTo = null,
            IsActive = true
        };

    /// <summary>周一至周五 WORK 08:00–17:00（工厂级或部门级）。</summary>
    private static IEnumerable<ProductionWorkCalendarSlot> WeekdayWork(string scopeType, int? deptId)
        => WeekdayWorkRange(scopeType, deptId, 8, 17);

    private static IEnumerable<ProductionWorkCalendarSlot> WeekdayWorkRange(
        string scopeType, int? deptId, int fromHour, int toHour)
    {
        for (byte iso = 1; iso <= 5; iso++)
            yield return Row(scopeType, deptId, iso, null, "WORK",
                TimeSpan.FromHours(fromHour), TimeSpan.FromHours(toHour));
    }

    private static IReadOnlyDictionary<int, int> DeptMap(params int[] depts)
        => depts.ToDictionary(d => d, _ => FactoryId);

    private static DepartmentWorkWindowProjector.Result Project(
        IEnumerable<ProductionWorkCalendarSlot> rows,
        IReadOnlyDictionary<int, int>? deptMap = null,
        int[]? depts = null,
        int[]? factories = null,
        DateTime? start = null,
        DateTime? end = null)
        => DepartmentWorkWindowProjector.Project(
            rows.ToList(),
            deptMap ?? DeptMap(DeptId),
            depts ?? new[] { DeptId },
            factories,
            start ?? Mon,
            end ?? Fri);

    private static List<DateTime> DayStarts(
        DepartmentWorkWindowProjector.Result r, int? deptId)
        => r.Windows.Where(w => w.DepartmentId == deptId).Select(w => w.Start).OrderBy(x => x).ToList();

    // ── ① 继承已物化 ────────────────────────────────────────────────────

    [Fact]
    public void 部门自身无事实_部门行等于工厂行_即继承已由投影侧完成()
    {
        var result = Project(WeekdayWork("FACTORY", null));

        var factoryWins = result.Windows.Where(w => w.DepartmentId == null).ToList();
        var deptWins = result.Windows.Where(w => w.DepartmentId == DeptId).ToList();

        factoryWins.Should().HaveCount(5, "周一至周五各一片");
        deptWins.Should().HaveCount(5, "部门无事实 ⇒ 继承工厂，消费侧不需要二级回落");
        deptWins.Select(w => (w.Start, w.End))
            .Should().Equal(factoryWins.Select(w => (w.Start, w.End)));
        deptWins.Should().OnlyContain(w => w.FactoryId == FactoryId);

        result.DepartmentWithOwnFactsCount.Should().Be(0);
        result.DepartmentScopeCount.Should().Be(1);
        result.FactoryScopeCount.Should().Be(1);
        result.DaysProjected.Should().Be(5);
    }

    // ── ② 部门显式 OFF 不被工厂 WORK 复活 ───────────────────────────────

    [Fact]
    public void 部门整日OFF_工厂WORK不得把它复活()
    {
        var rows = WeekdayWork("FACTORY", null)
            .Append(Row("DEPARTMENT", DeptId, null, Wed, "OFF", null, null));

        var result = Project(rows);

        DayStarts(result, null).Should().HaveCount(5, "工厂级照常工作日");
        DayStarts(result, DeptId).Should().HaveCount(4, "部门本周三整日 OFF，不得由工厂 WORK 复活");
        DayStarts(result, DeptId).Should().NotContain(d => d.Date == Wed);
    }

    // ── ③ 工厂整日 OFF 压住部门 WORK ───────────────────────────────────

    [Fact]
    public void 工厂整日OFF_部门WORK不得复活上级关闭()
    {
        var rows = WeekdayWork("FACTORY", null)
            .Append(Row("FACTORY", null, null, Wed, "OFF", null, null))
            .Concat(WeekdayWork("DEPARTMENT", DeptId));

        var result = Project(rows);

        // 工厂层：周三被日期例外（整日 OFF）覆盖 ⇒ 只有 4 片
        DayStarts(result, null).Should().HaveCount(4);
        // 部门层：自身 WORK 与工厂 OFF 取交集后仍为空 ⇒ 周三无窗口（不是「部门更细就听部门的」）
        DayStarts(result, DeptId).Should().HaveCount(4);
        DayStarts(result, DeptId).Should().NotContain(d => d.Date == Wed);
    }

    // ── ④ 两层都有 WORK ⇒ 交集，不相加 ─────────────────────────────────

    [Fact]
    public void 部门WORK与工厂WORK取交集_不是并集也不是相加()
    {
        var rows = WeekdayWork("FACTORY", null)
            .Concat(WeekdayWorkRange("DEPARTMENT", DeptId, 9, 15));

        var result = Project(rows);

        var deptWins = result.Windows.Where(w => w.DepartmentId == DeptId).ToList();
        deptWins.Should().HaveCount(5);
        deptWins.Should().OnlyContain(w =>
            w.Start.TimeOfDay == TimeSpan.FromHours(9) && w.End.TimeOfDay == TimeSpan.FromHours(15));

        // 工厂级仍是完整的 08:00–17:00（不被部门行改写）
        result.Windows.Where(w => w.DepartmentId == null)
            .Should().OnlyContain(w => w.Start.TimeOfDay == TimeSpan.FromHours(8)
                                       && w.End.TimeOfDay == TimeSpan.FromHours(17));
    }

    // ── 边界：计划期裁剪 ────────────────────────────────────────────────

    [Fact]
    public void 只投影计划期内的自然日()
    {
        var result = Project(WeekdayWork("FACTORY", null), start: Wed, end: Wed.AddDays(1));

        DayStarts(result, null).Should().HaveCount(2);
        DayStarts(result, null).Should().OnlyContain(d => d.Date == Wed || d.Date == Wed.AddDays(1));
        result.DaysProjected.Should().Be(2);
    }

    [Fact]
    public void 计划期非法_不投影且标记HorizonInvalid()
    {
        var result = Project(WeekdayWork("FACTORY", null), start: Fri, end: Mon);

        result.HorizonInvalid.Should().BeTrue();
        result.Windows.Should().BeEmpty();
    }

    // ── 边界：部门主档缺工厂 ────────────────────────────────────────────

    [Fact]
    public void 部门在ProductionDepartment查不到工厂_不投影且计数()
    {
        var result = Project(
            WeekdayWork("FACTORY", null),
            deptMap: new Dictionary<int, int>(),
            depts: new[] { DeptId });

        result.DepartmentsWithoutFactoryCount.Should().Be(1);
        result.DepartmentScopeCount.Should().Be(0);
        result.Windows.Should().BeEmpty("没有可解析归属 ⇒ 既不投影工厂也不投影部门");
    }

    // ── 边界：本域无部门上下文 ──────────────────────────────────────────

    [Fact]
    public void 本域无部门也无需求工厂_投影为空表且不是缺陷()
    {
        var result = Project(WeekdayWork("FACTORY", null), depts: Array.Empty<int>(), factories: Array.Empty<int>());

        result.Windows.Should().BeEmpty();
        result.HorizonInvalid.Should().BeFalse();
        result.ScopesWithoutAnyWindow.Should().Be(0, "一个 scope 都没投影，不该被算成「全期无工时」");
    }

    [Fact]
    public void 需求工厂无任何部门上下文_仍要投影厂级行()
    {
        // 该 Stage 没部门上下文、但厂级有事实 ⇒ 消费侧按 (厂, null) 必须查得到，否则会被当成无工时。
        var result = Project(
            WeekdayWork("FACTORY", null),
            deptMap: new Dictionary<int, int>(),
            depts: Array.Empty<int>(),
            factories: new[] { FactoryId });

        result.FactoryScopeCount.Should().Be(1);
        result.DepartmentScopeCount.Should().Be(0);
        DayStarts(result, null).Should().HaveCount(5);
        result.Windows.Should().OnlyContain(w => w.DepartmentId == null);
    }

    // ── 边界：源表无事实 ────────────────────────────────────────────────

    [Fact]
    public void 源表无任何日历事实_全期无窗口且不兜底()
    {
        var result = Project(Array.Empty<ProductionWorkCalendarSlot>());

        result.Windows.Should().BeEmpty("RC-007：三级无权威来源时无可排 Slot，禁止补 8h / 7×24");
        result.ScopesWithoutAnyWindow.Should().Be(2, "厂级 + 部门级各一个 scope 全期无窗口");
    }
}
