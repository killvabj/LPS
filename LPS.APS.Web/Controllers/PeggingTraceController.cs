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
/// Supply/Pegging Trace供需追溯控制器（5号位提供给4号位）
///
/// 路由规范：
///   GET /api/pegging-trace?planVersionId=              - Pegging分配列表查询
///   GET /api/pegging-trace/order/{orderId}?planVersionId=  - 按订单查询Pegging分配
///   GET /api/pegging-trace/summary?planVersionId=      - Pegging Trace汇总
///
/// 【职责边界】
/// - 5号位提供只读查询接口，直接读取PeggingSupplyAllocation表
/// - 不重算Allocation，不修改分配结果
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/pegging-trace")]
public class PeggingTraceController : ControllerBase
{
    private readonly PeggingTraceService _service;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<PeggingTraceController> _logger;

    public PeggingTraceController(
        PeggingTraceService service,
        IDataScopeService dataScopeService,
        ILogger<PeggingTraceController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析当前登录用户 Id（无效返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>
    /// 查询Pegging分配列表。
    /// commitmentStatus 支持 'UNKNOWN'（=未标注，匹配 NULL，与汇总同口径）；
    /// ⚠️ orderNo 依赖 RootOrderNo/CurrentOrderNo——dev 现全 NULL（写入侧归 2号位），实际筛选用 <c>rootOrderId</c>（100% 非空）。
    /// </summary>
    [HttpGet]
    public async Task<ApiResponse<List<PeggingTraceDto>>> Query(
        [FromQuery] int planVersionId,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? supplyType = null,
        [FromQuery] string? commitmentStatus = null,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? supplyDocumentNo = null,
        [FromQuery] string? demandFactoryCode = null,     // P07 H1：逗号多值
        [FromQuery] string? supplyFactoryCode = null,     // P07 H1：逗号多值
        [FromQuery] string? supplyDocumentType = null,    // P07 H3：逗号多值（现库列全 NULL，见裁决函）
        [FromQuery] long? rootOrderId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);

            // 过滤：Pegging Trace 主对象 = Demand，Factory Scope = DemandFactoryCode（31-0 Q2 裁决）。
            // 空参按授权范围收窄结果集，避免越权返回全域数据。
            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);

            var result = await _service.QueryAsync(
                planVersionId: planVersionId, materialCode: materialCode, supplyType: supplyType,
                commitmentStatus: commitmentStatus,
                orderNo: orderNo, supplyDocumentNo: supplyDocumentNo, rootOrderId: rootOrderId,
                demandFactoryCode: demandFactoryCode, supplyFactoryCode: supplyFactoryCode,
                supplyDocumentType: supplyDocumentType,
                skip: skip, take: take, ct: cancellationToken,
                allowedFactories: allowedFactories);

            return ApiResponse<List<PeggingTraceDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<PeggingTraceDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query pegging trace");
            return ApiResponse<List<PeggingTraceDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 筛选后总数（**R2.1，11-14 4号位件**，形状 B）：`GET /api/pegging-trace/count?<与列表完全相同的筛选参数>`
    /// ⇒ `{ total }`。与列表**同一 WHERE 文本、同一数据范围**；前端零改动（多发一次并发请求即可）。
    /// </summary>
    [HttpGet("count")]
    public async Task<ApiResponse<PeggingTraceCountDto>> Count(
        [FromQuery] int planVersionId,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? supplyType = null,
        [FromQuery] string? commitmentStatus = null,
        [FromQuery] string? orderNo = null,
        [FromQuery] string? supplyDocumentNo = null,
        [FromQuery] string? demandFactoryCode = null,     // P07 H1：逗号多值
        [FromQuery] string? supplyFactoryCode = null,     // P07 H1：逗号多值
        [FromQuery] string? supplyDocumentType = null,    // P07 H3：逗号多值（现库列全 NULL，见裁决函）
        [FromQuery] long? rootOrderId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);

            var total = await _service.CountAsync(
                planVersionId, materialCode, supplyType, commitmentStatus,
                orderNo: orderNo, supplyDocumentNo: supplyDocumentNo, rootOrderId: rootOrderId,
                demandFactoryCode: demandFactoryCode, supplyFactoryCode: supplyFactoryCode,
                supplyDocumentType: supplyDocumentType,
                ct: cancellationToken, allowedFactories: allowedFactories);

            return ApiResponse<PeggingTraceCountDto>.Success(new PeggingTraceCountDto { Total = total });
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<PeggingTraceCountDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count pegging trace");
            return ApiResponse<PeggingTraceCountDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 按订单查询Pegging分配（11-14：补数据范围收窄，同列表单源——此前按 orderId 可绕 Scope）。
    /// </summary>
    [HttpGet("order/{orderId:long}")]
    public async Task<ApiResponse<List<PeggingTraceDto>>> QueryByOrder(
        long orderId,
        [FromQuery] int planVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);

            var result = await _service.QueryByOrderAsync(planVersionId, orderId, cancellationToken, allowedFactories);
            return ApiResponse<List<PeggingTraceDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<PeggingTraceDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query pegging trace by order");
            return ApiResponse<List<PeggingTraceDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询Pegging Trace汇总（**R2.4，11-14 裁定：收进数据范围**——与列表/计数同谓词，
    /// 受限账号不再看到全厂总数；前端"当前计划版本的全部笔数"文案改"当前账号可见笔数"）。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ApiResponse<PeggingTraceSummaryDto>> GetSummary(
        [FromQuery] int planVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var allowedFactories = scope.GetValues(DataScopeTypes.Factory);

            var result = await _service.GetSummaryAsync(planVersionId, cancellationToken, allowedFactories);
            return ApiResponse<PeggingTraceSummaryDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<PeggingTraceSummaryDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pegging trace summary");
            return ApiResponse<PeggingTraceSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
