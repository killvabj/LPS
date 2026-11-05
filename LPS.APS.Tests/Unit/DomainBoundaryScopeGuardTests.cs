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
/// 缺口 A 边界层验收：域内实体级业务范围边界守卫（IDomainBoundaryScopeGuard）
/// 与入口闸（IDepartmentScopeGuard.EnsureAsync，交集）的区别：本层要求 <c>D ⊆ S</c> 且 <c>F ⊆ S</c>，
/// <b>任一条越界即整轮中止</b>——不允许「部分授权」升级成「全域写入」（2026-10-29 0号位 裁定）。
/// 覆盖：D⊆S 且 F⊆S 放行 / 任一部门越界拒 / 工厂越界拒 / 维度未配范围C放行 /
///       Global 放行 / 无 Run 哨兵跳过 / Id 解析不出 fail-closed / 系统主体不参与 / 主体无法解析。
/// 边界：纯内存，不连库不写库；不验证 2号位 装配侧接线（属 seam 交付后的联合验收）。
/// </summary>
public class DomainBoundaryScopeGuardTests
{
    private const long RunId = 12345L;
    private const string DomainKey = "DH1";
    private const string User = "zhangsan";

    private readonly Mock<IScheduleRunRepository> _runRepo = new();
    private readonly Mock<IDataScopeService> _scopeService = new();
    private readonly Mock<IMasterDataLookupRepository> _masterDataLookup = new();
    private readonly DepartmentScopeGuard _sut;

    public DomainBoundaryScopeGuardTests()
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

