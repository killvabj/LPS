using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **A-6 验收：三个需求口账计数器**（1号位 2026-11-02）。
///
/// <para><b>为什么现在才落</b>：我方 2026-10-25《FAMILY_X 四份对账回执》认领 A-6 时就点了这三个名字
/// （`DemandsReceived` / `DemandQtySum` / `DemandsWithLandedTask`），当轮**认领了但没落码**；
/// 2号位 2026-11-02 回执 §五.3 报「全仓零命中，请确认是还没落、还是改了名」。
/// 如实答案是 **没落，不是改名** —— 本类守的就是补上之后的取值口径。</para>
///
/// <para><b>为什么这三个数容易被读错</b>（本类逐条钉住）：</para>
/// <list type="bullet">
///   <item>`DemandsReceived` 是**需求条数**，不是 `SolveSummary.TotalDrafts`（后者是 Task 条数）；</item>
///   <item>`DemandQtySum` 是**需求侧净量**（`NetOutputQty`），不是已排量；</item>
///   <item>`DemandsWithLandedTask` 只认**能归回本次入参需求键**的 Task ⇒ 锁定任务不计入。</item>
/// </list>
///
/// <para>⚠ 取 `SolverDiagnostics.Active`（`AsyncLocal`，本执行上下文独占），**不取** `LiveCounters`
/// —— 后者是普通静态，全量并行时会被别的求解用例覆盖（与 `SetupInitialStateProbeTests` 同口径）。</para>
/// </summary>
public class DemandAccountingCountersTests
{
    private static readonly DateTime PlanningStart = new(2026, 11, 1);
    private static readonly DateTime PlanningEnd = new(2026, 12, 1);
    private const int DeptId = 100;
    private const string Stage = "STAGE1";

    // ───────────────────── 一、收到的需求：按条计、按净量求和 ─────────────────────

    [Fact(DisplayName = "A-6｜两条需求都排下 ⇒ 收到 2 条、净量 2、有 Task 的 2 条")]
    public async Task AllLanded_CountsMatchIntake()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var request = BuildRequest(
            Demand("M1", 1m, Stage),
            Demand("M2", 1m, Stage));

        var result = await new FiniteCapacitySolver().SolveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        var c = SolverDiagnostics.Active;
        Assert.NotNull(c);
        Assert.Equal(2L, c!.DemandsReceived);
        Assert.Equal(2m, c.DemandQtySum);
        Assert.Equal(2L, c.DemandsWithLandedTask);
    }

    [Fact(DisplayName = "A-6｜一条排下、一条阶段不存在 ⇒ 收到仍是 2 条，有 Task 的只有 1 条")]
    public async Task OneLanded_ReceivedStillCountsBoth()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var request = BuildRequest(
            Demand("M1", 1m, Stage),
            Demand("M2", 1m, "STAGE_NOT_IN_ROUTING"));

        await new FiniteCapacitySolver().SolveAsync(request);

        var c = SolverDiagnostics.Active;
        Assert.NotNull(c);

        // ★ 鉴别点：**收到**按入参计，不因排不下而变少。
        Assert.Equal(2L, c!.DemandsReceived);
        Assert.Equal(2m, c.DemandQtySum);

        // ★ 鉴别点：**有 Task 的**按实际结果计。若实现写成「恒等于 DemandsReceived」，这里必红。
        Assert.Equal(1L, c.DemandsWithLandedTask);
    }

    [Fact(DisplayName = "A-6｜净量按各自 NetOutputQty 求和，不是按条数")]
    public async Task QtySum_UsesNetOutputQty()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var request = BuildRequest(
            Demand("M1", 3.5m, Stage),
            Demand("M2", 6.25m, Stage));

        await new FiniteCapacitySolver().SolveAsync(request);

        var c = SolverDiagnostics.Active;
        Assert.NotNull(c);
        Assert.Equal(2L, c!.DemandsReceived);
        Assert.Equal(9.75m, c.DemandQtySum);   // 不是 2，也不是别的口径
    }

    // ───────────────────── 二、空输入：三个数全 0，不臆造 ─────────────────────

    [Fact(DisplayName = "A-6｜无需求 ⇒ 三个计数器全 0")]
    public async Task NoDemand_AllZero()
    {
        using var scope = SolverDiagnostics.BeginScope();

        await new FiniteCapacitySolver().SolveAsync(BuildRequest());

        var c = SolverDiagnostics.Active;
        Assert.NotNull(c);
        Assert.Equal(0L, c!.DemandsReceived);
        Assert.Equal(0m, c.DemandQtySum);
        Assert.Equal(0L, c.DemandsWithLandedTask);
    }

    // ───────────────────── 三、与 Task 条数不是一回事 ─────────────────────

    [Fact(DisplayName = "A-6｜需求条数 ≠ Task 条数：一条需求落多个工序，Received 仍是 1")]
    public async Task ReceivedIsDemandCount_NotTaskCount()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var request = BuildRequest(Demand("M1", 1m, Stage), operationCount: 3);

        var result = await new FiniteCapacitySolver().SolveAsync(request);
        Assert.True(result.Success, result.ErrorMessage);

        var c = SolverDiagnostics.Active;
        Assert.NotNull(c);
        Assert.Equal(1L, c!.DemandsReceived);

        // 同一条需求被拆成多个工序 Task ⇒ Task 数明显大于需求数，两者不可混读。
        Assert.True(result.FinalTasks.Count > 1,
            $"夹具应产出多个 Task，实际 {result.FinalTasks.Count} 条");
        Assert.Equal(1L, c.DemandsWithLandedTask);
    }

    // ════════════════════════ 夹具 ════════════════════════

    private static DomainSolveRequest BuildRequest(params LogicalProductionDemand[] demands)
        => BuildRequestCore(demands, operationCount: 1);

    private static DomainSolveRequest BuildRequest(LogicalProductionDemand demand, int operationCount)
        => BuildRequestCore(new[] { demand }, operationCount);

    private static DomainSolveRequest BuildRequestCore(
        IReadOnlyList<LogicalProductionDemand> demands, int operationCount)
    {
        var ops = new List<RoutingOperation>();
        var elig = new List<OperationResourceEligibility>();
        for (var i = 1; i <= operationCount; i++)
        {
            var opCode = "OP" + i.ToString("00");
            ops.Add(new RoutingOperation
            {
                MaterialId = 1,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = opCode,
                StageCode = Stage,
                StandardDuration = 60m * 60m,   // 秒（PhaseOneConstraintBuilder 里 /60 折成分钟）
                SetupTime = 0m
            });
            elig.Add(new OperationResourceEligibility
            {
                MaterialId = 1,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = opCode,
                ResourceId = 1,
                Priority = 1,
                CapacityFactor = 1m
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands.ToList(),
            RoutingOperations = ops,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = Stage, ProductionDepartmentId = DeptId }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new()
                {
                    ResourceId = 1,
                    Start = PlanningStart,
                    End = PlanningStart.AddDays(20),
                    IsAvailable = true
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
                BatchPolicies = TestBatchPolicy.Permissive(new[] { 1 }, DeptId),
                SolverStrategy = new SolverStrategyBlock
                {
                    BottleneckMode = DynamicBottleneckMode.Auto,
                    AnchorResourceCode = null
                }
            }
        };
    }

    private static LogicalProductionDemand Demand(string key, decimal qty, string startStage)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = key,
            MaterialId = 1,
            FactoryId = 1,
            StartStageCode = startStage,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            RequiredAvailableTime = PlanningEnd,
            DemandSequence = 1,
        };
}
