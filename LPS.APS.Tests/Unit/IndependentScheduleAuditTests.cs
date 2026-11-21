using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 v1.8 §三「资源重叠/TaskDependency」**：
/// 「提交真正**独立**的 Task 时间窗 / Setup 窗口 / 资源锁 / 依赖边逆序
/// SQL 或**内存校验清单**和**破坏性反例**」。
///
/// <para>本文件的校验器是**测试侧独立实现**：<b>不调用</b>
/// `PhaseFiveCompression.ValidateHardResult`、<b>不复用</b>其判据代码。
/// 目的正是「用另一套写法再查一遍」——若两套同时说绿才有意义；
/// 只让被测代码自己证明自己，等于没证。</para>
///
/// <para>覆盖四类（均为 0号位 点名）：</para>
/// <list type="number">
///   <item>**同资源时间窗重叠**：占用起点 = `PlannedStart − SetupTime`，半开区间相交即违规；</item>
///   <item>**Setup 窗口被他人占用**：Setup 段不得与同资源的其它 Task 加工段相交；</item>
///   <item>**依赖边逆序**：父（下游）开工不得早于子（上游）完工；</item>
///   <item>**资源锁**：被锁定 Task 的起止时间不得被改动。</item>
/// </list>
///
/// <para>纯内存，不碰数据库、不跑 Solver。</para>
/// </summary>
public sealed class IndependentScheduleAuditTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0);

    // ─────────────────────── 独立校验器（测试侧实现） ───────────────────────

    private sealed record AuditTask(
        string Id, int ResourceId, DateTime Start, DateTime End, decimal SetupMinutes,
        bool Locked = false, DateTime? LockedStart = null, DateTime? LockedEnd = null);

    private sealed record AuditDep(string UpstreamId, string DownstreamId);

    /// <summary>占用起点 = 加工起点 − Setup（Setup 占用资源时间）。</summary>
    private static DateTime OccupiedStart(AuditTask t) => t.Start.AddMinutes(-(double)t.SetupMinutes);

    /// <summary>1+2：同资源时间窗 / Setup 窗口重叠。</summary>
    private static List<string> FindResourceOverlaps(IReadOnlyList<AuditTask> tasks)
    {
        var violations = new List<string>();
        foreach (var group in tasks.GroupBy(t => t.ResourceId))
        {
            var rows = group.OrderBy(OccupiedStart).ToList();
            for (var i = 0; i + 1 < rows.Count; i++)
            {
                var a = rows[i];
                var b = rows[i + 1];
                // 半开区间：a 占用 [occupiedStart, End)
                if (OccupiedStart(b) < a.End)
                {
                    violations.Add(
                        $"资源 {group.Key}: {a.Id}[{OccupiedStart(a):HH:mm}-{a.End:HH:mm}] "
                        + $"与 {b.Id}[{OccupiedStart(b):HH:mm}-{b.End:HH:mm}] 重叠");
                }
            }
        }
        return violations;
    }

    /// <summary>3：依赖边逆序（下游开工早于上游完工）。</summary>
    private static List<string> FindDependencyViolations(
        IReadOnlyList<AuditTask> tasks, IReadOnlyList<AuditDep> deps)
    {
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var dep in deps)
        {
            if (!byId.TryGetValue(dep.UpstreamId, out var up) ||
                !byId.TryGetValue(dep.DownstreamId, out var down))
            {
                continue;
            }
            if (down.Start < up.End)
            {
                violations.Add($"{dep.DownstreamId} 开工 {down.Start:HH:mm} 早于上游 {dep.UpstreamId} 完工 {up.End:HH:mm}");
            }
        }
        return violations;
    }

    /// <summary>4：资源锁 —— 被锁 Task 的起止必须与锁定值逐位一致。</summary>
    private static List<string> FindLockDrift(IReadOnlyList<AuditTask> tasks)
    {
        var violations = new List<string>();
        foreach (var t in tasks.Where(x => x.Locked))
        {
            if (t.LockedStart is { } ls && t.Start != ls)
                violations.Add($"{t.Id} 锁定开工 {ls:HH:mm} 实际 {t.Start:HH:mm}");
            if (t.LockedEnd is { } le && t.End != le)
                violations.Add($"{t.Id} 锁定完工 {le:HH:mm} 实际 {t.End:HH:mm}");
        }
        return violations;
    }

    private static (List<string> Overlaps, List<string> Deps, List<string> Locks) Audit(
        IReadOnlyList<AuditTask> tasks, IReadOnlyList<AuditDep> deps)
        => (FindResourceOverlaps(tasks), FindDependencyViolations(tasks, deps), FindLockDrift(tasks));

    // ─────────────────────────────── 正例 ───────────────────────────────

    [Fact(DisplayName = "独立审计正例：加工窗口、Setup、依赖、锁全部合法 ⇒ 零违规")]
    public void CleanSchedule_NoViolations()
    {
        var tasks = new List<AuditTask>
        {
            new("C1", 1, T0, T0.AddHours(2), 0m),
            // 下游含 30 分钟 Setup：占用起点 = 10:30，晚于上游完工 10:00 ⇒ 合法
            new("P1", 2, T0.AddHours(2.5), T0.AddHours(4), 30m),
        };
        var deps = new List<AuditDep> { new("C1", "P1") };

        var (overlaps, depViolations, lockDrift) = Audit(tasks, deps);

        Assert.Empty(overlaps);
        Assert.Empty(depViolations);
        Assert.Empty(lockDrift);
    }

    // ─────────────────────────────── 破坏性反例 ───────────────────────────────

    [Fact(DisplayName = "破坏性反例①：同资源两 Task 加工窗口重叠 ⇒ 必须被检出")]
    public void Destructive_ResourceOverlap_Detected()
    {
        var tasks = new List<AuditTask>
        {
            new("A", 1, T0, T0.AddHours(3), 0m),
            new("B", 1, T0.AddHours(2), T0.AddHours(5), 0m),   // 与 A 重叠 1 小时
        };

        var (overlaps, _, _) = Audit(tasks, Array.Empty<AuditDep>());

        Assert.Single(overlaps);
        Assert.Contains("A", overlaps[0]);
        Assert.Contains("B", overlaps[0]);
    }

    [Fact(DisplayName = "破坏性反例②：Setup 窗口侵入前一个 Task ⇒ 必须被检出")]
    public void Destructive_SetupWindowIntrusion_Detected()
    {
        var tasks = new List<AuditTask>
        {
            new("A", 1, T0, T0.AddHours(2), 0m),
            // 加工 10:00 开始看着不重叠，但 60 分钟 Setup 从 09:00 起 ⇒ 侵入 A
            new("B", 1, T0.AddHours(2), T0.AddHours(4), 60m),
        };

        var (overlaps, _, _) = Audit(tasks, Array.Empty<AuditDep>());

        Assert.Single(overlaps);
    }

    [Fact(DisplayName = "破坏性反例③：下游开工早于上游完工 ⇒ 必须被检出")]
    public void Destructive_DependencyReversed_Detected()
    {
        var tasks = new List<AuditTask>
        {
            new("C1", 1, T0, T0.AddHours(5), 0m),
            new("A1", 2, T0.AddHours(1), T0.AddHours(3), 0m),   // 上游还没完就开工
        };
        var deps = new List<AuditDep> { new("C1", "A1") };

        var (_, depViolations, _) = Audit(tasks, deps);

        Assert.Single(depViolations);
        Assert.Contains("A1", depViolations[0]);
    }

    [Fact(DisplayName = "破坏性反例④：被锁 Task 的时间被移动 ⇒ 必须被检出")]
    public void Destructive_LockDrift_Detected()
    {
        var tasks = new List<AuditTask>
        {
            new("L1", 1, T0.AddHours(1), T0.AddHours(3), 0m,
                Locked: true, LockedStart: T0, LockedEnd: T0.AddHours(2)),
        };

        var (_, _, lockDrift) = Audit(tasks, Array.Empty<AuditDep>());

        Assert.Equal(2, lockDrift.Count);   // 开工与完工都被移动
    }
}
