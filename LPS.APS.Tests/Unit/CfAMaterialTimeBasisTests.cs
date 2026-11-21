using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 v1.5（2026-11-19）§三 1-CF-A**：物料切片 `TimeBasis` 的消费。
///
/// <para>2号位 已发（`PeggingOrchestrator:18176-18237`）：`KNOWN`＝真实时间；`CURRENT`＝现货按计划期起点；
/// `UNRESOLVED` / `SH_TIME_UNRESOLVED`＝**数量真实、时间未定**，其 `AvailableTime` 只是**计划期末的保守上界**。</para>
///
/// <para>纠正前：1号位 四处消费点都不读 `TimeBasis`，直接把上界当"最早可用" ⇒ 内制件/在制件被推到期末
/// （或反过来被误判缺料）。纠正后：时间未定切片的时点改用**子件 Task 推导下界**（完工 + 跨物料 Lag）；
/// 下界不可得 ⇒ 时间不可判，**不得据此删 Task，也不得当已具料**。</para>
///
/// <para>纯内存、直调 <see cref="PhaseFiveCompression.ValidateHardResultForTest"/>，不碰数据库。</para>
/// </summary>
public sealed class CfAMaterialTimeBasisTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0);

    private static LogicalProductionDemand Demand(string key, decimal qty, long alloc)
        => new()
        {
            LogicalDemandKey = key,
            DemandKey = key,
            AllocationSequence = alloc,
            MaterialId = 1,
            FactoryId = 1,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = "S1",
        };

    private static FinalTaskDraft Task(string id, string demandKey, decimal qty, DateTime start)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = demandKey,
            MaterialId = 1,
            FactoryId = 1,
            StageCode = "S1",
            OperationCode = "OP10",
            Quantity = qty,
            PlannedProcessQty = qty,
            PlannedStartTime = start,
            PlannedEndTime = start.AddHours(2),
        };

    private static AllocationTaskShare Share(string taskId, long alloc, decimal qty)
        => new() { FinalDraftId = taskId, AllocationSequence = alloc, ComponentQty = qty };

    private static (bool Valid, string Error) Run(
        List<FinalTaskDraft> tasks,
        List<AllocationTaskShare> shares,
        List<LogicalProductionDemand> demands,
        ConstraintContext ctx)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks, shares, new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = demands },
            ctx,
            new HashSet<string>(),
            new Dictionary<string, List<(string, decimal)>>());

    [Fact(DisplayName = "1-CF-A：UNRESOLVED 切片按子件Task推导下界判时序，不按计划期末上界")]
    public void UnresolvedSlice_UsesTaskDerivedFloor()
    {
        // 父件 P1 开工 T0+4h；子件 C1 完工 T0+2h；跨物料 Lag 60min ⇒ 下界 T0+3h ⇒ 合规。
        // 整改前把切片上界（T0+30d）当最早可用 ⇒ 必判缺料并删 Task。
        var tasks = new List<FinalTaskDraft>
        {
            Task("PT", "P1", 10m, T0.AddHours(4)),
            Task("CT", "C1", 10m, T0),
        };
        var shares = new List<AllocationTaskShare> { Share("PT", 1, 10m), Share("CT", 2, 10m) };
        var demands = new List<LogicalProductionDemand> { Demand("P1", 10m, 1), Demand("C1", 10m, 2) };

        var ctx = new ConstraintContext();
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(30), TimeBasis = "UNRESOLVED" },
        };
        ctx.CrossMaterialParentToChildren["P1"] = new List<string> { "C1" };
        ctx.CrossMaterialLagMinutes[("P1", "C1")] = 60m;

        var (valid, err) = Run(tasks, shares, demands, ctx);

        Assert.True(valid, "时间未定切片应按子件 Task 推导下界判时序，不得用计划期末上界。" + err);
        Assert.Equal(2, tasks.Count);   // 父件未被删
    }

    [Fact(DisplayName = "1-CF-A：UNRESOLVED 切片无子件边 ⇒ 时间不可判，不据此删 Task")]
    public void UnresolvedSlice_NoFloor_NotTreatedAsShortage()
    {
        var tasks = new List<FinalTaskDraft> { Task("PT", "P1", 10m, T0) };
        var shares = new List<AllocationTaskShare> { Share("PT", 1, 10m) };
        var demands = new List<LogicalProductionDemand> { Demand("P1", 10m, 1) };

        var ctx = new ConstraintContext();
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(30), TimeBasis = "UNRESOLVED" },
        };

        var (valid, err) = Run(tasks, shares, demands, ctx);

        Assert.True(valid, "时间未定且无下界时不得据此删 Task（也不得把上界当可用）。" + err);
        Assert.Single(tasks);
    }

    [Fact(DisplayName = "1-CF-A：KNOWN 切片晚于开工 ⇒ 仍判缺料并剔除（回归护栏）")]
    public void KnownSlice_LaterThanStart_StillInsufficient()
    {
        var tasks = new List<FinalTaskDraft> { Task("PT", "P1", 10m, T0) };
        var shares = new List<AllocationTaskShare> { Share("PT", 1, 10m) };
        var demands = new List<LogicalProductionDemand> { Demand("P1", 10m, 1) };

        var ctx = new ConstraintContext();
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(30), TimeBasis = "KNOWN" },
        };

        var (valid, err) = Run(tasks, shares, demands, ctx);

        Assert.True(valid, "缺料 Task 应转未排、整域不废（冻结 v1.7 §41）。" + err);
        Assert.Empty(tasks);
    }

    /// <summary>
    /// **2026-11-20 新发现的口径缺口（先立例、后裁决，不擅自改行为）**：`SH_LINKED` 切片**拿不到硬边下界**时，
    /// 现行代码走「契约缺失 ⇒ 不得把 planningStart 占位当可用」⇒ 取 `DateTime.MaxValue` ⇒ check#5 **删 Task**
    /// （并沿依赖级联）。而 2号位 2026-11-20 件 §二 写的是「1号位 只能判'时间不可判'」——**两边理解不一致**。
    ///
    /// 本用例按**现行为**写死断言（删 Task），并标记 Skip：0号位 裁定「删 / 不删」后要么取消 Skip、
    /// 要么改断言 —— 避免在裁决前就用单测把某一种解释钉成"已验收口径"。
    /// </summary>
    [Fact(
        DisplayName = "1-CF-B.4（待0号位裁）：SH_LINKED 无硬边 现行为=删Task(契约缺失FailClosed)",
        Skip = "待 0号位 裁决：SH_LINKED 拿不到硬边时是「删 Task」还是「判时间不可判但不删」；裁决后取消 Skip 或改断言。")]
    public void ShLinked_NoHardEdge_CurrentBehavior_DeletesTask()
    {
        var tasks = new List<FinalTaskDraft> { Task("PT", "P1", 10m, T0) };
        var shares = new List<AllocationTaskShare> { Share("PT", 1, 10m) };
        var demands = new List<LogicalProductionDemand> { Demand("P1", 10m, 1) };

        var ctx = new ConstraintContext();
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            // SH_LINKED：时间只是计划期起点**占位**，真实可用要由已排源 Task + Lag 推导；此处**故意不给硬边**。
            new() { Quantity = 10m, AvailableTime = T0, TimeBasis = "SH_LINKED" },
        };

        var _ = Run(tasks, shares, demands, ctx);

        // 现行为：无硬边 ⇒ 不把占位当可用 ⇒ 物料不足 ⇒ 删除该 Task。
        Assert.Empty(tasks);
    }
}