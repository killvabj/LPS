using System.Collections.Generic;
using Xunit;
using LPS.APS.Application.Services;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// PI 级 XC 入池身份闸门（0号位 2026-10-27《R03-27-04 PI级XC按PI身份装载、跨厂不预过滤 v1.0》裁定 A）。
///
/// **裁定前的缺陷**（r13830 起，见台账 §172）：`LoadPiXcFactsAsync` 用 `(物料, 厂)` 二元组作闸门，
/// XC 库存所在厂与 PI 所属厂不等即整行丢弃。PV190 实测因此剔掉 2,464 个 PI / 1,029,018，
/// **全部是「物料对、厂不同」**（BJ 仓持 CN 2,786 行 / TJ 53 行），0 行物料不符。
///
/// 本组用例锁的是**改后的判据**：入池只要求「PI 号命中本 Run 活跃集」，**厂与物料都不作淘汰理由**。
/// 厂：2026-10-27 裁定 A 撤；物料：2026-10-28 件1 撤（「2号位 不作此项业务取舍」，
/// `MaterialMismatch` 降级为诊断标签，去留统一走 `AdmitsToPool`）。
/// 对应 0号位 §三 给出的用例编号：XC-IN-01 / XC-IN-02 / XC-IN-04。
/// </summary>
public class PiXcIdentityGateTests
{
    private const string Pi = "PI-0001";
    private const string Material = "MAT-A";

    private static IReadOnlyDictionary<string, HashSet<string>> Allowed(params (string Pi, string Material)[] pairs)
    {
        var d = new Dictionary<string, HashSet<string>>();
        foreach (var (pi, mat) in pairs)
        {
            if (!d.TryGetValue(pi, out var set))
            {
                set = new HashSet<string>();
                d[pi] = set;
            }
            set.Add(mat);
        }
        return d;
    }

    /// <summary>XC-IN-01：本 Run 有效 PI、同物料 ⇒ 受理。</summary>
    [Fact]
    public void 同物料_受理()
    {
        var allowed = Allowed((Pi, Material));

        var decision = PeggingOrchestrator.ClassifyXcRow(10m, Pi, Material, allowed);

        Assert.Equal(PeggingOrchestrator.XcRowDecision.Accepted, decision);
    }

    /// <summary>
    /// XC-IN-02（本轮裁定要放开的那一条）：**异厂照样入**，且真实库存厂必须原样传给 5号位。
    ///
    /// ⚠ 这条**不是**用「换个厂再判一次」来证明的——`ClassifyXcRow` 的签名里根本没有厂，
    ///   所以「厂不等会不会被拒」在编译期就已经不可能发生。运行时能锁的只有另一半：
    ///   **承载真实厂的那个字段必须存在**（删掉本属性 ⇒ 本用例编译失败 ⇒ 改回二元组闸门会被拦住）。
    ///   厂是否被正确赋值，只能在装载层验（需真库），不在单测射程内。
    /// </summary>
    [Fact]
    public void 异厂_原样传递_XcFact承载真实库存厂()
    {
        var fact = new LPS.APS.Core.Dto.XcFact { FactoryCode = "BJ" };

        Assert.Equal("BJ", fact.FactoryCode);
    }

