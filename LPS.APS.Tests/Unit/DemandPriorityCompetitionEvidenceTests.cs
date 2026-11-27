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
/// 排序「竞争证据」中**可由配置表达**的三项（0号位 11-20 排序证据要求 / 总表 v1.18 §十三.2）：
/// ③ `PROTECTED` 必要保护份额、⑤ Round3 孤儿 SH 先于孤儿 MTS、⑦ 段2 组A（库存消耗日 ≤ 0）。
///
/// <para><b>本类证明什么</b>：这三项的先后关系**由配置决定、不是代码写死**——
/// 同一组需求、换一套合法配置，先后就反过来。这正是 0号位 要求的「改配置即改结果」。</para>
///
/// <para><b>本类**不**证明什么</b>（如实标注，不冒充）：</para>
/// <list type="bullet">
/// <item>**实际承接份额**是否被保住 —— 那是供给池按序分配的结果，不在这层；
///   本类只证「保护单排到了前面」，两者不能互相替代；</item>
/// <item>Round3 的**分池本身**（哪些单算孤儿）—— 由 `ClassifyCompetitionRound` 决定，
///   本类直接给它孤儿形态的输入，不重复验证分池；</item>
/// <item>真实发布配置（`RuleSet478`）的读取 —— 本类用内存配置对象，属执行器层证据。</item>
/// </list>
/// </summary>
public class DemandPriorityCompetitionEvidenceTests
{
    private readonly DemandPriorityExecutor _executor = new(NullLogger<DemandPriorityExecutor>.Instance);

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

    private static MatchCondition Cond(string field, string op, string value)
        => new() { FieldName = field, Operator = op, Value = value };

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

    // ═══════════ ③ PROTECTED 必要保护份额（排序层）═══════════

    private static List<UpstreamDemand> ProtectionDemands() => new()
    {
        // DemandKey 字典序：A(未保护) 在 B(保护) 前 —— 若不按配置，A 会先出
        new() { DemandKey = "A-UNPROTECTED", ProtectionStatus = "UNPROTECTED", CustomerDueDate = new DateTime(2027, 5, 1) },
        new() { DemandKey = "B-PROTECTED",   ProtectionStatus = "PROTECTED",   CustomerDueDate = new DateTime(2027, 6, 1) },
    };

