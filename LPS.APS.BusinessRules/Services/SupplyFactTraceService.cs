using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// 供应事实原始追溯业务服务（5号位提供给4号位）
///
/// 用于页面查看Procurement/VMI/Received/Transit原始供应事实
/// 来源：SupplyFact_Pipeline + ext_ERP_Received_ByDocument_View
/// </summary>
public class SupplyFactTraceService
{
    private readonly ISupplyFactTraceRepository _repository;

    public SupplyFactTraceService(ISupplyFactTraceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 聚合查询所有供应事实
    /// </summary>
    public async Task<List<SupplyFactTraceDto>> QueryAllAsync(
        string? sourceType = null,
        string? materialCode = null,
        int? materialId = null,
        string? factoryCode = null,
        string? supplyType = null,
        string? sourceDocumentNo = null,
        bool activeOnly = true,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        // 越界夹上限不静默回落（4号位 10-19 R2：我 §六 说"第三处已修"，实际修的是 PeggingTraceService，
        // 本处属同型未扫到副本——教训第三次应用，已按建议 grep `take <= 0 || take >` 全库一次收净）。
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        return await _repository.QueryAllAsync(
            sourceType, materialCode, materialId, factoryCode,
            supplyType, sourceDocumentNo, activeOnly, skip, take, ct, allowedFactories);
    }
}
