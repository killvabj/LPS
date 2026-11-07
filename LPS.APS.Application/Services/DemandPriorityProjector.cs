using System.Collections;
using System.Globalization;
using System.Text.Json;
using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 冻结快照 DemandPriority 子块 → 执行器配置 的投影（2号位 投影层，唯一实现处）。
///
/// 为什么需要一次投影：契约上 3号位 冻结的是 <see cref="DemandPriorityBlock"/>（强类型枚举、**无计算层维度**），
/// 而 <see cref="IDemandPriorityExecutor"/> 消费的是 <see cref="DemandPriorityConfig"/>
/// （字符串词表、每段带 <c>CompetitionRound</c>）。两者不是同一类型，必须有一个显式投影点；
/// 本类是唯一投影实现，禁止在 Provider / 执行器里各写一份。
///
/// 投影规则：
/// 1. **Field / Operator / Direction**：快照用枚举、执行器用字符串词表 → 逐值**显式**映射
///    （刻意不写 <c>enum.ToString()</c>：枚举一旦改名会静默变成未知字段，而显式 switch 会在编译期/运行期立刻暴露）。
///    执行器白名单外的枚举值一律**显式抛错**，不猜配——与执行器 P1-02「未知字段显式报错」同一口径。
/// 2. **Value**：快照为 <c>object?</c>（JSON 反序列化后实为 <see cref="JsonElement"/>；<c>In</c> 时为数组）
///    → 统一转字符串；<c>In</c> 用逗号连接（执行器 IN 分支按 ',' 拆分）。
/// 2b. **MatchConditionGroups（路线甲 DNF）**：组内 AND、组间 OR，**整组逐条**投影（与平铺条件同一 `ProjectCondition`）。
///    组非空 ⇒ 执行器忽略平铺列表；组为空 ⇒ 回退平铺（存量快照语义不变）。
///    2026-11-07 前此处 fail-closed，执行器接线后改为放行。
/// 3. **CompetitionRound（竞争轮次）**：**fail-closed**，三种情形分开处理 ——
///    <list type="bullet">
///      <item>整块**没有任何段**声明 <c>CompetitionRound</c> ⇒ 全部投影为
///        <see cref="DefaultCompetitionRound"/>=1（今日行为不变；未上轮次的快照照常可用）。</item>
///      <item>**部分段声明、部分段没写** ⇒ **显式抛错**，不把没写的段静默归第 1 轮。
///        这是本类唯一能堵的口子：一旦静默归 1，多轮配置会被压扁成单轮而**不报错不告警**，
///        现象只有「排序看起来不对」，排查代价极高。</item>
///      <item>**全部声明** ⇒ 逐个透传，并校验轮次 <c>&gt;= 1</c>。</item>
///    </list>
///    为什么<u>必须给 1 而不能给 0</u>：调用点按轮次过滤段，投影成 0 会让
///    「第 1 轮」取不到任何段 ⇒ 全体 Demand 退化 DemandKey ASC 兜底，冻结策略形同未生效。
///    轮次 ≠ BOM 计算层级（0号位 Q1），不要用 BOM 深度往里塞值。
/// 4. **StableTieBreakFields**：已是强类型 <see cref="TieBreakField"/>（B3 v1.2 §3），
///    枚举成员名**直接等于执行器白名单词**（`DEMANDKEY`），本层只做 `ToString()` 透传，
///    不再有任何手工别名换算。存量快照的 `"OrderId"` 由
///    <see cref="TieBreakFieldJsonConverter"/> 在**反序列化时**归一化，进不到本层。
///    （历史：2026-09-29 实测 RuleSet 478 三个段的 Tie-break 全写 `"OrderId"`，
///    而执行器白名单叫 `DEMANDKEY`，原样透传会让整条 Pegging 失败——这正是改强类型的起因。）
/// </summary>
internal static class DemandPriorityProjector
{
    /// <summary>
    /// 未声明轮次时的默认轮次 = 第 1 轮（顶层独立需求）。
    /// 与 <c>PeggingOrchestrator</c> 里「订单行默认第 1 轮」的取值必须一致。**不可改成 0**。
    /// </summary>
    internal const int DefaultCompetitionRound = 1;

