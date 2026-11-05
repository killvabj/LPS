using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R08-O02 验收**（1号位 2026-10-31，应 0号位《R01～R12 未闭环最小任务单》P0 第 1 条）。
///
/// <para>0号位 原文要求：</para>
/// <para><b>交付</b>：「以冻结完整键区分 Dept/Stage/Operation/Resource/From/To，碰撞不得首条胜出」；</para>
/// <para><b>验收</b>：「相同短键不同部门 30/40 分钟正确选择」。</para>
///
/// <para><b>为什么单开一个测试类</b>：`SetupTransitionRuleProjectionTests` 测的是**装载投影**
/// （库里的行 → 快照），从来没有测过「运行时拿同一个短键、不同部门，到底取到哪个分钟数」。
/// 而 0号位 点名的病灶恰恰在运行时这一侧：键退化成短键 ⇒ 谁生效完全取决于载荷到达顺序。
/// 这层性质只能靠断言守住，装载侧测再多也替代不了。</para>
///
/// <para><b>同时钉死三条不许退化的边界</b>：</para>
/// <list type="number">
///   <item>同键异值 ⇒ **整条剔除**，不许「首条胜出」（这是整改的核心，不是附带）；</item>
///   <item>同键**同值**重复 ⇒ **无害**，不许因为「见过重复」就把该键一起剔了（防过度整改）；</item>
///   <item>取不到部门 ⇒ `SourceUnavailable`，**不许**被洗成「无前产品」的 `InitialState`。</item>
/// </list>
/// </summary>
public class SetupDeptStageKeyAcceptanceTests
{
    private const int ResourceA = 7632;
    private const int FromMat = 1001;
    private const int ToMat = 1002;

