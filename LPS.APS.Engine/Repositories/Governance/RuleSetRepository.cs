using System.Text;
using Dapper;
using LPS.APS.Core.DTOs.Governance;
using RuleSet = LPS.APS.Core.Entities.APS.RuleSet;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 规则集主表仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.RuleSet
/// 3-4联调接口 G1（A1）：规则集列表查询。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class RuleSetRepository : IRuleSetRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<RuleSetRepository> _logger;

    public RuleSetRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<RuleSetRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RuleSet>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        var sql = new StringBuilder(@"
            SELECT * FROM [dbo].[RuleSet]
            WHERE 1 = 1");

        var parameters = new DynamicParameters();
        if (activeOnly.HasValue)
        {
            sql.Append(" AND [IsActive] = @IsActive");
            parameters.Add("IsActive", activeOnly.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND ([RuleSetCode] LIKE @Keyword OR [RuleSetName] LIKE @Keyword)");
            parameters.Add("Keyword", $"%{keyword.Trim()}%");
        }

        sql.Append(" ORDER BY [Id] DESC");

        if (skip.HasValue)
        {
            sql.Append(" OFFSET @Skip ROWS");
            parameters.Add("Skip", skip.Value);
        }

        if (take.HasValue)
        {
            sql.Append(" FETCH NEXT @Take ROWS ONLY");
            parameters.Add("Take", take.Value);
        }

        var results = await _connectionManager.QueryAsync<RuleSet>(
            sql.ToString(), parameters, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<IReadOnlyList<RuleSetListItemDto>> GetListWithVersionAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        // R2（催办单 2026-09-22）：列表主表 LEFT JOIN 最新版本摘要。
        // 版本摘要语义：
        //  - CurrentVersionId / CurrentVersionCode = 最新 PUBLISHED 版本 Id + 版本码（C-4 身份补全）
        //  - DraftVersionId   / DraftVersionCode   = 最新 DRAFT 版本 Id + 版本码（C-4 身份补全）
        //  - Status / LastChangeReason / PublishedAt / RetiredAt(EffectiveTo) = 最新版本
        // DomainKey 无真源（线上无规则集↔域关联），未纳入（已提报 0号位 裁决）。
        var sql = new StringBuilder(@"
            SELECT r.[Id], r.[RuleSetCode], r.[RuleSetName], r.[Description], r.[IsActive],
                   r.[CreatedAt], r.[CreatedBy], r.[UpdatedAt], r.[UpdatedBy],
                   v.CurrentVersionId, v.CurrentVersionCode,
                   v.DraftVersionId, v.DraftVersionCode,
                   v.Status, v.LastChangeReason,
                   v.PublishedAt, v.RetiredAt
            FROM [dbo].[RuleSet] r
            OUTER APPLY (
                SELECT TOP 1
                       (SELECT TOP 1 [Id] FROM [dbo].[RuleSetVersion]
                         WHERE [RuleSetId] = r.[Id] AND [Status] = 'PUBLISHED'
                         ORDER BY [Id] DESC) AS CurrentVersionId,
                       (SELECT TOP 1 [VersionCode] FROM [dbo].[RuleSetVersion]
                         WHERE [RuleSetId] = r.[Id] AND [Status] = 'PUBLISHED'
                         ORDER BY [Id] DESC) AS CurrentVersionCode,
                       (SELECT TOP 1 [Id] FROM [dbo].[RuleSetVersion]
                         WHERE [RuleSetId] = r.[Id] AND [Status] = 'DRAFT'
                         ORDER BY [Id] DESC) AS DraftVersionId,
                       (SELECT TOP 1 [VersionCode] FROM [dbo].[RuleSetVersion]
                         WHERE [RuleSetId] = r.[Id] AND [Status] = 'DRAFT'
                         ORDER BY [Id] DESC) AS DraftVersionCode,
                       [Status], [Remarks] AS LastChangeReason, [PublishedAt],
                       [EffectiveTo] AS RetiredAt
                FROM [dbo].[RuleSetVersion]
                WHERE [RuleSetId] = r.[Id]
                ORDER BY [Id] DESC
            ) v
            WHERE 1 = 1");

        var parameters = new DynamicParameters();
        if (activeOnly.HasValue)
        {
            sql.Append(" AND r.[IsActive] = @IsActive");
            parameters.Add("IsActive", activeOnly.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND (r.[RuleSetCode] LIKE @Keyword OR r.[RuleSetName] LIKE @Keyword)");
            parameters.Add("Keyword", $"%{keyword.Trim()}%");
        }

        sql.Append(" ORDER BY r.[Id] DESC");

        if (skip.HasValue)
        {
            sql.Append(" OFFSET @Skip ROWS");
            parameters.Add("Skip", skip.Value);
        }

        if (take.HasValue)
        {
            sql.Append(" FETCH NEXT @Take ROWS ONLY");
            parameters.Add("Take", take.Value);
        }

        var results = await _connectionManager.QueryAsync<RuleSetListItemDto>(
            sql.ToString(), parameters, db: DatabaseId.APS);

        return results.ToList();
    }

    // ==================== 主表写路径（2026-10-22：补齐 4号位 缺口，3号位 治理） ====================

    public async Task<RuleSet?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM [dbo].[RuleSet] WHERE [Id] = @Id";
        return await _connectionManager.QueryFirstOrDefaultAsync<RuleSet>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<RuleSet> CreateAsync(RuleSet entity, CancellationToken ct = default)
    {
        // Q2 两步语义：主表仅建壳，内容（第一版）由前端建主表后自动跟建空白 DRAFT 版本承载；
        // RuleSetCode 创建时唯一（服务层预检），入库由 UNIQUE 约束兜底。
        const string sql = @"
            INSERT INTO [dbo].[RuleSet]
                ([RuleSetCode], [RuleSetName], [Description], [IsActive],
                 [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt])
            VALUES
                (@RuleSetCode, @RuleSetName, @Description, @IsActive,
                 @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, entity, db: DatabaseId.APS);

        entity.Id = id;
        return entity;
    }

    public async Task UpdateAsync(RuleSet entity, CancellationToken ct = default)
    {
        // Q1：RuleSetCode 一经创建不可变更（对齐无重命名语义）——UPDATE 仅改 Name/Description/IsActive + 审计时间戳。
        // Q4：IsActive 仅关主表，不触碰 RuleSetVersion；级联失效语义不引入（版本状态不受影响）。
        const string sql = @"
            UPDATE [dbo].[RuleSet]
            SET [RuleSetName] = @RuleSetName,
                [Description] = @Description,
                [IsActive] = @IsActive,
                [UpdatedBy] = @UpdatedBy,
                [UpdatedAt] = @UpdatedAt
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, entity, db: DatabaseId.APS);
    }

    public async Task<bool> RuleSetCodeExistsAsync(string code, long? excludeId = null, CancellationToken ct = default)
    {
        // Q1：大小写不敏感去重（显式 COLLATE Latin1_General_CI_AS 保证契约不随服务器默认排序规则漂移）；
        // excludeId 用于更新时排除自身。
        const string sql = @"
            SELECT COUNT(1) FROM [dbo].[RuleSet]
            WHERE [RuleSetCode] = @RuleSetCode COLLATE Latin1_General_CI_AS
              AND (@ExcludeId IS NULL OR [Id] <> @ExcludeId)";

        var count = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, new { RuleSetCode = code, ExcludeId = excludeId }, db: DatabaseId.APS);
        return count > 0;
    }

    /// <inheritdoc />
    public async Task DeleteFamilyAsync(long id, CancellationToken ct = default)
    {
        // R1 2026-10-25：物理删整族，单事务内「删版本→删主表」。调用方已保证无 PUBLISHED 版本 + 版本未被策略包引用。
        // 事务内直接用传入连接与事务执行，不在事务中再经 GetConnectionAsync 取连接（DatabaseConnectionManager 死锁警告）。
        await _connectionManager.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                const string delVersions = @"DELETE FROM [dbo].[RuleSetVersion] WHERE [RuleSetId] = @Id";
                const string delMain = @"DELETE FROM [dbo].[RuleSet] WHERE [Id] = @Id";

                await connection.ExecuteAsync(delVersions, new { Id = id }, transaction);
                await connection.ExecuteAsync(delMain, new { Id = id }, transaction);
                return 0;
            },
            DatabaseId.APS);
    }
}
