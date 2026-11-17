using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 订单查询Repository接口
/// </summary>
public interface IOrderQueryRepository
{
    /// <summary>
    /// 查询订单列表（分页 + total）
    /// </summary>
    Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        string? sortBy = null,
        string? sortDir = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null);

    /// <summary>
    /// 查询订单详情（含Pegging和生产计划）
    /// </summary>
    Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default);

    /// <summary>
    /// 查询订单汇总（R3，11-12：与列表同筛选参数+同 riskLevel 判定；全空参数=旧行为逐字一致）。
    /// </summary>
    Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default);

    /// <summary>
    /// 订单口径可选值+条数（R4，11-12：六维 GROUPING SETS 单扫，与列表同谓词同范围）。
    /// </summary>
    Task<OrderFacetsDto> GetFacetsAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default);
}
