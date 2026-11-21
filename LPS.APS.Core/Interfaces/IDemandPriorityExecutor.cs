using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 需求优先级执行器（2号位职责 — 消费3号位的 DemandPriorityConfig）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出 FrozenStrategySnapshot.DemandPriority（DemandPriorityConfig）
/// - 2号位负责执行器实现：按策略对 UpstreamDemand 排序，并回填 DemandSequence
///
/// 执行算法（PM 冻结口径，方案A：外部按层调用）：
/// 1. PeggingOrchestrator 逐层形成「当前层 Demand 集合」
/// 2. PeggingOrchestrator 从 Frozen DemandPriority 取「当前层 Segments」后调用本执行器
/// 3. 本执行器只排序单个计算层：按 SegmentOrder 升序遍历 Segment
/// 4. 每个 Demand 从第一个 Segment 开始匹配，命中第一条后停止（First Match）
/// 5. 每个 Segment 内部按 SortFields 依次排序
/// 6. StableTieBreak 确保确定性（最终兜底：DemandKey ASC）
/// </summary>
public interface IDemandPriorityExecutor
{
    /// <summary>
    /// 探针专用（2026-11-21）：调用方置为非 null 时，执行器把**每条需求命中的段号**记进来
    /// （段号 = `SegmentOrder`；**0 = 未命中任何段、走 DemandKey 兜底**）。
    /// 默认 null ⇒ 生产零行为变化、零内存开销（仅多一次 null 判断）。
    /// 用途＝「排序规则真实执行证据」：可逐段核对命中与段内顺序，不必事后猜段归属。
    /// </summary>
    List<(string DemandKey, int SegmentOrder)>? SegmentTrace { get; set; }

    /// <summary>
    /// 执行「单个计算层」的 Demand 排序（config 已由调用方收敛为当前层 Segments），
    /// 返回有序列表并已赋值 DemandSequence = 1, 2, 3...
    /// S5（PM 0923）：expediteOrderCanonicalIds 非空时，命中的 Demand 作为 EXPEDITE 前置竞争层整体前置。
    /// </summary>
    List<UpstreamDemand> ExecutePrioritySort(
        IEnumerable<UpstreamDemand> demands,
        DemandPriorityConfig config,
        IReadOnlySet<long>? expediteOrderCanonicalIds = null);
}
