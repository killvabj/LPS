// APS V1 Phase2 / 同PI锚批优先排序 —— 1号位源码集成参考，不是独立可编译工程。
// 定向基线: killvabj/LPS@217fa2445e19e29c61035e909d2c96173515c2ca
// 目标：只让真实可追溯的同PI C桶参与锚批搜索；锚批按交期优先、同交期批量升序排列。
// 这段代码只负责候选纯度、排序；完整重排、回滚、首个可行即停止由原 TryMergeOrSchedule 接线。
// 交期顺序: <= 当前需求的锚批优先（越接近越靠前），> 当前交期的锚批后置备选（越接近越靠前），
// 缺交期锚批最后。任何交期都不是在排序阶段的硬拒绝条件。
using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

internal static class PiStageAnchorPriority
{
    private readonly record struct Key(
        int DueTier,
        long DueDistanceTicks,
        decimal ExistingBatchQty,
        string BatchKey);

    private sealed class Entry
    {
        public required List<FinalTaskDraft> Tasks { get; init; }
        public required Key SortKey { get; init; }
    }

    /// <summary>
    /// 本地纯读：只读当前候选组、需求字典、份额构成及日期口径。
    /// 不修改真正的Task、占用表或份额表；只原地重排调用方局部 candidates 列表。
    /// 重要：这里排除的是跨PI候选和自身已参与的重复合并；路由/日历/锁定资格仍由原有守卫判断。
    /// </summary>
    public static int FilterAndSort(
        List<List<FinalTaskDraft>> candidates,
        LogicalProductionDemand currentDemand,
        ConstraintContext constraints,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> shares,
        bool applySorting = true)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(currentDemand);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(demandByKey);
        ArgumentNullException.ThrowIfNull(shares);

        string? currentPi = currentDemand.ProductionInstructionNo;
        if (string.IsNullOrWhiteSpace(currentPi))
        {
            int dropped = candidates.Count;
            candidates.Clear(); // 无可信PI时不猜PI、不以相同Material代替PI归属
            return dropped;
        }

        var currentDue = constraints.EffectiveDueJudgementBound(currentDemand);
        int initialCount = candidates.Count;
        var ordered = new List<Entry>(initialCount);

        foreach (var group in candidates)
        {
            if (group is null || group.Count == 0) continue;
            if (!TryGetAnchorDueAndCheckPi(
                group, currentDemand, currentPi!, constraints, demandByKey, shares,
                out var anchorDue)) continue;

            // 优先搜索：锚批有效交期 <= 当前有效交期；在这个区间中越接近越先试。
            // 晚交期不是硬不合格；只能放到第二排序层，避免错误丢弃真实候选。
            int tier;
            long distance;
            if (anchorDue == DateTime.MaxValue || currentDue == DateTime.MaxValue)
            {
                // 任一方缺有效交期，不能编造“交期最近”。回落批量+稳定键。
                tier = 2;
                distance = 0;
            }
            else if (anchorDue <= currentDue)
            {
                tier = 0;
                distance = currentDue.Ticks - anchorDue.Ticks;
            }
            else
            {
                tier = 1;
                distance = anchorDue.Ticks - currentDue.Ticks;
            }

            ordered.Add(new Entry
            {
                Tasks = group,
                SortKey = new Key(
                    tier,
                    distance,
                    group[0].Quantity, // 用锚批当前合计净数量，不是最初需求量或经济Preferred
                    group[0].ExecutionBatchDraftKey ?? string.Empty)
            });
        }

        // 只比较已经算出的Key。禁止排序比较器内访问数据库、扫描份额表或尝试排程。
        if (applySorting)
        ordered.Sort(static (a, b) =>
        {
            int r = a.SortKey.DueTier.CompareTo(b.SortKey.DueTier);
            if (r != 0) return r;
            r = a.SortKey.DueDistanceTicks.CompareTo(b.SortKey.DueDistanceTicks);
            if (r != 0) return r;
            r = a.SortKey.ExistingBatchQty.CompareTo(b.SortKey.ExistingBatchQty);
            if (r != 0) return r;
            return string.CompareOrdinal(a.SortKey.BatchKey, b.SortKey.BatchKey);
        });

