namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// `[Task].Status` 的 MES 报工回写（2号位，W4）。
///
/// 裁定依据：我方 2026-11-05《回两事裁定》——
///   「**归我方。V1 做 MES 报工回写段（`IN_PROGRESS` / `COMPLETED`）；
///     `PLANNED → RELEASED` 段待下发链（`O8` / `TaskDispatch`）跑通后一并做。**」
///
/// 取值范围严格按冻结 07 §4.4：`PLANNED / RELEASED / IN_PROGRESS / COMPLETED / CANCELLED`。
/// 本服务**只会写 `IN_PROGRESS` 与 `COMPLETED`**，且**只前进不回退**：
///   · MES `IN_PROGRESS` ⇒ `PLANNED → IN_PROGRESS`
///   · MES `CLOSED`      ⇒ `PLANNED|IN_PROGRESS → COMPLETED`
///   · 其它 MES 状态（`RELEASED` / `DELETED`）**不动** —— `RELEASED` 段不在 V1 范围。
///
/// 匹配键：`[Task].MESWorkOrderNo` ↔ `MESWorkOrderSnapshot.MESWorkOrderNo`，
///         且两边同属一个 `ScheduleRunId`（`[Task].PlanVersionId → PlanVersion.SourceScheduleRunId`）。
///
/// 不变量：**只推进状态，不改任何数量、时间、资源字段**（裁定 §3.3 口径约束）。
/// </summary>
public interface ITaskExecutionStatusSyncService
{
    /// <summary>
    /// 按 MES 工单快照回写指定 Run 下 `[Task].Status`。
    /// </summary>
    /// <param name="scheduleRunId">MES 快照所属的 Run；0 或负数将拒绝执行。</param>
    Task<TaskStatusSyncResult> SyncByRunAsync(int scheduleRunId, CancellationToken cancellationToken = default);
}

/// <summary>回写结果（用于验收信号：`ToInProgress + ToCompleted` 应 ≥ 1）。</summary>
public sealed class TaskStatusSyncResult
{
    public int ScheduleRunId { get; init; }
    public int ToInProgress { get; init; }
    public int ToCompleted { get; init; }
    public int TotalChanged => ToInProgress + ToCompleted;
}