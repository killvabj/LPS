using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// 订单查询Repository实现
/// 直接读取[Order]、PeggingSupplyAllocation、Task表
/// </summary>
public class OrderQueryRepository : IOrderQueryRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    /// <summary>冻结 07:4937 权威 15 码白名单（PrimaryReasonCode 只透出白名单内的码；白名单外=null）</summary>
    private const string DeliveryReasonWhitelist = @"'RESOURCE_CAPACITY_WAIT','MATERIAL_SHORTAGE','PRECEDENCE_WAIT','FROZEN_ZONE_LOCK','ROUTING_FALLBACK','STAGE_LEADTIME_FALLBACK','BOM_DEGRADE','CROSS_ORG_HANDOFF','PRIORITY_LOWER_THAN_OTHERS','DUE_DATE_RISK','LOGISTICS_DELAY','PRIORITY_INHERITANCE','CROSS_DOMAIN_VERSION_MISMATCH_RISK','MANUAL_COMPLETED_SHORT','EQUIPMENT_BREAKDOWN_RISK'";

    public OrderQueryRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    // ── 11-12（4号位《订单需求计划页改版》R2/R3/R4）：FROM/WHERE/risk 判定 **类级单源** ──
    // 列表、COUNT、summary、facets 四处必须逐字同一谓词文本（total/列表/KPI 分叉在源头堵死；
    // 同「FROM+WHERE 逐字一致」10-19 纪律的跨方法延伸）。
    private const string FromJoin = @"
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN ProductFamily pf ON pf.Id = o.ProductFamilyId";

    private const string WherePart = @"
WHERE o.PlanVersionId = @PlanVersionId
    AND (@OrderNo IS NULL OR o.OrderNo LIKE '%' + @OrderNo + '%')
    AND (@MaterialCode IS NULL OR o.MaterialCode LIKE '%' + @MaterialCode + '%')
    AND (@CustomerName IS NULL OR o.CustomerName LIKE '%' + @CustomerName + '%')
    AND (@FactoryCode IS NULL OR f.Code = @FactoryCode)
    AND (@DomainKey IS NULL OR o.DomainKey = @DomainKey)
    AND (@DelayStatus IS NULL OR o.DelayStatus = @DelayStatus)
    AND (@Status IS NULL OR o.Status = @Status)
    AND (@ProductionInstructionNo IS NULL OR o.MTS_InstructionNo LIKE '%' + @ProductionInstructionNo + '%')
    AND (@ProductFamilyCode IS NULL OR pf.Code = @ProductFamilyCode)
    AND (@DueDateFrom IS NULL OR o.CustomerDueDate >= @DueDateFrom)
    AND (@DueDateTo IS NULL OR o.CustomerDueDate <= @DueDateTo)
    AND (@HasFactories = 0 OR f.Code IN @AllowedFactories)
    AND (@HasDomains = 0 OR o.DomainKey IN @AllowedDomains)";

    /// <summary>
    /// 计划派生交付判定（冻结 01 §64 / 07 §八.2）——与列表富化 C# 式逐语等价：
    /// 无 Task 行 / MAX(PlannedEndTime) NULL / 无交期 ⇒ 'UNSCHEDULED'（**仅筛选域值**：响应里 riskLevel
    /// 缺键省略纪律不变、永不回显该词）；日期口径与 DelayedOnDate/summary 三桶同族。
    /// 仅当语句引用 #tc(tc.OrderId/tc.MaxEnd) 时使用；#tc 与 summary 同型两段式（11-07 计划退化教训）。
    /// </summary>
    private const string RiskCaseExpr = @"CASE WHEN tc.OrderId IS NULL OR tc.MaxEnd IS NULL OR o.CustomerDueDate IS NULL
             THEN 'UNSCHEDULED'
             WHEN CAST(tc.MaxEnd AS date) > o.CustomerDueDate THEN 'DELAYED'
             ELSE 'ON_TRACK' END";

    /// <summary>两段式 #tc 前置文本**类级单源**（11-17 B2 收编：原列表/summary/facets 三处散本，
    /// 逐字同式属"同一口径"要件，第四处新用法（delivery-kpi）禁止再抄——11-07 计划退化教训的文本面）。</summary>
    private const string TcPreamble = @"SET NOCOUNT ON;
