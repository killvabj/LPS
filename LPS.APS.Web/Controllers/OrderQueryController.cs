using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 订单/需求计划查询控制器（5号位提供给4号位）
///
/// 路由规范：
///   GET /api/order-query?planVersionId=             - 订单列表查询
///   GET /api/order-query/{orderId}?planVersionId=   - 订单详情（含Pegging+生产计划）
///
/// 【职责边界】
/// - 5号位提供只读查询接口，直接读取APS事实表
/// - 不重算Pegging，不修改订单状态
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/order-query")]
public class OrderQueryController : ControllerBase
{
    private readonly OrderQueryService _service;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<OrderQueryController> _logger;

    public OrderQueryController(
        OrderQueryService service,
        IDataScopeService dataScopeService,
        ILogger<OrderQueryController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析当前登录用户 Id（无效返回 0 → 范围解析为拒绝全部，安全默认）</summary>
        private static List<string>? ParseCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>
    /// 查询订单列表
    /// </summary>
    [HttpGet]
    public async Task<ApiResponse<PageResult<OrderListItemDto>>> QueryOrders(
        [FromQuery] int planVersionId,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? customerName = null,
        [FromQuery] string? factoryCode = null,
        [FromQuery] string? domainKey = null,
        [FromQuery] string? delayStatus = null,
        [FromQuery] string? status = null,
        [FromQuery] string? productionInstructionNo = null,
        [FromQuery] string? productFamilyCode = null,
        [FromQuery] string? orderType = null,        // A5：PRODUCTION_INSTRUCTION/SALES_ORDER（缺省不过滤）
        [FromQuery] string? materialType = null,     // A5：标准 MaterialType 原值（缺省不过滤）
        [FromQuery] string? departmentCode = null,   // OR3 丙：执行部门 DeptCode 逗号多值（筛选，不收窄可见性）
        [FromQuery] DateTime? dueDateFrom = null,
        [FromQuery] DateTime? dueDateTo = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] int pageIndex = 0,
        [FromQuery] int pageSize = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 分页换算：兼容前端 pageIndex/pageSize（1 起）与后端 skip/take（0 起）。
            // pageIndex/pageSize 优先；均未传时回退 skip/take 默认。
            if (pageSize > 0) take = pageSize;
            if (pageIndex > 0)
            {
                skip = (pageIndex - 1) * take;
            }
            if (skip < 0) skip = 0;
            if (take < 1) take = 50;

            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);

            // 校验：非空入参必须落在授权范围（Global 全放行，无范围全拒绝）
            if (!string.IsNullOrEmpty(factoryCode) && !scope.Allows(DataScopeTypes.Factory, factoryCode))
                return ApiResponse<PageResult<OrderListItemDto>>.Fail(403, "Factory 范围越界");
            if (!string.IsNullOrEmpty(domainKey) && !scope.Allows(DataScopeTypes.Domain, domainKey))
                return ApiResponse<PageResult<OrderListItemDto>>.Fail(403, "Domain 范围越界");

