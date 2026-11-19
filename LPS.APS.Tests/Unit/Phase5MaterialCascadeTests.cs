using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Phase5 check#5（缺料剔除）× check#7（TaskDependency）：**级联剔除下游 + 裁剪悬空边**（2026-11-18）。
///
/// <para>真因（FAMILY_CYL 离线重放 UPMISS）：check#5 把缺料的上游 Task 剔除后，`taskDependencies`
/// 是在剔除**之前**生成的 ⇒ 下游的依赖边悬空指向不存在的上游 ⇒ check#7 判 `UPMISS`、整域废。
/// 修法：沿依赖边向下游级联剔除（上游产不出⇒下游也产不出），并只保留两端都在场的边。</para>
///
/// <para>判别点：A（缺料，被 check#5 删）→ B（不缺料，但依赖 A）。整改前：A 删、B 留、边 A→B 悬空⇒UPMISS 红；
/// 整改后：A、B 一并级联剔除、边裁剪、判据放行。</para>
///
/// <para>纯内存、直调 <see cref="PhaseFiveCompression.ValidateHardResultForTest"/>，不碰数据库。</para>
/// </summary>
public sealed class Phase5MaterialCascadeTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 8, 0, 0);

    private static LogicalProductionDemand Demand(string key, decimal qty, long alloc, int materialId)
        => new()
        {
            LogicalDemandKey = key,
            DemandKey = key,
            AllocationSequence = alloc,
            MaterialId = materialId,
            FactoryId = 1,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            StartStageCode = "S1",
        };

    private static FinalTaskDraft Task(string id, string demandKey, string op, decimal qty, DateTime start)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = demandKey,
            MaterialId = 1,
            FactoryId = 1,
            StageCode = "S1",
            OperationCode = op,
            Quantity = qty,
            PlannedProcessQty = qty,
            PlannedStartTime = start,
            PlannedEndTime = start.AddHours(1),
        };

    [Fact(DisplayName = "check#5缺料删上游 → 级联删下游 + 不放UPMISS整域废")]
    public void CascadeRemovesDownstream_NoUmiss()
    {
        // 上游 A：需求 D1/alloc1，OP10，起点 T0；物料要到 T0+10d ⇒ 缺料，check#5 剔除。
        // 下游 B：需求 D2/alloc2，OP20，起点 T0+11d；物料 T0 即到 ⇒ 自身不缺料，但依赖 A ⇒ 应被级联剔除。
        var tasks = new List<FinalTaskDraft>
        {
            Task("A", "D1", "OP10", 5m, T0),
            Task("B", "D2", "OP20", 5m, T0.AddDays(11)),
        };
        var deps = new List<FinalTaskPeggingDraft>
        {
            new()
            {
                UpstreamFinalDraftId = "A",
                DownstreamFinalDraftId = "B",
                UpstreamMaterialId = 1,
                DownstreamMaterialId = 1,
                Quantity = 5m,
                LagTime = 0m,
            },
        };
        var request = new DomainSolveRequest
        {
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Demand("D1", 5m, 1L, materialId: 1),
                Demand("D2", 5m, 2L, materialId: 1),
            },
        };

        var constraints = new ConstraintContext();
        constraints.MaterialAvailability[1L] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 5m, AvailableTime = T0.AddDays(10) },
            new() { Quantity = 5m, AvailableTime = T0.AddDays(20) },
        };
        constraints.MaterialAvailability[2L] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 5m, AvailableTime = T0 },
        };

        var (valid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, new List<AllocationTaskShare>(), deps, request, constraints,
            new HashSet<string>(), new Dictionary<string, List<(string, decimal)>>());

        // 整改前红：A 删、B 留、边 A→B 悬空 ⇒ check#7 UPMISS ⇒ valid=false。
        // 整改后：A、B 级联剔除、边裁剪 ⇒ 放行，且两 Task 都不在结果里。
        Assert.True(valid, "缺料上游 + 其下游应转未排、判据放行（不得 UPMISS 整域废）。" + error);
        Assert.Empty(tasks);
    }

    [Fact(DisplayName = "check#8：含缺料剔除的需求 Σ<NetOutput 应放行（认作未排，非‘已排缺量’）")]
    public void PartialMaterialRemoval_ClosureAllowsUnderProduction()
    {
        // 需求 D1 NetOutput=20，同一工序 OP10 拆两份各 10（合 20）。
        // Task A(10) 缺料被 check#5 删；Task B(10) 自身料够留存。无依赖边（B 不被级联）。
        // Result：存活 Σ=10 < 20，但 D1 含缺料剔除 ⇒ 应认作未排、允许 ≤。
        var tasks = new List<FinalTaskDraft>
        {
            Task("A", "D1", "OP10", 10m, T0),
            Task("B", "D1", "OP10", 10m, T0.AddDays(11)),
        };
        var request = new DomainSolveRequest
        {
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Demand("D1", 20m, 1L, materialId: 1),
            },
        };
        var constraints = new ConstraintContext();
        constraints.MaterialAvailability[1L] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 10m, AvailableTime = T0.AddDays(10) },
        };

        var (valid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, new List<AllocationTaskShare>(), new List<FinalTaskPeggingDraft>(), request, constraints,
            new HashSet<string>(), new Dictionary<string, List<(string, decimal)>>());

        // 整改前红：D1 不在未排集合 ⇒ Σ=10 != 20 ⇒ DEMAND_STAGE_QTY_GAP 硬拒。
        // 整改后：D1 含缺料剔除 ⇒ 认作未排 ⇒ 允许 ≤ ⇒ 放行；A 删、B 留。
        Assert.True(valid, "含缺料剔除的需求不足 NetOutput 应放行（不得判‘已排缺量’）。" + error);
        Assert.Single(tasks);              // 只剩 B
        Assert.Equal("B", tasks[0].FinalDraftId);
    }
}