CREATE TABLE #tc(OrderId bigint PRIMARY KEY, MaxEnd datetime2(7));
INSERT #tc SELECT OrderId, MAX(PlannedEndTime) FROM [Task] WHERE PlanVersionId = @PlanVersionId GROUP BY OrderId;";

    public async Task<PageResult<OrderListItemDto>> QueryOrdersAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        string? sortBy = null,
        string? sortDir = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null)
    {
        // Dapper 列表参数只能出现在 IN 内；@X IS NULL(标量) 会随 @X 一起扩成 (@p1,@p2) → "(@p1,@p2) IS NULL" 非法 SQL 4145。
        // 用 Has 标志替代标量判空；空集 fail-closed 返空，避免空集传入 IN ()。
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new PageResult<OrderListItemDto> { Items = Array.Empty<OrderListItemDto>(), Total = 0, Page = 1, PageSize = take };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        // FROM / WHERE 单源常量见类级（11-12 R3：列表/COUNT/summary/facets 同一文本）。

        // 冻结 01:1908 第 2 项：预计完成时间 / 延期时长 / 主要原因（后端算，前端不得自算）。
        // 延期判定走 Q1 日期口径。
        // ⚠️ PrimaryReasonCode 必须过 07:4937 冻结 15 码白名单（4号位 10-19 U2：v2 前200行 78/79 为非冻结码
        // ——BATCH_POLICY_MISSING/中文Phase/MISSING_PRODUCTION_DEPARTMENT_CONTEXT，源头=1号位 ExplanationFactDraft
        //   产出+2号位透传落库，07:4935 门禁"不作正式原因验收"）。库内历史行保留不动（10-12 裁决各侧维持姿态），
        // 但**不得经本端点透出给用户**：白名单外的码按"无可用原因"（null）处理，不造假也不代 0号位 洗白。
        // 10-31 性能改造（4号位 R2-b）：三字段改为「分页后再批量富化」。
        // 原实现把两个 OUTER APPLY 挂在带 ORDER BY+OFFSET/FETCH 的 SELECT 上，
        // 实测优化器退化为逐行 0.25s（200 行 50s，前端 30s 超时）。APPLY 单独执行是 0ms，
        // 只有与 ORDER BY/OFFSET 同句才退化 ⇒ 拆两段：先取本页 200 行（无富化），再按 OrderId 批量聚合。
        // 语义逐字保留：MAX(PlannedEndTime)；原因码=15 码白名单内 ImpactHours 最大者（并列处理与原 TOP 1 相同，
        // 均为无序并列，未新增 tiebreaker——4号位 R4 要求语义变化须回执，本处无变化）。
        // 15 码白名单见类级常量 DeliveryReasonWhitelist（以下富化查询引用）。

        const string selectCols = @"
SELECT
    o.Id,
    o.PlanVersionId,
    o.OrderCanonicalId,
    o.OrderNo,
    o.OrderType,
    o.MaterialCode,
    o.MaterialId,
    o.CustomerName,
    o.CustomerSegment,
    f.Code AS FactoryCode,
    o.FactoryId,
    pf.Code AS ProductFamilyCode,
    o.DomainKey,
    o.Quantity,
    o.UOM,
    o.CustomerDueDate,
    o.PromisedDate,
    o.Priority,
    o.Status,
    o.DelayStatus,
    o.DemandMaturityStatus,
    o.MTS_InstructionNo,
    o.BOMNO";

        // R1（11-12 4号位件）：sortBy 白名单——**缺省排序逐字保留全链清扫原式**；每个自定义排序尾挂 o.Id 保分页确定性。
        // dir 仅二值（service 已校验；仓储防御性二值化，杜绝注入）。非法 sortBy 抛 ArgumentException→400（不静默忽略，R2 教训）。
        var dir = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderBy = (sortBy ?? "") switch
        {
            ""           => "ORDER BY o.Priority DESC, o.CustomerDueDate, o.Id",   // 现排序原样（4号位 10-19 §二；全链清扫 8 处之一）
            "dueDate"    => $"ORDER BY o.CustomerDueDate {dir}, o.Priority DESC, o.Id",
            "priority"   => $"ORDER BY o.Priority {dir}, o.CustomerDueDate ASC, o.Id",
            "orderNo"    => $"ORDER BY o.OrderNo {dir}, o.Id",
            "quantity"   => $"ORDER BY o.Quantity {dir}, o.Id",
            "riskLevel"  => $"ORDER BY CASE {RiskCaseExpr} WHEN 'DELAYED' THEN 0 WHEN 'ON_TRACK' THEN 1 ELSE 2 END {dir}, o.Id",
            _            => throw new ArgumentException($"非法 sortBy：{sortBy}（允许：dueDate/priority/orderNo/riskLevel/quantity 或缺省）")
        };

        // R2：riskLevel 筛选/排序 ⇒ 引用 #tc，走与 summary 同型两段式（11-07 计划退化教训：Task 聚合先落临时表再关联）。
        var needsRisk = !string.IsNullOrEmpty(riskLevel)
                        || string.Equals(sortBy, "riskLevel", StringComparison.OrdinalIgnoreCase);
        var tcJoin = needsRisk ? "\nLEFT JOIN #tc tc ON tc.OrderId = o.Id" : "";
        var riskPred = needsRisk && !string.IsNullOrEmpty(riskLevel)
            ? $"\n    AND ({RiskCaseExpr}) = @RiskLevel"
            : "";
        var tcPreamble = TcPreamble;   // 11-17 类级单源收编（原文本逐字入 TcPreamble，行为零变化）

        var dataSql = needsRisk
            ? tcPreamble + selectCols + FromJoin + tcJoin + WherePart + riskPred + $"\n{orderBy}\nOFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY\nDROP TABLE #tc;"
            : selectCols + FromJoin + tcJoin + WherePart + riskPred + $"\n{orderBy}\nOFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        var countSql = needsRisk
            ? tcPreamble + "SELECT COUNT(*)" + FromJoin + tcJoin + WherePart + riskPred + "\nDROP TABLE #tc;"
            : "SELECT COUNT(*)" + FromJoin + tcJoin + WherePart + riskPred;

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderNo = orderNo,
            MaterialCode = materialCode,
            CustomerName = customerName,
            FactoryCode = factoryCode,
            DomainKey = domainKey,
            DelayStatus = delayStatus,
            Status = status,
            ProductionInstructionNo = productionInstructionNo,
            ProductFamilyCode = productFamilyCode,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            RiskLevel = string.IsNullOrWhiteSpace(riskLevel) ? null : riskLevel,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains,
            Skip = skip,
            Take = take
        };

        var results = await _connectionManager.QueryAsync<OrderListItemDto>(
            dataSql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            countSql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var items = results.ToList();

        // 10-31 R2-b 两段式富化：先取本页（无 APPLY），再按 OrderId 批量聚合。
        // 原「APPLY 挂 ORDER BY+OFFSET」实测逐行 0.25s（200 行 50s → 前端 30s 超时）；
        // 拆后实测：Task 聚合 ~0.3s、原因码 ~0.01s（20 万单量级版本）。
        if (items.Count > 0)
        {
            var ids = string.Join(",", items.Select(i => i.Id));

            var plannedRows = await _connectionManager.QueryAsync<OrderPlannedCompletionRow>(
                @"SELECT t.OrderId, MAX(t.PlannedEndTime) AS PlannedCompletion
                    FROM [Task] t
                    WHERE t.PlanVersionId = @PlanVersionId
                      AND t.OrderId IN (SELECT CAST(value AS bigint) FROM STRING_SPLIT(@Ids, ','))
                    GROUP BY t.OrderId
                    OPTION (RECOMPILE)",
                new { PlanVersionId = planVersionId, Ids = ids },
                CommandType.Text, DatabaseId.APS, commandTimeout: 30);

            // 与原 TOP 1 ORDER BY ImpactHours DESC 同语义（并列均为无序，未新增 tiebreaker）
            var reasonRows = await _connectionManager.QueryAsync<OrderReasonRow>(
                $@"SELECT x.OrderId, x.ReasonCode
                     FROM (SELECT f.OrderId, f.ReasonCode,
                                  ROW_NUMBER() OVER (PARTITION BY f.OrderId ORDER BY f.ImpactHours DESC) AS rn
                             FROM ScheduleExplanationFact f
                            WHERE f.PlanVersionId = @PlanVersionId
                              AND f.OrderId IN (SELECT CAST(value AS bigint) FROM STRING_SPLIT(@Ids, ','))
                              AND f.ReasonCode IN ({DeliveryReasonWhitelist})) x
                    WHERE x.rn = 1",
                new { PlanVersionId = planVersionId, Ids = ids },
                CommandType.Text, DatabaseId.APS, commandTimeout: 30);

            var plannedById = plannedRows.ToDictionary(r => r.OrderId, r => r.PlannedCompletion);
            var reasonById = reasonRows.ToDictionary(r => r.OrderId, r => r.ReasonCode);

            foreach (var item in items)
            {
                plannedById.TryGetValue(item.Id, out var completion);
                item.PlannedCompletionTime = completion;
                item.PrimaryReasonCode = reasonById.TryGetValue(item.Id, out var code) ? code : null;
                item.DelayHours = CalcDelayHours(completion, item.CustomerDueDate);
                // 冻结 01 §64 计划派生延期判定（riskLevel，11-02 口径函 Q1）：无 Task 或无交期 ⇒ 缺键=不可判定（≠未延迟）；
                // 日期口径与 delayHours 同判据家族（Q1/DelayedOnDate 族）。AT_RISK 无源不出（OrderScheduleSummary 空表）。
                item.RiskLevel = completion is null || item.CustomerDueDate == default
                    ? null
                    : completion.Value.Date > item.CustomerDueDate.Date ? "DELAYED" : "ON_TRACK";
            }
        }

        return new PageResult<OrderListItemDto>
        {
            Items = items,
            Total = total,
            Page = take > 0 ? (skip / take) + 1 : 1,
            PageSize = take
        };
    }

    public async Task<OrderDetailDto?> GetOrderDetailAsync(
        int planVersionId,
        long orderId,
        CancellationToken ct = default)
    {
        // 1. 查询订单基本信息
        var orderSql = @"
SELECT
    o.Id,
    o.PlanVersionId,
    o.OrderCanonicalId,
    o.OrderNo,
    o.OrderType,
    o.MaterialCode,
    o.MaterialId,
    o.CustomerName,
    o.CustomerSegment,
    f.Code AS FactoryCode,
    o.FactoryId,
    pf.Code AS ProductFamilyCode,
    o.DomainKey,
    o.Quantity,
    o.UOM,
    o.CustomerDueDate,
    o.PromisedDate,
    o.Priority,
    o.Status,
    o.DelayStatus,
    o.DemandMaturityStatus,
    o.MTS_InstructionNo,
    o.BOMNO
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN ProductFamily pf ON pf.Id = o.ProductFamilyId
WHERE o.PlanVersionId = @PlanVersionId AND o.Id = @OrderId";

        var orderParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var order = (await _connectionManager.QueryAsync<OrderListItemDto>(
            orderSql, orderParams, CommandType.Text, DatabaseId.APS, commandTimeout: 10))
            .FirstOrDefault();

        if (order == null) return null;

        // 2. 查询Pegging承接
        var peggingSql = @"
SELECT
    AllocationSequence,
    MaterialCode,
    AllocatedQty,
    SupplyType,
    SupplyFactoryCode,
    SupplyWarehouseCode,
    ERPProperty,
    SupplyDocumentNo,
    SupplyDocumentType,
    ETA,
    KnownAvailableTime,
    CommitmentStatus,
    SupplyMode
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @PlanVersionId
    AND (RootOrderId = @OrderId OR CurrentOrderId = @OrderId)
ORDER BY AllocationSequence";

        var peggingParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var pegging = (await _connectionManager.QueryAsync<OrderPeggingDto>(
            peggingSql, peggingParams, CommandType.Text, DatabaseId.APS, commandTimeout: 30))
            .ToList();

        // 3. 查询生产计划（FinalTask）
        var taskSql = @"
SELECT
    t.Id AS TaskId,
    t.TaskNo,
    t.OperationCode,
    t.OperationSeq,
    r.ResourceCode,
    r.ResourceName,
    t.Quantity,
    t.PlannedProcessQty,
    t.PlannedStartTime,
    t.PlannedEndTime,
    t.Duration,
    t.Status,
    t.IsCriticalPath,
    t.IsLocked,
    t.MTS_InstructionNo
FROM Task t
LEFT JOIN Resource r ON r.Id = t.ResourceId
WHERE t.PlanVersionId = @PlanVersionId AND t.OrderId = @OrderId
ORDER BY t.OperationSeq";

        var taskParams = new { PlanVersionId = planVersionId, OrderId = orderId };
        var tasks = (await _connectionManager.QueryAsync<OrderTaskDto>(
            taskSql, taskParams, CommandType.Text, DatabaseId.APS, commandTimeout: 30))
            .ToList();

        return new OrderDetailDto
        {
            Order = order,
            Pegging = pegging,
            Tasks = tasks
        };
    }

    /// <summary>
    /// summary（R3，11-12 4号位件）：接受与列表**同名同义**的全部筛选参数 + riskLevel，
    /// FROM/WHERE 用类级单源常量 ⇒ KPI 与列表 total 天然同口径。
    /// **全部参数为空时行为与旧版逐字一致**（谓词全部短路；999472/999464 两版桶数已预跑对照）。
    /// </summary>
    public async Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        // Dapper 列表参数只进 IN；空集 fail-closed 返空摘要。
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new OrderSummaryDto { PlanVersionId = planVersionId };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        // 口径史：2026-10-17 ①NULL≠按期 ②RiskCount 改从 OrderScheduleSummary 取（该表现空⇒恒 0，不造假）。
        // ⚠ 2026-11-02 口径函 Q2 定稿（4号位 发现同页打架，本函数为根因之一）：
        //   OnTime/Delayed **不再按 o.DelayStatus**——它是订单入库期 ERP 独立维度（07:2688），与本版本排产无关
        //   （dev 实证：999328 的 5,490 单 FIRST_DELAY 全无 Task；999333 计划超期单反而挂 ON_TIME）。
        //   冻结 01 §64：计划延期唯一判定 = 本版本 MAX(PlannedEndTime) 日期 vs CustomerDueDate。
        //   UnscheduledCount（原"无权威口径⇒0"撤销）：本版本无 Task 行即未排产——§64 判定自带该判据，
        //   与列表富化同一批数据，**未排产单列、不得混入"未延迟"**。RiskCount 维持从 OrderScheduleSummary（空表⇒0）。
        // 11-07 修复（4号位 缺陷函「数据范围过滤致订单摘要超时」）：
        //   原把 Task 聚合写成内联派生表；带范围过滤时**首次编译**改走嵌套循环，Task 聚合被逐组重扫
        //   （实测 7,953 次 / 445,131 逻辑读 / 12.4s 撞 10s 超时；无过滤与缓存复用均为百毫秒级）。
        //   ⇒ 改两段式临时表：Task 聚合先落 #tc（一次扫描、主键基数明确），再与订单范围过滤关联。
        //   两参数域（无过滤 / Factory+Domain 过滤）实测均 ~90–110ms，确定性不靠提示。
        //   ⚠ 与订单列表的 RECOMPILE 病**方向相反**：此处坏在「新编译本身」，加 RECOMPILE 反而必现 12.2s；
        //   全局 OPTION(HASH JOIN) 也不可用——riskCount 子查询下提示会导致「无法生成计划」500（11-07 实测）。
        var sql = TcPreamble + @"
