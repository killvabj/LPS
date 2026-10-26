using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// D-3 默认版本承载（2026-10-17）：规则集/参数集版本的 SetDefault 原子切换与读侧 Resolve 默认解析。
/// 镜像 StrategyProfileVersionGovernanceTests 既有 SPV 默认语义测试（C-7 SetDefault 显式操作）。
/// 覆盖：非 PUBLISHED 拒绝 / 幂等早退 / 原子切换（清同族其它 + 置目标）/ 读侧 0/1/多 三态 / 生效窗过滤 / 停用清默认联动。
/// </summary>
public class RuleParameterSetDefaultVersionTests
{
    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _paramRepo = new();
    private readonly Mock<IStrategyProfileRepository> _strategyProfileRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly GovernanceVersionService _service;

    public RuleParameterSetDefaultVersionTests()
    {
        _service = new GovernanceVersionService(
            _ruleSetRepo.Object,
            _paramRepo.Object,
            _strategyProfileRepo.Object,
            _strategyProfileVersionRepo.Object,
            _auditRepo.Object);
    }

    // ==================== 规则集 SetDefault ====================

    [Fact]
    public async Task SetDefault_规则集版本不存在_抛异常()
    {
        _ruleSetRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((RuleSetVersion?)null);

        var act = () => _service.SetDefaultRuleSetVersionAsync(1, "u", actorUserId: 7, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*规则集版本不存在*");
    }

    [Fact]
    public async Task SetDefault_规则集非PUBLISHED_拒绝()
    {
        _ruleSetRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Draft });

        var act = () => _service.SetDefaultRuleSetVersionAsync(1, "u", actorUserId: 7, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*仅 PUBLISHED 版本可设为默认*");
    }