    /// <summary>
    /// 把冻结快照的 DemandPriority 子块投影成执行器配置。子块缺失即报错，不静默给空配置。
    /// </summary>
    /// <param name="block">冻结快照的 DemandPriority 子块。</param>
    /// <param name="frozenAt">
    /// **Run 冻结时点**（= <c>FrozenStrategySnapshot.FrozenAt</c>，Run 启动时刻）。
    /// 仅用于把 `CustomerDueDate` 的**相对日阈值**换算成具体日期，见 <see cref="ConvertRelativeDateThreshold"/>。
    /// 传 <c>null</c> 表示调用方拿不到冻结时点：此时若快照里**确实存在**相对日阈值，**显式抛错**（fail-closed）；
    /// 若不存在，则整个参数无影响，历史调用照旧。
    /// </param>
    internal static DemandPriorityConfig Project(DemandPriorityBlock? block, DateTime? frozenAt = null)
    {
        if (block is null)
        {
            throw new InvalidOperationException(
                "冻结策略快照的 DemandPriority 子块为空，无法投影为执行器配置（禁止静默当空策略执行）");
        }

        var declared = block.Segments.Count(s => s.CompetitionRound.HasValue);

        // 情形一：整块无一轮次声明 ⇒ 全部第 1 轮。这是当前线上快照的形态，行为必须与之前逐字一致。
        if (declared == 0)
        {
            return new DemandPriorityConfig
            {
                Segments = block.Segments.Select(s => ProjectSegment(s, DefaultCompetitionRound, frozenAt)).ToList()
            };
        }

        // 情形二：部分声明 ⇒ fail-closed。宁可整条 Pegging 报错，也不把漏写的段压进第 1 轮。
        if (declared != block.Segments.Count)
        {
            var missing = block.Segments
                .Where(s => !s.CompetitionRound.HasValue)
                .Select(s => $"SegmentOrder={s.SegmentOrder}({s.SegmentName})");
            throw new InvalidOperationException(
                "冻结策略快照的 DemandPriority 子块**部分段声明了 CompetitionRound、部分段没有**，"
                + "无法确定这些段属于哪一轮：" + string.Join("、", missing)
                + "。要么全块声明，要么全块不声明（不声明则统一按第 1 轮）；"
                + "禁止把未声明的段静默归入第 1 轮。");
        }

        // 情形三：全部声明 ⇒ 透传，逐段校验值域。
        foreach (var segment in block.Segments)
        {
            if ((int)segment.CompetitionRound!.Value < 1)
            {
                throw new InvalidOperationException(
                    $"冻结策略快照的 DemandPriority 段 SegmentOrder={segment.SegmentOrder}({segment.SegmentName}) "
                    + $"声明的 CompetitionRound={(int)segment.CompetitionRound.Value} 非法（必须 >= 1）");
            }
        }

        return new DemandPriorityConfig
        {
            Segments = block.Segments
                .Select(s => ProjectSegment(s, (int)s.CompetitionRound!.Value, frozenAt))
                .ToList()
        };
    }

    private static PrioritySegmentConfig ProjectSegment(
        PrioritySegment segment, int competitionRound, DateTime? frozenAt)
    {
        // 路线甲 DNF 条件组（2026-11-07 接线，3号位《null 双缺陷／DNF 段2／CustomerDueDate 分叉》转办件 B）：
        // 组内 AND、组间 OR；非空即以组为准、忽略平铺列表（与快照契约 `MatchConditionGroups` 的优先级一致）。
        // 原实现在此处 fail-closed 抛 `NotSupportedException`（防静默丢组），现执行器已支持真 OR 组求值，
        // 改为**整组投影**：组内每条条件走与平铺条件**同一个** `ProjectCondition`，
        // 因此相对日阈值换算（`CustomerDueDate` 的 `TODAY+N`）在组内同样生效，不会出现两套口径。
        var groups = (segment.MatchConditionGroups ?? [])
            .Select(g => new PriorityConditionGroup
            {
                Conditions = g.Conditions.Select(c => ProjectCondition(c, frozenAt)).ToList()
            })
            .ToList();

        return new PrioritySegmentConfig
        {
            CompetitionRound = competitionRound,
            SegmentOrder = segment.SegmentOrder,
            IsEnabled = segment.IsEnabled,
            MatchConditions = segment.MatchConditions.Select(c => ProjectCondition(c, frozenAt)).ToList(),
            MatchConditionGroups = groups,
            SortFields = segment.SortFields.Select(ProjectSortField).ToList(),
            // Tie-break 已是强类型枚举，成员名即执行器词表词 ⇒ 只做 ToString()，
            // 不再有别名换算（历史别名已在 TieBreakFieldJsonConverter 的反序列化处归一化）。
            StableTieBreakFields = segment.StableTieBreakFields.Select(t => t.ToString()).ToList(),
        };
    }

    private static MatchCondition ProjectCondition(SegmentMatchCondition condition, DateTime? frozenAt)
    {
        var fieldName = MapField(condition.Field);
        var operatorText = MapOperator(condition.Operator);
        var rawValue = ToMatchValue(condition.Value);

        return new MatchCondition
        {
            FieldName = fieldName,
            Operator = operatorText,
            Value = IsRelativeDateThreshold(fieldName, operatorText)
                ? ConvertRelativeDateThreshold(rawValue, frozenAt, condition)
                : rawValue,
        };
    }

