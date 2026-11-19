using System.Data;
using Dapper;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// 夜间批次编排器（2号位职责）
///
/// 时序：
///   Step 1   — 全量订单同步（ERP → Staging → Order_Canonical）
///   **Step 1.5 — BOM 展开请求推送（必须在 Step 1 之后：明细按“此刻”的 Order_Canonical 生成）**
///   Step 2   — 创建 ScheduleRun（DataCutoffTime 在此锁定，供后续 MES 快照使用）
///   Step 3   — 创建 PlanVersion（每 Domain 一个：SourceScheduleRunId + DomainKey）
///   Step 4   — 订单装载到分区表（每 Domain 一次：Order_Canonical → Order，SP 按 DomainKey 筛选）
///   **Step 4.6 — BOM 批展开（ODS 侧整批重建 → READY）**（必须晚于 Step 2，见该处注释）
///   Step 5   — BOM展开结果接货（ODS.MES_APS_BOM_Workset → APS.APS_BOM_RAW，同时生成 OrderBomRequestLink）
///
/// 🔴 2026-11-18 顺序修正（否则每天必漏一批订单）：
///   原设计把 `bom-request-push`(00:00) / `bom-batch-expand`(00:05) 放在本编排器(00:30) **之前**，
///   而 Step 1 的全量订单同步在 00:30 才跑 ⇒ 推送明细用的是**同步之前**的 `Order_Canonical`。
///   之后 Step 4 把新订单装进 PV，Step 5 却只按旧明细建 `OrderBomRequestLink`
///   ⇒ **推送之后、Step 1 之前进来的订单全部无链接**（实测 PV999550：1,839 单无链接，
///   其中 1,777 单是当天 12:35 全量同步才进 canonical；这些单照排，但 BOM 退化成并集、
///   跨厂目标厂回退成源厂 ⇒ 74 张 TJ 单直接扣 BJ 库存）。
///   修法：把「推送 + 展开」收进本编排器，紧跟 Step 1 之后。
///
/// 异常策略：Step 1~4 任一步失败则中断；BOM 两段（1.5/1.6）失败**不硬断**（转 WARN，
///   与 Step 5「未 READY 则跳过接货」的既有语义一致），避免 MES 侧抖动拖垮整轮订单准备。
/// </summary>
public class NightlyBatchOrchestrator : INightlyBatchOrchestrator
{
    private readonly IERPOrderSyncService _orderSyncService;
    private readonly IOrderLoadingService _orderLoadingService;
    private readonly IBOMResultPullService _bomResultPullService;
    private readonly IScheduleRunService _scheduleRunService;
    private readonly IBOMRequestService _bomRequestService;
    private readonly IBomBatchExpandService _bomExpandService;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<NightlyBatchOrchestrator> _logger;

