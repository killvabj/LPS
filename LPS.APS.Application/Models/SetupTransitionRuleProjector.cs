using System.Text.Json;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;

namespace LPS.APS.Application.Models;

/// <summary>
/// Setup 换型规则投影/裁剪纯函数（v1.2 §19.3 ①/②）。无状态、无 IO，供单测钉死：
///   * Project：SetupTransitionRule 实体 → SetupTransitionRuleSnapshot（筛 IsActive，去审计字段）；
///   * CropToDomain：按 Domain（ProductionDepartmentId + StageCode）裁剪，只保留本 Domain 涉及规则。
/// 装载编排在 PeggingOrchestrator.LoadSetupTransitionRulesAsync（取表 → Project → 填第⑦块）；
/// 透传前用 CropToDomain 裁剪。不参与 EXACT/DEFAULT 命中（属 1号位 SetupOptimizer.ResolveSetup）。
/// </summary>
public static class SetupTransitionRuleProjector
{
    /// <summary>筛 IsActive=true 并投影为快照（仅业务键 + 分钟 + 类型，不含审计字段）。</summary>
    public static List<SetupTransitionRuleSnapshot> Project(IEnumerable<SetupTransitionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .Where(r => r.IsActive)
            .Select(r => new SetupTransitionRuleSnapshot
            {
                ProductionDepartmentId = r.ProductionDepartmentId,
                StageCode              = r.StageCode,
                OperationCode          = r.OperationCode,
                ResourceId             = r.ResourceId,
                FromMaterialId         = r.FromMaterialId,
                ToMaterialId           = r.ToMaterialId,
                RuleType               = r.RuleType,
                SetupMinutes           = r.SetupMinutes
            })
            .ToList();
    }

    /// <summary>
    /// 消费端便捷入口（S-8：Project 输入源 = 快照子块，并入反序列化一行到位）：
    /// ContentSnapshotJson 的 SetupTransitionRules 子块 → 消费快照。语义同 <see cref="ExtractRules"/>：
    /// **缺子块 / null / 显式 [] / 空串 → 空列表**（版本可不配置换型规则，合法未配置），随后 Project 筛 IsActive；
    /// **JSON 损坏 → 抛 <see cref="SetupRuleDataRedLineException"/>**（2026-10-14 由 fail-open 改 fail-fast，见 ExtractRules 注释）。
    /// </summary>
    public static List<SetupTransitionRuleSnapshot> ProjectFromSnapshot(string? contentSnapshotJson)
        => Project(ExtractRules(contentSnapshotJson));

    /// <summary>按 Domain 裁剪：保留 (ProductionDepartmentId, StageCode) 落在本 Domain 物料阶段上下文集合内的规则。</summary>
    public static IReadOnlyList<SetupTransitionRuleSnapshot> CropToDomain(
        IReadOnlyList<SetupTransitionRuleSnapshot> rules,
        IReadOnlyList<MaterialStageDepartmentContextDto> domainContexts)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(domainContexts);

        if (rules.Count == 0 || domainContexts.Count == 0)
            return Array.Empty<SetupTransitionRuleSnapshot>();

        var domainKeys = domainContexts
            .Select(c => (c.ProductionDepartmentId, c.StageCode))
            .ToHashSet();

