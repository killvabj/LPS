using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 计划良率**缺配三态**只读计数测试（3号位 2026-10-25《缺配三态词表说明 v1.0》§三／§四）。
///
/// 断言的正是本改动**声明的目的**（不是「跑绿即有效」）：
///   ① 三态口径与词表一致 —— 缺配（出口 A 未配置 ＋ 出口 C 无匹配）、越界（出口 B）、重复（同键多规则）；
///   ② **只读**：判定返回值与改造前逐例相同（100 无损兜底不被计数改变）；
///   ③ 计数口径＝**物料去重**，重复查询不虚增；
///   ④ Stage 级规则在本口径下**被跳过**，不得被误计成越界（既有行为）。
///
/// 被测对象为 `PeggingOrchestrator.ClassifyPlanningYield`（`PlanningYieldPercent` 的唯一判定实现）
/// 与 `PlanningYieldDiagnostics` 计数器 —— 二者都是 `internal`，经 `InternalsVisibleTo` 对测试可见。
/// ⚠ 只覆盖内存对象，不触库、不跑 Run。
/// </summary>
public class PlanningYieldDiagnosticsTests
{
    private static PlanningYieldRule Rule(string materialId, decimal yieldPercent, string? stageCode = null)
        => new() { MaterialId = materialId, StageCode = stageCode, YieldPercent = yieldPercent };

    private static PlanningYieldRule[] Rules(params PlanningYieldRule[] rules) => rules;

    // ── 判定三态（值 + 状态） ─────────────────────────────────────────────

    [Fact]
    public void 整块未配置_返回100且判缺配()
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("M001", Rules());

        percent.Should().Be(100m, "未配置不得放大加工量（冻结 §7.5 无损兜底）");
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Missing, "出口 A：PlanningYields 整块未配置 ⇒ 缺配");
    }

    [Fact]
    public void 快照为null_返回100且判缺配()
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("M001", null);

        percent.Should().Be(100m);
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Missing);
    }

    [Fact]
    public void 无任一规则匹配_返回100且判缺配()
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("M999", Rules(Rule("M001", 90m)));

        percent.Should().Be(100m);
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Missing, "出口 C：无匹配规则 ⇒ 缺配");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public void 命中但越界_返回100且判越界(decimal yieldPercent)
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("M001", Rules(Rule("M001", yieldPercent)));

        percent.Should().Be(100m, "越界 ⇒ 跳过并落无损兜底");
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.OutOfRange,
            "出口 B：越界与无匹配原实现同返回 100，词表要求**分开计**，不得落成缺配");
    }

    [Fact]
    public void 合法规则_返回实际值且状态非三态()
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("M001", Rules(Rule("M001", 95m)));

        percent.Should().Be(95m);
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Configured,
            "确配 95% 与「未配」必须可区分（本改动的存在理由）");
    }

    [Fact]
    public void 越界与合法并存_返回合法值且不判越界()
    {
        // 行为不变：命中第一条合法规则即返回；越界那条被跳过，但**整物料有可用规则** ⇒ 不算越界态。
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield(
            "M001", Rules(Rule("M001", 120m), Rule("M001", 90m)));

        percent.Should().Be(90m);
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Configured);
    }

    [Fact]
    public void Stage级规则被跳过_判缺配而非越界()
    {
        // 既有行为：只认 StageCode 为空的 Material 级默认，Stage 级规则 `continue` 跳过。
        // 因此即便该 Stage 级规则本身合法，本物料在**需求级口径**下仍是「无可用规则」⇒ 缺配。
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield(
            "M001", Rules(Rule("M001", 80m, stageCode: "S10")));

        percent.Should().Be(100m);
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Missing);
    }

    [Fact]
    public void 物料码大小写不同_仍命中同一条规则()
    {
        var (state, percent) = PeggingOrchestrator.ClassifyPlanningYield("m001", Rules(Rule("M001", 88m)));

        percent.Should().Be(88m, "比较用 OrdinalIgnoreCase，与改造前一致");
        state.Should().Be(PeggingOrchestrator.PlanningYieldState.Configured);
    }

    // ── 计数器口径 ───────────────────────────────────────────────────────

    [Fact]
    public void 计数器_同一物料多次查询按物料去重只计一次()
    {
        var diag = new PeggingOrchestrator.PlanningYieldDiagnostics();

        for (var i = 0; i < 3; i++)
        {
            diag.Query("M001");
            diag.Miss("M001");
        }

        diag.QueriedCount.Should().Be(1, "计数口径＝物料去重，不是调用次数");
        diag.MissingCount.Should().Be(1);
        diag.MissingSamples.Should().ContainSingle().Which.Should().Be("M001");
    }

    [Fact]
    public void 计数器_缺配与越界分桶互不污染()
    {
        var diag = new PeggingOrchestrator.PlanningYieldDiagnostics();

        diag.Query("M001"); diag.Miss("M001");
        diag.Query("M002"); diag.OutOfRange("M002");

        diag.QueriedCount.Should().Be(2);
        diag.MissingCount.Should().Be(1);
        diag.OutOfRangeCount.Should().Be(1);
        diag.MissingSamples.Should().Equal("M001");
        diag.OutOfRangeSamples.Should().Equal("M002");
    }

    // ── 重复态（同 (MaterialId, StageCode) 多规则） ───────────────────────

    [Fact]
    public void 重复态_同物料同Stage多规则计一键_单规则不计()
    {
        var duplicates = PeggingOrchestrator.CountPlanningYieldDuplicateKeys(Rules(
            Rule("M001", 90m),                              // 同键第 1 条
            Rule("m001", 80m),                              // 同键第 2 条（大小写归一 ⇒ 同一键）
            Rule("M002", 95m),                              // 不同键，单规则
            Rule("M003", 70m, stageCode: "S10"),
            Rule("M003", 60m, stageCode: "S10")));          // 同 (物料, Stage) 多规则

        duplicates.Should().Be(2,
            "重复态按 (MaterialId, StageCode) 计数：(M001, 空) 与 (M003, S10) 各一，M002 不算");
    }

    [Fact]
    public void 重复态_同物料不同Stage不算重复()
    {
        var duplicates = PeggingOrchestrator.CountPlanningYieldDuplicateKeys(Rules(
            Rule("M001", 90m),
            Rule("M001", 80m, stageCode: "S10")));

        duplicates.Should().Be(0, "Stage 不同 ⇒ 不同键，不是重复");
    }

    [Fact]
    public void 重复态_无规则时为零()
    {
        PeggingOrchestrator.CountPlanningYieldDuplicateKeys(Rules()).Should().Be(0);
        PeggingOrchestrator.CountPlanningYieldDuplicateKeys(null).Should().Be(0);
    }
}
