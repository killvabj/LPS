using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Setup;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 采购人工ETA Repository实现
///
/// 【实现说明 - 2026-08-26审核修正】
/// - 数据库：DatabaseId.APS（不是ODS）
/// - 表名：ProcurementManualEtaOverride
/// - 业务键：PONo + LineNo + MaterialId + ReceivingWarehouse
/// - 取消方式：IsActive=0（不物理删除）
///
/// 审核裁决：Manual ETA属于APS业务事实表，不属于ODS防腐层
/// 参考：APS_V1_5号位_Commit_42a16ed_新基线整改复审报告_20260825.md P0-01
/// </summary>
public class ProcurementManualEtaRepository : IProcurementManualEtaRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public ProcurementManualEtaRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    // ── 11-12（4号位《人工到货时间页改版》R1/R2/R3）：筛选 WHERE **类级单源**——列表/COUNT/summary 同一文本，
    //    指标条与列表口径不可能分叉（订单页同型纪律）。时间列一律 naive 工厂墙钟比较（§三 已确认口径，无 Z/无偏移）。
    private const string FromJoinO = @"
FROM ProcurementManualEtaOverride o
LEFT JOIN Material m ON m.Id = o.MaterialId";

    private const string FilterWhere = @"
WHERE 1=1
    AND (@ActiveOnly = 0 OR o.IsActive = 1)
    AND (@CancelledOnly = 0 OR o.IsActive = 0)
    AND (@HasMaterialIds = 0 OR o.MaterialId IN @MaterialIds)
    AND (@HasMaterialCodes = 0 OR o.MaterialCode IN @MaterialCodes)
    AND (@HasPONos = 0 OR o.PONo IN @PONos)
    AND (@HasWarehouses = 0 OR o.ReceivingWarehouse IN @Warehouses)
    AND (@EtaBefore IS NULL OR o.ManualEta <= @EtaBefore)
    AND (@EtaAfter IS NULL OR o.ManualEta >= @EtaAfter)
    AND (@UpdatedAfter IS NULL OR o.UpdatedAt >= @UpdatedAfter)";

    private const string SelectCols = @"
