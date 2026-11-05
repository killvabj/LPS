using System.Text.Json;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// FrozenStrategySnapshotProvider 单元测试（阶段 B 装配逻辑验收）
/// 测试范围：
/// 1. 正常装配流程（三版本级联加载 + 六块反序列化）
/// 2. 异常处理（版本不存在场景）
/// 3. JSON 反序列化容错（空值、格式错误）
/// </summary>
[Collection("B5Cache")]
public class FrozenStrategySnapshotProviderTests
{
    private readonly Mock<IStrategyProfileVersionRepository> _mockStrategyProfileRepo;
    private readonly Mock<IRuleSetVersionRepository> _mockRuleSetRepo;
    private readonly Mock<IParameterSetVersionRepository> _mockParameterSetRepo;
    private readonly FrozenStrategySnapshotProvider _provider;

    public FrozenStrategySnapshotProviderTests()
    {
        _mockStrategyProfileRepo = new Mock<IStrategyProfileVersionRepository>();
        _mockRuleSetRepo = new Mock<IRuleSetVersionRepository>();
        _mockParameterSetRepo = new Mock<IParameterSetVersionRepository>();

        _provider = new FrozenStrategySnapshotProvider(
            _mockStrategyProfileRepo.Object,
            _mockRuleSetRepo.Object,
            _mockParameterSetRepo.Object
        );

        // B-5 缓存：快照缓存为进程级静态字典（内容不可变、无需失效），测试间必须隔离
        FrozenStrategySnapshotProvider.ClearCache();
    }

