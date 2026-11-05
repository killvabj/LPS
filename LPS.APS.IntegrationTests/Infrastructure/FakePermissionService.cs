using LPS.APS.Core.Interfaces;

namespace LPS.APS.IntegrationTests.Infrastructure;

/// <summary>
/// 权限判定替身：把「查 APS_Auth 库」换成「查一个内存集合」。
/// <para>
/// 判定路径不变——<c>PermissionAuthorizationHandler</c> 仍然是
/// <c>int.TryParse(NameIdentifier)</c> ＋ <c>HasPermissionAsync(userId, code)</c>，
/// 只有最后一跳换成了内存集合，因此测的仍是真实的授权处理器与策略映射。
/// </para>
/// <para>授权集可运行时改写，供同一宿主内切换「仅持 setup.publish」「持 rule.publish」等主体。</para>
/// </summary>
public sealed class FakePermissionService : IPermissionService
{
    private readonly object _gate = new();
    private IReadOnlySet<string> _grantedCodes = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>替换当前授权的权限码集合（整组覆盖，非追加）。</summary>
    public void SetGrantedCodes(params string[] codes)
    {
        lock (_gate)
        {
            _grantedCodes = new HashSet<string>(codes, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public Task<bool> HasPermissionAsync(int userId, string permissionCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // 与真实实现同口径：无效用户或空码恒为 false
            if (userId <= 0 || string.IsNullOrWhiteSpace(permissionCode))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_grantedCodes.Contains(permissionCode));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetPermissionCodesAsync(int userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(_grantedCodes.ToArray());
        }
    }
}
