using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Application.Services;

/// <summary>
/// 工厂 / 生产部门级**生效 WORK 窗口**投影（G-2）：把 `ProductionWorkCalendarSlot` 的上下两级事实，
/// 按三级继承规则合成后**逐日摊平**成 1号位 可直接累计工时的绝对时间窗列表。
///
/// 【为什么由我方合成、而不是把原始行丢给 1号位】
///   · 规则清单 v1.7 RC-001 逐字：「**2号位 合成合法能力窗口、1号位 只消费**」。
///   · 合成不是「取并集」也不是「谁细听谁」，而是 **有事实的层参与 + WORK 取交集 + OFF 作例外裁剪**。
///     若把两层原始行交出去、由消费侧自行判「部门有事实就用部门」，两层同时有事实的当天会**漏掉工厂级 OFF**
///     ⇒ 违反 RC-006「明确关闭不可由上级复活」。⇒ 合成只有一个实现（<see cref="ResourceCalendarResolver"/>）。
///
/// 【输出形态（1号位 侧零回落逻辑）】
///   · `DepartmentId = null` 的行 = 厂级口径（消费侧**拿不到部门**时用）；
///   · `DepartmentId = d` 的行 = 部门 d 的生效窗口。**凡本域部门上下文能解析出的部门都有一组行**；
///     该部门自身无事实时，这组行就是其工厂行的物化副本 —— 即「继承」已在这里做完，消费侧不做二级回落。
///   · 某 scope 某日不在列表 = 该日**无合法工时**（不是缺数据）；整表为空 = 本域没有任何可投影的归属。
///
/// 【不做什么】
///   · 不补 8 小时 / 7×24（RC-007）；不把「无事实」翻译成「全月无休」。
///   · 不做资源层（设备 / 人工槽）—— 那一路走 <see cref="ResourceCalendarResolver.Resolve"/> → `CalendarSlots`。
///   · 不跨午夜：每个窗口都落在单个自然日内。
/// </summary>
public static class DepartmentWorkWindowProjector
{
    /// <summary>投影结果（含诊断计数；窗口本身是唯一给 1号位 的部分）。</summary>
    public sealed class Result
    {
        public IReadOnlyList<DepartmentWorkWindow> Windows { get; init; }
            = Array.Empty<DepartmentWorkWindow>();

        /// <summary>投影出的厂级 scope 数（`DepartmentId = null` 的组数）。</summary>
        public int FactoryScopeCount { get; init; }

        /// <summary>投影出的部门 scope 数（含自身无事实、由工厂继承而来的部门）。</summary>
        public int DepartmentScopeCount { get; init; }

        /// <summary>其中**自身有日历事实**的部门数（其余为纯继承）。</summary>
        public int DepartmentWithOwnFactsCount { get; init; }

        /// <summary>计划期自然日数。</summary>
        public int DaysProjected { get; init; }

        /// <summary>同层矛盾被整日拒绝的「scope × 日」数（>0 需回 5号位 核查源数据）。</summary>
        public int ConflictDayCount { get; init; }

        /// <summary>整期一片窗口都拿不到的 scope 数（= 该 scope 全期无工时）。</summary>
        public int ScopesWithoutAnyWindow { get; init; }

        /// <summary>部门主档缺失（查不到 FactoryId）而无法投影的部门数。</summary>
        public int DepartmentsWithoutFactoryCount { get; init; }

        /// <summary>计划期非法（起 &gt; 止 / 未赋值）⇒ 未投影。</summary>
        public bool HorizonInvalid { get; init; }

        /// <summary>命中安全上限被截断（防异常数据把内存拖爆；正常量级远达不到）。</summary>
        public bool Truncated { get; init; }
    }

    /// <summary>单次投影的窗口上限（≈ 200k 个小对象；实测 161 部门 × 90 日 × ~2 片 ≈ 2.9 万）。</summary>
    private const int MaxWindowCount = 200_000;

    /// <summary>截断阈值（日志/诊断用；与内部阈值同值）。</summary>
    public const int MaxWindowCountForDiagnostics = MaxWindowCount;

    /// <summary>计划期安全上限（10 年），与 <see cref="ResourceCalendarResolver"/> 的防御上限同口径。</summary>
    private const int MaxDays = 3653;

