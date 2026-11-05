using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Core.DTOs.Governance;
using StrategyProfileVersion = LPS.APS.Core.Entities.APS.StrategyProfileVersion;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 夜间 Run 默认策略包解析（R12-O01，2026-10-30 r13979）——**接线层**单元测试。
///
/// 覆盖：夜间路径确实以 <see cref="StrategyProfileRunType.FullSchedule"/> 调仓储
/// <c>GetDefaultByRunTypeOrFallbackAsync</c>（与白天候选同一方法），以及 0／1／多 三态各自走到的分支。
///
/// **不覆盖**：仓储那段 SQL 的语义（精确优先 / NULL 次级 / 窗外不挡兜底 / 同层冲突）。
/// 此处仓储是 mock，返回什么由测试给定——SQL 语义只能实库夹具验（3号位 选定方案甲的可回滚脚本）。
/// 该边界已写入 2026-10-30《2号位致3号位 R12-O01 回执》§5.2，并经 3号位 2026-10-30 回件 §3.2 确认。
/// </summary>
public class ScheduleRunServiceDefaultResolutionTests
{
    private readonly Mock<IDomainDefinitionService> _domainDefinition = new();
    private readonly Mock<IStrategyProfileVersionRepository> _strategyProfileVersionRepo = new();
    private readonly Mock<DatabaseConnectionManager> _connection;

    /// <summary>INSERT 成功时返回的 Run Id（用于断言走完建壳）。</summary>
    private const int InsertedRunId = 4242;

    public ScheduleRunServiceDefaultResolutionTests()
    {
        _connection = CreateConnectionMock();

        // 当日无 RUNNING 复用行
        // 诊断查询（0 候选时才发）默认返回 NULL = 「治理未配置」，用例内按需覆写
        // 建壳 INSERT 返回固定 Id
        _connection
            .Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((string sql, object? _, CommandType _, DatabaseId _, int? _) =>
                sql.Contains("INSERT INTO ScheduleRun", StringComparison.Ordinal) ? InsertedRunId : 0);

        _connection
            .Setup(c => c.QueryFirstOrDefaultAsync<DateTime>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(new DateTime(2026, 10, 31, 0, 30, 0));

        _connection
            .Setup(c => c.QueryFirstOrDefaultAsync<string>(
                It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync((string? _1, object? _2, CommandType _3, DatabaseId _4, int? _5) => (string?)null);

        _domainDefinition
            .Setup(d => d.GetActiveDomainsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DomainDefinitionInfo>
            {
                new("BJ_Y", "FACTORY", 0, 1),
                new("SH_Y", "FACTORY", 0, 2)
            });
    }

    private static Mock<DatabaseConnectionManager> CreateConnectionMock()
    {
        var options = new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Production;" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=ODS_Production;" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Auth;" },
        };
        return new Mock<DatabaseConnectionManager>(Options.Create(options));
    }

    private ScheduleRunService CreateService() => new(
        _connection.Object,
        _domainDefinition.Object,
        _strategyProfileVersionRepo.Object,
        NullLogger<ScheduleRunService>.Instance);

    private void SetupDefaults(params StrategyProfileVersion[] versions) =>
        _strategyProfileVersionRepo
            .Setup(r => r.GetDefaultByRunTypeOrFallbackAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(versions);

    [Fact]
    public async Task CreateScheduleRunAsync_以FullSchedule调仓储_并采用唯一默认版本建壳()
    {
        SetupDefaults(new StrategyProfileVersion { Id = 1970, Status = "PUBLISHED", IsDefault = true });

        var id = await CreateService().CreateScheduleRunAsync();

        id.Should().Be(InsertedRunId);

        // 夜间路径必须与白天候选走同一个方法，且 RunType 用常量而非字面量
        _strategyProfileVersionRepo.Verify(
            r => r.GetDefaultByRunTypeOrFallbackAsync(
                StrategyProfileRunType.FullSchedule, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // 建壳 INSERT 的参数里带的是解析出来的那个版本
        _connection.Verify(
            c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("INSERT INTO ScheduleRun", StringComparison.Ordinal)),
                It.Is<object>(p => p.ToString()!.Contains("1970", StringComparison.Ordinal)),
                It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateScheduleRunAsync_当日已有RUNNING_直接复用_不解析默认()
    {
        _connection
            .Setup(c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("SELECT TOP 1 Id FROM ScheduleRun", StringComparison.Ordinal)),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(999333);

        var id = await CreateService().CreateScheduleRunAsync();

        id.Should().Be(999333);
        _strategyProfileVersionRepo.Verify(
            r => r.GetDefaultByRunTypeOrFallbackAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateScheduleRunAsync_两层都无可用默认_抛未配置文案_且不建壳()
    {
        SetupDefaults();

        var act = async () => await CreateService().CreateScheduleRunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*无默认 FULL_SCHEDULE 策略包版本*RunType IS NULL 的通用默认版本可兜底*");

        _connection.Verify(
            c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("INSERT INTO ScheduleRun", StringComparison.Ordinal)),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateScheduleRunAsync_精确默认全在窗外且无通用包_抛生效窗文案_并列出窗外版本()
    {
        SetupDefaults();

        _connection
            .Setup(c => c.QueryFirstOrDefaultAsync<string>(
                It.Is<string>(s => s.Contains("STRING_AGG", StringComparison.Ordinal)),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync("1970（生效窗 2026-10-14 ~ 未设终点）");

        var act = async () => await CreateService().CreateScheduleRunAsync();

        // 区分「治理未配置」与「有默认但生效窗未覆盖」——运维照报错直接能定位，不会去查一个本来就存在的默认版本
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FULL_SCHEDULE 默认策略包版本生效窗未覆盖当前时刻*1970*");
    }

    [Fact]
    public async Task CreateScheduleRunAsync_同层多个默认_抛歧义_不随机取一_且不建壳()
    {
        SetupDefaults(
            new StrategyProfileVersion { Id = 1970, Status = "PUBLISHED", IsDefault = true },
            new StrategyProfileVersion { Id = 478, Status = "PUBLISHED", IsDefault = true });

        var act = async () => await CreateService().CreateScheduleRunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*多个生效窗内的默认策略包版本*1970, 478*");

        _connection.Verify(
            c => c.QueryFirstOrDefaultAsync<int>(
                It.Is<string>(s => s.Contains("INSERT INTO ScheduleRun", StringComparison.Ordinal)),
                It.IsAny<object?>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()),
            Times.Never);
    }
}
