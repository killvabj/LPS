using LPS.APS.BusinessRules.Calculators;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LPS.APS.Tests.Position5.Calculators;

/// <summary>
/// NextOperationContext测试
/// 验证PI执行起点上下文计算逻辑
/// </summary>
[Collection("Position5")]
public class NextOperationContextTests
{
    private readonly ProductionInstructionPositionCalculator _calculator;

    public NextOperationContextTests()
    {
        _calculator = new ProductionInstructionPositionCalculator(NullLogger<ProductionInstructionPositionCalculator>.Instance);
    }

    /// <summary>
    /// 唯一 frontier 判定（39-0 §四.1）
    /// NC 已开工未完成（GoodQty=800>0, RemainingQty=200>0），挤丝未开工（GoodQty=0, RemainingQty=1000>0）
    /// 期望：1个切片，StartOperation = NC（唯一已开工未完成的工序），整量 1000
    /// </summary>
    [Fact]
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
                    GoodCompletedQty = 1000m,
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
                    GoodQty = 800m,
                    RemainingQty = 200m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    GoodQty = 0m,
                    RemainingQty = 1000m,
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

        Assert.True(result.IsSuccess);
        Assert.Equal(1000m, result.Positions.Sum(p => p.Quantity));

        // 39-0：唯一 frontier → 整量给该工序（不再按 OperationSequence 级联拆分）
        Assert.Equal(1, result.NextOperationContexts.Count);

