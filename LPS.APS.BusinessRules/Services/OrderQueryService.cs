using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// 订单查询业务服务
///
/// 5号位提供给4号位的订单/需求计划查询
/// 直接读取APS_Production事实表，不重算Pegging
/// </summary>
public class OrderQueryService
{
    private readonly IOrderQueryRepository _repository;

    public OrderQueryService(IOrderQueryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>R1（11-12 4号位件）：sortBy 白名单（OrdinalIgnoreCase；空=缺省排序原样）。</summary>
    private static readonly HashSet<string> SortByWhitelist = new(StringComparer.OrdinalIgnoreCase)
        { "dueDate", "priority", "orderNo", "riskLevel", "quantity" };

    /// <summary>R2：riskLevel 筛选域——07 §八.2 三值 + 'UNSCHEDULED'（仅筛选名：列表缺键行的显式写法，响应值永不含）。</summary>
    private static readonly HashSet<string> RiskLevelFilterWhitelist = new(StringComparer.Ordinal)
        { "ON_TRACK", "DELAYED", "AT_RISK", "UNSCHEDULED" };

    /// <summary>非法值一律 400（ArgumentException→控制器映射），**绝不静默忽略**（4号位 R2 实测教训：假参数返回全量）。</summary>
    private static void ValidateSortAndRisk(string? riskLevel, string? sortBy, string? sortDir)
    {
        if (!string.IsNullOrWhiteSpace(riskLevel) && !RiskLevelFilterWhitelist.Contains(riskLevel))
            throw new ArgumentException($"非法 riskLevel：{riskLevel}（允许：ON_TRACK/DELAYED/AT_RISK/UNSCHEDULED 或不传；AT_RISK 当前无源恒 0 行）");
        if (!string.IsNullOrWhiteSpace(sortBy) && !SortByWhitelist.Contains(sortBy))
            throw new ArgumentException($"非法 sortBy：{sortBy}（允许：dueDate/priority/orderNo/riskLevel/quantity 或不传=缺省排序）");
        if (!string.IsNullOrWhiteSpace(sortDir)
            && !sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase)
            && !sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"非法 sortDir：{sortDir}（允许：asc/desc 或不传=asc）");
    }

    /// <summary>
    /// 查询订单列表（R1/R2，11-12：+sortBy/sortDir/riskLevel 三参；缺省排序与现行为逐字一致）
    /// </summary>
    public async Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        string? sortBy = null,
        string? sortDir = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        // take 越界不再静默回落 50（同类静默错数据；4号位 10-17 R3 在 PI 端点发现）。
        // 非正数 → 默认 50；超上限 → 夹到 200。
        if (take <= 0) take = 50;
        if (take > 200) take = 200;
        ValidateSortAndRisk(riskLevel, sortBy, sortDir);

        return await _repository.QueryOrdersAsync(
            planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
            factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
            productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
            orderType: orderType, materialType: materialType,
            dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
            riskLevel: riskLevel, sortBy: sortBy, sortDir: sortDir,
            skip: skip, take: take, ct: ct, allowedFactories: allowedFactories, allowedDomains: allowedDomains);
    }

    /// <summary>
    /// 查询订单详情（含Pegging和生产计划）
    /// </summary>
    public async Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (orderId <= 0) throw new ArgumentException("orderId is required");

        return await _repository.GetOrderDetailAsync(planVersionId, orderId, ct);
    }

    /// <summary>
    /// 查询订单汇总（R3，11-12）：与列表同名同义筛选参数+同 riskLevel 判定；
    /// **全部筛选不传=与旧行为逐字一致**（总览页共用本端点，兼容基线 999472=0/0/970、999464=10,917/50,950/47,554 已预跑对照）。
    /// </summary>
    public async Task<OrderSummaryDto> GetSummaryAsync(
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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (!string.IsNullOrWhiteSpace(riskLevel) && !RiskLevelFilterWhitelist.Contains(riskLevel))
            throw new ArgumentException($"非法 riskLevel：{riskLevel}（允许：ON_TRACK/DELAYED/AT_RISK/UNSCHEDULED 或不传）");

        return await _repository.GetSummaryAsync(
            planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
            factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
            productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
            orderType: orderType, materialType: materialType,
            dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
            riskLevel: riskLevel, allowedFactories: allowedFactories, allowedDomains: allowedDomains, ct: ct);
    }

    /// <summary>
    /// B2（11-17，0号位 Q-c 裁）：P02 交付 KPI 分区聚合——校验与 summary 同规（planVersionId 必填、
    /// riskLevel 白名单），参数语义=列表同名同义（统计随筛选走，点卡筛回同一集合）。
    /// </summary>
    public async Task<DeliveryKpiResultDto> GetDeliveryKpiAsync(
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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (!string.IsNullOrWhiteSpace(riskLevel) && !RiskLevelFilterWhitelist.Contains(riskLevel))
            throw new ArgumentException($"非法 riskLevel：{riskLevel}（允许：ON_TRACK/DELAYED/AT_RISK/UNSCHEDULED 或不传）");

        return await _repository.GetDeliveryKpiAsync(
            planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
            factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
            productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
            orderType: orderType, materialType: materialType,
            dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
            riskLevel: riskLevel, allowedFactories: allowedFactories, allowedDomains: allowedDomains, ct: ct);
    }

    /// <summary>
    /// B3/B4（11-17）：订单完整树（P02 展开=P03 深链同一出口）。参数校验=家族同规（必填⇒ArgumentException 真400）。
    /// </summary>
    public async Task<LPS.APS.Core.Dto.OrderTreeResultDto> GetOrderTreeAsync(
        int planVersionId,
        long orderId,
        string? keyword = null,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId 必填");
        if (orderId <= 0) throw new ArgumentException("orderId 必填（[Order].Id 根订单身份）");
        return await _repository.GetOrderTreeAsync(planVersionId: planVersionId, orderId: orderId, keyword: keyword, allowedDomains: allowedDomains, allowedFactories: allowedFactories, ct: ct);
    }

    /// <summary>
    /// C2（11-18）：P09 选单池（参数校验同家族：participation 白名单在仓储抛⇒真400）。
    /// </summary>
    public async Task<LPS.APS.Core.Dto.OrderPoolResultDto> GetOrderPoolAsync(
        int planVersionId,
        string? participation = null,
        string? keyword = null,
        string? orderType = null,
        int pageIndex = 1,
        int pageSize = 50,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId 必填");
        return await _repository.GetOrderPoolAsync(planVersionId, participation, keyword, orderType,
            pageIndex, pageSize, allowedDomains, ct);
    }

    /// <summary>
    /// 订单口径可选值+条数（R4，11-12 新增只读端点）：与列表同筛选同范围；
    /// 六维（customerName/factoryCode/productFamilyCode/status/delayStatus/riskLevel）各按条数降序、截 200。
    /// </summary>
    public async Task<OrderFacetsDto> GetFacetsAsync(
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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (!string.IsNullOrWhiteSpace(riskLevel) && !RiskLevelFilterWhitelist.Contains(riskLevel))
            throw new ArgumentException($"非法 riskLevel：{riskLevel}");

        return await _repository.GetFacetsAsync(
            planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
            factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
            productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
            orderType: orderType, materialType: materialType,
            dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
            riskLevel: riskLevel, allowedFactories: allowedFactories, allowedDomains: allowedDomains, ct: ct);
    }
}
