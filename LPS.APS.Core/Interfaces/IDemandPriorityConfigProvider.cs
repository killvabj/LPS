using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// Demand 优先级策略配置提供器（2号位消费侧 — 3号位产出侧）
///
/// 职责边界：
/// - 3号位负责策略冻结，输出完整的 FrozenStrategySnapshot（其中包含 DemandPriorityConfig）
/// - 2号位通过本接口获取 DemandPriorityConfig 后交给 IDemandPriorityExecutor 执行
///
/// 生产实现：DemandPriorityConfigProvider（3号位 FrozenStrategySnapshot 客户端尚未接通，当前显式抛错）
/// 联调实现：DemandPriorityFixtureProvider（位于 Fixtures 命名空间，仅测试项目注册，禁止生产 Fallback）
/// 后续替换：3号位真实 FrozenStrategySnapshot 客户端接入后，替换 DemandPriorityConfigProvider 的实现体
/// </summary>
public interface IDemandPriorityConfigProvider
{
    /// <param name="frozenAtAnchor">
    /// **Run 级冻结锚点**（FULL-08，2026-11-09）。相对日阈值（如 `CustomerDueDate ≤ 冻结日+20`）
    /// 用它换算成具体日期；传 null 则退回 `FrozenStrategySnapshot.FrozenAt`。
    /// 为什么要这个参数：`FrozenAt` 在缓存命中路径每次 `DateTime.Now` 重置（快照装载时刻），
    /// 跨午夜的同一 Run 会让不同域/不同轮拿到不同日期。调用方（Pegging）应传本 Run 的
    /// `DataCutoffTime` —— 一次 Run 一个值，与域执行顺序、缓存命中次数无关。
    /// </param>
    Task<DemandPriorityConfig> GetPriorityConfigAsync(
        long strategyProfileVersionId,
        DateTime? frozenAtAnchor = null,
        CancellationToken cancellationToken = default);
}
