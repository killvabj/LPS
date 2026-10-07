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
/// 执行批（Execution Batch）形成与批键域的反证单测 —— 0号位 2026-10-07《未命名的Markdown文件 (5).md》
/// P0-01 / P0-02 / P0-03 落码验收。
///
/// 0号位 要求的链：
///   `Free Slice → Batch Policy → 1..N ExecutionBatchDraft(BatchDraftKey, Qty)
///    → 每个 Batch 分别 Direction + RoutingCandidate + Resource + Calendar + Setup 联合求解
///    → 每个 Batch 选一条完整 Path → 同 Batch 全部 Operation 共 BatchDraftKey → 不同 Batch 必不同 Key`
///
/// 0号位 指定的反证单测（本文件逐条落地，**不静默省略、不降级**）：
///   · **②**（一个 Demand 拆 2 批、即使同 Route/Path 也必须不同 Key）⇒ 见 <see cref="批键域_同Route同Path拆两批_键仍不同"/>。
///   · **①**（一个 C Demand 拆 2 批 ⇒ 每批各自一条完整多工序 Path）⇒ **生产路径当前不可达**，
///     原因与不可达锁见 <see cref="生产路径_批策略载体恒空_EBD02阻塞锁"/> 的方法文档（**登记为 EBD-02，未销账**）。
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class ExecutionBatchDraftTests
{
    private static readonly DateTime PlanningStart = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new DateTime(2026, 10, 31, 0, 0, 0);

    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const string Route = "RTA";
    private const int Path = 1;

    private readonly FiniteCapacitySolver _solver = new();

    // ═══════════════════════════════════════════════════════════════════
    //  夹具
    // ═══════════════════════════════════════════════════════════════════

    private static LogicalProductionDemand Demand(
        string key = "D1",
        decimal netQty = 10m,
        decimal? procQty = null,
        string? routeCode = null,
        int? pathId = null,
        bool isContinuation = false,
        bool noSplitMerge = false)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            NetOutputQty = netQty,
            PlannedProcessQty = procQty ?? netQty,
            RequiredAvailableTime = PlanningStart.AddDays(20),
            DemandSequence = 1,
            RouteCode = routeCode,
            PathId = pathId,
            IsContinuation = isContinuation,
            NoSplitMerge = noSplitMerge
        };

    /// <summary>一条两工序路径（OP10@STAGE1 → OP20@STAGE2，各 60 分钟），资源 1 + 全窗日历。</summary>
    private static DomainSolveRequest BuildRequest(
        string logicalDemandKey = "D1",
        decimal qty = 1m,
        string? demandRouteCode = null,
        int? demandPathId = null)
    {
        var ops = new List<RoutingOperation>();
        var deps = new List<RoutingDependency>();
        var elig = new List<OperationResourceEligibility>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var (code, stage) in new[] { ("OP10", "STAGE1"), ("OP20", "STAGE2") })
        {
            ops.Add(new RoutingOperation
            {
                MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                RouteCode = Route, PathId = Path,
                OperationCode = code, StageCode = stage,
                StandardDuration = 60m, OperationPlanningMode = "FINITE_RESOURCE"
            });
            elig.Add(new OperationResourceEligibility
            {
                MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                RouteCode = Route, PathId = Path,
                OperationCode = code, ResourceId = 1, Priority = 1, CapacityFactor = 1m
            });
        }

        deps.Add(new RoutingDependency
        {
            MaterialId = MaterialId, ProductionDepartmentId = DeptId,
            RouteCode = Route, PathId = Path,
            FromOperationCode = "OP10", ToOperationCode = "OP20"
        });

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
                    LogicalDemandKey = logicalDemandKey, PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = logicalDemandKey,
                    MaterialId = MaterialId, FactoryId = 1,
                    NetOutputQty = qty, PlannedProcessQty = qty,
                    RequiredAvailableTime = PlanningStart.AddDays(20), DemandSequence = 1,
                    RouteCode = demandRouteCode, PathId = demandPathId
                }
            },
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = PlanningStart, End = PlanningEnd, IsAvailable = true }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  A. 批键域（P0-02）—— 机制层，0号位 反证 ②
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// **0号位 反证 ②**：同一 Demand 拆 2 批，**即使都走同一 Route / 同一 Path**，批键仍必须不同。
    ///
    /// 旧键 `EB|{demand}|{route}|{path}` 由 Route/Path 派生 ⇒ 同 Route/Path 的两批会**撞成同一个键**
    /// ⇒「同 Batch 共键」成立但「**不同 Batch 必不同键**」**结构性失效**（这正是 0号位 判的
    /// 「把 LogicalDemand 当成了 Execution Batch」的根因之一）。
    ///
    /// 反证构造：需求**显式带** RouteCode="RTA" / PathId=1，且批数为 2 ⇒
    ///   新实现：`EB|D1|001` ≠ `EB|D1|002`（绿）；
    ///   旧实现：两批都是 `EB|D1|RTA|1` ⇒ <c>Assert.Equal(2, keys.Distinct().Count())</c> **红**。
    /// </summary>
    [Fact]
    public void 批键域_同Route同Path拆两批_键仍不同()
    {
        var demand = Demand(netQty: 10m, routeCode: Route, pathId: Path);
        var policy = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(MaxExecutionBatchQty: 6m);

        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(demand, policy);

        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches.Select(b => b.BatchDraftKey).Distinct().Count());
        Assert.Equal(new[] { "EB|D1|001", "EB|D1|002" }, batches.Select(b => b.BatchDraftKey));
        Assert.Equal(new[] { 1, 2 }, batches.Select(b => b.Ordinal));

        // 批键**不得**包含 RouteCode（否则又是「由路由反推批身份」）
        // （批键形如 EB|D1|001 ⇒ 既无 RouteCode，也无 PathId 段）
        Assert.All(batches, b => Assert.DoesNotContain(Route, b.BatchDraftKey, StringComparison.Ordinal));
        Assert.All(batches, b =>
        {
            var parts = b.BatchDraftKey.Split('|');
            Assert.Equal(3, parts.Length);
            Assert.Equal("EB", parts[0]);
            Assert.Equal("D1", parts[1]);
        });
    }

    /// <summary>
    /// 批键由 (逻辑需求键, 批序号) **唯一决定**：Route/Path 只是批的**属性**，
    /// 改 Route/Path 不得改变批键序列（否则批身份仍被路由反向决定）。
    /// </summary>
    [Fact]
    public void 批键域_由需求键与批序号唯一决定_与RoutePath无关()
    {
        var policy = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(MaxExecutionBatchQty: 6m);

        var keysWithRoute = PhaseTwoInitialScheduler
            .FormExecutionBatches(Demand(netQty: 10m, routeCode: Route, pathId: Path), policy)
            .Select(b => b.BatchDraftKey).ToArray();

        var keysWithoutRoute = PhaseTwoInitialScheduler
            .FormExecutionBatches(Demand(netQty: 10m), policy)
            .Select(b => b.BatchDraftKey).ToArray();

        Assert.Equal(keysWithoutRoute, keysWithRoute);

        // 纯函数口径锁
        Assert.Equal("EB|D1|001", PhaseTwoInitialScheduler.ExecutionBatchKey("D1"));
        Assert.Equal("EB|D1|002", PhaseTwoInitialScheduler.ExecutionBatchKey("D1", 2));
        Assert.Equal("EB|D1|007", PhaseTwoInitialScheduler.ExecutionBatchKey("D1", 7));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  B. 批数裁决（P0-01）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A/B（连续份额）**硬约束**：`IsContinuation` / `NoSplitMerge` ⇒ 恒 1 批，
    /// **即使硬最大批量小到「理论上该拆很多批」也不拆**（v1.6 NoSplitMerge；与 Phase4 P0_07 同向）。
    /// </summary>
    [Fact]
    public void AB连续份额_恒不拆批_硬最大也不拆()
    {
        var tinyMax = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(MaxExecutionBatchQty: 1m);

        var byContinuation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 100m, isContinuation: true, noSplitMerge: true), tinyMax);
        Assert.Single(byContinuation);
        Assert.Equal("EB|D1|001", byContinuation[0].BatchDraftKey);

        var byNoSplitMerge = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 100m, noSplitMerge: true), tinyMax);
        Assert.Single(byNoSplitMerge);
    }

    /// <summary>
    /// 缺 ⑧块批策略（`policy == null`）⇒ 恒 1 批。
    /// 依据：四级兜底链（0号位 20260928 §十五）中「缺策略」走兜底，**不认领** `BATCH_POLICY_MISSING`
    /// （该码与兜底链冲突，属待裁项，不擅自认领）。
    /// </summary>
    [Fact]
    public void 缺批策略_恒单批()
    {
        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 100m), null);

        Assert.Single(batches);
        Assert.Equal("EB|D1|001", batches[0].BatchDraftKey);
        Assert.Equal(100m, batches[0].NetOutputQty);
    }

    /// <summary>
    /// 硬最大批量（`MaxExecutionBatchQty`）⇒ **强制拆批**，且**不受 `AllowSplit` 限制**。
    /// 依据：`SplitParams.MaxOptimizationSplitCount` 注释明文
    /// 「仅限制优化性拆分搜索（**不限制硬 Max 强制拆分**）」。
    ///
    /// 反证构造：`AllowSplit = false` 而硬 Max 存在 ⇒ 仍必须拆（旧实现若以 AllowSplit 为拆批开关则 <c>Assert.Equal(3, ...)</c> 红）。
    /// </summary>
    [Fact]
    public void 硬最大批量_强制拆批且不受AllowSplit限制()
    {
        var policy = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(
            MaxExecutionBatchQty: 4m,
            AllowSplit: false);   // ← 刻意关掉优化性拆分开关

        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 10m), policy);

        // ceil(10 / 4) = 3
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.True(b.NetOutputQty <= 4m, $"批 {b.Ordinal} 超出硬最大：{b.NetOutputQty}"));
    }

    /// <summary>
    /// 数量**逐分不丢**：`Σ NetOutputQty = 需求 NetOutputQty`、`Σ PlannedProcessQty = 需求 PlannedProcessQty`。
    /// 构造刻意用除不尽的 10 / 3（前两批向下取整 3.3333，末批取余 3.3334）。
    /// </summary>
    [Fact]
    public void 数量守恒_逐分不丢_Σ等于需求数量()
    {
        var demand = Demand(netQty: 10m, procQty: 7m);
        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(MaxExecutionBatchQty: 4m));

        Assert.Equal(3, batches.Count);
        Assert.Equal(10m, batches.Sum(b => b.NetOutputQty));
        Assert.Equal(7m, batches.Sum(b => b.PlannedProcessQty));

        // 末批取余数 ⇒ 各批之和逐分等于需求（decimal 精确，无浮点误差）
        Assert.Equal(3.3333m, batches[0].NetOutputQty);
        Assert.Equal(3.3333m, batches[1].NetOutputQty);
        Assert.Equal(3.3334m, batches[2].NetOutputQty);
    }

    /// <summary>
    /// **EBD-03 锁**：硬 Min 与硬 Max 同时给出时，**硬 Max 优先**（容量型硬约束），
    /// V1 **不会**为满足硬 Min 而把批数收缩到违反硬 Max。
    ///
    /// 构造：qty=10、硬 Max=6 ⇒ 至少 2 批；硬 Min=6 ⇒ 2 批时每批 5 &lt; 6
    ///   （若要满足 Min 须收成 1 批 ⇒ 每批 10 &gt; 硬 Max 6 ⇒ 违反 Max）。
    /// V1 裁决：**保 2 批**（Max 优先）。依据：0号位 20260928 §十五 四级兜底链 + 容量型硬约束优先。
    /// </summary>
    [Fact]
    public void 硬最小与硬最大同时给_硬最大优先()
    {
        var policy = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(
            MinExecutionBatchQty: 6m,
            MaxExecutionBatchQty: 6m);

        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 10m), policy);

        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.True(b.NetOutputQty <= 6m, "硬 Max 必须优先于硬 Min"));
    }

    /// <summary>
    /// **EBD-03 作用面锁**：硬 Min **当前对批数无作用**（V1 已确认无作用面，见 `DecideExecutionBatchCount` 注释）。
    ///
    /// 构造：qty=10、硬 Max=2 ⇒ `ceil(10/2)=5` 批（每批 2）；硬 Min=4 ⇒ 想收缩到 2 批（每批 5）。
    ///   V1 裁决：**仍为 5 批** —— 因为 `ceil(qty/Max)` 已是「满足硬 Max 的**最小**批数」，
    ///   再减一批（10/4=2.5）就 > 硬 Max 2 ⇒ 违反硬 Max。
    ///   ⇒ 「批太小」这一 Min 想拦的形态，在硬 Max 存在时**本就不会出现**；无硬 Max 时批数恒 1。
    ///
    /// 本用例是**回归锁**：若将来引入「优化性拆批」（EBD-02/03 销账）使硬 Min 获得作用面，
    /// 本用例会**转红** ⇒ 届时必须重写本用例并同步 EBD-03 登记。
    /// </summary>
    [Fact]
    public void 硬最小批量_当前对批数无作用面()
    {
        var policy = new PhaseTwoInitialScheduler.ExecutionBatchPolicyInput(
            MinExecutionBatchQty: 4m,
            MaxExecutionBatchQty: 2m);

        var batches = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 10m), policy);

        Assert.Equal(5, batches.Count);
        Assert.Equal(10m, batches.Sum(b => b.NetOutputQty));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  C. Phase4 归批键回填（P0-02 的「同批共键」半边）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase4 重建 Task 的归批键解析：**已登记则逐字复用**（否则同一批被劈成两个键），**未登记则回落 1 号批键**。
    /// 用真实的 <see cref="PhaseOneConstraintBuilder"/> 造出 <see cref="ConstraintContext"/>（纯内存，不触库）。
    /// </summary>
    [Fact]
    public void Phase4归批键_已登记则复用未登记则回落首批()
    {
        var constraints = new PhaseOneConstraintBuilder().BuildConstraints(BuildRequest());

        // 未登记 ⇒ 回落 1 号批
        Assert.Equal("EB|D1|001", PhaseTwoInitialScheduler.ResolveExecutionBatchKeyForRebuild("D1", constraints));

        // Phase2 已登记 ⇒ 逐字复用（而不是重新推导）
        constraints.ExecutionBatchDraftKeys["D1"] = new List<string> { "EB|D1|001", "EB|D1|002" };
        Assert.Equal("EB|D1|001", PhaseTwoInitialScheduler.ResolveExecutionBatchKeyForRebuild("D1", constraints));

        // 登记了**别的**需求 ⇒ 本需求仍走回落
        Assert.Equal("EB|D9|001", PhaseTwoInitialScheduler.ResolveExecutionBatchKeyForRebuild("D9", constraints));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  D. 求解层 —— 批键落到 FinalTask
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 求解层批键域锁：单批时**同一批的全部工序 Task 共用一个** `ExecutionBatchDraftKey`，
    /// 且键值 = `EB|{需求键}|001`（**不含 RouteCode / PathId**）。
    ///
    /// 反证性：旧键 `EB|D1|RTA|1` ⇒ 本用例 <c>Assert.Equal("EB|D1|001", ...)</c> **红**。
    /// </summary>
    [Fact]
    public async Task 求解层_批键域锁_全体工序共键且不含RoutePath()
    {
        var result = await _solver.SolveAsync(BuildRequest(qty: 1m, demandRouteCode: Route, demandPathId: Path));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        var tasks = result.FinalTasks.OrderBy(t => t.StageCode, StringComparer.Ordinal).ToList();
        Assert.Equal(2, tasks.Count);   // 完整多工序链

        Assert.All(tasks, t => Assert.Equal("EB|D1|001", t.ExecutionBatchDraftKey));
        Assert.All(tasks, t => Assert.DoesNotContain(Route, t.ExecutionBatchDraftKey!, StringComparison.Ordinal));

        // 同批 ⇒ 同 Route/Path（每批一条完整 Path），且 Route/Path 仍如实落到 Task
        Assert.All(tasks, t => Assert.Equal(Route, t.RouteCode));
        Assert.All(tasks, t => Assert.Equal(Path, t.PathId));
    }

    /// <summary>
    /// **EBD-02 阻塞锁（0号位 反证 ① 的生产不可达性）**。
    ///
    /// 0号位 反证 ① 要求：*一个 C Demand 拆 2 批 ⇒ 两个不同 `ExecutionBatchDraftKey`，
    /// 且每个 Key 各自都有一条完整多工序链*（**端到端**）。
    ///
    /// **本用例断言：该端到端形态在当前生产路径上不可达**，因为：
    ///   · 批数输入来自 `ConstraintContext.ExecutionBatchPolicy`；
    ///   · 该属性的**唯一赋值来源**本应是 `DomainSolveRequest.StrategySnapshot`，
    ///     而 1↔2 的 `SolverStrategySnapshot` **没有** ⑧块 BatchPolicy 成员
    ///     （⑧块 `BatchPolicyRuleSnapshot` 存在于 `FrozenStrategySnapshot`，属 **2↔3** 层，未投影给 1号位）；
    ///   · 故生产装配处 `ExecutionBatchPolicy` **恒 null** ⇒ `FormExecutionBatches` 恒返回 1 批。
    ///
    /// 本用例同时是**回归锁**：⑧块载体一旦接入 1↔2（EBD-02 销账），
    /// `ExecutionBatchPolicy` 就可能非 null ⇒ 本用例**转红**，届时必须补上 0号位 反证 ① 的完整端到端用例。
    ///
    /// ⚠ 这是**如实登记的未达项**，不是「已达标」：多批机制已落码并单测（见 A/B 段），
    ///   **其生产入口缺失**（EBD-02）。**不得**据此认为 0号位 反证 ① 已完成。
    /// </summary>
    [Fact]
    public async Task 生产路径_批策略载体恒空_EBD02阻塞锁()
    {
        // ① 装配层：⑧块批策略确实到达不了 1号位（恒 null）
        var constraints = new PhaseOneConstraintBuilder().BuildConstraints(
            BuildRequest(qty: 10m, demandRouteCode: Route, demandPathId: Path));
        Assert.Null(constraints.ExecutionBatchPolicy);

        // ② 求解层：因此一个 C Demand 在生产路径上恒为 1 批（反证 ① 的「拆 2 批」端到端不可达）
        var result = await _solver.SolveAsync(BuildRequest(qty: 10m, demandRouteCode: Route, demandPathId: Path));

        Assert.True(result.Success, result.ErrorMessage);
        var distinctKeys = result.FinalTasks
            .Select(t => t.ExecutionBatchDraftKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct()
            .ToList();

        Assert.Single(distinctKeys);
        Assert.Equal("EB|D1|001", distinctKeys[0]);
    }
}
