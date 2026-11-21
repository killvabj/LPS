using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T01 收尾（2026-11-16）｜物料「非生效候选」证据留痕**（1号位）。
///
/// <para><b>背景</b>：`DiagnoseDelayReason` 的物料分支只有 BINDING（真实缺料 / 齐套即开工）会 `return`；
/// 候选（齐套时点明显早于实际开工）原来**静默跳过**，导致「最终原因 = 前序/其它，但物料其实已齐」
/// 这个并列事实在出口上查不到。整改后，候选物料以 <c>materialCandidate</c> 键写进
/// 最终原因的 <c>EvidenceJson.details</c>，不抢归因、不新增 ReasonCode。</para>
///
/// <para>纯内存、直调 <c>DiagnoseDelayReason</c>，不跑 Phase1、不连库。</para>
/// </summary>
public class PhaseThreeMaterialCandidateEvidenceTests
{
    private static readonly DateTime Base = new(2026, 9, 10, 8, 0, 0);

    private static LogicalProductionDemand Demand(decimal netQty) => new()
    {
        LogicalDemandKey = "D1",
        PlanVersionId = 1,
        DomainKey = "DOMAIN",
        AllocationSequence = 7,
        DemandKey = "ORDER_1_M1_1",
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = netQty,
        PlannedProcessQty = netQty,
        RequiredAvailableTime = Base.Date.AddDays(30),
        DemandSequence = 1
    };

    // ResourceId = null ⇒ 容量/路由/日历分支全部安全跳过，最终落兜底 PRECEDENCE_WAIT。
    private static FinalTaskDraft NonResourceTask(DateTime start, DateTime end) => new()
    {
        FinalDraftId = "T1",
        SourceDraftId = "D1",
        MaterialId = 1,
        FactoryId = 1,
        StageCode = "S1",
        OperationCode = "OP10",
        Quantity = 1m,
        PlannedProcessQty = 1m,
        PlannedStartTime = start,
        PlannedEndTime = end,
        SetupTime = 0m,
        ResourceId = null
    };

    private static ConstraintContext Context()
    {
        var ctx = new ConstraintContext();
        // 100 @ Base：量足够（累计 100 ≥ 需求 100）且齐套时点明显早于实际开工 ⇒ 候选。
        ctx.MaterialAvailability[7] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 100m, AvailableTime = Base }
        };
        return ctx;
    }

    [Fact(DisplayName = "物料齐套早于开工（非生效候选）⇒ 最终原因证据里带 materialCandidate/CANDIDATE")]
    public void CandidateMaterial_RecordedInFinalReasonEvidence()
    {
        var diag = new PhaseThreeDiagnostics();
        var code = diag.DiagnoseDelayReason(
            Demand(100m),
            new List<FinalTaskDraft> { NonResourceTask(Base.AddHours(5), Base.AddHours(6)) },
            Context(),
            new DomainSolveRequest(),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>(),
            out var evidenceType,
            out var extraDetails);

        // 物料是候选、不抢归因 ⇒ 落到兜底。
        // EX-T04（2026-11-20）：兜底**不再无条件冒充 PRECEDENCE**——本夹具无子件、无同需求串联等待，
        //   ⇒ 证据如实标 `UNRESOLVED`/`evidenceLevel=NONE`（ReasonCode 仍取 15 码内最近者）。
        Assert.Equal("PRECEDENCE_WAIT", code);
        Assert.Equal("UNRESOLVED", evidenceType);

        // 主断言（整改前红）：候选物料的事实必须留在最终原因的证据里。
        Assert.Contains("materialCandidate", extraDetails);
        Assert.Contains("bindingState\":\"CANDIDATE", extraDetails);
        Assert.Contains("materialReadyAt", extraDetails);
        // 齐套 Base(08:00) vs 实际开工 Base+5h(13:00) ⇒ slack = 300 分钟。
        Assert.Contains("slackMinutes\":300", extraDetails);
    }
}