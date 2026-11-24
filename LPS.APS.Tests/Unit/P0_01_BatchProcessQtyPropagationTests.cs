using System;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-01 投入侧按批执行量分配**（0号位 v1.1 §四 P0-01，2026-11-21）：
/// 「`Phase2` 整数 4+4+5 仍可能将投入侧分成三份均量，**5 件批资源分钟与实产量失配**」
///
/// <para>冻结口径（0号位 v1.7 N-11）：「良率投入侧 `PlannedProcessQty` 按**真实实际执行量**及 Stage 规则计算，
/// 允许计算中的小数」「MES 实际执行净量及每个新增批为正整数」「不得全字段 Round」。</para>
///
/// <para>旧实现只对**第二类（原生小数净量）**做按量分配；**第一类（整数净量）仍均分**
/// ⇒ 13 拆 4+4+5 时三批各拿 1/3 投料。本文件把「5 件批必须拿到比 4 件批更多的投料」钉住。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class P0_01_BatchProcessQtyPropagationTests
{
    [Fact(DisplayName = "P0-01正例：13 拆 3 批（4+4+5），投入侧按执行量分配 ⇒ 5 件批 > 4 件批，Σ 守恒")]
    public void IntegerSplit_UnequalBatches_ProcessQtyFollowsBatchQty()
    {
        var demand = Demand(net: 13m, proc: 14.754m, isContinuation: false);

        var f = PhaseTwoInitialScheduler.BuildBatchFormationForTest(demand, null, 3);

        Assert.True(f.IsLegal, f.ConflictReason);
        Assert.Equal(3, f.Batches.Count);
        // 余数按「逐批 +1 给**前**批」实现（与本仓既有口径一致：Phase4 同一写法）
        //   ⇒ 13 拆 3 批实际是 5+4+4（Σ=13），与 0号位 v1.1 里「4+4+5」是同一多重集、不同次序。
        Assert.Equal(new[] { 4m, 4m, 5m }, f.Batches.Select(b => b.NetOutputQty).OrderBy(v => v).ToArray());

        // 投入量必须跟着**本批自己的净量**走：5 件批 > 任一 4 件批。
        var maxNet = f.Batches.OrderByDescending(b => b.NetOutputQty).First();
        var minNet = f.Batches.OrderBy(b => b.NetOutputQty).First();
        Assert.True(maxNet.PlannedProcessQty > minNet.PlannedProcessQty,
            $"5 件批投入 {maxNet.PlannedProcessQty} 必须 > 4 件批投入 {minNet.PlannedProcessQty}");
        Assert.Equal(14.754m, f.Batches.Sum(b => b.PlannedProcessQty), 3);   // 总量守恒（末批补余）
    }

    [Fact(DisplayName = "P0-01反证：13 拆 4+4+5 时投入侧不得三份均分")]
    public void IntegerSplit_ProcessQtyMustNotBeFlat()
    {
        var demand = Demand(net: 13m, proc: 14.754m, isContinuation: false);

        var f = PhaseTwoInitialScheduler.BuildBatchFormationForTest(demand, null, 3);
        var p = f.Batches.Select(b => b.PlannedProcessQty).ToArray();

        Assert.False(p.All(v => Math.Abs(v - p[0]) < 0.0001m),
            $"投入侧被均分成了三份 [{string.Join(", ", p)}] —— 这正是 P0-01 点名的缺陷形态");
    }

    [Fact(DisplayName = "P0-01第二条：14.754 拆 3 批 ⇒ 净量 5+5+5（Σ=15），投入侧等量（净量相等，不是缺陷）")]
    public void DecimalNet_EqualIntegerBatches_ProcessQtyEqual()
    {
        var demand = Demand(net: 14.754m, proc: 14.754m, isContinuation: false);

        var f = PhaseTwoInitialScheduler.BuildBatchFormationForTest(demand, null, 3);

        Assert.True(f.IsLegal, f.ConflictReason);
        Assert.Equal(new[] { 5m, 5m, 5m }, f.Batches.Select(b => b.NetOutputQty).ToArray());
        Assert.Equal(15m, f.Batches.Sum(b => b.NetOutputQty));

        var p = f.Batches.Select(b => b.PlannedProcessQty).ToArray();
        Assert.All(p, v => Assert.Equal(p[0], v, 3));   // 净量相等 ⇒ 投入等量是正确的
        Assert.Equal(14.754m, p.Sum(), 3);
    }

    [Fact(DisplayName = "P0-01不回归：不拆批（1 批）时投入侧 = 需求 PlannedProcessQty 原值")]
    public void SingleBatch_ProcessQtyUnchanged()
    {
        var demand = Demand(net: 13m, proc: 14.754m, isContinuation: false);

        var f = PhaseTwoInitialScheduler.BuildBatchFormationForTest(demand, null, 1);

        Assert.True(f.IsLegal, f.ConflictReason);
        var only = Assert.Single(f.Batches);
        Assert.Equal(13m, only.NetOutputQty);
        Assert.Equal(14.754m, only.PlannedProcessQty, 4);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static LogicalProductionDemand Demand(decimal net, decimal proc, bool isContinuation) => new()
    {
        LogicalDemandKey = "P001_1",
        PlanVersionId = 1,
        DomainKey = "FAMILY_TEST",
        AllocationSequence = 1,
        DemandKey = "D1",
        MaterialId = 1,
        FactoryId = 1,
        StartStageCode = "S1",
        NetOutputQty = net,
        PlannedProcessQty = proc,
        RequiredAvailableTime = new DateTime(2026, 12, 31),
        DemandSequence = 1,
        IsContinuation = isContinuation,
        QuantityAuthority = QuantityAuthorityValues.None,
    };
}
