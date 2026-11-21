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
/// **0号位 2026-11-20《采购PI SH时间职责固定与共享PI硬边纠偏执行指令 v1.0》§四**：
/// 1号位不得等2号位而停专题，须先独立完成这些确定性单测。
///
/// <para>**固定时间分类（§四.1）**：同一需求分片分别注入 `CURRENT` / `KNOWN` / `UNRESOLVED` / `SH_LINKED`，
/// 验证 Solver 不会把假的期末日期/计划起点当真实到货，也不会忽略真实采购 ETA。</para>
///
/// <para>**共享 PI（§四.2/§四.3）**：一张 PI 被多个上层订单合法承接 —— 我方消费侧只能按
/// `MaterialRequirementLink` 的真实生产端身份建硬边；**不得按 PI 号/物料号猜**（0号位 §三 甲/乙/丙
/// 三选一均未被批准为默认方案）。负例锁「不得因共享而放松硬约束」。</para>
///
/// <para>**同域 SH（§四.4）**：一次 Solver 内源→目标硬边；目标不得早于源最后必要批 + 合法 LT。
/// LT 已配置事实优先（本文件夹具显式给 LT；24h 兜底属待冻结修订，不在本文件假装生效）。</para>
///
/// <para>纯内存，不碰数据库。⚠ 现行 DTO 装不下「PI 限定额/同号不同 Stage 的身份判别」等字段，
/// 对应的负例**不伪造夹具**，在本文件末尾以注释登记缺口，不冒充已验。</para>
/// </summary>
public sealed class PiShTimeResponsibilityTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);
    private static readonly DateTime PlanEnd = new(2027, 2, 18, 0, 0, 0);   // 与 0号位 点名的占位终点同形
    private const int DeptId = 100;
    private const string Stage = "STAGE1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    // ═══════════════════════ §四.1 固定时间分类 ═══════════════════════

    [Fact(DisplayName = "§四.1 CURRENT：现货按计划期起点即时可用（不是未来某时）")]
    public async Task T1_Current_AvailableAtPlanningStart()
    {
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { S(1, 1, 10m, Day, "CURRENT") },
            Array.Empty<L>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(Day, task.PlannedStartTime);
    }

    [Fact(DisplayName = "§四.1 KNOWN：真实采购 ETA 不被忽略，Task 不得早于该时点")]
    public async Task T2_Known_EtaRespected()
    {
        var eta = Day.AddDays(10);
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { S(1, 1, 10m, eta, "KNOWN") },
            Array.Empty<L>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.True(task.PlannedStartTime >= eta,
            $"采购 ETA={eta:O} 是真实到货时间，Task 开工 {task.PlannedStartTime:O} 不得早于它。");
    }

    [Fact(DisplayName = "§四.1 UNRESOLVED：期末占位不是可用时间 ⇒ 合法未排，不当现货也不当期末阻断")]
    public async Task T3_Unresolved_PlaceholderIsNotAvailability()
    {
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { S(1, 1, 10m, PlanEnd, "UNRESOLVED") },
            Array.Empty<L>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);          // 既不在 Day 排（不当现货），也不在 PlanEnd 排（占位不算）
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact(DisplayName = "§四.1 SH_LINKED 有硬边：按源 Task 推导动态时间（不按计划起点占位）")]
    public async Task T4_ShLinked_WithEdge_UsesTaskDerivedTime()
    {
        var request = Build(
            new[] { D("SRC", 1, 1, 10m), D("TGT", 2, 2, 10m) },
            new[] { S(2, 2, 10m, Day, "SH_LINKED") },   // 目标侧 SH 切片的 AvailableTime 只是占位 = Day
            new[] { new L { Parent = "TGT", Child = "SRC", Lag = 0m } });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var src = result.FinalTasks.Single(t => t.SourceDraftId == "SRC");
        var tgt = result.FinalTasks.Single(t => t.SourceDraftId == "TGT");

        Assert.True(tgt.PlannedStartTime >= src.PlannedEndTime,
            $"目标 {tgt.PlannedStartTime:O} 不得早于源完工 {src.PlannedEndTime:O}；"
            + "占位时点 Day 不是可用时间。");
        Assert.True(tgt.PlannedStartTime > Day,
            "若按占位 Day 当可用，目标会在 Day 排 —— 那是 0号位 明令禁止的「起点即期具料」。");
    }

    [Fact(DisplayName = "§四.1 SH_LINKED 缺硬边：不得当起点即期具料 ⇒ 合法未排（不产 SH_TIME_UNCLOSED）")]
    public async Task T5_ShLinked_WithoutEdge_NotTreatedAsAvailable()
    {
        var request = Build(
            new[] { D("TGT", 2, 2, 10m) },
            new[] { S(2, 2, 10m, Day, "SH_LINKED") },   // 无任何 MaterialRequirementLink
            Array.Empty<L>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "TGT");
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.Reason == "SH_TIME_UNCLOSED");
    }

    // ═══════════════════════ §四.2 共享 PI 正例 ═══════════════════════

    [Fact(DisplayName = "§四.2 共享PI：一源 100 供 A60+B30+C10，各自不早于源完工，Σ承接=100")]
    public async Task T6_SharedPi_ThreeConsumers_WithinRemaining()
    {
        var request = Build(
            new[]
            {
                D("PI", 1, 1, 100m),
                D("A", 2, 2, 60m),
                D("B", 3, 3, 30m),
                D("C", 4, 4, 10m),
            },
            Array.Empty<SliceSpec>(),
            new[]
            {
                new L { Parent = "A", Child = "PI", Lag = 0m },
                new L { Parent = "B", Child = "PI", Lag = 0m },
                new L { Parent = "C", Child = "PI", Lag = 0m },
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var pi = result.FinalTasks.Single(t => t.SourceDraftId == "PI");
        Assert.Equal(100m, pi.Quantity);

        foreach (var key in new[] { "A", "B", "C" })
        {
            var t = result.FinalTasks.Single(x => x.SourceDraftId == key);
            Assert.True(t.PlannedStartTime >= pi.PlannedEndTime,
                $"{key} 开工 {t.PlannedStartTime:O} 不得早于共享源 PI 完工 {pi.PlannedEndTime:O}。");
        }

        // 承接量守恒：60 + 30 + 10 = 100 = PI 剩余（不得凭空多承接、也不得丢量）。
        var absorbed = result.FinalTasks
            .Where(t => t.SourceDraftId is "A" or "B" or "C")
            .Sum(t => t.Quantity);
        Assert.Equal(100m, absorbed);

        Assert.Empty(result.UnscheduledTasks);
    }

    // ═══════════════════════ §四.3 共享 PI 负例 ═══════════════════════

    [Fact(DisplayName = "§四.3负例 共享源不可排 ⇒ 三消费方全部受阻，不得按 PI 号假具料")]
    public async Task T7_SharedPi_SourceUnschedulable_AllConsumersBlocked()
    {
        // 源物料 1 无路由（不可排）⇒ 不得凭 PI 号给消费方"有料"。
        var request = Build(
            new[] { D("PI", 1, 1, 100m), D("A", 2, 2, 60m) },
            Array.Empty<SliceSpec>(),
            new[] { new L { Parent = "A", Child = "PI", Lag = 0m } },
            materialsWithoutRouting: new HashSet<int> { 1 });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "PI");
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "A");
    }

    [Fact(DisplayName = "§四.3负例 消费方自身其它物料不足 ⇒ 只有它未排，共享源与其它消费方照排")]
    public async Task T8_SharedPi_ConsumerOwnMaterialShortage_Isolated()
    {
        var request = Build(
            new[] { D("PI", 1, 1, 100m), D("A", 2, 2, 60m), D("B", 3, 3, 30m) },
            new[]
            {
                S(2, 2, 1m, Day, "KNOWN"),    // A 自身物料只有 1 < 60 ⇒ 业务未排
                S(3, 3, 30m, Day, "KNOWN"),
            },
            new[]
            {
                new L { Parent = "A", Child = "PI", Lag = 0m },
                new L { Parent = "B", Child = "PI", Lag = 0m },
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "A");
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "B");
        Assert.NotEmpty(result.FinalTasks.Where(t => t.SourceDraftId == "B"));
        Assert.NotEmpty(result.FinalTasks.Where(t => t.SourceDraftId == "PI"));   // 共享源不得被连坐停排
    }

    // ═══════════════════════ §四.4 同域 SH 正反例 ═══════════════════════

    [Fact(DisplayName = "§四.4 同域SH：源 100 → 目标 A60+B40，一次 Solver，目标不早于源完工+LT")]
    public async Task T9_SameDomainSh_SingleSolver_TargetAfterSourcePlusLt()
    {
        var lt = 120m;   // 已配置的合法 LT（分钟），**不用** 24h 兜底
        var request = Build(
            new[] { D("SHSRC", 1, 1, 100m), D("SHA", 2, 2, 60m), D("SHB", 3, 3, 40m) },
            Array.Empty<SliceSpec>(),
            new[]
            {
                new L { Parent = "SHA", Child = "SHSRC", Lag = lt },
                new L { Parent = "SHB", Child = "SHSRC", Lag = lt },
            });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var src = result.FinalTasks.Single(t => t.SourceDraftId == "SHSRC");
        Assert.Equal(100m, src.Quantity);

        foreach (var key in new[] { "SHA", "SHB" })
        {
            var t = result.FinalTasks.Single(x => x.SourceDraftId == key);
            Assert.True(t.PlannedStartTime >= src.PlannedEndTime.AddMinutes((double)lt),
                $"{key} 开工 {t.PlannedStartTime:O} 必须 ≥ 源最后必要批完工 {src.PlannedEndTime:O} + LT {lt} 分钟。");
        }
        Assert.Empty(result.UnscheduledTasks);
    }

    [Fact(DisplayName = "§四.4反例 LT 未命中(Lag=0) ⇒ 目标仍不得早于源完工，不伪造提前时点")]
    public async Task T10_SameDomainSh_MissingLt_NoFabricatedEarlyTime()
    {
        var request = Build(
            new[] { D("SHSRC", 1, 1, 100m), D("SHA", 2, 2, 60m) },
            Array.Empty<SliceSpec>(),
            new[] { new L { Parent = "SHA", Child = "SHSRC", Lag = 0m } });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var src = result.FinalTasks.Single(t => t.SourceDraftId == "SHSRC");
        var tgt = result.FinalTasks.Single(t => t.SourceDraftId == "SHA");
        Assert.True(tgt.PlannedStartTime >= src.PlannedEndTime,
            "LT 缺失时不得把目标提前到源完工之前；缺边/LT 缺失的处置另记（待裁），"
            + "但「早于源完工」在任何分支都不合法。");
    }

    // ═══════════════════════ 现行 DTO 装不下的负例（不伪造夹具） ═══════════════════════
    //
    // §四.3 还点名了三种我方**当前契约无法表达**的情形，本文件不造假夹具冒充已验：
    //   · 「同号 PI 但不同 Stage」：`MaterialRequirementLink` 无 StageCode 字段 ⇒ 我方无法在消费侧
    //     校验 Stage 身份（只能靠 2号位 按真实 Stage/供给阈值给对 edge）；
    //   · 「共享份额超过 PI Remaining」：`MaterialRequirementLink.RequiredQty` 由 2号位给，
    //     我方**不承担**把 ΣRequiredQty ≤ Remaining 做门禁（那是 Allocation 侧事实）；
    //   · 「生产 Task 只完成 60」：属 MES 执行进度事实（5号位/2号位 输入），不在本 Solver 输入形态内。
    //   ⇒ 三项如实登记为**跨号位缺口**，随回执提请 0号位，不冒充单测通过。

    // ─────────────────────────── 夹具 ───────────────────────────

    private sealed class DemandSpec
    {
        public string Key = string.Empty;
        public int MaterialId;
        public long Alloc;
        public decimal NetQty;
    }

    private sealed class SliceSpec
    {
        public long Alloc;
        public int MaterialId;
        public decimal Qty;
        public DateTime At = Day;
        public string? Basis;
    }

    private sealed class L
    {
        public string Parent = string.Empty;
        public string Child = string.Empty;
        public decimal Lag;
    }

    private static DemandSpec D(string key, int materialId, long alloc, decimal netQty)
        => new() { Key = key, MaterialId = materialId, Alloc = alloc, NetQty = netQty };

    private static SliceSpec S(long alloc, int materialId, decimal qty, DateTime at, string? basis)
        => new() { Alloc = alloc, MaterialId = materialId, Qty = qty, At = at, Basis = basis };

    private static DomainSolveRequest Build(
        IReadOnlyList<DemandSpec> demands,
        IReadOnlyList<SliceSpec> slices,
        IReadOnlyList<L> links,
        ISet<int>? materialsWithoutRouting = null)
    {
        var without = materialsWithoutRouting ?? new HashSet<int>();
        var routed = demands.Where(d => !without.Contains(d.MaterialId)).ToList();
        var materials = routed.Select(d => d.MaterialId).Distinct().ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = PlanEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key,
                PlanVersionId = 1L,
                DomainKey = "DOMAIN",
                AllocationSequence = d.Alloc,
                DemandKey = d.Key,
                MaterialId = d.MaterialId,
                FactoryId = 1,
                StartStageCode = Stage,
                NetOutputQty = d.NetQty,
                PlannedProcessQty = d.NetQty,
                RequiredAvailableTime = PlanEnd,
                DemandSequence = (int)d.Alloc,
                QuantityAuthority = QuantityAuthorityValues.None,
            }).ToList(),
            RoutingOperations = materials.Select(m => new RoutingOperation
            {
                MaterialId = m,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = Op,
                StageCode = Stage,
                StandardDuration = 60m,
                SetupTime = 0m,
                OperationPlanningMode = "FINITE_RESOURCE",
            }).ToList(),
            OperationResourceEligibility = materials.Select(m => new OperationResourceEligibility
            {
                MaterialId = m,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = Op,
                ResourceId = m,
                Priority = 1,
                CapacityFactor = 1m,
            }).ToList(),
            MaterialStageDepartmentContexts = materials.Select(m => new MaterialStageDepartmentContextDto
            {
                MaterialId = m,
                StageCode = Stage,
                ProductionDepartmentId = DeptId,
            }).ToList(),
            Resources = materials.Select(m => new ResourceDefinition
            {
                ResourceId = m,
                ResourceCode = $"R{m}",
                FactoryCode = "F1",
                Capacity = 1m,
            }).ToList(),
            CalendarSlots = materials.Select(m => new ResourceCalendarSlot
            {
                ResourceId = m,
                Start = Day,
                End = PlanEnd,
                IsAvailable = true,
            }).ToList(),
            MaterialConstraints = slices.Select(s => new MaterialAvailabilitySlice
            {
                AllocationSequence = s.Alloc,
                MaterialId = s.MaterialId,
                FactoryId = 1,
                Quantity = s.Qty,
                AvailableTime = s.At,
                TimeBasis = s.Basis,
            }).ToList(),
            MaterialRequirementLinks = links.Select(l => new MaterialRequirementLink
            {
                ConsumerLogicalDemandKey = l.Parent,
                ProducerLogicalDemandKey = l.Child,
                ConsumerMaterialId = demands.First(d => d.Key == l.Parent).MaterialId,
                ProducerMaterialId = demands.First(d => d.Key == l.Child).MaterialId,
                RequiredQty = demands.First(d => d.Key == l.Child).NetQty,
                ProducerAllocationSequence = demands.First(d => d.Key == l.Child).Alloc,
                LagMinutes = l.Lag,
            }).ToList(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(materials, DeptId),
            },
        };
    }
}
