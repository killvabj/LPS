using LPS.APS.BusinessRules.Calculators;
using LPS.APS.Core.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LPS.APS.Tests.Position5.Calculators;

/// <summary>
/// ExistingExecutionContext Stage 兜底测试（2号位 v2.1 §四修复）
///
/// 场景：工序层全部完成（RemainingQty=0）但 Stage 层仍有剩余（RemainingQty>0）
/// 修复前：「全部完成」分支静默归零，405 个工单 E=0
/// 修复后：回退取 Stage RemainingQty，按工单 PlannedQty 比例拆分
/// </summary>
[Collection("Position5")]
public class ExistingExecutionContextStageFallbackTests
{
    private readonly ProductionInstructionPositionCalculator _calculator;

    public ExistingExecutionContextStageFallbackTests()
    {
        _calculator = new ProductionInstructionPositionCalculator(NullLogger<ProductionInstructionPositionCalculator>.Instance);
    }

    /// <summary>
    /// 单工单：工序全部完成（GoodQty=PlannedQty），Stage 剩余 500
    /// 期望：DerivedRemainingQty = 500（唯一工单，比例=100%）
    /// </summary>
    [Fact]
    public async Task StageFallback_SingleWorkOrder_TakesFullStageRemaining()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001",
                    MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001",
                    PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS",
                    DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A",
                    OperationName = "装配",
                    StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001",
                    PlannedQty = 1000m,
                    GoodQty = 1000m,       // 已完工
                    RemainingQty = 0m       // 工序层无剩余
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY",
                    GoodCompletedQty = 1000m,
                    PlannedQty = 1500m,
                    RemainingQty = 500m,    // Stage 层仍有 500 剩余
                    StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.Null(ctx.StartOperationCode);          // 工序全完成，无前沿工序
        Assert.Equal(500m, ctx.DerivedRemainingQty); // Stage 兜底生效
        Assert.Equal("CN_ASSY", ctx.StartStageCode);
        Assert.Equal("MAT-001", ctx.MaterialCode);  // T5-03: 物料身份透出
    }

    /// <summary>
    /// 双工单：工序都全完成，Stage 剩余 600，PlannedQty 比例 1:2
    /// 期望：WO-A = 200，WO-B = 400，Σ E = 600（比例拆分闭合）
    /// </summary>
    [Fact]
    public async Task StageFallback_TwoWorkOrders_SplitsByPlannedQtyRatio()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-A",
                    MaterialCode = "MAT-001", PlannedQty = 200m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                },
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-B",
                    MaterialCode = "MAT-001", PlannedQty = 400m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-A", PlannedQty = 200m, GoodQty = 200m, RemainingQty = 0m
                },
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-B", PlannedQty = 400m, GoodQty = 400m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 600m,
                    PlannedQty = 1200m, RemainingQty = 600m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var contexts = results.First().ExistingExecutionContexts;
        Assert.Equal(2, contexts.Count);

        var woA = contexts.Single(c => c.MESWorkOrderNo == "WO-A");
        var woB = contexts.Single(c => c.MESWorkOrderNo == "WO-B");

        // 600 × (200/600) = 200；600 × (400/600) = 400
        Assert.Equal(200m, woA.DerivedRemainingQty);
        Assert.Equal(400m, woB.DerivedRemainingQty);
        Assert.Equal(600m, woA.DerivedRemainingQty + woB.DerivedRemainingQty);
    }

    /// <summary>
    /// 混现场景（2号位 v2.1 残留口径）：同 PI 内「全部完成工单」+「无工序数据工单」并存
    /// 修复前：无工序数据工单取 Stage 全量 1000（Math.Min 截不住，因 WO-B PlannedQty=2000 > 1000）
    ///         → ΣE = 130.43 + 1000 = 1130.43 > Stage 剩余 1000，超分
    /// 修复后：两路都按 PlannedQty 比例拆分 → ΣE = Stage RemainingQty 闭合
    /// </summary>
    [Fact]
    public async Task StageFallback_MixedCompletedAndNoOps_SigmaDoesNotExceedStageRemaining()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                // WO-A：有工序数据，全部完成
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-A",
                    MaterialCode = "MAT-001", PlannedQty = 300m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                },
                // WO-B：无任何工序进度数据，且 PlannedQty > Stage 剩余（暴露旧分支全量超分）
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-B",
                    MaterialCode = "MAT-001", PlannedQty = 2000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                // 只有 WO-A 的工序（已完工）；WO-B 无工序行
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-A", PlannedQty = 300m, GoodQty = 300m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 300m,
                    PlannedQty = 2300m, RemainingQty = 1000m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var contexts = results.First().ExistingExecutionContexts;
        Assert.Equal(2, contexts.Count);

        var woA = contexts.Single(c => c.MESWorkOrderNo == "WO-A");
        var woB = contexts.Single(c => c.MESWorkOrderNo == "WO-B");

        // 1000 × 300/2300 ≈ 130.43；1000 × 2000/2300 ≈ 869.57
        Assert.True(Math.Abs(1000m * 300m / 2300m - (woA.DerivedRemainingQty)) <= 0.0001m);
        Assert.True(Math.Abs(1000m * 2000m / 2300m - (woB.DerivedRemainingQty)) <= 0.0001m);

        // 核心断言1：WO-B 不再取 Stage 全量（旧分支会给 1000）
        Assert.True(woB.DerivedRemainingQty < 1000m);

        // 核心断言2：ΣE 闭合到 Stage 剩余，不超分
        Assert.True(Math.Abs(1000m - (woA.DerivedRemainingQty + woB.DerivedRemainingQty)) <= 0.0001m);
    }

    /// <summary>
    /// 工序全部完成且 Stage 也无剩余 → DerivedRemainingQty = 0（真完成，不兜底）
    /// </summary>
    [Fact]
    public async Task StageFallback_StageAlsoComplete_ReturnsZero()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 1000m, GoodQty = 1000m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 1000m,
                    PlannedQty = 1000m, RemainingQty = 0m,   // Stage 也无剩余
                    StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.Equal(0m, ctx.DerivedRemainingQty);
        Assert.Null(ctx.StartOperationCode);
    }

    /// <summary>
    /// 多 Slice（T5-02 / 接口 v1.35 §18.3）：两道平行工序都已开工未完成 → 产 2 个 Slice，各带各的剩余。
    /// 无 Routing 数据走中间态多候选分支；验证 Slices[] 结构 + Σ SliceQty = DerivedRemainingQty(兼容投影)。
    /// </summary>
    [Fact]
    public async Task MultiSlice_TwoParallelInProgress_ProducesTwoSlices()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
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
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 1000m,
                    PlannedQty = 1000m, RemainingQty = 0m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();

        // 关键断言：Slices[] 产 2 个
        Assert.Equal(2, ctx.Slices.Count);

        // 各 Slice 数量 = 各自 RemainingQty（挤丝 300 + 磨削 200）
        var total = ctx.Slices.Sum(s => s.SliceQty);
        Assert.True(Math.Abs(500m - (total)) <= 0.0001m);

        // 兼容投影：N>1 时 StartOperationCode 为 null、DerivedRemainingQty = Σ
        Assert.Null(ctx.StartOperationCode);
        Assert.True(Math.Abs(500m - (ctx.DerivedRemainingQty)) <= 0.0001m);

        // 每个 Slice 的 Stage 正确
        Assert.True(ctx.Slices.All(s => s.StartStageCode == "CN_ASSY"));
    }

    /// <summary>
    /// 单工序有剩余 → 1 个 Slice（兼容投影 N=1，StartOperationCode 不 null）
    /// </summary>
    [Fact]
    public async Task SingleSlice_OneRemaining_ProducesOneSlice()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "挤丝", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 1000m, GoodQty = 700m, RemainingQty = 300m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 1000m,
                    PlannedQty = 1000m, RemainingQty = 0m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.Equal(1, ctx.Slices.Count);
        Assert.Equal(300m, ctx.Slices[0].SliceQty);
        Assert.Equal("CN_ASSY", ctx.Slices[0].StartStageCode);
        Assert.Equal(300m, ctx.DerivedRemainingQty);
    }

    /// <summary>
    /// 诉求2 §四①：无 StageProgress 行（信息缺口）→ 不判 0，改走 ERP 兜底总量
    /// 场景：PI 无任何 Stage 行，但有 IN_PROGRESS 工单；ErpRemainingQty=400
    /// 期望：DerivedRemainingQty = 400（兜底总量 = ErpRemainingQty − 已定位量 = 400 − 0）
    /// </summary>
    [Fact]
    public async Task ErpFallback_NoStageRow_UsesErpRemainingNotZero()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 400m,
            StageProgress = Array.Empty<StageProgressFact>(),
            OperationProgress = Array.Empty<OperationProgressFact>(),
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();

        // 核心：无 Stage 行 ≠ 0，兜底 = ErpRemainingQty − 已定位量（0）= 400
        Assert.Equal(400m, ctx.DerivedRemainingQty);
        Assert.Null(ctx.StartOperationCode);
        Assert.Equal("CN_ASSY", ctx.StartStageCode);
        // §四④：兜底份额可区分标记（复用 UNLOCATED_STAGE）
        Assert.Equal("UNLOCATED_STAGE", ctx.Slices.Single().IssueCode);
    }

    /// <summary>
    /// 诉求2 §四③/④：双工单、无 Stage 行、ErpRemainingQty=1999（不可整除）→ 向下取整不放大
    /// WO-A PlannedQty=1000, WO-B PlannedQty=1000 → 各 floor(1999×1000/2000)=floor(999.5)=999
    /// （PlannedQty=1000 > 999，故工单上限不截断）
    /// 期望：ΣE = 1998 ≤ ErpRemainingQty(1999)，不放大
    /// </summary>
    [Fact]
    public async Task ErpFallback_NoStageRow_TwoWorkOrders_FloorNotAmplify()
    {
        var input = new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = 1999m,
            StageProgress = Array.Empty<StageProgressFact>(),
            OperationProgress = Array.Empty<OperationProgressFact>(),
            WorkOrders = new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-A",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                },
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-B",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            StagePath = new[]
            {
                new StagePathFact { StageCode = "CN_ASSY", StageSequence = 1, IsStartStage = true }
            }
        };

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var contexts = results.First().ExistingExecutionContexts;
        var woA = contexts.Single(c => c.MESWorkOrderNo == "WO-A");
        var woB = contexts.Single(c => c.MESWorkOrderNo == "WO-B");

        Assert.Equal(999m, woA.DerivedRemainingQty);
        Assert.Equal(999m, woB.DerivedRemainingQty);
        // §四④：ΣE ≤ ErpRemainingQty，向下取整不放大
        Assert.Equal(1998m, woA.DerivedRemainingQty + woB.DerivedRemainingQty);
        Assert.True(woA.DerivedRemainingQty + woB.DerivedRemainingQty <= 1999m);
    }

    /// <summary>
    /// 诉求2 §四①：有 Stage 行且 RemainingQty<=0（真做完）→ 仍判 0（不受兜底影响）
    /// 场景：有 Stage 行但 RemainingQty=0 且工序已完成
    /// </summary>
    [Fact]
    public async Task ErpFallback_StageRowPresentButComplete_StaysZero()
    {
        var input = BuildInput(
            workOrders: new[]
            {
                new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = "PI-001", MESWorkOrderNo = "WO-001",
                    MaterialCode = "MAT-001", PlannedQty = 1000m,
                    WorkOrderStatus = "IN_PROGRESS", DataCutoffTime = DateTime.UtcNow
                }
            },
            operationProgress: new[]
            {
                new OperationProgressFact
                {
                    OperationCode = "OP-A", OperationName = "装配", StageCode = "CN_ASSY",
                    MESWorkOrderNo = "WO-001", PlannedQty = 1000m, GoodQty = 1000m, RemainingQty = 0m
                }
            },
            stageProgress: new[]
            {
                new StageProgressFact
                {
                    StageCode = "CN_ASSY", GoodCompletedQty = 1000m,
                    PlannedQty = 1000m, RemainingQty = 0m, StageSequence = 1
                }
            });

        var results = await _calculator.CalculateProductionInstructionPositionsAsync(
            new[] { input }, new FrozenFactParameters(), CancellationToken.None);

        var ctx = results.First().ExistingExecutionContexts.Single();
        Assert.Equal(0m, ctx.DerivedRemainingQty);
    }

    /// <summary>
    /// 构造最小输入（ErpRemainingQty 取 Stage GoodCompleted+Remaining 使 Position 闭合）
    /// </summary>
    private static ProductionInstructionPositionInput BuildInput(
        IReadOnlyList<WorkOrderSnapshotFact> workOrders,
        IReadOnlyList<OperationProgressFact> operationProgress,
        IReadOnlyList<StageProgressFact> stageProgress)
    {
        var stage = stageProgress.First();
        return new ProductionInstructionPositionInput
        {
            ProductionInstructionNo = "PI-001",
            MaterialId = 1001,
            MaterialCode = "MAT-001",
            FactoryId = 5001,
            FactoryCode = "CN",
            ErpRemainingQty = stage.GoodCompletedQty,   // Position 闭合用（Stage 差分口径）
            StageProgress = stageProgress,
            OperationProgress = operationProgress,
            WorkOrders = workOrders,
            StagePath = new[]
            {
                new StagePathFact { StageCode = stage.StageCode, StageSequence = 1, IsStartStage = true }
            }
        };
    }
}
