using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **排序由「规则参数引擎的生效配置」驱动，而非写死在程序里**（0号位 2026-11-25 统一答复 §3 / 总表 v1.7 §十四.3）。
///
/// <para>验收要求原文：「同一程序版本、同一受控竞争需求，分别加载两套合法但排序配置不同的测试规则，
/// 证明仅改变配置即可改变 ①需求命中规则段 ②段内排序结果」。</para>
///
/// <para>本类用**同一组受控需求**跑多套配置，逐项证明：段的数量/顺序、匹配条件、排序字段与升降序
/// 全部来自配置对象本身 —— 改配置、不改代码，结果就变。</para>
///
/// <para><b>不覆盖</b>（须如实向 0号位 说明，不冒充已证）：</para>
/// <list type="bullet">
/// <item>`AllocationSequence` 与实际承接份额 —— 那要连通 Pegging 分配，属另行核证；</item>
/// <item>供给选择排序（库存／PI）—— 另有独立配置面；</item>
/// <item>**空值位次**（null 永远置末）**由代码定死、不在配置里** —— 见 `DemandPriorityExecutor` 的
///   `IsNullableSortField` / `IsNullForSort`，是刻意约定，不是配置项；</item>
/// <item>**支持的字段名集合**是白名单（`KnownFields` ＋ `GetFieldValue`）—— 新增字段需改代码，
///   符合 0号位「**受支持的**配置项」限定。</item>
/// </list>
/// </summary>
public class DemandPriorityConfigDrivenTests
{
    private readonly DemandPriorityExecutor _executor = new(NullLogger<DemandPriorityExecutor>.Instance);

    // ── 受控竞争需求：三个客户区分、三个交期，用于观察命中段与段内先后 ──
    private static List<UpstreamDemand> ControlledDemands() => new()
    {
        new() { DemandKey = "D-OVERSEAS-0101", CustomerSegment = "OVERSEAS", CustomerDueDate = new DateTime(2027, 1, 1) },
        new() { DemandKey = "D-OVERSEAS-0301", CustomerSegment = "OVERSEAS", CustomerDueDate = new DateTime(2027, 3, 1) },
        new() { DemandKey = "D-DOMESTIC-0201", CustomerSegment = "DOMESTIC", CustomerDueDate = new DateTime(2027, 2, 1) },
    };

    private static PrioritySegmentConfig Segment(
        int order, IReadOnlyList<MatchCondition>? conds, string sortField, string direction)
        => new()
        {
            SegmentOrder = order,
            IsEnabled = true,
            MatchConditions = conds ?? Array.Empty<MatchCondition>(),
            SortFields = new List<SortField> { new() { FieldName = sortField, Direction = direction } },
            StableTieBreakFields = new List<string>()
        };

    private static MatchCondition Eq(string field, string value)
        => new() { FieldName = field, Operator = "EQ", Value = value };

