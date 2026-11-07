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
/// 3号位 2026-11-07《null 双缺陷 + DNF 段2 + CustomerDueDate 装载分叉》三项转办件的回归钉：
///
/// <list type="bullet">
///   <item><b>A-1 空值排序</b>：`null` 必须**全向置末**。原实现只用 `MaxValue` 哨兵，
///     只在 ASC 生效；DESC 下哨兵成最大值 ⇒ null 被顶到最前。</item>
///   <item><b>A-2 空值 × 范围比较</b>：字段无值时 `LT/LTE/GT/GTE` **一律不命中**。
///     原实现空值返 `""` ⇒ 落 `string.Compare("", 日期串) &lt; 0` ⇒ `LTE 某日` 恒真、
///     `GT 某日` 恒假（「无法判定」被当成「满足」）。</item>
///   <item><b>B DNF 条件组</b>：`1∧2∧(3∨4)` 必须在**生产执行器**（`DemandPriorityExecutor`）
///     里真求值，而不是只在仅测试使用的 `DemandPriorityMatcher` 里。</item>
/// </list>
///
/// 验收锚点直接取 3号位 转办件给的正/反例，数值逐字一致。
/// </summary>
public class DemandPriorityNullAndDnfTests
{
    private readonly DemandPriorityExecutor _executor = new(NullLogger<DemandPriorityExecutor>.Instance);

    // ── 组装修补 ──

    private static PrioritySegmentConfig Segment(
        int order,
        IReadOnlyList<MatchCondition>? flat = null,
        IReadOnlyList<PriorityConditionGroup>? groups = null,
        IReadOnlyList<SortField>? sorts = null) => new()
    {
        SegmentOrder = order,
        IsEnabled = true,
        MatchConditions = flat ?? [],
        MatchConditionGroups = groups ?? [],
        SortFields = sorts ?? [],
        StableTieBreakFields = [],
    };

    private static DemandPriorityConfig Config(params PrioritySegmentConfig[] segments) => new()
    {
        Segments = segments.ToList()
    };