    public NightlyBatchOrchestrator(
        IERPOrderSyncService orderSyncService,
        IOrderLoadingService orderLoadingService,
        IBOMResultPullService bomResultPullService,
        IScheduleRunService scheduleRunService,
        IBOMRequestService bomRequestService,
        IBomBatchExpandService bomExpandService,
        DatabaseConnectionManager connectionManager,
        ILogger<NightlyBatchOrchestrator> logger)
    {
        _orderSyncService    = orderSyncService    ?? throw new ArgumentNullException(nameof(orderSyncService));
        _orderLoadingService = orderLoadingService ?? throw new ArgumentNullException(nameof(orderLoadingService));
        _bomResultPullService = bomResultPullService ?? throw new ArgumentNullException(nameof(bomResultPullService));
        _scheduleRunService  = scheduleRunService  ?? throw new ArgumentNullException(nameof(scheduleRunService));
        _bomRequestService   = bomRequestService   ?? throw new ArgumentNullException(nameof(bomRequestService));
        _bomExpandService    = bomExpandService    ?? throw new ArgumentNullException(nameof(bomExpandService));
        _connectionManager   = connectionManager   ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var batchNo = $"NIGHTLY_{DateTime.Now:yyyyMMdd_HHmmss}";
        _logger.LogInformation("夜间批次开始: {BatchNo}", batchNo);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // ═══════════════════════════════════════════
            // Step 1: 全量订单同步（ERP → Staging → Order_Canonical）
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 1: 全量订单同步", batchNo);
            await _orderSyncService.FullSyncAsync(cancellationToken);

            // ═══════════════════════════════════════════
            // Step 1.5: BOM 展开请求推送（**必须在 Step 1 之后**）
            //   明细取自「此刻」的 Order_Canonical；先同步再推送，才不会漏掉当天新单。
            //   幂等：`BomNightBatchResolver` 要求「当日恰好一条 BomRequestPush/SUCCESS」，
            //   故若当日已有推送成功记录，**不再重复推送**（重复会让解析器判定不唯一 ⇒ 展开 SKIPPED）。
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 1.5: BOM 展开请求推送", batchNo);
            try
            {
                var existingPush = await BomNightBatchResolver.ResolveAsync(_connectionManager, DateTime.Now, cancellationToken);
                if (existingPush.IsUnique)
                {
                    _logger.LogWarning(
                        "[{BatchNo}] 当日已有 BOM 推送成功记录（BatchNo={BatchNo}），本步跳过、复用之；" +
                        "若该批早于本次全量同步，今天新增订单仍可能缺链接（重跑场景的已知边界）。",
                        batchNo, existingPush.BatchNo);
                    await LogETLAsync(batchNo, BomNightBatchIdentity.PushStep,
                        $"SKIPPED：当日已有一条推送成功记录 {existingPush.BatchNo}（不重复推送）", "SKIPPED");
                }
                else if (existingPush.Records.Count > 1)
                {
                    _logger.LogError(
                        "[{BatchNo}] 当日 BOM 推送成功记录 {Count} 条，无法唯一识别 ⇒ 不再推送，展开/接货将按既有规则跳过",
                        batchNo, existingPush.Records.Count);
                    await LogETLAsync(batchNo, BomNightBatchIdentity.PushStep,
                        $"SKIPPED：当日已有 {existingPush.Records.Count} 条推送成功记录，无法唯一识别", "SKIPPED");
                }
                else
                {
                    var pushResult = await _bomRequestService.PushBOMRequestToODSAsync(cancellationToken);
                    _logger.LogInformation(
                        "[{BatchNo}] BOM 请求推送完成：BatchNo={BomBatchNo}, RootCount={Roots}, TotalOrders={Total}",
                        batchNo, pushResult.BatchNo, pushResult.RootCount, pushResult.TotalOrderCount);
                }
            }
            catch (Exception ex)
            {
                // 不硬断：与 Step 5「未 READY 则跳过接货」的既有语义一致；下游会因 BOM 缺失转 WARN。
                _logger.LogError(ex, "[{BatchNo}] Step 1.5 BOM 请求推送失败（不阻断，后续 BOM 接货将跳过）", batchNo);
                await LogETLAsync(batchNo, BomNightBatchIdentity.PushStep,
                    $"FAILED（不阻断）：{ex.Message}", "FAILED");
            }

            // ═══════════════════════════════════════════
            // Step 2: 创建 ScheduleRun（锁定 DataCutoffTime，供 00:40~50 MES快照使用）
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 2: 创建 ScheduleRun", batchNo);
            var scheduleRunId = await _scheduleRunService.CreateScheduleRunAsync(cancellationToken);
            _logger.LogInformation("[{BatchNo}] ScheduleRun 已创建: ScheduleRunId={ScheduleRunId}", batchNo, scheduleRunId);

            // ═══════════════════════════════════════════
            // Step 3: 创建计划版本（每 Domain 一个，写入 SourceScheduleRunId + DomainKey）
            // 依据：PM 终裁 2026-09-02 —— 每 Domain 独立 PlanVersion，按冻结 ExpectedDomainKeysJson 逐个创建
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 3: 创建计划版本（每 Domain 一个）", batchNo);
            var domainKeys = await _scheduleRunService.GetExpectedDomainKeysAsync(scheduleRunId, cancellationToken);
            var planVersions = new List<(string DomainKey, int PlanVersionId)>(domainKeys.Count);

            foreach (var domainKey in domainKeys)
            {
                var planVersionId = await CreatePlanVersionAsync(batchNo, scheduleRunId, domainKey, cancellationToken);
                planVersions.Add((domainKey, planVersionId));
                _logger.LogInformation("[{BatchNo}] 计划版本已创建: DomainKey={DomainKey}, PlanVersionId={PlanVersionId}",
                    batchNo, domainKey, planVersionId);
            }

            // ═══════════════════════════════════════════
            // Step 4: 订单装载到分区表（每 Domain 一次；SP 内部按 DomainKey 定位 DomainDefinition 筛选）
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 4: 订单装载到分区表（每 Domain 一次）", batchNo);
            var loadedCount = 0;

            foreach (var (domainKey, planVersionId) in planVersions)
            {
                var loaded = await _orderLoadingService.LoadOrdersToPartitionTableAsync(planVersionId, cancellationToken);
                loadedCount += loaded;
                _logger.LogInformation("[{BatchNo}] Domain {DomainKey} 订单装载完成: {LoadedCount}条",
                    batchNo, domainKey, loaded);
            }

            _logger.LogInformation("[{BatchNo}] 订单装载合计: {LoadedCount}条（{DomainCount}个 Domain）",
                batchNo, loadedCount, planVersions.Count);

            // ═══════════════════════════════════════════
            // Step 4.5: 归域失败捡漏（非阻塞）——检测未归入任何 Domain 的活跃订单，登记 WARN 日志标记数据问题
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 4.5: 归域失败捡漏", batchNo);
            var unassignedCount = await _orderLoadingService.DetectUnassignedOrdersAsync(
                planVersions.Select(p => p.PlanVersionId).ToList(), cancellationToken);
            _logger.LogInformation("[{BatchNo}] 归域失败捡漏完成: {UnassignedCount}条未归域订单（已登记 WARN 日志）",
                batchNo, unassignedCount);

            // ═══════════════════════════════════════════
            // Step 4.6: BOM 批展开（ODS 四表整批重建 → 批次转 READY）
            //
            // 🔴 2026-11-19 位置调整：**必须排在 Step 2 之后**。
            //   原因（上线时序风险，实测量化）：Step 1 全量同步约 3m46s、推送约 1min、展开约 5m18s；
            //   若展开排在 Step 2 之前，ScheduleRun 要到 00:40 之后才建出来，
            //   而 `mes-snapshot-workorder` 正是 00:40 触发，`GetCurrentRunAsync()` 取不到当日 RUNNING Run
            //   会**直接抛错**（`MESSnapshotSyncService` 明确 throw，不再静默）⇒ 当晚 MES 工单快照整批丢失，
            //   Pegging 无 Stage 进度事实。排到 Step 4.5 之后、Step 5 之前，Run 仍在 ~00:35 建出来，
            //   展开用掉 5 分钟正好落在 00:40~45 这段空档，接货随后完成。
            //   破坏性不变（`sp_ExpandBOMBatch_vNext` 四表 truncate），失败仍不硬断（Step 5 会因未 READY 跳过并记 WARN）。
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 4.6: BOM 批展开", batchNo);
            try
            {
                var expandResult = await _bomExpandService.ExpandNightBatchAsync(cancellationToken);
                _logger.LogInformation(
                    "[{BatchNo}] BOM 批展开：BatchNo={BomBatchNo}, Expanded={Expanded}, PENDING积压={Backlog}, Issues={IssueCount}",
                    batchNo, expandResult.BatchNo, expandResult.Expanded,
                    expandResult.PendingBacklogCount, expandResult.Issues.Count);
                if (!expandResult.Expanded)
                {
                    _logger.LogWarning("[{BatchNo}] BOM 批未展开：{Issues}",
                        batchNo, string.Join(" | ", expandResult.Issues));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{BatchNo}] Step 4.6 BOM 批展开失败（不阻断，后续 BOM 接货将跳过）", batchNo);
            }

            // ═══════════════════════════════════════════
            // Step 5: BOM展开结果接货（ODS → APS）
            // BOM 是物料级事实、按批次接货一次；OrderBomRequestLink 跨本批全部 Domain PlanVersion 映射
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{BatchNo}] Step 5: BOM展开结果接货", batchNo);

