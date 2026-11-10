using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// O49（2026-11-10）：竞争轮次判定。
/// 被测对象 = <see cref="PeggingOrchestrator.ClassifyCompetitionRound"/>（纯函数）。
///
/// 【本组用例守什么】
///   ① 成品层判据 = 码前缀 `FINAL_FG`。**用户 2026-11-10 当面定性**：
///      「成品只可能出现于成品库，也就是出口库，肯定都是 `FINAL_FG`；如果有其他 `FG` 开头，肯定是有问题」。
///   ② **不得改用 `Material.MaterialType`** —— 实测有 28,632 行码为 `FG-` 的物料 `MaterialType`
///      被打成 `FINISHED_GOOD`，按上述口径那是主档数据问题，拿它判成品会把坏数据当权威。
///      本组用例用「码是 FG- 但 MaterialType 说成品」这一形态来钉住：判定只看码，不看类型。
///   ③ 第 2 轮（SH 源厂履约）**当前不产出** —— 0号位 2026-11-09 明令须由本次 Pegging 的
///      合法上层关系判定，不得用跨厂标记等静态特征代替。
/// </summary>
public class CompetitionRoundClassificationTests
{
    private const int Round1 = 1;
    private const int Round3 = 3;

    // ── 轴 B：P 行身份 ──────────────────────────────────────────────────────

    [Theory(DisplayName = "O49 普通销售订单（O 行）恒第 1 轮，与物料无关")]
    [InlineData("O1234567", "FINAL_FG-AR30-02E-D")]
    [InlineData("O1234567", "WIP-C24FP-030")]
    [InlineData("O1234567", "FG-AFM20-02C-D")]
    [InlineData("O1234567", null)]
    public void SalesOrderRow_AlwaysRound1(string orderNo, string? materialCode)
    {
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound(orderNo, materialCode));
    }

    [Fact(DisplayName = "O49 空单号按非 P 行处理 ⇒ 第 1 轮，不抛错")]
    public void EmptyOrderNo_TreatedAsNonPiRow()
    {
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound(null, "WIP-X"));
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound("", "WIP-X"));
    }

    // ── 轴 A：成品层身份 ────────────────────────────────────────────────────

    [Fact(DisplayName = "O49 P 行 + FINAL_FG 前缀 ⇒ 第 1 轮（成品顶层 MTS）")]
    public void PiRow_FinalFgPrefix_Round1()
    {
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound("P17226504", "FINAL_FG-AR30-02E-D"));
    }

    [Fact(DisplayName = "O49 前缀大小写不敏感")]
    public void FinalFgPrefix_IsCaseInsensitive()
    {
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound("P1", "final_fg-AR30-02E-D"));
    }

    [Theory(DisplayName = "O49 P 行 + 非成品前缀 ⇒ 第 3 轮（孤儿 MTS）")]
    [InlineData("WIP-C24FP-030")]
    [InlineData("ASSY-AD27A-D")]
    [InlineData("RAW-M45")]
    [InlineData("FINAL_WIP-04-2232-X512")]
    [InlineData("FINAL_ASS-04-2232-X512")]
    [InlineData("")]
    public void PiRow_NonFinishedPrefix_Round3(string materialCode)
    {
        Assert.Equal(Round3, PeggingOrchestrator.ClassifyCompetitionRound("P17226504", materialCode));
    }

    [Fact(DisplayName = "O49 物料码缺失 ⇒ 保守落第 3 轮，不猜成成品")]
    public void PiRow_MissingMaterialCode_FallsToRound3()
    {
        Assert.Equal(Round3, PeggingOrchestrator.ClassifyCompetitionRound("P17226504", null));
    }

    // ── 本次最关键的回归：FG- 不是成品 ─────────────────────────────────────

    [Fact(DisplayName = "O49 缺陷回归：FG- 前缀不得判成成品——即便 MaterialType 说它是 FINISHED_GOOD")]
    public void PlainFgPrefix_IsNotFinishedGood()
    {
        // 主档现状：28,632 行码为 FG- 的物料 MaterialType='FINISHED_GOOD'。
        // 用户定性：成品必然 FINAL_FG，其它 FG 开头「肯定是有问题」⇒ 判成品只看码前缀。
        // 本函数不接收 MaterialType，故这里断言的是「码 FG- ⇒ 第 3 轮」这一结论本身。
        Assert.Equal(Round3, PeggingOrchestrator.ClassifyCompetitionRound("P17226504", "FG-AFM20-02C-D"));

        // 对照：同一个单号，换成 FINAL_FG 前缀就必须进第 1 轮。
        Assert.Equal(Round1, PeggingOrchestrator.ClassifyCompetitionRound("P17226504", "FINAL_FG-AFM20-02C-D"));
    }

    // ── 第 2 轮未落码：显式钉住 ─────────────────────────────────────────────

    [Fact(DisplayName = "O49 Round2 当前不产出（须由本次 Pegging 合法上层关系判定，不得用静态特征代填）")]
    public void Round2_IsNotProduced_Yet()
    {
        var inputs = new (string? OrderNo, string? Code)[]
        {
            ("P1", "FINAL_FG-X"), ("P1", "WIP-Y"), ("P1", "FG-Z"),
            ("O1", "FINAL_FG-X"), ("", null), (null, null)
        };

        foreach (var (orderNo, code) in inputs)
        {
            var round = PeggingOrchestrator.ClassifyCompetitionRound(orderNo, code);
            Assert.True(round == Round1 || round == Round3,
                $"O49：Round2 尚未落码，任何输入都不得产出 2（实测 {orderNo}/{code} 得到 {round}）。"
                + "若本轮确实实现了 Round2，请同步修改本用例并在登记簿说明判据来源。");
        }
    }
}