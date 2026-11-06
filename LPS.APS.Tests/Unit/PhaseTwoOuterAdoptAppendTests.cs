using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-06 P2-A：外层批方案采纳「纯追加快路径」的**判定**回归。
/// 依据 0号位《Phase2源码级性能优化明确实施指令 v2.0》§三.1：
/// 「取 `oldCount`，用 `SameReferences(...)` 做原Task引用及次序一致性校验；另确保 `winner.Tasks.Count >= oldCount`」。
///
/// <para>【为什么单独测判定】这一步是整条快路径的**唯一闸门**：判 true 就会跳过整表替换，
/// 判错（把「其实变了」的表当成纯追加）会直接产出错误的 Task 表。所以边界要逐条钉死，
/// 尤其是「胜者表更短」（真实合批摘除 / Stage 合批后重排）与「同长但次序不同」两种必须返回 false。</para>
///
/// <para>【测不到的部分】真正的采纳动作（追加 vs Clear+AddRange）、批分组索引对齐、
/// 份额/占用/时间线等其他状态的采纳，由既有 Unit 用例整体兜住，本文件不重复。</para>
/// </summary>
public sealed class PhaseTwoOuterAdoptAppendTests
{
    private static FinalTaskDraft Task(string id)
        => new FinalTaskDraft { FinalDraftId = id, ResourceId = 1 };

    [Fact(DisplayName = "P2-A 判定 · 胜者表更长且前缀逐引用相同 ⇒ 纯追加")]
    public void Longer_WinnerSamePrefix_IsPureAppend()
    {
        var t1 = Task("T1");
        var t2 = Task("T2");
        var current = new List<FinalTaskDraft> { t1, t2 };
        var winner = new List<FinalTaskDraft> { t1, t2, Task("T3") };

        Assert.True(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 等长且全同引用 ⇒ 纯追加（等价于不动）")]
    public void EqualLength_SameReferences_IsPureAppend()
    {
        var t1 = Task("T1");
        var t2 = Task("T2");
        var current = new List<FinalTaskDraft> { t1, t2 };
        var winner = new List<FinalTaskDraft> { t1, t2 };

        Assert.True(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 胜者表更短（真实合批摘除 / 重排）⇒ 回退旧路径")]
    public void Shorter_Winner_FallsBack()
    {
        var t1 = Task("T1");
        var t2 = Task("T2");
        var current = new List<FinalTaskDraft> { t1, t2 };
        var winner = new List<FinalTaskDraft> { t1 };

        Assert.False(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 前缀某位换成另一个对象（中间替换）⇒ 回退旧路径")]
    public void PrefixDifferentReference_FallsBack()
    {
        var t1 = Task("T1");
        var t2 = Task("T2");
        var current = new List<FinalTaskDraft> { t1, t2 };
        // 内容相仿但**不是同一个对象** —— 引用比较必须判否
        var winner = new List<FinalTaskDraft> { t1, Task("T2"), Task("T3") };

        Assert.False(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 等长但次序不同 ⇒ 回退旧路径")]
    public void SameLength_DifferentOrder_FallsBack()
    {
        var t1 = Task("T1");
        var t2 = Task("T2");
        var current = new List<FinalTaskDraft> { t1, t2 };
        var winner = new List<FinalTaskDraft> { t2, t1 };

        Assert.False(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 旧表为空 ⇒ 纯追加（与 Clear+AddRange 等价）")]
    public void EmptyCurrent_IsPureAppend()
    {
        var current = new List<FinalTaskDraft>();
        var winner = new List<FinalTaskDraft> { Task("T1"), Task("T2") };

        Assert.True(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(current, winner));
    }

    [Fact(DisplayName = "P2-A 判定 · 两表都空 ⇒ 纯追加（空表清空是空操作）")]
    public void BothEmpty_IsPureAppend()
    {
        Assert.True(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(
            new List<FinalTaskDraft>(), new List<FinalTaskDraft>()));
    }

    [Fact(DisplayName = "P2-A 判定 · 胜者表为空而旧表非空 ⇒ 回退旧路径")]
    public void EmptyWinner_NonEmptyCurrent_FallsBack()
    {
        var current = new List<FinalTaskDraft> { Task("T1") };
        Assert.False(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(
            current, new List<FinalTaskDraft>()));
    }

    [Fact(DisplayName = "P2-A 判定 · 胜者只在尾部追加多条（合批链）⇒ 纯追加")]
    public void AppendManyTailTasks_IsPureAppend()
    {
        var head = new List<FinalTaskDraft> { Task("T1"), Task("T2"), Task("T3") };
        var winner = new List<FinalTaskDraft>(head) { Task("B1"), Task("B2") };

        Assert.True(PhaseTwoInitialScheduler.IsOuterAdoptPureAppend(head, winner));
    }
}