using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **R09-③｜「中间无 Routing Stage」提前期的唯一消费点**（2026-10-29 落码）。
///
/// <para><b>为什么单独立一个类</b>：这条提前期不是「Phase2 一家的事」——
/// 只要有一个阶段会**移动已排 Task**，它就必须遵守同一条下界，否则约束会被后面的阶段抹掉。
/// 实测过：只改 Phase2 时，Phase2 把 STAGE3 排到 05:00，**Phase5 `CompactGaps` 又把它前拉回 01:00**
/// （压实只看 Routing 边的 `LagTime`，不看无 Routing Stage 的提前期）。
/// ⇒ 三个消费点（Phase2 正/倒排、Phase4 传播、Phase5 压实）**必须调同一份代码**，
/// 各写一份迟早会分叉。</para>
///
/// <para><b>数据从哪来</b>：<see cref="ConstraintContext.RoutelessLeadStepsByDemand"/> 由
/// `PhaseOneConstraintBuilder.BuildRoutelessLeadSteps` 在 Phase1 一次算好；
/// 本类只做「按需查表 + 按 WORK 窗口推进/回退」，**不建表、不改表**。</para>
///
/// <para><b>不臆造时间</b>：缺 LT（`LeadTimeResolved = false`）或 WORK 窗口证明不了时
/// **不推进**（原值返回），由 `StageTimingNodeBuilder.BuildCore` 按同一函数、同一输入
/// 独立标 `CompletionTimeCertain = false`。本类**不造原因码、不写 TraceNotes**。</para>
/// </summary>
internal static class RoutelessLeadTimePolicy
{
    /// <summary>
    /// 正向：从 <paramref name="cursor"/> 起，把「<paramref name="fromStageCode"/> → <paramref name="toStageCode"/>」
    /// 之间那段无 Routing Stage 的提前期按有效生产小时往后推。无登记 / 同 Stage ⇒ 原值返回。
    /// </summary>
    public static DateTime Advance(
        DomainSolveRequest? request,
        ConstraintContext constraints,
        LogicalProductionDemand demand,
        string? fromStageCode,
        string? toStageCode,
        DateTime cursor)
    {
        var legs = FindLegs(constraints, demand, fromStageCode, toStageCode);
        if (legs is null || legs.Count == 0 || request is null)
        {
            return cursor;
        }

        foreach (var leg in legs)
        {
            if (!leg.LeadTimeResolved || leg.Hours <= 0m)
            {
                continue;
            }

            if (StageTimingNodeBuilder.TryAdvanceByWorkHours(
                    request, demand, leg.StageCode, cursor, leg.Hours, out var end, out _))
            {
                cursor = end;
            }
        }

        return cursor;
    }

    /// <summary>
    /// 反向：与 <see cref="Advance"/> 严格镜像 —— 让前一个 Routing Stage **更早结束**同样多的有效工时。
    /// 倒排/后继上界用它，保证同一份输入换个方向排出来的时间链一致。
    /// </summary>
    public static DateTime Retreat(
        DomainSolveRequest? request,
        ConstraintContext constraints,
        LogicalProductionDemand demand,
        string? fromStageCode,
        string? toStageCode,
        DateTime cursor)
    {
        var legs = FindLegs(constraints, demand, fromStageCode, toStageCode);
        if (legs is null || legs.Count == 0 || request is null)
        {
            return cursor;
        }

        // 倒排时 leg 的消耗顺序是**倒序**（链上最后一个无 Routing Stage 最先回退）。
        for (var i = legs.Count - 1; i >= 0; i--)
        {
            var leg = legs[i];
            if (!leg.LeadTimeResolved || leg.Hours <= 0m)
            {
                continue;
            }

            if (StageTimingNodeBuilder.TryRetreatByWorkHours(
                    request, demand, leg.StageCode, cursor, leg.Hours, out var start, out _))
            {
                cursor = start;
            }
        }

        return cursor;
    }

    /// <summary>
    /// 取 `(fromStage → toStage)` 这一段「中间无 Routing Stage」的 leg 列表。
    /// 无登记 / 同 Stage / 任一端为空 ⇒ <c>null</c>（调用方原值返回，等于改造前行为）。
    /// </summary>
    private static IReadOnlyList<ConstraintContext.RoutelessLeadLeg>? FindLegs(
        ConstraintContext constraints,
        LogicalProductionDemand demand,
        string? fromStageCode,
        string? toStageCode)
    {
        if (string.IsNullOrEmpty(fromStageCode) || string.IsNullOrEmpty(toStageCode))
        {
            return null;
        }

        // 同一 Stage 内换工序 ⇒ 不存在「中间 Stage」。
        if (string.Equals(fromStageCode, toStageCode, StringComparison.Ordinal))
        {
            return null;
        }

        var byDemand = constraints.RoutelessLeadStepsByDemand;
        if (byDemand.Count == 0)
        {
            return null;
        }

        if (!byDemand.TryGetValue(demand.LogicalDemandKey, out var steps) || steps is null)
        {
            return null;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (string.Equals(step.FromStageCode, fromStageCode, StringComparison.Ordinal) &&
                string.Equals(step.ToStageCode, toStageCode, StringComparison.Ordinal))
            {
                return step.Legs;
            }
        }

        return null;
    }
}
