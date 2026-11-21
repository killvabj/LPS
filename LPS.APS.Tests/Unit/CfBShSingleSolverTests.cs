using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 v1.5（2026-11-19）§三 1-CF-B**：同域 SH「一次 Solver」硬约束——
/// 目标实际消耗不得早于**源厂最后必要批次完工 + 跨厂 LT**；硬边缺失时**不得悄悄当即期可用**。
///
/// <para>夹具：目标需求 T（净量 100）的物料切片为 `TimeBasis=SH_LINKED`（2号位 仅在已建
/// `MaterialRequirementLink` 时发它，`AvailableTime=planningStart` 只是占位）；
/// 源厂两批 40/60 为 T 的子需求（各一条 Task），跨物料 Lag = 120 分钟。</para>
///
/// <para>纯内存、直调 <see cref="PhaseFiveCompression.ValidateHardResultForTest"/>，不碰数据库。</para>
/// </summary>
public sealed class CfBShSingleSolverTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0);
    private const decimal ShLagMinutes = 120m;

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
        ConstraintContext ctx,
        string targetKey)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks, shares, new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = demands },
            ctx,
            new HashSet<string>(),
            new Dictionary<string, List<(string, decimal)>>());

    private static ConstraintContext ShContext(bool withEdges, string firstChild, string secondChild,
        decimal lagMinutes = ShLagMinutes)
    {
        var ctx = new ConstraintContext();
        // 目标 T 的物料切片：SH_LINKED（时间为 planningStart 占位，真实时点须由源 Task 推导）
        ctx.MaterialAvailability[1] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 100m, AvailableTime = T0, TimeBasis = "SH_LINKED", SourceType = "INTER_FACTORY_ORDER" },
        };
        if (withEdges)
        {
            ctx.CrossMaterialParentToChildren["T"] = new List<string> { firstChild, secondChild };
            ctx.CrossMaterialLagMinutes[("T", firstChild)] = lagMinutes;
            ctx.CrossMaterialLagMinutes[("T", secondChild)] = lagMinutes;
        }
        return ctx;
    }

    [Theory(DisplayName = "1-CF-B：SH 40/60 两种顺序，目标不早于末批+LT ⇒ 通过")]
    [InlineData("S40", "S60")]
    [InlineData("S60", "S40")]
    public void Sh40And60_TargetAfterLastBatchPlusLag_ShouldPass(string first, string second)
    {
        // 第一批 T0~T0+2h；第二批 T0+4h~T0+6h ⇒ 末批完工 T0+6h + 120min = T0+8h
        var tasks = new List<FinalTaskDraft>
        {
            Task("B1", first, first == "S40" ? 40m : 60m, T0, 2),
            Task("B2", second, second == "S40" ? 40m : 60m, T0.AddHours(4), 2),
            Task("TT", "T", 100m, T0.AddHours(8), 2),
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("B1", 2, first == "S40" ? 40m : 60m),
            Share("B2", 3, second == "S40" ? 40m : 60m),
            Share("TT", 1, 100m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("T", 100m, 1),
            Demand(first, first == "S40" ? 40m : 60m, 2),
            Demand(second, second == "S40" ? 40m : 60m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ShContext(true, first, second), "T");

        Assert.True(valid, "目标在末批完工 + LT 之后消耗，应放行。" + err);
        Assert.Equal(3, tasks.Count);
    }

    [Fact(DisplayName = "1-CF-B：目标早于末批+LT ⇒ 判缺料剔除（不得假齐料）")]
    public void Sh_TargetBeforeLastBatchPlusLag_ShouldBeRemoved()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("B1", "S40", 40m, T0, 2),
            Task("B2", "S60", 60m, T0.AddHours(4), 2),
            Task("TT", "T", 100m, T0.AddHours(7), 2),   // 早 1 小时
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("B1", 2, 40m), Share("B2", 3, 60m), Share("TT", 1, 100m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("T", 100m, 1), Demand("S40", 40m, 2), Demand("S60", 60m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ShContext(true, "S40", "S60"), "T");

        Assert.True(valid, "缺料只剔该 Task 转未排，整域不废。" + err);
        Assert.Equal(2, tasks.Count);
        Assert.DoesNotContain(tasks, t => t.FinalDraftId == "TT");
    }

    [Fact(DisplayName = "1-CF-C：24h 兜底（Lag=1440）同样硬生效，目标不得早于末批+24h")]
    public void Sh_24HourFallbackLag_EnforcedAsHardConstraint()
    {
        // 末批完工 T0+6h + 1440min（24h）= T0+30h；目标 T0+29h 必须被剔除
        var tasks = new List<FinalTaskDraft>
        {
            Task("B1", "S40", 40m, T0, 2),
            Task("B2", "S60", 60m, T0.AddHours(4), 2),
            Task("TT", "T", 100m, T0.AddHours(29), 2),
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("B1", 2, 40m), Share("B2", 3, 60m), Share("TT", 1, 100m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("T", 100m, 1), Demand("S40", 40m, 2), Demand("S60", 60m, 3),
        };

        var (valid, err) = Run(tasks, shares, demands, ShContext(true, "S40", "S60", lagMinutes: 1440m), "T");

        Assert.True(valid, "24h 兜底作为 Lag 必须是硬约束。" + err);
        Assert.DoesNotContain(tasks, t => t.FinalDraftId == "TT");
    }

    [Fact(DisplayName = "1-CF-B：SH_LINKED 但缺硬边 ⇒ 不得悄悄当期可用，判缺料剔除")]
    public void ShLinkedWithoutEdge_ShouldNotBeTreatedAsImmediatelyAvailable()
    {
        // 无 CrossMaterialParentToChildren（硬边缺失）⇒ 占位 planningStart 不得当可用
        var tasks = new List<FinalTaskDraft>
        {
            Task("B1", "S40", 40m, T0, 2),
            Task("TT", "T", 100m, T0, 2),               // 计划期起点即开工
        };
        var shares = new List<AllocationTaskShare>
        {
            Share("B1", 2, 40m), Share("TT", 1, 100m),
        };
        var demands = new List<LogicalProductionDemand>
        {
            Demand("T", 100m, 1), Demand("S40", 40m, 2),
        };

        var (valid, err) = Run(tasks, shares, demands, ShContext(false, "S40", "S60"), "T");

        Assert.True(valid, "缺硬边不得悄悄当即期可用（Task 应被剔除转未排）。" + err);
        Assert.Single(tasks);
        Assert.DoesNotContain(tasks, t => t.FinalDraftId == "TT");
    }
}