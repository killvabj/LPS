using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// DemandPriorityValidator 单元测试（发布前校验）。
///
/// 这组测试守的是一条链上的**两个接缝**：
/// 1. 白名单接缝：发布校验放行 ⇔ 投影层/执行器真的认这个字段。
///    2026-10-20 之前的缺陷正是这里比运行侧宽 3 个字段（`RemainingTimeHours` /
///    `IsPmcProtected` / `PriorityLevel` 只有旧 Matcher 认，执行器一律抛 `NotSupportedException`）
///    ⇒ 治理侧能发布、运行期整条 Pegging 炸。
/// 2. 兜底段接缝：空 `MatchConditions` ＝ 无条件命中，是合法形态（线上快照 478 的段 3 就是它），
///    但会吞掉其后所有段 ⇒ 只允许恰好一段且必须在段序末。
/// </summary>
public class DemandPriorityValidatorTests
{
    private readonly DemandPriorityValidator _validator = new();

    /// <summary>构造一个只引用指定字段的配置（匹配 + 排序各一处，便于同时覆盖两条白名单路径）。</summary>
    private static DemandPriorityBlock BlockWithField(DemandField field) => new()
    {
        Segments =
        [
            new PrioritySegment
            {
                SegmentOrder = 1, SegmentName = $"字段校验-{field}", IsEnabled = true,
                MatchConditions =
                [
                    new SegmentMatchCondition { Field = field, Operator = ConditionOperator.Equals, Value = null }
                ],
                SortFields =
                [
                    new SegmentSortField { Field = field, Direction = SortDirection.Asc }
                ]
            }
        ]
    };

    /// <summary>
    /// 参数化的字段清单 = <see cref="DemandField"/> 的全部成员，逐个钉死发布结论。
    /// 新增枚举值时本测试不会自动覆盖 —— 这是有意的：新值必须先由执行器落地比较分支，
    /// 再在 Validator 与 Projector 两侧同时登记，然后才在这里加一行。
    /// </summary>
    [Theory]
    [InlineData(DemandField.DelayStatus, false)]  // 2026-11-07 归参数集：投影器已 fail-closed
    [InlineData(DemandField.CustomerTier, true)]
    [InlineData(DemandField.OrderType, true)]
    [InlineData(DemandField.DueDate, true)]
    [InlineData(DemandField.IssueDate, true)]
    [InlineData(DemandField.RemainingTimeHours, false)]
    [InlineData(DemandField.IsPmcProtected, false)]
    [InlineData(DemandField.PriorityLevel, false)]
    public void 发布校验结论_必须与投影层实际能力一致(DemandField field, bool runtimeSupports)
    {
        var block = BlockWithField(field);

        // 投影层（内部）是运行期的第一道真闸：它认不认这个字段，就是运行期认不认。
        var projectorAccepts = true;
        try
        {
            DemandPriorityProjector.Project(block);
        }
        catch (NotSupportedException)
        {
            projectorAccepts = false;
        }

        var result = _validator.Validate(block);

        projectorAccepts.Should().Be(runtimeSupports,
            $"投影层对 {field} 的实际能力是这条断言的锚，改了它这里必须同步");
        result.IsValid.Should().Be(projectorAccepts,
            $"发布校验必须与投影层同口径：{field} 在运行侧{(runtimeSupports ? "可用" : "不可用")}，"
            + "放行运行侧用不了的字段会造成「发布放行、运行期抛」");
    }

