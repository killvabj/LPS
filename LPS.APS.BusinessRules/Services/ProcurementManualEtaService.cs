using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// 采购人工ETA业务服务
///
/// 【职责边界 - 2026-08-26】
/// - 5号位提供此服务供4号位前端调用
/// - 封装Repository，提供业务层验证和操作
/// - 不负责ETA计算（属于2号位职责）
///
/// 参考：复审报告P1-01
/// </summary>
public class ProcurementManualEtaService
{
    private readonly IProcurementManualEtaRepository _repository;

    public ProcurementManualEtaService(IProcurementManualEtaRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>R1（11-12 4号位件）：sortBy 白名单（OrdinalIgnoreCase）；不传=缺省排序（UpdatedAt DESC+业务键 tiebreaker）。</summary>
    private static readonly HashSet<string> SortByWhitelist = new(StringComparer.OrdinalIgnoreCase)
        { "manualEta", "updatedAt", "poNo", "materialCode" };

    /// <summary>
    /// 查询Manual ETA记录（R1/R2，11-12：+cancelledOnly/sortBy/sortDir）。
    /// 互斥规则（函 §2.2 建议采纳并写死）：**cancelledOnly=true 时 activeOnly 让位**（不再叠加生效/已取消两个互斥态），
    /// 两者同真不报错、按"只看已取消"执行——前端指标条点「已取消」即用此参。
    /// </summary>
    public async Task<PageResult<ProcurementManualEtaOverride>> QueryAsync(
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
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(sortBy) && !SortByWhitelist.Contains(sortBy))
            throw new ArgumentException($"非法 sortBy：{sortBy}（允许：manualEta/updatedAt/poNo/materialCode 或不传）");
        if (!string.IsNullOrWhiteSpace(sortDir)
            && !sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase)
            && !sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"非法 sortDir：{sortDir}（允许：asc/desc 或不传）");
        // R6（11-13 函）：bucket=overdue|dueSoon，与 /summary 两桶同源判定（仓储类级常量）。
        // **bucket 与 cancelledOnly 互斥**（桶自带 IsActive=1，叠加"只看已取消"必空集——矛盾参数显式 400，不静默空转，同 sortBy 纪律）；
        // 与 activeOnly 可叠加（桶已含 IsActive=1，activeOnly=true 为恒真冗余、false 也不放宽桶）。
        if (!string.IsNullOrWhiteSpace(bucket)
            && !bucket.Equals("overdue", StringComparison.OrdinalIgnoreCase)
            && !bucket.Equals("dueSoon", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"非法 bucket：{bucket}（允许：overdue/dueSoon 或不传）");
        if (!string.IsNullOrWhiteSpace(bucket) && cancelledOnly)
            throw new ArgumentException($"bucket={bucket} 与 cancelledOnly=true 互斥（时间桶只统计生效中行）——看已取消请去掉 bucket");

        return await _repository.QueryAsync(
            materialIds, materialCodes, poNos, receivingWarehouses,
            etaBefore, etaAfter, updatedAfter,
            activeOnly, cancelledOnly, sortBy, sortDir, bucket,
            pageIndex, pageSize, withMatched, allowedFactories, ct);
    }

    /// <summary>
    /// 指标条一次五数（R3，11-12）：与列表同筛选参数（不含 active/cancelled 开关，五桶横跨两态）、同 WHERE 单源。
    /// </summary>
    public async Task<ProcurementManualEtaSummaryDto> GetSummaryAsync(
        List<int>? materialIds = null,
        List<string>? materialCodes = null,
        List<string>? poNos = null,
        List<string>? receivingWarehouses = null,
        DateTime? etaBefore = null,
        DateTime? etaAfter = null,
        DateTime? updatedAfter = null,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        return await _repository.GetSummaryAsync(
            materialIds, materialCodes, poNos, receivingWarehouses,
            etaBefore, etaAfter, updatedAfter, allowedFactories, ct);
    }

    /// <summary>筛选候选（R4，11-12）：本表 distinct PONo/物料码，search 服务端过滤，每维顶 500。</summary>
    public async Task<ProcurementManualEtaFacetsDto> GetFacetsAsync(
        string? search = null,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        return await _repository.GetFacetsAsync(search, allowedFactories, ct);
    }

