using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T01 / L1-01｜物料阻挡判据正反例**（1号位，2026-11-14）。
///
/// <para><b>被替换的旧判据</b>：`firstTaskStart &lt; segments.Min(AvailableTime)`。
/// 排程侧 `GetMaterialEarliestTime` 已保证开工不早于物料可用 ⇒ 旧条件在正常结果里恒为假，
/// 物料真正把开工顶后的情形一条都不命中。</para>
///
/// <para><b>新判据</b>：按 `AvailableTime` 升序累计 `Quantity`，取「累计首次 ≥ 本需求 `NetOutputQty`」
/// 的时点作为齐套时点，再与实际首工序开工比较：
/// ① 累计不足 ⇒ `MATERIAL_SHORTAGE` + `bindingState=BINDING`（缺口可量化）；
/// ② 齐套时点 ≈ 实际开工（≤ 1 分钟容差）⇒ `MATERIAL_SHORTAGE` + `bindingState=BINDING`；
/// ③ 齐套时点明显早于实际开工 ⇒ **不判物料**，继续走后续归因。</para>
///
/// 纯内存，不连库、不跑 Phase1。
/// </summary>
public class PhaseThreeMaterialBindingTests
{
    private static readonly DateTime Base = new(2026, 9, 10, 8, 0, 0);

    private static LogicalProductionDemand Demand(decimal netQty, long allocSeq = 7) => new()
    {
        LogicalDemandKey = "D1",
        PlanVersionId = 1,
        DomainKey = "DOMAIN",
        AllocationSequence = allocSeq,
        DemandKey = "ORDER_1_M1_1",
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = netQty,
        PlannedProcessQty = netQty,
        RequiredAvailableTime = Base.Date.AddDays(30),
        DemandSequence = 1
    };