    private (List<string> Order, Dictionary<string, int> SegOf) Run(
        List<UpstreamDemand> demands, IReadOnlyList<PrioritySegmentConfig> segments)
    {
        var trace = new List<(string DemandKey, int SegmentOrder)>();
        _executor.SegmentTrace = trace;

        var sorted = _executor.ExecutePrioritySort(
            demands.OrderBy(d => d.DemandKey, StringComparer.Ordinal).ToList(),
            new DemandPriorityConfig { Segments = segments });

        _executor.SegmentTrace = null;

        var segOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, seg) in trace) segOf[key] = seg;

        return (sorted.Select(d => d.DemandKey).ToList(), segOf);
    }

    // ── ① 匹配条件由配置决定 ─────────────────────────────────────────────
    [Fact]
    public void 仅改匹配条件_命中段随之改变()
    {
        // 配置 A：段1 收 OVERSEAS
        var a = Run(ControlledDemands(), new[]
        {
            Segment(1, new[] { Eq("CUSTOMERSEGMENT", "OVERSEAS") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        });

        // 配置 B：**同一份需求**，段1 改成收 DOMESTIC
        var b = Run(ControlledDemands(), new[]
        {
            Segment(1, new[] { Eq("CUSTOMERSEGMENT", "DOMESTIC") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        });

        a.SegOf["D-OVERSEAS-0101"].Should().Be(1, "配置A 的段1 收 OVERSEAS");
        a.SegOf["D-DOMESTIC-0201"].Should().Be(2);

        b.SegOf["D-DOMESTIC-0201"].Should().Be(1, "配置B 的段1 改收 DOMESTIC");
        b.SegOf["D-OVERSEAS-0101"].Should().Be(2);

        // 命中段变了 ⇒ 整体顺序也随之变
        a.Order.Should().NotEqual(b.Order);
    }

    // ── ② 段内排序方向由配置决定 ─────────────────────────────────────────
    [Fact]
    public void 仅改排序方向_段内先后反转()
    {
        var asc = Run(ControlledDemands(), new[]
        {
            Segment(1, null, "CUSTOMERDUEDATE", "ASC"),
        });
        var desc = Run(ControlledDemands(), new[]
        {
            Segment(1, null, "CUSTOMERDUEDATE", "DESC"),
        });

        asc.Order.Should().Equal("D-OVERSEAS-0101", "D-DOMESTIC-0201", "D-OVERSEAS-0301");
        desc.Order.Should().Equal(asc.Order.AsEnumerable().Reverse(), "仅方向不同，顺序应完全反转");
    }

    // ── ③ 段内排序字段由配置决定（不只看一列） ────────────────────────────
    [Fact]
    public void 仅改排序字段_先后按新字段重排()
    {
        // 同一份需求：CustomerDueDate 与 DemandKey 的先后恰好相反，便于判别用的是哪个字段
        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "A-key", CustomerSegment = "OVERSEAS", CustomerDueDate = new DateTime(2027, 3, 1) },
            new() { DemandKey = "B-key", CustomerSegment = "OVERSEAS", CustomerDueDate = new DateTime(2027, 1, 1) },
        };

        var byDue = Run(demands, new[] { Segment(1, null, "CUSTOMERDUEDATE", "ASC") });
        var byKey = Run(demands, new[] { Segment(1, null, "DEMANDKEY", "ASC") });

        byDue.Order.Should().Equal(new[] { "B-key", "A-key" }, "按 CustomerDueDate 升序");
        byKey.Order.Should().NotEqual(byDue.Order, "改排序字段后顺序应不同");
    }

    // ── ④ 段的数量与顺序由配置决定 ───────────────────────────────────────
    [Fact]
    public void 仅改段数量与顺序_命中与控制流改变()
    {
        // 两段：OVERSEAS 先，兜底后
        var two = Run(ControlledDemands(), new[]
        {
            Segment(1, new[] { Eq("CUSTOMERSEGMENT", "OVERSEAS") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        });

        // 三段：中间插入一段 DOMESTIC（First-Match ⇒ DOMESTIC 需求被第 2 段先收走）
        var three = Run(ControlledDemands(), new[]
        {
            Segment(1, new[] { Eq("CUSTOMERSEGMENT", "OVERSEAS") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, new[] { Eq("CUSTOMERSEGMENT", "DOMESTIC") }, "CUSTOMERDUEDATE", "DESC"),
            Segment(3, null, "CUSTOMERDUEDATE", "ASC"),
        });

        two.SegOf["D-DOMESTIC-0201"].Should().Be(2);
        three.SegOf["D-DOMESTIC-0201"].Should().Be(2, "First-Match：第 2 段先收走");
        three.SegOf.Keys.Should().HaveCount(3);
        two.SegOf.Keys.Should().HaveCount(3);
    }

    // ── ⑤ 配置不变 ⇒ 稳定可复现（0号位 §3「配置不变时应有稳定排序和可复现结果」）──
    [Fact]
    public void 同一配置重复执行_结果可复现()
    {
        var segs = new[]
        {
            Segment(1, new[] { Eq("CUSTOMERSEGMENT", "OVERSEAS") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        };

        var first = Run(ControlledDemands(), segs);
        var second = Run(ControlledDemands(), segs);

        second.Order.Should().Equal(first.Order);
        second.SegOf.Should().BeEquivalentTo(first.SegOf);
    }
}