            // 0号位 2026-10-17 §三.C：接货必须锁定「**本次夜间**那一个批号」，不再查「任意最新 READY」。
            var night = await BomNightBatchResolver.ResolveAsync(_connectionManager, DateTime.Now, cancellationToken);
            string? bomBatchNo = null;
            var bomPulledCount = 0;
            var bomReady = false;

            if (!night.IsUnique)
            {
                _logger.LogWarning("[{BatchNo}] BOM接货跳过：{Reason}", batchNo, night.Reason);
                await LogETLAsync(batchNo, BomNightBatchIdentity.PullStep,
                    $"SKIPPED：{night.Reason}（未接货，不回落任意最新 READY）", "SKIPPED");
            }
            else
            {
                bomBatchNo = await _bomResultPullService.FindReadyBatchAsync(night.BatchNo!, cancellationToken);
                if (bomBatchNo != null)
                {
                    var planVersionIds = planVersions.Select(p => p.PlanVersionId).ToList();
                    bomPulledCount = await _bomResultPullService.PullBOMResultFromODSAsync(bomBatchNo, planVersionIds, cancellationToken);
                    bomReady = true;
                    _logger.LogInformation("[{BatchNo}] BOM接货完成: BOMBatchNo={BOMBatchNo}, 行数={PulledCount}",
                        batchNo, bomBatchNo, bomPulledCount);
                }
                else
                {
                    _logger.LogWarning(
                        "[{BatchNo}] BOM接货跳过：本次批次 {ExpectedBatchNo} 尚未 READY（或行数为 0）",
                        batchNo, night.BatchNo);
                    await LogETLAsync(batchNo, BomNightBatchIdentity.PullStep,
                        $"SKIPPED：本次批次 {night.BatchNo} 未 READY（未接货，不回落任意最新 READY）", "SKIPPED");
                }
            }

