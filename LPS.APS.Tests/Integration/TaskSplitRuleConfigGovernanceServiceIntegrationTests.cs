using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.Exceptions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// TaskSplitRuleConfig 治理服务集成测试（4号位 P0：Dapper nullable 字段 DBNull → 500，2026-10-08）。
/// 复现 4号位 函件触发 A/B：任意 nullable 字段为 null（含 productionDepartmentId=null）时 POST/PUT
/// 不再抛 NotSupportedException——修复前 DynamicParameters.Add(name, (object?)X ?? DBNull.Value) 无 DbType，
/// Dapper 编译阶段抛「The member X of type System.DBNull cannot be used as a parameter value」。
/// 直连真实 APS_Production 库（与 4号位 dev 库 curl 同路径）；测试物料 = 运行时挑「尚无规则的既有 Material」
/// （满足 FK→Material.Id，且保证首建不触发去重）；Dispose 仅清理自建数据。
/// 开发者：3号位
/// </summary>
[Collection("TaskSplitRuleConfigGovernance")]
public class TaskSplitRuleConfigGovernanceServiceIntegrationTests : IDisposable
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly List<AuditLog> _auditLogs = new();
    private readonly TaskSplitRuleConfigGovernanceService _service;
    private readonly Mock<IDataScopeService> _dataScope;
    private int _testMaterialId;
    private readonly List<int> _pagingMaterialIds = new();

    public TaskSplitRuleConfigGovernanceServiceIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var dbOptions = configuration.GetSection("Database").Get<LPS.APS.Engine.Configuration.DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration not found in appsettings.Test.json");

        _connectionManager = new DatabaseConnectionManager(Options.Create(dbOptions));

        var auditRepo = new Mock<IAuditLogRepository>();
        auditRepo.Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditLog entity, CancellationToken _) => { _auditLogs.Add(entity); return entity; });

        // A2：默认放行任意 Department Scope（越界用例在测试内单独 Setup 抛 ScopeViolationException）。
        _dataScope = new Mock<IDataScopeService>();
        _dataScope.Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new TaskSplitRuleConfigGovernanceService(_connectionManager, auditRepo.Object, _dataScope.Object);
    }

    /// <summary>挑一个「尚无 TaskSplitRuleConfig 规则」的既有 Material（FK→Material.Id 必须存在；且无既有规则保证首建不触发去重）。</summary>
    private async Task<int> PickFreeMaterialIdAsync()
    {
        const string sql = @"
SELECT TOP 1 m.Id
FROM Material m
LEFT JOIN TaskSplitRuleConfig t ON t.MaterialId = m.Id
WHERE t.Id IS NULL
ORDER BY m.Id";

        var id = await _connectionManager.QueryFirstOrDefaultAsync<int>(sql, null, db: DatabaseId.APS);
        if (id <= 0)
        {
            throw new InvalidOperationException("APS_Production.Material 无可用（无既有 TaskSplitRuleConfig 规则的）物料，集成测试无法运行。");
        }
        return id;
    }

    /// <summary>取任一既有生产部门（FK→ProductionDepartment.Id 须存在；v5.1.10 收口④后治理写路径须明确部门）。</summary>
    private async Task<int> PickAnyDepartmentIdAsync()
    {
        const string sql = "SELECT TOP 1 Id FROM ProductionDepartment ORDER BY Id";
        var id = await _connectionManager.QueryFirstOrDefaultAsync<int?>(sql, null, db: DatabaseId.APS);
        if (id is null || id.Value <= 0)
        {
            throw new InvalidOperationException("APS_Production.ProductionDepartment 无数据，集成测试无法运行。");
        }
        return id.Value;
    }

    [Fact]
    public async Task CreateAsync_全部nullable字段null_不抛NotSupported_成功入库()
    {
        // 4号位 函件 §二 触发 A+B 回归（Dapper DBNull → NotSupportedException）：Min/Max/Effective 等 nullable 字段为 null 不再抛。
        // v5.1.10 收口③④后：ProductionDepartmentId 须明确、PreferredBatchQty 必填且 >0，业务校验已接管该语义。
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = null,
            PreferredBatchQty = 50m,
            AllowSplit = true,
            AllowMerge = true,
            EffectiveFrom = null,
            EffectiveTo = null,
        };

        var dto = await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        dto.Id.Should().BeGreaterThan(0);
        dto.ProductionDepartmentId.Should().Be(deptId);
        dto.MinExecutionBatchQty.Should().BeNull();
        _auditLogs.Should().ContainSingle(x => x.ActionCode == "Create");
    }

    [Fact]
    public async Task CreateAsync_NULL部门规则_业务校验拒绝()
    {
        // v5.1.10 收口④：NULL 部门仅历史兼容，治理写路径拒绝新增 NULL 部门规则（不默认为所有部门生效）。
        _testMaterialId = await PickFreeMaterialIdAsync();

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = null,
            PreferredBatchQty = 50m,
            AllowSplit = true,
            AllowMerge = true,
        };

        var act = async () => await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);
        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_PreferredBatchQty非法_业务校验拒绝()
    {
        // v5.1.10 收口③：PreferredBatchQty 业务生效必填且 >0、处于硬 Min/Max 内（后端发布校验承担）。
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();

        // 未提供 Preferred → 拒绝
        var missing = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            AllowSplit = true,
            AllowMerge = true,
        };
        await ((Func<Task>)(async () => await _service.CreateAsync(missing, 1, "test", CancellationToken.None)))
            .Should().ThrowAsync<SetupRuleDataRedLineException>();

        // Preferred=0 → 拒绝
        var zero = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            PreferredBatchQty = 0m,
            AllowSplit = true,
            AllowMerge = true,
        };
        await ((Func<Task>)(async () => await _service.CreateAsync(zero, 1, "test", CancellationToken.None)))
            .Should().ThrowAsync<SetupRuleDataRedLineException>();

        // Preferred 超出硬 Min/Max → 拒绝（Min=10 / Max=50 / Preferred=100）
        var outOfRange = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            MinExecutionBatchQty = 10m,
            MaxExecutionBatchQty = 50m,
            PreferredBatchQty = 100m,
            AllowSplit = true,
            AllowMerge = true,
        };
        await ((Func<Task>)(async () => await _service.CreateAsync(outOfRange, 1, "test", CancellationToken.None)))
            .Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_同Material同Dept重复_命中去重_抛去重异常()
    {
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            PreferredBatchQty = 50m,
            AllowSplit = true,
            AllowMerge = true,
        };

        await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        // 第二次同 (Material, Dept)（无界时间窗）→ 与既有有效规则窗口重叠，A1 唯一有效期拦截（v5.1.10 收口③/④，显式部门分支）。
        var act = async () => await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);
        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_同键非重叠时间窗_顺序生效_允许创建()
    {
        // A1 唯一有效期：同一 (Material+Dept) 顺序非重叠窗口允许（[1/1-6/30] → [7/1-12/31]），不触发重复/冲突。
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();
        var year = DateTime.UtcNow.Year;

        await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = deptId,
                PreferredBatchQty = 50m,
                AllowSplit = true,
                AllowMerge = true,
                EffectiveFrom = new DateTime(year, 1, 1),
                EffectiveTo = new DateTime(year, 6, 30),
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        var second = await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = deptId,
                PreferredBatchQty = 60m,
                AllowSplit = true,
                AllowMerge = true,
                EffectiveFrom = new DateTime(year, 7, 1),
                EffectiveTo = new DateTime(year, 12, 31),
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        second.EffectiveFrom.Should().Be(new DateTime(year, 7, 1));
        second.Id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAsync_同键重叠时间窗_唯一有效期冲突_拒绝()
    {
        // A1 唯一有效期：新窗口 [3/1-9/30] 与既有有效规则 [1/1-12/31] 重叠 → 拒绝。
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();
        var year = DateTime.UtcNow.Year;

        await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = deptId,
                PreferredBatchQty = 50m,
                AllowSplit = true,
                AllowMerge = true,
                EffectiveFrom = new DateTime(year, 1, 1),
                EffectiveTo = new DateTime(year, 12, 31),
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        var act = async () => await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = deptId,
                PreferredBatchQty = 50m,
                AllowSplit = true,
                AllowMerge = true,
                EffectiveFrom = new DateTime(year, 3, 1),
                EffectiveTo = new DateTime(year, 9, 30),
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        await act.Should().ThrowAsync<SetupRuleDataRedLineException>();
    }

    [Fact]
    public async Task CreateAsync_部门超出用户DepartmentScope_越界拒绝()
    {
        // A2 Department Scope：服务经部门码 EnsureInScopeAsync，越界抛 ScopeViolationException（Controller 映射 403）。
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();

        var deptCode = await _connectionManager.QueryFirstOrDefaultAsync<string?>(
            "SELECT DeptCode FROM ProductionDepartment WHERE Id = @Id",
            new { Id = deptId }, db: DatabaseId.APS);
        deptCode.Should().NotBeNullOrWhiteSpace();

        _dataScope.Setup(s => s.EnsureInScopeAsync(It.IsAny<int>(), DataScopeTypes.Department, deptCode!, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ScopeViolationException(DataScopeTypes.Department, deptCode!));

        var input = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            PreferredBatchQty = 50m,
            AllowSplit = true,
            AllowMerge = true,
        };

        var act = async () => await _service.CreateAsync(input, actorUserId: 1, actorUserCode: "test", CancellationToken.None);
        await act.Should().ThrowAsync<ScopeViolationException>();
    }

    [Fact]
    public async Task UpdateAsync_部分nullable字段null_成功更新()
    {
        _testMaterialId = await PickFreeMaterialIdAsync();
        var deptId = await PickAnyDepartmentIdAsync();

        var created = await _service.CreateAsync(
            new SaveTaskSplitRuleConfigRequest
            {
                MaterialId = _testMaterialId,
                ProductionDepartmentId = deptId,
                MinExecutionBatchQty = 1,
                MaxExecutionBatchQty = 2000,
                PreferredBatchQty = 100m,
                AllowSplit = true,
                AllowMerge = true,
            }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        var update = new SaveTaskSplitRuleConfigRequest
        {
            MaterialId = _testMaterialId,
            ProductionDepartmentId = deptId,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = null,
            PreferredBatchQty = 500m,
            AllowSplit = false,
            AllowMerge = false,
            EffectiveFrom = null,
            EffectiveTo = null,
        };

        var dto = await _service.UpdateAsync(created.Id, update, actorUserId: 1, actorUserCode: "test", CancellationToken.None);

        dto.AllowSplit.Should().BeFalse();
        dto.ProductionDepartmentId.Should().Be(deptId);
        _auditLogs.Should().ContainSingle(x => x.ActionCode == "Update");
    }

    [Fact]
    public async Task ListAsync_分页契约_pageSize截断与越界与归1()
    {
        // R2 标准分页契约（4号位 2026-10-08 提请，方案 A）：pageSize=2 → items.length=2 / total≥items.length / page=1 / pageSize=2；
        // pageSize=9999 → 截断 200；pageIndex 超末页 → items 空数组但 total 真实；pageIndex<1 → 归 1。
        // 建 3 条不同 Material（去重键 (MaterialId, ProductionDepartmentId) 各不相同，避免触发业务键冲突）。
        var deptId = await PickAnyDepartmentIdAsync();
        for (var i = 0; i < 3; i++)
        {
            var mid = await PickFreeMaterialIdAsync();
            _pagingMaterialIds.Add(mid);
            await _service.CreateAsync(
                new SaveTaskSplitRuleConfigRequest
                {
                    MaterialId = mid,
                    ProductionDepartmentId = deptId,
                    PreferredBatchQty = 50m,
                    AllowSplit = true,
                    AllowMerge = true,
                }, actorUserId: 1, actorUserCode: "test", CancellationToken.None);
        }

        var page1 = await _service.ListAsync(null, null, null, pageIndex: 1, pageSize: 2, CancellationToken.None);
        page1.Page.Should().Be(1);
        page1.PageSize.Should().Be(2);
        page1.Items.Should().HaveCount(2);
        page1.Total.Should().BeGreaterThanOrEqualTo(3);

        // pageSize 超 200 → 静默截断到 200；items 条数 = min(200, total)
        //（dev 库可能已有 >200 行历史规则（如 83 万），total 为全量 COUNT，不可假设 items == total）
        var all = await _service.ListAsync(null, null, null, pageIndex: 1, pageSize: 9999, CancellationToken.None);
        all.PageSize.Should().Be(200);
        all.Items.Should().HaveCount(Math.Min(200, all.Total));

        // pageIndex 远超末页 → items 空数组，total 仍真实
        //（dev 库已有 83 万行历史规则：pageIndex=99999 × pageSize=200 → OFFSET≈2e7 出界且不触发 int32 溢出；999999999 会溢出为负被 SQL 拒绝）
        var beyond = await _service.ListAsync(null, null, null, pageIndex: 99999, pageSize: 200, CancellationToken.None);
        beyond.Page.Should().Be(99999);
        beyond.Items.Should().BeEmpty();
        beyond.Total.Should().BeGreaterThanOrEqualTo(3);

        // pageIndex < 1 → 强制归 1
        var clamped = await _service.ListAsync(null, null, null, pageIndex: 0, pageSize: 2, CancellationToken.None);
        clamped.Page.Should().Be(1);
    }

    public void Dispose()
    {
        // 清理本测试自建数据（仅按测试期选定的 MaterialId，绝不误删 4号位 curl / 真实数据）
        if (_testMaterialId > 0)
        {
            _connectionManager.ExecuteAsync(
                "DELETE FROM TaskSplitRuleConfig WHERE MaterialId = @MaterialId",
                new { MaterialId = _testMaterialId },
                db: DatabaseId.APS).GetAwaiter().GetResult();
        }
        foreach (var mid in _pagingMaterialIds)
        {
            _connectionManager.ExecuteAsync(
                "DELETE FROM TaskSplitRuleConfig WHERE MaterialId = @MaterialId",
                new { MaterialId = mid },
                db: DatabaseId.APS).GetAwaiter().GetResult();
        }
        _connectionManager.Dispose();
    }
}
