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
        int scheduleRunId, int maxGroups = 50, CancellationToken ct = default)
    {
        if (scheduleRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scheduleRunId), scheduleRunId,
                "必须点名 ScheduleRunId：MES 下发按 Run 收口，不能跨 Run 混跑。");
        if (maxGroups <= 0) maxGroups = 50;

        var adapter = _adapters.FirstOrDefault();
        var adapterMissing = adapter is null;

        // ① 取待发组合（粒度 = (PI, Stage, PV)）
        //   `Quantity` 取 MAX：同组合各行是"同 PV 定容"载体量、逐行同值；**不得 SUM**（会按行数放大）。
        var groups = (await _connectionManager.QueryAsync<DispatchGroupRow>(
            @"SELECT TOP (@Max) d.ProductionInstructionNo AS PI,
                     d.StageCode                      AS StageCode,
                     d.SourcePlanVersionId            AS Pv,
                     MIN(d.MaterialCode)              AS MaterialCode,
                     MAX(d.Quantity)                  AS Quantity,
                     MIN(d.UOM)                       AS Uom,
                     MIN(d.FactoryId)                 AS FactoryId,
                     COUNT(*)                         AS RowCnt,
                     MIN(d.PlannedStartTime)          AS PlannedStartTime,
                     MAX(d.PlannedEndTime)            AS PlannedEndTime
              FROM   TaskDispatch d
              INNER JOIN PlanVersion pv ON pv.Id = d.SourcePlanVersionId
              WHERE  d.DispatchStatus = @Pending
                AND  d.SourcePlanVersionId IS NOT NULL
                AND  pv.SourceScheduleRunId = @Run
                AND  ISNULL(d.ProductionInstructionNo, N'') <> ''
              GROUP BY d.ProductionInstructionNo, d.StageCode, d.SourcePlanVersionId
              ORDER BY MIN(d.Id)",
            new { Max = maxGroups, Pending = StatusPending, Run = scheduleRunId },
            db: DatabaseId.APS, commandTimeout: 300)).ToList();

        if (groups.Count == 0)
        {
            _logger.LogInformation("[MesDispatch] Run={Run} 无 PENDING 组合可发（适配器{Adapter}）",
                scheduleRunId, adapterMissing ? "未注册" : "已注册");
            return new MesDispatchDrainResult { GroupsSeen = 0, AdapterMissing = adapterMissing };
        }

        int accepted = 0, rejected = 0, unknown = 0, rowsCreated = 0, rowsFailed = 0, rowsPending = 0;

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var pi = g.PI ?? string.Empty;
            var stage = g.StageCode ?? string.Empty;

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
                        g.Quantity, g.Uom ?? string.Empty, string.Empty, null,
                        g.PlannedStartTime ?? DateTime.Now, g.PlannedEndTime ?? DateTime.Now, taskIds);

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
            "[MesDispatch] Run={Run} 消费完成：组合 {Seen}（Accepted {A} / Rejected {R} / Unknown {U}）；行 Accepted {RowsA} / Failed {RowsF} / 回 PENDING {RowsP}；适配器{AdapterState}",
            scheduleRunId, groups.Count, accepted, rejected, unknown, rowsCreated, rowsFailed, rowsPending,
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
        public int RowCnt { get; set; }
        public DateTime? PlannedStartTime { get; set; }
        public DateTime? PlannedEndTime { get; set; }
    }
}