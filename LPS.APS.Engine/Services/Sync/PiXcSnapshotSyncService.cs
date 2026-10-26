using System.Data;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// PI 级 XC 夜间快照同步服务（2号位职责 — 每日 00:37）
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
        var batchNo = $"PIXC_{DateTime.Now:yyyyMMdd_HHmmss}";
        _logger.LogInformation("PI 级 XC 快照同步开始: BatchNo={BatchNo}", batchNo);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

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

            stopwatch.Stop();

            var result = new PiXcSnapshotSyncResultDto
            {
                BatchNo      = batchNo,
                RowsAffected = spParams.Get<int>("@RowsAffected"),
                ErrorMessage = spParams.Get<string?>("@ErrorMessage")
            };

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "PI 级 XC 快照同步完成: BatchNo={BatchNo}, 耗时={Elapsed}ms, 行数={Rows}",
                    batchNo, stopwatch.ElapsedMilliseconds, result.RowsAffected);
            }
            else
            {
                // 不清表：上一批保留。是否可用由 Run 侧新鲜度闸门判定。
                _logger.LogError(
                    "PI 级 XC 快照同步SP返回错误（保留上一批，未清表）: BatchNo={BatchNo}, Error={ErrorMessage}",
                    batchNo, result.ErrorMessage);
            }

            await LogETLAsync(batchNo, "PiXcSnapshotSync.CSharp",
                $"RowsAffected={result.RowsAffected}, Elapsed={stopwatch.ElapsedMilliseconds}ms",
                result.IsSuccess ? "SUCCESS" : "FAILED");

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "PI 级 XC 快照同步异常: BatchNo={BatchNo}", batchNo);

            await LogETLAsync(batchNo, "PiXcSnapshotSync.CSharp",
                $"PI 级 XC 快照同步异常: {ex.Message}", "FAILED");

            throw;
        }
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
