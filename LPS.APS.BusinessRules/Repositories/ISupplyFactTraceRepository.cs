using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 供应事实原始追溯Repository接口
/// 从SupplyFact_Pipeline聚合查询（2026-11-24 Owner 判定 ext_ERP_Received_ByDocument_View 暂不使用，
/// RECEIVED 腿已停用；QueryReceivedAsync 保留为将来换源 ERP_U13_Received 的恢复骨架，须经 0号位 裁决）
/// </summary>
public interface ISupplyFactTraceRepository
{
    /// <summary>
    /// 查询SupplyFact_Pipeline（采购/Transit/VMI/已到厂未入库）
    /// </summary>
    Task<List<SupplyFactTraceDto>> QueryPipelineAsync(
        string? materialCode = null,
        int? materialId = null,
        string? factoryCode = null,
        string? supplyType = null,
        string? sourceDocumentNo = null,
        bool activeOnly = true,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 查询Received事实（SH/PI Received）
    /// </summary>
    Task<List<SupplyFactTraceDto>> QueryReceivedAsync(
        string? materialCode = null,
        int? materialId = null,
        string? factoryCode = null,
        string? documentType = null,
        string? documentNo = null,
        bool activeOnly = true,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 聚合查询所有供应事实
    /// </summary>
    Task<List<SupplyFactTraceDto>> QueryAllAsync(
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
        IReadOnlySet<string>? allowedFactories = null);
}