SELECT
    o.PONo, o.[LineNo], o.MaterialId, o.MaterialCode, o.ReceivingWarehouse,
    o.ManualEta, o.IsActive, o.UpdatedBy, o.UpdatedAt, o.CreatedBy, o.CreatedAt, o.Remark,
    m.MaterialName AS MaterialName";   // R5：LEFT JOIN 投影；主数据缺行 ⇒ null 整键省略

    // ── R6（11-13 4号位件）：时间桶布尔式**类级单源**——summary 的 SUM(CASE WHEN {式}) 与列表的
    //    WHERE AND ({式}) 引用同一段文本，「指标条说几行、列表给几行」从两处定义收敛为一处。
    //    日界=服务端 GETDATE()（工厂墙钟，11-12 已确认最终口径）；overdue 严格早于今日00:00、
    //    dueSoon=[今日00:00, +7d)半开——与 R3 交付语义逐字不变，只是物理上不再可能漂移。 ──
    private const string OverdueExpr =
        "o.IsActive = 1 AND o.ManualEta < CAST(CAST(GETDATE() AS date) AS datetime2)";

    private const string DueSoonExpr =
        "o.IsActive = 1 AND o.ManualEta >= CAST(CAST(GETDATE() AS date) AS datetime2)"
        + " AND o.ManualEta < DATEADD(day, 7, CAST(CAST(GETDATE() AS date) AS datetime2))";

    // ── 11-14（4号位 v2.0 件）**覆盖能否生效**的供给事实谓词 = 类级单源 ──
    //    人工到货时间是**对供给事实的覆盖**，键=(PONo, LineNo, MaterialId, ReceivingWarehouse)
    //    逐位对位装配侧 AvailableTimeCalculator.BuildManualEtaMap / Explain（PONo+LineNo+MaterialId+StorageCode）。
    //    因此「能生效」= 该四元组能在装载侧出现：与 2号位 TimedSupplyFactLoader 同一资格谓词
    //    （IsActive=1 + Quantity>0 + 五类采购在途）。以下两处引用同一文本，口径不可能分叉：
    //      ① GetSupplyCombosAsync（订单行供货组合，物料×存储地点成对）；
    //      ② MarkMatchedAsync（列表/详情 IsMatched 标注："这条覆盖现在能不能命中"）。
    //    别名为 `s`（SupplyFact_Pipeline）。
    private const string SupplyFactEligiblePredicate = @"
    s.IsActive = 1
    AND s.Quantity > 0
    AND s.SupplyType IN ('PURCHASE_IN_TRANSIT', 'OPEN_PO_REMAINING',
                         'ARRIVED_NOT_RECEIVED', 'VMI_ONSITE',
                         'INTERPLANT_IN_TRANSIT')";

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
        CancellationToken ct = default)
    {
        // Dapper 列表参数只能出现在 IN 内；@X IS NULL(标量) 会随 @X 一起扩成 (@p1,@p2) → "(@p1,@p2) IS NULL" 非法 SQL 4145。
        // 用 Has 标志替代标量判空；任一个列表空集即 fail-closed 返空，避免空集传入 IN ()。
        if (materialIds is { Count: 0 } || materialCodes is { Count: 0 }
            || poNos is { Count: 0 } || receivingWarehouses is { Count: 0 })
            return new PageResult<ProcurementManualEtaOverride>
            {
                Items = Array.Empty<ProcurementManualEtaOverride>(),
                Total = 0,
                Page = pageIndex <= 0 ? 1 : pageIndex,
                PageSize = pageSize
            };
        var hasMaterialIds = materialIds is { Count: > 0 };
        var hasMaterialCodes = materialCodes is { Count: > 0 };
        var hasPONos = poNos is { Count: > 0 };
        var hasWarehouses = receivingWarehouses is { Count: > 0 };

        // R1：sortBy 白名单仓储层再校验一遍（服务层第一道 400）；**缺省=现排序逐字保留**（全链清扫 tiebreaker）。
        var dir = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderBy = (sortBy ?? "") switch
        {
            ""             => "ORDER BY o.UpdatedAt DESC, o.PONo, o.[LineNo]",
            "manualEta"    => $"ORDER BY o.ManualEta {dir}, o.PONo, o.[LineNo]",
            "updatedAt"    => $"ORDER BY o.UpdatedAt {dir}, o.PONo, o.[LineNo]",
            "poNo"         => $"ORDER BY o.PONo {dir}, o.[LineNo], o.MaterialId, o.ReceivingWarehouse",
            "materialCode" => $"ORDER BY o.MaterialCode {dir}, o.PONo, o.[LineNo]",
            _              => throw new ArgumentException($"非法 sortBy：{sortBy}（允许：manualEta/updatedAt/poNo/materialCode 或不传=缺省排序）")
        };

        // R6：bucket=overdue|dueSoon ⇒ 引用类级桶常量（与 summary 同一文本）；不传=现语义逐字不变。
        // 桶自带 IsActive=1 ⇒ 与 activeOnly 叠加为恒真（无害）；与 cancelledOnly 互斥（服务层拦 400）。
        var bucketPred = string.IsNullOrWhiteSpace(bucket)
            ? ""
            : bucket.Equals("overdue", StringComparison.OrdinalIgnoreCase) ? $"\n    AND ({OverdueExpr})"
            : bucket.Equals("dueSoon", StringComparison.OrdinalIgnoreCase) ? $"\n    AND ({DueSoonExpr})"
            : throw new ArgumentException($"非法 bucket：{bucket}（允许：overdue/dueSoon 或不传）");

        var parameters = new
        {
            ActiveOnly = activeOnly && !cancelledOnly ? 1 : 0,   // R2 互斥：cancelledOnly=true 时 activeOnly 让位（服务层已归一，此处双保险）
            CancelledOnly = cancelledOnly ? 1 : 0,
            HasMaterialIds = hasMaterialIds,
            HasMaterialCodes = hasMaterialCodes,
            HasPONos = hasPONos,
            HasWarehouses = hasWarehouses,
            MaterialIds = materialIds,
            MaterialCodes = materialCodes,
            PONos = poNos,
            Warehouses = receivingWarehouses,
            EtaBefore = etaBefore,
            EtaAfter = etaAfter,
            UpdatedAfter = updatedAfter,
            Skip = (pageIndex <= 1 ? 0 : (pageIndex - 1)) * pageSize,
            Take = pageSize
        };

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*)" + FromJoinO + FilterWhere + bucketPred, parameters,
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var items = (await _connectionManager.QueryAsync<ProcurementManualEtaOverride>(
            SelectCols + FromJoinO + FilterWhere + bucketPred + $"\n{orderBy}\nOFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        // 11-14（v2.0 R2 取向乙）：本页逐行标注 IsMatched（是否命中一条**能生效的**供给事实）。
        // **默认不标注**（withMatched=true 才做）：SupplyFact_Pipeline 无 SourceDocumentNo 索引，
        // 标注需多扫 42.7 万行（实测单次 ≈273ms），无条件加会给每次翻页/筛选都添这一笔；要显示的页面显式开启。
        if (withMatched)
            await MarkMatchedAsync(items, ct);

        return new PageResult<ProcurementManualEtaOverride>
        {
            Items = items,
            Total = total,
            Page = pageIndex <= 1 ? 1 : pageIndex,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// R3（11-12）：指标条五数一批聚合。**筛选 WHERE 与列表同常量单源**（active/cancelled 开关不启用——
    /// 五桶本身横跨两态）；overdue/dueSoon 按服务器 GETDATE() 日界（=工厂墙钟），半开区间、三桶互斥且并集=active。
    /// </summary>
    public async Task<ProcurementManualEtaSummaryDto> GetSummaryAsync(
        List<int>? materialIds = null,
        List<string>? materialCodes = null,
        List<string>? poNos = null,
        List<string>? receivingWarehouses = null,
        DateTime? etaBefore = null,
        DateTime? etaAfter = null,
        DateTime? updatedAfter = null,
        CancellationToken ct = default)
    {
        if (materialIds is { Count: 0 } || materialCodes is { Count: 0 }
            || poNos is { Count: 0 } || receivingWarehouses is { Count: 0 })
            return new ProcurementManualEtaSummaryDto();
        var parameters = new
        {
            ActiveOnly = 0,        // summary 恒横跨两态：两开关都短路
            CancelledOnly = 0,
            HasMaterialIds = materialIds is { Count: > 0 },
            HasMaterialCodes = materialCodes is { Count: > 0 },
            HasPONos = poNos is { Count: > 0 },
            HasWarehouses = receivingWarehouses is { Count: > 0 },
            MaterialIds = materialIds,
            MaterialCodes = materialCodes,
            PONos = poNos,
            Warehouses = receivingWarehouses,
            EtaBefore = etaBefore,
            EtaAfter = etaAfter,
            UpdatedAfter = updatedAfter
        };

        // R6 同源化：两桶 CASE 引用类级常量（与列表 bucket 同一文本）；并回带实际日界（函 §2.3 采纳）。
        var sql = $@"
SELECT
    COUNT(*) AS AllCount,
    SUM(CASE WHEN o.IsActive = 1 THEN 1 ELSE 0 END) AS ActiveCount,
    SUM(CASE WHEN o.IsActive = 0 THEN 1 ELSE 0 END) AS CancelledCount,
    SUM(CASE WHEN {OverdueExpr} THEN 1 ELSE 0 END) AS OverdueCount,
    SUM(CASE WHEN {DueSoonExpr} THEN 1 ELSE 0 END) AS DueSoonCount,
    CAST(CAST(GETDATE() AS date) AS datetime2) AS OverdueBefore,
    CAST(CAST(GETDATE() AS date) AS datetime2) AS DueSoonFrom,
    DATEADD(day, 7, CAST(CAST(GETDATE() AS date) AS datetime2)) AS DueSoonTo"
            + FromJoinO + FilterWhere;

        return await _connectionManager.QueryFirstOrDefaultAsync<ProcurementManualEtaSummaryDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);
    }

    /// <summary>
    /// R4（11-12）：本表 distinct 候选（含已取消行——候选是为了筛到记录）；search 对两维同打；
    /// 每维 TOP 501 探 500 顶 ⇒ Truncated（前端提示改用搜索）。收货仓库不在本端点（既有 /warehouses 单源）。
    /// </summary>
    public async Task<ProcurementManualEtaFacetsDto> GetFacetsAsync(
        string? search = null,
        CancellationToken ct = default)
    {
        var parameters = new { Kw = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" };

        var ponos = (await _connectionManager.QueryAsync<string>(
            @"SELECT DISTINCT TOP (501) PONo FROM ProcurementManualEtaOverride
              WHERE (@Kw IS NULL OR PONo LIKE @Kw) ORDER BY PONo",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10)).ToList();
        var mats = (await _connectionManager.QueryAsync<string>(
            @"SELECT DISTINCT TOP (501) MaterialCode FROM ProcurementManualEtaOverride
              WHERE (@Kw IS NULL OR MaterialCode LIKE @Kw) ORDER BY MaterialCode",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10)).ToList();

        var truncated = ponos.Count > 500 || mats.Count > 500;
        return new ProcurementManualEtaFacetsDto
        {
            PONos = truncated ? ponos.Take(500).ToList() : ponos,
            MaterialCodes = truncated ? mats.Take(500).ToList() : mats,
            Truncated = truncated
        };
    }

    public async Task<List<ProcurementManualEtaOverride>> GetActiveOverridesAsync(CancellationToken ct = default)
    {
        var sql = @"
SELECT
    PONo, [LineNo], MaterialId, MaterialCode, ReceivingWarehouse,
    ManualEta, IsActive, UpdatedBy, UpdatedAt, CreatedBy, CreatedAt, Remark
FROM ProcurementManualEtaOverride
WHERE IsActive = 1
ORDER BY UpdatedAt DESC, PONo, [LineNo]";   // 同口径定序：非分页导出，防并列序漂移

        var results = await _connectionManager.QueryAsync<ProcurementManualEtaOverride>(
            sql,
            null,
            CommandType.Text,
            DatabaseId.APS,
            commandTimeout: 30);

        return results.ToList();
    }

    public async Task<ProcurementManualEtaOverride?> GetByBusinessKeyAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        CancellationToken ct = default)
    {
        var sql = @"
SELECT
    o.PONo, o.[LineNo], o.MaterialId, o.MaterialCode, o.ReceivingWarehouse,
    o.ManualEta, o.IsActive, o.UpdatedBy, o.UpdatedAt, o.CreatedBy, o.CreatedAt, o.Remark,
    m.MaterialName AS MaterialName
FROM ProcurementManualEtaOverride o
LEFT JOIN Material m ON m.Id = o.MaterialId
WHERE o.PONo = @PONo
    AND o.[LineNo] = @LineNo
    AND o.MaterialId = @MaterialId
    AND o.ReceivingWarehouse = @ReceivingWarehouse";

        var parameters = new
        {
            PONo = poNo,
            LineNo = lineNo,
            MaterialId = materialId,
            ReceivingWarehouse = receivingWarehouse
        };

        var results = await _connectionManager.QueryAsync<ProcurementManualEtaOverride>(
            sql,
            parameters,
            CommandType.Text,
            DatabaseId.APS,
            commandTimeout: 10);

        var row = results.FirstOrDefault();
        if (row != null)
        {
            // 11-14（v2.0 R2）：详情同样带 IsMatched（与本行 Upsert 的键逐位一致）。
            var one = new List<ProcurementManualEtaOverride> { row };
            await MarkMatchedAsync(one, ct);
        }
        return row;
    }

    /// <summary>
    /// 乙端点（3号位 11-13 裁定"乙"、4号位 11-14 函落实）：**本页域内**物料候选——
    /// 消除「选物料要持 aps.setup.view」的跨域权限依赖，权限=本控制器类级 aps.manual_eta.view。
    /// · 不传 poNo：活跃物料（IsActive=1）按 search 模糊编码/名称/规格，MaterialCode 升序，TOP(limit)；
    /// · 传 poNo：该采购订单行的物料集合——`SupplyFact_Pipeline.SourceDocumentNo + SourceDocumentLineNo`
    ///   （lineNo 传了就再收窄到该行）；Received 视图无行号列，只按 DocumentNo+MasterID 并入；
    ///   union 后按 MaterialId 去重，**不按工厂范围裁剪**（4号位 §二.R1：限定范围会把受限账号的合法候选挡掉）；
    ///   PO 查无/已关闭 ⇒ **空集不报错**（§一.5：种子数据与历史 PO 查不到供给事实，收窄不可阻断录入）。
    /// 【11-14 晚·4号位 v2.0 §4.1 撤回并表】曾按同日 v1.0 请求把**本表**并入 `hit`，已**撤回**（未提交、工作副本已还原）：
    ///   本表 17 行/10 个 (物料,仓库) 组合在供给事实里命中 0 行（实库复核），并表只会把"选不到"变成"能选但不生效"。
    ///   本端点保持"物料候选"单一职责（无仓库维度）；**成对口径**见 `GetSupplyCombosAsync`。
    /// </summary>
    public async Task<List<MaterialLookupItem>> GetMaterialCandidatesAsync(
        string? search = null,
        string? poNo = null,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);
        var kw = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";

        const string selectPart = @"
SELECT TOP (@Limit)
    m.Id AS MasterId, m.MaterialCode AS Code, m.MaterialName AS Name,
    m.Spec AS Spec, m.UOM AS Uom, CAST(m.IsActive AS bit) AS IsActive
FROM Material m";

        string sql;
        object parameters;
        if (string.IsNullOrWhiteSpace(poNo))
        {
            sql = selectPart + @"
WHERE m.IsActive = 1
  AND (@Kw IS NULL OR m.MaterialCode LIKE @Kw OR m.MaterialName LIKE @Kw OR m.Spec LIKE @Kw)
ORDER BY m.MaterialCode";
            parameters = new { Limit = clamped, Kw = kw };
        }
        else
        {
            sql = @"
WITH hit AS (
    SELECT MaterialId FROM SupplyFact_Pipeline
     WHERE SourceDocumentNo = @PoNo
       AND (@LineNo IS NULL OR TRY_CAST(SourceDocumentLineNo AS int) = @LineNo)
    UNION
    SELECT ISNULL(MasterID, 0) FROM ext_ERP_Received_ByDocument_View
     WHERE DocumentNo = @PoNo
)" + selectPart + @"
INNER JOIN (SELECT DISTINCT MaterialId FROM hit WHERE MaterialId > 0) h ON h.MaterialId = m.Id
WHERE (@Kw IS NULL OR m.MaterialCode LIKE @Kw OR m.MaterialName LIKE @Kw OR m.Spec LIKE @Kw)
ORDER BY m.MaterialCode";
            parameters = new
            {
                Limit = clamped,
                PoNo = poNo.Trim(),
                LineNo = lineNo,
                Kw = kw
            };
        }

        var results = await _connectionManager.QueryAsync<MaterialLookupItem>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    /// <summary>
    /// 订单行供货组合（11-14 4号位 v2.0 §二.R1）：按 PO(+行号) 返回 **(物料 × 存储地点)** 一行一组合，
    /// 物料与仓库**成对取自同一条供给事实** —— 前端据此同时锁定两字段，从源头不再拼出命不中的四元组。
    /// · 取数 `SupplyFact_Pipeline`，谓词 = <see cref="SupplyFactEligiblePredicate"/>（与 2号位 TimedSupplyFactLoader 同口径）：
    ///   只返**能生效**的组合（失效/零量/非采购在途类的事实即便列出也覆盖不上）；
    /// · **不按工厂范围裁剪**（同乙端点纪律：受限账号的候选要完整）；
    /// · 行号非整数的事实**不返回**：覆盖键要求 int 行号（AvailableTimeCalculator 用 int.TryParse），
    ///   这类事实永远覆盖不上，列出来只会再生产无效记录；
    /// · PO 查无 ⇒ **空集 200**，不 400/404（收窄不可阻断录入）。
    /// 命中口径 = `(PONo, LineNo, MaterialId, StorageCode)` 逐位等于覆盖键
    /// `ProcurementManualEtaOverride(PONo, LineNo, MaterialId, ReceivingWarehouse)`。
    /// </summary>
    public async Task<List<ProcurementManualEtaSupplyComboDto>> GetSupplyCombosAsync(
        string poNo,
        int? lineNo = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);

        var sql = $@"
SELECT TOP (@Limit)
    x.PONo,
    x.[LineNo],
    x.MaterialId,
    ISNULL(m.MaterialCode, x.MaterialCode) AS MaterialCode,
    m.MaterialName,
    m.Spec,
    m.UOM AS Uom,
    x.StorageCode,
    v.processname AS StorageName
FROM (
    SELECT SourceDocumentNo                     AS PONo,
           TRY_CAST(SourceDocumentLineNo AS int) AS [LineNo],
           MaterialId,
           MAX(MaterialCode)                     AS MaterialCode,
           StorageCode
      FROM SupplyFact_Pipeline s
     WHERE s.SourceDocumentNo = @PoNo
       AND (@LineNo IS NULL OR TRY_CAST(s.SourceDocumentLineNo AS int) = @LineNo)
       AND TRY_CAST(s.SourceDocumentLineNo AS int) IS NOT NULL
       AND {SupplyFactEligiblePredicate}
     GROUP BY SourceDocumentNo, SourceDocumentLineNo, MaterialId, StorageCode
) x
LEFT JOIN Material m ON m.Id = x.MaterialId
LEFT JOIN ext_MES_ProcessCode_View v ON v.processcode = x.StorageCode
ORDER BY x.[LineNo], MaterialCode, x.StorageCode";

        var results = await _connectionManager.QueryAsync<ProcurementManualEtaSupplyComboDto>(
            sql, new { Limit = clamped, PoNo = poNo.Trim(), LineNo = lineNo },
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return results.ToList();
    }

    /// <summary>
    /// 11-14（v2.0 §二.R2 取向乙）：给列表/详情行补 `IsMatched` —— 该行的
    /// `(PONo, LineNo, MaterialId, ReceivingWarehouse)` 是否命中一条**能生效的**供给事实（谓词见
    /// <see cref="SupplyFactEligiblePredicate"/>）。命中 ⇒ 这条到货时间真会参与排产；未命中 ⇒ 静默不生效，
    /// 前端据此显示「未命中当前供货事实」。
    ///
    /// **分两段而非逐行 EXISTS**：SupplyFact_Pipeline 在 `SourceDocumentNo` 上无索引（实查仅 Id 聚簇），
    /// 逐行相关子查询 = 每条扫 42.7 万行；改为「本页涉及的 PO 一次 IN 取组合 → 内存比对」只扫一遍。
    /// 比对键统一大写：SQL 侧排序规则大小写不敏感、C# 元组比较敏感，不归一会造成假阴性。
    ///
    /// 取数失败时**置 null（=未判定）而非 false（=未命中）**——未判定与未命中必须可区分，不得把故障说成"没匹配上"。
    /// </summary>
    private async Task MarkMatchedAsync(List<ProcurementManualEtaOverride> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return;

        foreach (var i in items)
            i.IsMatched = null;

        var poNos = items.Select(i => i.PONo).Distinct().ToList();
        var sql = $@"
SELECT DISTINCT
    s.SourceDocumentNo                     AS PONo,
    TRY_CAST(s.SourceDocumentLineNo AS int) AS [LineNo],
    s.MaterialId,
    s.StorageCode                          AS ReceivingWarehouse
FROM SupplyFact_Pipeline s
WHERE s.SourceDocumentNo IN @PoNos
  AND TRY_CAST(s.SourceDocumentLineNo AS int) IS NOT NULL
  AND {SupplyFactEligiblePredicate}";

        HashSet<string> set;
        try
        {
            var rows = await _connectionManager.QueryAsync<FactKeyRow>(
                sql, new { PoNos = poNos }, CommandType.Text, DatabaseId.APS, commandTimeout: 30);
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
                if (r.LineNo.HasValue)
                    set.Add(MatchKey(r.PONo, r.LineNo.Value, r.MaterialId, r.ReceivingWarehouse));
        }
        catch
        {
            // 标注是辅助信息：取数失败留 null（未判定），不阻断列表/详情主查询，也不谎报"未命中"。
            return;
        }

        foreach (var i in items)
            i.IsMatched = set.Contains(MatchKey(i.PONo, i.LineNo, i.MaterialId, i.ReceivingWarehouse));
    }

    private static string MatchKey(string? poNo, int lineNo, int materialId, string? warehouse)
        => string.Concat(poNo ?? string.Empty, "\u0001", lineNo.ToString(), "\u0001",
                         materialId.ToString(), "\u0001", warehouse ?? string.Empty);

    /// <summary>供给事实业务键投影（IsMatched 比对用）。</summary>
    private sealed class FactKeyRow
    {
        public string PONo { get; init; } = string.Empty;
        public int? LineNo { get; init; }
        public int MaterialId { get; init; }
        public string? ReceivingWarehouse { get; init; }
    }

    public async Task<ProcurementManualEtaOverride> UpsertAsync(ProcurementManualEtaOverride @override, CancellationToken ct = default)
    {
        var sql = @"
MERGE INTO ProcurementManualEtaOverride AS target
USING (SELECT
    @PONo AS PONo,
    @LineNo AS [LineNo],
    @MaterialId AS MaterialId,
    @ReceivingWarehouse AS ReceivingWarehouse
) AS source
ON target.PONo = source.PONo
    AND target.[LineNo] = source.[LineNo]
    AND target.MaterialId = source.MaterialId
    AND target.ReceivingWarehouse = source.ReceivingWarehouse
WHEN MATCHED THEN
    UPDATE SET
        ManualEta = @ManualEta,
        IsActive = @IsActive,
        UpdatedBy = @UpdatedBy,
        UpdatedAt = GETDATE(),
        Remark = @Remark
WHEN NOT MATCHED THEN
    INSERT (PONo, [LineNo], MaterialId, MaterialCode, ReceivingWarehouse,
            ManualEta, IsActive, UpdatedBy, UpdatedAt, CreatedBy, CreatedAt, Remark)
    VALUES (@PONo, @LineNo, @MaterialId, @MaterialCode, @ReceivingWarehouse,
            @ManualEta, @IsActive, @UpdatedBy, GETDATE(), @UpdatedBy, GETDATE(), @Remark);";

        var parameters = new
        {
            PONo = @override.PONo,
            LineNo = @override.LineNo,
            MaterialId = @override.MaterialId,
            MaterialCode = @override.MaterialCode,
            ReceivingWarehouse = @override.ReceivingWarehouse,
            ManualEta = @override.ManualEta,
            IsActive = @override.IsActive ? 1 : 0,
            UpdatedBy = @override.UpdatedBy,
            Remark = @override.Remark
        };

        await _connectionManager.ExecuteAsync(
            sql,
            parameters,
            CommandType.Text,
            DatabaseId.APS,
            commandTimeout: 30);

        // 查询保存后的完整实体返回
        var saved = await GetByBusinessKeyAsync(
            @override.PONo, @override.LineNo, @override.MaterialId, @override.ReceivingWarehouse, ct);

        return saved!;
    }

    public async Task<bool> CancelAsync(
        string poNo,
        int lineNo,
        int materialId,
        string receivingWarehouse,
        string updatedBy,
        CancellationToken ct = default)
    {
        var sql = @"
UPDATE ProcurementManualEtaOverride
SET IsActive = 0,
    UpdatedBy = @UpdatedBy,
    UpdatedAt = GETDATE()
WHERE PONo = @PONo
    AND [LineNo] = @LineNo
    AND MaterialId = @MaterialId
    AND ReceivingWarehouse = @ReceivingWarehouse;";

        var parameters = new
        {
            PONo = poNo,
            LineNo = lineNo,
            MaterialId = materialId,
            ReceivingWarehouse = receivingWarehouse,
            UpdatedBy = updatedBy
        };

        var rowCount = await _connectionManager.ExecuteAsync(
            sql,
            parameters,
            CommandType.Text,
            DatabaseId.APS,
            commandTimeout: 10);

        return rowCount > 0;
    }
}
