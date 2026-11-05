using LPS.APS.Core.Interfaces;

namespace LPS.APS.IntegrationTests.Infrastructure;

/// <summary>
/// 启动种子替身。<c>Program.cs</c> 在 <c>app.Run()</c> 之前会急切调用
/// <c>IPermissionSeedService.EnsureSeededAsync()</c>（写 APS_Auth 真库，幂等）。
/// 集成测试不碰真库，故换成立即返回的空实现。
/// </summary>
public sealed class NoopPermissionSeedService : IPermissionSeedService
{
    /// <inheritdoc />
    public Task EnsureSeededAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>空集合 = 已对齐（集成测试不校验权限基线）。</summary>
    public Task<IReadOnlyCollection<string>> FindMissingPermissionCodesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
}
