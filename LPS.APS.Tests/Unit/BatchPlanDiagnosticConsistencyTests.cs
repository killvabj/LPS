using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **回归锁（2026-11-26）**：Phase2 失败报文里「**批方案共 N 个**」与「**本工序最长需 X 分钟**」
///   必须来自**同一个候选方案**。
///
/// <para><b>它锁的是什么 bug</b>：`Phase2BlockedDetails` 原先是调度器**实例字段**、只按需求键、
///   **不分候选方案**；而批方案候选试排**每一轮都往里写**、后写覆盖先写 ⇒ 最终报文会把
///   「选中方案的批数」和「另一候选方案的工时」拼一起。</para>
///
/// <para><b>实测证据（`999634_191332`，FRL PV999634）</b>：外层写「批方案共 2 个」（2 批 ⇒ 批001 = 1400 件），
///   内层写「本工序最长需 3662.68 分钟」——那是 **3 批候选**（批001 = 934 件）的 M102 工时
///   （`235.29 秒/件 × 934 ÷ 60 = 3662.68`）。两者差 **1.5 倍**，0号位 据此要求核查
///   「实际资源系数 / 投入 / 公式」；查实结论是**诊断串方案**，不是排程算错。</para>
///
/// <para><b>本测试怎么锁</b>：构造一条需求，使**每个候选批数**的批001 工时**互不相同**，
///   且**全部**候选都因窗太短排不进；然后从最终报文里解析出 N（批数）与 X（工时），
///   断言 `X == 单件分钟 × 批001 件数(N)`。旧实现下二者会来自不同候选 ⇒ 必然不等。</para>
///
/// <para>⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。</para>
/// </summary>
public class BatchPlanDiagnosticConsistencyTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int MaterialId = 9601;
    private const int Dept = 2601;
    private const string Factory = "F1";

    /// <summary>单件标准工时（分钟）——取整数便于从报文里反算。</summary>
    private const decimal PerUnitMinutes = 100m;

    /// <summary>需求净量：拆 1/2/3 批分别是 6 / 3+3 / 2+2+2，批001 工时 600 / 300 / 200 各不相同。</summary>
    private const decimal NetQty = 6m;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 失败报文里的批数与工时必须来自同一候选()
    {
        var result = await _solver.SolveAsync(Build());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);   // 窗太短 ⇒ 任何批数都排不进

        var allText = string.Join(" ‖ ", result.SolveTraceNotes.Select(n => n.Message));

        // 报文里的两个量
        var mBatch = Regex.Match(allText, @"批方案共\s*(\d+)\s*个");
        var mNeed = Regex.Match(allText, @"本工序最长需\s*([\d.]+)\s*分钟");
        Assert.True(mBatch.Success, "报文里没找到「批方案共 N 个」：" + allText);
        Assert.True(mNeed.Success, "报文里没找到「本工序最长需 X 分钟」：" + allText);

        var batchCount = int.Parse(mBatch.Groups[1].Value);
        Assert.True(batchCount > 1, $"本次只评估了 {batchCount} 个批，未走到多候选路径；报文：{allText}");
        var reportedNeed = decimal.Parse(mNeed.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        // 该批数下批001 的件数（与 BuildBatchFormation 同式：整除 + 余数逐批 +1）
        var total = decimal.Truncate(NetQty);
        var baseNet = decimal.Floor(total / batchCount);
        var remainder = total - baseNet * batchCount;
        var firstBatchQty = baseNet + (remainder >= 1 ? 1m : 0m);

        var expectedNeed = PerUnitMinutes * firstBatchQty;

        Assert.Equal(expectedNeed, reportedNeed);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build()
    {
        const string stage = "S1";
        const string op = "OP10";
        const int rid = 31001;

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
                    StartStageCode = stage, NetOutputQty = NetQty, PlannedProcessQty = NetQty,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            MaterialRequirementLinks = new List<MaterialRequirementLink>(),
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = op, StageCode = stage,
                    // 输入单位是**秒**（Phase1 ÷60 归一）⇒ 100 分钟/件 = 6000 秒
                    StandardDuration = PerUnitMinutes * 60m, SetupTime = 0m
                }
            },
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                    OperationCode = op, ResourceId = rid, Priority = 1, CapacityFactor = 1m
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
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = rid, ResourceCode = "R1", FactoryCode = Factory, Capacity = 1m }
            },
            // 窗只有 50 分钟 ⇒ 600/300/200 分钟三种批全都装不下（全部候选都失败）
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = rid, Start = Day, End = Day.AddMinutes(50), IsAvailable = true }
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
                        // Min/Max/Preferred 取 3 ⇒ 经济参考 = ceil(6/3) = 2，候选批数 {1,2,3}
                        MinExecutionBatchQty = 3m, MaxExecutionBatchQty = 3m, PreferredBatchQty = 3m,
                        AllowSplit = true, AllowMerge = false
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