    private static FinalTaskDraft Task(DateTime start, DateTime end) => new()
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
        ResourceId = null          // 非资源 Task：让后续容量/路由/日历分支安全跳过
    };

    private static ConstraintContext Context(params (decimal Qty, DateTime At)[] segments)
    {
        var ctx = new ConstraintContext();
        ctx.MaterialAvailability[7] = segments
            .Select(s => new MaterialAvailabilitySegment { Quantity = s.Qty, AvailableTime = s.At })
            .ToList();
        return ctx;
    }

    private static string Diagnose(
        decimal netQty,
        ConstraintContext ctx,
        List<FinalTaskDraft> tasks,
        out string evidenceType,
        out string extraDetails)
    {
        var diag = new PhaseThreeDiagnostics();
        return diag.DiagnoseDelayReason(
            Demand(netQty),
            tasks,
            ctx,
            new DomainSolveRequest(),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>(),
            out evidenceType,
            out extraDetails);
    }

    [Fact(DisplayName = "物料累计不足本批Q ⇒ MATERIAL_SHORTAGE，bindingState=BINDING 且缺口可量化")]
    public void InsufficientCumulative_ReturnsMaterialShortage_Binding()
    {
        // 供应 20 + 30 = 50，需求 100 ⇒ 累计不足
        var ctx = Context((20m, Base), (30m, Base));

        var code = Diagnose(100m, ctx,
            new List<FinalTaskDraft> { Task(Base.AddHours(5), Base.AddHours(6)) },
            out var evidenceType, out var details);

        Assert.Equal("MATERIAL_SHORTAGE", code);
        Assert.Equal("MATERIAL", evidenceType);
        Assert.Contains("bindingState\":\"BINDING", details);
        Assert.Contains("accumulatedQty\":50", details);
        Assert.Contains("materialReadyAt\":null", details);
    }

    [Fact(DisplayName = "齐套时点即实际开工 ⇒ MATERIAL_SHORTAGE，bindingState=BINDING，slack≈0")]
    public void ReadyAtActualStart_ReturnsMaterialShortage_Binding()
    {
        // 60@08:00 + 60@12:00 = 120 ≥ 100 ⇒ 齐套时点 = 12:00
        var ctx = Context((60m, Base), (60m, Base.AddHours(4)));

        var code = Diagnose(100m, ctx,
            new List<FinalTaskDraft> { Task(Base.AddHours(4), Base.AddHours(5)) },
            out var evidenceType, out var details);

        Assert.Equal("MATERIAL_SHORTAGE", code);
        Assert.Equal("MATERIAL", evidenceType);
        Assert.Contains("bindingState\":\"BINDING", details);
        Assert.Contains("slackMinutes\":0", details);
    }

    [Fact(DisplayName = "物料早于实际开工 10 天 ⇒ 不判物料，继续走后续归因（PRECEDENCE_WAIT）")]
    public void MaterialReadyMuchEarlier_DoesNotReturnMaterial()
    {
        // 100@08:00 全部可用；实际开工在 10 天后 ⇒ 物料不是绑定入边
        var ctx = Context((100m, Base));

        var code = Diagnose(100m, ctx,
            new List<FinalTaskDraft> { Task(Base.AddDays(10), Base.AddDays(10).AddHours(1)) },
            out var evidenceType, out var details);

        Assert.NotEqual("MATERIAL_SHORTAGE", code);
        // EX-T01（2026-11-21 补）：**「最后必要片」判定** —— 多切片时齐套时点取「累计首次够量」那一片。
        // EX-T04（2026-11-20）：本夹具无子件、无同需求串联等待 ⇒ 兜底不再冒充 PRECEDENCE，
        //   证据如实标 `UNRESOLVED`（ReasonCode 仍取 15 码内最近者）。
        Assert.Equal("UNRESOLVED", evidenceType);
        Assert.Equal("PRECEDENCE_WAIT", code);
        Assert.DoesNotContain("bindingState\":\"BINDING", details ?? string.Empty);   // 候选≠绑定：不得声称物料是绑定约束（CANDIDATE 态可留痕）
    }

    // ═══════════════════════════════════════════════════════════════════
    // EX-T01（2026-11-21 补）：**多切片「最后必要片」正反例**
    //   0号位 v1.8 §六 要求「物料各 Slice 时间与累计数量、**最后必要片**、Stage10 齐料的正反例」。
    //   上面用例是单切片；这里用三切片锁「齐套时点 = 累计首次够量那一片」。
    // ═══════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "EX-T01：三切片 30/40/40 需求 100 ⇒ 最后必要片是第 3 片，开工=该片时点")]
    public void MultiSlice_LastNecessarySlice_IsThird()
    {
        var t0 = Base;
        var t1 = Base.AddDays(1);
        var t2 = Base.AddDays(2);

        // 30 + 40 = 70 < 100；再加 40 = 110 ≥ 100 ⇒ 第 3 片(t2)是最后必要片。
        var ctx = Context((30m, t0), (40m, t1), (40m, t2));
        var code = Diagnose(100m, ctx,
            new List<FinalTaskDraft> { Task(t2, t2.AddHours(1)) },
            out _, out var details);

        Assert.Equal("MATERIAL_SHORTAGE", code);
        Assert.Contains("bindingState\":\"BINDING", details);
        Assert.Contains($"materialReadyAt\":\"{t2:O}", details);   // 不是 t0/t1
        Assert.Contains("accumulatedQty\":110", details);          // 累计到够量为止的总量
    }

    [Fact(DisplayName = "EX-T01：三切片 30/40/40 需求 70 ⇒ 最后必要片是第 2 片，第 3 片不参与")]
    public void MultiSlice_SmallerDemand_LastNecessarySlice_IsSecond()
    {
        var t0 = Base;
        var t1 = Base.AddDays(1);
        var t2 = Base.AddDays(2);

        // 需求 70：30 + 40 = 70 ⇒ 第 2 片(t1)就够，第 3 片(t2)不是必要片。
        var ctx = Context((30m, t0), (40m, t1), (40m, t2));
        var code = Diagnose(70m, ctx,
            new List<FinalTaskDraft> { Task(t1, t1.AddHours(1)) },
            out _, out var details);

        Assert.Equal("MATERIAL_SHORTAGE", code);
        Assert.Contains($"materialReadyAt\":\"{t1:O}", details);
        Assert.DoesNotContain($"materialReadyAt\":\"{t2:O}", details);
        Assert.Contains("accumulatedQty\":70", details);   // 恰好够，不多累计
    }
}