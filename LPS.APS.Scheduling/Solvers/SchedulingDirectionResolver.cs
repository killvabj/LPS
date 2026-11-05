using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **B-005 Direction 上下文决策器（Owner = 1号位）**。
///
/// 【为什么需要它】0号位 2026-10-08《未命名的Markdown文件 (2)(1).md》§三 / §十四 第二优先级 判 **P1-DIR-01**：
///   冻结要求是「Demand / Execution Batch ↓ 综合其**自己的**上下文 ⇒ 由 1号位 得到实际 Direction」，
///   而旧实现是「一个 Run/Strategy 给出一个 <c>SchedulingDirection</c>，所有 Demand 共用」，
///   且 `AUTO` 与 `MIXED` 行为等价 ⇒ 冻结能力**未实现**。本类即该能力的正式落地。
///
/// 【冻结依据（逐条可查）】
///   · 规则清单 v1.5 `B-004`（源：业务基线 v1.8、有限产能 v1.7）：
///     「Direction 支持 AUTO/FORWARD/BACKWARD/MIXED；**OrderType 不得直接决定 Direction**」。
///   · 规则清单 v1.5 `B-005`（源：有限产能 v1.7）：
///     「Direction 由 **DemandGoal、RequiredAvailableTime、Slack、Material、Resource、Execution、
///       Firm/Frozen/Lock** 等上下文综合决定」，Owner = 1号位。
///   · 业务基线 v1.8 §Demand Goal、Batch、Direction、Routing：
///     「Direction 不得由 OrderType 硬映射，按冻结策略综合 Demand Goal、RequiredAvailableTime、Slack、
///       Material、资源、Execution 与 Lock 决定」。
///   · 有限产能 v1.7 §11：「正排适合**最早承诺、物料刚可用**等场景；倒排适合**靠近 DueDate 减少 WIP**。
///       V1 在同一个 Solver 内混合使用」。
///   · Pegging v1.6 边界 2：「Pegging 传播 Qty、RequiredAvailableTime、**DemandGoal**、Demand Protection、
///       DemandSequence 与 lineage；**不传播 Direction**、Batch、Resource、TaskNo 或 Routing 最终选择」
///     ⇒ Direction **必须**由 1号位 自决（不是 2号位 透传），但 DemandGoal **必须**由 Pegging 侧传播进来。
///
/// 【本实现只用「确实存在」的输入】七类上下文与 C# 载体的对应关系：
///   · Firm/Frozen/Lock → <c>ConstraintContext.LockedTasks</c>（锁定任务 = 原地继承的硬锚点）
///   · Execution（连续性）→ <c>LogicalProductionDemand.IsContinuation</c> / <c>NoSplitMerge</c> / <c>ContinuationKey</c>
///   · Material → <c>dynamicMaterialFloor</c>（跨物料子件完成下界）+ <c>ConstraintContext.MaterialAvailability</c>
///   · RequiredAvailableTime → <c>LogicalProductionDemand.RequiredAvailableTime</c>（Due）
///   · Slack → **派生量**（见 <see cref="ComputeSlackMinutes"/>），非契约字段
///   · Resource → <c>PreferredResourceCode</c> / <c>PreferredResourceId</c> / <c>FallbackResourceId</c>
///   · DemandGoal → <c>LogicalProductionDemand.DemandGoal</c>（2026-10-12 载体落地；1号位 R-5 接线 2026-10-13）
///
/// 【DemandGoal 载体状态（2026-10-13 更新；原「载体缺失」缺口已关闭）】
///   冻结侧 `DemandGoal` 是**已定义**概念：规则清单 `P-006`「V1 DemandGoal 包含
///   <c>CUSTOMER_COMMITMENT</c> 与 <c>INVENTORY_REPLENISHMENT</c>」（源：Pegging v1.6），
///   且 `P-008` 要求 Pegging **传播** DemandGoal。
///   历史：2026-10-08 时 <c>LogicalProductionDemand</c> **无** DemandGoal 属性，全 C# 零命中 ⇒
///     本类按「**载体缺失**」处置（保留形参与判据、生产路径传 <c>null</c>、信号记缺口）。
///   **现状（已变）**：该字段 **2026-10-12 已落地**（`LogicalProductionDemand.cs:243`，
///     **正是 1号位 点名的字段**，0号位 裁决 DG-01／DG-03.4／DG-05）；2号位 已在 Pegging 侧
///     **原样传播**（`PeggingOrchestrator.cs:2842` / `:2886`）；1号位 生产路径 **2026-10-13 已接线**
///     为 `demandGoal: demand.DemandGoal`（R-5）。
///   ✅ **2026-10-13 取值链已投产**（2号位 `PeggingOrchestrator.FillDemandGoalsAsync`，`:250` 调用；
///     `LogicalProductionDemand.cs:236-241` 原「当前恒 null」段已**作废**）⇒ 判据 6 **已真正生效**：
///     客户承诺（O 行非跨厂）走 `CUSTOMER_COMMITMENT` ⇒ **反向（交期驱动）**；P 行走
///     `INVENTORY_REPLENISHMENT` ⇒ 正向。⇒ **Direction 自决结果自此可能由 FORWARD 变 BACKWARD**；
///     端到端鉴别性反证见 `PhaseTwoRoutingCandidateTests.AUTO_DemandGoal非空_端到端确实改变方向`。
///     仍缺值时（跨厂 SH 未落地／血缘无法唯一确定）记 <see cref="SignalDemandGoalAbsent"/>，**不猜**。
///   处理方式不变：缺值 ⇒ 在信号里记 <see cref="SignalDemandGoalAbsent"/>
///     ⇒ **缺口在决策记录里可见，绝不静默**（生产路径另经 `SolveTraceNotes` 出口，见 `PhaseTwoInitialScheduler`）。
///
/// 【与 MIXED 的关系】0号位 §三：「`AUTO == MIXED` 不能再作为最终 V1 实现」；`MIXED` 本身
///   「可以作为人工/策略明确模式继续保留」⇒ **MIXED 保持字面语义不变**（先倒排、失败转正排），
///   只有 `AUTO` 走本类自决。自决结果可以是 `FORWARD`/`BACKWARD`/`MIXED` 三者之一（信号冲突时为 MIXED）。
/// </summary>
internal static class SchedulingDirectionResolver
{
    internal const string Forward = "FORWARD";
    internal const string Backward = "BACKWARD";
    internal const string Mixed = "MIXED";
    internal const string Auto = "AUTO";

