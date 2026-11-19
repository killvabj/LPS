namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// O77：MES 下发消费环的 Hangfire 触发口。
/// 两个周期任务共用本入口，但只按各自的 `WorkOrderType` 取组：
///   · ASSEMBLY → 组装 MES；
///   · MACHINING → 加工 MES（接口未就绪前不启用）。
/// 本接口只触发消费，不创建 `TaskDispatch`、不调 Solver。
/// </summary>
public interface IMesDispatchDrainJob
{
    /// <param name="workOrderType">只允许 ASSEMBLY / MACHINING；各自独立 Job，避免混通道。</param>
    Task RunAsync(string workOrderType, CancellationToken cancellationToken = default);
}
