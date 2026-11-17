using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Models;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 供给可见上界判定测试（C10 整改，2026-11-12；依据 0号位 2026-11-12 请裁回复 §二②）。
///
/// <para><b>本测试断言的是「声明的目的」，不是「跑得过」</b>：</para>
/// <list type="number">
///   <item><b>核心回归</b>：数据截止时点之后、计划窗口终点之前的**合法未来供给必须可见** ——
///         这正是原缺陷（13,891 行 / 62.41M 被 `availableAt > cutoff` 丢弃）的判据。</item>
///   <item><b>不得自建第二时间源</b>：拿不到计划窗口时**回落为数据截止时点**（不是 `cutoff+90天`，
///         也不是无条件放行）。</item>
///   <item><b>不得无限放行</b>：窗口终点之外的供给仍排除。</item>
///   <item><b>不得反向缩小</b>：窗口终点早于数据截止时点时，不得把「截止前本已可用」的供给截掉
///         （那比原缺陷更重）。</item>
///   <item><b>两类空不得混源</b>：`null`（算不出时点）不归本闸，与「远未来」分开。</item>
/// </list>
/// </summary>
public class SupplyVisibilityWindowTests
{
    /// <summary>本 Run 数据截止时点（＝`ScheduleRun.DataCutoffTime`；下面全部用例的基准）。</summary>
    private static readonly DateTime Cutoff = new(2026, 11, 11, 8, 16, 26);

    // ── ① 核心回归：cutoff 之后、窗口终点之前 ⇒ 可见 ────────────────────────────

    [Fact(DisplayName = "核心回归：数据截止之后的合法未来供给落在计划窗口内 ⇒ 判为可见（不再按 cutoff 丢弃）")]
    public void FutureSupply_AfterCutoffButWithinPlanWindow_IsVisible()
    {
        // 现场口径：cutoff = 2026-11-11 08:16；PO ETA = 2026-11-22；计划窗口终点 = 2027-02-09（90 天窗）。
        var visibilityEnd = SupplyVisibilityWindow.ResolveEnd(new DateTime(2027, 2, 9), Cutoff);
        var poEta = new DateTime(2026, 11, 22);

        SupplyVisibilityWindow.IsBeyond(poEta, visibilityEnd).Should().BeFalse(
            "数据截止时点不是供给可见上限（0号位 2026-11-12 §二②）；这是 13,891 行/62.41M 被误丢的同一个判据");
    }

    [Fact(DisplayName = "核心回归：同一 ETA 在旧口径（cutoff）下会被丢弃，用于证明本改动确实翻转了行为")]
    public void SameEta_UnderOldCutoffRule_WouldHaveBeenDropped()
    {
        var poEta = new DateTime(2026, 11, 22);

        // 旧口径 = 直接拿 cutoff 当上界（等价原 `availableAt > cutoff → continue`）。
        var oldVisibilityEnd = Cutoff;
        SupplyVisibilityWindow.IsBeyond(poEta, oldVisibilityEnd).Should().BeTrue(
            "旧口径确实会丢 —— 若此断言为假，说明本用例已失去回归意义");

        var newVisibilityEnd = SupplyVisibilityWindow.ResolveEnd(new DateTime(2027, 2, 9), Cutoff);
        SupplyVisibilityWindow.IsBeyond(poEta, newVisibilityEnd).Should().BeFalse("新口径不再丢");
    }

    // ── ② 不得自建第二时间源 ───────────────────────────────────────────────────

    [Fact(DisplayName = "拿不到计划窗口（null）⇒ 回落数据截止时点，不做默认外推")]
    public void NoPlanWindow_FallsBackToCutoff()
    {
        SupplyVisibilityWindow.ResolveEnd(null, Cutoff).Should().Be(Cutoff,
            "0号位 明令不得自建第二时间源：缺事实时维持原行为，由调用方留 WARN");
    }

    [Fact(DisplayName = "计划窗口为 default(DateTime)（未设）⇒ 同样回落数据截止时点")]
    public void DefaultPlanWindow_FallsBackToCutoff()
    {
        SupplyVisibilityWindow.ResolveEnd(default(DateTime), Cutoff).Should().Be(Cutoff);
    }

    [Fact(DisplayName = "回落值不得是 cutoff+90天：窗口缺失时第 100 天的供给仍应不可见")]
    public void Fallback_IsNotCutoffPlus90Days()
    {
        var visibilityEnd = SupplyVisibilityWindow.ResolveEnd(null, Cutoff);
        var day100 = Cutoff.AddDays(100);

        visibilityEnd.Should().Be(Cutoff);
        SupplyVisibilityWindow.IsBeyond(day100, visibilityEnd).Should().BeTrue(
            "回落必须严格等于 cutoff；若等于 cutoff+90 就是自造第二时间源");
    }

    // ── ③ 不得无限放行，也不得反向缩小 ─────────────────────────────────────────

    [Fact(DisplayName = "窗口终点之外的供给仍排除（不得无限放行未来供给）")]
    public void Supply_BeyondPlanWindowEnd_IsExcluded()
    {
        var visibilityEnd = new DateTime(2027, 2, 9);

        SupplyVisibilityWindow.IsBeyond(new DateTime(2027, 5, 1), visibilityEnd).Should().BeTrue();
    }

    [Fact(DisplayName = "窗口终点早于数据截止时点 ⇒ 取截止时点，不得反向缩小可见性")]
    public void StalePlanWindow_DoesNotShrinkVisibility()
    {
        // 旧 PV／补跑：窗口终点（2026-10-01）已早于本次数据截止（2026-11-11）。
        var visibilityEnd = SupplyVisibilityWindow.ResolveEnd(new DateTime(2026, 10, 1), Cutoff);

        visibilityEnd.Should().Be(Cutoff, "可见性至少要覆盖到数据切面本身，否则会把「截止前已可用」的供给也截掉");
        SupplyVisibilityWindow.IsBeyond(new DateTime(2026, 11, 5), visibilityEnd)
            .Should().BeFalse("2026-11-05 早于 11-11 数据截止 ⇒ 必须可见");
    }

