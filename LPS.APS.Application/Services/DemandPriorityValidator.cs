using LPS.APS.Core.Dto;
using System.Text.Json;

namespace LPS.APS.Application.Services;

/// <summary>
/// Demand Priority 发布前校验服务（阶段 C-3）
/// 红线校验：
/// 1. 禁止全局 PriorityScore
/// 2. 第一命中即止（命中后不再匹配后续 Segment）
/// 3. Segment 配置合法性
/// </summary>
public sealed class DemandPriorityValidator
{
    /// <summary>
    /// 运行侧**实际支持**的匹配/排序字段白名单。
    ///
    /// 唯一真源 = <c>DemandPriorityExecutor.KnownFields</c>（7 词表）：
    /// `ORDERTYPE / DELAYSTATUS / CUSTOMERTIER / DUEDATE / ISSUEDATE / PROTECTIONSTATUS / DEMANDKEY`。
    /// 其中 `PROTECTIONSTATUS` / `DEMANDKEY` 没有对应的 <see cref="DemandField"/>，
    /// 所以落在本白名单上的就是下面 5 个。
    ///
    /// 2026-10-20（B3 v1.2 §3.2）：**收紧至运行侧 5 值**。此前这里比运行侧宽 3 个
    /// （`RemainingTimeHours` / `IsPmcProtected` / `PriorityLevel`），只有旧 <c>DemandPriorityMatcher</c> 认它们，
    /// 运行时 <c>DemandPriorityExecutor</c> 一律抛 `NotSupportedException`
    /// ⇒ 发布放行、运行期炸。旧 Matcher 只被测试消费，不能作为白名单依据。
    /// 新增 <see cref="DemandField"/> 值时**必须先让 2号位 在执行器落地比较分支**，再来这里登记。
    /// </summary>
    private static readonly HashSet<DemandField> SupportedDemandFields =
    [
        // 2026-11-07 0号位 裁决：延迟状态 `DelayStatus`、需求成熟度 `DemandMaturityStatus`
        // **归属参数集**（需求保护：`ProtectDelayed` / `ProtectMaturityStatus`），【本就不该存放在规则集】，
        // 故不在本白名单（规则集命中/排序字段）内。「延迟」与「成熟度」是保护触发条件，不是优先级分段条件。
        // ⚠ 防空转一致：478 已完成整份替换为整理稿 6 段（不含这两个字段）；此白名单同步剔除——
        //   规则集配置若再引用 DelayStatus/DemandMaturityStatus 作命中/排序，发布期即拦截。
        //   A系 `DemandPriorityExecutor` 仍读这两个字段作参数集保护比较，规则集侧不再承载。
        DemandField.CustomerTier,
        DemandField.OrderType,
        DemandField.DueDate,     // P0-07
        DemandField.IssueDate,   // P0-07
        // 2026-11-06 2号位 来文核准：截 8 字段已在 `DemandPriorityExecutor.KnownFields` 真实接线（匹配+排序分支落地），放行。
        DemandField.CustomerSegment,        // 客户区分
        DemandField.CustomerDueDate,        // 出荷指示入物流日
        DemandField.TransportMode,          // 运输方式
        DemandField.SalesOrderCategory,     // 销售类别
        DemandField.NonStockShipCategory,   // 非在库出荷区分
        DemandField.MaterialCode,           // 物料编码
        DemandField.Quantity,               // 数量
        DemandField.StockConsumeDay,        // 库存消耗日（2026-11-06 放行：载体 `IAD.AverageDailyConsumption` 已落库、执行器/投影器已接线＝防空转满足；取值=小数 CoverageDays）
    ];

