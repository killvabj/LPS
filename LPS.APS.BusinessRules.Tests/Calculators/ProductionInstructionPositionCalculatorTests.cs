using LPS.APS.BusinessRules.Calculators;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace LPS.APS.BusinessRules.Tests.Calculators;

/// <summary>
/// PI Position Calculator 测试
/// 验证文档中定义的F01-F08场景
/// </summary>
public class ProductionInstructionPositionCalculatorTests
{
    private readonly ProductionInstructionPositionCalculator _calculator;

    public ProductionInstructionPositionCalculatorTests()
    {
        _calculator = new ProductionInstructionPositionCalculator(NullLogger<ProductionInstructionPositionCalculator>.Instance);
    }

    [Test]
    public async Task F01_NormalStageProgress_ShouldCloseTo100()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F01-001",
            MaterialId = 1001,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 80m,
                    SnapshotId = 1
                },
                new StageProgressFact
                {
                    StageCode = "S20",
                    StageSequence = 2,
                    GoodCompletedQty = 50m,
                    SnapshotId = 2
                },
                new StageProgressFact
                {
                    StageCode = "S30",
                    StageSequence = 3,
                    GoodCompletedQty = 20m,
                    SnapshotId = 3
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));
        Assert.That(result.Positions.Count, Is.EqualTo(4));

        var s10Position = result.Positions.First(p => p.StageCode == "S10");
        Assert.That(s10Position.Quantity, Is.EqualTo(30m));

        var s20Position = result.Positions.First(p => p.StageCode == "S20");
        Assert.That(s20Position.Quantity, Is.EqualTo(30m));

        var s30Position = result.Positions.First(p => p.StageCode == "S30");
        Assert.That(s30Position.Quantity, Is.EqualTo(20m));

        var unlocatedPosition = result.Positions.First(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocatedPosition.Quantity, Is.EqualTo(20m));
    }

    [Test]
    public async Task F02_DownstreamGreaterThanUpstream_ShouldCorrectConservatively()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F02-001",
            MaterialId = 1002,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 60m,
                    SnapshotId = 1
                },
                new StageProgressFact
                {
                    StageCode = "S20",
                    StageSequence = 2,
                    GoodCompletedQty = 80m,
                    SnapshotId = 2
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Issues.Any(i => i.IssueType == "DOWNSTREAM_GT_UPSTREAM"), Is.True);

        var s20Position = result.Positions.FirstOrDefault(p => p.StageCode == "S20");
        Assert.That(s20Position, Is.Not.Null);
        Assert.That(s20Position.Quantity, Is.EqualTo(60m));
    }

    [Test]
    public async Task F03_MissingMiddleStage_ShouldUseDownstreamMinimum()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F03-001",
            MaterialId = 1003,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 80m,
                    SnapshotId = 1
                },
                new StageProgressFact
                {
                    StageCode = "S30",
                    StageSequence = 3,
                    GoodCompletedQty = 30m,
                    SnapshotId = 3
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var s10Position = result.Positions.First(p => p.StageCode == "S10");
        Assert.That(s10Position.Quantity, Is.EqualTo(50m));

        var s30Position = result.Positions.First(p => p.StageCode == "S30");
        Assert.That(s30Position.Quantity, Is.EqualTo(30m));
    }

    [Test]
    public async Task F04_XcOverlapsStage_ShouldDeduplicate()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04-001",
            MaterialId = 1004,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 80m,
                    SnapshotId = 1
                },
                new StageProgressFact
                {
                    StageCode = "S20",
                    StageSequence = 2,
                    GoodCompletedQty = 30m,
                    SnapshotId = 2
                }
            },
            XcFacts = new[]
            {
                new XcFact
                {
                    XcWarehouseCode = "XC-WAREHOUSE-01",
                    RelatedStageCode = "S10",
                    Quantity = 20m,
                    AvailableTime = DateTime.Now,
                    SourceDocument = "XC-DOC-001"
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var s10Position = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.STAGE_WAITING && p.StageCode == "S10");
        Assert.That(s10Position, Is.Not.Null);
        Assert.That(s10Position.Quantity, Is.EqualTo(30m));

        var xcPosition = result.Positions.First(p => p.PositionType == PositionType.XC);
        Assert.That(xcPosition.Quantity, Is.EqualTo(20m));
    }

    [Test]
    public async Task F04b_XcSameStageMultipleSlices_DeductsOnceAcrossGroup()
    {
        // 回归 R03-A②：同一 Stage 有两个 STAGE_WAITING 切片（MES 进度派生 S10 + PiInventories 派生 S10），
        // XC 只应把该 Stage 总量扣一次（池化），不得对每个切片各扣同一份 XC 总量（否则过度扣减→Stage 被吞进 UNLOCATED）。
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04B-001",
            MaterialId = 1004,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StagePath = new[]
            {
                new StagePathFact { StageCode = "S10", StageSequence = 1, IsStartStage = true }
            },
            // MES 进度：S10 已完成 40，差分得 STAGE_WAITING 40（ErpRemaining=100 → 末Stage qty=40）
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "S10", StageSequence = 1, GoodCompletedQty = 40m, SnapshotId = 1 }
            },
            // PiInventories：同 S10 再切一片 30 → 同 Stage 两切片(40+30=70)
            PiInventories = new[]
            {
                new PiInventoryFact { WarehouseCode = "WH-A", RelatedStageCode = "S10", Quantity = 30m, LocationCategory = "STAGE_INVENTORY" }
            },
            // 同 Stage 只有 20 的 XC
            XcFacts = new[]
            {
                new XcFact { XcWarehouseCode = "XC-01", RelatedStageCode = "S10", Quantity = 20m, SourceDocument = "D" }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);

        // S10 全部 STAGE_WAITING 切片合计应 = (40+30) - 20 = 50，即 XC 只从该 Stage 总量扣一次。
        // 旧实现会对每个切片各扣 20 → 40-20=20、30-20=10 合计 30（过度扣减 20）。
        var s10Total = result.Positions
            .Where(p => p.PositionType == PositionType.STAGE_WAITING && p.StageCode == "S10")
            .Sum(p => p.Quantity);
        Assert.That(s10Total, Is.EqualTo(50m), "同 Stage 多切片时 XC 应池化只扣一次");

        // 不得误触发 XC_EXCEEDS_STAGE（单切片 30 或 40 都 > 0，总量 70 远大于 XC 20）
        Assert.That(result.Issues.Any(i => i.IssueType == "XC_EXCEEDS_STAGE"), Is.False);
    }

    /// <summary>
    /// F04c（0号位 2026-10-27《R03-27-04》）：跨厂 XC（库存实际厂 ≠ PI 所属厂）
    /// —— 位置照记（物理事实），并登记 XC_CROSS_FACTORY（WARN）；
    /// 位置成立 ≠ 该厂可开工 ⇒ 不产 NextOperationContext；Σ 仍闭合到 ERP Remaining。
    /// </summary>
    [Test]
    public async Task F04c_CrossFactoryXc_ShouldCreatePositionAndWarnIssue()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04C-001",
            MaterialId = 1040,
            MaterialCode = "MAT-F04C",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04C-001",
                    FactoryCode = "BJ",                    // 库存实际厂=BJ，PI 归属厂=CN ⇒ 跨厂
                    XcWarehouseCode = "010699",
                    Quantity = 100m,
                    SnapshotTime = new DateTime(2026, 10, 26, 22, 6, 0)
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 位置照记：跨厂不剔除、不降级
        var xc = result.Positions.Single(p => p.PositionType == PositionType.XC);
        Assert.That(xc.Quantity, Is.EqualTo(100m));
        Assert.That(xc.LocationKey, Is.EqualTo("010699"));

        // 跨厂留痕：恰好一条 XC_CROSS_FACTORY，携带量
        var crossIssues = result.Issues.Where(i => i.IssueType == "XC_CROSS_FACTORY").ToList();
        Assert.That(crossIssues, Has.Count.EqualTo(1));
        Assert.That(crossIssues[0].Level, Is.EqualTo(PositionIssueLevel.WARN));
        Assert.That(crossIssues[0].AffectedQuantity, Is.EqualTo(100m));

        // 位置成立 ≠ 开工起点：XC 不进 NextOperationContext（同厂也如此，跨厂不得新增）
        Assert.That(result.NextOperationContexts, Is.Empty);

        // 总量闭合不破
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// F04f（R03-27-04 §二.5 类④/⑤复合例）：同 PI 同厂＋异厂两行 —— 两位置片都在、
    /// 仅异厂行出一条 Issue、Σ 守恒（不因跨厂输入增加任何总量）。
    /// </summary>
    [Test]
    public async Task F04f_MixedFactoryXc_EachCrossRowOwnsIssue_ClosurePreserved()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04F-001",
            MaterialId = 1046,
            MaterialCode = "MAT-F04F",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04F-001",
                    FactoryCode = "CN",                    // 同厂
                    XcWarehouseCode = "510699",
                    Quantity = 30m
                },
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04F-001",
                    FactoryCode = "BJ",                    // 异厂
                    XcWarehouseCode = "010699",
                    Quantity = 70m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.Positions.Count(p => p.PositionType == PositionType.XC), Is.EqualTo(2));

        var crossIssues = result.Issues.Where(i => i.IssueType == "XC_CROSS_FACTORY").ToList();
        Assert.That(crossIssues, Has.Count.EqualTo(1), "仅异厂行出 Issue，同厂行不得出");
        Assert.That(crossIssues[0].AffectedQuantity, Is.EqualTo(70m));

        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// F04g 护栏：同厂 XC 不得出 XC_CROSS_FACTORY（误报=污染 Issue 台账）。
    /// </summary>
    [Test]
    public async Task F04g_SameFactoryXc_ShouldNotRaiseCrossFactoryIssue()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04G-001",
            MaterialId = 1047,
            FactoryCode = "CN",
            ErpRemainingQty = 50m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04G-001",
                    FactoryCode = "CN",
                    XcWarehouseCode = "510699",
                    Quantity = 50m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.Positions.Count(p => p.PositionType == PositionType.XC), Is.EqualTo(1));
        Assert.That(result.Issues.Any(i => i.IssueType == "XC_CROSS_FACTORY"), Is.False);
    }

    /// <summary>
    /// F04h 护栏：厂不明（XcFact.FactoryCode=null 或 input.FactoryCode 空）不参与判定、
    /// 不出 Issue——订阅方构造的非 PI 级行/旧样本保持既有行为（R03-27-04 §二.2 只裁"有真实厂事实"的行）。
    /// </summary>
    [Test]
    public async Task F04h_UnknownFactoryXc_ShouldNotRaiseCrossFactoryIssue()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04H-001",
            MaterialId = 1048,
            FactoryCode = "CN",
            ErpRemainingQty = 50m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04H-001",
                    FactoryCode = null,                    // 厂不明
                    XcWarehouseCode = "XC-01",
                    Quantity = 50m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.Positions.Count(p => p.PositionType == PositionType.XC), Is.EqualTo(1));
        Assert.That(result.Issues.Any(i => i.IssueType == "XC_CROSS_FACTORY"), Is.False);
    }

    /// <summary>
    /// F04i（0号位 2026-10-28《R03-27-04 新DTO最小联测》件2·轨迹2）：PI 号合法但 XC 源物料与
    /// PI 物料不符（r13926 起该事实照入池、取舍归 5号位）——
    /// 不得计为有效 XC 位置；差额经既有闭合落 UNLOCATED（复用优先）；
    /// 另出一条可追溯 WARN（哪个仓、源物料 vs PI 物料、量），因既有 Issue 均不携带错配原因，
    /// 属"实际不适配⇒最小自举"的新增码 XC_MATERIAL_MISMATCH。
    /// </summary>
    [Test]
    public async Task F04i_SamePiForeignMaterialXc_ShouldNotCountAsValidXc()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04I-001",
            MaterialId = 1049,
            MaterialCode = "MAT-F04I",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04I-001",
                    MaterialCode = "MAT-F04I",             // 物料相符
                    FactoryCode = "CN",
                    XcWarehouseCode = "510699",
                    Quantity = 30m
                },
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04I-001",
                    MaterialCode = "MAT-OTHER-PI",         // PI号合法、源物料不符 ⇒ 不得计有效XC
                    FactoryCode = "CN",
                    XcWarehouseCode = "010699",
                    Quantity = 40m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 有效 XC 位置只有一条（30），错配 40 不得成位
        var xcPositions = result.Positions.Where(p => p.PositionType == PositionType.XC).ToList();
        Assert.That(xcPositions, Has.Count.EqualTo(1));
        Assert.That(xcPositions[0].LocationKey, Is.EqualTo("510699"));
        Assert.That(xcPositions[0].Quantity, Is.EqualTo(30m));

        // 可追溯异常：一条 XC_MATERIAL_MISMATCH（WARN），带源物料/PI物料/仓/量
        var mismatches = result.Issues.Where(i => i.IssueType == "XC_MATERIAL_MISMATCH").ToList();
        Assert.That(mismatches, Has.Count.EqualTo(1));
        Assert.That(mismatches[0].Level, Is.EqualTo(PositionIssueLevel.WARN));
        Assert.That(mismatches[0].AffectedQuantity, Is.EqualTo(40m));

        // 闭合复用 UNLOCATED：错配份额落 UNLOCATED=70（100−30），Σ 仍等于 ERP Remaining
        var unloc = result.Positions.Single(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unloc.Quantity, Is.EqualTo(70m));
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// F04j 护栏：源物料不明（XcFact.MaterialCode=null，订阅方旧构造/非PI级行）不参与比较、
    /// 行为与 r13923 完全一致（位置保留、不出错配 Issue）——null=不明，不猜。
    /// </summary>
    [Test]
    public async Task F04j_UnknownSourceMaterialXc_ShouldKeepLegacyBehavior()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04J-001",
            MaterialId = 1050,
            MaterialCode = "MAT-F04J",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 50m,
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04J-001",
                    MaterialCode = null,                   // 源物料不明
                    FactoryCode = "CN",
                    XcWarehouseCode = "510699",
                    Quantity = 50m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.Positions.Count(p => p.PositionType == PositionType.XC), Is.EqualTo(1));
        Assert.That(result.Issues.Any(i => i.IssueType == "XC_MATERIAL_MISMATCH"), Is.False);
    }

    [Test]
    public async Task F04k_FirstStageInProgressNotCompleted_ShouldProduceStageWaitingPosition()
    {
        // 2号位 10-31《Stage 部分完工态落 UNLOCATED》§3.4 样例 PI=17145065：
        // Stage 层 GoodCompletedQty=0（整 Stage 未完工），但 4 道工步中 3 道已完工 30。
        // 冻结《Pegging供需承接》§9.2「MACH未完成100」⇒ Stage 未完工量本身就是一个位置。
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "17145065",
            MaterialId = 2001,
            MaterialCode = "FINAL_FG-25A-MHZ2-10D",
            FactoryId = 2,
            ErpRemainingQty = 30m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", StageSequence = 10,
                    PlannedQty = 30m, GoodCompletedQty = 0m, RemainingQty = 30m, SnapshotId = 1
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 10, IsStartStage = true }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-1", OperationName = "滑台组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 0m, RemainingQty = 30m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-2", OperationName = "前盖ASSY", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 30m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-3", OperationName = "主体Assy", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 30m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-4", OperationName = "组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 30m, RemainingQty = 0m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.UNLOCATED), Is.False,
            "Stage 已开工未完工的存量不得落 UNLOCATED");

        var stageWaiting = result.Positions.Single(p => p.PositionType == PositionType.STAGE_WAITING);
        Assert.That(stageWaiting.StageCode, Is.EqualTo("CN_ASSY"));
        Assert.That(stageWaiting.Quantity, Is.EqualTo(30m));
    }

    [Test]
    public async Task F04l_FirstStageNotStarted_ShouldStayFirstStagePending()
    {
        // 无任何工步完工 ⇒ 仍是"首Stage待生产"，不得因残差项改判为 STAGE_WAITING
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04L-001",
            MaterialId = 1051,
            FactoryId = 2,
            ErpRemainingQty = 30m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", StageSequence = 10,
                    PlannedQty = 30m, GoodCompletedQty = 0m, RemainingQty = 30m, SnapshotId = 1
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 10, IsStartStage = true }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-1", OperationName = "滑台组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 0m, RemainingQty = 30m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.UNLOCATED), Is.False);
        var pending = result.Positions.Single(p => p.PositionType == PositionType.FIRST_STAGE_PENDING);
        Assert.That(pending.StageCode, Is.EqualTo("CN_ASSY"));
        Assert.That(pending.Quantity, Is.EqualTo(30m));
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.STAGE_WAITING), Is.False);
    }

    [Test]
    public async Task F04m_FirstStageResidualExceedsRemaining_ShouldCapAndWarn()
    {
        // Stage 计划量(100) > PI 剩余量(60)：残差不得放大位置总量 ⇒ 封顶到 60 + WARN
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04M-001",
            MaterialId = 1052,
            FactoryId = 2,
            ErpRemainingQty = 60m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", StageSequence = 10,
                    PlannedQty = 100m, GoodCompletedQty = 0m, RemainingQty = 100m, SnapshotId = 1
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 10, IsStartStage = true }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-1", OperationName = "滑台组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 100m, GoodQty = 20m, RemainingQty = 80m
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(60m));
        Assert.That(result.Issues.Any(i => i.IssueType == "QUANTITY_OVERFLOW"), Is.False);
        var stageWaiting = result.Positions.Single(p => p.PositionType == PositionType.STAGE_WAITING);
        Assert.That(stageWaiting.Quantity, Is.EqualTo(60m));
        Assert.That(result.Issues.Any(i => i.IssueType == "FIRST_STAGE_RESIDUAL_CAPPED"), Is.True);
    }

    [Test]
    public async Task F04n_FirstStageResidualWithXc_ShouldNotDoubleDeduct()
    {
        // 残差是"未完成"量、XC 是"已完成"量的另一位置：互不扣减，总量按闭合缺口分配
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F04N-001",
            MaterialId = 1053,
            MaterialCode = "MAT-F04N",
            FactoryId = 2,
            FactoryCode = "CN",
            ErpRemainingQty = 30m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", StageSequence = 10,
                    PlannedQty = 30m, GoodCompletedQty = 0m, RemainingQty = 30m, SnapshotId = 1
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 10, IsStartStage = true }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-1", OperationName = "滑台组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-1", PlannedQty = 30m, GoodQty = 10m, RemainingQty = 20m
                }
            },
            XcFacts = new[]
            {
                new XcFact
                {
                    ProductionInstructionNo = "PI-F04N-001",
                    MaterialCode = "MAT-F04N",
                    FactoryCode = "CN",
                    XcWarehouseCode = "010699",
                    Quantity = 10m,
                    RelatedStageCode = "CN_ASSY"
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(30m));
        Assert.That(result.Positions.Single(p => p.PositionType == PositionType.XC).Quantity, Is.EqualTo(10m));
        Assert.That(result.Positions.Single(p => p.PositionType == PositionType.STAGE_WAITING).Quantity, Is.EqualTo(20m));
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.UNLOCATED), Is.False);
    }

    [Test]
    public async Task F05_TransitIndependent_ShouldNotOverlapStage()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F05-001",
            MaterialId = 1005,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 60m,
                    SnapshotId = 1
                }
            },
            TransitFacts = new[]
            {
                new InterplantTransitFact
                {
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "BJ",
                    Quantity = 25m,
                    EstimatedArrivalTime = DateTime.Now.AddDays(2),
                    TransitDocumentNo = "TRANSIT-001"
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var transitPosition = result.Positions.First(p => p.PositionType == PositionType.INTERPLANT_TRANSIT);
        Assert.That(transitPosition.Quantity, Is.EqualTo(25m));

        // F05规则：Transit与Stage互斥去重，Transit应从Stage扣除
        // Stage原始60 - Transit 25 = 35
        var stagePosition = result.Positions.First(p => p.PositionType == PositionType.STAGE_WAITING);
        Assert.That(stagePosition.Quantity, Is.EqualTo(35m), "Transit should be deducted from Stage (60 - 25 = 35)");

        // 剩余40应该进入UNLOCATED（Stage 60被Transit 25扣除后=35，100-35-25=40）
        var unlocatedPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.UNLOCATED);
        if (unlocatedPosition != null)
        {
            Assert.That(unlocatedPosition.Quantity, Is.EqualTo(40m));
        }
    }

    [Test]
    public async Task F06_UnlocatedGap_ShouldFillWithUnlocated()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F06-001",
            MaterialId = 1006,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 85m,
                    SnapshotId = 1
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var unlocatedPosition = result.Positions.First(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocatedPosition.Quantity, Is.EqualTo(15m));
        Assert.That(unlocatedPosition.IsUnlocated, Is.True);

        Assert.That(result.Issues.Any(i => i.IssueType == "UNLOCATED_GAP"), Is.True);
    }

    [Test]
    public async Task F07_StrongFactCorrection_ShouldAdjustPosition()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F07-001",
            MaterialId = 1007,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 80m,
                    SnapshotId = 1
                },
                new StageProgressFact
                {
                    StageCode = "S20",
                    StageSequence = 2,
                    GoodCompletedQty = 40m,
                    SnapshotId = 2
                }
            },
            StrongFacts = new[]
            {
                new ReceivedFact
                {
                    RelatedStageCode = "S10",
                    Quantity = 30m,
                    ReceivedAt = DateTime.Now,
                    DocumentNo = "PI-F07-001",
                    DocumentType = "PI"
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var s10Position = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.STAGE_WAITING && p.StageCode == "S10");
        if (s10Position != null)
        {
            Assert.That(s10Position.Quantity, Is.EqualTo(10m));
        }
    }

    [Test]
    public async Task F08_MultiplePiSameMaterial_ShouldReturnSeparateResults()
    {
        var inputs = new[]
        {
            new ProductionInstructionPositionInput
            {
                ProductionInstructionNo = "PI-F08-001",
                MaterialId = 1008,
                FactoryId = 1,
                ErpRemainingQty = 100m,
                StageProgress = new[]
                {
                    new StageProgressFact
                    {
                        StageCode = "S10",
                        StageSequence = 1,
                        GoodCompletedQty = 80m,
                        SnapshotId = 1
                    }
                }
            },
            new ProductionInstructionPositionInput
            {
                ProductionInstructionNo = "PI-F08-002",
                MaterialId = 1008,
                FactoryId = 1,
                ErpRemainingQty = 50m,
                StageProgress = new[]
                {
                    new StageProgressFact
                    {
                        StageCode = "S10",
                        StageSequence = 1,
                        GoodCompletedQty = 30m,
                        SnapshotId = 2
                    }
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(inputs, new FrozenFactParameters(), CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(2));

        var result1 = results.First(r => r.ProductionInstructionNo == "PI-F08-001");
        Assert.That(result1.IsSuccess, Is.True);
        Assert.That(result1.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var result2 = results.First(r => r.ProductionInstructionNo == "PI-F08-002");
        Assert.That(result2.IsSuccess, Is.True);
        Assert.That(result2.Positions.Sum(p => p.Quantity), Is.EqualTo(50m));
    }

    // ============================================================================
    // P0-11警告：以下F09-F12测试属于跨厂订单链（INTER_FACTORY_ORDER），不属于PI Position Calculator
    //
    // 根据APS_V1_5号位代码第一轮综合符合性审核报告_冻结基线核对版_v1.0_20260820：
    // - F09-F12涉及SH（出荷指示）Transit和Received的同单号扣减逻辑
    // - SH内部Transit/Received不属于PI Position Calculator职责
    // - 这些测试需要迁移到独立的跨厂订单链测试类中
    // - 当前ProductionInstructionPositionCalculator已移除F10-F12逻辑
    //
    // 整改要求：
    // 1. 创建新测试类 InterFactoryOrderCalculatorTests 或类似名称
    // 2. 迁移F09-F12测试到该新类中
    // 3. 按跨厂订单链的正确业务语义重新设计测试
    // 4. 不新增2↔5接口字段
    // ============================================================================

    [Test]
    public async Task F09_StageHandoff_ShouldReturnPITransitWithoutShippingTask()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F09-001",
            MaterialId = 3001,
            FactoryId = 1,
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 50m,
                    SnapshotId = 1
                }
            },
            TransitFacts = new[]
            {
                new InterplantTransitFact
                {
                    TransitDocumentNo = "TRANSIT-001",
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "TJ",
                    Quantity = 50m,
                    SourceDocument = "SH-20260819-001",
                    ShippedAt = DateTime.Now.AddDays(-1)
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        var transitPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.INTERPLANT_TRANSIT);
        Assert.That(transitPosition, Is.Not.Null);
        Assert.That(transitPosition.Quantity, Is.EqualTo(50m));

        // Transit(50) 与 Stage(50) 去重后，Stage 被完全扣除
        var stagePosition = result.Positions.FirstOrDefault(p => p.StageCode == "S10");
        Assert.That(stagePosition, Is.Null, "Stage fully consumed by Transit deduction should be removed");

        // 剩余50进入UNLOCATED
        var unlocatedPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocatedPosition, Is.Not.Null);
        Assert.That(unlocatedPosition.Quantity, Is.EqualTo(50m));
    }

    [Test]
    public async Task F10_SameSHReceived_ShouldMatchCorrectly()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F10-001",
            MaterialId = 3002,
            FactoryId = 2,
            ErpRemainingQty = 100m,
            TransitFacts = new[]
            {
                new InterplantTransitFact
                {
                    TransitDocumentNo = "TRANSIT-002",
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "TJ",
                    Quantity = 60m,
                    SourceDocument = "SH-20260819-002",
                    ShippedAt = DateTime.Now.AddDays(-2)
                }
            },
            StrongFacts = new[]
            {
                new ReceivedFact
                {
                    DocumentNo = "SH-20260819-002",
                    DocumentType = "SH",
                    Quantity = 30m,
                    ReceivedAt = DateTime.Now,
                    WarehouseCode = "TJ-WH01",
                    RelatedStageCode = "S10"
                }
            },
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 30m,
                    SnapshotId = 1
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        // SH DocumentType 被 PI Position Calculator 拒绝（RECEIVED_SHIPPING_IN_PI_POSITION）
        // Transit(60) 完整保留，Stage(30) 被 Transit 完全扣除
        var transitPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.INTERPLANT_TRANSIT);
        Assert.That(transitPosition, Is.Not.Null);
        Assert.That(transitPosition.Quantity, Is.EqualTo(60m), "Transit preserved - SH Received rejected by PI Position Calculator");

        // Stage(30) 被 Transit 完全扣除移除
        var stagePosition = result.Positions.FirstOrDefault(p => p.StageCode == "S10");
        Assert.That(stagePosition, Is.Null, "Stage fully consumed by Transit deduction");

        // 差额40进入UNLOCATED（Stage 30被Transit 60完全扣除，100-60=40）
        var unlocatedPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocatedPosition, Is.Not.Null);
        Assert.That(unlocatedPosition.Quantity, Is.EqualTo(40m));

        // 验证 RECEIVED_SHIPPING_IN_PI_POSITION Issue 已生成
        Assert.That(result.Issues.Any(i => i.IssueType == "RECEIVED_SHIPPING_IN_PI_POSITION"), Is.True);
    }

    [Test]
    public async Task F11_DifferentSHSameMaterial_ShouldNotMix()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F11-001",
            MaterialId = 3003,
            FactoryId = 2,
            ErpRemainingQty = 100m,
            TransitFacts = new[]
            {
                new InterplantTransitFact
                {
                    TransitDocumentNo = "TRANSIT-003A",
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "TJ",
                    Quantity = 40m,
                    SourceDocument = "SH-20260819-003A",
                    ShippedAt = DateTime.Now.AddDays(-2)
                },
                new InterplantTransitFact
                {
                    TransitDocumentNo = "TRANSIT-003B",
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "TJ",
                    Quantity = 60m,
                    SourceDocument = "SH-20260819-003B",
                    ShippedAt = DateTime.Now.AddDays(-1)
                }
            },
            StrongFacts = new[]
            {
                new ReceivedFact
                {
                    DocumentNo = "SH-20260819-003A",
                    DocumentType = "SH",
                    Quantity = 40m,
                    ReceivedAt = DateTime.Now,
                    WarehouseCode = "TJ-WH01",
                    RelatedStageCode = "S10"
                }
            },
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 40m,
                    SnapshotId = 1
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        // SH DocumentType 被 PI Position Calculator 拒绝
        // 两笔 Transit(40+60=100) 完整保留，Stage(40) 被 Transit 去重完全扣除
        var transitPositions = result.Positions.Where(p => p.PositionType == PositionType.INTERPLANT_TRANSIT).ToList();
        Assert.That(transitPositions.Count, Is.EqualTo(2), "Both Transit facts preserved");
        Assert.That(transitPositions.Sum(t => t.Quantity), Is.EqualTo(100m));

        // Stage(40) 被 Transit 去重完全扣除
        var stagePosition = result.Positions.FirstOrDefault(p => p.StageCode == "S10");
        Assert.That(stagePosition, Is.Null, "Stage fully consumed by Transit deduction");

        // 验证 RECEIVED_SHIPPING_IN_PI_POSITION Issue
        Assert.That(result.Issues.Any(i => i.IssueType == "RECEIVED_SHIPPING_IN_PI_POSITION"), Is.True);
    }

    [Test]
    public async Task F12_TransitAlreadyReceived_ShouldNotDuplicate()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F12-001",
            MaterialId = 3004,
            FactoryId = 2,
            ErpRemainingQty = 100m,
            TransitFacts = new[]
            {
                new InterplantTransitFact
                {
                    TransitDocumentNo = "TRANSIT-004",
                    SourceFactoryCode = "CN",
                    TargetFactoryCode = "TJ",
                    Quantity = 50m,
                    SourceDocument = "SH-20260819-004",
                    ShippedAt = DateTime.Now.AddDays(-3)
                }
            },
            StrongFacts = new[]
            {
                new ReceivedFact
                {
                    DocumentNo = "SH-20260819-004",
                    DocumentType = "SH",
                    Quantity = 50m,
                    ReceivedAt = DateTime.Now,
                    WarehouseCode = "TJ-WH01",
                    RelatedStageCode = "S10"
                }
            },
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "S10",
                    StageSequence = 1,
                    GoodCompletedQty = 50m,
                    SnapshotId = 1
                }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(100m));

        // SH DocumentType 被 PI Position Calculator 拒绝（RECEIVED_SHIPPING_IN_PI_POSITION）
        // Transit(50) 完整保留，Stage(50) 被 Transit 去重完全扣除
        var transitPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.INTERPLANT_TRANSIT);
        Assert.That(transitPosition, Is.Not.Null, "Transit preserved - SH Received rejected");
        Assert.That(transitPosition.Quantity, Is.EqualTo(50m));

        // Stage(50) 被 Transit 去重完全扣除
        var stagePosition = result.Positions.FirstOrDefault(p => p.StageCode == "S10");
        Assert.That(stagePosition, Is.Null, "Stage fully consumed by Transit deduction");

        // 差额50进入UNLOCATED
        var unlocatedPosition = result.Positions.FirstOrDefault(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocatedPosition, Is.Not.Null);
        Assert.That(unlocatedPosition.Quantity, Is.EqualTo(50m));

        // 验证 RECEIVED_SHIPPING_IN_PI_POSITION Issue
        Assert.That(result.Issues.Any(i => i.IssueType == "RECEIVED_SHIPPING_IN_PI_POSITION"), Is.True);
    }

    /// <summary>
    /// U03（TECH-03 §八）：PI 剩余 600，各互斥 Position 合计 550 ⇒ 额外 UNLOCATED 50，最终合计 600。
    /// 互斥位置由 Stage 累计差分得出：S10(累计550) − S20(累计300) = S10 250；S20 = 300；合计 550。
    /// </summary>
    [Test]
    public async Task U03_MutuallyExclusivePositions550_UnlocatedFills50_TotalCloses600()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U03-001",
            MaterialId = 1003,
            FactoryId = 1,
            ErpRemainingQty = 600m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "S10", StageSequence = 1, GoodCompletedQty = 550m, SnapshotId = 1 },
                new StageProgressFact { StageCode = "S20", StageSequence = 2, GoodCompletedQty = 300m, SnapshotId = 2 }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "S10", StageSequence = 1, IsStartStage = true },
                new StagePathFact { StageCode = "S20", StageSequence = 2 }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 差分消重后的互斥位置合计 = 550（S10 250 + S20 300）
        var located = result.Positions.Where(p => p.PositionType != PositionType.UNLOCATED).ToList();
        Assert.That(located.Sum(p => p.Quantity), Is.EqualTo(550m));

        // 缺口 50 归 UNLOCATED，最终总量闭合到 600
        var unlocated = result.Positions.First(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocated.Quantity, Is.EqualTo(50m));
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// U05（TECH-03 §八）：PI 剩余 600，两个 Stage 原始累计各 500、指向同一批重叠数量。
    /// 断言：不得直接 MAX/SUM 当 PI 总量；Stage 差分消重后互斥位置合计 ≤ 600（本例 = 500，非 SUM=1000）。
    /// </summary>
    [Test]
    public async Task U05_OverlappingMultiStageRawValues_DedupedByDiff_NotSummed()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U05-001",
            MaterialId = 1005,
            FactoryId = 1,
            ErpRemainingQty = 600m,
            StageProgress = new[]
            {
                // 两个 Stage 原始累计各 500：S20 是最后一段，S10 = 500 − 500 = 0（重叠，不记录）
                new StageProgressFact { StageCode = "S10", StageSequence = 1, GoodCompletedQty = 500m, SnapshotId = 1 },
                new StageProgressFact { StageCode = "S20", StageSequence = 2, GoodCompletedQty = 500m, SnapshotId = 2 }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "S10", StageSequence = 1, IsStartStage = true },
                new StagePathFact { StageCode = "S20", StageSequence = 2 }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 差分消重：互斥位置合计 500（不是 SUM 1000），且 ≤ ERP 总量 600
        var located = result.Positions.Where(p => p.PositionType != PositionType.UNLOCATED).ToList();
        Assert.That(located.Sum(p => p.Quantity), Is.EqualTo(500m));
        Assert.That(located.Sum(p => p.Quantity), Is.LessThanOrEqualTo(600m));

        // 总量仍闭合到入参 600（缺口 100 归 UNLOCATED），Stage 原始值不扩大 PI 总量
        var unlocated = result.Positions.First(p => p.PositionType == PositionType.UNLOCATED);
        Assert.That(unlocated.Quantity, Is.EqualTo(100m));
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// U04（TECH-03 §八）：PI 剩余 600，但互斥位置算出 650（Stage S10=600 ＋ 异 Stage 的 XC=50 未被扣）。
    /// 断言（冻结 Pegging v1.6 §9.7／§18.1／§24 + TECH-03 U04「Issue/UNLOCATED 或冻结允许的保守降级」）：
    /// 不扩大 PI 至 650；**该 PI 剩余整体降级为 UNLOCATED（=600）**；
    /// QUANTITY_OVERFLOW 登记（超出量 50，错误不静默）；总量闭合 ⇒ IsSuccess=true，Domain 继续。
    /// </summary>
    [Test]
    public async Task U04_ExclusivePositionsExceedErp_ShouldDowngradeWholePiToUnlocated()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U04-001",
            MaterialId = 1004,
            FactoryId = 1,
            ErpRemainingQty = 600m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "S10", StageSequence = 1, GoodCompletedQty = 600m, SnapshotId = 1 }
            },
            // XC 关联 S20（无 S20 的 Stage 位置），故不被 Stage 去重扣除 ⇒ 互斥位置合计 = 600 + 50 = 650
            XcFacts = new[]
            {
                new XcFact
                {
                    XcWarehouseCode = "XC-WAREHOUSE-01",
                    RelatedStageCode = "S20",
                    Quantity = 50m,
                    AvailableTime = DateTime.Now,
                    SourceDocument = "XC-DOC-U04"
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "S10", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 不扩大 PI 至 650、也不静默改小：整体降级为单条 UNLOCATED，数量 = PI 剩余 600
        Assert.That(result.Positions.Count, Is.EqualTo(1));
        var downgraded = result.Positions.Single();
        Assert.That(downgraded.PositionType, Is.EqualTo(PositionType.UNLOCATED));
        Assert.That(downgraded.Quantity, Is.EqualTo(600m));
        Assert.That(downgraded.SourceKey, Is.EqualTo("OVERFLOW_DOWNGRADE"));
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.STAGE_WAITING), Is.False);
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.XC), Is.False);

        // 错误不静默：QUANTITY_OVERFLOW 登记，超出量 50
        var overflow = result.Issues.FirstOrDefault(i => i.IssueType == "QUANTITY_OVERFLOW");
        Assert.That(overflow, Is.Not.Null);
        Assert.That(overflow.AffectedQuantity, Is.EqualTo(50m));

        // 总量闭合（Σ=入参 600）⇒ Domain 继续（§18.1「单PI事实冲突」可降级）
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);
    }

    /// <summary>
    /// U14（TECH-03 §八）：同 PI 一个 MES WO 多个 Slice —— 多个 Slice 保持同一 WO 身份；
    /// Position / NextOperation 数量**不重叠扩张**（Σ 均为 PI 总量，不因多 Slice 翻倍）。
    /// </summary>
    [Test]
    public async Task U14_SameWorkOrderMultiSlice_PositionAndNextOpNotOverlapExpanded()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U14-001",
            MaterialId = 1014,
            MaterialCode = "MAT-U14",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 500m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 500m, SnapshotId = 1 }
            },
            // 同一 WO 两道平行工序各有剩余 → 中间态多 Slice
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "挤丝", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 500m, GoodQty = 200m, RemainingQty = 300m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-B", OperationName = "磨削", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 500m, GoodQty = 300m, RemainingQty = 200m
                }
            },
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-U14-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-U14", PlannedQty = 500m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // Position 侧：总量闭合（不因多 Slice 扩张）
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(500m));

        // 同一 WO 的多 Slice：结构正确、Σ 不重叠扩张（=500，非 2×500）
        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.MESWorkOrderNo, Is.EqualTo("WO-001"));
        Assert.That(ctx.Slices.Count, Is.EqualTo(2));
        Assert.That(ctx.Slices.Sum(s => s.SliceQty), Is.EqualTo(500m));
        Assert.That(ctx.Slices.All(s => s.StartStageCode == "CN_ASSY"), Is.True);

        // NextOperation 侧：Σ SliceQty = Position 总量（不重叠扩张）
        Assert.That(result.NextOperationContexts.Sum(c => c.SliceQty), Is.EqualTo(500m));
    }

    // ── A/B 路径身份 PathId（2026-10-22，2号位《上级日历真实事实与A-B片路径身份》§二）──────

    private static ProductionInstructionPositionInput BuildPathIdInput(
        string woRouteCode, RoutingOperationFact[] routing) => new()
    {
        ProductionInstructionNo = "PI-PATHID-001",
        MaterialId = 1090,
        MaterialCode = "MAT-PATHID",
        FactoryId = 1,
        FactoryCode = "CN",
        ErpRemainingQty = 500m,
        StageProgress = new[]
        {
            new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 500m, SnapshotId = 1 }
        },
        OperationProgress = new[]
        {
            new OperationProgressFact
            {
                OperationCode = "OP-A", OperationName = "挤丝", StageCode = "CN_ASSY",
                MESWorkOrderNo = "WO-001", PlannedQty = 500m, GoodQty = 200m, RemainingQty = 300m,
                RouteCode = woRouteCode
            }
        },
        WorkOrders = new[]
        {
            new WorkOrderSnapshotFact
            {
                ProductionInstructionNo = "PI-PATHID-001", MESWorkOrderNo = "WO-001",
                MaterialCode = "MAT-PATHID", PlannedQty = 500m,
                WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
            }
        },
        StagePath = new[]
        {
            new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
        },
        RoutingOperations = routing
    };

    [Test]
    public async Task AbPathId_UniqueRouteTextMatch_ShouldDeliverRealPathId()
    {
        var input = BuildPathIdInput("挤丝,磨削,组装", new[]
        {
            new RoutingOperationFact { OperationCode = "J1", OperationName = "挤丝", StageCode = "CN_ASSY", RouteCode = "挤丝,磨削,组装", PathId = 7 },
            new RoutingOperationFact { OperationCode = "J2", OperationName = "磨削", StageCode = "CN_ASSY", RouteCode = "挤丝,磨削,组装", PathId = 7 }
        });

        var result = (await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None)).First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.RouteCode, Is.EqualTo("挤丝,磨削,组装"));
        Assert.That(ctx.PathId, Is.EqualTo(7), "文本唯一命中（多工序同行号）⇒ 交付真实 PathId");
        Assert.That(result.Issues.Any(i => i.IssueType.StartsWith("AB_PATH_ID")), Is.False);
    }

    [Test]
    public async Task AbPathId_AmbiguousRouteText_ShouldStayNullWithErrorAndNotPickOne()
    {
        var input = BuildPathIdInput("挤丝,磨削,组装", new[]
        {
            new RoutingOperationFact { OperationCode = "J1", OperationName = "挤丝", StageCode = "CN_ASSY", RouteCode = "挤丝,磨削,组装", PathId = 7 },
            new RoutingOperationFact { OperationCode = "J1b", OperationName = "挤丝", StageCode = "CN_ASSY", RouteCode = "挤丝,磨削,组装", PathId = 9 }
        });

        var result = (await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None)).First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.RouteCode, Is.EqualTo("挤丝,磨削,组装"), "RouteCode 真值照常交付");
        Assert.That(ctx.PathId, Is.Null, "同文本多 PathId（C桶假歧义）⇒ 留 null，绝不择一（Q-3）");
        Assert.That(result.Issues.Any(i => i.IssueType == "AB_PATH_ID_AMBIGUOUS"), Is.True);
    }

    [Test]
    public async Task AbPathId_NoRouteTextMatch_ShouldStayNullWithWarn()
    {
        var input = BuildPathIdInput("挤丝,未知工序X", new[]
        {
            new RoutingOperationFact { OperationCode = "J1", OperationName = "挤丝", StageCode = "CN_ASSY", RouteCode = "挤丝,磨削,组装", PathId = 7 }
        });

        var result = (await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None)).First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.PathId, Is.Null, "无匹配路径 ⇒ 留 null，1号位 Fail Closed 为预期");
        Assert.That(result.Issues.Any(i => i.IssueType == "AB_PATH_ID_NOT_RESOLVED"), Is.True);
    }

    // ── PI 级 XC 源不可用（2026-10-25，2号位《PI级XC缺源时Position侧表达位询问》）──────

    private static ProductionInstructionPositionInput BuildPiXcUnavailableInput(bool unavailable) => new()
    {
        ProductionInstructionNo = "PI-XCUNAVAIL-001",
        MaterialId = 1091,
        MaterialCode = "MAT-XCU",
        FactoryId = 1,
        FactoryCode = "CN",
        ErpRemainingQty = 500m,
        StageProgress = Array.Empty<StageProgressFact>(),
        OperationProgress = Array.Empty<OperationProgressFact>(),
        WorkOrders = new[]
        {
            new WorkOrderSnapshotFact
            {
                ProductionInstructionNo = "PI-XCUNAVAIL-001", MESWorkOrderNo = "WO-X1",
                MaterialCode = "MAT-XCU", PlannedQty = 500m,
                WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
            }
        },
        StagePath = new[] { new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true } },
        PiXcSourceUnavailable = unavailable
    };

    /// <summary>
    /// 缺源时：①闭合不破（份额落 UNLOCATED）；②产 PI_XC_SOURCE_UNAVAILABLE Issue；
    /// ③**抑制 ERP 兜底**（DerivedRemainingQty 0 vs 关闭时 500），防止未知 XC 份额流入 A/B 片造成过排；
    /// ④ UNLOCATED 不再产 NextOperationContext。关闭标记时行为与既有完全一致。
    /// </summary>
    [Test]
    public async Task PiXcSourceUnavailable_ShouldIssueAndSuppressFallback_KeepClosure()
    {
        var on = (await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { BuildPiXcUnavailableInput(true) }, new FrozenFactParameters(), CancellationToken.None)).First();
        var off = (await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { BuildPiXcUnavailableInput(false) }, new FrozenFactParameters(), CancellationToken.None)).First();

        // ① 闭合不破：两态 ΣPosition 都等于 ERP Remaining
        Assert.That(on.Positions.Sum(p => p.Quantity), Is.EqualTo(500m));
        Assert.That(off.Positions.Sum(p => p.Quantity), Is.EqualTo(500m));
        Assert.That(on.Positions.Any(p => p.PositionType == PositionType.UNLOCATED), Is.True);

        // ② Issue 留痕（仅开启时）
        Assert.That(on.Issues.Any(i => i.IssueType == "PI_XC_SOURCE_UNAVAILABLE"), Is.True);
        Assert.That(off.Issues.Any(i => i.IssueType == "PI_XC_SOURCE_UNAVAILABLE"), Is.False);

        // ③ 兜底抑制：开启=0（不过排）；关闭=500（既有行为）
        Assert.That(on.ExistingExecutionContexts.Single().DerivedRemainingQty, Is.EqualTo(0m));
        Assert.That(off.ExistingExecutionContexts.Single().DerivedRemainingQty, Is.EqualTo(500m));

        // ④ UNLOCATED 不产起点：开启时无 IsUnlocated 上下文；关闭时有一个
        Assert.That(on.NextOperationContexts.Any(c => c.IsUnlocated), Is.False);
        Assert.That(off.NextOperationContexts.Any(c => c.IsUnlocated), Is.True);
    }

    /// <summary>
    /// U17（TECH-03 §八，按 0号位 2026-10-12《业务口径两问统一回复》§4.1 更新期望）：
    /// PI 总量可信、无任何 MES 位置事实、**无活跃工单**、但有 StagePath ⇒
    /// **不得视为 Stage 剩余为零/全量 UNLOCATED**，未生产剩余归入真实首Stage待生产位置（FIRST_STAGE_PENDING）。
    /// 真正无法定位（连 StagePath 都没有）才进 UNLOCATED/Issue。
    /// </summary>
    [Test]
    public async Task U17_NoMesFactsWithStagePath_FirstStagePendingNotUnlocated()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U17-001",
            MaterialId = 1017,
            MaterialCode = "MAT-U17",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 600m,
            StageProgress = Array.Empty<StageProgressFact>(),
            OperationProgress = Array.Empty<OperationProgressFact>(),
            WorkOrders = Array.Empty<WorkOrderSnapshotFact>(),
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // 0号位 §4.1：可信首Stage待生产位置，不再是全量 UNLOCATED
        Assert.That(result.Positions.Count, Is.EqualTo(1));
        var firstStage = result.Positions.Single();
        Assert.That(firstStage.PositionType, Is.EqualTo(PositionType.FIRST_STAGE_PENDING));
        Assert.That(firstStage.StageCode, Is.EqualTo("CN_ASSY"));
        Assert.That(firstStage.Quantity, Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Any(p => p.PositionType == PositionType.UNLOCATED), Is.False);

        // NextOperation：起点=首Stage、非 UNLOCATED（1号位 可正常消费排程）
        var nextOp = result.NextOperationContexts.Single();
        Assert.That(nextOp.IsUnlocated, Is.False);
        Assert.That(nextOp.StartStageCode, Is.EqualTo("CN_ASSY"));
        Assert.That(nextOp.SliceQty, Is.EqualTo(600m));
    }

    /// <summary>
    /// U17 变体：真正无法定位（无事实 **且** 无 StagePath）⇒ 才走 UNLOCATED 降级 + NO_STAGE_PATH Issue，
    /// 总量仍闭合、批次不阻断（§9.7）。
    /// </summary>
    [Test]
    public async Task U17b_NoFactsNoStagePath_UnlocatedFallbackWithIssue()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-U17B-001",
            MaterialId = 1017,
            MaterialCode = "MAT-U17B",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 600m,
            StageProgress = Array.Empty<StageProgressFact>(),
            OperationProgress = Array.Empty<OperationProgressFact>(),
            WorkOrders = Array.Empty<WorkOrderSnapshotFact>(),
            StagePath = Array.Empty<StagePathFact>()
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var unlocated = result.Positions.Single();
        Assert.That(unlocated.PositionType, Is.EqualTo(PositionType.UNLOCATED));
        Assert.That(unlocated.Quantity, Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Issues.Any(i => i.IssueType == "NO_STAGE_PATH"), Is.True);
    }

    /// <summary>
    /// 首Stage兜底的门禁：**存在 IN_PROGRESS 工单时不触发**（该场景走 E 侧诉求2 ERP 兜底链，
    /// Position 侧维持 UNLOCATED，A/B 连续性归属由 2号位 消费 E 处理）。
    /// </summary>
    [Test]
    public async Task FirstStageFallback_NotTriggered_WhenInProgressWorkOrderExists()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-GUARD-001",
            MaterialId = 1032,
            MaterialCode = "MAT-GUARD",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 600m,
            StageProgress = Array.Empty<StageProgressFact>(),
            OperationProgress = Array.Empty<OperationProgressFact>(),
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-GUARD-001", MESWorkOrderNo = "WO-G1",
                    MaterialCode = "MAT-GUARD", PlannedQty = 600m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        // Position 侧：仍是 UNLOCATED（兜底未触发）
        var unlocated = result.Positions.Single();
        Assert.That(unlocated.PositionType, Is.EqualTo(PositionType.UNLOCATED));
        Assert.That(unlocated.Quantity, Is.EqualTo(600m));

        // E 侧：诉求2 兜底仍给量（erpFallbackTotal=600−0=600），并带 UNLOCATED_STAGE 标记
        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.DerivedRemainingQty, Is.EqualTo(600m));
        Assert.That(ctx.Slices.Single().IssueCode, Is.EqualTo("UNLOCATED_STAGE"));
    }

    /// <summary>
    /// 诉求1 恢复（2026-10-10）：有 Routing、但 DAG 前沿为空（"前道完工、后道未开工"）时，
    /// StartOperationCode=null，但 StartOperationName 必须承载工序名，供 2号位 按「名→码」反查。
    /// 用真实形态 PI 14946237：活塞挤压(M103) 已完工、组装(M102) 未开工。
    /// </summary>
    [Test]
    public async Task DagFrontierEmpty_ShouldEmitStartOperationName()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "14946237",
            MaterialId = 163118,
            MaterialCode = "FINAL_FG-CDM3B20-50",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 27m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 27m, SnapshotId = 1 }
            },
            OperationProgress = new[]
            {
                // 活塞挤压：已完工（RemainingQty=0）
                new OperationProgressFact
                {
                    OperationCode = "M103", OperationName = "活塞挤压", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "014946237092989", PlannedQty = 27m, GoodQty = 27m, RemainingQty = 0m
                },
                // 组装：未开工（GoodQty=0、RemainingQty>0）⇒ 不构成 DAG 前沿候选
                new OperationProgressFact
                {
                    OperationCode = "M102", OperationName = "组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "014946237092989", PlannedQty = 27m, GoodQty = 0m, RemainingQty = 27m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "M103", OperationName = "活塞挤压", StageCode = "CN_ASSY", RouteCode = "配品,活塞挤压,组装" },
                new RoutingOperationFact { OperationCode = "M102", OperationName = "组装", StageCode = "CN_ASSY", RouteCode = "配品,活塞挤压,组装" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "M103", ToOperationCode = "M102", RouteCode = "配品,活塞挤压,组装" }
            },
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "14946237", MESWorkOrderNo = "014946237092989",
                    MaterialCode = "FINAL_FG-CDM3B20-50", PlannedQty = 27m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var ctx = result.ExistingExecutionContexts.Single();

        // 有 Routing（M103→M102）但前沿为空 ⇒ 无码
        Assert.That(ctx.StartOperationCode, Is.Null);
        // 但名必须传出（诉求1 恢复：这是 2号位 反查的唯一可用输入）
        Assert.That(ctx.StartOperationName, Is.EqualTo("组装"));

        // slice 级同样承载（2号位 消费的是 Slices[]）
        var slice = ctx.Slices.Single();
        Assert.That(slice.StartOperationCode, Is.Null);
        Assert.That(slice.StartOperationName, Is.EqualTo("组装"));
        Assert.That(slice.StartStageCode, Is.EqualTo("CN_ASSY"));
    }

    /// <summary>
    /// 诉求1 范围界定：**无 Routing**（RoutingDependencies==0）= 无 APS 路由可反查的 StageLeadTimeParam 降级域，
    /// 即使中间态选出了工序（前道完工、后道未开工），也**不吐 StartOperationName**（保持原样），码同样为 null。
    /// </summary>
    [Test]
    public async Task NoRouting_ShouldNotEmitStartOperationName()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-NOROUTE-001",
            MaterialId = 1020,
            MaterialCode = "MAT-NOROUTE",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 27m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 27m, SnapshotId = 1 }
            },
            OperationProgress = new[]
            {
                // 与"有 Routing、前沿为空"完全相同的中间态，唯一差别是**没有 Routing 数据**
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "活塞挤压", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 27m, GoodQty = 27m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-B", OperationName = "组装", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 27m, GoodQty = 0m, RemainingQty = 27m
                }
            },
            // 无 RoutingOperations / RoutingDependencies
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-NOROUTE-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-NOROUTE", PlannedQty = 27m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.StartOperationCode, Is.Null);
        Assert.That(ctx.StartOperationName, Is.Null); // 无 Routing 不吐名（保持原样）

        var slice = ctx.Slices.Single();
        Assert.That(slice.StartOperationCode, Is.Null);
        Assert.That(slice.StartOperationName, Is.Null);
    }

    /// <summary>
    /// 形态②（2026-10-10 用户定：由 5号位 从 Routing DAG 推）：后道工序在 MES 快照**无行**时，
    /// 从 Routing DAG 推「前驱全部完工的未完工工序」⇒ 唯一后继直接填码 + 名。
    /// 形态：活塞挤压(M103) 已完工、组装(M102) **无行**；M103→M102。
    /// </summary>
    [Test]
    public async Task Form2_NoBackOpRow_ResolvesFromRoutingDag()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "14946237",
            MaterialId = 163118,
            MaterialCode = "FINAL_FG-CDM3B20-50",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 27m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 27m, RemainingQty = 27m, SnapshotId = 1 }
            },
            OperationProgress = new[]
            {
                // 只有前道有行（已完工）；组装 **无行**
                new OperationProgressFact
                {
                    OperationCode = "M103", OperationName = "活塞挤压", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "014946237092989", PlannedQty = 27m, GoodQty = 27m, RemainingQty = 0m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "M103", OperationName = "活塞挤压", StageCode = "CN_ASSY", RouteCode = "配品,活塞挤压,组装" },
                new RoutingOperationFact { OperationCode = "M102", OperationName = "组装", StageCode = "CN_ASSY", RouteCode = "配品,活塞挤压,组装" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "M103", ToOperationCode = "M102", RouteCode = "配品,活塞挤压,组装" }
            },
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "14946237", MESWorkOrderNo = "014946237092989",
                    MaterialCode = "FINAL_FG-CDM3B20-50", PlannedQty = 27m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.StartOperationCode, Is.EqualTo("M102")); // 从 Routing DAG 推出的码
        Assert.That(ctx.StartOperationName, Is.EqualTo("组装"));
        var slice = ctx.Slices.Single();
        Assert.That(slice.StartOperationCode, Is.EqualTo("M102"));
        Assert.That(slice.StartOperationName, Is.EqualTo("组装"));
    }

    /// <summary>
    /// 形态② + 路由后继**多个并行分支** ⇒ AMBIGUOUS，不得静默取首条。
    /// </summary>
    [Test]
    public async Task Form2_MultipleRoutingSuccessors_Ambiguous()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-AMB-001",
            MaterialId = 1030,
            MaterialCode = "MAT-AMB",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 10m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 10m, RemainingQty = 10m, SnapshotId = 1 }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "M101", OperationName = "配品", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-AMB", PlannedQty = 10m, GoodQty = 10m, RemainingQty = 0m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "M101", OperationName = "配品", StageCode = "CN_ASSY", RouteCode = "配品,并行A,并行B" },
                new RoutingOperationFact { OperationCode = "M102A", OperationName = "并行A", StageCode = "CN_ASSY", RouteCode = "配品,并行A,并行B" },
                new RoutingOperationFact { OperationCode = "M102B", OperationName = "并行B", StageCode = "CN_ASSY", RouteCode = "配品,并行A,并行B" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "M101", ToOperationCode = "M102A" },
                new RoutingDependencyFact { FromOperationCode = "M101", ToOperationCode = "M102B" }
            },
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-AMB-001", MESWorkOrderNo = "WO-AMB",
                    MaterialCode = "MAT-AMB", PlannedQty = 10m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.StartOperationCode, Is.Null);
        Assert.That(ctx.StartOperationName, Is.Null);
        Assert.That(ctx.Slices.Single().IssueCode, Is.EqualTo("NEXT_OPERATION_AMBIGUOUS"));
    }

    /// <summary>
    /// 形态② 但**无 Routing** ⇒ 不推（保持原样），码/名均 null。
    /// </summary>
    [Test]
    public async Task Form2_NoRouting_StaysNull()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-F2NR-001",
            MaterialId = 1031,
            MaterialCode = "MAT-F2NR",
            FactoryId = 1,
            FactoryCode = "CN",
            ErpRemainingQty = 27m,
            StageProgress = new[]
            {
                new StageProgressFact { StageCode = "CN_ASSY", StageSequence = 1, GoodCompletedQty = 27m, RemainingQty = 27m, SnapshotId = 1 }
            },
            OperationProgress = new[]
            {
                // 前道完工、后道无行；且**无 Routing**
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "活塞挤压", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-F2NR", PlannedQty = 27m, GoodQty = 27m, RemainingQty = 0m
                }
            },
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-F2NR-001", MESWorkOrderNo = "WO-F2NR",
                    MaterialCode = "MAT-F2NR", PlannedQty = 27m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(new[] { input }, new FrozenFactParameters(), CancellationToken.None);
        var result = results.First();

        var ctx = result.ExistingExecutionContexts.Single();
        Assert.That(ctx.StartOperationCode, Is.Null);
        Assert.That(ctx.StartOperationName, Is.Null);
    }
}

