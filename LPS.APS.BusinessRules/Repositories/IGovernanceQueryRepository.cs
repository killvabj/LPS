using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 治理查询Repository接口（G4/G7查询）
/// 5号位直接读取APS_Production事实表
/// </summary>
public interface IGovernanceQueryRepository
{
    /// <summary>
    /// 查询排程运行列表（G4）
    /// </summary>
    Task<List<ScheduleRunGov>> QueryScheduleRunsAsync(
        string? status = null,
        string? runType = null,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>
    /// 查询排程域依赖（G7）
    /// </summary>
    Task<List<DomainDependencyDto>> QueryDomainDependenciesAsync(
        string? domainCode = null,
        string? direction = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null);

    /// <summary>
    /// 单 Run 范围原始行（run/{id}/scope 用；null=Run 不存在→上层 404）。
    /// **独立方法，不扩展 ScheduleRunGov**（4号位 §四：复用会改变 runs 列表响应形状，3号位 已声明该列表保持现状）。
    /// </summary>
    Task<RunScopeRawRow?> GetRunScopeRawAsync(int scheduleRunId, CancellationToken ct = default);

    /// <summary>订单号富化：Order_Canonical 本地表（4号位 §3.1，无跨号位映射面）。</summary>
    Task<Dictionary<long, (string? OrderNo, string? MTS_InstructionNo)>> GetOrderCanonicalMapAsync(
        IReadOnlyList<long> orderCanonicalIds, CancellationToken ct = default);

    /// <summary>任务号富化：Task 按全局唯一 Id 直查（PK 前导列，无结果版本的 Run 也能带出）。</summary>
    Task<Dictionary<long, (string? TaskNo, string? OperationCode)>> GetTaskMapAsync(
        IReadOnlyList<long> taskIds, CancellationToken ct = default);

    /// <summary>资源富化：Resource 本地表。</summary>
    Task<Dictionary<int, (string? ResourceCode, string? ResourceName)>> GetResourceMapAsync(
        IReadOnlyList<int> resourceIds, CancellationToken ct = default);
}

/// <summary>run/{id}/scope 原始行（内部传输；ScopeJson 原文由服务层解析）。</summary>
public sealed class RunScopeRawRow
{
    public int Id { get; set; }
    public string RunType { get; set; } = string.Empty;
    public string? ScopeJson { get; set; }
    public int? BasePlanVersionId { get; set; }
    public string? BaseVersionCode { get; set; }
}
