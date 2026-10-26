using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;
using StrategyProfile = LPS.APS.Core.Entities.APS.StrategyProfile;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using LPS.APS.Core.Enum;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 策略包主表治理服务单元测试（2026-10-25：补齐 4号位 缺口「策略包缺主表写端点 POST/PUT」，3号位）
/// 覆盖口径（对照 4号位 §四）：
///   Q1 Create 默认启用 + 编码唯一 + 编码创建后不可变更；Q2 **只建壳不自动跟建版本**（本服务仅建主表壳）；
///   Q8 RunType 仅五值或 NULL（冻结 07 §5.5）——非法值拒绝；
///   P1-04 审计：Create/Update 写后落 AuditLog；审计前置失败 → 业务零变更（fail-before-write）。
/// 注：Q5 不给 DELETE——本服务无 Delete 方法（与 rule-set R1 例外区分）。
/// </summary>
public class StrategyProfileGovernanceServiceTests
{
    private readonly Mock<IStrategyProfileRepository> _repository = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IScheduleRunRepository> _scheduleRunRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly StrategyProfileGovernanceService _service;

    public StrategyProfileGovernanceServiceTests()
    {
        // 默认唯一性通过 + 审计库可写，只让「被测变量」失败
        _repository
            .Setup(r => r.StrategyProfileCodeExistsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new StrategyProfileGovernanceService(_repository.Object, _strategyProfileVersionRepo.Object, _scheduleRunRepo.Object, _auditRepo.Object);
    }

    private static StrategyProfile ValidInput(string code = "SP_A", string name = "测试策略包", string? runType = null, bool isActive = true)
        => new()
        {
            StrategyProfileCode = code,
            StrategyProfileName = name,
            Description = "说明",
            RunType = runType,
            IsActive = isActive,
        };

    private static StrategyProfile ExistingStrategyProfile(long id = 1, string code = "SP_A", string? runType = null, bool isActive = true)
        => new()
        {
            Id = id,
            StrategyProfileCode = code,
            StrategyProfileName = "既有策略包",
            Description = "既有说明",
            RunType = runType,
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
            .Setup(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()))
            .Callback<StrategyProfile, CancellationToken>((e, _) => e.Id = 99)
            .ReturnsAsync((StrategyProfile e, CancellationToken _) => e);

        var result = await _service.CreateStrategyProfileAsync(ValidInput(), "u1", 1, CancellationToken.None);

        result.Id.Should().Be(99);
        result.IsActive.Should().BeTrue();              // Q1 新建默认启用
        result.StrategyProfileCode.Should().Be("SP_A");
        result.RunType.Should().BeNull();               // Q8 留空 = 适用所有运行类型
        result.CreatedAt.Should().NotBe(default);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Create" && a.EntityType == "StrategyProfile" && a.EntityId == "99"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_带合法RunType_落库并审计()
    {
        _repository
            .Setup(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StrategyProfile e, CancellationToken _) => e);

        var result = await _service.CreateStrategyProfileAsync(ValidInput(runType: "FULL_SCHEDULE"), "u1", 1, CancellationToken.None);

        result.RunType.Should().Be("FULL_SCHEDULE");
    }

