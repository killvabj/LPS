using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// Pegging Trace查询Repository实现
/// 直接读取PeggingSupplyAllocation表
/// </summary>
public class PeggingTraceRepository : IPeggingTraceRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public PeggingTraceRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    // ── 11-14（4号位《供需追溯列表端点需返筛选后total…》件 R2.2）：**列表 / count / summary 同一 WHERE 文本单源** ──
    // 核心筛选与数据范围谓词分两段常量，三处拼接；「total 与列表两个口径」结构性消除
    // （同订单页 KPI 三桶单源、人工ETA 桶常量的同型纪律）。
    // 核心筛选沿用 10-19 既定语义：commitmentStatus=UNKNOWN 特判 IS NULL（与汇总 ISNULL 同口径）、
    // orderNo 依赖 RootOrderNo/CurrentOrderNo（dev 全 NULL，写入侧归 2号位，筛选用 rootOrderId）。
    private const string TraceWhereCore = @"
WHERE PlanVersionId = @PlanVersionId
    AND (@MaterialCode IS NULL OR MaterialCode LIKE '%' + @MaterialCode + '%')
    AND (@SupplyType IS NULL OR SupplyType = @SupplyType)
    AND (@CommitmentStatus IS NULL
         OR (@CommitmentStatus = 'UNKNOWN' AND CommitmentStatus IS NULL)
         OR CommitmentStatus = @CommitmentStatus)
    AND (@OrderNo IS NULL OR RootOrderNo LIKE '%' + @OrderNo + '%' OR CurrentOrderNo LIKE '%' + @OrderNo + '%')
    AND (@RootOrderId IS NULL OR RootOrderId = @RootOrderId)
    AND (@SupplyDocumentNo IS NULL OR SupplyDocumentNo LIKE '%' + @SupplyDocumentNo + '%')";

    /// <summary>数据范围谓词（DemandFactoryCode，31-0 Q2 裁决；空集 fail-closed 由调用方前置处理）。</summary>
    private const string TraceScopePredicate = @"
    AND (@HasFactories = 0 OR DemandFactoryCode IN @AllowedFactories)";

    private const string TraceSelectCols = @"
SELECT
    Id,
    PlanVersionId,
    ScheduleRunId,
    AllocationSequence,
    RootOrderId,
    RootOrderNo,
    CurrentOrderId,
    CurrentOrderNo,
    OrderType,
    MaterialCode,
    MaterialId,
    DemandFactoryCode,
    DemandStageCode,
    DemandQty,
    AllocatedQty,
    SupplyType,
    SupplyFactoryCode,
    SupplyWarehouseCode,
    ERPProperty,
    SupplyDocumentType,
    SupplyDocumentNo,
    SupplyMode,
    CrossFactoryEdgeId,
    TransportLeadTimeHours,
    ETA,
    KnownAvailableTime,
    CommitmentStatus,
    AttachStageCode,
    CompletedStageCode,
    NextRequiredStageCode,
    CreatedAt";

    public async Task<List<PeggingTraceDto>> QueryAsync(
        int planVersionId,
        string? materialCode = null,
        string? supplyType = null,
        string? commitmentStatus = null,
        string? orderNo = null,
        string? supplyDocumentNo = null,
        long? rootOrderId = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        // Dapper 列表参数只进 IN；@AllowedFactories IS NULL 标量判空会随列表一起扩成 (…) 导致 4145，用 Has 标志替代；空集 fail-closed 返空。
        if (allowedFactories is { Count: 0 })
            return new List<PeggingTraceDto>();
        var hasFactories = allowedFactories is { Count: > 0 };

        var sql = TraceSelectCols + @"
FROM PeggingSupplyAllocation" + TraceWhereCore + TraceScopePredicate + @"
ORDER BY AllocationSequence
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            MaterialCode = materialCode,
            SupplyType = supplyType,
            CommitmentStatus = commitmentStatus,
            OrderNo = orderNo,
            SupplyDocumentNo = supplyDocumentNo,
            RootOrderId = rootOrderId,
            HasFactories = hasFactories,
            AllowedFactories = allowedFactories,
            Skip = skip,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<PeggingTraceDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    /// <summary>
    /// R2.1（11-14 4号位件，形状 B）：与 <see cref="QueryAsync"/> **同一 WHERE 文本**的筛选后总数。
    /// 空范围集 fail-closed=0；返回 0 与"查无数据"在**范围集为空**时不可区分——调用方（端点）只对真实空集返回 0，
    /// 范围拒绝应另走权限语义（本端点权限在控制器层，越权账号在 Scope 解析处即被收窄/拒绝）。
    /// </summary>
    public async Task<int> CountAsync(
        int planVersionId,
        string? materialCode = null,
        string? supplyType = null,
        string? commitmentStatus = null,
        string? orderNo = null,
        string? supplyDocumentNo = null,
        long? rootOrderId = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        if (allowedFactories is { Count: 0 })
            return 0;
        var hasFactories = allowedFactories is { Count: > 0 };

        var sql = "SELECT COUNT(*) FROM PeggingSupplyAllocation" + TraceWhereCore + TraceScopePredicate;

        var parameters = new
        {
            PlanVersionId = planVersionId,
            MaterialCode = materialCode,
            SupplyType = supplyType,
            CommitmentStatus = commitmentStatus,
            OrderNo = orderNo,
            SupplyDocumentNo = supplyDocumentNo,
            RootOrderId = rootOrderId,
            HasFactories = hasFactories,
            AllowedFactories = allowedFactories
        };

        return await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);
    }

    /// <summary>
    /// 按订单查询Pegging分配。
    /// 11-14：补数据范围谓词（同列表同一 TraceScopePredicate）——此前本查询**完全不过 Scope**，
    /// 属同族越权面（受限账号可凭一个 orderId 拿到他厂分配行）；空范围集 fail-closed 返空。
    /// </summary>
    public async Task<List<PeggingTraceDto>> QueryByOrderAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        if (allowedFactories is { Count: 0 })
            return new List<PeggingTraceDto>();
        var hasFactories = allowedFactories is { Count: > 0 };

        var sql = TraceSelectCols + @"
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId
    AND (RootOrderId = @OrderId OR CurrentOrderId = @OrderId)" + TraceScopePredicate + @"
