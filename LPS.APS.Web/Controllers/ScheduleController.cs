using System.Security.Claims;
using LPS.APS.Application.Services.Query;
using LPS.APS.Application.Services.Query.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 排程结果查询控制器（5号位中转，供4号位前端甘特图调用）
///
/// 路由规范：
///   GET /api/schedule/versions          - 计划版本列表
///   GET /api/schedule/gantt/{id}        - 指定版本的甘特图数据
///   GET /api/schedule/summary/{id}      - 指定版本的排程概要 KPI
///
/// 【职责边界】
/// - 5号位提供只读查询接口
/// - 数据来源：IScheduleQueryService（读取已落盘APS事实）
/// - V1不包含：触发重排/拖拽调整/解冻申请等写操作
/// F-G4：以当前登录用户 Id 为入口，由 IScheduleQueryService 内部解析业务范围并过滤（Domain/Factory 维度）。
/// V1 不包含：触发重排 / 拖拽调整 / 解冻申请 等写操作（后续阶段铺开）
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ScheduleController : ControllerBase
{
    private readonly IScheduleQueryService _queryService;
    private readonly ILogger<ScheduleController> _logger;

    public ScheduleController(
        IScheduleQueryService queryService,
        ILogger<ScheduleController> logger)
    {
        _queryService = queryService;
        _logger = logger;
    }

    /// <summary>
    /// 获取计划版本列表（最新优先，默认 30 条，按当前用户业务范围过滤）
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("versions")]
    public async Task<ApiResponse<IReadOnlyList<PlanVersionSummaryDto>>> GetVersions(
        [FromQuery] int take = 30,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0 || take > 200) take = 30;

        var list = await _queryService.GetVersionsAsync(GetCurrentUserId(), take, cancellationToken);
        return ApiResponse<IReadOnlyList<PlanVersionSummaryDto>>.Success(list);
    }

    /// <summary>
    /// 获取指定计划版本的甘特图数据（资源行 + 任务条；越权返回空且 ScopeDenied=true）。
    /// 冻结 05:1013「Gantt按时间窗/资源加载」：支持可选 from / to / resourceIds。
    /// <para>⚠️ <c>resourceIds</c> 只认<b>重复键</b>写法：<c>?resourceIds=1&amp;resourceIds=2</c>。
    /// 逗号形式（<c>1,2</c>）会被模型绑定拒绝（422）；<c>resourceIds[]=1</c> 形式会被静默忽略而返回全量，
    /// 故下方显式拦截，避免调用方误以为过滤生效（4号位 10-17 §3.2）。</para>
    /// <para>10-29 分页契约（4号位 三步走·第 1 步，5号位 复函定稿）：
    /// <c>limit</c> 缺省 8,000、硬上限 20,000——<b>不传亦按默认截断，"裸调=全量"旧语义终结</b>；
    /// 翻页=把响应 <c>nextAfter</c> 的 <c>afterStartAt/afterTaskId</c> 回填（11-06 时间序二元组；
    /// <c>afterResourceId</c> 仍可收到、被忽略——旧前端不需要同批改）。
    /// 响应新增 <c>limit/totalTasks/hasMore/nextAfter</c>；<c>totalTasks</c>=当前过滤口径数（非版本总数）。</para>
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("gantt/{planVersionId:int}")]
    public async Task<ApiResponse<GanttDataDto>> GetGantt(
        int planVersionId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int[]? resourceIds = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? afterResourceId = null,
        [FromQuery] DateTime? afterStartAt = null,
        [FromQuery] long? afterTaskId = null,
        CancellationToken cancellationToken = default)
    {
        // 显式拒绝 resourceIds[] 形式：模型绑定不认识该键，会静默当"未传"→ 返回全量（静默错数据）。
        // R2（4号位 10-19）：原 `return ApiResponse.Fail(400,…)` 只改信封 code、HTTP 恒 200（ApiResponse.Fail 已知形态，
        // 同 10-16 R4 domain-status 根因）——监控/网关/裸 HTTP 消费方会把失败记成成功。
        // 改抛 InvalidOperationException → GlobalExceptionFilter 统一映射为 **真 HTTP 400** + 信封(message+traceId)，
        // 与同端点逗号写法的真 422、domain-status 真 404 的 HTTP 语义对齐。
        if (Request.Query.ContainsKey("resourceIds[]") || Request.Query.ContainsKey("resourceIds[0]"))
        {
            throw new InvalidOperationException(
                "resourceIds 不支持数组下标写法（resourceIds[]），请使用重复键：?resourceIds=1&resourceIds=2");
        }

        var data = await _queryService.GetGanttAsync(
            GetCurrentUserId(), planVersionId, from, to, resourceIds,
            limit, afterResourceId, afterStartAt, afterTaskId, cancellationToken);
        return ApiResponse<GanttDataDto>.Success(data);
    }

    /// <summary>
    /// 甘特「资源×时间桶」聚合层（三步走·第 2 步；5号位 2026-10-30 复函九条口径定稿）。
    /// <c>from/to 必填</c>（缺→真 400）；<c>bucket=day|week|month</c>（缺省 day）；
    /// 行数>50,000 自动 day→week→month 降级（回显 <c>bucketWidth/bucketDowngraded</c>），月桶仍超→真 400 要求 resourceIds 收窄。
    /// 恒等式 <c>Σ items.taskCount == totalTasks</c>（钳制归桶+未排桶）；未排桶行 <c>bucketStart</c> 整键省略；
    /// 不存在/越权沿用 200+flag（versionExists/scopeDenied）与明细体同惯例。
    /// <c>resourceIds</c> 与明细体同款拦截：只认重复键，<c>resourceIds[]</c> 真 400。
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("gantt/{planVersionId:int}/buckets")]
    public async Task<ApiResponse<GanttBucketsDto>> GetGanttBuckets(
        int planVersionId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? bucket = null,
        [FromQuery] int[]? resourceIds = null,
        CancellationToken cancellationToken = default)
    {
        // 与明细体同款：[] 下标写法模型绑定会静默当"未传"→ 返回全资源（静默错数据），显式拦截
        if (Request.Query.ContainsKey("resourceIds[]") || Request.Query.ContainsKey("resourceIds[0]"))
        {
            throw new InvalidOperationException(
                "resourceIds 不支持数组下标写法（resourceIds[]），请使用重复键：?resourceIds=1&resourceIds=2");
        }

        var data = await _queryService.GetGanttBucketsAsync(
            GetCurrentUserId(), planVersionId, from, to, bucket, resourceIds, cancellationToken);
        return ApiResponse<GanttBucketsDto>.Success(data);
    }

    /// <summary>
    /// 获取指定计划版本的排程概要 KPI（越权返回空）
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("summary/{planVersionId:int}")]
    public async Task<ApiResponse<ScheduleSummaryDto>> GetSummary(
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        var summary = await _queryService.GetSummaryAsync(GetCurrentUserId(), planVersionId, cancellationToken);
        return ApiResponse<ScheduleSummaryDto>.Success(summary);
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;
}
