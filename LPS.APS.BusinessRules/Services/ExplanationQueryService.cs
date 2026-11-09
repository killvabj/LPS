using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// Explanation查询业务服务
///
/// 5号位提供给4号位的排程原因解释查询
/// 直接读取ScheduleExplanationFact表，不重新裁决延期原因
/// </summary>
public class ExplanationQueryService
{
    private readonly IExplanationQueryRepository _repository;

    public ExplanationQueryService(IExplanationQueryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 查询Explanation列表
    /// </summary>
    public async Task<List<ExplanationDto>> QueryAsync(
        int planVersionId,
        string? objectType = null,
        long? orderId = null,
        long? taskId = null,
        string? reasonCode = null,
        string? severity = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        // 越界夹上限不静默回落（4号位 10-19 R2 同型扫描批修）。
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        return await _repository.QueryAsync(
            planVersionId, objectType, orderId, taskId,
            reasonCode, severity, skip, take, ct);
    }

    /// <summary>
    /// 查询Explanation汇总
    /// </summary>
    public async Task<ExplanationSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");

        return await _repository.GetSummaryAsync(planVersionId, ct);
    }

    /// <summary>FULL-06 三族词表（大小写敏感，Ordinal；族外值 400 不静默过滤成空）。</summary>
    private static readonly HashSet<string> ReconciliationFamilies = new(StringComparer.Ordinal)
    {
        "UNSCHEDULED_TASK", "RECONCILIATION_REMAINDER", "RECONCILIATION_FULLY_SCHEDULED"
    };

    /// <summary>
    /// Demand 数量对账读模型（**FULL-06**，0号位 11-09 任务书 §2.A）：
    /// 同 Run／同 PlanVersion／同 Demand 身份的 Q/S/U 原值 + 事实类型 + 缺口标记；
    /// 只读投影、不重算（读侧 min/max 掩盖超排=违令，详见 <see cref="DemandReconciliationRowDto"/>）。
    /// </summary>
    public async Task<List<DemandReconciliationRowDto>> QueryDemandReconciliationAsync(
        int planVersionId,
        int? scheduleRunId = null,
        long? orderId = null,
        string? logicalDemandKey = null,
        string? unscheduledSource = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        if (planVersionId <= 0) throw new ArgumentException("planVersionId is required");
        if (unscheduledSource is not null && !ReconciliationFamilies.Contains(unscheduledSource))
            throw new ArgumentException(
                $"unscheduledSource 仅允许三族：UNSCHEDULED_TASK / RECONCILIATION_REMAINDER / RECONCILIATION_FULLY_SCHEDULED，收到：{unscheduledSource}");
        if (take <= 0) take = 100;
        if (take > 500) take = 500;   // 同 QueryAsync 上限惯例
        if (skip < 0) skip = 0;

        return await _repository.QueryDemandReconciliationAsync(
            planVersionId, scheduleRunId, orderId, logicalDemandKey, unscheduledSource, skip, take, ct);
    }
}