        var ncSlice = result.NextOperationContexts.First();
        Assert.Equal("NC", ncSlice.StartOperationCode);
        Assert.Equal(1000m, ncSlice.SliceQty);
        Assert.Equal("CN_MACHINING", ncSlice.StartStageCode);
    }

    /// <summary>
    /// 测试无OperationProgress时只输出StartStageCode
    /// </summary>
    [Fact]
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
                    GoodCompletedQty = 100m,  // 所有100件都在这个Stage
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

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.NextOperationContexts.Count);
        Assert.Equal("CN_MACHINING", result.NextOperationContexts[0].StartStageCode);
        Assert.Null(result.NextOperationContexts[0].StartOperationCode);
        Assert.Equal(100m, result.NextOperationContexts[0].SliceQty);
    }

    /// <summary>
    /// 测试UNLOCATED时IsUnlocated=true
    /// （2026-10-12 按 0号位 §4.1 调整输入形态：零事实+有StagePath 现走可信首Stage兜底、不再产 UNLOCATED；
    ///   本用例改用"部分事实+缺口"形态合法产生 UNLOCATED，测试意图不变）
    /// </summary>
    [Fact]
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
            // 部分进度事实：CN_MACHINING 累计 60 ⇒ Stage 位置 60，缺口 40 → UNLOCATED
            StageProgress = new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_MACHINING",
                    GoodCompletedQty = 60m,
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

        Assert.True(result.IsSuccess);

        // 应有UNLOCATED的NextOperationContext
        var unlocatedContext = result.NextOperationContexts.FirstOrDefault(c => c.IsUnlocated);
        Assert.NotNull(unlocatedContext);
        Assert.Equal("CN_MACHINING", unlocatedContext!.StartStageCode);
    }

    /// <summary>
    /// 测试XC位置被跳过（不产生NextOperationContext）
    /// </summary>
    [Fact]
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
                    GoodCompletedQty = 100m,
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

        Assert.True(result.IsSuccess);

        // XC位置不应产生NextOperationContext
        var xcContext = result.NextOperationContexts.FirstOrDefault(c => c.PositionType == "XC");
        Assert.Null(xcContext);

        // 应有STAGE_WAITING的NextOperationContext
        var stageContext = result.NextOperationContexts.FirstOrDefault(c => c.PositionType == "STAGE_WAITING");
        Assert.NotNull(stageContext);
    }

    /// <summary>
    /// 测试NextOperationContext不生成Operation Supply
    /// </summary>
    [Fact]
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
                    GoodCompletedQty = 800m,
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
                    GoodQty = 800m,
                    RemainingQty = 200m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    GoodQty = 0m,
                    RemainingQty = 1000m,
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

        // NextOperationContext不是Supply类型
        foreach (var context in result.NextOperationContexts)
        {
            Assert.NotEqual("SUPPLY", context.PositionType);
            Assert.NotEqual("OPERATION_SUPPLY", context.PositionType);
        }

        // Position不是Operation级
        foreach (var position in result.Positions)
        {
            Assert.NotEqual(PositionType.XC, position.PositionType);
            Assert.NotEqual(PositionType.INTERPLANT_TRANSIT, position.PositionType);
        }
    }

    /// <summary>
    /// 多 frontier → NEXT_OPERATION_AMBIGUOUS（39-0 §四.2 / §五）
    /// NC(GoodQty=500>0, RemainingQty=500>0) + 挤丝(GoodQty=200>0, RemainingQty=800>0) 均已开工未完成
    /// 研磨(GoodQty=0, RemainingQty=1000>0) 未开工
    /// 2个 frontier 候选 → 无法唯一定位，降级到 Stage 级 + NEXT_OPERATION_AMBIGUOUS
    /// </summary>
    [Fact]
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
                    GoodCompletedQty = 1000m,
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
                    GoodQty = 500m,
                    RemainingQty = 500m,
                    OperationSequence = 1
                },
                new OperationProgressFact
                {
                    OperationCode = "EXTRUDE001",
                    OperationName = "挤丝",
                    StageCode = "CN_MACHINING",
                    GoodQty = 200m,
                    RemainingQty = 800m,
                    OperationSequence = 2
                },
                new OperationProgressFact
                {
                    OperationCode = "GRIND001",
                    OperationName = "研磨",
                    StageCode = "CN_MACHINING",
                    GoodQty = 0m,
                    RemainingQty = 1000m,
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

        Assert.True(result.IsSuccess);
        Assert.Equal(1000m, result.Positions.Sum(p => p.Quantity));

        // 39-0：2个已开工未完成（NC + 挤丝）→ AMBIGUOUS，降级到 Stage 级
        Assert.Equal(1, result.NextOperationContexts.Count);

        var ambiguousSlice = result.NextOperationContexts.First();
        Assert.Null(ambiguousSlice.StartOperationCode);
        Assert.Equal(1000m, ambiguousSlice.SliceQty);
        Assert.Equal("CN_MACHINING", ambiguousSlice.StartStageCode);
        Assert.Equal("NEXT_OPERATION_AMBIGUOUS", ambiguousSlice.IssueCode);
    }

    // ========================================================================
    // S11: DAG 拓扑前沿测试（39-0 §二/§五，Routing 数据可用时）
    // ========================================================================

    /// <summary>
    /// DAG 串行链：A→B→C，A 完成，B 已开工未完成，C 未开工
    /// 前沿 = B（唯一已开工未完成且前驱全部完成）
    /// </summary>
    [Fact]
    public async Task NextOp_DagSerialChain_FindsFrontier()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-DAG-SERIAL-001",
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
                    GoodCompletedQty = 1000m,
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "NC",
                    StageCode = "CN_MACHINING", GoodQty = 1000m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-B", OperationName = "挤丝",
                    StageCode = "CN_MACHINING", GoodQty = 200m, RemainingQty = 800m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-C", OperationName = "研磨",
                    StageCode = "CN_MACHINING", GoodQty = 0m, RemainingQty = 1000m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "OP-A", OperationName = "NC", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-B", OperationName = "挤丝", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-C", OperationName = "研磨", StageCode = "CN_MACHINING" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "OP-A", ToOperationCode = "OP-B" },
                new RoutingDependencyFact { FromOperationCode = "OP-B", ToOperationCode = "OP-C" }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.True(result.IsSuccess);

        // DAG 唯一前沿 = 挤丝（NC 完成，挤丝已开工，研磨未开工）
        // 2026-11-26 D05-C13 语义：前沿分量=挤丝自身剩余 800；已过挤丝、未到研磨的 200 归唯一后继「研磨」
        // （旧断言"1000 全给挤丝"编码的正是打包缺陷，已按冻结 Pegging §2/§3 更新）
        Assert.Equal(2, result.NextOperationContexts.Count);
        Assert.Contains(result.NextOperationContexts, c => c.StartOperationCode == "挤丝" && c.SliceQty == 800m && c.IssueCode == null);
        Assert.Contains(result.NextOperationContexts, c => c.StartOperationCode == "研磨" && c.SliceQty == 200m && c.IssueCode == null);
        Assert.DoesNotContain(result.NextOperationContexts, c => c.StartOperationCode == "挤丝" && c.SliceQty == 1000m);
    }

    /// <summary>
    /// DAG 并行分支：A→B, A→C, B→D, C→D，A 完成，B 和 C 均已开工未完成
    /// AMBIGUOUS（2 个合法前沿，不得重新线性化）
    /// </summary>
    [Fact]
    public async Task NextOp_DagParallelBranch_Ambiguous()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-DAG-PARALLEL-001",
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
                    GoodCompletedQty = 1000m,
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "NC",
                    StageCode = "CN_MACHINING", GoodQty = 1000m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-B", OperationName = "挤丝",
                    StageCode = "CN_MACHINING", GoodQty = 200m, RemainingQty = 800m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-C", OperationName = "研磨",
                    StageCode = "CN_MACHINING", GoodQty = 300m, RemainingQty = 700m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-D", OperationName = "检测",
                    StageCode = "CN_MACHINING", GoodQty = 0m, RemainingQty = 1000m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "OP-A", OperationName = "NC", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-B", OperationName = "挤丝", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-C", OperationName = "研磨", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-D", OperationName = "检测", StageCode = "CN_MACHINING" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "OP-A", ToOperationCode = "OP-B" },
                new RoutingDependencyFact { FromOperationCode = "OP-A", ToOperationCode = "OP-C" },
                new RoutingDependencyFact { FromOperationCode = "OP-B", ToOperationCode = "OP-D" },
                new RoutingDependencyFact { FromOperationCode = "OP-C", ToOperationCode = "OP-D" }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.True(result.IsSuccess);

        // 并行分支：挤丝 + 研磨 均已开工，A 完成 → AMBIGUOUS
        Assert.Equal(1, result.NextOperationContexts.Count);
        var slice = result.NextOperationContexts.First();
        Assert.Null(slice.StartOperationCode);
        Assert.Equal("NEXT_OPERATION_AMBIGUOUS", slice.IssueCode);
        Assert.Equal(1000m, slice.SliceQty);
    }

    /// <summary>
    /// DAG 并行结构但仅一侧开工：A→B, A→C，A 完成，B 已开工未完成，C 未开工
    /// 前沿 = B（只有 B 是已开工且前驱完成的）
    /// </summary>
    [Fact]
    public async Task NextOp_DagParallelOneStarted_FindsFrontier()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-DAG-PARALLEL-ONE-001",
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
                    GoodCompletedQty = 1000m,
                    StageSequence = 1,
                    SnapshotId = 1
                }
            },
            OperationProgress = new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "NC",
                    StageCode = "CN_MACHINING", GoodQty = 1000m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-B", OperationName = "挤丝",
                    StageCode = "CN_MACHINING", GoodQty = 200m, RemainingQty = 800m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-C", OperationName = "研磨",
                    StageCode = "CN_MACHINING", GoodQty = 0m, RemainingQty = 1000m
                }
            },
            RoutingOperations = new[]
            {
                new RoutingOperationFact { OperationCode = "OP-A", OperationName = "NC", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-B", OperationName = "挤丝", StageCode = "CN_MACHINING" },
                new RoutingOperationFact { OperationCode = "OP-C", OperationName = "研磨", StageCode = "CN_MACHINING" }
            },
            RoutingDependencies = new[]
            {
                new RoutingDependencyFact { FromOperationCode = "OP-A", ToOperationCode = "OP-B" },
                new RoutingDependencyFact { FromOperationCode = "OP-A", ToOperationCode = "OP-C" }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_MACHINING", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var result = results.First();

        Assert.True(result.IsSuccess);

        // 并行结构但只有 B 开工 → 唯一前沿 = 挤丝（800）；差距 200 无「直接后继」（B 是叶子，C 是兄弟分支不是 B 的后继）
        // ⇒ 2026-11-26 P2：无直接后继（末工序）判 NEXT_OPERATION_NO_SUCCESSOR，与「多后继 AMBIGUOUS」分列；残差不猜、起点 null
        Assert.Equal(2, result.NextOperationContexts.Count);
        Assert.Contains(result.NextOperationContexts, c => c.StartOperationCode == "挤丝" && c.SliceQty == 800m && c.IssueCode == null);
        var residual = result.NextOperationContexts.Single(c => c.StartOperationCode == null);
        Assert.Equal(200m, residual.SliceQty);
        Assert.Equal("NEXT_OPERATION_NO_SUCCESSOR", residual.IssueCode);
        Assert.Contains(result.Issues, i => i.IssueType == "NEXT_OPERATION_NO_SUCCESSOR");
        Assert.DoesNotContain(result.Issues, i => i.IssueType == "NEXT_OPERATION_AMBIGUOUS");
    }
}
