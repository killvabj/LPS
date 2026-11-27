using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 执行函 v1.7 §C**（逐字）：
/// 「修正失败事务的原子性、完整回滚与候选变更……**失败重试不能总选同一劣质位置**，要尝试合法替代方案，
///   已落定合法批不可丢失」。
///
/// <para>本类锁 <see cref="PhaseFourLocalRepair.RepairRollbackJournal"/> 的两件可独立验证的事实：</para>
/// <list type="number">
///   <item><b>完整回滚</b>：回滚后占用窗集合与回滚前**逐字相等**（不是"窗数为 0"这种弱断言）；</item>
///   <item><b>候选变更的载体</b>：回滚**不**清空 <c>AddedStarts</c> —— 重试要把这些
///         `(ResourceId, 占用起点)` 当「已否决位置」传下去，否则会落回同一处。</item>
/// </list>
///
/// <para>⚠ 边界（如实声明）：本类**不**证明 Solver 端到端「重试换到另一台资源」——那需要构造
/// 「第一道工序落定、第二道全资源失败、重试」的整链场景；在 0号位 未授权整域/大切片试跑前，
/// 这里只验证回滚与载体。纯内存，不触库。</para>
/// </summary>
public class RepairRollbackCandidateChangeTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    private static TimeWindow W(int fromMin, int toMin)
        => new(T0.AddMinutes(fromMin), T0.AddMinutes(toMin));

    private static string Sig(IEnumerable<TimeWindow> windows)
        => string.Join("|", windows.Select(w => $"[{w.Start:HH:mm}-{w.End:HH:mm}]"));

    [Fact]
    public void 回滚后占用窗集合逐字还原_且已否决位置仍可读()
    {
        // 既有合法占用（必须原样保留）
        var existing = new List<TimeWindow> { W(0, 60), W(240, 300) };
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [1] = new List<TimeWindow>(existing),
            [2] = new List<TimeWindow>()
        };
        var timeline = new ResourceProductTimeline();
        var before1 = Sig(occupancy[1]);

        var journal = new PhaseFourLocalRepair.RepairRollbackJournal();
        // 模拟「第一道工序落定、第二道失败」：两次写入都记进日志
        var added1 = W(60, 120);
        occupancy[1].Add(added1);
        occupancy[1].Sort((a, b) => a.Start.CompareTo(b.Start));
        journal.AddedOccupancy(1, added1);

        var added2 = W(120, 180);
        occupancy[2].Add(added2);
        journal.AddedOccupancy(2, added2);

        // 回滚（失败事务原子性）
        journal.Rollback(occupancy, timeline);

        Assert.Equal(before1, Sig(occupancy[1]));       // 既有合法占用不受影响
        Assert.Empty(occupancy[2]);                      // 新增的窗撤干净
        Assert.DoesNotContain(occupancy[1], w => w.Start == added1.Start && w.End == added1.End);

        // 候选变更载体：回滚**不**清空「已否决位置」
        Assert.Contains((1, added1.Start), journal.AddedStarts);
        Assert.Contains((2, added2.Start), journal.AddedStarts);
    }

    [Fact]
    public void 已否决位置以资源加起点为键_同资源不同起点不受牵连()
    {
        var journal = new PhaseFourLocalRepair.RepairRollbackJournal();
        var at9 = W(60, 120);      // 09:00-10:00
        var at11 = W(180, 240);    // 11:00-12:00

        journal.AddedOccupancy(7, at9);
        journal.AddedOccupancy(7, at11);

        Assert.Contains((7, at9.Start), journal.AddedStarts);
        Assert.Contains((7, at11.Start), journal.AddedStarts);
        // 同资源、不同起点是两条不同的否决记录（重试可换到同资源更晚的合法窗口）
        Assert.Equal(2, journal.AddedStarts.Count);
        // 不同资源同起点也不混
        Assert.DoesNotContain((8, at9.Start), journal.AddedStarts);
    }

    [Fact]
    public void 全部失败时回滚幂等_二次回滚不抛不误删既有窗()
    {
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [3] = new List<TimeWindow> { W(0, 60) }
        };
        var timeline = new ResourceProductTimeline();
        var before = Sig(occupancy[3]);

        var journal = new PhaseFourLocalRepair.RepairRollbackJournal();
        var added = W(60, 120);
        occupancy[3].Add(added);
        journal.AddedOccupancy(3, added);

        journal.Rollback(occupancy, timeline);
        journal.Rollback(occupancy, timeline);   // 幂等：第二次没有记录可撤

        Assert.Equal(before, Sig(occupancy[3]));
        // 幂等回滚**不应**清掉否决记录 —— 调用方可能需要在同一日志上再来一次候选变更
        Assert.Contains((3, added.Start), journal.AddedStarts);
    }
}
