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
/// **回归锁（r14490，2026-11-26，0号位 v1.13 复核）**：
/// 「空 `Resources` 整体退回旧行为」必须不再成立。
///
/// <para><b>它锁的是什么 bug</b>：`ConstraintContext.DefinedResourceIds` 原以
/// `Count &gt; 0` 当护栏开关 ⇒ **显式下发空 `Resources`（= 漏装）** 与 **压根没下发 `Resources`
/// （= 旧夹具 / 老载荷）** 被混成一件事，两者都静默 fail-open，找槽按「空占用表」放行。
/// 0号位 v1.13 逐字点名：「空 Resources 整体退回旧行为」。</para>
///
/// <para><b>现在的判定表</b>（开关 = `ResourcesPayloadPresent`）：</para>
/// <list type="number">
///   <item>`Resources == null` ⇒ Phase1 直接抛 `ArgumentNullException`（实测见下方用例），**到不了护栏**
///     ⇒ 「旧夹具/老载荷」的真实形态是**空列表**，不是 null；</item>
///   <item>`Resources == []` ⇒ 护栏**启用** ⇒ 资格指向的资源全部按「资源未知」拒绝，<b>不产出 Task</b>；</item>
///   <item>并且必须留一条 `RESOURCES_PAYLOAD_EMPTY_BUT_ELIGIBILITY_PRESENT`（<b>绝不静默</b>）。</item>
/// </list>
///
/// ⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。
/// </summary>
public class ResourcePayloadPresenceGuardTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int MaterialId = 9711;
    private const int Dept = 1811;
    private const int ResourceId = 9911;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>情形 ②：显式空 `Resources` + 资格指向资源 ⇒ 漏装，**不得**按空字典放行。</summary>
    [Fact]
    public async Task 显式空Resources_资格指向资源_不得按空字典放行()
    {
        var result = await _solver.SolveAsync(Build(ResourcesMode.ExplicitEmpty));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
    }

    /// <summary>情形 ③：漏装必须留痕 —— 不能只看结果「全未排」却不知道为什么。</summary>
    [Fact]
    public async Task 显式空Resources_必须留漏装注记()
    {
        var result = await _solver.SolveAsync(Build(ResourcesMode.ExplicitEmpty));

        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == "RESOURCES_PAYLOAD_EMPTY_BUT_ELIGIBILITY_PRESENT");
    }

    /// <summary>
    /// **实测记录（r14490）**：`Resources == null` 在求解路径上**根本到不了护栏** ——
    ///   `PhaseOneConstraintBuilder.BuildResourceCodes`（`:1495`）先对 `request.Resources` 做 `GroupBy`，
    ///   null 直接抛 `ArgumentNullException`。
    ///   ⇒ 「旧夹具 / 老载荷」这一形态**在当前代码里不存在**：真正的旧形态是**空列表**。
    ///   这也说明 `ResourcesPayloadPresent == false` 那一支是**防御性死支**（只为直接构造
    ///   `ConstraintContext` 的测试路径保留），用它做 fail-open 恰恰就是 0号位 点名的缺陷。
    /// </summary>
    [Fact]
    public async Task Resources为null_在Phase1即抛异常_到不了护栏()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _solver.SolveAsync(Build(ResourcesMode.Absent)));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private enum ResourcesMode
    {
        /// <summary>`Resources` 字段不存在（null）—— 仅用于记录 Phase1 直接抛异常这一事实。</summary>
        Absent,

        /// <summary>`Resources` 下发但为空列表 = 漏装。</summary>
        ExplicitEmpty
    }

    private static DomainSolveRequest Build(ResourcesMode mode)
    {
        const string stage = "STAGE1";
        const string op = "OP10";

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "D1", DemandSequence = 1,
                    MaterialId = MaterialId, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = stage, NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = op, StageCode = stage, StandardDuration = 3600m, SetupTime = 0m
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "DEFAULT",
                    OperationCode = op, ResourceId = ResourceId, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = stage, ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(30) }
            },
            Resources = mode == ResourcesMode.Absent ? null : new List<ResourceDefinition>(),
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResourceId, Start = Day, End = Day.AddDays(30), IsAvailable = true }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = true, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