    /// <summary>
    /// **值域白名单注册表**（命中条件字段 → 合法值集，2026-11-06）。
    /// 与 <see cref="SupportedDemandFields"/>（字段名白名单）分层：
    /// - 字段名白名单：该字段能否被使用（Source 运行侧已接线才放行）
    /// - 值域白名单：字段可用时，其命中**值**必须落在合法枚举内
    ///
    /// 这里统一登记各命中字段的维护枚举值，拦截拼写错误（如 "销售"、"SALESORDER"、"一般"）在发布期
    /// 即拒绝，避免运行期与实库/维护值比对时空匹配或误匹配。
    ///
    /// 注意：**新字段的字段名白名单仍须 2号位 在 A系 <c>KnownFields</c> 接线后**才由
    /// <see cref="SupportedDemandFields"/> 放行；值域白名单可先行登记（纯登记、不改变可用性），
    /// 待字段可用时即作为第二道闸自动生效（实施包 §五 空转声明）。
    /// </summary>
    private static readonly Dictionary<DemandField, IReadOnlySet<string>> SupportedFieldValues = new()
    {
        [DemandField.OrderType] = new HashSet<string>
        {
            // 注意：存量运行侧 OrderType 用 `SO`/`WO` 旧词（快照478、In["SO","WO"] 即此），
            // Excel 新规则词为 SALES_ORDER/PRODUCTION_INSTRUCTION。二者是**同义别名**，须并存，
            // 否则值域校验会误拦存量快照、致重新发布原地失败（防空转不得牺牲存量兼容）。
            // 别名映射：SO = SALES_ORDER（销售订单）、WO = PRODUCTION_INSTRUCTION（生产指示）。
            "SALES_ORDER",            // 销售订单（Excel 新词）
            "PRODUCTION_INSTRUCTION", // 生产指示（Excel 新词）
            "SO",                     // 存量旧词（等价 SALES_ORDER）
            "WO",                     // 存量旧词（等价 PRODUCTION_INSTRUCTION）
        },
        // == V1.1 新增字段值域（备登记；字段名白名单待 2号位 A系 接线后放行）==
        [DemandField.CustomerSegment] = new HashSet<string>
        {
            "JAPAN", "DOMESTIC", "OVERSEAS", "VIETNAM", "INTER_FACTORY", "OTHER",
        },
        [DemandField.SalesOrderCategory] = new HashSet<string>
        {
            "DIRECT_SALES", "SALES_REPLENISHMENT",
        },
        [DemandField.TransportMode] = new HashSet<string>
        {
            "LAND", "SEA", "AIR", "COURIER",
        },
        // 注：DemandMaturityStatus（需求成熟度）不在此册——归属参数集（需求保护，非规则集命中），
        //     其值域 PRE_CONFIRMED/FORECAST 由参数集保护条件（§一.4 IN 集）承载。
        [DemandField.NonStockShipCategory] = new HashSet<string>
        {
            "FULL_PURPLE_SLIP", "DIFF_PURPLE_SLIP",
        },
    };

    /// <summary>
    /// 运行侧支持的操作符白名单（执行器支持 EQ/IN/LT/LTE/GT/GTE/CONTAINS，**无 NE**）。
    /// `NotEquals` 在发布期即拦截，不留到运行期（B3 v1.2 §3.2）。
    /// 2026-11-06：`Contains` 放行——真运行侧 <c>DemandPriorityExecutor</c> 的 CONTAINS 已落地**前缀匹配**
    /// （`:234-235` `.StartsWith(...TrimEnd('*'))`），非空转，可发布（防空转：只放行真能跑的算子）。
    /// </summary>
    private static readonly HashSet<ConditionOperator> SupportedOperators =
    [
        ConditionOperator.Equals,
        ConditionOperator.LessThan,
        ConditionOperator.LessOrEqual,
        ConditionOperator.GreaterThan,
        ConditionOperator.GreaterOrEqual,
        ConditionOperator.In,
        ConditionOperator.Contains,
    ];

    /// <summary>
    /// 验证 DemandPriorityBlock 配置的合法性
    /// </summary>
    public ValidationResult Validate(DemandPriorityBlock block)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (block.Segments.Count == 0)
        {
            errors.Add("至少需要配置一个优先级 Segment");
            return new ValidationResult(false, errors, warnings);
        }

        var orders = block.Segments.Select(s => s.SegmentOrder).ToList();
        if (orders.Count != orders.Distinct().Count())
        {
            errors.Add("Segment SegmentOrder 必须唯一");
        }

