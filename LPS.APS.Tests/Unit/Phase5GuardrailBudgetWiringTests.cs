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
/// **R11 三档限时接线实测**（2026-10-29）。
///
/// <para><b>接的是什么</b>：`PhaseFiveCompression.SetupSearchBudget` 从冻结
/// `CandidateGuardrailBlock`（载体 `DomainSolveRequest.StrategySnapshot.CandidateGuardrail`）读
/// `NormalMs` / `SoftMs` / `LocalHardMs`，三档分别约束
/// ① 「开新生产窗口/新段」② 「当前段邻域枚举」③ 「整个序列优化立即中止」。
/// 此前该处只有 `PhaseFiveCompression.cs` 内一个 `private const int SetupSearchGlobalTimeLimitSeconds = 120`，
/// 三个字段在 `LPS.APS.Scheduling` 内 **0 命中**。</para>
///
/// <para><b>本文件钉的三条</b>：</para>
/// <list type="number">
///   <item><b>T1 不降目标</b>：三档全配成 1 毫秒（预算立即用尽）⇒ 仍然 `Success`、Task 一个不少。
///     三档只能削**可选优化**的搜索时长，不能把任何需求变成未排 —— 这条是 0号位 红线
///     「保留完整硬合法基础解」的机器判据。</item>
///   <item><b>T2 不静默</b>：三档缺失/非单调时回落 120s 单档，并留 Warning 级
///     `PHASE5_GUARDRAIL_BUDGET_FALLBACK` 追溯 —— 不把配置异常默默当成「无限预算」或「0 预算」。</item>
///   <item><b>T3 正常配置不留痕</b>：默认三档（60000/90000/180000）合法 ⇒ 不出现回落痕。
///     与 T2 合起来说明该痕确实是「配置异常」信号，不是每次都有。</item>
/// </list>
///
/// <para><b>不测什么</b>：不测「预算 90s 时到底少优化了几个换型」—— 那需要真实规模的夹具，
/// 而且属于性能读数，不是 Unit 能钉的。本文件只钉「接线生效 + 不越红线」。</para>
/// </summary>
public class Phase5GuardrailBudgetWiringTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private const string Stage1 = "STAGE1";
    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const int ResourceId = 1;
    private const int FactoryId = 1;

    private const string FallbackReasonCode = "PHASE5_GUARDRAIL_BUDGET_FALLBACK";

    // ════════════════════════════════════════════════════════════════════
    // T1：预算立即用尽，排程结果不得少一件
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 三档都配成 1 毫秒 ⇒ 进入 Setup 序列优化时预算已用尽，窗口循环第一轮就返回。
    /// 红线：这只能让**可选优化**不做，绝不能让任何需求变未排。
    /// </summary>
    [Fact]
    public async Task T1_三档预算立即用尽_不得把任何需求拖成未排()
    {
        var withTinyBudget = await new FiniteCapacitySolver().SolveAsync(
            Build(normalMs: 1, softMs: 1, localHardMs: 1));
        var withDefaultBudget = await new FiniteCapacitySolver().SolveAsync(
            Build(normalMs: 60_000, softMs: 90_000, localHardMs: 180_000));

        Assert.True(withTinyBudget.Success, withTinyBudget.ErrorMessage);
        Assert.True(withDefaultBudget.Success, withDefaultBudget.ErrorMessage);

        // ① 一件都不能少。
        Assert.Equal(2, withTinyBudget.FinalTasks.Count);
        Assert.Equal(withDefaultBudget.FinalTasks.Count, withTinyBudget.FinalTasks.Count);

        // ② 每条需求仍然有完成时间事实（不是被「优化掉」了）。
        Assert.Equal(2, withTinyBudget.DemandCompletions.Count);

        // ③ 预算用尽不是缺料，不该冒出任何缺料码。
        Assert.DoesNotContain(withTinyBudget.SolveTraceNotes, n =>
            n.ReasonCode.StartsWith("PHASE4_MATERIAL", StringComparison.Ordinal)
            || n.ReasonCode.StartsWith("PHASE5_MATERIAL", StringComparison.Ordinal));
    }

    // ════════════════════════════════════════════════════════════════════
    // T2：配置非法 ⇒ 回落 + 留痕（不静默）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// `NormalMs=100000 / SoftMs=1000 / LocalHardMs=1` —— 正常 &gt; 软 &gt; 硬，不单调。
    /// 语义上「软超时早于正常预算」是自相矛盾的配置 ⇒ 三档整块不可信，
    /// 回落 `SetupSearchGlobalTimeLimitSeconds = 120s` 单档，并明确留一条 Warning。
    /// </summary>
    [Fact]
    public async Task T2_三档非单调_回落单档并留Warning痕()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            Build(normalMs: 100_000, softMs: 1_000, localHardMs: 1));

        Assert.True(result.Success, result.ErrorMessage);

        var note = Assert.Single(result.SolveTraceNotes, n => n.ReasonCode == FallbackReasonCode);
        Assert.Equal("Warning", note.Level);

        // 回落只影响搜索时长，不影响「已排结果硬合法」。
        Assert.Equal(2, result.FinalTasks.Count);
    }

    // ════════════════════════════════════════════════════════════════════
    // T3：默认配置不留痕
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 默认三档（`NormalMs=60000` / `SoftMs=90000` / `LocalHardMs=180000`）合法且单调
    /// ⇒ 走正常接线路径，**不**该出现回落痕。否则 T2 的痕就不具备鉴别力。
    /// </summary>
    [Fact]
    public async Task T3_默认三档合法_不出现回落痕()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            Build(normalMs: 60_000, softMs: 90_000, localHardMs: 180_000));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == FallbackReasonCode);
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    /// <summary>2 条需求各 6，各用独立 AllocationSequence 且各供 10 ⇒ 都合法可排，排除缺料干扰。</summary>
    private static DomainSolveRequest Build(int normalMs, int softMs, int localHardMs)
    {
        var demands = new[] { (Seq: 1L, Qty: 6m), (Seq: 2L, Qty: 6m) };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            LogicalProductionDemands = demands
                .Select((d, i) => new LogicalProductionDemand
                {
                    LogicalDemandKey = $"D{i + 1}",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = d.Seq,
                    DemandKey = $"D{i + 1}",
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    StartStageCode = Stage1,
                    NetOutputQty = d.Qty,
                    PlannedProcessQty = d.Qty,
                    RequiredAvailableTime = Day.AddDays(20),
                    DemandSequence = i + 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                })
                .ToList(),

            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = Stage1,
                    StandardDuration = 60m,          // 源事实：秒 ⇒ 1 分钟/件
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

            MaterialConstraints = demands
                .Select(d => new MaterialAvailabilitySlice
                {
                    AllocationSequence = d.Seq,
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    Quantity = 10m,
                    AvailableTime = Day
                })
                .ToList(),

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                CandidateGuardrail = new CandidateGuardrailBlock
                {
                    NormalMs = normalMs,
                    SoftMs = softMs,
                    LocalHardMs = localHardMs
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
    }
}
