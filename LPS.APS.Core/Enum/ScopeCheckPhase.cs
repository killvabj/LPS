namespace LPS.APS.Core.Enum;

/// <summary>
/// 业务范围边界校验的<b>时点</b>（0号位 2026-10-29《01》`:18` 要求的两段式复核）。
/// </summary>
/// <remarks>
/// <b>只有两个值，不再扩</b>——这是 3号位 2026-10-29 D-2 自裁收口的自我约束第 ④ 条。
/// <para>
/// 本枚举只进日志与异常文案，<b>不改变判定规则</b>：两个时点用的是同一套「全集子集」判据，
/// 差别只在「届时哪些集合已可证」。不新增错误码（`01 :18`），越界仍复用既有两个边界码。
/// </para>
/// </remarks>
public enum ScopeCheckPhase
{
    /// <summary>
    /// 创建/装载阶段：只查届时可证的 Order 与 Domain 业务范围。
    /// 需求归属工厂已可证；Task/FinalTask 真实生产部门此时尚未落定。
    /// </summary>
    Load = 0,

    /// <summary>
    /// 提交/激活/发布前：Task/FinalTask 真实生产部门已可证，
    /// 对实际涉及的生产/操作工厂与采购收货工厂复核，不得以「事后才知道」为由放过越权生产动作。
    /// </summary>
    Commit = 1,
}
