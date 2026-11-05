using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Application.Services.Query;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Interfaces;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;
using PlanVersion = LPS.APS.Core.Entities.APS.PlanVersion;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// P0-08 验收：ScheduleRun 运行生命周期治理（IRunLifecycleService）
/// 覆盖：ExpectedDomainKeysJson 冻结规则校验（FULL≥1 / RESCHEDULE 恰1）、Candidate 最小确认、
///       候选激活（CANDIDATE→ACTIVE + 同域唯一预检）、FAILED 恢复新建、Run 引用追溯。
/// 边界：不重写 2号位运行状态执行流转；旧 FAILED 记录绝不动（不回改 RUNNING）。
/// </summary>
public class RunLifecycleServiceTests
{
    private readonly Mock<IScheduleRunRepository> _scheduleRunRepo = new();
    private readonly Mock<IPlanVersionRepository> _planVersionRepo = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<IRuleSetVersionRepository> _ruleSetRepo = new();
    private readonly Mock<IParameterSetVersionRepository> _parameterSetRepo = new();
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly Mock<IDataScopeService> _dataScopeService = new();
    private readonly Mock<IDomainDefinitionService> _domainDefinitionService = new();
    private readonly Mock<IMasterDataLookupRepository> _masterDataLookup = new();
    private readonly Mock<IOverviewQueryRepository> _overviewQueryRepo = new();
    private readonly Mock<ISchedulingOrchestrator> _schedulingOrchestrator = new();
    private readonly Mock<IScheduleRunService> _scheduleRunService = new();
    private readonly RunLifecycleService _service;

    public RunLifecycleServiceTests()
    {
        // 5e：默认全范围放行（Global），既有用例不感知业务范围校验
        _dataScopeService
            .Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // 缺口A（R11-3B 项1）：默认启用域集为空 → 跳过 ProductFamily/Factory 两维校验（既有用例零感知）；
        // ResolveScopeAsync 默认 Global（全放行），仅显式配范围的用例才参与判定。
        _domainDefinitionService
            .Setup(s => s.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DomainDefinitionInfo>());
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.Global);

        // P1-02：3→2 排程发令枪默认成功（2号位 主流程不在单测内重演）
        _schedulingOrchestrator
            .Setup(o => o.RunSchedulingAndFinalizeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchedulingRunResult { IsSuccess = true });
        // P1-03：FAILED 恢复内联执行 seam（ExecuteRunAsync）默认全成功
        _schedulingOrchestrator
            .Setup(o => o.ExecuteRunAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchedulingRunResult { IsSuccess = true });

