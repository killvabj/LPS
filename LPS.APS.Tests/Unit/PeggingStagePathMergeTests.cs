using System.Collections.Generic;
using System.Linq;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// `1c 兜底 StagePath` 改为「内存合并已装批次 ＋ 只查残余批」后的**取值等价性**守卫。
///
/// 背景：`PeggingOrchestrator` 装 Stage 顺序分两步 —— 1.b) 按「本批」逐批查 `APS_BOM_STAGE_PATH_RAW`，
/// 1.c) 按「本 PlanVersion 全部关联批」再查一次作兜底。PV190（FAMILY_X 全量域）实测两侧批次集**恒等**
/// （`OrderBomRequestLink` 21,778 行只有 1 个批次），故 1.c) 原先是把 1.b) 已装的同一批行再装一遍，
/// 单次耗时 27.4 秒。
///
/// 改法：1.c) 对「1b 已装过的关联批」直接在内存里并进去，只对残余批查库。成立的唯一依据是
/// **MIN 可结合**：`MIN(MIN(已装), MIN(残余)) ≡ MIN(全部关联批)`。
///
/// 本文件锁三件事：
///   · 同一 StageCode 出现多次时取**最小** StageSeq（不是覆盖、不是先到先得）；
///   · 分两次并入与一次性并入**逐值相等**（结合律）；
///   · 不同 (物料, StageScope) 之间**不串**。
/// </summary>
public class PeggingStagePathMergeTests
{
    private static Dictionary<(string MaterialCode, string Scope), Dictionary<string, int>> NewTarget()
        => new();

    [Fact]
    public void 同一Stage重复出现时取最小StageSeq_不是覆盖为先到先得()
    {
        var target = NewTarget();

        PeggingOrchestrator.MergeStageSeq(target, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 5), new KeyValuePair<string, int>("S2", 9) });
        PeggingOrchestrator.MergeStageSeq(target, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 2), new KeyValuePair<string, int>("S3", 7) });

        var m = target[("M1", "ROOT")];
        Assert.Equal(2, m["S1"]);   // 后到的更小 ⇒ 取 2
        Assert.Equal(9, m["S2"]);
        Assert.Equal(7, m["S3"]);
        Assert.Equal(3, m.Count);
    }

    [Fact]
    public void 先到更小则不得被后到的更大值覆盖()
    {
        var target = NewTarget();

        PeggingOrchestrator.MergeStageSeq(target, "M1", "EDGE",
            new[] { new KeyValuePair<string, int>("S1", 1) });
        PeggingOrchestrator.MergeStageSeq(target, "M1", "EDGE",
            new[] { new KeyValuePair<string, int>("S1", 4) });

        Assert.Equal(1, target[("M1", "EDGE")]["S1"]);
    }

    [Fact]
    public void 分两次并入与一次性并入逐值相等_结合律()
    {
        // 模拟 1b 已装批次（batchA）＋ 1c 残余批次（batchB）分两次并入
        var split = NewTarget();
        PeggingOrchestrator.MergeStageSeq(split, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 3), new KeyValuePair<string, int>("S2", 8) });
        PeggingOrchestrator.MergeStageSeq(split, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 6), new KeyValuePair<string, int>("S2", 4) });

        // 模拟原实现：一次查全部关联批（GROUP BY 不含批次 ⇒ 跨批取 MIN）
        var atOnce = NewTarget();
        PeggingOrchestrator.MergeStageSeq(atOnce, "M1", "ROOT",
            new[]
            {
                new KeyValuePair<string, int>("S1", 3), new KeyValuePair<string, int>("S2", 4),
            });

        Assert.Equal(atOnce[("M1", "ROOT")], split[("M1", "ROOT")]);
        Assert.Equal(3, split[("M1", "ROOT")]["S1"]);
        Assert.Equal(4, split[("M1", "ROOT")]["S2"]);
    }

    [Fact]
    public void 同一物料不同StageScope互不串用()
    {
        var target = NewTarget();

        PeggingOrchestrator.MergeStageSeq(target, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 2) });
        PeggingOrchestrator.MergeStageSeq(target, "M1", "EDGE",
            new[] { new KeyValuePair<string, int>("S1", 5) });

        Assert.Equal(2, target[("M1", "ROOT")]["S1"]);
        Assert.Equal(5, target[("M1", "EDGE")]["S1"]);
        Assert.Equal(2, target.Count);
    }

    [Fact]
    public void 不同物料互不串用()
    {
        var target = NewTarget();

        PeggingOrchestrator.MergeStageSeq(target, "M1", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 2) });
        PeggingOrchestrator.MergeStageSeq(target, "M2", "ROOT",
            new[] { new KeyValuePair<string, int>("S1", 7) });

        Assert.Equal(2, target[("M1", "ROOT")]["S1"]);
        Assert.Equal(7, target[("M2", "ROOT")]["S1"]);
    }

    [Fact]
    public void 空序列只建桶不产生Stage()
    {
        var target = NewTarget();

        PeggingOrchestrator.MergeStageSeq(target, "M1", "ROOT", Enumerable.Empty<KeyValuePair<string, int>>());

        Assert.True(target.ContainsKey(("M1", "ROOT")));
        Assert.Empty(target[("M1", "ROOT")]);
    }
}