        candidates.Clear();
        foreach (var entry in ordered) candidates.Add(entry.Tasks);
        return initialCount - candidates.Count;
    }

    /// <summary>
    /// 2026-11-10（0号位《Phase2 安全性能优化 v1.0》§3-B3 / §7 第 2 步）：
    ///   **单工序**候选 Task 的就地排序。口径与 <see cref="FilterAndSort"/> 完全一致：
    ///   ① 锚批交期 ≤ 当前需求 ⇒ tier 0，越接近越前；② 晚于当前 ⇒ tier 1，越接近越前；
    ///   ③ 任一方缺有效交期 ⇒ tier 2 置后；④ 同级按锚批当前批量升序；⑤ 再按稳定键断平。
    ///
    /// <para>⚠ 这里**不做 PI 过滤** —— 单工序路径的 PI 守卫已在 `ScanMergeCandidateRange` 里逐条做过；
    ///   本方法只负责「先试谁」，不改变候选集合，也不执行任何真实排程。</para>
    ///
    /// <para>⚠ 只原地重排调用方传入的局部列表，不修改 Task 对象、份额表或占用表。</para>
    /// </summary>
    public static void SortSingleOpCandidates(
        List<FinalTaskDraft> candidates,
        LogicalProductionDemand currentDemand,
        ConstraintContext constraints,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> shares)
    {
        if (candidates is null || candidates.Count < 2)
        {
            return;
        }

        var currentDue = constraints.EffectiveDueJudgementBound(currentDemand);
        var keyed = new List<(FinalTaskDraft Task, int Tier, long Dist, decimal Qty, string Key)>(candidates.Count);

        foreach (var task in candidates)
        {
            // 锚批有效交期取「全部已承接成员」的最早值；同一需求的多个 Operation 会重复登记，只取最小。
            var anchorDue = DateTime.MaxValue;
            if (!string.IsNullOrWhiteSpace(task.SourceDraftId)
                && demandByKey.TryGetValue(task.SourceDraftId, out var sourceDemand))
            {
                anchorDue = constraints.EffectiveDueJudgementBound(sourceDemand);
            }

            if (shares is not null && shares.TryGetValue(task.FinalDraftId, out var composition))
            {
                foreach (var (memberKey, _) in composition)
                {
                    if (demandByKey.TryGetValue(memberKey, out var member))
                    {
                        var due = constraints.EffectiveDueJudgementBound(member);
                        if (due < anchorDue) anchorDue = due;
                    }
                }
            }

            int tier;
            long distance;
            if (anchorDue == DateTime.MaxValue || currentDue == DateTime.MaxValue)
            {
                tier = 2;
                distance = 0;
            }
            else if (anchorDue <= currentDue)
            {
                tier = 0;
                distance = currentDue.Ticks - anchorDue.Ticks;
            }
            else
            {
                tier = 1;
                distance = anchorDue.Ticks - currentDue.Ticks;
            }

            keyed.Add((task, tier, distance, task.Quantity, task.FinalDraftId ?? string.Empty));
        }

        keyed.Sort(static (a, b) =>
        {
            var r = a.Tier.CompareTo(b.Tier);
            if (r != 0) return r;
            r = a.Dist.CompareTo(b.Dist);
            if (r != 0) return r;
            r = a.Qty.CompareTo(b.Qty);
            if (r != 0) return r;
            return string.CompareOrdinal(a.Key, b.Key);
        });

        candidates.Clear();
        foreach (var e in keyed) candidates.Add(e.Task);
    }

    private static bool TryGetAnchorDueAndCheckPi(
        List<FinalTaskDraft> group,
        LogicalProductionDemand currentDemand,
        string currentPi,
        ConstraintContext constraints,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> shares,
        out DateTime earliestDue)
    {
        earliestDue = DateTime.MaxValue;
        string anchorSource = group[0].SourceDraftId;
        if (!demandByKey.TryGetValue(anchorSource, out var sourceDemand)
            || !SamePiAndNewBatch(sourceDemand, currentDemand, currentPi)) return false;

        earliestDue = constraints.EffectiveDueJudgementBound(sourceDemand);

        // 多Operation的每条Task可能重复登记同一需求构成。
        // 只求交期最小值，重复读取不改变值；不应把多个Operation的份额累计求和。
        foreach (var task in group)
        {
            if (!string.Equals(task.SourceDraftId, anchorSource, StringComparison.Ordinal)) return false;
            if (!shares.TryGetValue(task.FinalDraftId, out var composition)) continue;
            foreach (var (key, _) in composition)
            {
                if (!demandByKey.TryGetValue(key, out var member)
                    || !SamePiAndNewBatch(member, currentDemand, currentPi)) return false;
                var due = constraints.EffectiveDueJudgementBound(member);
                if (due < earliestDue) earliestDue = due;
            }
        }
        return true;
    }

    private static bool SamePiAndNewBatch(
        LogicalProductionDemand member,
        LogicalProductionDemand current,
        string expectedPi)
        => string.Equals(member.ProductionInstructionNo, expectedPi, StringComparison.Ordinal)
           && !member.IsContinuation
           && !member.NoSplitMerge
           && !string.Equals(member.LogicalDemandKey, current.LogicalDemandKey, StringComparison.Ordinal);
}
