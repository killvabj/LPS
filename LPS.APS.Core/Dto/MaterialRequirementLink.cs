namespace LPS.APS.Core.Dto;

/// <summary>
/// 多层 BOM「任务喂任务」血缘输入（PM 2026-09-10 裁决 v1.0 落地 R2）。
/// 父 LogicalProductionDemand（消费侧）→ 子 LogicalProductionDemand（生产侧）的运行时关系。
/// 由 2号位 Pegging 遍历 BOM 时产出，随 LogicalProductionDemands 一起透传 1号位；
/// 1号位据此 + 拆批/合批 + Routing 生成真实 TaskDependency（FinalTaskPeggingDraft，只记生产 Task 实际份额）。
/// 子件全库存 / 采购占位（0910 §二十 Case B）时仍产 link 以保留「父需求→子需求」真相，ProducerLogicalDemandKey 为 null；1号位 据此不产该边的 TaskDependency。
/// </summary>
public sealed class MaterialRequirementLink
{
    /// <summary>
    /// 父需求键（消费侧 LogicalProductionDemand.LogicalDemandKey）。
    /// </summary>
    public string ConsumerLogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 子需求键（生产侧 LogicalProductionDemand.LogicalDemandKey）。
    /// 子件全库存/采购占位（0910 §二十 Case B）时为 null —— 此时仍产 link 以保留「父需求→子需求」真相，
    /// 仅无生产侧身份（1号位 生成 TaskDependency 时跳过 Producer=null 的 link）。
    /// </summary>
    public string? ProducerLogicalDemandKey { get; init; }

    /// <summary>
    /// 子件需求键（需求侧 DemandKey = ORDER_{OrderId}_{MaterialCode}_{FactoryId}，与 AllocationLineage.DemandKey 同源）。
    /// 供 1号位 §二十一 数量闭环验收作连接键：Σ AllocationLineage(同 ChildDemandKey).Quantity = RequiredQty。
    /// </summary>
    public string ChildDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 父物料（消费侧）——供 1号位 判别跨物料（R4：UpstreamMaterialId != DownstreamMaterialId）。
    /// </summary>
    public int ConsumerMaterialId { get; init; }

    /// <summary>
    /// 子物料（生产侧）。
    /// </summary>
    public int ProducerMaterialId { get; init; }

    /// <summary>
    /// **该生产方（<see cref="ProducerLogicalDemandKey"/>）的合法承接份额**。
    ///
    /// <para>⚠ 2026-11-23 修订（原注释写「完整子需求数量（父对子的总需求…非子件新增生产缺口量）」，**已过期**）：
    /// 自 0号位 2026-11-20 §二.4 定「多来源按数量份额拆边」起，本字段即表达**份额**而非总需求 ——
    /// 一条需求被多张 PI 承接时拆成多条边、各自带份额；单来源时同样只写该 PI 的承接份额
    /// （子件总需求中由**库存等已承接**的部分不计入本字段）。
    /// ⇒ 按旧注释读会把它当总需求，必错。</para>
    /// </summary>
    public decimal RequiredQty { get; init; }

    /// <summary>
    /// **本边 `Producer` 的 `AllocationSequence`**（= `ProducerLogicalDemandKey` 那条 LPD 的序号），1号位零推断。
    ///
    /// <para>⚠ 2026-11-23 修订：多来源拆边时原实现**复制原 Link 的旧值** ⇒ 拆边后指向错的 Producer（字段失真）。
    /// 现按每条边的 Producer 取各自序号；取不到才回退原值。</para>
    /// </summary>
    public long ProducerAllocationSequence { get; init; }

    /// <summary>
    /// 父件消费相对子件完工的滞后时间（分钟，P1-12）。
    /// 同 Domain 普通 BOM 父-子 = 0（Consumer floor = Producer completion）；
    /// 跨厂边 = InterFactoryLT（3号位 CrossFactoryLeadTime 三元组 TransportDays/InspectionDays/TransferDays 天数 × 1440，
    /// 由 2号位装载时换算）。与输出侧 FinalTaskPeggingDraft.LagTime 同单位（分钟）。
    /// </summary>
    public decimal LagMinutes { get; init; }
}