            // ═══════════════════════════════════════════
            // 记录批次日志
            // 0号位 §三.C：只有 BOM 缺口时不得再记无条件 SUCCESS —— 用既有 WARN 表示「夜间准备不完整」，
            //   但 WARN 不是完成排程的许可（下游仍按严格 BOM 输入有效性判定）。
            // ═══════════════════════════════════════════
            stopwatch.Stop();
            var nightlyStatus = bomReady ? "SUCCESS" : "WARN";
            var bomSummary = bomReady
                ? $"BOM接货:{bomPulledCount}行"
                : $"BOM未接货（{night.Reason}）";

            await LogETLAsync(batchNo, "NightlyBatch",
                $"夜间批次{(bomReady ? "完成" : "准备不完整")} | ScheduleRunId:{scheduleRunId} | Domain数:{planVersions.Count} | 装载订单:{loadedCount} | 未归域:{unassignedCount} | {bomSummary} | 耗时:{stopwatch.ElapsedMilliseconds}ms",
                nightlyStatus);

            _logger.LogInformation("夜间批次{Status}: {BatchNo}, 耗时={Elapsed}ms",
                bomReady ? "完成" : "准备不完整（BOM 未接货）", batchNo, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "夜间批次失败: {BatchNo}", batchNo);

            await LogETLAsync(batchNo, "NightlyBatch",
                $"夜间批次失败: {ex.Message}",
                "FAILED");

