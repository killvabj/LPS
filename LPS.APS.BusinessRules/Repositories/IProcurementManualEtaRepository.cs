using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 采购人工ETA Repository接口
///
/// 【职责边界 - 2026-08-25新基线】
/// - 5号位负责：表结构、Repository实现、后端维护/查询接口
/// - 4号位负责：页面维护
/// - 2号位负责：读取ManualEta并计算Effective ETA/AvailableTime
///
/// 参考：APS_V1_5号位新基线增量整改开发包_v1.0_20260825.md P0-2
/// </summary>
public interface IProcurementManualEtaRepository
{
    /// <summary>
    /// 查询指定范围内的有效Manual ETA覆盖
    /// </summary>
    /// <param name="materialIds">物料ID列表（可选，null表示不过滤）</param>
    /// <param name="poNos">采购订单号列表（可选，null表示不过滤）</param>
    /// <param name="activeOnly">是否只查询有效记录（IsActive=1）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>Manual ETA覆盖列表</returns>
    Task<PageResult<ProcurementManualEtaOverride>> QueryAsync(
        List<int>? materialIds = null,
        List<string>? materialCodes = null,
        List<string>? poNos = null,
        List<string>? receivingWarehouses = null,
        DateTime? etaBefore = null,
        DateTime? etaAfter = null,
        DateTime? updatedAfter = null,
        bool activeOnly = true,
        bool cancelledOnly = false,
        string? sortBy = null,
        string? sortDir = null,
        string? bucket = null,
        int pageIndex = 1,
        int pageSize = 20,
        bool withMatched = false,
        CancellationToken ct = default);

    /// <summary>指标条五数一批（R3，11-12：与列表同 WHERE 单源；overdue/dueSoon 按服务器墙钟日界半开）。</summary>
    Task<ProcurementManualEtaSummaryDto> GetSummaryAsync(
        List<int>? materialIds = null,
        List<string>? materialCodes = null,
        List<string>? poNos = null,
        List<string>? receivingWarehouses = null,
        DateTime? etaBefore = null,
        DateTime? etaAfter = null,
        DateTime? updatedAfter = null,
        CancellationToken ct = default);

    /// <summary>本表 distinct 候选（R4，11-12：含已取消行；search 两维同打；每维顶 500+Truncated）。</summary>
    Task<ProcurementManualEtaFacetsDto> GetFacetsAsync(
        string? search = null,
        CancellationToken ct = default);

    /// <summary>
    /// 全量查询所有有效（IsActive=1）Manual ETA 覆盖（供排程装载构建查找表）。
    /// 覆盖表是人工维护、天然小，全量查 + 内存按业务键精确匹配，避免大 IN 列表超 SQL Server 2100 参数上限
    /// （1号位 2026-09-21 催办：LoadSupplyPoolAsync 传 distinct MaterialId=36k / PONo=56k 超限）。
    /// </summary>
    Task<List<ProcurementManualEtaOverride>> GetActiveOverridesAsync(CancellationToken ct = default);

    /// <summary>
    /// 根据业务键查询单条Manual ETA
    /// </summary>
    /// <param name="poNo">采购订单号</param>
    /// <param name="lineNo">行号</param>
    /// <param name="materialId">物料ID</param>
    /// <param name="receivingWarehouse">接收仓库</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>Manual ETA覆盖，不存在时返回null</returns>
    Task<ProcurementManualEtaOverride?> GetByBusinessKeyAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        CancellationToken ct = default);

    /// <summary>
    /// 新增或更新Manual ETA（按业务键Upsert）
    /// </summary>
    /// <param name="override">Manual ETA覆盖数据</param>
    /// <param name="ct">取消令牌</param>
    Task<ProcurementManualEtaOverride> UpsertAsync(ProcurementManualEtaOverride @override, CancellationToken ct = default);

    /// <summary>
    /// 取消Manual ETA（设置IsActive=0，不物理删除）
    /// </summary>
    /// <param name="poNo">采购订单号</param>
    /// <param name="lineNo">行号</param>
    /// <param name="materialId">物料ID</param>
    /// <param name="receivingWarehouse">接收仓库</param>
    /// <param name="updatedBy">操作人</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否成功取消（false表示记录不存在）</returns>
    Task<bool> CancelAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        string updatedBy,
        CancellationToken ct = default);

    /// <summary>
    /// 乙端点（11-14）：本页域内物料候选；传 poNo[/lineNo] 收窄到该采购订单行物料集合
    /// （供给事实同键取数、不按工厂范围裁剪、PO 查无=空集不报错）。
    /// </summary>
    Task<List<LPS.APS.Core.DTOs.Setup.MaterialLookupItem>> GetMaterialCandidatesAsync(
        string? search = null,
        string? poNo = null,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default);

    /// <summary>
    /// 订单行供货组合（11-14 v2.0 R1）：按 PO(+行号) 返回 **(物料 × 存储地点)** 组合，
    /// 物料与仓库成对取自同一条**能生效的**供给事实；不按工厂范围裁剪、行号非整数的组合不返、查无=空集。
    /// </summary>
    Task<List<ProcurementManualEtaSupplyComboDto>> GetSupplyCombosAsync(
        string poNo,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default);
}
