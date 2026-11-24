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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default);

    /// <summary>
    /// B2（11-17，0号位 Q-c 裁）：P02 交付 KPI 分区聚合——判定唯一复用 RiskCaseExpr（计划派生日期级，
    /// 非 summary 的 ERP DelayStatus 桶）；与列表同 WHERE+同 #tc；分母含未判定不剔除；
    /// FG/WIP 层无权威映射暂缺位（FgWipDimensionAvailable=false）、目标键待 3号位 结论（TargetPercent=null）。
    /// </summary>
    Task<DeliveryKpiResultDto> GetDeliveryKpiAsync(
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
        CancellationToken ct = default);

    /// <summary>
    /// B3/B4（11-17）：订单完整树——P02 行展开与 P03 深链**同一端点**（不建第二套）。
    /// 结构=2号位 Pegging 图上游闭包（深度≤10、路径防环），种子=Task.OrderId ∪ share.RootOrderId 两路；
    /// 数量账=PSA 行（Gap 后端单点算式）；Q/S/U 不在此重复（走 explanation/demand-reconciliation）；
    /// StageTimingNode=未提供注记（Q03）。三段独立截断位（edges 1500/nodes 500/demands 500 缺省）。
    /// </summary>
    Task<LPS.APS.Core.Dto.OrderTreeResultDto> GetOrderTreeAsync(
        int planVersionId,
        long orderId,
        string? keyword = null,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        int nodeLimit = 500,
        int edgeLimit = 1500,
        int demandLimit = 500,
        CancellationToken ct = default);

    /// <summary>
    /// C2（11-18）：P09 选单池——判据=v0.2 原文（ACTIVE PV 参与关系）：SCHEDULED（场景5/6）
    /// ∪ NOT_IN_PLAN（canonical∈、PV∉，场景1/2）；**禁 CreatedAt/前缀猜新老**。
    /// 新单归属估算链 物料→族→域（FACTORY_FAMILY 域叠厂码）；PV 非 ACTIVE/无归属⇒注记位。
    /// </summary>
    Task<LPS.APS.Core.Dto.OrderPoolResultDto> GetOrderPoolAsync(
        int planVersionId,
        string? participation = null,
        string? keyword = null,
        string? orderType = null,
        int pageIndex = 1,
        int pageSize = 50,
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
        string? orderType = null,
        string? materialType = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default);
}
