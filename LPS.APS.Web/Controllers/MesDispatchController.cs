using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Shared.Models;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// MES 受控操作下发接口（中转端点）。
/// 归属：**端点/受控操作中转层归 5号位**（冻结 06 v1.38 §7.4、05 v1.48:379,390-391、13 v1.8 §十：4号位 → 5号位 API → 权限校 → 2号位 Application）。
/// 5号位 职责：中转承载 + F-G4 业务范围（Factories）二次校验。
/// 权限码（MesControlledOperation）由 3号位 落；核心下发能力归 2号位，接入前返回 501，属预期。
/// </summary>
/// <remarks>骨架：3号位；归属归一（10-17）后中转承载：5号位；业务判定/下发：2号位</remarks>
[ApiController]
[Route("api/mes")]
public class MesDispatchController : ControllerBase
{
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<MesDispatchController> _logger;
    private readonly LPS.APS.BusinessRules.Repositories.IMesStatusRepository _statusRepository;

    public MesDispatchController(IDataScopeService dataScopeService, ILogger<MesDispatchController> logger,
        LPS.APS.BusinessRules.Repositories.IMesStatusRepository statusRepository)
    {
        _dataScopeService = dataScopeService;
        _logger = logger;
        _statusRepository = statusRepository;
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>
    /// P11 状态查询列表（**B6，11-17；Q-d 裁读面=5号位**）：`GET /api/mes/status?planVersionId=…`——
    /// 一行=一个 Stage 执行批（TaskNo）。**三层状态分列互不冒充**：
    /// 下发计数（TaskDispatch 原词表）／工单状态（MESWorkOrderSnapshot）／报工数量（OperationProgressSnapshot）。
    /// 筛选：`from/to`（计划窗，NULL 时刻恒保留=同甘特语义）、`stageCodes`/`departmentCodes`（逗号）、
    /// `piNo`、`workOrderNo`、`keyword`（物料码/TaskNo/PI 三路）、`dispatchStatus`（**批内含该态工序行**才命中，HAVING 级）。
    /// Scope：PV 域＋Factory＋Department 三重 fail-closed；越权=200+`scopeDenied`、PV 缺=`versionExists=false`。
    /// 顶层 `woSnapshotSourceUpdatedAt/opsSnapshotSourceUpdatedAt/dataCutoffTime`=快照同步面（"数据截至"列权威）；
    /// **无 dispatchUpdatedAt 面**——全表 NULL=消费环留痕缺口（Q-d 在案），不得据缺痕断言链未开。
    /// 分类位 `mesRequirementCategory` 恒 null（Q-a 未裁，禁由工单号空值反推"无需MES"）。
    /// </summary>
    // R1（3号位 11-21 裁甲；4号位 11-25 绑码完成）：P11 读面自 r14415 起挂 `aps.mes.view`
    // （原 aps.plan.view 系 B6 建面时的保守位；绑码未就绪前不得先改——防五角色断读，红线见 4号位 11-25 函 §四）。
    [Authorize(Policy = PermissionCodes.MesView)]
    [HttpGet("status")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.MesStatusListResultDto>> GetStatus(
        [FromQuery] int planVersionId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? stageCodes = null,
        [FromQuery] string? departmentCodes = null,
        [FromQuery] string? piNo = null,
        [FromQuery] string? workOrderNo = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? dispatchStatus = null,
        [FromQuery] string? resourceIds = null,        // P11 H3：设备 Id 逗号多值
        [FromQuery] string? workOrderStatuses = null,  // P11 H3：MES 工单状态逗号多值（批内任一工单命中）
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (planVersionId <= 0) throw new ArgumentException("planVersionId 必填");
            var stages = string.IsNullOrWhiteSpace(stageCodes)
                ? null
                : stageCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var depts = string.IsNullOrWhiteSpace(departmentCodes)
                ? null
                : departmentCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            var resIds = string.IsNullOrWhiteSpace(resourceIds)
                ? null
                : resourceIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim(), out var v) ? v : -1).ToList();
            var woSt = string.IsNullOrWhiteSpace(workOrderStatuses)
                ? null
                : workOrderStatuses.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
            var result = await _statusRepository.QueryAsync(
                planVersionId: planVersionId, from: from, to: to, stageCodes: stages,
                piNo: piNo, workOrderNo: workOrderNo, keyword: keyword, dispatchStatus: dispatchStatus,
                departmentCodes: depts, resourceIds: resIds, workOrderStatuses: woSt,
                allowedDomains: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Domain),
                allowedFactories: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Factory),
                allowedDepartments: scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Department),
                pageIndex: pageIndex, pageSize: pageSize, ct: cancellationToken);
            return ApiResponse<LPS.APS.Core.Dto.MesStatusListResultDto>.Success(result);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogError(ex, "Failed to query MES status");
            return ApiResponse<LPS.APS.Core.Dto.MesStatusListResultDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// P11 批详情（点行展开）：`GET /api/mes/status/batch?planVersionId=&amp;taskNo=`——
    /// 工序级下发行（FailureCode/Reason 原值）＋同 (Run,PI,Stage,OperationName) 报工列（缺行=null=未判定）。
    /// 批不存在/越权=200+`found=false`（不 404，同全链形状 B 纪律）。
    /// </summary>
    [Authorize(Policy = PermissionCodes.MesView)]   // R1 同批（列表与批详情同码）
    [HttpGet("status/batch")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.MesStatusBatchDetailDto>> GetStatusBatch(
        [FromQuery] int planVersionId,
        [FromQuery] string taskNo,
        CancellationToken cancellationToken = default)
    {
        if (planVersionId <= 0 || string.IsNullOrWhiteSpace(taskNo))
            throw new ArgumentException("planVersionId 与 taskNo 均必填");

        var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), cancellationToken);
        var result = await _statusRepository.GetBatchDetailAsync(
            planVersionId, taskNo,
            scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Domain),
            scope.IsGlobal ? null : scope.GetValues(DataScopeTypes.Factory),
            cancellationToken);
        return ApiResponse<LPS.APS.Core.Dto.MesStatusBatchDetailDto>.Success(result);
    }

    /// <summary>发起 MES 受控操作下发——中转：权限/Scope 校验 + 透传 2号位 判定，接入前返回 501（预期）</summary>
    /// <remarks>中转承载：5号位；判定/下发能力：2号位（接入前 501，前端不接线）</remarks>
    [Authorize(Policy = PermissionCodes.MesControlledOperation)]
    [HttpPost("dispatch")]
    public async Task<IActionResult> Dispatch([FromBody] MesDispatchRequest request, CancellationToken ct)
    {
        try
        {
            // F-G4 fail-closed：Factories ∋ request.FactoryCode（Global 放行）
            await _dataScopeService.EnsureInScopeAsync(GetCurrentUserId(), DataScopeTypes.Factory, request.FactoryCode, ct);
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "MES 下发业务范围越界：{FactoryCode}", request.FactoryCode);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }

        // 核心 MES 受控操作下发归 2号位，接入前返回 501
        _logger.LogInformation("MES 下发端点（骨架）被调用：{FactoryCode}，核心能力待接入", request.FactoryCode);
        return StatusCode(501, ApiResponse.Fail(501, "MES 受控操作下发核心能力由 2号位 接入，暂未开放"));
    }
}

/// <summary>MES 下发请求体</summary>
public sealed record MesDispatchRequest
{
    /// <summary>目标工厂代码</summary>
    [Required(ErrorMessage = "FactoryCode 必填")]
    public required string FactoryCode { get; init; }
}