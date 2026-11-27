using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;

namespace LPS.APS.Application.Services.Query;

/// <inheritdoc cref="IDemandResultQueryService"/>
/// <remarks>
/// 纯只读：两条语句都是 `SELECT … WHERE PlanVersionId = @pv`，**不写库、不建临时表**。
/// 参数一律走 Dapper 命名参数，**无裸 `IN @` 超参风险**。
/// 返回上限由调用方 `take` 控制（默认 500，硬上限 5,000）——单域需求可达数万，
/// 不设上限会让一次查询把整表拉回进程。
/// </remarks>
public sealed class DemandResultQueryService : IDemandResultQueryService
{
    private const int MaxTake = 5_000;

    private readonly DatabaseConnectionManager _connectionManager;

    public DemandResultQueryService(DatabaseConnectionManager connectionManager)
        => _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));

    /// <inheritdoc />
    public async Task<DemandResultPage<DemandStageTimingResultView>> QueryStageTimingAsync(
        int planVersionId,
        string? logicalDemandKey = null,
        int? materialId = null,
        string? resultType = null,
        string? timeStatus = null,
        bool usableOnly = false,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(planVersionId), "PlanVersionId 必填且必须 > 0（跨 PV 取数会混时间）");

        // 过滤条件用 `(@X IS NULL OR col = @X)` 而非拼串：Dapper 单命令、无注入面。
        var sql = @"
SELECT Id, PlanVersionId, ScheduleRunId, DomainKey, LogicalDemandKey, DemandKey,
       OrderId, OrderCanonicalId, MaterialId, DemandFactoryId, ResolvedBOMNO, RepWorksetId,
       StageScopeType, DemandNetQty, TimingOnlyQty, ResultType,
       PlanningStartTime, PlannedCompletionTime, CompletionTimeCertain, TimeStatus,
       StageSegmentsJson, InputDigest, EvidenceJson, CreatedAt
  FROM dbo.APS_DemandStageTimingResult
 WHERE PlanVersionId = @Pv
   AND (@Key      IS NULL OR LogicalDemandKey = @Key)
   AND (@Material IS NULL OR MaterialId = @Material)
   AND (@Result   IS NULL OR ResultType = @Result)
   AND (@TimeStat IS NULL OR TimeStatus = @TimeStat)
   AND (@Usable   = 0     OR CompletionTimeCertain = 1)
 ORDER BY LogicalDemandKey
 OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var rows = await _connectionManager.QueryAsync<DemandStageTimingResultView>(
            sql,
            new
            {
                Pv = planVersionId,
                Key = string.IsNullOrWhiteSpace(logicalDemandKey) ? null : logicalDemandKey,
                Material = materialId,
                Result = string.IsNullOrWhiteSpace(resultType) ? null : resultType,
                TimeStat = string.IsNullOrWhiteSpace(timeStatus) ? null : timeStatus,
                Usable = usableOnly ? 1 : 0,
                Skip = Math.Max(skip, 0),
                Take = ClampTake(take) + 1   // 多取 1 行探测是否还有下一批
            },
            db: DatabaseId.APS);

        return ToPage(rows, skip, take);
    }

    /// <inheritdoc />
    public async Task<DemandResultPage<DemandScheduleTechnicalIssueView>> QueryTechnicalIssuesAsync(
        int planVersionId,
        string? logicalDemandKey = null,
        string? issueCode = null,
        int? materialId = null,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(planVersionId), "PlanVersionId 必填且必须 > 0（跨 PV 取数会混时间）");

        var sql = @"
SELECT Id, PlanVersionId, ScheduleRunId, DomainKey, LogicalDemandKey, BindingKey, DemandKey,
       OrderId, MaterialId, StageScopeType, ResolvedBOMNO, RepWorksetId,
       IssueCode, Severity, IssueStatus, EvidenceJson, CreatedAt
  FROM dbo.APS_DemandScheduleTechnicalIssue
 WHERE PlanVersionId = @Pv
   AND (@Key      IS NULL OR LogicalDemandKey = @Key)
   AND (@Code     IS NULL OR IssueCode = @Code)
   AND (@Material IS NULL OR MaterialId = @Material)
 ORDER BY IssueCode, LogicalDemandKey
 OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var rows = await _connectionManager.QueryAsync<DemandScheduleTechnicalIssueView>(
            sql,
            new
            {
                Pv = planVersionId,
                Key = string.IsNullOrWhiteSpace(logicalDemandKey) ? null : logicalDemandKey,
                Code = string.IsNullOrWhiteSpace(issueCode) ? null : issueCode,
                Material = materialId,
                Skip = Math.Max(skip, 0),
                Take = ClampTake(take) + 1   // 多取 1 行探测是否还有下一批
            },
            db: DatabaseId.APS);

        return ToPage(rows, skip, take);
    }

    private static int ClampTake(int take) => take <= 0 ? 500 : Math.Min(take, MaxTake);

    /// <summary>
    /// 把「多取 1 行」的结果切成分页：多余的那行只用于判定 `HasMore`，**不返回给调用方**。
    /// 这样既保住 5,000 的安全上限，又不会静默丢行（0号位 v1.20 §二十四.3）。
    /// </summary>
    private static DemandResultPage<T> ToPage<T>(IEnumerable<T> rowsWithProbe, int skip, int take)
    {
        var effective = ClampTake(take);
        var list = rowsWithProbe as IList<T> ?? rowsWithProbe.ToList();
        var hasMore = list.Count > effective;
        var page = hasMore
            ? list.Take(effective).ToList()
            : list;

        return new DemandResultPage<T>
        {
            Rows = page as IReadOnlyList<T> ?? page.ToList(),
            Skip = Math.Max(skip, 0),
            HasMore = hasMore
        };
    }
}