    /// <summary>
    /// 生成窗口列表。
    /// </summary>
    /// <param name="rows">`ProductionWorkCalendarSlot` 行（调用方按 `IsActive=1` 装载；**不在这里再过滤有效期**，
    /// 有效期按日判定由解析器做）。</param>
    /// <param name="factoryIdByDepartmentId">生产部门主档 Id → FactoryId（缺失的部门不投影、计数）。</param>
    /// <param name="usedDepartmentIds">本域实际用到的生产部门（来自 `MaterialStageDepartmentContexts`）。</param>
    /// <param name="usedFactoryIds">
    /// 本域需求出现的工厂（`LogicalProductionDemand.FactoryId`）。与「部门所属工厂」**取并集**，
    /// 否则「部门上下文缺该 Stage、但有厂级事实」的工厂会整厂缺行 —— 消费侧按 (厂, null) 查不到就会当成无工时。
    /// 传 null = 只按部门所属工厂投影。
    /// </param>
    /// <param name="horizonStart">计划期起（含；按自然日取整）。</param>
    /// <param name="horizonEnd">计划期止（含；按自然日取整）。</param>
    public static Result Project(
        IReadOnlyList<ProductionWorkCalendarSlot> rows,
        IReadOnlyDictionary<int, int> factoryIdByDepartmentId,
        IReadOnlyCollection<int> usedDepartmentIds,
        IReadOnlyCollection<int>? usedFactoryIds,
        DateTime horizonStart,
        DateTime horizonEnd)
    {
        if (horizonStart == default || horizonEnd == default || horizonEnd.Date < horizonStart.Date)
            return new Result { HorizonInvalid = true };

        var firstDay = horizonStart.Date;
        var lastDay = horizonEnd.Date;
        var dayCount = (lastDay - firstDay).Days + 1;
        if (dayCount > MaxDays)
        {
            dayCount = MaxDays;
            lastDay = firstDay.AddDays(MaxDays - 1);
        }

        // ── 按 scope 分组（与 `ResourceCalendarResolver.Resolve` 同一分组口径）──
        var active = (rows ?? Array.Empty<ProductionWorkCalendarSlot>())
            .Where(r => r.IsActive)
            .ToList();

        var factoryRows = active
            .Where(r => ScopeEquals(r.ScopeType, "FACTORY"))
            .GroupBy(r => r.FactoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductionWorkCalendarSlot>)g.ToList());

        var departmentRows = active
            .Where(r => ScopeEquals(r.ScopeType, "DEPARTMENT") && r.ProductionDepartmentId.HasValue)
            .GroupBy(r => r.ProductionDepartmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductionWorkCalendarSlot>)g.ToList());

        // ── 本域部门 → 工厂 ──
        var scopes = new List<(int DepartmentId, int FactoryId)>();
        var withoutFactory = 0;

        foreach (var deptId in (usedDepartmentIds ?? Array.Empty<int>()).Distinct().OrderBy(x => x))
        {
            if (factoryIdByDepartmentId is not null && factoryIdByDepartmentId.TryGetValue(deptId, out var factoryId))
                scopes.Add((deptId, factoryId));
            else
                withoutFactory++;
        }

        // 厂级 scope = 部门所属工厂 ∪ 需求出现的工厂（去重后排序，保证载荷顺序稳定）。
        var factories = scopes.Select(s => s.FactoryId)
            .Concat(usedFactoryIds ?? Array.Empty<int>())
            .Where(f => f > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var windows = new List<DepartmentWorkWindow>();
        var conflictDays = 0;
        var withoutAnyWindow = 0;
        var deptWithOwnFacts = 0;
        var truncated = false;

        // ── 厂级 scope ──
        foreach (var factoryId in factories)
        {
            factoryRows.TryGetValue(factoryId, out var fRows);
            if (AppendScope(windows, factoryId, null, fRows, null, firstDay, dayCount,
                    ref conflictDays, ref truncated))
                withoutAnyWindow++;
            if (truncated) break;
        }

        // ── 部门 scope（自身无事实 ⇒ 合成结果自然等于工厂行，即继承已物化）──
        if (!truncated)
        {
            foreach (var (deptId, factoryId) in scopes)
            {
                factoryRows.TryGetValue(factoryId, out var fRows);
                departmentRows.TryGetValue(deptId, out var dRows);
                if (dRows is { Count: > 0 }) deptWithOwnFacts++;

                if (AppendScope(windows, factoryId, deptId, fRows, dRows, firstDay, dayCount,
                        ref conflictDays, ref truncated))
                    withoutAnyWindow++;

                if (truncated) break;
            }
        }

        return new Result
        {
            Windows = windows,
            FactoryScopeCount = factories.Count,
            DepartmentScopeCount = scopes.Count,
            DepartmentWithOwnFactsCount = deptWithOwnFacts,
            DaysProjected = dayCount,
            ConflictDayCount = conflictDays,
            ScopesWithoutAnyWindow = withoutAnyWindow,
            DepartmentsWithoutFactoryCount = withoutFactory,
            Truncated = truncated
        };
    }

    /// <summary>
    /// 把一个 scope 的整期窗口追加进 <paramref name="sink"/>。返回 **true = 整期一片窗口都没有**。
    /// </summary>
    private static bool AppendScope(
        List<DepartmentWorkWindow> sink,
        int factoryId,
        int? departmentId,
        IReadOnlyList<ProductionWorkCalendarSlot>? factoryRows,
        IReadOnlyList<ProductionWorkCalendarSlot>? departmentRows,
        DateTime firstDay,
        int dayCount,
        ref int conflictDays,
        ref bool truncated)
    {
        var added = 0;

        for (var i = 0; i < dayCount; i++)
        {
            var day = firstDay.AddDays(i);
            var dayResult = ResourceCalendarResolver.ComposeUpperScopeDay(factoryRows, departmentRows, day);

            if (dayResult.IsConflict) conflictDays++;

            foreach (var w in dayResult.Windows)
            {
                // 解析器已保证 S < E；此处不再造零长片。
                sink.Add(new DepartmentWorkWindow
                {
                    FactoryId = factoryId,
                    DepartmentId = departmentId,
                    Start = w.Start,
                    End = w.End
                });
                added++;

                if (sink.Count >= MaxWindowCount) { truncated = true; return added == 0; }
            }
        }

        return added == 0;
    }

    private static bool ScopeEquals(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
