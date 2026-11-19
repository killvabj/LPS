using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **P0-04 多路由数量闭合正反例**（0号位 2026-11-11 §4 C01/C02 及反向破坏）。
///
/// <para>三层闭合的第 8 项就是要解「同一需求拆批、各批选不同合法 Path、工序集合互斥」时的
/// 旧按工序聚合误判。本文件直接复刻 C01（EB001/Path2=25.5 + EB002/Path3=25.5，工序互斥）与
/// 其反向破坏（某 Path 缺一道必需工序必须拒）。纯内存，直调 <c>ValidateHardResultForTest</c>。</para>
/// </summary>
public sealed class P0FourMultiRoutingClosureTests
{
    private const int MaterialId = 1;
    private const string Stage = "STAGE1";
    private const string Route = "RT";

    private static LogicalProductionDemand Demand(decimal qty) => new()
    {
        LogicalDemandKey = "D1",
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = 1L,
        DemandKey = "D1",
        MaterialId = MaterialId,
        FactoryId = 1,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        DemandSequence = 1,
        StartStageCode = Stage,
        // 自由需求：不固定 Route/Path（由各执行批各自选 Path）。
        RouteCode = null,
        PathId = null,
    };

    private static FinalTaskDraft T(string id, string op, decimal qty, long pathId, string seb)
        => new()
        {
            FinalDraftId = id,
            SourceDraftId = "D1",
            MaterialId = MaterialId,
            FactoryId = 1,
            StageCode = Stage,
            OperationCode = op,
            RouteCode = Route,
            PathId = pathId,
            StageExecutionBatchDraftKey = seb,
            ExecutionBatchDraftKey = seb,
            Quantity = qty,
            PlannedProcessQty = qty,
            SetupTime = 0m,
            PlannedStartTime = new DateTime(2026, 11, 10, 8, 0, 0),
            PlannedEndTime = new DateTime(2026, 11, 10, 9, 0, 0),
        };

    /// <summary>Path2 = {M202, M203}、Path3 = {M302, M303}，工序集合互斥。</summary>
    private static RoutingGraph PathGraph(long pathId, params string[] ops)
    {
        var g = new RoutingGraph();
        foreach (var op in ops)
        {
            var key = OperationNodeKey.Of(Stage, op);
            g.Operations[key] = new OperationNode
            {
                OperationCode = op, StageCode = Stage, StandardDuration = 1m,
                OperationPlanningMode = "FINITE_RESOURCE", RouteCode = Route, PathId = (int)pathId, ProductionDepartmentId = 1,
            };
            g.RootOperations.Add(key);
        }
        return g;
    }

    private static ConstraintContext Context()
    {
        var c = new ConstraintContext();
        c.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
        {
            [RoutePathKey.Of(Route, 2)] = PathGraph(2, "M202", "M203"),
            [RoutePathKey.Of(Route, 3)] = PathGraph(3, "M302", "M303"),
        };
        return c;
    }

    private static (bool Valid, string Error) Run(List<FinalTaskDraft> tasks, decimal netQty)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            new List<AllocationTaskShare>(),
            new List<FinalTaskPeggingDraft>(),
            new DomainSolveRequest { LogicalProductionDemands = new List<LogicalProductionDemand> { Demand(netQty) } },
            Context(),
            new HashSet<string>(),
            new Dictionary<string, List<(string, decimal)>>());

    // C01：需求 51，EB001/Path2 = M202/M203 各 25、EB002/Path3 = M302/M303 各 26 ⇒ 通过。
    //
    // ⚠ **候选勘误（2026-11-19，待 0号位 确认，非悄然改预期）**：原 C01 夹具为 `25.5 + 25.5`，
    //   与 0号位 v1.2 §二.4 逐字「`51→25.5+25.5` 不能以新增可执行批身份通过；`51→25+26` 为整数正例」
    //   **直接冲突**。本处按 v1.2 改为 `25 + 26`，保留 C01 原意（两批走不同 Path、工序互斥、合计 51）。
    //   若 0号位 裁定原 C01 预期仍有效，则需同时撤销整数门禁 —— 二者不可并存，已随进度件提请。
    [Fact(DisplayName = "C01 两批25/26走不同Path工序互斥 通过（按v1.2整数口径，候选勘误待确认）")]
    public void C01_TwoBatchesDifferentPath_MutuallyExclusiveOps_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "M202", 25m, 2, "EB001"), T("T2", "M203", 25m, 2, "EB001"),
            T("T3", "M302", 26m, 3, "EB002"), T("T4", "M303", 26m, 3, "EB002"),
        };
        var (valid, err) = Run(tasks, netQty: 51m);
        Assert.True(valid, "两批各 25/26 走不同 Path、工序互斥，按批净产出合计 51，必须通过。" + err);
    }

    // C02：需求 6，两批分别 3 走不同 Path ⇒ 通过。
    [Fact(DisplayName = "C02 两批3走不同Path 通过")]
    public void C02_TwoBatchesDifferentPath_SmallQty_ShouldPass()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "M202", 3m, 2, "EB001"), T("T2", "M203", 3m, 2, "EB001"),
            T("T3", "M302", 3m, 3, "EB002"), T("T4", "M303", 3m, 3, "EB002"),
        };
        var (valid, err) = Run(tasks, netQty: 6m);
        Assert.True(valid, "两批各 3 走不同 Path，合计 6，必须通过。" + err);
    }

    // 反向破坏：Path2 工序集合本应是 {M202, M203}，但该批缺了 M203 ⇒ 必须拒。
    [Fact(DisplayName = "多路径某Path缺一道工序 拒")]
    public void MultiPath_MissingOpInOnePath_ShouldReject()
    {
        var tasks = new List<FinalTaskDraft>
        {
            T("T1", "M202", 25.5m, 2, "EB001"), // 缺 M203
            T("T3", "M302", 25.5m, 3, "EB003"), T("T4", "M303", 25.5m, 3, "EB003"),
        };
        var (valid, err) = Run(tasks, netQty: 51m);
        Assert.False(valid, "Path2 缺必需工序 M203，必须被 BATCH_OP_MISSING 拒绝。");
    }
}