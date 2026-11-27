using System.Collections.Generic;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 起点赋值顺序（0号位 2026-11-27 统一审核回复 §3 第 1 问）：
/// 「Routing 首工序赋值早于 PI Position 补空，是否阻止权威执行起点生效」。
///
/// <para><b>裁决</b>：有真实 PI 的需求以 5号位 权威 Position/切片为准；
/// Routing 用于合法阶段内的工序，**不得覆盖真实续排起点**。</para>
///
/// <para>本类钉死：Routing 回填**跳过有 PI 的需求**（把它们留给 Position 填），
/// 且**不得误伤无 PI 的合法规划需求**（那批仍按 BOM/Routing 链首展开）。</para>
/// </summary>
public class StartStageAuthorityOrderTests
{
    private static RoutingOperation Op(int materialId, string opCode, string stage)
        => new() { MaterialId = materialId, OperationCode = opCode, StageCode = stage };

    private static LogicalProductionDemand Demand(int materialId, string? pi)
        => new()
        {
            LogicalDemandKey = $"PV_{materialId}_{pi ?? "NOPI"}",
            PlanVersionId = 1,
            DomainKey = "D",
            MaterialId = materialId,
            NetOutputQty = 1m,
            PlannedProcessQty = 1m,
            ProductionInstructionNo = pi,
            StartStageCode = string.Empty
        };

    private static PeggingResultVoucher Voucher(params LogicalProductionDemand[] demands)
        => new() { PlanVersionId = 1, LogicalProductionDemands = new List<LogicalProductionDemand>(demands) };

    [Fact(DisplayName = "有 PI 的需求：Routing 不得占位（留给权威 Position）")]
    public void DemandWithPi_NotFilledByRouting()
    {
        var v = Voucher(Demand(1, "PI123"));
        PeggingOrchestrator.FillStartStageCodes(v, new List<RoutingOperation> { Op(1, "M101", "BJ_ASSY") },
            new List<RoutingDependency>(), new HashSet<string> { "PI123" });

        v.LogicalProductionDemands[0].StartStageCode.Should().BeEmpty(
            "有 PI 时起点必须由 5号位 权威 Position/切片决定；Routing 先填会把它挡住");
    }

    [Fact(DisplayName = "无 PI 的合法规划需求：仍按 Routing 源结点填（不得误伤）")]
    public void DemandWithoutPi_FilledByRouting()
    {
        var v = Voucher(Demand(1, null));
        PeggingOrchestrator.FillStartStageCodes(v, new List<RoutingOperation> { Op(1, "M101", "BJ_ASSY") },
            new List<RoutingDependency>());

        v.LogicalProductionDemands[0].StartStageCode.Should().Be("BJ_ASSY",
            "无 PI 的需求按合法 BOM/Routing 链首展开，本步必须照旧生效");
    }

    [Fact(DisplayName = "同物料一有 PI 一无 PI：只填空的那个，互不影响")]
    public void SameMaterial_Mixed_OnlyNonPiFilled()
    {
        var withPi = Demand(1, "PI1");
        var noPi = Demand(1, null);
        var v = Voucher(withPi, noPi);

        PeggingOrchestrator.FillStartStageCodes(v, new List<RoutingOperation> { Op(1, "M101", "BJ_ASSY") },
            new List<RoutingDependency>(), new HashSet<string> { "PI1" });

        withPi.StartStageCode.Should().BeEmpty("有 PI ⇒ 不出自 Routing");
        noPi.StartStageCode.Should().Be("BJ_ASSY", "无 PI ⇒ 照旧填");
    }

    [Fact(DisplayName = "PI 号不是真生产指示（如 SH 出荷号）⇒ 仍由 Routing 填")]
    public void PiNumberNotReal_StillFilledByRouting()
    {
        var v = Voucher(Demand(1, "SH123"));
        PeggingOrchestrator.FillStartStageCodes(v, new List<RoutingOperation> { Op(1, "M101", "BJ_ASSY") },
            new List<RoutingDependency>(), new HashSet<string> { "PI_REAL_OTHER" });

        v.LogicalProductionDemands[0].StartStageCode.Should().Be("BJ_ASSY",
            "ProductionInstructionNo 非空 ≠ 真生产指示；Round2 源厂履约需求带的是 SH 出荷号，不应被 Q1 误挡");
    }
}
