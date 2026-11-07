using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-07：**钉死 `SolverBatchBudget` 四档边界的当前行为**（0号位《54c687e 六项答复》Q5 末段：
/// 「建议增加 0、1、负数/空值及极端上限单测」）。
///
/// <para><b>⚠ 本文件只锁定「现状」，不代表我方认可这套语义。</b>
/// 现状有一处可疑（已提请 3号位/0号位 裁定，见下），在裁定前用测试把行为钉住，
/// 免得后续任何改动无声改变它。</para>
///
/// <para><b>现状（2026-11-07 实测）</b>：</para>
/// <list type="bullet">
///   <item>`MaxOptimizationSplitCount = 0` ⇒ **不施加上限** —— 使用点 `PhaseTwoInitialScheduler.cs:6946`
///     是 `if (x &gt; 0) { 截断 }`，0 直接跳过 ⇒ 只剩 `MaxBatchCandidates` 兜底。
///     **「0 = 不限制」而不是「0 = 禁用优化」**。</item>
///   <item>`MaxOptimizationSplitCount &lt; 0` ⇒ **回落默认 3** —— `FromSplitParams:6771` 三元表达式
///     `&lt; 0 ? Default : 原值`。</item>
/// </list>
///
/// <para><b>⚠ 待裁</b>：`0` 放任、`负数` 收紧 —— 两者方向是**反的**。通常语义应为
/// 「0 = 禁用优化拆批」、「负数 = 非法值回落默认」。因 `MaxBatchCandidates = 8` 兜底，
/// 这不是性能炸弹，是**语义歧义**：3号位 若配 0 想表达「不做优化拆批」，实际会多拿候选。
/// 裁定落地后，本文件的 <c>Zero_MeansUnbounded_TODAY</c> 用例应随之调整。</para>
/// </summary>
public sealed class SolverBatchBudgetSemanticsTests
{
    private const int M = 7001;
    private const int Dept = 1;

    /// <summary>构造一个能产出多个优化候选的需求：
    /// `Qty=100 / Min=1 / Max=10 / Preferred=2` ⇒ 经济参考批数 10、`{1,2,3}` 落在经济区间内、
    /// Preferred 附近 50、锚点 11。</summary>
    private static List<int> CandidateCounts(PhaseTwoInitialScheduler.SolverBatchBudget budget)
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            MaterialId = M,
            FactoryId = 1,
            AllocationSequence = 1,
            NetOutputQty = 100m,
            PlannedProcessQty = 100m,
            StartStageCode = "STAGE1",
        };
        var policy = new BatchPolicyRuleSnapshot
        {
            MaterialId = M,
            ProductionDepartmentId = Dept,
            MinExecutionBatchQty = 1m,
            MaxExecutionBatchQty = 10m,
            PreferredBatchQty = 2m,
            AllowSplit = true,
            AllowMerge = true,
        };

        var baseline = PhaseTwoInitialScheduler.FormExecutionBatches(demand, policy);
        Assert.True(baseline.IsLegal, baseline.ConflictReason);

        return PhaseTwoInitialScheduler
            .EnumerateLegalBatchPlanCandidates(demand, policy, baseline, budget)
            .Select(f => f.Batches.Count)
            .ToList();
    }

    [Fact(DisplayName = "预算语义 · MaxOptimizationSplitCount=1 ⇒ 基线 + 1 个优化候选")]
    public void One_LimitsToOneOptimizationCandidate()
    {
        var counts = CandidateCounts(new PhaseTwoInitialScheduler.SolverBatchBudget(1, 8, "test"));
        Assert.Equal(2, counts.Count);   // 基线 1 条 + 优化 1 条
    }

    [Fact(DisplayName = "预算语义 · MaxOptimizationSplitCount=3 ⇒ 基线 + 3 个优化候选")]
    public void Three_LimitsToThreeOptimizationCandidates()
    {
        var counts = CandidateCounts(new PhaseTwoInitialScheduler.SolverBatchBudget(3, 8, "test"));
        // 本夹具最多产出 3 个候选（基线 10 + 优化 50/11），cap=3 时全部放行。
        Assert.Equal(3, counts.Count);
    }

    [Fact(DisplayName = "预算语义【现状=不限制】· 0 不截断优化候选，只由 MaxBatchCandidates 兜底")]
    public void Zero_MeansUnbounded_TODAY()
    {
        var counts = CandidateCounts(new PhaseTwoInitialScheduler.SolverBatchBudget(0, 8, "test"));

        // 【现状】`if (MaxOptimizationSplitCount > 0)` 在 0 时整段跳过 ⇒ **0 不等于「禁用优化」**：
        //   cap=1 只放 1 个优化候选，cap=0 反而把可用的全放出来。
        //   本断言**只是在钉住现状**，不代表认可 —— 语义待 3号位 裁定（见类注释「待裁」）。
        Assert.True(counts.Count > 2,
            $"现状应表现为「0 = 不限制」：0 档候选数({counts.Count}) 应多于 cap=1 档(2)。"
            + "若此断言失败，说明预算语义已被改动，请先确认是否已按正式参数字典裁定。");

        // 且仍受 MaxBatchCandidates=8 兜底（上限 = 基线 + (8-1)）
        Assert.True(counts.Count <= 8, $"应受 MaxBatchCandidates=8 兜底，实际 {counts.Count}");
    }

    [Fact(DisplayName = "预算语义 · MaxOptimizationSplitCount 取极端大 ⇒ 仍由 MaxBatchCandidates=8 兜底")]
    public void HugeSplitCount_StillBoundedByMaxBatchCandidates()
    {
        var counts = CandidateCounts(new PhaseTwoInitialScheduler.SolverBatchBudget(999, 8, "test"));
        Assert.True(counts.Count <= 8, $"MaxBatchCandidates=8 ⇒ 候选总数 ≤ 8，实际 {counts.Count}");
    }

    [Fact(DisplayName = "预算语义 · FromSplitParams 传负数 ⇒ 回落版本化默认 3")]
    public void NegativeSplitCount_FallsBackToDefault()
    {
        var budget = PhaseTwoInitialScheduler.SolverBatchBudget.FromSplitParams(-5);

        Assert.Equal(
            PhaseTwoInitialScheduler.SolverBatchBudget.DefaultMaxOptimizationSplitCount,
            budget.MaxOptimizationSplitCount);
    }

    [Fact(DisplayName = "预算语义 · FromSplitParams 传 0 ⇒ 原样透传（不回落默认）")]
    public void ZeroSplitCount_PassedThroughVerbatim()
    {
        var budget = PhaseTwoInitialScheduler.SolverBatchBudget.FromSplitParams(0);

        // 【现状】`< 0 ? Default : 原值` ⇒ 0 原样透传，与「负数回落」形成方向相反的语义。
        Assert.Equal(0, budget.MaxOptimizationSplitCount);
    }
}