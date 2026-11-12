using System.Collections.Generic;

namespace LPS.APS.Application.Models;

// ════════════════════════════════════════════════════════════════════════════
// BOM 结构展开（Explosion）纯函数层的输入/输出数据结构（2号位）。
//
// 这些是纯数据 record，不是 DI 服务——必须住在 LPS.APS.Application.Models（`Services`
// 命名空间之外），否则 ApplicationServiceExtensions 的 Scrutor 扫描会按 record 自动实现
// 的 IEquatable<T> 把它们误注册为 Scoped 服务，导致 DI 容器在 ValidateOnBuild 阶段
// 因「无法解析 String/Int64/Int32 构造函数参数」启动失败。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>某一层的一条 BOM 子件边（单位配比）。</summary>
public sealed record BomComponent(string ChildCode, int ChildMaterialId, decimal QtyPerUnit)
{
    /// <summary>
    /// 该子件**应归属的账面工厂码**（`APS_BOM_RAW.ChildRequiredFactory`，R17 `Produce→厂` 的产物）。
    ///
    /// 【为什么要带上它】此前边结构没有工厂字段，`TraverseBomNode` 递归时把**父件的工厂原样下传**
    /// ⇒ 子件需求永远落在父厂。BOM 说"该子件在 BJ 做"，需求却建在 TJ：在制 PI 在 BJ、需求键在 TJ，
    /// 供给池键 `物料|工厂` 对不上 ⇒ 全转新增生产 ⇒ 无 PI（`TJ_FRL` 84.6% 的根因）。
    ///
    /// 【空值语义（5号位 2026-11-10 逐答）】空 = **外购件**（该批 2,716,680 行全空、100% 是
    /// `ChildSourceHintCode ∈ {0,2,3,4,10}`），子件自身不制造，无制造厂可言 —— 不是缺数据。
    /// 但**递归点看到的空值一定是异常**：外购件在 `NetAndAllocate` 就返回 null 不再下钻，
    /// 能走到子件递归的都是要自制的 ⇒ 空值即缺值，记 Issue 交出去，不静默兜底。
    /// </summary>
    public string? ChildRequiredFactoryCode { get; init; }
}

/// <summary>BOM 结构快照（纯数据）：父件 → 子件单位向量；物料 → 是否采购件。</summary>
public sealed record BomStructure(
    IReadOnlyDictionary<string, IReadOnlyList<BomComponent>> ByParent,
    IReadOnlyDictionary<string, bool> IsPurchasedByMaterial)
{
    /// <summary>
    /// 工厂码 → 工厂 Id（`Factory` 主档）。展开时据此把子件的
    /// <see cref="BomComponent.ChildRequiredFactoryCode"/> 解析成可入池的工厂 Id。
    /// 空字典 ⇒ 一律保持父厂并标记 <see cref="BomFactorySource.UnknownFallback"/>。
    /// </summary>
    public IReadOnlyDictionary<string, int> FactoryIdByCode { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// 展开节点「工厂是怎么定下来的」——供主链留痕，避免把「继承父厂」与「按边改厂」混为一谈。
/// </summary>
public enum BomFactorySource
{
    /// <summary>与父件同厂（根节点、或边上给自己指定的就是父厂）。</summary>
    SameAsParent = 0,

    /// <summary>按 `ChildRequiredFactory` **改到了自己的厂**（与父厂不同）。</summary>
    Rerouted = 1,

    /// <summary>
    /// `ChildRequiredFactory` 为空且该物料**是采购件**（ERP `SupplyMode='PURCHASE'`）⇒ 保持父厂。
    /// 这是**正常态**（5号位：空值 100% 是外购行，子件不制造、无制造厂可言），不计缺值。
    /// </summary>
    PurchasedNoFactory = 5,

    /// <summary>
    /// `ChildRequiredFactory` 为空但该物料**要自制**（非采购件）⇒ 保持父厂。**这才是缺值**
    /// （Produce 非外购却要求厂为空），必须计数告警，不得与 <see cref="PurchasedNoFactory"/> 混为一谈。
    /// </summary>
    MissingFallback = 2,

    /// <summary>厂码在 `Factory` 主档查无（含停用/非生产厂）⇒ 保持父厂。</summary>
    UnknownFallback = 3,

    /// <summary>
    /// 多条父边给该子件指定了**不同的**应归属厂。节点模型按物料唯一，只能取首个父边的厂；
    /// 此标记表示「这里被压过了」，必须被看见（真实业务上它意味着该物料要在两个厂各建一次需求，
    /// 现有 `BomLevelNode` 结构表达不了）。
    /// </summary>
    ConflictFirstParentWins = 4,
}

/// <summary>成品订单的顶层需求（爆炸起点）。</summary>
public sealed record BomOrderDemand(
    long RootOrderId,
    int RootMaterialId,
    string RootMaterialCode,
    int FactoryId,
    decimal RootQty);

/// <summary>逐路径毛需求行。Path 从根开始以 '/' 连接的物料码序列，是「需求血缘」的最小载体。</summary>
public sealed record BomExplosionLine(
    long RootOrderId,
    int MaterialId,
    string MaterialCode,
    int FactoryId,
    decimal GrossQty,
    int Level,
    string Path,
    bool IsPurchased,
    bool IsLeaf);

/// <summary>去重结构中「父件 → 本物料」的一条边（配比=父件单位需求下本物料用量，血缘载体）。</summary>
public sealed record BomParentEdge(int ParentMaterialId, string ParentMaterialCode, decimal QtyPerUnit)
{
    /// <summary>该父边所指向子件的应归属厂码（BOM 边上的 `ChildRequiredFactory`）。空 = 外购/缺值。</summary>
    public string? ChildRequiredFactoryCode { get; init; }
}

/// <summary>
/// 去重逐层结构节点：本订单内同一物料只一行。Level=该物料最深出现层（LLC 语义），
/// ParentEdges=本订单爆炸中命中的全部父边（共享子件 D 经 B/C → 两父边，路径不丢）。
/// </summary>
public sealed record BomLevelNode(
    int MaterialId,
    string MaterialCode,
    int FactoryId,
    int Level,
    bool IsPurchased,
    IReadOnlyList<BomParentEdge> ParentEdges)
{
    /// <summary>该节点工厂的判定来源（改厂／缺值兜底／厂码查无／多父冲突），供主链留痕。</summary>
    public BomFactorySource FactorySource { get; init; } = BomFactorySource.SameAsParent;
}