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
    /// 获取计划版本列表（最新优先，默认 30 条，按当前用户业务范围过滤）。
    /// R5（11-12 4号位件）两点增量、均不破坏现契约：
    /// ① 行内 `isDefault`=该行是其**域内**当前生效版本（ACTIVE；UQ 保证每域≤1；无全局唯一默认，
    ///    跨域页面默认请取 isDefault 行中 ActivatedAt 最新者）；
    /// ② 响应头 `X-Total-Count`=同 WHERE 全量条数（TOP take 截断显式化；不改变响应体结构）。
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("versions")]
    public async Task<ApiResponse<IReadOnlyList<PlanVersionSummaryDto>>> GetVersions(
        [FromQuery] int take = 30,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0 || take > 200) take = 30;

        var list = await _queryService.GetVersionsAsync(GetCurrentUserId(), take, cancellationToken);
        Response.Headers["X-Total-Count"] = (await _queryService.CountVersionsAsync(GetCurrentUserId(), cancellationToken)).ToString();
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
        [FromQuery] string? departmentIds = null,   // P04 H2：部门 Id 逗号多值（与 /tasks 同口径）
        [FromQuery] string? orderNo = null,          // P04 H2：订单号模糊（EXISTS 谓词，不扇出）
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

        var ganttDepts = string.IsNullOrWhiteSpace(departmentIds)
            ? null
            : departmentIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), out var v) ? v : -1).ToList();

        var data = await _queryService.GetGanttAsync(
            userId: GetCurrentUserId(), planVersionId: planVersionId, from: from, to: to,
            resourceIds: resourceIds, departmentIds: ganttDepts, orderNo: orderNo,
            limit: limit, afterResourceId: afterResourceId, afterStartAt: afterStartAt,
            afterTaskId: afterTaskId, cancellationToken: cancellationToken);
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
    /// P05 任务清单（11-16 矩阵 A4，5号位）：<c>GET /api/schedule/tasks/{planVersionId}</c>——
    /// 一行=一个真实 <c>Task.Id</c>（不按 TaskNo 折叠）；<c>total</c>=服务端全量命中、<c>items</c>=本页已加载，两数分开。
    /// 筛选：<c>from/to</c>（naive 墙钟窗，NULL 计划时刻恒保留——同甘特窗口件）、<c>resourceIds</c>（重复键；
    /// <c>resourceIds[]</c> 真 400）、<c>stageCodes</c>/<c>departmentIds</c>（逗号）、<c>keyword</c>（物料码/名/TaskNo 三路）、
    /// <c>status</c>（原值等值）、<c>hasWorkOrder</c>（三态：缺省=不过滤 true=有工单 false=无工单，**只是工单号列过滤，
    /// 不是"无需MES"分类**——分类权威未建，矩阵 Q-a）。
    /// 排序：<c>sortBy=plannedStart|quantity|materialCode|taskNo|id</c>（缺省 plannedStart）+ <c>sortDir=asc|desc</c>，非法⇒真 400。
    /// 分页 <c>pageIndex/pageSize</c>（缺省 50、钳 ≤200）。越权=200+<c>scopeDenied=true</c>；PV 不存在=<c>versionExists=false</c>。
    /// 域/部门 Scope 与甘特**同一套解析件**（受限账号两页口径恒一致）。
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("tasks/{planVersionId:int}")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.TaskListResultDto>> QueryTasks(
        int planVersionId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int[]? resourceIds = null,
        [FromQuery] string? stageCodes = null,
        [FromQuery] string? operationCodes = null,   // P05 H1（11-23 4号位件）：工序码逗号多值，与 stageCodes 同形
        [FromQuery] string? departmentIds = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? status = null,
        [FromQuery] bool? hasWorkOrder = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (Request.Query.ContainsKey("resourceIds[]") || Request.Query.ContainsKey("resourceIds[0]"))
        {
            throw new InvalidOperationException(
                "resourceIds 不支持数组下标写法（resourceIds[]），请使用重复键：?resourceIds=1&resourceIds=2");
        }

        var stages = string.IsNullOrWhiteSpace(stageCodes)
            ? null
            : stageCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var ops = string.IsNullOrWhiteSpace(operationCodes)
            ? null
            : operationCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        List<int>? depts = null;
        if (!string.IsNullOrWhiteSpace(departmentIds))
        {
            depts = departmentIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.Parse(x.Trim())).ToList();
        }

        var data = await _queryService.QueryTasksAsync(
            userId: GetCurrentUserId(), planVersionId: planVersionId, from: from, to: to,
            resourceIds: resourceIds, stageCodes: stages, operationCodes: ops, departmentIds: depts,
            keyword: keyword, status: status, hasWorkOrder: hasWorkOrder,
            sortBy: sortBy, sortDir: sortDir, pageIndex: pageIndex, pageSize: pageSize,
            cancellationToken: cancellationToken);
        return ApiResponse<LPS.APS.Core.Dto.TaskListResultDto>.Success(data);
    }

    /// <summary>
    /// P06 单 Task 详情（11-16 矩阵 A5）：<c>GET /api/schedule/task/{taskId}</c>——
    /// 列表行全字段 + 版本身份（versionCode/domainKey/planVersionStatus/dataCutoffTime）+
    /// 份额明细（AllocationTaskShare 同 PV 逐行，根订单号=同 PV [Order] 反查）。
    /// 共享 Task 多根订单**全量给**，页面不得贴单一订单为唯一需求来源；
    /// <c>found=false</c>=不存在；<c>scopeDenied=true</c>=域外/部门外（两态不混报）。
    /// 前后依赖边、MES 实际执行事实=未提供（挂 Q-a/Q-d，空位显式，不做推断）。
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("task/{taskId:long}")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.TaskDetailResultDto>> GetTaskDetail(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        var data = await _queryService.GetTaskDetailAsync(GetCurrentUserId(), taskId, cancellationToken);
        return ApiResponse<LPS.APS.Core.Dto.TaskDetailResultDto>.Success(data);
    }

    /// <summary>
    /// P12 资源负荷三率（11-17 矩阵 A6/A7；0号位 Q-b 已裁）：<c>GET /api/schedule/resource-load/{planVersionId}?from=&amp;to=&amp;resourceIds=</c>——
    /// 一行=**设备×业务日**。①日历负荷率=当日已排 Task 小时÷三级继承解析可用小时×100；②固定 16h 负荷率=当日已排÷16×100
    /// （恒可算，<b>不是</b>真实可用时间、不得代替①或当 MES 利用率）；③换型损失率=<b>暂不产出</b>——
    /// Setup 权威=1号位 Solver 落库产物、现无持久列 ⇒ <c>setupHoursAvailable=false</c>、③全表 null（Q-b 禁读时重匹配/禁 Duration 代）。
    /// 三态：<c>notApplicableReason</c> 非空=不适用（零合法可用小时/无日历/范围剔除三类文案，<b>禁显示 0</b>）；
    /// 有可用小时且当日无排产=① 0%（明确零，合法值）。跨午夜 Task 按日切片。<c>from/to 必填</c>（缺→真400）、窗≤31天、设备≤200；
    /// 不传 resourceIds=窗内有排产设备 TOP200（<c>resourcesTruncated=true</c> 须提示收窄）。域/部门 Scope 同甘特件；
    /// 越权=200+<c>scopeDenied</c>，PV 缺=<c>versionExists=false</c>。
    /// </summary>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("resource-load/{planVersionId:int}")]
    public async Task<ApiResponse<LPS.APS.Core.Dto.ResourceLoadResultDto>> GetResourceLoad(
        int planVersionId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int[]? resourceIds = null,
        CancellationToken cancellationToken = default)
    {
        if (Request.Query.ContainsKey("resourceIds[]") || Request.Query.ContainsKey("resourceIds[0]"))
        {
            throw new InvalidOperationException(
                "resourceIds 不支持数组下标写法（resourceIds[]），请使用重复键：?resourceIds=1&resourceIds=2");
        }

        var data = await _queryService.GetResourceLoadAsync(
            GetCurrentUserId(), planVersionId, from, to, resourceIds, cancellationToken);
        return ApiResponse<LPS.APS.Core.Dto.ResourceLoadResultDto>.Success(data);
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
