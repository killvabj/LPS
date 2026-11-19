using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Entities.Auth;
using Moq;
using Xunit;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 3号位 2026-11-17 回执更正（4号位 反馈「跨厂LT 应与现有参数走同一通用接口，而非单独端点」）：
/// 跨厂LT 作为参数集版本普通 6 号子块，走通用参数集版本接口——
/// ① Create/Update 经 <c>EnsureParameterSetNormalized</c> 归一化入快照（提交值优先）；② 仅改其它主题时快照保留该块；
/// ③ 通用详情 <c>GetParameterSetVersionAsync</c> 读回该块。
/// </summary>
public class CrossFactoryLeadTimeGovernanceTests
{
    private const int ActorUserId = 7;

    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _paramRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly GovernanceVersionService _service;

    public CrossFactoryLeadTimeGovernanceTests()
    {
        // P1-04：审计预检 + 落审计默认成功
        _auditRepo.Setup(a => a.EnsureWritableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AuditLog());

        _service = new GovernanceVersionService(
            _ruleSetRepo.Object,
            _paramRepo.Object,
            Mock.Of<IStrategyProfileRepository>(),
            Mock.Of<IStrategyProfileVersionRepository>(),
            _auditRepo.Object);
    }

    private const string Hours24Rule =
        "[{\"FromFactoryCode\":\"BJ\",\"ToFactoryCode\":\"CN\",\"ProductFamilyCode\":null,\"CrossFactoryLeadTimeHours\":24,\"IsActive\":true}]";

    private const string Hours48Rule =
        "[{\"FromFactoryCode\":\"BJ\",\"ToFactoryCode\":\"CN\",\"ProductFamilyCode\":null,\"CrossFactoryLeadTimeHours\":48,\"IsActive\":true}]";

    // ==================== 通用写：Create / Update 归一化 ====================

    [Fact]
    public async Task CreateParameterSet_带跨厂LT子块_归一化入快照()
    {
        _paramRepo.Setup(r => r.AddAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ParameterSetVersion v, CancellationToken _) => v);

        var request = new ParameterSetVersion
        {
            ParameterSetId = 50,
            VersionCode = "P1",
            LockJson = "{\"Trigger\":{}}",
            CrossFactoryLeadTimeRulesJson = Hours24Rule
        };

        var created = await _service.CreateParameterSetVersionAsync(request, "creator", ActorUserId);

