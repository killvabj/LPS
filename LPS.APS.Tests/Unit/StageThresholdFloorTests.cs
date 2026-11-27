using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 v1.3 §4.4：有供给阈值但阈值段完成时间未知 ⇒ 不得假回落成整链完成时间**。
///
/// <para>逐字：「要求逐边阈值、真实最后必要数量、实际可信时间、合法绑定和供给类型，
///   **缺阈值不能假回落**；如果阈值合法且后段无关不应过阻」。</para>
///
/// <para>修复前：`map[key] = RequiredStageCompletionTime ?? CompletionTime` —— 阈值存在但阈值段
///   算不出时间时，**静默**换成整链完成时间（后段无关工序也算进去 ⇒ 父件被过度推后，
///   且真实缺口被掩盖）。</para>
///
/// <para>本组用同一对父子需求，只改「阈值 Stage 是否有 LT」：</para>
/// <list type="bullet">
///   <item>有 LT ⇒ 父件下界 = 阈值段完成时间；</item>
///   <item>无 LT ⇒ 阈值时间未知 ⇒ 父件 fail-closed（不排），并留
///     `STAGE_THRESHOLD_COMPLETION_UNKNOWN` 追溯。</item>
/// </list>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class StageThresholdFloorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int ParentMat = 9201;
    private const int ChildMat = 9202;
    private const int Dept = 1601;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 阈值段有LT_阈值完成时间可得_不得报阈值未知()
    {
        var result = await _solver.SolveAsync(Build(childThresholdLeadTime: true));

        // 子件链 CS1(2h) → CS2(3h)，阈值 = CS2 ⇒ 阈值完成 = 计划起点 + 5h。
        var child = result.DemandCompletions.Single(c => c.LogicalDemandKey == "C1");
        Assert.Equal(Day.AddHours(5), child.RequiredStageCompletionTime);

        // 阈值可得 ⇒ **不得**报「阈值完成时间未知」
        Assert.DoesNotContain(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "STAGE_THRESHOLD_COMPLETION_UNKNOWN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 阈值段缺LT_不得用整链时间顶替_须显式留痕()
    {
        var result = await _solver.SolveAsync(Build(childThresholdLeadTime: false));

        // 子件仍产出 CS1 段（有 LT），但阈值 Stage CS2 无 LT ⇒ 阈值完成时间未知。
        //   修复前：`?? CompletionTime` 会拿 CS1 末端（整链）当阈值时间，且**无任何留痕**。
        //   修复后：不入下界 map（父件按缺料 fail-closed），并显式留痕。
        var child = result.DemandCompletions.Single(c => c.LogicalDemandKey == "C1");
        Assert.Null(child.RequiredStageCompletionTime);

        Assert.Contains(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "STAGE_THRESHOLD_COMPLETION_UNKNOWN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 阈值之前链已断_阈值完成时间不可信_须置空且留痕()
    {
        // 2026-11-25（0号位 v1.3 §4.4「实际可信时间」）：
        //   CS1 有 LT 但**缺部门上下文** ⇒ CS1 取不到合法窗、链在此断；
        //   CS2 有 LT、有窗 ⇒ 仍会「推进」，但其时间建在**冻结的游标**上 ⇒ **不可信**。
        //   修复前：`requiredStageEnd` 只要取到值就当真 ⇒ 拿断链后的 CS2 时间当供给下界（过度释放）。
        //   修复后：置 null + `STAGE_THRESHOLD_COMPLETION_UNKNOWN`，父件按缺料 fail-closed。
        var result = await _solver.SolveAsync(Build(childThresholdLeadTime: true, childCs1Dept: false));

        var child = result.DemandCompletions.Single(c => c.LogicalDemandKey == "C1");
        Assert.Null(child.RequiredStageCompletionTime);

        Assert.Contains(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "STAGE_THRESHOLD_COMPLETION_UNKNOWN", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.5 M06 原文：「后段无关不误阻」）
    //   阈值合法、且**断点在阈值之后** ⇒ 阈值时间仍然可信，**不得**因为后段缺 LT 而误阻父件。
    //   这条与上一条是同一条规则的两侧：只看「阈值之前断没断」，不看整链确定性。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 后段缺LT但阈值之前完好_阈值时间仍可信_不得误阻()
    {
        var result = await _solver.SolveAsync(TailMissingRequest());

        // CS1(2h) → CS2(3h, 阈值) 都有 LT 与窗 ⇒ 阈值完成 = Day + 5h；
        //   CS3 无 LT（在阈值之后）⇒ 只影响整链完成时间，不影响阈值下界。
        var child = result.DemandCompletions.Single(c => c.LogicalDemandKey == "C1");
        Assert.Equal(Day.AddHours(5), child.RequiredStageCompletionTime);

        Assert.DoesNotContain(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "STAGE_THRESHOLD_COMPLETION_UNKNOWN", StringComparison.Ordinal));
    }

    /// <summary>CS1→CS2(阈值)→CS3，仅 CS3 缺 LT 的最小请求。</summary>
    private static DomainSolveRequest TailMissingRequest()
    {
        var request = Build(childThresholdLeadTime: true);

        var stages = new List<StageSequenceStep>
        {
            new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
            new() { StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
            new() { StageCode = "CS3", StageSeq = 3, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 }
        };

        var depts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = ChildMat, StageCode = "CS1", ProductionDepartmentId = Dept },
            new() { MaterialId = ChildMat, StageCode = "CS2", ProductionDepartmentId = Dept },
            new() { MaterialId = ChildMat, StageCode = "CS3", ProductionDepartmentId = Dept }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = request.LogicalProductionDemands,
            RoutingOperations = request.RoutingOperations,
            RoutingDependencies = request.RoutingDependencies,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = request.DepartmentWorkWindows,
            StageLeadTimes = request.StageLeadTimes,          // 只有 CS1/CS2，CS3 故意缺
            StageSequenceChains = request.StageSequenceChains,
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2|3:CS3", StageScopeType = "ROOT",
                    ChainSignature = "1:CS1|2:CS2|3:CS3", Stages = stages
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "C1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:CS2|3:CS3", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            ExecutionConstraints = request.ExecutionConstraints,
            StrategySnapshot = request.StrategySnapshot
        };
    }

    private static DomainSolveRequest Build(bool childThresholdLeadTime, bool childCs1Dept = true)
    {
        var leadTimes = new List<StageLeadTimeFact>
        {
            new() { MaterialId = ChildMat, StageCode = "CS1", LeadTimeHours = 2m, MatchLevel = "DEPT_EXACT" }
        };
        if (childThresholdLeadTime)
        {
            leadTimes.Add(new StageLeadTimeFact
            {
                MaterialId = ChildMat, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT"
            });
        }

        // 2026-11-25：`childCs1Dept = false` ⇒ CS1 无部门上下文 ⇒ CS1 取不到窗（链在阈值前断）。
        var contexts = new List<MaterialStageDepartmentContextDto>();
        if (childCs1Dept)
        {
            contexts.Add(new() { MaterialId = ChildMat, StageCode = "CS1", ProductionDepartmentId = Dept });
        }
        contexts.Add(new() { MaterialId = ChildMat, StageCode = "CS2", ProductionDepartmentId = Dept });

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "P1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "P1",
                    MaterialId = ParentMat, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "CS1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(30)
                },
                new()
                {
                    LogicalDemandKey = "C1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 2, DemandKey = "C1",
                    MaterialId = ChildMat, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "CS1",
                    // 供给阈值 = CS2（父件做到该 Stage 即可用）
                    RequiredStageCode = "CS2",
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(30)
                }
            },
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                new()
                {
                    ConsumerLogicalDemandKey = "P1",
                    ProducerLogicalDemandKey = "C1",
                    LagMinutes = 0
                }
            },
            RoutingOperations = new List<RoutingOperation>(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = contexts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = leadTimes,
            StageSequenceChains = new List<StageSequenceChain>(),
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT", ChainSignature = "1:CS1|2:CS2",
                    Stages = new List<StageSequenceStep>
                    {
                        new()
                        {
                            StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept,
                            StageFactoryCode = Factory, StageFactoryId = 1
                        },
                        new()
                        {
                            StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept,
                            StageFactoryCode = Factory, StageFactoryId = 1
                        }
                    }
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "C1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },
            Resources = new List<ResourceDefinition>(),
            CalendarSlots = new List<ResourceCalendarSlot>(),
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD", AllowMerge = false, AllowSplit = false
                },
                BatchPolicies = Array.Empty<BatchPolicyRuleSnapshot>(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
