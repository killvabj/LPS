using System.Text;
using LPS.APS.Application.Services.Query.Dto;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// 跨厂未闭合净缺口只读查询实现（CFV1-08，2026-11-13）。表定义见 `08 DDL v5.1.8.11`，
/// 建表脚本 `Database/Scripts/APS/03_SQL脚本/建表_APS_UnresolvedCrossFactorySupplyResult_20261113.sql`。
///
/// <para><b>为什么把异常吞成 QUERY_FAILED 而不往外抛</b>：`06 v1.42` 第 5 条与 `13 v1.9` 明令
/// 「无记录的可信零缺口」与「接口不可用/装载失败」必须能区分。若直接抛异常，上层若用
/// try/catch 兜成空列表，就会把**查询失败显示成「没有缺口」**——正是冻结禁止的。
/// 故此处显式返回状态，异常原因落日志并在 <c>ErrorDetail</c> 带出。</para>
///
/// <para><b>只读、不推算</b>：SQL 只做等值过滤与字段投影，不在服务层补默认值、不推源厂、不猜需用时间。</para>
/// </summary>
public class UnresolvedCrossFactorySupplyQueryService : IUnresolvedCrossFactorySupplyQueryService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<UnresolvedCrossFactorySupplyQueryService> _logger;

    public UnresolvedCrossFactorySupplyQueryService(
        DatabaseConnectionManager connectionManager,
        ILogger<UnresolvedCrossFactorySupplyQueryService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private const string SelectClause = @"
        SELECT [Id], [ScheduleRunId], [PlanVersionId], [ResultRecordKey],
               [RootOrderId], [RootDemandKey], [MaterialId], [MaterialCode],
               [TargetFactoryId], [DomainKey], [SourceFactoryId], [SourceDomainKey], [NeedByTime],
               [RequiredQty], [LegallyAllocatedQty], [UnresolvedQty], [UnresolvedCategory],
               [ActualSHOrderNo], [ActualProductionInstructionNo],
               [SourceEvidenceJson], [EvidenceBasisCode], [IssueRemark],
               [CommitmentStatus], [CreatedAt]
          FROM [dbo].[APS_UnresolvedCrossFactorySupplyResult]
         WHERE [ScheduleRunId] = @ScheduleRunId
           AND [PlanVersionId] = @PlanVersionId";

    /// <inheritdoc />
    public async Task<UnresolvedCrossFactorySupplyQueryDto> QueryAsync(
        int scheduleRunId,
        int planVersionId,
        string? domainKey = null,
        string? rootDemandKey = null,
        string? materialCode = null,
        int? targetFactoryId = null,
        CancellationToken cancellationToken = default)
    {
        // 只拼接**固定的**条件子句，所有值一律走参数，不做字符串插值（防注入、防类型漂移）。
        var sql = new StringBuilder(SelectClause);
        if (!string.IsNullOrWhiteSpace(domainKey))      sql.Append(" AND [DomainKey] = @DomainKey");
        if (!string.IsNullOrWhiteSpace(rootDemandKey))  sql.Append(" AND [RootDemandKey] = @RootDemandKey");
        if (!string.IsNullOrWhiteSpace(materialCode))   sql.Append(" AND [MaterialCode] = @MaterialCode");
        if (targetFactoryId.HasValue)                   sql.Append(" AND [TargetFactoryId] = @TargetFactoryId");
        sql.Append(" ORDER BY [Id]");

        try
        {
            var rows = await _connectionManager.QueryAsync<UnresolvedCrossFactorySupplyQueryDtoRow>(
                sql.ToString(),
                new
                {
                    ScheduleRunId = scheduleRunId,
                    PlanVersionId = planVersionId,
                    DomainKey = domainKey,
                    RootDemandKey = rootDemandKey,
                    MaterialCode = materialCode,
                    TargetFactoryId = targetFactoryId
                },
                db: DatabaseId.APS);

            var items = rows.Select(ToItem).ToList();

            _logger.LogInformation(
                "[CFV1-08] 未闭合结果查询：Run={RunId} PV={Pv} Domain={Domain} 根需求={Root} 物料={Material} 目标厂={Factory} ⇒ {Count} 条",
                scheduleRunId, planVersionId, domainKey ?? "(不限)", rootDemandKey ?? "(不限)",
                materialCode ?? "(不限)", targetFactoryId?.ToString() ?? "(不限)", items.Count);

            return new UnresolvedCrossFactorySupplyQueryDto
            {
                ScheduleRunId = scheduleRunId,
                PlanVersionId = planVersionId,
                ResultState = UnresolvedCrossFactorySupplyQueryState.Ok,
                ItemCount = items.Count,
                Items = items
            };
        }
        catch (Exception ex)
        {
            // 查询失败**不得**伪装成「零缺口」（冻结逐字要求可区分）。
            _logger.LogError(ex,
                "[CFV1-08] 未闭合结果查询失败：Run={RunId} PV={Pv}（对外须呈现为「查询不可用」，不得呈现为「无缺口」）",
                scheduleRunId, planVersionId);

            return new UnresolvedCrossFactorySupplyQueryDto
            {
                ScheduleRunId = scheduleRunId,
                PlanVersionId = planVersionId,
                ResultState = UnresolvedCrossFactorySupplyQueryState.QueryFailed,
                ErrorDetail = ex.Message,
                ItemCount = 0,
                Items = Array.Empty<UnresolvedCrossFactorySupplyItemDto>()
            };
        }
    }

    /// <summary>库行 → 对外 DTO。**只搬运，不补值、不推算**。</summary>
    private static UnresolvedCrossFactorySupplyItemDto ToItem(UnresolvedCrossFactorySupplyQueryDtoRow r) => new()
    {
        Id                            = r.Id,
        ResultRecordKey               = r.ResultRecordKey ?? string.Empty,
        RootOrderId                   = r.RootOrderId,
        RootDemandKey                 = r.RootDemandKey ?? string.Empty,
        MaterialId                    = r.MaterialId,
        MaterialCode                  = r.MaterialCode ?? string.Empty,
        TargetFactoryId               = r.TargetFactoryId,
        DomainKey                     = r.DomainKey ?? string.Empty,
        SourceFactoryId               = r.SourceFactoryId,
        SourceDomainKey               = r.SourceDomainKey,
        NeedByTime                    = r.NeedByTime,
        RequiredQty                   = r.RequiredQty,
        LegallyAllocatedQty           = r.LegallyAllocatedQty,
        UnresolvedQty                 = r.UnresolvedQty,
        UnresolvedCategory            = r.UnresolvedCategory ?? string.Empty,
        ActualSHOrderNo               = r.ActualSHOrderNo,
        ActualProductionInstructionNo = r.ActualProductionInstructionNo,
        SourceEvidenceJson            = r.SourceEvidenceJson,
        EvidenceBasisCode             = r.EvidenceBasisCode ?? string.Empty,
        IssueRemark                   = r.IssueRemark,
        CommitmentStatus              = r.CommitmentStatus ?? "NOT_COMMITTED",
        CreatedAt                     = r.CreatedAt
    };

    /// <summary>Dapper 映射用的可写行类型（DTO 是 <c>init</c>-only，不直接给 Dapper 映射）。</summary>
    private sealed class UnresolvedCrossFactorySupplyQueryDtoRow
    {
        public long Id { get; set; }
        public int ScheduleRunId { get; set; }
        public int PlanVersionId { get; set; }
        public string? ResultRecordKey { get; set; }
        public long? RootOrderId { get; set; }
        public string? RootDemandKey { get; set; }
        public int? MaterialId { get; set; }
        public string? MaterialCode { get; set; }
        public int TargetFactoryId { get; set; }
        public string? DomainKey { get; set; }
        public int? SourceFactoryId { get; set; }
        public string? SourceDomainKey { get; set; }
        public DateTime? NeedByTime { get; set; }
        public decimal RequiredQty { get; set; }
        public decimal LegallyAllocatedQty { get; set; }
        public decimal UnresolvedQty { get; set; }
        public string? UnresolvedCategory { get; set; }
        public string? ActualSHOrderNo { get; set; }
        public string? ActualProductionInstructionNo { get; set; }
        public string? SourceEvidenceJson { get; set; }
        public string? EvidenceBasisCode { get; set; }
        public string? IssueRemark { get; set; }
        public string? CommitmentStatus { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}