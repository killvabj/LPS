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
    [InlineData(DemandField.DelayStatus, true)]
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
                        new SegmentMatchCondition { Field = DemandField.DelayStatus, Operator = ConditionOperator.Equals, Value = "DELAYED" },
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
    /// 这是改白名单/兜底段规则的验收口径：规则收紧后若把现行快照判成非法，
    /// 后果是「线上配置原地不可再发布」，比原缺陷更难收拾。载荷与
    /// <see cref="DemandPriorityProjectorTests"/> 里的真实快照一致（含 Tie-break 旧写法 "OrderId"）。
    /// </summary>
    [Fact]
    public void 现行线上快照478_重新发布校验通过()
    {
        const string real478Json = """
        {"Segments":[
          {"SegmentOrder":1,"SegmentName":"延迟订单","IsEnabled":true,
           "MatchConditions":[{"Field":1,"Operator":0,"Value":"Delayed"},{"Field":3,"Operator":0,"Value":"SO"}],
           "SortFields":[{"Field":6,"Direction":0},{"Field":2,"Direction":1},{"Field":7,"Direction":0}],
           "StableTieBreakFields":["OrderId"]},
          {"SegmentOrder":2,"SegmentName":"VIP","IsEnabled":true,
           "MatchConditions":[{"Field":2,"Operator":0,"Value":"VIP"}],
           "SortFields":[{"Field":6,"Direction":0}],
           "StableTieBreakFields":["OrderId"]},
          {"SegmentOrder":3,"SegmentName":"其余","IsEnabled":true,
           "MatchConditions":[],
           "SortFields":[{"Field":6,"Direction":0}],
           "StableTieBreakFields":["OrderId"]}
        ]}
        """;

        var block = System.Text.Json.JsonSerializer.Deserialize<DemandPriorityBlock>(real478Json)!;

        var result = _validator.Validate(block);

        result.IsValid.Should().BeTrue(
            "段 3 的空条件兜底是合法形态（执行器对空表返回 true），且它位于段序末，"
            + "把现行快照判成非法会让重新发布原地失败：" + result.GetErrorMessage());
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
                    SegmentOrder = 1, SegmentName = "延迟订单", IsEnabled = true,
                    MatchConditions = [new SegmentMatchCondition { Field = DemandField.DelayStatus, Operator = ConditionOperator.Equals, Value = "Delayed" }],
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
