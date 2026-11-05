using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R07/B2-02「严格变更门禁 ＋ 失败拒错」判据的回归测试（2026-10-30）。
///
/// 【判据是什么】Step5～8 装载前后各取一次 `ManualCapacitySlot` 与 `TaskSplitRuleConfig` 的
///   `(行数, 校验和)` 指纹；两次不等即整域 Fail-Closed，并在错误文本里点名是**哪张表**变了。
///
/// 【为什么断言这几条】不是「结构体存得下」，而是三条可证伪的行为：
///   ① 两次读数完全相同 ⇒ **不误拒**（否则每轮装载都会被自己的门禁打死）；
///   ② 任一张表变 ⇒ 拒绝，且 `Diff` **点名到表**（否则运维拿到「源表变了」无从下手）；
///   ③ 行数与校验和任一变化都算变（新增/删除与内容修改两条路都要抓）。
///
/// 【口径边界】本测试只覆盖**比较与归因**这一层纯逻辑；`CaptureEditableSourceFingerprintAsync`
///   的 SQL 谓词须与读取谓词逐字一致，属集成面，不在本测试内断言。
/// </summary>
public class EditableSourceFingerprintTests
{
    private static PeggingOrchestrator.EditableSourceFingerprint Fp(
        long slotCnt = 15360, long slotSig = -10721263213339,
        long ruleCnt = 7, long ruleSig = -3979104217)
        => new(slotCnt, slotSig, ruleCnt, ruleSig);

    // ① 不误拒：前后两次读数一致时必须判「无变化」。
    [Fact]
    public void 前后读数一致_判无变化()
    {
        var before = Fp();
        var after = Fp();

        Assert.True(before.SameAs(in after));
        Assert.Equal("<无差异>", before.Diff(in after));
    }

    // ② 人工槽变（行数）⇒ 拒绝，且归因到 ManualCapacitySlot。
    [Fact]
    public void 人工槽行数变化_判变化并点名人工槽()
    {
        var before = Fp(slotCnt: 15360);
        var after = Fp(slotCnt: 15361);

        Assert.False(before.SameAs(in after));
        var diff = before.Diff(in after);
        Assert.Contains("ManualCapacitySlot", diff);
        Assert.DoesNotContain("TaskSplitRuleConfig", diff);
    }

    // ③ 人工槽只改内容、行数不变（改一个槽的容量／停用再启用）⇒ 校验和变，同样必须拒。
    [Fact]
    public void 人工槽仅内容变化_行数相同也必须拒()
    {
        var before = Fp(slotSig: 111);
        var after = Fp(slotSig: 112);

        Assert.False(before.SameAs(in after));
        Assert.Contains("ManualCapacitySlot", before.Diff(in after));
    }

    // ④ 批次规则变（治理侧「维护即生效」）⇒ 归因到 TaskSplitRuleConfig，不冤枉人工槽。
    [Fact]
    public void 批次规则变化_点名批次规则()
    {
        var before = Fp(ruleCnt: 7);
        var after = Fp(ruleCnt: 6);

        Assert.False(before.SameAs(in after));
        var diff = before.Diff(in after);
        Assert.Contains("TaskSplitRuleConfig", diff);
        Assert.DoesNotContain("ManualCapacitySlot", diff);
    }

    // ⑤ 两张表同时变 ⇒ 归因必须两条都在（只报一条会把另一张藏起来）。
    [Fact]
    public void 两张表同时变化_两条都要点名()
    {
        var before = Fp();
        var after = Fp(slotCnt: 1, ruleSig: 2);

        Assert.False(before.SameAs(in after));
        var diff = before.Diff(in after);
        Assert.Contains("ManualCapacitySlot", diff);
        Assert.Contains("TaskSplitRuleConfig", diff);
    }
}