            throw;
        }
    }

    /// <summary>
    /// 创建计划版本，同时写入 SourceScheduleRunId + DomainKey（每 Domain 一个）
    /// VersionCode 格式: NIGHTLY_yyyyMMdd_{DomainKey}，PlanHorizon: 今天起 90 天
    /// </summary>
    private async Task<int> CreatePlanVersionAsync(string batchNo, int scheduleRunId, string domainKey, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var versionCode = $"NIGHTLY_{now:yyyyMMdd}_{domainKey}";

        var sql = @"
            -- ============================================================
            -- 2026-11-05 修复：同日重跑必撞键
            --   原实现：「先把同 VersionCode 的旧壳 UPDATE 成 ARCHIVED，再 INSERT」，
            --     并注释「目的是给 INSERT 让路（UQ_PlanVersion_VersionCode 唯一约束）」。
            --   **该假设不成立**：`UQ_PlanVersion_VersionCode` 是**无过滤**唯一索引（实测
            --     `has_filter=0`、`filter_definition=NULL`），**不看 Status** ⇒ 归档行照样占着码
            --     ⇒ 同一天第二次跑 INSERT 必报 Msg 2627。
            --   （另一条 `UQ_PlanVersion_OneActivePerDomain` 才是过滤索引 `WHERE Status='ACTIVE'`。）
            --   实测：2026-11-05 08:51 重跑夜间批，第 1 个域就撞 2627，整批中断。
            --
            --   现改为「**有则 UPDATE 复用同一行，无则 INSERT**」：
            --     · 天然幂等，Id 稳定（下游 [Order] / OrderLoadRejection 等按 PlanVersionId 挂着，不用重建）；
            --     · 同日的旧壳本来就没算完（BUILDING/ARCHIVED 且 Task=0），复用不丢任何有效结果。
            --   ⚠️ 跨天的历史版本**仍不在这里动**：由新版本算完自动采用时的 ReplaceActiveAsync 归档旧 ACTIVE。
            -- ============================================================
            DECLARE @Id INT = (SELECT Id FROM PlanVersion WHERE VersionCode = @VersionCode);

            IF @Id IS NOT NULL
            BEGIN
                UPDATE PlanVersion
                SET VersionCategory        = 'DAILY_BASELINE',
                    PlanHorizonStart       = @PlanHorizonStart,
                    PlanHorizonEnd         = @PlanHorizonEnd,
                    ComputeMode            = 'FULL',
                    Status                 = 'BUILDING',
                    BatchNo                = @BatchNo,
                    SourceScheduleRunId    = @SourceScheduleRunId,
                    DomainKey              = @DomainKey,
                    CreatedBy              = 'SYSTEM',
                    CreatedAt              = GETDATE(),
                    ComputedAt             = NULL,
                    ArchivedAt             = NULL,
                    ActivatedAt            = NULL,
                    ActivatedBy            = NULL,
                    TotalOrders            = NULL,
                    TotalTasks             = NULL,
                    SnapshotFilePath       = NULL,
                    SnapshotFileSize       = NULL,
                    SnapshotCompressedSize = NULL,
                    SnapshotFileHash       = NULL,
                    SnapshotCreatedAt      = NULL
                WHERE Id = @Id;

                SELECT @Id;
            END
            ELSE
            BEGIN
                INSERT INTO PlanVersion (
                    VersionCode, VersionCategory, PlanHorizonStart, PlanHorizonEnd,
                    ComputeMode, Status, BatchNo, SourceScheduleRunId, DomainKey, CreatedBy, CreatedAt
                )
                VALUES (
                    @VersionCode, 'DAILY_BASELINE', @PlanHorizonStart, @PlanHorizonEnd,
                    'FULL', 'BUILDING', @BatchNo, @SourceScheduleRunId, @DomainKey, 'SYSTEM', GETDATE()
                );

                SELECT CAST(SCOPE_IDENTITY() AS INT);
            END";

        var parameters = new DynamicParameters();
        parameters.Add("@VersionCode", versionCode);
        parameters.Add("@PlanHorizonStart", now.Date);
        parameters.Add("@PlanHorizonEnd", now.Date.AddDays(90));
        parameters.Add("@BatchNo", batchNo);
        parameters.Add("@SourceScheduleRunId", scheduleRunId);
        parameters.Add("@DomainKey", domainKey);

        var planVersionId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, parameters, db: DatabaseId.APS);

        if (planVersionId <= 0)
            throw new InvalidOperationException($"创建 PlanVersion 失败: VersionCode={versionCode}");

        return planVersionId;
    }

    /// <summary>
    /// 记录 ETL 日志到 APS_ETL_Log
    /// </summary>
    private async Task LogETLAsync(string batchNo, string step, string message, string status)
    {
        try
        {
            var sql = @"
                INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())";

            await _connectionManager.ExecuteAsync(sql, new { BatchNo = batchNo, Step = step, Message = message, Status = status },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入ETL日志失败（非致命）");
        }
    }
}
