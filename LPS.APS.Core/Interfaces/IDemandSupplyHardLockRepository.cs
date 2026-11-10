using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// DemandSupplyHardLock 仓储接口（V1.2新增，§8）
/// 负责 STRICT_BINDING 和 DEMAND_PROTECTION 锁的持久化操作
/// </summary>
public interface IDemandSupplyHardLockRepository
{
    /// <summary>
    /// 查询供给上的活跃 Lock（根据 SupplyKey）
    /// </summary>
    Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnSupplyAsync(
        string supplyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// **R02-O02 T02（2026-11-10）**：按**键集合**一次性取活跃锁。
    ///
    /// 存在理由：原实现按「每个供给键一次查询」逐键取锁，供给上万条时就是上万次往返（0号位 点名的 N+1）。
    /// 本方法只传**一个 JSON 参数**、SQL 侧 `OPENJSON` 表值连接，单批键数由调用方分块控制，
    /// 既不裸拼大 `IN`、也不并发轰库。返回行仍是各键上的 ACTIVE 锁，**结果集与逐键查询等价**。
    /// </summary>
    /// <param name="supplyKeysJson">`string[]` 的 JSON 序列化文本（调用方分块，单块建议 ≤20,000 个键）。</param>
    Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnSupplyKeysAsync(
        string supplyKeysJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询需求上的活跃 Lock（根据 DemandKey）
    /// </summary>
    Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnDemandAsync(
        string demandKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Id 列表查询 Lock 记录（不区分状态，供释放前校验）
    /// </summary>
    Task<IEnumerable<DemandSupplyHardLock>> GetLocksByIdsAsync(
        IEnumerable<long> lockIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量插入 Lock 记录
    /// </summary>
    Task<int> BulkInsertAsync(
        IEnumerable<DemandSupplyHardLock> locks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放 Lock（将状态更新为 RELEASED）
    /// </summary>
    Task<int> ReleaseLocksAsync(
        IEnumerable<long> lockIds,
        string releasedBy,
        string releaseReason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记 Lock 为 BROKEN（当发现供需不一致时）
    /// </summary>
    Task<int> MarkLocksAsBrokenAsync(
        IEnumerable<long> lockIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据来源 PlanVersion 查询 Lock
    /// </summary>
    Task<IEnumerable<DemandSupplyHardLock>> GetLocksBySourcePlanVersionAsync(
        int planVersionId,
        CancellationToken cancellationToken = default);
}
