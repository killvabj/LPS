using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// 策略包绑定内容指纹读取器（Dapper + APS_Production）。
/// 见 <see cref="ISnapshotContentFingerprintReader"/> 的方法注释：库内 <c>HASHBYTES('SHA2_256')</c>
/// 只回传 64 位十六进制串，避免为判断「内容变没变」而回读约 17KB 的快照正文。
///
/// 三表 <c>INNER JOIN</c>：任一子版本缺行即无结果 ⇒ 返回 <c>null</c>，交由装载路径抛具体错误。
/// 实测（2026-10-30，APS_Production）：SPV 1970 → RSV 478 + PSV 2365 指纹
/// <c>B698DAAB…0EDE13</c>；SPV 811 → RSV 478 + PSV 807 指纹 <c>32D333FE…CB32B73</c>
/// （同规则集、不同参数集 ⇒ 指纹不同，指纹确实覆盖了绑定的两个维度）。
/// </summary>
/// <remarks>开发者：2号位</remarks>
public sealed class SnapshotContentFingerprintReader : ISnapshotContentFingerprintReader
{
    /// <summary>
    /// 库内算指纹。两段正文分别转 <c>varbinary(max)</c> 后**相接再整体哈希**——
    /// 不用「先各算再拼」，是为了让「(A,B) 与 (A‖B 的边界)」不存在歧义（长度前缀由定长二进制拼接天然保证）。
    /// <c>ISNULL(... , N'')</c> 让「正文为 NULL」与「正文为空串」不撞指纹；两者都属异常形态，
    /// 真正拦截在装载路径（<c>ContentSnapshotJson 为空/缺失</c>），此处只求不误命中缓存。
    /// </summary>
    private const string FingerprintSql = @"
        SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256',
                 CONVERT(varbinary(max), ISNULL(rsv.[ContentSnapshotJson], N'')) +
                 CONVERT(varbinary(max), ISNULL(psv.[ContentSnapshotJson], N''))), 2) AS [Fingerprint]
        FROM [dbo].[StrategyProfileVersion] spv
        INNER JOIN [dbo].[RuleSetVersion] rsv ON rsv.[Id] = spv.[RuleSetVersionId]
        INNER JOIN [dbo].[ParameterSetVersion] psv ON psv.[Id] = spv.[ParameterSetVersionId]
        WHERE spv.[Id] = @Id";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<SnapshotContentFingerprintReader> _logger;

    public SnapshotContentFingerprintReader(
        DatabaseConnectionManager connectionManager,
        ILogger<SnapshotContentFingerprintReader> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetStrategyProfileContentFingerprintAsync(
        long strategyProfileVersionId,
        CancellationToken ct = default)
    {
        var fingerprint = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            FingerprintSql, new { Id = strategyProfileVersionId }, db: DatabaseId.APS);

        if (string.IsNullOrEmpty(fingerprint))
        {
            // 不抛：SPV 或其子版本缺失由装载路径报错（那里口径完整、带版本号）。此处只让调用方知道「取不到」。
            _logger.LogDebug("策略包版本 {StrategyProfileVersionId} 取内容指纹为空（版本或绑定的子版本不存在），本次跳过缓存", strategyProfileVersionId);
            return null;
        }

        return fingerprint;
    }
}
