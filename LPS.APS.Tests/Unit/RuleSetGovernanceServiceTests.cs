using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;
using RuleSet = LPS.APS.Core.Entities.APS.RuleSet;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using LPS.APS.Core.Enum;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 规则集主表治理服务单元测试（2026-10-22：补齐 4号位 缺口「主表零写端点」，3号位）
/// 覆盖口径（回执六条拍板）：
///   Q1 Create 默认启用 + 编码唯一（大小写不敏感由仓库 COLLATE 契约承担，本层仅调 Exists）+ 编码创建后不可变更；
///   Q2 两步语义（本服务仅建主表壳，版本由前端另建）；
///   P1-04 审计：Create/Update 写后落 AuditLog；审计前置 EnsureWritableAsync 失败 → 业务零变更（fail-before-write）；
///   编码/名称非法 → InvalidOperationException（Web 层 400）。
/// </summary>
public class RuleSetGovernanceServiceTests
{
    private readonly Mock<IRuleSetRepository> _repository = new();
    private readonly Mock<IRuleSetVersionRepository> _versionRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyVersionRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly RuleSetGovernanceService _service;

    public RuleSetGovernanceServiceTests()
    {
        // 默认唯一性通过 + 审计库可写，只让「被测变量」失败
        _repository
            .Setup(r => r.RuleSetCodeExistsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new RuleSetGovernanceService(_repository.Object, _versionRepo.Object, _strategyVersionRepo.Object, _auditRepo.Object);
    }

    private static RuleSet ValidInput(string code = "RS_A", string name = "测试规则集", bool isActive = true)
        => new()
        {
            RuleSetCode = code,
            RuleSetName = name,
            Description = "说明",
            IsActive = isActive,
        };

    private static RuleSet ExistingRuleSet(long id = 1, string code = "RS_A", bool isActive = true)
        => new()
        {
            Id = id,
            RuleSetCode = code,
            RuleSetName = "既有规则集",
            Description = "既有说明",
            IsActive = isActive,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    // ==================== CreateAsync ====================

    [Fact]
    public async Task Create_成功_默认启用写库并审计()
    {
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()))
            .Callback<RuleSet, CancellationToken>((e, _) => e.Id = 99)
            .ReturnsAsync((RuleSet e, CancellationToken _) => e);

        var result = await _service.CreateRuleSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        result.Id.Should().Be(99);
        result.IsActive.Should().BeTrue();           // Q1 新建默认启用
        result.RuleSetCode.Should().Be("RS_A");
        result.CreatedAt.Should().NotBe(default);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Create" && a.EntityType == "RuleSet" && a.EntityId == "99"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_编码为空_抛异常不落库()
    {
        var act = async () => await _service.CreateRuleSetAsync(ValidInput(code: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RuleSetCode 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_编码重复_抛异常不落库()
    {
        _repository
            .Setup(r => r.RuleSetCodeExistsAsync("RS_A", It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = async () => await _service.CreateRuleSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_名称为空_抛异常不落库()
    {
        var act = async () => await _service.CreateRuleSetAsync(ValidInput(name: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RuleSetName 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_审计前置失效_业务零变更不写主表()
    {
        // P1-04 fail-before-write：审计库不可写 → 整操作失败，不 INSERT 主表、更不落审计
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库不可写"));

        var act = async () => await _service.CreateRuleSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(r => r.CreateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== UpdateAsync ====================

    [Fact]
    public async Task Update_成功_改名称与停用并审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A", isActive: true));

        var result = await _service.UpdateRuleSetAsync(1, ValidInput(code: "RS_A", name: "改名", isActive: false), "u1", 1, CancellationToken.None);

        result.RuleSetName.Should().Be("改名");
        result.IsActive.Should().BeFalse();          // Q4 IsActive=0 停用
        result.RuleSetCode.Should().Be("RS_A");      // 编码冻结不可改
        _repository.Verify(r => r.UpdateAsync(It.Is<RuleSet>(e => e.IsActive == false), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Update" && a.OldValue == "Active" && a.NewValue == "Inactive" && a.EntityId == "1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_主表不存在_抛异常()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RuleSet?)null);

        var act = async () => await _service.UpdateRuleSetAsync(1, ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码变更_拒绝不写库()
    {
        // Q1 创建后不可变更：既有 RS_A → 入参 RS_B（含大小写差异）一律拒绝
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A"));

        var act = async () => await _service.UpdateRuleSetAsync(1, ValidInput(code: "RS_B"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不可变更*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码唯一冲突_抛异常不写库()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A"));
        _repository
            .Setup(r => r.RuleSetCodeExistsAsync("RS_A", 1, It.IsAny<CancellationToken>())) // excludeId=1 ⇒ 排除自身仍冲突
            .ReturnsAsync(true);

        var act = async () => await _service.UpdateRuleSetAsync(1, ValidInput(code: "RS_A"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<RuleSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== DeleteRuleSet（R1 2026-10-25：无 PUBLISHED 才可删）====================

    private static RuleSetVersion DraftVersion(long id = 11, long ruleSetId = 1, string code = "V2")
        => new()
        {
            Id = id,
            RuleSetId = ruleSetId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Draft,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    private static RuleSetVersion PublishedVersion(long id = 10, long ruleSetId = 1, string code = "V1")
        => new()
        {
            Id = id,
            RuleSetId = ruleSetId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    [Fact]
    public async Task DeleteRuleSet_无Published无引用_整族删并前置审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A"));
        _versionRepo
            .Setup(r => r.GetByRuleSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftVersion(11) });
        _strategyVersionRepo
            .Setup(r => r.CountReferencingRuleSetVersionAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _service.DeleteRuleSetAsync(1, "u1", 1, CancellationToken.None);

        _repository.Verify(r => r.DeleteFamilyAsync(1L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Delete" && a.EntityType == "RuleSet" && a.EntityId == "1" && a.NewValue == "Deleted"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRuleSet_存在Published版本_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A"));
        _versionRepo
            .Setup(r => r.GetByRuleSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { PublishedVersion(10), DraftVersion(11) });

        var act = async () => await _service.DeleteRuleSetAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已发布过*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRuleSet_版本被策略包引用_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingRuleSet(id: 1, code: "RS_A"));
        _versionRepo
            .Setup(r => r.GetByRuleSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftVersion(11) });
        _strategyVersionRepo
            .Setup(r => r.CountReferencingRuleSetVersionAsync(new long[] { 11 }, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var act = async () => await _service.DeleteRuleSetAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*被策略包引用*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== DeleteRuleSetVersion（R1：仅 DRAFT 草稿壳可删）====================

    [Fact]
    public async Task DeleteRuleSetVersion_Draft无引用_删单行并前置审计()
    {
        _versionRepo
            .Setup(r => r.GetByIdAsync(11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DraftVersion(11));
        _strategyVersionRepo
            .Setup(r => r.CountReferencingRuleSetVersionAsync(new long[] { 11 }, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _service.DeleteRuleSetVersionAsync(11, "u1", 1, CancellationToken.None);

        _versionRepo.Verify(r => r.DeleteByIdAsync(11L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Delete" && a.EntityType == "RuleSetVersion" && a.EntityId == "11"
            && a.OldValue == "Draft" && a.NewValue == "Deleted"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteRuleSetVersion_非Draft_拒绝不删()
    {
        _versionRepo
            .Setup(r => r.GetByIdAsync(10L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PublishedVersion(10));

        var act = async () => await _service.DeleteRuleSetVersionAsync(10, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*仅草稿版本可删除*");
        _versionRepo.Verify(r => r.DeleteByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRuleSetVersion_被策略包引用_拒绝不删()
    {
        _versionRepo
            .Setup(r => r.GetByIdAsync(11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DraftVersion(11));
        _strategyVersionRepo
            .Setup(r => r.CountReferencingRuleSetVersionAsync(new long[] { 11 }, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var act = async () => await _service.DeleteRuleSetVersionAsync(11, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*被策略包引用*");
        _versionRepo.Verify(r => r.DeleteByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}