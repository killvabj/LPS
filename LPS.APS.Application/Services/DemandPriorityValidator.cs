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
    /// 新增 <see cref="DemandField"/> 值时**必须先让 1号位 在执行器落地比较分支**，再来这里登记。
    /// </summary>
    private static readonly HashSet<DemandField> SupportedDemandFields =
    [
        DemandField.DelayStatus,
        DemandField.CustomerTier,
        DemandField.OrderType,
        DemandField.DueDate,     // P0-07
        DemandField.IssueDate,   // P0-07
    ];

    /// <summary>
    /// 运行侧支持的操作符白名单（执行器只有 EQ/IN/LT/LTE/GT/GTE，**无 NE**）。
    /// `NotEquals` 在发布期即拦截，不留到运行期（B3 v1.2 §3.2）。
    /// </summary>
    private static readonly HashSet<ConditionOperator> SupportedOperators =
    [
        ConditionOperator.Equals,
        ConditionOperator.LessThan,
        ConditionOperator.LessOrEqual,
        ConditionOperator.GreaterThan,
        ConditionOperator.GreaterOrEqual,
        ConditionOperator.In,
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
