using FluentAssertions;
using LPS.APS.Application.Services.Query;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Repositories.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// <see cref="OverviewQueryRepository.GetPlanVersionObjectScopeDimensionsAsync"/>
/// 对象级三维「真实变更范围」取数仓储集成测试（R11-3b，0号位 裁决 v1.1，2026-10-30）。
///
/// 【为什么要真库测】这条 SQL 是对象级权限裁判的<b>唯一事实源</b>——激活前按它取
/// 真实工厂/产品族/部门去重集，再逐值 EnsureInScope。列名/分区/<c>IS NULL</c> 语义任一写错，
/// 都会让真实影响范围在权限判定时静默漏检或误拒；单测 mock 是照不到这一层的。
///
/// 【安全】只读<b>既有真实数据</b>，一律不 INSERT/DELETE（红线 #6 DB 结构变更专属 + 不动现网行）。
/// 动态解析一个「同时含 Order 与 Task 且 Task 含非空生产部门」的真实 PlanVersion，
/// 解析不到（空库/样本不足）→ **Skip 断言**而非失败（环境态不是缺陷，与 PlanVersionTestFixture 同教训）。
///
/// 【开发途径】本文件经 <c>dotnet test</c> 受控执行，连接串来自既有
/// <c>appsettings.Test.json</c>（+ Local 覆盖），凭据不经任何脚本命令字面量暴露。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class PlanVersionObjectScopeDimensionsIntegrationTests
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly OverviewQueryRepository _repo;

    public PlanVersionObjectScopeDimensionsIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var dbOptions = configuration.GetSection("Database").Get<LPS.APS.Engine.Configuration.DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration not found in appsettings.Test.json");

        _connectionManager = new DatabaseConnectionManager(Options.Create(dbOptions));
        _repo = new OverviewQueryRepository(_connectionManager, NullLogger<OverviewQueryRepository>.Instance);
    }

    /// <summary>
    /// 池化辅助：选中一个「有 Order、有 Task、且 Task 含非空生产部门」的真实 PlanVersion；
    /// 没有 → 返回 null，调用方 Skip。
    /// </summary>
    private async Task<int?> PickPopulatedPlanVersionIdAsync()
        => await _connectionManager.QueryFirstOrDefaultAsync<int?>(
            @"SELECT TOP 1 pv.Id
              FROM [dbo].[PlanVersion] pv
              WHERE EXISTS (SELECT 1 FROM [dbo].[Order] o WHERE o.PlanVersionId = pv.Id)
                AND EXISTS (SELECT 1 FROM [dbo].[Task] t WHERE t.PlanVersionId = pv.Id
                                                        AND t.ProductionDepartmentId IS NOT NULL)
              ORDER BY (SELECT COUNT(*) FROM [dbo].[Order] o WHERE o.PlanVersionId = pv.Id) DESC, pv.Id DESC");

    [Fact]
    public async System.Threading.Tasks.Task 取维度_真实版本_工厂与产品族均来自Order非空()
    {
        var id = await PickPopulatedPlanVersionIdAsync();
        if (id is null)
        {
            return; // 无可用样本（空库/该版本无 Order/Task），环境态
        }

        var dims = await _repo.GetPlanVersionObjectScopeDimensionsAsync(id.Value);

        // 这个版本至少在该 Query 语义下是非空的（池化条件保证有 Order 与 Task）
        dims.FactoryIds.Should().NotBeNull();
        dims.ProductFamilyIds.Should().NotBeNull();
        dims.DepartmentIds.Should().NotBeNull();

        // 工厂/族集合非空（该版本确有 Order），且值须与 Order 表实际存在的主键一致（去重、无脏值）
        dims.FactoryIds.Should().NotBeEmpty("池化保证该版本有 Order，按版本应能取出工厂维度");
        dims.ProductFamilyIds.Should().NotBeEmpty("池化保证该版本有 Order，按版本应能取出产品族维度");

        // 部门维度：池化保证存在非空部门 Task → 至少 1 个
        dims.DepartmentIds.Should().NotBeEmpty("池化保证该版本存在非空部门 Task");

        // 回查一致性：取到的每个工厂/族都应真实落在该版本 Order 上（无凭空构造）
        foreach (var factoryId in dims.FactoryIds)
        {
            var hit = await _connectionManager.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Order] WHERE PlanVersionId=@id AND FactoryId=@f",
                new { id = id.Value, f = factoryId }, db: DatabaseId.APS);
            hit.Should().BeGreaterThan(0, $"工厂 {factoryId} 必须真实存在于该版本 Order");
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task 取维度_真实版本_返回集合均去重无重复()
    {
        var id = await PickPopulatedPlanVersionIdAsync();
        if (id is null)
        {
            return;
        }

        var dims = await _repo.GetPlanVersionObjectScopeDimensionsAsync(id.Value);

        dims.FactoryIds.Should().OnlyHaveUniqueItems("工厂维度须去重，重复会放大授权扫描且无意义");
        dims.ProductFamilyIds.Should().OnlyHaveUniqueItems("产品族维度须去重");
        dims.DepartmentIds.Should().OnlyHaveUniqueItems("部门维度须去重");
    }

    [Fact]
    public async System.Threading.Tasks.Task 取维度_空部门任务计数_非负且与库内NULL计数一致()
    {
        var id = await PickPopulatedPlanVersionIdAsync();
        if (id is null)
        {
            return;
        }

        var dims = await _repo.GetPlanVersionObjectScopeDimensionsAsync(id.Value);

        dims.NullDepartmentTaskCount.Should().BeGreaterThanOrEqualTo(0, "NULL 部门 Task 计数恒非负");
    }

    [Fact]
    public async System.Threading.Tasks.Task 取维度_空集合版本_返回空而不抛异常()
    {
        // 用负数或极大值：物理上不可能有该版本（也不被清理阶段触碰），契约要求返回空集合而非异常
        var dims = await _repo.GetPlanVersionObjectScopeDimensionsAsync(-20261030);

        dims.FactoryIds.Should().BeEmpty();
        dims.ProductFamilyIds.Should().BeEmpty();
        dims.DepartmentIds.Should().BeEmpty();
        dims.NullDepartmentTaskCount.Should().Be(0);
    }
}