using System.Text.Json;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 冻结快照 <b>进程级缓存</b>在「已发布内容被原地改写」下的行为（R12-1 负例 ＋ B-6 内容指纹，2026-10-30）。
///
/// 【为什么要这组测试】
/// `FrozenStrategySnapshotProvider` 原注释假设「PUBLISHED 版本业务内容不可变（修改必产生新 Version）」，
/// 而现网已有 3 次反例：`PSV 2365` 在 PUBLISHED 下被原地改内容、版本号一次没变
/// （`AuditLog` 8391 WarehousePriority→`[]`／8462 PlanningYields→`[]`／9486 StageHandoffWaitRules 37 行直写 SQL）。
/// 原缓存键只有 `StrategyProfileVersionId`、无失效路径 ⇒ 内容改了、**同一进程内看不到，须重启**。
///
/// 【B-6 之后的两种键语义，本文件各钉一条】
///   ① **注入**了内容指纹读取器（生产 DI 恒注入）⇒ 键 = (VersionId, 指纹)，
///      内容变 ⇒ 指纹变 ⇒ 换键 ⇒ **不清缓存也见新值**；内容不变 ⇒ 仍命中缓存、不回库。
///   ② **未注入**（仅裸 `new` 构造的既有单测路径）⇒ 退回旧语义，内容原地改后仍返回旧值 ——
///      这条留着是为了说明「回退长什么样」，**不代表生产行为**。
///
/// 【隔离】`Cache` 是 `internal static`（进程级），与 `FrozenStrategySnapshotProviderIntegrationTests`
/// 共用同一字典 ⇒ 同 `[Collection("B5Cache")]` 串行、每例前后 `ClearCache()`。
/// </summary>
[Collection("B5Cache")]
public class FrozenStrategySnapshotCacheTests
{
    private const long SpvId = 990001;
    private const long RsvId = 990002;
    private const long PsvId = 990003;

    /// <summary>规则集子块：只需 `DemandPriority`（六块之一，缺块即装载失败）。</summary>
    private static string RuleSetJson() => JsonSerializer.Serialize(new
    {
        DemandPriority = new { }
    });

    /// <summary>参数集子块：六块中的五块全给（`Lock/Supply/Procurement/SolverStrategy/CandidateGuardrail`），
    /// 载体只放一个可观测值 `Supply.Inventory.WarehousePriority`。</summary>
    private static string ParameterSetJson(string warehouse) => JsonSerializer.Serialize(new
    {
        Lock = new { },
        Supply = new { Inventory = new { WarehousePriority = new[] { warehouse } } },
        Procurement = new { },
        SolverStrategy = new { },
        CandidateGuardrail = new { }
    });

    /// <summary>建一套版本对象 + 三个仓储 mock（内容对象可变，模拟「直连 SQL 原地改」）。</summary>
    private static (
        RuleSetVersion RuleSet,
        ParameterSetVersion ParameterSet,
        StrategyProfileVersion StrategyProfile,
        Mock<IRuleSetVersionRepository> RuleSetRepo,
        Mock<IParameterSetVersionRepository> ParameterSetRepo,
        Mock<IStrategyProfileVersionRepository> StrategyProfileRepo) BuildMocks(string warehouse)
    {
        var ruleSet = new RuleSetVersion
        {
            Id = RsvId,
            RuleSetId = 1,
            Status = GovernanceVersionStatus.Published,
            ContentSnapshotJson = RuleSetJson()
        };
        var parameterSet = new ParameterSetVersion
        {
            Id = PsvId,
            ParameterSetId = 1,
            Status = GovernanceVersionStatus.Published,
            ContentSnapshotJson = ParameterSetJson(warehouse)
        };
        var strategyProfile = new StrategyProfileVersion
        {
            Id = SpvId,
            StrategyProfileId = 1,
            RuleSetVersionId = RsvId,
            ParameterSetVersionId = PsvId,
            Status = GovernanceVersionStatus.Published
        };

        var ruleSetRepo = new Mock<IRuleSetVersionRepository>();
        ruleSetRepo.Setup(r => r.GetByIdAsync(RsvId, It.IsAny<CancellationToken>())).ReturnsAsync(ruleSet);

        var parameterSetRepo = new Mock<IParameterSetVersionRepository>();
        parameterSetRepo.Setup(r => r.GetByIdAsync(PsvId, It.IsAny<CancellationToken>())).ReturnsAsync(parameterSet);

        var strategyProfileRepo = new Mock<IStrategyProfileVersionRepository>();
        strategyProfileRepo.Setup(r => r.GetByIdAsync(SpvId, It.IsAny<CancellationToken>())).ReturnsAsync(strategyProfile);

        return (ruleSet, parameterSet, strategyProfile, ruleSetRepo, parameterSetRepo, strategyProfileRepo);
    }