    private void SetupFactoryCode(int factoryId, string? factoryCode)
        => _masterDataLookup
            .Setup(x => x.GetFactoryCodeAsync(factoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(factoryCode);

    [Fact]
    public async Task EnsureBoundaryAsync_部门与工厂全部落入授权_放行()
    {
        // Arrange：D={11,12} 都在 S；F={21} 也在 S
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Department, "BJ116"),
            (DataScopeTypes.Factory, "SUZ")
        }));
        SetupDeptCode(11, "BJ115");
        SetupDeptCode(12, "BJ116");
        SetupFactoryCode(21, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11, 12 }, new[] { 21 });

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureBoundaryAsync_任一部门越界_抛边界码_不因另一部门命中而放行()
    {
        // Arrange：D={11,12}，仅 11 命中授权集 —— 这正是入口闸（交集）会放行、边界层必须拒绝的场景
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "BJ115") }));
        SetupDeptCode(11, "BJ115");
        SetupDeptCode(12, "BJ116");

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11, 12 }, Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.DepartmentBoundaryViolationCode);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_工厂越界_抛工厂边界码()
    {
        // Arrange：部门全命中，但工厂 SUZ 不在授权集（只授权 SH）
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupDeptCode(11, "BJ115");
        SetupFactoryCode(21, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11 }, new[] { 21 });

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_维度整体未配_按范围C放行()
    {
        // Arrange：用户只配了 Factory，未配 Department ⇒ Department 维度不构成约束
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Factory, "SUZ") }));
        SetupFactoryCode(21, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11, 12, 13 }, new[] { 21 });

        // Assert
        await act.Should().NotThrowAsync();
        _masterDataLookup.Verify(
            x => x.GetDepartmentCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_Global_放行且不解析主档()
    {
        // Arrange
        SetupRun(User);
        SetupScope(DataScopeContext.Global);

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11 }, new[] { 21 });

        // Assert
        await act.Should().NotThrowAsync();
        _masterDataLookup.Verify(
            x => x.GetDepartmentCodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_无Run哨兵_跳过判定()
    {
        // Arrange：0 为本仓既有成文哨兵（手动/联调入口）
        // Act
        var act = () => _sut.EnsureBoundaryAsync(0, DomainKey, new[] { 11 }, new[] { 21 });

        // Assert
        await act.Should().NotThrowAsync();
        _scopeService.Verify(
            x => x.ResolveScopeByUserCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_Id解析不出编码_边界fail_closed()
    {
        // Arrange：部门 12 在主档查不到 Code ⇒ 不放过无法证明的值
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "BJ115") }));
        SetupDeptCode(11, "BJ115");
        SetupDeptCode(12, null);

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11, 12 }, Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.DepartmentBoundaryViolationCode);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_主体无法解析_抛主体未解析码()
    {
        // Arrange：TriggeredBy 对应的用户不存在
        SetupRun(User);
        SetupScope(null);

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11 }, Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.ActorUnresolvedCode);
    }

    [Fact]
    public async Task EnsureBoundaryAsync_系统主体全量排程_不参与()
    {
        // Arrange：Hangfire 触发的 FULL_SCHEDULE 没有真实用户
        SetupRun("Hangfire", StrategyProfileRunType.FullSchedule);

        // Act
        var act = () => _sut.EnsureBoundaryAsync(RunId, DomainKey, new[] { 11 }, new[] { 21 });

        // Assert
        await act.Should().NotThrowAsync();
        _scopeService.Verify(
            x => x.ResolveScopeByUserCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== 按用途分组（EnsureBoundaryByPurposeAsync）====================
    // 契约 §7 E-1~E-6 六类正反例落成用例（2026-10-29）。
    // 核心命题：四组集合「分别」判定，组间不合并——任一用途组越界即拒，且文案指明是哪一组。

    /// <summary>E-1 需求归属 ≠ 可写生产：可写生产厂越界，即使需求厂合法也必须拒</summary>
    [Fact]
    public async Task E1_需求厂在授权内但可写生产厂越界_拒且文案指明可写生产工厂()
    {
        // Arrange：授权只有 SH；需求厂 21=SH ✔，可写生产厂 22=SUZ ✘
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(21, "SH");
        SetupFactoryCode(22, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            Array.Empty<int>(), new[] { 21 }, new[] { 22 }, Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
        ex.Which.Message.Should().Contain("可写生产工厂");
    }

    /// <summary>E-1 正例：需求厂与可写生产厂分属不同厂，但都在授权内 ⇒ 放行</summary>
    [Fact]
    public async Task E1_需求厂与可写生产厂不同但在授权内_放行()
    {
        // Arrange：授权含 SH 两座厂；需求 21=SH、可写 23=SH
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(21, "SH");
        SetupFactoryCode(23, "SH");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            Array.Empty<int>(), new[] { 21 }, new[] { 23 }, Array.Empty<int>());

        // Assert
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// E-2 跨厂 SH：需求归属工厂越界时，不因它是 SH 供给而豁免——
    /// 需求侧来源只认 Order.SourceFactoryId，不因供给形态改变授权判定。
    /// </summary>
    [Fact]
    public async Task E2_跨厂SH_需求厂越界_不因SH豁免且文案指明需求归属工厂()
    {
        // Arrange：可写生产厂 21=SH ✔，需求厂 22=SUZ ✘
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(21, "SH");
        SetupFactoryCode(22, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Commit,
            Array.Empty<int>(), new[] { 22 }, new[] { 21 }, Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
        ex.Which.Message.Should().Contain("需求归属工厂");
    }

    /// <summary>E-3 一个 Task 承接多个 Demand：多个需求厂里任一个越界即拒（全集子集，不取交集中任意一个）</summary>
    [Fact]
    public async Task E3_一Task多Demand_任一需求厂越界即拒()
    {
        // Arrange：需求厂 21=SH ✔ 与 22=SUZ ✘ 同时出现
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(21, "SH");
        SetupFactoryCode(22, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            Array.Empty<int>(), new[] { 21, 22 }, Array.Empty<int>(), Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
    }

    /// <summary>
    /// E-4 FAMILY 域 <c>FactoryId=NULL</c>：三组工厂全为空集<b>不得冒充全放行</b>——
    /// 部门维度的越界仍必须被拒，空集不是短路条件。
    /// </summary>
    [Fact]
    public async Task E4_工厂三组全空集_不得冒充全放行_部门越界仍拒()
    {
        // Arrange：授权只有 BJ115；本域部门含 12=BJ116 ✘，工厂全为空
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "BJ115") }));
        SetupDeptCode(11, "BJ115");
        SetupDeptCode(12, "BJ116");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            new[] { 11, 12 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.DepartmentBoundaryViolationCode);
    }

    /// <summary>E-4 正例：工厂全空、部门全在授权内 ⇒ 放行（空集不构成越界，也不扩权）</summary>
    [Fact]
    public async Task E4_工厂全空且部门全在授权内_放行()
    {
        // Arrange
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[] { (DataScopeTypes.Department, "BJ115") }));
        SetupDeptCode(11, "BJ115");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            new[] { 11 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

        // Assert
        await act.Should().NotThrowAsync();
    }

    /// <summary>E-5 只读跨厂 Supply：只读供给不构成写权限豁免，收货厂越界仍拒</summary>
    [Fact]
    public async Task E5_采购只读厂越界_只读不构成豁免且文案指明采购只读工厂()
    {
        // Arrange：授权只有 SH；采购收货厂 22=SUZ ✘
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(22, "SUZ");

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Commit,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), new[] { 22 });

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
        ex.Which.Message.Should().Contain("采购只读工厂");
    }

    /// <summary>E-6 未知部门/仓库工厂：Id 解析不出编码 ⇒ fail-closed，不放过无法证明的值</summary>
    [Fact]
    public async Task E6_工厂Id解析不出编码_fail_closed()
    {
        // Arrange：工厂 99 在主档查不到 Code
        SetupRun(User);
        SetupScope(DataScopeContext.FromPolicies(new[]
        {
            (DataScopeTypes.Department, "BJ115"),
            (DataScopeTypes.Factory, "SH")
        }));
        SetupFactoryCode(99, null);

        // Act
        var act = () => _sut.EnsureBoundaryByPurposeAsync(
            RunId, DomainKey, ScopeCheckPhase.Load,
            Array.Empty<int>(), new[] { 99 }, Array.Empty<int>(), Array.Empty<int>());

        // Assert
        var ex = await act.Should().ThrowAsync<ScopeViolationException>();
        ex.Which.Code.Should().Be(DepartmentScopeGuard.FactoryBoundaryViolationCode);
    }
}
