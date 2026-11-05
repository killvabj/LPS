using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **R10-O01｜订单级「整单按期率」纯内存比较器**（1号位 2026-10-31，应 0号位
/// 《致1号位_二次回执完整审核回复_v3.0_20261031_拟发.md》`:9` 最小交付、`:18` 已明确口径、`:22` 落点时机）。
///
/// <para>裁决原文（`:9`）：「在 `LPS.APS.Scheduling` 内实现同 Domain 逐客户 OrderId 聚合、
/// 最终 Stage 全量完工后以**原始 DATE 交期**比较的**第三层候选比较**；遵守方案乙；
/// <b>先提供最小比较器与不依赖 DB 的 Unit</b>，接口联通后提交 Revision 与同 Run」。</para>
///
/// <para>裁决原文（`:22`）：「2号位 完成交付后 1号位将第三层真实调用点落码」⇒ **本类当前只做比较器本体，
/// 不接任何生产调用点**。改动面＝新增文件，零回归。</para>
///
/// <h3>口径（逐条来自 `:18`「0号位已明确、不再询问」）</h3>
/// <list type="bullet">
///   <item><b>一订单唯一 Domain</b> ⇒ 按 DomainKey 分组出结果，**不跨 Domain 合并分母**。</item>
///   <item><b>最后 Stage 全净量完成 = ZP/BP 履约时刻</b> ⇒ 履约时刻取需求级
///     <see cref="DemandCompletionFact.CompletionTime"/>（该类文档：= 末端真实 Task.End ＋ 其**之后**
///     各无 Routing Stage 的 LeadTime 之和），**不取** <c>FinalTasks.Max(PlannedEndTime)</c>。
///     正是 `:18` 那句「**不用所有下阶 Task 最大结束时间**」。</item>
///   <item><b>Domain 内订单按张计算</b> ⇒ 分母是**订单张数**，不是需求条数。</item>
///   <item><b>客户 DATE 技术以次日 00:00 排他上界</b> ⇒ 复用
///     <see cref="ConstraintContext.ToJudgementBound"/>（返回「交期日最后一刻」），判据沿用
///     「到达 &gt; 上界 ⇒ 延期」⇒ 本类写成 <c>履约时刻 &lt;= 上界 ⇒ 按期</c>，**两侧口径逐字一致**。</item>
///   <item><b>第三层方案乙持续追更高按期率、未达目标仍返回最佳合法计划</b> ⇒
///     <see cref="CompareOnTimeRate"/> **只排序不断言失败**；达标与否另由
///     <see cref="RateResult.TargetMet"/> 报出，**不写死 100%**（目标取冻结的
///     <c>OnTimeTarget.TargetPercent</c>，缺省即 `null` ＝ 无门槛，不臆造成 100）。</item>
/// </list>
///
/// <h3>订单履约时刻怎么取（含一处 0号位 未逐字规定、本类显式留痕的取舍）</h3>
/// <para>`:18` 只说「最后 Stage 全净量完成 = ZP/BP 履约时刻」，没逐字规定**一张订单挂多条需求**
/// （根需求 ＋ BOM 子需求）时取哪条的末端。本类的取法与其留痕：</para>
/// <list type="bullet">
///   <item>该订单存在 <see cref="LogicalProductionDemand.IsRootDemand"/>＝true 的需求 ⇒
///     履约时刻取**根需求** <c>CompletionTime</c> 的最大值，依据「ZP/BP 履约时刻」
///     指的是**订单根**走到最后 Stage 的那一下，不是 BOM 子件 Task 的最大结束时间
///     （后者正是 `:18` 明令禁止的口径）。留痕 <see cref="FulfilmentBasis.RootDemand"/>。</item>
///   <item>该订单一条根标记都没有（旧链路 / 未装载该字段）⇒ 回落取**该订单全部需求**
///     <c>CompletionTime</c> 的最大值，并留痕 <see cref="FulfilmentBasis.AllDemandsFallback"/>。
///     回落分支**必须可见**，不得当成根需求口径静默通过。</item>
///   <item><b>不论走哪条，只要该订单有任一需求未完成 / 完成时间不可信 ⇒ 整单判不按期</b>
///     （子件没做完，整单不算交付）。这是 Fail Closed，**不因取根需求而放过缺子件**。</item>
/// </list>
///
/// <h3>不做的事</h3>
/// <para>不重做 Demand 排序 / Pegging；不建 DB 连接；不读写 <see cref="DateTime.Now"/>；
/// 不改 <c>LPS.APS.Core</c> 任何 DTO（订单级 <c>CustomerDueDate</c> 映射归 2号位，
/// 本类以入参 <c>orderDueDates</c> 接收，接口联通后由调用方喂）。</para>
/// </summary>
internal static class OrderOnTimeRateEvaluator
{
    /// <summary>
    /// 一条需求的履约输入。<b>纯值</b>，可由
    /// <see cref="FromSolveResult"/> 从现有 Core DTO 直接摊平，也可由单测直接构造。
    /// </summary>
    /// <param name="DomainKey">所属 Domain（`:18`「一订单唯一 Domain」⇒ 分组键）。</param>
    /// <param name="LogicalDemandKey">需求键（仅用于留痕与去重）。</param>
    /// <param name="OrderId">客户订单号；<c>null</c> ＝ 无订单身份（不计入分母，见
    /// <see cref="RateResult.UnattributedDemandCount"/>）。</param>
    /// <param name="CompletionTime">末端完成时刻；<c>null</c> ＝ 未完成 / 未排下。</param>
    /// <param name="CompletionTimeCertain">完成时刻是否可信；<c>false</c> ⇒ 不得当承诺（Fail Closed）。</param>
    /// <param name="IsRootDemand">该需求是否为订单的顶层根需求（BOM 展开第 0 层）。</param>
    internal readonly record struct DemandFulfilment(
        string DomainKey,
        string LogicalDemandKey,
        long? OrderId,
        DateTime? CompletionTime,
        bool CompletionTimeCertain,
        bool IsRootDemand);

