using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Phase5 物料校验第 5 项的「所需数量」应当是**本 Task 对需求的份额**，不是需求整单 NetOutputQty。
///
/// <para>真因（FAMILY_CYL / PV999549 整域归零）：需求拆批/合批时，每一条子 Task 在 Phase5 被要求
/// 「物料累计凑满整单」才许可开工，而调度侧是按该批份额查物料 ⇒ 物料稍不足整单即误判「早于物料」、
/// 整域清空。整单不拆时二者相等 ⇒ 本修正对未拆/未合批需求零行为变化。</para>
///
/// <para>判别点：需求 10 拆成两份 5，物料两段 [5@T0, 5@T0+10d]——
///   按份额 5 查，首段即够（物料 T0 就到）；按整单 10 查，要到第二段 T0+10d 才够。
///   整改前：第 5 项用整单 10 ⇒ 误拒；整改后：用份额 5 ⇒ 放行。反例：整单 10、料真晚到 ⇒ 仍拒（不误放宽）。</para>
///
/// <para>纯内存、直调 <see cref="PhaseFiveCompression.ValidateHardResultForTest"/>，不碰数据库。</para>
/// </summary>
public sealed class Phase5MaterialRequiredQtyShareTests
{
    private static readonly DateTime T0 = new DateTime(2026, 11, 18, 8, 0, 0);

    private static LogicalProductionDemand Demand(decimal qty, long alloc = 1L)
        => new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            DemandKey = "D1",
            AllocationSequence = alloc,
            MaterialId = 1,
            FactoryId = 1,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = "S1",
        };

    private static FinalTaskDraft Task(decimal qty, DateTime start)
        => new FinalTaskDraft
        {
            FinalDraftId = "T1",
            SourceDraftId = "D1",
            MaterialId = 1,
            FactoryId = 1,
            StageCode = "S1",
            OperationCode = "OP1",
            Quantity = qty,
            PlannedProcessQty = qty,
            PlannedStartTime = start,
            PlannedEndTime = start.AddHours(1),
        };

    private static ConstraintContext Context(long alloc, params (decimal Qty, DateTime At)[] segments)
    {
        var c = new ConstraintContext();
        c.MaterialAvailability[alloc] = new List<MaterialAvailabilitySegment>();
        foreach (var (qty, at) in segments)
        {
            c.MaterialAvailability[alloc].Add(new MaterialAvailabilitySegment { Quantity = qty, AvailableTime = at });
        }
        return c;
    }

    [Fact(DisplayName = "Phase5第5项：拆批Task按份额查物料（份额够即放行，不按整单误判）")]
    public void RequiredQty_UsesTaskShare_SoSplitBatchNotFalselyRejected()
    {
        // 需求 10，拆成 5 的一份（另有 5 未进本 Task）；物料 5@T0 + 5@T0+10d。
        var tasks = new List<FinalTaskDraft> { Task(5m, T0) };
        var request = new DomainSolveRequest { LogicalProductionDemands = new List<LogicalProductionDemand> { Demand(10m) } };
        var constraints = Context(1L, (5m, T0), (5m, T0.AddDays(10)));
        var unscheduled = new HashSet<string> { "D1" };   // 让 5 < 10 属「部分落定」，不触第 8 项数量闭合

        var (valid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, new List<AllocationTaskShare>(), new List<FinalTaskPeggingDraft>(),
            request, constraints, unscheduled, new Dictionary<string, List<(string, decimal)>>());

        // 整改前红：按整单 10 查，物料要到第二段 T0+10d 才够 ⇒ Task(T0) 早于 T0+10d ⇒ 误拒。
        Assert.True(valid, "按份额 5 查物料首段即够，拆批 Task 不得按整单 10 被判「早于物料」。" + error);
    }

    [Fact(DisplayName = "Phase5第5项：整单Task物料真晚到 → 剔除该Task转未排，不整域废")]
    public void RequiredQty_WholeDemand_LateMaterial_TaskExcluded_NotDomainFail()
    {
        // 需求 10 未拆，Task 就是整单 10；物料 10@T0+10d（真晚到/未闭合）。
        // 冻结 v1.42/v2.7 / v1.7 §41：缺料 → 阻断该需求、其它继续、Success+未排，不是整域废。
        var tasks = new List<FinalTaskDraft> { Task(10m, T0) };
        var request = new DomainSolveRequest { LogicalProductionDemands = new List<LogicalProductionDemand> { Demand(10m) } };
        var constraints = Context(1L, (10m, T0.AddDays(10)));

        var (valid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, new List<AllocationTaskShare>(), new List<FinalTaskPeggingDraft>(),
            request, constraints, new HashSet<string>(), new Dictionary<string, List<(string, decimal)>>());

        Assert.True(valid, "物料真晚到 → 该 Task 转未排、其余继续，校验应放行（不得整域废）。" + error);
        Assert.Empty(tasks);   // 该 Task 已被 check #5 剔除
    }
}