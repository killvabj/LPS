using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// Pegging Trace查询Repository接口
/// </summary>
public interface IPeggingTraceRepository
{
    /// <summary>
    /// 查询Pegging分配列表
    /// </summary>
    Task<List<PeggingTraceDto>> QueryAsync(
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
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 筛选后总数（R2.1，11-14：与 <see cref="QueryAsync"/> 同一 WHERE 文本/同一参数对象；空范围集 fail-closed=0）。
    /// </summary>
    Task<int> CountAsync(
        int planVersionId,
        string? materialCode = null,
        string? supplyType = null,
        string? commitmentStatus = null,
        string? orderNo = null,
        string? supplyDocumentNo = null,
        long? rootOrderId = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 按订单查询Pegging分配（11-14：补数据范围谓词，同列表单源）。
    /// </summary>
    Task<List<PeggingTraceDto>> QueryByOrderAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 查询Pegging Trace汇总（11-14：三查全部收进数据范围，与列表/计数同谓词单源）。
    /// </summary>
    Task<PeggingTraceSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);
}
