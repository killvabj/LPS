using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R06 动态瓶颈 —— 四模式必须影响实际候选/结果**（0号位 2026-10-28
/// 《01_正式回复1号位_R04秒转分钟唯一装配_WAIT_ONLY_AUTO与R06本地先修…》§1-R06）：
/// 0号位 原文「Phase3 仅诊断不等于瓶颈策略进入 Phase1/2/4 … 只需起码一组 AUTO 与
/// PREFER/FORCE/NOT_ANCHOR 导致实际候选/结果差异的最小合法例」。
///
/// 落地口径（只调**候选顺序**，不增删候选、不改任何硬约束）：
///   · `Auto`         — 不干预（锚点编码按冻结字段口径在 Auto 下本就忽略）；
///   · `ForceAnchor`  — 锚点在合格集内 ⇒ 排最前；
///   · `PreferAnchor` — 锚点有**静态负荷**（至少一条需求的合格工序能排上它）⇒ 排最前，否则回落 Auto；
///   · `NotAnchor`    — 锚点排最后（不从合格集删除）。
///
/// 两个夹具把四模式全部分开：
///   · 夹具 A：锚点 R1 资格优先级 **1**（Auto 本来就会选 R1）⇒ **NotAnchor 与 Auto 结果相反**；
///   · 夹具 B：锚点 R1 资格优先级 **2**（Auto 选 R2）⇒ **Force/Prefer 与 Auto 结果相反**。
/// 两个夹具的资源、日历、工序时长完全一致，**只改资格优先级与策略** ⇒ 结果差异只能来自策略本身。
/// </summary>
public class R06BottleneckModeSelectionTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly FiniteCapacitySolver _solver = new();

    // ── 夹具 A：锚点 R1 优先级 1（Auto 选 R1）─────────────────────────

    [Fact]
    public async Task 夹具A_Auto_按资格优先级选锚点R1()
    {
        var task = await Solve(anchorPriority: 1, DynamicBottleneckMode.Auto);
        Assert.Equal(1, task.ResourceId);
    }

    [Fact]
    public async Task 夹具A_NotAnchor_锚点排最后_改选R2()
    {
        var task = await Solve(anchorPriority: 1, DynamicBottleneckMode.NotAnchor);
        Assert.Equal(2, task.ResourceId);   // 与 Auto 相反
    }

    // ── 夹具 B：锚点 R1 优先级 2（Auto 选 R2）─────────────────────────

    [Fact]
    public async Task 夹具B_Auto_按资格优先级选R2()
    {
        var task = await Solve(anchorPriority: 2, DynamicBottleneckMode.Auto);
        Assert.Equal(2, task.ResourceId);
    }

    [Fact]
    public async Task 夹具B_ForceAnchor_锚点排最前_改选R1()
    {
        var task = await Solve(anchorPriority: 2, DynamicBottleneckMode.ForceAnchor);
        Assert.Equal(1, task.ResourceId);   // 与 Auto 相反
    }

    [Fact]
    public async Task 夹具B_PreferAnchor_锚点有静态负荷_排最前_改选R1()
    {
        var task = await Solve(anchorPriority: 2, DynamicBottleneckMode.PreferAnchor);
        Assert.Equal(1, task.ResourceId);   // 与 Auto 相反
    }

    [Fact]
    public async Task 夹具B_NotAnchor_锚点本就最后_结果与Auto相同()
    {
        // 反证「NotAnchor 只是把锚点排最后」而不是「无条件换资源」：锚点已是最末 ⇒ 结果应保持 R2。
        var task = await Solve(anchorPriority: 2, DynamicBottleneckMode.NotAnchor);
        Assert.Equal(2, task.ResourceId);
    }

    /// <summary>四模式都只是改顺序：任何模式下排程都**必须成功且落在日历内**（合法性不因策略改变）。</summary>
    [Theory]
    [InlineData(1, DynamicBottleneckMode.Auto)]
    [InlineData(1, DynamicBottleneckMode.PreferAnchor)]
    [InlineData(1, DynamicBottleneckMode.ForceAnchor)]
    [InlineData(1, DynamicBottleneckMode.NotAnchor)]
    [InlineData(2, DynamicBottleneckMode.Auto)]
    [InlineData(2, DynamicBottleneckMode.PreferAnchor)]
    [InlineData(2, DynamicBottleneckMode.ForceAnchor)]
    [InlineData(2, DynamicBottleneckMode.NotAnchor)]
    public async Task 任何模式_排程都必须成功且在日历窗内(int anchorPriority, DynamicBottleneckMode mode)
    {
        var task = await Solve(anchorPriority, mode);

        Assert.True(task.PlannedStartTime >= Day);
        Assert.True(task.PlannedEndTime <= Day.AddDays(29));
        Assert.Equal(1d, (task.PlannedEndTime - task.PlannedStartTime).TotalMinutes, 3);
    }

    // ─────────────────────────── 构造辅助 ───────────────────────────

    private async Task<FinalTaskDraft> Solve(int anchorPriority, DynamicBottleneckMode mode)
    {
        var otherPriority = anchorPriority == 1 ? 2 : 1;

        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = 1, FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 60m,                      // 源事实：**秒** = 1 分钟
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            // 同一工序两条资格：R1（锚点）与 R2，优先级由夹具参数决定；两台都是全量日历、系数 1。
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = 1, Priority = anchorPriority, CapacityFactor = 1m },
                new() { MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = 2, Priority = otherPriority, CapacityFactor = 1m }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
                new() { ResourceId = 2, ResourceCode = "R2", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(29), IsAvailable = true },
                new() { ResourceId = 2, Start = Day, End = Day.AddDays(29), IsAvailable = true }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                SolverStrategy = new SolverStrategyBlock
                {
                    BottleneckMode = mode,
                    AnchorResourceCode = "R1"      // 锚点固定指向 R1，两个夹具只改 mode 与优先级
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, 100)
            }
        };

        var result = await _solver.SolveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);
        return Assert.Single(result.FinalTasks);
    }
}
