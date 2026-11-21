using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **EX-T02（0号位 v1.8 §六）**：Phase5 最终「真紧约束」归因 ——
/// 要求「资源 / 锁 / 日历 / Setup / 物料 **五类**的最终真紧约束，及『曾紧后松』对照」。
///
/// <para>本文件锁**判定优先级与证据类别**（同一夹具叠加多种成因，看最终归因是否落在该落的地方）：</para>
/// <list type="number">
///   <item>锁定 + 缺料 ⇒ **锁**优先（`FROZEN_ZONE_LOCK`）；</item>
///   <item>同域资源阻挡 ⇒ `RESOURCE_CAPACITY_WAIT` + `evidenceType=SHARED_RESOURCE`；</item>
///   <item>跨域资源阻挡 ⇒ `RESOURCE_CAPACITY_WAIT` + `evidenceType=CROSS_DOMAIN_RESOURCE_BLOCK` + 来源键；</item>
///   <item>仅 Setup 边际 ⇒ `RESOURCE_CAPACITY_WAIT` + `evidenceType=SETUP`。</item>
/// </list>
///
/// <para>纯内存，不连库、不跑 Phase1。</para>
/// </summary>
public sealed class ExT02FinalBindingPriorityTests
{
    private static readonly DateTime Base = new(2026, 11, 21, 8, 0, 0);
    private const string Stage = "S1";
    private const string Op = "OP10";

    private static LogicalProductionDemand Demand() => new()
    {
        LogicalDemandKey = "D1",
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = 7,
        DemandKey = "D1",
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = 10m,
        PlannedProcessQty = 10m,
        StartStageCode = Stage,
        RequiredAvailableTime = Base.AddHours(-1),   // 必然延期
    };

    private static FinalTaskDraft Task(DateTime start, DateTime end, decimal setup = 0m, int resourceId = 1) => new()
    {
        FinalDraftId = "T1",
        SourceDraftId = "D1",
        MaterialId = 1,
        FactoryId = 1,
        ResourceId = resourceId,
        StageCode = Stage,
        OperationCode = Op,
        Quantity = 10m,
        PlannedProcessQty = 10m,
        SetupTime = setup,
        PlannedStartTime = start,
        PlannedEndTime = end,
    };

    private static string Run(
        ConstraintContext ctx,
        List<FinalTaskDraft> tasks,
        Dictionary<int, List<(ResourceBlock Block, bool Cross)>>? blocks,
        out string evidenceType,
        out string details)
        => new PhaseThreeDiagnostics().DiagnoseDelayReason(
            Demand(),
            tasks,
            ctx,
            new DomainSolveRequest { PlanningStart = Base },
            // 与生产调用同口径：锁定索引由 ctx.LockedTasks 建（传空集合 ⇒ 锁永远不命中）。
            PhaseThreeDiagnostics.BuildLockedDraftIdIndex(ctx),
            blocks ?? new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>(),
            out evidenceType,
            out details);

    // ── 1. 锁 优先于 物料 ──
    [Fact(DisplayName = "EX-T02：锁定且缺料 ⇒ 最终归因取锁（FROZEN_ZONE_LOCK）")]
    public void LockWinsOverMaterial()
    {
        var ctx = new ConstraintContext();
        // 缺料：只到 1 < 10
        ctx.MaterialAvailability[7] = new List<MaterialAvailabilitySegment>
        {
            new() { Quantity = 1m, AvailableTime = Base },
        };
        // 锁定：该需求在锁定表里
        ctx.LockedTasks[("D1", Stage, Op)] = new LockedTaskConstraint
        {
            DraftId = "D1", ResourceId = 1, ConstraintType = "FROZEN",
            LockedStart = Base, LockedEnd = Base.AddHours(1),
        };

        var code = Run(ctx, new List<FinalTaskDraft> { Task(Base.AddHours(5), Base.AddHours(6)) },
            null, out var evidenceType, out _);

        Assert.Equal("FROZEN_ZONE_LOCK", code);
        Assert.Equal("LOCK", evidenceType);
    }

