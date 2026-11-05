using Dapper;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Maintenance;

/// <summary>
/// 写入表保留期清理（2号位）。规则 = 0号位 2026-11-02 v2.0 §2.1 的 D1~D4，**不自拟天数**。
/// </summary>
/// <remarks>
/// <para><b>硬约束（逐条都可对照原文）</b></para>
/// <list type="number">
/// <item>D4：不在 Solver / 主落库事务中删 —— 本服务独立连接、独立语句，不参与任何事务。</item>
/// <item>D4：不新增 Hangfire Cron —— 本服务不自建定时器，只提供方法。</item>
/// <item>D4：清理失败不反推正式排程失败 —— <see cref="RunAsync"/> 内部吞异常并记 <see cref="RetentionRunResult.Error"/>。</item>
/// <item>默认 dryRun —— 一行不删是缺省行为，真删要显式传 false。</item>
/// <item>正式身份留存 —— 只删六张数据表，**永不删 <c>PlanVersion</c> / <c>ScheduleRun</c> 本身**。</item>
/// </list>
/// <para>
/// <b>保护集</b>（D1 原文「ACTIVE 和历史正式 PV 关键解释、FAILED Run 诊断、必要审计与 Task/Pegging 血缘」），
/// 本服务从严取三条并集，任一条命中即不删：
/// ① PV 状态 ∈ (BUILDING, Computing, CANDIDATE, ACTIVE) —— 现行与在途；
/// ② 所属 Run 状态 ∈ (RUNNING, FAILED, PARTIAL_SUCCESS) —— 失败诊断保留；
/// ③ 每 Domain 最近 2 个成功 Run —— 安全垫，与 D2 同款。
/// </para>
/// </remarks>
public sealed class RetentionMaintenanceService : IRetentionMaintenanceService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<RetentionMaintenanceService> _logger;

    /// <summary>单批删除行数。D4 要求「小批维护」，不一次扫全表。</summary>
    private const int BatchSize = 20000;

    /// <summary>单条规则单次运行最多删多少行——保险丝，触发只记录、不静默继续。</summary>
    private const long MaxRowsPerRule = 500_000;

    /// <summary>D2 的同款安全垫：每 Domain 保留最近几个成功 Run。</summary>
    private const int KeepSuccessfulRunsPerDomain = 2;

    /// <summary>
    /// 保护集 CTE。所有测算/删除语句共用同一份，避免「测算用一套、删除用另一套」。
    /// </summary>
    private const string ProtectCte = @"
