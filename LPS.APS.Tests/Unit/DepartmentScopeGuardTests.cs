using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 队列3 验收：部门维度业务范围守卫（IDepartmentScopeGuard）
/// 覆盖：D=∅ 放行且不读 Run / 无 Run（手动入口）跳过 / 系统主体不参与 / 范围C 未配放行 /
///       已配命中放行 / 已配不命中拒绝（DEPT_SCOPE_VIOLATION）/ Id 解析失败 fail-closed /
///       主体无法解析拒绝（DEPT_SCOPE_ACTOR_UNRESOLVED）/ Run 不存在 / Global 放行 / Id→Code 单次 memo。
/// 边界：纯内存判定，不连库、不写库；不验证 2号位 编排侧 catch/break（属 seam 交付时的验收）。
/// </summary>
public class DepartmentScopeGuardTests
{
    private const long RunId = 12345L;
    private const string DomainKey = "DH1";
    private const string User = "zhangsan";

    private readonly Mock<IScheduleRunRepository> _runRepo = new();
    private readonly Mock<IDataScopeService> _scopeService = new();
    private readonly Mock<IMasterDataLookupRepository> _masterDataLookup = new();
    private readonly DepartmentScopeGuard _sut;

    public DepartmentScopeGuardTests()
    {
        _sut = new DepartmentScopeGuard(
            _runRepo.Object,
            _scopeService.Object,
            _masterDataLookup.Object,
            NullLogger<DepartmentScopeGuard>.Instance);
    }

    private void SetupRun(string triggeredBy, string runType = StrategyProfileRunType.ManualReschedule)
        => _runRepo
            .Setup(x => x.GetByIdAsync((int)RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleRunGov
            {
                Id = (int)RunId,
                RunType = runType,
                TriggeredBy = triggeredBy
            });

    private void SetupScope(DataScopeContext? ctx)
        => _scopeService
            .Setup(x => x.ResolveScopeByUserCodeAsync(User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

    private void SetupDeptCode(int departmentId, string? deptCode)
        => _masterDataLookup
            .Setup(x => x.GetDepartmentCodeAsync(departmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deptCode);

    [Fact]
    public async Task EnsureAsync_D为空_放行且不读Run()
    {
        await _sut.EnsureAsync(RunId, DomainKey, Array.Empty<int>());

        _runRepo.Verify(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_无Run手动入口_跳过判定不抛异常()
    {
        // Arrange：手动/联调入口没有 ScheduleRun
        // Act
        var act = () => _sut.EnsureAsync(null, DomainKey, new[] { 11, 12 });

        // Assert：既不拒绝，也不解析主体
        await act.Should().NotThrowAsync();
        _scopeService.Verify(
            x => x.ResolveScopeByUserCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_RunId为零哨兵_跳过判定不抛异常()
    {
        // Arrange：手动/联调链路传 0（本仓既有成文哨兵），形参非 null
        // Act
        var act = () => _sut.EnsureAsync(0L, DomainKey, new[] { 11, 12 });

        // Assert：与 null 同义，跳过判定且不读 Run
        await act.Should().NotThrowAsync();
        _runRepo.Verify(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _scopeService.Verify(
            x => x.ResolveScopeByUserCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_RunId为负值_不跳过按主体未解析拒绝()
    {
        // Arrange：负值不是「无 Run」哨兵，属调用方缺陷
        // Act
        var act = () => _sut.EnsureAsync(-1L, DomainKey, new[] { 11 });

        // Assert：fail-closed，不静默放过
        var ex = (await act.Should().ThrowAsync<ScopeViolationException>()).Which;
        ex.Code.Should().Be(DepartmentScopeGuard.ActorUnresolvedCode);
    }

    [Fact]
    public async Task EnsureAsync_系统主体全量排程_Department维度不参与()
    {
        SetupRun("Hangfire", StrategyProfileRunType.FullSchedule);

        await _sut.EnsureAsync(RunId, DomainKey, new[] { 1, 2 });

        _scopeService.Verify(
            x => x.ResolveScopeByUserCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_未配Department维度_范围C不构成拒绝()
    {
        // Arrange：只配了 Factory 维度
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Factory, "F1") }));

        // Act
        await _sut.EnsureAsync(RunId, DomainKey, new[] { 11, 12 });

        // Assert：未配该维度时不查主档、不放行码、不拒绝
        _masterDataLookup.Verify(
            x => x.GetDepartmentCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_已配且命中_放行()
    {
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "D002") }));
        SetupDeptCode(11, "D001");
        SetupDeptCode(12, "D002");

        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11, 12 });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAsync_已配但全不命中_抛越权()
    {
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "D900") }));
        SetupDeptCode(11, "D001");
        SetupDeptCode(12, "D002");

        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11, 12 });

        var ex = (await act.Should().ThrowAsync<ScopeViolationException>()).Which;
        ex.Code.Should().Be(DepartmentScopeGuard.ViolationCode);
        ex.ScopeType.Should().Be(DataScopeTypes.Department);
    }

    [Fact]
    public async Task EnsureAsync_已配但Id解析不出Code_按fail_closed拒绝()
    {
        // Arrange：11 主档无行（解析失败），12 解析成功但不命中
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "D900") }));
        SetupDeptCode(11, null);
        SetupDeptCode(12, "D002");

        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11, 12 });

        var ex = (await act.Should().ThrowAsync<ScopeViolationException>()).Which;
        ex.Code.Should().Be(DepartmentScopeGuard.ViolationCode);
        ex.Value.Should().Contain("无法解析为 DeptCode");
    }

    [Fact]
    public async Task EnsureAsync_TriggeredBy无法解析为有效用户_抛主体未解析()
    {
        SetupRun("ghost.user");
        SetupScope(null);

        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11 });

        var ex = (await act.Should().ThrowAsync<ScopeViolationException>()).Which;
        ex.Code.Should().Be(DepartmentScopeGuard.ActorUnresolvedCode);
    }

    [Fact]
    public async Task EnsureAsync_Run不存在_抛主体未解析()
    {
        // Arrange：默认 mock 返回 null
        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11 });

        var ex = (await act.Should().ThrowAsync<ScopeViolationException>()).Which;
        ex.Code.Should().Be(DepartmentScopeGuard.ActorUnresolvedCode);
    }

    [Fact]
    public async Task EnsureAsync_Global范围_直接放行且不查主档()
    {
        SetupRun(User);
        SetupScope(DataScopeContext.Global);

        await _sut.EnsureAsync(RunId, DomainKey, new[] { 11, 12 });

        _masterDataLookup.Verify(
            x => x.GetDepartmentCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_授权值带前后空格_Trim归一后仍命中()
    {
        // Arrange：授权值带前后空格（历史脏数据），本域 Code 为 "D002"
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, " D002 ") }));
        SetupDeptCode(12, "D002");

        // Act
        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 12 });

        // Assert：归一后命中，不因空格误判越权
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAsync_授权值全为空白_视同未配置放行()
    {
        // Arrange：配置行存在但值为空白 ⇒ 等价于该维度未配置（范围C），不得拒
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "   ") }));
        SetupDeptCode(11, "D001");

        // Act
        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 11 });

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAsync_同Id重复出现_主档只查一次()
    {
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "D900") }));
        SetupDeptCode(7, "D007");

        var act = () => _sut.EnsureAsync(RunId, DomainKey, new[] { 7, 7, 7 });

        await act.Should().ThrowAsync<ScopeViolationException>();
        _masterDataLookup.Verify(
            x => x.GetDepartmentCodeAsync(7, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
