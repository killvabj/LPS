using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// 治理查询业务服务（G4/G7查询）
///
/// 5号位提供给4号位的只读查询服务
/// 直接读取APS_Production事实表，不重算业务
/// </summary>
public class GovernanceQueryService
{
    private readonly IGovernanceQueryRepository _repository;
    private readonly ILogger<GovernanceQueryService>? _logger;

    public GovernanceQueryService(IGovernanceQueryRepository repository, ILogger<GovernanceQueryService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
    }

    // ScopeJson 读侧：大小写容忍；枚举走类型级 EnumMemberJsonConverter（勿挂 JsonStringEnumConverter，
    // 4号位 10-19 §3.3 / 2号位 ScheduleRunRepository:169-171 同结论）。
    private static readonly JsonSerializerOptions ScopeReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static string? EnumLiteral(object? enumValue)
        => enumValue is null ? null : JsonSerializer.SerializeToElement(enumValue, ScopeReadOptions).GetString();

    /// <summary>
    /// 查询排程运行列表（G4）
    /// </summary>
    public async Task<List<ScheduleRunGov>> QueryScheduleRunsAsync(
        string? status = null,
        string? runType = null,
        int take = 100,
        CancellationToken ct = default)
    {
        // 越界夹上限不静默回落（4号位 10-19 R2；本端点上限 200 档）。
        if (take <= 0) take = 100;
        if (take > 200) take = 200;

        return await _repository.QueryScheduleRunsAsync(status, runType, take, ct);
    }

    /// <summary>
    /// 查询排程域依赖（G7）
    /// </summary>
    public async Task<List<DomainDependencyDto>> QueryDomainDependenciesAsync(
        string? domainCode = null,
        string? direction = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedDomains = null)
    {
        return await _repository.QueryDomainDependenciesAsync(domainCode, direction, ct, allowedDomains);
    }

    /// <summary>
    /// 单 Run 候选范围明细（run/{id}/scope；G4，归属变更承接：3号位 撤回设计、5号位 落码）。
    /// 返回 null = Run 不存在（Controller → 404）；Run 在但 ScopeJson NULL/坏 → ScopeAvailable=false 的 200，
    /// 坏 JSON 不 500（4号位 §二 硬要求 1/3 的「200+false+日志」降级选项）。
    /// </summary>
    public async Task<RunScopeDto?> GetRunScopeAsync(int scheduleRunId, CancellationToken ct = default)
    {
        var raw = await _repository.GetRunScopeRawAsync(scheduleRunId, ct);
        if (raw is null) return null;

        ScopeJsonV2? v2 = null;
        if (!string.IsNullOrWhiteSpace(raw.ScopeJson))
        {
            try
            {
                v2 = JsonSerializer.Deserialize<ScopeJsonV2>(raw.ScopeJson, ScopeReadOptions);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "run/{id}/scope: Run {RunId} ScopeJson 反序列化失败，降级 scopeAvailable=false", raw.Id);
            }
        }

        if (v2 is null)
        {
            // 夹具/老 Run 无载荷：明细与计数全 null（不是 0），仅回身份与 Base（可回显）。
            return new RunScopeDto
            {
                ScheduleRunId = raw.Id,
                RunType = raw.RunType,
                ScopeAvailable = false,
                BasePlanVersionId = raw.BasePlanVersionId,
                BaseVersionCode = raw.BaseVersionCode,
            };
        }

        // 显式 null 列表归一空列表：System.Text.Json 遇 `"xxx": null` 会用 null 覆盖 [] 初始值；
        // 且 NRE/ArgumentNullException 不是 JsonException、上面降级闸接不住（4号位 10-19 §二 漏口修复）。
        var orderTargets = v2.OrderTargets ?? [];
        var taskTargets = v2.TaskTargets ?? [];
        var resourceIds = (v2.ChangedResourceIds ?? []).Distinct().ToList();   // 去重后计数（硬要求 4）
        var orders = await _repository.GetOrderCanonicalMapAsync(orderTargets.Select(t => t.OrderCanonicalId).ToList(), ct);
        var tasks = await _repository.GetTaskMapAsync(taskTargets.Select(t => t.TaskId).ToList(), ct);
        var resources = await _repository.GetResourceMapAsync(resourceIds, ct);

        return new RunScopeDto
        {
            ScheduleRunId = raw.Id,
            RunType = raw.RunType,
            ScopeAvailable = true,
            SchemaVersion = v2.SchemaVersion,
            BusinessTriggerType = System.Enum.IsDefined(v2.Trigger) ? EnumLiteral(v2.Trigger) : null,  // 缺 trigger/未定义值(如默认0)→null，不透出"0"（4号位 §三）
            PriorityMode = EnumLiteral(v2.PriorityMode),               // null 直传，禁兜底 NORMAL
            BasePlanVersionId = raw.BasePlanVersionId,
            BaseVersionCode = raw.BaseVersionCode,
            OrderTargets = orderTargets
                .Select(t => orders.TryGetValue(t.OrderCanonicalId, out var m)
                    ? new RunScopeOrderTargetDto { OrderCanonicalId = t.OrderCanonicalId, OrderNo = m.OrderNo, MTS_InstructionNo = m.MTS_InstructionNo, ManualTargetDueDate = t.ManualTargetDueDate }
                    : new RunScopeOrderTargetDto { OrderCanonicalId = t.OrderCanonicalId, ManualTargetDueDate = t.ManualTargetDueDate })   // 查不到保留行、展示字段 null
                .ToList(),
            TaskTargets = taskTargets
                .Select(t => tasks.TryGetValue(t.TaskId, out var m)
                    ? new RunScopeTaskTargetDto { TaskId = t.TaskId, TaskNo = m.TaskNo, OperationCode = m.OperationCode, TargetTime = t.TargetTime }
                    : new RunScopeTaskTargetDto { TaskId = t.TaskId, TargetTime = t.TargetTime })
                .ToList(),
            ChangedResources = resourceIds
                .Select(id => resources.TryGetValue(id, out var m)
                    ? new RunScopeResourceDto { ResourceId = id, ResourceCode = m.ResourceCode, ResourceName = m.ResourceName }
                    : new RunScopeResourceDto { ResourceId = id })
                .ToList(),
            Counts = new RunScopeCountsDto
            {
                OrderTargets = orderTargets.Count,
                TaskTargets = taskTargets.Count,
                ChangedResources = resourceIds.Count,
            },
        };
    }
}
