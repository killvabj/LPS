using System.Text;
using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 策略包主表仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.StrategyProfile
/// P0-06：RunType 匹配须经父表 StrategyProfile.RunType（StrategyProfileVersion 无 RunType 列）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileRepository : IStrategyProfileRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<StrategyProfileRepository> _logger;

    public StrategyProfileRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<StrategyProfileRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<StrategyProfile?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[StrategyProfile]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<StrategyProfile>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<StrategyProfile>> GetByRunTypeAsync(string runType, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[StrategyProfile]
            WHERE [RunType] = @RunType
              AND [IsActive] = 1";

        var results = await _connectionManager.QueryAsync<StrategyProfile>(
            sql, new { RunType = runType }, db: DatabaseId.APS);

        return results.ToList();
    }

    public async Task<IReadOnlyList<StrategyProfile>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default)
    {
        var sql = new StringBuilder(@"
            SELECT * FROM [dbo].[StrategyProfile]
            WHERE 1 = 1");

        var parameters = new DynamicParameters();
        if (activeOnly.HasValue)
        {
            sql.Append(" AND [IsActive] = @IsActive");
            parameters.Add("IsActive", activeOnly.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.Append(" AND ([StrategyProfileCode] LIKE @Keyword OR [StrategyProfileName] LIKE @Keyword)");
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

        var results = await _connectionManager.QueryAsync<StrategyProfile>(
            sql.ToString(), parameters, db: DatabaseId.APS);

        return results.ToList();
    }

    // ==================== 主表写路径（2026-10-25：补齐 4号位 缺口，3号位 治理） ====================

    public async Task<StrategyProfile> CreateAsync(StrategyProfile entity, CancellationToken ct = default)
    {
        // Q2 策略包语义：**只建壳，不能自动跟建版本**（StrategyProfileVersion.RuleSetVersionId/ParameterSetVersionId 均必填 FK，
        // 没有「空白首版」合法状态 ⇒ 首版由用户在「新建草稿」里选两个已发布版本）。
        // StrategyProfileCode 创建时唯一（服务层预检），入库由 UNIQUE 约束兜底。
        const string sql = @"
            INSERT INTO [dbo].[StrategyProfile]
                ([StrategyProfileCode], [StrategyProfileName], [Description], [RunType], [IsActive],
                 [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt])
            VALUES
                (@StrategyProfileCode, @StrategyProfileName, @Description, @RunType, @IsActive,
                 @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            sql, entity, db: DatabaseId.APS);

        entity.Id = id;
        return entity;
    }

    public async Task UpdateAsync(StrategyProfile entity, CancellationToken ct = default)
    {
        // Q1：StrategyProfileCode 一经创建不可变更（对齐无重命名语义）——UPDATE 仅改 Name/Description/RunType/IsActive + 审计时间戳。
        // Q4：IsActive 仅关主表，不触碰 StrategyProfileVersion；级联失效语义不引入（版本状态不受影响）。
        const string sql = @"
            UPDATE [dbo].[StrategyProfile]
            SET [StrategyProfileName] = @StrategyProfileName,
                [Description] = @Description,
                [RunType] = @RunType,
                [IsActive] = @IsActive,
                [UpdatedBy] = @UpdatedBy,
                [UpdatedAt] = @UpdatedAt
            WHERE [Id] = @Id";

        await _connectionManager.ExecuteAsync(sql, entity, db: DatabaseId.APS);
    }

    public async Task<bool> StrategyProfileCodeExistsAsync(string code, long? excludeId = null, CancellationToken ct = default)
    {
        // Q1：大小写不敏感去重（显式 COLLATE Latin1_General_CI_AS 保证契约不随服务器默认排序规则漂移）；
        // excludeId 用于更新时排除自身。
        const string sql = @"
            SELECT COUNT(1) FROM [dbo].[StrategyProfile]
            WHERE [StrategyProfileCode] = @StrategyProfileCode COLLATE Latin1_General_CI_AS
              AND (@ExcludeId IS NULL OR [Id] <> @ExcludeId)";

        var count = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, new { StrategyProfileCode = code, ExcludeId = excludeId }, db: DatabaseId.APS);
        return count > 0;
    }

    /// <inheritdoc />
    public async Task DeleteFamilyAsync(long id, CancellationToken ct = default)
    {
        // 2026-10-26 同 R1 口径：物理删整族，单事务内「删版本→删主表」。调用方已保证无 PUBLISHED 版本 + 版本未被运行引用。
        // 事务内直接用传入连接与事务执行，不在事务中再经 GetConnectionAsync 取连接（DatabaseConnectionManager 死锁警告）。
        await _connectionManager.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                const string delVersions = @"DELETE FROM [dbo].[StrategyProfileVersion] WHERE [StrategyProfileId] = @Id";
                const string delMain = @"DELETE FROM [dbo].[StrategyProfile] WHERE [Id] = @Id";

                await connection.ExecuteAsync(delVersions, new { Id = id }, transaction);
                await connection.ExecuteAsync(delMain, new { Id = id }, transaction);
                return 0;
            },
            DatabaseId.APS);
    }
}