    // ── 相对日阈值（`TODAY+20`）──────────────────────────────────────────────────────────
    //
    // 3号位 2026-11-06 回执 §三 的裁决原文口径：
    //   「Run 冻结日 = `FrozenStrategySnapshot.FrozenAt` 的日期部分；配置 `Value` 存整数偏移（如 `20`）；
    //     装载/匹配期把 `FrozenAt.Date + OffsetDays` 换算为具体日期再比较；发布侧不预折算。」
    // ⇒ 换算点就在本投影层（装载期），**不改执行器**：换算后的值就是一个普通日期串，
    //    执行器 `CompareNumericOrDate` 的日期分支天然处理。
    //
    // 为什么必须换而不是「留给执行器比」：执行器 `CompareNumericOrDate` 有三段兜底（日期→数值→字符串）。
    //   `"2026-11-28T00:00:00.0000000"` 与 `"20"` 两侧都解析不成日期、也解析不成数值 ⇒ **落到字符串比较**，
    //   而 `"20"` 恰是 `"2026-…"` 的前缀 ⇒ `LTE 20` **恒 false（命中 0 条）**、`GTE 20` **恒 true（全命中）**。
    //   发布放行、运行不报错、段内静默漏排或全吃 —— 正是「防空转」红线要挡的那类。
    //   本换算走「投影层」而非「执行器条件求值」，是为了让**配置在装载期即定型**、行为可用单元测试钉住。

    /// <summary>参与相对日换算的操作符：只有大小比较有意义（EQ/IN 比较日期时配置必须写具体日期）。</summary>
    private static readonly string[] RelativeDateThresholdOperators = ["LT", "LTE", "GT", "GTE"];

