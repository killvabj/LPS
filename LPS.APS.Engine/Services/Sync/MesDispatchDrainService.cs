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

        // ── P11-0（0号位 2026-11-23）：**出站前最后一道不可绕过的服务端门禁** ──────────────
        //   动机：仅靠「上游只给 ACTIVE 建队列」不充分——队列是历史数据，PV/任务/供给在入队后仍可能变化；
        //   必须在**真正调用 MES 之前**再核一次，不满足即**拒发并显性挂起**（保持 PENDING + 写 FailureCode），
        //   绝不进入 5号位 的真实建单调用。
        //   判据（0号位 P11-0 清单的可实现子集，逐条对应）：
        //     ① PV 仍 ACTIVE 且非 Candidate/Sim
        //     ② 组内任务真实属于本 PV（TaskDispatch.SourceTaskId → Task 同 PV 存在）
        //     ③ 路径身份齐全：Task.RouteCode/PathId/OperationCode 非空，且**组内一致**（一条执行批一条 Route+Path）
        //     ④ PI 位置快照存在，且**存在已定位（非 UNLOCATED）的行**（纯 UNLOCATED 才拒）
        //     ⑤ 该 Stage 的**已定位**可执行自由量 > 0
        //     ⑥ 该 PV 无未闭合 SH（UnresolvedCategory='SH_MISSING'）
        //   暂未覆盖（在文档与本方法注释中显式留缺口，不装作已实现）：
        //     · Domain/Scope/发布窗口、采购占位（非承诺规划采购）——缺权威表，待与 0号位/5号位 确认落点
        var gateVerdicts = await EvaluateOutboundGateAsync(groups, ct);

        int accepted = 0, rejected = 0, unknown = 0, rowsCreated = 0, rowsFailed = 0, rowsPending = 0;
        int gateBlocked = 0;

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

            // P11-0：门禁不过 ⇒ 拒发并显性挂起（保持 PENDING，写 FailureCode/FailureReason 供审计与筛选）
            if (gateVerdicts.TryGetValue((g.Pv, pi, stage), out var verdict) && !verdict.Passed)
            {
                gateBlocked++;
                await _connectionManager.ExecuteAsync(
                    @"UPDATE TaskDispatch
                      SET    FailureCode = @Code, FailureReason = @Reason
                      WHERE  DispatchStatus = @Pending
                        AND  SourcePlanVersionId = @Pv
                        AND  ISNULL(ProductionInstructionNo, N'') = @Pi
                        AND  ISNULL(StageCode, N'') = @Stage",
                    new { Code = verdict.Code, Reason = verdict.Reason, Pending = StatusPending, g.Pv, Pi = pi, Stage = stage },
                    db: DatabaseId.APS, commandTimeout: 120);
                _logger.LogWarning(
                    "[MesDispatch][P11-0 拒发] PV={Pv} PI={Pi} Stage={Stage} ⇒ {Code}：{Reason}（保持 PENDING，不调用 MES）",
                    g.Pv, pi, stage, verdict.Code, verdict.Reason);
                continue;
            }

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

    // ══════════════════════════════════════════════════════════════════════════
    // P11-0 出站门禁（2026-11-23，0号位 指令）
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>门禁结论：`Passed=false` 时带 `Code`（稳定机读码）与 `Reason`（人读）。</summary>
    private readonly record struct GateVerdict(bool Passed, string? Code, string? Reason)
    {
        public static readonly GateVerdict Ok = new(true, null, null);
    }

    /// <summary>
    /// **出站前最后一次服务端校验**（0号位 P11-0）。批量取数，避免逐组往返。
    ///
    /// <para>不满足 ⇒ 调用方**保持 PENDING 并写 FailureCode**（显性挂起），绝不进入 MES 建单。</para>
    /// </summary>
    private async Task<Dictionary<(int Pv, string Pi, string Stage), GateVerdict>> EvaluateOutboundGateAsync(
        IReadOnlyList<DispatchGroupRow> groups, CancellationToken ct)
    {
        var result = new Dictionary<(int, string, string), GateVerdict>();
        if (groups.Count == 0) return result;

        var pvs = groups.Select(g => g.Pv).Distinct().ToList();
        var pis = groups.Select(g => g.PI ?? string.Empty).Where(x => x.Length > 0).Distinct().ToList();

        // ① PV 状态 / 类别
        var pvRows = (await _connectionManager.QueryAsync<PvGateRow>(
            @"SELECT Id, Status, VersionCategory FROM PlanVersion WHERE Id IN @Ids",
            new { Ids = pvs }, db: DatabaseId.APS, commandTimeout: 120))
            .ToDictionary(r => r.Id);

        // ② + ③ 任务真实性与路径身份：TaskDispatch → Task（同 PV）
        var taskRows = (await _connectionManager.QueryAsync<TaskPathRow>(
            @"SELECT d.SourcePlanVersionId AS Pv,
                     ISNULL(d.ProductionInstructionNo, N'') AS Pi,
                     ISNULL(d.StageCode, N'') AS StageCode,
                     d.SourceTaskId AS TaskId,
                     t.Id AS MatchedTaskId,
                     t.RouteCode, t.PathId, t.OperationCode,
                     ro.Id AS RoutingMatchedId
              FROM TaskDispatch d
              LEFT JOIN [Task] t ON t.Id = d.SourceTaskId AND t.PlanVersionId = d.SourcePlanVersionId
              LEFT JOIN RoutingOperation ro
                     ON ro.MaterialId = t.MaterialId
                    AND ro.RouteCode  = t.RouteCode
                    AND ro.PathId     = t.PathId
                    AND ro.OperationCode = t.OperationCode
                    AND ro.IsActive = 1
              WHERE d.DispatchStatus = @Pending
                AND d.SourcePlanVersionId IN @Pvs
                AND ISNULL(d.ProductionInstructionNo, N'') IN @Pis",
            new { Pending = StatusPending, Pvs = pvs, Pis = pis },
            db: DatabaseId.APS, commandTimeout: 300)).ToList();

        var tasksByGroup = taskRows
            .GroupBy(r => (r.Pv, r.Pi ?? string.Empty, r.StageCode ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ④ + ⑤ PI 位置快照（UNLOCATED 与可执行自由量）
        var posRows = (await _connectionManager.QueryAsync<PiPositionGateRow>(
            @"SELECT PlanVersionId AS Pv, ProductionInstructionNo AS Pi, PositionType,
                     Quantity, CurrentStageCode, NextStageCode
              FROM ProductionInstructionPositionSnapshot
              WHERE PlanVersionId IN @Pvs AND ProductionInstructionNo IN @Pis",
            new { Pvs = pvs, Pis = pis }, db: DatabaseId.APS, commandTimeout: 300)).ToList();

        var posByPi = posRows.GroupBy(r => (r.Pv, r.Pi ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ⑥-前置：**PI → 订单类型**（B-01 身份闸门把 `SALES_ORDER` 行排除在 PI 供给事实之外，
        //   ⇒ 客户订单的 PI **本就不会有 Position 快照**；门禁不能拿"PI 位置"作为它们的开工条件）。
        var piOrderType = (await _connectionManager.QueryAsync<PiOrderTypeRow>(
            @"SELECT DISTINCT o.MTS_InstructionNo AS Pi, o.OrderType
              FROM [Order] o
              WHERE o.PlanVersionId IN @Pvs
                AND o.MTS_InstructionNo IN @Pis",
            new { Pvs = pvs, Pis = pis },
            db: DatabaseId.APS, commandTimeout: 300))
            .Where(r => !string.IsNullOrEmpty(r.Pi))
            .GroupBy(r => r.Pi!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().OrderType, StringComparer.Ordinal);

        // ⑥ 未闭合 SH（按 PV）
        var shUnclosed = (await _connectionManager.QueryAsync<int>(
            @"SELECT DISTINCT PlanVersionId FROM APS_UnresolvedCrossFactorySupplyResult
              WHERE PlanVersionId IN @Pvs AND UnresolvedCategory = N'SH_MISSING'",
            new { Pvs = pvs }, db: DatabaseId.APS, commandTimeout: 300)).ToHashSet();

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var key = (g.Pv, g.PI ?? string.Empty, g.StageCode ?? string.Empty);

            // ① PV
            if (!pvRows.TryGetValue(g.Pv, out var pvRow))
            { result[key] = new GateVerdict(false, "PV_NOT_FOUND", $"PlanVersion {g.Pv} 不存在"); continue; }
            if (!string.Equals(pvRow.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            { result[key] = new GateVerdict(false, "PV_NOT_ACTIVE", $"PlanVersion {g.Pv} 状态={pvRow.Status}，非 ACTIVE"); continue; }
            if (pvRow.VersionCategory is { Length: > 0 } vc &&
                (vc.Contains("CANDIDATE", StringComparison.OrdinalIgnoreCase) ||
                 vc.Contains("SIM", StringComparison.OrdinalIgnoreCase)))
            { result[key] = new GateVerdict(false, "PV_CANDIDATE", $"PlanVersion {g.Pv} 类别={vc}（Candidate/Sim 不得下发）"); continue; }

            // ⑥ 未闭合 SH
            if (shUnclosed.Contains(g.Pv))
            { result[key] = new GateVerdict(false, "SH_UNCLOSED", $"PlanVersion {g.Pv} 存在未闭合 SH（SH_MISSING），不得下发"); continue; }

            // ② 任务真实性 + ③ 路径身份
            if (!tasksByGroup.TryGetValue(key, out var rows) || rows.Count == 0)
            { result[key] = new GateVerdict(false, "TASK_MISSING", "组内无对应的 TaskDispatch/Task"); continue; }

            var missingTask = rows.Where(r => r.MatchedTaskId is null).ToList();
            if (missingTask.Count > 0)
            { result[key] = new GateVerdict(false, "TASK_NOT_IN_PV", $"{missingTask.Count} 个 SourceTaskId 在同 PV 的 Task 中不存在（任务已撤销或跨 PV）"); continue; }

            var noPath = rows.Where(r => string.IsNullOrWhiteSpace(r.RouteCode) || r.PathId is null || r.PathId <= 0
                                      || string.IsNullOrWhiteSpace(r.OperationCode)).ToList();
            if (noPath.Count > 0)
            { result[key] = new GateVerdict(false, "PATH_IDENTITY_MISSING", $"{noPath.Count} 个 Task 缺 RouteCode/PathId/OperationCode，无法组成唯一路径身份"); continue; }

            // ★ 2026-11-23：**只拦真占位，不拦逗号串**。
            //   核实结论（纠正我方当日早前的错误判断）：
            //     · `Task.RouteCode = 'DEFAULT'` 的 7,478,746 行**全部是历史 PV**（999423/999328/999437…，
            //       均早于 2026-11-09 删除 `NormalizeToSingleRoute` 之前）；
            //     · 近期 PV（999633/999634）**527,620 行中 DEFAULT 为 0**，RouteCode 是 `'NC,,'`／`'ASSY,,'`／`'MB'`
            //       这类**逗号串——这就是 APS 的真实格式**（工序名序列，非"脏值"）；
            //     · `RoutingOperation.RouteCode` 同形态（源视图 `MES_APS_Routing_Operation_View` 原样提供）。
            //   ⇒ 仅 `null/空`、`'DEFAULT'`、`'NULL'` 视为占位；**含逗号必须放行**。
            var placeholder = rows.Where(r => IsPlaceholderRoute(r.RouteCode)).ToList();
            if (placeholder.Count > 0)
            {
                result[key] = new GateVerdict(false, "PATH_IDENTITY_PLACEHOLDER",
                    $"RouteCode 为占位/脏值（如 DEFAULT 或含逗号）：'{placeholder[0].RouteCode}'；真实 Route+Path 尚不可得");
                continue;
            }

            // ★ 2026-11-23（用户口径纠正）：**同一执行批出现多条 Route+Path 是合法的**——
            //   3号位 提供、我方装载的工艺可能有多套（如 963 部门同时有 J1xx / J2xx 两组），
            //   1号位 按实际选哪套、Task 如实体现。实测 PV999633 的 249,386 条 MACH 任务
            //   **100% 命中** `RoutingOperation`（物料+RouteCode+PathId+OperationCode）。
            //   故判据不是「组内一致」，而是「**每个 Task 的路线必须是我们装载过的那套**」。
            var notInRouting = rows.Where(r => r.RoutingMatchedId is null).ToList();
            if (notInRouting.Count > 0)
            {
                var sample0 = notInRouting[0];
                result[key] = new GateVerdict(false, "PATH_NOT_IN_LOADED_ROUTING",
                    $"{notInRouting.Count} 个 Task 的 RouteCode/PathId/OperationCode 在 RoutingOperation 中不存在" +
                    $"（例：{sample0.RouteCode}/{sample0.PathId}/{sample0.OperationCode}）");
                continue;
            }

            // ④ 位置 / UNLOCATED
            //   ★ 2026-11-23（A29）：**客户订单（SALES_ORDER）的 PI 本就没有 Position 快照**——
            //     B-01 身份闸门规定「客户订单行不得作为 Supply」，其 PI 不进 `PiRemainingFacts`
            //     ⇒ 计算器不产位置行（实测 PV999633：队列 40,509 个 PI 中 10,056 个无位置，**全部是 SALES_ORDER**）。
            //     这类任务的"可开工量"是**订单/任务自身数量**，不是 PI 剩余 ⇒ **不做 PI 位置校验**。
            //     其它来源（非客户订单）的 PI 若无位置，仍按 `PI_POSITION_MISSING` 拒发（原行为不变）。
            var isSalesOrderPi = piOrderType.TryGetValue(key.Item2, out var ot) &&
                                 string.Equals(ot, "SALES_ORDER", StringComparison.OrdinalIgnoreCase);

            if (!isSalesOrderPi)
            {
                if (!posByPi.TryGetValue((g.Pv, key.Item2), out var posGuard) || posGuard.Count == 0)
                { result[key] = new GateVerdict(false, "PI_POSITION_MISSING", $"PI {key.Item2} 在本 PV 无位置快照"); continue; }
            }

            if (isSalesOrderPi)
            {
                // 客户订单：只要求组本身有正数量（已由分组 SQL 保证），不做 PI 位置/可执行量校验。
                result[key] = GateVerdict.Ok;
                continue;
            }

            var posList = posByPi[(g.Pv, key.Item2)];

            // ④/⑤ 合判：**只拦「纯 UNLOCATED」**，部分有位置照发（2026-11-23 口径）
            //   业务含义（01 业务基线 v1.14 §31.3）：`Located + UNLOCATED = RemainingQty`；
            //   `UNLOCATED` = 该部分余量**对不上位置**（ERP 说还有、MES 工序进度/库位无法闭合），
            //   开工找不到料 ⇒ **这部分不能发**；但**已定位的部分可以发**。
            //   实测 PV999633：含 UNLOCATED 的 PI 共 4,874，其中**纯 UNLOCATED 仅 1,647**，
            //   3,227 条是「有位置 ＋ 有 UNLOCATED」——原实现「存在任一即拒」会把这 3,227 条全误拦。
            var located = posList
                .Where(x => !string.Equals(x.PositionType, "UNLOCATED", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (located.Count == 0)
            { result[key] = new GateVerdict(false, "PI_UNLOCATED", $"PI {key.Item2} 全部位置为 UNLOCATED（无可开工部分），不得出站"); continue; }

            var execQty = located
                .Where(x => string.Equals(x.CurrentStageCode, key.Item3, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(x.NextStageCode, key.Item3, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Quantity);
            if (execQty <= 0m)
            { result[key] = new GateVerdict(false, "PI_NO_EXECUTABLE_QTY", $"PI {key.Item2} 在 Stage {key.Item3} 的**已定位**可执行量 <= 0"); continue; }

            result[key] = GateVerdict.Ok;
        }

        return result;
    }

    /// <summary>
    /// **路径身份占位判定**：`DEFAULT`／空／含逗号的拼接脏值都**不是**合法 RouteCode。
    /// （0号位 P11-1：不得默认 `PathId=1`／`RouteCode='DEFAULT'`；真库 `Task.RouteCode` 747 万行全 DEFAULT。）
    /// </summary>
    private static bool IsPlaceholderRoute(string? routeCode)
    {
        if (string.IsNullOrWhiteSpace(routeCode)) return true;
        var v = routeCode.Trim();
        if (v.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return true;
        // ⚠ **含逗号不是占位**：APS 的 `RouteCode` 本形态就是「工序名逗号序列」（如 'NC,,'／',ASSY,,'）。
        //   2026-11-23 核实：该形态同样出现在 MES 源视图与近期 PV 的 Task 上，是真实值。
        return false;
    }

    /// <summary>A29：PI → 订单类型（客户订单的 PI 不产位置快照，门禁需区别对待）。</summary>
    private sealed class PiOrderTypeRow
    {
        public string? Pi { get; set; }
        public string? OrderType { get; set; }
    }

    private sealed class PvGateRow
    {
        public int Id { get; set; }
        public string? Status { get; set; }
        public string? VersionCategory { get; set; }
    }

    private sealed class TaskPathRow
    {
        public int Pv { get; set; }
        public string? Pi { get; set; }
        public string? StageCode { get; set; }
        public long TaskId { get; set; }
        public long? MatchedTaskId { get; set; }
        public string? RouteCode { get; set; }
        public int? PathId { get; set; }
        public string? OperationCode { get; set; }
        /// <summary>该 Task 的 (物料, RouteCode, PathId, OperationCode) 在本库 `RoutingOperation`(IsActive=1) 中的命中行；NULL＝不在我们装载的路由里。</summary>
        public long? RoutingMatchedId { get; set; }
    }

    private sealed class PiPositionGateRow
    {
        public int Pv { get; set; }
        public string? Pi { get; set; }
        public string? PositionType { get; set; }
        public decimal Quantity { get; set; }
        public string? CurrentStageCode { get; set; }
        public string? NextStageCode { get; set; }
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