        foreach (var segment in block.Segments)
        {
            if (segment.SegmentOrder < 1)
            {
                errors.Add($"Segment {segment.SegmentOrder}: SegmentOrder 必须为正整数");
            }
        }

        foreach (var segment in block.Segments)
        {
            ValidateSegment(segment, errors, warnings);
            ValidateFieldMapping(segment, errors);   // P0-07：合法字段映射（与运行侧执行器白名单一致）
        }

        ValidateFallbackSegment(block, errors);
        ValidateNoGlobalPriorityScore(block, errors);

        return new ValidationResult(errors.Count == 0, errors, warnings);
    }

    private void ValidateSegment(PrioritySegment segment, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(segment.SegmentName))
        {
            errors.Add($"Segment {segment.SegmentOrder}: SegmentName 不能为空");
        }

        // 空条件＝「无条件命中」的兜底段，语义上合法（执行器 IsMatchSegment 对空表返回 true），
        // 但**只允许一段且必须在段序末**，由 ValidateFallbackSegment 统一判定。
        foreach (var condition in segment.MatchConditions)
        {
            ValidateMatchCondition(segment.SegmentOrder, condition, errors);
        }

        if (segment.SortFields.Count == 0)
        {
            warnings.Add($"Segment {segment.SegmentOrder}: 建议配置至少一个排序字段（当前保持原顺序）");
        }
    }

    /// <summary>
    /// 兜底段（`MatchConditions` 为空＝无条件命中）的准入规则（B3 v1.2 §3.2，2026-10-20）。
    ///
    /// 为什么放行：现行线上快照 <c>RuleSetVersion 478</c> 的段 3 就是空条件兜底，
    /// 执行器 `IsMatchSegment` 对空表返回 true。原规则「至少需要一个匹配条件」会让
    /// **每次重新发布现行快照都失败**，是把合法配置判成非法。
    ///
    /// 为什么要限制：空条件段会**吞掉**它之后的所有段（First-Match 先命中先返回）。
    /// 所以只允许**恰好一段**，且必须位于**段序末**，否则后面的段永远不可达。
    /// </summary>
    private void ValidateFallbackSegment(DemandPriorityBlock block, List<string> errors)
    {
        var fallbacks = block.Segments.Where(s => s.MatchConditions.Count == 0).ToList();
        if (fallbacks.Count == 0)
        {
            return;
        }

        if (fallbacks.Count > 1)
        {
            errors.Add(
                $"空条件兜底段只能有 1 个，实际有 {fallbacks.Count} 个："
                + string.Join("、", fallbacks.Select(s => $"SegmentOrder={s.SegmentOrder}")));
            return;
        }

        var maxOrder = block.Segments.Max(s => s.SegmentOrder);
        if (fallbacks[0].SegmentOrder != maxOrder)
        {
            errors.Add(
                $"Segment {fallbacks[0].SegmentOrder}: 空条件兜底段必须置于段序末（当前最大 SegmentOrder={maxOrder}），"
                + "否则其后各段永远不可达");
        }
    }

    private void ValidateMatchCondition(int segmentOrder, SegmentMatchCondition condition, List<string> errors)
    {
        if (!SupportedOperators.Contains(condition.Operator))
        {
            errors.Add(
                $"Segment {segmentOrder}: 操作符 {condition.Operator} 运行侧不支持"
                + "（支持 EQ/IN/LT/LTE/GT/GTE；NotEquals 无对应实现）");
        }

        // 非 Equals/NotEquals 操作符必须有值；Equals/NotEquals 允许 null（语义为"字段为空"）
        if (condition.Value == null && condition.Operator != ConditionOperator.Equals && condition.Operator != ConditionOperator.NotEquals)
        {
            errors.Add($"Segment {segmentOrder}: {condition.Operator} 操作符的条件值不能为 null");
        }

        // IN 操作符要求值必须是列表类型（排除字符串，因为 string 也实现 IEnumerable<char>）；
        // 判定须同时认 JsonElement 数组——见 IsListValue 注释（4号位 2026-10-25 F2）。
        if (condition.Operator == ConditionOperator.In && !IsListValue(condition.Value))
        {
            errors.Add($"Segment {segmentOrder}: IN 操作符的值必须是列表类型");
        }

        // 值域白名单：命中字段若登记了合法值集，校验其值（Equals 单值 / In 列表）是否落在值域内
        //（2026-11-06）。OrderType 及 V1.1 命中字段已登记，拦截拼写错在发布期。
        if (SupportedFieldValues.TryGetValue(condition.Field, out var allowed)
            && condition.Operator is ConditionOperator.Equals or ConditionOperator.In)
        {
            foreach (var v in EnumerateConditionValues(condition.Value))
            {
                if (!allowed.Contains(v, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Segment {segmentOrder}: {condition.Field} 命中值 {v} 不在合法值域内"
                        + $"（支持 {string.Join(" / ", allowed)}）");
                }
            }
        }
    }

    /// <summary>IN 的值必须是列表。JSON 数组经 System.Text.Json 反序列化为 <see cref="JsonElement"/>（非 List&lt;object&gt;），
    /// 而 .NET 8 的 JsonElement **不实现** System.Collections.IEnumerable ⇒ 不能用 `is IEnumerable` 单独判，
    /// 否则任何数组都被误判「不是列表」。数组分量须置于 null/string 检查之前（JsonElement 为 struct，不受前两类命中）。</summary>
    private static bool IsListValue(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } => true,
        null or string => false,
        System.Collections.IEnumerable => true,
        _ => false
    };

    /// <summary>
    /// 提取命中值清单（兼容 Equals 的 string 与 In 的 JsonElement 数组两种形态）。
    /// 只在 <see cref="ValidateMatchCondition"/> 对 Equals/In 操作符调用；null 返回空集。
    /// </summary>
    private static IEnumerable<string> EnumerateConditionValues(object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            return array.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .OfType<string>();
        }

        if (value is JsonElement { ValueKind: JsonValueKind.String } str)
        {
            return [str.GetString() ?? string.Empty];
        }

        if (value is System.Collections.IEnumerable sequence and not string)
        {
            return sequence.Cast<object?>().Select(v => v?.ToString()).OfType<string>();
        }

        return [value.ToString() ?? string.Empty];
    }

    /// <summary>
    /// 校验 Segment 引用的所有 DemandField 均在 Matcher 支持白名单内（P0-07）。
    /// 防止枚举新增值未同步 Matcher 时，配置越过发布校验、运行期才抛 NotSupportedException。
    /// </summary>
    private void ValidateFieldMapping(PrioritySegment segment, List<string> errors)
    {
        foreach (var condition in segment.MatchConditions)
        {
            if (!SupportedDemandFields.Contains(condition.Field))
            {
                errors.Add($"Segment {segment.SegmentOrder}: 匹配字段 {condition.Field} 不在合法字段白名单内");
            }
        }

        foreach (var sortField in segment.SortFields)
        {
            if (!SupportedDemandFields.Contains(sortField.Field))
            {
                errors.Add($"Segment {segment.SegmentOrder}: 排序字段 {sortField.Field} 不在合法字段白名单内");
            }
        }
    }

    private static readonly string[] ForbiddenGlobalScoreFields =
        ["PriorityScore", "GlobalPriorityScore"];

    private void ValidateNoGlobalPriorityScore(DemandPriorityBlock block, List<string> errors)
    {
        foreach (var segment in block.Segments)
        {
            foreach (var sortField in segment.SortFields)
            {
                var fieldName = sortField.Field.ToString();
                if (ForbiddenGlobalScoreFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Segment {segment.SegmentOrder}: 红线违规：禁止使用全局 {fieldName} 字段");
                }
            }
        }
    }
}

public sealed class ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ValidationResult(bool isValid, IReadOnlyList<string> errors, IReadOnlyList<string>? warnings = null)
    {
        IsValid = isValid;
        Errors = errors;
        Warnings = warnings ?? [];
    }

    public string GetErrorMessage() => string.Join("; ", Errors);
}
