using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Algorithms;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T03 共享 Task 份额逆追**（0号位 v1.8 §六：1号位先做消费算法）。
///
/// <para>被测对象：<see cref="SharedTaskShareTracer"/>。
/// 该算法**尚未接线**到任何生产出口（需要一个"AllocationSequence → 根单键"的映射，
/// 按 0号位 §四 D-01 由 2号位 提供）。本文件锁**算法口径**。</para>
///
/// <para>铁律：①份额按 `AllocationTaskShare` 归属，**不按 Task 的 `SourceDraftId` 反推**；
/// ②映射缺失**不得静默丢量**，必须计入 `UnattributedQty` 并列键；③同一上游被多路需要 ⇒ **累加**。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class ExT03SharedTaskShareTracerTests
{
    // ─────────────── 一、按根单归属 ───────────────

    [Fact(DisplayName = "EX-T03①：一个共享 Task 被两个根单的 Allocation 分摊 ⇒ 各拿各的份额")]
    public void SharedTask_TwoRoots_EachGetsOwnShare()
    {
        var shares = new List<AllocationTaskShare>
        {
            Share("T1", 101, 6m),
            Share("T1", 202, 4m),
        };
        var map = new Dictionary<long, string> { [101] = "ROOT-A", [202] = "ROOT-B" };

        var r = SharedTaskShareTracer.AttributeByRoot(shares, map);

        Assert.Equal(6m, r.ByRoot["ROOT-A"]);
        Assert.Equal(4m, r.ByRoot["ROOT-B"]);
        Assert.Equal(0m, r.UnattributedQty);
        Assert.Empty(r.UnmappedAllocationSequences);
    }

    [Fact(DisplayName = "EX-T03②：同一根单的两个 Allocation ⇒ 累加到同一根单")]
    public void SameRoot_TwoAllocations_Accumulate()
    {
        var shares = new List<AllocationTaskShare>
        {
            Share("T1", 101, 6m),
            Share("T2", 102, 3m),
            Share("T3", 201, 9m),
        };
        var map = new Dictionary<long, string> { [101] = "ROOT-A", [102] = "ROOT-A", [201] = "ROOT-B" };

        var r = SharedTaskShareTracer.AttributeByRoot(shares, map);

        Assert.Equal(9m, r.ByRoot["ROOT-A"]);
        Assert.Equal(9m, r.ByRoot["ROOT-B"]);
    }

    [Fact(DisplayName = "EX-T03③：映射缺失 ⇒ 计入未归属并列键，**不得静默丢量**")]
    public void UnmappedAllocation_ReportedNotDropped()
    {
        var shares = new List<AllocationTaskShare>
        {
            Share("T1", 101, 6m),
            Share("T1", 999, 4m),   // 999 不在映射里
        };
        var map = new Dictionary<long, string> { [101] = "ROOT-A" };

        var r = SharedTaskShareTracer.AttributeByRoot(shares, map);

        Assert.Equal(6m, r.ByRoot["ROOT-A"]);
        Assert.Equal(4m, r.UnattributedQty);
        Assert.Equal(new long[] { 999 }, r.UnmappedAllocationSequences);

        // 守恒：归属 + 未归属 == 全部份额
        Assert.Equal(shares.Sum(s => s.ComponentQty), r.ByRoot.Values.Sum() + r.UnattributedQty);
    }

    [Fact(DisplayName = "EX-T03④：空份额 ⇒ 空结果（不抛异常）")]
    public void EmptyShares_ReturnsEmpty()
    {
        var r = SharedTaskShareTracer.AttributeByRoot(
            Array.Empty<AllocationTaskShare>(),
            new Dictionary<long, string>());

        Assert.Empty(r.ByRoot);
        Assert.Equal(0m, r.UnattributedQty);
        Assert.Empty(r.UnmappedAllocationSequences);
    }

    // ─────────────── 二、最后必要数量链 ───────────────

    [Fact(DisplayName = "EX-T03⑤：两层链 A→B→C ⇒ 从 C 逆追得到 B 与 A 各自被需要的量")]
    public void TwoLayerChain_TracesBothUpstreams()
    {
        var drafts = new List<FinalTaskPeggingDraft>
        {
            Edge("A", "B", 10m),
            Edge("B", "C", 7m),
        };

        var required = SharedTaskShareTracer.TraceLastNecessaryUpstream("C", drafts);

        Assert.Equal(7m, required["B"]);
        Assert.Equal(10m, required["A"]);
        Assert.False(required.ContainsKey("C"));   // 起点不是上游
    }

    [Fact(DisplayName = "EX-T03⑥：菱形（B1/B2 都需要 A）⇒ A 的量按多路**累加**，不取 max")]
    public void Diamond_AccumulatesNotMax()
    {
        var drafts = new List<FinalTaskPeggingDraft>
        {
            Edge("A", "B1", 10m),
            Edge("A", "B2", 20m),
            Edge("B1", "C", 1m),
            Edge("B2", "C", 1m),
        };

        var required = SharedTaskShareTracer.TraceLastNecessaryUpstream("C", drafts);

        Assert.Equal(1m, required["B1"]);
        Assert.Equal(1m, required["B2"]);
        Assert.Equal(30m, required["A"]);   // 10 + 20，不是 max(10,20)
    }

    [Fact(DisplayName = "EX-T03⑦：成环 ⇒ 抛异常（宁可显式失败，也不返回残缺链）")]
    public void Cycle_Throws()
    {
        var drafts = new List<FinalTaskPeggingDraft>
        {
            Edge("A", "B", 1m),
            Edge("B", "A", 1m),
        };

        Assert.Throws<InvalidOperationException>(
            () => SharedTaskShareTracer.TraceLastNecessaryUpstream("B", drafts));
    }

    [Fact(DisplayName = "EX-T03⑧：起点无上游 / 空边集 ⇒ 空链")]
    public void NoUpstream_ReturnsEmpty()
    {
        Assert.Empty(SharedTaskShareTracer.TraceLastNecessaryUpstream("X", Array.Empty<FinalTaskPeggingDraft>()));
        Assert.Empty(SharedTaskShareTracer.TraceLastNecessaryUpstream("X",
            new List<FinalTaskPeggingDraft> { Edge("A", "B", 1m) }));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static AllocationTaskShare Share(string taskId, long allocationSequence, decimal qty)
        => new() { FinalDraftId = taskId, AllocationSequence = allocationSequence, ComponentQty = qty };

    private static FinalTaskPeggingDraft Edge(string upstream, string downstream, decimal qty)
        => new()
        {
            UpstreamFinalDraftId = upstream,
            DownstreamFinalDraftId = downstream,
            Quantity = qty,
            UOM = "PCS",
        };
}