    [Fact]
    public void P007_含DueDateIssueDate的冻结示例配置_校验通过()
    {
        // Arrange —— 0 号位冻结示例：Delayed SALES_ORDER → DueDate ASC → CustomerTier DESC → IssueDate ASC
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "延迟销售订单", IsEnabled = true,
                    MatchConditions =
                    [
                        // 2026-11-07 0号位 裁决：DelayStatus/DemandMaturityStatus 归参数集，不入规则集。
                        // 冻结示例本以 DelayStatus=DELAYED 作命中，现移参数集(ProtectDelayed)，此处改纯 OrderType 命中保留排序校验。
                        new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "SALES_ORDER" }
                    ],
                    SortFields =
                    [
                        new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc },
                        new SegmentSortField { Field = DemandField.CustomerTier, Direction = SortDirection.Desc },
                        new SegmentSortField { Field = DemandField.IssueDate, Direction = SortDirection.Asc }
                    ]
                }
            ]
        };

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void P007_DueDate匹配条件_白名单校验通过()
    {
        // Arrange —— 日期字段既可用于匹配也可用于排序
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "一周内到期", IsEnabled = true,
                    MatchConditions =
                    [
                        new SegmentMatchCondition
                        {
                            Field = DemandField.DueDate,
                            Operator = ConditionOperator.LessOrEqual,
                            Value = new DateTime(2026, 8, 27)
                        }
                    ],
                    SortFields =
                    [
                        new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }
                    ]
                }
            ]
        };

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── 兜底段准入（B3 v1.2 §3.2）────────────────────────────────────────────

    /// <summary>
    /// 现行线上快照 <c>RuleSetVersion 478</c>（实读 APS_Production 的 ContentSnapshotJson）必须能**重新发布**。
    /// 2026-11-07 0号位 裁决：DelayStatus/DemandMaturityStatus 归参数集，478 已整份替换为整理稿 6 段 / 3 轮
    /// （无 DelayStatus/DemandMaturity 命中/排序、OriginalDueDate 已删、≠INTER_FACTORY 改 IN 补集）。
    /// 本载荷 = 用户 2026-11-07 在 SSMS 执行整份替换 SQL 后读回的真实 478 段集合（防空转：测试锚=真实现状，
    /// 不沿用已作废的旧 3 段 DelayStatus 载荷）。
    /// </summary>
    [Fact]
    public void 现行线上快照478_重新发布校验通过()
    {
        const string real478Json = """
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

        var block = System.Text.Json.JsonSerializer.Deserialize<DemandPriorityBlock>(real478Json)!;

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue(
            "478 已整份替换为 6 段（无 DelayStatus/Maturity 命中排序、无空条件兜底），"
            + "此为新真源，必须能重新发布：" + result.GetErrorMessage());
        result.Errors.Should().BeEmpty();
    }

    /// <summary>线上快照 RuleSetVersion 478 的真实形态：段 3 为空条件兜底，位于段序末。</summary>
    [Fact]
    public void 空条件兜底段_恰好一段且在段序末_放行()
    {
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "VIP订单", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.CustomerTier, Operator = ConditionOperator.Equals, Value = "VIP" }],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                },
                new PrioritySegment
                {
                    SegmentOrder = 2, SegmentName = "其余", IsEnabled = true,
                    MatchConditions = [],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue(
            "空条件＝无条件命中，执行器 IsMatchSegment 对空表返回 true；这是现行线上快照的形态，"
            + "若判非法，现行快照每次重新发布都会失败");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void 空条件兜底段_不在段序末_报错_因为其后各段永远不可达()
    {
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "兜底放错位置", IsEnabled = true,
                    MatchConditions = [],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                },
                new PrioritySegment
                {
                    SegmentOrder = 2, SegmentName = "永远不可达", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.CustomerTier, Operator = ConditionOperator.Equals, Value = "VIP" }],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("空条件兜底段必须置于段序末"));
    }

    [Fact]
    public void 空条件兜底段_多于一段_报错()
    {
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "兜底甲", IsEnabled = true,
                    MatchConditions = [],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                },
                new PrioritySegment
                {
                    SegmentOrder = 2, SegmentName = "兜底乙", IsEnabled = true,
                    MatchConditions = [],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("空条件兜底段只能有 1 个"));
    }

    // ── 操作符白名单（执行器无 NE 分支）──────────────────────────────────────

    [Fact]
    public void NotEquals_发布期即拦截_不留到运行期()
    {
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "NE", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.NotEquals, Value = "SO" }],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeFalse("执行器只有 EQ/IN/LT/LTE/GT/GTE，NotEquals 没有对应实现");
        result.Errors.Should().Contain(e => e.Contains("运行侧不支持"));
    }

    [Fact]
    public void Contains操作符_发布期放行_与执行器CONTAINS分支对齐()
    {
        // 2026-11-07 O26：执行器 `EvaluateCondition` 自 r14060 起已有 "CONTAINS" 分支
        //（语义＝前缀匹配：剥结尾 `*` + 忽略大小写，3号位 2026-11-06 裁决），
        // 但发布期白名单一直没收 ⇒ 任何写了「包含」的策略包**发布时原地被拒**，运行侧却完全支持。
        // 本用例钉住「白名单 ≥ 运行侧实现」，防止再次落后。
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "前缀命中", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.MaterialCode, Operator = ConditionOperator.Contains, Value = "FINAL*" }],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue("执行器已有 CONTAINS 分支，发布期不得再拦");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void In操作符_列表值_校验通过()
    {
        var block = new DemandPriorityBlock
        {
            Segments =
            [
                new PrioritySegment
                {
                    SegmentOrder = 1, SegmentName = "多值命中", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.CustomerTier, Operator = ConditionOperator.In, Value = new[] { "A", "B" } }],
                    SortFields = [new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }]
                }
            ]
        };

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void 属于操作符_JsonElement数组值_校验通过_修复F2()
    {
        // 4号位 2026-10-25 F2：走查实发的「属于」段——Value 是 JSON 数组，
        // STJ 把它反序列化成 JsonElement（.NET 8 不实现 System.Collections.IEnumerable），
        // 旧判定 `condition.Value is not IEnumerable` 便把任何数组误判「不是列表」。
        // 运行侧 DemandPriorityProjector.ToMatchValue（拼成 "SO,WO"）+ DemandPriorityExecutor 按逗号拆分
        // 本就支持 IN，只有发布校验误杀。此用例走真实 JSON 载荷复现 JsonElement 路径。
        // （上方 In操作符_列表值_校验通过 传的是真 string[]，本就是 IEnumerable，守不住这条缺陷。）
        const string inJson = """
        {"Segments":[
          {"SegmentOrder":1,"SegmentName":"订单类型组","IsEnabled":true,
           "MatchConditions":[{"Field":3,"Operator":6,"Value":["SO","WO"]}],
           "SortFields":[{"Field":6,"Direction":0}],
           "StableTieBreakFields":["DEMANDKEY"]}
        ]}
        """;

        var block = System.Text.Json.JsonSerializer.Deserialize<DemandPriorityBlock>(inJson)!;

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue(
            "「订单类型属于[SO,WO]」运行侧支持（逗号拆分比对），发布校验应放行：" + result.GetErrorMessage());
        result.Errors.Should().BeEmpty();
    }
}
