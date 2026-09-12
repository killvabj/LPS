using LPS.APS.BusinessRules.Calculators;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace LPS.APS.BusinessRules.Tests.Calculators;

/// <summary>
/// NextOperationContext测试
/// 验证PI执行起点上下文计算逻辑
/// </summary>
public class NextOperationContextTests
{
    private readonly ProductionInstructionPositionCalculator _calculator;

    public NextOperationContextTests()
    {
        _calculator = new ProductionInstructionPositionCalculator(NullLogger<ProductionInstructionPositionCalculator>.Instance);
    }

    /// <summary>
    /// 测试同一PI多执行起点切片
    /// PI001 RemainingQty = 1000
    /// Stage CN_MACHINING: CumulativeCompletedQty = 1000（所有件都在这个Stage）
    /// NC (seq=1): CumulativeCompletedQty = 800
    /// 挤丝 (seq=2): CumulativeCompletedQty = 0
    /// 期望：200件 StartOperation = NC, 800件 StartOperation = 挤丝
    /// </summary>
    [Test]
    public async Task NextOp_MultiSlice_SplitsCorrectly()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-MULTI-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 1000m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 1000m,  // 所有1000件都在这个Stage
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "NC001",
                    OperationName = "NC",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 800m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 0m,
                    OperationSequence = 2
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        // 验证Position闭合
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(1000m));

        // 验证NextOperationContext
        Assert.That(result.NextOperationContexts.Count, Is.EqualTo(2));

        // 200件 StartOperation = NC
        var ncSlice = result.NextOperationContexts.FirstOrDefault(c => c.StartOperationCode == "NC");
        Assert.That(ncSlice, Is.Not.Null);
        Assert.That(ncSlice!.SliceQty, Is.EqualTo(200m));
        Assert.That(ncSlice.StartStageCode, Is.EqualTo("CN_MACHINING"));

        // 800件 StartOperation = 挤丝
        var extrudeSlice = result.NextOperationContexts.FirstOrDefault(c => c.StartOperationCode == "挤丝");
        Assert.That(extrudeSlice, Is.Not.Null);
        Assert.That(extrudeSlice!.SliceQty, Is.EqualTo(800m));
        Assert.That(extrudeSlice.StartStageCode, Is.EqualTo("CN_MACHINING"));

        // 验证SliceQty闭合
        Assert.That(result.NextOperationContexts.Sum(c => c.SliceQty), Is.EqualTo(1000m));
    }

    /// <summary>
    /// 测试无OperationProgress时只输出StartStageCode
    /// </summary>
    [Test]
    public async Task NextOp_NoOperationProgress_OutputStartStageOnly()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-NOOP-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 100m,  // 所有100件都在这个Stage
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.NextOperationContexts.Count, Is.EqualTo(1));
        Assert.That(result.NextOperationContexts[0].StartStageCode, Is.EqualTo("CN_MACHINING"));
        Assert.That(result.NextOperationContexts[0].StartOperationCode, Is.Null);
        Assert.That(result.NextOperationContexts[0].SliceQty, Is.EqualTo(100m));
    }

    /// <summary>
    /// 测试UNLOCATED时IsUnlocated=true
    /// </summary>
    [Test]
    public async Task NextOp_Unlocated_SetsIsUnlocatedTrue()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-UNLOC-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);

        // 应有UNLOCATED的NextOperationContext
        var unlocatedContext = result.NextOperationContexts.FirstOrDefault(c => c.IsUnlocated);
        Assert.That(unlocatedContext, Is.Not.Null);
        Assert.That(unlocatedContext!.StartStageCode, Is.EqualTo("CN_MACHINING"));
    }

    /// <summary>
    /// 测试XC位置被跳过（不产生NextOperationContext）
    /// </summary>
    [Test]
    public async Task NextOp_XcPosition_Skipped()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-XC-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 100m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 100m,
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            XcFacts = new[]
            {
                new XcFact
                {
                    XcWarehouseCode = "XC-WH-01",
                    RelatedStageCode = "CN_MACHINING",
                    Quantity = 30m,
                    SourceDocument = "XC-DOC-001"
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.That(result.IsSuccess, Is.True);

        // XC位置不应产生NextOperationContext
        var xcContext = result.NextOperationContexts.FirstOrDefault(c => c.PositionType == "XC");
        Assert.That(xcContext, Is.Null);

        // 应有STAGE_WAITING的NextOperationContext
        var stageContext = result.NextOperationContexts.FirstOrDefault(c => c.PositionType == "STAGE_WAITING");
        Assert.That(stageContext, Is.Not.Null);
    }

    /// <summary>
    /// 测试NextOperationContext不生成Operation Supply
    /// </summary>
    [Test]
    public async Task NextOp_DoesNotCreateOperationSupply()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-NOOP-SUPPLY-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 1000m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 800m,
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "NC001",
                    OperationName = "NC",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 800m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 0m,
                    OperationSequence = 2
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        // 验证NextOperationContext不是Supply类型
        foreach (var context in result.NextOperationContexts)
        {
            Assert.That(context.PositionType, Is.Not.EqualTo("SUPPLY"));
            Assert.That(context.PositionType, Is.Not.EqualTo("OPERATION_SUPPLY"));
        }

        // 验证Position不是Operation级
        foreach (var position in result.Positions)
        {
            Assert.That(position.PositionType, Is.Not.EqualTo(PositionType.XC) | Is.Not.EqualTo(PositionType.INTERPLANT_TRANSIT));
        }
    }

    /// <summary>
    /// 测试三个Operation的拆分
    /// Stage CN_MACHINING: CumulativeCompletedQty = 1000
    /// NC(seq=1): CumulativeCompletedQty = 500
    /// 挤丝(seq=2): CumulativeCompletedQty = 200
    /// 研磨(seq=3): CumulativeCompletedQty = 0
    /// 期望：500件 NC, 300件 挤丝, 200件 研磨
    /// </summary>
    [Test]
    public async Task NextOp_ThreeOperations_SplitsCorrectly()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-3OP-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 1000m,
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 1000m,  // 所有1000件都在这个Stage
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "NC001",
                    OperationName = "NC",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 500m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 200m,
                    OperationSequence = 2
                },
                new OperationProgressFact
                {
                    OperationCode = "GRIND001",
                    OperationName = "研磨",
                    StageCode = "CN_MACHINING",
                    CumulativeCompletedQty = 0m,
                    OperationSequence = 3
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        // 验证Position闭合
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Positions.Sum(p => p.Quantity), Is.EqualTo(1000m));

        // 验证NextOperationContext
        Assert.That(result.NextOperationContexts.Count, Is.EqualTo(3));

        // 1000 - 500 = 500件 StartOperation = NC
        var ncSlice = result.NextOperationContexts.FirstOrDefault(c => c.StartOperationCode == "NC");
        Assert.That(ncSlice, Is.Not.Null);
        Assert.That(ncSlice!.SliceQty, Is.EqualTo(500m));

        // 500 - 200 = 300件 StartOperation = 挤丝
        var extrudeSlice = result.NextOperationContexts.FirstOrDefault(c => c.StartOperationCode == "挤丝");
        Assert.That(extrudeSlice, Is.Not.Null);
        Assert.That(extrudeSlice!.SliceQty, Is.EqualTo(300m));

        // 200 - 0 = 200件 StartOperation = 研磨
        var grindSlice = result.NextOperationContexts.FirstOrDefault(c => c.StartOperationCode == "研磨");
        Assert.That(grindSlice, Is.Not.Null);
        Assert.That(grindSlice!.SliceQty, Is.EqualTo(200m));

        // 验证SliceQty闭合
        Assert.That(result.NextOperationContexts.Sum(c => c.SliceQty), Is.EqualTo(1000m));
    }
}