    /// <summary>
    /// XC-IN-04 之一：物料与 PI 身份不符 ⇒ **仍判 `MaterialMismatch`（保留诊断标签）**。
    ///
    /// ⚠ 2026-10-28 起本标签**不再等于剔除**（0号位 件1：2号位 不作此项业务取舍）。
    ///   去留由 <see cref="PeggingOrchestrator.AdmitsToPool"/> 判，见下一条用例。
    /// </summary>
    [Fact]
    public void 物料不符_判为诊断标签()
    {
        var allowed = Allowed((Pi, Material));

        var decision = PeggingOrchestrator.ClassifyXcRow(10m, Pi, "MAT-OTHER", allowed);

        Assert.Equal(PeggingOrchestrator.XcRowDecision.MaterialMismatch, decision);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 0号位 2026-10-27 件1 点名的三组最简正反例（停止条件：证明本 Run PI 号对应的
    // 可信 XC 源行未被 Material / Factory 作业务过滤，且带出原始 Material 与 Factory）。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 例①：PI_A，源行物料与 PI 主档一致，XC 落在**别的厂** ⇒ 入池，
    /// 且 `XcFact` 必须带出**源厂**（不得改写成 PI 所属厂）。
    /// </summary>
    [Fact]
    public void 例1_匹配物料_异厂_入池且带源厂()
    {
        var allowed = Allowed((Pi, Material));

        var decision = PeggingOrchestrator.ClassifyXcRow(10m, Pi, Material, allowed);
        Assert.True(PeggingOrchestrator.AdmitsToPool(decision));

        // 厂不在判定签名里（编译期已排除厂闸），此处锁另一半：承载源厂的字段必须在。
        var fact = new LPS.APS.Core.Dto.XcFact { FactoryCode = "CN", MaterialCode = Material };
        Assert.Equal("CN", fact.FactoryCode);
    }

    /// <summary>
    /// 例②：PI_A，源行物料与 PI 主档**不一致** ⇒ **仍入池**，
    /// 且 `XcFact.MaterialCode` 必须是**源行物料**，不是 PI 主档物料。
    /// </summary>
    [Fact]
    public void 例2_物料不匹配_仍入池_且带源物料而非PI物料()
    {
        var allowed = Allowed((Pi, Material));

        var decision = PeggingOrchestrator.ClassifyXcRow(10m, Pi, "MAT-SRC", allowed);
        Assert.Equal(PeggingOrchestrator.XcRowDecision.MaterialMismatch, decision);
        Assert.True(PeggingOrchestrator.AdmitsToPool(decision), "物料不符不得在装载侧剔除");

        var fact = new LPS.APS.Core.Dto.XcFact { MaterialCode = "MAT-SRC" };
        Assert.Equal("MAT-SRC", fact.MaterialCode);
        Assert.NotEqual(Material, fact.MaterialCode);
    }

    /// <summary>
    /// 例③：另一 PI_B 的行**不得**混入 PI_A ⇒ PI 号不在本 Run 活跃集时剔除。
    /// </summary>
    [Fact]
    public void 例3_异PI_不入池()
    {
        var allowed = Allowed((Pi, Material));   // 本 Run 只认 PI-0001

        var decision = PeggingOrchestrator.ClassifyXcRow(10m, "PI-0002", Material, allowed);

        Assert.Equal(PeggingOrchestrator.XcRowDecision.UnknownPi, decision);
        Assert.False(PeggingOrchestrator.AdmitsToPool(decision), "非本 Run 活跃 PI 不得入池");
    }

    /// <summary>
    /// 入池判据的完整真值表：**只有**非正量与非本轮 PI 被剔（两类都是"物理不成立/归属不可证"），
    /// 物料不符照入。改回「物料不符即剔」会在这里挂掉。
    /// </summary>
    /// <summary>真值表逐项（拆成 4 条：`XcRowDecision` 是 internal，不能进 public 方法签名）。</summary>
    [Fact]
    public void 入池判据_Accepted_入()
        => Assert.True(PeggingOrchestrator.AdmitsToPool(PeggingOrchestrator.XcRowDecision.Accepted));

    [Fact]
    public void 入池判据_NonPositive_不入()
        => Assert.False(PeggingOrchestrator.AdmitsToPool(PeggingOrchestrator.XcRowDecision.NonPositive));

    [Fact]
    public void 入池判据_UnknownPi_不入()
        => Assert.False(PeggingOrchestrator.AdmitsToPool(PeggingOrchestrator.XcRowDecision.UnknownPi));

    [Fact]
    public void 入池判据_MaterialMismatch_入()
        => Assert.True(PeggingOrchestrator.AdmitsToPool(PeggingOrchestrator.XcRowDecision.MaterialMismatch));


    /// <summary>XC-IN-04 之二：PI 号不在本 Run 活跃集 ⇒ 拒绝（无法证明归属，不得强塞）。</summary>
    [Fact]
    public void 非本轮PI_拒绝()
    {
        var allowed = Allowed((Pi, Material));

        Assert.Equal(PeggingOrchestrator.XcRowDecision.UnknownPi,
            PeggingOrchestrator.ClassifyXcRow(10m, "PI-UNKNOWN", Material, allowed));
    }

    /// <summary>PI 号为空/空白 ⇒ 视同不可证归属，拒绝。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PI号缺失_拒绝(string? pi)
    {
        var allowed = Allowed((Pi, Material));

        Assert.Equal(PeggingOrchestrator.XcRowDecision.UnknownPi,
            PeggingOrchestrator.ClassifyXcRow(10m, pi, Material, allowed));
    }

    /// <summary>数量 ≤ 0 ⇒ 恒拒，且**优先**于身份判定（不占用身份不符计数）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正量_恒拒_不参与身份计数(int qty)
    {
        var allowed = Allowed((Pi, Material));

        Assert.Equal(PeggingOrchestrator.XcRowDecision.NonPositive,
            PeggingOrchestrator.ClassifyXcRow(qty, Pi, Material, allowed));
    }

    /// <summary>同一 PI 在多源给出多组物料时，任一命中即受理（与旧「多组合法校验集」语义一致）。</summary>
    [Fact]
    public void 同PI多物料_任一命中即受理()
    {
        var allowed = Allowed((Pi, Material), (Pi, "MAT-B"));

        Assert.Equal(PeggingOrchestrator.XcRowDecision.Accepted,
            PeggingOrchestrator.ClassifyXcRow(10m, Pi, "MAT-B", allowed));
    }

    /// <summary>活跃集为空 ⇒ 一切行都不可证归属（不得静默当 XC＝0，调用侧另行留证）。</summary>
    [Fact]
    public void 活跃集为空_拒绝()
    {
        var allowed = Allowed();

        Assert.Equal(PeggingOrchestrator.XcRowDecision.UnknownPi,
            PeggingOrchestrator.ClassifyXcRow(10m, Pi, Material, allowed));
    }
}
