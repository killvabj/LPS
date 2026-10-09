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
        ExecutionConstraint? locked = null,
        bool isContinuation = false,
        string? continuationKey = null,
        DateTime? due = null,
        string? preferredResourceCode = null)
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
                    // `due` 可覆写：B-005 Direction 自决的 E2E 用例靠「只改交期」驱动 Slack 变化。
                    RequiredAvailableTime = due ?? PlanningStart.AddDays(20), DemandSequence = 1,
                    RouteCode = demandRouteCode, PathId = demandPathId,
                    // B-005「Resource」上下文载体（P1-11 软偏好；仅进次级，不 Hard Lock）。
                    PreferredResourceCode = preferredResourceCode,
                    // 0号位 2026-10-07 (5).md P0-05：连续份额输入完整性 Fail Closed 的反证锁用。
                    IsContinuation = isContinuation,
                    ContinuationKey = continuationKey,
                    NoSplitMerge = isContinuation
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
                Parameters = new FiniteCapacityParameters { SchedulingDirection = direction },
                // P0-01（0号位 2026-10-08 §四）：C 桶必须显式给出有效 Batch Policy，否则 Fail Closed。
                //   本夹具验证的是 Routing 候选择优，非批决策 ⇒ Material 级宽松策略（恒 1 批）。
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId)
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

        // ── P0-01（0号位 2026-10-09 第三轮复审 §二）：「不得假成功」的**回归护栏** ──
        //   锁定任务身份不可恢复属**输入完整性问题**，绝不能被当成「普通业务排不下」（后者 Success 仍为 true）。
        //   ⚠ 本夹具（**部分**锁定 0.5）里 Phase5 硬校验会先一步拦下（「需求 D1 未排定却产出超额 FinalTask」）
        //     ⇒ 整改前后 Success 均为 false ⇒ 本断言**不是**该整改的鉴别器，只是护栏。
        //   鉴别器见 ⑦-a / ⑦-b：那两处 Phase5 校验**通过**，唯一使其 Success=false 的就是本整改。
        Assert.False(result.Success, "锁定Task身份不可恢复必须使 Success=false（不得假成功）");
    }

    /// <summary>
    /// ⑦-a P0-01 **反证**（复审 §二 点名：「应补 `LockedTask + 多Path + 路径缺失` 反证，并验证没有假成功」）：
    ///   多路径物料 + 锁定任务无固定路径 ⇒ 身份不可恢复；且锁定量 = 需求全量 ⇒ 需求**全量锁定**、
    ///   不再排剩余份额 ⇒ 最终集合里**没有任何 Task**，Phase5 的「锁定锚点时间」与「未排定却超额」两条
    ///   校验都**通过**。
    ///   ⇒ 此时**唯一**能让 `Success=false` 的就是本整改写入的 `TechnicalFailure`；
    ///     ⚠ 整改前该输入会得到「**无锁定 Task、却 Success=true**」的假成功 ⇒ 本用例**红**。
    /// </summary>
    [Fact]
    public async Task 锁定任务_多路径无固定路径_全量锁定_身份不可恢复_不假成功()
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
            LockedQuantity = 1m        // = 需求 NetOutputQty ⇒ 全量锁定 ⇒ 不排剩余份额
        };

        var result = await _solver.SolveAsync(Build(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
            },
            locked: locked));

        Assert.Empty(result.FinalTasks);
        Assert.False(result.Success, "有锁定事实却身份不可恢复，必须 Success=false（不得假成功）");
        Assert.Contains("锁定执行Task身份不可恢复", result.ErrorMessage ?? string.Empty);
    }

    /// <summary>
    /// ⑦-b P0-01 **反证**（**节点缺失**变体）：单路径物料（图可解）但锁定任务的
    ///   (StageCode, OperationCode) 在图中不存在 ⇒ 身份不可恢复；锁定量 = 需求全量 ⇒ 无 Task
    ///   ⇒ Phase5 校验通过 ⇒ 唯一使 Success=false 的是本整改。
    ///   本用例把「图缺失」与「节点缺失」两条路径分开锁死（复审 §二 点名三种情形）。
    /// </summary>
    [Fact]
    public async Task 锁定任务_节点在图中缺失_技术失败不假成功()
    {
        var locked = new ExecutionConstraint
        {
            DraftId = "D1",
            ResourceId = 1,
            LockedStart = PlanningStart,
            LockedEnd = PlanningStart.AddHours(1),
            ConstraintType = "MANUAL",
            StageCode = "STAGE9",        // 图中不存在
            OperationCode = "OP90",      // 图中不存在
            LockedQuantity = 1m          // 全量锁定 ⇒ 不排剩余份额
        };

        var result = await _solver.SolveAsync(Build(
            new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) },
            locked: locked));

        Assert.False(result.Success, "锁定Task节点缺失必须使 Success=false（不得假成功）");
        Assert.Contains("锁定执行Task身份不可恢复", result.ErrorMessage ?? string.Empty);
        Assert.Contains("图中无该", result.ErrorMessage ?? string.Empty);
    }

    /// <summary>
    /// ⑦-c P0-01 反证（**对应需求缺失**变体）：锁定任务引用的 DraftId 在本次请求里
    ///   没有对应需求 ⇒ 身份不可恢复 ⇒ 同样必须 Success=false。
    ///   （其余需求仍正常排程 ⇒ 本用例同时锁死「技术失败 ≠ 整域无产出」的既有载体语义。）
    /// </summary>
    [Fact]
    public async Task 锁定任务_对应需求缺失_技术失败不假成功()
    {
        var locked = new ExecutionConstraint
        {
            DraftId = "D_MISSING",   // 请求中不存在该需求
            ResourceId = 1,
            LockedStart = PlanningStart,
            LockedEnd = PlanningStart.AddHours(1),
            ConstraintType = "MANUAL",
            StageCode = "STAGE1",
            OperationCode = "OP10",
            LockedQuantity = 0.5m
        };

        var result = await _solver.SolveAsync(Build(
            new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) },
            locked: locked));

        Assert.False(result.Success, "锁定Task对应需求缺失必须使 Success=false（不得假成功）");
        Assert.Contains("锁定执行Task身份不可恢复", result.ErrorMessage ?? string.Empty);
        Assert.Contains("(对应需求缺失)", result.ErrorMessage ?? string.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ⑧ Direction `AUTO` **正式按上下文自决**（0号位 2026-10-08《未命名的Markdown文件 (2)(1).md》
    //    §三 **P1-DIR-01** / §十四 第二优先级 / §十五 第 3 项）
    //
    // 整改要点：**撤销**「`AUTO` 与 `MIXED` 行为等价」（0号位 2026-10-07 Q-1 的旧口径已被明判
    //   「不能再作为最终 V1 实现」）。`AUTO` 现由 `SchedulingDirectionResolver` 按 Demand / Execution Batch
    //   **自身上下文**（RequiredAvailableTime/Slack、Material、Resource、Execution、Firm-Frozen-Lock；
    //   DemandGoal 载体缺失则登记缺口）正式裁决为 FORWARD / BACKWARD / MIXED 之一。
    //
    // 断言方式（行为层；**对 Phase5 压缩免疫**）：`AUTO` 的结果必须**逐字段等于**
    //   「把自决方向显式传入」的结果；且同一夹具**只改一处上下文**时结果必须随之变化
    //   ⇒ 证明 AUTO 不是「恒等某一固定方向」的空壳。
    //
    // 覆盖 §十五 第 3 项点名的四类场景：
    //   ① 明显倒排（交期紧）② 明显正排（交期松）③ 连续份额（A/B 既存执行批）④ Slack / 资源 致 Direction 变化。
    //   ⚠ Material（`dynamicMaterialFloor`）为 Scheduling 内部派生量，其 Direction 影响由
    //     `SchedulingDirectionResolverTests` 判据层覆盖；本文件只做行为层 E2E。
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>排程几何签名（确定性字符串）：用于「AUTO 结果 == 显式方向结果」与「上下文变 ⇒ 结果变」的比对。</summary>
    private static string Signature(DomainSolveResult r)
        => string.Join("\n", r.FinalTasks
            .OrderBy(t => t.StageCode, StringComparer.Ordinal)
            .ThenBy(t => t.OperationCode, StringComparer.Ordinal)
            .Select(t => $"{t.StageCode}|{t.OperationCode}|{t.ResourceId}|{t.RouteCode}|{t.PathId}"
                       + $"|{t.PlannedStartTime:O}|{t.PlannedEndTime:O}"));

    private static void AssertSameSchedule(DomainSolveResult expected, DomainSolveResult actual)
    {
        Assert.Equal(expected.FinalTasks.Count, actual.FinalTasks.Count);
        Assert.Equal(Signature(expected), Signature(actual));
    }

    /// <summary>
    /// ⑧-① **明显倒排场景**：两工序各 60min（lead = 120min），交期 = 计划起点 + 3h
    ///   ⇒ Slack = 180 − 120 = 60min，落在工艺周期内 ⇒ 无交期信号、无其它上下文
    ///   ⇒ 确定性默认 **BACKWARD**（`NO_CONTEXT_SIGNAL`，靠交期减少 WIP）。
    ///   行为断言：AUTO 结果 == 显式 BACKWARD 结果。
    /// </summary>
    [Fact]
    public async Task AUTO_明显倒排场景_自决为BACKWARD()
    {
        var paths = new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) };
        var due = PlanningStart.AddHours(3);

        var auto = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: due));
        var backward = await _solver.SolveAsync(Build(paths, direction: "BACKWARD", due: due));

        Assert.True(auto.Success, auto.ErrorMessage);
        AssertSameSchedule(backward, auto);
    }

    /// <summary>
    /// ⑧-② **明显正排场景**：同一夹具，只把交期推到 20 天后
    ///   ⇒ Slack = 20d − 120min ≫ lead ⇒ `DUE_LOOSE`（最早承诺）⇒ **FORWARD**。
    /// </summary>
    [Fact]
    public async Task AUTO_明显正排场景_自决为FORWARD()
    {
        var paths = new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) };
        var due = PlanningStart.AddDays(20);

        var auto = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: due));
        var forward = await _solver.SolveAsync(Build(paths, direction: "FORWARD", due: due));

        Assert.True(auto.Success, auto.ErrorMessage);
        AssertSameSchedule(forward, auto);
    }

    /// <summary>
    /// ⑧-③ **连续份额场景**：`IsContinuation = true`（A/B 既存执行批，已经开始）
    ///   ⇒ `CONTINUATION_SLICE` 正向信号（最早承诺）⇒ **FORWARD**（交期取中性值以免掩盖该信号）。
    ///   反向对照：同夹具去掉连续性 ⇒ 退回 ⑧-① 的 BACKWARD ⇒ 证明 Execution 上下文**真的**参与裁决。
    /// </summary>
    [Fact]
    public async Task AUTO_连续份额场景_自决为FORWARD()
    {
        var paths = new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) };
        var due = PlanningStart.AddHours(3);

        var auto = await _solver.SolveAsync(Build(paths, direction: "AUTO",
            demandRouteCode: "RTA", demandPathId: 1, isContinuation: true, due: due));
        var forward = await _solver.SolveAsync(Build(paths, direction: "FORWARD",
            demandRouteCode: "RTA", demandPathId: 1, isContinuation: true, due: due));

        Assert.True(auto.Success, auto.ErrorMessage);
        AssertSameSchedule(forward, auto);

        // 反向对照：同一交期下，仅去掉连续性 ⇒ 方向退回 BACKWARD（= ⑧-①）
        var notContinuation = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: due));
        AssertSameSchedule(
            await _solver.SolveAsync(Build(paths, direction: "BACKWARD", due: due)), notContinuation);
        Assert.NotEqual(Signature(auto), Signature(notContinuation));
    }

    /// <summary>
    /// ⑧-④ **Slack / Resource 导致 Direction 变化场景**：
    ///   · Slack：同一夹具只改交期（3h ↔ 20d）⇒ 方向 BACKWARD ↔ FORWARD，结果必须变；
    ///   · Resource：交期取中性（3h）时，仅加 `PreferredResourceCode` ⇒ `PREFERRED_RESOURCE` 正向信号
    ///     ⇒ 方向由 BACKWARD 翻为 FORWARD。
    /// </summary>
    [Fact]
    public async Task AUTO_仅改Slack或Resource_自决方向随之变化()
    {
        var paths = new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) };
        var tightDue = PlanningStart.AddHours(3);

        // ── Slack 变化 ──
        var tight = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: tightDue));
        var loose = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: PlanningStart.AddDays(20)));

        Assert.True(tight.Success, tight.ErrorMessage);
        Assert.True(loose.Success, loose.ErrorMessage);
        AssertSameSchedule(
            await _solver.SolveAsync(Build(paths, direction: "BACKWARD", due: tightDue)), tight);
        AssertSameSchedule(
            await _solver.SolveAsync(Build(paths, direction: "FORWARD", due: PlanningStart.AddDays(20))), loose);
        Assert.NotEqual(Signature(tight), Signature(loose));

        // ── Resource 偏好变化（交期不变）──
        var withPref = await _solver.SolveAsync(Build(paths, direction: "AUTO",
            due: tightDue, preferredResourceCode: "R1"));

        Assert.True(withPref.Success, withPref.ErrorMessage);
        AssertSameSchedule(
            await _solver.SolveAsync(Build(paths, direction: "FORWARD",
                due: tightDue, preferredResourceCode: "R1")), withPref);
        Assert.NotEqual(Signature(tight), Signature(withPref));
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ⑨ P1-01 反证（0号位 2026-10-09 第三轮复审 §二）：**AUTO 方向与批/路由候选评分口径一致**
    //
    // 复审判词（源码证实）：`AUTO` 原先只在 `ScheduleDemandOperations`（排程执行时）本地解析，
    //   而 `SelectBestRoutingCandidate` / `CompareBatchPlans` 拿到的是**原始策略值**（仍为 `AUTO`）
    //   ⇒ 第③层「均按期时的交期目标」在 AUTO 下走「更早完成优先」，与同批真实 ResolvedDirection
    //     （BACKWARD ⇒「更晚但不延期」）**不一致**。
    // 整改后：AUTO 在**进入择优之前一次性**解析，**同一 ResolvedDirection** 贯穿试排/比较/落定。
    //
    // ── 夹具几何的**硬约束**（源码推导，决定反证怎么造才真有鉴别力）──
    //   `ScheduleBackward` 把末工序锚在 `EffectiveDue`：`candidateEnd = currentEndTime + overlapExtension`
    //   ⇒ **倒排下每条可行候选的完成时间恒等于交期**；而 `FindBackwardSlot` 不做滑动（查不到槽即该资源不可行）。
    //   ⇒ 「双 Route 均按期但完成时间不同」在**纯倒排的非合批候选之间不可实现**，
    //     第③层会被跳过（完成时间相等）⇒ 只能靠**合批**（Merge 把目标 Task 的末端后延）造出完成时间差。
    //   故本反证用「一个**锁定锚点 Task**（方向无关、两次运行逐字相同）+ 一条可合批的候选（早完）
    //   + 一条不可合批的候选（晚完且恰按期）」把第③层真正逼出来。
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⑨-① **双 Route（一条经合批提前完成、一条恰在交期完成，且**都按期**）** ⇒ AUTO 自决为 BACKWARD
    ///   ⇒ 候选择优必须与**显式 BACKWARD 逐字段一致**（都取「更晚但不延期」那条 = RTB）。
    ///
    /// 夹具几何（`lead` = 1×60min = 60min）：
    ///   · `D0`：**锁定锚点**（`ExecutionConstraint` 固定 `RTA/1`、锁定量 0.5 = 需求全量 ⇒ 全量锁定、
    ///     原地继承 `[P, P+30m]`、不参与排程）；交期 `P+90m` 是**合批合法性闸**
    ///     （`TryMergeDemandIntoTask` 要求 `newEndTime ≤ min(本需求交期, 目标已有份额交期)`）。
    ///     锁定锚点**与 Direction 无关** ⇒ AUTO / 显式 BACKWARD / 显式 FORWARD 三次运行的 D0 逐字相同，
    ///     故三者的差异**只能**来自 D1 的候选择优。
    ///   · `D1`：自由候选（RTA/RTB 两条 Path），交期 `P+2h` ⇒ Slack = 120 − 60 = 60（不 > lead）
    ///     ⇒ 无交期信号、无其它上下文 ⇒ `NO_CONTEXT_SIGNAL` ⇒ **BACKWARD**。
    ///     · 候选 **RTA**：合批进 D0 的锚点 Task ⇒ 合并量 0.5+1 = 1.5 ⇒ 新末端 `P+90m`（**早完**，按期）
    ///     · 候选 **RTB**：不可合批（RouteCode 不同）⇒ 倒排 `[P+1h, P+2h]`（**晚完**，恰按期）
    ///   ⇒ 第③层被真正触发：显式 FORWARD 取**更早**（RTA 合批 `P+90m`）、
    ///     显式 BACKWARD 取**更晚**（RTB `P+2h`），二者必然不同（下方 `NotEqual` 即夹具自证的**鉴别力**锁）。
    ///
    /// ⚠ 整改前：AUTO 把原始 `"AUTO"` 传进候选比较 ⇒ 走「更早完成」分支 ⇒ 选中 RTA 合批
    ///   ⇒ 与显式 BACKWARD（RTB）**不一致** ⇒ 本用例 **红**（正是复审点名的第三层目标冲突）。
    /// ⚠ 本用例刻意**不用** `Build`（两工序）夹具：两工序 `operations.Count != 1` 时
    ///   `FindMergeableTasks` 直接返回空 ⇒ 合批永不发生 ⇒ 造不出完成时间差 ⇒ **假反证**。
    /// </summary>
    [Fact]
    public async Task AUTO_双Route一条经合批提前完成_自决BACKWARD须与显式BACKWARD一致()
    {
        var paths = new[]
        {
            new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
            new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
        };
        var demands = new[]
        {
            // D0：锁定锚点（固定 RTA/1 ⇒ 身份可解，不触发 P0-01 技术失败）；交期 P+90m = 合批合法性闸上界。
            new DemandSpec2("D0", 1, 0.5m, "RTA", 1, Due: PlanningStart.AddMinutes(90)),
            // D1：自由候选；交期 P+2h ⇒ Slack = 60（不 > lead = 60）⇒ 无交期信号 ⇒ 自决 BACKWARD。
            new DemandSpec2("D1", 2, 1m, null, null, Due: PlanningStart.AddMinutes(120))
        };
        var locked = new ExecutionConstraint
        {
            DraftId = "D0",
            ResourceId = 1,
            LockedStart = PlanningStart,
            LockedEnd = PlanningStart.AddMinutes(30),
            ConstraintType = "MANUAL",
            StageCode = "STAGE1",
            OperationCode = "OP10",
            LockedQuantity = 0.5m          // = D0 需求全量 ⇒ 全量锁定 ⇒ 不排剩余份额
        };

        var lockedSet = new[] { locked };
        var auto = await _solver.SolveAsync(BuildSingleOp(paths, demands, direction: "AUTO", locked: lockedSet));
        var backward = await _solver.SolveAsync(BuildSingleOp(paths, demands, direction: "BACKWARD", locked: lockedSet));
        var forward = await _solver.SolveAsync(BuildSingleOp(paths, demands, direction: "FORWARD", locked: lockedSet));

        Assert.True(auto.Success, auto.ErrorMessage);
        Assert.True(backward.Success, backward.ErrorMessage);
        Assert.True(forward.Success, forward.ErrorMessage);

        // 锁定锚点必须原地保留（方向无关 ⇒ 三次运行同一事实，反证的「唯一变量」是 D1 的择优）。
        var d0 = auto.FinalTasks.Single(t => t.SourceDraftId == "D0");
        Assert.Equal("RTA", d0.RouteCode);
        Assert.Equal(PlanningStart, d0.PlannedStartTime);
        Assert.Equal(PlanningStart.AddMinutes(30), d0.PlannedEndTime);

        // 夹具自证「可判别」：两个显式方向必须选出**不同**的 D1 落点，否则本用例没有鉴别力。
        Assert.NotEqual(Signature(forward), Signature(backward));
        Assert.Equal("RTB", backward.FinalTasks.Single(t => t.SourceDraftId == "D1").RouteCode);

        // 反证核心：AUTO 自决为 BACKWARD ⇒ 候选择优必须与显式 BACKWARD **逐字段一致**。
        //   整改前 AUTO 走「更早完成」分支 ⇒ 选中 RTA（合批进 D0 锚点、D1 无自有 Task）⇒ 本断言红。
        AssertSameSchedule(backward, auto);

        // AUTO 不得退化成「恒等 FORWARD」（显式 FORWARD 取更早完成的 RTA 合批）。
        Assert.NotEqual(Signature(forward), Signature(auto));
    }

    /// <summary>
    /// ⑩ P1-02 反证（复审 §二）：**DemandGoal 缺失的缺口必须真正进入生产诊断出口**。
    ///   复审判词：「`DEMAND_GOAL_ABSENT` 只加到 `Decision.Signals`，未写入 `SolveTraceNotes`」
    ///   ⇒ 开发报告「生产路径恒记缺口」的说法只对内部对象成立，**不等于**用户/日志可见证据。
    ///   整改：AUTO 自决时若缺 DemandGoal 载体 ⇒ 经**既有合法追溯通道** `DomainSolveResult.SolveTraceNotes`
    ///   写出一条 `ReasonCode = DEMAND_GOAL_ABSENT` 的记录（**未自造** ReasonCode 枚举）。
    ///
    /// 反向对照：**显式**方向（BACKWARD）不经过 B-005 自决 ⇒ **不得**产出该 trace
    ///   （锁死「不是无条件乱写 trace」）。
    /// </summary>
    [Fact]
    public async Task AUTO解析_DemandGoal缺口经SolveTraceNotes出口()
    {
        var paths = new[] { new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd) };
        var due = PlanningStart.AddHours(3);

        var auto = await _solver.SolveAsync(Build(paths, direction: "AUTO", due: due));
        Assert.True(auto.Success, auto.ErrorMessage);

        // 出口断言：trace 载体（`SolveTraceNotes`）里必须能看到缺口
        Assert.Contains(auto.SolveTraceNotes, n => n.ReasonCode == "DEMAND_GOAL_ABSENT");
        // 追溯键归属该需求（可定位，不是匿名声）
        Assert.Contains(auto.SolveTraceNotes,
            n => n.ReasonCode == "DEMAND_GOAL_ABSENT" && n.Key == "D1");

        // 反向对照：显式方向不做 B-005 自决 ⇒ 无此 trace
        var explicitRun = await _solver.SolveAsync(Build(paths, direction: "BACKWARD", due: due));
        Assert.True(explicitRun.Success, explicitRun.ErrorMessage);
        Assert.DoesNotContain(explicitRun.SolveTraceNotes, n => n.ReasonCode == "DEMAND_GOAL_ABSENT");
    }

    // ─────────── 0号位 2026-10-07 (5).md 反证单测辅助 + ③④⑤ ───────────

    private readonly record struct DemandSpec2(
        string Key, int Seq, decimal Qty, string? RouteCode, int? PathId, bool IsContinuation = false,
        DateTime? Due = null);

    /// <summary>
    /// 单工序 Path 夹具（Merge 类反证专用）：每条 Path 只含一道 OP10@STAGE1，独立资源 + 日历。
    /// Merge 仅在 <c>operations.Count == 1</c> 时才会被尝试（<c>FindMergeableTasks</c> 守卫）
    /// ⇒ 反证「跨 Path 禁 Merge」「Merge 真实完成」必须用**单工序**路径。
    /// </summary>
    private static DomainSolveRequest BuildSingleOp(
        IReadOnlyList<PathSpec> paths,
        IReadOnlyList<DemandSpec2> demands,
        bool allowMerge = true,
        string direction = "FORWARD",
        IReadOnlyList<ExecutionConstraint>? locked = null)
    {
        var ops = new List<RoutingOperation>();
        var elig = new List<OperationResourceEligibility>();
        var resources = new List<ResourceDefinition>();
        var calendars = new List<ResourceCalendarSlot>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var p in paths)
        {
            ops.Add(new RoutingOperation
            {
                MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                RouteCode = p.RouteCode, PathId = p.PathId,
                OperationCode = "OP10", StageCode = "STAGE1",
                StandardDuration = 60m, OperationPlanningMode = "FINITE_RESOURCE"
            });
            elig.Add(new OperationResourceEligibility
            {
                MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                RouteCode = p.RouteCode, PathId = p.PathId,
                OperationCode = "OP10", ResourceId = p.ResourceId, Priority = 1, CapacityFactor = 1m
            });
            resources.Add(new ResourceDefinition
            {
                ResourceId = p.ResourceId, ResourceCode = $"R{p.ResourceId}", FactoryCode = "F1", Capacity = 1m
            });
            if (p.CalStart is DateTime cs && p.CalEnd is DateTime ce)
            {
                calendars.Add(new ResourceCalendarSlot
                {
                    ResourceId = p.ResourceId, Start = cs, End = ce, IsAvailable = true
                });
            }
        }

        stageDepts.Add(new MaterialStageDepartmentContextDto
        {
            MaterialId = MaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId
        });

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key, PlanVersionId = 1L, DomainKey = "DOMAIN",
                AllocationSequence = d.Seq, DemandKey = d.Key, MaterialId = MaterialId, FactoryId = 1,
                NetOutputQty = d.Qty, PlannedProcessQty = d.Qty,
                RequiredAvailableTime = d.Due ?? PlanningStart.AddDays(20), DemandSequence = d.Seq,
                RouteCode = d.RouteCode, PathId = d.PathId,
                IsContinuation = d.IsContinuation, NoSplitMerge = d.IsContinuation
            }).ToList(),
            RoutingOperations = ops,
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = locked is null ? Array.Empty<ExecutionConstraint>() : locked,
            Resources = resources,
            CalendarSlots = calendars,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = direction,
                    AllowMerge = allowMerge
                },
                // P0-01（0号位 2026-10-08 §四）：C 桶必须显式给出有效 Batch Policy，否则 Fail Closed。
                //   `AllowMerge` 镜像夹具参数：策略是 Merge 的正式控制源（§八 P1-01）。
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, allowMerge)
            }
        };
    }

    /// <summary>
    /// ③ P0-05 反证锁：`IsContinuation=true` 但 **RouteCode / PathId / ContinuationKey / StartOperationCode
    ///    全缺** ⇒ 必须 Fail Closed（Unscheduled），**禁止**退化成「无固定路径 ⇒ 自由候选选路」。
    ///    图中**确有两条可行路径**：若实现按旧口径（`RouteCode 非空 || PathId 非空` 才固定路径）就会排下
    ///    ⇒ 本用例红。这正是 0号位 判的「把 A/B 当 C 桶选路」。
    /// </summary>
    [Fact]
    public async Task 连续份额_缺固定路径身份_FailClosed不排()
    {
        var result = await _solver.SolveAsync(Build(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
            },
            isContinuation: true));

        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    /// <summary>
    /// ④ P0-06 反证锁：两条 Path **含同名工序码**（OP10 在 RTA、RTB 各一份）时，
    ///    不得把 Path A 的既有 Task 当成 Path B 需求的合并目标（否则 target 的 RouteCode/PathId/
    ///    ExecutionBatchDraftKey 被保留 ⇒ 同一 Execution Batch 混入两条 Path 的节点）。
    ///
    /// ⚠ 构造要点：两条 Path **必须共用同一资源**。若资源不同，对被合并 Task 的 ResourceId 取
    ///    CapacityFactor 会因资格键带 (RouteCode, PathId) 而查不到 ⇒ Merge 本就被拒，
    ///    这样即使删掉跨 Path 检查用例也依然是绿的（**假反证**）。共用资源才真正把
    ///    「唯一拦截点 = RouteCode/PathId 比较」暴露出来。
    ///
    ///     D0 钉 RTA 先排；D1 钉 RTB ⇒ 必须各自成 Task、批键不同。
    /// </summary>
    [Fact]
    public async Task 两条Path同名工序_禁止PathA合并进PathB的Task()
    {
        var result = await _solver.SolveAsync(BuildSingleOp(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 1, PlanningStart, PlanningEnd)   // 同资源、同 PathId，仅 RouteCode 不同
            },
            new[]
            {
                new DemandSpec2("D0", 1, 1m, "RTA", 1),
                new DemandSpec2("D1", 2, 1m, "RTB", 1)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FinalTasks.Count);

        var d0 = result.FinalTasks.Single(t => t.SourceDraftId == "D0");
        var d1 = result.FinalTasks.Single(t => t.SourceDraftId == "D1");
        Assert.Equal("RTA", d0.RouteCode);
        Assert.Equal("RTB", d1.RouteCode);
        Assert.NotEqual(d0.ExecutionBatchDraftKey, d1.ExecutionBatchDraftKey);
    }

    /// <summary>
    /// ⑤ P0-04 反证锁：Merge 候选必须用**合并后的真实 PlannedEndTime**参与择优。
    ///    D0 钉 RTA（qty1）先排；D1 自由（2 候选、qty1、AllowMerge=true）：
    ///      · 走 RTA：Merge 进 D0 的 Task ⇒ 合并后 end = t0 + 2×60min；
    ///      · 走 RTB：新建 Task ⇒ end = t0 + 1×60min。
    ///    FORWARD 取更早完成 ⇒ **必须选 RTB**。旧实现对 Merge 候选给 `DateTime.MinValue`
    ///    ⇒ 被误评为「0 延期、完成最早」而永远胜出 ⇒ 本用例在旧实现下红。
    /// </summary>
    [Fact]
    public async Task 合并候选真实完成更晚_必须选另一条Path()
    {
        var result = await _solver.SolveAsync(BuildSingleOp(
            new[]
            {
                new PathSpec("RTA", 1, 1, PlanningStart, PlanningEnd),
                new PathSpec("RTB", 1, 2, PlanningStart, PlanningEnd)
            },
            new[]
            {
                new DemandSpec2("D0", 1, 1m, "RTA", 1),
                new DemandSpec2("D1", 2, 1m, null, null)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FinalTasks.Count);

        var d1 = result.FinalTasks.Single(t => t.SourceDraftId == "D1");
        Assert.Equal("RTB", d1.RouteCode);
        Assert.Equal(1m, d1.Quantity);   // 未被合并（若被 Merge 进 D0，则 D1 无自有 Task 且数量为 2）
    }
}