;WITH RunRanked AS (
    SELECT pv.Id AS PlanVersionId,
           ROW_NUMBER() OVER (PARTITION BY ISNULL(pv.DomainKey, '(NODOMAIN)')
                              ORDER BY sr.CompletedAt DESC, sr.Id DESC) AS rnSuccess
      FROM PlanVersion pv
      JOIN ScheduleRun  sr ON sr.Id = pv.SourceScheduleRunId
     WHERE sr.Status = 'COMPLETED'
),
Protected AS (
    SELECT PlanVersionId FROM RunRanked WHERE rnSuccess <= @KeepRuns
    UNION
    SELECT Id FROM PlanVersion
     WHERE Status IN ('BUILDING', 'Computing', 'CANDIDATE', 'ACTIVE')
    UNION
    SELECT pv.Id FROM PlanVersion pv
      JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
     WHERE sr.Status IN ('RUNNING', 'FAILED', 'PARTIAL_SUCCESS')
)
";

    public RetentionMaintenanceService(
        DatabaseConnectionManager connectionManager,
        ILogger<RetentionMaintenanceService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetentionPlanItem>> PlanAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var items = new List<RetentionPlanItem>(6);

        // ── D1-a ExplainTrace：在线 30 天 ─────────────────────────────
        items.Add(await BuildItemAsync(
            rule: "D1-a ExplainTrace",
            table: "ExplainTrace",
            windowDays: 30,
            now: now,
            countSql: ProtectCte + @"
SELECT COUNT_BIG(*)                                            AS TotalRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut THEN 1 END)      AS OverWindowRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut
                       AND EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId)
                      THEN 1 END)                              AS ProtectedRows
  FROM ExplainTrace t",
            note: null,
            cancellationToken: cancellationToken));

        // ── D1-b ScheduleExplanationFact：在线 90 天 ───────────────────
        // ⚠ 0号位 要求：「须先证明 ScheduleRunId IS NULL 的来源不明记录不被误删」
        //   ⇒ ScheduleRunId IS NULL 一律计入受保护，永不删。
        items.Add(await BuildItemAsync(
            rule: "D1-b ScheduleExplanationFact",
            table: "ScheduleExplanationFact",
            windowDays: 90,
            now: now,
            countSql: ProtectCte + @"
SELECT COUNT_BIG(*)                                            AS TotalRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut THEN 1 END)      AS OverWindowRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut
                       AND (t.ScheduleRunId IS NULL
                            OR EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId))
                      THEN 1 END)                              AS ProtectedRows
  FROM ScheduleExplanationFact t",
            note: "ScheduleRunId IS NULL 的来源不明记录一律不删",
            cancellationToken: cancellationToken));

        // ── D2 ProductionInstructionPositionSnapshot：30 天 + 每域 2 个成功 Run ──
        items.Add(await BuildItemAsync(
            rule: "D2 PositionSnapshot",
            table: "ProductionInstructionPositionSnapshot",
            windowDays: 30,
            now: now,
            countSql: ProtectCte + @"
SELECT COUNT_BIG(*)                                            AS TotalRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut THEN 1 END)      AS OverWindowRows,
       COUNT_BIG(CASE WHEN t.CreatedAt < @Cut
                       AND EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId)
                      THEN 1 END)                              AS ProtectedRows
  FROM ProductionInstructionPositionSnapshot t",
            note: "每 Domain 最近 2 个成功 Run 取更严保护",
            cancellationToken: cancellationToken));

        // ── D3-a ERP_Order_Staging：已解决失败 90 天；未解决不删 ────────
        // 「已解决」判据：NextActionCode 不是 MANUAL_REVIEW。
        // ⚠ 这是我方从严的暂定判据；0号位 若另有结案标识，按他的改。
        items.Add(await BuildItemAsync(
            rule: "D3-a ERP_Order_Staging(已解决失败)",
            table: "ERP_Order_Staging",
            windowDays: 90,
            now: now,
            countSql: @"
SELECT COUNT_BIG(*) AS TotalRows,
       COUNT_BIG(CASE WHEN SyncStatus = 'FAILED'
                       AND ISNULL(NextActionCode, 'MANUAL_REVIEW') <> 'MANUAL_REVIEW'
                      THEN 1 END) AS OverWindowRows,
       COUNT_BIG(CASE WHEN SyncStatus = 'FAILED'
                       AND ISNULL(NextActionCode, 'MANUAL_REVIEW') <> 'MANUAL_REVIEW'
                       AND SyncedAt >= @Cut THEN 1 END) AS ProtectedRows
  FROM ERP_Order_Staging",
            note: "未解决失败（NextActionCode = MANUAL_REVIEW）永不自动删",
            cancellationToken: cancellationToken));

        // ── D3-b APS_ETL_Log：普通 30 天／异常 90 天 ───────────────────
        // 原文未提 SUCCESS/SKIPPED/PARTIAL，按「普通」30 天处理并在此标明。
        items.Add(await BuildItemAsync(
            rule: "D3-b APS_ETL_Log",
            table: "APS_ETL_Log",
            windowDays: 30,
            now: now,
            countSql: @"
SELECT COUNT_BIG(*) AS TotalRows,
       COUNT_BIG(CASE WHEN (Status IN ('INFO', 'SUCCESS', 'SKIPPED', 'PARTIAL') AND CreatedAt < @Cut)
                        OR (Status IN ('WARNING', 'WARN', 'ERROR', 'FAILED')
                            AND CreatedAt < DATEADD(DAY, -90, @Now))
                      THEN 1 END) AS OverWindowRows,
       0 AS ProtectedRows
  FROM APS_ETL_Log",
            note: "异常档 90 天；SUCCESS/SKIPPED/PARTIAL 按普通档 30 天（原文未列，待 0号位 复核）",
            cancellationToken: cancellationToken));

        // ── D3-c ComputeStageProgress：完成 30 天／异常 90 天，未结案不删 ──
        items.Add(await BuildItemAsync(
            rule: "D3-c ComputeStageProgress",
            table: "ComputeStageProgress",
            windowDays: 30,
            now: now,
            countSql: @"
SELECT COUNT_BIG(*) AS TotalRows,
       COUNT_BIG(CASE WHEN Status = 'DONE'   AND ISNULL(EndedAt, StartedAt) < @Cut              THEN 1 END)
     + COUNT_BIG(CASE WHEN Status = 'FAILED' AND ISNULL(EndedAt, StartedAt) < DATEADD(DAY, -90, @Now) THEN 1 END)
       AS OverWindowRows,
       COUNT_BIG(CASE WHEN Status = 'STARTED' THEN 1 END) AS ProtectedRows
  FROM ComputeStageProgress",
            note: "Status = STARTED（未结案）永不删",
            cancellationToken: cancellationToken));

        return items;
    }

    /// <inheritdoc />
    public async Task<RetentionRunResult> RunAsync(bool dryRun = true, CancellationToken cancellationToken = default)
    {
        var result = new RetentionRunResult { DryRun = dryRun, RanAt = DateTime.Now };

        try
        {
            var plan = await PlanAsync(cancellationToken);
            result = new RetentionRunResult { DryRun = dryRun, RanAt = result.RanAt, Items = plan };

            if (dryRun)
            {
                _logger.LogInformation(
                    "保留期清理【dry-run】测算完成：可删合计 {TotalDeletable} 行，未删除任何数据。",
                    result.TotalDeletable);
                return result;
            }

            long deleted = 0;
            deleted += await DeleteExplainTraceAsync(cancellationToken);
            deleted += await DeleteSefAsync(cancellationToken);
            deleted += await DeletePositionSnapshotAsync(cancellationToken);
            deleted += await DeleteStagingFailedAsync(cancellationToken);
            deleted += await DeleteEtlLogAsync(cancellationToken);
            deleted += await DeleteComputeStageProgressAsync(cancellationToken);

            result.DeletedRows = deleted;
            _logger.LogInformation("保留期清理完成：实际删除 {DeletedRows} 行。", deleted);
        }
        catch (Exception ex)
        {
            // D4：清理失败不反推正式排程失败 —— 只记，不抛。
            result.Error = ex.Message;
            _logger.LogError(ex, "保留期清理失败（不影响排程结果）。");
        }

        return result;
    }

    // ─────────────────────────── 删除实现 ───────────────────────────
    // 每条规则：分批 DELETE TOP(@Batch)，直到删不动或触及保险丝。
    // 保护集用同一份 ProtectCte，与测算完全一致。

    private Task<long> DeleteExplainTraceAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D1-a ExplainTrace",
        windowDays: 30,
        now: DateTime.Now,
        deleteSql: ProtectCte + @"
