using System.Collections.Generic;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A/B 连续份额的路径身份（`RouteCode`／`PathId`）回填口径。
///
/// <para>0号位 2026-10-22 §五(一)／2026-11-27 §3：缺真路径的片**不得**填 `DEFAULT / 1`，
/// 由 1号位 硬闸 Fail Closed。但**载荷里该物料只有一条路径**时，路径身份是可确定的 ⇒ 应回填。</para>
///
/// <para>本类钉死：唯一路径回填；多条不填；零条不填。</para>
/// </summary>
public class ContinuationPathIdentityFillTests
{
    private static LogicalProductionDemand Cont(int materialId) => new()
    {
        LogicalDemandKey = $"PV_WO{materialId}",
        PlanVersionId = 1,
        DomainKey = "D",
        MaterialId = materialId,
        NetOutputQty = 1m,
        PlannedProcessQty = 1m,
        IsContinuation = true,
        RouteCode = null,
        PathId = null
    };

    private static RoutingOperation Op(int materialId, string route, int pathId)
        => new() { MaterialId = materialId, OperationCode = "M101", StageCode = "BJ_MACH", RouteCode = route, PathId = pathId };

    private static void Run(PeggingResultVoucher v, params RoutingOperation[] ops)
        => PeggingOrchestrator.DiagnoseContinuationRouteIdentities(v, new List<RoutingOperation>(ops));

    [Fact(DisplayName = "载荷只有一条路径 ⇒ 回填 RouteCode/PathId（可确定，不是猜）")]
    public void SingleRoute_Filled()
    {
        var d = Cont(1);
        Run(new PeggingResultVoucher { PlanVersionId = 1, LogicalProductionDemands = new List<LogicalProductionDemand> { d } },
            Op(1, "NC,清洗,精修", 1));

        d.RouteCode.Should().Be("NC,清洗,精修");
        d.PathId.Should().Be(1);
    }

    [Fact(DisplayName = "载荷多条路径 ⇒ 不回填（交 1号位 Fail Closed）")]
    public void MultipleRoutes_NotFilled()
    {
        var d = Cont(1);
        Run(new PeggingResultVoucher { PlanVersionId = 1, LogicalProductionDemands = new List<LogicalProductionDemand> { d } },
            Op(1, "R1", 1), Op(1, "R2", 2));

        d.RouteCode.Should().BeNull("多条路径无法确定用了哪条");
        d.PathId.Should().BeNull("不得填 DEFAULT/1 冒充真值");
    }

    [Fact(DisplayName = "物料无路由节点 ⇒ 不回填")]
    public void NoRouting_NotFilled()
    {
        var d = Cont(1);
        Run(new PeggingResultVoucher { PlanVersionId = 1, LogicalProductionDemands = new List<LogicalProductionDemand> { d } });

        d.RouteCode.Should().BeNull();
        d.PathId.Should().BeNull();
    }

    [Fact(DisplayName = "已带真值的片不被覆盖")]
    public void AlreadyReal_NotOverwritten()
    {
        var d = Cont(1);
        d.RouteCode = "EXISTING";
        d.PathId = 9;
        Run(new PeggingResultVoucher { PlanVersionId = 1, LogicalProductionDemands = new List<LogicalProductionDemand> { d } },
            Op(1, "OTHER", 3));

        d.RouteCode.Should().Be("EXISTING");
        d.PathId.Should().Be(9);
    }
}
