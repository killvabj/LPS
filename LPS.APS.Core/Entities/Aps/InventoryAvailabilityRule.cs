using System.ComponentModel.DataAnnotations.Schema;

namespace LPS.APS.Core.Entities.APS;

/// <summary>
/// 统一库存可用规则表（L3：准入 + 扣减优先级）
/// 对应 APS_Production.InventoryAvailabilityRule（DDL v5.0.39 V1 统一口径）
///
/// V1 口径变更：本表取代并删除了以下旧表（v5.0.39 起已删除）：
///   · ProductFamilyInventoryScope（旧 §2.5.1 准入规则）  -- 已删除
///   · InventorySourceRule（旧 §2.5.2 来源优先级/排除）   -- 已删除
///   · InventorySourcePriority（旧 §2.6，v2.8 已废弃）    -- 已删除
///
/// 业务用途：一张表回答两个问题——
///   1. 某工厂 + 某产品族 + 某物料模式下，允许哪些 SourceSystem + StorageCode 进入可用库存池？
///   2. 允许时，扣减优先级是多少？（Priority 数值越小越优先）
///
/// 废弃口径：RuleAction(PREFER/EXCLUDE) 已废止 → 改用 IsAvailable + Priority 描述准入/扣除与顺序。
///
/// 库存链路定位（V1 统一口径）：
///   InventoryFact_ERP / InventoryFact_MES
///     → InventorySupplyCandidate（候选池）
///       → 经本表（InventoryAvailabilityRule）裁决「哪些源可用 + 扣减优先级」→ IsEligible
///         → 汇总至 InventoryBalance（排程读取的唯一库存真相）
///   同一物料可在不同产品族上下文下形成不同的可用库存池（因本表范围不同）。
///
/// 归属：5号位 负责本表内容维护 + Pegging 时的运行时判定（需凭授权新建策略并跨号位评审）。
/// </summary>
[Table("InventoryAvailabilityRule")]
public class InventoryAvailabilityRule
{
    public long Id { get; set; }

    /// <summary>库存使用上下文产品族（非物料自身产品族）</summary>
    public int ProductFamilyId { get; set; }

    /// <summary>规则所属工厂</summary>
    public int FactoryId { get; set; }

    /// <summary>物料过滤模式，支持 LIKE 通配符（如 CYL-%）；NULL = 该工厂+产品族下所有物料通用</summary>
    public string? MaterialCodePattern { get; set; }

    /// <summary>来源系统：ERP / MES</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>仓库/库位代码（V1 统一使用 WarehouseCode）</summary>
    public string StorageCode { get; set; } = string.Empty;

    /// <summary>1=允许进入可用库存池；0=排除</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>扣减优先级（数值越小越优先）</summary>
    public int Priority { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}