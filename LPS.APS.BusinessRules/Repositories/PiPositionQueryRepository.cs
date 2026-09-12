using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// PI Position查询Repository实现
/// 直接读取ProductionInstructionPositionSnapshot表
/// </summary>
public class PiPositionQueryRepository : IPiPositionQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public PiPositionQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public async Task<List<PiPositionDto>> QueryAsync(
        int planVersionId,
        string? productionInstructionNo = null,
        string? materialCode = null,
        string? positionType = null,
        string? stageCode = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    Id,
    ScheduleRunId,
    PlanVersionId,
    ProductionInstructionNo,
    MaterialId,
    MaterialCode,
    PositionType,
    Quantity,
    CurrentStageCode,
    NextStageCode,
    AvailableTime,
    SourceType,
    SourceKey,
    IssueCode,
    Confidence,
    CreatedAt
FROM ProductionInstructionPositionSnapshot
WHERE PlanVersionId = @PlanVersionId
    AND (@PINO IS NULL OR ProductionInstructionNo LIKE '%' + @PINO + '%')
    AND (@MaterialCode IS NULL OR MaterialCode LIKE '%' + @MaterialCode + '%')
    AND (@PositionType IS NULL OR PositionType = @PositionType)
    AND (@StageCode IS NULL OR CurrentStageCode = @StageCode OR NextStageCode = @StageCode)
ORDER BY ProductionInstructionNo, PositionType
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            PINO = productionInstructionNo,
            MaterialCode = materialCode,
            PositionType = positionType,
            StageCode = stageCode,
            Skip = skip,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<PiPositionDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    public async Task<PiPositionSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        var summaryParams = new { PlanVersionId = planVersionId };

        // 汇总统计
        var summarySql = @"
SELECT
    @PlanVersionId AS PlanVersionId,
    COUNT(DISTINCT ProductionInstructionNo) AS PiCount,
    COUNT(*) AS TotalPositions
FROM ProductionInstructionPositionSnapshot
WHERE PlanVersionId = @PlanVersionId";

        var summary = (await _connectionManager.QueryAsync<PiPositionSummaryDto>(
            summarySql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .FirstOrDefault() ?? new PiPositionSummaryDto { PlanVersionId = planVersionId };

        // PositionType统计
        var typeSql = @"
SELECT
    PositionType,
    COUNT(*) AS Count,
    SUM(Quantity) AS TotalQuantity
FROM ProductionInstructionPositionSnapshot
WHERE PlanVersionId = @PlanVersionId
GROUP BY PositionType
ORDER BY COUNT(*) DESC";

        var typeCounts = (await _connectionManager.QueryAsync<PositionTypeCountDto>(
            typeSql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .ToList();

        return new PiPositionSummaryDto
        {
            PlanVersionId = summary.PlanVersionId,
            PiCount = summary.PiCount,
            TotalPositions = summary.TotalPositions,
            PositionTypeCounts = typeCounts
        };
    }
}