SELECT
    @PlanVersionId AS PlanVersionId,   -- 回填入参（此前漏挑该列 → Dapper 填默认 0，4号位 10-14 R5 报）
    COUNT(*) AS TotalCount,
    SUM(CASE WHEN tc.OrderId IS NOT NULL AND o.CustomerDueDate IS NOT NULL
              AND CAST(tc.MaxEnd AS date) <= o.CustomerDueDate THEN 1 ELSE 0 END) AS OnTimeCount,
    SUM(CASE WHEN tc.OrderId IS NOT NULL AND o.CustomerDueDate IS NOT NULL
              AND CAST(tc.MaxEnd AS date) >  o.CustomerDueDate THEN 1 ELSE 0 END) AS DelayedCount,
    (SELECT COUNT(*)
       FROM OrderScheduleSummary s
       INNER JOIN [Order] o2 ON o2.Id = s.OrderId AND o2.PlanVersionId = s.PlanVersionId
       LEFT JOIN Factory f2 ON f2.Id = o2.FactoryId
      WHERE s.PlanVersionId = @PlanVersionId
        AND s.RiskLevel IN ('AT_RISK', 'DELAYED')
        AND (@HasFactories = 0 OR f2.Code IN @AllowedFactories)
        AND (@HasDomains = 0 OR o2.DomainKey IN @AllowedDomains)) AS RiskCount,
    SUM(CASE WHEN tc.OrderId IS NULL OR o.CustomerDueDate IS NULL THEN 1 ELSE 0 END) AS UnscheduledCount"
            // R3：FROM/WHERE 接类级单源（与列表逐字同谓词）+ 可选 riskLevel（同 RiskCaseExpr 单源）。
            + FromJoin + "\nLEFT JOIN #tc tc ON tc.OrderId = o.Id" + WherePart
            + (string.IsNullOrWhiteSpace(riskLevel) ? "" : $"\n    AND ({RiskCaseExpr}) = @RiskLevel")
            + @";