    /// <summary>履约时刻的取法（留痕用，见类文档「订单履约时刻怎么取」）。</summary>
    internal enum FulfilmentBasis
    {
        /// <summary>订单无原始交期 ⇒ 不可判（不计入分母）。</summary>
        None = 0,

        /// <summary>取根需求（<c>IsRootDemand=true</c>）的末端完成最大值。</summary>
        RootDemand = 1,

        /// <summary>订单无根标记 ⇒ 回落取全部需求的末端完成最大值。</summary>
        AllDemandsFallback = 2,
    }

    /// <summary>一张订单的判定结果。</summary>
    internal sealed record OrderVerdict(
        long OrderId,
        DateTime DueDate,
        DateTime JudgementBound,
        DateTime? FulfilmentTime,
        FulfilmentBasis Basis,
        bool IsOnTime,
        decimal LatenessHours,
        IReadOnlyList<string> DemandKeys,
        IReadOnlyList<string> UnfulfilledDemandKeys);

    /// <summary>一个 Domain 的整单按期率读数。</summary>
    internal sealed record RateResult(
        string DomainKey,
        IReadOnlyList<OrderVerdict> Orders,
        int JudgedOrderCount,
        int OnTimeOrderCount,
        decimal? OnTimeRatePercent,
        int? TargetPercent,
        bool? TargetMet,
        int UnattributedDemandCount,
        int OrdersWithoutDueDateCount);

    /// <summary>
    /// 按 Domain 分组求整单按期率。「只看一个 Domain」就是「不跨 Domain 合并分母」（`:18`）。
    /// 返回的字典按 DomainKey 序数排序 ⇒ 同一输入恒得同一输出。
    /// </summary>
    /// <param name="fulfils">需求级履约输入（同一 Domain 内 <see cref="DemandFulfilment.LogicalDemandKey"/>
    /// 重复时取**第一条**，避免同一需求被重复计数）。</param>
    /// <param name="orderDueDates">订单号 → **客户原始交期**（归 2号位 装载；本类只消费）。
    /// 缺失的订单**不计入分母**并计数 <see cref="RateResult.OrdersWithoutDueDateCount"/>。</param>
    /// <param name="targetPercent">冻结的 <c>OnTimeTarget.TargetPercent</c>（0~100）；<c>null</c> ＝ 不设门槛。</param>
    internal static IReadOnlyDictionary<string, RateResult> EvaluateByDomain(
        IEnumerable<DemandFulfilment> fulfils,
        IReadOnlyDictionary<long, DateTime> orderDueDates,
        int? targetPercent)
    {
        ArgumentNullException.ThrowIfNull(fulfils);
        ArgumentNullException.ThrowIfNull(orderDueDates);

        var result = new SortedDictionary<string, RateResult>(StringComparer.Ordinal);

        // 先去重：同一 (DomainKey, LogicalDemandKey) 只算一次。用序数比较，不吃区域设置。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var byDomain = new Dictionary<string, List<DemandFulfilment>>(StringComparer.Ordinal);
        foreach (var f in fulfils)
        {
            var domain = f.DomainKey ?? string.Empty;
            if (!seen.Add(domain + "\u0000" + (f.LogicalDemandKey ?? string.Empty))) continue;
            if (!byDomain.TryGetValue(domain, out var list))
            {
                list = new List<DemandFulfilment>();
                byDomain[domain] = list;
            }
            list.Add(f);
        }

        foreach (var pair in byDomain.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            result[pair.Key] = EvaluateDomain(pair.Key, pair.Value, orderDueDates, targetPercent);
        }

        return result;
    }

