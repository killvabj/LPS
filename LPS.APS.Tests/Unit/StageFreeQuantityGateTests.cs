using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Stage 闸纯算式（5号位 2026-11-07 §一.3）：`free(PI,M,S) = Max(Baselines − E − 第三项, 0)`。
///
/// <para>
/// **为什么这组必须存在**：这段决定「本轮还能建多少单」。判错不会编译报错、也不会抛异常，
/// 只会**多建或少建**，且现场看到的只是「某 PI 怎么没建单 / 怎么建多了」。
/// </para>
/// <para>
/// **刻意反向断言的三处**：
/// <list type="bullet">
///   <item>`E > Baselines` 时必须 clamp 到 0，**不得**产出负封顶（负封顶会把闸算反，变成「还能建负数」）。</item>
///   <item>第三项**减在封顶上、不减在提案上** —— 本类只出封顶，提案由调用方 `min` 进去；
///     若哪天有人把第三项挪去减 `final.Quantity`，就是双重扣减，这一组看不出，故在此写死「封顶 = Baselines − E − 第三项」的期望值。</item>
///   <item>第三项有、基线无 ⇒ **不扣**且计数上报 —— 「未知 ≠ 零」，不得把未知键当 0 封顶静默建单。</item>
/// </list>
/// </para>
/// </remarks>
public class StageFreeQuantityGateTests
{
    private static PiStageQuantityBaseline Baseline(
        string pi, int materialId, string stage, decimal stageFreeEligible, decimal alreadyCommitted)
        => new()
        {
            ProductionInstructionNo = pi,
            MaterialId              = materialId,
            StageCode               = stage,
            StageFreeEligibleQty    = stageFreeEligible,
            AlreadyCommittedQty     = alreadyCommitted,
        };

    private static StageFreeQuantityGate.Key K(string pi, int materialId, string stage)
        => new(pi, materialId, stage);

