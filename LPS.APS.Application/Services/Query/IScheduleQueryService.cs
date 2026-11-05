using LPS.APS.Application.Services.Query.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// 排程结果查询服务（3号位接口域职责 — §阶段5.3/第八部分场景1）
///
/// 用途：供 4号位前端甘特图拉取排程结果，不触碰推演期的内存沙盘
/// 数据来源：已落盘的 APS_Production 库（PlanVersion + Task + Resource + Order + Material）
///
/// F-G4 业务范围过滤：所有方法以 <paramref name="userId"/> 为入口，实现内部经 IDataScopeService 解析有效范围：
///   - 版本列表按 Domain/Factory 维度过滤（DomainKey）；
///   - 单版本读取按 DomainKey 做范围校验（越权返回空/视为不存在）；
///   - Global 放行；无任何范围拒绝全部（安全默认）。
///
/// V1 范围：
///   - 版本列表（供前端切换查看）
///   - 甘特图数据（按 PlanVersionId 返回 Task + Resource 明细）
///   - 排程概要（KPI：已排/未排/延期计数）
///   - 候选对比（candidate vs base 两个版本间的 Task 变化，v1.2 §三 H1）
///
/// V2 扩展点（预留接口但暂不实现）：
///   - 战报查询（ExplainTrace）
///   - 局部重排触发
/// </summary>
public interface IScheduleQueryService
{
    /// <summary>获取计划版本列表（最新优先，按当前用户业务范围过滤）</summary>
    Task<IReadOnlyList<PlanVersionSummaryDto>> GetVersionsAsync(
        int userId,
        int take = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定计划版本的甘特图数据（越权返回空，ScopeDenied=true）。
    /// 冻结 05:1013 要求「Gantt按时间窗/资源加载」，故支持可选时间窗与资源过滤。
    /// ⚠ 10-29 分页契约（4号位 三步走·第 1 步）：<b>任务条单次上限恒存在</b>——
    /// 不传 limit 亦按默认 8,000 截断返回（hasMore/totalTasks 显式声明），「不传=全量」旧语义终结；
    /// 全量导出不属于本端点职责。翻页=游标三元组回填（排序键全序，不漏不重）。
    /// </summary>
    /// <param name="from">时间窗起（含）。按任务区间相交过滤：PlannedEndTime ≥ from；无计划时间的任务始终保留。</param>
    /// <param name="to">时间窗止（含）。PlannedStartTime ≤ to；无计划时间的任务始终保留。</param>
    /// <param name="resourceIds">资源过滤（可选）。空/ null = 不过滤。</param>
    /// <param name="limit">单次任务条上限；null=8,000（缺省），钳制 1..20,000。</param>
    /// <param name="afterResourceId">游标·排序键1（上页 nextAfter.resourceId）；三参须成套传，缺一即视为首页。</param>
    /// <param name="afterStartAt">游标·排序键2（归一后时刻，NULL 起排传 1753-01-01）。</param>
    /// <param name="afterTaskId">游标·排序键3（Task Id）。</param>
    Task<GanttDataDto> GetGanttAsync(
        int userId,
        int planVersionId,
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<int>? resourceIds = null,
        int? limit = null,
        int? afterResourceId = null,
        DateTime? afterStartAt = null,
        long? afterTaskId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 甘特「资源×时间桶」聚合（三步走·第 2 步，2026-10-30 复函九条定稿）。
    /// 与明细体共用窗口谓词与资源口径；naive 不转时区；钳制归桶+未排桶 ⇒ Σ taskCount ≡ totalTasks。
    /// </summary>
    /// <param name="from">必填（缺 ⇒ InvalidOperationException→真400）。</param>
    /// <param name="to">必填。</param>
    /// <param name="bucket">day|week|month（缺省 day）；行数>50,000 自动降级并回显 bucketWidth/bucketDowngraded。</param>
    /// <param name="resourceIds">重复键写法，同明细体拦截。</param>
    Task<GanttBucketsDto> GetGanttBucketsAsync(
        int userId,
        int planVersionId,
        DateTime? from,
        DateTime? to,
        string? bucket = null,
        IReadOnlyList<int>? resourceIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>获取排程概要 KPI（已排 / 未排 / 延期数；越权返回空）</summary>
    Task<ScheduleSummaryDto> GetSummaryAsync(
        int userId,
        int planVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 候选 vs 基础 计划版本对比复合查询（H1；U10 候选对比页，v1.2 §三）
    /// 校验：两版本不存在抛 KeyNotFoundException（404）；候选状态非 CANDIDATE 抛 InvalidOperationException（400）；越权抛 KeyNotFoundException（404）
    /// 边界：estimatedOnlyCount 归 2号位 返回 null；crossDomain 计数未落盘保守返 0（v1.2 §九-b）；reasons[] 不在 v1.2 返回
    /// </summary>
    Task<CandidateComparisonDto> GetCandidateComparisonAsync(
        int userId,
        int candidatePlanVersionId,
        int basePlanVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 候选运行摘要（E：api/candidate/{candidateId}/summary；candidateId = ScheduleRun.Id）。
    /// 校验：候选 Run 不存在抛 KeyNotFoundException（404）；越权抛 KeyNotFoundException（404）。
    /// </summary>
    Task<CandidateSummaryDto> GetCandidateSummaryAsync(
        int userId,
        int candidateRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 候选 vs 基础差异（E：api/candidate/{candidateId}/comparison；最小集字段）。
    /// 校验同摘要；Run 无 BasePlanVersionId 抛 InvalidOperationException（400）。
    /// </summary>
    Task<CandidateDiffDto> GetCandidateDiffAsync(
        int userId,
        int candidateRunId,
        CancellationToken cancellationToken = default);
}
