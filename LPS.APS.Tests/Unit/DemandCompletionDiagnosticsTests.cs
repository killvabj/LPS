using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using static LPS.APS.Application.Services.PeggingOrchestrator;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Demand 级完成时间事实的只读诊断 ＋ 消费闸门测试
/// （0号位 2026-10-25《四份回执联合核证》§二-5「`DemandCompletions` 整族零消费」／§五 P0-B「2端消费不可用标记」）。
///
/// 断言的正是本改动**声明的目的**（不是「跑绿即有效」）：
///   ① **闸门只排 `false`** —— `null`（未标注，向后兼容）与 `true`（可信）放行，`false`（缺 LT／缺合法 WORK）排除；
///   ② **只读** —— `Summarize` 不改动入参集合，不产生任何 Task 语义；
///   ③ 计数如实 —— 当前生产侧尚未置值（恒 `null`）时 `CertainNullCount == FactCount`，不得被算成「不可信」；
///   ④ 样本上限 10 条（抽样非全量清单）。
///
/// 被测对象 `DemandCompletionDiagnostics` / `DemandCompletionSummary` 都是 `internal`，经 `InternalsVisibleTo` 对测试可见。
/// ⚠ 只覆盖内存对象，不触库、不跑 Run。
/// </summary>
public class DemandCompletionDiagnosticsTests
{
    private static DemandCompletionFact Fact(
        string key,
        bool? certain,
        params string[] segmentLevels)
        => new()
        {
            LogicalDemandKey = key,
            CompletionTimeCertain = certain,
            StageTimings = segmentLevels
                .Select(level => new StageTimingSegmentFact { MatchLevel = level })
                .ToList()
        };

    // ── ① 闸门：只排 false ─────────────────────────────────────────────

    [Fact]
    public void 闸门_全未标注时全部放行_按现状语义()
    {
        var facts = new[] { Fact("D1", null), Fact("D2", null), Fact("D3", null) };

        var passed = DemandCompletionDiagnostics.CertainOnly(facts);

        passed.Should().HaveCount(3, "null = 未标注 ⇒ 向后兼容按现状放行（不得因未置值就全部丢弃）");
    }

    [Fact]
    public void 闸门_排除不可信_保留可信与未标注()
    {
        var facts = new[]
        {
            Fact("CERTAIN", true),
            Fact("UNSURE", false),
            Fact("UNANNOTATED", null)
        };

        var passed = DemandCompletionDiagnostics.CertainOnly(facts);

        passed.Select(f => f.LogicalDemandKey).Should().BeEquivalentTo(
            new[] { "CERTAIN", "UNANNOTATED" },
            "唯一排除项是 CompletionTimeCertain == false（Core 契约「2号位 读侧义务」）");
    }

    [Fact]
    public void 闸门_null入参返回空集不抛()
    {
        DemandCompletionDiagnostics.CertainOnly(null).Should().BeEmpty();
    }

    // ── ② 只读 ────────────────────────────────────────────────────────

    [Fact]
    public void 汇总_不改动入参集合()
    {
        var facts = new List<DemandCompletionFact> { Fact("D1", false), Fact("D2", null) };
        var before = facts.ToList();

        DemandCompletionDiagnostics.Summarize(facts);

        facts.Should().BeEquivalentTo(before, "Summarize 是只读诊断，不得增删入参元素");
    }

    // ── ③ 计数如实 ────────────────────────────────────────────────────

    [Fact]
    public void 汇总_生产现状_全部未标注时不误判为不可信()
    {
        var facts = Enumerable.Range(1, 7).Select(i => Fact($"D{i}", null)).ToList();

        var summary = DemandCompletionDiagnostics.Summarize(facts);

        summary.FactCount.Should().Be(7);
        summary.CertainNullCount.Should().Be(7, "生产侧尚未置值 ⇒ 全部落 null 支");
        summary.CertainFalseCount.Should().Be(0, "`null` 不是 `false`，不得计入不可信");
        summary.CertainTrueCount.Should().Be(0);
        summary.AnyUncertain.Should().BeFalse();
    }

    [Fact]
    public void 汇总_三态分列()
    {
        var facts = new[]
        {
            Fact("A", true), Fact("B", false), Fact("C", false), Fact("D", null)
        };

        var summary = DemandCompletionDiagnostics.Summarize(facts);

        summary.FactCount.Should().Be(4);
        summary.CertainTrueCount.Should().Be(1);
        summary.CertainFalseCount.Should().Be(2);
        summary.CertainNullCount.Should().Be(1);
        summary.AnyUncertain.Should().BeTrue();
    }

    [Fact]
    public void 汇总_空集合与null入参均返回Empty()
    {
        DemandCompletionDiagnostics.Summarize(null).Should().Be(DemandCompletionSummary.Empty);
        DemandCompletionDiagnostics.Summarize(Array.Empty<DemandCompletionFact>())
            .Should().Be(DemandCompletionSummary.Empty);
    }

    [Fact]
    public void 汇总_空需求键单独计数()
    {
        var facts = new[] { Fact(string.Empty, null), Fact("D1", null) };

        var summary = DemandCompletionDiagnostics.Summarize(facts);

        summary.FactCount.Should().Be(2);
        summary.EmptyDemandKeyCount.Should().Be(1, "空键是独立诊断项，不得混进三态计数");
    }

    // ── ④ 样本上限 ────────────────────────────────────────────────────

    [Fact]
    public void 汇总_不可信样本最多10条()
    {
        var facts = Enumerable.Range(1, 25).Select(i => Fact($"D{i:D2}", false)).ToList();

        var summary = DemandCompletionDiagnostics.Summarize(facts);

        summary.CertainFalseCount.Should().Be(25, "计数是全量");
        summary.UncertainSamples.Should().HaveCount(10, "样本是抽样非全量清单");
    }

    // ── Stage 段诊断（纯时间节点，非 Task） ───────────────────────────

    [Fact]
    public void 汇总_统计段数与缺LT段()
    {
        var facts = new[]
        {
            Fact("D1", null, "DEPT_EXACT", "STAGE_LEADTIME_MISSING"),
            Fact("D2", null, "GLOBAL_STAGE_DEFAULT")
        };

        var summary = DemandCompletionDiagnostics.Summarize(facts);

        summary.StageTimingSegmentCount.Should().Be(3);
        summary.MissingStageLeadTimeSegmentCount.Should().Be(1, "MatchLevel 区分大小写无关，缺 LT 段单独可数");
    }

    [Fact]
    public void 汇总_无StageTimings时不计数不抛()
    {
        var fact = new DemandCompletionFact { LogicalDemandKey = "D1", StageTimings = null! };

        var summary = DemandCompletionDiagnostics.Summarize(new[] { fact });

        summary.FactCount.Should().Be(1);
        summary.StageTimingSegmentCount.Should().Be(0);
    }
}
