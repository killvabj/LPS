using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位《类别整改强制执行专项函 v1.0》§二·03**：合法 LT-only Stage 必须能形成可信时间。
///
/// <para>缺陷（本次实测）：`TryAdvanceByWorkHours` 对 `IsLeadTimeOnly=true` 的 Stage
/// **故意不查部门**（这类 Stage 在部门字典里本就没有部门），但随后 `TryResolveWindowFactoryId`
/// 没有部门就解析不出厂 ⇒ `STAGE_FACTORY_UNKNOWN` ⇒ 整条无 Routing 需求落 `P2_NO_ROUTING`。
/// 代码注释早已写明「本层 MISSING ⇒ 允许继承上级已认证工作窗口」，但**从未实现**。
/// 实测候选：FRL 3,517 + CYL 823 条（`表A_03_四门禁复拆` F6）。</para>
///
/// <para>本类锁两件事（纯内存，不触库）：
/// ① 传入链上已解析的厂 ⇒ LT-only 段按**工厂级窗口**推进成功，并回传该厂；
/// ② 没有继承厂 ⇒ 仍然失败（不得回落需求厂、不得假造时间）。</para>
/// </summary>
public class StageTimingLeadTimeOnlyFactoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0);

    private static DomainSolveRequest BuildRequest()
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = T0,
            PlanningEnd = T0.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>(),
            DepartmentWorkWindows = new[]
            {
                // 厂级行（DepartmentId = null）：LT-only Stage 专用
                new DepartmentWorkWindow { FactoryId = 7, DepartmentId = null, Start = T0, End = T0.AddHours(8) }
            }
        };

    private static LogicalProductionDemand BuildDemand()
        => new()
        {
            LogicalDemandKey = "D-FINAL",
            PlanVersionId = 1L,
            DomainKey = "D",
            DemandKey = "D-FINAL",
            DemandSequence = 1,
            AllocationSequence = 1,
            MaterialId = 990001,
            FactoryId = 1,          // 需求厂故意 ≠ 继承厂，验证"不回落需求厂"
            FactoryCode = "F1",
            StartStageCode = "FINAL",
            NetOutputQty = 1,
            PlannedProcessQty = 1,
            UOM = "PS"
        };

    [Fact]
    public void LT_only段_有继承厂时按工厂级窗口推进成功()
    {
        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHoursWithFactory(
            BuildRequest(), BuildDemand(), "FINAL", T0, hours: 2m,
            out var end, out var reason, out var usedFactory,
            isLeadTimeOnly: true, inheritedFactoryId: 7);

        Assert.True(ok, reason);
        Assert.Equal(7, usedFactory);
        Assert.Equal(T0.AddHours(2), end);
    }

    [Fact]
    public void LT_only段_无继承厂时仍失败且不回落需求厂()
    {
        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHoursWithFactory(
            BuildRequest(), BuildDemand(), "FINAL", T0, hours: 2m,
            out _, out var reason, out _,
            isLeadTimeOnly: true);

        Assert.False(ok);
        Assert.Equal("STAGE_FACTORY_UNKNOWN", reason);
    }
}
