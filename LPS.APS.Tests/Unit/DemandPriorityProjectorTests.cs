using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// DemandPriorityProjector 单元测试（2号位 投影层：3号位冻结快照子块 → 2号位执行器配置）。
///
/// 为什么要这一组：快照块 <see cref="DemandPriorityBlock"/> 与执行器配置 <see cref="DemandPriorityConfig"/>
/// 是**两套类型系统**（前者强类型枚举、无计算层；后者字符串词表、带 CompetitionRound），
/// 投影错了不会编译报错，只会静默错排或整条 Pegging 失败。
/// 本组用**生产实读的真实快照载荷**做端到端断言，并直接调真执行器验收 —— 投影不合法会在执行器白名单处炸出来。
///
/// 2026-09-29 真跑实测踩到的两个坑已在此钉死：
///   1. Tie-break 自由文本 "OrderId" 撞执行器白名单（原样透传 ⇒ 整条 Pegging 失败）；
///   2. CompetitionRound 投影成 0 会让全体 Demand 静默退化为 DemandKey ASC 兜底（冻结策略形同未生效）。
/// </summary>
public class DemandPriorityProjectorTests
{
    private readonly DemandPriorityExecutor _executor = new(NullLogger<DemandPriorityExecutor>.Instance);

    /// <summary>
    /// 生产默认策略包 StrategyProfileVersion 811 (SP-DEMO-V3.0, IsDefault=1) 冻结的 DemandPriority 子块，
    /// 即 RuleSetVersion 478 的 ContentSnapshotJson —— 0号位 20261107 裁决整份替换（6 段 / 3 轮），用户已在 SSMS 执行。
    /// 段1-6 全不含 DelayStatus / DemandMaturityStatus（归参数集需求保护），OriginalDueDate 已删，
    /// CustomerSegment ≠INTER_FACTORY 改 IN 补集，CompetitionRound 1/1/1/1/2/3 显式声明。
    /// 注意枚举仍以**数值**序列化（Field:3=OrderType, 8=CustomerSegment, 9=CustomerDueDate, 10=TransportMode,
    /// 15=MaterialCode, 13=StockConsumeDay, 7=IssueDate；Operator:0=Equals,6=In,7=Contains,3=LessOrEqual；
    /// Direction:0=Asc）。
    ///
    /// ⚠ 段1 `CUSTOMERDUEDATE` 已升为整数偏移 `20`（换算链已接通：Provider 传 FrozenAt + Projector 换算
    /// `FrozenAt.Date+20`）。故本组 `Project()` 一律传 <see cref="TestFrozenAt"/>，否则整数偏移遇 null 冻结点
    /// 会 fail-closed 抛错。构造需求时**避开依赖换算阈值**：段1 命中用 CustomerDueDate=null
    /// （空串 LTE 换算后日期成立）、段2 用 MaterialCode FINAL* 作主路由，保证路由确定。
    /// </summary>
    private static readonly DateTime TestFrozenAt = new(2026, 11, 1, 15, 30, 0);
    private const string RealSnapshot811Json = """
    {"Segments":[
      {"SegmentOrder":1,"SegmentName":"顶层-销售订单(需求内)","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"SALES_ORDER"},
                          {"Field":8,"Operator":6,"Value":["JAPAN","DOMESTIC","OVERSEAS","VIETNAM","OTHER"]},
                          {"Field":9,"Operator":3,"Value":"20"}],
       "SortFields":[{"Field":9,"Direction":0},{"Field":10,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":1},
      {"SegmentOrder":2,"SegmentName":"顶层-生产指示(库存消耗<=0)","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"PRODUCTION_INSTRUCTION"},
                          {"Field":15,"Operator":7,"Value":"FINAL*"},
                          {"Field":13,"Operator":3,"Value":"0"}],
       "SortFields":[{"Field":13,"Direction":0},{"Field":7,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":1},
      {"SegmentOrder":3,"SegmentName":"顶层-销售订单(需求外,正常)","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"SALES_ORDER"},
                          {"Field":8,"Operator":6,"Value":["JAPAN","DOMESTIC","OVERSEAS","VIETNAM","OTHER"]}],
       "SortFields":[{"Field":9,"Direction":0},{"Field":10,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":1},
      {"SegmentOrder":4,"SegmentName":"顶层-生产指示(FINAL*)","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"PRODUCTION_INSTRUCTION"},
                          {"Field":15,"Operator":7,"Value":"FINAL*"}],
       "SortFields":[{"Field":13,"Direction":0},{"Field":7,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":1},
      {"SegmentOrder":5,"SegmentName":"SH履约-工厂自用销售订单","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"SALES_ORDER"},
                          {"Field":8,"Operator":0,"Value":"INTER_FACTORY"}],
       "SortFields":[{"Field":9,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":2},
      {"SegmentOrder":6,"SegmentName":"第3轮-孤儿与下阶生产指示","IsEnabled":true,
       "MatchConditions":[{"Field":3,"Operator":0,"Value":"PRODUCTION_INSTRUCTION"}],
       "SortFields":[{"Field":13,"Direction":0},{"Field":7,"Direction":0}],
       "StableTieBreakFields":["DEMANDKEY"],"CompetitionRound":3}
    ]}
    """;

