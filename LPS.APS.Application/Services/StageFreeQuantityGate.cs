using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// Stage 闸的**纯算式**部分：`free(PI,M,S) = Max(Baselines − E − 第三项, 0)`。
///
/// <para>
/// 口径来源：5号位 2026-11-07《`pending` 认①，但 Stage 闸须补 `CREATED` 第三项》§一.3，
/// 四项确认全认。总量闸仍在 <c>PeggingOrchestrator</c> 内（`Q − ΣWo`），本类只管 Stage 这一层。
/// </para>
/// <para>
/// **为什么单独成类**：这段决定「本轮还能建多少单」，判错了不会编译报错、只会**多建或少建**；
/// 而它原本埋在一个带数据库往返的私有方法里，单测够不着。抽出纯函数后，
/// 三点可被断言：①封顶来自 `Baselines − E` 且不产负值；②第三项**减在封顶上**；③无基线不施闸、但须报数。
/// </para>
/// <para>
/// ⚠ **第三项减在封顶上、不减在提案上**：1号位 求解器已把 `final.Quantity` 封在
/// `StageFreeEligibleQty − AlreadyCommittedQty` 以内（另按 V1_4 NEW-04 把本 Run 已成批折进已占）。
/// 若对提案再减第三项，提案本就低于封顶时会被**双重扣减** ⇒ 少建单。
/// </para>
/// <remarks>开发者：2号位</remarks>
public static class StageFreeQuantityGate
{
    /// <summary>Stage 闸键 = `(PI, 物料ID, StageCode)` 三键（5号位 确认 #2）。</summary>
    public readonly record struct Key(string ProductionInstructionNo, int MaterialId, string StageCode);

    /// <summary>第三项折算结果：真正扣上了几组、因「无投影键」没扣的几组。</summary>
    public readonly record struct ThirdTermApplyResult(int AppliedGroups, int WithoutBaselineGroups);

    /// <summary>
    /// 由内存基线算出封顶 `Baselines − E`（clamp 到 ≥0）。
    /// 空/纯空白 `PI` 或 `StageCode` 的基线**不收**——它们根本组不成三键，硬收会造出一个谁也不匹配的键。
    /// </summary>
    public static Dictionary<Key, decimal> BuildCap(IEnumerable<PiStageQuantityBaseline>? baselines)
    {
        var cap = new Dictionary<Key, decimal>();
        if (baselines is null) return cap;

        foreach (var b in baselines)
        {
            var pi  = b.ProductionInstructionNo?.Trim();
            var st  = b.StageCode?.Trim();
            if (string.IsNullOrEmpty(pi) || string.IsNullOrEmpty(st)) continue;

            // 上游已 clamp 到 [0, 毛量]；这里再兜一次，防口径改动后出现负封顶（负封顶会把闸算反）。
            cap[new Key(pi, b.MaterialId, st)] = Math.Max(b.StageFreeEligibleQty - b.AlreadyCommittedQty, 0m);
        }

        return cap;
    }

    /// <summary>
    /// 把第三项 `UnreportedOwnCreated` 从封顶上扣掉（就地修改 <paramref name="cap"/>，clamp 到 ≥0）。
    ///
    /// <para>
    /// 第三项有、基线无 ⇒ **不扣**（该键本就没有 Stage 闸，硬扣等于凭空造一个未知封顶），
    /// 但必须计数返回，由调用方留痕 —— 「未知 ≠ 零」。
    /// </para>
    /// </summary>
    public static ThirdTermApplyResult ApplyThirdTerm(
        IDictionary<Key, decimal> cap,
        IEnumerable<KeyValuePair<Key, decimal>> thirdTerm)
    {
        ArgumentNullException.ThrowIfNull(cap);
        if (thirdTerm is null) return new ThirdTermApplyResult(0, 0);

        var applied = 0;
        var withoutBaseline = 0;

        foreach (var kv in thirdTerm)
        {
            if (cap.TryGetValue(kv.Key, out var capNow))
            {
                cap[kv.Key] = Math.Max(capNow - kv.Value, 0m);
                applied++;
            }
            else
            {
                withoutBaseline++;
            }
        }

        return new ThirdTermApplyResult(applied, withoutBaseline);
    }
}
