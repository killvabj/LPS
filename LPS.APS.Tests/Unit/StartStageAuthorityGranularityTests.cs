using System;
using System.Collections.Generic;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 起点赋值的**粒度**（0号位 2026-11-27 统一审核回复 §3 Q2／Q3／Q5）。
///
/// <para>裁决：有 PI 以 5号位 权威 `NextOperationContext`／切片为准；
/// 多位置多切片时**不得取首**，取不到/不一致就留空交 Fail-Closed。</para>
///
/// <para>本类钉死三件事：①起点取自 NextContext 而非 `Position.StageCode`（物理所在≠下一执行）；
/// ②同一 PI 多片起点不一致 ⇒ 不填；③恰好一个起点才填。</para>
/// </summary>
public class StartStageAuthorityGranularityTests
{
    private static LogicalProductionDemand Demand(string pi) => new()
    {
        LogicalDemandKey = "K1",
        PlanVersionId = 1,
        DomainKey = "D",
        MaterialId = 1,
        NetOutputQty = 1m,
        PlannedProcessQty = 1m,
        ProductionInstructionNo = pi,
        StartStageCode = string.Empty
    };

    private static NextOperationContextDto Ctx(string? stage) => new() { StartStageCode = stage };

    private static void Run(LogicalProductionDemand d, params NextOperationContextDto[] ctxs)
    {
        var pos = new Dictionary<string, ProductionInstructionPositionResult>(StringComparer.Ordinal)
        {
            ["PI1"] = new()
            {
                ProductionInstructionNo = "PI1",
                NextOperationContexts = ctxs,
                Positions = new List<PositionSlice>
                {
                    // 物理所在 Stage：故意放一个与 NextContext 不同的值，用来证明"没拿它当起点"
                    new() { PositionType = PositionType.STAGE_WAITING, StageCode = "PHYSICAL_STAGE", Quantity = 1m }
                }
            }
        };
        PeggingOrchestrator.FillPiStartStageCodesFromPositions(new[] { d }, pos);
    }

    [Fact(DisplayName = "Q2：起点取 NextOperationContext，不取物理所在 StageCode")]
    public void UsesNextContextStage_NotPhysicalStage()
    {
        var d = Demand("PI1");
        Run(d, Ctx("NEXT_STAGE"));

        d.StartStageCode.Should().Be("NEXT_STAGE",
            "物理所在 Stage 不等于下一执行 Stage；权威起点只能取 NextOperationContext");
    }

    [Fact(DisplayName = "Q3/Q5：同一 PI 多片起点不一致 ⇒ 不填（不得取首）")]
    public void MultipleDistinctSlices_NotFilled()
    {
        var d = Demand("PI1");
        Run(d, Ctx("STAGE_A"), Ctx("STAGE_B"));

        d.StartStageCode.Should().BeEmpty("多片起点不一致时不得取首，留空交 Fail-Closed");
    }

    [Fact(DisplayName = "多片但起点一致 ⇒ 视为唯一，可填")]
    public void MultipleSlices_SameStage_Filled()
    {
        var d = Demand("PI1");
        Run(d, Ctx("STAGE_A"), Ctx("STAGE_A"));

        d.StartStageCode.Should().Be("STAGE_A", "去重后唯一 ⇒ 内容一致，可视为同一起点");
    }

    [Fact(DisplayName = "Q2：Position 有物理 Stage 但无 NextContext ⇒ 仍留空，不得回退")]
    public void NoNextContext_NotFilledEvenIfPositionHasStage()
    {
        var d = Demand("PI1");
        Run(d);   // 无 NextContext

        d.StartStageCode.Should().BeEmpty("没有权威下一执行 Stage 就不能拿物理 Stage 顶上");
    }
}
