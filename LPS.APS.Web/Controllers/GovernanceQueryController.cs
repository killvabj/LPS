using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 治理查询控制器（G4/G7：5号位业务接口接入层）
///
/// 路由规范：
///   GET /api/governance-query/run/{scheduleRunId}/scope - 单 Run 候选范围明细（G4；10-19 新增，4号位 提议路径已登记采纳）
///   GET /api/governance-query/domain-dependencies     - 排程域依赖（G7）
///   （注：原头注所列 GET runs **无实现**——QueryScheduleRunsAsync 死方法；10-19 与 4号位 复核一致后关闭该账面项，不补路由）
///
/// 【职责边界 - 2026-09-06审核确认】
/// - 普通结果查询：4 → 5 → 2（5号位中转，2号位运行真值）
/// - 治理动作：4 → 3 → 2（3号位治理，5号位不涉及）
/// - 5号位提供只读查询接口，不修改、不重算业务结果
/// - ScheduleRun状态由2号位运行收口产生，5号位只读展示
/// - 后续目标：G4/G7数据源从直读APS表迁移到2号位Query Service
/// </summary>
[Authorize(Policy = PermissionCodes.PlanView)]
[ApiController]
[Route("api/governance-query")]
public class GovernanceQueryController : ControllerBase
{
    private readonly GovernanceQueryService _service;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<GovernanceQueryController> _logger;
    private readonly LPS.APS.Engine.Data.DatabaseConnectionManager _connectionManager;

    public GovernanceQueryController(
        GovernanceQueryService service,
        IDataScopeService dataScopeService,
        ILogger<GovernanceQueryController> logger,
        LPS.APS.Engine.Data.DatabaseConnectionManager connectionManager)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    /// <summary>
    /// 治理侧仓库/工序码取值下拉（4号位 10-22 R2 建；10-25 Q1 补列）：
    /// ① **按 ProcessCode 归并去重**（`51305101` 视图两行⇒下拉一次；下游 PeggingOrchestrator:5822 亦按 MIN(StageCode) 归并）；
    /// ② 补 `erpProperty / warehouseRole / factoryCode` 三列（前端**展示用**，用于用户识别码的性质，**不据此过滤**——
    ///    冻结 07:1286 值域写 M/XC/ZP/BP，但实库另有 KS/BS/AC/ST/NULL 五类且前 6 大收货仓恰为 KS/BS，
    ///    值域漂移待 0号位 裁决，见 5号位 10-25 回执 Q1）；
    /// ③ **不按码长过滤**：ERP 6 位码与 MES 8 位码是两源互不相交的合法空间（PeggingOrchestrator:9470-9472）。
    /// 权限=类级 aps.plan.view（Q3 本轮维持现状）。
    /// </summary>
    [HttpGet("warehouses")]
    public async Task<ApiResponse<IReadOnlyList<GovernanceWarehouseItem>>> ListWarehouses(
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = @"
SELECT processcode              AS Code,
       MIN(processname)         AS Name,
       MIN(ERPProperty)         AS ErpProperty,
       MIN(WarehouseRole)       AS WarehouseRole,
       MIN(FactoryCode)         AS FactoryCode
FROM ext_MES_ProcessCode_View
WHERE (@Search IS NULL OR processcode LIKE '%' + @Search + '%' OR processname LIKE '%' + @Search + '%')
GROUP BY processcode
ORDER BY processcode";
            var items = await _connectionManager.QueryAsync<GovernanceWarehouseItem>(
                sql, new { Search = search }, System.Data.CommandType.Text,
                LPS.APS.Engine.Data.DatabaseId.APS, commandTimeout: 10);
            return ApiResponse<IReadOnlyList<GovernanceWarehouseItem>>.Success(items.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query governance warehouses");
            return ApiResponse<IReadOnlyList<GovernanceWarehouseItem>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>解析当前登录用户 Id（无效返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>
    /// 查询排程域依赖（G7）
    /// </summary>
    [HttpGet("domain-dependencies")]
    public async Task<ApiResponse<List<DomainDependencyDto>>> GetDomainDependencies(
        [FromQuery] string? domainCode = null,
        [FromQuery] string? direction = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);

            // 校验：非空入参必须落在授权范围（Global 全放行，无范围全拒绝）
            if (!string.IsNullOrEmpty(domainCode) && !scope.Allows(DataScopeTypes.Domain, domainCode))
                return ApiResponse<List<DomainDependencyDto>>.Fail(403, "Domain 范围越界");

            // 过滤：空参按授权范围收窄结果集，避免越权返回全域数据
            var allowedDomains = scope.GetValues(DataScopeTypes.Domain);

            var result = await _service.QueryDomainDependenciesAsync(
                domainCode, direction, cancellationToken, allowedDomains);
            return ApiResponse<List<DomainDependencyDto>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query domain dependencies");
            return ApiResponse<List<DomainDependencyDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 单 Run 候选范围明细（G4；候选五字段的明细面板数据源，13 v1.8 §五）。
    /// 语义分档（4号位 10-19 §二硬要求）：RunId 不存在 → 404（经全局 Filter 为真 HTTP 404）；
    /// Run 存在但无 ScopeJson（夹具/老 Run）→ **200 + scopeAvailable=false**（"老数据"与"单号打错"必须可分）。
    /// </summary>
    [HttpGet("run/{scheduleRunId:int}/scope")]
    public async Task<ApiResponse<RunScopeDto>> GetRunScope(
        int scheduleRunId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetRunScopeAsync(scheduleRunId, cancellationToken);
            return result is null
                ? ApiResponse<RunScopeDto>.Fail(404, $"ScheduleRun {scheduleRunId} 不存在")
                : ApiResponse<RunScopeDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query run scope: {RunId}", scheduleRunId);
            return ApiResponse<RunScopeDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }
}
