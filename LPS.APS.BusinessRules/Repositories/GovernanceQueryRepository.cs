using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 治理查询Repository实现（G4/G7查询）
/// 5号位直接读取APS_Production事实表
/// </summary>
public class GovernanceQueryRepository : IGovernanceQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public GovernanceQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<List<ScheduleRunGov>> QueryScheduleRunsAsync(
        string? status = null,
        string? runType = null,
        int take = 100,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT TOP (@Take)
    Id,
    RunType,
    Status,
    TriggeredBy,
    DataCutoffTime,
    StrategyProfileVersionId,
    ExpectedDomainKeysJson,
    StartedAt,
    CompletedAt,
    ErrorMessage
FROM ScheduleRun
WHERE 1=1
    AND (@Status IS NULL OR Status = @Status)
    AND (@RunType IS NULL OR RunType = @RunType)
ORDER BY Id DESC";

        var parameters = new
        {
            Status = status,
            RunType = runType,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<ScheduleRunGov>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    public async Task<List<DomainDependencyDto>> QueryDomainDependenciesAsync(
        string? domainCode = null,
        string? direction = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null)
    {
        // Dapper 列表参数只进 IN；@AllowedDomains IS NULL 标量判空会随列表一起扩成 (…) 导致 4145，用 Has 标志替代；空集 fail-closed 返空。
        // 注意 "all" 分支含两处 IN @AllowedDomains 均保留，仅标量判空改为 Has 标志。
        if (allowedDomains is { Count: 0 })
            return new List<DomainDependencyDto>();
        var hasDomains = allowedDomains is { Count: > 0 };

        string sql;

        if (direction == "upstream")
        {
            // 查本域依赖谁（upstream = 本域是Downstream，查Upstream）
            sql = @"
SELECT
    UpstreamDomainCode,
    DownstreamDomainCode,
    ChildMaterialCode,
    DefaultLeadTimeDays,
    ScannedAt
FROM Domain_Dependency
WHERE DownstreamDomainCode = @DomainCode
    AND (@HasDomains = 0 OR DownstreamDomainCode IN @AllowedDomains)
ORDER BY UpstreamDomainCode";
        }
        else if (direction == "downstream")
        {
            // 查谁依赖本域（downstream = 本域是Upstream，查Downstream）
            sql = @"
SELECT
    UpstreamDomainCode,
    DownstreamDomainCode,
    ChildMaterialCode,
    DefaultLeadTimeDays,
    ScannedAt
FROM Domain_Dependency
WHERE UpstreamDomainCode = @DomainCode
    AND (@HasDomains = 0 OR UpstreamDomainCode IN @AllowedDomains)
ORDER BY DownstreamDomainCode";
        }
        else
        {
            // 查所有依赖
            sql = @"
SELECT
    UpstreamDomainCode,
    DownstreamDomainCode,
    ChildMaterialCode,
    DefaultLeadTimeDays,
    ScannedAt
FROM Domain_Dependency
WHERE 1=1
    AND (@DomainCode IS NULL
         OR UpstreamDomainCode = @DomainCode
         OR DownstreamDomainCode = @DomainCode)
    AND (@HasDomains = 0
         OR UpstreamDomainCode IN @AllowedDomains
         OR DownstreamDomainCode IN @AllowedDomains)
ORDER BY UpstreamDomainCode, DownstreamDomainCode";
        }

        var parameters = new
        {
            DomainCode = domainCode,
            HasDomains = hasDomains,
            AllowedDomains = allowedDomains
        };

        var results = await _connectionManager.QueryAsync<DomainDependencyDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    // ───── run/{id}/scope 支撑读取（4号位 10-19《候选scope明细端点改归你侧》§二/§三；归属变更承接）─────

    public async Task<RunScopeRawRow?> GetRunScopeRawAsync(int scheduleRunId, CancellationToken ct = default)
        => await _connectionManager.QueryFirstOrDefaultAsync<RunScopeRawRow>(@"
SELECT TOP 1
    sr.Id,
    sr.RunType,
    sr.ScopeJson,
    sr.BasePlanVersionId,
    bpv.VersionCode AS BaseVersionCode
FROM ScheduleRun sr
LEFT JOIN PlanVersion bpv ON bpv.Id = sr.BasePlanVersionId
WHERE sr.Id = @Id",
            new { Id = scheduleRunId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

    public async Task<Dictionary<long, (string? OrderNo, string? MTS_InstructionNo)>> GetOrderCanonicalMapAsync(
        IReadOnlyList<long> orderCanonicalIds, CancellationToken ct = default)
    {
        if (orderCanonicalIds.Count == 0) return new();
        var rows = await _connectionManager.QueryAsync<OrderCanonicalMapRow>(
            "SELECT Id, OrderNo, MTS_InstructionNo FROM Order_Canonical WHERE Id IN @Ids",
            new { Ids = orderCanonicalIds }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        return rows.ToDictionary(r => r.Id, r => (r.OrderNo, r.MTS_InstructionNo));
    }

    public async Task<Dictionary<long, (string? TaskNo, string? OperationCode)>> GetTaskMapAsync(
        IReadOnlyList<long> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return new();
        // Task.Id BIGINT IDENTITY 全局唯一 + PK(Id,PlanVersionId) 前导列 → 按 Id 直查 seek（4号位 §3.2）；
        // 无结果版本的 Run 也能带出 taskNo；行不存在 → 上层置 null 保留行，不 500。
        var rows = await _connectionManager.QueryAsync<TaskMapRow>(
            "SELECT Id, TaskNo, OperationCode FROM Task WHERE Id IN @Ids",
            new { Ids = taskIds }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        return rows.ToDictionary(r => r.Id, r => ((string?)r.TaskNo, (string?)r.OperationCode));
    }

    public async Task<Dictionary<int, (string? ResourceCode, string? ResourceName)>> GetResourceMapAsync(
        IReadOnlyList<int> resourceIds, CancellationToken ct = default)
    {
        if (resourceIds.Count == 0) return new();
        var rows = await _connectionManager.QueryAsync<ResourceMapRow>(
            "SELECT Id, ResourceCode, ResourceName FROM Resource WHERE Id IN @Ids",
            new { Ids = resourceIds }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        return rows.ToDictionary(r => r.Id, r => ((string?)r.ResourceCode, (string?)r.ResourceName));
    }

    private sealed class OrderCanonicalMapRow { public long Id { get; set; } public string? OrderNo { get; set; } public string? MTS_InstructionNo { get; set; } }
    private sealed class TaskMapRow { public long Id { get; set; } public string? TaskNo { get; set; } public string? OperationCode { get; set; } }
    private sealed class ResourceMapRow { public int Id { get; set; } public string? ResourceCode { get; set; } public string? ResourceName { get; set; } }
}
