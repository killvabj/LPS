namespace LPS.APS.Core.Dto;

/// <summary>
/// 优先级段配置（Demand排序的一个匹配段）
///
/// 执行语义：
/// 1. 每个Demand按SegmentOrder从小到大遍历所有Segment
/// 2. 命中第一个满足MatchConditions的Segment后停止（First Match）
/// 3. 在该Segment内按SortFields依次排序
/// 4. 最后用StableTieBreakFields确保确定性
///
/// 冻结约束：
/// - 不支持任意C#表达式、SQL、动态脚本
/// - 只支持白名单业务字段 + 简单比较符（EQ/IN/LT/LTE/GT/GTE）
/// </summary>
public sealed class PrioritySegmentConfig
{
    /// <summary>
    /// 竞争轮次（越小越先竞争；同一轮内按 <see cref="SegmentOrder"/> 匹配）。
    ///
    /// **轮次 ≠ BOM 计算层级**：冻结 `01 §22` 的三层是「业务计算层级」（顶层独立需求／SH 源厂履约／
    /// PI-BOM 下阶扫尾），与 BOM 深度无关；0号位 Q1 明令不得用旧名冒名替代。
    /// 值域由 3号位 B3 定；2号位 默认三段 = 1 顶层独立需求／2 SH 源厂履约／3 孤儿与下阶。
    ///
    /// 轮次**只影响 `DemandSequence` 的赋值先后**，不新增排序键 —— `RunBfsLoop` 仍只按
    /// `DemandSequence` 升序处理，1号位 零改动。供给池是一个共用池，按轮次顺序消耗。
    /// </summary>
    public int CompetitionRound { get; init; }

    /// <summary>
    /// 段顺序（同 <see cref="CompetitionRound"/> 内的执行顺序，越小越先匹配）
    /// </summary>
    public int SegmentOrder { get; init; }

    /// <summary>
    /// 是否启用该段
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// 匹配条件列表（AND关系，全部满足才进入该段）
    /// </summary>
    public IReadOnlyList<MatchCondition> MatchConditions { get; init; } = [];

    /// <summary>
    /// 排序字段列表（依次排序，第一个字段优先级最高）
    /// </summary>
    public IReadOnlyList<SortField> SortFields { get; init; } = [];

    /// <summary>
    /// 稳定性Tie-break字段（当SortFields完全相同时，按这些字段确保确定性）
    /// 最终兜底：DemandKey ASC
    /// </summary>
    public IReadOnlyList<string> StableTieBreakFields { get; init; } = [];
}
