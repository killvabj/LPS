namespace LPS.APS.Core.Dto;

/// <summary>
/// 需求优先级配置（3号位职责输出 — FrozenStrategySnapshot的一部分）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出FrozenStrategySnapshot
/// - 2号位负责执行器实现，消费DemandPriorityConfig
///
/// 执行算法：
/// 1. 按CompetitionRound分轮（轮次≠BOM计算层级，见 PrioritySegmentConfig.CompetitionRound）
/// 2. 每轮内按SegmentOrder升序遍历Segment
/// 3. 每个Demand从第一个Segment开始匹配
/// 4. 命中第一条后停止，不再进入其它Segment（First Match）
/// 5. 每个Segment内部按SortFields依次排序
/// 6. 最后StableTieBreak确保确定性
/// </summary>
public sealed class DemandPriorityConfig
{
    /// <summary>
    /// 优先级段列表（已按CompetitionRound和SegmentOrder排序）
    /// </summary>
    public IReadOnlyList<PrioritySegmentConfig> Segments { get; init; } = [];
}
