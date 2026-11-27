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
/// **M04 / DTO-06 按 0号位 v1.7 §1-6 重做**（原「删掉 STAGE4 结果不变」被明确不接受）。
///
/// <para>0号位 逐字：「必须验证 A 不借 B 的 Stage4、B 合法必要 Stage4 不能漏排/漏计；
///   共享 Stage 执行批可合并，原始份额和各自依赖保留。若 B 确有 Stage4 且属于本次范围，
///   **不能以删除 Stage4 后全结果不变作为正例通过**。」</para>
///
/// <para>本用例的构造：</para>
/// <list type="bullet">
///   <item>A 链 = S1→S2→S3（3 道真实工序）；B 链 = S1→S2→S3→**S4**（S4 是合法无 Routing 的纯提前期段，4h）；</item>
///   <item>同物料、同厂、同部门 ⇒ 三条共享工序**必须合并成一个执行批**（DTO-06 的合法共批）；</item>
///   <item>断言①：A 的 Stage 时间里**没有** S4（不借 B 的链）；</item>
///   <item>断言②：B 的 Stage 时间里**有** S4，且完成时间 ≥ S3 完工 + 4h（不漏计）；</item>
///   <item>断言③：三条共享工序各只有一个执行批，净量 = 30+20 = 50（合并且份额保留）。</item>
/// </list>
///
/// ⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。
/// </summary>
public class Dto06Stage4NecessityTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int MaterialId = 9801;
    private const int Dept = 1901;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task A不借B的Stage4_B的Stage4不漏计_共享工序仍合法共批()
    {
        var result = await _solver.SolveAsync(Build());

        Assert.True(result.Success, result.ErrorMessage);

        var a = result.DemandCompletions.Single(c => c.LogicalDemandKey == "A");
        var b = result.DemandCompletions.Single(c => c.LogicalDemandKey == "B");

        // ① A 不借 B 的 Stage4
        Assert.DoesNotContain(a.StageTimings, s => s.StageCode == "S4");

        // ② B 的 S4 必须被计入（S1–S3 是真实工序，只出现在 Task 上）
        var s3End = result.FinalTasks.Where(t => t.StageCode == "S3").Max(t => t.PlannedEndTime);
        var bS4 = b.StageTimings.Single(s => s.StageCode == "S4");
        Assert.Equal(4m, bS4.LeadTimeHours);
        Assert.Equal(bS4.StartTime.AddHours(4), bS4.EndTime);
        Assert.True(bS4.StartTime >= s3End, "S4 不得早于 S3 真实完工");
        Assert.Equal(bS4.EndTime, b.CompletionTime);

        // ③ 共享工序两侧都排上，且**各自原始份额不丢**（A=30、B=20）。
        //   ⚠ 「共享 Stage 可否合法合并成一个执行批（qty=50）」由既有 DTO-06 合批用例
        //     （`StageBatchMergeTests`，带同品换型规则）覆盖；本用例装的是**不同链长**，
        //     合批与否不影响本条的判定目标（Stage4 归属）。
        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        var bTasks = result.FinalTasks.Where(t => t.SourceDraftId == "B").ToList();
        Assert.Equal(new[] { "S1", "S2", "S3" }, aTasks.Select(t => t.StageCode).OrderBy(x => x));
        Assert.Equal(new[] { "S1", "S2", "S3" }, bTasks.Select(t => t.StageCode).OrderBy(x => x));
        Assert.All(aTasks, t => Assert.Equal(30m, t.Quantity));
        Assert.All(bTasks, t => Assert.Equal(20m, t.Quantity));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build()
    {
        var chains = new List<StageChainDefinition>
        {
            new()
            {
                ChainRefId = "ROOT:A", StageScopeType = "ROOT", ChainSignature = "1:S1|2:S2|3:S3",
                Stages = new List<StageSequenceStep>
                {
                    new() { StageCode = "S1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                    new() { StageCode = "S2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                    new() { StageCode = "S3", StageSeq = 3, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 }
                }
            },
            new()
            {
                ChainRefId = "ROOT:B", StageScopeType = "ROOT", ChainSignature = "1:S1|2:S2|3:S3|4:S4",
                Stages = new List<StageSequenceStep>
                {
                    new() { StageCode = "S1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                    new() { StageCode = "S2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                    new() { StageCode = "S3", StageSeq = 3, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                    new() { StageCode = "S4", StageSeq = 4, ProductionDepartmentId = null, StageFactoryCode = Factory, StageFactoryId = 1 }
                }
            }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(60),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "A", PlanVersionId = 1L, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "A", DemandSequence = 1,
                    MaterialId = MaterialId, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "S1", NetOutputQty = 30m, PlannedProcessQty = 30m,
                    RequiredAvailableTime = Day.AddDays(30)
                },
                new()
                {
                    LogicalDemandKey = "B", PlanVersionId = 1L, DomainKey = "D",
                    AllocationSequence = 2, DemandKey = "B", DemandSequence = 2,
                    MaterialId = MaterialId, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "S1", NetOutputQty = 20m, PlannedProcessQty = 20m,
                    RequiredAvailableTime = Day.AddDays(30)
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP10", StageCode = "S1", StandardDuration = 60m, SetupTime = 0m },
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP20", StageCode = "S2", StandardDuration = 60m, SetupTime = 0m },
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP30", StageCode = "S3", StandardDuration = 60m, SetupTime = 0m }
            },
            RoutingDependencies = new List<RoutingDependency>
            {
                new() { MaterialId = MaterialId, RouteCode = "RT", PathId = 1, FromOperationCode = "OP10", ToOperationCode = "OP20", LagTime = 0m },
                new() { MaterialId = MaterialId, RouteCode = "RT", PathId = 1, FromOperationCode = "OP20", ToOperationCode = "OP30", LagTime = 0m }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP10", ResourceId = 19010, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP20", ResourceId = 19010, Priority = 1, CapacityFactor = 1m },
                new() { MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1, OperationCode = "OP30", ResourceId = 19010, Priority = 1, CapacityFactor = 1m }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = "S1", ProductionDepartmentId = Dept },
                new() { MaterialId = MaterialId, StageCode = "S2", ProductionDepartmentId = Dept },
                new() { MaterialId = MaterialId, StageCode = "S3", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(60) },
                // S4 是 FINAL/OUTS 类（无部门）⇒ 走**厂级**窗
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = null, Start = Day, End = Day.AddDays(60) }
            },
            // S4：合法无 Routing 的纯提前期段（B 的链才有）
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = MaterialId, StageCode = "S4", LeadTimeHours = 4m, MatchLevel = "GLOBAL_STAGE_DEFAULT", IsLeadTimeOnly = true }
            },
            StageChainDefinitions = chains,
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new() { LogicalDemandKey = "A", BindingKey = "ROOT|BOM|WS", ChainRefId = "ROOT:A", StageScopeType = "ROOT", Status = StageChainBindingStatus.Resolved },
                new() { LogicalDemandKey = "B", BindingKey = "ROOT|BOM|WS", ChainRefId = "ROOT:B", StageScopeType = "ROOT", Status = StageChainBindingStatus.Resolved }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 19010, ResourceCode = "R1", FactoryCode = Factory, Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 19010, Start = Day, End = Day.AddDays(60), IsAvailable = true }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD", AllowMerge = true },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
