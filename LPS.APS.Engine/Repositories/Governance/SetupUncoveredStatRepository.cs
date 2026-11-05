using Dapper;
using LPS.APS.Core.DTOs.Setup;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// Setup 规则缺失（uncovered-stats）只读聚合查询仓储实现（Dapper + APS_Production）。
/// #10 uncovered-stats 端点数据源：<see cref="Task"/>.[SetupSource] = 'SETUP_RULE_MISSING_ZERO_FALLBACK' 命中频次聚合。
/// 只读查询，无任何写操作，禁止查询层反推；列名对齐冻结 DDL v5.1.2（Task / PlanVersion / Resource / ProductionDepartment / Material）。
/// 3号位 依 G4 只读查询归属自写（Setup 治理域只读事实），与 <see cref="MasterDataLookupRepository"/> 同源先例。
/// </summary>
public sealed class SetupUncoveredStatRepository : ISetupUncoveredStatRepository
{
    /// <summary>
    /// 规则缺失兜底值（默认换型缺失，SetupSource 4 态之一）。
    /// Engine 不可引 Scheduling <c>SetupOptimizer</c>（红线 #1 反向引用），故用字面量（与 Task.cs:60 契约注释一致）。
    /// </summary>
    private const string SetupSourceMissingZeroFallback = "SETUP_RULE_MISSING_ZERO_FALLBACK";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<SetupUncoveredStatRepository> _logger;

