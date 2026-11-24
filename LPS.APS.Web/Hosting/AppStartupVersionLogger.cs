using System.Reflection;
using LPS.APS.Engine.Data;

namespace LPS.APS.Web.Hosting;

/// <summary>
/// **启动版本留痕（A28，2026-11-23 2号位自定）**：进程启动时把「程序集版本＋启动时刻＋主机」
/// 写一行 `APS_ETL_Log`，使后续「本 Run 当时跑的是哪一版代码」可自证。
///
/// <para><b>为什么需要</b>：**Run 的创建时间不等于执行时间**（探针/手工入口可在任意时刻起跑），
/// 因此「Run 建于何时」推不出「当时跑的是哪一版」；而程序集 `AssemblyInformationalVersion`
/// 里嵌的是 **git 提交哈希**，与 SVN 修订号无逐提交映射，部署/启动也无版本日志。
/// 结果是任何历史 Run 都只能标 `PROCESS_REV_UNVERIFIED`（既不能证明也不能排除某版本）。</para>
///
/// <para><b>写什么</b>：`BatchNo='APP_START'`、`Step='AppStartup'`、`Status='SUCCESS'`，
/// `Message` 内含「InformationalVersion / AssemblyVersion / 主机名 / 进程启动时刻(UTC+本地)」。
/// 只用既有表，不新增列、不新增枚举。</para>
///
/// <para><b>失败策略</b>：DB 不可用等异常**只记日志、不影响宿主启动**（调用方已 try/catch）。</para>
/// </summary>
public sealed class AppStartupVersionLogger
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<AppStartupVersionLogger> _logger;

    public AppStartupVersionLogger(
        DatabaseConnectionManager connectionManager,
        ILogger<AppStartupVersionLogger> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(CancellationToken cancellationToken = default)
    {
        var asm = typeof(AppStartupVersionLogger).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? asm.GetName().Version?.ToString()
                            ?? "unknown";
        var assemblyVersion = asm.GetName().Version?.ToString() ?? "unknown";
        var entryAssembly = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var host = Environment.MachineName;
        var startedLocal = DateTime.Now;
        var startedUtc = DateTime.UtcNow;

        var message =
            $"InformationalVersion={informational}; AssemblyVersion={assemblyVersion}; " +
            $"EntryAssemblyVersion={entryAssembly}; Host={host}; " +
            $"StartedLocal={startedLocal:yyyy-MM-dd HH:mm:ss.fff}; StartedUtc={startedUtc:yyyy-MM-dd HH:mm:ss.fff}";

        await _connectionManager.ExecuteAsync(
            @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
              VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())",
            new { BatchNo = "APP_START", Step = "AppStartup", Message = message, Status = "SUCCESS" },
            db: DatabaseId.APS);

        _logger.LogInformation("[A28] 启动版本留痕已写入：{Message}", message);
    }
}
