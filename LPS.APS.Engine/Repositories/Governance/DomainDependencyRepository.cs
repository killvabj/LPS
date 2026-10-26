using Dapper;
using DomainDependency = LPS.APS.Core.Entities.APS.DomainDependency;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 域依赖关系仓储实现（Dapper + APS_Production，只读）
/// 对应表：APS_Production.dbo.Domain_Dependency（2号位 sp_ScanDomainDependency 扫描落库）
/// 3-4联调接口 G7：失败链 / 域依赖关系查询。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class DomainDependencyRepository : IDomainDependencyRepository
{
    private const string DirectionDownstream = "downstream";
    private const string DirectionUpstream = "upstream";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<DomainDependencyRepository> _logger;

    public DomainDependencyRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<DomainDependencyRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DomainDependency>> GetByDomainAsync(
        string domainCode,
        string direction,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null)
    {
        // 授权域收窄（4号位 2026-10-22 缺口修复；语义按 IDomainDependencyRepository 注释逐字实现）：
        //   null      → Global 全放行，不追加过滤（与原行为一致）
        //   空集      → fail-closed 直接返空（**不得**退化成「全放行」）
        //   非空      → 任一端（Upstream 或 Downstream）落在授权范围即可见该边
        if (allowedDomains is { Count: 0 }) return Array.Empty<DomainDependency>();

        var normalizedDirection = string.Equals(direction, DirectionUpstream, StringComparison.OrdinalIgnoreCase)
            ? DirectionUpstream
            : DirectionDownstream;

        // downstream：查询以该域为上游的依赖（本域的下游）
        // upstream  ：查询以该域为下游的依赖（本域的上游）
        // 授权过滤用 `IN @Allowed`（Dapper 展开为参数列表）；域码为小集合（全库仅十余个域），
        // 不存在装载层「裸 IN 超 2100 参数」的量级风险。
        var scopeFilter = allowedDomains is null
            ? string.Empty
            : " AND ([UpstreamDomainCode] IN @Allowed OR [DownstreamDomainCode] IN @Allowed)";

        var sql = normalizedDirection == DirectionUpstream
            ? @"SELECT * FROM [dbo].[Domain_Dependency]
                WHERE [DownstreamDomainCode] = @DomainCode" + scopeFilter + @"
                ORDER BY [UpstreamDomainCode], [ChildMaterialCode]"
            : @"SELECT * FROM [dbo].[Domain_Dependency]
                WHERE [UpstreamDomainCode] = @DomainCode" + scopeFilter + @"
                ORDER BY [DownstreamDomainCode], [ChildMaterialCode]";

        var results = await _connectionManager.QueryAsync<DomainDependency>(
            sql,
            new
            {
                DomainCode = domainCode,
                Allowed    = allowedDomains?.ToList()
            },
            db: DatabaseId.APS);

        return results.ToList();
    }
}