    // ═══════════════════════════════════════════════════════════════════════════
    // A-1：null 排序全向置末
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "A-1 验收正例：StockConsumeDay DESC 下 null 必须仍在 A(1) 之后，不得被顶到最前")]
    public void A1_库存消耗日降序_空值仍置末()
    {
        var config = Config(Segment(1,
            sorts: [new SortField { FieldName = "STOCKCONSUMEDAY", Direction = "DESC" }]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "A", StockConsumeDay = 1m },
            new() { DemandKey = "B", StockConsumeDay = null },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // 现值（修复前）是 [B,A]：哨兵 decimal.MaxValue 在 DESC 下最大 ⇒ 反被排第一。
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "A", "B" },
            because: "「无可信日均」不得在降序下被当成最紧急；null 必须全向置末");
    }

    [Fact(DisplayName = "A-1 反向：ASC 下 null 仍置末（存量行为不得回退）")]
    public void A1_库存消耗日升序_空值置末()
    {
        var config = Config(Segment(1,
            sorts: [new SortField { FieldName = "STOCKCONSUMEDAY", Direction = "ASC" }]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "B", StockConsumeDay = null },
            new() { DemandKey = "A", StockConsumeDay = 1m },
            new() { DemandKey = "C", StockConsumeDay = 0m },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "C", "A", "B" },
            because: "0（ZERO_STOCK）最紧急、1 次之、null（无可信日均）置末");
    }

    [Fact(DisplayName = "A-1 覆盖面：日期/数值五个可空字段在 DESC 下均置末")]
    public void A1_五个可空字段降序均置末()
    {
        foreach (var field in new[] { "DUEDATE", "ISSUEDATE", "CUSTOMERDUEDATE", "QUANTITY", "STOCKCONSUMEDAY" })
        {
            var config = Config(Segment(1, sorts: [new SortField { FieldName = field, Direction = "DESC" }]));

            var demands = new List<UpstreamDemand>
            {
                new() { DemandKey = "NULL", DueDate = null, IssueDate = null, CustomerDueDate = null, Quantity = null, StockConsumeDay = null },
                new() { DemandKey = "HAS", DueDate = new DateTime(2026, 11, 10), IssueDate = new DateTime(2026, 11, 10), CustomerDueDate = new DateTime(2026, 11, 10), Quantity = 5m, StockConsumeDay = 5m },
            };

            _executor.ExecutePrioritySort(demands, config).Select(d => d.DemandKey)
                .Should().Equal(new[] { "HAS", "NULL" }, because: $"{field} 降序时空值不得置前");
        }
    }

    [Fact(DisplayName = "A-1 边界：多排序键时，前键的空值秩只作用在本键区间内")]
    public void A1_多排序键_空值秩不串键()
    {
        var config = Config(Segment(1, sorts:
        [
            new SortField { FieldName = "ORDERTYPE", Direction = "ASC" },
            new SortField { FieldName = "QUANTITY", Direction = "DESC" },
        ]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "P-null", OrderType = "PRODUCTION_INSTRUCTION", Quantity = null },
            new() { DemandKey = "S-null", OrderType = "SALES_ORDER", Quantity = null },
            new() { DemandKey = "P-9", OrderType = "PRODUCTION_INSTRUCTION", Quantity = 9m },
            new() { DemandKey = "S-9", OrderType = "SALES_ORDER", Quantity = 9m },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // 先按 OrderType 升序（PRODUCTION_INSTRUCTION < SALES_ORDER），再在各自组内按数量 DESC、null 置末
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "P-9", "P-null", "S-9", "S-null" });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // A-2：空值 × 范围比较 ⇒ 不命中
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "A-2 验收反例：CustomerDueDate=null 对 LTE 某日 必须不命中")]
    public void A2_空客户交期_小于等于不命中()
    {
        var config = Config(Segment(1, flat: [new MatchCondition
        {
            FieldName = "CUSTOMERDUEDATE",
            Operator = "LTE",
            Value = "2026-11-30"
        }]));

        var demands = new List<UpstreamDemand>
        {
            // 修复前：GetFieldValue 返 ""、string.Compare("", "2026-11-30") < 0 ⇒ 命中
            new() { DemandKey = "NULL", CustomerDueDate = null },
            new() { DemandKey = "IN", CustomerDueDate = new DateTime(2026, 11, 10) },
            new() { DemandKey = "OUT", CustomerDueDate = new DateTime(2026, 12, 10) },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // 段内只有 "IN"；其余未命中落兜底按 DemandKey 升序 ⇒ [IN, NULL, OUT]
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "IN", "NULL", "OUT" },
            because: "空值不参与大小比较；未命中按 DemandKey ASC 兜底，NULL 与 OUT 同属未命中");
    }

    [Fact(DisplayName = "A-2 四个范围算子：空值一律不命中，只让有值且符合条件的进段")]
    public void A2_空值对任一范围算子均不命中()
    {
        // 段内配 QUANTITY DESC 作排序键，使「命中集合」与「兜底集合」在输出顺序上可区分：
        //   命中段：先按数量降序；兜底段：按 DemandKey ASC。
        foreach (var op in new[] { "LT", "LTE", "GT", "GTE" })
        {
            var config = Config(
                Segment(1,
                    flat: [new MatchCondition { FieldName = "DUEDATE", Operator = op, Value = "2026-11-30" }],
                    sorts: [new SortField { FieldName = "QUANTITY", Direction = "DESC" }]),
                Segment(2, flat: []));   // 空条件兜底段，必须在段序末

            var demands = new List<UpstreamDemand>
            {
                new() { DemandKey = "NULL",  DueDate = null,                   Quantity = 1m },
                new() { DemandKey = "EARLY", DueDate = new DateTime(2026, 11, 1),  Quantity = 2m },
                new() { DemandKey = "LATE",  DueDate = new DateTime(2026, 12, 31), Quantity = 3m },
            };

            var sorted = _executor.ExecutePrioritySort(demands, config);

            // 命中项在前（段1），未命中项 {另一个有值项, NULL} 落兜底段按 DemandKey ASC
            var expected = op is "LT" or "LTE"
                ? new[] { "EARLY", "LATE", "NULL" }
                : new[] { "LATE", "EARLY", "NULL" };

            sorted.Select(d => d.DemandKey).Should().Equal(expected,
                because: $"{op} 下 NULL 必须落兜底段（未命中），不得被空串字符串比较误判为命中");
        }
    }

    [Fact(DisplayName = "A-2 不误伤：EQ / IN 对空值的既有语义不变（空值不等于任何非空值）")]
    public void A2_EQ与IN语义不变()
    {
        var config = Config(
            Segment(1, flat: [new MatchCondition { FieldName = "CUSTOMERSEGMENT", Operator = "EQ", Value = "JAPAN" }]),
            Segment(2, flat: [new MatchCondition { FieldName = "CUSTOMERTIER", Operator = "IN", Value = "VIP,GENERAL" }]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "J", CustomerSegment = "JAPAN", CustomerTier = null },
            new() { DemandKey = "X", CustomerSegment = null, CustomerTier = "VIP" },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // J 命中段1；X 未命中段1、段2 的 IN 因 CustomerTier 有值而命中 ⇒ J 先于 X
        sorted.Select(d => d.DemandKey).Should().Equal("J", "X");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // B：DNF 条件组（组内 AND、组间 OR）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>段2 契约：`OrderType=生产指示 ∧ MaterialCode=FINAL* ∧ (库存消耗≤0 ∨ IssueDate&lt;TODAY-30)`。</summary>
    private static PriorityConditionGroup GroupA() => new()
    {
        Conditions =
        [
            new MatchCondition { FieldName = "ORDERTYPE", Operator = "EQ", Value = "PRODUCTION_INSTRUCTION" },
            new MatchCondition { FieldName = "MATERIALCODE", Operator = "CONTAINS", Value = "FINAL*" },
            new MatchCondition { FieldName = "STOCKCONSUMEDAY", Operator = "LTE", Value = "0" },
        ]
    };

    private static PriorityConditionGroup GroupB() => new()
    {
        Conditions =
        [
            new MatchCondition { FieldName = "ORDERTYPE", Operator = "EQ", Value = "PRODUCTION_INSTRUCTION" },
            new MatchCondition { FieldName = "MATERIALCODE", Operator = "CONTAINS", Value = "FINAL*" },
            new MatchCondition { FieldName = "ISSUEDATE", Operator = "LT", Value = "2026-10-08" },
        ]
    };

    [Fact(DisplayName = "B 组A 命中（库存消耗≤0）、组B 不命中 ⇒ 整段命中")]
    public void B_组A成立即命中()
    {
        var config = Config(Segment(1, groups: [GroupA(), GroupB()]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "A", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 0m, IssueDate = new DateTime(2026, 10, 20) },
        };

        _executor.ExecutePrioritySort(demands, config).Single().DemandSequence.Should().Be(1);
    }

    [Fact(DisplayName = "B 组A 不命中（库存>0）、组B 命中（发行日早于阈值）⇒ 整段命中 —— 修复前此需求不可达")]
    public void B_组B成立即命中()
    {
        var config = Config(Segment(1, groups: [GroupA(), GroupB()]));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "B", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 30m, IssueDate = new DateTime(2026, 9, 1) },
        };

        // 修复前执行器只读平铺列表 ⇒ 该需求完全不进段；现应作为唯一命中项拿到序号 1
        _executor.ExecutePrioritySort(demands, config).Single().DemandSequence.Should().Be(1);
    }

    [Fact(DisplayName = "B 组A 与 组B 皆成立 ⇒ 整段命中（组间 OR：任一 AND 组全成立即命中）")]
    public void B_两组皆成立则命中()
    {
        var config = Config(Segment(1, groups: [GroupA(), GroupB()]));

        // 同时满足组A（StockConsumeDay≤0）与组B（IssueDate 早于 2026-10-08）⇒ 两组皆成立，整段命中
        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "AB", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 0m, IssueDate = new DateTime(2026, 9, 1) },
        };

        _executor.ExecutePrioritySort(demands, config).Single().DemandSequence.Should().Be(1);
    }

    /// <summary>段2 组B 变体：仅把 `ISSUEDATE` 算子参数化（LT=设计意图 / GT=现网 478 现状），供反证用例区分配置。</summary>
    private static PriorityConditionGroup GroupBWithOperator(string issueDateOperator) => new()
    {
        Conditions =
        [
            new MatchCondition { FieldName = "ORDERTYPE", Operator = "EQ", Value = "PRODUCTION_INSTRUCTION" },
            new MatchCondition { FieldName = "MATERIALCODE", Operator = "CONTAINS", Value = "FINAL*" },
            new MatchCondition { FieldName = "ISSUEDATE", Operator = issueDateOperator, Value = "2026-10-08" },
        ]
    };

    [Fact(DisplayName = "B 反证（Operator:4 反例）：同一需求早于阈值，配 GT 不命中、配 LT 命中 —— oracle 可区分配置")]
    public void B_反证GT不命中LT命中_可区分配置()
    {
        // 目标需求 B1：不满足组A（库存>0），仅靠组B 决定 —— IssueDate 早于阈值 2026-10-08。
        // 契约意图是 `IssueDate<TODAY-30`（LT）；现网 478 段2 却落成 GT（Operator:4）。
        // 若 oracle 只会念设计意图、区分配置，则 GT/LT 下 B1 的段位应相同；反之 GT 时它应落兜底。
        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "A1", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 0m, IssueDate = new DateTime(2026, 11, 10), Quantity = 1m },
            new() { DemandKey = "B1", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 30m, IssueDate = new DateTime(2026, 9, 1), Quantity = 9m },
        };

        // ── GT 变体（现网 478 段2 现状）⇒ B1 组B 不命中、组A 也不命中 ⇒ 落兜底（保持输入序，B1 在 A1 之后）
        var gtConfig = Config(Segment(1,
            groups: [GroupA(), GroupBWithOperator("GT")],
            sorts: [new SortField { FieldName = "QUANTITY", Direction = "DESC" }]));
        var gtSorted = _executor.ExecutePrioritySort(demands, gtConfig);
        // 若 B1 误命中段1，会按数量降序被提到最前（seq 1）；应为 seq 2（落兜底）
        gtSorted.Single(d => d.DemandKey == "B1").DemandSequence.Should().Be(2,
            because: "组B 配 GT（现网现状）：发行日早于阈值的需求不命中段1，落兜底，而非被数量排序前置");

        // ── LT 变体（设计意图 `IssueDate<TODAY-30`）⇒ 同一 B1 组B 命中 ⇒ 进段1，按数量降序前置（seq 1）
        var ltConfig = Config(Segment(1,
            groups: [GroupA(), GroupBWithOperator("LT")],
            sorts: [new SortField { FieldName = "QUANTITY", Direction = "DESC" }]));
        var ltSorted = _executor.ExecutePrioritySort(demands, ltConfig);
        ltSorted.Single(d => d.DemandKey == "B1").DemandSequence.Should().Be(1,
            because: "组B 改配 LT（设计意图）：同一需求即命中段1，按数量降序前置 ⇒ oracle 能区分 GT/LT 配置");
    }

    [Fact(DisplayName = "B 两组均不成立 ⇒ 不命中，落兜底段（用段内排序键区分命中集合与兜底集合）")]
    public void B_两组均不成立则不命中()
    {
        var config = Config(
            Segment(1,
                groups: [GroupA(), GroupB()],
                sorts: [new SortField { FieldName = "QUANTITY", Direction = "DESC" }]),
            Segment(2, flat: []));   // 兜底段（空条件＝无条件命中，必须在段序末）

        // 两条都不满足组A（库存均 > 0）也不满足组B（发行日均不早于 2026-10-08）
        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "N1", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-1", StockConsumeDay = 30m, IssueDate = new DateTime(2026, 11, 1), Quantity = 1m },
            new() { DemandKey = "N2", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-2", StockConsumeDay = 40m, IssueDate = new DateTime(2026, 11, 2), Quantity = 9m },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // 若误命中段1，会按数量降序出 [N2,N1]；实际应落兜底段，保持输入序 [N1,N2]
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "N1", "N2" },
            because: "两组均不成立即整段不命中，必须落兜底段（兜底段无排序字段 ⇒ 保持输入顺序）");
    }

    [Fact(DisplayName = "B 空组不得退化为「无条件命中」（否则会吞掉后续所有段）")]
    public void B_空组不成立()
    {
        var config = Config(
            Segment(1,
                groups: [new PriorityConditionGroup { Conditions = [] }],
                sorts: [new SortField { FieldName = "QUANTITY", Direction = "DESC" }]),
            Segment(2, flat: []));

        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "N1", Quantity = 1m },
            new() { DemandKey = "N2", Quantity = 9m },
        };

        var sorted = _executor.ExecutePrioritySort(demands, config);

        // 空组若被当成 AND-over-empty＝true，段1 命中 ⇒ [N2,N1]；正确行为是不成立、落兜底 ⇒ [N1,N2]
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "N1", "N2" },
            because: "空组必须按不成立处理，否则会退化成无条件命中把后续段全部吞掉");
    }

    [Fact(DisplayName = "B 组优先生效：组非空时平铺列表被忽略（两种写法不叠加）")]
    public void B_组非空时忽略平铺()
    {
        var config = Config(Segment(1,
            flat: [new MatchCondition { FieldName = "ORDERTYPE", Operator = "EQ", Value = "SALES_ORDER" }],
            groups: [GroupA()]));

        var demands = new List<UpstreamDemand>
        {
            // 满足组A、不满足平铺条件；若平铺仍在生效则不会进段
            new() { DemandKey = "A", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-X", StockConsumeDay = 0m },
        };

        _executor.ExecutePrioritySort(demands, config).Single().DemandSequence.Should().Be(1,
            because: "组非空即以组为准，平铺列表不得参与求值");
    }

    // ── 投影层：条件组必须被真正投影（不得再 fail-closed） ──

    private static DemandPriorityBlock GroupBlock(
        List<SegmentConditionGroup> groups, ConditionOperator? dateOperator = null, object? dateValue = null)
    {
        var segment = new PrioritySegment
        {
            SegmentOrder = 1,
            SegmentName = "段2-DNF",
            IsEnabled = true,
            MatchConditions = [],
            MatchConditionGroups = groups,
            SortFields = [new SegmentSortField { Field = DemandField.IssueDate, Direction = SortDirection.Asc }],
            StableTieBreakFields = [TieBreakField.DEMANDKEY],
            CompetitionRound = CompetitionRound.Round1,
        };

        if (dateOperator.HasValue)
        {
            groups[1].Conditions.Add(new SegmentMatchCondition
            {
                Field = DemandField.CustomerDueDate,
                Operator = dateOperator.Value,
                Value = dateValue,
            });
        }

        return new DemandPriorityBlock { Segments = [segment] };
    }

    [Fact(DisplayName = "B 投影层：条件组按组投影到执行器配置，不再抛 NotSupportedException")]
    public void B_投影层放行条件组()
    {
        var block = GroupBlock(
        [
            new SegmentConditionGroup
            {
                Conditions =
                [
                    new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "PRODUCTION_INSTRUCTION" },
                    new SegmentMatchCondition { Field = DemandField.MaterialCode, Operator = ConditionOperator.Contains, Value = "FINAL*" },
                    new SegmentMatchCondition { Field = DemandField.StockConsumeDay, Operator = ConditionOperator.LessOrEqual, Value = 0 },
                ]
            },
            new SegmentConditionGroup
            {
                Conditions =
                [
                    new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "PRODUCTION_INSTRUCTION" },
                    new SegmentMatchCondition { Field = DemandField.MaterialCode, Operator = ConditionOperator.Contains, Value = "FINAL*" },
                    new SegmentMatchCondition { Field = DemandField.IssueDate, Operator = ConditionOperator.LessThan, Value = "2026-10-08" },
                ]
            },
        ]);

        var act = () => DemandPriorityProjector.Project(block, new DateTime(2026, 11, 1));
        act.Should().NotThrow("执行器已接线 DNF，投影层不得再 fail-closed");

        var config = act();
        config.Segments.Single().MatchConditionGroups.Should().HaveCount(2);
        config.Segments.Single().MatchConditionGroups[0].Conditions.Select(c => c.FieldName)
            .Should().Equal("ORDERTYPE", "MATERIALCODE", "STOCKCONSUMEDAY");
        config.Segments.Single().MatchConditionGroups[0].Conditions[1].Operator.Should().Be("CONTAINS");
    }

    [Fact(DisplayName = "B 投影层：组内的 CustomerDueDate 相对日阈值同样换算（不得出现两套口径）")]
    public void B_组内相对日阈值也换算()
    {
        var block = GroupBlock(
        [
            new SegmentConditionGroup
            {
                Conditions =
                [
                    new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "PRODUCTION_INSTRUCTION" },
                ]
            },
            new SegmentConditionGroup { Conditions = [] },
        ], ConditionOperator.LessOrEqual, 20);

        var config = DemandPriorityProjector.Project(block, new DateTime(2026, 11, 1));

        config.Segments.Single().MatchConditionGroups[1].Conditions.Single().Value
            .Should().Be("2026-11-21", because: "组内条件与平铺条件共用同一 ProjectCondition，阈值必须一样被换算");
    }

    // ── 发布校验：空组 / 组与平铺并存 ⇒ 拒绝 ──

    [Fact(DisplayName = "B 校验：空条件组必须被拒绝（否则会静默吞掉后续段）")]
    public void B_校验拒绝空组()
    {
        var block = GroupBlock([new SegmentConditionGroup { Conditions = [] }]);

        var result = new DemandPriorityValidator().Validate(block);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("条件组为空"));
    }

    [Fact(DisplayName = "B 校验：组与平铺同时配置必须被拒绝（执行器会忽略平铺，属歧义配置）")]
    public void B_校验拒绝组与平铺并存()
    {
        var block = GroupBlock(
        [
            new SegmentConditionGroup
            {
                Conditions = [new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "SALES_ORDER" }]
            }
        ]);
        block.Segments[0].MatchConditions =
        [
            new SegmentMatchCondition { Field = DemandField.CustomerTier, Operator = ConditionOperator.Equals, Value = "VIP" }
        ];

        var result = new DemandPriorityValidator().Validate(block);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("同时配置了"));
    }

    [Fact(DisplayName = "B 校验：只有条件组、平铺为空的段不得被误判为「空条件兜底段」")]
    public void B_校验组段不算兜底段()
    {
        var block = GroupBlock(
        [
            new SegmentConditionGroup
            {
                Conditions = [new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "SALES_ORDER" }]
            }
        ]);
        // 再补一个真正的兜底段（空条件、段序末）——若 DNF 段被误判为兜底，这里会报「兜底段只能有 1 个」
        block.Segments.Add(new PrioritySegment
        {
            SegmentOrder = 2,
            SegmentName = "兜底",
            IsEnabled = true,
            MatchConditions = [],
            SortFields = [],
            StableTieBreakFields = [TieBreakField.DEMANDKEY],
            CompetitionRound = CompetitionRound.Round1,
        });

        var result = new DemandPriorityValidator().Validate(block);

        result.Errors.Should().NotContain(e => e.Contains("空条件兜底段只能有 1 个"));
    }
}
