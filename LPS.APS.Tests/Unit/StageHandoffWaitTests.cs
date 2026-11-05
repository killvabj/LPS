using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-03｜**Stage 固定交接等待**（冻结 v2.0 `:17`／合同 v1.1 §五）的正反例。
///
/// 立此文件的原因：`$.StageHandoffWaitRules` 的载体早在 Core（`SolverStrategySnapshot.StageHandoffWaitRules`），
///   但 `LPS.APS.Scheduling` **全仓零读取** —— 冻结要求「按真实有向对和 WORK/ELAPSED 应用一次合法等待」
///   从未落地。本文件守住落地后的口径：
///   ① 命中 = `IsActive==true` + `FactoryCode` 全等 + `ToStageCode` 全等 + `From≠To`；
///   ② 基准 = **本批内 FromStage 全部 Task 最晚 `PlannedEndTime`**，等待**加一次**、不按数量乘；
///   ③ A→B 与 B→A 独立，互不影响；
///   ④ 多规则命中取**最晚**，且**只抬不降**；
///   ⑤ `WORK` 基准**不静默当 ELAPSED**：登记 `WORK_NO_CALENDAR`、不应用该条等待。
/// </summary>
public class StageHandoffWaitTests
{
    private const string Factory = "F1";
    private static readonly DateTime T0 = new(2026, 12, 1, 8, 0, 0, DateTimeKind.Unspecified);

    private static LogicalProductionDemand Demand(string factory = Factory)
        => new()
        {
            LogicalDemandKey = "D1",
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            MaterialId = 1,
            FactoryId = 1,
            FactoryCode = factory,
            StartStageCode = "S1",
            NetOutputQty = 10m,
            PlannedProcessQty = 10m
        };

    private static FinalTaskDraft Task(string stage, DateTime end)
        => new()
        {
            FinalDraftId = $"{stage}-T",
            SourceDraftId = "D1",
            StageCode = stage,
            PlannedStartTime = end.AddHours(-1),
            PlannedEndTime = end
        };

    private static StageHandoffWaitRule Rule(
        string from, string to, int minutes,
        StageHandoffTimeBasis basis = StageHandoffTimeBasis.ELAPSED,
        bool? active = true, string factory = Factory)
        => new()
        {
            FactoryCode = factory,
            FromStageCode = from,
            ToStageCode = to,
            WaitMinutes = minutes,
            TimeBasis = basis,
            IsActive = active
        };

    private static ConstraintContext Ctx(params StageHandoffWaitRule[] rules)
    {
        var ctx = new ConstraintContext();
        ctx.Request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            StrategySnapshot = new SolverStrategySnapshot { StageHandoffWaitRules = rules }
        };
        return ctx;
    }

    private static DateTime Apply(
        ConstraintContext ctx, string toStage, List<FinalTaskDraft> tasks, DateTime? earliestStart = null)
        => new PhaseTwoInitialScheduler()
            .ApplyStageHandoffWait(earliestStart ?? T0, Demand(), toStage, tasks, ctx);

    /// <summary>ELAPSED 命中：ToStage 起点 = FromStage 最晚结束 + WaitMinutes。</summary>
    [Fact]
    public void ELAPSED命中_起点抬到FromStage最晚结束加等待()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S2", 90)), "S2", tasks);

        Assert.Equal(T0.AddHours(2).AddMinutes(90), start);
    }

    /// <summary>基准取**全部** FromStage Task 的最晚结束，而不是最后一个/第一个。</summary>
    [Fact]
    public void 取FromStage全部Task的最晚结束()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("S1", T0.AddHours(1)),
            Task("S1", T0.AddHours(5)),   // 最晚
            Task("S1", T0.AddHours(3)),
        };
        var start = Apply(Ctx(Rule("S1", "S2", 30)), "S2", tasks);

        Assert.Equal(T0.AddHours(5).AddMinutes(30), start);
    }

    /// <summary>反例：ToStage 不匹配 ⇒ 起点不变。</summary>
    [Fact]
    public void ToStage不匹配_起点不变()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S9", 90)), "S2", tasks);

        Assert.Equal(T0, start);
    }

    /// <summary>反例：厂代码不同 ⇒ 不命中（键域必须是需求自带 FactoryCode）。</summary>
    [Fact]
    public void 厂代码不同_不命中()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S2", 90, factory: "F2")), "S2", tasks);

        Assert.Equal(T0, start);
    }

    /// <summary>反例：IsActive 非 true（null / false）⇒ 不参与匹配。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void 未启用规则不参与匹配(bool? active)
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S2", 90, active: active)), "S2", tasks);

        Assert.Equal(T0, start);
    }

    /// <summary>方向独立：只配了 S1→S2，不得反向影响 S2→S1。</summary>
    [Fact]
    public void 方向独立_反向不命中()
    {
        var tasks = new List<FinalTaskDraft> { Task("S2", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S2", 90)), "S1", tasks);

        Assert.Equal(T0, start);
    }

    /// <summary>多规则命中 ⇒ 取最晚（只抬不降）。</summary>
    [Fact]
    public void 多规则命中取最晚()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("S1", T0.AddHours(1)),
            Task("S0", T0.AddHours(4)),
        };
        var start = Apply(Ctx(Rule("S1", "S2", 30), Rule("S0", "S2", 120)), "S2", tasks);

        // S0: 4h + 120min = 6h ；S1: 1h + 30min = 1.5h ⇒ 取 6h
        Assert.Equal(T0.AddHours(6), start);
    }

    /// <summary>基准已晚于候选 ⇒ 只抬不降（起点不得被拉回）。</summary>
    [Fact]
    public void 只抬不降()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(1)) };
        var start = Apply(Ctx(Rule("S1", "S2", 30)), "S2", tasks, earliestStart: T0.AddHours(10));

        Assert.Equal(T0.AddHours(10), start);
    }

    /// <summary>
    /// `WORK` 基准：**不得静默按 ELAPSED 处理** ⇒ 起点不变，且登记 `WORK_NO_CALENDAR`。
    /// （当前载荷无「日历级别」，我方不猜）
    /// </summary>
    [Fact]
    public void WORK基准_不应用且留痕()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var ctx = Ctx(Rule("S1", "S2", 90, StageHandoffTimeBasis.WORK));

        var start = Apply(ctx, "S2", tasks);

        Assert.Equal(T0, start);   // 未被当成 90 分钟 ELAPSED
        var note = Assert.Single(ctx.TraceNotes);
        Assert.Equal("WORK_NO_CALENDAR", note.ReasonCode);
        Assert.Contains("未应用", note.Message);
    }

    /// <summary>空规则集 ⇒ 逐字返回原值（零行为变化）。</summary>
    [Fact]
    public void 空规则集_原值返回()
    {
        var tasks = new List<FinalTaskDraft> { Task("S1", T0.AddHours(2)) };
        var ctx = Ctx();
        var start = Apply(ctx, "S2", tasks);

        Assert.Equal(T0, start);
        Assert.Empty(ctx.TraceNotes);
    }

    /// <summary>本批没有 FromStage 的 Task ⇒ 无基准可加，起点不变。</summary>
    [Fact]
    public void 本批无FromStage任务_不加等待()
    {
        var tasks = new List<FinalTaskDraft> { Task("S9", T0.AddHours(2)) };
        var start = Apply(Ctx(Rule("S1", "S2", 90)), "S2", tasks);

        Assert.Equal(T0, start);
    }
}