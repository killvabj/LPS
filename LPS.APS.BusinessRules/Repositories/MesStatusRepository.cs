using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using System.Data;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// P11 状态查询读面（**B6，11-17；0号位 Q-d 裁**）。只读三张表：
/// `TaskDispatch`（APS 下发队列：5号位 入队、2号位 消费——消费环留痕缺口见 DTO 契约注）、
/// `MESWorkOrderSnapshot` / `OperationProgressSnapshot`（2号位 随批快照，本类**从不写**）。
///
/// 单源纪律：批聚合 WHERE/HAVING 文本 = 列表与 COUNT 共用同一常量（<see cref="BatchInsert"/>）；
/// 越权=fail-closed（同订单页家族）；快照三列在 PV 无源 Run（@SrcRun=0）时**恒 null=未提供**，不是零。
/// </summary>
public class MesStatusRepository : IMesStatusRepository
{
    private readonly DatabaseConnectionManager _connectionManager;

    public MesStatusRepository(DatabaseConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    private const string BatchTmpSchema = @"
CREATE TABLE #b(
    -- 键宽=实测列长（TaskNo/PI 均 nvarchar(100)，11-17 核）；PK 400B<900B 聚集索引上限，防溢出插入失败
    TaskNo nvarchar(100) NOT NULL,
    PiNo nvarchar(100) NOT NULL,
    StageCode nvarchar(100), InstructionNo nvarchar(200), WorkOrderType nvarchar(50),
    MaterialCode nvarchar(100), DeptCode nvarchar(100),
    OpCount int, PStart datetime2(7), PEnd datetime2(7),
    CreatedCnt int, PendingCnt int, FailedCnt int, OtherCnt int,
    WoCnt int, WoSample nvarchar(100),
    PRIMARY KEY (TaskNo, PiNo));";

    /// <summary>批聚合入表段——列表体与 COUNT **共用本文本**（分叉在源头堵死）。</summary>
    private const string BatchInsert = @"
INSERT #b
SELECT d.TaskNo, d.ProductionInstructionNo,
       MAX(d.StageCode), MAX(d.InstructionNo), MAX(d.WorkOrderType),
       MAX(d.MaterialCode), MAX(d.ProductionDepartmentCode),
       COUNT(*), MIN(d.PlannedStartTime), MAX(d.PlannedEndTime),
       SUM(CASE WHEN d.DispatchStatus = 'CREATED' THEN 1 ELSE 0 END),
       SUM(CASE WHEN d.DispatchStatus = 'PENDING' THEN 1 ELSE 0 END),
       SUM(CASE WHEN d.DispatchStatus = 'FAILED' THEN 1 ELSE 0 END),
       SUM(CASE WHEN d.DispatchStatus NOT IN ('CREATED','PENDING','FAILED') THEN 1 ELSE 0 END),
       COUNT(DISTINCT NULLIF(d.MESWorkOrderNo, '')), MAX(NULLIF(d.MESWorkOrderNo, ''))
FROM TaskDispatch d
LEFT JOIN Factory f ON f.Id = d.FactoryId
WHERE d.SourcePlanVersionId = @Pv
  AND (@From IS NULL OR d.PlannedEndTime IS NULL OR d.PlannedEndTime >= @From)
  AND (@To IS NULL OR d.PlannedStartTime IS NULL OR d.PlannedStartTime <= @To)
  AND (@HasStages = 0 OR d.StageCode IN @StageCodes)
  AND (@PiNo IS NULL OR d.ProductionInstructionNo = @PiNo)
  AND (@Wo IS NULL OR d.MESWorkOrderNo = @Wo)
  AND (@Kw IS NULL OR d.MaterialCode LIKE @Kw OR d.TaskNo LIKE @Kw OR d.ProductionInstructionNo LIKE @Kw)
  AND (@HasDeptFilter = 0 OR d.ProductionDepartmentCode IN @DepartmentCodes)
  AND (@HasScopeDepts = 0 OR d.ProductionDepartmentCode IN @ScopeDepartments)
  AND (@HasFactories = 0 OR f.Code IN @AllowedFactories)
  -- P11 H3（11-23 4号位件）：设备筛选经同批 Task 行 EXISTS（TaskDispatch 无资源列）；
  -- MES 执行状态筛选=批内任一工单命中（快照按源 Run；SrcRun=0 恒空=未提供，不猜别 Run）。
  AND (@HasResources = 0 OR EXISTS (SELECT 1 FROM Task rt
        WHERE rt.PlanVersionId = d.SourcePlanVersionId AND rt.TaskNo = d.TaskNo AND rt.ResourceId IN @ResourceIds))
  AND (@HasWoStatus = 0 OR EXISTS (SELECT 1 FROM MESWorkOrderSnapshot w2
        WHERE w2.ScheduleRunId = @SrcRun AND w2.MESWorkOrderNo = d.MESWorkOrderNo AND w2.WorkOrderStatus IN @WoStatuses))
GROUP BY d.TaskNo, d.ProductionInstructionNo
HAVING (@Status IS NULL OR SUM(CASE WHEN d.DispatchStatus = @Status THEN 1 ELSE 0 END) > 0)";

