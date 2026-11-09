using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// <see cref="ITaskExecutionStatusSyncService"/> 实现（W4，2号位）。
///
/// 两次 UPDATE，**只前进不回退**，且**只写 `IN_PROGRESS` / `COMPLETED`**：
///   ① MES `IN_PROGRESS` ⇒ `PLANNED → IN_PROGRESS`
///   ② MES `CLOSED`      ⇒ `PLANNED|IN_PROGRESS → COMPLETED`
///
/// 为什么要先聚合再 JOIN：`MESWorkOrderSnapshot` 同一 `MESWorkOrderNo` 可能有多行（按物料拆行），
/// 直接 JOIN 会让同一条 Task 被多次匹配、结果依赖执行顺序。这里按
/// `CLOSED(2) > IN_PROGRESS(1) > 其它(0)` 取该工单的**最强状态**，保证确定性。
/// </summary>
public class TaskExecutionStatusSyncService : ITaskExecutionStatusSyncService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<TaskExecutionStatusSyncService> _logger;

    public TaskExecutionStatusSyncService(
        DatabaseConnectionManager connectionManager,
        ILogger<TaskExecutionStatusSyncService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<TaskStatusSyncResult> SyncByRunAsync(int scheduleRunId, CancellationToken cancellationToken = default)
    {
        if (scheduleRunId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scheduleRunId), scheduleRunId,
                "必须点名 MES 快照所属的 ScheduleRunId（<=0 无法定位快照批次）。");

        _logger.LogInformation("[TaskStatus] MES 报工回写开始：ScheduleRunId={RunId}", scheduleRunId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ① PLANNED → IN_PROGRESS
        var toInProgress = await _connectionManager.ExecuteAsync(
            @"UPDATE t
              SET    t.Status = N'IN_PROGRESS', t.UpdatedAt = GETDATE()
              FROM   [Task] t
              INNER JOIN PlanVersion pv ON pv.Id = t.PlanVersionId
              INNER JOIN (
                  SELECT MESWorkOrderNo,
                         MAX(CASE WorkOrderStatus WHEN N'CLOSED' THEN 2
                                                  WHEN N'IN_PROGRESS' THEN 1
                                                  ELSE 0 END) AS Rank
                  FROM   MESWorkOrderSnapshot
                  WHERE  ScheduleRunId = @RunId
                  GROUP BY MESWorkOrderNo
              ) s ON s.MESWorkOrderNo = t.MESWorkOrderNo
              WHERE  pv.SourceScheduleRunId = @RunId
                AND  s.Rank = 1
                AND  t.Status = N'PLANNED'",
            new { RunId = scheduleRunId },
            db: DatabaseId.APS,
            commandTimeout: 600);

        // ② PLANNED / IN_PROGRESS → COMPLETED
        var toCompleted = await _connectionManager.ExecuteAsync(
            @"UPDATE t
              SET    t.Status = N'COMPLETED', t.UpdatedAt = GETDATE()
              FROM   [Task] t
              INNER JOIN PlanVersion pv ON pv.Id = t.PlanVersionId
              INNER JOIN (
                  SELECT MESWorkOrderNo,
                         MAX(CASE WorkOrderStatus WHEN N'CLOSED' THEN 2
                                                  WHEN N'IN_PROGRESS' THEN 1
                                                  ELSE 0 END) AS Rank
                  FROM   MESWorkOrderSnapshot
                  WHERE  ScheduleRunId = @RunId
                  GROUP BY MESWorkOrderNo
              ) s ON s.MESWorkOrderNo = t.MESWorkOrderNo
              WHERE  pv.SourceScheduleRunId = @RunId
                AND  s.Rank = 2
                AND  t.Status IN (N'PLANNED', N'IN_PROGRESS')",
            new { RunId = scheduleRunId },
            db: DatabaseId.APS,
            commandTimeout: 600);

        sw.Stop();
        _logger.LogInformation(
            "[TaskStatus] MES 报工回写完成（{Elapsed:F1}s）：→IN_PROGRESS={ToInProgress}，→COMPLETED={ToCompleted}，合计={Total}",
            sw.Elapsed.TotalSeconds, toInProgress, toCompleted, toInProgress + toCompleted);

        return new TaskStatusSyncResult
        {
            ScheduleRunId = scheduleRunId,
            ToInProgress = toInProgress,
            ToCompleted = toCompleted
        };
    }
}