        _service = new RunLifecycleService(
            _scheduleRunRepo.Object,
            _planVersionRepo.Object,
            _strategyProfileVersionRepo.Object,
            _ruleSetRepo.Object,
            _parameterSetRepo.Object,
            _auditRepo.Object,
            _dataScopeService.Object,
            _domainDefinitionService.Object,
            _masterDataLookup.Object,
            _overviewQueryRepo.Object,
            _schedulingOrchestrator.Object,
            _scheduleRunService.Object,
            NullLogger<RunLifecycleService>.Instance);
    }

    /// <summary>构造合法 FULL_SCHEDULE 运行（两 Domain）</summary>
    private static ScheduleRunGov FullRun(int id = 1, string? expectedJson = """["D1","D2"]""")
        => new()
        {
            Id = id,
            RunType = "FULL_SCHEDULE",
            Status = "RUNNING",
            TriggeredBy = "Hangfire",
            DataCutoffTime = DateTime.Now,
            StrategyProfileVersionId = 10,
            ExpectedDomainKeysJson = expectedJson,
            StartedAt = DateTime.Now,
        };

    /// <summary>构造合法 RESCHEDULE（Candidate）运行（单 Domain）</summary>
    private static ScheduleRunGov CandidateRun(int id = 2, string? expectedJson = """["D1"]""")
        => new()
        {
            Id = id,
            RunType = "MANUAL_RESCHEDULE",
            Status = "RUNNING",
            TriggeredBy = "API",
            DataCutoffTime = DateTime.Now,
            StrategyProfileVersionId = 11,
            ExpectedDomainKeysJson = expectedJson,
            StartedAt = DateTime.Now,
        };

    /// <summary>构造带冻结 Base 锚点的来源 Run（P0-08 项2：BasePlanVersionId 权威落盘，激活重查当前 ACTIVE 用）</summary>
    private static ScheduleRunGov BaseFrozenCandidateRun(int id = 2, int? basePlanVersionId = null,
        string? runType = "MANUAL_RESCHEDULE", string? expectedJson = """["D1"]""")
        => new()
        {
            Id = id,
            RunType = runType,
            Status = "RUNNING",
            TriggeredBy = "API",
            DataCutoffTime = DateTime.Now,
            StrategyProfileVersionId = 11,
            BasePlanVersionId = basePlanVersionId,
            ExpectedDomainKeysJson = expectedJson,
            StartedAt = DateTime.Now,
        };

    /// <summary>构造 CANDIDATE 计划版本（D7：候选身份在 VersionCategory，Status 走执行词表完成态 Computed；默认关联可激活来源 Run 2）</summary>
    private static PlanVersion CandidateVersion(int id = 5, string? domainKey = "D1", int? sourceScheduleRunId = 2)
        => new()
        {
            Id = id,
            VersionCode = "V-CAND-5",
            VersionCategory = "CANDIDATE",
            DomainKey = domainKey,
            Status = "CANDIDATE",
            SourceScheduleRunId = sourceScheduleRunId,
            CreatedAt = DateTime.Now,
        };

    /// <summary>构造"已完成最小人工确认"的审计记录（P0-04 激活硬前置）</summary>
    private static IReadOnlyList<AuditLog> ConfirmedLogs(int planVersionId = 5)
        => new[]
        {
            new AuditLog
            {
                ActionCode = "ConfirmCandidate",
                EntityType = "PlanVersion",
                EntityId = planVersionId.ToString(),
                UserCode = "u1",
                OccurredAt = DateTime.Now,
            },
        };

    /// <summary>构造 INSERT_ORDER_WHATIF 来源 Run（CTP/INSERT_IMPACT_ANALYSIS，永远不得激活）</summary>
    private static ScheduleRunGov WhatIfRun(int id = 3)
        => new()
        {
            Id = id,
            RunType = "INSERT_ORDER_WHATIF",
            Status = "COMPLETED",
            TriggeredBy = "API",
            DataCutoffTime = DateTime.Now,
            ExpectedDomainKeysJson = """["D1"]""",
            StartedAt = DateTime.Now,
        };

    // ==================== ValidateExpectedDomainKeysAsync ====================

    [Fact]
    public async Task Validate_Run不存在_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((ScheduleRunGov?)null);

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(999, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_ExpectedDomainKeysJson为空_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(expectedJson: null));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(1, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_ExpectedDomainKeysJson非JSON数组_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(expectedJson: """{"not":"array"}"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(1, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_FullSchedule_两Domain_通过()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(expectedJson: """["D1","D2"]"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(1, 1, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Validate_FullSchedule_重复DomainKey_抛异常()
    {
        // Arrange（P1-01）：FULL 预期 Domain 集合含重复 DomainKey
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(expectedJson: """["D1","D1"]"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(1, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_FullSchedule_空数组_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(expectedJson: "[]"));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(1, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_Reschedule_恰一Domain_通过()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateRun(expectedJson: """["D1"]"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(2, 1, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Validate_Reschedule_两Domain_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateRun(expectedJson: """["D1","D2"]"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(2, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Validate_数组含空DomainKey_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateRun(expectedJson: """["D1",""]"""));

        // Act
        var act = async () => await _service.ValidateExpectedDomainKeysAsync(2, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ==================== ConfirmCandidateAsync ====================

    [Fact]
    public async Task Confirm_版本不存在_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((PlanVersion?)null);

        // Act
        var act = async () => await _service.ConfirmCandidateAsync(999, 1, "u1", null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Confirm_非候选VersionCategory_抛异常()
    {
        // Arrange（D7：候选身份在 VersionCategory，RESCHEDULE 非候选 → 拒绝）
        var version = CandidateVersion(5);
        version.VersionCategory = "RESCHEDULE";
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);

        // Act
        var act = async () => await _service.ConfirmCandidateAsync(5, 1, "u1", null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Confirm_DomainKey为空_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateVersion(5, domainKey: null));

        // Act
        var act = async () => await _service.ConfirmCandidateAsync(5, 1, "u1", null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Confirm_同域已有ACTIVE_仍可正常确认()
    {
        // Arrange：Base ACTIVE 存在（正常"Base ACTIVE → Candidate 比较/确认 → 新 Candidate 采用"流程）
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);

        // Act：确认不预检同域 ACTIVE（P0-06：确认与唯一性无关，只记录事实）
        await _service.ConfirmCandidateAsync(5, 1, "u1", "基于 Base ACTIVE 的新 Candidate", CancellationToken.None);

        // Assert：确认成功，仅记审计，不写 Activated、不转 ACTIVE、不触碰同域 ACTIVE
        version.Status.Should().Be("CANDIDATE");
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "ConfirmCandidate"
                && l.EntityId == "5"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_合法_仅记审计不写Activated()
    {
        // Arrange
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);

        // Act
        await _service.ConfirmCandidateAsync(5, 1, "u1", "人工确认", CancellationToken.None);

        // Assert（P0-05）：确认不污染 ActivatedAt/ActivatedBy，状态保持 CANDIDATE，不写库
        version.ActivatedAt.Should().BeNull();
        version.ActivatedBy.Should().BeNull();
        version.Status.Should().Be("CANDIDATE");
        _planVersionRepo.Verify(r => r.UpdateAsync(It.IsAny<PlanVersion>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "ConfirmCandidate"
                && l.EntityType == "PlanVersion"
                && l.EntityId == "5"
                && l.UserCode == "u1"
                && l.Remark!.Contains("5")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_业务范围未授权Domain_抛异常()
    {
        // Arrange（5e：F-G4 Domain 维度）—— 用户 DataScopePolicy 仅授权 D2，目标版本 Domain=D1
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _dataScopeService
            .Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), DataScopeTypes.Domain, "D1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ScopeViolationException(DataScopeTypes.Domain, "D1"));

        // Act
        var act = async () => await _service.ConfirmCandidateAsync(5, 1, "u1", null, CancellationToken.None);

        // Assert：范围拒绝，不写审计
        await act.Should().ThrowAsync<InvalidOperationException>();
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== ActivateCandidateAsync ====================

    [Fact]
    public async Task Activate_非CANDIDATE身份_抛异常()
    {
        // Arrange（门禁判**身份** VersionCategory —— 这一条 D7 是对的，保留）
        var version = CandidateVersion(5);
        version.VersionCategory = "RESCHEDULE";
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Activate_DomainKey为空_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateVersion(5, domainKey: null));

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Activate_未确认_抛异常()
    {
        // Arrange（P0-04）：无 ConfirmCandidate 审计
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(CandidateVersion(5, "D1"));
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AuditLog>());

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：激活被拒，未触碰来源 Run / 仓储写
        await act.Should().ThrowAsync<InvalidOperationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_来源Run为INSERT_ORDER_WHATIF_抛异常()
    {
        // Arrange（P0-03）：已确认，但来源 Run 为 INSERT_ORDER_WHATIF（CTP/INSERT_IMPACT_ANALYSIS，永远不得激活）
        var version = CandidateVersion(5, "D1", sourceScheduleRunId: 3);
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(WhatIfRun(3));

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：无条件拒绝激活，未触发原子替换
        await act.Should().ThrowAsync<InvalidOperationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_SourceScheduleRunId为空_抛异常()
    {
        // Arrange（P0-03 防绕过）：无法证明来源 Run 可激活 → 拒绝
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CandidateVersion(5, "D1", sourceScheduleRunId: null));
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_同域已有ACTIVE_原子替换()
    {
        // Arrange（P0-06）：Base ACTIVE 存在不再是失败，激活走原子替换（归档旧 ACTIVE + 新版本置 ACTIVE）
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(CandidateRun(2));

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：Status Computed → ACTIVE + 走原子替换（非单版本 UpdateAsync），不因同域 ACTIVE 拒绝
        // 采用记在 Status；身份 VersionCategory 出生即定，激活**不动**它
        version.Status.Should().Be("ACTIVE");
        version.VersionCategory.Should().Be("CANDIDATE");
        version.ActivatedAt.Should().NotBeNull();
        version.ActivatedBy.Should().Be("u1");
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(version, "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _planVersionRepo.Verify(r => r.UpdateAsync(It.IsAny<PlanVersion>(), It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "ActivateCandidate"
                && l.EntityType == "PlanVersion"
                && l.EntityId == "5"
                && l.OldValue == "CANDIDATE"
                && l.NewValue == "ACTIVE"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Activate_合法_已确认且来源可激活_状态转ACTIVE()
    {
        // Arrange：已完成最小确认 + 来源 Run 为可激活的 MANUAL_RESCHEDULE
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(CandidateRun(2));

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：激活 = 正式采用，Status Computed → ACTIVE + 写 ActivatedAt/ActivatedBy + 原子替换
        version.Status.Should().Be("ACTIVE");
        version.VersionCategory.Should().Be("CANDIDATE");
        version.ActivatedAt.Should().NotBeNull();
        version.ActivatedBy.Should().Be("u1");
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(version, "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "ActivateCandidate"
                && l.EntityType == "PlanVersion"
                && l.EntityId == "5"
                && l.OldValue == "CANDIDATE"
                && l.NewValue == "ACTIVE"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Activate_业务范围未授权Domain_抛异常()
    {
        // Arrange（5e：F-G4 Domain 维度）—— 用户 DataScopePolicy 仅授权 D2，目标版本 Domain=D1
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _dataScopeService
            .Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), DataScopeTypes.Domain, "D1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ScopeViolationException(DataScopeTypes.Domain, "D1"));

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：范围拒绝在先（早于确认/来源 Run 校验），不触发原子替换
        await act.Should().ThrowAsync<InvalidOperationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_Base已被顶替为新的ACTIVE_拒激活()
    {
        // Arrange（P0-08 项2 / R11-3B：Base 陈旧/被顶替禁直接采用，防并发竞态）
        // 候选 5 的冻结 Base = 50（来源 Run.BasePlanVersionId）；但同域当前 ACTIVE 已是新的 60（50 已被顶替 ARCHIVED）
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BaseFrozenCandidateRun(2, basePlanVersionId: 50));
        // 重查：冻结 Base(50) 存在，但当前 ACTIVE 已是 60 → Base 陈旧
        _planVersionRepo.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveBase(id: 50, domainKey: "D1"));
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveBase(id: 60, domainKey: "D1"));

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：Base 已被顶替 → 拒直接采用，不触发原子替换
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Base 已被顶替/变更*");
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_Base仍为当前ACTIVE_可激活()
    {
        // Arrange（P0-08 项2 正例：冻结 Base 仍是当前 ACTIVE，不因重查误阻断）
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BaseFrozenCandidateRun(2, basePlanVersionId: 50));
        _planVersionRepo.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveBase(id: 50, domainKey: "D1"));
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveBase(id: 50, domainKey: "D1"));

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：Base 仍为当前 ACTIVE → 正常激活（正式采用）
        version.Status.Should().Be("ACTIVE");
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(version, "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ==================== GetRunDomainStatusAsync（G8 域级状态；先判 Status 再判身份） ====================

    /// <summary>构造某 Run 下属的 PlanVersion（身份 VersionCategory 与进度 Status 两列分别指定）</summary>
    private static PlanVersion DomainVersion(
        int id, string versionCategory, string status, string domainKey = "D1")
        => new()
        {
            Id = id,
            VersionCode = $"V-{id}",
            VersionCategory = versionCategory,
            DomainKey = domainKey,
            Status = status,
            ComputedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
        };

    [Theory]
    // 判定**只看 Status**，VersionCategory 不参与 —— 词表对齐后 Status 自己就表达了「算完待采用」，
    // 原先那条 `_ when pv.VersionCategory=='CANDIDATE'` 兜底已删（它会把夜间日基线算完态误判成候选）。
    // 采用/归档 ⇒ 该域本次已落定；注意身份仍是 CANDIDATE（出生即定，激活不翻转它）
    [InlineData("CANDIDATE", "ACTIVE", "COMPLETED")]
    [InlineData("CANDIDATE", "ARCHIVED", "COMPLETED")]
    // 算完待采用（**不分身份**：白天候选与夜间日基线同判）⇒ 待人工确认
    [InlineData("CANDIDATE", "CANDIDATE", "CANDIDATE")]
    [InlineData("DAILY_BASELINE", "CANDIDATE", "CANDIDATE")]
    // 失败：写点与读点同值（2026-10-15 前写点写 ComputeFailed、读点判 FAILED ⇒ 此分支永不可达，已修）
    [InlineData("DAILY_BASELINE", "FAILED", "FAILED")]
    [InlineData("CANDIDATE", "FAILED", "FAILED")]
    // 未定：待领 / 在算
    [InlineData("DAILY_BASELINE", "BUILDING", "RUNNING")]
    [InlineData("DAILY_BASELINE", "Computing", "RUNNING")]
    public async Task 域级状态_只按Status判定(string versionCategory, string status, string expected)
    {
        // Arrange：单域 Run（D1）+ 该域一个 PlanVersion
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(1, expectedJson: """["D1"]"""));
        _planVersionRepo.Setup(r => r.GetByScheduleRunIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlanVersion> { DomainVersion(7, versionCategory, status) });

        // Act
        var domains = await _service.GetRunDomainStatusAsync(1, CancellationToken.None);

        // Assert
        domains.Should().HaveCount(1);
        domains[0].DomainKey.Should().Be("D1");
        domains[0].PlanVersionId.Should().Be(7);
        domains[0].Status.Should().Be(expected);
    }

    /// <summary>
    /// 回归钉（2026-10-15，B）：激活写点产出的实体，喂给域级状态读点，必须判 COMPLETED。
    /// 原 bug：读点先判 VersionCategory=='CANDIDATE'，而每一行已采用的 VersionCategory 恒为 CANDIDATE
    ///        ⇒ 「ACTIVE ⇒ COMPLETED」分支永不可达，激活后域级视图永远显示「候选待确认」。
    /// 本用例同时钉死两件事：① 激活写的是 Status，VersionCategory 不动；② 写点与读点口径一致。
    /// </summary>
    [Fact]
    public async Task 激活写点与域级读点口径一致_激活后判COMPLETED()
    {
        // Arrange：走完整激活路径（已确认 + 来源 Run 可激活）
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs());
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(CandidateRun(2));

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // 写点产出：Status Computed → ACTIVE；身份 VersionCategory 不动（仍 CANDIDATE）
        version.Status.Should().Be("ACTIVE");
        version.VersionCategory.Should().Be("CANDIDATE");

        // 把写点产出的实体原样作为读点输入（同一域 D1）
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullRun(1, expectedJson: """["D1"]"""));
        _planVersionRepo.Setup(r => r.GetByScheduleRunIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlanVersion> { version });

        var domains = await _service.GetRunDomainStatusAsync(1, CancellationToken.None);

        // Assert：读点看出「已正式采用 ⇒ COMPLETED」，不再是「候选待确认」
        domains.Should().HaveCount(1);
        domains[0].Status.Should().Be("COMPLETED");
    }

    // ==================== RecoverFailedRunAsync ====================

    [Fact]
    public async Task Recover_旧Run不存在_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((ScheduleRunGov?)null);

        // Act
        var act = async () => await _service.RecoverFailedRunAsync(999, 1, "zhangsan", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Recover_旧Run非FAILED_抛异常()
    {
        // Arrange
        var run = FullRun(1);
        run.Status = "COMPLETED";
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(run);

        // Act
        var act = async () => await _service.RecoverFailedRunAsync(1, 1, "zhangsan", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Recover_合法_新建RUNNING继承基线_旧记录不动()
    {
        // Arrange
        var failed = FullRun(1, expectedJson: """["D1","D2"]""");
        failed.Status = "FAILED";
        failed.CompletedAt = DateTime.Now;
        failed.ErrorMessage = "致命错误";
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(failed);
        _scheduleRunRepo.Setup(r => r.InsertForRecoveryWithShellsAsync(
            failed, It.IsAny<IReadOnlyList<RecoveryShellSpec>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);
        // 失败 Run 无既有 PlanVersion → 壳窗口兜底今天 ~ +90 天（不影响本用例断言）
        _planVersionRepo.Setup(r => r.GetByScheduleRunIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlanVersion>());

        // Act
        var newId = await _service.RecoverFailedRunAsync(1, 1, "zhangsan", CancellationToken.None);

        // Assert：返回新 Id、继承 StrategyProfileVersionId + ExpectedDomainKeysJson 基线、一域一壳（2 壳）、旧 FAILED 记录不动
        newId.Should().Be(100);
        _scheduleRunRepo.Verify(r => r.InsertForRecoveryWithShellsAsync(
            It.Is<ScheduleRunGov>(s =>
                s.StrategyProfileVersionId == 10
                && s.ExpectedDomainKeysJson == """["D1","D2"]"""
                && s.RunType == "FULL_SCHEDULE"),
            It.Is<IReadOnlyList<RecoveryShellSpec>>(shells => shells.Count == 2),
            "zhangsan",   // 冻结主体＝恢复人登录名，不再写死 'Recover'（否则范围守卫整轮拒）
            It.IsAny<CancellationToken>()), Times.Once);
        failed.Status.Should().Be("FAILED");   // 旧记录不回改 RUNNING
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "RecoverFailedRun"
                && l.EntityType == "ScheduleRun"
                && l.EntityId == "1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Recover_继承基线不合法_抛异常且不新建()
    {
        // Arrange：FAILED 的 FULL_SCHEDULE 但基线为空数组（不满足 ≥1）
        var failed = FullRun(1, expectedJson: "[]");
        failed.Status = "FAILED";
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(failed);

        // Act
        var act = async () => await _service.RecoverFailedRunAsync(1, 1, "zhangsan", CancellationToken.None);

        // Assert：不新建、旧记录不动
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.InsertForRecoveryWithShellsAsync(
            It.IsAny<ScheduleRunGov>(), It.IsAny<IReadOnlyList<RecoveryShellSpec>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        failed.Status.Should().Be("FAILED");
    }

    [Fact]
    public async Task Recover_非FULL_RunType_拒绝恢复且不新建()
    {
        // Arrange：FAILED 但 RunType 为 MANUAL_RESCHEDULE（非 FULL_SCHEDULE，P1-03 §5.1 fail-closed，无 Base 基线可继承）
        var failed = CandidateRun(2, expectedJson: """["D1"]""");
        failed.Status = "FAILED";
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(failed);

        // Act
        var act = async () => await _service.RecoverFailedRunAsync(2, 1, "zhangsan", CancellationToken.None);

        // Assert：拒绝且不新建（旧记录不动、不产生 RUNNING 壳）
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*仅 FULL_SCHEDULE 可恢复*");
        _scheduleRunRepo.Verify(r => r.InsertForRecoveryWithShellsAsync(
            It.IsAny<ScheduleRunGov>(), It.IsAny<IReadOnlyList<RecoveryShellSpec>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        failed.Status.Should().Be("FAILED");
    }

    [Fact]
    public async Task Recover_操作人登录名为空_抛异常且不新建()
    {
        // Arrange：FAILED 的 FULL_SCHEDULE 基线合法，但操作人登录名缺失（JWT 无 Name 声明时 GetCurrentUserCode() 返回空串）
        var failed = FullRun(1, expectedJson: """["D1"]""");
        failed.Status = "FAILED";
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(failed);

        // Act
        var act = async () => await _service.RecoverFailedRunAsync(1, 1, "  ", CancellationToken.None);

        // Assert：fail-fast —— 不读旧 Run、不新建 RUNNING（否则新 Run 主体为空，范围守卫必整轮拒）
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*actorUserCode*");
        _scheduleRunRepo.Verify(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _scheduleRunRepo.Verify(r => r.InsertForRecoveryWithShellsAsync(
            It.IsAny<ScheduleRunGov>(), It.IsAny<IReadOnlyList<RecoveryShellSpec>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== GetRunReferenceTraceAsync ====================

    [Fact]
    public async Task Trace_Run不存在_抛异常()
    {
        // Arrange
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((ScheduleRunGov?)null);

        // Act
        var act = async () => await _service.GetRunReferenceTraceAsync(999, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Trace_合法_完整追溯链()
    {
        // Arrange
        var run = FullRun(1, expectedJson: """["D1"]""");
        run.Status = "COMPLETED";
        run.CompletedAt = DateTime.Now;
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(run);

        _strategyProfileVersionRepo.Setup(r => r.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrategyProfileVersion
            {
                Id = 10,
                VersionCode = "SPV-10",
                RuleSetVersionId = 20,
                ParameterSetVersionId = 30,
            });
        _ruleSetRepo.Setup(r => r.GetByIdAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleSetVersion { Id = 20, VersionCode = "RS-20" });
        _parameterSetRepo.Setup(r => r.GetByIdAsync(30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ParameterSetVersion { Id = 30, VersionCode = "PS-30" });
        // 该 Run 产出并已被正式采用：Status = ACTIVE（采用记在 Status），身份为夜间日基线
        _planVersionRepo.Setup(r => r.GetLatestByScheduleRunIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersion { Id = 7, VersionCategory = "DAILY_BASELINE", Status = "ACTIVE", DomainKey = "D1" });

        // Act
        var trace = await _service.GetRunReferenceTraceAsync(1, CancellationToken.None);

        // Assert
        trace.ScheduleRunId.Should().Be(1);
        trace.RunType.Should().Be("FULL_SCHEDULE");
        trace.Status.Should().Be("COMPLETED");
        trace.StrategyProfileVersionId.Should().Be(10);
        trace.StrategyProfileVersionCode.Should().Be("SPV-10");
        trace.RuleSetVersionCode.Should().Be("RS-20");
        trace.ParameterSetVersionCode.Should().Be("PS-30");
        trace.ExpectedDomainKeysJson.Should().Be("""["D1"]""");
        trace.PlanVersionId.Should().Be(7);
        // PlanVersionStatus 回显 Status 列（采用后为 ACTIVE；本 DTO 不含身份列）
        trace.PlanVersionStatus.Should().Be("ACTIVE");
    }

    // ==================== CreateCandidateRunAsync（B-1 白天候选运行创建） ====================

    /// <summary>构造白天候选运行创建入参（默认 MANUAL_RESCHEDULE + MANUAL_ADJUSTMENT + D1）</summary>
    private static CandidateRunCreateSpec DaytimeSpec(
        string runType = "MANUAL_RESCHEDULE",
        string purpose = "MANUAL_ADJUSTMENT",
        string domainKey = "D1",
        int? basePlanVersionId = null)
        => new()
        {
            RunType = runType,
            Purpose = purpose,
            DomainKey = domainKey,
            BasePlanVersionId = basePlanVersionId,
            Actor = "u1",
        };

    /// <summary>构造 Domain=D1 的 Base ACTIVE 计划版本（白天 Candidate 锚定；采用记在 Status，身份为日基线）</summary>
    private static PlanVersion ActiveBase(int id = 50, string? domainKey = "D1")
        => new()
        {
            Id = id,
            VersionCode = "V-BASE-50",
            VersionCategory = "DAILY_BASELINE",
            DomainKey = domainKey,
            Status = "ACTIVE",
            PlanHorizonStart = DateTime.Today,
            PlanHorizonEnd = DateTime.Today.AddDays(90),
            CreatedAt = DateTime.Now,
        };

    /// <summary>构造默认 PUBLISHED 策略包版本列表（默认 1 条，无生效窗口限制）</summary>
    private static IReadOnlyList<StrategyProfileVersion> DefaultStrategyVersions(int count = 1)
    {
        var list = new List<StrategyProfileVersion>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new StrategyProfileVersion
            {
                Id = 11 + i,
                Status = "PUBLISHED",
                IsDefault = true,
            });
        }
        return list;
    }

    /// <summary>给每个用例铺默认策略版本 mock（合法窗口）</summary>
    private void SetupDefaultStrategyVersions(int count = 1)
        => _strategyProfileVersionRepo
            .Setup(r => r.GetDefaultByRunTypeOrFallbackAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultStrategyVersions(count));

    [Fact]
    public async Task Create_Actor为空_抛异常()
    {
        // Arrange
        var spec = DaytimeSpec();
        spec.Actor = string.Empty;

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(spec, 1, CancellationToken.None);

        // Assert：不触发任何仓储写
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_RunType非白天候选类_抛异常()
    {
        // Arrange（FULL_SCHEDULE / SIMULATION 拒绝创建）
        var spec = DaytimeSpec(runType: "FULL_SCHEDULE");

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(spec, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_Purpose不在RunType合法组合_抛异常()
    {
        // Arrange（实施包 §十九：MANUAL_RESCHEDULE 仅 MANUAL_ADJUSTMENT；CTP 仅配 INSERT_ORDER_WHATIF）
        var spec = DaytimeSpec(runType: "MANUAL_RESCHEDULE", purpose: "CTP");

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(spec, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_DomainKey为空_抛异常()
    {
        // Arrange
        var spec = DaytimeSpec(domainKey: string.Empty);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(spec, 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_Base版本不存在_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((PlanVersion?)null);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(basePlanVersionId: 999), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_Base非ACTIVE_抛异常()
    {
        // Arrange（B：Base 门禁判 Status）
        var baseVersion = ActiveBase();
        baseVersion.Status = "BUILDING";
        _planVersionRepo.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(baseVersion);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(basePlanVersionId: 50), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_BaseDomainKey与目标不一致_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase(domainKey: "D2"));

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(basePlanVersionId: 50), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_未传Base且无当前ACTIVE_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync((PlanVersion?)null);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert：白天候选必须基于 Base ACTIVE
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_业务范围未授权Domain_抛ScopeViolation()
    {
        // Arrange（R11-3B 项1「无权403」Service 端证明：F-G4 Domain 维度 fail-closed）
        // 用户 DataScopePolicy 仅授权 D2，目标 Domain=D1 → EnsureDomainInScopeAsync（RunLifecycleService:487）抛 ScopeViolationException
        // （Web 层 GovernanceController.CreateCandidateRun 现已有独立 catch 映射 403，不再被 InvalidOperation 分支吞成 400）
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _dataScopeService
            .Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), DataScopeTypes.Domain, "D1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ScopeViolationException(DataScopeTypes.Domain, "D1"));

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert：范围拒绝（派生 InvalidOperationException，Web 层映射 403），不触发任何仓储写
        await act.Should().ThrowAsync<ScopeViolationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== 缺口A（R11-3B 项1）：Domain 所属 ProductFamily / Factory 两维范围（范围C） ====================

    /// <summary>铺 Create 走通所需的既有前置（Base ACTIVE + 唯一默认策略版本 + 原子写返回）</summary>
    private void SetupCreateHappyPath()
    {
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _scheduleRunRepo.Setup(r => r.CreateCandidateRunAsync(
                It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandidateRunCreatedResult { NewScheduleRunId = 200, NewPlanVersionId = 300 });
    }

    /// <summary>铺 D1 域定义（FAMILY 类，无工厂）与用户范围策略</summary>
    private void SetupFamilyDomainAndScope(params (string ScopeType, string ScopeValue)[] policies)
    {
        _domainDefinitionService
            .Setup(s => s.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DomainDefinitionInfo("D1", "FAMILY", 1, null)]);
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(policies));
    }

    [Fact]
    public async Task Create_产品族维度已配且命中_放行()
    {
        // Arrange：域 D1 → ProductFamily Id=1 → Code=CYL；用户授权 ProductFamily=CYL（交集命中）
        SetupFamilyDomainAndScope((DataScopeTypes.ProductFamily, "CYL"));
        _masterDataLookup.Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync("CYL");
        SetupCreateHappyPath();

        // Act
        var result = await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        result.NewScheduleRunId.Should().Be(200);
    }

    [Fact]
    public async Task Create_产品族维度已配但不命中_抛ScopeViolation且不写()
    {
        // Arrange：域 → CYL；用户只授权 FRL → 交集为空，拒绝
        SetupFamilyDomainAndScope((DataScopeTypes.ProductFamily, "FRL"));
        _masterDataLookup.Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync("CYL");
        SetupCreateHappyPath();

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ScopeViolationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_未配产品族维度_不构成拒绝理由_放行()
    {
        // Arrange：范围C——用户只配了 Domain 维度，未配 ProductFamily → 该维度不参与交集
        SetupFamilyDomainAndScope((DataScopeTypes.Domain, "D1"));
        SetupCreateHappyPath();

        // Act
        var result = await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        result.NewScheduleRunId.Should().Be(200);
    }

    [Fact]
    public async Task Create_已配产品族但主档解析不出Code_拒绝()
    {
        // Arrange：域指向脏 Id（主档无该 Id → null），已配该维度用户不得因数据脏而放行（fail-closed）
        SetupFamilyDomainAndScope((DataScopeTypes.ProductFamily, "FRL"));
        _masterDataLookup.Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        SetupCreateHappyPath();

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ScopeViolationException>();
    }

    [Fact]
    public async Task Create_工厂维度已配但不命中_抛ScopeViolation()
    {
        // Arrange：FACTORY_FAMILY 域（FactoryId=7 → Code=BJ）；用户只授权 Factory=SUZ
        _domainDefinitionService
            .Setup(s => s.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DomainDefinitionInfo("D1", "FACTORY_FAMILY", 1, 7)]);
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies([(DataScopeTypes.Factory, "SUZ")]));
        _masterDataLookup.Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync("CYL");
        _masterDataLookup.Setup(m => m.GetFactoryCodeAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("BJ");
        SetupCreateHappyPath();

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ScopeViolationException>();
    }

    [Fact]
    public async Task Create_无默认策略版本_抛异常()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions(0);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_多默认策略版本歧义_抛异常()
    {
        // Arrange（红线 #4：歧义拒绝，不盲目取第一个）
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions(2);

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_合法_MANUAL_RESCHEDULE_冻结基线并审计()
    {
        // Arrange：Base ACTIVE + 唯一默认策略版本 + 原子写返回新 Run/壳
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _scheduleRunRepo.Setup(r => r.CreateCandidateRunAsync(
                It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandidateRunCreatedResult { NewScheduleRunId = 200, NewPlanVersionId = 300 });

        // Act
        var result = await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert：返回新 Run/壳 Id；冻结 RunType/Domain/Base/策略版本；写 CreateCandidateRun 审计
        result.NewScheduleRunId.Should().Be(200);
        result.NewPlanVersionId.Should().Be(300);
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.Is<CandidateRunCreateSpec>(s =>
                s.RunType == "MANUAL_RESCHEDULE"
                && s.DomainKey == "D1"
                && s.BasePlanVersionId == 50),
            It.IsAny<long>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l =>
                l.ActionCode == "CreateCandidateRun"
                && l.EntityType == "ScheduleRun"
                && l.EntityId == "200"
                && l.NewValue == "RUNNING"
                && l.UserCode == "u1"
                && l.Remark!.Contains("Purpose=MANUAL_ADJUSTMENT")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_合法_INSERT_ORDER_WHATIF_CTP_创建允许()
    {
        // Arrange（实施包 §十九：INSERT_ORDER_WHATIF+CTP 允许创建；激活由 ActivateCandidateAsync 拦截，已有测试覆盖）
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _scheduleRunRepo.Setup(r => r.CreateCandidateRunAsync(
                It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandidateRunCreatedResult { NewScheduleRunId = 210, NewPlanVersionId = 310 });

        // Act
        var result = await _service.CreateCandidateRunAsync(
            DaytimeSpec(runType: "INSERT_ORDER_WHATIF", purpose: "CTP"), 1, CancellationToken.None);

        // Assert
        result.NewScheduleRunId.Should().Be(210);
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.Is<CandidateRunCreateSpec>(s => s.RunType == "INSERT_ORDER_WHATIF" && s.Purpose == "CTP"),
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_合法_LOCAL_RESCHEDULE_INSERT_RESCHEDULE_成功()
    {
        // Arrange
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _scheduleRunRepo.Setup(r => r.CreateCandidateRunAsync(
                It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandidateRunCreatedResult { NewScheduleRunId = 220, NewPlanVersionId = 320 });

        // Act
        var result = await _service.CreateCandidateRunAsync(
            DaytimeSpec(runType: "LOCAL_RESCHEDULE", purpose: "INSERT_RESCHEDULE"), 1, CancellationToken.None);

        // Assert
        result.NewScheduleRunId.Should().Be(220);
        _scheduleRunRepo.Verify(r => r.CreateCandidateRunAsync(
            It.Is<CandidateRunCreateSpec>(s => s.RunType == "LOCAL_RESCHEDULE" && s.Purpose == "INSERT_RESCHEDULE"),
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_仓储原子写失败_异常向上传播且不写审计()
    {
        // Arrange：Run 或壳任一失败（mock 抛）→ 服务不吞异常、不写审计
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        SetupDefaultStrategyVersions();
        _scheduleRunRepo.Setup(r => r.CreateCandidateRunAsync(
                It.IsAny<CandidateRunCreateSpec>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("模拟原子写失败"));

        // Act
        var act = async () => await _service.CreateCandidateRunAsync(DaytimeSpec(), 1, CancellationToken.None);

        // Assert：异常向上传播；不产生审计（无孤立写入）
        await act.Should().ThrowAsync<InvalidOperationException>();
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== 缺口A（R11-3B）：确认 / 激活两路径的三维业务范围（2026-10-30 补齐） ====================
    // 背景：创建路径早已判 Domain + ProductFamily + Factory 三维，确认/激活两条路径原先只判 Domain 一维。
    // 0号位 R11-3b 要求「创建/激活 Candidate 时」三维无权限一律 403 ⇒ 这两条用例钉住补齐后的行为。

    /// <summary>铺「域 D1 → 产品族 Id=1 → Code=CYL」，用户授权 ProductFamily=FRL（不命中）。</summary>
    private void SetupDomainAndMismatchedProductFamilyScope()
    {
        _domainDefinitionService
            .Setup(s => s.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DomainDefinitionInfo("D1", "FAMILY", 1, null)]);
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.ProductFamily, "FRL"),
            ]));
        _masterDataLookup
            .Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync("CYL");
    }

    [Fact]
    public async Task Confirm_产品族维度越界_抛ScopeViolation且不写审计()
    {
        // Arrange
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        SetupDomainAndMismatchedProductFamilyScope();

        // Act
        var act = async () => await _service.ConfirmCandidateAsync(5, 1, "u1", null, CancellationToken.None);

        // Assert：与创建路径同口径——不能只判 Domain 一维
        await act.Should().ThrowAsync<ScopeViolationException>();
        _auditRepo.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never,
            "越界确认不得留下 ConfirmCandidate 审计（否则会变成激活的硬前置）");
    }

    [Fact]
    public async Task Activate_产品族维度越界_抛ScopeViolation且不改状态()
    {
        // Arrange
        var version = CandidateVersion(5, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        SetupDomainAndMismatchedProductFamilyScope();

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：越界在写状态之前拦住，PlanVersion 与同域 ACTIVE 都不动
        await act.Should().ThrowAsync<ScopeViolationException>();
        version.Status.Should().Be("CANDIDATE");
        _planVersionRepo.Verify(
            r => r.ReplaceActiveAsync(It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never, "越界激活不得触发同域 ACTIVE 的原子替换");
    }

    // ==================== R11-3b 对象级三维「真实变更范围」复核（0号位 裁决 v1.1）====================

    /// <summary>激活常见 Setup：版本 + 已确认 + 来源 Run 可激活（对象级复核后置通过则走通整激活链）</summary>
    private void SetupActivationBaseline(int planVersionId = 5)
    {
        var version = CandidateVersion(planVersionId, "D1");
        _planVersionRepo.Setup(r => r.GetByIdAsync(planVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _auditRepo.Setup(r => r.GetByEntityAsync("PlanVersion", planVersionId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfirmedLogs(planVersionId));
        _scheduleRunRepo.Setup(r => r.GetByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(CandidateRun(2));
        // 来源 Run 冻结 Base = 50，且同域当前 ACTIVE 就是 50（Base 未顶替，可激活）
        _planVersionRepo.Setup(r => r.GetByIdAsync(50, It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
        _planVersionRepo.Setup(r => r.GetActiveByDomainKeyAsync("D1", It.IsAny<CancellationToken>())).ReturnsAsync(ActiveBase());
    }

    [Fact]
    public async Task Activate_对象级部门未授权_抛ScopeViolation且不改状态()
    {
        // Arrange（切面3：真实变更范围——Task 实际落到未授权部门 → 激活前拒绝）
        SetupActivationBaseline();
        _domainDefinitionService
            .Setup(s => s.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DomainDefinitionInfo("D1", "FAMILY", 1, null)]);
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.Department, "DEP1"),
            ]));
        // 该版本实际 Task.ProductionDepartmentId=7 → 换算 Code=DEP OTHER，不在授权
        _overviewQueryRepo
            .Setup(r => r.GetPlanVersionObjectScopeDimensionsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersionObjectScopeDimensions
            {
                DepartmentIds = [7],
            });
        _masterDataLookup.Setup(m => m.GetDepartmentCodeAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("DEP_OTHER");

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：真实部门越权 → 激活前拒绝，不改状态、不原子替换
        await act.Should().ThrowAsync<ScopeViolationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_对象级工厂未授权_抛ScopeViolation()
    {
        // Arrange（切面2/6：真实 Order.FactoryId 越权 → 拒绝）
        SetupActivationBaseline();
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.Factory, "SUZ"),
            ]));
        _overviewQueryRepo
            .Setup(r => r.GetPlanVersionObjectScopeDimensionsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersionObjectScopeDimensions
            {
                FactoryIds = [7],          // 主档解析 BJ，不在 SUZ 授权
                ProductFamilyIds = [1],    // 未配族维 → 不构成约束
            });
        _masterDataLookup.Setup(m => m.GetFactoryCodeAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("BJ");

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ScopeViolationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_对象级全维度命中授权_正常激活()
    {
        // Arrange（切面1：合法局部 Candidate —— 工厂/族/部门全命中 → 放行）
        SetupActivationBaseline();
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.Factory, "SUZ"),
                (DataScopeTypes.ProductFamily, "CYL"),
                (DataScopeTypes.Department, "DEP1"),
            ]));
        _overviewQueryRepo
            .Setup(r => r.GetPlanVersionObjectScopeDimensionsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersionObjectScopeDimensions
            {
                FactoryIds = [7],
                ProductFamilyIds = [1],
                DepartmentIds = [9],
            });
        _masterDataLookup.Setup(m => m.GetFactoryCodeAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("SUZ");
        _masterDataLookup.Setup(m => m.GetProductFamilyCodeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync("CYL");
        _masterDataLookup.Setup(m => m.GetDepartmentCodeAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync("DEP1");

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：三维全命中 → 正常激活，替换同域 ACTIVE 一次 + 审计
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(r => r.AddAsync(
            It.Is<AuditLog>(l => l.ActionCode == "ActivateCandidate"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Activate_Global用户_对象级复核放行()
    {
        // Arrange（切面1反例：Global 全维度放行，不触发对象级取数）
        SetupActivationBaseline();
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.Global);

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：Global → 正常激活；对象级取数不应被调用（Global 早在 ResolveScope 即放行）
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _overviewQueryRepo.Verify(
            r => r.GetPlanVersionObjectScopeDimensionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "Global 用户不得触发对象级取数");
    }

    [Fact]
    public async Task Activate_对象级主档解析失败_抛ScopeViolation()
    {
        // Arrange（切面7）：真实对象指向脏主档（FactoryId=7 换算不出 Code）→ fail-closed 拒绝，不因数据脏而放行
        SetupActivationBaseline();
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.Factory, "SUZ"),
            ]));
        _overviewQueryRepo
            .Setup(r => r.GetPlanVersionObjectScopeDimensionsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersionObjectScopeDimensions { FactoryIds = [7] });
        _masterDataLookup
            .Setup(m => m.GetFactoryCodeAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);   // 脏主档：解析不出

        // Act
        var act = async () => await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：fail-closed，不改状态、不原子替换
        await act.Should().ThrowAsync<ScopeViolationException>();
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_对象级空集合_放行()
    {
        // Arrange（边界）：该版本无 Order/Task 维度可越权 → 空集合放行；同域 ACTIVE 替换放行
        SetupActivationBaseline();
        _dataScopeService
            .Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(
            [
                (DataScopeTypes.Domain, "D1"),
                (DataScopeTypes.Factory, "SUZ"),
                (DataScopeTypes.ProductFamily, "CYL"),
                (DataScopeTypes.Department, "DEP1"),
            ]));
        _overviewQueryRepo
            .Setup(r => r.GetPlanVersionObjectScopeDimensionsAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanVersionObjectScopeDimensions
            {
                FactoryIds = [],
                ProductFamilyIds = [],
                DepartmentIds = [],
                NullDepartmentTaskCount = 0,
            });

        // Act
        await _service.ActivateCandidateAsync(5, 1, "u1", CancellationToken.None);

        // Assert：空集 → 无对象可越权 → 正常激活（替换同域 ACTIVE 一次）
        _planVersionRepo.Verify(r => r.ReplaceActiveAsync(
            It.IsAny<PlanVersion>(), "u1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