            // 过滤：空参按授权范围收窄结果集，避免越权返回全域数据
            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);
            var allowedDomains = scope.GetValues(DataScopeTypes.Domain);

            var result = await _service.QueryOrdersAsync(
                planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
                factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
                productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
                orderType: orderType, materialType: materialType, execDepartmentCodes: ParseCsv(departmentCode),
                dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
                riskLevel: riskLevel, sortBy: sortBy, sortDir: sortDir,
                skip: skip, take: take, ct: cancellationToken,
                allowedFactories: allowedFactories, allowedDomains: allowedDomains);

            return ApiResponse<PageResult<OrderListItemDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<PageResult<OrderListItemDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query orders");
            return ApiResponse<PageResult<OrderListItemDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询订单详情（含Pegging承接+生产计划）
    /// </summary>
    [HttpGet("{orderId:long}")]
    public async Task<ApiResponse<OrderDetailDto>> GetOrderDetail(
        long orderId,
        [FromQuery] int planVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetOrderDetailAsync(planVersionId, orderId, cancellationToken);
            if (result == null)
                return ApiResponse<OrderDetailDto>.Fail(404, "Order not found");

            return ApiResponse<OrderDetailDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<OrderDetailDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order detail");
            return ApiResponse<OrderDetailDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 订单完整树（**B3/B4，11-17**）：`GET /api/order-query/{orderId}/tree?planVersionId=…`——
    /// P02 行展开与 P03 独立深链**共用本端点**（同一树同一数据同一 PV，v0.2 §5 禁第二套）。
    /// 三段：①身份头（域/PV 状态/cutoff/交期）；②**结构**=nodes+edges（Pegging 图上游闭包，深度≤10 路径防环；
    /// 种子=Task.OrderId 直挂 ∪ share.RootOrderId 两路；节点带 InSeed/OwnRootOrder/MinDepth，前端按 parent→child 组树）；
    /// ③**数量账**=demands（PSA 行：DemandQty/AllocatedQty/Gap＋供给段＋Stage 链三列＋RemainingStagePathJson 原文）。
    /// 截断三态独立：<c>nodesTruncated/edgesTruncated/demandsTruncated</c>——页面按"已加载 N/截断"如实标注（v0.2 §P02.2）。
    /// 边界：Q/S/U 内制账**不在本响应**（走 <c>GET /api/explanation/demand-reconciliation</c>，两本账禁互加）；
    /// <c>stageTimingNote</c> 恒"未提供"（Q03 待 2号位，禁前端造节点）；NeedBy 列 PSA 不存在=如实缺位。
    /// 越权/不存在分态：<c>scopeDenied=true</c>（域外/工厂外，403 语义走信封位不吞 404）vs <c>found=false</c>。
    /// </summary>
    [HttpGet("{orderId:long}/tree")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.OrderTreeResultDto>> GetOrderTree(
        long orderId,
        [FromQuery] int planVersionId,
        [FromQuery] string? keyword = null,   // P03 H2：深层检索关键词（闭包全量六路 LIKE）
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (planVersionId <= 0) throw new ArgumentException("planVersionId 必填");
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var result = await _service.GetOrderTreeAsync(
                planVersionId: planVersionId, orderId: orderId, keyword: keyword,
                allowedDomains: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Domain),
                allowedFactories: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Factory),
                ct: cancellationToken);
            return ApiResponse<LPS.APS.Core.Dto.OrderTreeResultDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            throw;   // GlobalExceptionFilter ⇒ 真 400（信封 200 假 400 形态禁用，10-16 R4 教训）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order tree");
            return ApiResponse<LPS.APS.Core.Dto.OrderTreeResultDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// P09 选单池（**C2，11-18**）：`GET /api/order-query/pool?planVersionId=&amp;participation=&amp;keyword=&amp;orderType=&amp;pageIndex=&amp;pageSize=`——
    /// 判据=v0.2 §P09 原文「当前真实 FULL/ACTIVE＋合法根订单参与关系」的具体化：<b>participation</b>=
    /// `all`（缺省）/`scheduled`（在当前 ACTIVE PV 装载集=老订单，场景5/6 候选）/`fresh`（在 Order_Canonical、不在 PV=新订单，场景1/2 候选）。
    /// **禁 CreatedAt/单号前缀猜新老**（v0.2 点名禁令）——本端点即服务端可证判据，前端不再自算。
    /// 新单归属估算链=物料→族→域（FACTORY_FAMILY 域叠厂码；999473 实测 PV 载体 10,247 行 100% 同族反证）；
    /// `freshScopeBasis` 位常驻（Q10 契约化后更新）。行含 `participation/hasPlanningTasks/scheduledTime`
    /// （装载时刻≠承诺/首次关注时刻，D05-08 注记）；新单行 `orderId=null`（身份=OrderNo，canonical 严格一行/单号 D05-12）。
    /// PV 非 ACTIVE⇒注记位 `noActiveNote`（选单以 ACTIVE 为参照）；域越权=`scopeDenied`。场景10 整域重排不走选单（本端点与之无关）。
    /// </summary>
    [HttpGet("pool")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.OrderPoolResultDto>> GetOrderPool(
        [FromQuery] int planVersionId,
        [FromQuery] string? participation = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? orderType = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (planVersionId <= 0) throw new ArgumentException("planVersionId 必填");
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var result = await _service.GetOrderPoolAsync(
                planVersionId: planVersionId, participation: participation, keyword: keyword,
                orderType: orderType, pageIndex: pageIndex, pageSize: pageSize,
                allowedDomains: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Domain),
                allowedFactories: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Factory),
                ct: cancellationToken);
            return ApiResponse<LPS.APS.Core.Dto.OrderPoolResultDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            throw;   // 真 400（同家族纪律）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order pool");
            return ApiResponse<LPS.APS.Core.Dto.OrderPoolResultDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询订单汇总 KPI（供 Order.vue 顶部；R3，11-12 4号位件：**接受与列表同名同义的全部筛选参数**，
    /// 谓词与列表类级单源 ⇒ KPI 与列表 total 同口径；**不传筛选=与旧行为逐字一致**，总览页共用不受影响）。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ApiResponse<OrderSummaryDto>> GetSummary(
        [FromQuery] int planVersionId,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? customerName = null,
        [FromQuery] string? factoryCode = null,
        [FromQuery] string? domainKey = null,
        [FromQuery] string? delayStatus = null,
        [FromQuery] string? status = null,
        [FromQuery] string? productionInstructionNo = null,
        [FromQuery] string? productFamilyCode = null,
        [FromQuery] string? orderType = null,        // A5：PRODUCTION_INSTRUCTION/SALES_ORDER（缺省不过滤）
        [FromQuery] string? materialType = null,     // A5：标准 MaterialType 原值（缺省不过滤）
        [FromQuery] string? departmentCode = null,   // OR3 丙：执行部门 DeptCode 逗号多值（筛选，不收窄可见性）
        [FromQuery] DateTime? dueDateFrom = null,
        [FromQuery] DateTime? dueDateTo = null,
        [FromQuery] string? riskLevel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);

            // 与列表同款越权校验（筛选参数同样不得越界）。
            if (!string.IsNullOrEmpty(factoryCode) && !scope.Allows(DataScopeTypes.Factory, factoryCode))
                return ApiResponse<OrderSummaryDto>.Fail(403, "Factory 范围越界");
            if (!string.IsNullOrEmpty(domainKey) && !scope.Allows(DataScopeTypes.Domain, domainKey))
                return ApiResponse<OrderSummaryDto>.Fail(403, "Domain 范围越界");

            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);
            var allowedDomains = scope.GetValues(DataScopeTypes.Domain);

            var result = await _service.GetSummaryAsync(
                planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
                factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
                productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
                orderType: orderType, materialType: materialType, execDepartmentCodes: ParseCsv(departmentCode),
                dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
                riskLevel: riskLevel, allowedFactories: allowedFactories, allowedDomains: allowedDomains, ct: cancellationToken);

            return ApiResponse<OrderSummaryDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<OrderSummaryDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order summary");
            return ApiResponse<OrderSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// P02 交付 KPI（**B2，11-17；0号位 Q-c 裁后读模型**）：`GET /api/order-query/delivery-kpi?planVersionId=…`——
    /// 与列表/summary **同名同义全部筛选参数**（统计随筛选走、点卡筛回同一集合）＋同 Scope 越权校验（403 同规）。
    /// 行=分区：总计行（orderType=null）＋ SALES_ORDER ＋ PRODUCTION_INSTRUCTION 各一（GROUPING SETS 单扫）。
    /// **语义别混读三件事**：①判定=计划派生日期级（当天完成=按期），**不是** summary 的 ERP `DelayStatus` 桶、
    /// 不是"实际交付准交率"；②分母=去重根订单**含未判定**（onTime＋late＋undetermined=total 恒成立）；
    /// ③`plannedCompletionSource=readmodel_task_max`（2号位 正式列落库后切 formal）——页面须带 `caliber` 文案展示。
    /// FG/WIP 分区当前缺源（`fgWipDimensionAvailable=false`）、目标值 `targetPercent=null`（待 3号位 合同核对）。
    /// </summary>
    [HttpGet("delivery-kpi")]
    public async Task<ApiResponse<DeliveryKpiResultDto>> GetDeliveryKpi(
        [FromQuery] int planVersionId,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? customerName = null,
        [FromQuery] string? factoryCode = null,
        [FromQuery] string? domainKey = null,
        [FromQuery] string? delayStatus = null,
        [FromQuery] string? status = null,
        [FromQuery] string? productionInstructionNo = null,
        [FromQuery] string? productFamilyCode = null,
        [FromQuery] string? orderType = null,        // A5：PRODUCTION_INSTRUCTION/SALES_ORDER（缺省不过滤）
        [FromQuery] string? materialType = null,     // A5：标准 MaterialType 原值（缺省不过滤）
        [FromQuery] string? departmentCode = null,   // OR3 丙：执行部门 DeptCode 逗号多值（筛选，不收窄可见性）
        [FromQuery] DateTime? dueDateFrom = null,
        [FromQuery] DateTime? dueDateTo = null,
        [FromQuery] string? riskLevel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            if (!string.IsNullOrEmpty(factoryCode) && !scope.Allows(DataScopeTypes.Factory, factoryCode))
                return ApiResponse<DeliveryKpiResultDto>.Fail(403, "Factory 范围越界");
            if (!string.IsNullOrEmpty(domainKey) && !scope.Allows(DataScopeTypes.Domain, domainKey))
                return ApiResponse<DeliveryKpiResultDto>.Fail(403, "Domain 范围越界");

            var result = await _service.GetDeliveryKpiAsync(
                planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
                factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
                productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
                orderType: orderType, materialType: materialType, execDepartmentCodes: ParseCsv(departmentCode),
                dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
                riskLevel: riskLevel, allowedFactories: scope.GetValues(DataScopeTypes.Factory),
                allowedDomains: scope.GetValues(DataScopeTypes.Domain), ct: cancellationToken);

            return ApiResponse<DeliveryKpiResultDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<DeliveryKpiResultDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get delivery kpi");
            return ApiResponse<DeliveryKpiResultDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 订单口径可选值+条数（R4，11-12 新增）：`GET /api/order-query/facets?planVersionId=…`，
    /// 与列表同筛选参数（不含排序）+同范围收窄；六维各 {value,count} 按 count 降序截 200。
    /// 工厂/产品族中文名请继续用 3号位 governance 端点，本端点只出订单口径的编码+条数。
    /// </summary>
    [HttpGet("facets")]
    public async Task<ApiResponse<OrderFacetsDto>> GetFacets(
        [FromQuery] int planVersionId,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? customerName = null,
        [FromQuery] string? factoryCode = null,
        [FromQuery] string? domainKey = null,
        [FromQuery] string? delayStatus = null,
        [FromQuery] string? status = null,
        [FromQuery] string? productionInstructionNo = null,
        [FromQuery] string? productFamilyCode = null,
        [FromQuery] string? orderType = null,        // A5：PRODUCTION_INSTRUCTION/SALES_ORDER（缺省不过滤）
        [FromQuery] string? materialType = null,     // A5：标准 MaterialType 原值（缺省不过滤）
        [FromQuery] string? departmentCode = null,   // OR3 丙：执行部门 DeptCode 逗号多值（筛选，不收窄可见性）
        [FromQuery] DateTime? dueDateFrom = null,
        [FromQuery] DateTime? dueDateTo = null,
        [FromQuery] string? riskLevel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            if (!string.IsNullOrEmpty(factoryCode) && !scope.Allows(DataScopeTypes.Factory, factoryCode))
                return ApiResponse<OrderFacetsDto>.Fail(403, "Factory 范围越界");
            if (!string.IsNullOrEmpty(domainKey) && !scope.Allows(DataScopeTypes.Domain, domainKey))
                return ApiResponse<OrderFacetsDto>.Fail(403, "Domain 范围越界");

            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);
            var allowedDomains = scope.GetValues(DataScopeTypes.Domain);

            var result = await _service.GetFacetsAsync(
                planVersionId: planVersionId, orderNo: orderNo, materialCode: materialCode, customerName: customerName,
                factoryCode: factoryCode, domainKey: domainKey, delayStatus: delayStatus, status: status,
                productionInstructionNo: productionInstructionNo, productFamilyCode: productFamilyCode,
                orderType: orderType, materialType: materialType, execDepartmentCodes: ParseCsv(departmentCode),
                dueDateFrom: dueDateFrom, dueDateTo: dueDateTo,
                riskLevel: riskLevel, allowedFactories: allowedFactories, allowedDomains: allowedDomains, ct: cancellationToken);

            return ApiResponse<OrderFacetsDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<OrderFacetsDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order facets");
            return ApiResponse<OrderFacetsDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
