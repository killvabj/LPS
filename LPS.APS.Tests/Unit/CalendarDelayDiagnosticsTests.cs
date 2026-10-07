using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 「资源日历不可用造成的等待」延期原因分支的回归测试（纯内存，直接调 FiniteCapacitySolver）。
///
/// 被测分支：<c>PhaseThreeDiagnostics.DiagnoseDelayReason</c> 第 7 步（2026-10-07 新增）。
/// 背景（2026-10-07 补测）：该分支此前**只有不误报路径**被既有 415 项覆盖，**命中路径未被断言**
/// —— 本文件即补此缺口（1号位 自查登记项，见致 0号位 报告 §1.7 / §五）。
///
/// 口径依据：
/// · 15 码权威枚举：《APS数据库字段说明文档 v5.1.9》§八.1 `:4833`（「全文 ReasonCode 必须属于此列表」）
///   ⇒ 日历不可用**不新增码**，取语义最近者 `RESOURCE_CAPACITY_WAIT`。
/// · 细分承载：`EvidenceJson` 外壳含 `evidenceType` / `summary` / `details`（`v5.1.9:4836`/`:4840`；
///   `details` 内部 schema 阶段一不冻结，`evidenceType` **无值域枚举**）⇒ 细分 = `evidenceType="CALENDAR"`。
/// · 双通道不混用：`v5.1.9:4100`（`ScheduleExplanationFact` 与 `ExplainTrace` 共存不替代、禁止混用）。
///
/// 0号位 裁定（Calendar 语义，本文件据此立判据）：
/// 《APS_V1_批量正倒排执行批_0号位对1_2_3号位评估回执统一解读与裁决回复_v1.0_20260928.md》
/// · §11.1（`:452-475`）DueDate / PlanningEnd / 90天窗口**不是硬终止边界**；
///   只要正式 Resource Calendar 中仍有合法产能，Task 就要排过去并记录「延期 + 超计划窗口」，
///   **不得**因超过 PlanningEnd 直接变成 Unscheduled。
/// · §11.2（`:477-496`）**不允许制造虚拟无限产能**：Calendar 结束后**不得**自动生成 7×24 虚拟日历、
///   不得用低置信虚拟 Capacity Slot 当正式计划 ——「没有真实日历，就不能说『未来仍存在合法产能』」。
/// · §11.3（`:498-533`）搜索边界 = 已加载的 Eligible Resource 正式 Calendar 最晚有效时间；
///   到末端仍找不到 ⇒ 返回 `CALENDAR_COVERAGE_INSUFFICIENT`（或现有等价 Issue），其业务含义是
///   「日历覆盖范围不足，APS 无法证明更远未来的合法产能」，**不是**「真实产能一定不存在」，
///   **也不是**「普通延期」。
///
/// ⇒ 三个用例分别锁：①日历空洞确实被识别为等待（命中路径）；②「厂未开门」不被误算为等待（防误报）；
///   ③「日历覆盖不足排不下」走 Unscheduled + 专属 Reason，**不得**被记成延期原因（§11.3）。
/// </summary>
public class CalendarDelayDiagnosticsTests
{
    private static readonly DateTime PlanningStart = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new DateTime(2026, 10, 31, 0, 0, 0);

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>
    /// ① 命中路径：日历有「空洞」，Task 只能落到第二个窗 ⇒ 空档内含日历不可用时间 ⇒ 判为日历因。
    ///
    /// 场景：R1 日历 = [T0, T0+30) + [T0+120, T0+720)；工序时长 60 分钟 ⇒ 30 分钟的首窗装不下，
    /// Task 落到 T0+120（第二窗起点），结束 T0+180；交期 T0+60 ⇒ 延期 120 分钟。
    /// 空档 [T0, T0+120) 内仅 30 分钟被日历覆盖 ⇒ 其余 90 分钟是**日历不可用**，非占用。
    /// </summary>
    [Fact]
    public async Task 日历空洞致等待_判为日历因_不误报为前序因()
    {
        var result = await _solver.SolveAsync(Build(
            durationMinutes: 60,
            requiredAvailableTime: PlanningStart.AddMinutes(60),
            windows: new[]
            {
                (PlanningStart, PlanningStart.AddMinutes(30)),
                (PlanningStart.AddMinutes(120), PlanningStart.AddMinutes(720))
            }));

        var fact = Assert.Single(DemandFacts(result));

        // ReasonCode 取 15 码中语义最近者（日历不可用 ⇒ 资源在需要时不可用）；不得新增码
        Assert.Equal("RESOURCE_CAPACITY_WAIT", fact.ReasonCode);
        // 细分由 EvidenceJson 外壳承载，而非新码
        Assert.Contains("\"evidenceType\":\"CALENDAR\"", fact.EvidenceJson);
        // Severity 权威值域 INFO / WARN / ERROR（v5.1.9:4834）
        Assert.Equal("ERROR", fact.Severity);
        // 延期 120 分钟
        Assert.Equal(2.0m, fact.ImpactHours);
        // EvidenceJson 外壳三字段齐备（v5.1.9:4836）
        Assert.Contains("\"summary\":", fact.EvidenceJson);
        Assert.Contains("\"details\":", fact.EvidenceJson);
        // 需求已排下（§11.1：不得因日历等待变成 Unscheduled）
        Assert.Empty(result.UnscheduledTasks);
    }

