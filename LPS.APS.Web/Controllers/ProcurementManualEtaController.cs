using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Setup;
using LPS.APS.Engine.Data;
using LPS.APS.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 采购人工ETA维护控制器（5号位提供，供4号位前端调用）
///
/// 路由规范：
///   GET    /api/procurement-manual-eta                  - 查询Manual ETA列表
///   GET    /api/procurement-manual-eta/{poNo}/{lineNo}  - 查询单条记录
///   POST   /api/procurement-manual-eta                  - 新增或更新Manual ETA
///   DELETE /api/procurement-manual-eta/{poNo}/{lineNo}  - 取消Manual ETA
///
/// 【职责边界 - 2026-08-26】
/// - 5号位提供Manual ETA维护API
/// - 2号位消费Manual ETA并计算Effective ETA
///
/// 参考：复审报告P1-01
/// </summary>
[Authorize(Policy = PermissionCodes.ManualEtaView)]
[ApiController]
[Route("api/procurement-manual-eta")]
public class ProcurementManualEtaController : ControllerBase
{
    private readonly ProcurementManualEtaService _service;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<ProcurementManualEtaController> _logger;

    public ProcurementManualEtaController(
        ProcurementManualEtaService service,
        DatabaseConnectionManager connectionManager,
        ILogger<ProcurementManualEtaController> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 收货仓库下拉（数据源：ext_MES_ProcessCode_View，processcode→code、processname→name）
    /// GET /api/procurement-manual-eta/warehouses
    /// </summary>
    [HttpGet("warehouses")]
    public async Task<ApiResponse<IReadOnlyList<WarehouseLookupItem>>> ListWarehouses(
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = @"
SELECT processcode AS Code, processname AS Name
FROM ext_MES_ProcessCode_View
WHERE (@Search IS NULL OR processcode LIKE '%' + @Search + '%' OR processname LIKE '%' + @Search + '%')
ORDER BY processcode";
            var items = await _connectionManager.QueryAsync<WarehouseLookupItem>(
                sql, new { Search = search }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
            return ApiResponse<IReadOnlyList<WarehouseLookupItem>>.Success(items.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query warehouses");
            return ApiResponse<IReadOnlyList<WarehouseLookupItem>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 物料候选（**乙端点，3号位 11-13 裁定"乙"、4号位 11-14 函落实**）：
    /// `GET /api/procurement-manual-eta/materials?search=&poNo=&lineNo=&limit=`——
    /// 权限随本控制器类级 `aps.manual_eta.view`，**消除抽屉选物料对 `aps.setup.view` 的跨域依赖**。
    /// · 不传 `poNo`：活跃物料全量，`search` 模糊编码/名称/规格，`MaterialCode` 升序；
    /// · 传 `poNo`（可选 `lineNo`）：该采购订单行的物料集合（供给事实 `SourceDocumentNo/LineNo` 同键、
    ///   与人工 ETA 覆盖键逐位对位），按 MaterialId 去重；**不按工厂范围裁剪**（候选完整性优先，4号位 §二.R1）；
    ///   PO 查无/已关闭 ⇒ **返回空集合不报错**（§一.5：收窄不可阻断录入）。
    /// 响应结构与 3号位 共用 lookup 同型（MaterialLookupItem：masterId/code/name/spec/uom/isActive）。
    /// </summary>
    [HttpGet("materials")]
    public async Task<ApiResponse<List<MaterialLookupItem>>> ListMaterials(
        [FromQuery] string? search = null,
        [FromQuery] string? poNo = null,
        [FromQuery] int? lineNo = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetMaterialCandidatesAsync(
                search, poNo, lineNo, limit, cancellationToken);
            return ApiResponse<List<MaterialLookupItem>>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query material candidates");
            return ApiResponse<List<MaterialLookupItem>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 订单行供货组合（**11-14 4号位 v2.0 §二.R1**）：
    /// `GET /api/procurement-manual-eta/supply-combos?poNo=&lineNo=&limit=`——
    /// 一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，物料与仓库**成对**给出。
    /// **为什么必须成对**：人工到货时间的覆盖键是四元组精确匹配
    /// `PONo+LineNo+MaterialId+ReceivingWarehouse` ≡ 供给事实 `SourceDocumentNo+LineNo+MaterialId+StorageCode`；
    /// 物料（本控制器 `/materials`，无仓库维度）与仓库（`/warehouses`，258 条全集）各选各的，
    /// 拼出的组合大概率命不中 ⇒ 这条到货时间**静默不生效**（不报错、不提示）。
    /// 故抽屉请用本端点一次性取回两字段：`materialId` 与 `storageCode` **原样**写入表单。
    /// · `poNo` 必填（缺 ⇒ 400）；`lineNo` 可选；`limit` 默认 200、钳 [1,500]；
    /// · 只含**能生效**的组合（与装载侧 TimedSupplyFactLoader 同谓词：有效 + 数量&gt;0 + 五类采购在途）；
    ///   行号非整数的事实不返回（覆盖键要求 int 行号，这类事实永远覆盖不上）；
    /// · **不按工厂范围裁剪**（受限账号候选要完整）；PO 查无 ⇒ **200 + 空数组**（收窄不可阻断录入）。
    /// 注意：`/materials` 仍保留（不传 poNo 的关键字全库搜索档）；本端点上线后，PO 收窄档请改用它。
    /// </summary>
    [HttpGet("supply-combos")]
    public async Task<ApiResponse<List<ProcurementManualEtaSupplyComboDto>>> ListSupplyCombos(
        [FromQuery] string? poNo = null,
        [FromQuery] int? lineNo = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetSupplyCombosAsync(poNo, lineNo, limit, cancellationToken);
            return ApiResponse<List<ProcurementManualEtaSupplyComboDto>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<List<ProcurementManualEtaSupplyComboDto>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query supply combos");
            return ApiResponse<List<ProcurementManualEtaSupplyComboDto>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询Manual ETA列表
    /// **11-14（v2.0 R2 取向乙）**：`withMatched=true` 时每行带 `isMatched`（true=会参与排产 /
    /// false=未命中当前供货事实，静默不生效 / null=未判定）。**默认 false**——标注要多扫一遍供给事实表
    /// （实库 42.7 万行、SourceDocumentNo 无索引，实测 ≈273ms），不需要标注的调用不付这笔钱。
    /// </summary>
    [HttpGet]
    public async Task<ApiResponse<PageResult<ProcurementManualEtaOverride>>> Query(
        [FromQuery] string? materialIds = null,
        [FromQuery] string? materialCodes = null,
        [FromQuery] string? poNos = null,
        [FromQuery] string? receivingWarehouses = null,
        [FromQuery] DateTime? etaBefore = null,
        [FromQuery] DateTime? etaAfter = null,
        [FromQuery] DateTime? updatedAfter = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] bool cancelledOnly = false,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? bucket = null,
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool withMatched = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            List<int>? materialIdList = null;
            if (!string.IsNullOrWhiteSpace(materialIds))
            {
                materialIdList = materialIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.Parse(x.Trim()))
                    .ToList();
            }

            List<string>? materialCodeList = null;
            if (!string.IsNullOrWhiteSpace(materialCodes))
            {
                materialCodeList = materialCodes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToList();
            }

            List<string>? poNoList = null;
            if (!string.IsNullOrWhiteSpace(poNos))
            {
                poNoList = poNos.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToList();
            }

            List<string>? warehouseList = null;
            if (!string.IsNullOrWhiteSpace(receivingWarehouses))
            {
                warehouseList = receivingWarehouses.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToList();
            }

            if (pageIndex <= 0) pageIndex = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 20;

            var result = await _service.QueryAsync(
                materialIds: materialIdList,
                materialCodes: materialCodeList,
                poNos: poNoList,
                receivingWarehouses: warehouseList,
                etaBefore: etaBefore,
                etaAfter: etaAfter,
                updatedAfter: updatedAfter,
                activeOnly: activeOnly,
                cancelledOnly: cancelledOnly,
                sortBy: sortBy,
                sortDir: sortDir,
                bucket: bucket,
                pageIndex: pageIndex,
                pageSize: pageSize,
                withMatched: withMatched,
                ct: cancellationToken);

            return ApiResponse<PageResult<ProcurementManualEtaOverride>>.Success(result);
        }
        catch (ArgumentException ex)
        {
            // R1/R2（11-12）：非法 sortBy/sortDir ⇒ 真 400，不静默返回"看着像排过"的结果（订单页 riskLevel 同类教训）。
            return ApiResponse<PageResult<ProcurementManualEtaOverride>>.Fail(400, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Manual ETA records");
            return ApiResponse<PageResult<ProcurementManualEtaOverride>>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 指标条一次五数（R3，11-12）：`GET /api/procurement-manual-eta/summary`，
    /// 接受与列表同名同义的筛选参数（不含 active/cancelled/sortBy——五桶横跨两态）；与列表 WHERE 类级单源。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ApiResponse<ProcurementManualEtaSummaryDto>> GetSummary(
        [FromQuery] string? materialIds = null,
        [FromQuery] string? materialCodes = null,
        [FromQuery] string? poNos = null,
        [FromQuery] string? receivingWarehouses = null,
        [FromQuery] DateTime? etaBefore = null,
        [FromQuery] DateTime? etaAfter = null,
        [FromQuery] DateTime? updatedAfter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetSummaryAsync(
                SplitIds(materialIds), SplitList(materialCodes), SplitList(poNos), SplitList(receivingWarehouses),
                etaBefore, etaAfter, updatedAfter, cancellationToken);

            return ApiResponse<ProcurementManualEtaSummaryDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Manual ETA summary");
            return ApiResponse<ProcurementManualEtaSummaryDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 筛选候选（R4，11-12）：`GET /api/procurement-manual-eta/facets?search=`——本表 distinct PONo/物料码（含已取消行），
    /// 每维顶 500，Truncated=true 表示候选被截（请改用 search）。收货仓库仍走 /warehouses。
    /// </summary>
    [HttpGet("facets")]
    public async Task<ApiResponse<ProcurementManualEtaFacetsDto>> GetFacets(
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetFacetsAsync(search, cancellationToken);
            return ApiResponse<ProcurementManualEtaFacetsDto>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Manual ETA facets");
            return ApiResponse<ProcurementManualEtaFacetsDto>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    private static List<int>? SplitIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim())).ToList();

    private static List<string>? SplitList(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

    /// <summary>
    /// 根据业务键查询单条Manual ETA记录
    /// </summary>
    [HttpGet("{poNo}/{lineNo}")]
    public async Task<ApiResponse<ProcurementManualEtaOverride?>> GetByBusinessKey(
        [FromRoute] string poNo,
        [FromRoute] int lineNo,
        [FromQuery] int materialId,
        [FromQuery] string receivingWarehouse,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _service.GetByBusinessKeyAsync(poNo, lineNo, materialId, receivingWarehouse, cancellationToken);
            if (result == null)
                return ApiResponse<ProcurementManualEtaOverride?>.Fail(404, "Record not found");

            return ApiResponse<ProcurementManualEtaOverride?>.Success(result);
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<ProcurementManualEtaOverride?>.Fail(400, $"Invalid parameters: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Manual ETA record");
            return ApiResponse<ProcurementManualEtaOverride?>.Fail(500, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 新增或更新Manual ETA
    /// </summary>
    [Authorize(Policy = PermissionCodes.ManualEtaMaintain)]
    [HttpPost]
    public async Task<ApiResponse<ProcurementManualEtaOverride>> Upsert(
        [FromBody] ProcurementManualEtaOverride etaOverride,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var saved = await _service.UpsertAsync(etaOverride, cancellationToken);
            return ApiResponse<ProcurementManualEtaOverride>.Success(saved, "Manual ETA saved successfully");
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<ProcurementManualEtaOverride>.Fail(400, $"Validation failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert Manual ETA");
            return ApiResponse<ProcurementManualEtaOverride>.Fail(500, $"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消Manual ETA（设置IsActive=0）
    /// </summary>
    [Authorize(Policy = PermissionCodes.ManualEtaCancel)]
    [HttpDelete("{poNo}/{lineNo}")]
    public async Task<ApiResponse<CancelManualEtaResponse>> Cancel(
        [FromRoute] string poNo,
        [FromRoute] int lineNo,
        [FromBody] CancelManualEtaRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await _service.CancelAsync(
                poNo, lineNo, request.MaterialId, request.ReceivingWarehouse,
                request.UpdatedBy, cancellationToken);

            if (!success)
            {
                // 可能是记录不存在，也可能是已经inactive（幂等）
                var existing = await _service.GetByBusinessKeyAsync(
                    poNo, lineNo, request.MaterialId, request.ReceivingWarehouse, cancellationToken);

                if (existing == null)
                    return ApiResponse<CancelManualEtaResponse>.Fail(404, "Record not found");

                // 记录存在但已inactive，幂等返回200
                if (!existing.IsActive)
                {
                    return ApiResponse<CancelManualEtaResponse>.Success(
                        new CancelManualEtaResponse
                        {
                            PONo = poNo,
                            LineNo = lineNo,
                            MaterialId = request.MaterialId,
                            ReceivingWarehouse = request.ReceivingWarehouse,
                            AlreadyCanceled = true,
                            CanceledAt = existing.UpdatedAt
                        },
                        "Manual ETA was already inactive");
                }

                return ApiResponse<CancelManualEtaResponse>.Fail(404, "Record not found");
            }

            return ApiResponse<CancelManualEtaResponse>.Success(
                new CancelManualEtaResponse
                {
                    PONo = poNo,
                    LineNo = lineNo,
                    MaterialId = request.MaterialId,
                    ReceivingWarehouse = request.ReceivingWarehouse,
                    AlreadyCanceled = false,
                    CanceledAt = DateTime.Now
                },
                "Manual ETA canceled successfully");
        }
        catch (ArgumentException ex)
        {
            return ApiResponse<CancelManualEtaResponse>.Fail(400, $"Invalid parameters: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel Manual ETA");
            return ApiResponse<CancelManualEtaResponse>.Fail(500, $"Cancel failed: {ex.Message}");
        }
    }
}

/// <summary>
/// 收货仓库下拉项（数据源 ext_MES_ProcessCode_View：processcode→Code、processname→Name）
/// </summary>
public sealed class WarehouseLookupItem
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// 取消Manual ETA请求体
/// </summary>
public class CancelManualEtaRequest
{
    public int MaterialId { get; init; }
    public string ReceivingWarehouse { get; init; } = string.Empty;
    public string UpdatedBy { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

/// <summary>
/// 取消Manual ETA响应体
/// </summary>
public class CancelManualEtaResponse
{
    public string PONo { get; init; } = string.Empty;
    public int LineNo { get; init; }
    public int MaterialId { get; init; }
    public string ReceivingWarehouse { get; init; } = string.Empty;
    public bool AlreadyCanceled { get; init; }
    public DateTime CanceledAt { get; init; }
}
