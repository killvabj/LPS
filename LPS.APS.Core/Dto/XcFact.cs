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
    /// XC 库存**实际所在厂**（源：`ODS_ProductionInstructionInventoryBalance.FactoryCode`）。
    ///
    /// ⚠ 2026-10-27（0号位《R03-27-04 PI级XC按PI身份装载、跨厂不预过滤》）：
    ///   · 本字段是**库存物理事实**，必须**原样传递**，**不得**改写成 PI 所属厂；
    ///   · **不得**以「本厂 ≠ PI 所属厂」作为入池/拒入判据（原 `LoadPiXcFactsAsync` 以
    ///     (物料,厂) 二元组作闸门，PV190 实测因此整批剔掉 2,464 个 PI / 1,029,018，
    ///     全部是「物料对、厂不同」——BJ 仓持 CN/TJ 的 PI）；
    ///   · PI 归属只由 <see cref="ProductionInstructionNo"/> 锚定，厂不参与归属。
    ///
    /// 可空：非 PI 级来源（订阅方构造）可能无厂信息；null = 厂不明，不参与任何判定。
    /// </summary>
    public string? FactoryCode { get; init; }

    /// <summary>
    /// XC 库存行**自身的物料代码**（源：`InventoryFact_PiXc.MaterialCode`
    /// ← ODS `ODS_ProductionInstructionInventoryBalance.MaterialCode`）。
    ///
    /// ⚠ 2026-10-27（0号位《R03-27-04 PI级XC按PI身份装载、跨厂不预过滤》裁定 A / 件1 第②条）：
    ///   · 本字段是**源行事实**，必须**原样传递**；
    ///   · **不得**用父 PI 自身物料覆盖它——PI 主档物料 ≠ 该 XC 行实际记载的物料，
    ///     替换了就等于伪造源，下游再也看不出「这行其实对不上」；
    ///   · 装载侧**不再**因「源行物料 ≠ PI 主档物料」剔除本行（原 `MaterialMismatch → continue` 已撤）：
    ///     **是否错配是 5号位 的业务判断，2号位 不作此项取舍**。
    ///     装载侧只留计数＋样本作诊断（日志 `物料与其 PI 身份行不符`）。
    ///
    /// 可空：非 PI 级来源（订阅方构造）可能无物料信息；null = 物料不明，不参与任何判定。
    /// </summary>
    public string? MaterialCode { get; init; }

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
    /// **最后一次业务动作时刻（`ISNULL(LastOutTime, LastInTime)`）**，不是快照时刻、也不是可承诺的
    /// 可用时点 ⇒ **不映射到 <see cref="AvailableTime"/>**，只作留痕/对账用。
    ///
    /// <para>
    /// <b>⛔ 禁止用作判据</b>：<b>禁止</b>拿本字段做时点判定、可用性判定、新旧/新鲜度判定，
    /// 或据此剔除/降权任何 XC 行。理由：① 它表达的是「该 <c>(PI, 仓)</c> 余额<b>最后一次发生变动</b>
    /// 的时刻」，余额两个月没动 ⇒ 该值就是两个月前，<b>不代表数据旧</b>；
    /// ② 同一批快照内各行散布于近两月窗，<b>散布是设计预期</b>（采集切片身份由 <c>BatchNo</c> +
    /// <c>CapturedAt</c> 表达，两者均为单值）；③ 新鲜度权威是装载闸门（`APS_ETL_Log` 状态 + 采集时刻），
    /// 不是行级业务时刻。
    /// </para>
    /// <para>
    /// 语义出处：5号位 2026-10-27《数据时点表 v1.0 收悉 · SnapshotTime 语义补证》§三，
    /// 附 ODS 八列视图定义原文 `CAST(ISNULL(o.LastOutTime, a.LastInTime) AS DATETIME2) AS SnapshotTime`。
    /// 本字段当前<b>下游读取点 0 处</b>（仅赋值与留痕），加注即为此——见台账 §200。
    /// </para>
    /// </summary>
    public DateTime? SnapshotTime { get; init; }
}
