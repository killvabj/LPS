namespace LPS.APS.Core.Dto;

/// <summary>
/// XC（线边仓）事实
/// </summary>
public sealed class XcFact
{
    /// <summary>
    /// 所属生产指示号（PI级XC身份锚，XC-001/004/006：号别余额必须带真实PI号）。
    /// ⚠ 过渡期现状（2026-10-22）：
    ///   · 2号位装载**已切换**为按本 Run 活跃 PI 键批量读 APS 本地快照 `InventoryFact_PiXc`
    ///     （再上游 ODS 八列 → 同义词 ext_ODS_ProductionInstructionInventoryBalance），逐 PI 填本字段；
    ///   · 5号位计算器尚未启用"无PI号拒入/号不匹配拒入"闸门 —— 按 5号位 R03 回执 §B批，
    ///     闸门由其启用，届时无号/号不匹配的输入才会被拒（否则会反向误触发 FIRST_STAGE_PENDING 兜底）；
    ///   · 订阅方仍可能构造无号 `XcFact`（null），故保持可空：null = 未带 PI 身份，不参与 PI 归属。
    /// </summary>
    public string? ProductionInstructionNo { get; init; }

    /// <summary>
    /// XC仓库代码
    /// </summary>
    public string XcWarehouseCode { get; init; } = string.Empty;

    /// <summary>
    /// 数量
    /// </summary>
    public decimal Quantity { get; init; }

    /// <summary>
    /// 关联的Stage代码（XC通常关联某个Stage）
    /// </summary>
    public string? RelatedStageCode { get; init; }

    /// <summary>
    /// 可用时间
    /// </summary>
    public DateTime? AvailableTime { get; init; }

    /// <summary>
    /// 来源单据
    /// </summary>
    public string? SourceDocument { get; init; }

    /// <summary>
    /// 源侧时点。PI 级 XC 源（`ODS_ProductionInstructionInventoryBalance.SnapshotTime`）的语义是
    /// **最后一次业务动作时刻（ISNULL(LastOutTime, LastInTime)）**，不是快照时刻、也不是可承诺的
    /// 可用时点 ⇒ **不映射到 <see cref="AvailableTime"/>**，只作留痕/对账用。
    /// </summary>
    public DateTime? SnapshotTime { get; init; }
}
