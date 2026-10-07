using System.Text.Json;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Application.Models;

/// <summary>
/// Batch Policy（批量策略）投影/编解码纯函数（0号位 2026-10-07 裁决：本轮落码）。无状态、无 IO，供单测钉死：
///   * Project：TaskSplitRuleConfig 实体 → BatchPolicyRuleSnapshot（筛 IsActive + 生效区间，去审计/兼容字段）；
///   * SetRules / ExtractRules：ContentSnapshotJson.BatchPolicy 子块编解码（与 SetupTransitionRules 同轨，零 DDL）。
/// 发布编排在 GovernanceVersionService.PublishParameterSetVersionAsync（读表 active 规则 → Project → 注入第⑧块源）；
/// 装载在 FrozenStrategySnapshotProvider.DeserializeBatchPolicy（ContentSnapshotJson.BatchPolicy 子块 → 第⑧块）。
/// 缺策略 fail-closed（BATCH_POLICY_MISSING）属 1号位 消费侧行为（查无 (Material,Dept) 命中规则即失败），本投影不承载信号位。
/// 开发者：3号位
/// </summary>
public static class TaskSplitRuleConfigProjector
{
    /// <summary>ContentSnapshotJson 子块键名（契约登记 §6.10.5 扩展）。</summary>
    public const string BatchPolicyBlockName = "BatchPolicy";

    /// <summary>瓶颈批量策略合法枚举（PREFER_SPLIT / PREFER_MERGE）。</summary>
    public static readonly System.Collections.Frozen.FrozenSet<string> ValidBottleneckSplitStrategies =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(["PREFER_SPLIT", "PREFER_MERGE"]);

    /// <summary>非瓶颈批量策略合法枚举（PREFER_LARGE_BATCH / PREFER_SMALL_BATCH）。</summary>
    public static readonly System.Collections.Frozen.FrozenSet<string> ValidNonBottleneckStrategies =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(["PREFER_LARGE_BATCH", "PREFER_SMALL_BATCH"]);

    /// <summary>
    /// 把 active + 生效区间内规则投影为快照（去审计字段 Id/Created/Updated/IsActive + 兼容字段，仅业务键 + 参数）。
    /// 生效区间以 asOf 判定（发布时传入，默认当前 UTC）；IsActive=false 或超出生效窗的行排除。
    /// </summary>
    public static List<BatchPolicyRuleSnapshot> Project(IEnumerable<TaskSplitRuleConfig> rules, DateTime? asOf = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var now = asOf ?? DateTime.UtcNow;

        return rules
            .Where(r => r.IsActive
                && (r.EffectiveFrom is null || r.EffectiveFrom <= now)
                && (r.EffectiveTo is null || r.EffectiveTo >= now))
            .Select(r => new BatchPolicyRuleSnapshot
            {
                MaterialId = r.MaterialId,
                ProductionDepartmentId = r.ProductionDepartmentId,
                MinExecutionBatchQty = r.MinExecutionBatchQty,
                MaxExecutionBatchQty = r.MaxExecutionBatchQty,
                PreferredBatchQty = r.PreferredBatchQty,
                AllowSplit = r.AllowSplit,
                AllowMerge = r.AllowMerge,
                MaxOptimizationSplitCount = r.MaxOptimizationSplitCount,
                MaxBatchCandidates = r.MaxBatchCandidates,
                BottleneckSplitStrategy = r.BottleneckSplitStrategy,
                NonBottleneckStrategy = r.NonBottleneckStrategy,
            })
            .ToList();
    }

    /// <summary>装载端便捷入口：ContentSnapshotJson.BatchPolicy 子块 → 第⑧块快照（fail-open：缺失/为空/损坏 → 空列表）。</summary>
    public static List<BatchPolicyRuleSnapshot> ProjectFromSnapshot(string? contentSnapshotJson)
        => ExtractRules(contentSnapshotJson);

    /// <summary>提取 BatchPolicy 子块（反序列化 BatchPolicyRuleSnapshot 列表；缺失/为 null/损坏 → 空列表）。</summary>
    public static List<BatchPolicyRuleSnapshot> ExtractRules(string? contentSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            return new List<BatchPolicyRuleSnapshot>();
        }

        try
        {
            using var doc = JsonDocument.Parse(contentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(BatchPolicyBlockName, out var block))
            {
                return new List<BatchPolicyRuleSnapshot>();
            }

            var rules = block.Deserialize<List<BatchPolicyRuleSnapshot>>(JsonOptions);
            return rules ?? new List<BatchPolicyRuleSnapshot>();
        }
        catch (JsonException)
        {
            return new List<BatchPolicyRuleSnapshot>();
        }
    }

    /// <summary>将 BatchPolicy 规则列表写回 ContentSnapshotJson（保留其它子块，原子替换 BatchPolicy 键）。</summary>
    public static string SetRules(string? contentSnapshotJson, IReadOnlyList<BatchPolicyRuleSnapshot> rules)
    {
        var blocks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(contentSnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(contentSnapshotJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, BatchPolicyBlockName, StringComparison.Ordinal))
                    {
                        blocks[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // 原快照损坏 → 忽略原内容，仅以 BatchPolicy 子块重建（与 Setup 规则写路径同语义）。
            }
        }

        blocks[BatchPolicyBlockName] = JsonSerializer.SerializeToElement(rules, JsonOptions);

        return JsonSerializer.Serialize(blocks, JsonOptions);
    }

    /// <summary>子块序列化选项（属性名原样，不启用缩进——ContentSnapshotJson 单列存储）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}