    public SetupUncoveredStatRepository(DatabaseConnectionManager connectionManager, ILogger<SetupUncoveredStatRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SetupUncoveredStatDto>> GetUncoveredAsync(
        int? runId,
        int? departmentId,
        string? operationCode,
        int? resourceId,
        int? materialId,
        CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    pv.[SourceScheduleRunId]                 AS [ScheduleRunId],
    -- P1 (2026-10-28)：RuleSetVersionId 追溯链路（Run 绑定策略包版本所引规则集版本；LEFT JOIN，Run 无策略包映射时为空）
    spv.[RuleSetVersionId]                   AS [RuleSetVersionId],
    r.[ProductionDepartmentId]               AS [DepartmentId],
    pd.[DeptCode]                            AS [DepartmentCode],
    t.[OperationCode]                        AS [OperationCode],
    t.[ResourceId]                           AS [ResourceId],
    r.[ResourceCode]                         AS [ResourceCode],
    t.[MaterialId]                           AS [MaterialId],
    m.[MaterialCode]                         AS [MaterialCode],
    -- P1 (2026-10-27)：补 StageCode 维度（Task.StageCode 直取），前端大工艺列可回带
    t.[StageCode]                            AS [StageCode],
    COUNT(*)                                 AS [HitCount],
    MIN(t.[Id])                              AS [SampleTaskId]
FROM [dbo].[Task] t
INNER JOIN [dbo].[PlanVersion]          pv ON t.[PlanVersionId]      = pv.[Id]
LEFT  JOIN [dbo].[ScheduleRun]          sr ON sr.[Id]                = pv.[SourceScheduleRunId]
LEFT  JOIN [dbo].[StrategyProfileVersion] spv ON spv.[Id]            = sr.[StrategyProfileVersionId]
INNER JOIN [dbo].[Resource]              r ON t.[ResourceId]         = r.[Id]
INNER JOIN [dbo].[ProductionDepartment] pd ON r.[ProductionDepartmentId] = pd.[Id]
INNER JOIN [dbo].[Material]              m ON t.[MaterialId]         = m.[Id]
WHERE t.[SetupSource] = @SetupSource
  AND (@RunId         IS NULL OR pv.[SourceScheduleRunId]   = @RunId)
  AND (@DepartmentId  IS NULL OR r.[ProductionDepartmentId] = @DepartmentId)
  AND (@OperationCode IS NULL OR t.[OperationCode]          = @OperationCode)
  AND (@ResourceId    IS NULL OR t.[ResourceId]             = @ResourceId)
  AND (@MaterialId    IS NULL OR t.[MaterialId]             = @MaterialId)
GROUP BY pv.[SourceScheduleRunId], spv.[RuleSetVersionId],
         r.[ProductionDepartmentId], pd.[DeptCode],
         t.[OperationCode], t.[ResourceId], r.[ResourceCode], t.[MaterialId], m.[MaterialCode],
         t.[StageCode]
ORDER BY [ScheduleRunId], [DepartmentCode], [OperationCode], [ResourceId], [MaterialCode]";

        return (await _connectionManager.QueryAsync<SetupUncoveredStatDto>(
            sql,
            new
            {
                SetupSource = SetupSourceMissingZeroFallback,
                RunId = runId,
                DepartmentId = departmentId,
                OperationCode = operationCode,
                ResourceId = resourceId,
                MaterialId = materialId,
            },
            db: DatabaseId.APS))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SetupUncoveredStatsPage> GetUncoveredPageAsync(
        int? runId,
        int? departmentId,
        string? operationCode,
        int? resourceId,
        int? materialId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        const string pageSql = @"
SELECT
    pv.[SourceScheduleRunId]                 AS [ScheduleRunId],
    spv.[RuleSetVersionId]                   AS [RuleSetVersionId],
    r.[ProductionDepartmentId]               AS [DepartmentId],
    pd.[DeptCode]                            AS [DepartmentCode],
    t.[OperationCode]                        AS [OperationCode],
    t.[ResourceId]                           AS [ResourceId],
    r.[ResourceCode]                         AS [ResourceCode],
    t.[MaterialId]                           AS [MaterialId],
    m.[MaterialCode]                         AS [MaterialCode],
    t.[StageCode]                            AS [StageCode],
    COUNT(*)                                 AS [HitCount],
    MIN(t.[Id])                              AS [SampleTaskId],
    COUNT(*) OVER()                          AS [Total]
FROM [dbo].[Task] t
INNER JOIN [dbo].[PlanVersion]          pv ON t.[PlanVersionId]      = pv.[Id]
LEFT  JOIN [dbo].[ScheduleRun]          sr ON sr.[Id]                = pv.[SourceScheduleRunId]
LEFT  JOIN [dbo].[StrategyProfileVersion] spv ON spv.[Id]            = sr.[StrategyProfileVersionId]
INNER JOIN [dbo].[Resource]              r ON t.[ResourceId]         = r.[Id]
INNER JOIN [dbo].[ProductionDepartment] pd ON r.[ProductionDepartmentId] = pd.[Id]
INNER JOIN [dbo].[Material]              m ON t.[MaterialId]         = m.[Id]
WHERE t.[SetupSource] = @SetupSource
  AND (@RunId         IS NULL OR pv.[SourceScheduleRunId]   = @RunId)
  AND (@DepartmentId  IS NULL OR r.[ProductionDepartmentId] = @DepartmentId)
  AND (@OperationCode IS NULL OR t.[OperationCode]          = @OperationCode)
  AND (@ResourceId    IS NULL OR t.[ResourceId]             = @ResourceId)
  AND (@MaterialId    IS NULL OR t.[MaterialId]             = @MaterialId)
GROUP BY pv.[SourceScheduleRunId], spv.[RuleSetVersionId],
         r.[ProductionDepartmentId], pd.[DeptCode],
         t.[OperationCode], t.[ResourceId], r.[ResourceCode], t.[MaterialId], m.[MaterialCode],
         t.[StageCode]
ORDER BY [ScheduleRunId], [DepartmentCode], [OperationCode], [ResourceId], [MaterialCode]
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        // KPI summary：涉及部门 / 涉及大工艺 distinct 计数，与列表同一 WHERE（无 GROUP BY 的轻量聚合，与分页行独立）。
        const string summarySql = @"
SELECT COUNT(DISTINCT r.[ProductionDepartmentId]) AS [DepartmentCount],
       COUNT(DISTINCT t.[StageCode])              AS [StageCount]
FROM [dbo].[Task] t
INNER JOIN [dbo].[PlanVersion]          pv ON t.[PlanVersionId] = pv.[Id]
INNER JOIN [dbo].[Resource]              r ON t.[ResourceId]    = r.[Id]
INNER JOIN [dbo].[ProductionDepartment] pd ON r.[ProductionDepartmentId] = pd.[Id]
INNER JOIN [dbo].[Material]              m ON t.[MaterialId]    = m.[Id]
WHERE t.[SetupSource] = @SetupSource
  AND (@RunId         IS NULL OR pv.[SourceScheduleRunId]   = @RunId)
  AND (@DepartmentId  IS NULL OR r.[ProductionDepartmentId] = @DepartmentId)
  AND (@OperationCode IS NULL OR t.[OperationCode]          = @OperationCode)
  AND (@ResourceId    IS NULL OR t.[ResourceId]             = @ResourceId)
  AND (@MaterialId    IS NULL OR t.[MaterialId]             = @MaterialId)";

        var offset = (pageIndex - 1) * pageSize;
        var filter = new
        {
            SetupSource = SetupSourceMissingZeroFallback,
            RunId = runId,
            DepartmentId = departmentId,
            OperationCode = operationCode,
            ResourceId = resourceId,
            MaterialId = materialId,
        };

        // Total = 过滤后全量聚合行数（COUNT(*) OVER()，与列表同 WHERE 同 GROUP，一次查询；空行/越页时无窗口值 → Total 0，正常页码调用不受影响）。
        var rows = (await _connectionManager.QueryAsync<UncoveredStatPageRow>(
            pageSql, new { filter.SetupSource, filter.RunId, filter.DepartmentId, filter.OperationCode, filter.ResourceId, filter.MaterialId, Offset = offset, PageSize = pageSize }, db: DatabaseId.APS)).ToList();
        var summaryRows = (await _connectionManager.QueryAsync<UncoveredStatSummaryRow>(summarySql, filter, db: DatabaseId.APS)).ToList();
        var summary = summaryRows.FirstOrDefault() ?? new UncoveredStatSummaryRow();

        return new SetupUncoveredStatsPage
        {
            Items = rows.Select(r => new SetupUncoveredStatDto
            {
                ScheduleRunId = r.ScheduleRunId,
                RuleSetVersionId = r.RuleSetVersionId,
                DepartmentId = r.DepartmentId,
                DepartmentCode = r.DepartmentCode,
                StageCode = r.StageCode,
                OperationCode = r.OperationCode,
                ResourceId = r.ResourceId,
                ResourceCode = r.ResourceCode,
                MaterialId = r.MaterialId,
                MaterialCode = r.MaterialCode,
                HitCount = r.HitCount,
                SampleTaskId = r.SampleTaskId,
            }).ToList(),
            Total = rows.Count == 0 ? 0 : rows[0].Total,
            Page = pageIndex,
            PageSize = pageSize,
            DepartmentCount = summary.DepartmentCount,
            StageCount = summary.StageCount,
        };
    }

    /// <summary>分页主查询行：本地组合投影（Dapper 映射列 + 窗口函数 Total），避免污染公共 DTO。</summary>
    private sealed class UncoveredStatPageRow
    {
        public int? ScheduleRunId { get; set; }
        public long? RuleSetVersionId { get; set; }
        public int DepartmentId { get; set; }
        public string DepartmentCode { get; set; } = default!;
        public string? StageCode { get; set; }
        public string OperationCode { get; set; } = default!;
        public int ResourceId { get; set; }
        public string ResourceCode { get; set; } = default!;
        public int MaterialId { get; set; }
        public string MaterialCode { get; set; } = default!;
        public int HitCount { get; set; }
        public long? SampleTaskId { get; set; }
        public int Total { get; set; }
    }

    /// <summary>KPI summary 行（涉及部门 / 涉及大工艺 distinct 计数）。</summary>
    private sealed class UncoveredStatSummaryRow
    {
        public int DepartmentCount { get; set; }
        public int StageCount { get; set; }
    }
}