    private const string Preamble = "SET NOCOUNT ON;" + BatchTmpSchema + BatchInsert;

    public async Task<MesStatusListResultDto> QueryAsync(
        int planVersionId,
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<string>? stageCodes = null,
        string? piNo = null,
        string? workOrderNo = null,
        string? keyword = null,
        string? dispatchStatus = null,
        IReadOnlyList<string>? departmentCodes = null,
        IReadOnlyList<int>? resourceIds = null,
        IReadOnlyList<string>? workOrderStatuses = null,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        IReadOnlySet<string>? allowedDepartments = null,
        int pageIndex = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        if (stageCodes is { Count: 0 } || departmentCodes is { Count: 0 } || resourceIds is { Count: 0 } || workOrderStatuses is { Count: 0 })
            return new MesStatusListResultDto { PlanVersionId = planVersionId, Page = Math.Max(1, pageIndex), PageSize = Math.Clamp(pageSize, 1, 200) };

        var header = await _connectionManager.QueryFirstOrDefaultAsync<MesPvHeader>(
            @"SELECT TOP 1 pv.Id, pv.DomainKey, pv.Status AS PlanVersionStatus, pv.SourceScheduleRunId, sr.DataCutoffTime
              FROM PlanVersion pv
              LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
              WHERE pv.Id = @Pv",
            new { Pv = planVersionId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        if (header is null || header.Id == 0)
            return new MesStatusListResultDto { PlanVersionId = planVersionId, VersionExists = false };
        if (allowedDomains is { Count: 0 })
            return new MesStatusListResultDto { PlanVersionId = planVersionId, ScopeDenied = true };
        if (allowedDomains is not null && !allowedDomains.Contains(header.DomainKey))
            return new MesStatusListResultDto { PlanVersionId = planVersionId, ScopeDenied = true };
        if (allowedFactories is { Count: 0 })
            return new MesStatusListResultDto { PlanVersionId = planVersionId, ScopeDenied = true };

        var page = Math.Max(1, pageIndex);
        var size = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
        var parameters = new
        {
            Pv = planVersionId,
            // 快照按 Run 装载：无源 Run ⇒ 0（join 恒空=未提供，不猜别的 Run）
            SrcRun = header.SourceScheduleRunId ?? 0,
            From = from,
            To = to,
            HasStages = stageCodes is { Count: > 0 },
            StageCodes = stageCodes,
            PiNo = string.IsNullOrWhiteSpace(piNo) ? null : piNo.Trim(),
            Wo = string.IsNullOrWhiteSpace(workOrderNo) ? null : workOrderNo.Trim(),
            Kw = string.IsNullOrWhiteSpace(keyword) ? null : "%" + keyword.Trim() + "%",
            Status = string.IsNullOrWhiteSpace(dispatchStatus) ? null : dispatchStatus.Trim(),
            HasDeptFilter = departmentCodes is { Count: > 0 },
            DepartmentCodes = departmentCodes,
            HasResources = resourceIds is { Count: > 0 },
            ResourceIds = resourceIds,
            HasWoStatus = workOrderStatuses is { Count: > 0 },
            WoStatuses = workOrderStatuses,
            HasScopeDepts = allowedDepartments is { Count: > 0 },
            ScopeDepartments = allowedDepartments is { Count: > 0 } ? allowedDepartments.ToList() : null,
            HasFactories = allowedFactories is { Count: > 0 },
            AllowedFactories = allowedFactories is { Count: > 0 } ? allowedFactories.ToList() : null,
            Skip = (page - 1) * size,
            Take = size
        };

        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            Preamble + "\nSELECT COUNT(*) FROM #b;\nDROP TABLE #b;",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        var items = (await _connectionManager.QueryAsync<MesStatusBatchRowDto>(
            Preamble + @"
SELECT b.TaskNo, b.PiNo, b.StageCode, b.InstructionNo, b.WorkOrderType, b.MaterialCode, b.DeptCode,
       b.OpCount AS OperationCount, b.PStart AS PlannedStart, b.PEnd AS PlannedEnd,
       tq.BatchQty,
       b.CreatedCnt AS CreatedCount, b.PendingCnt AS PendingCount,
       b.FailedCnt AS FailedCount, b.OtherCnt AS OtherCount,
       b.WoCnt AS WorkOrderCount, b.WoSample AS WorkOrderNo,
       w.WorkOrderStatus,
       CASE WHEN b.WoCnt > 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS WorkOrderStatusMixed,
       CASE WHEN rep.Present = 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Reported,
       rep.PlannedQtyReported, rep.GoodQty, rep.ScrapQty, rep.ReworkQty, rep.RemainingQty, rep.LastReportTime
FROM #b b
OUTER APPLY (
    SELECT TOP 1 t.StageExecutionBatchQty AS BatchQty
    FROM Task t WHERE t.PlanVersionId = @Pv AND t.TaskNo = b.TaskNo
    ORDER BY t.OperationSeq) tq
OUTER APPLY (
    SELECT TOP 1 w0.WorkOrderStatus
    FROM MESWorkOrderSnapshot w0
    WHERE w0.ScheduleRunId = @SrcRun AND w0.MESWorkOrderNo = b.WoSample) w
OUTER APPLY (
    SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS Present,
           SUM(p.PlannedQty) AS PlannedQtyReported, SUM(p.GoodQty) AS GoodQty,
           SUM(p.ScrapQty) AS ScrapQty, SUM(p.ReworkQty) AS ReworkQty,
           SUM(p.RemainingQty) AS RemainingQty, MAX(p.LastReportTime) AS LastReportTime
    FROM OperationProgressSnapshot p
    WHERE p.ScheduleRunId = @SrcRun AND p.ProductionInstructionNo = b.PiNo AND p.StageCode = b.StageCode) rep
ORDER BY b.PiNo, b.TaskNo
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
DROP TABLE #b;",
            parameters, CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();

        var sync = await _connectionManager.QueryFirstOrDefaultAsync<MesSyncTimesRow>(
            @"SELECT (SELECT MAX(w.SourceUpdatedAt) FROM MESWorkOrderSnapshot w WHERE w.ScheduleRunId = @SrcRun) AS WoSrcUpd,
                     (SELECT MAX(p.SourceUpdatedAt) FROM OperationProgressSnapshot p WHERE p.ScheduleRunId = @SrcRun) AS OpsSrcUpd,
                     (SELECT MAX(d.CreatedAt) FROM TaskDispatch d WHERE d.SourcePlanVersionId = @Pv) AS DispatchMaxCreated",
            new { SrcRun = header.SourceScheduleRunId ?? 0, Pv = planVersionId },
            CommandType.Text, DatabaseId.APS, commandTimeout: 30);

        return new MesStatusListResultDto
        {
            PlanVersionId = planVersionId,
            DomainKey = header.DomainKey,
            PlanVersionStatus = header.PlanVersionStatus,
            SourceScheduleRunId = header.SourceScheduleRunId,
            DataCutoffTime = header.DataCutoffTime,
            WoSnapshotSourceUpdatedAt = sync?.WoSrcUpd,
            OpsSnapshotSourceUpdatedAt = sync?.OpsSrcUpd,
            DispatchMaxCreatedAt = sync?.DispatchMaxCreated,
            Items = items,
            Total = total,
            Page = page,
            PageSize = size
        };
    }

    /// <summary>P06 H3（11-23 4号位件）：单任务执行事实（工单状态 + 报工聚合）。
    /// 键与 P11 批读面 OUTER APPLY 完全同构（SrcRun+WO / SrcRun+PI+Stage）；E2 五态如获 0 裁将在此单源派生。
    /// srcRun 空/0 ⇒ 返回 null（未提供——快照按 Run 装载纪律，不猜别的 Run）。</summary>
    public async Task<MesTaskExecFactsDto?> GetTaskExecFactsAsync(
        long? srcRun, string? piNo, string? stageCode, string? workOrderNo, CancellationToken ct = default)
    {
        if (srcRun is null or <= 0) return null;
        var sql = @"
SELECT
    (SELECT TOP 1 w0.WorkOrderStatus FROM MESWorkOrderSnapshot w0
      WHERE w0.ScheduleRunId = @SrcRun AND w0.MESWorkOrderNo = @Wo) AS WorkOrderStatus,
    CASE WHEN COUNT(*) > 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Reported,
    SUM(p.PlannedQty) AS PlannedQtyReported, SUM(p.GoodQty) AS GoodQty,
    SUM(p.ScrapQty) AS ScrapQty, SUM(p.ReworkQty) AS ReworkQty,
    SUM(p.RemainingQty) AS RemainingQty, MAX(p.LastReportTime) AS LastReportTime
FROM OperationProgressSnapshot p
WHERE p.ScheduleRunId = @SrcRun AND p.ProductionInstructionNo = @Pi AND p.StageCode = @Stage";
        var row = await _connectionManager.QueryFirstOrDefaultAsync<MesTaskExecFactsDto>(
            sql, new { SrcRun = srcRun, Wo = string.IsNullOrWhiteSpace(workOrderNo) ? null : workOrderNo.Trim(),
                       Pi = string.IsNullOrWhiteSpace(piNo) ? null : piNo.Trim(),
                       Stage = string.IsNullOrWhiteSpace(stageCode) ? null : stageCode.Trim() },
            CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        return row;
    }

    public async Task<MesStatusBatchDetailDto> GetBatchDetailAsync(
        int planVersionId,
        string taskNo,
        IReadOnlySet<string>? allowedDomains = null,
        IReadOnlySet<string>? allowedFactories = null,
        CancellationToken ct = default)
    {
        if (allowedDomains is { Count: 0 } || allowedFactories is { Count: 0 })
            return new MesStatusBatchDetailDto { Found = true };   // 空授权=读不到（fail-closed），与"批不存在"用 Found 区分由上层 header 判

        var header = await _connectionManager.QueryFirstOrDefaultAsync<MesPvHeader>(
            @"SELECT TOP 1 pv.Id, pv.DomainKey, pv.Status AS PlanVersionStatus, pv.SourceScheduleRunId, sr.DataCutoffTime
              FROM PlanVersion pv
              LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
              WHERE pv.Id = @Pv",
            new { Pv = planVersionId }, CommandType.Text, DatabaseId.APS, commandTimeout: 10);
        if (header is null || header.Id == 0
            || (allowedDomains is not null && !allowedDomains.Contains(header.DomainKey)))
            return new MesStatusBatchDetailDto { Found = false };

        var ops = (await _connectionManager.QueryAsync<MesStatusOperationRowDto>(
            @"SELECT d.OperationCode, d.OperationName, d.DispatchStatus, d.MESWorkOrderNo AS MesWorkOrderNo,
                     d.FailureCode, d.FailureReason, d.Quantity, d.UOM,
                     d.PlannedStartTime AS PlannedStart, d.PlannedEndTime AS PlannedEnd, d.ResourceCode,
                     p.GoodQty, p.ScrapQty, p.ReworkQty, p.RemainingQty, p.LastReportTime
              FROM TaskDispatch d
              LEFT JOIN OperationProgressSnapshot p
                ON p.ScheduleRunId = @SrcRun AND p.ProductionInstructionNo = d.ProductionInstructionNo
               AND p.StageCode = d.StageCode AND p.OperationName = d.OperationName
              WHERE d.SourcePlanVersionId = @Pv AND d.TaskNo = @TaskNo
              ORDER BY d.OperationCode",
            new
            {
                Pv = planVersionId,
                TaskNo = taskNo.Trim(),
                SrcRun = header.SourceScheduleRunId ?? 0
            }, CommandType.Text, DatabaseId.APS, commandTimeout: 15)).ToList();

        if (ops.Count == 0)
            return new MesStatusBatchDetailDto { Found = false };

        return new MesStatusBatchDetailDto { Found = true, Operations = ops };
    }

    private sealed class MesPvHeader
    {
        public int Id { get; set; }
        public string DomainKey { get; set; } = string.Empty;
        public string? PlanVersionStatus { get; set; }
        public int? SourceScheduleRunId { get; set; }
        public DateTime? DataCutoffTime { get; set; }
    }

    private sealed class MesSyncTimesRow
    {
        public DateTime? WoSrcUpd { get; set; }
        public DateTime? OpsSrcUpd { get; set; }
        public DateTime? DispatchMaxCreated { get; set; }
    }
}