    /// <summary>
    /// B-5 测试辅助：装配一套合法三版本（六块经 ContentSnapshotJson 真实来源，P0-02 收口后不误伤）。
    /// SolverStrategy/CandidateGuardrail 为真实内容（R14~R17 重放断言基准）。
    /// </summary>
    private void SetupValidVersions(
        long strategyProfileVersionId,
        long ruleSetVersionId,
        long parameterSetVersionId)
    {
        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            StrategyProfileId = 1,
            VersionCode = "V1.0",
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            RuleSetId = 2,
            VersionCode = "R1.0",
            ContentSnapshotJson = BuildRuleSetSnapshot(
                new DemandPriorityBlock
                {
                    Segments = new List<PrioritySegment>
                    {
                        new PrioritySegment { SegmentOrder = 1, SegmentName = "紧急订单", IsEnabled = true }
                    }
                }),
            Status = "PUBLISHED"
        };

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ParameterSetId = 3,
            VersionCode = "P1.0",
            ContentSnapshotJson = BuildParameterSetSnapshot(
                new LockBlock
                {
                    Trigger = new ProtectionTriggerParams { UseRemainingTimeThreshold = true, RemainingTimeThresholdHours = 24 }
                },
                new SupplyBlock
                {
                    Inventory = new InventoryAvailabilityRule { IsEnabled = true, WarehousePriority = new List<string> { "WH01" } }
                },
                new ProcurementBlock
                {
                    PlanningYields = new List<PlanningYieldRule>
                    {
                        new PlanningYieldRule { MaterialId = "MAT001", YieldPercent = 0.95m }
                    }
                },
                new SolverStrategyBlock
                {
                    Mode = SolverStrategyMode.Forward,
                    OnTimeTarget = new OnTimeTargetParams { TargetPercent = 90, IsPrimaryObjective = true },
                    Setup = new SetupParams()
                },
                new CandidateGuardrailBlock
                {
                    NormalMs = 60_000,
                    SoftMs = 90_000,
                    LocalHardMs = 180_000,
                    MaxRepairAttempts = 5,
                    MaxPropagationRounds = 10
                }),
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ruleSetVersion);

        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameterSetVersion);
    }

    /// <summary>测试辅助：组装 RuleSet 侧 ContentSnapshotJson（DemandPriority 子块，契约 §6.10.5 键名）</summary>
    private static string BuildRuleSetSnapshot(DemandPriorityBlock demandPriority)
        => JsonSerializer.Serialize(new Dictionary<string, object> { ["DemandPriority"] = demandPriority });

    /// <summary>测试辅助：组装 ParameterSet 侧 ContentSnapshotJson（五子块，契约 §6.10.5 键名）</summary>
    private static string BuildParameterSetSnapshot(
        LockBlock lockBlock,
        SupplyBlock supplyBlock,
        ProcurementBlock procurementBlock,
        SolverStrategyBlock solverStrategyBlock,
        CandidateGuardrailBlock candidateGuardrailBlock)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Lock"] = lockBlock,
            ["Supply"] = supplyBlock,
            ["Procurement"] = procurementBlock,
            ["SolverStrategy"] = solverStrategyBlock,
            ["CandidateGuardrail"] = candidateGuardrailBlock
        });

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_正常装配六块_成功()
    {
        // Arrange
        const long strategyProfileVersionId = 100;
        const long ruleSetVersionId = 200;
        const long parameterSetVersionId = 300;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            StrategyProfileId = 1,
            VersionCode = "V1.0",
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var demandPriorityJson = JsonSerializer.Serialize(new DemandPriorityBlock
        {
            Segments = new List<PrioritySegment>
            {
                new PrioritySegment
                {
                    SegmentOrder = 1,
                    SegmentName = "紧急订单",
                    IsEnabled = true
                }
            }
        });

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            RuleSetId = 2,
            VersionCode = "R1.0",
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["DemandPriority"] = JsonSerializer.Deserialize<DemandPriorityBlock>(demandPriorityJson)
            }),
            Status = "PUBLISHED"
        };

        var lockJson = JsonSerializer.Serialize(new LockBlock
        {
            Trigger = new ProtectionTriggerParams
            {
                UseRemainingTimeThreshold = true,
                RemainingTimeThresholdHours = 24
            }
        });

        var supplyJson = JsonSerializer.Serialize(new SupplyBlock
        {
            Inventory = new InventoryAvailabilityRule
            {
                IsEnabled = true,
                WarehousePriority = new List<string> { "WH01", "WH02" }
            }
        });

        var procurementJson = JsonSerializer.Serialize(new ProcurementBlock
        {
            PlanningYields = new List<PlanningYieldRule>
            {
                new PlanningYieldRule
                {
                    MaterialId = "MAT001",
                    YieldPercent = 0.95m
                }
            }
        });

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
            Id = parameterSetVersionId,
            ParameterSetId = 3,
            VersionCode = "P1.0",
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = JsonSerializer.Deserialize<LockBlock>(lockJson),
                ["Supply"] = JsonSerializer.Deserialize<SupplyBlock>(supplyJson),
                ["Procurement"] = JsonSerializer.Deserialize<ProcurementBlock>(procurementJson),
                ["SolverStrategy"] = JsonSerializer.Deserialize<SolverStrategyBlock>(solverStrategyJson),
                ["CandidateGuardrail"] = JsonSerializer.Deserialize<CandidateGuardrailBlock>(candidateGuardrailJson)
            }),
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ruleSetVersion);

        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameterSetVersion);

        // Act
        var snapshot = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        // Assert
        snapshot.Should().NotBeNull();
        snapshot.StrategyProfileVersionId.Should().Be(strategyProfileVersionId);
        snapshot.RuleSetVersionId.Should().Be(ruleSetVersionId);
        snapshot.ParameterSetVersionId.Should().Be(parameterSetVersionId);
        snapshot.FrozenAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));

        // 验证六块装配
        snapshot.DemandPriority.Should().NotBeNull();
        snapshot.DemandPriority.Segments.Should().HaveCount(1);
        snapshot.DemandPriority.Segments[0].SegmentName.Should().Be("紧急订单");

        snapshot.Lock.Should().NotBeNull();
        snapshot.Lock.Trigger.Should().NotBeNull();
        snapshot.Lock.Trigger.RemainingTimeThresholdHours.Should().Be(24);

        snapshot.Supply.Should().NotBeNull();
        snapshot.Supply.Inventory.Should().NotBeNull();
        snapshot.Supply.Inventory.WarehousePriority.Should().Contain("WH01");

        snapshot.Procurement.Should().NotBeNull();
        snapshot.Procurement.PlanningYields.Should().HaveCount(1);
        snapshot.Procurement.PlanningYields[0].YieldPercent.Should().Be(0.95m);

        // R14~R17 真实重放断言（P0-02：不能只断言 NotNull，须断言具体值等于该版本 JSON 中内容）
        snapshot.SolverStrategy.Mode.Should().Be(SolverStrategyMode.Backward);
        snapshot.SolverStrategy.OnTimeTarget.TargetPercent.Should().Be(85);

        snapshot.CandidateGuardrail.NormalMs.Should().Be(70_000);
        snapshot.CandidateGuardrail.SoftMs.Should().Be(110_000);
        snapshot.CandidateGuardrail.LocalHardMs.Should().Be(200_000);
        snapshot.CandidateGuardrail.MaxRepairAttempts.Should().Be(7);
        snapshot.CandidateGuardrail.MaxPropagationRounds.Should().Be(12);

        // 验证仓储调用
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()), Times.Once);
        _mockRuleSetRepo.Verify(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()), Times.Once);
        _mockParameterSetRepo.Verify(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_策略包版本不存在_抛出异常()
    {
        // Arrange
        const long strategyProfileVersionId = 999;

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StrategyProfileVersion?)null);

        // Act & Assert
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"策略包版本不存在：{strategyProfileVersionId}");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_规则集版本不存在_抛出异常()
    {
        // Arrange
        const long strategyProfileVersionId = 100;
        const long ruleSetVersionId = 999;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = 300,
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RuleSetVersion?)null);

        // Act & Assert
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"规则集版本不存在：{ruleSetVersionId}");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集版本不存在_抛出异常()
    {
        // Arrange
        const long strategyProfileVersionId = 100;
        const long ruleSetVersionId = 200;
        const long parameterSetVersionId = 999;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ruleSetVersion);

        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ParameterSetVersion?)null);

        // Act & Assert
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"参数集版本不存在：{parameterSetVersionId}");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_JSON为空_装载失败抛异常()
    {
        // Arrange
        const long strategyProfileVersionId = 100;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = 200,
            ParameterSetVersionId = 300,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = 200,
            ContentSnapshotJson = null,
            Status = "PUBLISHED"
        };

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = 300,
            ContentSnapshotJson = string.Empty,
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ruleSetVersion);

        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameterSetVersion);

        // Act & Assert（P0-02 六块统一：ContentSnapshotJson 缺失 → Snapshot 装载失败，不静默回退空 Block）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ContentSnapshotJson 为空/缺失*");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_JSON格式错误_装载失败抛异常()
    {
        // Arrange
        const long strategyProfileVersionId = 100;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = 200,
            ParameterSetVersionId = 300,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = 200,
            ContentSnapshotJson = "{ invalid json }",
            Status = "PUBLISHED"
        };

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = 300,
            ContentSnapshotJson = "not json at all",
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(strategyProfileVersion);

        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ruleSetVersion);

        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameterSetVersion);

        // Act & Assert（P0-02 六块统一：ContentSnapshotJson 损坏 → Snapshot 装载失败，不静默回退空 Block）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ContentSnapshotJson 格式无效*");
    }

    // ===================== B-5：Snapshot 缓存（清单 #51 性能红线 / 契约 C2-4） =====================

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_同一版本二次调用_命中缓存不重复查库()
    {
        // Arrange（B-5：一次 Run 只加载一次 Snapshot；同 VersionId 缓存命中）
        const long strategyProfileVersionId = 101;
        const long ruleSetVersionId = 201;
        const long parameterSetVersionId = 301;
        SetupValidVersions(strategyProfileVersionId, ruleSetVersionId, parameterSetVersionId);

        // Act：同一 VersionId 连续调用两次
        var first = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);
        var second = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        // Assert：二次调用命中缓存，仓储不再查询（各仅一次）
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()), Times.Once);
        _mockRuleSetRepo.Verify(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>()), Times.Once);
        _mockParameterSetRepo.Verify(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_不同版本_互不污染各走仓储()
    {
        // Arrange（B-5 / 契约 C2-4：Cache Key 含 VersionId，不同版本绝不互相污染）
        SetupValidVersions(102, 202, 302);
        SetupValidVersions(103, 203, 303);

        // Act：102 → 103 → 102 再读（验证加载 103 不会覆盖/污染 102 的缓存条目）
        var snapshotA = await _provider.GetFrozenStrategySnapshotAsync(102, CancellationToken.None);
        var snapshotB = await _provider.GetFrozenStrategySnapshotAsync(103, CancellationToken.None);
        var snapshotA2 = await _provider.GetFrozenStrategySnapshotAsync(102, CancellationToken.None);

        // Assert：
        // ① 各自返回对应版本内容（不串数据）
        snapshotA.RuleSetVersionId.Should().Be(202);
        snapshotB.RuleSetVersionId.Should().Be(203);
        // ② 102 第三次读取仍返回 102 内容（未被 103 污染）且命中缓存（仓储仍只查一次）
        snapshotA2.RuleSetVersionId.Should().Be(202);
        snapshotA2.Procurement.PlanningYields.Should().HaveCount(1);
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(102, It.IsAny<CancellationToken>()), Times.Once);
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(103, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_缓存命中_反序列化重建独立实例_内容值相等FrozenAt刷新()
    {
        // Arrange（B-5：Run 内不刷新——命中时从 JSON 快照反序列化重建，六块内容与首次一致；FrozenAt=本次调用时点）
        const long strategyProfileVersionId = 104;
        SetupValidVersions(strategyProfileVersionId, 204, 304);

        // Act：连续调用两次（间隔 50ms 确保 FrozenAt 时间戳变化——Windows 默认时钟 tick 约 15.6ms，10ms 不足产生波动）
        var first = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);
        await System.Threading.Tasks.Task.Delay(50);
        var second = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        // Assert：
        // ① 每次返回独立实例（JSON 重建，杜绝共享可变对象污染——消费者就地修改不影响缓存）
        second.Should().NotBeSameAs(first);
        second.DemandPriority.Should().NotBeSameAs(first.DemandPriority);
        second.Lock.Should().NotBeSameAs(first.Lock);
        second.Supply.Should().NotBeSameAs(first.Supply);
        second.Procurement.Should().NotBeSameAs(first.Procurement);
        second.SolverStrategy.Should().NotBeSameAs(first.SolverStrategy);
        second.CandidateGuardrail.Should().NotBeSameAs(first.CandidateGuardrail);
        // ② 六块内容值相等（Run 内不刷新）
        second.StrategyProfileVersionId.Should().Be(first.StrategyProfileVersionId);
        second.RuleSetVersionId.Should().Be(first.RuleSetVersionId);
        second.ParameterSetVersionId.Should().Be(first.ParameterSetVersionId);
        second.DemandPriority.Segments.Should().HaveCount(1);
        second.DemandPriority.Segments[0].SegmentName.Should().Be("紧急订单");
        second.Lock.Trigger.RemainingTimeThresholdHours.Should().Be(24);
        second.Supply.Inventory.WarehousePriority.Should().Contain("WH01");
        second.Procurement.PlanningYields.Should().HaveCount(1);
        // ③ FrozenAt 刷新为本次调用时点（冻结时点语义，跨 Run 不陈旧）
        second.FrozenAt.Should().BeAfter(first.FrozenAt);
        // ④ 命中缓存不再查库
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_装载失败_不写入缓存_同版本重试重新走仓储()
    {
        // Arrange（P0-04 × B-5 交互：坏配置装载失败绝不写入缓存，避免"陈旧坏快照"被后续同版本命中）
        const long strategyProfileVersionId = 105;

        // 第一次：ContentSnapshotJson 为空 → 装载失败
        var badStrategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = 205,
            ParameterSetVersionId = 305,
            Status = "PUBLISHED"
        };
        var badRuleSetVersion = new RuleSetVersion { Id = 205, ContentSnapshotJson = null, Status = "PUBLISHED" };
        var badParameterSetVersion = new ParameterSetVersion { Id = 305, ContentSnapshotJson = "{}", Status = "PUBLISHED" };

        _mockStrategyProfileRepo
            .Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(badStrategyProfileVersion);
        _mockRuleSetRepo
            .Setup(r => r.GetByIdAsync(205, It.IsAny<CancellationToken>()))
            .ReturnsAsync(badRuleSetVersion);
        _mockParameterSetRepo
            .Setup(r => r.GetByIdAsync(305, It.IsAny<CancellationToken>()))
            .ReturnsAsync(badParameterSetVersion);

        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ContentSnapshotJson 为空/缺失*");

        // 修复配置后重试同一 VersionId → 必须重新走仓储（证明失败未写入缓存，无陈旧坏快照）
        SetupValidVersions(strategyProfileVersionId, 205, 305);

        var snapshot = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot.DemandPriority.Segments.Should().HaveCount(1);
        // 失败装载 1 次 + 修复后重试成功 1 次 = 共 2 次仓储查询（若失败写入了缓存，则重试命中缓存仅 1 次）
        _mockStrategyProfileRepo.Verify(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ===================== P0-02：六块统一失败语义（R14~R17，契约 §6.10.5） =====================

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集快照缺少SolverStrategy子块_装载失败()
    {
        // Arrange（R14~R17：六块统一缺失即失败；SolverStrategy 为 P0-02 收口核心）
        const long strategyProfileVersionId = 106;
        const long ruleSetVersionId = 206;
        const long parameterSetVersionId = 306;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            ContentSnapshotJson = BuildRuleSetSnapshot(new DemandPriorityBlock { Segments = [] }),
            Status = "PUBLISHED"
        };

        // 参数集快照缺 SolverStrategy 子块（其余四块齐全）
        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = new LockBlock(),
                ["Supply"] = new SupplyBlock(),
                ["Procurement"] = new ProcurementBlock(),
                ["CandidateGuardrail"] = new CandidateGuardrailBlock()
            }),
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);
        _mockRuleSetRepo.Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSetVersion);
        _mockParameterSetRepo.Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSetVersion);

        // Act & Assert（P0-02 六块统一：缺任一子块 → Snapshot 装载失败，不静默回退）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*缺少 SolverStrategy 子块*");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集快照缺少CandidateGuardrail子块_装载失败()
    {
        // Arrange（R14~R17：CandidateGuardrail 同样六块统一缺失即失败）
        const long strategyProfileVersionId = 107;
        const long ruleSetVersionId = 207;
        const long parameterSetVersionId = 307;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            ContentSnapshotJson = BuildRuleSetSnapshot(new DemandPriorityBlock { Segments = [] }),
            Status = "PUBLISHED"
        };

        // 参数集快照缺 CandidateGuardrail 子块（其余四块齐全）
        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = new LockBlock(),
                ["Supply"] = new SupplyBlock(),
                ["Procurement"] = new ProcurementBlock(),
                ["SolverStrategy"] = new SolverStrategyBlock()
            }),
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);
        _mockRuleSetRepo.Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSetVersion);
        _mockParameterSetRepo.Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSetVersion);

        // Act & Assert（P0-02 六块统一：缺任一子块 → Snapshot 装载失败，不静默回退）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*缺少 CandidateGuardrail 子块*");
    }

    // ===================== P1-01：装载前版本状态防御 =====================

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_规则集版本非PUBLISHED_装载失败()
    {
        // Arrange（P1-01：Run 装载必须 PUBLISHED，未发布版本直接失败）
        const long strategyProfileVersionId = 108;
        const long ruleSetVersionId = 208;
        const long parameterSetVersionId = 308;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            ContentSnapshotJson = BuildRuleSetSnapshot(new DemandPriorityBlock { Segments = [] }),
            Status = "DRAFT"   // 未发布 → 拒绝装载
        };

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = new LockBlock(),
                ["Supply"] = new SupplyBlock(),
                ["Procurement"] = new ProcurementBlock(),
                ["SolverStrategy"] = new SolverStrategyBlock(),
                ["CandidateGuardrail"] = new CandidateGuardrailBlock()
            }),
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);
        _mockRuleSetRepo.Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSetVersion);
        _mockParameterSetRepo.Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSetVersion);

        // Act & Assert（P1-01：非 PUBLISHED → 装载失败）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*非 PUBLISHED*");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集版本已失效_仍可装载_历史重放不阻断()
    {
        // Arrange（P0-03 收口：历史版本按显式 VersionId 重放不受当前时间影响——
        // EffectiveFrom/EffectiveTo 窗口检查已移除，新 Run 准入归 ResolveDefaultStrategyProfileVersionAsync）
        const long strategyProfileVersionId = 109;
        const long ruleSetVersionId = 209;
        const long parameterSetVersionId = 309;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            ContentSnapshotJson = BuildRuleSetSnapshot(new DemandPriorityBlock { Segments = [] }),
            Status = "PUBLISHED"
        };

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ContentSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Lock"] = new LockBlock(),
                ["Supply"] = new SupplyBlock(),
                ["Procurement"] = new ProcurementBlock(),
                ["SolverStrategy"] = new SolverStrategyBlock(),
                ["CandidateGuardrail"] = new CandidateGuardrailBlock()
            }),
            Status = "PUBLISHED",
            EffectiveTo = DateTime.Now.AddHours(-1)   // 已失效（历史重放不应阻断）
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);
        _mockRuleSetRepo.Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSetVersion);
        _mockParameterSetRepo.Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSetVersion);

        // Act & Assert（P0-03：已失效版本仍可装载——历史重放成功，不抛异常）
        var snapshot = await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot.ParameterSetVersionId.Should().Be(parameterSetVersionId);
        snapshot.SolverStrategy.Should().NotBeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_父策略包版本非PUBLISHED_装载失败()
    {
        // Arrange（P0-03 收口：补父策略包版本装载前置校验——仅 PUBLISHED 可装载，新 Run 首次装载防御）
        const long strategyProfileVersionId = 110;
        const long ruleSetVersionId = 210;
        const long parameterSetVersionId = 310;

        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "DRAFT"   // 未发布 → 拒绝装载
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);

        // Act & Assert（P0-03：父策略包版本非 PUBLISHED → 装载失败）
        var act = async () => await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*非 PUBLISHED*");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage 固定交接等待规则装载（合同 v1.1 §四/§五；1号位 2026-10-28《消费侧回执》§1.2 断点 1 / §三 第 1 项）
    //
    // 这组的**声明目的**（照 self-check-must-assert-its-stated-purpose）：
    //   ① 缺块 ⇒ 空集合、**不抛**——这条就是「禁止套 DeserializeParameterSetBlock」的回归护栏。
    //      现网绝大多数 PSV 没有该块；若有人把它并回六块 fail-fast 写法，本测试立刻转红，
    //      而不是等到某个 Run 装载失败才发现（那会是全库级事故：除 PSV 2365 外全部装载失败）。
    //   ② 有块 ⇒ 逐字段解析正确（含 TimeBasis 枚举与 IsActive 可空）。
    //   ③ 装载侧**不筛 IsActive**——IsActive=false 的行必须一并带出，否则「规则存在但未命中」
    //      与「规则不存在」重新同形，破坏 1号位 合同要求的「未命中不静默 0」独立诊断。
    //   ④ 显式 null ⇒ 空集合（H-3 无源不阻断）。
    //   ⑤ JSON 损坏（类型非法）⇒ **抛**，不静默降级成「合法缺配」。
    //   ⑥ **零行空数组 `[]` ⇒ 抛**（契约 C9）——与 ④ 构成对照：字面都是「空」但语义相反，
    //      要表达「本版不用这块」只能删键 / 置 null；留 `[]` 是「配了但零行」的非法形态。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>测试辅助：装配一套三版本并装载快照；<paramref name="rawBlockJson"/> = null 表示**键缺失**。</summary>
    private async System.Threading.Tasks.Task<FrozenStrategySnapshot> LoadSnapshotWithRawBlockAsync(
        long strategyProfileVersionId,
        long ruleSetVersionId,
        long parameterSetVersionId,
        string? rawBlockJson)
    {
        var strategyProfileVersion = new StrategyProfileVersion
        {
            Id = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersionId,
            ParameterSetVersionId = parameterSetVersionId,
            Status = "PUBLISHED"
        };

        var ruleSetVersion = new RuleSetVersion
        {
            Id = ruleSetVersionId,
            ContentSnapshotJson = BuildRuleSetSnapshot(new DemandPriorityBlock { Segments = [] }),
            Status = "PUBLISHED"
        };

        var baseParameterJson = BuildParameterSetSnapshot(
            new LockBlock(), new SupplyBlock(), new ProcurementBlock(),
            new SolverStrategyBlock(), new CandidateGuardrailBlock());

        var parameterSetJson = baseParameterJson;
        if (rawBlockJson is not null)
        {
            // 追加顶层键（六块原样保留），模拟「六块齐全 + 该块存在」的真实 PSV 形态
            var node = System.Text.Json.Nodes.JsonNode.Parse(baseParameterJson)!.AsObject();
            node[SolverStrategyValidator.StageHandoffWaitRulesBlockName] =
                System.Text.Json.Nodes.JsonNode.Parse(rawBlockJson);
            parameterSetJson = node.ToJsonString();
        }

        var parameterSetVersion = new ParameterSetVersion
        {
            Id = parameterSetVersionId,
            ContentSnapshotJson = parameterSetJson,
            Status = "PUBLISHED"
        };

        _mockStrategyProfileRepo.Setup(r => r.GetByIdAsync(strategyProfileVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfileVersion);
        _mockRuleSetRepo.Setup(r => r.GetByIdAsync(ruleSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSetVersion);
        _mockParameterSetRepo.Setup(r => r.GetByIdAsync(parameterSetVersionId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSetVersion);

        return await _provider.GetFrozenStrategySnapshotAsync(strategyProfileVersionId, CancellationToken.None);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集无StageHandoffWaitRules子块_空集合且不抛()
    {
        // Arrange：六块齐全、但**没有** StageHandoffWaitRules 键（= 现网绝大多数 PSV 的真实形态）
        // Act
        var snapshot = await LoadSnapshotWithRawBlockAsync(201, 211, 311, rawBlockJson: null);

        // Assert：缺块 = 无交接等待规则（H-3 无源不阻断），**不是装载失败**
        snapshot.Should().NotBeNull("套用六块 fail-fast 写法会让除 PSV 2365 外的所有 PSV 装载失败");
        snapshot.StageHandoffWaitRules.Should().NotBeNull();
        snapshot.StageHandoffWaitRules.Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_参数集含StageHandoffWaitRules_逐字段解析且不筛IsActive()
    {
        // Arrange：3 行——2 行 IsActive=true、1 行 IsActive=false（装载侧必须一并带出）
        const string blockJson = """
        [
          { "FactoryCode": "BJ", "FromStageCode": "MACH", "ToStageCode": "OXID", "WaitMinutes": 240, "TimeBasis": "ELAPSED", "IsActive": true },
          { "FactoryCode": "CN6课", "FromStageCode": "OXID", "ToStageCode": "FINAL", "WaitMinutes": 60, "TimeBasis": "WORK", "IsActive": true },
          { "FactoryCode": "SH", "FromStageCode": "FINAL", "ToStageCode": "OUTS", "WaitMinutes": 0, "TimeBasis": "ELAPSED", "IsActive": false }
        ]
        """;

        // Act
        var snapshot = await LoadSnapshotWithRawBlockAsync(202, 212, 312, blockJson);

        // Assert
        var rules = snapshot.StageHandoffWaitRules;
        rules.Should().HaveCount(3, "装载侧不筛 IsActive：先筛行会让「规则存在但未命中」与「规则不存在」重新同形");

        rules[0].FactoryCode.Should().Be("BJ");
        rules[0].FromStageCode.Should().Be("MACH");
        rules[0].ToStageCode.Should().Be("OXID");
        rules[0].WaitMinutes.Should().Be(240);
        rules[0].TimeBasis.Should().Be(StageHandoffTimeBasis.ELAPSED);
        rules[0].IsActive.Should().BeTrue();

        rules[1].FactoryCode.Should().Be("CN6课");
        rules[1].TimeBasis.Should().Be(StageHandoffTimeBasis.WORK);
        rules[1].WaitMinutes.Should().Be(60);

        rules[2].IsActive.Should().BeFalse();
        rules[2].WaitMinutes.Should().Be(0, "显式 0 分钟是合法配置（确实零交接等待），不与「未定义」混同");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_TimeBasis为数值写法_同样解析()
    {
        // Arrange：`TimeBasis` 用**数值** 0/1 写（System.Text.Json 默认枚举写法）。
        // 声明目的：装载侧对**两种既有写法都宽容**——现网 2365 是字符串 `"ELAPSED"`，
        // 而 DTO 默认序列化产物是数值 ⇒ 两种都可能出现在真实载荷里，窄口径会让其中一种静默装不进来。
        const string blockJson = """
        [
          { "FactoryCode": "TJ", "FromStageCode": "TJ_A", "ToStageCode": "TJ_B", "WaitMinutes": 30, "TimeBasis": 0, "IsActive": true },
          { "FactoryCode": "TJ", "FromStageCode": "TJ_B", "ToStageCode": "TJ_C", "WaitMinutes": 45, "TimeBasis": 1, "IsActive": true }
        ]
        """;

        // Act
        var snapshot = await LoadSnapshotWithRawBlockAsync(205, 215, 315, blockJson);

        // Assert
        snapshot.StageHandoffWaitRules.Should().HaveCount(2);
        snapshot.StageHandoffWaitRules[0].TimeBasis.Should().Be(StageHandoffTimeBasis.WORK);
        snapshot.StageHandoffWaitRules[1].TimeBasis.Should().Be(StageHandoffTimeBasis.ELAPSED);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_StageHandoffWaitRules为显式null_空集合且不抛()
    {
        // Arrange：`"StageHandoffWaitRules": null`（缺配置的另一种写法）
        // Act
        var snapshot = await LoadSnapshotWithRawBlockAsync(203, 213, 313, rawBlockJson: "null");

        // Assert
        snapshot.StageHandoffWaitRules.Should().BeEmpty();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_StageHandoffWaitRules为零行空数组_装载失败()
    {
        // Arrange：键存在、值是 `[]`（契约 C9，3号位 2026-10-29 裁决）
        // 声明目的：`[]` **不是**「未配」——它是「配了但零行」的非法形态。若放行，下游再次无法区分
        // 「规则存在但零行」与「根本没配」（这正是当初要求上游取消 OR 并集的原因）⇒ 必须 fail-closed。
        // 与「显式 null 放行」构成对照：两者字面都是「空」，但语义相反，本用例锁死这条边界。
        // Act
        var act = async () => await LoadSnapshotWithRawBlockAsync(206, 216, 316, rawBlockJson: "[]");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*STAGE_HANDOFF_WAIT_BLOCK_EMPTY*");
    }

    [Fact]
    public async System.Threading.Tasks.Task GetFrozenStrategySnapshotAsync_StageHandoffWaitRules类型非法_装载失败()
    {
        // Arrange：块存在但类型非法（字符串而非数组） ⇒ 属「JSON 损坏」，不许静默降级成「合法缺配」
        // Act
        var act = async () => await LoadSnapshotWithRawBlockAsync(204, 214, 314, rawBlockJson: "\"not-an-array\"");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*StageHandoffWaitRules*");
    }
}
