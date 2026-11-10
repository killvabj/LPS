using System.Data;
using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Pegging;

/// <summary>
/// DemandSupplyHardLock 仓储实现（V1.2新增，§8）
/// </summary>
public class DemandSupplyHardLockRepository : IDemandSupplyHardLockRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<DemandSupplyHardLockRepository> _logger;

    public DemandSupplyHardLockRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<DemandSupplyHardLockRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnSupplyAsync(
        string supplyKey,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[DemandSupplyHardLock]
            WHERE [SupplyKey] = @SupplyKey
              AND [Status] = N'ACTIVE'
            ORDER BY [CreatedAt]";

        return await _connectionManager.QueryAsync<DemandSupplyHardLock>(
            sql,
            new { SupplyKey = supplyKey },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// R02-O02 T02（2026-11-10）：键集合一次性取 ACTIVE 锁（替代逐键 N+1）。
    /// 单 JSON 参数 ＋ `OPENJSON` 表值连接；空集合直接返回空，不发查询。
    /// </summary>
    public async Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnSupplyKeysAsync(
        string supplyKeysJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supplyKeysJson))
            return Array.Empty<DemandSupplyHardLock>();

        // 不加强制 JOIN 提示：锁表与单个键集都小，让优化器自选；强制 HASH 在该基数上反而不被采纳并出警告。
        // 无裸大 IN（单 JSON 参数 ＋ OPENJSON），亦不并发 — 符合 0号位 T02 的「受控技术实现」要求。
        const string sql = @"
            SELECT l.*
            FROM [dbo].[DemandSupplyHardLock] l
            INNER JOIN OPENJSON(@SupplyKeysJson) j
                    ON j.[value] = l.[SupplyKey]
            WHERE l.[Status] = N'ACTIVE'
            ORDER BY l.[CreatedAt]";

        return await _connectionManager.QueryAsync<DemandSupplyHardLock>(
            sql,
            new { SupplyKeysJson = supplyKeysJson },
            db: DatabaseId.APS);
    }

    public async Task<IEnumerable<DemandSupplyHardLock>> GetActiveLocksOnDemandAsync(
        string demandKey,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[DemandSupplyHardLock]
            WHERE [DemandKey] = @DemandKey
              AND [Status] = N'ACTIVE'
            ORDER BY [CreatedAt]";

        return await _connectionManager.QueryAsync<DemandSupplyHardLock>(
            sql,
            new { DemandKey = demandKey },
            db: DatabaseId.APS);
    }

    public async Task<IEnumerable<DemandSupplyHardLock>> GetLocksByIdsAsync(
        IEnumerable<long> lockIds,
        CancellationToken cancellationToken = default)
    {
        var ids = lockIds.ToList();
        if (ids.Count == 0) return Array.Empty<DemandSupplyHardLock>();

        const string sql = @"
            SELECT * FROM [dbo].[DemandSupplyHardLock]
            WHERE [Id] IN @LockIds";

        return await _connectionManager.QueryAsync<DemandSupplyHardLock>(
            sql,
            new { LockIds = ids },
            db: DatabaseId.APS);
    }

    public async Task<int> BulkInsertAsync(
        IEnumerable<DemandSupplyHardLock> locks,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[DemandSupplyHardLock]
                ([LockType], [DemandType], [DemandKey], [SupplyType], [SupplyKey],
                 [LockedQty], [SourcePlanVersionId], [SourceAllocationSequence],
                 [Status], [CreatedAt], [CreatedBy])
            VALUES
                (@LockType, @DemandType, @DemandKey, @SupplyType, @SupplyKey,
                 @LockedQty, @SourcePlanVersionId, @SourceAllocationSequence,
                 @Status, @CreatedAt, @CreatedBy)";

        var rows = locks.ToList();
        if (rows.Count == 0) return 0;

        return await _connectionManager.ExecuteInTransactionAsync<int>(
            async (conn, tx) =>
            {
                var affected = 0;
                foreach (var batch in rows.Chunk(1000))
                {
                    affected += await conn.ExecuteAsync(sql, batch, tx);
                }
                return affected;
            },
            DatabaseId.APS);
    }

    public async Task<int> ReleaseLocksAsync(
        IEnumerable<long> lockIds,
        string releasedBy,
        string releaseReason,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE [dbo].[DemandSupplyHardLock]
            SET [Status] = N'RELEASED',
                [ReleasedAt] = GETDATE(),
                [ReleasedBy] = @ReleasedBy,
                [ReleaseReason] = @ReleaseReason
            WHERE [Id] IN @LockIds
              AND [Status] = N'ACTIVE'";

        var ids = lockIds.ToList();
        if (ids.Count == 0) return 0;

        return await _connectionManager.ExecuteAsync(
            sql,
            new { LockIds = ids, ReleasedBy = releasedBy, ReleaseReason = releaseReason },
            db: DatabaseId.APS);
    }

    public async Task<int> MarkLocksAsBrokenAsync(
        IEnumerable<long> lockIds,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE [dbo].[DemandSupplyHardLock]
            SET [Status] = N'BROKEN',
                [ReleasedAt] = GETDATE()
            WHERE [Id] IN @LockIds
              AND [Status] = N'ACTIVE'";

        var ids = lockIds.ToList();
        if (ids.Count == 0) return 0;

        return await _connectionManager.ExecuteAsync(
            sql,
            new { LockIds = ids },
            db: DatabaseId.APS);
    }

    public async Task<IEnumerable<DemandSupplyHardLock>> GetLocksBySourcePlanVersionAsync(
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[DemandSupplyHardLock]
            WHERE [SourcePlanVersionId] = @PlanVersionId
            ORDER BY [CreatedAt]";

        return await _connectionManager.QueryAsync<DemandSupplyHardLock>(
            sql,
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);
    }
}
