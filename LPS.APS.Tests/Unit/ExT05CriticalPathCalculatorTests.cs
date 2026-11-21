using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Algorithms;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T05 关键路径**（冻结 07 字段说明 §4.4 `Task.IsCriticalPath`「1=关键路径」）。
///
/// <para>被测对象：<see cref="CriticalPathCalculator"/>（`LPS.APS.Scheduling/Algorithms`）。
/// 该算法**尚未接线**（`FinalTaskDraft` 的 `IsCriticalPath` 字段属 `LPS.APS.Core` 契约件，
/// 待 2号位 明示辖区授权后加入并接线）。本文件锁的是**算法口径本身**，接线后不得改语义。</para>
///
/// <para>口径：项目终点 = 全图 `PlannedEndTime` 最大值；反向推 `latestStart`；
/// `slack = latestStart − plannedStart`；**`slack ≤ 1s` ⇒ 关键**。
/// 图 = 同需求按 `OperationSeq` 串联 ＋ 跨物料「子件末 Task → 父件首 Task（带 lag）」。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class ExT05CriticalPathCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0);

    private static DateTime At(int hours, int minutes = 0) => T0.AddHours(hours).AddMinutes(minutes);

    [Fact(DisplayName = "EX-T05①：同需求 3 道工序首尾相接 ⇒ 三道全部关键")]
    public void SingleChain_AllCritical()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("d1", "D1", 10, At(0), At(1)),
            Task("d2", "D1", 20, At(1), At(2)),
            Task("d3", "D1", 30, At(2), At(3)),
        };

        var critical = CriticalPathCalculator.Compute(tasks);

        Assert.Equal(3, critical.Count);
        Assert.Contains("d1", critical);
        Assert.Contains("d2", critical);
        Assert.Contains("d3", critical);
    }

    [Fact(DisplayName = "EX-T05②：两条并行链，短链早完工 ⇒ 只长链关键（短链有 slack）")]
    public void ParallelChains_OnlyLongestIsCritical()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("long1", "D1", 10, At(0), At(1)),
            Task("long2", "D1", 20, At(1), At(3)),   // D1 到 11:00
            Task("short1", "D2", 10, At(0), At(1)),  // D2 到 09:00，比终点早 2 小时
        };

        var critical = CriticalPathCalculator.Compute(tasks);

        Assert.Contains("long1", critical);
        Assert.Contains("long2", critical);
        Assert.DoesNotContain("short1", critical);   // 有 2 小时 slack
    }

    [Fact(DisplayName = "EX-T05③：子件紧接父件（lag=0）⇒ 子件无 slack、判关键")]
    public void CrossMaterial_ChildFinishesRightBeforeParent_ChildIsCritical()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("c1", "C", 10, At(0), At(1)),    // 子件 08:00-09:00
            Task("p1", "P", 10, At(1), At(2)),    // 父件 09:00-10:00（紧接）
        };
        var lags = new Dictionary<(string Parent, string Child), decimal>
        {
            [("P", "C")] = 0m,
        };

        var critical = CriticalPathCalculator.Compute(tasks, lags);

        Assert.Contains("p1", critical);
        Assert.Contains("c1", critical);
    }

    [Fact(DisplayName = "EX-T05④：父件晚 30 分钟才开工 ⇒ 这 30 分钟就是子件的 slack，子件不关键")]
    public void CrossMaterial_ChildHasGap_ChildNotCritical()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("c1", "C", 10, At(0), At(1)),          // 09:00 完工
            Task("p1", "P", 10, At(1, 30), At(2, 30)),  // 09:30 才开工 ⇒ 中间 30 分钟是子件的余量
        };
        var lags = new Dictionary<(string Parent, string Child), decimal>
        {
            [("P", "C")] = 0m,
        };

        var critical = CriticalPathCalculator.Compute(tasks, lags);

        Assert.Contains("p1", critical);
        Assert.DoesNotContain("c1", critical);
    }

    [Fact(DisplayName = "EX-T05⑤：父件早于子件完工（非法排程）⇒ 不构成绑定链，只标终点")]
    public void CrossMaterial_ParentStartsBeforeChildEnds_NotBinding()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("c1", "C", 10, At(0), At(1)),      // 09:00 完工
            Task("p1", "P", 10, At(0, 30), At(1, 30)), // 08:30 就开工（违反先后）
        };
        var lags = new Dictionary<(string Parent, string Child), decimal>
        {
            [("P", "C")] = 0m,
        };

        var critical = CriticalPathCalculator.Compute(tasks, lags);

        // 新口径（绑定链）：子件完工晚于父件开工 ⇒ 该边**不可能绑定** ⇒ 链不往子件延展。
        Assert.Contains("p1", critical);
        Assert.DoesNotContain("c1", critical);
    }

    [Fact(DisplayName = "EX-T05⑨：同资源首尾相接 ⇒ 前一个 Task 是绑定前驱，判关键")]
    public void SameResource_BackToBack_IsBinding()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskWithResource("r1", "D1", At(0), At(1), resourceId: 7),
            TaskWithResource("r2", "D2", At(1), At(2), resourceId: 7),   // 紧接在 r1 之后，同资源
        };

        var critical = CriticalPathCalculator.Compute(tasks);

        Assert.Contains("r2", critical);   // 终点
        Assert.Contains("r1", critical);   // 同资源绑定前驱
    }

    [Fact(DisplayName = "EX-T05⑥：跨物料 lag 计入 → lag 越大子件 slack 越大")]
    public void CrossMaterial_LagEnlargesChildSlack()
    {
        // 子件 08:00-09:00；父件 11:00-12:00；lag=60 分钟 ⇒ 父件要求子件 ≤ 10:00 完工
        // ⇒ 子件 slack = 10:00 − 09:00 = 60 分钟 ⇒ 不关键
        var tasks = new List<FinalTaskDraft>
        {
            Task("c1", "C", 10, At(0), At(1)),
            Task("p1", "P", 10, At(3), At(4)),
        };
        var lags = new Dictionary<(string Parent, string Child), decimal>
        {
            [("P", "C")] = 60m,
        };

        var critical = CriticalPathCalculator.Compute(tasks, lags);

        Assert.Contains("p1", critical);
        Assert.DoesNotContain("c1", critical);
    }

    [Fact(DisplayName = "EX-T05⑦：空列表 ⇒ 空集（不抛异常）")]
    public void EmptyInput_ReturnsEmpty()
    {
        var critical = CriticalPathCalculator.Compute(Array.Empty<FinalTaskDraft>());
        Assert.Empty(critical);
    }

    [Fact(DisplayName = "EX-T05⑧：数据成环 ⇒ 不死循环；只有**真正绑定**的那个方向进链")]
    public void Cycle_TerminatesAndKeepsOnlyBindingDirection()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("a", "A", 10, At(0), At(1)),   // 08:00-09:00
            Task("b", "B", 10, At(1), At(2)),   // 09:00-10:00
        };
        var lags = new Dictionary<(string Parent, string Child), decimal>
        {
            [("A", "B")] = 0m,   // 父 A / 子 B ⇒ 边 B→A
            [("B", "A")] = 0m,   // 父 B / 子 A ⇒ 边 A→B（与上一条成环）
        };

        var critical = CriticalPathCalculator.Compute(tasks, lags);

        // 绑定链口径下：a(09:00 完工) 与 b(09:00 开工) 正好接上 ⇒ a 是 b 的绑定前驱；
        // 反向那条（b 完工 10:00 晚于 a 的 08:00 开工）**不可能绑定** ⇒ 不会被采纳。
        // 关键是最坏情况不得死循环（本用例能跑完即证明）。
        Assert.Contains("b", critical);
        Assert.Contains("a", critical);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static FinalTaskDraft TaskWithResource(
        string finalDraftId, string sourceDraftId, DateTime start, DateTime end, int resourceId)
        => new()
        {
            FinalDraftId = finalDraftId,
            SourceDraftId = sourceDraftId,
            MaterialId = 1,
            FactoryId = 1,
            OperationSeq = 0,
            OperationCode = "OP10",
            StageCode = "S1",
            Quantity = 10m,
            ResourceId = resourceId,
            PlannedStartTime = start,
            PlannedEndTime = end,
        };

    private static FinalTaskDraft Task(
        string finalDraftId, string sourceDraftId, int operationSeq, DateTime start, DateTime end)
        => new()
        {
            FinalDraftId = finalDraftId,
            SourceDraftId = sourceDraftId,
            MaterialId = 1,
            FactoryId = 1,
            OperationSeq = operationSeq,
            OperationCode = $"OP{operationSeq}",
            StageCode = "S1",
            Quantity = 10m,
            PlannedStartTime = start,
            PlannedEndTime = end,
        };
}