    [Fact]
    public async System.Threading.Tasks.Task 注入内容指纹_同VersionId原地改内容_指纹变即见新值_无需清缓存()
    {
        FrozenStrategySnapshotProvider.ClearCache();

        var mocks = BuildMocks("WH01");

        // 指纹由「库内内容」决定，测试里用一个可变变量代替库内 HASHBYTES 的返回值
        var fingerprint = "FP-1";
        var fingerprintReader = new Mock<ISnapshotContentFingerprintReader>();
        fingerprintReader
            .Setup(r => r.GetStrategyProfileContentFingerprintAsync(SpvId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => fingerprint);

        var provider = new FrozenStrategySnapshotProvider(
            mocks.StrategyProfileRepo.Object,
            mocks.RuleSetRepo.Object,
            mocks.ParameterSetRepo.Object,
            logger: null,
            contentFingerprintReader: fingerprintReader.Object);

        try
        {
            // ── 第一次：指纹 FP-1，冷启动，走 miss 路径、装配并写入缓存 ──
            var first = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            first.Supply.Inventory.WarehousePriority.Should().Equal(new[] { "WH01" });

            // ── 原地改内容、版本号不变：与 PSV 2365 的直写 SQL 路径同形（AuditLog 8391/8462/9486）──
            mocks.ParameterSet.ContentSnapshotJson = ParameterSetJson("WH02");
            fingerprint = "FP-2"; // 库内 HASHBYTES 随之变化

            // ── 第二次：指纹变了 ⇒ 换个键 ⇒ miss ⇒ 重新装配，**不清缓存、不重启也见新值**（B-6 的目的）──
            var second = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            second.Supply.Inventory.WarehousePriority.Should().Equal(
                new[] { "WH02" },
                "内容指纹进了缓存键，同 VersionId 原地改内容后必须读到新值");
            mocks.ParameterSetRepo.Verify(
                r => r.GetByIdAsync(PsvId, It.IsAny<CancellationToken>()), Times.Exactly(2),
                "第二次必须真的回库重新装配，不能拿旧条目凑");

            // ── 有界性：同 VersionId 下旧指纹条目已被清掉，字典不随「原地改次数」增长 ──
            FrozenStrategySnapshotProvider.Cache.Keys
                .Should().ContainSingle(k => k.VersionId == SpvId)
                .Which.Fingerprint.Should().Be("FP-2");

            // ── 第三次：内容与指纹都没再变 ⇒ 命中缓存、不回库（缓存仍有效，不是「每次都重装」）──
            var third = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            third.Supply.Inventory.WarehousePriority.Should().Equal(new[] { "WH02" });
            mocks.ParameterSetRepo.Verify(
                r => r.GetByIdAsync(PsvId, It.IsAny<CancellationToken>()), Times.Exactly(2),
                "指纹未变时应命中缓存，仓储不再查询");
        }
        finally
        {
            FrozenStrategySnapshotProvider.ClearCache();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task 未注入指纹读取器_退回旧键语义_同VersionId原地改内容仍返回旧值()
    {
        FrozenStrategySnapshotProvider.ClearCache();

        var mocks = BuildMocks("WH01");

        // 刻意不传 contentFingerprintReader：模拟既有单测的裸 new 构造（生产 DI 恒注入，走不到这里）
        var provider = new FrozenStrategySnapshotProvider(
            mocks.StrategyProfileRepo.Object,
            mocks.RuleSetRepo.Object,
            mocks.ParameterSetRepo.Object);

        try
        {
            var first = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            first.Supply.Inventory.WarehousePriority.Should().Equal(new[] { "WH01" });

            mocks.ParameterSet.ContentSnapshotJson = ParameterSetJson("WH02");

            var second = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            second.Supply.Inventory.WarehousePriority.Should().Equal(
                new[] { "WH01" },
                "无指纹读取器时键只剩 VersionId，内容原地变了也看不见 —— 这是旧语义，仅测试路径会走到");
            mocks.ParameterSetRepo.Verify(
                r => r.GetByIdAsync(PsvId, It.IsAny<CancellationToken>()), Times.Once,
                "第二次未回库 ⇒ 返回的是缓存旧内容，不是「库里还是旧值」");

            FrozenStrategySnapshotProvider.ClearCache();

            var third = await provider.GetFrozenStrategySnapshotAsync(SpvId, CancellationToken.None);
            third.Supply.Inventory.WarehousePriority.Should().Equal(new[] { "WH02" });
        }
        finally
        {
            FrozenStrategySnapshotProvider.ClearCache();
        }
    }
}
