using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// S12 策略包版本<b>富对比</b>（06 §6.4.1「版本富对比（H1）」）验收。
/// </summary>
/// <remarks>
/// 覆盖：双方版本概要回填 / 绑定差异标记 / 变更汇总计数 / 同一版本自比全零 /
///      绑定编码查不到不判失败（fail-open 仅影响展示）/ 版本不存在抛 KeyNotFoundException /
///      <b>与字段级 diff 判等规则同源</b>（防两套判等规则漂移）。
/// 边界：纯内存，不连库不写库；不验证 HTTP 状态码与鉴权（属端点层，另验）。
/// </remarks>
public class StrategyProfileVersionRichCompareTests
{
    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _parameterSetRepo = new();
    private readonly Mock<IStrategyProfileRepository> _strategyProfileRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly GovernanceVersionService _service;

    public StrategyProfileVersionRichCompareTests()
    {
        _service = new GovernanceVersionService(
            _ruleSetRepo.Object,
            _parameterSetRepo.Object,
            _strategyProfileRepo.Object,
            _strategyProfileVersionRepo.Object,
            _auditRepo.Object);
    }

    private static StrategyProfileVersion Version(
        long id,
        string versionCode,
        long ruleSetVersionId,
        long parameterSetVersionId,
        string status = GovernanceVersionStatus.Published,
        bool isDefault = false,
        DateTime? effectiveFrom = null,
        DateTime? effectiveTo = null,
        DateTime? publishedAt = null,
        string? publishedBy = "lisi") => new()
        {
            Id = id,
            StrategyProfileId = 10,
            VersionCode = versionCode,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = status,
            IsDefault = isDefault,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            PublishedAt = publishedAt,
            PublishedBy = publishedBy,
        };