    // ── 判据信号名（确定性、可断言、可审计）──
    internal const string SignalLockedAnchor = "LOCKED_ANCHOR";
    internal const string SignalContinuationSlice = "CONTINUATION_SLICE";
    internal const string SignalMaterialLate = "MATERIAL_LATE";
    internal const string SignalDueTight = "DUE_TIGHT";
    internal const string SignalDueLoose = "DUE_LOOSE";
    internal const string SignalDueOverdue = "DUE_OVERDUE";
    internal const string SignalDueAbsent = "DUE_ABSENT";
    /// <summary>
    /// 2026-10-28｜R05-27-10 新增：Slack 估算**不全**（缺产能系数／缺资格资源／缺日历）⇒ 走不确定出口。
    /// 0号位 2026-10-27《01_…》§1-R05-27-10：「无法准确得出时采取可解释保守方向或不确定出口」。
    /// 只登记缺口，**不**据此判「交期紧/松」。
    /// </summary>
    internal const string SignalDueSlackUncertain = "DUE_SLACK_UNCERTAIN";
    internal const string SignalGoalCustomerCommitment = "GOAL_CUSTOMER_COMMITMENT";
    internal const string SignalGoalInventoryReplenishment = "GOAL_INVENTORY_REPLENISHMENT";
    internal const string SignalDemandGoalAbsent = "DEMAND_GOAL_ABSENT";
    internal const string SignalPreferredResource = "PREFERRED_RESOURCE";
    internal const string SignalNoContext = "NO_CONTEXT_SIGNAL";
    internal const string SignalConflicting = "CONFLICTING_SIGNALS";

    // ── DemandGoal 冻结取值（P-006；载体与取值链**均已投产**，见类注释）──
    internal const string GoalCustomerCommitment = "CUSTOMER_COMMITMENT";
    internal const string GoalInventoryReplenishment = "INVENTORY_REPLENISHMENT";