    // ── 2. 同域资源阻挡 ──
    [Fact(DisplayName = "EX-T02：同域资源阻挡 ⇒ RESOURCE_CAPACITY_WAIT + SHARED_RESOURCE")]
    public void SameDomainBlock_EvidenceSharedResource()
    {
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

        var code = Run(new ConstraintContext(),
            new List<FinalTaskDraft> { Task(Base.AddHours(5), Base.AddHours(6)) },
            blocks, out var evidenceType, out var details);

        Assert.Equal("RESOURCE_CAPACITY_WAIT", code);
        Assert.Equal("SHARED_RESOURCE", evidenceType);
        Assert.Contains("ResourceId\":1", details);
    }

    // ── 3. 跨域资源阻挡 ──
    [Fact(DisplayName = "EX-T02：跨域资源阻挡 ⇒ CROSS_DOMAIN_RESOURCE_BLOCK + 来源 Domain/PV")]
    public void CrossDomainBlock_EvidenceWithSourceKeys()
    {
        var blocks = new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>();
        blocks[1] = new List<(ResourceBlock, bool)>
        {
            (new ResourceBlock
            {
                ResourceId = 1,
                StartTime = Base.AddHours(5),
                EndTime = Base.AddHours(6),
                Reason = "跨域占用",
                SourceDomainKey = "OTHER",
                SourcePlanVersionId = 42,
            }, true),
        };

        var code = Run(new ConstraintContext(),
            new List<FinalTaskDraft> { Task(Base.AddHours(5), Base.AddHours(6)) },
            blocks, out var evidenceType, out var details);

        Assert.Equal("RESOURCE_CAPACITY_WAIT", code);
        Assert.Equal("CROSS_DOMAIN_RESOURCE_BLOCK", evidenceType);
        Assert.Contains("SourceDomainKey\":\"OTHER", details);
        Assert.Contains("SourcePlanVersionId\":42", details);
    }

    // ── 4. 仅 Setup 边际 ──
    [Fact(DisplayName = "EX-T02：去掉 Setup 即不延期 ⇒ SETUP 证据、归资源等待")]
    public void SetupMarginal_EvidenceSetup()
    {
        // 完工 07:00(含 2h Setup)；去掉 Setup ⇒ 05:00，早于判定期上界 ⇒ Setup 是边际成因。
        var tasks = new List<FinalTaskDraft>
        {
            Task(Base.AddHours(-1), Base.AddHours(1), setup: 120m),
        };

        var code = Run(new ConstraintContext(), tasks, null, out var evidenceType, out _);

        Assert.Equal("RESOURCE_CAPACITY_WAIT", code);
        Assert.Equal("SETUP", evidenceType);
    }

    // ── 5. 曾紧后松：输入里有阻挡块，但最终 Task 已避开它 ⇒ 不得再拿旧块归因 ──
    [Fact(DisplayName = "EX-T02「曾紧后松」：阻挡块存在但最终 Task 不重叠 ⇒ 不报资源阻挡")]
    public void BlockWasPresentButAvoided_NotAttributed()
    {
        // 输入块：08:00-09:00（曾紧）
        var blocks = new Dictionary<int, List<(ResourceBlock Block, bool Cross)>>();
        blocks[1] = new List<(ResourceBlock, bool)>
        {
            (new ResourceBlock
            {
                ResourceId = 1,
                StartTime = Base,
                EndTime = Base.AddHours(1),
                Reason = "曾被占用",
                SourceDomainKey = "DOMAIN",
            }, false),
        };

        // 最终 Task：10:00-11:00（后松，已避开）
        var tasks = new List<FinalTaskDraft> { Task(Base.AddHours(2), Base.AddHours(3)) };
        var code = Run(new ConstraintContext(), tasks, blocks, out var evidenceType, out var details);

        Assert.NotEqual("RESOURCE_CAPACITY_WAIT", code);
        Assert.NotEqual("SHARED_RESOURCE", evidenceType);
        Assert.NotEqual("CROSS_DOMAIN_RESOURCE_BLOCK", evidenceType);
        Assert.DoesNotContain("SourcePlanVersionId", details);
    }
}
