using System.Globalization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 需求优先级执行器（2号位职责 — 消费3号位的DemandPriorityConfig）
///
/// 执行算法（PM冻结口径，方案A：外部按层调用）：
/// 1. PeggingOrchestrator 逐层形成「当前层 Demand 集合」
/// 2. PeggingOrchestrator 从 Frozen DemandPriority 取「当前层 Segments」后调用本执行器
/// 3. 本执行器只排序单个计算层：按 SegmentOrder 升序遍历 Segment
/// 4. 每个 Demand 从第一个 Segment 开始匹配
/// 5. 命中第一条后停止，不再进入其它 Segment（First Match）
/// 6. 每个 Segment 内部按 SortFields 依次排序
/// 7. 最后 StableTieBreak 确保确定性（最终兜底：DemandKey ASC）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出 FrozenStrategySnapshot.DemandPriority
/// - 2号位负责执行器实现，消费策略并生成 DemandSequence
/// - 本执行器只排序「单个计算层」的 Demand；分层编排由 PeggingOrchestrator 负责
/// </summary>
public sealed class DemandPriorityExecutor : IDemandPriorityExecutor
{
    /// <summary>
    /// 当前执行器支持的业务字段白名单（大小写不敏感）。
    /// 3号位配置中出现白名单外的 FieldName 属于策略配置错误（P1-02），必须显式报错，不得静默当作空值继续排。
    /// </summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORDERTYPE", "DELAYSTATUS", "CUSTOMERTIER", "DUEDATE",
        "ISSUEDATE", "PROTECTIONSTATUS", "DEMANDKEY",
        // 2026-11-06 V1.1 新增（3号位 实施包 v1.0 §四.1）。**只接订单源确有值的 7 个**：
        //   `STOCKCONSUMEDAY`（库存消耗日）/`NONSTOCKSHIPCATEGORY`（非在库出荷区分）是页面维护字段、
        //   当前无存储载体 ⇒ 未接入。接了只会拿到 null，正是实施包 §五 点名的「配置了也空转」。
        "CUSTOMERSEGMENT", "CUSTOMERDUEDATE", "TRANSPORTMODE", "SALESORDERCATEGORY",
        "DEMANDMATURITYSTATUS", "NONSTOCKSHIPCATEGORY", "MATERIALCODE", "QUANTITY"
    };

    private readonly ILogger<DemandPriorityExecutor> _logger;

    public DemandPriorityExecutor(ILogger<DemandPriorityExecutor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 执行「单个计算层」的 Demand 排序（config 已由 PeggingOrchestrator 收敛为当前层 Segments）。
    ///
    /// 返回：有序的 Demand 列表，已赋值 DemandSequence = 1, 2, 3...
    /// </summary>
    public List<UpstreamDemand> ExecutePrioritySort(
        IEnumerable<UpstreamDemand> demands,
        DemandPriorityConfig config,
        IReadOnlySet<long>? expediteOrderCanonicalIds = null)
    {
        var demandList = demands.ToList();
        if (demandList.Count == 0)
        {
            return demandList;
        }

        // S5（PM 0923）：EXPEDITE 前置竞争层 —— 命中 OrderTargets.OrderCanonicalId 的需求作为独立竞争层
        // 整体前置（「可调整 Demand 排序前置竞争层」，非恒先、非新增 Task 优先级）；
        // 层内仍按 3号位冻结 DemandPriorityConfig 正常竞争排序，不突破任何字段/规则。
        if (expediteOrderCanonicalIds is { Count: > 0 })
        {
            static bool IsExpedite(UpstreamDemand d, IReadOnlySet<long> keys)
                => d.OrderCanonicalId.HasValue && keys.Contains(d.OrderCanonicalId.Value);

            if (demandList.Any(d => IsExpedite(d, expediteOrderCanonicalIds)))
            {
                var expediteBucket = demandList.Where(d => IsExpedite(d, expediteOrderCanonicalIds)).ToList();
                var normalBucket   = demandList.Where(d => !IsExpedite(d, expediteOrderCanonicalIds)).ToList();

                var combined = new List<UpstreamDemand>(demandList.Count);
                combined.AddRange(SortSingleLayer(expediteBucket, config));
                combined.AddRange(SortSingleLayer(normalBucket, config));
                StampDemandSequence(combined);
                return combined;
            }
        }

        var sorted = SortSingleLayer(demandList, config);
        StampDemandSequence(sorted);
        return sorted;
    }

    /// <summary>
    /// 单个竞争层的 Demand 排序（不打 DemandSequence，序号由外层统一赋值）。
    /// </summary>
    private List<UpstreamDemand> SortSingleLayer(List<UpstreamDemand> demandList, DemandPriorityConfig config)
    {
        var segments = config.Segments
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.SegmentOrder)
            .ToList();

        if (segments.Count == 0)
        {
            // P1-01：当前层无启用 Segment 时，不按调用方原始集合顺序，改用稳定 DemandKey ASC 兜底（确定性）
            _logger.LogWarning(
                "DemandPriority 当前层无启用 Segment，按稳定 DemandKey ASC 兜底（共 {Count} 条）",
                demandList.Count);
            return demandList.OrderBy(d => d.DemandKey, StringComparer.Ordinal).ToList();
        }

        // P1-02：未知 FieldName 显式报错，不允许静默吞掉3号位拼写错误
        ValidateConfigFields(segments);

        var sortedDemands = new List<UpstreamDemand>(demandList.Count);
        var assigned = new HashSet<UpstreamDemand>();

        foreach (var segment in segments)
        {
            var matched = new List<UpstreamDemand>();
            foreach (var demand in demandList)
            {
                if (!assigned.Contains(demand) && IsMatchSegment(demand, segment))
                {
                    matched.Add(demand);
                }
            }

            if (matched.Count == 0)
            {
                continue;
            }

            foreach (var demand in SortWithinSegment(matched, segment))
            {
                sortedDemands.Add(demand);
                assigned.Add(demand);
            }
        }

        // 未命中任何 Segment 的 Demand：稳定 DemandKey ASC 兜底（确定性）
        var unmatched = demandList
            .Where(d => !assigned.Contains(d))
            .OrderBy(d => d.DemandKey, StringComparer.Ordinal)
            .ToList();

        if (unmatched.Count > 0)
        {
            _logger.LogWarning(
                "DemandPriority 当前层 {Count} 条 Demand 未命中任何 Segment，按稳定 DemandKey ASC 兜底",
                unmatched.Count);
            sortedDemands.AddRange(unmatched);
        }

        return sortedDemands;
    }

    private static void StampDemandSequence(List<UpstreamDemand> sorted)
    {
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DemandSequence = i + 1;
        }
    }

    private void ValidateConfigFields(IReadOnlyList<PrioritySegmentConfig> segments)
    {
        foreach (var segment in segments)
        {
            foreach (var condition in segment.MatchConditions)
            {
                EnsureKnownField(condition.FieldName);
            }

            foreach (var sortField in segment.SortFields)
            {
                EnsureKnownField(sortField.FieldName);
            }

            foreach (var tieBreakField in segment.StableTieBreakFields)
            {
                EnsureKnownField(tieBreakField);
            }
        }
    }

    private void EnsureKnownField(string fieldName)
    {
        if (!KnownFields.Contains(fieldName))
        {
            throw new InvalidOperationException(
                $"DemandPriority 配置错误：未知字段名 '{fieldName}'。允许的字段：{string.Join(", ", KnownFields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private bool IsMatchSegment(UpstreamDemand demand, PrioritySegmentConfig segment)
    {
        if (segment.MatchConditions.Count == 0)
        {
            return true;
        }

        foreach (var condition in segment.MatchConditions)
        {
            if (!EvaluateCondition(demand, condition))
            {
                return false;
            }
        }

        return true;
    }

    private bool EvaluateCondition(UpstreamDemand demand, MatchCondition condition)
    {
        var fieldValue = GetFieldValue(demand, condition.FieldName);

        return condition.Operator.ToUpperInvariant() switch
        {
            // OrderType 的 SO/WO 旧词等价归一（3号位 实施包 §二B），两侧同时施加
            "EQ" => string.Equals(NormalizeFieldValue(condition.FieldName, fieldValue),
                                  NormalizeFieldValue(condition.FieldName, condition.Value),
                                  StringComparison.OrdinalIgnoreCase),
            "IN" => condition.Value.Split(',').Any(v => string.Equals(
                                  NormalizeFieldValue(condition.FieldName, v.Trim()),
                                  NormalizeFieldValue(condition.FieldName, fieldValue),
                                  StringComparison.OrdinalIgnoreCase)),
            "LT" => CompareNumericOrDate(fieldValue, condition.Value) < 0,
            "LTE" => CompareNumericOrDate(fieldValue, condition.Value) <= 0,
            "GT" => CompareNumericOrDate(fieldValue, condition.Value) > 0,
            "GTE" => CompareNumericOrDate(fieldValue, condition.Value) >= 0,
            // 2026-11-06 实施包 v1.0 §二.2「包含」算子；**语义已由 3号位 裁定为「前缀匹配」**
            //   （其回执 v1.0 §二-1：`FINAL*` 只匹配以 `FINAL` 开头的值，要求改 StartsWith）。
            //   实现：忽略大小写；比较前剥结尾 `*`（`*` 只是书写习惯，`FINAL*` 与 `FINAL` 等价）。
            //   ⚠ 首版按字面实现成子串 `Contains`，于同日按其裁决策改为 `StartsWith`。
            "CONTAINS" => (fieldValue ?? string.Empty)
                .StartsWith((condition.Value ?? string.Empty).TrimEnd('*'), StringComparison.OrdinalIgnoreCase),
            _ => throw new NotSupportedException($"Unsupported operator: {condition.Operator}")
        };
    }

    private List<UpstreamDemand> SortWithinSegment(
        List<UpstreamDemand> demands,
        PrioritySegmentConfig segment)
    {
        var query = demands.AsEnumerable();

        // 依次按 SortFields 排序
        if (segment.SortFields.Count > 0)
        {
            IOrderedEnumerable<UpstreamDemand>? orderedQuery = null;

            foreach (var sortField in segment.SortFields)
            {
                var isAscending = string.Equals(sortField.Direction, "ASC", StringComparison.OrdinalIgnoreCase);

                if (orderedQuery == null)
                {
                    orderedQuery = isAscending
                        ? query.OrderBy(d => GetComparableValue(d, sortField.FieldName))
                        : query.OrderByDescending(d => GetComparableValue(d, sortField.FieldName));
                }
                else
                {
                    orderedQuery = isAscending
                        ? orderedQuery.ThenBy(d => GetComparableValue(d, sortField.FieldName))
                        : orderedQuery.ThenByDescending(d => GetComparableValue(d, sortField.FieldName));
                }
            }

            query = orderedQuery ?? query;
        }

        // StableTieBreak
        if (segment.StableTieBreakFields.Count > 0)
        {
            var orderedQuery = query as IOrderedEnumerable<UpstreamDemand>;

            foreach (var tieBreakField in segment.StableTieBreakFields)
            {
                orderedQuery = orderedQuery == null
                    ? query.OrderBy(d => GetComparableValue(d, tieBreakField))
                    : orderedQuery.ThenBy(d => GetComparableValue(d, tieBreakField));
            }

            query = orderedQuery ?? query;
        }

        // 最终兜底：DemandKey ASC（稳定、文化无关）
        var finalOrdered = (query as IOrderedEnumerable<UpstreamDemand>)?.ThenBy(d => d.DemandKey, StringComparer.Ordinal)
                           ?? query.OrderBy(d => d.DemandKey, StringComparer.Ordinal);

        return finalOrdered.ToList();
    }

    /// <summary>
    /// 字段取值别名归一（2026-11-06，3号位 实施包 v1.0 §二B）。
    ///
    /// `OrderType` 的存量快照写的是旧词 `SO` / `WO`，而库内实际值是
    /// `SALES_ORDER` / `PRODUCTION_INSTRUCTION`。3号位 在发布校验侧把四值并列为合法
    /// （「SO=存量旧词等价 SALES_ORDER、WO 等价 PRODUCTION_INSTRUCTION，防空转不牺牲存量快照」），
    /// 因此**运行侧必须做等价归一**，否则 EQ 严格比较下 `SO` 永远不命中 —— 配置了也空转。
    /// 归一在**两侧同时**施加（字段实际值与配置值），保证对称；其它字段原样返回。
    /// </summary>
    private static string NormalizeFieldValue(string fieldName, string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (!string.Equals(fieldName, "ORDERTYPE", StringComparison.OrdinalIgnoreCase)) return value;

        return value.Trim().ToUpperInvariant() switch
        {
            "SO" => "SALES_ORDER",
            "WO" => "PRODUCTION_INSTRUCTION",
            _ => value
        };
    }

    private string GetFieldValue(UpstreamDemand demand, string fieldName)
    {
        return fieldName.ToUpperInvariant() switch
        {
            "ORDERTYPE" => demand.OrderType ?? string.Empty,
            "DELAYSTATUS" => demand.DelayStatus ?? string.Empty,
            "CUSTOMERTIER" => demand.CustomerTier ?? string.Empty,
            "DUEDATE" => demand.DueDate?.ToString("O") ?? string.Empty,
            "ISSUEDATE" => demand.IssueDate?.ToString("O") ?? string.Empty,
            "PROTECTIONSTATUS" => demand.ProtectionStatus ?? string.Empty,
            // 2026-11-06 V1.1 新增（3号位 实施包 v1.0 §四.1）
            "CUSTOMERSEGMENT"      => demand.CustomerSegment ?? string.Empty,
            "CUSTOMERDUEDATE"      => demand.CustomerDueDate?.ToString("O") ?? string.Empty,
            "TRANSPORTMODE"        => demand.TransportMode ?? string.Empty,
            "SALESORDERCATEGORY"   => demand.SalesOrderCategory ?? string.Empty,
            "DEMANDMATURITYSTATUS" => demand.DemandMaturityStatus ?? string.Empty,
            "NONSTOCKSHIPCATEGORY" => demand.NonStockShipmentType ?? string.Empty,
            "MATERIALCODE"         => demand.MaterialCode ?? string.Empty,
            "QUANTITY"             => demand.Quantity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "DEMANDKEY" => demand.DemandKey,
            _ => throw UnknownFieldError(fieldName)
        };
    }

    /// <summary>
    /// 段内排序取值：日期/数值必须给**强类型**，否则字符串序会把 "10" 排在 "9" 之前。
    /// </summary>
    private IComparable GetComparableValue(UpstreamDemand demand, string fieldName)
    {
        return fieldName.ToUpperInvariant() switch
        {
            "DUEDATE" => demand.DueDate ?? DateTime.MaxValue,
            "ISSUEDATE" => demand.IssueDate ?? DateTime.MaxValue,
            "DEMANDKEY" => demand.DemandKey,
            "ORDERTYPE" => demand.OrderType ?? string.Empty,
            "DELAYSTATUS" => demand.DelayStatus ?? string.Empty,
            "CUSTOMERTIER" => demand.CustomerTier ?? string.Empty,
            "PROTECTIONSTATUS" => demand.ProtectionStatus ?? string.Empty,
            // 2026-11-06 V1.1 新增（3号位 实施包 v1.0 §四.1）
            "CUSTOMERDUEDATE"      => demand.CustomerDueDate ?? DateTime.MaxValue,
            "QUANTITY"             => demand.Quantity ?? decimal.MaxValue,
            "CUSTOMERSEGMENT"      => demand.CustomerSegment ?? string.Empty,
            "TRANSPORTMODE"        => demand.TransportMode ?? string.Empty,
            "SALESORDERCATEGORY"   => demand.SalesOrderCategory ?? string.Empty,
            "DEMANDMATURITYSTATUS" => demand.DemandMaturityStatus ?? string.Empty,
            "NONSTOCKSHIPCATEGORY" => demand.NonStockShipmentType ?? string.Empty,
            "MATERIALCODE"         => demand.MaterialCode ?? string.Empty,
            _ => throw UnknownFieldError(fieldName)
        };
    }

        private static InvalidOperationException UnknownFieldError(string fieldName)
        => new($"DemandPriority 配置错误：未知字段名 '{fieldName}'");

    private int CompareNumericOrDate(string value1, string value2)
    {
        if (DateTime.TryParse(value1, out var date1) && DateTime.TryParse(value2, out var date2))
        {
            return date1.CompareTo(date2);
        }

        if (decimal.TryParse(value1, out var num1) && decimal.TryParse(value2, out var num2))
        {
            return num1.CompareTo(num2);
        }

        return string.Compare(value1, value2, StringComparison.OrdinalIgnoreCase);
    }
}