    /// <summary>方向裁决结果：方向 + 判据信号集合（顺序 = 收集顺序，确定性）。</summary>
    internal sealed record Decision(string Direction, IReadOnlyList<string> Signals)
    {
        /// <summary>是否含某判据信号。</summary>
        public bool Has(string signal) => Signals.Contains(signal, StringComparer.Ordinal);

        /// <summary>可追溯的判据串（写日志/回执用，非契约字段）。</summary>
        public string Reason => $"{string.Join("+", Signals)}⇒{Direction}";
    }

    /// <summary>
    /// **B-005 正式自决**：按 Demand / Execution Batch 自身上下文决定实际 Direction。
    ///
    /// 裁决规则（确定性、可复现；先判硬锚点，再按信号冲突收敛）：
    ///   1. **锁定任务存在** ⇒ `FORWARD`。锁定 = 原地继承的既成事实锚点（`ExecutionConstraint` /
    ///      `LockedTasks`），剩余工序只能**从锚点向前**续排 ⇒ 正排。该判据是硬事实，压过一切软信号。
    ///   2. 否则收集正/反信号：
    ///        正向（最早承诺）: `CONTINUATION_SLICE` / `MATERIAL_LATE` / `DUE_LOOSE` / `DUE_OVERDUE` /
    ///                          `GOAL_INVENTORY_REPLENISHMENT` / `PREFERRED_RESOURCE`
    ///        反向（交期驱动）: `DUE_TIGHT` / `GOAL_CUSTOMER_COMMITMENT`
    ///      · 正反**同时**非空 ⇒ `MIXED`（信号冲突：先倒排、失败转正排，即 v1.7 §11「同一 Solver 内混合」）
    ///      · 仅反向非空 ⇒ `BACKWARD`（靠 DueDate 减少 WIP）
    ///      · 仅正向非空 ⇒ `FORWARD`（最早承诺 / 物料刚可用）
    ///      · **都空** ⇒ `BACKWARD` + `NO_CONTEXT_SIGNAL`（无上下文可依时取系统既有默认方向，
    ///        确定性且**不是** MIXED —— 不再回到「AUTO 恒等 MIXED」）
    /// </summary>
    internal static Decision Resolve(
        LogicalProductionDemand demand,
        IReadOnlyList<OperationNode> operations,
        ConstraintContext constraints,
        DateTime planningStart,
        DateTime dynamicMaterialFloor,
        string? demandGoal = null)
    {
        var signals = new List<string>();

        // ── 判据 1：Firm/Frozen/Lock —— 锁定任务（硬锚点，最高优先）──
        if (HasLockedAnchor(demand, constraints))
        {
            signals.Add(SignalLockedAnchor);
            return new Decision(Forward, signals);
        }

        var forwardSignals = new List<string>();
        var backwardSignals = new List<string>();

        // ── 判据 2：Execution —— 连续份额（A/B 既存执行批：已经开始 ⇒ 最早承诺）──
        if (demand.IsContinuation || demand.NoSplitMerge || !string.IsNullOrEmpty(demand.ContinuationKey))
        {
            forwardSignals.Add(SignalContinuationSlice);
        }

        // ── 判据 3：Resource —— 资源连续性软偏好（P1-11 / v1.6 Q4：与上一执行资源保持一致）──
        if (!string.IsNullOrEmpty(demand.PreferredResourceCode)
            || demand.PreferredResourceId is not null
            || demand.FallbackResourceId is not null)
        {
            forwardSignals.Add(SignalPreferredResource);
        }

        // ── 判据 4：Material —— 物料/子件下界晚于计划起点 ⇒「物料刚可用」⇒ 正排 ──
        var materialFloor = dynamicMaterialFloor > planningStart ? dynamicMaterialFloor : planningStart;
        if (dynamicMaterialFloor > planningStart)
        {
            forwardSignals.Add(SignalMaterialLate);
        }

        // ── 判据 5：RequiredAvailableTime + Slack —— 交期驱动 vs 最早承诺 ──
        var slackMinutes = ComputeSlackMinutes(
            demand, operations, constraints, materialFloor, planningStart,
            out var dueAbsent, out var slackEstimated, out var leadCalendarMinutes);
        if (dueAbsent)
        {
            signals.Add(SignalDueAbsent);
        }
        else if (demand.RequiredAvailableTime <= planningStart)
        {
            // 交期**已过**：倒排（靠 DueDate 减少 WIP）已无意义 ⇒ 尽早产出 ⇒ 正排（最早承诺）。
            forwardSignals.Add(SignalDueOverdue);
        }
        else if (slackEstimated)
        {
            // ── 2026-10-28｜R05-27-10：时长算不全就**不许判紧/松** ──
            //   旧实现拿「工艺标准工时之和」当周期，既不含产能系数也不含日历，
            //   在 AUTO 上直接把 slack 撑大 ⇒ 错的 `DUE_LOOSE ⇒ 正排`。
            //   现在：缺资格/缺产能系数/缺日历/无工序任一成立 ⇒ 本判据**弃权**，
            //   只登记 `DUE_SLACK_UNCERTAIN`（中性信号，不投正反任一票），方向交其它上下文裁决。
            signals.Add(SignalDueSlackUncertain);
        }
        else if (slackMinutes < 0m)
        {
            // 交期未过但余量为负（周期都装不下）⇒ 交期驱动 ⇒ 倒排。
            backwardSignals.Add(SignalDueTight);
        }
        else if (slackMinutes > leadCalendarMinutes)
        {
            // 余量超过**一个完整日历周期**⇒ 明显不紧 ⇒ 最早承诺（正排）。
            //   ⚠ 阈值必须是日历跨度（自然分钟），不能再用工艺分钟 —— 两边口径不同不能直接比。
            forwardSignals.Add(SignalDueLoose);
        }
        // 0 <= slack <= 日历周期：余量一般 ⇒ 不产生交期信号（交由其它上下文裁决）

        // ── 判据 6：DemandGoal —— 客户承诺（交期驱动）vs 库存补充（非交期驱动）──
        if (string.IsNullOrEmpty(demandGoal))
        {
            // 取值缺失（**2026-10-13 起为个案缺口**：跨厂 SH 未落地／血缘无法唯一确定）：**记录缺口**，不静默、不猜。
            signals.Add(SignalDemandGoalAbsent);
        }
        else if (string.Equals(demandGoal, GoalCustomerCommitment, StringComparison.Ordinal))
        {
            backwardSignals.Add(SignalGoalCustomerCommitment);
        }
        else if (string.Equals(demandGoal, GoalInventoryReplenishment, StringComparison.Ordinal))
        {
            forwardSignals.Add(SignalGoalInventoryReplenishment);
        }
        else
        {
            // 未知取值：不认（不猜），同样登记缺口。
            signals.Add(SignalDemandGoalAbsent);
        }

        // ── 收敛裁决 ──
        signals.AddRange(forwardSignals);
        signals.AddRange(backwardSignals);

        if (forwardSignals.Count > 0 && backwardSignals.Count > 0)
        {
            signals.Add(SignalConflicting);
            return new Decision(Mixed, signals);
        }

        if (backwardSignals.Count > 0)
        {
            return new Decision(Backward, signals);
        }

        if (forwardSignals.Count > 0)
        {
            return new Decision(Forward, signals);
        }

        signals.Add(SignalNoContext);
        return new Decision(Backward, signals);
    }

