using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// Explanation查询Repository接口
/// </summary>
public interface IExplanationQueryRepository
{
    /// <summary>
    /// 查询Explanation列表
    /// </summary>
    Task<List<ExplanationDto>> QueryAsync(
        int planVersionId,
        string? objectType = null,
        long? orderId = null,
        long? taskId = null,
        string? reasonCode = null,
        string? severity = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>
    /// 查询Explanation汇总（含按ReasonCode分组统计）
    /// </summary>
    Task<ExplanationSummaryDto> GetSummaryAsync(
        int planVersionId,
        CancellationToken ct = default);

    /// <summary>
    /// Demand 数量对账读模型（**FULL-06**）：按 `EvidenceJson.UnscheduledSource` 三族取 Q/S/U 原值，
    /// 不重算、不掩盖 S&gt;Q（详见 <see cref="DemandReconciliationRowDto"/> 类型注释）。
    /// </summary>
    Task<List<DemandReconciliationRowDto>> QueryDemandReconciliationAsync(
        int planVersionId,
        int? scheduleRunId = null,
        long? orderId = null,
        string? logicalDemandKey = null,
        string? unscheduledSource = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default);
}
