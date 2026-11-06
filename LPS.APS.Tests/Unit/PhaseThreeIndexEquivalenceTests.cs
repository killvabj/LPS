using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-06 Phase3 首批整改（0号位《Phase3首批性能代码整改明确指令 v1.0》）第1步 / 第2步的**等价性**回归。
///
/// <para>【为什么这样测】指令 §四 要求「边界样本：锁定集合0/1/多条，同一DraftId重复；单资源多Block、
/// 跨域/上游Block同时存在、重叠及紧邻窗口、第一命中证据一致」。索引化最容易悄悄改变的是**命中顺序**
/// （哪一条 Block 被选中），而不是「有没有命中」，所以本文件对新旧算法做**逐例差分**，
/// 断言的不只是 bool，还包括**被选中的那一条 Block 的引用身份**。</para>
///
/// <para>【旧算法从哪来】<see cref="LegacyHasResourceBlockOverlap"/> 是整改前
/// `PhaseThreeDiagnostics.HasResourceBlockOverlap` 的逐字副本（仅改方法名、去掉纯观测计数器），
/// 作为差分基准。新算法直接调**生产代码**，不做任何改写。</para>
///
/// <para>【覆盖不到的地方】Phase5 第二次诊断的索引重建时序由 `PhaseThreeDiagnostics.Diagnose`
/// 内部保证（每次调用各建一份），本文件只测两个建索引方法的纯函数行为。</para>
/// </summary>
public sealed class PhaseThreeIndexEquivalenceTests
{
    // ═══════════════════════════════════════════════════════════════════
    // 第2步：资源阻挡块索引 —— 新旧逐例差分
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>整改前实现（逐字副本）：外部块在前、上游块在后拼成一个 List，再 Task×Block 嵌套扫。</summary>
    private static bool LegacyHasResourceBlockOverlap(
        List<FinalTaskDraft> demandTasks,
        DomainSolveRequest request,
        out ResourceBlock? matchedBlock,
        out bool crossDomain)
    {
        matchedBlock = null;
        crossDomain = false;

        var blocks = new List<(ResourceBlock Block, bool Cross)>();

        if (request.CandidateContext?.ExternalDomainResourceBlocks != null)
        {
            foreach (var b in request.CandidateContext.ExternalDomainResourceBlocks)
            {
                blocks.Add((b, !string.IsNullOrEmpty(b.SourceDomainKey)));
            }
        }

        if (request.UpstreamDomainResourceBlocks != null)
        {
            foreach (var b in request.UpstreamDomainResourceBlocks)
            {
                blocks.Add((b, !string.IsNullOrEmpty(b.SourceDomainKey)));
            }
        }

        foreach (var task in demandTasks)
        {
            if (task.ResourceId is not int taskResourceId) continue;

            foreach (var (block, cross) in blocks)
            {
                if (block.ResourceId == taskResourceId &&
                    task.PlannedStartTime < block.EndTime && block.StartTime < task.PlannedEndTime)
                {
                    matchedBlock = block;
                    crossDomain = cross;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>新旧两路各跑一次，逐项断言：命中与否、命中块的**引用身份**、crossDomain。</summary>
    private static void AssertSameAsLegacy(
        List<FinalTaskDraft> demandTasks,
        DomainSolveRequest request,
        string scenario)
    {
        var legacyHas = LegacyHasResourceBlockOverlap(
            demandTasks, request, out var legacyBlock, out var legacyCross);

        var index = PhaseThreeDiagnostics.BuildResourceBlockIndex(request);
        var newHas = PhaseThreeDiagnostics.HasResourceBlockOverlap(
            demandTasks, index, out var newBlock, out var newCross);

        Assert.True(legacyHas == newHas,
            $"[{scenario}] 命中与否不一致：旧={legacyHas} 新={newHas}");
        Assert.True(legacyCross == newCross,
            $"[{scenario}] crossDomain 不一致：旧={legacyCross} 新={newCross}");
        Assert.True(ReferenceEquals(legacyBlock, newBlock),
            $"[{scenario}] 命中的 Block 不是同一条：旧={Describe(legacyBlock)} 新={Describe(newBlock)}");

        static string Describe(ResourceBlock? b)
            => b is null
                ? "<null>"
                : $"ResourceId={b.ResourceId} [{b.StartTime:O}~{b.EndTime:O}) dom={b.SourceDomainKey ?? "<null>"}";
    }

    private static FinalTaskDraft TaskAt(int? resourceId, DateTime start, DateTime end)
        => new FinalTaskDraft
        {
            FinalDraftId = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            PlannedStartTime = start,
            PlannedEndTime = end,
        };

    private static ResourceBlock BlockAt(int resourceId, DateTime start, DateTime end, string? domainKey = null)
        => new ResourceBlock
        {
            ResourceId = resourceId,
            StartTime = start,
            EndTime = end,
            SourceDomainKey = domainKey,
        };

    private static DomainSolveRequest RequestWith(
        IReadOnlyList<ResourceBlock> external,
        IReadOnlyList<ResourceBlock>? upstream)
        => new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = new DateTime(2026, 1, 1),
            PlanningEnd = new DateTime(2026, 1, 31),
            CandidateContext = new CandidateContext { ExternalDomainResourceBlocks = external },
            UpstreamDomainResourceBlocks = upstream,
        };

    private static readonly DateTime T0 = new DateTime(2026, 1, 5, 8, 0, 0);

    [Fact(DisplayName = "第2步 · 无 Task / 无 Block：不命中")]
    public void Empty_NoHit()
    {
        AssertSameAsLegacy(new List<FinalTaskDraft>(), RequestWith(Array.Empty<ResourceBlock>(), null), "空 Task + 空块");
        AssertSameAsLegacy(
            new List<FinalTaskDraft> { TaskAt(1, T0, T0.AddHours(1)) },
            RequestWith(Array.Empty<ResourceBlock>(), Array.Empty<ResourceBlock>()),
            "有 Task + 空块");
        AssertSameAsLegacy(
            new List<FinalTaskDraft>(),
            RequestWith(new[] { BlockAt(1, T0, T0.AddHours(1)) }, null),
            "空 Task + 有块");
    }

    [Fact(DisplayName = "第2步 · 非资源 Task（ResourceId=null）全部跳过")]
    public void NullResourceTasks_Skipped()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskAt(null, T0, T0.AddHours(2)),
            TaskAt(null, T0, T0.AddHours(2)),
        };
        // ResourceId=0 的块也不能被 null Task 命中
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(0, T0, T0.AddHours(1)) }, null), "null Task vs 资源0块");
    }

    [Fact(DisplayName = "第2步 · 不同资源不命中；同资源命中")]
    public void DifferentResource_NoHit_SameResource_Hit()
    {
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(2)) };
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(8, T0, T0.AddHours(1)) }, null), "不同资源");
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1)) }, null), "同资源");
    }

    [Fact(DisplayName = "第2步 · 半开区间：紧邻不算重叠，重叠 1 tick 算")]
    public void HalfOpenBoundary()
    {
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0.AddHours(1), T0.AddHours(2)) };

        // Block 结束 == Task 开始 ⇒ 不重叠
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1)) }, null), "紧邻（块在左）");
        // Block 开始 == Task 结束 ⇒ 不重叠
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0.AddHours(2), T0.AddHours(3)) }, null), "紧邻（块在右）");
        // 重叠 1 tick
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1).AddTicks(1)) }, null), "重叠 1 tick（左）");
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0.AddHours(2).AddTicks(-1), T0.AddHours(3)) }, null), "重叠 1 tick（右）");
        // 完全包含 / 被包含
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(5)) }, null), "块包住 Task");
        AssertSameAsLegacy(
            tasks, RequestWith(new[] { BlockAt(7, T0.AddHours(1).AddMinutes(10), T0.AddHours(1).AddMinutes(20)) }, null), "块被 Task 包住");
    }

    [Fact(DisplayName = "第2步 · 单资源多 Block：命中顺序必须与原 List 拼接顺序一致")]
    public void MultiBlock_PriorityOrderPreserved()
    {
        // 故意让「时间上更晚」的块排在前面：旧算法选它，新算法也必须选它（证明不是按时间排序）
        var late = BlockAt(7, T0.AddHours(4), T0.AddHours(5));
        var early = BlockAt(7, T0, T0.AddHours(1));
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(6)) };

        AssertSameAsLegacy(tasks, RequestWith(new[] { late, early }, null), "同资源多块：顺序优先");
    }

    [Fact(DisplayName = "第2步 · 外部块优先于上游块（同资源同窗口）")]
    public void ExternalBlockBeatsUpstream()
    {
        var external = BlockAt(7, T0, T0.AddHours(1), "DOM_EXT");
        var upstream = BlockAt(7, T0, T0.AddHours(1), "DOM_UP");
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(2)) };

        AssertSameAsLegacy(tasks, RequestWith(new[] { external }, new[] { upstream }), "外部块在前");
        // 反过来：外部列表为空时，上游块成为唯一候选
        AssertSameAsLegacy(tasks, RequestWith(Array.Empty<ResourceBlock>(), new[] { upstream }), "只有上游块");
    }

    [Fact(DisplayName = "第2步 · 同资源两块都重叠：必须取第一条")]
    public void TwoOverlappingBlocks_FirstWins()
    {
        var first = BlockAt(7, T0, T0.AddHours(3));
        var second = BlockAt(7, T0.AddHours(1), T0.AddHours(2));
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0.AddHours(1).AddMinutes(30), T0.AddHours(4)) };

        AssertSameAsLegacy(tasks, RequestWith(new[] { first, second }, null), "两块都重叠");
    }

    [Fact(DisplayName = "第2步 · 重复 Block（同一实例出现两次）：取第一次出现")]
    public void DuplicateSameInstance_FirstOccurrence()
    {
        var b = BlockAt(7, T0, T0.AddHours(1), "DOM_A");
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(2)) };

        AssertSameAsLegacy(tasks, RequestWith(new[] { b, b }, null), "同列表重复实例");
        AssertSameAsLegacy(tasks, RequestWith(new[] { b }, new[] { b }), "跨两类列表重复实例");
    }

    [Fact(DisplayName = "第2步 · 多 Task：外层 Task 顺序决定命中哪一个")]
    public void MultipleTasks_OuterOrderWins()
    {
        var blockFor1 = BlockAt(1, T0, T0.AddHours(1));
        var blockFor2 = BlockAt(2, T0, T0.AddHours(1));

        // Task(2) 在前 ⇒ 应先命中资源2的块；Task(1) 在前 ⇒ 应先命中资源1的块
        var task2First = new List<FinalTaskDraft> { TaskAt(2, T0, T0.AddHours(2)), TaskAt(1, T0, T0.AddHours(2)) };
        var task1First = new List<FinalTaskDraft> { TaskAt(1, T0, T0.AddHours(2)), TaskAt(2, T0, T0.AddHours(2)) };

        AssertSameAsLegacy(task2First, RequestWith(new[] { blockFor1, blockFor2 }, null), "Task2 在前");
        AssertSameAsLegacy(task1First, RequestWith(new[] { blockFor1, blockFor2 }, null), "Task1 在前");
    }

    [Fact(DisplayName = "第2步 · 前面 Task 不命中、后面 Task 命中")]
    public void EarlierTaskMisses_LaterTaskHits()
    {
        var tasks = new List<FinalTaskDraft>
        {
            TaskAt(7, T0.AddHours(10), T0.AddHours(11)),   // 不重叠
            TaskAt(8, T0, T0.AddHours(1)),                // 资源不匹配
            TaskAt(7, T0.AddHours(2), T0.AddHours(3)),    // 命中
        };
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0.AddHours(2), T0.AddHours(4)) }, null), "后位命中");
    }

    [Fact(DisplayName = "第2步 · SourceDomainKey 空 / 空白 / 有值 三态下 crossDomain 一致")]
    public void CrossDomainFlag()
    {
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(2)) };

        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1), null) }, null), "null");
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1), "") }, null), "空串");
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1), " ") }, null), "空白串（非空 ⇒ 算跨域）");
        AssertSameAsLegacy(tasks, RequestWith(new[] { BlockAt(7, T0, T0.AddHours(1), "DOM_X") }, null), "有值");
    }

    [Fact(DisplayName = "第2步 · 无 CandidateContext（CandidateContext=null）+ 有上游块")]
    public void NullCandidateContext_UpstreamOnly()
    {
        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = new DateTime(2026, 1, 1),
            PlanningEnd = new DateTime(2026, 1, 31),
            CandidateContext = null,
            UpstreamDomainResourceBlocks = new[] { BlockAt(7, T0, T0.AddHours(1), "DOM_UP") },
        };
        var tasks = new List<FinalTaskDraft> { TaskAt(7, T0, T0.AddHours(2)) };

        AssertSameAsLegacy(tasks, request, "CandidateContext=null");
    }

    [Fact(DisplayName = "第2步 · 定位随机扫描 5000 例：新旧结果逐例相同")]
    public void RandomSweep_MatchesLegacy()
    {
        // 固定种子 ⇒ 可重放。日期固定，不依赖当前时钟。
        var rnd = new Random(20261106);
        var baseTime = new DateTime(2026, 1, 5, 0, 0, 0);
        var domainKeys = new string?[] { null, "", "DOM_A", "DOM_B" };

        for (var iter = 0; iter < 5000; iter++)
        {
            var taskCount = rnd.Next(0, 9);
            var externalCount = rnd.Next(0, 7);
            var upstreamCount = rnd.Next(0, 7);

            var tasks = new List<FinalTaskDraft>(taskCount);
            for (var i = 0; i < taskCount; i++)
            {
                // ResourceId 取 null / 0..3（含 0 与越界资源）
                int? rid = rnd.Next(0, 6) == 0 ? null : rnd.Next(0, 4);
                var s = baseTime.AddMinutes(rnd.Next(0, 600));
                var e = s.AddMinutes(rnd.Next(0, 240));
                tasks.Add(TaskAt(rid, s, e));
            }

            var external = new List<ResourceBlock>(externalCount);
            for (var i = 0; i < externalCount; i++)
            {
                var s = baseTime.AddMinutes(rnd.Next(0, 600));
                var e = s.AddMinutes(rnd.Next(0, 240));
                external.Add(BlockAt(rnd.Next(0, 4), s, e, domainKeys[rnd.Next(domainKeys.Length)]));
            }

            var upstream = new List<ResourceBlock>(upstreamCount);
            for (var i = 0; i < upstreamCount; i++)
            {
                var s = baseTime.AddMinutes(rnd.Next(0, 600));
                var e = s.AddMinutes(rnd.Next(0, 240));
                upstream.Add(BlockAt(rnd.Next(0, 4), s, e, domainKeys[rnd.Next(domainKeys.Length)]));
            }

            var request = RequestWith(external, upstream);
            AssertSameAsLegacy(tasks, request, $"随机第 {iter} 例");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 第1步：锁定表索引 —— 0 / 1 / 多条 / 同 DraftId 重复
    // ═══════════════════════════════════════════════════════════════════

    private static ConstraintContext ContextWith(params (string DraftId, string Stage, string Op)[] locked)
    {
        var ctx = new ConstraintContext();
        foreach (var (draftId, stage, op) in locked)
        {
            ctx.LockedTasks[(draftId, stage, op)] = new LockedTaskConstraint { DraftId = draftId };
        }
        return ctx;
    }

    [Fact(DisplayName = "第1步 · 锁定集合 0 条 ⇒ 索引为空")]
    public void LockIndex_Empty()
    {
        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(ContextWith());
        Assert.Empty(index);
    }

    [Fact(DisplayName = "第1步 · 锁定集合 1 条 ⇒ 命中")]
    public void LockIndex_Single()
    {
        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(ContextWith(("D1", "S1", "OP10")));
        Assert.Single(index);
        Assert.Contains("D1", index);
    }

    [Fact(DisplayName = "第1步 · 锁定集合多条 ⇒ 全部命中")]
    public void LockIndex_Multiple()
    {
        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(
            ContextWith(("D1", "S1", "OP10"), ("D2", "S1", "OP10"), ("D3", "S2", "OP20")));
        Assert.Equal(3, index.Count);
        Assert.Contains("D1", index);
        Assert.Contains("D2", index);
        Assert.Contains("D3", index);
    }

    [Fact(DisplayName = "第1步 · 同 DraftId 多工序（不同 Stage/Op）⇒ 仍只算命中，不重复")]
    public void LockIndex_DuplicateDraftId()
    {
        // 同一 DraftId 有两条锁定（复合键不同）——原 FirstOrDefault 扫到任一条即算「有锁」
        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(
            ContextWith(("D1", "S1", "OP10"), ("D1", "S2", "OP10"), ("D2", "S1", "OP20")));
        Assert.Equal(2, index.Count);
        Assert.Contains("D1", index);
    }

    [Fact(DisplayName = "第1步 · 序数比较：大小写不同不算命中（与 string== 一致）")]
    public void LockIndex_OrdinalNotCaseInsensitive()
    {
        var index = PhaseThreeDiagnostics.BuildLockedDraftIdIndex(ContextWith(("DEMAND-1", "S1", "OP10")));
        Assert.Contains("DEMAND-1", index);
        Assert.DoesNotContain("demand-1", index);
        Assert.DoesNotContain("Demand-1", index);
    }
}