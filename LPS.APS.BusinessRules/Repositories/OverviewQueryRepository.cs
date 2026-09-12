using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// Overview查询Repository实现
/// 直接读取APS_Production事实表
/// </summary>
public class OverviewQueryRepository : IOverviewQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public OverviewQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<OverviewActivePlanDto?> GetActivePlanAsync(
        string? domainKey = null,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT TOP 1
    Id AS PlanVersionId,
    VersionCode,
    DomainKey,
    PlanHorizonStart,
    PlanHorizonEnd,
    ActivatedAt,
    SourceScheduleRunId,
    TotalTasks,
    TotalOrders,
    CreatedAt
FROM PlanVersion
WHERE Status = 'ACTIVE'
    AND (@DomainKey IS NULL OR DomainKey = @DomainKey)
ORDER BY ActivatedAt DESC";

        var parameters = new { DomainKey = domainKey };

        var results = await _connectionManager.QueryAsync<OverviewActivePlanDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        return results.FirstOrDefault();
    }

    public async Task<OverviewTaskSummaryDto> GetTaskSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    @PlanVersionId AS PlanVersionId,
    COUNT(*) AS TotalTasks,
    SUM(CASE WHEN Status = 'COMPLETED' AND PlannedEndTime <= GETDATE() THEN 1 ELSE 0 END) AS OnTimeCount,
    SUM(CASE WHEN Status = 'COMPLETED' AND PlannedEndTime > GETDATE() THEN 1 ELSE 0 END) AS DelayedCount,
    SUM(CASE WHEN Status = 'IN_PROGRESS' AND IsCriticalPath = 1 THEN 1 ELSE 0 END) AS RiskCount,
    SUM(CASE WHEN Status = 'PENDING' OR Status = 'QUEUED' THEN 1 ELSE 0 END) AS UnscheduledCount,
    SUM(CASE WHEN Status = 'ESTIMATED' THEN 1 ELSE 0 END) AS EstimatedOnlyCount,
    SUM(CASE WHEN Status NOT IN ('COMPLETED', 'IN_PROGRESS', 'PENDING', 'QUEUED', 'ESTIMATED') THEN 1 ELSE 0 END) AS OtherCount
FROM Task
WHERE PlanVersionId = @PlanVersionId";

        var parameters = new { PlanVersionId = planVersionId };

        var results = await _connectionManager.QueryAsync<OverviewTaskSummaryDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.FirstOrDefault() ?? new OverviewTaskSummaryDto { PlanVersionId = planVersionId };
    }

    public async Task<List<OverviewResourceBottleneckDto>> GetResourceBottleneckAsync(
        int planVersionId,
        int topN = 10,
        CancellationToken ct = default)
    {
        // 注意：Bottleneck判定应由2号位正式Query提供，5号位不做二次计算
        // 此处仅返回资源任务数统计，不判定Bottleneck
        var sql = @"
SELECT TOP (@TopN)
    t.ResourceId,
    r.ResourceCode,
    r.ResourceName,
    r.ResourceType,
    COUNT(*) AS TaskCount,
    SUM(ISNULL(t.Duration, 0)) / 3600.0 AS TotalPlannedHours,
    NULL AS UtilizationRate,
    0 AS IsBottleneck
FROM Task t
INNER JOIN Resource r ON r.Id = t.ResourceId
WHERE t.PlanVersionId = @PlanVersionId
    AND t.ResourceId IS NOT NULL
GROUP BY t.ResourceId, r.ResourceCode, r.ResourceName, r.ResourceType
ORDER BY TotalPlannedHours DESC";

        var parameters = new { PlanVersionId = planVersionId, TopN = topN };

        var results = await _connectionManager.QueryAsync<OverviewResourceBottleneckDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    public async Task<OverviewCandidateSummaryDto> GetCandidateSummaryAsync(
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    Id AS PlanVersionId,
    VersionCode,
    DomainKey,
    CreatedAt,
    CreatedByUserName
FROM PlanVersion
WHERE VersionCategory = 'CANDIDATE'
ORDER BY CreatedAt DESC";

        var candidates = await _connectionManager.QueryAsync<CandidateBriefDto>(
            sql, null, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        var list = candidates.ToList();

        return new OverviewCandidateSummaryDto
        {
            PendingCount = list.Count,
            Candidates = list
        };
    }
}
