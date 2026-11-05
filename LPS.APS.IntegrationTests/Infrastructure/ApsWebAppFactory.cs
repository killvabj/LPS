using Hangfire;
using Hangfire.MemoryStorage;
using LPS.APS.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace LPS.APS.IntegrationTests.Infrastructure;

/// <summary>
/// 进程内启动**真实的** <c>LPS.APS.Web/Program.cs</c>（含完整认证/授权管线），
/// 只用 4 处替身把「会碰真库的边」切干净：
/// <list type="number">
///   <item>Hangfire 存储 → 内存（<c>Program.cs</c> 启动时会同步写定时任务注册）</item>
///   <item>权限种子 → 空实现（<c>Program.cs:371-380</c> 启动时急切写 APS_Auth）</item>
///   <item>权限判定 → 内存集合（授权处理器与策略映射仍走真实代码）</item>
///   <item>两个发布服务 → 严格 Moq（调用即抛，天然实现「业务层零接触」）</item>
/// </list>
/// </summary>
public sealed class ApsWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>集成测试使用的固定用户 Id（与 JWT 的 <c>NameIdentifier</c> 一致）。</summary>
    public const int TestUserId = 1;

    /// <summary>集成测试使用的固定工号（与 JWT 的 <c>Name</c> 一致）。</summary>
    public const string TestUserCode = "E2E-TEST";

    /// <summary>Web 工程根目录（内容根；<c>appsettings*.json</c> 从这里读）。</summary>
    public static readonly string WebProjectRoot = ResolveWebProjectRoot();

    /// <summary>权限判定替身；用例按需调用 <see cref="FakePermissionService.SetGrantedCodes"/> 切换主体。</summary>
    public FakePermissionService PermissionService { get; } = new();

    /// <summary>治理侧发布服务严格替身。</summary>
    public Mock<IGovernanceVersionService> GovernanceVersionService { get; } =
        new(MockBehavior.Strict);

    /// <summary>Setup 侧发布服务严格替身。</summary>
    public Mock<ISetupRuleService> SetupRuleService { get; } =
        new(MockBehavior.Strict);

    /// <summary>JWT 工厂（与 Web 侧同一套签名参数自签）。</summary>
    public TestJwtTokenFactory Tokens { get; } = new(WebProjectRoot);

    public ApsWebAppFactory()
    {
        // 兜底：任何早于 DI 解析的 Hangfire 静态调用都拿到内存存储，绝不落到 SqlServerStorage
        JobStorage.Current = new MemoryStorage();
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 只有 Development 才允许 Jwt:SecretKey 为占位串（Program.cs:193-199 硬校验），
        // 故显式钉死环境，避免被外部 ASPNETCORE_ENVIRONMENT 带偏到 Production 后拒绝启动。
        builder.UseEnvironment("Development");
        builder.UseContentRoot(WebProjectRoot);

        builder.ConfigureTestServices(services =>
        {
            // ① Hangfire：先摘掉 SqlServer 配置，再挂内存存储（后注册者生效）
            services.RemoveAll<IGlobalConfiguration>();
            services.AddHangfire(config => config.UseMemoryStorage());

            // ② 启动种子：不写 APS_Auth
            services.RemoveAll<IPermissionSeedService>();
            services.AddSingleton<IPermissionSeedService, NoopPermissionSeedService>();

            // ③ 权限判定：查内存集合，不查 APS_Auth
            services.RemoveAll<IPermissionService>();
            services.AddSingleton<IPermissionService>(PermissionService);

            // ④ 发布服务：严格替身，被调用即抛异常
            services.RemoveAll<IGovernanceVersionService>();
            services.AddSingleton(GovernanceVersionService.Object);
            services.RemoveAll<ISetupRuleService>();
            services.AddSingleton(SetupRuleService.Object);
        });
    }

    /// <summary>从测试输出目录向上找 <c>LPS.APS.Web\LPS.APS.Web.csproj</c>。</summary>
    private static string ResolveWebProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "LPS.APS.Web");
            if (File.Exists(Path.Combine(candidate, "LPS.APS.Web.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上定位 LPS.APS.Web 工程目录。");
    }
}
