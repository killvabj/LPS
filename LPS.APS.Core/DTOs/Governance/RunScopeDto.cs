namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 单 Run 候选范围明细（run/{id}/scope 端点响应；G4 通道，5号位）。
/// 契约形状：4号位 10-19《候选scope明细端点改归你侧》§二；ScopeJson(SchemaVersion=2) 投影 + 本地表展示值富化。
/// null 纪律：Run 存在但 ScopeJson=NULL → ScopeAvailable=false、明细/计数全 null（不是 0）；
/// 目标行查不到映射（订单/任务/资源已清理）→ 该行展示字段 null，整行保留、不 500。
/// </summary>
public sealed class RunScopeDto
{
    /// <summary>ScheduleRun.Id。JSON: scheduleRunId</summary>
    public int ScheduleRunId { get; init; }

    /// <summary>JSON: runType</summary>
    public string? RunType { get; init; }

    /// <summary>是否存在可用范围载荷：false=ScopeJson 为 NULL/坏 JSON（历史夹具或旧调用），五投影与计数为 null。JSON: scopeAvailable</summary>
    public bool ScopeAvailable { get; init; }

    /// <summary>JSON: schemaVersion（scopeAvailable=false 时 null）</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>业务触发类型（EnumMember 契约字面量，如 EXISTING_ORDER_ADVANCE）。JSON: businessTriggerType</summary>
    public string? BusinessTriggerType { get; init; }

    /// <summary>优先级模式；null=该触发本就不走优先级（禁兜底 NORMAL）。JSON: priorityMode</summary>
    public string? PriorityMode { get; init; }

    /// <summary>JSON: basePlanVersionId</summary>
    public int? BasePlanVersionId { get; init; }

    /// <summary>JSON: baseVersionCode</summary>
    public string? BaseVersionCode { get; init; }

    /// <summary>订单目标明细（含本地表富化）。JSON: orderTargets</summary>
    public IReadOnlyList<RunScopeOrderTargetDto>? OrderTargets { get; init; }

    /// <summary>任务目标明细。JSON: taskTargets</summary>
    public IReadOnlyList<RunScopeTaskTargetDto>? TaskTargets { get; init; }

    /// <summary>变更资源明细（按资源 Id 去重）。JSON: changedResources</summary>
    public IReadOnlyList<RunScopeResourceDto>? ChangedResources { get; init; }

    /// <summary>三计数（scopeAvailable=false 时 null；计数口径与 Brief 一致）。JSON: counts</summary>
    public RunScopeCountsDto? Counts { get; init; }
}

/// <summary>订单目标行。orderNo/mtSInstructionNo 来自 Order_Canonical 本地表（4号位 §3.1：无需跨号位映射面）。</summary>
public sealed class RunScopeOrderTargetDto
{
    /// <summary>JSON: orderCanonicalId</summary>
    public long OrderCanonicalId { get; init; }

    /// <summary>JSON: orderNo（查不到=历史清理 → null）</summary>
    public string? OrderNo { get; init; }

    /// <summary>生产指示号；CamelCase 后 JSON 键为 mtS_InstructionNo。JSON: mtS_InstructionNo（注意实际序列化键 mtS_）</summary>
    public string? MTS_InstructionNo { get; init; }

    /// <summary>本次 Run 的手工目标交期（不改正式 DueDate）。JSON: manualTargetDueDate</summary>
    public DateTime? ManualTargetDueDate { get; init; }
}

/// <summary>任务目标行；Task 表 Id 全局唯一（PK(Id,PlanVersionId) 前导列），按 Id 直查、无结果版本的 Run 也能带出（§3.2）。</summary>
public sealed class RunScopeTaskTargetDto
{
    /// <summary>JSON: taskId</summary>
    public long TaskId { get; init; }

    /// <summary>JSON: taskNo（查不到 → null）</summary>
    public string? TaskNo { get; init; }

    /// <summary>JSON: operationCode</summary>
    public string? OperationCode { get; init; }

    /// <summary>软目标时间。JSON: targetTime</summary>
    public DateTime? TargetTime { get; init; }
}

/// <summary>变更资源行。</summary>
public sealed class RunScopeResourceDto
{
    /// <summary>JSON: resourceId</summary>
    public int ResourceId { get; init; }

    /// <summary>JSON: resourceCode（查不到 → null）</summary>
    public string? ResourceCode { get; init; }

    /// <summary>JSON: resourceName</summary>
    public string? ResourceName { get; init; }
}

/// <summary>三计数（与 CandidateBrief 同口径：changedResources=去重后资源个数，非任务数）。</summary>
public sealed class RunScopeCountsDto
{
    /// <summary>JSON: orderTargets</summary>
    public int OrderTargets { get; init; }

    /// <summary>JSON: taskTargets</summary>
    public int TaskTargets { get; init; }

    /// <summary>JSON: changedResources</summary>
    public int ChangedResources { get; init; }
}
