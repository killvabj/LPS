using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 域依赖关系仓储接口（3号位治理，对应 APS_Production.dbo.Domain_Dependency）
/// 3-4联调接口 G7：4号位失败链 / 域依赖关系查询（3号位文档 §十六）。
/// 数据来源：2号位 sp_ScanDomainDependency 每日扫描（本接口只读，不落库）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IDomainDependencyRepository
{
    /// <summary>
    /// 按域查询依赖关系
    /// </summary>
    /// <param name="domainCode">目标 DomainKey（必填）</param>
    /// <param name="direction">
    /// 查询方向：
    /// downstream = 该域的下游依赖（UpstreamDomainCode = @domainCode，即本域被谁依赖）；
    /// upstream   = 该域的上游依赖（DownstreamDomainCode = @domainCode，即本域依赖谁）。
    /// </param>
    /// <param name="ct">取消令牌</param>
    /// <param name="allowedDomains">授权域集合（数据范围收窄，4号位 2026-10-22 缺口修复）；
    /// 空集 → fail-closed 返空；null = Global 全放行（不追加过滤）；非空 → 任一端在授权范围即可见该边。</param>
    Task<IReadOnlyList<DomainDependency>> GetByDomainAsync(
        string domainCode,
        string direction,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null);
}
