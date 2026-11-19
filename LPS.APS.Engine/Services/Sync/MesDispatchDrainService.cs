using LPS.APS.Core.Constants;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// <see cref="IMesDispatchDrainService"/> 实现（FULL-07 组合聚合消费环，2号位）。
///
/// 流程（每组一次）：抢占 `PENDING → PROCESSING` → 组命令 → 调适配器 → 三态回写。
/// 抢占用 `AND DispatchStatus='PENDING'` 的乐观并发：抢不到说明别的执行体已领走，跳过本组。
/// </summary>
public class MesDispatchDrainService : IMesDispatchDrainService
{
    private const string StatusPending = TaskDispatchStatus.Pending;
    private const string StatusProcessing = TaskDispatchStatus.Processing;
    private const string StatusCreated = TaskDispatchStatus.Created;
    private const string StatusFailed = TaskDispatchStatus.Failed;

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IEnumerable<IMesDispatchAdapter> _adapters;
    private readonly ILogger<MesDispatchDrainService> _logger;

    public MesDispatchDrainService(
        DatabaseConnectionManager connectionManager,
        IEnumerable<IMesDispatchAdapter> adapters,
        ILogger<MesDispatchDrainService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _adapters = adapters ?? Array.Empty<IMesDispatchAdapter>();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MesDispatchDrainResult> DrainAsync(
        int scheduleRunId, int maxGroups = 50, string? workOrderType = null, CancellationToken ct = default)
    {
        if (scheduleRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scheduleRunId), scheduleRunId,
                "必须点名 ScheduleRunId：MES 下发按 Run 收口，不能跨 Run 混跑。");
        if (maxGroups <= 0) maxGroups = 50;

        var adapter = _adapters.FirstOrDefault();
        var adapterMissing = adapter is null;

        // ① 取待发组合（粒度 = (PI, Stage, PV) ＝ 11-05 终稿 §2.1 的「单次下发波」）
        //   **组量 = 组内各批批量之和**：11-05 终稿 §三「组Σ数量(净合格)」；5号位 2026-11-12 §二.4
        //   对本 PI 的期望值即 `266.6666 + 266.6666 + 266.6668 = 800.0000`。
        //   ⚠ 两条都不能碰：
        //     · **不得按行求和**——每批的各工序行都冗余携带同一整批量，按行 SUM 会放大「工序行数」倍；
        //     · **不得取 MAX**——一个组合可含多批（TaskNo），MAX 只会送出其中一批的量。
        //       2026-11-12 12:2x 实测 PENDING 段 150 组合／204 批：Σ各批 = 797,276.6668，
        //       MAX 合计 = 776,076.6666 ⇒ 少发 21,200.0002；最大单组 PI 17094977×TJ_CAST
        //       69 行 = 23 批 × 3 工序，MAX 送 400、应送 9,200（少 8,800）。
        //   ⇒ 用 `OpRank=1` 每批只取一行再 SUM：既不按行放大，也不丢批。
        var groups = (await _connectionManager.QueryAsync<DispatchGroupRow>(
            @"SELECT TOP (@Max) x.ProductionInstructionNo AS PI,
                     x.StageCode                      AS StageCode,
                     x.SourcePlanVersionId            AS Pv,
                     MIN(x.MaterialCode)              AS MaterialCode,
                     SUM(CASE WHEN x.OpRank = 1 THEN x.Quantity END) AS Quantity,
                     MIN(x.UOM)                       AS Uom,
                     MIN(x.FactoryId)                 AS FactoryId,
                     MIN(x.ResourceCode)              AS ResourceCode,
                     COUNT(*)                         AS RowCnt,
                     MIN(x.PlannedStartTime)          AS PlannedStartTime,
                     MAX(x.PlannedEndTime)            AS PlannedEndTime,
                     -- 2026-11-12 会签（5号位 §四）：只加这两列；组内应同值，取 MIN 兜底
                     MIN(NULLIF(x.InstructionType, N'')) AS InstructionType,
                     MIN(NULLIF(x.WorkOrderType, N''))   AS WorkOrderType,
                     -- 2026-11-16 5号位 会签（Command 第三列）：组内应同部门，MIN 兜底；空串归一 NULL
                     MIN(NULLIF(x.ProductionDepartmentCode, N'')) AS ProductionDepartmentCode
              FROM   (SELECT d.*,
                             ROW_NUMBER() OVER (PARTITION BY d.SourcePlanVersionId,
                                                              d.ProductionInstructionNo,
                                                              ISNULL(d.StageCode, N''),
                                                              ISNULL(d.TaskNo, CONCAT(N'T#', CONVERT(nvarchar(20), d.SourceTaskId)))
                                                ORDER BY d.Id) AS OpRank
                      FROM   TaskDispatch d
                      INNER JOIN PlanVersion pv ON pv.Id = d.SourcePlanVersionId
                      WHERE  d.DispatchStatus = @Pending
                        AND  d.SourcePlanVersionId IS NOT NULL
                        AND  pv.SourceScheduleRunId = @Run
                        AND  ISNULL(d.ProductionInstructionNo, N'') <> ''
                        -- 2026-11-17 O77：按 MES 通道过滤；仅允许 ASSEMBLY/MACHINING 的 Job 取自己的组
                        AND  (@WorkOrderType IS NULL OR d.WorkOrderType = @WorkOrderType)) x
              GROUP BY x.ProductionInstructionNo, x.StageCode, x.SourcePlanVersionId
              ORDER BY MIN(x.Id)",
            new { Max = maxGroups, Pending = StatusPending, Run = scheduleRunId, WorkOrderType = workOrderType },
            db: DatabaseId.APS, commandTimeout: 300)).ToList();

        if (groups.Count == 0)
        {
            _logger.LogInformation("[MesDispatch] Run={Run} 无 PENDING 组合可发（适配器{Adapter}）",
                scheduleRunId, adapterMissing ? "未注册" : "已注册");
            return new MesDispatchDrainResult { GroupsSeen = 0, AdapterMissing = adapterMissing };
        }

        int accepted = 0, rejected = 0, unknown = 0, rowsCreated = 0, rowsFailed = 0, rowsPending = 0;

        // 2026-11-12（`O77`）：`FactoryId → Factory.Code`（附录 A 的 `factoryCode` 必填）。
        //   为什么在此之前一直是空串：`TaskDispatch.FactoryId` 落库侧**从未写过**（实测 89,261 行全 NULL，
        //   见 `PeggingOrchestrator` 同日补写）⇒ 组查询 `MIN(d.FactoryId)` 恒 NULL ⇒ 组令里只能塞 `string.Empty`。
        //   现在落库侧已补写，这里把 Id 换成码；**换不到就不发**（见下面跳过分支）。
        var factoryIds = groups
            .Select(g => g.FactoryId)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var factoryCodeById = factoryIds.Count == 0
            ? new Dictionary<int, string>()
            : (await _connectionManager.QueryAsync<FactoryCodeRow>(
                "SELECT Id, Code FROM Factory WHERE Id IN @Ids",
                new { Ids = factoryIds }, db: DatabaseId.APS, commandTimeout: 120))
              .ToDictionary(r => r.Id, r => r.Code);

        int skippedMissing = 0;

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var pi = g.PI ?? string.Empty;
            var stage = g.StageCode ?? string.Empty;

            // ── 附录 A 必填字段闸（2026-11-12，`O77`）────────────────────────────────
            //   `factoryCode` 与 `materialCode` 都是必填。缺一个就**不发**：送出去只能拿到 MES 拒收，
            //   而且我方分不清是「我方数据缺」还是「接口不接受」。
            //   **保持 `PENDING`**（在抢占之前 `continue`），落库侧补齐后下一轮自然重试 —— 不落 `FAILED`。
            var factoryCode = g.FactoryId is int fid && factoryCodeById.TryGetValue(fid, out var fc) ? fc : string.Empty;
            if (factoryCode.Length == 0 || string.IsNullOrWhiteSpace(g.MaterialCode))
            {
                skippedMissing++;
                _logger.LogWarning(
                    "[MesDispatch] 组合 PI={Pi} Stage={Stage} PV={Pv} 缺附录 A 必填字段" +
                    "（factoryCode='{Factory}'／materialCode='{Material}'）⇒ 本轮跳过，保持 PENDING",
                    pi, stage, g.Pv, factoryCode, g.MaterialCode ?? "(NULL)");
                continue;
            }

            // ② 抢占：PENDING → PROCESSING（乐观并发；抢不到说明已被别的执行体领走）
            var claimed = await _connectionManager.ExecuteAsync(
                @"UPDATE TaskDispatch
                  SET    DispatchStatus = @Processing, UpdatedAt = GETDATE()
                  WHERE  DispatchStatus = @Pending
                    AND  SourcePlanVersionId = @Pv
                    AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                    AND  ISNULL(StageCode, N'') = @Stage",
                new { Processing = StatusProcessing, Pending = StatusPending, g.Pv, Pi = pi, Stage = stage },
                db: DatabaseId.APS, commandTimeout: 120);

            if (claimed == 0) continue;   // 已被别人领走

            var referenceNo = MesDispatchReference.Compute(pi, stage, g.Pv);

            // ③ 出站（无适配器 = NOT_CONFIGURED，按 Unknown 处理，不外呼）
            MesDispatchResult result;
            if (adapter is null)
            {
                result = new MesDispatchResult(
                    MesDispatchKind.Unknown, null, null,
                    "NOT_CONFIGURED：IMesDispatchAdapter 未注册，真实 MES 未接通（联调阻断）", null);
            }
            else
            {
                try
                {
                    var taskIds = (await _connectionManager.QueryAsync<long>(
                        @"SELECT SourceTaskId FROM TaskDispatch
                          WHERE  SourcePlanVersionId = @Pv
                            AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                            AND  ISNULL(StageCode, N'') = @Stage
                            AND  DispatchStatus = @Processing",
                        new { g.Pv, Pi = pi, Stage = stage },
                        db: DatabaseId.APS, commandTimeout: 120)).ToList();

                    var command = new MesDispatchCommand(
                        referenceNo, pi, stage, g.Pv, g.MaterialCode ?? string.Empty,
                        g.Quantity, g.Uom ?? string.Empty,
                        factoryCode,
                        string.IsNullOrWhiteSpace(g.ResourceCode) ? null : g.ResourceCode,
                        g.PlannedStartTime ?? DateTime.Now, g.PlannedEndTime ?? DateTime.Now, taskIds,
                        g.InstructionType, g.WorkOrderType, g.ProductionDepartmentCode);

                    result = await adapter.CreateWorkOrderAsync(command, ct);
                }
                catch (Exception ex)
                {
                    // 适配器抛异常也不得落 FAILED —— 归 Unknown，保持可重试
                    result = new MesDispatchResult(
                        MesDispatchKind.Unknown, null, null, $"适配器异常：{ex.Message}", null);
                }
            }

            // ④ 三态回写（整组）
            switch (result.Kind)
            {
                case MesDispatchKind.Accepted:
                {
                    var n = await _connectionManager.ExecuteAsync(
                        @"UPDATE TaskDispatch
                          SET    DispatchStatus = @Created, MESWorkOrderNo = @Wo,
                                 FailureCode = NULL, FailureReason = NULL, UpdatedAt = GETDATE()
                          WHERE  DispatchStatus = @Processing
                            AND  SourcePlanVersionId = @Pv
                            AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                            AND  ISNULL(StageCode, N'') = @Stage",
                        new { Created = StatusCreated, Wo = result.MesWorkOrderNo, Processing = StatusProcessing,
                              g.Pv, Pi = pi, Stage = stage },
                        db: DatabaseId.APS, commandTimeout: 300);
                    accepted++; rowsCreated += n;
                    break;
                }
                case MesDispatchKind.Rejected:
                {
                    var code = string.IsNullOrWhiteSpace(result.FailureCode)
                        ? TaskDispatchFailureCode.MesRejectUnmapped : result.FailureCode;
                    var n = await _connectionManager.ExecuteAsync(
                        @"UPDATE TaskDispatch
                          SET    DispatchStatus = @Failed, FailureCode = @Code,
                                 FailureReason = @Reason, UpdatedAt = GETDATE()
                          WHERE  DispatchStatus = @Processing
                            AND  SourcePlanVersionId = @Pv
                            AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                            AND  ISNULL(StageCode, N'') = @Stage",
                        new { Failed = StatusFailed, Code = code, Reason = result.FailureReason,
                              Processing = StatusProcessing, g.Pv, Pi = pi, Stage = stage },
                        db: DatabaseId.APS, commandTimeout: 300);
                    rejected++; rowsFailed += n;
                    break;
                }
                default:
                {
                    // Unknown：回 PENDING 保持可重试，**不落 FAILED**
                    var n = await _connectionManager.ExecuteAsync(
                        @"UPDATE TaskDispatch
                          SET    DispatchStatus = @Pending, FailureReason = @Reason, UpdatedAt = GETDATE()
                          WHERE  DispatchStatus = @Processing
                            AND  SourcePlanVersionId = @Pv
                            AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                            AND  ISNULL(StageCode, N'') = @Stage",
                        new { Pending = StatusPending, Reason = result.FailureReason,
                              Processing = StatusProcessing, g.Pv, Pi = pi, Stage = stage },
                        db: DatabaseId.APS, commandTimeout: 300);
                    unknown++; rowsPending += n;
                    break;
                }
            }

            _logger.LogInformation(
                "[MesDispatch] 组合 PI={Pi} Stage={Stage} PV={Pv} → {Kind}（参考号={Ref}，行数={Rows}）",
                pi, stage, g.Pv, result.Kind, referenceNo, g.RowCnt);
        }

