using System.Data;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// PI 级 XC 夜间快照同步服务（2号位职责 — 每日 00:28，nightly-batch 锁 DataCutoffTime 之前）
///
/// 只做一件事：调 `sp_SyncPiXcSnapshot`，把 ODS 的 PI 号别 XC 余额落到 APS 本地
/// `InventoryFact_PiXc`。Run 期一律读本地快照，**不再直连** `[mes]` 链接服务器。
///
/// 失败语义：SP 返回 @ErrorMessage 时**不清表**（上一批保留），本服务只记 ETL 日志；
/// 数据是否还能用由 Pegging 侧的「快照新鲜度闸门」判定，避免把技术不可用当成 XC=0。
/// </summary>
public class PiXcSnapshotSyncService : IPiXcSnapshotSyncService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PiXcSnapshotSyncService> _logger;

    private const int CommandTimeoutSeconds = 600;

    /// <summary>与 SP 自身写入 APS_ETL_Log 的 Step 同名 —— Run 侧新鲜度闸门正是按这个 Step 取「最近一次刷新」。</summary>
    private const string SpStepName = "sp_SyncPiXcSnapshot";

    /// <summary>
    /// 批内重试次数。为什么要有：Run 侧的 XC 闸门是 Fail-Closed（最近一次刷新非 SUCCESS 即整轮拒绝），
    /// 而 Hangfire 全局配置了 <c>AutomaticRetry(Attempts = 0)</c> —— 一次 ODS 抖动就会让整晚的排程报废。
    /// 把重试放在服务内，既不改全局策略，又能让「瞬时失败」在闸门看到之前自愈。
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// 重试间隔。00:28 起跑、00:30 nightly-batch 锁 cutoff，
    /// 3 次 × 30s 间隔（含 SP 自身耗时）仍落在 cutoff 之前，不会把切片推到 cutoff 之后。
    /// </summary>
    private const int RetryDelaySeconds = 30;

    public PiXcSnapshotSyncService(
        DatabaseConnectionManager connectionManager,
        ILogger<PiXcSnapshotSyncService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PiXcSnapshotSyncResultDto> SyncAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PiXcSnapshotSyncResultDto? result = null;
        Exception? lastError = null;
        var batchNo = string.Empty;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // 每次尝试单独批号：台账里能直接读出「第几次才成」。
            batchNo = $"PIXC_{DateTime.Now:yyyyMMdd_HHmmss}";
            lastError = null;
            _logger.LogInformation("PI 级 XC 快照同步开始（第 {Attempt}/{MaxAttempts} 次）: BatchNo={BatchNo}",
                attempt, MaxAttempts, batchNo);

            try
            {
                var spParams = new DynamicParameters();
                spParams.Add("@BatchNo", batchNo);
                spParams.Add("@RowsAffected", dbType: DbType.Int32, direction: ParameterDirection.Output);
                spParams.Add("@ErrorMessage", dbType: DbType.String, size: -1, direction: ParameterDirection.Output);

                await _connectionManager.ExecuteAsync(
                    "sp_SyncPiXcSnapshot",
                    spParams,
                    CommandType.StoredProcedure,
                    DatabaseId.APS,
                    commandTimeout: CommandTimeoutSeconds);

                result = new PiXcSnapshotSyncResultDto
                {
                    BatchNo      = batchNo,
                    RowsAffected = spParams.Get<int>("@RowsAffected"),
                    ErrorMessage = spParams.Get<string?>("@ErrorMessage")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            var succeeded = lastError == null && result is { IsSuccess: true };
            var reason    = lastError?.Message ?? result?.ErrorMessage ?? "未知";

            await LogETLAsync(batchNo, "PiXcSnapshotSync.CSharp",
                $"第 {attempt}/{MaxAttempts} 次: RowsAffected={result?.RowsAffected}, Elapsed={stopwatch.ElapsedMilliseconds}ms"
                + (succeeded ? string.Empty : $", Error={reason}"),
                succeeded ? "SUCCESS" : "FAILED");

            if (succeeded)
                break;

            if (attempt < MaxAttempts)
            {
                // 不清表：上一批保留。是否可用由 Run 侧新鲜度闸门判定。
                _logger.LogWarning(lastError,
                    "PI 级 XC 快照同步未成功（第 {Attempt}/{MaxAttempts} 次，保留上一批未清表）: BatchNo={BatchNo}, Error={Reason} ⇒ {Delay}s 后重试",
                    attempt, MaxAttempts, batchNo, reason, RetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), cancellationToken);
            }
        }

        stopwatch.Stop();

        if (result is { IsSuccess: true })
        {
            _logger.LogInformation(
                "PI 级 XC 快照同步完成: BatchNo={BatchNo}, 耗时={Elapsed}ms, 行数={Rows}",
                batchNo, stopwatch.ElapsedMilliseconds, result.RowsAffected);
            return result;
        }

        // 连续失败：SP 抛异常时不会自己写台账，闸门③便会拿上一批的 SUCCESS 行当「最近一次刷新成功」，
        // 于是一整晚都在用过期切片而无人察觉。这里用与 SP 相同的 Step 名补一行 FAILED，
        // 让闸门按「最近一次刷新未成功」整轮拒绝（Fail Closed）。
        var finalReason = lastError?.Message ?? result?.ErrorMessage ?? "未知";
        await LogETLAsync(batchNo, SpStepName,
            $"连续 {MaxAttempts} 次尝试均未成功，最终错误：{finalReason}", "FAILED");

        _logger.LogError(lastError,
            "PI 级 XC 快照同步连续 {MaxAttempts} 次失败，已补写 FAILED 台账供 Run 侧闸门拦截: BatchNo={BatchNo}, Error={Reason}",
            MaxAttempts, batchNo, finalReason);

        if (lastError != null)
            throw new InvalidOperationException($"PI 级 XC 快照同步连续 {MaxAttempts} 次失败：{finalReason}", lastError);

        return result!;
    }

    private async Task LogETLAsync(string batchNo, string step, string message, string status)
    {
        try
        {
            await _connectionManager.ExecuteAsync(
                @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                  VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())",
                new { BatchNo = batchNo, Step = step, Message = message, Status = status },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 ETL 日志失败（非致命）");
        }
    }
}
