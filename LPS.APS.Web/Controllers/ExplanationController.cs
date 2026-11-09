using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// Explanation排程原因解释控制器（5号位提供给4号位）
///
/// 路由规范：
///   GET /api/explanation?planVersionId=           - Explanation列表查询
///   GET /api/explanation/summary?planVersionId=   - Explanation汇总
///
/// 【职责边界】
/// - 5号位提供只读查询接口，直接读取ScheduleExplanationFact表
/// - 不重新裁决延期原因
/// - 数据来源：1号位产出，2号位落盘
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/explanation")]
public class ExplanationController : ControllerBase
{
    private readonly ExplanationQueryService _service;
    private readonly ILogger<ExplanationController> _logger;

    public ExplanationController(
        ExplanationQueryService service,
        ILogger<ExplanationController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 查询Explanation列表
    /// </summary>
    [HttpGet]
    public async Task<ApiResponse<List<ExplanationDto>>> Query(
        [FromQuery] int planVersionId,
        [FromQuery] string? objectType = null,
        [FromQuery] long? orderId = null,
        [FromQuery] long? taskId = null,
        [FromQuery] string? reasonCode = null,
        [FromQuery] string? severity = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.QueryAsync(
                planVersionId, objectType, orderId, taskId,
                reasonCode, severity, skip, take, cancellationToken);

            return ApiResponse<List<ExplanationDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<ExplanationDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query explanations");
            return ApiResponse<List<ExplanationDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询Explanation汇总（含按ReasonCode分组统计）
    /// </summary>
    [HttpGet("summary")]
    public async Task<ApiResponse<ExplanationSummaryDto>> GetSummary(
        [FromQuery] int planVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetSummaryAsync(planVersionId, cancellationToken);
            return ApiResponse<ExplanationSummaryDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<ExplanationSummaryDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get explanation summary");
            return ApiResponse<ExplanationSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Demand 数量对账读模型（**FULL-06**，0号位 11-09 任务书 §2.A）：
    /// `GET /api/explanation/demand-reconciliation?planVersionId=…`
    /// 同 Run／同 PlanVersion／同 Demand 的 Q/S/U **原值**+事实类型三族（判据=UnscheduledSource，非 ObjectType）；
    /// 读侧不重算、不掩盖 S&gt;Q（211 行现值如实透出，修数=2号位 FULL-01）。
    /// </summary>
    [HttpGet("demand-reconciliation")]
    public async Task<ApiResponse<List<DemandReconciliationRowDto>>> GetDemandReconciliation(
        [FromQuery] int planVersionId,
        [FromQuery] int? scheduleRunId = null,
        [FromQuery] long? orderId = null,
        [FromQuery] string? logicalDemandKey = null,
        [FromQuery] string? unscheduledSource = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.QueryDemandReconciliationAsync(
                planVersionId, scheduleRunId, orderId, logicalDemandKey,
                unscheduledSource, skip, take, cancellationToken);

            return ApiResponse<List<DemandReconciliationRowDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<DemandReconciliationRowDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query demand reconciliation facts");
            return ApiResponse<List<DemandReconciliationRowDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