DROP TABLE #tc;";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderNo = orderNo,
            MaterialCode = materialCode,
            CustomerName = customerName,
            FactoryCode = factoryCode,
            DomainKey = domainKey,
            DelayStatus = delayStatus,
            Status = status,
            ProductionInstructionNo = productionInstructionNo,
            ProductFamilyCode = productFamilyCode,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            RiskLevel = string.IsNullOrWhiteSpace(riskLevel) ? null : riskLevel,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains
        };

        var result = await _connectionManager.QueryFirstOrDefaultAsync<OrderSummaryDto>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 10);

        return result ?? new OrderSummaryDto { PlanVersionId = planVersionId };
    }

    /// <summary>
    /// R4（11-12 4号位件）：订单口径可选值+命中条数。**与列表同一 FROM/WHERE/同一范围收窄**（类级单源），
    /// 六维一次 GROUPING SETS 扫描；riskLevel 维与三桶同式（RiskCaseExpr）。
    /// Val=NULL 组（如 DelayStatus 空值行）不回显（前端无从按值过滤），仅计不进下拉。
    /// 每维按条数降序截 200（超限维在回执中说明，当前 dev 最大客户数远小于）。
    /// </summary>
    public async Task<OrderFacetsDto> GetFacetsAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new OrderFacetsDto { PlanVersionId = planVersionId };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        var sql = TcPreamble + @"