    /// <summary>
    /// ② 防误报路径：资源**首窗起点晚于计划期起点**（厂未开门）⇒ 不得算成等待。
    ///
    /// 场景：R1 日历只有 [T0+600, T0+1200)，工序时长 60 ⇒ Task 排在 T0+600、结束 T0+660；
    /// 交期 T0+600 ⇒ 延期 60 分钟。空档起点被夹到**该资源首个可用窗起点** T0+600，
    /// 故空档长度 0 ⇒ **不判日历因**，落兜底 PRECEDENCE_WAIT。
    /// 若少了这层夹取，「计划期起点 → 首窗起点」这 600 分钟会被误算成等待，
    /// 凡在首窗开工的延期需求都会误报 CALENDAR。
    /// </summary>
    [Fact]
    public async Task 首窗开工_厂未开门段不计为等待_不误判日历因()
    {
        var result = await _solver.SolveAsync(Build(
            durationMinutes: 60,
            requiredAvailableTime: PlanningStart.AddMinutes(600),
            windows: new[] { (PlanningStart.AddMinutes(600), PlanningStart.AddMinutes(1200)) }));

        var fact = Assert.Single(DemandFacts(result));

        Assert.Equal("PRECEDENCE_WAIT", fact.ReasonCode);
        Assert.DoesNotContain("\"evidenceType\":\"CALENDAR\"", fact.EvidenceJson);
        Assert.Equal(1.0m, fact.ImpactHours);
    }

    /// <summary>
    /// ③ 0号位 §11.3 裁定：真实日历**覆盖不足**导致排不下 ⇒ 走 Unscheduled + 专属 Reason
    /// `CALENDAR_COVERAGE_INSUFFICIENT`，且**不得**被记成延期原因（「不是普通延期」）。
    ///
    /// 场景：R1 日历只有 [T0, T0+30)，工序时长 60 ⇒ 任何真实日历窗都装不下
    /// （Phase2 <c>FindForwardSlot</c> 对「无日历」与「窗长不够」均返回 null）⇒ Unscheduled。
    /// §11.2 禁止用虚拟 7×24 日历补齐，故此处**不得**回退成排得下。
    /// </summary>
    [Fact]
    public async Task 日历覆盖不足排不下_报CALENDAR_COVERAGE_INSUFFICIENT_不误报为延期因()
    {
        var result = await _solver.SolveAsync(Build(
            durationMinutes: 60,
            requiredAvailableTime: PlanningStart.AddMinutes(30),
            windows: new[] { (PlanningStart, PlanningStart.AddMinutes(30)) }));

        // 排不下 ⇒ 走 Unscheduled 通道，并带 0号位 §11.3 裁定的专属 Reason
        Assert.Contains(result.UnscheduledTasks,
            u => u.DraftId == "M1" && u.Reason == "CALENDAR_COVERAGE_INSUFFICIENT");
        // §11.3：这不是「普通延期」⇒ 不得产出任何 Demand 级延期原因事实
        Assert.Empty(DemandFacts(result));
    }

    /// <summary>Demand 级延期原因事实（ObjectType = "DEMAND"）。</summary>
    private static IEnumerable<ScheduleExplanationFact> DemandFacts(DomainSolveResult result)
        => result.ExplanationFacts.Where(f => f.ObjectType == "DEMAND");

    private static DomainSolveRequest Build(
        double durationMinutes,
        DateTime requiredAvailableTime,
        (DateTime Start, DateTime End)[] windows)
    {
        const int deptId = 100;
        const string stage = "STAGE1";
        const string opCode = "OP10";

        var logicalDemands = new List<LogicalProductionDemand>
        {
            new()
            {
                LogicalDemandKey = "M1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                AllocationSequence = 1, DemandKey = "M1", MaterialId = 1, FactoryId = 1,
                NetOutputQty = 1m, PlannedProcessQty = 1m,
                RequiredAvailableTime = requiredAvailableTime, DemandSequence = 1
            }
        };

        var routingOps = new List<RoutingOperation>
        {
            new()
            {
                MaterialId = 1, ProductionDepartmentId = deptId, RouteCode = "DEFAULT",
                OperationCode = opCode, StageCode = stage,
                StandardDuration = (decimal)durationMinutes, SetupTime = 0m
            }
        };

        var eligibilities = new List<OperationResourceEligibility>
        {
            new()
            {
                MaterialId = 1, ProductionDepartmentId = deptId, RouteCode = "DEFAULT",
                OperationCode = opCode, ResourceId = 1, Priority = 1, CapacityFactor = 1m
            }
        };

        var deptContexts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = 1, StageCode = stage, ProductionDepartmentId = deptId }
        };

        var resources = new List<ResourceDefinition>
        {
            new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
        };

        // ⚠ 只装 IsAvailable = true 的窗 —— 与 Phase1 BuildResourceCalendars 同口径；
        //   「日历空洞」由**缺失窗**表达，不得用 IsAvailable=false 表达（那会被 Phase1 直接滤掉）。
        var calendarSlots = windows
            .Select(w => new ResourceCalendarSlot
            {
                ResourceId = 1, Start = w.Start, End = w.End, IsAvailable = true
            })
            .ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = logicalDemands,
            RoutingOperations = routingOps,
            OperationResourceEligibility = eligibilities,
            MaterialStageDepartmentContexts = deptContexts,
            Resources = resources,
            CalendarSlots = calendarSlots,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                SolverStrategy = new SolverStrategyBlock
                {
                    BottleneckMode = DynamicBottleneckMode.Auto,
                    AnchorResourceCode = null
                }
            }
        };
    }
}