ORDER BY AllocationSequence";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderId = orderId,
            HasFactories = hasFactories,
            AllowedFactories = allowedFactories
        };

        var results = await _connectionManager.QueryAsync<PeggingTraceDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    /// <summary>
    /// Pegging Trace汇总。
    /// 11-14（R2.4 裁定**收进数据范围**）：三查全部追加同一 <see cref="TraceScopePredicate"/>——
    /// 旧版不带范围，受限账号看到全厂总数（dev 999512：pmc 列表 17,181 而 summary 报 73,375，虚高 4.27 倍），
    /// 既是口径分叉也是元数据泄漏；空范围集 fail-closed 返空汇总。
    /// ⚠ 前端「当前计划版本的全部笔数」文案需改为「当前账号可见笔数」（复函已告知）。
    /// </summary>
    public async Task<PeggingTraceSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        if (allowedFactories is { Count: 0 })
            return new PeggingTraceSummaryDto { PlanVersionId = planVersionId };
        var hasFactories = allowedFactories is { Count: > 0 };
        var summaryParams = new { PlanVersionId = planVersionId, HasFactories = hasFactories, AllowedFactories = allowedFactories };

        // 总数统计
        var totalSql = @"
SELECT COUNT(*) AS TotalAllocations
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId" + TraceScopePredicate;

        var total = (await _connectionManager.QueryAsync<int>(
            totalSql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .FirstOrDefault();

        // SupplyType统计
        var supplyTypeSql = @"
SELECT
    SupplyType,
    COUNT(*) AS Count,
    SUM(AllocatedQty) AS TotalAllocatedQty
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId" + TraceScopePredicate + @"
GROUP BY SupplyType
ORDER BY COUNT(*) DESC";

        var supplyTypeCounts = (await _connectionManager.QueryAsync<SupplyTypeCountDto>(
            supplyTypeSql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .ToList();

        // CommitmentStatus统计
        var commitmentSql = @"
SELECT
    ISNULL(CommitmentStatus, 'UNKNOWN') AS CommitmentStatus,
    COUNT(*) AS Count,
    SUM(AllocatedQty) AS TotalAllocatedQty
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId" + TraceScopePredicate + @"
GROUP BY CommitmentStatus
ORDER BY COUNT(*) DESC";

        var commitmentCounts = (await _connectionManager.QueryAsync<CommitmentStatusCountDto>(
            commitmentSql, summaryParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .ToList();

        // Q3（11-16 4号位矩阵回执 §四.Q3）：「源批缺」**追溯侧显式位**——判据=供给事实现批在否
        // （D05 定稿：IsActive=现批代理）。姿势注记：现批恒为最新插入段（实库 MIN(active Id)=5.35M、
        // 全表 85 万），**倒序 TOP 1=0ms**；正向 EXISTS 全扫 ≈217ms 禁用。探测失败= null（未判定），
        // 不把故障说成"没批次"（三态纪律，同 isMatched 口径）。
        bool? batchPresent = null;
        string? batchNo = null;
        try
        {
            batchNo = await _connectionManager.QueryFirstOrDefaultAsync<string>(
                @"SELECT TOP 1 BatchNo FROM SupplyFact_Pipeline WHERE IsActive = 1 ORDER BY Id DESC",
                null, CommandType.Text, DatabaseId.APS, commandTimeout: 5);
            batchPresent = batchNo is not null;
        }
        catch
        {
            // 保持 null：前端此态显示"未判定"，不落"源批缺"文案。
        }

        return new PeggingTraceSummaryDto
        {
            PlanVersionId = planVersionId,
            TotalAllocations = total,
            SupplyTypeCounts = supplyTypeCounts,
            CommitmentStatusCounts = commitmentCounts,
            SupplyBatchPresent = batchPresent,
            SupplyBatchNo = batchNo
        };
    }
}
