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
    /// 匹配条件列表（AND关系，全部满足才进入该段）。
    /// **仅在 <see cref="MatchConditionGroups"/> 为空时生效**（存量平铺语义）。
    /// </summary>
    public IReadOnlyList<MatchCondition> MatchConditions { get; init; } = [];

    /// <summary>
    /// 命中条件组（**路线甲 DNF：组内 AND、组间 OR**；3号位 实施包 v1.0 §一.4）。
    /// 非空即以本组为准、**忽略** <see cref="MatchConditions"/>；为空则回退平铺列表（存量语义不变）。
    /// <para>例「1 并 2 并 (3 或 4)」＝组A{1,2,3}、组B{1,2,4}：任一 AND 组全成立即命中。</para>
    /// <para>空组（<c>Conditions.Count == 0</c>）**不成立**——否则空组会退化成「无条件命中」把后续段吞掉；
    /// 发布校验亦拒绝空组。</para>
    /// </summary>
    public IReadOnlyList<PriorityConditionGroup> MatchConditionGroups { get; init; } = [];

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

/// <summary>
/// 一条 AND 条件组（DNF 的「组」）：组内条件全部满足才算本组成立，组间取 OR。
/// 与冻结快照的 <c>SegmentConditionGroup</c> 同构，投影层只是把强类型枚举换成执行器字符串词表。
/// </summary>
public sealed class PriorityConditionGroup
{
    /// <summary>组内条件（AND 语义）</summary>
    public IReadOnlyList<MatchCondition> Conditions { get; init; } = [];
}