    [Fact(DisplayName = "窗口终点与数据截止时点相等 ⇒ 取截止时点（边界不产生额外放行）")]
    public void PlanWindowEqualsCutoff_UsesCutoff()
    {
        SupplyVisibilityWindow.ResolveEnd(Cutoff, Cutoff).Should().Be(Cutoff);
    }

    // ── ④ 边界与空值 ───────────────────────────────────────────────────────────

    [Theory(DisplayName = "边界半开区间：恰好等于上界仍可见，越过 1 秒即不可见")]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public void Boundary_Semantics(int offsetSeconds, bool expectedBeyond)
    {
        var visibilityEnd = new DateTime(2027, 2, 9);
        var availableAt = visibilityEnd.AddSeconds(offsetSeconds);

        SupplyVisibilityWindow.IsBeyond(availableAt, visibilityEnd).Should().Be(expectedBeyond);
    }

    [Fact(DisplayName = "null 可用时点不归本闸（走「无合法可用时点」闸，两类空不得混源）")]
    public void NullAvailableAt_IsNotHandledByThisGate()
    {
        SupplyVisibilityWindow.IsBeyond(null, new DateTime(2027, 2, 9)).Should().BeFalse(
            "null 由 0号位 v1.1 §二A／§三「第一拍」另行阻断；若在此判为「远未来」，诊断分类就错了");
    }

    // ── ⑤ 受控对照（0号位 点名的场景）：手工时点 → AvailableTime 计算 → 可见性判定，三段串起来跑 ──

    private static FrozenStrategySnapshot Snapshot(
        PurchaseLtRule[]? ltRules = null,
        decimal marginPercent = 0m)
        => new()
        {
            Procurement = new ProcurementBlock
            {
                DefaultPurchaseLt = ltRules ?? [],
                ArrivalToUsableOffsets = [],
                OverdueMargin = new OverdueMarginParams { MarginPercent = marginPercent }
            }
        };

    private static RawProcurementFact Fact(
        DateTime? eta,
        string supplyType = "PURCHASE_IN_TRANSIT",
        string storageCode = "WH-A")
        => new()
        {
            SupplyType = supplyType,
            MaterialId = 100,
            MaterialCode = "MAT-100",
            FactoryId = 1,
            FactoryCode = "F1",
            StorageCode = storageCode,
            RemainingQty = 50m,
            Eta = eta,
            SourceDocumentNo = "PO-001",
            SourceDocumentLineNo = "10"
        };

    [Fact(DisplayName = "受控对照（0号位 场景）：cutoff=11-12 ＋ PO ETA=11-22 ＋ 窗口终点=次年02-09 ⇒ 该 PO 由「被丢」翻为「可见」")]
    public void ControlledComparison_FuturePoWithinWindow_FlipsFromDroppedToVisible()
    {
        var dataCutoff = new DateTime(2026, 11, 12);
        var planHorizonEnd = new DateTime(2027, 2, 9);      // 本 PV 实测窗口终点
        var demandDate = new DateTime(2026, 11, 20);        // 需求早于到货
        var snapshot = Snapshot();
        var emptyMap = new Dictionary<(string, int, int, string), DateTime>();

        var d = AvailableTimeCalculator.Explain(Fact(new DateTime(2026, 11, 22)), emptyMap, snapshot, dataCutoff);
        d.AvailableAt.Should().NotBeNull("ETA 在三级链里可解析 ⇒ 必有时点");
        var availableAt = d.AvailableAt!.Value;

        // 旧口径（cutoff 当上界）会把这条丢掉 —— 这就是 62.41M 的来源。
        SupplyVisibilityWindow.IsBeyond(availableAt, dataCutoff)
            .Should().BeTrue("旧口径下 11-22 的到货晚于 11-12 数据截止 ⇒ 被丢弃（对照基线）");

        // 新口径：窗口终点内 ⇒ 入池；该需求有机会等到这批货，而不是被记成缺口。
        var visibilityEnd = SupplyVisibilityWindow.ResolveEnd(planHorizonEnd, dataCutoff);
        SupplyVisibilityWindow.IsBeyond(availableAt, visibilityEnd)
            .Should().BeFalse("新口径下 11-22 落在计划窗口内 ⇒ 可见");

        availableAt.Should().BeAfter(dataCutoff, "供给确实晚于数据截止 —— 可见性判据正是为这种行翻案");
        availableAt.Should().BeAfter(demandDate, "到货晚于需求日 ⇒ 由 Pegging 决定「等货后到」还是「记缺口」，不再被前置过滤替它下结论");
    }

    [Fact(DisplayName = "逾期重估仍锚在数据截止时点（不是窗口终点）：口径未被本次改动带偏")]
    public void ControlledComparison_OverdueReEstimation_StillAnchoredOnCutoff()
    {
        var dataCutoff = new DateTime(2026, 11, 12);
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0.10m);          // ceil(20 × 0.10) = 2 天
        var emptyMap = new Dictionary<(string, int, int, string), DateTime>();

        var d = AvailableTimeCalculator.Explain(Fact(new DateTime(2026, 11, 5)), emptyMap, snapshot, dataCutoff);

        d.AvailableAt.Should().Be(new DateTime(2026, 11, 14),
            "ETA* = 数据截止时点 + ceil(L × p) = 11-12 + 2 天；若被改成窗口终点，这里会是 2027 年");
    }
}
