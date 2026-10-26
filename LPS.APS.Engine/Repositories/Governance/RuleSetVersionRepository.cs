using System.Data;
using Dapper;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 规则集版本仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.RuleSetVersion
/// </summary>
public class RuleSetVersionRepository : IRuleSetVersionRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<RuleSetVersionRepository> _logger;

    public RuleSetVersionRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<RuleSetVersionRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<RuleSetVersion?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[RuleSetVersion]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<RuleSetVersion>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<RuleSetVersion>> GetByRuleSetIdAsync(long ruleSetId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[RuleSetVersion]
            WHERE [RuleSetId] = @RuleSetId
            ORDER BY [VersionCode]";

        var results = await _connectionManager.QueryAsync<RuleSetVersion>(
            sql, new { RuleSetId = ruleSetId }, db: DatabaseId.APS);

        return results.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RuleSetVersionSummaryDto>> GetSummariesByRuleSetIdAsync(long ruleSetId, CancellationToken ct = default)
    {
        // 2026-10-15 4号位 来文 Q2（同款缺陷·潜伏）：与参数集列表同源 SELECT * 整列搬大 LOB。
        // 显式列仅取摘要字段（冻结 DDL v5.1.8.3 已确认列；ContentSnapshotJson/DemandPriorityJson/Remarks 不取——同上理）。
        const string sql = @"
            SELECT [Id], [RuleSetId], [VersionCode], [Status],
                   [EffectiveFrom], [EffectiveTo], [PublishedAt], [PublishedBy],
                   [ApprovedAt], [ApprovedBy], [CreatedAt], [CreatedBy]
            FROM [dbo].[RuleSetVersion]
            WHERE [RuleSetId] = @RuleSetId
            ORDER BY [VersionCode]";

        var results = await _connectionManager.QueryAsync<RuleSetVersionSummaryDto>(
            sql, new { RuleSetId = ruleSetId }, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<RuleSetVersion> AddAsync(RuleSetVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO [dbo].[RuleSetVersion]
                ([RuleSetId], [VersionCode], [Status], [IsDefault], [ContentSnapshotJson],
                 [EffectiveFrom], [EffectiveTo],
                 [PublishedAt], [PublishedBy], [ApprovedAt], [ApprovedBy],
                 [CreatedAt], [CreatedBy])
            VALUES
                (@RuleSetId, @VersionCode, @Status, @IsDefault, @ContentSnapshotJson,
                 @EffectiveFrom, @EffectiveTo,
                 @PublishedAt, @PublishedBy, @ApprovedAt, @ApprovedBy,
                 @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, version, db: DatabaseId.APS);

        version.Id = id;
        return version;
    }

    public async Task UpdateAsync(RuleSetVersion version, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE [dbo].[RuleSetVersion]
            SET [VersionCode] = @VersionCode,
                [Status] = @Status,
                [IsDefault] = @IsDefault,
                [ContentSnapshotJson] = @ContentSnapshotJson,
                [EffectiveFrom] = @EffectiveFrom,
                [EffectiveTo] = @EffectiveTo,
                [PublishedAt] = @PublishedAt,
                [PublishedBy] = @PublishedBy,
                [ApprovedAt] = @ApprovedAt,
                [ApprovedBy] = @ApprovedBy
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, version, db: DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task SetDefaultAsync(long ruleSetId, long versionId, CancellationToken ct = default)
    {
        // D-3 原子默认切换（2026-10-17）：单事务内「清同族其它 IsDefault=1」+「置目标 IsDefault=1」，
        // 修正 SPV 现 ClearDefaultFlagAsync + UpdateAsync 两条独立 SQL 的非原子缺陷（崩溃/并发下可能同族双默认）。
        // 事务内直接用传入连接与事务执行，不在事务中再经 GetConnectionAsync 取连接（DatabaseConnectionManager 死锁警告）。
        await _connectionManager.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                const string clearSql = @"
                    UPDATE [dbo].[RuleSetVersion]
                    SET [IsDefault] = 0
                    WHERE [RuleSetId] = @RuleSetId
                      AND [Id] <> @VersionId
                      AND [IsDefault] = 1";
                const string setSql = @"
                    UPDATE [dbo].[RuleSetVersion]
                    SET [IsDefault] = 1
                    WHERE [Id] = @VersionId";

                await connection.ExecuteAsync(clearSql, new { RuleSetId = ruleSetId, VersionId = versionId }, transaction);
                await connection.ExecuteAsync(setSql, new { VersionId = versionId }, transaction);
                return 0;
            },
            DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task DeleteByIdAsync(long id, CancellationToken ct = default)
    {
        // R1 2026-10-25：物理删单版本（仅 DRAFT 草稿壳）。服务层已保证 Status=DRAFT + 未被策略包引用。
        const string sql = @"DELETE FROM [dbo].[RuleSetVersion] WHERE [Id] = @Id";
        await _connectionManager.ExecuteAsync(sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<RuleSetVersion>> GetDefaultCandidatesAsync(long ruleSetId, CancellationToken ct = default)
    {
        // D-3 读侧默认解析（2026-10-17）：同族 IsDefault=1 且 PUBLISHED 候选全集（不 TOP 1），
        // Application 层结合生效窗过滤后判定 0/1/多（多→歧义）。显式列排除大 LOB 避免搬运。
        const string sql = @"
            SELECT [Id], [RuleSetId], [VersionCode], [Status], [IsDefault],
                   [EffectiveFrom], [EffectiveTo], [PublishedAt], [PublishedBy],
                   [ApprovedAt], [ApprovedBy], [CreatedAt], [CreatedBy]
            FROM [dbo].[RuleSetVersion]
            WHERE [RuleSetId] = @RuleSetId
              AND [IsDefault] = 1
              AND [Status] = 'PUBLISHED'";

        var results = await _connectionManager.QueryAsync<RuleSetVersion>(
            sql, new { RuleSetId = ruleSetId }, db: DatabaseId.APS);

        return results.ToList();
    }
}