DELETE TOP (@Batch) t
  FROM ExplainTrace t
 WHERE t.CreatedAt < @Cut
   AND NOT EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId)",
        ct: ct);

    private Task<long> DeleteSefAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D1-b ScheduleExplanationFact",
        windowDays: 90,
        now: DateTime.Now,
        deleteSql: ProtectCte + @"
DELETE TOP (@Batch) t
  FROM ScheduleExplanationFact t
 WHERE t.CreatedAt < @Cut
   AND t.ScheduleRunId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId)",
        ct: ct);

    private Task<long> DeletePositionSnapshotAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D2 PositionSnapshot",
        windowDays: 30,
        now: DateTime.Now,
        deleteSql: ProtectCte + @"
DELETE TOP (@Batch) t
  FROM ProductionInstructionPositionSnapshot t
 WHERE t.CreatedAt < @Cut
   AND NOT EXISTS (SELECT 1 FROM Protected p WHERE p.PlanVersionId = t.PlanVersionId)",
        ct: ct);

    private Task<long> DeleteStagingFailedAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D3-a ERP_Order_Staging",
        windowDays: 90,
        now: DateTime.Now,
        deleteSql: @"
DELETE TOP (@Batch)
  FROM ERP_Order_Staging
 WHERE SyncStatus = 'FAILED'
   AND ISNULL(NextActionCode, 'MANUAL_REVIEW') <> 'MANUAL_REVIEW'
   AND SyncedAt < @Cut",
        ct: ct);

    private Task<long> DeleteEtlLogAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D3-b APS_ETL_Log",
        windowDays: 30,
        now: DateTime.Now,
        deleteSql: @"