    /// <summary>
    /// Slack（派生量，**非契约字段**）= `RequiredAvailableTime − 最早可能完成时间`（自然分钟）。
    ///
    /// 最早可能完成 = `max(PlanningStart, 物料/子件下界)` 起，按工序顺序**过资源工作日历**推进：
    ///   单工序有效时长 = `StandardDuration × PlannedProcessQty ÷ 最大产能系数`（与 Phase2 同口径）。
    ///   · 这是**估算**（不含资源争用与 Setup），仅用于「交期紧/松」的**方向判据**，
    ///     **不**作为可行性结论（可行性仍由 Phase2 找槽 / Phase3 诊断 / Phase4 修复给出）。
    ///   · `estimated = true` ⇒ 这个估算**不可信**，调用方**不得**据此判紧/松（应登记
    ///     `DUE_SLACK_UNCERTAIN` 并弃权）。触发条件任一成立：
    ///       ① 该需求**无工序**；② 某工序查不到资格资源；③ 查到资源但**无产能系数**（≤0）；
    ///       ④ 该资源**无日历**（只能按自然分钟硬推，工作日历缺口会把 slack 算小 ⇒ 不能判松）。
    ///     ⚠ 注意方向：缺日历会让**周期变短**（少扣非工作时段）⇒ slack 偏**大** ⇒ 误判「松」。
    ///       所以缺日历必须弃权，不能当「乐观下界」用。
    ///   · `leadCalendarMinutes` = 同一行程在**日历上的跨度**，是判「松」的比较基准
    ///     （`slackMinutes` 是自然分钟，与工艺分钟不同口径，不能直接比）。
    ///   · `dueAbsent = true` **仅当** `RequiredAvailableTime == default`（**无交期**），此时**不产生交期信号**。
    ///     ⚠ 「交期已过」**不**归入 absent（那是「已逾期」，是有效交期）—— 由调用方按
    ///     `DUE_OVERDUE ⇒ 正排` 处理，避免把「已逾期」误判成「无交期」而丢失方向信息。
    /// </summary>
    internal static decimal ComputeSlackMinutes(
        LogicalProductionDemand demand,
        IReadOnlyList<OperationNode> operations,
        ConstraintContext constraints,
        DateTime materialFloor,
        DateTime planningStart,
        out bool dueAbsent,
        out bool estimated,
        out decimal leadCalendarMinutes)
    {
        dueAbsent = false;
        estimated = false;
        leadCalendarMinutes = 0m;

        if (demand.RequiredAvailableTime == default)
        {
            dueAbsent = true;
            return 0m;
        }

        var earliestStart = materialFloor > planningStart ? materialFloor : planningStart;

        // 无工序 ⇒ 算不出工艺周期，不能拿「0 分钟」冒充（旧实现正是这样把 slack 撑大的）
        if (operations.Count == 0)
        {
            estimated = true;
            return (decimal)(demand.RequiredAvailableTime - earliestStart).TotalMinutes;
        }

        var cursor = earliestStart;
        foreach (var operation in operations)
        {
            var (processMinutes, resourceId, resolved) = EstimateOperationProcessMinutes(demand, operation, constraints);
            if (!resolved)
            {
                estimated = true;
            }

            if (processMinutes <= 0m)
            {
                continue;
            }

            if (resourceId is int rid
                && constraints.ResourceCalendars.TryGetValue(rid, out var windows)
                && windows is { Count: > 0 })
            {
                cursor = AdvanceOverWorkCalendar(cursor, processMinutes, windows);
            }
            else
            {
                // 没有日历 ⇒ 只能按自然分钟推进（已标 estimated，调用方不会据此判紧/松）
                cursor = cursor.AddMinutes((double)processMinutes);
            }
        }

        // 判「松」的阈值必须是**日历跨度**，不能再用工艺分钟：slack 是自然分钟，
        //   拿「自然分钟」比「工艺分钟」正是旧实现口径不一致的第二处错。
        leadCalendarMinutes = (decimal)(cursor - earliestStart).TotalMinutes;
        return (decimal)(demand.RequiredAvailableTime - cursor).TotalMinutes;
    }