    private void SetupVersions(StrategyProfileVersion baseVersion, StrategyProfileVersion compareVersion)
    {
        _strategyProfileVersionRepo
            .Setup(r => r.GetByIdAsync(baseVersion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(baseVersion);
        _strategyProfileVersionRepo
            .Setup(r => r.GetByIdAsync(compareVersion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(compareVersion);
    }

    private void SetupRuleSetCode(long versionId, string? versionCode)
        => _ruleSetRepo
            .Setup(r => r.GetByIdAsync(versionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(versionCode is null
                ? null
                : new RuleSetVersion { Id = versionId, RuleSetId = 100, VersionCode = versionCode, Status = GovernanceVersionStatus.Published });

    private void SetupParameterSetCode(long versionId, string? versionCode)
        => _parameterSetRepo
            .Setup(r => r.GetByIdAsync(versionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(versionCode is null
                ? null
                : new ParameterSetVersion { Id = versionId, ParameterSetId = 200, VersionCode = versionCode, Status = GovernanceVersionStatus.Published });

    [Fact]
    public async Task 绑定与状态均不同_绑定差异与汇总计数如实反映()
    {
        // Arrange：b=基准（RS101/PS201、DRAFT），a=对比（RS102/PS202、PUBLISHED）
        var baseVersion = Version(1, "V1", 101, 201, GovernanceVersionStatus.Draft, isDefault: false);
        var compareVersion = Version(2, "V2", 102, 202, GovernanceVersionStatus.Published, isDefault: true);
        SetupVersions(baseVersion, compareVersion);
        SetupRuleSetCode(101, "RS-V1");
        SetupRuleSetCode(102, "RS-V2");
        SetupParameterSetCode(201, "PS-V1");
        SetupParameterSetCode(202, "PS-V2");

        // Act
        var result = await _service.CompareStrategyProfileVersionRichAsync(baseVersionId: 1, compareVersionId: 2);

        // Assert —— 方向：Base=b、Compare=a
        result.Base.VersionId.Should().Be(1);
        result.Compare.VersionId.Should().Be(2);
        result.Base.RuleSetVersionCode.Should().Be("RS-V1");
        result.Base.ParameterSetVersionCode.Should().Be("PS-V1");
        result.Compare.RuleSetVersionCode.Should().Be("RS-V2");
        result.Compare.ParameterSetVersionCode.Should().Be("PS-V2");

        // 绑定差异
        result.Binding.RuleSetChanged.Should().BeTrue();
        result.Binding.ParameterSetChanged.Should().BeTrue();
        result.Binding.AnyChanged.Should().BeTrue();
        result.Binding.BaseRuleSetVersionId.Should().Be(101);
        result.Binding.CompareRuleSetVersionId.Should().Be(102);

        // 汇总标记
        result.Summary.StatusChanged.Should().BeTrue();
        result.Summary.DefaultFlagChanged.Should().BeTrue();
        result.Summary.TotalFieldCount.Should().Be(result.FieldDiffs.Count);
        result.Summary.ChangedFieldCount.Should().Be(result.FieldDiffs.Count(d => d.IsChanged));
        result.Summary.ChangedFieldNames.Should().Contain("RuleSetVersionId")
            .And.Contain("ParameterSetVersionId")
            .And.Contain("Status")
            .And.Contain("IsDefault");
    }

    [Fact]
    public async Task 同一版本自比_变更计数为零且绑定未变()
    {
        // Arrange
        var version = Version(1, "V1", 101, 201, publishedAt: new DateTime(2026, 10, 29, 8, 0, 0));
        SetupVersions(version, version);
        SetupRuleSetCode(101, "RS-V1");
        SetupParameterSetCode(201, "PS-V1");

        // Act
        var result = await _service.CompareStrategyProfileVersionRichAsync(1, 1);

        // Assert
        result.Summary.ChangedFieldCount.Should().Be(0);
        result.Summary.ChangedFieldNames.Should().BeEmpty();
        result.Binding.AnyChanged.Should().BeFalse();
        result.FieldDiffs.Should().OnlyContain(d => !d.IsChanged);
    }

    [Fact]
    public async Task 绑定版本编码查不到_仅编码为null_不判对比失败()
    {
        // Arrange：绑定 Id 指向不存在的规则集版本（版本本身仍在，绑定 Id 才是权威值）
        var baseVersion = Version(1, "V1", 101, 201);
        var compareVersion = Version(2, "V2", 999, 201);
        SetupVersions(baseVersion, compareVersion);
        SetupRuleSetCode(101, "RS-V1");
        SetupRuleSetCode(999, null);
        SetupParameterSetCode(201, "PS-V1");

        // Act
        var result = await _service.CompareStrategyProfileVersionRichAsync(1, 2);

        // Assert
        result.Compare.RuleSetVersionId.Should().Be(999);
        result.Compare.RuleSetVersionCode.Should().BeNull();
        result.Binding.RuleSetChanged.Should().BeTrue();
        result.Compare.ParameterSetVersionCode.Should().Be("PS-V1");
    }

    [Fact]
    public async Task 基准方版本不存在_抛KeyNotFound()
    {
        // Arrange：仅对比方存在
        var compareVersion = Version(2, "V2", 101, 201);
        _strategyProfileVersionRepo
            .Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(compareVersion);

        // Act
        var act = () => _service.CompareStrategyProfileVersionRichAsync(404, 2);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*基准策略包版本不存在*");
    }

    [Fact]
    public async Task 对比方版本不存在_抛KeyNotFound()
    {
        // Arrange：仅基准方存在
        var baseVersion = Version(1, "V1", 101, 201);
        _strategyProfileVersionRepo
            .Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(baseVersion);

        // Act
        var act = () => _service.CompareStrategyProfileVersionRichAsync(1, 404);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*对比策略包版本不存在*");
    }

    [Fact]
    public async Task 与字段级diff同源_同一对版本两端口径一致()
    {
        // Arrange：任一字段都可能变（状态/默认/生效窗/发布信息/绑定）
        var baseVersion = Version(1, "V1", 101, 201, GovernanceVersionStatus.Draft, false,
            new DateTime(2026, 10, 1, 0, 0, 0), new DateTime(2026, 12, 31, 0, 0, 0), null, null);
        var compareVersion = Version(2, "V2", 102, 202, GovernanceVersionStatus.Published, true,
            new DateTime(2026, 11, 1, 0, 0, 0), new DateTime(2027, 1, 31, 0, 0, 0),
            new DateTime(2026, 10, 29, 8, 0, 0), "lisi");
        SetupVersions(baseVersion, compareVersion);
        SetupRuleSetCode(101, "RS-V1");
        SetupRuleSetCode(102, "RS-V2");
        SetupParameterSetCode(201, "PS-V1");
        SetupParameterSetCode(202, "PS-V2");

        // Act
        var rich = await _service.CompareStrategyProfileVersionRichAsync(1, 2);
        var diff = await _service.CompareStrategyProfileVersionsAsync(1, 2);

        // Assert —— 字段名/显示名/双方值/IsChanged 逐项相同（单一构造点，不许两套判等规则）
        rich.FieldDiffs.Should().HaveCount(diff.FieldDiffs.Count);
        rich.FieldDiffs.Select(d => (d.FieldName, d.FieldDisplayName, d.SourceValue, d.TargetValue, d.IsChanged))
            .Should().Equal(diff.FieldDiffs.Select(d => (d.FieldName, d.FieldDisplayName, d.SourceValue, d.TargetValue, d.IsChanged)));

        rich.Summary.EffectiveWindowChanged.Should().BeTrue();
        rich.Summary.PublishInfoChanged.Should().BeTrue();
    }
}
