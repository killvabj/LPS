using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// Explanation查询Repository实现
/// 直接读取ScheduleExplanationFact表
/// </summary>
public class ExplanationQueryRepository : IExplanationQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public ExplanationQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<List<ExplanationDto>> QueryAsync(
        int planVersionId,
        string? objectType = null,
        long? orderId = null,
        long? taskId = null,
        string? reasonCode = null,
        string? severity = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    f.Id,
    f.PlanVersionId,
    f.ScheduleRunId,
    f.ObjectType,
    f.OrderId,
    o.OrderNo                                     AS OrderNo,        -- 4号位 R3 可读标识（11-10）：LEFT JOIN，不可关联=null 整键省略
    m.MaterialCode                                AS MaterialCode,   -- 4号位 R3：证据 MaterialId → Material；无键/不可关联=null
    f.TaskId,
    f.ResourceId,
    f.StageCode,
    f.ReasonCode,
    f.Severity,
    f.ImpactHours,
    f.EvidenceJson,
    f.CreatedAt
FROM ScheduleExplanationFact f
LEFT JOIN [Order] o ON o.Id = f.OrderId
LEFT JOIN Material m ON m.Id = TRY_CAST(JSON_VALUE(f.EvidenceJson, '$.MaterialId') AS int)
WHERE f.PlanVersionId = @PlanVersionId
    AND (@ObjectType IS NULL OR f.ObjectType = @ObjectType)
    AND (@OrderId IS NULL OR f.OrderId = @OrderId)
    AND (@TaskId IS NULL OR f.TaskId = @TaskId)
    AND (@ReasonCode IS NULL OR f.ReasonCode = @ReasonCode)
    AND (@Severity IS NULL OR f.Severity = @Severity)
ORDER BY
    CASE f.Severity WHEN 'ERROR' THEN 1 WHEN 'WARN' THEN 2 ELSE 3 END,
    f.ImpactHours DESC,
    f.Id   -- tiebreaker：分页确定性（全链清扫）
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            ObjectType = objectType,
            OrderId = orderId,
            TaskId = taskId,
            ReasonCode = reasonCode,
            Severity = severity,
            Skip = skip,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<ExplanationDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    public async Task<ExplanationSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        // 查询汇总统计
        var summarySql = @"
SELECT
    @PlanVersionId AS PlanVersionId,
    COUNT(*) AS TotalCount,
    SUM(CASE WHEN Severity = 'ERROR' THEN 1 ELSE 0 END) AS ErrorCount,
    SUM(CASE WHEN Severity = 'WARN' THEN 1 ELSE 0 END) AS WarnCount,
    SUM(CASE WHEN Severity = 'INFO' THEN 1 ELSE 0 END) AS InfoCount
FROM ScheduleExplanationFact
WHERE PlanVersionId = @PlanVersionId";

        var summaryParams = new { PlanVersionId = planVersionId };
        var summary = (await _connectionManager.QueryAsync<ExplanationSummaryDto>(
            summarySql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .FirstOrDefault() ?? new ExplanationSummaryDto { PlanVersionId = planVersionId };

        // 查询按ReasonCode分组统计
        var reasonCodeSql = @"
SELECT
    ReasonCode,
    COUNT(*) AS Count,
    SUM(ImpactHours) AS TotalImpactHours
FROM ScheduleExplanationFact
WHERE PlanVersionId = @PlanVersionId
GROUP BY ReasonCode
ORDER BY COUNT(*) DESC";

        var reasonCodeCounts = (await _connectionManager.QueryAsync<ReasonCodeCountDto>(
            reasonCodeSql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .ToList();

        // 查询ERROR级别的Explanation列表
        var items = await QueryAsync(planVersionId, severity: "ERROR", take: 50, ct: ct);

        return new ExplanationSummaryDto
        {
            PlanVersionId = summary.PlanVersionId,
            TotalCount = summary.TotalCount,
            ErrorCount = summary.ErrorCount,
            WarnCount = summary.WarnCount,
            InfoCount = summary.InfoCount,
            ReasonCodeCounts = reasonCodeCounts,
            Items = items
        };
    }

    /// <summary>
    /// FULL-06（0号位任务书 11-09 §2.A）：Demand Q/S/U 对账读模型。
    /// 判据=证据键 `UnscheduledSource` 三族（**不是 ObjectType**——同 ORDER+TaskId NULL 下混有 1号位
    /// `evidenceType=PI_FREE_OVER_COMMIT/EXECUTION_OVER_COMMIT` 诊断族，实测 999464 为 2,390 行，不得合并）。
    /// Q/S/U/OverlapDetected/ReasonCodeGap/UpstreamReasonCode 一律 `JSON_VALUE` **原值透出**，
    /// 读侧不重算、不用 min/max 掩盖 S&gt;Q（修数=2号位 FULL-01）。
    /// JSON 布尔取 'true'/'false' 文本显式转 bit，缺键=NULL（区别于 false）。
    /// </summary>
    public async Task<List<DemandReconciliationRowDto>> QueryDemandReconciliationAsync(
        int planVersionId,
        int? scheduleRunId = null,
        long? orderId = null,
        string? logicalDemandKey = null,
        string? unscheduledSource = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    Id,
    PlanVersionId,
    ScheduleRunId,
    OrderId,
    StageCode,
    ReasonCode,
    Severity,
    JSON_VALUE(EvidenceJson, '$.LogicalDemandKey')                              AS LogicalDemandKey,
    TRY_CAST(JSON_VALUE(EvidenceJson, '$.MaterialId')          AS int)          AS MaterialId,
    JSON_VALUE(EvidenceJson, '$.DomainKey')                                     AS DomainKey,
    TRY_CAST(JSON_VALUE(EvidenceJson, '$.AllocationSequence')  AS int)          AS AllocationSequence,
    TRY_CAST(JSON_VALUE(EvidenceJson, '$.NetOutputQty')        AS decimal(18,4)) AS NetOutputQty,
    TRY_CAST(JSON_VALUE(EvidenceJson, '$.ScheduledShareQty')   AS decimal(18,4)) AS ScheduledShareQty,
    TRY_CAST(JSON_VALUE(EvidenceJson, '$.UnscheduledQty')      AS decimal(18,4)) AS UnscheduledQty,
    JSON_VALUE(EvidenceJson, '$.UnscheduledSource')                             AS UnscheduledSource,
    CASE JSON_VALUE(EvidenceJson, '$.OverlapDetected')
         WHEN 'true'  THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END AS OverlapDetected,
    CASE JSON_VALUE(EvidenceJson, '$.ReasonCodeGap')
         WHEN 'true'  THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END AS ReasonCodeGap,
    JSON_VALUE(EvidenceJson, '$.UpstreamReasonCode')                            AS UpstreamReasonCode,
    CreatedAt
FROM ScheduleExplanationFact
WHERE PlanVersionId = @PlanVersionId
    AND JSON_VALUE(EvidenceJson, '$.UnscheduledSource')
        IN ('UNSCHEDULED_TASK', 'RECONCILIATION_REMAINDER', 'RECONCILIATION_FULLY_SCHEDULED')
    AND (@ScheduleRunId IS NULL OR ScheduleRunId = @ScheduleRunId)
    AND (@OrderId IS NULL OR OrderId = @OrderId)
    AND (@LogicalDemandKey IS NULL OR JSON_VALUE(EvidenceJson, '$.LogicalDemandKey') = @LogicalDemandKey)
    AND (@UnscheduledSourceFilter IS NULL OR JSON_VALUE(EvidenceJson, '$.UnscheduledSource') = @UnscheduledSourceFilter)
ORDER BY
    CASE Severity WHEN 'ERROR' THEN 1 WHEN 'WARN' THEN 2 ELSE 3 END,
    Id   -- tiebreaker：分页确定性（同 QueryAsync 惯例）
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var results = await _connectionManager.QueryAsync<DemandReconciliationRowDto>(
            sql,
            new
            {
                PlanVersionId = planVersionId,
                ScheduleRunId = scheduleRunId,
                OrderId = orderId,
                LogicalDemandKey = logicalDemandKey,
                UnscheduledSourceFilter = unscheduledSource,
                Skip = skip,
                Take = take
            },
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }
}