    private static DemandPriorityBlock ParseRealBlock()
        => JsonSerializer.Deserialize<DemandPriorityBlock>(RealSnapshot811Json)!;

    // ── 真实快照端到端：投影 → 真执行器验收 ──

    [Fact]
    public void 真实478快照_投影后执行器接受_并按段序输出连续DemandSequence()
    {
        var config = DemandPriorityProjector.Project(ParseRealBlock(), TestFrozenAt);

        var demands = new List<UpstreamDemand>
        {
            // 段1（round1）：销售订单·厂外客户区分·CustomerDueDate=null ⇒ 段1 的 CUSTOMERDUEDATE LTE 占位对空串成立
            new() { DemandKey = "A", OrderType = "SALES_ORDER", CustomerSegment = "JAPAN", CustomerDueDate = null },
            // 段2（round1）：生产指示·MaterialCode FINAL*·库存消耗<=0
            new() { DemandKey = "B", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "FINAL-PART-1", StockConsumeDay = 0 },
            // 段6（round3）：生产指示·非FINAL ⇒ 只落段6
            new() { DemandKey = "C", OrderType = "PRODUCTION_INSTRUCTION", MaterialCode = "NONFINAL", StockConsumeDay = 5 },
        };

        var act = () => _executor.ExecutePrioritySort(demands, config);
        act.Should().NotThrow("投影结果必须能通过执行器白名单校验（新增 V1.1 字段名漂移会在这里炸）");

        var sorted = _executor.ExecutePrioritySort(demands, config);
        sorted.Select(d => d.DemandKey).Should().Equal(new[] { "A", "B", "C" }, because: "段序优先：段1 → 段2 → 段6");
        sorted.Select(d => d.DemandSequence).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void 真实478快照_六段三轮_轮次逐段透传_防静默退化()
    {
        var config = DemandPriorityProjector.Project(ParseRealBlock(), TestFrozenAt);

        config.Segments.Should().NotBeEmpty();
        config.Segments.Select(s => s.CompetitionRound).Should().Equal(new[] { 1, 1, 1, 1, 2, 3 },
            "478 整份替换后六段显式声明轮次（情形三），投影须逐段透传；若被压平回第 1 轮，"
            + "第 2/3 轮段会在 PeggingOrchestrator 按轮次取段时被取错，排序错而无声（防空转防静默退化）");
    }

    [Fact]
    public void 真实478快照_各段TieBreak恒为DEMANDKEY()
    {
        var config = DemandPriorityProjector.Project(ParseRealBlock(), TestFrozenAt);

        config.Segments.Should().OnlyContain(s =>
            s.StableTieBreakFields.Count == 1 && s.StableTieBreakFields[0] == "DEMANDKEY",
            "478 整份替换后 Tie-break 已是强类型成员名 DEMANDKEY（反序列化即归一）；投影后必须是执行器白名单词 DEMANDKEY");
    }

    [Fact]
    public void 真实478快照_新增V1_1字段与方向逐值映射正确()
    {
        var config = DemandPriorityProjector.Project(ParseRealBlock(), TestFrozenAt);

        // 段5：OrderType + CustomerSegment(INTER_FACTORY) 命中，CustomerDueDate ASC 排序
        var seg5 = config.Segments.Single(s => s.SegmentOrder == 5);
        seg5.MatchConditions.Should().HaveCount(2);
        seg5.MatchConditions[0].FieldName.Should().Be("ORDERTYPE");
        seg5.MatchConditions[0].Value.Should().Be("SALES_ORDER");
        seg5.MatchConditions[1].FieldName.Should().Be("CUSTOMERSEGMENT");
        seg5.MatchConditions[1].Operator.Should().Be("EQ");
        seg5.MatchConditions[1].Value.Should().Be("INTER_FACTORY");
        seg5.SortFields.Select(f => (f.FieldName, f.Direction))
            .Should().Equal(("CUSTOMERDUEDATE", "ASC"));

        // 段2：生产指示 + MaterialCode CONTAINS + StockConsumeDay LTE；Sort StockConsumeDay/IssueDate ASC
        var seg2 = config.Segments.Single(s => s.SegmentOrder == 2);
        seg2.MatchConditions.Should().HaveCount(3);
        seg2.MatchConditions[0].FieldName.Should().Be("ORDERTYPE");
        seg2.MatchConditions[1].FieldName.Should().Be("MATERIALCODE");
        seg2.MatchConditions[1].Operator.Should().Be("CONTAINS");
        seg2.MatchConditions[2].FieldName.Should().Be("STOCKCONSUMEDAY");
        seg2.SortFields.Select(f => (f.FieldName, f.Direction))
            .Should().Equal(("STOCKCONSUMEDAY", "ASC"), ("ISSUEDATE", "ASC"));

        // 防空转锚：0号位 裁决 DelayStatus/DemandMaturityStatus 归参数集 ⇒ 整块投影后不得再出现这两字段
        var allFields = config.Segments
            .SelectMany(s => s.MatchConditions.Select(m => m.FieldName).Concat(s.SortFields.Select(f => f.FieldName)));
        allFields.Should().NotContain("DELAYSTATUS", "0号位 裁决：规则集不得再承载 DelayStatus（归参数集需求保护）");
        allFields.Should().NotContain("DEMANDMATURITYSTATUS");
    }

    // ── 显示失败纪律（不静默、不猜配）──

    [Fact]
    public void 快照子块为空_显式抛错_禁止静默当空策略执行()
    {
        var act = () => DemandPriorityProjector.Project(null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DemandPriority 子块为空*");
    }

    [Fact]
    public void 白名单外的字段_显式抛错_不猜配()
    {
        // DemandField.RemainingTimeHours(=0) 在执行器白名单里没有对应字段。
        // 刻意不猜（例如猜成 DUEDATE）——静默错排比报错危险得多。
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1,
                    IsEnabled = true,
                    SortFields = [new SegmentSortField { Field = DemandField.RemainingTimeHours, Direction = SortDirection.Asc }],
                }
            ]
        };

