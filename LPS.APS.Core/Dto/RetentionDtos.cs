namespace LPS.APS.Core.Dto;

/// <summary>
/// 保留期清理——**单条规则的测算结果**（D1~D4 各一条，或多条）。
/// </summary>
/// <remarks>
/// <para>
/// 三个数必须同时给出，缺一即无法判断规则是否按预期生效：
/// <list type="bullet">
/// <item><see cref="TotalRows"/> —— 该表当前总行数（用来判「是不是压根没数据」，避免把「0 可删」误读成「脚本没跑」）</item>
/// <item><see cref="OverWindowRows"/> —— 超过在线窗口的行数（规则的第一道筛）</item>
/// <item><see cref="ProtectedRows"/> —— 超窗但被保护集挡住的行数（证明保护逻辑确实在挡）</item>
/// <item><see cref="DeletableRows"/> —— 真正可删的行数 = 超窗 − 受保护</item>
/// </list>
/// </para>
/// <para>
/// ⚠ 2026-11-02 实测：本库最老数据仅 4 天 ⇒ 全部 <see cref="DeletableRows"/> = 0。
/// 这是**正确结果**，不是失效。判空请看 <see cref="TotalRows"/>。
/// </para>
/// </remarks>
public sealed class RetentionPlanItem
{
    /// <summary>规则名，如 <c>D1-a ExplainTrace</c>。</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>目标表名。</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>在线窗口天数（规则里写死的那个数）。</summary>
    public int OnlineWindowDays { get; init; }

    /// <summary>该表当前总行数。</summary>
    public long TotalRows { get; init; }

    /// <summary>超过在线窗口的行数。</summary>
    public long OverWindowRows { get; init; }

    /// <summary>超窗但命中保护集、必须保留的行数。</summary>
    public long ProtectedRows { get; init; }

    /// <summary>真正可删的行数。</summary>
    public long DeletableRows { get; init; }

    /// <summary>本规则的补充说明（如「未解决失败不删」导致的永久保留量）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// 保留期清理——**一次运行的结果汇总**。
/// </summary>
public sealed class RetentionRunResult
{
    /// <summary>是否只测算未删除。<c>true</c> 时 <see cref="DeletedRows"/> 恒为 0。</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>运行时刻（本地时间，全链路禁 UTC）。</summary>
    public DateTime RanAt { get; init; }

    /// <summary>逐条规则的测算结果。</summary>
    public IReadOnlyList<RetentionPlanItem> Items { get; init; } = Array.Empty<RetentionPlanItem>();

    /// <summary>实际删除的总行数（<see cref="DryRun"/> = true 时恒 0）。</summary>
    public long DeletedRows { get; set; }

    /// <summary>失败原因；为空表示未发生异常。清理失败**不得反推正式排程失败**。</summary>
    public string? Error { get; set; }

    /// <summary>可删总行数（= 各规则 <see cref="RetentionPlanItem.DeletableRows"/> 之和）。</summary>
    public long TotalDeletable => Items.Sum(i => i.DeletableRows);
}
