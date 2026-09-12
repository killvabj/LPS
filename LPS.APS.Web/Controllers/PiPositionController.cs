using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// PI Position查询控制器（5号位提供给4号位）
///
/// 路由规范：
///   GET /api/pi-position?planVersionId=        - PI Position列表查询
///   GET /api/pi-position/summary?planVersionId= - PI Position汇总
///
/// 【职责边界】
/// - 5号位提供只读查询接口，直接读取ProductionInstructionPositionSnapshot表
/// - 不重算Position，不修改Snapshot
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/pi-position")]
public class PiPositionController : ControllerBase
{
    private readonly PiPositionQueryService _service;
    private readonly ILogger<PiPositionController> _logger;

    public PiPositionController(
        PiPositionQueryService service,
        ILogger<PiPositionController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 查询PI Position列表
    /// </summary>
    [HttpGet]
    public async Task<ApiResponse<List<PiPositionDto>>> Query(
        [FromQuery] int planVersionId,
        [FromQuery] string? productionInstructionNo = null,
        [FromQuery] string? materialCode = null,
        [FromQuery] string? positionType = null,
        [FromQuery] string? stageCode = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.QueryAsync(
                planVersionId, productionInstructionNo, materialCode,
                positionType, stageCode, skip, take, cancellationToken);

            return ApiResponse<List<PiPositionDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<PiPositionDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query PI positions");
            return ApiResponse<List<PiPositionDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询PI Position汇总
    /// </summary>
    [HttpGet("summary")]
    public async Task<ApiResponse<PiPositionSummaryDto>> GetSummary(
        [FromQuery] int planVersionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetSummaryAsync(planVersionId, cancellationToken);
            return ApiResponse<PiPositionSummaryDto>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<PiPositionSummaryDto>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PI position summary");
            return ApiResponse<PiPositionSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