    [Fact]
    public void 保护单前置_由配置决定_保护单排前()
    {
        var (order, segOf) = Run(ProtectionDemands(), new[]
        {
            Segment(1, new[] { Cond("PROTECTIONSTATUS", "EQ", "PROTECTED") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        });

        order.Should().Equal(new[] { "B-PROTECTED", "A-UNPROTECTED" }, because: "配置把 PROTECTED 段排在前面 ⇒ 保护单必须先出，而不是按 DemandKey 字典序");
        segOf["B-PROTECTED"].Should().Be(1);
        segOf["A-UNPROTECTED"].Should().Be(2);
    }

    [Fact]
    public void 保护单前置_换一套配置即反转_证明不是写死()
    {
        // 同一组需求、仅把「谁在前」的配置换掉 ⇒ 顺序必须反过来
        var (order, segOf) = Run(ProtectionDemands(), new[]
        {
            Segment(1, new[] { Cond("PROTECTIONSTATUS", "EQ", "UNPROTECTED") }, "CUSTOMERDUEDATE", "ASC"),
            Segment(2, null, "CUSTOMERDUEDATE", "ASC"),
        });

        order.Should().Equal(new[] { "A-UNPROTECTED", "B-PROTECTED" }, because: "配置换成未保护段在前 ⇒ 顺序反转；若顺序没变，说明保护优先是写死的");
        segOf["A-UNPROTECTED"].Should().Be(1);
    }

    // ═══════════ ⑤ Round3 孤儿 SH 先于孤儿 MTS ═══════════

    private static List<UpstreamDemand> OrphanDemands() => new()
    {
        // 孤儿 MTS（P 行、生产指示）：DemandKey 字典序在 SH 之前
        new() { DemandKey = "A-ORPHAN-MTS", OrderType = "PRODUCTION_INSTRUCTION", CustomerSegment = null },
        // 孤儿 SH（P 行、厂间出荷）
        new() { DemandKey = "B-ORPHAN-SH",  OrderType = "SALES_ORDER",          CustomerSegment = "INTER_FACTORY" },
    };

    [Fact]
    public void 孤儿SH先于孤儿MTS_由配置决定()
    {
        var (order, segOf) = Run(OrphanDemands(), new[]
        {
            Segment(1, new[] { Cond("CUSTOMERSEGMENT", "EQ", "INTER_FACTORY") }, "DEMANDKEY", "ASC"),
            Segment(2, null, "DEMANDKEY", "ASC"),
        });

        order.Should().Equal(new[] { "B-ORPHAN-SH", "A-ORPHAN-MTS" }, because: "配置把厂间出荷段排在前面 ⇒ 孤儿 SH 先于孤儿 MTS；默认字典序本会把 MTS 排前");
        segOf["B-ORPHAN-SH"].Should().Be(1);
        segOf["A-ORPHAN-MTS"].Should().Be(2);
    }

    [Fact]
    public void 孤儿SH优先_换配置即反转_证明不是写死()
    {
        var (order, _) = Run(OrphanDemands(), new[]
        {
            Segment(1, new[] { Cond("ORDERTYPE", "EQ", "PRODUCTION_INSTRUCTION") }, "DEMANDKEY", "ASC"),
            Segment(2, null, "DEMANDKEY", "ASC"),
        });

        order.Should().Equal(new[] { "A-ORPHAN-MTS", "B-ORPHAN-SH" }, because: "换配置后顺序反转 ⇒ 「孤儿 SH 先」是配置效果，不是代码常量");
    }

    // ═══════════ ⑦ 段2 组A：库存消耗日 ≤ 0 ═══════════

    private static List<UpstreamDemand> StockDayDemands() => new()
    {
        new() { DemandKey = "A-ZERO",  StockConsumeDay = 0m },      // 真·最紧急（ZERO_STOCK）
        new() { DemandKey = "B-HALF",  StockConsumeDay = 0.5m },
        new() { DemandKey = "C-NULL",  StockConsumeDay = null },     // 无可信日均，不得被当作 0 命中
    };

    [Fact]
    public void 组A_库存消耗日小于等于0_只命中真0且不误纳无日均()
    {
        var (order, segOf) = Run(StockDayDemands(), new[]
        {
            Segment(1, new[] { Cond("STOCKCONSUMEDAY", "LTE", "0") }, "STOCKCONSUMEDAY", "ASC"),
            Segment(2, null, "STOCKCONSUMEDAY", "ASC"),
        });

        segOf["A-ZERO"].Should().Be(1, "库存消耗日 = 0 属组A（ZERO_STOCK），必须命中段1");
        segOf["B-HALF"].Should().Be(2, "0.5 天不满足 ≤0，不得进组A");
        segOf["C-NULL"].Should().Be(2,
            "**无日均 ≠ 0**：空值遇范围比较一律不命中（fail-closed），否则会被误当最紧急提前排");

        order.First().Should().Be("A-ZERO", "组A 单独前置，必须排最前");
    }

    [Fact]
    public void 组A_样本为正例_不是零命中空转()
    {
        // 反证「组A 永远 0 命中」的怀疑：只要有一条真 0，就必须被命中
        var (_, segOf) = Run(StockDayDemands(), new[]
        {
            Segment(1, new[] { Cond("STOCKCONSUMEDAY", "LTE", "0") }, "STOCKCONSUMEDAY", "ASC"),
        });

        segOf.Count.Should().Be(3, "三条需求都应留下段轨迹（未命中记 0 段兜底）");
        segOf["A-ZERO"].Should().Be(1);
        segOf.ContainsKey("B-HALF").Should().BeTrue();
    }
}