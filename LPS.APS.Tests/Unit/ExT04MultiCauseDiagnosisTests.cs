using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T04（0号位 v1.8 §六）**：多原因 / UNKNOWN 归因。
///
/// <para>0号位 要求逐字：「**双物料并列紧因、未知原因不造15码、可核证证据等级**」。</para>
///
/// <para>改前缺陷（本文件锁的就是它）：
/// `DiagnoseDelayReason` 第 8 步无条件投 `PRECEDENCE_WAIT` + `evidenceType="PRECEDENCE"` ——
/// 即使该需求**根本没有前序**（无跨物料子件、同需求内也无串联等待），也会被说成"前序延期"。
/// 这正是「未知原因被造成 15 码」：下游看到 PRECEDENCE 会去查前序，而前序根本不存在。</para>
///
/// <para>改后口径：`ReasonCode` 保持封闭 15 码（`ScheduleExplanationFact.ReasonCode` 是硬值域，
/// 不允许空码），但**证据必须诚实地说是"未识别"**：
/// `evidenceType="UNRESOLVED"` + `details.evidenceLevel="NONE"` + `details.attribution="UNATTRIBUTED"`；
/// 真有前序时才是 `PRECEDENCE` + `evidenceLevel="OBSERVED"` + 前序键。</para>
///
/// <para>纯内存、直调 <c>DiagnoseDelayReason</c>，不跑 Phase1、不连库。</para>
/// </summary>
public sealed class ExT04MultiCauseDiagnosisTests
{
    private static readonly DateTime Base = new(2026, 11, 20, 8, 0, 0);

    private static LogicalProductionDemand Demand(decimal qty) => new()
    {
        LogicalDemandKey = "D1",
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = 7,
        DemandKey = "D1",
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        StartStageCode = "S1",
        // 交期已过 ⇒ 必然延期。
        RequiredAvailableTime = Base.AddHours(-1),
    };

    private static FinalTaskDraft Task(string id, DateTime start, DateTime end)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = "D1",
            MaterialId = 1,
            FactoryId = 1,
            ResourceId = 1,          // 资源阻挡/日历判定只对带资源落点的 Task 生效
            StageCode = "S1",
            OperationCode = "OP10",
            Quantity = 10m,
            PlannedProcessQty = 10m,
            SetupTime = 0m,
            PlannedStartTime = start,
            PlannedEndTime = end,
        };

    private static string Run(
        ConstraintContext ctx,
        out string evidenceType,
        out string details,
        List<FinalTaskDraft>? tasks = null)
        => new PhaseThreeDiagnostics().DiagnoseDelayReason(
            Demand(10m),
            tasks ?? new List<FinalTaskDraft> { Task("T1", Base.AddHours(5), Base.AddHours(6)) },
            ctx,
            new DomainSolveRequest { PlanningStart = Base },
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>(),
            out evidenceType,
            out details);

    // ── 1. 未知不得伪装成前序 ──
    [Fact(DisplayName = "EX-T04：无任何前序证据 ⇒ evidenceType=UNRESOLVED、evidenceLevel=NONE（不得假装 PRECEDENCE）")]
    public void NoPrecedenceEvidence_MustBeMarkedUnresolved()
    {
        var code = Run(new ConstraintContext(), out var evidenceType, out var details);

        // 值域仍封闭 15 码
        Assert.Equal("PRECEDENCE_WAIT", code);
        // 但证据必须诚实
        Assert.Equal("UNRESOLVED", evidenceType);
        Assert.Contains("evidenceLevel\":\"NONE", details);
        Assert.Contains("attribution\":\"UNATTRIBUTED", details);
    }

    // ── 2. 真有前序才叫前序 ──
    [Fact(DisplayName = "EX-T04：该需求有跨物料子件 ⇒ PRECEDENCE + evidenceLevel=OBSERVED + 前序键")]
    public void RealPrecedence_MarkedObservedWithKeys()
    {
        var ctx = new ConstraintContext();
        ctx.CrossMaterialParentToChildren["D1"] = new List<string> { "C1" };

        var code = Run(ctx, out var evidenceType, out var details);

        Assert.Equal("PRECEDENCE_WAIT", code);
        Assert.Equal("PRECEDENCE", evidenceType);
        Assert.Contains("evidenceLevel\":\"OBSERVED", details);
        Assert.Contains("C1", details);
    }

    // ── 3. 同需求内串联等待也算前序证据 ──
    [Fact(DisplayName = "EX-T04：同需求后道开工恰在前道完工 ⇒ 有前序证据（OBSERVED）")]
    public void IntraDemandSeries_CountsAsPrecedenceEvidence()
    {
        var tasks = new List<FinalTaskDraft>
        {
            Task("T1", Base.AddHours(3), Base.AddHours(5)),
            Task("T2", Base.AddHours(5), Base.AddHours(6)),
        };

        var code = Run(new ConstraintContext(), out var evidenceType, out var details, tasks);

        Assert.Equal("PRECEDENCE_WAIT", code);
        Assert.Equal("PRECEDENCE", evidenceType);
        Assert.Contains("evidenceLevel\":\"OBSERVED", details);
    }

    // ── 4. 并列紧因（双因可查）：物料是候选、资源阻挡是主因 ⇒ 两条事实都在证据里 ──
    [Fact(DisplayName = "EX-T04：双因并列 ⇒ 主因=资源阻挡，同时保留物料候选事实（不得只留一条）")]
    public void TwoTightCauses_BothRecorded()
    {
        var ctx = new ConstraintContext();
        // 物料：100 件早在 Base 就齐了 ⇒ 非绑定（候选）
        ctx.MaterialAvailability[7] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 100m, AvailableTime = Base },
        };

        // 资源阻挡：正好压在任务开工窗口 ⇒ 主因
        var blocks = new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>();
        blocks[1] = new List<(ResourceBlock, bool)>
        {
            (new ResourceBlock
            {
                ResourceId = 1,
                StartTime = Base.AddHours(5),
                EndTime = Base.AddHours(6),
                Reason = "外部占用",
                SourceDomainKey = "DOMAIN",
            }, false),
        };

        var tasks = new List<FinalTaskDraft> { Task("T1", Base.AddHours(5), Base.AddHours(6)) };
        var code = new PhaseThreeDiagnostics().DiagnoseDelayReason(
            Demand(10m), tasks, ctx,
            new DomainSolveRequest { PlanningStart = Base },
            PhaseThreeDiagnostics.BuildLockedDraftIdIndex(ctx),
            blocks, out var evidenceType, out var details);

        Assert.Equal("RESOURCE_CAPACITY_WAIT", code);
        Assert.Equal("SHARED_RESOURCE", evidenceType);
        // 主因证据
        Assert.Contains("ResourceId\":1", details);
        // 并列的第二条事实（物料候选，含齐套时点与 slack）
        Assert.Contains("materialCandidate", details);
        Assert.Contains("bindingState\":\"CANDIDATE", details);
    }
}
