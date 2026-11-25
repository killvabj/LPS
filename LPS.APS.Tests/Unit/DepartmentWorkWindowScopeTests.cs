using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **日历投影 scope 正反例**（0号位 2026-11-25 总表 v1.10 §十六.2 点名要的隔离夹具）：
///   · 正例：**执行厂在本域旧 scope 之外**时，把该厂并入 `usedFactoryIds` 后其厂级窗口可取到；
///   · 负例①：**显式 OFF 不得被上级 WORK 复活**；
///   · 负例②：**确无合法窗口**时不得凭空补工时（不补 8h／7×24）。
///
/// <para>隔离夹具：全部为合成行，**不读库、不依赖任何真实 PV**。</para>
/// </summary>
public class DepartmentWorkWindowScopeTests
{
    private static readonly DateTime Day = new(2026, 11, 18); // 周三
    private const byte WedIso = 3;

    private static ProductionWorkCalendarSlot FactoryWork(int factoryId, int startHour = 8, int endHour = 17) => new()
    {
        ScopeType = "FACTORY",
        FactoryId = factoryId,
        WeekdayIso = WedIso,
        StartAt = TimeSpan.FromHours(startHour),
        EndAt = TimeSpan.FromHours(endHour),
        WorkState = "WORK",
        IsActive = true,
        EffectiveFrom = new DateTime(2026, 1, 1)
    };

    private static ProductionWorkCalendarSlot DepartmentState(
        int factoryId, int departmentId, string state, int startHour, int endHour) => new()
    {
        ScopeType = "DEPARTMENT",
        FactoryId = factoryId,
        ProductionDepartmentId = departmentId,
        WeekdayIso = WedIso,
        StartAt = TimeSpan.FromHours(startHour),
        EndAt = TimeSpan.FromHours(endHour),
        WorkState = state,
        IsActive = true,
        EffectiveFrom = new DateTime(2026, 1, 1)
    };

    private static DepartmentWorkWindowProjector.Result Project(
        IReadOnlyList<ProductionWorkCalendarSlot> rows,
        IReadOnlyDictionary<int, int> deptFactory,
        IReadOnlyCollection<int> usedDepts,
        IReadOnlyCollection<int> usedFactories)
        => DepartmentWorkWindowProjector.Project(
            rows, deptFactory, usedDepts, usedFactories, Day, Day);

    // ── 正例：执行厂在旧 scope 之外 ─────────────────────────────────────
    [Fact]
    public void 正例_执行厂不在需求厂与部门厂之内_并入scope后其厂级窗口可取到()
    {
        // 部门 D1 属厂 F1；厂 F9 既不是需求厂、也不是任何部门的厂（模拟「只由 Stage 执行厂引入」）
        var rows = new[] { FactoryWork(1), FactoryWork(9) };
        var deptFactory = new Dictionary<int, int> { [1] = 1 };

        // 旧 scope ＝ 部门所属工厂 ∪ 需求工厂 ＝ {F1}
        var oldScope = Project(rows, deptFactory, new[] { 1 }, Array.Empty<int>());
        oldScope.Windows.Where(w => w.FactoryId == 9).Should().BeEmpty(
            "旧 scope 不含 F9 ⇒ 其厂级窗口根本不会投影出来（这正是 0号位 指出的结构性风险）");

        // 并入 Stage 执行厂后（＝ 我方 r14423/r14426 的做法）
        var newScope = Project(rows, deptFactory, new[] { 1 }, new[] { 9 });
        newScope.Windows.Where(w => w.FactoryId == 9).Should().NotBeEmpty(
            "并入 scope 后 F9 的厂级窗口可取到，第三级继承有数据可用");
        newScope.Windows.Where(w => w.FactoryId == 9 && w.DepartmentId is null)
            .Should().ContainSingle("厂级行的 DepartmentId 必须为 null（厂级口径）");
    }

    // ── 负例①：OFF 不得被上级 WORK 复活 ─────────────────────────────────
    [Fact]
    public void 负例_部门显式OFF_不得被上级工厂WORK复活()
    {
        var rows = new[]
        {
            FactoryWork(1),                                     // 厂级作业 08–17
            DepartmentState(1, 1, "OFF", 12, 13)                // 部门明确停 12–13
        };
        var deptFactory = new Dictionary<int, int> { [1] = 1 };

        var r = Project(rows, deptFactory, new[] { 1 }, Array.Empty<int>());
        var deptWindows = r.Windows.Where(w => w.DepartmentId == 1).ToList();

        deptWindows.Should().NotBeEmpty();
        deptWindows.Should().NotContain(w =>
                w.Start <= Day.AddHours(12) && w.End >= Day.AddHours(13),
            "12:00–13:00 被部门显式 OFF，上级工厂的 WORK 不得把它复活（RC-006）");
    }

    // ── 负例②：确无合法窗口 ⇒ 不补工时 ─────────────────────────────────
    [Fact]
    public void 负例_该厂无任何日历事实_不得凭空补工时()
    {
        // 厂 F9 无任何行；但被并入 scope（模拟「执行厂在 scope 内、但确无日历事实」）
        var rows = new[] { FactoryWork(1) };
        var deptFactory = new Dictionary<int, int> { [1] = 1 };

        var r = Project(rows, deptFactory, new[] { 1 }, new[] { 9 });

        r.Windows.Where(w => w.FactoryId == 9).Should().BeEmpty(
            "无事实 ⇒ 无窗口；不得补 8h／7×24（RC-007）");
        r.Windows.Should().OnlyContain(w => w.FactoryId == 1);
    }

    // ── 负例②补充：只给 OFF、不给 WORK ⇒ 同样无窗口 ─────────────────────
    [Fact]
    public void 负例_该厂当日只有OFF_无WORK窗口()
    {
        var offOnly = new ProductionWorkCalendarSlot
        {
            ScopeType = "FACTORY",
            FactoryId = 9,
            WeekdayIso = WedIso,
            StartAt = TimeSpan.FromHours(8),
            EndAt = TimeSpan.FromHours(17),
            WorkState = "OFF",
            IsActive = true,
            EffectiveFrom = new DateTime(2026, 1, 1)
        };

        var r = Project(new[] { FactoryWork(1), offOnly },
                        new Dictionary<int, int> { [1] = 1 },
                        new[] { 1 }, new[] { 9 });

        r.Windows.Where(w => w.FactoryId == 9).Should().BeEmpty(
            "该厂当日全是 OFF ⇒ 无合法 WORK 窗口");
    }
}