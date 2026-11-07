using System;
using System.Collections.Generic;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 需求保护触发派生（B4 §三）单元测试 —— 冻结快照 <c>$.Lock.Trigger</c> → <c>ProtectionStatus</c>。
///
/// <para>
/// **为什么这组必须存在**：<c>$.Lock</c> 自 2026-08-27 装进冻结快照起就**零读者**，
/// 配置面「已配置、有实值、不生效」挂了两个多月。补上消费点后，这段逻辑直接决定
/// <c>PROTECTIONSTATUS</c> 排序字段的取值 —— 判错了不会编译报错，只会把该保护的需求排到后面。
/// </para>
/// <para>
/// **本组刻意反向断言（不是「跑通就行」）**：
/// <list type="bullet">
///   <item>线上配置 <c>ProtectedOrderTypes=["SO"]</c> vs 运行词表 <c>SALES_ORDER</c> —— 必须**不命中**且给出缺口说明，
///     而不是被宽容匹配「修好」；那个错位是 3号位 侧要改配置，不是我方要在代码里猜。</item>
///   <item>缺事实（<c>CustomerDueDate</c> 为空）—— 必须**不保护＋留痕**，不得静默当已保护。</item>
///   <item>DNF 触发组非空 —— 必须**显式抛错**，不得静默回退旧布尔簇。</item>
/// </list>
/// </para>
/// </summary>
public class DemandProtectionTriggerEvaluatorTests
{
    /// <summary>基准时点 = 本 Run 的 <c>DataCutoffTime</c>（3号位 2026-10-20 回执 #4）。</summary>
    private static readonly DateTime BasisTime = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Unspecified);

    private static DemandProtectionTriggerEvaluator.DemandFacts Facts(
        DateTime? customerDueDate = null,
        string? delayStatus = "ON_TIME",
        string? customerTier = "GENERAL",
        string? orderType = "SALES_ORDER",
        string? demandMaturityStatus = "PRE_CONFIRMED")
        => new(customerDueDate, delayStatus, customerTier, orderType, demandMaturityStatus);

    private static LockBlock Block(Action<ProtectionTriggerParams> configure)
    {
        var block = new LockBlock();
        configure(block.Trigger);
        return block;
    }

    [Fact(DisplayName = "未配置任何触发 ⇒ 全部 UNPROTECTED，且不留缺口噪声")]
    public void 未配置触发_全未保护()
    {
        var verdict = DemandProtectionTriggerEvaluator.Evaluate(new LockBlock(), Facts(BasisTime.AddDays(1)), BasisTime);

        verdict.IsProtected.Should().BeFalse();
        verdict.Status.Should().Be("UNPROTECTED");
        verdict.MatchedTrigger.Should().BeNull();
        verdict.Gap.Should().BeNull("没配置触发就谈不上「无法判定」，不该刷缺口日志");
    }

    [Fact(DisplayName = "Lock 子块为空（快照缺块）⇒ 不保护，不抛错")]
    public void Lock块为空_不保护()
    {
        var verdict = DemandProtectionTriggerEvaluator.Evaluate(null, Facts(BasisTime.AddDays(1)), BasisTime);

        verdict.IsProtected.Should().BeFalse();
        verdict.Status.Should().Be("UNPROTECTED");
    }

    [Fact(DisplayName = "T1 剩余交期阈值：18h ≤ 24h ⇒ 命中")]
    public void T1_剩余交期不足阈值_命中()
    {
        var block = Block(t =>
        {
            t.UseRemainingTimeThreshold = true;
            t.RemainingTimeThresholdHours = 24;
        });

        var verdict = DemandProtectionTriggerEvaluator.Evaluate(
            block, Facts(BasisTime.AddHours(18)), BasisTime);

        verdict.IsProtected.Should().BeTrue();
        verdict.MatchedTrigger.Should().Be(DemandProtectionTriggerEvaluator.ProtectionTrigger.RemainingTimeThreshold);
    }

    [Fact(DisplayName = "T1 剩余交期阈值：30h > 24h ⇒ 不命中")]
    public void T1_剩余交期超过阈值_不命中()
    {
        var block = Block(t =>
        {
            t.UseRemainingTimeThreshold = true;
            t.RemainingTimeThresholdHours = 24;
        });

        var verdict = DemandProtectionTriggerEvaluator.Evaluate(
            block, Facts(BasisTime.AddHours(30)), BasisTime);

        verdict.IsProtected.Should().BeFalse();
        verdict.MatchedTrigger.Should().BeNull();
    }

    [Fact(DisplayName = "T1 缺 CustomerDueDate ⇒ 不保护＋留痕（不得静默当已保护）")]
    public void T1_缺交期_不保护但留痕()
    {
        var block = Block(t =>
        {
            t.UseRemainingTimeThreshold = true;
            t.RemainingTimeThresholdHours = 24;
        });

        var verdict = DemandProtectionTriggerEvaluator.Evaluate(block, Facts(customerDueDate: null), BasisTime);

        verdict.IsProtected.Should().BeFalse("判不出不等于受保护 —— B4 §3.1 失败方向＝不保护");
        verdict.Gap.Should().NotBeNullOrWhiteSpace();
        verdict.Gap.Should().Contain("CustomerDueDate");
    }

    [Fact(DisplayName = "T1 基准时点取 DataCutoffTime：同一需求换基准时点，结论必须翻转")]
    public void T1_基准时点不同_结论翻转()
    {
        var block = Block(t =>
        {
            t.UseRemainingTimeThreshold = true;
            t.RemainingTimeThresholdHours = 24;
        });

        // 交期 = 11-09 00:00。基准 11-07 09:00 ⇒ 剩 39h，不命中；基准 11-08 09:00 ⇒ 剩 15h，命中。
        var due = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Unspecified);

        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(due), BasisTime)
            .IsProtected.Should().BeFalse("距基准 39 小时，超过 24 小时阈值");

        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(due), BasisTime.AddDays(1))
            .IsProtected.Should().BeTrue("距基准 15 小时，已进 24 小时阈值");
    }

    [Fact(DisplayName = "T2 已延期：FIRST_DELAY / REPEATED_DELAY 均命中，ON_TIME 不命中")]
    public void T2_延迟状态触发()
    {
        var block = Block(t => t.ProtectDelayed = true);

        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(delayStatus: "FIRST_DELAY"), BasisTime)
            .IsProtected.Should().BeTrue();
        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(delayStatus: "REPEATED_DELAY"), BasisTime)
            .IsProtected.Should().BeTrue();
        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(delayStatus: "ON_TIME"), BasisTime)
            .IsProtected.Should().BeFalse();
    }

    [Fact(DisplayName = "T3 客户等级：VIP 命中，GENERAL 不命中，大小写不同不命中（精确等值）")]
    public void T3_客户等级触发_精确等值()
    {
        var block = Block(t =>
        {
            t.ProtectVipTier = true;
            t.VipTierValue = "VIP";
        });

        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(customerTier: "VIP"), BasisTime)
            .IsProtected.Should().BeTrue();
        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(customerTier: "GENERAL"), BasisTime)
            .IsProtected.Should().BeFalse();

        // 大小写不同即不命中：B4 §3.2 T3 写的是精确等值。宽容匹配会把「配置写错」永远藏起来。
        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(customerTier: "vip"), BasisTime)
            .IsProtected.Should().BeFalse("精确等值语义；大小写不一致应由 §3.4 诊断暴露，而不是在代码里猜");
    }

    [Fact(DisplayName = "T4 订单类型：配置写运行词表则命中；写 \"SO\" 与 SALES_ORDER 不匹配（线上空转形态）")]
    public void T4_订单类型_配置词表决定命中()
    {
        var runtimeVocab = Block(t => t.ProtectedOrderTypes = ["SALES_ORDER", "PRODUCTION_INSTRUCTION"]);
        DemandProtectionTriggerEvaluator.Evaluate(runtimeVocab, Facts(orderType: "SALES_ORDER"), BasisTime)
            .IsProtected.Should().BeTrue("配置按运行词表写，应命中");

        // 这正是 ParameterSetVersion 现存配置的形态：ProtectedOrderTypes=["SO"]。必须不命中，
        // 由 §3.4 诊断报 PROTECTION_TRIGGER_UNEVALUABLE —— 不许在代码里把 "SO" 猜成 SALES_ORDER。
        var legacyVocab = Block(t => t.ProtectedOrderTypes = ["SO"]);
        DemandProtectionTriggerEvaluator.Evaluate(legacyVocab, Facts(orderType: "SALES_ORDER"), BasisTime)
            .IsProtected.Should().BeFalse("[\"SO\"] 与运行词表 SALES_ORDER 不等值，绝不能在此处猜配");
    }

    [Fact(DisplayName = "T5 需求成熟度：命中值集即保护（2026-11-06 补加的成熟度触发）")]
    public void T5_成熟度触发()
    {
        var block = Block(t =>
        {
            t.ProtectMaturityStatus = true;
            t.MaturityStatusValues = ["PRE_CONFIRMED", "FORECAST"];
        });

        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(demandMaturityStatus: "FORECAST"), BasisTime)
            .IsProtected.Should().BeTrue();
        DemandProtectionTriggerEvaluator.Evaluate(block, Facts(demandMaturityStatus: "CONFIRMED"), BasisTime)
            .IsProtected.Should().BeFalse();
    }

    [Fact(DisplayName = "多触发合成：任一命中即 PROTECTED，不加权、不设第二套优先级")]
    public void 多触发合成_任一命中即保护()
    {
        var block = Block(t =>
        {
            t.ProtectDelayed = true;
            t.ProtectVipTier = true;
            t.VipTierValue = "VIP";
            t.ProtectedOrderTypes = ["SALES_ORDER"];
        });

        // 未延期、非 VIP，但订单类型命中 ⇒ 仍应 PROTECTED（B4 §3.2 合成规则）。
        var verdict = DemandProtectionTriggerEvaluator.Evaluate(
            block,
            Facts(delayStatus: "ON_TIME", customerTier: "GENERAL", orderType: "SALES_ORDER"),
            BasisTime);

        verdict.IsProtected.Should().BeTrue();
        verdict.MatchedTrigger.Should().Be(DemandProtectionTriggerEvaluator.ProtectionTrigger.OrderTypes);
    }

    [Fact(DisplayName = "DNF 触发组非空 ⇒ 显式抛错，不静默回退旧布尔簇")]
    public void DNF触发组_显式抛错()
    {
        var block = Block(t =>
        {
            t.ProtectDelayed = true;
            t.TriggerConditionGroups =
            [
                new SegmentConditionGroup
                {
                    Conditions =
                    [
                        new SegmentMatchCondition
                        {
                            Field = DemandField.DelayStatus,
                            Operator = ConditionOperator.In,
                            Value = new[] { "FIRST_DELAY", "REPEATED_DELAY" },
                        },
                    ],
                },
            ];
        });

        var act = () => DemandProtectionTriggerEvaluator.Evaluate(block, Facts(), BasisTime);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*TriggerConditionGroups*")
            .WithMessage("*拒绝静默回退*");
    }

    [Fact(DisplayName = "IsConfigured：只有真能被评价的配置才算「已配置」（供 §3.4 零命中诊断使用）")]
    public void IsConfigured_判据()
    {
        DemandProtectionTriggerEvaluator.IsConfigured(new ProtectionTriggerParams(), DemandProtectionTriggerEvaluator.ProtectionTrigger.Delayed)
            .Should().BeFalse();

        DemandProtectionTriggerEvaluator.IsConfigured(new ProtectionTriggerParams { ProtectDelayed = true }, DemandProtectionTriggerEvaluator.ProtectionTrigger.Delayed)
            .Should().BeTrue();

        // ProtectVipTier=true 但 VipTierValue 为空 ⇒ 判据不完整，不算「已配置」，否则会误报「配置了却零命中」。
        DemandProtectionTriggerEvaluator.IsConfigured(
            new ProtectionTriggerParams { ProtectVipTier = true, VipTierValue = null },
            DemandProtectionTriggerEvaluator.ProtectionTrigger.VipTier)
            .Should().BeFalse();
    }
}
