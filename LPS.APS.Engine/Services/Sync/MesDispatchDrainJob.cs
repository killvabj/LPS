using LPS.APS.Engine.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// O77：MES 下发消费环的 Hangfire 触发实现。
///
/// <para>只做三件事：解析本次 Run → 调 <see cref="IMesDispatchDrainService.DrainAsync"/> → 日志。
/// 不创建 TaskDispatch 行、不生成工单号、不落 Task；那些都在 Pegging/Adapter 的既有链路。</para>
///
/// <para>RunId：优先 `MesDispatch:Drain:ScheduleRunId`（人工指定）；否则取最新 `ScheduleRun.Status='RUNNING'`。
/// 找不到 Run 就不发，避免把旧 Run 的 PENDING 当成当前任务。</para>
///
/// <para>手动期：Web 层注册为 `NeverFireCron`，只能从 Hangfire Dashboard “Trigger Now”触发；
/// 上线后再按组装/加工分别改 cron。</para>
/// </summary>
public sealed class MesDispatchDrainJob : IMesDispatchDrainJob
{
    private readonly IMesDispatchDrainService _drainService;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MesDispatchDrainJob> _logger;

    public MesDispatchDrainJob(
        IMesDispatchDrainService drainService,
        DatabaseConnectionManager connectionManager,
        IConfiguration configuration,
        ILogger<MesDispatchDrainJob> logger)
    {
        _drainService = drainService ?? throw new ArgumentNullException(nameof(drainService));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(string workOrderType, CancellationToken cancellationToken = default)
    {
        var type = (workOrderType ?? string.Empty).Trim().ToUpperInvariant();
        if (type is not ("ASSEMBLY" or "MACHINING"))
            throw new ArgumentException("workOrderType 只能是 ASSEMBLY 或 MACHINING", nameof(workOrderType));

        var configured = _configuration.GetValue<int?>("MesDispatch:Drain:ScheduleRunId");
        var runId = configured is > 0
            ? configured.Value
            : await _connectionManager.QueryFirstOrDefaultAsync<int?>(
                "SELECT TOP 1 Id FROM ScheduleRun WHERE Status = N'RUNNING' ORDER BY Id DESC",
                db: DatabaseId.APS, commandTimeout: 60);

        if (runId is null or <= 0)
        {
            _logger.LogWarning("[MesDispatchDrainJob] 无可用 RUNNING ScheduleRun（type={Type}）⇒ 本轮不触发", type);
            return;
        }

        var maxGroups = _configuration.GetValue<int?>("MesDispatch:Drain:MaxGroups") ?? 50;
        var result = await _drainService.DrainAsync(runId.Value, maxGroups, type, cancellationToken);

        _logger.LogInformation(
            "[MesDispatchDrainJob] type={Type} Run={Run}：组合={Seen}（Accepted {A}/Rejected {R}/Unknown {U}/缺字段 {Skip}）；" +
            "行→CREATED {RC}/FAILED {RF}/PENDING {RP}；适配器{Adapter}",
            type, runId.Value, result.GroupsSeen, result.GroupsAccepted, result.GroupsRejected,
            result.GroupsUnknown, result.GroupsSkippedMissingField,
            result.RowsCreated, result.RowsFailed, result.RowsBackToPending,
            result.AdapterMissing ? "未注册(NOT_CONFIGURED)" : "已注册");
    }
}
