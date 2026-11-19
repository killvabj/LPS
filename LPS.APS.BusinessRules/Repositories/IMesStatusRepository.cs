using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// P11 状态查询读面（B6，11-17；Q-d 裁 5号位 职责）。
/// 数据面：TaskDispatch（APS 下发，5号位 入队/2号位 消费）＋ MESWorkOrderSnapshot ＋
/// OperationProgressSnapshot（2号位 随批快照，本仓储**只读**）。
/// </summary>
public interface IMesStatusRepository
{
    /// <summary>批列表（一行=TaskNo 执行批；三段状态分列；批级 COUNT 与列表同 WHERE 单源）。</summary>
    Task<MesStatusListResultDto> QueryAsync(
        int planVersionId,
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<string>? stageCodes = null,
        string? piNo = null,
        string? workOrderNo = null,
        string? keyword = null,
        string? dispatchStatus = null,
        IReadOnlyList<string>? departmentCodes = null,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDepartments = null,
        int pageIndex = 1,
        int pageSize = 50,
        CancellationToken ct = default);

    /// <summary>批详情（工序级下发/报工行）。taskNo 全局业务身份（跨版延续），必须带 planVersionId 限定版本。</summary>
    Task<MesStatusBatchDetailDto> GetBatchDetailAsync(
        int planVersionId,
        string taskNo,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default);
}