        using var doc = System.Text.Json.JsonDocument.Parse(created.ContentSnapshotJson!);
        doc.RootElement.TryGetProperty("Lock", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("CrossFactoryLeadTimeRules", out var cf).Should().BeTrue();
        cf.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task UpdateParameterSet_提交跨厂LT子块_提交值优先写入()
    {
        // 既有快照含旧跨厂LT（24h）；通用 PUT 提交新块（48h）＋ Lock → 快照应取提交值 48h。
        var existing = new ParameterSetVersion
        {
            Id = 51,
            ParameterSetId = 510,
            VersionCode = "P1",
            Status = GovernanceVersionStatus.Draft,
            ContentSnapshotJson = "{\"Lock\":{\"Trigger\":{}},\"CrossFactoryLeadTimeRules\":" + Hours24Rule + "}"
        };
        _paramRepo.Setup(r => r.GetByIdAsync(51, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        ParameterSetVersion? captured = null;
        _paramRepo.Setup(r => r.UpdateAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .Callback<ParameterSetVersion, CancellationToken>((v, _) => captured = v)
            .Returns(Task.CompletedTask);

        var update = new ParameterSetVersion
        {
            Id = 51,
            ParameterSetId = 510,
            VersionCode = "P1",
            LockJson = "{\"Trigger\":{}}",
            CrossFactoryLeadTimeRulesJson = Hours48Rule
        };

        await _service.UpdateParameterSetVersionAsync(51, update, ActorUserId, default);

        captured.Should().NotBeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(captured!.ContentSnapshotJson!);
        doc.RootElement.TryGetProperty("CrossFactoryLeadTimeRules", out var cf).Should().BeTrue();
        cf[0].GetProperty("CrossFactoryLeadTimeHours").GetInt32().Should().Be(48);
    }

    // R1（4号位 2026-11-18 回函核证缺口的回归用例）：五主题全空、仅提交跨厂LT 子块时，
    // EnsureParameterSetNormalized 的早退不得吞掉本块 —— 典型场景「新建一个版本只配跨厂LT」。
    [Fact]
    public async Task CreateParameterSet_仅跨厂LT子块_五主题全空_子块归一化入快照()
    {
        _paramRepo.Setup(r => r.AddAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ParameterSetVersion v, CancellationToken _) => v);

        var request = new ParameterSetVersion
        {
            ParameterSetId = 54,
            VersionCode = "P1",
            // 五主题全空；仅提交跨厂LT 子块（R1 场景）
            CrossFactoryLeadTimeRulesJson = Hours24Rule
        };

        var created = await _service.CreateParameterSetVersionAsync(request, "creator", ActorUserId);

        using var doc = System.Text.Json.JsonDocument.Parse(created.ContentSnapshotJson!);
        doc.RootElement.TryGetProperty("CrossFactoryLeadTimeRules", out var cf).Should().BeTrue("五主题全空+仅带跨厂LT 时早退不得吞掉本块");
        cf.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task UpdateParameterSet_仅跨厂LT子块_五主题全空_提交值优先写入()
    {
        // 既有快照含旧跨厂LT（24h）；通用 PUT 仅提交跨厂LT（48h）、五主题全空 → 快照应取提交值 48h（不得被早退吞为 24h/无值）。
        var existing = new ParameterSetVersion
        {
            Id = 54,
            ParameterSetId = 540,
            VersionCode = "P1",
            Status = GovernanceVersionStatus.Draft,
            ContentSnapshotJson = "{\"CrossFactoryLeadTimeRules\":" + Hours24Rule + "}"
        };
        _paramRepo.Setup(r => r.GetByIdAsync(54, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        ParameterSetVersion? captured = null;
        _paramRepo.Setup(r => r.UpdateAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .Callback<ParameterSetVersion, CancellationToken>((v, _) => captured = v)
            .Returns(Task.CompletedTask);

        // 前端读改写：仅回传跨厂LT 块，五主题全空（R1 场景）
        var update = new ParameterSetVersion
        {
            Id = 54,
            ParameterSetId = 540,
            VersionCode = "P1",
            CrossFactoryLeadTimeRulesJson = Hours48Rule
        };

        await _service.UpdateParameterSetVersionAsync(54, update, ActorUserId, default);

        captured.Should().NotBeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(captured!.ContentSnapshotJson!);
        doc.RootElement.TryGetProperty("CrossFactoryLeadTimeRules", out var cf).Should().BeTrue("五主题全空+仅带跨厂LT 时早退不得吞掉本块");
        cf[0].GetProperty("CrossFactoryLeadTimeHours").GetInt32().Should().Be(48);
    }

    [Fact]
    public async Task UpdateParameterSet_仅改主题_跨厂LT子块保留()
    {
        // 4号位 来函 R3 场景：前端只改任意主题参数保存草稿、未带跨厂LT 子块 → 快照重建不得清空本块。
        var existing = new ParameterSetVersion
        {
            Id = 52,
            ParameterSetId = 520,
            VersionCode = "P1",
            Status = GovernanceVersionStatus.Draft,
            ContentSnapshotJson = "{\"Lock\":{\"Trigger\":{}},\"CrossFactoryLeadTimeRules\":" + Hours24Rule + "}"
        };
        _paramRepo.Setup(r => r.GetByIdAsync(52, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        ParameterSetVersion? captured = null;
        _paramRepo.Setup(r => r.UpdateAsync(It.IsAny<ParameterSetVersion>(), It.IsAny<CancellationToken>()))
            .Callback<ParameterSetVersion, CancellationToken>((v, _) => captured = v)
            .Returns(Task.CompletedTask);

        // 前端读改写：只回传 LockJson，CrossFactoryLeadTimeRulesJson=null
        var update = new ParameterSetVersion { Id = 52, ParameterSetId = 520, VersionCode = "P1", LockJson = "{\"Trigger\":{}}" };

        await _service.UpdateParameterSetVersionAsync(52, update, ActorUserId, default);

        captured.Should().NotBeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(captured!.ContentSnapshotJson!);
        doc.RootElement.TryGetProperty("Lock", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("CrossFactoryLeadTimeRules", out var cf).Should().BeTrue();
        cf.GetArrayLength().Should().Be(1);
    }

    // ==================== 通用读：详情投影读回 ====================

    [Fact]
    public async Task GetParameterSetVersion_通用详情读回跨厂LT子块()
    {
        var detail = new ParameterSetVersion
        {
            Id = 53,
            ParameterSetId = 530,
            VersionCode = "P1",
            LockJson = "{\"Trigger\":{}}",
            CrossFactoryLeadTimeRulesJson = Hours24Rule
        };
        _paramRepo.Setup(r => r.GetDetailByIdAsync(53, It.IsAny<CancellationToken>())).ReturnsAsync(detail);

        var result = await _service.GetParameterSetVersionAsync(53);

        result.Should().NotBeNull();
        result!.CrossFactoryLeadTimeRulesJson.Should().Be(Hours24Rule);
    }
}