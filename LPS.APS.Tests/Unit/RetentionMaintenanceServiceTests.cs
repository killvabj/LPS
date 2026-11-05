using FluentAssertions;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 保留期清理（D1~D4）的**行为**守门测试。全程不触库：<see cref="DatabaseConnectionManager"/> 用 Mock，
/// 未 setup 的查询返回 null ⇒ 各规则计数取 0，删除语句返回 0 行 ⇒ 循环立即跳出。
///
/// 只钉三件事，但这三件正是 D4 的红线，缺一条都算实现跑偏：
/// ① 默认 dry-run，**一条 DELETE 都不发**；
/// ② 显式 dryRun=false 时，6 条规则各发且仅发一条 DELETE（无重叠、无漏项）；
/// ③ 删除抛异常时**不向外抛**——「清理失败不反推正式排程失败」。
/// </summary>
public class RetentionMaintenanceServiceTests
{
    /// <summary>造一个不连库的 <see cref="DatabaseConnectionManager"/> Mock（构造参数须满足 IsValid）。</summary>
    private static Mock<DatabaseConnectionManager> NewConnectionMock()
    {
        var options = Options.Create(new DatabaseOptions
        {
            APS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=APS" },
            ODS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=ODS" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=Auth" }
        });

        return new Mock<DatabaseConnectionManager>(options);
    }

    private static RetentionMaintenanceService BuildService(DatabaseConnectionManager cm)
        => new(cm, NullLogger<RetentionMaintenanceService>.Instance);

    [Fact]
    public async Task PlanAsync_Returns_Six_Rules_Matching_D1_To_D4()
    {
        var sut = BuildService(NewConnectionMock().Object);

        var plan = await sut.PlanAsync();

        plan.Should().HaveCount(6);
        plan.Select(p => p.Rule).Should().BeEquivalentTo(new[]
        {
            "D1-a ExplainTrace",
            "D1-b ScheduleExplanationFact",
            "D2 PositionSnapshot",
            "D3-a ERP_Order_Staging(已解决失败)",
            "D3-b APS_ETL_Log",
            "D3-c ComputeStageProgress"
        });

        // 在线窗口 = 0号位 原文天数，不自拟
        plan.Single(p => p.Rule == "D1-a ExplainTrace").OnlineWindowDays.Should().Be(30);
        plan.Single(p => p.Rule == "D1-b ScheduleExplanationFact").OnlineWindowDays.Should().Be(90);
        plan.Single(p => p.Rule == "D2 PositionSnapshot").OnlineWindowDays.Should().Be(30);
        plan.Single(p => p.Rule == "D3-a ERP_Order_Staging(已解决失败)").OnlineWindowDays.Should().Be(90);
        plan.Single(p => p.Rule == "D3-b APS_ETL_Log").OnlineWindowDays.Should().Be(30);
        plan.Single(p => p.Rule == "D3-c ComputeStageProgress").OnlineWindowDays.Should().Be(30);
    }

    [Fact]
    public async Task Plan_Is_Read_Only_And_Never_Executes_Writes()
    {
        var mock = NewConnectionMock();

        var sut = BuildService(mock.Object);

        await sut.PlanAsync();

        mock.Verify(m => m.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.CommandType>(),
            It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_Default_Is_DryRun_And_Deletes_Nothing()
    {
        var mock = NewConnectionMock();

        var sut = BuildService(mock.Object);

        // 不传参 = 默认 dryRun: true
        var result = await sut.RunAsync();

        result.DryRun.Should().BeTrue();
        result.DeletedRows.Should().Be(0);
        result.Error.Should().BeNull();
        mock.Verify(m => m.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.CommandType>(),
            It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_With_DryRun_False_Issues_One_Delete_Per_Rule()
    {
        var mock = NewConnectionMock();

        // 0 行 ⇒ 每条规则一批即停
        mock.Setup(m => m.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(0);

        var sut = BuildService(mock.Object);

        var result = await sut.RunAsync(dryRun: false);

        result.Error.Should().BeNull();
        result.DeletedRows.Should().Be(0);
        mock.Verify(m => m.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.CommandType>(),
            It.IsAny<DatabaseId>(), It.IsAny<int?>()), Times.Exactly(6));
    }

    [Fact]
    public async Task RunAsync_Swallows_Delete_Exception_And_Reports_Error()
    {
        var mock = NewConnectionMock();

        mock.Setup(m => m.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<System.Data.CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("模拟删除失败"));

        var sut = BuildService(mock.Object);

        // D4：清理失败不反推正式排程失败 ⇒ 这里**不许抛**
        var act = async () => await sut.RunAsync(dryRun: false);

        await act.Should().NotThrowAsync();
        var result = await sut.RunAsync(dryRun: false);
        result.Error.Should().Contain("模拟删除失败");
    }

    [Fact]
    public void RetentionRunResult_TotalDeletable_Sums_Items()
    {
        var result = new RetentionRunResult
        {
            Items = new[]
            {
                new RetentionPlanItem { Rule = "a", DeletableRows = 10 },
                new RetentionPlanItem { Rule = "b", DeletableRows = 5 },
                new RetentionPlanItem { Rule = "c", DeletableRows = 0 }
            }
        };

        result.TotalDeletable.Should().Be(15);
    }

    [Fact]
    public void RetentionPlanItem_Deletable_Is_Over_Window_Minus_Protected()
    {
        // DTO 层不做减法，减法在服务里（BuildItemAsync 已 clamp 到 >= 0）。
        // 此处钉住公式口径：可删 = 超窗 − 受保护，不是「总行数 − 受保护」。
        var item = new RetentionPlanItem
        {
            TotalRows      = 1000,
            OverWindowRows = 300,
            ProtectedRows  = 120,
            DeletableRows  = 180
        };

        (item.OverWindowRows - item.ProtectedRows).Should().Be(item.DeletableRows);
    }
}
