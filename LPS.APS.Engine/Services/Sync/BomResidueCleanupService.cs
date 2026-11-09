using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// <see cref="IBomResidueCleanupService"/> 实现（2号位，每日 01:00）。
///
/// 只做一件事：在 ODS 上 `EXEC dbo.sp_CleanupBOMRequestResidue`（无参数），
/// 然后把本次 `CLEANUP_yyyyMMdd` 日志行读回来落应用日志。
///
/// ⚠ 幂等与事务边界全部由 SP 负责（批级独立事务、谓词状态驱动），本服务**不加事务、不重试**：
///   失败由 SP 自己写一行 `CLEANUP 段N 失败` 日志并中止本次，次日人工先看日志再决定是否重跑。
/// </summary>
public class BomResidueCleanupService : IBomResidueCleanupService
{
    private const string SpName = "dbo.sp_CleanupBOMRequestResidue";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<BomResidueCleanupService> _logger;

    public BomResidueCleanupService(
        DatabaseConnectionManager connectionManager,
        ILogger<BomResidueCleanupService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string?> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var logBatchNo = "CLEANUP_" + DateTime.Now.ToString("yyyyMMdd");
        _logger.LogInformation("BOM 残留清理开始：SP={Sp}，日志批号={BatchNo}", SpName, logBatchNo);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // SP 无参数；命令超时放宽到 30 分钟（首次清欠量级可达千万行，后续每日应接近零）
        await _connectionManager.ExecuteAsync(
            SpName,
            parameters: null,
            commandType: System.Data.CommandType.StoredProcedure,
            db: DatabaseId.ODS,
            commandTimeout: 1800);

        stopwatch.Stop();

        // 读回本次日志行（SP 自己写；只取一行，用于值守可见）
        var message = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            @"SELECT TOP 1 Message
              FROM MES_API_BOM_Request_Log
              WHERE BatchNo = @BatchNo
              ORDER BY Id DESC",
            new { BatchNo = logBatchNo },
            db: DatabaseId.ODS,
            commandTimeout: 60);

        if (message is null)
        {
            _logger.LogWarning(
                "BOM 残留清理已执行（{Elapsed:F1}s），但未读到日志行 {BatchNo} —— 清理可能中途失败或 T-SQL 侧未落日志，请人工核对",
                stopwatch.Elapsed.TotalSeconds, logBatchNo);
        }
        else
        {
            _logger.LogInformation(
                "BOM 残留清理完成（{Elapsed:F1}s）：{Message}", stopwatch.Elapsed.TotalSeconds, message);
        }

        return message;
    }
}