        var act = () => DemandPriorityProjector.Project(block);

        act.Should().Throw<NotSupportedException>().WithMessage("*RemainingTimeHours*");
    }

    // ── Tie-break 强类型化（B3 v1.2 §3，2026-10-20）────────────────────────
    // 改强类型的目的就是**把名字漂移从运行期提前到反序列化期**：
    // 之前未知 Tie-break 名要等到执行器 EnsureKnownField 才报错（整条 Pegging 失败），
    // 现在快照一装载就炸，且报错点明确。

    [Fact]
    public void 未知TieBreak名_反序列化即失败_不进到运行期()
    {
        const string json = """
        {"Segments":[{"SegmentOrder":1,"SegmentName":"S1","IsEnabled":true,
          "MatchConditions":[],
          "SortFields":[{"Field":6,"Direction":0}],
          "StableTieBreakFields":["SomethingBrandNew"]}]}
        """;

        var act = () => JsonSerializer.Deserialize<DemandPriorityBlock>(json);

        act.Should().Throw<JsonException>().WithMessage("*SomethingBrandNew*");
    }

    [Theory]
    [InlineData("DEMANDKEY")]
    [InlineData("OrderId")]     // 存量快照 477/478/1240 的写法
    [InlineData("DemandKey")]   // Fixture 的写法
    public void 存量TieBreak写法_一律归一化为DEMANDKEY(string raw)
    {
        var json = $$"""
        {"Segments":[{"SegmentOrder":1,"SegmentName":"S1","IsEnabled":true,
          "MatchConditions":[],
          "SortFields":[{"Field":6,"Direction":0}],
          "StableTieBreakFields":["{{raw}}"]}]}
        """;

        var block = JsonSerializer.Deserialize<DemandPriorityBlock>(json)!;

        block.Segments[0].StableTieBreakFields.Should().Equal(TieBreakField.DEMANDKEY);
    }

    // ── 竞争轮次 fail-closed（B1，2026-10-20）────────────────────────────────
    // 这三条断言的是**声明目的**本身：「漏写轮次的段绝不能被静默压进第 1 轮」。
    // 若把实现退化成「缺省一律给 1」，第二条会立刻变红 —— 这正是要防的那个退化。

    private static PrioritySegment Seg(int order, CompetitionRound? round) => new()
    {
        SegmentOrder = order,
        SegmentName = $"S{order}",
        IsEnabled = true,
        CompetitionRound = round,
        SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }],
        StableTieBreakFields = [TieBreakField.DEMANDKEY],
    };

    [Fact]
    public void 整块未声明轮次_全部投影为第1轮()
    {
        var block = new DemandPriorityBlock { Segments = [Seg(1, null), Seg(2, null)] };

        var config = DemandPriorityProjector.Project(block);

        config.Segments.Should().OnlyContain(s => s.CompetitionRound == DemandPriorityProjector.DefaultCompetitionRound,
            "整块都没声明时按第 1 轮处理 —— 这是当前线上快照的形态，行为必须与改动前一致");
    }

    [Fact]
    public void 部分段声明轮次_必须抛错_不得把漏写的段压进第1轮()
    {
        // 漏写的是 SegmentOrder=2：若静默归 1，第 2 轮的段会跑到第 1 轮去竞争，
        // 现象只有「排序看起来不对」，不报错不告警，排查代价极高。
        var block = new DemandPriorityBlock { Segments = [Seg(1, CompetitionRound.Round1), Seg(2, null)] };

        var act = () => DemandPriorityProjector.Project(block);

        // 报错必须点名是哪一段没写轮次，否则不知道去找谁。
        act.Should().Throw<InvalidOperationException>().WithMessage("*SegmentOrder=2*");
    }

    [Fact]
    public void 全部声明轮次_逐个透传()
    {
        var block = new DemandPriorityBlock { Segments = [Seg(1, CompetitionRound.Round1), Seg(2, CompetitionRound.Round3)] };

        var config = DemandPriorityProjector.Project(block);

        config.Segments.Select(s => s.CompetitionRound).Should().Equal(new[] { 1, 3 },
            "声明了就必须透传，不能被默认值覆盖");
    }

    /// <summary>
    /// 治理侧与运行侧**同名**是这条链的硬约束（B3 v1.2 §四.2：「两端字段名必须一字不差」）。
    ///
    /// 危险形态：治理侧若写 <c>"Round"</c> 而我们读 <c>CompetitionRound</c>，
    /// <c>JsonOptions</c> 只做大小写不敏感匹配、**不报未知成员**
    /// ⇒ 轮次被整块静默忽略，所有段回落第 1 轮，且没有任何异常。
    /// 这条断言把「属性改名」从静默漂移变成红灯。
    /// </summary>
    [Fact]
    public void 轮次JSON键名_必须是CompetitionRound_与运行侧一字不差()
    {
        var json = JsonSerializer.Serialize(new PrioritySegment
        {
            SegmentOrder = 1,
            CompetitionRound = CompetitionRound.Round2,
        });

        json.Should().Contain("\"CompetitionRound\":2",
            "治理侧落库键名必须与运行侧 PrioritySegmentConfig.CompetitionRound 同名");
        json.Should().NotContain("\"Round\"");

        // 漏写轮次必须在 JSON 里表现为 null（而不是被序列化成第 1 轮），否则 fail-closed 失去可判定输入。
        var undeclared = JsonSerializer.Serialize(new PrioritySegment { SegmentOrder = 1 });
        undeclared.Should().Contain("\"CompetitionRound\":null");
    }

    /// <summary>
    /// 键名写错的另一半保护：`JsonOptions` 默认**忽略**未知成员，若快照写 `"Round":1`，
    /// 段会被当成「未声明轮次」⇒ 整块回落第 1 轮，无异常无告警。
    /// 段类型设为拒收未知成员后，同一情形在装载期就报错并点名 `Round`。
    /// </summary>
    [Fact]
    public void 段内未知成员_装载即失败_轮次键名写错不静默()
    {
        const string json = """
        {"Segments":[{"SegmentOrder":1,"SegmentName":"S1","IsEnabled":true,
          "MatchConditions":[],
          "SortFields":[{"Field":6,"Direction":0}],
          "StableTieBreakFields":["DEMANDKEY"],
          "Round":1}]}
        """;

        var act = () => JsonSerializer.Deserialize<DemandPriorityBlock>(json);

        act.Should().Throw<JsonException>().WithMessage("*Round*");
    }

    [Fact]
    public void 声明轮次小于1_必须抛错()
    {
        var block = new DemandPriorityBlock { Segments = [Seg(1, (CompetitionRound)0)] };

        var act = () => DemandPriorityProjector.Project(block);

        act.Should().Throw<InvalidOperationException>().WithMessage("*必须 >= 1*");
    }

    // ── `CustomerDueDate` 相对日阈值（`TODAY+20`）──────────────────────────────
    // 3号位 2026-11-06 回执 §三：Run 冻结日 = `FrozenStrategySnapshot.FrozenAt.Date`；
    // 配置 `Value` 存整数偏移，由**我方在装载/匹配期**换算成具体日期再比较。
    //
    // 这一组断言的目的**不是**「字符串变了个样子」，而是「不换算就会静默错判」这件事不再可能发生：
    // 执行器 `CompareNumericOrDate` 对「日期串 vs "20"」会落到字符串比较 ⇒ LTE 恒 false、GTE 恒 true。
    // 所以第 2 条走真执行器，直接验命中结果随冻结日不同而不同。

    private static DemandPriorityBlock CustomerDueDateThresholdBlock(
        ConditionOperator op, object? value) => new()
    {
        Segments =
        [
            new PrioritySegment
            {
                SegmentOrder = 1,
                SegmentName = "客户交期阈值段",
                IsEnabled = true,
                MatchConditions = [new SegmentMatchCondition { Field = DemandField.CustomerDueDate, Operator = op, Value = value }],
                SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }],
                StableTieBreakFields = [TieBreakField.DEMANDKEY],
            }
        ]
    };

    [Fact]
    public void 客户交期整数阈值_按冻结日换算为具体日期_不同冻结日得不同阈值()
    {
        var block = CustomerDueDateThresholdBlock(ConditionOperator.LessOrEqual, 20);

        var early = DemandPriorityProjector.Project(block, new DateTime(2026, 11, 1, 15, 30, 0));
        var late = DemandPriorityProjector.Project(block, new DateTime(2026, 12, 1, 8, 0, 0));

        early.Segments[0].MatchConditions[0].Value.Should().Be("2026-11-21");
        late.Segments[0].MatchConditions[0].Value.Should().Be("2026-12-21",
            "阈值必须随 Run 冻结日移动；若两条相等，说明换算没接上、仍在比原始整数");
    }

    [Fact]
    public void 客户交期整数阈值_真执行器按换算后日期命中_同一需求在不同冻结日结果不同()
    {
        // 需求客户交期 2026-12-10：
        //   冻结日 11-01 ⇒ 阈值 11-21 ⇒ 不命中（落到未命中兜底，排在命中之后）
        //   冻结日 12-01 ⇒ 阈值 12-21 ⇒ 命中
        var block = CustomerDueDateThresholdBlock(ConditionOperator.LessOrEqual, 20);
        var demands = new List<UpstreamDemand>
        {
            new() { DemandKey = "D1", CustomerDueDate = new DateTime(2026, 12, 10) },
            new() { DemandKey = "D2", CustomerDueDate = new DateTime(2026, 11, 5) },
        };

        var earlySorted = _executor.ExecutePrioritySort(
            demands, DemandPriorityProjector.Project(block, new DateTime(2026, 11, 1)));
        var lateSorted = _executor.ExecutePrioritySort(
            demands, DemandPriorityProjector.Project(block, new DateTime(2026, 12, 1)));

        // 11-01 冻结：只有 D2 命中段，D1 未命中落兜底
        earlySorted.Select(d => d.DemandKey).Should().Equal(new[] { "D2", "D1" });
        // 12-01 冻结：两条都命中段，按 DueDate 升序（两条 DueDate 皆 null ⇒ 稳定退出到 DEMANDKEY）
        lateSorted.Select(d => d.DemandKey).Should().Equal(new[] { "D1", "D2" });
    }

    [Fact]
    public void 客户交期整数阈值_未传冻结时点_显式抛错_不静默退化为字符串比较()
    {
        var block = CustomerDueDateThresholdBlock(ConditionOperator.LessOrEqual, 20);

        var act = () => DemandPriorityProjector.Project(block);

        act.Should().Throw<InvalidOperationException>().WithMessage("*冻结时点*");
    }

    [Fact]
    public void 客户交期显式日期阈值_原样保留_不做偏移换算()
    {
        // 配置直接写具体日期是合法写法，不能被当成「整数偏移」再算一次。
        var block = CustomerDueDateThresholdBlock(ConditionOperator.LessOrEqual, "2026-11-20");

        var config = DemandPriorityProjector.Project(block, new DateTime(2026, 11, 1));

        config.Segments[0].MatchConditions[0].Value.Should().Be("2026-11-20");
    }
}