    /// <summary>
    /// 该条件是否为「相对日阈值」。支持 `CustomerDueDate` ＋ `IssueDate`（2号位 2026-11-07 回执答 A-扩）。
    /// 段2 组B 用 `IssueDate &lt; 冻结日-30`（相对日），须走换算否则 `-30` 原样进执行器恒不命中（空转）。
    /// 不扩到 `DUEDATE`：那会把「显式日期的字段」悄悄改成「偏移语义」，属于自造口径（防空转）。
    /// </summary>
    private static bool IsRelativeDateThreshold(string fieldName, string operatorText)
        => (string.Equals(fieldName, "CUSTOMERDUEDATE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fieldName, "ISSUEDATE", StringComparison.OrdinalIgnoreCase))
           && RelativeDateThresholdOperators.Contains(operatorText, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 相对日阈值换算：纯整数 <c>N</c> ⇒ <c>FrozenAt.Date + N 天</c> 的日期串；否则原样返回。
    ///
    /// 三种输入分开处理，**不猜**：
    /// <list type="bullet">
    ///   <item>已是具体日期（如 `2026-11-20`）⇒ **原样保留**。显式日期写法合法，不强制改成偏移。</item>
    ///   <item>纯整数（如 `20`、`0`、`-5`）⇒ 换算成冻结日 + N 天。</item>
    ///   <item>既非日期也非整数 ⇒ **原样保留**，交给执行器按原值比较（拼写错误由运行期表现暴露，不在这里臆断）。</item>
    /// </list>
    ///
    /// 需要换算但拿不到 <paramref name="frozenAt"/>（或其为 `default`）⇒ **显式抛错**。
    /// 不静默跳过：跳过就等于把上面那个「恒 false / 恒 true」的静默错判又放回来，
    /// 且只在生产排出错序时才可能被发现。
    /// </summary>
    private static string ConvertRelativeDateThreshold(
        string rawValue, DateTime? frozenAt, SegmentMatchCondition condition)
    {
        var text = rawValue.Trim();
        if (text.Length == 0)
        {
            return rawValue;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return rawValue;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offsetDays))
        {
            return rawValue;
        }

        if (frozenAt is null || frozenAt.Value == default)
        {
            throw new InvalidOperationException(
                $"DemandPriority 快照的 {condition.Field} 命中条件配置了相对日阈值 '{text}'（整数偏移天数），"
                + "但投影时未拿到 Run 冻结时点（FrozenStrategySnapshot.FrozenAt）⇒ 无法换算成具体日期。"
                + "拒绝静默跳过：不换算会让该条件退化为字符串比较（LTE 恒 false / GTE 恒 true），"
                + "发布放行、运行不报错、段内静默漏排。请由调用方传入冻结时点。");
        }

        // 用 `yyyy-MM-dd` 而非 `"O"`：阈值只需要到天（`FrozenAt.Date`），带时分秒反而给了「同一天边界」歧义。
        return frozenAt.Value.Date.AddDays(offsetDays)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static SortField ProjectSortField(SegmentSortField sortField) => new()
    {
        FieldName = MapField(sortField.Field),
        Direction = sortField.Direction switch
        {
            SortDirection.Asc => "ASC",
            SortDirection.Desc => "DESC",
            _ => throw new NotSupportedException(
                $"DemandPriority 快照排序方向 {sortField.Direction} 不受支持（仅 Asc/Desc）")
        },
    };

    /// <summary>
    /// 快照字段枚举 → 执行器白名单字符串。
    /// 白名单以 <c>DemandPriorityExecutor.KnownFields</c> 为准：
    /// ORDERTYPE / DELAYSTATUS / CUSTOMERTIER / DUEDATE / ISSUEDATE / PROTECTIONSTATUS / DEMANDKEY。
    /// </summary>
    private static string MapField(DemandField field) => field switch
    {
        // 2026-11-07 0号位 裁决：DelayStatus / DemandMaturityStatus 归【参数集】（需求保护），不入规则集
    //   （478 已整份替换为整理稿 6 段，不含这两字段）。本映射同步剔除 ⇒ 若规则集仍引用则 fail-closed
    //   NotSupportedException（防空转：与 Validator 白名单同口径，防「校验拒绝、投影放行」）。
        DemandField.CustomerTier => "CUSTOMERTIER",
        DemandField.OrderType => "ORDERTYPE",
        DemandField.DueDate => "DUEDATE",
        DemandField.IssueDate => "ISSUEDATE",
        // 2026-11-06 V1.1（3号位 实施包 v1.0 §一.2）。当日只映射执行器已接线的 7 个；
        //   StockConsumeDay 当时无载体 ⇒ fail-closed。**当晚载体（`IAD.AverageDailyConsumption`）落库，已补映射（见本 switch 末项）。**
        DemandField.CustomerSegment      => "CUSTOMERSEGMENT",
        DemandField.CustomerDueDate      => "CUSTOMERDUEDATE",
        DemandField.TransportMode        => "TRANSPORTMODE",
        DemandField.SalesOrderCategory   => "SALESORDERCATEGORY",
        DemandField.NonStockShipCategory  => "NONSTOCKSHIPCATEGORY",
        DemandField.MaterialCode         => "MATERIALCODE",
        DemandField.Quantity             => "QUANTITY",
        // 2026-11-06 晚：库存消耗日载体已落库（`InventoryAvailableSupplyDetail.AverageDailyConsumption`），
        //   执行器 `KnownFields` 已接线 ⇒ 解除 fail-closed。
        DemandField.StockConsumeDay      => "STOCKCONSUMEDAY",
        // RemainingTimeHours / IsPmcProtected / PriorityLevel：快照枚举里有，但执行器白名单无对应字段。
        // 不猜配（例如把 IsPmcProtected 猜成 PROTECTIONSTATUS）——静默错排比报错危险得多。
        _ => throw new NotSupportedException(
            $"DemandPriority 快照使用了字段 {field}，但 2号位 执行器白名单无对应字段"
            + "（白名单：ORDERTYPE/DELAYSTATUS/CUSTOMERTIER/DUEDATE/ISSUEDATE/PROTECTIONSTATUS/DEMANDKEY）。"
            + " 需 1↔3 契约对齐后再启用，不得猜配。")
    };

    /// <summary>快照操作符枚举 → 执行器词表。执行器支持 EQ/IN/LT/LTE/GT/GTE（无 NE）。</summary>
    private static string MapOperator(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => "EQ",
        ConditionOperator.LessThan => "LT",
        ConditionOperator.LessOrEqual => "LTE",
        ConditionOperator.GreaterThan => "GT",
        ConditionOperator.GreaterOrEqual => "GTE",
        ConditionOperator.In => "IN",
        // 2026-11-06：实施包 v1.0 §二.2 新增「包含」算子（供 MaterialCode 模糊匹配）
        ConditionOperator.Contains => "CONTAINS",
        _ => throw new NotSupportedException(
            $"DemandPriority 快照操作符 {op} 不受 2号位 执行器支持（支持 EQ/IN/LT/LTE/GT/GTE）")
    };

    /// <summary>
    /// 快照比较值 → 字符串。JSON 反序列化后 Value 实为 <see cref="JsonElement"/>
    /// （<c>In</c> 时为数组），必须显式处理，否则会得到 "System.Text.Json.JsonElement" 这种废串。
    /// </summary>
    private static string ToMatchValue(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;

            case JsonElement { ValueKind: JsonValueKind.Array } array:
                return string.Join(",", array.EnumerateArray().Select(e => e.ToString()));

            case JsonElement element:
                return element.ToString();

            case string s:
                return s;

            // In 的多值也可能是普通集合（快照对象由代码构造而非 JSON 时）
            case IEnumerable sequence:
                return string.Join(",", sequence.Cast<object?>().Select(v => v?.ToString() ?? string.Empty));

            default:
                return value.ToString() ?? string.Empty;
        }
    }
}
