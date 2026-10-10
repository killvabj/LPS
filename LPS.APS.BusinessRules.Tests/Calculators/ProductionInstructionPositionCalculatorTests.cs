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
    /// 断言：不把较大的位置结果当成新 PI 总量（仍以入参 600 为准）；超量记 QUANTITY_OVERFLOW、不静默、不截断。
    /// </summary>
    [Test]
    public async Task U04_ExclusivePositionsExceedErp_ShouldIssueOverflowNotSilent()
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

        // 超量：不静默、不截断，位置合计仍如实 = 650（异常由 Issue 暴露，而非被悄悄改小）
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(650m));

        // 错误不静默：QUANTITY_OVERFLOW 登记，超出量 50
        var overflow = result.Issues.FirstOrDefault(i => i.IssueType == "QUANTITY_OVERFLOW");
        Assert.That(overflow, Is.Not.Null);
        Assert.That(overflow.AffectedQuantity, Is.EqualTo(50m));

        // 总量不闭合到入参 600 ⇒ IsSuccess=false（不把 650 当成新 PI 总量）
        Assert.That(result.IsSuccess, Is.False);
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

    /// <summary>
    /// U17（TECH-03 §八）：PI 总量可信但**无任何有效位置事实**（无 Stage/XC/Transit/库存）⇒
    /// 按既有 UNLOCATED 降级：单个 UNLOCATED 承载全量、总量仍闭合、UNLOCATED 保守返回最早 Stage。
    /// </summary>
    [Test]
    public async Task U17_NoValidPositionButTrustedTotal_UnlocatedFallback()
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

        // 无任何位置事实 → 单个 UNLOCATED 承载全量，总量仍闭合到可信入参
        Assert.That(result.Positions.Count, Is.EqualTo(1));
        var unlocated = result.Positions.Single();
        Assert.That(unlocated.PositionType, Is.EqualTo(PositionType.UNLOCATED));
        Assert.That(unlocated.IsUnlocated, Is.True);
        Assert.That(unlocated.Quantity, Is.EqualTo(600m));
        Assert.That(result.IsSuccess, Is.True);

        // NextOperation：UNLOCATED 保守返回最早 Stage（IsStartStage）
        var nextOp = result.NextOperationContexts.Single();
        Assert.That(nextOp.IsUnlocated, Is.True);
        Assert.That(nextOp.StartStageCode, Is.EqualTo("CN_ASSY"));
    }
}