    [Fact]
    public async Task SetDefault_规则集已是默认_幂等早退不审计()
    {
        var version = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(version);

        await _service.SetDefaultRuleSetVersionAsync(1, "u", actorUserId: 7, CancellationToken.None);

        _ruleSetRepo.Verify(r => r.SetDefaultAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetDefault_规则集Published_原子切换并审计()
    {
        var version = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = false };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(a => a.EnsureWritableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await _service.SetDefaultRuleSetVersionAsync(1, "operatedBy", actorUserId: 7, CancellationToken.None);

        _ruleSetRepo.Verify(r => r.SetDefaultAsync(10L, 1L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditLog>(log => log.ActionCode == "SetDefault"
                && log.EntityType == "RuleSetVersion"
                && log.EntityId == "1"
                && log.UserId == 7
                && log.UserCode == "operatedBy"
                && log.OldValue == "false"
                && log.NewValue == "true"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ==================== 参数集 SetDefault ====================

    [Fact]
    public async Task SetDefault_参数集非PUBLISHED_拒绝()
    {
        _paramRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ParameterSetVersion { Id = 1, ParameterSetId = 20, VersionCode = "P1", Status = GovernanceVersionStatus.Disabled });

        var act = () => _service.SetDefaultParameterSetVersionAsync(1, "u", actorUserId: 7, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*仅 PUBLISHED 版本可设为默认*");
    }

    [Fact]
    public async Task SetDefault_参数集Published_原子切换并审计()
    {
        var version = new ParameterSetVersion { Id = 1, ParameterSetId = 20, VersionCode = "P1", Status = GovernanceVersionStatus.Published, IsDefault = false };
        _paramRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(a => a.EnsureWritableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await _service.SetDefaultParameterSetVersionAsync(1, "operatedBy", actorUserId: 8, CancellationToken.None);

        _paramRepo.Verify(r => r.SetDefaultAsync(20L, 1L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditLog>(log => log.ActionCode == "SetDefault"
                && log.EntityType == "ParameterSetVersion"
                && log.EntityId == "1"
                && log.UserId == 8
                && log.NewValue == "true"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ==================== 读侧 Resolve：规则集 ====================

    [Fact]
    public async Task Resolve_规则集单候选_返回该版本()
    {
        var candidate = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _ruleSetRepo.Setup(r => r.GetDefaultCandidatesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion> { candidate });

        var result = await _service.ResolveDefaultRuleSetVersionAsync(10, ct: CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_规则集无候选_返回Null()
    {
        _ruleSetRepo.Setup(r => r.GetDefaultCandidatesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion>());

        var result = await _service.ResolveDefaultRuleSetVersionAsync(10, ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_规则集多候选_抛歧义异常()
    {
        // 跨号位冻结语义：歧义报配置错误，不随机取一个（红线 #4）
        var c1 = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        var c2 = new RuleSetVersion { Id = 2, RuleSetId = 10, VersionCode = "R2", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _ruleSetRepo.Setup(r => r.GetDefaultCandidatesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion> { c1, c2 });

        var act = () => _service.ResolveDefaultRuleSetVersionAsync(10, ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*存在歧义*");
    }

    [Fact]
    public async Task Resolve_规则集生效窗过滤_窗口外不入选()
    {
        // asOf=2026-06-01：c1 在窗口内、c2 已过期 → 恰 1 个
        var c1 = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = true,
            EffectiveFrom = new DateTime(2026, 1, 1), EffectiveTo = new DateTime(2026, 12, 31) };
        var c2 = new RuleSetVersion { Id = 2, RuleSetId = 10, VersionCode = "R2", Status = GovernanceVersionStatus.Published, IsDefault = true,
            EffectiveFrom = new DateTime(2025, 1, 1), EffectiveTo = new DateTime(2025, 6, 30) };
        _ruleSetRepo.Setup(r => r.GetDefaultCandidatesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion> { c1, c2 });

        var result = await _service.ResolveDefaultRuleSetVersionAsync(10, asOf: new DateTime(2026, 6, 1), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }

    // ==================== 读侧 Resolve：参数集 ====================

    [Fact]
    public async Task Resolve_参数集多候选_抛歧义异常()
    {
        var c1 = new ParameterSetVersion { Id = 1, ParameterSetId = 20, VersionCode = "P1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        var c2 = new ParameterSetVersion { Id = 2, ParameterSetId = 20, VersionCode = "P2", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _paramRepo.Setup(r => r.GetDefaultCandidatesAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ParameterSetVersion> { c1, c2 });

        var act = () => _service.ResolveDefaultParameterSetVersionAsync(20, ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*存在歧义*");
    }

    [Fact]
    public async Task Resolve_参数集生效窗过滤_返回窗口内候选()
    {
        var c1 = new ParameterSetVersion { Id = 1, ParameterSetId = 20, VersionCode = "P1", Status = GovernanceVersionStatus.Published, IsDefault = true,
            EffectiveFrom = new DateTime(2026, 1, 1), EffectiveTo = new DateTime(2026, 12, 31) };
        var c2 = new ParameterSetVersion { Id = 2, ParameterSetId = 20, VersionCode = "P2", Status = GovernanceVersionStatus.Published, IsDefault = true,
            EffectiveFrom = new DateTime(2025, 1, 1), EffectiveTo = new DateTime(2025, 6, 30) };
        _paramRepo.Setup(r => r.GetDefaultCandidatesAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ParameterSetVersion> { c1, c2 });

        var result = await _service.ResolveDefaultParameterSetVersionAsync(20, asOf: new DateTime(2026, 6, 1), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }

    // ==================== 停用联动清默认 ====================

    [Fact]
    public async Task Disable_规则集IsDefault停用_触发清同族默认()
    {
        var version = new RuleSetVersion { Id = 1, RuleSetId = 10, VersionCode = "R1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _ruleSetRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(a => a.EnsureWritableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // D-2 引用保护：无合法引用（返回空列表，避免 NRE）
        _strategyProfileVersionRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(It.IsAny<long?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StrategyProfileVersion>());

        await _service.DisableRuleSetVersionAsync(1, "u", actorUserId: 7, ct: CancellationToken.None);

        _ruleSetRepo.Verify(r => r.SetDefaultAsync(10L, 1L, It.IsAny<CancellationToken>()), Times.Once);
        _ruleSetRepo.Verify(r => r.UpdateAsync(
            It.Is<RuleSetVersion>(v => v.Status == GovernanceVersionStatus.Disabled),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disable_参数集IsDefault停用_触发清同族默认()
    {
        var version = new ParameterSetVersion { Id = 1, ParameterSetId = 20, VersionCode = "P1", Status = GovernanceVersionStatus.Published, IsDefault = true };
        _paramRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(a => a.EnsureWritableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _strategyProfileVersionRepo.Setup(r => r.GetReferencingPublishedVersionsAsync(null, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StrategyProfileVersion>());

        await _service.DisableParameterSetVersionAsync(1, "u", actorUserId: 7, ct: CancellationToken.None);

        _paramRepo.Verify(r => r.SetDefaultAsync(20L, 1L, It.IsAny<CancellationToken>()), Times.Once);
        _paramRepo.Verify(r => r.UpdateAsync(
            It.Is<ParameterSetVersion>(v => v.Status == GovernanceVersionStatus.Disabled),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}