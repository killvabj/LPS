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

        // 共用 FROM / WHERE：列表与 COUNT 的 FROM+WHERE 必须逐字一致，否则 total 与 items 对不上。
        // （10-19 拆分：为「冻结 01:1908 三字段」加的 OUTER APPLY 只挂明细查询，不污染 COUNT 性能与行数语义。）
        const string fromJoin = @"
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN ProductFamily pf ON pf.Id = o.ProductFamilyId";

        const string wherePart = @"
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

        var dataSql = @"
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
    o.BOMNO"
            + fromJoin + wherePart + @"
ORDER BY o.Priority DESC, o.CustomerDueDate, o.Id   -- tiebreaker：分页确定性（4号位 10-19 §二；全链清扫 8 处之一）
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var countSql = "SELECT COUNT(*)" + fromJoin + wherePart;

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

    public async Task<OrderSummaryDto> GetSummaryAsync(
        int planVersionId,
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
        var sql = @"
SET NOCOUNT ON;
CREATE TABLE #tc(OrderId bigint PRIMARY KEY, MaxEnd datetime2(7));
INSERT #tc SELECT OrderId, MAX(PlannedEndTime) FROM [Task] WHERE PlanVersionId = @PlanVersionId GROUP BY OrderId;
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
    SUM(CASE WHEN tc.OrderId IS NULL OR o.CustomerDueDate IS NULL THEN 1 ELSE 0 END) AS UnscheduledCount
FROM [Order] o
LEFT JOIN Factory f ON f.Id = o.FactoryId
LEFT JOIN #tc tc ON tc.OrderId = o.Id
WHERE o.PlanVersionId = @PlanVersionId
    AND (@HasFactories = 0 OR f.Code IN @AllowedFactories)
    AND (@HasDomains = 0 OR o.DomainKey IN @AllowedDomains);
DROP TABLE #tc;";

        var parameters = new
        {
            PlanVersionId = planVersionId,
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
