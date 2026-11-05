using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// Overview查询Repository接口
/// </summary>
public interface IOverviewQueryRepository
{
    /// <summary>
    /// 查询当前ACTIVE计划版本信息（R1-a：按 Domain 列表，每 Domain 至多一条）
    /// </summary>
    Task<List<OverviewActivePlanDto>> GetActivePlanAsync(
        string? domainKey = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null);

    /// <summary>
    /// 查询资源负荷/Bottleneck
    /// </summary>
    Task<List<OverviewResourceBottleneckDto>> GetResourceBottleneckAsync(
        int planVersionId,
        int topN = 10,
        CancellationToken ct = default);

    /// <summary>
    /// 查询Candidate待处理摘要
    /// </summary>
    Task<OverviewCandidateSummaryDto> GetCandidateSummaryAsync(
        CancellationToken ct = default);

    /// <summary>
    /// 查询某计划版本实际涉及的对象级业务维度去重集合（R11-3b 三维 Scope 最小整改·对象级，0号位 裁决 v1.1）。
    /// 语义＝**对象级真实变更范围**（非域声明名义维度）：直接读该版本 <c>Order</c> 表取工厂/产品族、<c>Task</c> 表取生产部门，
    /// 供激活/发布前按"真实变更范围"复核（3号位 <c>RunLifecycleService</c> 对象级权限裁判）。
    /// 返回三个维度去重集合（Id 值，调用方经主档换算 Code 后逐值 EnsureInScope）：
    ///   <c>FactoryIds</c> —— Order.FactoryId；<c>ProductFamilyIds</c> —— Order.ProductFamilyId；
    ///   <c>DepartmentIds</c> —— Task.ProductionDepartmentId（v5.0 真实生产部门，**可空**：NULL 部门 Task 不入集，
    ///     其数量经 <c>NullDepartmentTaskCount</c> 回传，供调用方记 Warning，不静默）。
    /// 只读、幂等、无副作用；该版本无任何 Order/Task → 返回空集合（由调用方按"无对象可越权"放行）。
    /// </summary>
    Task<PlanVersionObjectScopeDimensions> GetPlanVersionObjectScopeDimensionsAsync(
        int planVersionId,
        CancellationToken ct = default);
}