        return rules
            .Where(r => domainKeys.Contains((r.ProductionDepartmentId, r.StageCode)))
            .ToList();
    }

    // ==================== 治理端子块编解码（重构方案 S-1：Setup 规则承载 → RuleSetVersion.ContentSnapshotJson.SetupTransitionRules） ====================
    // 承载语义：治理端规则（含 Id/IsActive/审计字段）以 JSON 数组存于 RuleSetVersion.ContentSnapshotJson 的
    // "SetupTransitionRules" 子块（与 DemandPriority 子块同轨，零 DDL）。消费端仍由 SetupOptimizer 走
    // SetupTransitionRuleSnapshot 快照（本项目不涉及）。仅增删改查编解码，无状态、无 IO，供单测钉死。

    /// <summary>ContentSnapshotJson 子块键名（契约登记 §6.10.5 扩展，S-7 登记件）</summary>
    public const string SetupTransitionRulesBlockName = "SetupTransitionRules";

    /// <summary>
    /// 从 RuleSetVersion.ContentSnapshotJson 提取 Setup 规则列表（反序列化 "SetupTransitionRules" 子块）。
    ///
    /// **三态（2026-10-14 与 ⑧ 块 <see cref="TaskSplitRuleConfigProjector.ExtractRules"/> 同轨）**：
    /// ① 缺子块 / null / 空串 → 空列表（该版本未配置换型规则，**合法未配置**，非装载失败）；
    /// ② 显式空配置 <c>"SetupTransitionRules":[]</c> → 空列表（明确空配置，合法）；
    /// ③ **JSON 结构损坏 → 抛 <see cref="SetupRuleDataRedLineException"/>**（显式失败，不再静默空列表）。
    ///
    /// **为什么 ③ 必须 fail-fast**：「完全没有换型规则」与「本有配置但内容损坏」是两件事，前者是合法
    /// 缺配（无规则 = DEFAULT 兜底 0 分钟），后者是**数据事故**。原 fail-open 把二者压成同一个空列表 ⇒
    /// 损坏版本一路静默降级成「无规则」，1号位 消费侧（<c>PhaseOneConstraintBuilder</c> Setup 分支）
    /// 据此按 0 分钟排出**看似成立、实则丢失换型时间**的计划，且全链无任何告警位可察觉。
    /// 依据：1号位 2026-10-13《BatchPolicy三态载体择定…回执》§五「**应同轨**」；
    /// 边界照该件 §5.3（**仅 JSON 损坏抛异常**；缺子块／<c>null</c>／显式 <c>[]</c> **仍空列表**）。
    ///
    /// ⚠ 与 ⑧ 块的一处**非对称**：本方法的调用方除装载链（<see cref="ProjectFromSnapshot"/>）外，
    /// 还有治理读写链 <c>SetupRuleService</c>（11 处，List/GetById/Create/Update/Delete/Copy/校验）。
    /// 该服务类头已声明「数据红线 → <see cref="SetupRuleDataRedLineException"/>（422）」⇒ 抛异常**在其既定契约内**，
    /// 且修掉了治理侧原有的静默隐患（读到损坏快照时先当空列表、再经 <see cref="SetRules"/> 重建 ⇒ **其余子块被静默丢弃**）。
    /// </summary>
    public static List<SetupTransitionRule> ExtractRules(string? contentSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            return new List<SetupTransitionRule>();
        }

        try
        {
            using var doc = JsonDocument.Parse(contentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(SetupTransitionRulesBlockName, out var block)
                || block.ValueKind == JsonValueKind.Null)
            {
                return new List<SetupTransitionRule>();
            }

            var rules = block.Deserialize<List<SetupTransitionRule>>(JsonOptions);
            return rules ?? new List<SetupTransitionRule>();
        }
        catch (JsonException ex)
        {
            throw new SetupRuleDataRedLineException(
                $"ContentSnapshotJson.SetupTransitionRules 子块 JSON 损坏，无法装载换型规则快照：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 将 Setup 规则列表写回 RuleSetVersion.ContentSnapshotJson（保留其它子块，如 DemandPriority）。
    /// 返回新 ContentSnapshotJson 字符串；原子替换 "SetupTransitionRules" 子块，不触碰其它键。
    /// </summary>
    public static string SetRules(string? contentSnapshotJson, IReadOnlyList<SetupTransitionRule> rules)
    {
        var blocks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(contentSnapshotJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, SetupTransitionRulesBlockName, StringComparison.Ordinal))
                    {
                        blocks[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // 原快照损坏 → 忽略原内容，仅以 Setup 子块重建（写路径 DRAFT 态可重建）。
            }
        }

        blocks[SetupTransitionRulesBlockName] = JsonSerializer.SerializeToElement(rules, JsonOptions);

        return JsonSerializer.Serialize(blocks, JsonOptions);
    }

    /// <summary>治理 Id 合成：ruleSetVersionId 高位 + 版本内序号低位（long，契约 Id 类型不变，全局唯一）。</summary>
    public static long BuildRuleId(long ruleSetVersionId, int seq) => ruleSetVersionId * 10_000_000L + seq;

    /// <summary>治理 Id 解析：拆回 (ruleSetVersionId, 版本内序号)。</summary>
    public static (long RuleSetVersionId, int Seq) ParseRuleId(long id) => (id / 10_000_000L, (int)(id % 10_000_000L));

    /// <summary>
    /// 草稿副本重编（4号位 P0「草稿继承的换型规则不可改不可删」Q1 修复）：
    /// 将随源版本快照整段照抄带进来的 ⑦ 子块，按**新草稿版本 Id 空间**重编——每条规则
    /// <paramref name="ruleSetVersionId"/> 置为新版本、<c>Id</c> 高位改为新版本、低位序号按 1..N 重排。
    /// 使继承行的写路径（SetupRuleService 经 <see cref="ParseRuleId"/> 反查版本）自然归入草稿，
    /// 从而草稿内可改可删（422/400 消除）；已发布源块一字不动（改动仅作用于本函数读入的传入快照副本）。
    /// **纯函数、无状态、无 IO**（同文件其它投影）。
    /// </summary>
    /// <remarks>
    /// 语义边界：
    ///   · 子块缺 / null / 显式 [] / 空串 → 原样返回（无可重编行，不误造负载）。
    ///   · 子块 JSON 损坏 → 抛 <see cref="SetupRuleDataRedLineException"/>（与 <see cref="ExtractRules"/> 三态同轨，fail-fast）。
    ///   · 仅重编 Id 高位与 RuleSetVersionId；业务字段（部门/工艺/工序/设备/产品/分钟/类型/IsActive）原样保持。
    ///   · 重编语义是「复制时重编号」，非把已发布版规则改 Id——本函数不改读入快照的源版本凭据之外任何持久化对象。
    /// </remarks>
    public static string RenumberToVersion(string? contentSnapshotJson, long ruleSetVersionId)
    {
        var rules = ExtractRules(contentSnapshotJson);
        if (rules.Count == 0)
        {
            return contentSnapshotJson; // 原样返回（null→null、空串→空串、无继承行→快照原文）
        }

        var seq = 0;
        var renumbered = new List<SetupTransitionRule>(rules.Count);
        foreach (var rule in rules)
        {
            seq++;
            renumbered.Add(new SetupTransitionRule
            {
                Id                 = BuildRuleId(ruleSetVersionId, seq),
                RuleSetVersionId   = ruleSetVersionId,
                ProductionDepartmentId = rule.ProductionDepartmentId,
                StageCode          = rule.StageCode,
                OperationCode      = rule.OperationCode,
                ResourceId         = rule.ResourceId,
                FromMaterialId     = rule.FromMaterialId,
                ToMaterialId       = rule.ToMaterialId,
                RuleType           = rule.RuleType,
                SetupMinutes       = rule.SetupMinutes,
                IsActive           = rule.IsActive,
                CreatedAt          = rule.CreatedAt,
                CreatedBy          = rule.CreatedBy,
                UpdatedAt          = rule.UpdatedAt,
                UpdatedBy          = rule.UpdatedBy,
            });
        }

        return SetRules(contentSnapshotJson, renumbered);
    }

    /// <summary>子块序列化选项（属性名原样，不启用缩进——ContentSnapshotJson 单列存储）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // ==================== Setup 业务键单源（4号位 P0 Q2：CompareRuleSetVersions ⑦ 子块业务键对比口径） ====================
    // 服务端唯一键「EXACT 七元组 / DEFAULT 五元组」此前散落在 SetupRuleService.BuildKey（GetDiffAsync 用）。
    // Q2 要求版本 Diff 的 ⑦ 子块**按业务键**对比（非原始 JSON 串——Id 高低位重编/行序变化不判变更），
    // 且与 GetDiffAsync **同口径防双实现漂移**。故把键构造抽到本投影器作单源，SetupRuleService.BuildKey 委托此处；
    // 本文件 AllMeta（SetupTransitionRules 子块无状态纯函数）继续维持无状态、无 IO、供单测钉死。

    /// <summary>
    /// Setup 换型规则**业务键**（EXACT 七元组：DEPT|Stage|Op|Resource|From|To；DEFAULT 五元组：DEPT|Stage|Op|Resource；
    /// 前缀 E/D 区分类型，杜绝跨类误并）。与 <see cref="SetupRuleService.GetDiffAsync"/> 同一口径（唯一键判重/变更定位），
    /// 单源在投影器，防 SetupRuleService 与版本 Diff 两处实现漂移。
    /// </summary>
    public static string BuildBusinessKey(SetupTransitionRule rule)
    {
        if (rule.RuleType == SetupTransitionRuleType.Exact)
        {
            return $"E|{rule.ProductionDepartmentId}|{rule.StageCode}|{rule.OperationCode}|{rule.ResourceId}|{rule.FromMaterialId}|{rule.ToMaterialId}";
        }

        return $"D|{rule.ProductionDepartmentId}|{rule.StageCode}|{rule.OperationCode}|{rule.ResourceId}";
    }

    /// <summary>
    /// ⑦ 子块**业务内容对比**结果（纯函数，供版本 Diff 消费；语义与 <see cref="SetupRuleService.GetDiffAsync"/> 一致，
    /// 但后者做 Dept Scope 过滤 + Code/Before-After 投影，此处仅回答「业务键维度是否变更」三计数）。
    /// 判定规则：按业务键对齐源/目标规则列表 ——
    ///   · 目标有而源无 → Added；
    ///   · 源有目标无 → Removed；
    ///   · 双方都有但 SetupMinutes 不同 → Modified；
    ///   · Id/顺序/审计字段差异一律不判变更（Q2：fork 重编高位、行序变化均为实现细节，非业务变更）。
    /// 入参为已反序列化规则（调用方走 <see cref="ExtractRules"/>，缺子块/null/[] → 空列表，合法缺配）。
    /// </summary>
    public static SetupRuleBusinessDiffResult CompareBusinessContent(
        IReadOnlyList<SetupTransitionRule> source,
        IReadOnlyList<SetupTransitionRule> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var sourceByKey = source.ToDictionary(BuildBusinessKey);
        var targetByKey = target.ToDictionary(BuildBusinessKey);

        int added = 0, removed = 0, modified = 0;
        foreach (var (key, targetRule) in targetByKey)
        {
            if (!sourceByKey.TryGetValue(key, out var sourceRule))
            {
                added++;
            }
            else if (sourceRule.SetupMinutes != targetRule.SetupMinutes)
            {
                modified++;
            }
        }

        foreach (var key in sourceByKey.Keys)
        {
            if (!targetByKey.ContainsKey(key))
            {
                removed++;
            }
        }

        return new SetupRuleBusinessDiffResult
        {
            AddedCount = added,
            ModifiedCount = modified,
            RemovedCount = removed,
        };
    }
}

/// <summary>⑦ 子块业务内容对比结果（纯数据；供版本 Diff/前端标「变化量」）。</summary>
public class SetupRuleBusinessDiffResult
{
    /// <summary>目标有而源无的规则数（按业务键）</summary>
    public int AddedCount { get; set; }

    /// <summary>双方按业务键匹配但 SetupMinutes 不同的规则数</summary>
    public int ModifiedCount { get; set; }

    /// <summary>源有而目标无的规则数（按业务键）</summary>
    public int RemovedCount { get; set; }

    /// <summary>是否存在业务内容变更（任一计数不为 0）</summary>
    public bool IsChanged => AddedCount != 0 || ModifiedCount != 0 || RemovedCount != 0;
}