DELETE TOP (@Batch)
  FROM APS_ETL_Log
 WHERE (Status IN ('INFO', 'SUCCESS', 'SKIPPED', 'PARTIAL') AND CreatedAt < @Cut)
    OR (Status IN ('WARNING', 'WARN', 'ERROR', 'FAILED')      AND CreatedAt < DATEADD(DAY, -90, @Now))",
        ct: ct);

    private Task<long> DeleteComputeStageProgressAsync(CancellationToken ct) => DeleteInBatchesAsync(
        ruleName: "D3-c ComputeStageProgress",
        windowDays: 30,
        now: DateTime.Now,
        deleteSql: @"
DELETE TOP (@Batch)
  FROM ComputeStageProgress
 WHERE (Status = 'DONE'   AND ISNULL(EndedAt, StartedAt) < @Cut)
    OR (Status = 'FAILED' AND ISNULL(EndedAt, StartedAt) < DATEADD(DAY, -90, @Now))",
        ct: ct);

    /// <summary>
    /// 分批删除到删不动为止；单规则累计达 <see cref="MaxRowsPerRule"/> 即停并记 WARN（保险丝）。
    /// </summary>
    private async Task<long> DeleteInBatchesAsync(
        string ruleName, int windowDays, DateTime now, string deleteSql, CancellationToken ct)
    {
        var parameters = new
        {
            Cut    = now.AddDays(-windowDays),
            Now    = now,
            Batch  = BatchSize,
            KeepRuns = KeepSuccessfulRunsPerDomain
        };

        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            var affected = await _connectionManager.ExecuteAsync(
                deleteSql, parameters, db: DatabaseId.APS, commandTimeout: 300);

            total += affected;

            if (affected < BatchSize)
                break;

            if (total >= MaxRowsPerRule)
            {
                _logger.LogWarning(
                    "保留期清理【{Rule}】已达单次保险丝 {Max} 行，本轮停止；剩余留待下一次维护。",
                    ruleName, MaxRowsPerRule);
                break;
            }
        }

        if (total > 0)
            _logger.LogInformation("保留期清理【{Rule}】删除 {Rows} 行。", ruleName, total);

        return total;
    }

    /// <summary>跑一条规则的三个数并组装成 <see cref="RetentionPlanItem"/>。</summary>
    private async Task<RetentionPlanItem> BuildItemAsync(
        string rule, string table, int windowDays, DateTime now, string countSql,
        string? note, CancellationToken cancellationToken)
    {
        var cut = now.AddDays(-windowDays);

        var row = await _connectionManager.QueryFirstOrDefaultAsync<Counts>(
            countSql,
            new { Cut = cut, Now = now, KeepRuns = KeepSuccessfulRunsPerDomain },
            db: DatabaseId.APS, commandTimeout: 300);

        var total     = row?.TotalRows ?? 0;
        var over      = row?.OverWindowRows ?? 0;
        var protectedRows = row?.ProtectedRows ?? 0;

        return new RetentionPlanItem
        {
            Rule             = rule,
            Table            = table,
            OnlineWindowDays = windowDays,
            TotalRows        = total,
            OverWindowRows   = over,
            ProtectedRows    = protectedRows,
            DeletableRows    = Math.Max(0, over - protectedRows),
            Note             = note
        };
    }

    /// <summary>Dapper 行映射（三个 COUNT 都走本类）。</summary>
    private sealed class Counts
    {
        public long TotalRows { get; set; }
        public long OverWindowRows { get; set; }
        public long ProtectedRows { get; set; }
    }
}