WITH base AS (
    SELECT o.Id, o.CustomerName, f.Code AS FactoryCode, pf.Code AS ProductFamilyCode,
           o.Status AS OrderStatus, o.DelayStatus,
           " + RiskCaseExpr + @" AS RiskLevel"
            + FromJoin + "\n    LEFT JOIN #tc tc ON tc.OrderId = o.Id" + WherePart
            + (string.IsNullOrWhiteSpace(riskLevel) ? "" : $"\n    AND ({RiskCaseExpr}) = @RiskLevel")
            + @")
SELECT
    CASE WHEN GROUPING(CustomerName)=0      THEN 'customerName'
         WHEN GROUPING(FactoryCode)=0       THEN 'factoryCode'
         WHEN GROUPING(ProductFamilyCode)=0 THEN 'productFamilyCode'
         WHEN GROUPING(OrderStatus)=0       THEN 'status'
         WHEN GROUPING(DelayStatus)=0       THEN 'delayStatus'
         ELSE 'riskLevel' END AS Dim,
    COALESCE(CustomerName, FactoryCode, ProductFamilyCode, OrderStatus, DelayStatus, RiskLevel) AS Val,
    COUNT(*) AS Cnt
FROM base
GROUP BY GROUPING SETS ((CustomerName),(FactoryCode),(ProductFamilyCode),(OrderStatus),(DelayStatus),(RiskLevel))
ORDER BY Dim, Cnt DESC;
DROP TABLE #tc;";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderNo = orderNo,
            MaterialCode = materialCode,
            CustomerName = customerName,
            FactoryCode = factoryCode,
            DomainKey = domainKey,
            DelayStatus = delayStatus,
            Status = status,
            ProductionInstructionNo = productionInstructionNo,
            ProductFamilyCode = productFamilyCode,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            RiskLevel = string.IsNullOrWhiteSpace(riskLevel) ? null : riskLevel,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains
        };

        var rows = (await _connectionManager.QueryAsync<OrderFacetRow>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        static List<OrderFacetItemDto> Pick(List<OrderFacetRow> all, string dim) =>
            all.Where(r => r.Dim == dim && r.Val is not null)
               .Select(r => new OrderFacetItemDto { Value = r.Val, Count = r.Cnt })
               .Take(200)
               .ToList();

        return new OrderFacetsDto
        {
            PlanVersionId = planVersionId,
            CustomerNames = Pick(rows, "customerName"),
            FactoryCodes = Pick(rows, "factoryCode"),
            ProductFamilyCodes = Pick(rows, "productFamilyCode"),
            Statuses = Pick(rows, "status"),
            DelayStatuses = Pick(rows, "delayStatus"),
            RiskLevels = Pick(rows, "riskLevel"),
        };
    }

    /// <summary>facets 行（非契约 DTO）。</summary>
    private sealed class OrderFacetRow
    {
        public string Dim { get; set; } = string.Empty;
        public string? Val { get; set; }
        public int Cnt { get; set; }
    }

    /// <summary>
    /// B2（11-17，0号位 Q-c 裁）：P02 交付 KPI 分区聚合——**判定唯一复用 RiskCaseExpr**（计划派生、日期级、
    /// 当天完成=按期；禁 ERP `DelayStatus` 桶代——那是 summary 现口径，语义另一本账；禁1号位内存 OnTimeRate；
    /// `OrderScheduleSummary` 空表不当硬依赖）。分母=分区全部授权去重根订单**含未判定**（"不得偷偷换分母"）。
    /// 与列表/COUNT/summary/facets 同 WHERE+同 #tc 文本（类级单源，GROUPING SETS 两层：() 总计行 + (OrderType) SO/PI 分区）。
    /// FG/WIP 维度**当前无权威映射**（ProductFamily 无此属性、订单物料含 SEMI_FINISHED、FINAL_ 前缀禁作判据，
    /// 实库 11-17 核）⇒ FgWipDimensionAvailable=false，该层分区等待源（最小问题已列复函）。
    /// </summary>
    public async Task<DeliveryKpiResultDto> GetDeliveryKpiAsync(
        int planVersionId,
        string? orderNo = null,
        string? materialCode = null,
        string? customerName = null,
        string? factoryCode = null,
        string? domainKey = null,
        string? delayStatus = null,
        string? status = null,
        string? productionInstructionNo = null,
        string? productFamilyCode = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        string? riskLevel = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        if (allowedFactories is { Count: 0 } || allowedDomains is { Count: 0 })
            return new DeliveryKpiResultDto { PlanVersionId = planVersionId };
        var hasFactories = allowedFactories is { Count: > 0 };
        var hasDomains = allowedDomains is { Count: > 0 };

        var header = await _connectionManager.QueryFirstOrDefaultAsync<KpiPlanVersionRow>(
            "SELECT TOP 1 Id, DomainKey, Status FROM PlanVersion WHERE Id = @Pv",
            new { Pv = planVersionId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        if (header is null || header.Id == 0)
            return new DeliveryKpiResultDto { PlanVersionId = planVersionId, VersionExists = false };

        var riskPred = string.IsNullOrWhiteSpace(riskLevel)
            ? ""
            : $"\n    AND ({RiskCaseExpr}) = @RiskLevel";

        var parameters = new
        {
            PlanVersionId = planVersionId,
            OrderNo = orderNo,
            MaterialCode = materialCode,
            CustomerName = customerName,
            FactoryCode = factoryCode,
            DomainKey = domainKey,
            DelayStatus = delayStatus,
            Status = status,
            ProductionInstructionNo = productionInstructionNo,
            ProductFamilyCode = productFamilyCode,
            DueDateFrom = dueDateFrom,
            DueDateTo = dueDateTo,
            RiskLevel = string.IsNullOrWhiteSpace(riskLevel) ? null : riskLevel,
            HasFactories = hasFactories,
            HasDomains = hasDomains,
            AllowedFactories = allowedFactories,
            AllowedDomains = allowedDomains
        };

        var sql = TcPreamble + @"
SELECT
    GROUPING(o.OrderType) AS IsGrandTotal,
    GROUPING(m.MaterialType) AS IsMT,
    o.OrderType,
    m.MaterialType,
    COUNT(*) AS TotalOrders,
    SUM(CASE WHEN " + RiskCaseExpr + @" = 'ON_TRACK' THEN 1 ELSE 0 END) AS OnTimeOrders,
    SUM(CASE WHEN " + RiskCaseExpr + @" = 'DELAYED' THEN 1 ELSE 0 END) AS LateOrders,
    SUM(CASE WHEN " + RiskCaseExpr + @" = 'UNSCHEDULED' THEN 1 ELSE 0 END) AS UndeterminedOrders"
            + FromJoin + "\nLEFT JOIN #tc tc ON tc.OrderId = o.Id"
            // 0号位 11-20 裁：FG/WIP=标准 Material 分类（MaterialType），四组 GROUPING SETS 逐行出全、不合并。
            + "\nLEFT JOIN Material m ON m.Id = o.MaterialId"
            + WherePart + riskPred + @"
GROUP BY GROUPING SETS ((o.OrderType), (m.MaterialType), (m.MaterialType, o.OrderType), ())
ORDER BY IsGrandTotal DESC, IsMT DESC, m.MaterialType, o.OrderType
DROP TABLE #tc;";

        var raw = (await _connectionManager.QueryAsync<KpiRow>(
            sql, parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        return new DeliveryKpiResultDto
        {
            PlanVersionId = planVersionId,
            DomainKey = header.DomainKey,
            Caliber = "计划预计按期率（非实际交付准交率）；日期级判定、当天完成=按期（0号位裁）；"
                      + "计划完成=同PV Task MAX(PlannedEndTime) 读模型计算；分母=去重根订单含未判定",
            PlannedCompletionSource = "readmodel_task_max",
            FgWipDimensionAvailable = true,   // 0号位 11-20 裁：标准 MaterialType 为唯一判据（禁前缀/ERP属性/Routing 推断）
            TargetPercent = null,   // 待 3号位 合同核对（Q-c：我方不自选配置源）
            Rows = raw.Select(r => new DeliveryKpiRowDto
            {
                OrderType = r.IsGrandTotal == 1 ? null : r.OrderType,
                MaterialType = r.IsMT == 1 ? null : r.MaterialType,
                TotalOrders = r.TotalOrders,
                OnTimeOrders = r.OnTimeOrders,
                LateOrders = r.LateOrders,
                UndeterminedOrders = r.UndeterminedOrders,
                OnTimeRatePct = r.TotalOrders == 0 ? (decimal?)null : Math.Round((decimal)r.OnTimeOrders / r.TotalOrders * 100m, 1),
                LateRatePct = r.TotalOrders == 0 ? (decimal?)null : Math.Round((decimal)r.LateOrders / r.TotalOrders * 100m, 1)
            }).ToList()
        };
    }

    private sealed class KpiPlanVersionRow
    {
        public int Id { get; set; }
        public string? DomainKey { get; set; }
        public string? Status { get; set; }
    }

    private sealed class KpiRow
    {
        public int IsGrandTotal { get; set; }
        public int IsMT { get; set; }
        public string? OrderType { get; set; }
        public string? MaterialType { get; set; }
        public int TotalOrders { get; set; }
        public int OnTimeOrders { get; set; }
        public int LateOrders { get; set; }
        public int UndeterminedOrders { get; set; }
    }

    // ───────────── B3/B4（11-17）：订单完整树（P02 行展开＝P03 深链同一端点，不建第二套） ─────────────
    // 结构真相=2号位 Pegging 图（按 PV 隔离）；种子=Task.OrderId 直挂 ∪ share.RootOrderId 两路（D05-12：
    // RootOrderNo 列空、RootOrderId 反查 [Order] 100% 命中，两路并集最全）。方向=上游闭包（组件树，
    // 路径表防环，深度≤10=workset 展开上限 R? 冻结 10 层同族）。数量账=PSA 行原值；Q/S/U 走既有
    // demand-reconciliation 出口（本方法**不重复**）；StageTimingNode 可证性挂 Q03 ⇒ 固定"未提供"注。
    // 截断三态独立（edges 1500/nodes 500/demands 500 缺省）——"已加载≠全部"做进结构。

    public async Task<OrderTreeResultDto> GetOrderTreeAsync(
        int planVersionId,
        long orderId,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        int nodeLimit = 500,
        int edgeLimit = 1500,
        int demandLimit = 500,
        CancellationToken ct = default)
    {
        if (allowedDomains is { Count: 0 } || allowedFactories is { Count: 0 })
            return new OrderTreeResultDto { PlanVersionId = planVersionId, OrderId = orderId, Found = true, ScopeDenied = true };
        var hasD = allowedDomains is { Count: > 0 };
        var hasF = allowedFactories is { Count: > 0 };

        var header = await _connectionManager.QueryFirstOrDefaultAsync<TreeHeaderRow>(
            @"SELECT TOP 1 o.Id, o.OrderNo, o.OrderType, o.MaterialCode, o.CustomerDueDate, o.Status,
                     o.DomainKey, pv.Status AS PlanVersionStatus, sr.DataCutoffTime, f.Code AS FactoryCode
              FROM [Order] o
              INNER JOIN PlanVersion pv ON pv.Id = o.PlanVersionId
              LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
              LEFT JOIN Factory f ON f.Id = o.FactoryId
              WHERE o.PlanVersionId = @Pv AND o.Id = @Or",
            new { Pv = planVersionId, Or = orderId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        if (header is null || header.Id == 0)
            return new OrderTreeResultDto { PlanVersionId = planVersionId, OrderId = orderId, Found = false };
        if ((hasD && allowedDomains!.Contains(header.DomainKey ?? "") is false)
            || (hasF && header.FactoryCode is not null && allowedFactories!.Contains(header.FactoryCode) is false))
            return new OrderTreeResultDto { PlanVersionId = planVersionId, OrderId = orderId, Found = true, ScopeDenied = true };

        var seedIds = (await _connectionManager.QueryAsync<long>(
            @"SELECT DISTINCT t.Id FROM Task t WHERE t.PlanVersionId = @Pv AND t.OrderId = @Or
              UNION
              SELECT s.TaskId FROM AllocationTaskShare s WHERE s.PlanVersionId = @Pv AND s.RootOrderId = @Or",
            new { Pv = planVersionId, Or = orderId }, CommandType.Text, DatabaseId.APS, commandTimeout: 15)).ToList();

        var edgeRows = new List<TreeEdgeRow>();
        if (seedIds.Count > 0)
        {
            edgeRows = (await _connectionManager.QueryAsync<TreeEdgeRow>(@"
;WITH seed AS (
    SELECT t.Id AS TaskId FROM Task t WHERE t.PlanVersionId = @Pv AND t.OrderId = @Or
    UNION
    SELECT s.TaskId FROM AllocationTaskShare s WHERE s.PlanVersionId = @Pv AND s.RootOrderId = @Or
),
closure AS (
    SELECT CAST(NULL AS bigint) AS ParentTaskId, sd.TaskId AS ChildTaskId, 0 AS Depth,
           CONVERT(varchar(max), CONCAT(',', sd.TaskId, ',')) AS Path
    FROM seed sd
    UNION ALL
    -- 前沿=本行距种子最远的端点：种子行（Parent 为 NULL）前沿在 Child；其后行前沿在 Parent。
    -- （两版弯路 11-17 预跑抓出：①续走 Child＝原地只出 1 跳；②只认 Parent＝种子行断扩。COALESCE 才对。）
    SELECT p.UpstreamTaskId, COALESCE(c.ParentTaskId, c.ChildTaskId), c.Depth + 1,
           CONVERT(varchar(max), CONCAT(c.Path, p.UpstreamTaskId, ','))
    FROM closure c
    JOIN Pegging p ON p.PlanVersionId = @Pv
       AND p.DownstreamTaskId = COALESCE(c.ParentTaskId, c.ChildTaskId)
    WHERE c.Depth < 10
      AND c.Path NOT LIKE CONCAT('%,', p.UpstreamTaskId, ',%')
)
SELECT TOP (@EdgeCap) c.ParentTaskId, c.ChildTaskId, c.Depth,
       p.Quantity, p.UOM AS Uom, p.PeggingType, p.LeadTimeDays, p.IsCrossDomain, p.AllocationReason
FROM closure c
JOIN Pegging p ON p.PlanVersionId = @Pv
   AND p.UpstreamTaskId = c.ParentTaskId AND p.DownstreamTaskId = c.ChildTaskId
WHERE c.ParentTaskId IS NOT NULL
ORDER BY c.Depth, c.ChildTaskId, c.ParentTaskId
OPTION (MAXRECURSION 12);",
                new { Pv = planVersionId, Or = orderId, EdgeCap = edgeLimit + 1 },
                CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();
        }
        var edgesTruncated = edgeRows.Count > edgeLimit;
        if (edgesTruncated) edgeRows = edgeRows.Take(edgeLimit).ToList();

        // 节点全集=种子∪边两端；深度=距种子最小值（父=该边 Depth，子=Depth−1，种子恒 0）
        var depthMap = new Dictionary<long, int>();
        foreach (var id in seedIds) depthMap[id] = 0;
        foreach (var e in edgeRows)
        {
            if (!depthMap.TryGetValue(e.ParentTaskId, out var pd) || e.Depth < pd) depthMap[e.ParentTaskId] = e.Depth;
            var cd = Math.Max(0, e.Depth - 1);
            if (!depthMap.TryGetValue(e.ChildTaskId, out var ex) || cd < ex) depthMap[e.ChildTaskId] = cd;
        }
        var nodeIds = depthMap.Keys.ToList();
        var nodesTruncated = nodeIds.Count > nodeLimit;
        if (nodesTruncated) nodeIds = nodeIds.OrderBy(i => depthMap[i]).Take(nodeLimit).ToList();

        var nodeRows = nodeIds.Count == 0
            ? new List<TreeNamedRow>()
            : (await _connectionManager.QueryAsync<TreeNamedRow>(
                @"SELECT t.Id AS TaskId, t.TaskNo, t.MaterialId, m.MaterialCode, m.MaterialName,
                         t.StageCode, t.OperationSeq, t.OperationName, t.Quantity, t.UOM AS Uom,
                         t.PlannedStartTime AS PlannedStart, t.PlannedEndTime AS PlannedEnd,
                         t.Status, t.IsLocked, t.MESWorkOrderNo AS MesWorkOrderNo,
                         t.ProductionDepartmentId, pd.DeptName AS ProductionDepartmentName,
                         t.ResourceId, r.ResourceCode, r.ResourceName,
                         t.ManualSlotId, ms.SlotCode AS ManualSlotCode,
                         CASE WHEN t.OrderId = @Or THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS OwnRootOrder
                  FROM Task t
                  LEFT JOIN Material m ON m.Id = t.MaterialId
                  LEFT JOIN Resource r ON r.Id = t.ResourceId
                  LEFT JOIN ManualCapacitySlot ms ON ms.ManualSlotId = t.ManualSlotId
                  LEFT JOIN ProductionDepartment pd ON pd.Id = t.ProductionDepartmentId
                  WHERE t.PlanVersionId = @Pv AND t.Id IN @Ids",
                new { Pv = planVersionId, Or = orderId, Ids = nodeIds },
                CommandType.Text, DatabaseId.APS, commandTimeout: 20)).ToList();

        // A2（11-18）：链节点的份额解释两列——单查一次按 TaskId 回填（组件链常无份额行 ⇒ null，非 0）。
        var shareRows = nodeIds.Count == 0
            ? new List<TreeShareRow>()
            : (await _connectionManager.QueryAsync<TreeShareRow>(@"
SELECT s.TaskId,
       SUM(CASE WHEN s.RootOrderId = @Or THEN s.ShareQty END) AS OwnShareQty,
       COUNT(DISTINCT s.RootOrderId) AS ShareRootCount
FROM AllocationTaskShare s
WHERE s.PlanVersionId = @Pv AND s.TaskId IN @Ids
GROUP BY s.TaskId",
                new { Pv = planVersionId, Or = orderId, Ids = nodeIds },
                CommandType.Text, DatabaseId.APS, commandTimeout: 20)).ToList();
        var shareMap = shareRows.ToDictionary(r => r.TaskId);

        var demandsRaw = (await _connectionManager.QueryAsync<OrderTreeDemandDto>(@"
SELECT TOP (@DemandCap)
    AllocationSequence, MaterialId, MaterialCode, DemandStageCode,
    DemandQty, AllocatedQty, DemandQty - AllocatedQty AS GapQty,
    SupplyType, SupplyDocumentNo, SupplyFactoryCode, SupplyMode, CommitmentStatus,
    ETA AS Eta, KnownAvailableTime,
    AttachStageCode, CompletedStageCode, NextRequiredStageCode, RemainingStagePathJson
FROM PeggingSupplyAllocation
WHERE PlanVersionId = @Pv AND RootOrderId = @Or
ORDER BY AllocationSequence",
            new { Pv = planVersionId, Or = orderId, DemandCap = demandLimit + 1 },
            CommandType.Text, DatabaseId.APS, commandTimeout: 20)).ToList();
        var demandsTruncated = demandsRaw.Count > demandLimit;

        var seedSet = seedIds.ToHashSet();
        return new OrderTreeResultDto
        {
            PlanVersionId = planVersionId,
            DomainKey = header.DomainKey,
            PlanVersionStatus = header.PlanVersionStatus,
            DataCutoffTime = header.DataCutoffTime,
            Found = true,
            OrderId = header.Id,
            OrderNo = header.OrderNo,
            OrderType = header.OrderType,
            MaterialCode = header.MaterialCode,
            CustomerDueDate = header.CustomerDueDate,
            Status = header.Status,
            RootHasOwnTasks = seedIds.Count > 0,
            Nodes = nodeRows.Select(r => new OrderTreeNodeDto
            {
                TaskId = r.TaskId, TaskNo = r.TaskNo, MaterialId = r.MaterialId,
                MaterialCode = r.MaterialCode, MaterialName = r.MaterialName,
                StageCode = r.StageCode, OperationSeq = r.OperationSeq, OperationName = r.OperationName,
                Quantity = r.Quantity, Uom = r.Uom,
                PlannedStart = r.PlannedStart, PlannedEnd = r.PlannedEnd,
                Status = r.Status, IsLocked = r.IsLocked, MesWorkOrderNo = r.MesWorkOrderNo,
                ProductionDepartmentId = r.ProductionDepartmentId, ProductionDepartmentName = r.ProductionDepartmentName,
                ResourceId = r.ResourceId, ResourceCode = r.ResourceCode, ResourceName = r.ResourceName,
                ManualSlotId = r.ManualSlotId, ManualSlotCode = r.ManualSlotCode,
                OwnRootOrder = r.OwnRootOrder, InSeed = seedSet.Contains(r.TaskId),
                MinDepth = depthMap.TryGetValue(r.TaskId, out var d) ? d : 0,
                OwnShareQty = shareMap.TryGetValue(r.TaskId, out var shr) ? shr.OwnShareQty : null,
                ShareRootCount = shareMap.TryGetValue(r.TaskId, out var shr2) ? shr2.ShareRootCount : 0
            }).ToList(),
            Edges = edgeRows.Select(e => new OrderTreeEdgeDto
            {
                ParentTaskId = e.ParentTaskId, ChildTaskId = e.ChildTaskId, Depth = e.Depth,
                Quantity = e.Quantity, Uom = e.Uom, PeggingType = e.PeggingType,
                LeadTimeDays = e.LeadTimeDays, IsCrossDomain = e.IsCrossDomain, AllocationReason = e.AllocationReason
            }).ToList(),
            Demands = demandsTruncated ? demandsRaw.Take(demandLimit).ToList() : demandsRaw,
            NodesTruncated = nodesTruncated,
            EdgesTruncated = edgesTruncated,
            DemandsTruncated = demandsTruncated,
            StageTimingNote = "StageTimingNode（无 Routing 的 Stage 时间节点）当前不可证可查（§八 Q03 待 2号位核证）=未提供；页面在完整链对应位如实标注，禁前端自造节点"
        };
    }

    // ───────────── C2（11-18）：P09 选单池（新/老订单按 v0.2 原文判据=ACTIVE PV 参与关系） ─────────────
    // 判据具体化（v0.2 §P09「依据当前真实 FULL/ACTIVE 及合法根订单参与关系」）：
    //   SCHEDULED=订单号∈当前 ACTIVE PV 的 [Order] 装载集（权威=2号位装载；场景5/6 候选）；
    //   NOT_IN_PLAN=Order_Canonical∈、ACTIVE PV∉（场景1/2 候选）。
    // **禁 CreatedAt/单号前缀猜新老**（v0.2 点名禁令）。新单归属估算链=物料→ProductFamily→域族
    // （999473 实测 PV 载体 10,247 行 100% 同族反证；FACTORY_FAMILY 域再叠厂码过滤）。
    // count 与 page 共用同一段 SQL 文本（局部组装，非跨方法复制）。
    public async Task<OrderPoolResultDto> GetOrderPoolAsync(
        int planVersionId,
        string? participation = null,
        string? keyword = null,
        string? orderType = null,
        int pageIndex = 1,
        int pageSize = 50,
        IReadOnlySet<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        var part = string.IsNullOrWhiteSpace(participation) ? "all" : participation.Trim().ToLowerInvariant();
        if (part is not ("all" or "scheduled" or "fresh"))
            throw new ArgumentException($"非法 participation：{participation}（允许：all/scheduled/fresh）");

        var header = await _connectionManager.QueryFirstOrDefaultAsync<PoolPvHeader>(
            @"SELECT TOP 1 pv.Id, pv.DomainKey, pv.Status AS PlanVersionStatus
              FROM PlanVersion pv WHERE pv.Id = @Pv",
            new { Pv = planVersionId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        if (header is null || header.Id == 0)
            return new OrderPoolResultDto { PlanVersionId = planVersionId, VersionExists = false };
        if (allowedDomains is { Count: 0 } || (allowedDomains is not null && !allowedDomains.Contains(header.DomainKey)))
            return new OrderPoolResultDto { PlanVersionId = planVersionId, ScopeDenied = true };
        if (!string.Equals(header.PlanVersionStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return new OrderPoolResultDto
            {
                PlanVersionId = planVersionId,
                DomainKey = header.DomainKey,
                NoActiveNote = "选单池以该域当前 ACTIVE 版本为参照，所传 PV 非 ACTIVE——请走 ACTIVE 六域锚表取数"
            };

        var dom = await _connectionManager.QueryFirstOrDefaultAsync<PoolDomainRow>(
            @"SELECT dd.ProductFamilyId, dd.ScopeType, f.Code AS FactoryCode
              FROM DomainDefinition dd
              LEFT JOIN Factory f ON f.Id = dd.FactoryId
              WHERE dd.DomainKey = @DomainKey",
            new { DomainKey = header.DomainKey }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        // 域字典缺归属时不炸：@Fam=0 使 fresh 段自然空，只回装载侧＋注记（装载侧仍是权威数据）。
        string? governanceNote = null;
        if (dom?.ProductFamilyId is null or 0)
            governanceNote = "域字典缺 ProductFamilyId 归属，新单池估算不可判（登记待治理）；本响应只回 ACTIVE 装载侧";

        var page = Math.Max(1, pageIndex);
        var size = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
        var parameters = new
        {
            Pv = planVersionId,
            Fam = dom?.ProductFamilyId ?? 0,
            DomFactory = string.Equals(dom.ScopeType, "FACTORY_FAMILY", StringComparison.OrdinalIgnoreCase) ? dom.FactoryCode : null,
            P = part,
            Kw = string.IsNullOrWhiteSpace(keyword) ? null : "%" + keyword.Trim() + "%",
            Ot = string.IsNullOrWhiteSpace(orderType) ? null : orderType.Trim(),
            Skip = (page - 1) * size,
            Take = size
        };

        var sqlBody = @"
sched AS (
    SELECT o.OrderNo, o.Id AS OrderId, o.OrderCanonicalId,
           o.OrderType, o.MaterialCode, m.MaterialName,
           o.Quantity, o.UOM AS Uom, o.CustomerDueDate AS DueDate,
           NULLIF(o.CustomerName, '') AS CustomerName, o.Status,
           'SCHEDULED' AS Participation,
           CASE WHEN EXISTS (SELECT 1 FROM Task t WHERE t.PlanVersionId = @Pv AND t.OrderId = o.Id)
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasPlanningTasks,
           o.UpdatedAt AS ScheduledTime
    FROM [Order] o
    LEFT JOIN Material m ON m.Id = o.MaterialId
    WHERE o.PlanVersionId = @Pv AND (@P = 'all' OR @P = 'scheduled')
),
fresh AS (
    SELECT c.OrderNo, CAST(NULL AS bigint) AS OrderId, c.Id AS OrderCanonicalId,
           c.OrderType, c.MaterialCode, m.MaterialName,
           c.Quantity, c.UOM AS Uom, c.DueDate,
           NULLIF(c.CustomerName, '') AS CustomerName, CAST(NULL AS nvarchar(50)) AS Status,
           'NOT_IN_PLAN' AS Participation,
           CAST(NULL AS bit) AS HasPlanningTasks, CAST(NULL AS datetime2(7)) AS ScheduledTime
    FROM Order_Canonical c
    JOIN Material m ON m.MaterialCode = c.MaterialCode
    WHERE m.ProductFamilyId = @Fam
      AND (@DomFactory IS NULL OR c.FactoryCode = @DomFactory)
      AND NOT EXISTS (SELECT 1 FROM [Order] o WHERE o.PlanVersionId = @Pv AND o.OrderNo = c.OrderNo)
      AND (@P = 'all' OR @P = 'fresh')
),
pool AS (
    SELECT * FROM sched
    UNION ALL
    SELECT * FROM fresh
)";
        var sqlFilter = @"
WHERE (@Kw IS NULL OR OrderNo LIKE @Kw OR MaterialCode LIKE @Kw OR ISNULL(CustomerName, '') LIKE @Kw)
  AND (@Ot IS NULL OR OrderType = @Ot)";

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SET NOCOUNT ON; WITH " + sqlBody + "\nSELECT COUNT(*) FROM pool" + sqlFilter,
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var items = (await _connectionManager.QueryAsync<OrderPoolItemDto>(
            "SET NOCOUNT ON; WITH " + sqlBody + @"
SELECT OrderNo, OrderId, OrderCanonicalId, OrderType, MaterialCode, MaterialName, Quantity, Uom, DueDate, CustomerName, Status,
       Participation, HasPlanningTasks, ScheduledTime
FROM pool" + sqlFilter + @"
ORDER BY DueDate, OrderNo
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        return new OrderPoolResultDto
        {
            PlanVersionId = planVersionId,
            DomainKey = header.DomainKey,
            NoActiveNote = governanceNote,
            Items = items,
            Total = total,
            Page = page,
            PageSize = size
        };
    }

    private sealed class PoolPvHeader
    {
        public int Id { get; set; }
        public string DomainKey { get; set; } = string.Empty;
        public string? PlanVersionStatus { get; set; }
    }

    private sealed class PoolDomainRow
    {
        public int? ProductFamilyId { get; set; }
        public string? ScopeType { get; set; }
        public string? FactoryCode { get; set; }
    }

    private sealed class TreeHeaderRow
    {
        public long Id { get; set; }
        public string? OrderNo { get; set; }
        public string? OrderType { get; set; }
        public string? MaterialCode { get; set; }
        public DateTime? CustomerDueDate { get; set; }
        public string? Status { get; set; }
        public string? DomainKey { get; set; }
        public string? PlanVersionStatus { get; set; }
        public DateTime? DataCutoffTime { get; set; }
        public string? FactoryCode { get; set; }
    }

    private sealed class TreeEdgeRow
    {
        public long ParentTaskId { get; set; }
        public long ChildTaskId { get; set; }
        public int Depth { get; set; }
        public decimal Quantity { get; set; }
        public string? Uom { get; set; }
        public string? PeggingType { get; set; }
        public int? LeadTimeDays { get; set; }
        public bool IsCrossDomain { get; set; }
        public string? AllocationReason { get; set; }
    }

    private sealed class TreeShareRow
    {
        public long TaskId { get; set; }
        public decimal? OwnShareQty { get; set; }
        public int ShareRootCount { get; set; }
    }

    private sealed class TreeNamedRow
    {
        public long TaskId { get; set; }
        public string? TaskNo { get; set; }
        public int MaterialId { get; set; }
        public string? MaterialCode { get; set; }
        public string? MaterialName { get; set; }
        public string? StageCode { get; set; }
        public int OperationSeq { get; set; }
        public string? OperationName { get; set; }
        public int? ProductionDepartmentId { get; set; }
        public string? ProductionDepartmentName { get; set; }
        public int? ResourceId { get; set; }
        public string? ResourceCode { get; set; }
        public string? ResourceName { get; set; }
        public int? ManualSlotId { get; set; }
        public string? ManualSlotCode { get; set; }
        public decimal Quantity { get; set; }
        public string Uom { get; set; } = string.Empty;
        public DateTime? PlannedStart { get; set; }
        public DateTime? PlannedEnd { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public string? MesWorkOrderNo { get; set; }
        public bool OwnRootOrder { get; set; }
    }

    /// <summary>
    /// 延期小时（与旧 SQL `DATEDIFF(MINUTE, CAST(due AS datetime2), completion)/60.0` 等价）：
    /// 仅当 completion 的**日期** &gt; due 的**日期**时给值（Q1 日期口径）；未排/未延期=null（两态不混）。
    /// DTO 的 CustomerDueDate 为非空 DateTime，库内 NULL 会落成 0001-01-01，此处按 null 处理（等价 SQL 的 IS NOT NULL 判据）。
    /// </summary>
    private static decimal? CalcDelayHours(DateTime? completion, DateTime due)
    {
        if (completion is null) return null;
        if (due == default) return null;
        if (completion.Value.Date <= due.Date) return null;
        var minutes = (long)Math.Floor((completion.Value - due).TotalMinutes);
        return minutes / 60m;
    }

    /// <summary>两段式富化行：订单预计完成（MAX(PlannedEndTime) 按 OrderId 聚合）</summary>
    private sealed class OrderPlannedCompletionRow
    {
        public long OrderId { get; set; }
        public DateTime? PlannedCompletion { get; set; }
    }

    /// <summary>两段式富化行：订单主要原因码（15 码白名单内 ImpactHours 最大者）</summary>
    private sealed class OrderReasonRow
    {
        public long OrderId { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
    }
}