    /// <summary>乙端点（11-14）：本页域内物料候选；poNo[/lineNo] 收窄到该采购订单行（不裁剪工厂范围、查无=空集）。</summary>
    public async Task<List<LPS.APS.Core.DTOs.Setup.MaterialLookupItem>> GetMaterialCandidatesAsync(
        string? search = null,
        string? poNo = null,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        return await _repository.GetMaterialCandidatesAsync(search, poNo, lineNo, limit, ct);
    }

    /// <summary>
    /// 订单行供货组合（11-14 v2.0 R1）：`poNo` 必填（缺 ⇒ 400，不静默返全库组合）；
    /// `lineNo` 可选。响应里物料与 `storageCode` 成对来自同一条供给事实——抽屉必须两字段一起锁定。
    /// </summary>
    public async Task<List<ProcurementManualEtaSupplyComboDto>> GetSupplyCombosAsync(
        string? poNo = null,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(poNo))
            throw new ArgumentException("poNo 必填：人工到货时间的物料与收货仓库必须成对取自该订单行的供给事实");
        if (lineNo is <= 0)
            throw new ArgumentException($"非法 lineNo：{lineNo}（须为正整数或不传）");

        return await _repository.GetSupplyCombosAsync(poNo.Trim(), lineNo, limit, ct);
    }

    /// <summary>
    /// 根据业务键查询单条记录
    /// </summary>
    /// <summary>G-04 裁一透传：PO 工厂归属（null=不可解析，调用方据此 403）。</summary>
    public async Task<string?> ResolvePoFactoryAsync(string poNo, CancellationToken ct = default)
        => await _repository.ResolvePoFactoryAsync(poNo, ct);

    public async Task<ProcurementManualEtaOverride?> GetByBusinessKeyAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(poNo))
            throw new ArgumentException("PONo cannot be empty", nameof(poNo));
        if (lineNo <= 0)
            throw new ArgumentException("LineNo must be positive", nameof(lineNo));
        if (materialId <= 0)
            throw new ArgumentException("MaterialId must be positive", nameof(materialId));
        if (string.IsNullOrWhiteSpace(receivingWarehouse))
            throw new ArgumentException("ReceivingWarehouse cannot be empty", nameof(receivingWarehouse));

        return await _repository.GetByBusinessKeyAsync(poNo, lineNo, materialId, receivingWarehouse, allowedFactories, ct);
    }

    /// <summary>
    /// 新增或更新Manual ETA
    /// </summary>
    public async Task<ProcurementManualEtaOverride> UpsertAsync(ProcurementManualEtaOverride etaOverride, CancellationToken ct = default)
    {
        if (etaOverride == null)
            throw new ArgumentNullException(nameof(etaOverride));

        // 业务验证
        if (string.IsNullOrWhiteSpace(etaOverride.PONo))
            throw new ArgumentException("PONo cannot be empty");
        if (etaOverride.LineNo <= 0)
            throw new ArgumentException("LineNo must be positive");
        if (etaOverride.MaterialId <= 0)
            throw new ArgumentException("MaterialId must be positive");
        if (string.IsNullOrWhiteSpace(etaOverride.ReceivingWarehouse))
            throw new ArgumentException("ReceivingWarehouse cannot be empty");
        if (string.IsNullOrWhiteSpace(etaOverride.UpdatedBy))
            throw new ArgumentException("UpdatedBy cannot be empty");

        return await _repository.UpsertAsync(etaOverride, ct);
    }

    /// <summary>
    /// 取消Manual ETA（设置IsActive=0）
    /// </summary>
    public async Task<bool> CancelAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        string updatedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(poNo))
            throw new ArgumentException("PONo cannot be empty", nameof(poNo));
        if (lineNo <= 0)
            throw new ArgumentException("LineNo must be positive", nameof(lineNo));
        if (materialId <= 0)
            throw new ArgumentException("MaterialId must be positive", nameof(materialId));
        if (string.IsNullOrWhiteSpace(receivingWarehouse))
            throw new ArgumentException("ReceivingWarehouse cannot be empty", nameof(receivingWarehouse));
        if (string.IsNullOrWhiteSpace(updatedBy))
            throw new ArgumentException("UpdatedBy cannot be empty", nameof(updatedBy));

        return await _repository.CancelAsync(poNo, lineNo, materialId, receivingWarehouse, updatedBy, ct);
    }
}