    [Fact]
    public async Task Create_非法RunType_抛异常不落库()
    {
        var act = async () => await _service.CreateStrategyProfileAsync(ValidInput(runType: "BOGUS_TYPE"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RunType 非法*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_编码为空_抛异常不落库()
    {
        var act = async () => await _service.CreateStrategyProfileAsync(ValidInput(code: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*StrategyProfileCode 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_编码重复_抛异常不落库()
    {
        _repository
            .Setup(r => r.StrategyProfileCodeExistsAsync("SP_A", It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = async () => await _service.CreateStrategyProfileAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_名称为空_抛异常不落库()
    {
        var act = async () => await _service.CreateStrategyProfileAsync(ValidInput(name: "  "), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*StrategyProfileName 不能为空*");
        _repository.Verify(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_审计前置失效_业务零变更不写主表()
    {
        // P1-04 fail-before-write：审计库不可写 → 整操作失败，不 INSERT 主表、更不落审计
        _auditRepo
            .Setup(r => r.EnsureWritableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库不可写"));

        var act = async () => await _service.CreateStrategyProfileAsync(ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(r => r.CreateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== UpdateAsync ====================

    [Fact]
    public async Task Update_成功_改名称与停用并审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A", runType: null, isActive: true));

        var result = await _service.UpdateStrategyProfileAsync(1, ValidInput(code: "SP_A", name: "改名", isActive: false), "u1", 1, CancellationToken.None);

        result.StrategyProfileName.Should().Be("改名");
        result.IsActive.Should().BeFalse();              // Q4 IsActive=0 停用
        result.StrategyProfileCode.Should().Be("SP_A");  // 编码冻结不可改
        _repository.Verify(r => r.UpdateAsync(It.Is<StrategyProfile>(e => e.IsActive == false), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Update" && a.OldValue == "Active" && a.NewValue == "Inactive" && a.EntityId == "1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_改RunType_落库()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A", runType: null));

        var result = await _service.UpdateStrategyProfileAsync(1, ValidInput(code: "SP_A", runType: "MANUAL_RESCHEDULE"), "u1", 1, CancellationToken.None);

        result.RunType.Should().Be("MANUAL_RESCHEDULE");
        _repository.Verify(r => r.UpdateAsync(It.Is<StrategyProfile>(e => e.RunType == "MANUAL_RESCHEDULE"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_非法RunType_抛异常不写库()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));

        var act = async () => await _service.UpdateStrategyProfileAsync(1, ValidInput(code: "SP_A", runType: "BOGUS_TYPE"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RunType 非法*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_主表不存在_抛异常()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StrategyProfile?)null);

        var act = async () => await _service.UpdateStrategyProfileAsync(1, ValidInput(), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码变更_拒绝不写库()
    {
        // Q1 创建后不可变更：既有 SP_A → 入参 SP_B（含大小写差异）一律拒绝
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));

        var act = async () => await _service.UpdateStrategyProfileAsync(1, ValidInput(code: "SP_B"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*不可变更*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_编码唯一冲突_抛异常不写库()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));
        _repository
            .Setup(r => r.StrategyProfileCodeExistsAsync("SP_A", 1, It.IsAny<CancellationToken>())) // excludeId=1 ⇒ 排除自身仍冲突
            .ReturnsAsync(true);

        var act = async () => await _service.UpdateStrategyProfileAsync(1, ValidInput(code: "SP_A"), "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已存在*");
        _repository.Verify(r => r.UpdateAsync(It.IsAny<StrategyProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== DeleteStrategyProfile（2026-10-26 同 R1 规则集删除口径：无 PUBLISHED 才可删）====================

    private static StrategyProfileVersion DraftStrategyProfileVersion(long id = 11, long strategyProfileId = 1, string code = "V2")
        => new()
        {
            Id = id,
            StrategyProfileId = strategyProfileId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Draft,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    private static StrategyProfileVersion PublishedStrategyProfileVersion(long id = 10, long strategyProfileId = 1, string code = "V1")
        => new()
        {
            Id = id,
            StrategyProfileId = strategyProfileId,
            VersionCode = code,
            Status = GovernanceVersionStatus.Published,
            PublishedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            CreatedBy = "u1",
        };

    [Fact]
    public async Task DeleteStrategyProfile_无Published无引用_整族删并前置审计()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));
        _strategyProfileVersionRepo
            .Setup(r => r.GetByStrategyProfileIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftStrategyProfileVersion(11) });
        _scheduleRunRepo
            .Setup(r => r.CountReferencingStrategyProfileVersionAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _service.DeleteStrategyProfileAsync(1, "u1", 1, CancellationToken.None);

        _repository.Verify(r => r.DeleteFamilyAsync(1L, It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(It.Is<AuditLog>(a =>
            a.ActionCode == "Delete" && a.EntityType == "StrategyProfile" && a.EntityId == "1" && a.NewValue == "Deleted"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteStrategyProfile_存在Published版本_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));
        _strategyProfileVersionRepo
            .Setup(r => r.GetByStrategyProfileIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { PublishedStrategyProfileVersion(10), DraftStrategyProfileVersion(11) });

        var act = async () => await _service.DeleteStrategyProfileAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已发布过*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteStrategyProfile_版本被运行引用_拒绝不删()
    {
        _repository
            .Setup(r => r.GetByIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingStrategyProfile(id: 1, code: "SP_A"));
        _strategyProfileVersionRepo
            .Setup(r => r.GetByStrategyProfileIdAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DraftStrategyProfileVersion(11) });
        _scheduleRunRepo
            .Setup(r => r.CountReferencingStrategyProfileVersionAsync(new long[] { 11 }, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var act = async () => await _service.DeleteStrategyProfileAsync(1, "u1", 1, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*被运行引用*");
        _repository.Verify(r => r.DeleteFamilyAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}