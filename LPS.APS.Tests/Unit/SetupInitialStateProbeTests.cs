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
/// **R08 落码实测（2026-10-29）**：`InitialState`（`fromMaterialId = null`）这一条「不可信却长得像可信」的出口，
/// 本轮做两件不依赖外部字段的修补，本文件各钉一条：
///
/// <list type="number">
///   <item><b>措辞去歧义</b> —— 旧文本「当前资源无可追溯上一产品」把「有前产品但追溯断链」（不可信）
///     洗成了「本来就没有前产品」（可信）。现文本必须**同时**说出两种成因，且不得再断言「无可追溯」。</item>
///   <item><b>落点计数</b> —— `SolverDiagnostics.Counters.SetupInitialStatePlacements` 只在
///     **Phase2 实际落下 Task** 时 +1，**不在** `ResolveSetupCore` 每次解析时 +1。
///     后者在 `SetupOptimizer.FindSlotWithDynamicSetup` 的有界迭代里会被反复调用
///     （至少 `:459` 与 `:467` 两次），数出来是「试排次数」，读数会骗人。</item>
/// </list>
///
/// <para><b>本文件不证明什么（写清楚，免得被当成过了）</b>：它**不能**区分
/// 「真的计划期首件」与「追溯断链」—— 求解器目前没有这个能力，缺的是外部「前状态可信度」标记，
/// 已单独出件提请（R08 致 2号位）。本文件只能证明「这条不确定路径的落点数被如实统计了」。</para>
/// </summary>
public class SetupInitialStateProbeTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private const string Stage1 = "STAGE1";
    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const int ResourceId = 1;
    private const int FactoryId = 1;

    /// <summary>
    /// **单需求、单资源** ⇒ 该资源上不存在任何前产品 ⇒ `InitialState`。
    ///
    /// <para>为什么用 1 条需求当鉴别夹具：若计数写在 `ResolveSetupCore` 里，一次
    /// `FindSlotWithDynamicSetup` 至少解析两次（`:459` 与 `:467`），计数会 ≥2；
    /// 只有写在 Phase2 的落点点位上，计数才恰好等于 1。这条是**有鉴别力**的，不是恒真断言。</para>
    /// </summary>
    [Fact]
    public async Task T1_首件_InitialState按落点计数为1且措辞不再断言无前产品()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var result = await new FiniteCapacitySolver().SolveAsync(BuildSingleDemand());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.FinalTasks);

        // ① 措辞：必须说出「未取到」，不得再把「追不到」写成「没有」。
        var note = Assert.Single(result.SolveTraceNotes,
            n => n.ReasonCode == "INITIAL_SETUP_STATE");
        Assert.Contains("未取到上一相邻 Task 的产品", note.Message);
        Assert.Contains("追溯断链", note.Message);
        Assert.DoesNotContain("无可追溯上一产品", note.Message);

        // ② 计数：按「落下的 Task」记 1 次，不按「解析调用」记多次。
        //   ⚠ 取 `Active`（AsyncLocal，本执行上下文独占），**不要**取 `LiveCounters`
        //     —— 后者是普通静态、专供「别的线程中途轮询」用，会被并行跑的其他求解用例覆盖
        //     ⇒ 全量并行时本断言会随机读到别家的计数器（2026-10-29 实测 T1/T2 交替假红）。
        var counters = SolverDiagnostics.Active;
        Assert.NotNull(counters);
        Assert.Equal(1L, counters!.SetupInitialStatePlacements);
    }

    /// <summary>
    /// 全面无需求 ⇒ 没有任何 `InitialState` 落点，计数必须为 0。
    /// 与 T1 合起来说明该计数**确实跟着排程走**，不是启动时乱加一个常数。
    /// </summary>
    [Fact]
    public async Task T2_无需求_计数为0()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var request = BuildSingleDemand(withDemand: false);

        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        // 同 T1：本上下文独占的计数器，不用会被并行用例覆盖的 `LiveCounters`。
        var counters = SolverDiagnostics.Active;
        Assert.NotNull(counters);
        Assert.Equal(0L, counters!.SetupInitialStatePlacements);
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    private static DomainSolveRequest BuildSingleDemand(bool withDemand = true)
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            LogicalProductionDemands = !withDemand
                ? new List<LogicalProductionDemand>()
                : new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = "D1",
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    StartStageCode = Stage1,
                    NetOutputQty = 6m,
                    PlannedProcessQty = 6m,
                    RequiredAvailableTime = Day.AddDays(20),
                    DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },

            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = Stage1,
                    StandardDuration = 60m,          // 源事实：秒 ⇒ 1 分钟/件 ⇒ 6 分钟/需求
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },

            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = ResourceId,
                    Priority = 1, CapacityFactor = 1m
                }
            },

            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage1, ProductionDepartmentId = DeptId }
            },

            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = ResourceId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },

            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResourceId, Start = Day, End = Day.AddDays(29), IsAvailable = true }
            },

            MaterialConstraints = !withDemand
                ? new List<MaterialAvailabilitySlice>()
                : new List<MaterialAvailabilitySlice>
            {
                new()
                {
                    AllocationSequence = 1,
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    Quantity = 10m,
                    AvailableTime = Day
                }
            },

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
}
