using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;
using ParameterSet = LPS.APS.Core.Entities.APS.ParameterSet;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using LPS.APS.Core.Enum;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 参数集主表治理服务单元测试（2026-10-25：补齐 4号位 缺口「参数集缺主表写端点 POST/PUT」，3号位）
/// 覆盖口径（对照 4号位 §四）：
///   Q1 Create 默认启用 + 编码唯一 + 编码创建后不可变更；Q2 两步语义（本服务仅建主表壳）；
///   P1-04 审计：Create/Update 写后落 AuditLog；审计前置 EnsureWritableAsync 失败 → 业务零变更（fail-before-write）；
///   编码/名称非法 → InvalidOperationException（Web 层 400）。
/// 注：Q5 不给 DELETE——本服务无 Delete 方法（与 rule-set R1 例外区分）。
/// </summary>
public class ParameterSetGovernanceServiceTests
{
    private readonly Mock<IParameterSetRepository> _repository = new();
    private readonly Mock<IParameterSetVersionRepository> _versionRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly ParameterSetGovernanceService _service;

    public ParameterSetGovernanceServiceTests()
    {
        // 默认唯一性通过 + 审计库可写，只让「被测变量」失败
        _repository
            .Setup(r => r.ParameterSetCodeExistsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new ParameterSetGovernanceService(_repository.Object, _versionRepo.Object, _strategyProfileVersionRepo.Object, _auditRepo.Object);
    }

    private static ParameterSet ValidInput(string code = "PS_A", string name = "测试参数集", bool isActive = true)
        => new()
        {
            ParameterSetCode = code,
            ParameterSetName = name,
            Description = "说明",
            IsActive = isActive,
        };

    private static ParameterSet ExistingParameterSet(long id = 1, string code = "PS_A", bool isActive = true)
        => new()
        {
            Id = id,
            ParameterSetCode = code,
            ParameterSetName = "既有参数集",
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
            .Setup(r => r.CreateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()))
            .Callback<ParameterSet, CancellationToken>((e, _) => e.Id = 99)
            .ReturnsAsync((ParameterSet e, CancellationToken _) => e);

        var result = await _service.CreateParameterSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        result.Id.Should().Be(99);
        result.IsActive.Should().BeTrue();           // Q1 新建默认启用
        result.ParameterSetCode.Should().Be("PS_A");
        result.CreatedAt.Should().NotBe(default);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Create" && a.EntityType == "ParameterSet" && a.EntityId == "99"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_编码为空_抛异常不落库()
    {
        var act = async () => await _service.CreateParameterSetAsync(ValidInput(code: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ParameterSetCode 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_编码重复_抛异常不落库()
    {
        _repository
            .Setup(r => r.ParameterSetCodeExistsAsync("PS_A", It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = async () => await _service.CreateParameterSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_名称为空_抛异常不落库()
    {
        var act = async () => await _service.CreateParameterSetAsync(ValidInput(name: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ParameterSetName 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_审计前置失效_业务零变更不写主表()
    {
        // P1-04 fail-before-write：审计库不可写 → 整操作失败，不 INSERT 主表、更不落审计
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库不可写"));

        var act = async () => await _service.CreateParameterSetAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(r => r.CreateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== UpdateAsync ====================

    [Fact]
    public async Task Update_成功_改名称与停用并审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A", isActive: true));

        var result = await _service.UpdateParameterSetAsync(1, ValidInput(code: "PS_A", name: "改名", isActive: false), "u1", 1, CancellationToken.None);

        result.ParameterSetName.Should().Be("改名");
        result.IsActive.Should().BeFalse();          // Q4 IsActive=0 停用
        result.ParameterSetCode.Should().Be("PS_A"); // 编码冻结不可改
        _repository.Verify(r => r.UpdateAsync(It.Is<ParameterSet>(e => e.IsActive == false), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Update" && a.OldValue == "Active" && a.NewValue == "Inactive" && a.EntityId == "1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_主表不存在_抛异常()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ParameterSet?)null);

        var act = async () => await _service.UpdateParameterSetAsync(1, ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码变更_拒绝不写库()
    {
        // Q1 创建后不可变更：既有 PS_A → 入参 PS_B（含大小写差异）一律拒绝
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A"));

        var act = async () => await _service.UpdateParameterSetAsync(1, ValidInput(code: "PS_B"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不可变更*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码唯一冲突_抛异常不写库()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A"));
        _repository
            .Setup(r => r.ParameterSetCodeExistsAsync("PS_A", 1, It.IsAny<CancellationToken>())) // excludeId=1 ⇒ 排除自身仍冲突
            .ReturnsAsync(true);

        var act = async () => await _service.UpdateParameterSetAsync(1, ValidInput(code: "PS_A"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<ParameterSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== DeleteParameterSet（2026-10-26 同 R1 规则集删除口径：无 PUBLISHED 才可删）====================

    private static ParameterSetVersion DraftParameterSetVersion(long id = 11, long parameterSetId = 1, string code = "V2")
        => new()
        {
            Id = id,
            ParameterSetId = parameterSetId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Draft,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    private static ParameterSetVersion PublishedParameterSetVersion(long id = 10, long parameterSetId = 1, string code = "V1")
        => new()
        {
            Id = id,
            ParameterSetId = parameterSetId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    [Fact]
    public async Task DeleteParameterSet_无Published无引用_整族删并前置审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A"));
        _versionRepo
            .Setup(r => r.GetByParameterSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftParameterSetVersion(11) });
        _strategyProfileVersionRepo
            .Setup(r => r.CountReferencingParameterSetVersionAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _service.DeleteParameterSetAsync(1, "u1", 1, CancellationToken.None);

        _repository.Verify(r => r.DeleteFamilyAsync(1L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Delete" && a.EntityType == "ParameterSet" && a.EntityId == "1" && a.NewValue == "Deleted"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteParameterSet_存在Published版本_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A"));
        _versionRepo
            .Setup(r => r.GetByParameterSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { PublishedParameterSetVersion(10), DraftParameterSetVersion(11) });

        var act = async () => await _service.DeleteParameterSetAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已发布过*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteParameterSet_版本被策略包引用_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingParameterSet(id: 1, code: "PS_A"));
        _versionRepo
            .Setup(r => r.GetByParameterSetIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftParameterSetVersion(11) });
        _strategyProfileVersionRepo
            .Setup(r => r.CountReferencingParameterSetVersionAsync(new long[] { 11 }, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var act = async () => await _service.DeleteParameterSetAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*被策略包引用*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}