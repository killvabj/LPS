using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 定向补缺服务（乙方案 ①）—— 守卫「范围由调用方给」「默认不写库」「JSON 键名与 SP 契约一致」。
///
/// 背景（0号位 2026-11-02 v3.1 §2.2）：乙方案第①层是「圈定对象：由调用方给出本 Domain
/// 真实待排的 (MaterialId, StageCode)」。所以本服务最危险的失效方式不是算错部门，而是
/// **范围算大了**（变成全库口径）或**范围静默算成空**（SP 里 OPENJSON 解析不出来，
/// 于是「补缺成功、0 行」）。下面四条用例各钉一种。
/// </summary>
public class TargetedMaterialStageDeptContextServiceTests
{
    private static DatabaseOptions NewOptions() => new()
    {
        APS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=APS" },
        ODS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=ODS" },
        Auth = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=Auth" }
    };

    /// <summary>
    /// 造连接管理器 Mock：SP 返回一行统计，同时把调用参数（DynamicParameters）截下来供断言。
    /// </summary>
    private static Mock<DatabaseConnectionManager> NewConnectionMock(
        List<object> capturedParameters,
        TargetedMaterialStageDeptContextResultDto? returns = null)
    {
        var mock = new Mock<DatabaseConnectionManager>(Options.Create(NewOptions()));

        mock.Setup(m => m.QueryFirstOrDefaultAsync<TargetedMaterialStageDeptContextResultDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .Callback<string, object?, CommandType, DatabaseId, int?>(
                (_, p, _, _, _) => { if (p is not null) capturedParameters.Add(p); })
            .ReturnsAsync(returns ?? new TargetedMaterialStageDeptContextResultDto
            {
                TargetKeys      = 3,
                LayerOverride   = 1,
                LayerActivity1  = 1,
                LayerActivity0  = 1,
                IssueCount      = 0
            });

        return mock;
    }

    private static TargetedMaterialStageDeptContextService NewService(Mock<DatabaseConnectionManager> mock)
        => new(mock.Object, NullLogger<TargetedMaterialStageDeptContextService>.Instance);

    private static List<MaterialStageKey> ThreeKeys() => new()
    {
        new MaterialStageKey { MaterialId = 101, StageCode = "BJ_ASSY" },
        new MaterialStageKey { MaterialId = 202, StageCode = "CN_MACH" },
        new MaterialStageKey { MaterialId = 303, StageCode = "TJ_SURF" }
    };

    [Fact]
    public async Task 目标键为空_不调SP且返回错误()
    {
        var captured = new List<object>();
        var mock = NewConnectionMock(captured);
        var sut = NewService(mock);

        var result = await sut.RebuildAsync(Array.Empty<MaterialStageKey>());

        result.IsSuccess.Should().BeFalse("空范围必须报错，不能伪装成「补缺成功、0 行」");
        result.ErrorMessage.Should().Contain("目标键集合为空");

        mock.Verify(m => m.QueryFirstOrDefaultAsync<TargetedMaterialStageDeptContextResultDto>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()),
            Times.Never,
            "范围为空时一次 SP 都不该调 —— 调了就会在白跑一遍 3M 行 MSC");
    }

    [Fact]
    public async Task 不传dryRun时_默认只算不写()
    {
        var captured = new List<object>();
        var mock = NewConnectionMock(captured);
        var sut = NewService(mock);

        var result = await sut.RebuildAsync(ThreeKeys());

        var p = captured.Should().ContainSingle().Subject as DynamicParameters;
        p.Should().NotBeNull();
        p!.Get<bool>("@DryRun").Should().BeTrue("默认必须是 DryRun —— 没显式传 false 就不许写库");
        result.DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task 显式dryRun为false才写库()
    {
        var captured = new List<object>();
        var mock = NewConnectionMock(captured);
        var sut = NewService(mock);

        var result = await sut.RebuildAsync(ThreeKeys(), dryRun: false);

        var p = (DynamicParameters)captured.Should().ContainSingle().Subject;
        p.Get<bool>("@DryRun").Should().BeFalse();
        result.DryRun.Should().BeFalse();
    }

    [Fact]
    public async Task 目标键JSON的键名必须恰好是m和s()
    {
        var captured = new List<object>();
        var mock = NewConnectionMock(captured);
        var sut = NewService(mock);

        await sut.RebuildAsync(ThreeKeys());

        var p = (DynamicParameters)captured.Should().ContainSingle().Subject;
        var json = p.Get<string>("@TargetKeysJson");

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

        var items = doc.RootElement.EnumerateArray().ToList();
        items.Should().HaveCount(3, "一个键一行，不做去重合并以外的增删");

        foreach (var item in items)
        {
            var names = item.EnumerateObject().Select(o => o.Name).ToList();
            names.Should().BeEquivalentTo(new[] { "m", "s" },
                "SP 里写死 JSON_VALUE(v.value,'$.m') / '$.s'；键名一漂移就静默解析成空集合");

            item.GetProperty("m").ValueKind.Should().Be(JsonValueKind.Number);
            item.GetProperty("s").ValueKind.Should().Be(JsonValueKind.String);
        }

        items.Select(i => (i.GetProperty("m").GetInt32(), i.GetProperty("s").GetString()))
             .Should().BeEquivalentTo(new[] { (101, "BJ_ASSY"), (202, "CN_MACH"), (303, "TJ_SURF") });
    }

    [Fact]
    public async Task 空domainKey直接抛参数异常()
    {
        var captured = new List<object>();
        var sut = NewService(NewConnectionMock(captured));

        await FluentActions.Awaiting(() => sut.DeriveTargetKeysAsync("  "))
            .Should().ThrowAsync<ArgumentException>("空 DomainKey 会圈出一个空范围，属调用方错误");
    }

    [Fact]
    public void 未解析键数等于目标键减三层命中()
    {
        var r = new TargetedMaterialStageDeptContextResultDto
        {
            TargetKeys = 100, LayerOverride = 5, LayerActivity1 = 40, LayerActivity0 = 3
        };

        r.UnresolvedKeys.Should().Be(52);
    }
}