    private static RateResult EvaluateDomain(
        string domainKey,
        List<DemandFulfilment> fulfils,
        IReadOnlyDictionary<long, DateTime> orderDueDates,
        int? targetPercent)
    {
        var unattributed = fulfils.Count(f => f.OrderId is null);

        var orders = new List<OrderVerdict>();
        var byOrder = fulfils
            .Where(f => f.OrderId is not null)
            .GroupBy(f => f.OrderId!.Value)
            .OrderBy(g => g.Key);

        var withoutDue = 0;
        foreach (var group in byOrder)
        {
            var demandKeys = group.Select(f => f.LogicalDemandKey).OrderBy(k => k, StringComparer.Ordinal).ToList();

            if (!orderDueDates.TryGetValue(group.Key, out var dueDate))
            {
                // 无原始交期 ⇒ 不可判。**不进分母**，但要计数，不能被静默吞掉。
                withoutDue++;
                orders.Add(new OrderVerdict(
                    group.Key, default, default, null, FulfilmentBasis.None,
                    IsOnTime: false, LatenessHours: 0m,
                    DemandKeys: demandKeys, UnfulfilledDemandKeys: demandKeys));
                continue;
            }

            orders.Add(EvaluateOrder(group.Key, dueDate, group, demandKeys));
        }

        var judged = orders.Where(o => o.Basis != FulfilmentBasis.None).ToList();
        var onTime = judged.Count(o => o.IsOnTime);

        decimal? rate = judged.Count == 0 ? null : (decimal)onTime * 100m / judged.Count;
        bool? targetMet = targetPercent is null || rate is null ? null : rate >= targetPercent;

        return new RateResult(
            domainKey, orders, judged.Count, onTime, rate, targetPercent, targetMet,
            UnattributedDemandCount: unattributed,
            OrdersWithoutDueDateCount: withoutDue);
    }

    private static OrderVerdict EvaluateOrder(
        long orderId,
        DateTime dueDate,
        IEnumerable<DemandFulfilment> demands,
        IReadOnlyList<string> demandKeys)
    {
        // 判定期上界：客户**原始 DATE** ⇒ 交期日最后一刻（次日 00:00 − 1 tick）。
        // 订单级交期不带 Manual 覆盖语义（`:9`「以原始 DATE 交期比较」）⇒ isManualOverride 恒 false。
        var bound = ConstraintContext.ToJudgementBound(dueDate, isManualOverride: false);

        var demandList = demands as IReadOnlyList<DemandFulfilment> ?? demands.ToList();

        // Fail Closed：任一需求未完成 / 完成时间不可信 ⇒ 整单不按期。
        var unfulfilled = demandList
            .Where(d => d.CompletionTime is null || !d.CompletionTimeCertain)
            .Select(d => d.LogicalDemandKey)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var roots = demandList.Where(d => d.IsRootDemand).ToList();
        var basis = roots.Count > 0 ? FulfilmentBasis.RootDemand : FulfilmentBasis.AllDemandsFallback;
        var basisSet = roots.Count > 0 ? roots : demandList;

        // 取末端完成的最大值：**不是**「所有下阶 Task 最大结束时间」（`:18` 明令禁止）。
        var completions = basisSet
            .Where(d => d.CompletionTime is not null)
            .Select(d => d.CompletionTime!.Value)
            .ToList();
        DateTime? fulfilment = completions.Count == 0 ? null : completions.Max();

        // 未完成需求存在 ⇒ 无论末端多早都不算交付。
        var isOnTime = unfulfilled.Count == 0 && fulfilment is not null && fulfilment.Value <= bound;

        // 延期小时数：以「判定期上界」为基准，仅用于解释与留痕（与 PhaseThreeDiagnostics 同口径）。
        decimal lateness = 0m;
        if (fulfilment is not null && fulfilment.Value > bound)
        {
            lateness = Math.Round((decimal)(fulfilment.Value - bound).TotalHours, 4);
        }

        return new OrderVerdict(
            orderId, dueDate, bound, fulfilment, basis, isOnTime, lateness, demandKeys, unfulfilled);
    }

