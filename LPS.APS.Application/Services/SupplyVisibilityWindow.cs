namespace LPS.APS.Application.Services;

/// <summary>
/// 供给**可见上界**判定（C10 整改，2026-11-12；依据 0号位 2026-11-12 请裁回复 §二②）。
///
/// <para><b>为什么需要这个类</b>：入池原写作 <c>if (availableAt.HasValue &amp;&amp; availableAt &gt; cutoff) continue;</c>
/// —— 把「数据截止时点」当成了「供给可见上限」。0号位 逐字纠正：</para>
/// <blockquote>
/// 「<c>ScheduleRun.DataCutoffTime</c> 是本次 Run 的数据切面／可用事实与时间重估基准，
/// <b>不是</b>未来 Supply 的最终可见上限」；<c>if(availableAt&gt;cutoff) continue</c>
/// 「对 90 天期内合法未来 PO 不应一刀切」，本 Run 范围依计划窗口契约处理。
/// </blockquote>
/// <para>⇒ 可见上界改取<b>本 Run 计划窗口终点</b>（调用方传 <c>SchedulingContext.PlanHorizonEnd</c>，
/// 与 <c>DomainSolveRequest.PlanningEnd</c> 同源同值的那一个变量）。</para>
///
/// <para><b>两条红线（0号位 原文明令，本类据此收口）</b>：</para>
/// <list type="number">
///   <item><b>不得自建第二时间源</b> —— 禁止用 <c>DataCutoffTime + 90 天</c> 之类自造上界；
///         拿不到真实计划窗口时**回落为 <paramref name="dataCutoffTime"/>**（＝维持原行为），
///         由调用方留 WARN，本类不做任何默认外推。</item>
///   <item><b>不得无限放行未来供给</b> —— 上界之外（如第 100 天的到货之于 90 天窗口）仍排除；
///         但排除必须**可观测**（调用方计数＋留痕），不能像原来那样静默丢弃。</item>
/// </list>
///
/// <para><b>为什么取「窗口终点与截止时点的较大者」</b>：计划窗口若已早于本次数据截止（旧 PV／补跑），
/// 直接用窗口终点会把「截止时点之前本已可用」的供给也截掉 —— 那是比原缺陷更重的回归。
/// 可见性至少要覆盖到数据切面本身，故取 <c>max</c>。</para>
///
/// <para>纯函数、无 I/O、确定性，便于单测（<c>InternalsVisibleTo("LPS.APS.Tests")</c>）。</para>
/// </summary>
internal static class SupplyVisibilityWindow
{
    /// <summary>
    /// 解出供给可见上界：<paramref name="planHorizonEnd"/> 有效且晚于 <paramref name="dataCutoffTime"/>
    /// ⇒ 用窗口终点；否则回落数据截止时点（缺事实时的保守行为，不是外推）。
    /// </summary>
    /// <param name="planHorizonEnd">本 Run 计划窗口终点（<c>SchedulingContext.PlanHorizonEnd</c>）；缺省＝<c>null</c>/<c>default</c>。</param>
    /// <param name="dataCutoffTime">本次 Run 数据截止时点（<c>ScheduleRun.DataCutoffTime</c>，缺省已回退 <c>Now</c>）。</param>
    public static DateTime ResolveEnd(DateTime? planHorizonEnd, DateTime dataCutoffTime)
    {
        if (planHorizonEnd.HasValue
            && planHorizonEnd.Value != default
            && planHorizonEnd.Value > dataCutoffTime)
            return planHorizonEnd.Value;

        return dataCutoffTime;
    }

    /// <summary>
    /// 该可用时点是否落在可见上界之外（＝不进供给池）。
    /// <para>
    /// <b>无时点（<c>null</c>）不归本闸</b>：<c>null</c> 由「无合法可用时点」闸门（0号位 v1.1 §二A／§三「第一拍」）另行处置，
    /// 两类不得混在一处，否则「算不出时点」会被误记成「远未来」。
    /// </para>
    /// <para><b>边界含等</b>：<c>availableAt == 上界</c> 仍可见（半开区间口径与计划窗口一致：区间取 <c>[start, end]</c>）。</para>
    /// </summary>
    public static bool IsBeyond(DateTime? availableAt, DateTime visibilityEnd)
        => availableAt.HasValue && availableAt.Value > visibilityEnd;
}