        _logger.LogInformation(
            "[MesDispatch] Run={Run} 消费完成：组合 {Seen}（Accepted {A} / Rejected {R} / Unknown {U} / 缺必填跳过 {Skip}）；" +
            "行 Accepted {RowsA} / Failed {RowsF} / 回 PENDING {RowsP}；适配器{AdapterState}",
            scheduleRunId, groups.Count, accepted, rejected, unknown, skippedMissing,
            rowsCreated, rowsFailed, rowsPending,
            adapterMissing ? "未注册（NOT_CONFIGURED）" : "已注册");

        return new MesDispatchDrainResult
        {
            GroupsSeen = groups.Count,
            GroupsAccepted = accepted,
            GroupsRejected = rejected,
            GroupsUnknown = unknown,
            RowsCreated = rowsCreated,
            RowsFailed = rowsFailed,
            RowsBackToPending = rowsPending,
            GroupsSkippedMissingField = skippedMissing,
            AdapterMissing = adapterMissing
        };
    }

    private sealed class DispatchGroupRow
    {
        public string? PI { get; set; }
        public string? StageCode { get; set; }
        public int Pv { get; set; }
        public string? MaterialCode { get; set; }
        public decimal Quantity { get; set; }
        public string? Uom { get; set; }
        public int? FactoryId { get; set; }
        public string? ResourceCode { get; set; }
        public int RowCnt { get; set; }
        public DateTime? PlannedStartTime { get; set; }
        public DateTime? PlannedEndTime { get; set; }
        /// <summary>2026-11-12 会签（5号位 §四）：`InstructionType`／`WorkOrderType` 两列。</summary>
        public string? InstructionType { get; set; }
        public string? WorkOrderType { get; set; }
        /// <summary>2026-11-16 5号位 会签：MES 组装端口 Holon 部门码来源。</summary>
        public string? ProductionDepartmentCode { get; set; }
    }

    /// <summary>`Factory.Id → Code` 反查行（2026-11-12，`O77`：附录 A 的 `factoryCode` 必填，不能再送空串）。</summary>
    private sealed class FactoryCodeRow
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
    }
}