    /// <summary>
    /// **第三层（③ 交期）候选比较**：整单按期率高者胜。返回 &gt;0 ⇒ 挑战者更优，&lt;0 ⇒ 擂主更优，
    /// **0 ⇒ 本层判平，交回既有第 ③ 层规则**（FORWARD/MIXED 更早完成优先、BACKWARD 更靠近 Due 优先）。
    ///
    /// <para>**纯整数交叉相乘**比较两个分数，不做除法、不落小数 ⇒ 无舍入误差、无精度陷阱，
    /// 也不会因为分母不同而出现假的判平。</para>
    ///
    /// <para>任一侧分母为 0（该 Domain 无订单可判）⇒ 不可比 ⇒ 返回 0，交由既有规则处理，
    /// **不得**把「没数据」当成「按期率 0%」。</para>
    ///
    /// <para>⚠ 调用约定：本方法**只在第 ① 可行性、第 ② 履约层判平之后**才允许被调用。
    /// 它不复制也不覆盖那两层，更不参与可行性判定。</para>
    /// </summary>
    internal static int CompareOnTimeRate(RateResult challenger, RateResult incumbent)
    {
        if (challenger is null || incumbent is null) return 0;
        if (challenger.JudgedOrderCount == 0 || incumbent.JudgedOrderCount == 0) return 0;

        // 按期率比较：ci/ci_den  vs  ii/ii_den  ⇔  ci*ii_den vs ii*ci_den（两边同乘，方向不变）。
        var left = (long)challenger.OnTimeOrderCount * incumbent.JudgedOrderCount;
        var right = (long)incumbent.OnTimeOrderCount * challenger.JudgedOrderCount;
        return left.CompareTo(right);
    }

    /// <summary>
    /// 从现有 Core DTO 摊平出需求级履约输入（纯内存、无 DB）。
    /// 供将来的生产调用点与单测共用同一份转换，避免两处各写一套映射。
    ///
    /// <para>转换口径：</para>
    /// <list type="bullet">
    ///   <item>需求来自 <see cref="LogicalProductionDemand"/>：取 <c>DomainKey</c> / <c>LogicalDemandKey</c> /
    ///     <c>OrderId</c> / <c>IsRootDemand</c>。</item>
    ///   <item>完成事实来自 <see cref="DemandCompletionFact"/>：按 <c>LogicalDemandKey</c> 等值连接
    ///     （<b>不按 MaterialId</b> —— 同物料多需求会串）。</item>
    ///   <item>缺完成事实 ⇒ 该需求在结果里 <c>CompletionTime = null</c>（未完成），
    ///     <b>不是</b>丢行 —— 丢行会把「没排下」洗成「不存在」，从而抬高按期率。</item>
    ///   <item><see cref="DemandCompletionFact.CompletionTimeCertain"/> 为 <c>null</c> ⇒ 按契约
    ///     「未标注＝按现状」当作**可信**；只有显式 <c>false</c> 才判不可信。</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<DemandFulfilment> FromSolveResult(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<DemandCompletionFact> completions)
    {
        ArgumentNullException.ThrowIfNull(demands);
        ArgumentNullException.ThrowIfNull(completions);

        var completionByKey = new Dictionary<string, DemandCompletionFact>(StringComparer.Ordinal);
        foreach (var c in completions)
        {
            // 撞键取第一条：契约上同一 LogicalDemandKey 只有一条完成事实；
            // 真出现重复即上游契约破坏，此处不静默取最后一条掩盖它。
            if (!completionByKey.ContainsKey(c.LogicalDemandKey))
            {
                completionByKey[c.LogicalDemandKey] = c;
            }
        }

        var result = new List<DemandFulfilment>(demands.Count);
        foreach (var d in demands)
        {
            if (completionByKey.TryGetValue(d.LogicalDemandKey, out var c))
            {
                result.Add(new DemandFulfilment(
                    d.DomainKey,
                    d.LogicalDemandKey,
                    d.OrderId,
                    c.CompletionTime,
                    // null ＝ 未标注 ⇒「按现状」当可信；只有显式 false 才不可信。
                    CompletionTimeCertain: c.CompletionTimeCertain != false,
                    d.IsRootDemand));
            }
            else
            {
                result.Add(new DemandFulfilment(
                    d.DomainKey,
                    d.LogicalDemandKey,
                    d.OrderId,
                    CompletionTime: null,
                    CompletionTimeCertain: true,
                    d.IsRootDemand));
            }
        }

        return result;
    }
}