    [Fact(DisplayName = "封顶 = Baselines − E（不是毛量，也不是剩余量）")]
    public void 封顶_等于基线减已占()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI001", 1001, "S10", stageFreeEligible: 500m, alreadyCommitted: 120m),
        ]);

        cap[K("PI001", 1001, "S10")].Should().Be(380m,
            "Stage 闸的封顶是「毛量 − 入口已占」，E 已在 5号位 侧由 IN_PROGRESS 工单算好随基线传入");
    }

    [Fact(DisplayName = "E 大于毛量 ⇒ 封顶 clamp 到 0，绝不产负值")]
    public void 已占超毛量_封顶为零()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI002", 1002, "S20", stageFreeEligible: 100m, alreadyCommitted: 260m),
        ]);

        cap[K("PI002", 1002, "S20")].Should().Be(0m,
            "位置量与执行量可能重叠而不可信；保守取 0 —— 负封顶会让 `min(提案, 封顶)` 永远得负、整键不建单且无痕迹");
    }

    [Fact(DisplayName = "PI 或 StageCode 为空/纯空白 ⇒ 组不成三键，基线不收（不是当 0 收）")]
    public void 空键基线_不入封顶表()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("", 1003, "S30", 500m, 0m),
            Baseline("PI003", 1003, "   ", 500m, 0m),
            Baseline("PI003", 1003, "S30", 500m, 0m),
        ]);

        cap.Should().HaveCount(1, "空 PI / 空 Stage 的基线收进来就是一个谁也不匹配的键，只会掩盖问题");
        cap.Should().ContainKey(K("PI003", 1003, "S30"));
    }

    [Fact(DisplayName = "基线列表为 null ⇒ 空封顶表（调用方据此不施 Stage 闸，只走总量闸）")]
    public void 无基线_空表()
    {
        StageFreeQuantityGate.BuildCap(null).Should().BeEmpty();
        StageFreeQuantityGate.BuildCap([]).Should().BeEmpty();
    }

    [Fact(DisplayName = "第三项从封顶上扣：封顶 380 − 第三项 100 ⇒ 280")]
    public void 第三项扣在封顶上()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI004", 1004, "S40", stageFreeEligible: 500m, alreadyCommitted: 120m),
        ]);

        var r = StageFreeQuantityGate.ApplyThirdTerm(cap,
        [
            new KeyValuePair<StageFreeQuantityGate.Key, decimal>(K("PI004", 1004, "S40"), 100m),
        ]);

        cap[K("PI004", 1004, "S40")].Should().Be(280m,
            "封顶 = 500 − 120 − 100；第三项是「我方已建单、MES 证据未到」，必须占掉封顶");
        r.AppliedGroups.Should().Be(1);
        r.WithoutBaselineGroups.Should().Be(0);
    }

    [Fact(DisplayName = "第三项超过封顶 ⇒ clamp 到 0，不产负值")]
    public void 第三项超封顶_为零()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI005", 1005, "S50", stageFreeEligible: 200m, alreadyCommitted: 0m),
        ]);

        StageFreeQuantityGate.ApplyThirdTerm(cap,
        [
            new KeyValuePair<StageFreeQuantityGate.Key, decimal>(K("PI005", 1005, "S50"), 350m),
        ]);

        cap[K("PI005", 1005, "S50")].Should().Be(0m);
    }

    [Fact(DisplayName = "第三项有、基线无 ⇒ 不扣且计数上报（未知 ≠ 零，不得当 0 封顶静默建单）")]
    public void 无基线的第三项_不扣但计数()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI006", 1006, "S60", stageFreeEligible: 400m, alreadyCommitted: 0m),
        ]);

        var r = StageFreeQuantityGate.ApplyThirdTerm(cap,
        [
            new KeyValuePair<StageFreeQuantityGate.Key, decimal>(K("PI006", 1006, "S60"), 50m),
            new KeyValuePair<StageFreeQuantityGate.Key, decimal>(K("PI999", 1099, "S99"), 70m),
        ]);

        cap[K("PI006", 1006, "S60")].Should().Be(350m, "有基线的照扣");
        cap.Should().NotContainKey(K("PI999", 1099, "S99"),
            "无基线的键**不得**被凭空造一个 0 封顶 —— 那会让这个 PI 整键停止建单且看不见");
        r.AppliedGroups.Should().Be(1);
        r.WithoutBaselineGroups.Should().Be(1, "必须报数，由调用方按 (PI,M,S) 留痕（5号位 确认 #4）");
    }

    [Fact(DisplayName = "没有第三项 ⇒ 封顶原样，零改动")]
    public void 无第三项_封顶不变()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI007", 1007, "S70", 300m, 40m),
        ]);

        var r = StageFreeQuantityGate.ApplyThirdTerm(cap, []);

        cap[K("PI007", 1007, "S70")].Should().Be(260m);
        r.Should().Be(new StageFreeQuantityGate.ThirdTermApplyResult(0, 0));
    }

    [Fact(DisplayName = "同键重复基线 ⇒ 后者覆盖、键不翻倍（聚合粒度是三键，不是行）")]
    public void 重复基线_键唯一()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI008", 1008, "S80", 100m, 0m),
            Baseline("PI008", 1008, "S80", 250m, 50m),
        ]);

        cap.Should().HaveCount(1);
        cap[K("PI008", 1008, "S80")].Should().Be(200m);
    }

    [Fact(DisplayName = "三键区分物料与 Stage：同 PI 不同物料/Stage 互不影响")]
    public void 三键区分()
    {
        var cap = StageFreeQuantityGate.BuildCap(
        [
            Baseline("PI009", 1009, "S90A", 100m, 0m),
            Baseline("PI009", 1009, "S90B", 100m, 0m),
            Baseline("PI009", 1010, "S90A", 100m, 0m),
        ]);

        StageFreeQuantityGate.ApplyThirdTerm(cap,
        [
            new KeyValuePair<StageFreeQuantityGate.Key, decimal>(K("PI009", 1009, "S90A"), 60m),
        ]);

        cap[K("PI009", 1009, "S90A")].Should().Be(40m);
        cap[K("PI009", 1009, "S90B")].Should().Be(100m, "Stage 不同则封顶独立");
        cap[K("PI009", 1010, "S90A")].Should().Be(100m, "物料不同则封顶独立");
        cap.Values.Should().HaveCount(3);
        cap.Keys.Select(k => k.StageCode).Should().BeEquivalentTo(["S90A", "S90B", "S90A"]);
    }
}
