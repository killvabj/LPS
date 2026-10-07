using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Phase2 多 Routing 候选（Path-aware）回归测试 —— 0号位 2026-10-07 裁决 Q-1/Q-2/Q-3 + §四/§五 落码验收。
///
/// 被测分支：<see cref="PhaseTwoInitialScheduler"/> 需求级「候选解析 → 试排 → 择优 → 落定」四步。
///
/// 判据来源（逐条对应，不引新口径）：
/// · **Q-1**：统一四层目标（① 硬约束 → ② 履约 → ③ 交期/Direction → ④ 次级），
///   **禁止新增 Routing 专属加权目标函数**；PreferredResource 只进次级、不得 Hard Lock。
/// · **Q-2**：「候选内有界联合择优就是 V1 范围」，但**不得脱离真实上下文孤立选路**；
///   冻结业务单位 = Execution Batch（每个 ExecutionBatchDraftKey 一条完整 Path）。
/// · **Q-3**：任务级按自身 RouteCode + PathId 解析；**Fail Closed 红线** ——
///   声明了固定路径而图中不存在 ⇒ 不排，**不得**回退 TryGetSingleRoutingGraph 猜 Path。
/// · **§四**：**禁止用「候选 Path 条数」反推 A/B/C 身份**（故本文件只断言行为，不造桶枚举）。
/// · **§五**：Scheduling 内部自组 RoutingCandidateView 属临时适配；正式 Contract 仍归 2号位。
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class PhaseTwoRoutingCandidateTests
{
    private static readonly DateTime PlanningStart = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new DateTime(2026, 10, 31, 0, 0, 0);

    private const int MaterialId = 1;
    private const int DeptId = 100;

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>一条候选路径的描述：路由身份 + 承载资源 + 该资源日历窗（null = 无日历 ⇒ 不可行）。</summary>
    private readonly record struct PathSpec(
        string RouteCode, int PathId, int ResourceId, DateTime? CalStart, DateTime? CalEnd);

    /// <summary>
    /// 造一条两工序路径（OP10@STAGE1 → OP20@STAGE2，各 60 分钟），资源与日历按 <paramref name="paths"/> 铺。
    /// </summary>
    private static DomainSolveRequest Build(
        IReadOnlyList<PathSpec> paths,
        string? demandRouteCode = null,
        int? demandPathId = null,
        string direction = "FORWARD",
        ExecutionConstraint? locked = null)
    {
        var ops = new List<RoutingOperation>();
        var deps = new List<RoutingDependency>();
        var elig = new List<OperationResourceEligibility>();
        var resources = new List<ResourceDefinition>();
        var calendars = new List<ResourceCalendarSlot>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var p in paths)
        {
            foreach (var (code, stage) in new[] { ("OP10", "STAGE1"), ("OP20", "STAGE2") })
            {
                ops.Add(new RoutingOperation
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode,
                    PathId = p.PathId,
                    OperationCode = code,
                    StageCode = stage,
                    StandardDuration = 60m,
                    OperationPlanningMode = "FINITE_RESOURCE"
                });

                elig.Add(new OperationResourceEligibility
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode,
                    PathId = p.PathId,
                    OperationCode = code,
                    ResourceId = p.ResourceId,
                    Priority = 1,
                    CapacityFactor = 1m
                });
            }

            // 同 Path 内 OP10 → OP20
            deps.Add(new RoutingDependency
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = p.RouteCode,
                PathId = p.PathId,
                FromOperationCode = "OP10",
                ToOperationCode = "OP20"
            });

            resources.Add(new ResourceDefinition
            {
                ResourceId = p.ResourceId, ResourceCode = $"R{p.ResourceId}",
                FactoryCode = "F1", Capacity = 1m
            });

            // 日历缺省 ⇒ 该路径不可行（不造虚拟产能，0号位 §11.2）
            if (p.CalStart is DateTime cs && p.CalEnd is DateTime ce)
            {
                calendars.Add(new ResourceCalendarSlot
                {
                    ResourceId = p.ResourceId, Start = cs, End = ce, IsAvailable = true
                });
            }
        }

        foreach (var stage in new[] { "STAGE1", "STAGE2" })
        {
            stageDepts.Add(new MaterialStageDepartmentContextDto
            {
                MaterialId = MaterialId, StageCode = stage, ProductionDepartmentId = DeptId
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = MaterialId, FactoryId = 1,
                    NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = PlanningStart.AddDays(20), DemandSequence = 1,
                    RouteCode = demandRouteCode, PathId = demandPathId
                }
            },
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = locked is null
                ? Array.Empty<ExecutionConstraint>()
                : new[] { locked },
            Resources = resources,
            CalendarSlots = calendars,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = direction }
            }
        };
    }

    /// <summary>
    /// ① 单路径零回归：既有单路径物料行为**完全不变**，且路径身份/归批键正确落到 Task。
    ///    （C桶单候选 = A/B 固定路径 = 既有行为，三者共用同一段代码 ⇒ 本用例是回归基线。）
    /// </summary>
    [Fact]
    public async Task 单路径_零回归_路径身份与归批键落到Task()
    {
        var result = await _solver.SolveAsync(Build(new[]
        {
            new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd)
        }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        var tasks = result.FinalTasks.OrderBy(t => t.StageCode, StringComparer.Ordinal).ToList();
        Assert.Equal(2, tasks.Count);

        // v1.6 §1：真实 RouteCode/PathId（不得是 DEFAULT/1 归一化残值）
        Assert.All(tasks, t => Assert.Equal("RTA", t.RouteCode));
        Assert.All(tasks, t => Assert.Equal(1, t.PathId));

        // v1.6 `:30`：同一 Execution Batch 下多 Operation FinalTask 共 Key
        var batchKey = tasks[0].ExecutionBatchDraftKey;
        Assert.False(string.IsNullOrEmpty(batchKey));
        Assert.All(tasks, t => Assert.Equal(batchKey, t.ExecutionBatchDraftKey));

        // OP10 → OP20 串行（路径图依赖仍生效）
        var op10 = tasks.Single(t => t.OperationCode == "OP10");
        var op20 = tasks.Single(t => t.OperationCode == "OP20");
        Assert.True(op20.PlannedStartTime >= op10.PlannedEndTime);
    }

    /// <summary>
    /// ② 两条竞争路径、均可行 ⇒ 按 Q-1 统一四层目标（FORWARD ⇒ 更早可行完成优先）选出更早那条。
    ///    RTA 的资源日历从 D+10 才开 ⇒ RTB（D+0 开工）完成更早 ⇒ 应选 RTB。
    /// </summary>
    [Fact]
    public async Task 双路径_均可行_按Direction选更早完成者()
    {
        var result = await _solver.SolveAsync(Build(new[]
        {
            new PathSpec("RTA", 1, 1, PlanningStart.AddDays(10), PlanningEnd),
            new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
        }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        // 全批只允许一条 Path（Q-2：每个 Execution Batch 一条完整 Path）
        Assert.All(result.FinalTasks, t => Assert.Equal("RTB", t.RouteCode));
        Assert.All(result.FinalTasks, t => Assert.Equal(2, t.ResourceId));
    }

    /// <summary>
    /// ③ 两条路径**均不可行**（资源无日历）⇒ 需求 Unscheduled，不产 Task。
    ///    禁止「候选内挑不出就硬塞一条」。
    /// </summary>
    [Fact]
    public async Task 双路径_全不可行_需求Unscheduled()
    {
        var result = await _solver.SolveAsync(Build(new[]
        {
            new PathSpec("RTA", 1, 1, null, null),
            new PathSpec("RTB", 1, 2, null, null)
        }));

        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    /// <summary>
    /// ④ Calendar 判定**只对选中 Path** 生效：RTA 无日历（不可行）但 RTB 可行
    ///    ⇒ 需求经 RTB 排下，**不得**因未选中的 RTA 覆盖不足而误判 `CALENDAR_COVERAGE_INSUFFICIENT`。
    ///    （0号位 §11.3：Calendar 覆盖判定须绑定实际选用的路径，不得对旁路候选误伤。）
    /// </summary>
    [Fact]
    public async Task 双路径_仅一条日历不覆盖_选可行者且不误报日历不足()
    {
        var result = await _solver.SolveAsync(Build(new[]
        {
            new PathSpec("RTA", 1, 1, null, null),
            new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
        }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.All(result.FinalTasks, t => Assert.Equal("RTB", t.RouteCode));
        Assert.DoesNotContain(result.UnscheduledTasks,
            u => u.Reason == "CALENDAR_COVERAGE_INSUFFICIENT");
    }

    /// <summary>
    /// ⑤ 任务级不串 Path：两条路径**含同名工序码**（OP10/OP20 在 RTA、RTB 各一份）⇒
    ///    选中路径的每个 Task 的 (RouteCode, PathId) 必须自洽，且**不得**混入另一 Path 的节点/资源。
    ///    （Q-3 + 0号位 §5.3：节点身份与资格键都须带 Path 维度。）
    /// </summary>
    [Fact]
    public async Task 双路径_同名工序码_不串Path()
    {
        var result = await _solver.SolveAsync(Build(new[]
        {
            new PathSpec("RTA", 1, 1, PlanningStart.AddDays(10), PlanningEnd),
            new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
        }));

        Assert.True(result.Success, result.ErrorMessage);

        // 选中路径唯一
        var identities = result.FinalTasks
            .Select(t => (t.RouteCode, t.PathId))
            .Distinct()
            .ToList();
        Assert.Single(identities);

        // 选中 RTB ⇒ 资源只能是 RTB 的资源（RTA 的 R1 不得出现）
        Assert.All(result.FinalTasks, t => Assert.Equal(2, t.ResourceId));
        Assert.All(result.FinalTasks, t => Assert.Equal("RTB", t.RouteCode));
        Assert.All(result.FinalTasks, t => Assert.Equal(1, t.PathId));
    }

    /// <summary>
    /// ⑥ Q-3 Fail Closed 红线：需求**声明了固定路径**（A/B 桶语义）而图中不存在该路径
    ///    ⇒ **不排**（Unscheduled），**不得**回退 <c>TryGetSingleRoutingGraph</c> 猜唯一 Path 顶上。
    ///    本用例是「禁回退」的**反证锁**：图里确有 RTA/RTB 可用，若实现回退猜路径，本用例会排下 ⇒ 红。
    /// </summary>
    [Fact]
    public async Task 声明固定路径_图中不存在_FailClosed不排()
    {
        var result = await _solver.SolveAsync(Build(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
            },
            demandRouteCode: "RTX",   // 图中不存在
            demandPathId: 1));

        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    /// <summary>
    /// ⑦ Q-3 Fail Closed（**锁定任务**分支）：多路径物料 + 锁定任务**未带固定路径**
    ///    ⇒ 身份反查解不出真实 (RouteCode, PathId) ⇒ **不产伪身份 FinalTask**。
    ///    依据：0号位 2026-10-07 裁决 Q-3 `:186`「Route/Path 缺失时应 Fail Closed 或进入明确异常，
    ///    不得跨 Path 寻找替代节点」；同件职责表 `:357`「A/B 固定 Route/Path … **缺值 Fail Closed**」。
    ///    ⚠ 本用例是整改的**反证锁**：整改前该分支会带着 `RouteCode=null / PathId=null` 产出 FinalTask ⇒ 红。
    /// </summary>
    [Fact]
    public async Task 锁定任务_多路径且无固定路径_不产伪身份Task()
    {
        var locked = new ExecutionConstraint
        {
            DraftId = "D1",
            ResourceId = 1,
            LockedStart = PlanningStart,
            LockedEnd = PlanningStart.AddHours(1),
            ConstraintType = "MANUAL",
            StageCode = "STAGE1",
            OperationCode = "OP10",
            LockedQuantity = 0.5m
        };

        var result = await _solver.SolveAsync(Build(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
            },
            locked: locked));

        // 核心断言：任何 FinalTask 都必须带**真实**路径身份（不得出现 null / 空串）
        Assert.DoesNotContain(result.FinalTasks,
            t => string.IsNullOrEmpty(t.RouteCode) || t.PathId is null);

        // 且身份必须落在**候选路径内**（不得跨 Path 编造替代节点）
        Assert.All(result.FinalTasks, t =>
        {
            Assert.True(t.RouteCode is "RTA" or "RTB", $"非候选路径身份：{t.RouteCode}");
            Assert.Equal(1, t.PathId);
        });
    }
}