    // ════════════════════════════════════════════════════════════════════
    //  0号位 原文验收：相同短键不同部门 ⇒ 30 / 40
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void S_01_相同短键不同部门_DEFAULT层必须分别取到30和40()
    {
        // 短键完全一样：Stage=S1 / 工序=OP10 / 设备=7632；只有部门不同。
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
            DefaultRule(dept: 2, minutes: 40m),
        });

        Assert.Equal(30m, Resolve(defaults, exact, dept: 1).SetupMinutes);
        Assert.Equal(40m, Resolve(defaults, exact, dept: 2).SetupMinutes);

        // 两层都必须是「命中默认规则」，不能一路掉到 0 分钟。
        Assert.Equal(SetupOptimizer.SetupOutcome.DefaultHit, Resolve(defaults, exact, dept: 1).Outcome);
        Assert.Equal(SetupOptimizer.SetupOutcome.DefaultHit, Resolve(defaults, exact, dept: 2).Outcome);
    }

    [Fact]
    public void S_02_相同短键不同部门_EXACT层也必须分开()
    {
        // EXACT 的短键同样只有部门不同 ⇒ 6 维键必须把部门吃进去。
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            ExactRule(dept: 1, minutes: 30m),
            ExactRule(dept: 2, minutes: 40m),
        });

        Assert.Equal(30m, Resolve(defaults, exact, dept: 1).SetupMinutes);
        Assert.Equal(40m, Resolve(defaults, exact, dept: 2).SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.ExactHit, Resolve(defaults, exact, dept: 2).Outcome);
    }

    [Fact]
    public void S_03_同部门不同Stage也必须分开_Stage是键维不是描述字段()
    {
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m, stage: "S1"),
            DefaultRule(dept: 1, minutes: 40m, stage: "S2"),
        });

        Assert.Equal(30m, Resolve(defaults, exact, dept: 1, stage: "S1").SetupMinutes);
        Assert.Equal(40m, Resolve(defaults, exact, dept: 1, stage: "S2").SetupMinutes);
    }

    // ════════════════════════════════════════════════════════════════════
    //  「碰撞不得首条胜出」
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void S_04_同键异值必须整条剔除_不许首条胜出()
    {
        // 两条完全同键（含部门、Stage），值 30 / 40。
        // 旧实现 TryAdd 会让先到的那条生效 ⇒ 结果取决于载荷顺序，这是被点名禁止的。
        var (exact, defaults, report) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
            DefaultRule(dept: 1, minutes: 40m),
        });

        Assert.Equal(1, report.DefaultConflictKeyCount);
        Assert.True(report.HasProblems);
        Assert.NotEmpty(report.ConflictExamples);

        // 该键整条被剔除 ⇒ 解析落到第三层「规则缺失 0 分钟」，而**不是** 30 或 40。
        var res = Resolve(defaults, exact, dept: 1);
        Assert.Equal(0m, res.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.RuleMissing, res.Outcome);
        Assert.Equal("WARNING", res.TraceLevel);

        // 反向断言：无论载荷顺序怎么排，结果都不变（这才是「不首条胜出」的判据）。
        var (exact2, defaults2, report2) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 40m),
            DefaultRule(dept: 1, minutes: 30m),
        });

        Assert.Equal(1, report2.DefaultConflictKeyCount);
        Assert.Equal(0m, Resolve(defaults2, exact2, dept: 1).SetupMinutes);
    }

    [Fact]
    public void S_05_同键同值重复是无害的_不许连带剔除()
    {
        // 防过度整改：值一样就不存在「无法判定真值」，该键必须照常生效。
        var (exact, defaults, report) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
            DefaultRule(dept: 1, minutes: 30m),
        });

        Assert.Equal(0, report.DefaultConflictKeyCount);
        Assert.Equal(1, report.SameValueDuplicateCount);
        Assert.False(report.HasProblems);
        Assert.Equal(30m, Resolve(defaults, exact, dept: 1).SetupMinutes);
    }

    [Fact]
    public void S_06_另一个部门不受同键异值剔除的牵连()
    {
        // 只剔撞的那个键；部门 2 的规则完好。
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
            DefaultRule(dept: 1, minutes: 40m),
            DefaultRule(dept: 2, minutes: 40m),
        });

        Assert.Equal(0m, Resolve(defaults, exact, dept: 1).SetupMinutes);   // 被剔
        Assert.Equal(40m, Resolve(defaults, exact, dept: 2).SetupMinutes);  // 完好
    }

    // ════════════════════════════════════════════════════════════════════
    //  来源不可用 ≠ 可信无前态
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void S_07_取不到部门必须判SourceUnavailable_不许洗成无前产品()
    {
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
        });

        // 部门上下文缺失，但「有前产品」。
        var noDept = SetupOptimizer.ResolveSetupCore(
            OperationCode, productionDepartmentId: null, StageCode, ResourceA,
            FromMat, ToMat, exact, defaults);

        Assert.Equal(SetupOptimizer.SetupOutcome.SourceUnavailable, noDept.Outcome);
        Assert.Equal(0m, noDept.SetupMinutes);
        Assert.Equal("WARNING", noDept.TraceLevel);
        Assert.Equal(SetupOptimizer.RuleSourceUnavailableType, noDept.ExplanationType);

        // 落库值域仍是原 4 态里的那一档，值域不动。
        Assert.Equal(SetupOptimizer.RuleMissingZeroFallbackType,
            SetupOptimizer.SetupOutcomeToSource(noDept.Outcome));

        // 对照组：真·无前产品 ⇒ InitialState，与上面严格分开。
        var noFrom = SetupOptimizer.ResolveSetupCore(
            OperationCode, productionDepartmentId: 1, StageCode, ResourceA,
            fromMaterialId: null, toMaterialId: ToMat, exact, defaults);

        Assert.Equal(SetupOptimizer.SetupOutcome.InitialState, noFrom.Outcome);
        Assert.Equal(SetupOptimizer.InitialSetupStateType, noFrom.ExplanationType);
        Assert.NotEqual(noDept.ExplanationType, noFrom.ExplanationType);
    }

    [Fact]
    public void S_08_缺部门时不许命中任何有规则的分钟数()
    {
        // 部门 1 确实配了 30 分钟；但本 Task 取不到部门 ⇒ 必须 0 分钟 + WARNING，
        // 绝不能「猜个部门」把 30 分钟套上去。
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 30m),
        });

        var res = SetupOptimizer.ResolveSetupCore(
            OperationCode, productionDepartmentId: null, StageCode, ResourceA,
            FromMat, ToMat, exact, defaults);

        Assert.Equal(0m, res.SetupMinutes);
        Assert.NotEqual(SetupOptimizer.SetupOutcome.DefaultHit, res.Outcome);
        Assert.NotEqual(SetupOptimizer.SetupOutcome.ExactHit, res.Outcome);
    }

    // ════════════════════════════════════════════════════════════════════
    //  同产品 A→A 仍是 0（不许被默认规则误套）
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void S_09_同产品连续仍按0分钟_不被DEFAULT规则误套()
    {
        var (exact, defaults, _) = SetupOptimizer.BuildRuleLookups(new List<SetupTransitionRuleSnapshot>
        {
            DefaultRule(dept: 1, minutes: 40m),
        });

        var res = SetupOptimizer.ResolveSetupCore(
            OperationCode, productionDepartmentId: 1, StageCode, ResourceA,
            fromMaterialId: ToMat, toMaterialId: ToMat, exact, defaults);

        Assert.Equal(0m, res.SetupMinutes);
        Assert.Equal(SetupOptimizer.SetupOutcome.SameProductZero, res.Outcome);
        // 同键还有 DEFAULT 规则 ⇒ 必须留痕，否则「DEFAULT + 0」与「DEFAULT 显式 0」同形追不出来。
        Assert.Equal(SetupOptimizer.SameProductDefaultZeroType, res.ExplanationType);
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    private const string StageCode = "S1";
    private const string OperationCode = "OP10";

    private static SetupTransitionRuleSnapshot DefaultRule(int dept, decimal minutes, string stage = StageCode)
        => new()
        {
            RuleType = "DEFAULT",
            ProductionDepartmentId = dept,
            StageCode = stage,
            OperationCode = OperationCode,
            ResourceId = ResourceA,
            FromMaterialId = null,
            ToMaterialId = null,
            SetupMinutes = minutes,
        };

    private static SetupTransitionRuleSnapshot ExactRule(int dept, decimal minutes, string stage = StageCode)
        => new()
        {
            RuleType = "EXACT",
            ProductionDepartmentId = dept,
            StageCode = stage,
            OperationCode = OperationCode,
            ResourceId = ResourceA,
            FromMaterialId = FromMat,
            ToMaterialId = ToMat,
            SetupMinutes = minutes,
        };

    private static SetupOptimizer.SetupResolution Resolve(
        IReadOnlyDictionary<SetupOptimizer.SetupDefaultKey, decimal> defaults,
        IReadOnlyDictionary<SetupOptimizer.SetupExactKey, decimal> exact,
        int dept,
        string stage = StageCode)
        => SetupOptimizer.ResolveSetupCore(
            OperationCode, dept, stage, ResourceA, FromMat, ToMat, exact, defaults);
}
