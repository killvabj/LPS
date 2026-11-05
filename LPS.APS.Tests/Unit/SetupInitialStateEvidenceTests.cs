using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R08 回归锁：**「无上一产品」的成因必须可区分**。
///
/// 0号位 2026-10-28《R07/R08 更正回复 v1.1》§5 逐字：
/// 「**区分「可信确无前产品」和「执行/基础计划事实缺失」，不能都交 `fromMaterialId=null` 作为合法初态**」。
///
/// 链上的事实（已读代码核实，非推断）：`fromMaterialId` 不是 2→1 交付字段 —— 1号位 从
/// `ResourceProductTimeline.GetPrevMaterial(resourceId, 槽起点)` 自推，时间线只有「本轮已排」
/// 与「`ExecutionConstraints` 继承锁定任务」两个种子；而本域 `ExecutionConstraints` 在
/// `!IsCandidate || BasePlanVersionId is null` 时恒空 ⇒ 求解器只有一条 null 分支
/// （`InitialState`、0 分钟、INFO），三类事实被压成同一个值。
///
/// 本文件的锚点：**四个成因分支必须给出四段互不相同的判据文本**（任一处合并即红），
/// 且「无基准计划」那一支**不得**被写成可信初态。
/// </summary>
public class SetupInitialStateEvidenceTests
{
    private static PeggingExecutionRequest Req(bool isCandidate, int? basePlanVersionId)
        => new() { IsCandidate = isCandidate, BasePlanVersionId = basePlanVersionId };

    [Fact(DisplayName = "锚点_无基准计划_必须判事实缺失而不是可信初态")]
    public void 无基准计划_判事实缺失()
    {
        var text = PeggingOrchestrator.DescribeSetupInitialStateEvidence(
            Req(isCandidate: false, basePlanVersionId: null), executionConstraintCount: 0);

        text.Should().StartWith("BASE_PLAN_FACT_MISSING",
            "`BasePlanVersionId is null` ⇒ 求解器时间线里没有任何基准执行事实，"
            + "这时的 fromMaterialId=null 是「事实缺失」，**不是**「确无前产品」");
        text.Should().Contain("不得当作");
    }

    [Fact(DisplayName = "非候选Run_判不承接而不是事实缺失")]
    public void 非候选Run_判不承接()
    {
        var text = PeggingOrchestrator.DescribeSetupInitialStateEvidence(
            Req(isCandidate: false, basePlanVersionId: 190), executionConstraintCount: 0);

        text.Should().StartWith("NON_CANDIDATE_RUN",
            "有基准计划但本 Run 非候选 ⇒ 锚点构建直接 Empty，是「不承接」而非「读失败」");
        text.Should().Contain("190");
    }

    [Fact(DisplayName = "候选Run且无锁定任务_判可信")]
    public void 候选Run无锁定任务_判可信()
    {
        var text = PeggingOrchestrator.DescribeSetupInitialStateEvidence(
            Req(isCandidate: true, basePlanVersionId: 190), executionConstraintCount: 0);

        text.Should().StartWith("BASE_PLAN_WITHOUT_LOCKED_TASK");
        text.Should().Contain("可信");
    }

    [Fact(DisplayName = "候选Run且有锁定任务_判承接范围内可信且带条数")]
    public void 候选Run有锁定任务_判承接范围内可信()
    {
        var text = PeggingOrchestrator.DescribeSetupInitialStateEvidence(
            Req(isCandidate: true, basePlanVersionId: 190), executionConstraintCount: 7);

        text.Should().StartWith("BASE_PLAN_WITH_LOCKED_TASKS(7)");
        text.Should().Contain("可信");
    }

    [Fact(DisplayName = "锚点_四成因两两不同")]
    public void 四成因两两不同()
    {
        var texts = new[]
        {
            PeggingOrchestrator.DescribeSetupInitialStateEvidence(Req(false, null), 0),
            PeggingOrchestrator.DescribeSetupInitialStateEvidence(Req(false, 190), 0),
            PeggingOrchestrator.DescribeSetupInitialStateEvidence(Req(true, 190), 0),
            PeggingOrchestrator.DescribeSetupInitialStateEvidence(Req(true, 190), 7),
        };

        texts.Should().OnlyHaveUniqueItems(
            "四种成因一旦合并成同一句，就退回 0号位 点名的「都交 fromMaterialId=null 作为合法初态」");
        texts.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t));
    }
}
