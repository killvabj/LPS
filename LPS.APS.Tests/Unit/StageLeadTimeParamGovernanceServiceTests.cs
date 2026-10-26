using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 阶段提前期参数治理服务单元测试（StageLeadTimeParam，3号位 规则参数体系）。
/// 覆盖：构造空值守卫、ListAsync 透传与过滤条件拼接、Create/Update/Deactivate 的输入校验红线
/// （FactoryCode/StageCode 非空、提前期非负且不同时为 0、Priority 非负、EffectiveTo ≥ EffectiveFrom）。
/// 说明：Create/Update/Deactivate 的 DB 写库 happy path 依赖 DatabaseConnectionManager 的非 virtual
/// QueryFirstOrDefaultAsync/ExecuteAsync，须由真实库集成测试覆盖，本单测不企及（见类注释边界）。
/// </summary>
public class StageLeadTimeParamGovernanceServiceTests
{
    private readonly Mock<IAuditLogRepository> _auditRepo = new();
    private readonly Mock<IDataScopeService> _dataScope = new();

    private static Mock<DatabaseConnectionManager> CreateConnectionMock()
    {
        var options = new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Production;" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=MES_Integration;" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Auth;" },
        };
        return new Mock<DatabaseConnectionManager>(Options.Create(options));
    }

    /// <summary>构造服务；默认 Scope=Global（全放行），越权用例在测试内单独 Setup。</summary>
    private StageLeadTimeParamGovernanceService CreateService(Mock<DatabaseConnectionManager> connection)
    {
        _dataScope.Reset();
        _dataScope.Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.Global);
        return new(connection.Object, _auditRepo.Object, _dataScope.Object, Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());
    }

    private static SaveStageLeadTimeParamRequest Request(
        string factoryCode = "BJ",
        string stageCode = "BJ_ASSY",
        decimal leadTimeDays = 1,
        decimal leadTimeHours = 0,
        int priority = 0,
        DateTime? effectiveFrom = null,
        DateTime? effectiveTo = null,
        bool isDefault = false)
        => new()
        {
            FactoryCode = factoryCode,
            StageCode = stageCode,
            LeadTimeDays = leadTimeDays,
            LeadTimeHours = leadTimeHours,
            Priority = priority,
            EffectiveFrom = effectiveFrom ?? new DateTime(2026, 1, 1),
            EffectiveTo = effectiveTo,
            IsDefault = isDefault,
        };

    // ---------- 构造守卫 ----------

    [Fact]
    public void 构造_connectionManager为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            null!, _auditRepo.Object, Mock.Of<IDataScopeService>(), Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("connectionManager");
    }

    [Fact]
    public void 构造_auditLogRepository为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            CreateConnectionMock().Object, null!, Mock.Of<IDataScopeService>(), Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("auditLogRepository");
    }

    [Fact]
    public void 构造_dataScopeService为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            CreateConnectionMock().Object, _auditRepo.Object, null!, Mock.Of<ILogger<StageLeadTimeParamGovernanceService>>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("dataScopeService");
    }

    [Fact]
    public void 构造_logger为null_抛ArgumentNullException()
    {
        Action act = () => new StageLeadTimeParamGovernanceService(
            CreateConnectionMock().Object, _auditRepo.Object, Mock.Of<IDataScopeService>(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ---------- ListAsync ----------

    [Fact]
    public async Task ListAsync_返回行()
    {
        var rows = new List<StageLeadTimeParamDto>
        {
            new() { Id = 1, FactoryCode = "BJ", StageCode = "BJ_ASSY", IsActive = true },
            new() { Id = 2, FactoryCode = "BJ", StageCode = "BJ_FINAL", IsActive = true },
        };
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(rows);

        var result = await CreateService(conn).ListAsync(null, null, null, null, actorUserId: 1);

        result.Should().HaveCount(2);
        result[0].StageCode.Should().Be("BJ_ASSY");
        result[1].StageCode.Should().Be("BJ_FINAL");
    }

    [Fact]
    public async Task ListAsync_带过滤_拼入对应条件()
    {
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<StageLeadTimeParamDto>());

        await CreateService(conn).ListAsync("BJ", "BJ_ASSY", "DEPT-1", true, actorUserId: 1);

        conn.Verify(c => c.QueryAsync<StageLeadTimeParamDto>(
            It.Is<string>(s => s.Contains("AND FactoryCode = @FactoryCode")
                            && s.Contains("AND StageCode = @StageCode")
                            && s.Contains("AND ProductionDeptCode = @ProductionDeptCode")
                            && s.Contains("AND IsActive = @IsActive")),
            It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }

    // ---------- P0-03 Business Scope（0号位 审核 2026-10-09） ----------

    [Fact]
    public async Task ListAsync_非Global工厂与部门授权_拼入IN条件()
    {
        // P0-03：非 Global 用户按 Factory/Department 授权集合过滤（Auth §9.3 禁止先全量再前端隐藏）。
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<StageLeadTimeParamDto>());

        var svc = CreateService(conn);
        _dataScope.Reset();
        _dataScope.Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(new[]
            {
                (DataScopeTypes.Factory, "BJ"),
                (DataScopeTypes.Department, "DEPT-1"),
            }));

        await svc.ListAsync(null, null, null, null, actorUserId: 1);

        conn.Verify(c => c.QueryAsync<StageLeadTimeParamDto>(
            It.Is<string>(s => s.Contains("AND FactoryCode IN @Factories")
                            && s.Contains("AND (ProductionDeptCode IS NULL OR ProductionDeptCode IN @Depts)")),
            It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_无任何授权_返回空列表且不查库()
    {
        // P0-03：无任何业务范围 → fail-closed 空列表（禁止泄露其它工厂/部门数据）。
        var conn = CreateConnectionMock();
        var svc = CreateService(conn);
        _dataScope.Reset();
        _dataScope.Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.Empty);

        var result = await svc.ListAsync(null, null, null, null, actorUserId: 1);

        result.Should().BeEmpty();
        conn.Verify(c => c.QueryAsync<StageLeadTimeParamDto>(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_工厂越权_抛ScopeViolationException()
    {
        // P0-03：Factory=SH 越出授权（仅 BJ）→ 403（写路径 fail-closed）。
        var svc = CreateService(CreateConnectionMock());
        _dataScope.Reset();
        _dataScope.Setup(s => s.ResolveScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Factory, "BJ") }));

        Func<Task> act = () => svc.CreateAsync(Request(factoryCode: "SH"), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ScopeViolationException>();
    }

    // ---------- Create 校验红线 ----------

    [Fact]
    public async Task CreateAsync_FactoryCode为空_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(factoryCode: ""), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("FactoryCode");
    }

    [Fact]
    public async Task CreateAsync_StageCode为空_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(stageCode: "  "), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("StageCode");
    }

    [Fact]
    public async Task CreateAsync_LeadTimeDays为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeDays: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_LeadTimeHours为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeHours: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_提前期同时为0_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(leadTimeDays: 0, leadTimeHours: 0), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_Priority为负_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(Request(priority: -1), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_EffectiveTo早于EffectiveFrom_抛红线异常()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.CreateAsync(
            Request(effectiveFrom: new DateTime(2026, 1, 2), effectiveTo: new DateTime(2026, 1, 1)),
            1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    // ---------- Update / Deactivate 守卫 ----------

    [Fact]
    public async Task UpdateAsync_Id非正_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.UpdateAsync(0, Request(), 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public async Task DeactivateAsync_Id非正_抛ArgumentException()
    {
        var svc = CreateService(CreateConnectionMock());
        Func<Task> act = () => svc.DeactivateAsync(0, 1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("id");
    }

    // ---------- R09-V-A（3-P5）IsDefault 全局唯一 / 生效窗冲突校验 ----------
    // 语义：IsDefault=1 为跨工厂全局兜底（GLOBAL_STAGE_DEFAULT），生效窗闭区间重叠即冲突。
    // 纯谓词锁定语义（DefaultWindowsOverlap 抽出为可测纯函数）；DB 装载路径经 mock 覆盖红线触发。

    [Fact]
    public void DefaultWindowsOverlap_窗口重叠_返回true()
    {
        // 目标 [1/1,1/31] 与候选 [1/15,1/20] 内含 → 冲突
        StageLeadTimeParamGovernanceService.DefaultWindowsOverlap(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31),
            new DateTime(2026, 1, 15), new DateTime(2026, 1, 20))
            .Should().BeTrue();
    }

    [Fact]
    public void DefaultWindowsOverlap_窗口相离_返回false()
    {
        // 目标 [1/1,1/10] 与候选 [1/11,1/20] 严格相离 → 合法
        StageLeadTimeParamGovernanceService.DefaultWindowsOverlap(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 10),
            new DateTime(2026, 1, 11), new DateTime(2026, 1, 20))
            .Should().BeFalse();
    }

    [Fact]
    public void DefaultWindowsOverlap_端点相接_返回true()
    {
        // 目标 [1/1,1/10] 与候选 [1/10,1/20] 端点相接同一点 → 闭区间视为重叠（与 Q8-2 口径一致）
        StageLeadTimeParamGovernanceService.DefaultWindowsOverlap(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 10),
            new DateTime(2026, 1, 10), new DateTime(2026, 1, 20))
            .Should().BeTrue();
    }

    [Fact]
    public void DefaultWindowsOverlap_无界目标窗_返回true()
    {
        // 目标 [1/1,∞) 覆盖后任意 → 冲突
        StageLeadTimeParamGovernanceService.DefaultWindowsOverlap(
            new DateTime(2026, 1, 1), null,
            new DateTime(2027, 6, 1), new DateTime(2027, 6, 30))
            .Should().BeTrue();
    }

    [Fact]
    public void DefaultWindowsOverlap_无界候选窗_返回true()
    {
        // 候选 [1/5,∞) 内含目标 [1/1,1/10] → 冲突
        StageLeadTimeParamGovernanceService.DefaultWindowsOverlap(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 10),
            new DateTime(2026, 1, 5), null)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_IsDefault存在重叠全局默认_抛红线异常()
    {
        // R09-V-A：Create IsDefault=true 时须查 active 的 IsDefault=1 候选，存在生效窗重叠 → 拒绝
        var conn = CreateConnectionMock();
        conn.Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(0); // EnsureNotDuplicate 不命中
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<StageLeadTimeParamDto>
            {
                new() { Id = 9, EffectiveFrom = new DateTime(2026, 2, 1), EffectiveTo = new DateTime(2026, 5, 1), IsDefault = true, IsActive = true },
            });
        var svc = CreateService(conn);

        Func<Task> act = () => svc.CreateAsync(
            Request(effectiveFrom: new DateTime(2026, 3, 1), effectiveTo: new DateTime(2026, 6, 1), isDefault: true),
            1, "u", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_IsDefault无重叠全局默认_不触红线()
    {
        // R09-V-A：候选生效窗相离 → 合法放行（红线不触发，落 INSERT 回带 Id）
        var conn = CreateConnectionMock();
        conn.SetupSequence(c => c.QueryFirstOrDefaultAsync<int>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(0)   // 第 1 跳 EnsureNotDuplicate → 不重复
            .ReturnsAsync(9);  // 第 2 跳 INSERT → 回带 Id
        conn.Setup(c => c.QueryAsync<StageLeadTimeParamDto>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<StageLeadTimeParamDto>
            {
                new() { Id = 9, EffectiveFrom = new DateTime(2026, 9, 1), EffectiveTo = new DateTime(2026, 12, 1), IsDefault = true, IsActive = true },
            });
        var svc = CreateService(conn);

        var created = await svc.CreateAsync(
            Request(effectiveFrom: new DateTime(2026, 3, 1), effectiveTo: new DateTime(2026, 6, 1), isDefault: true),
            1, "u", CancellationToken.None);

        created.Id.Should().Be(9); // INSERT 第 2 跳回带 Id
        created.IsDefault.Should().BeTrue();
    }
}