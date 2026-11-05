using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using LPS.APS.Core.Authorization;
using LPS.APS.IntegrationTests.Infrastructure;
using Moq;
using Xunit;

namespace LPS.APS.IntegrationTests;

/// <summary>
/// 规则集统一发布门禁 · 端到端 403 负例（3号位 2026-10-28 协助请求 / 0号位 §四 用例 B）。
/// <para>
/// 两个发布端点各验三种主体：
/// <list type="bullet">
///   <item><b>匿名</b> → 401（对照组：证明 401≠403，不能拿 401 当越权被堵）</item>
///   <item><b>仅持 <c>aps.setup.publish</c></b> → 403 ＋ 发布服务零调用（主判据）</item>
///   <item><b>持 <c>aps.rule.publish</c></b> → 200 ＋ 发布服务被调用 1 次（正例对照）</item>
/// </list>
/// 没有第三条，第二条的 403 可能来自任何原因（路由写错、令牌无效、策略名拼错同样会 403）。
/// </para>
/// </summary>
public class PublishEndpointAuthorizationTests : IClassFixture<ApsWebAppFactory>
{
    /// <summary>治理侧规则集整版发布（<c>GovernanceController.cs:438</c>）。</summary>
    private const string GovernancePublishUrl = "/api/Governance/rule-set/version/1/publish";

    /// <summary>Setup 侧规则集整版发布（<c>SetupRuleController.cs:288</c>）。</summary>
    private const string SetupPublishUrl = "/api/governance/rule-set-versions/1/publish";

    private readonly ApsWebAppFactory _factory;

    public PublishEndpointAuthorizationTests(ApsWebAppFactory factory)
    {
        _factory = factory;

        // 每个用例从干净状态开始：清调用记录、清授权集
        _factory.GovernanceVersionService.Invocations.Clear();
        _factory.SetupRuleService.Invocations.Clear();
        _factory.PermissionService.SetGrantedCodes();
    }

    // ───────────────────────── 主判据：403 ＋ 零调用 ─────────────────────────

    [Fact(DisplayName = "治理侧发布：仅持 aps.setup.publish → 403 且发布服务零调用")]
    public async Task GovernancePublish_WithSetupPublishOnly_Returns403AndDoesNotReachService()
    {
        _factory.PermissionService.SetGrantedCodes(PermissionCodes.SetupPublish);

        var response = await PostAsync(GovernancePublishUrl, _factory.Tokens.CreateAuthenticatedToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.GovernanceVersionService.Verify(
            m => m.PublishRuleSetVersionAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never,
            "越权必须在鉴权层被拦，不得走进发布服务");
    }

    [Fact(DisplayName = "Setup 侧发布：仅持 aps.setup.publish → 403 且发布服务零调用")]
    public async Task SetupPublish_WithSetupPublishOnly_Returns403AndDoesNotReachService()
    {
        _factory.PermissionService.SetGrantedCodes(PermissionCodes.SetupPublish);

        var response = await PostAsync(SetupPublishUrl, _factory.Tokens.CreateAuthenticatedToken());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.SetupRuleService.Verify(
            m => m.PublishAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "越权必须在鉴权层被拦，不得走进发布服务");
    }

    // ───────────────────────── 对照组 1：401 ≠ 403 ─────────────────────────

    [Fact(DisplayName = "治理侧发布：匿名 → 401（不是 403）")]
    public async Task GovernancePublish_Anonymous_Returns401()
    {
        var response = await PostAsync(GovernancePublishUrl, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Setup 侧发布：匿名 → 401（不是 403）")]
    public async Task SetupPublish_Anonymous_Returns401()
    {
        var response = await PostAsync(SetupPublishUrl, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────────────────────── 对照组 2：有权限必须放行 ─────────────────────────

    [Fact(DisplayName = "治理侧发布：持 aps.rule.publish → 200 且发布服务被调用 1 次")]
    public async Task GovernancePublish_WithRulePublish_SucceedsAndReachesService()
    {
        _factory.PermissionService.SetGrantedCodes(PermissionCodes.RulePublish);
        _factory.GovernanceVersionService
            .Setup(m => m.PublishRuleSetVersionAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var response = await PostAsync(GovernancePublishUrl, _factory.Tokens.CreateAuthenticatedToken());

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "持 aps.rule.publish 的主体必须能通过门禁——否则 403 只是「门禁坏了」而非「挡住了越权」");
        _factory.GovernanceVersionService.Verify(
            m => m.PublishRuleSetVersionAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact(DisplayName = "Setup 侧发布：持 aps.rule.publish → 200 且发布服务被调用 1 次")]
    public async Task SetupPublish_WithRulePublish_SucceedsAndReachesService()
    {
        _factory.PermissionService.SetGrantedCodes(PermissionCodes.RulePublish);
        _factory.SetupRuleService
            .Setup(m => m.PublishAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var response = await PostAsync(SetupPublishUrl, _factory.Tokens.CreateAuthenticatedToken());

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "持 aps.rule.publish 的主体必须能通过门禁——否则 403 只是「门禁坏了」而非「挡住了越权」");
        _factory.SetupRuleService.Verify(
            m => m.PublishAsync(
                It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ───────────────────────── helpers ─────────────────────────

    private async Task<HttpResponseMessage> PostAsync(string url, string? token)
    {
        var client = _factory.CreateClient();

        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }
}
