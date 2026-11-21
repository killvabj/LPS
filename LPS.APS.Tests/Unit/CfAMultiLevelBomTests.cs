using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 v1.5（2026-11-19）§三 1-CF-A 第 3 条**：多层 BOM 下「子件真实排完 ⇒ 父件按合法数量/Lag 才齐料」。
///
/// <para>场景：A（父）← B（中）← C（孙），两条跨物料边各带 Lag；A/B 的物料切片为
/// `TimeBasis=UNRESOLVED`（数量真实、时间未定，`AvailableTime` 仅为计划期末上界）。</para>
///
/// <para>判据：父件任务的开工不得早于「子件 Task 完工 + 跨物料 Lag」；提前 ⇒ 该 Task 判缺料剔除转未排
/// （不是整域废）。同时区分：**时间尚未计算**（子件已排、时间可推导 ⇒ 按推导下界判）与
/// **上游未排/无边**（时间不可判 ⇒ 验证层不据此误删，调度侧由 Phase2 显式判不足）。</para>
///
/// <para>纯内存、直调 <see cref="PhaseFiveCompression.ValidateHardResultForTest"/>。</para>
/// </summary>
public sealed class CfAMultiLevelBomTests
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

    private static FinalTaskDraft Task(string id, string demandKey, decimal qty, DateTime start, int hours)
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
            PlannedEndTime = start.AddHours(hours),
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

    private static ConstraintContext ChainContext()
    {
        var ctx = new ConstraintContext();
        // A ← B：Lag 60min；B ← C：Lag 60min
        ctx.CrossMaterialParentToChildren["A"] = new List<string> { "B" };
        ctx.CrossMaterialParentToChildren["B"] = new List<string> { "C" };
        ctx.CrossMaterialLagMinutes[("A", "B")] = 60m;
        ctx.CrossMaterialLagMinutes[("B", "C")] = 60m;
        // A/B 的物料切片：数量真实、时间未定（上界 = 计划期末）
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(30), TimeBasis = "UNRESOLVED" },
        };
        ctx.MaterialAvailability[2] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(30), TimeBasis = "UNRESOLVED" },
        };
        return ctx;
    }

    [Fact(DisplayName = "1-CF-A：多层BOM 子件排完+Lag 后父件才齐料 ⇒ 通过")]
    public void MultiLevel_ChildFinishedThenParent_ShouldPass()
    {
        // C 完工 T0+2h ⇒ B 下界 T0+3h，实际 T0+3h 开工、T0+5h 完工
        // B 完工 T0+5h ⇒ A 下界 T0+6h，实际 T0+6h 开工
        var tasks = new List<FinalTaskDraft>
        {
            Task("CT", "C", 10m, T0, 2),
            Task("BT", "B", 10m, T0.AddHours(3), 2),
            Task("AT", "A", 10m, T0.AddHours(6), 2),
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("CT", 3, 10m), Share("BT", 2, 10m), Share("AT", 1, 10m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("A", 10m, 1), Demand("B", 10m, 2), Demand("C", 10m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ChainContext());

        Assert.True(valid, "父件在子件完工 + Lag 之后开工，应放行。" + err);
        Assert.Equal(3, tasks.Count);
    }

    [Fact(DisplayName = "1-CF-A：多层BOM 父件早于子件完工+Lag ⇒ 判缺料剔除（不得假齐套）")]
    public void MultiLevel_ParentStartsBeforeChildPlusLag_ShouldBeRemoved()
    {
        // A 在 T0+4h 就开工，但 B 完工 T0+5h + Lag 60min ⇒ 下界 T0+6h ⇒ A 必须被剔除转未排
        var tasks = new List<FinalTaskDraft>
        {
            Task("CT", "C", 10m, T0, 2),
            Task("BT", "B", 10m, T0.AddHours(3), 2),
            Task("AT", "A", 10m, T0.AddHours(4), 2),
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("CT", 3, 10m), Share("BT", 2, 10m), Share("AT", 1, 10m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("A", 10m, 1), Demand("B", 10m, 2), Demand("C", 10m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ChainContext());

        Assert.True(valid, "缺料只剔除该 Task 转未排，整域不废（冻结 v1.7 §41）。" + err);
        Assert.Equal(2, tasks.Count);                       // A 被剔除
        Assert.DoesNotContain(tasks, t => t.FinalDraftId == "AT");
    }

    [Fact(DisplayName = "1-CF-A：多层BOM 子件未排（无下界）⇒ 时间不可判，验证层不误删")]
    public void MultiLevel_ChildUnscheduled_TimeUnknown_NotRemovedByValidation()
    {
        // C 没有 Task（上游未排/缺边）⇒ A 的时间下界不可推导。
        // 此处验证层不得把"时间未定"当成"缺料"删 Task；真正的拦截在调度侧 Phase2（显式判不足）。
        var tasks = new List<FinalTaskDraft>
        {
            Task("BT", "B", 10m, T0.AddHours(3), 2),
            Task("AT", "A", 10m, T0.AddHours(6), 2),
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("BT", 2, 10m), Share("AT", 1, 10m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("A", 10m, 1), Demand("B", 10m, 2), Demand("C", 10m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ChainContext());

        Assert.True(valid, "时间不可判时验证层不得误删 Task（缺边/未排应由调度侧显式处理）。" + err);
        Assert.Equal(2, tasks.Count);
    }
}