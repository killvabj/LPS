using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Repositories.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using System.Text.Json;
using LPS.APS.Core.Dto;
using Dapper;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// FrozenStrategySnapshotProvider 集成测试（阶段 B 端到端验收）
/// 测试范围：
/// 1. 真实数据库连接下的三版本级联加载
/// 2. 完整 DI 容器的服务协作
/// 3. 真实 JSON 反序列化与装配
/// </summary>
/// <remarks>
/// 前置条件：
/// - SQL Server 可访问（通过 appsettings.Test.json 配置）
/// - APS_Production 数据库存在
/// - 测试数据已预置（或通过 Setup 方法创建）
/// </remarks>
[Collection("B5Cache")]
public class FrozenStrategySnapshotProviderIntegrationTests : IDisposable
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IStrategyProfileVersionRepository _strategyProfileRepo;
    private readonly IRuleSetVersionRepository _ruleSetRepo;
    private readonly IParameterSetVersionRepository _parameterSetRepo;
    private readonly FrozenStrategySnapshotProvider _provider;
    private readonly IConfiguration _configuration;

    private long _testStrategyProfileVersionId;
    private long _testRuleSetVersionId;
    private long _testParameterSetVersionId;

    public FrozenStrategySnapshotProviderIntegrationTests()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var loggerFactory = LoggerFactory.Create(builder => { });

        var dbOptions = _configuration.GetSection("Database").Get<LPS.APS.Engine.Configuration.DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration not found in appsettings.Test.json");

        _connectionManager = new DatabaseConnectionManager(
            Microsoft.Extensions.Options.Options.Create(dbOptions));

        _ruleSetRepo = new RuleSetVersionRepository(
            _connectionManager,
            loggerFactory.CreateLogger<RuleSetVersionRepository>()
        );

        _parameterSetRepo = new ParameterSetVersionRepository(
            _connectionManager,
            loggerFactory.CreateLogger<ParameterSetVersionRepository>()
        );

        _strategyProfileRepo = new StrategyProfileVersionRepository(
            _connectionManager,
            loggerFactory.CreateLogger<StrategyProfileVersionRepository>()
        );

        _provider = new FrozenStrategySnapshotProvider(
            _strategyProfileRepo,
            _ruleSetRepo,
            _parameterSetRepo
        );

        // B-5 缓存为进程级静态字典（key = VersionId）：集成测试用真实 DB identity，
        // 可能与单元测试 mock 的 VersionId 冲突，必须在每个测试前清缓存保证隔离
        FrozenStrategySnapshotProvider.ClearCache();
    }

    [SkippableFact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_真实数据库_完整装配成功()
    {
        Skip.If(!TestEnvironment.HasContentSnapshotJsonColumn(), "测试库缺 ContentSnapshotJson 列（方案 A 未迁移），需 2号位 v5.1.2 迁移后转绿");

        await SetupTestDataAsync();

        var snapshot = await _provider.GetFrozenStrategySnapshotAsync(_testStrategyProfileVersionId, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot.StrategyProfileVersionId.Should().Be(_testStrategyProfileVersionId);
        snapshot.RuleSetVersionId.Should().Be(_testRuleSetVersionId);
        snapshot.ParameterSetVersionId.Should().Be(_testParameterSetVersionId);
        snapshot.FrozenAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(10));

        snapshot.DemandPriority.Should().NotBeNull();
        snapshot.Lock.Should().NotBeNull();
        snapshot.Supply.Should().NotBeNull();
        snapshot.Procurement.Should().NotBeNull();
        snapshot.SolverStrategy.Should().NotBeNull();
        snapshot.CandidateGuardrail.Should().NotBeNull();
    }

    [SkippableFact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_多次调用_命中缓存_返回同一快照()
    {
        Skip.If(!TestEnvironment.HasContentSnapshotJsonColumn(), "测试库缺 ContentSnapshotJson 列（方案 A 未迁移），需 2号位 v5.1.2 迁移后转绿");

        await SetupTestDataAsync();

        var snapshot1 = await _provider.GetFrozenStrategySnapshotAsync(_testStrategyProfileVersionId, CancellationToken.None);
        await System.Threading.Tasks.Task.Delay(100);
        var snapshot2 = await _provider.GetFrozenStrategySnapshotAsync(_testStrategyProfileVersionId, CancellationToken.None);

        snapshot1.StrategyProfileVersionId.Should().Be(snapshot2.StrategyProfileVersionId);
        snapshot1.RuleSetVersionId.Should().Be(snapshot2.RuleSetVersionId);
        snapshot1.ParameterSetVersionId.Should().Be(snapshot2.ParameterSetVersionId);

        // B-5 语义（契约 C2-4）：同 VersionId 二次调用命中缓存、不重复查库；
        // 命中时从 JSON 快照反序列化重建独立实例（杜绝共享可变对象污染），六块内容值相等（Run 内不刷新），
        // FrozenAt 为"冻结时点"=每次调用刷新（跨 Run 不陈旧）
        snapshot2.FrozenAt.Should().BeAfter(snapshot1.FrozenAt);
        snapshot2.DemandPriority.Should().NotBeSameAs(snapshot1.DemandPriority);
        snapshot2.DemandPriority.Segments.Should().HaveCount(snapshot1.DemandPriority.Segments.Count);
        snapshot2.Lock.Trigger.RemainingTimeThresholdHours.Should().Be(snapshot1.Lock.Trigger.RemainingTimeThresholdHours);
        snapshot2.Supply.Inventory.WarehousePriority.Should().Equal(snapshot1.Supply.Inventory.WarehousePriority);
        snapshot2.Procurement.PlanningYields.Should().HaveCount(snapshot1.Procurement.PlanningYields.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_真实数据库版本不存在_抛出异常()
    {
        const long nonExistentVersionId = 999999999;

        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(nonExistentVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"策略包版本不存在：{nonExistentVersionId}");
    }

    /// <summary>
    /// 需求优先级 §四-A 配置契约回归闸（2号位 2026-11-07 回执 §三）：
    /// **直读现网已发布 `RuleSetVersion.Id = 478` 快照**，断言段2 组B 第三条
    /// `{Field=7(ISSUEDATE), Operator=2(LessThan), Value="-30"}` 保持设计意图
    /// （`IssueDate &lt; 冻结日-30`，发行超30天命中）。
    ///
    /// 与 §四-B（证明「执行器能区分配置」）互补——本闸证明「**库里的那份配置没被人改回去**」。
    /// 478 这行今天已在 PUBLISHED 原位整份替换、`UpdatedAt=NULL`、`APS_Auth.AuditLog` 零痕迹
    /// （2号位 §三：今日至少 4 例同形），发布校验不查相对日方向语义、运行期也不报错 ⇒
    /// 「把 Operator 从 2 改回 4」是现实会发生且静默错排的回归路径，须靠本闸红。
    ///
    /// 红判据（照 2号位 §三 原文）：Operator 变 4，或该条件被删/被移出组B，或 478 行缺失。
    /// </summary>
    [SkippableFact(DisplayName = "回归闸：现网 478 段2 组B ISSUEDATE 算子须保持 LessThan(Operator==2)—— 配置被改回即红")]
    public async System.Threading.Tasks.Task RuleSetVersion478_段2组B_ISSUEDATE算子须为LessThan_配置契约回归闸()
    {
        Skip.If(!TestEnvironment.HasContentSnapshotJsonColumn(), "测试库缺 ContentSnapshotJson 列（方案 A 未迁移），需 2号位 v5.1.2 迁移后转绿");

        var json = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT ContentSnapshotJson FROM [dbo].[RuleSetVersion] WHERE Id = 478",
            new { },
            db: DatabaseId.APS);

        json.Should().NotBeNull("回归闸：现网 RuleSetVersion 不存在 Id=478（配置被删，或测试库缺该活配置行），无法核验需求优先级段2 组B 算子");

        using var parsed = JsonDocument.Parse(json!);
        var root = parsed.RootElement;

        var hasSeg1 = root.TryGetProperty("DemandPriority", out var dp)
                      && dp.TryGetProperty("Segments", out var segs)
                      && segs.GetArrayLength() > 1;
        if (!hasSeg1)
        {
            throw new InvalidOperationException("回归闸红：478 DemandPriority.Segments[1]（段2）缺失或被移出 —— 配置被改动");
        }

        var seg1 = dp.GetProperty("Segments")[1];
        var hasGroups = seg1.TryGetProperty("MatchConditionGroups", out var groups)
                        && groups.GetArrayLength() > 1;
        if (!hasGroups)
        {
            throw new InvalidOperationException("回归闸红：478 段2 缺 MatchConditionGroups 双组（组B 丢失）—— 配置被改动");
        }

        var hasCond = groups[1].TryGetProperty("Conditions", out var conds)
                      && conds.GetArrayLength() > 2;
        if (!hasCond)
        {
            throw new InvalidOperationException("回归闸红：478 段2 组B 第3 条（ISSUEDATE）缺失或被移出 —— 配置被改动");
        }

        var cond = conds[2];
        cond.GetProperty("Field").GetInt32().Should().Be(7, "组B 第3 条须为 ISSUEDATE（Field=7）");
        cond.GetProperty("Value").GetString().Should().Be("-30", "组B 第3 条阈值保持整数偏移 -30（换算为 冻结日-30）");
        cond.GetProperty("Operator").GetInt32().Should().Be(2,
            "组B 第3 条算子须为 LessThan(2)；若被改回 GreaterThan(4)，段2 DNF 组B 语意即翻转（发行超30天误命中）");
    }

    private async System.Threading.Tasks.Task SetupTestDataAsync()
    {
        var now = DateTime.Now;
        var uniqueSuffix = $"{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}".Substring(0, 30);

        // 通过 _connectionManager 的辅助方法创建父表记录，避免连接死锁
        long ruleSetId;
        long parameterSetId;
        long strategyProfileId;

        // 创建 RuleSet 父记录
        const string insertRuleSetSql = @"
            INSERT INTO [dbo].[RuleSet] ([RuleSetCode], [RuleSetName], [Description], [IsActive], [CreatedAt], [CreatedBy])
            VALUES (@Code, @Name, @Description, 1, @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        ruleSetId = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            insertRuleSetSql,
            new { Code = $"TEST-RS-{uniqueSuffix}", Name = "集成测试规则集", Description = "Integration Test", CreatedAt = now, CreatedBy = "IntegrationTest" },
            db: DatabaseId.APS);

        // 创建 ParameterSet 父记录
        const string insertParameterSetSql = @"
            INSERT INTO [dbo].[ParameterSet] ([ParameterSetCode], [ParameterSetName], [Description], [IsActive], [CreatedAt], [CreatedBy])
            VALUES (@Code, @Name, @Description, 1, @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        parameterSetId = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            insertParameterSetSql,
            new { Code = $"TEST-PS-{uniqueSuffix}", Name = "集成测试参数集", Description = "Integration Test", CreatedAt = now, CreatedBy = "IntegrationTest" },
            db: DatabaseId.APS);

        // 创建 StrategyProfile 父记录
        const string insertStrategyProfileSql = @"
            INSERT INTO [dbo].[StrategyProfile] ([StrategyProfileCode], [StrategyProfileName], [Description], [IsActive], [CreatedAt], [CreatedBy])
            VALUES (@Code, @Name, @Description, 1, @CreatedAt, @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        strategyProfileId = await _connectionManager.QueryFirstOrDefaultAsync<long>(
            insertStrategyProfileSql,
            new { Code = $"TEST-SP-{uniqueSuffix}", Name = "集成测试策略包", Description = "Integration Test", CreatedAt = now, CreatedBy = "IntegrationTest" },
            db: DatabaseId.APS);

        var demandPriorityJson = JsonSerializer.Serialize(new DemandPriorityBlock
        {
            Segments = new List<PrioritySegment>
            {
                new PrioritySegment
                {
                    SegmentOrder = 1,
                    SegmentName = "集成测试-紧急订单",
                    IsEnabled = true,
                    MatchConditions = new List<SegmentMatchCondition>
                    {
                        new SegmentMatchCondition
                        {
                            Field = DemandField.DelayStatus,
                            Operator = ConditionOperator.Equals,
                            Value = "DELAYED"
                        }
                    },
                    SortFields = new List<SegmentSortField>
                    {
                        new SegmentSortField
                        {
                            Field = DemandField.RemainingTimeHours,
                            Direction = SortDirection.Asc
                        }
                    }
                }
            }
        });

        // 方案 A（P0-01 收口）：六主题 JSON 列退出持久化，ContentSnapshotJson 为唯一内容载体——
        // 装配 DemandPriority 子块 JSON 直接落库（与治理服务 EnsureNormalized 产物同构）
        var ruleSetVersion = new RuleSetVersion
        {
            RuleSetId = ruleSetId,
            VersionCode = $"TEST-R-{now:yyyyMMddHHmmss}",
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["DemandPriority"] = JsonSerializer.Deserialize<JsonElement>(demandPriorityJson)
            }),
            Status = "PUBLISHED",
            PublishedAt = now,
            PublishedBy = "IntegrationTest",
            CreatedAt = now,
            CreatedBy = "IntegrationTest"
        };

        ruleSetVersion = await _ruleSetRepo.AddAsync(ruleSetVersion);
        _testRuleSetVersionId = ruleSetVersion.Id;

        var lockJson = JsonSerializer.Serialize(new LockBlock
        {
            Trigger = new ProtectionTriggerParams
            {
                UseRemainingTimeThreshold = true,
                RemainingTimeThresholdHours = 48,
                ProtectDelayed = true,
                ProtectVipTier = true,
                VipTierValue = "VIP",
                AllowPmcManualProtection = true
            },
            Sticky = new StickyProtectionParams
            {
                RequireReleaseRecord = true,
                ProtectUntilCompletion = true,
                ProtectUntilSupplyInvalid = true
            }
        });

        var supplyJson = JsonSerializer.Serialize(new SupplyBlock
        {
            Inventory = new LPS.APS.Core.Dto.InventoryAvailabilityRule
            {
                IsEnabled = true,
                WarehousePriority = new List<string> { "WH-TEST-01", "WH-TEST-02" },
                RequireFactoryContext = true,
                RequireProductFamilyContext = false
            },
            PiSort = new PiSortParams
            {
                SortBy = PiSortBy.IssueDateAsc,
                UseStablePiNoTieBreak = true
            }
        });

        var procurementJson = JsonSerializer.Serialize(new ProcurementBlock
        {
            DefaultPurchaseLt = new List<PurchaseLtRule>
            {
                new PurchaseLtRule
                {
                    WarehouseCode = "WH-TEST-01",
                    MaterialId = null,
                    DefaultLtDays = 7
                }
            },
            OverdueMargin = new OverdueMarginParams
            {
                MarginPercent = 0.1m,
                MinimumExtraDays = 1
            },
            ArrivalToUsableOffsets = new List<WarehouseOffsetRule>
            {
                new WarehouseOffsetRule
                {
                    WarehouseCode = "WH-TEST-01",
                    OffsetHours = 24
                }
            },
            PlanningYields = new List<PlanningYieldRule>
            {
                new PlanningYieldRule
                {
                    MaterialId = "MAT-TEST-001",
                    StageCode = null,
                    YieldPercent = 0.95m
                }
            }
        });

        // SolverStrategy / CandidateGuardrail 子块（P0-02 收口：六块统一真实来源，缺一装载失败）
        var solverStrategyJson = JsonSerializer.Serialize(new SolverStrategyBlock
        {
            Mode = SolverStrategyMode.Backward,
            OnTimeTarget = new OnTimeTargetParams { TargetPercent = 85 },
            Setup = new SetupParams()
        });

        var candidateGuardrailJson = JsonSerializer.Serialize(new CandidateGuardrailBlock
        {
            NormalMs = 70_000,
            SoftMs = 110_000,
            LocalHardMs = 200_000,
            MaxRepairAttempts = 7,
            MaxPropagationRounds = 12
        });

        var parameterSetVersion = new ParameterSetVersion
        {
            ParameterSetId = parameterSetId,
            VersionCode = $"TEST-P-{now:yyyyMMddHHmmss}",
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = JsonSerializer.Deserialize<JsonElement>(lockJson),
                ["Supply"] = JsonSerializer.Deserialize<JsonElement>(supplyJson),
                ["Procurement"] = JsonSerializer.Deserialize<JsonElement>(procurementJson),
                ["SolverStrategy"] = JsonSerializer.Deserialize<JsonElement>(solverStrategyJson),
                ["CandidateGuardrail"] = JsonSerializer.Deserialize<JsonElement>(candidateGuardrailJson)
            }),
            Status = "PUBLISHED",
            PublishedAt = now,
            PublishedBy = "IntegrationTest",
            CreatedAt = now,
            CreatedBy = "IntegrationTest"
        };

        parameterSetVersion = await _parameterSetRepo.AddAsync(parameterSetVersion);
        _testParameterSetVersionId = parameterSetVersion.Id;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            StrategyProfileId = strategyProfileId,
            VersionCode = $"TEST-S-{now:yyyyMMddHHmmss}",
            RuleSetVersionId = _testRuleSetVersionId,
            ParameterSetVersionId = _testParameterSetVersionId,
            Status = "PUBLISHED",
            IsDefault = false,
            PublishedAt = now,
            PublishedBy = "IntegrationTest",
            CreatedAt = now,
            CreatedBy = "IntegrationTest"
        };

        strategyProfileVersion = await _strategyProfileRepo.AddAsync(strategyProfileVersion);
        _testStrategyProfileVersionId = strategyProfileVersion.Id;
    }

    public void Dispose()
    {
        CleanupTestDataAsync().GetAwaiter().GetResult();
    }

    private async System.Threading.Tasks.Task CleanupTestDataAsync()
    {
        if (_testStrategyProfileVersionId > 0)
        {
            await _connectionManager.ExecuteAsync(
                "DELETE FROM StrategyProfileVersion WHERE Id = @Id",
                new { Id = _testStrategyProfileVersionId },
                db: DatabaseId.APS);
        }
        if (_testRuleSetVersionId > 0)
        {
            await _connectionManager.ExecuteAsync(
                "DELETE FROM RuleSetVersion WHERE Id = @Id",
                new { Id = _testRuleSetVersionId },
                db: DatabaseId.APS);
        }
        if (_testParameterSetVersionId > 0)
        {
            await _connectionManager.ExecuteAsync(
                "DELETE FROM ParameterSetVersion WHERE Id = @Id",
                new { Id = _testParameterSetVersionId },
                db: DatabaseId.APS);
        }
    }
}