    /// <summary>
    /// 2026-10-28｜R05-27-10 整改：单工序的「真实加工分钟」估算，返回 (加工分钟, 用哪个资源估的, 是否算准了)。
    ///   · 加工分钟 = `StandardDuration(分钟) × PlannedProcessQty ÷ CapacityFactor`
    ///     —— `StandardDuration` 已由 Phase1 装配点 ÷60 成分钟（R04-S-G-Units），此处**不得再除 60**。
    ///   · 产能系数取**该工序资格资源里最大的那个**：系数越大 ⇒ 时长越短 ⇒ 完工越早 ⇒ 乐观下界。
    ///     乐观下界下 `slack` 只会**偏大** ⇒ 最多把「其实不松」判成「松」，不会把「能排下」误判成「排不下」。
    ///   · 算不准的三种情形（`resolved=false`，由调用方走 <see cref="SignalDueSlackUncertain"/>）：
    ///     ① 没有资格资源行 ② 有资格资源但没有任何产能系数 ③ 有产能系数但该资源没有日历行。
    /// </summary>
    private static (decimal Minutes, int? ResourceId, bool Resolved) EstimateOperationProcessMinutes(
        LogicalProductionDemand demand,
        OperationNode operation,
        ConstraintContext constraints)
    {
        var fallback = operation.StandardDuration * demand.PlannedProcessQty;

        var key = new EligibilityLookupKey(
            demand.MaterialId, operation.ProductionDepartmentId,
            operation.RouteCode, operation.PathId, operation.OperationCode);

        if (!constraints.OperationResourceEligibility.TryGetValue(key, out var eligible) || eligible.Count == 0)
        {
            return (fallback, null, false);
        }

        decimal bestFactor = 0m;
        int bestResourceId = eligible[0];
        if (constraints.ResourceCapacityFactors.TryGetValue(key, out var factors))
        {
            foreach (var resourceId in eligible)
            {
                if (factors.TryGetValue(resourceId, out var factor) && factor > bestFactor)
                {
                    bestFactor = factor;
                    bestResourceId = resourceId;
                }
            }
        }

        if (bestFactor <= 0m)
        {
            return (fallback, bestResourceId, false);
        }

        var minutes = operation.StandardDuration * demand.PlannedProcessQty / bestFactor;
        var hasCalendar = constraints.ResourceCalendars.TryGetValue(bestResourceId, out var cal) && cal.Count > 0;
        return (minutes, bestResourceId, hasCalendar);
    }

    /// <summary>
    /// 把 `start` 起算的 `workMinutes` 分钟**按 WORK 日历**推进，返回可完工时刻。
    /// 只累计窗内分钟；窗外（夜间/非工作日/OFF）不计。日历覆盖不到剩余量时按自然分钟顺延
    /// （调用方已把「日历缺失/不足」标成不确定，这里不再重复标记）。
    /// </summary>
    private static DateTime AdvanceOverWorkCalendar(DateTime start, decimal workMinutes, List<TimeWindow> windows)
    {
        if (workMinutes <= 0m)
        {
            return start;
        }

        // 先看是否已升序（生产里日历行通常是升序的）：是就不排序，避免每条需求都排一遍。
        var ascending = true;
        for (int i = 1; i < windows.Count; i++)
        {
            if (windows[i].Start < windows[i - 1].Start)
            {
                ascending = false;
                break;
            }
        }

        var ordered = ascending ? (IReadOnlyList<TimeWindow>)windows : windows.OrderBy(w => w.Start).ToList();

        var remaining = workMinutes;
        var cursor = start;
        foreach (var window in ordered)
        {
            if (window.End <= cursor)
            {
                continue;
            }

            var from = window.Start > cursor ? window.Start : cursor;
            if (window.End <= from)
            {
                continue;
            }

            var available = (decimal)(window.End - from).TotalMinutes;
            if (available <= 0m)
            {
                continue;
            }

            if (remaining <= available)
            {
                return from.AddMinutes((double)remaining);
            }

            remaining -= available;
            cursor = window.End;
        }

        return cursor.AddMinutes((double)remaining);
    }

    // ── 2026-10-28｜R05-27-10：`TotalLeadMinutes(IReadOnlyList<OperationNode>)` 已删除 ──
    //   它把「工序标准工时之和」当工艺周期（不含 PlannedProcessQty、不含产能系数、不含日历），
    //   正是 Slack 误判「松」的根因；保留它只会再被误用。现在周期一律由
    //   `EstimateOperationProcessMinutes` + `AdvanceOverWorkCalendar` 给出。

    /// <summary>该需求是否存在锁定任务（`LockedTasks` 键为 `(DraftId, StageCode, OperationCode)`，按 DraftId 归属需求）。</summary>
    private static bool HasLockedAnchor(LogicalProductionDemand demand, ConstraintContext constraints)
        => constraints.LockedTasks.Keys.Any(k => string.Equals(k.DraftId, demand.LogicalDemandKey, StringComparison.Ordinal)
                                                 || string.Equals(k.DraftId, demand.DemandKey, StringComparison.Ordinal));
}
