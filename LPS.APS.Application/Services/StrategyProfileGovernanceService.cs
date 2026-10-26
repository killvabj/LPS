using StrategyProfile = LPS.APS.Core.Entities.APS.StrategyProfile;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services;

/// <summary>
/// 策略包主表治理服务（2026-10-25：补齐 4号位 缺口「策略包缺主表写端点」，照 rule-set 10-22 范式，3号位 应用编排）
/// 镜像 rule-set 主表写范式：校验 → EnsureWritableAsync 前置 → 仓储写 → AuditAsync 落 AuditLog。
/// Q3 已裁＝跨域通用，故本服务**无 EnsureDomainAllowsAsync 域校验**。
/// Q1：StrategyProfileCode 用户填、唯一（大小写不敏感）、创建后不可变更；Q2：只建壳不自动跟建版本。
/// Q6：aps.strategy.edit；Q8：RunType 仅五值或 NULL（冻结 07 §5.5）。
/// 2026-10-26 同 R1 口径：补主表物理删除（DeleteStrategyProfileAsync，见接口注释；历史不可变守恒由「无 PUBLISHED 才可删」承接）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileGovernanceService : IStrategyProfileGovernanceService
{
    private const string EntityTypeStrategyProfile = "StrategyProfile";
    private const int MaxStrategyProfileCodeLength = 50;   // 冻结 DDL：StrategyProfileCode NVARCHAR(50) NOT NULL UNIQUE
    private const int MaxStrategyProfileNameLength = 200;  // 冻结 DDL：StrategyProfileName NVARCHAR(200) NOT NULL

    private readonly IStrategyProfileRepository _repository;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepository;
    private readonly IScheduleRunRepository _scheduleRunRepository;
    private readonly IAuditLogRepository _auditLogRepository;

    public StrategyProfileGovernanceService(
        IStrategyProfileRepository repository,
        IStrategyProfileVersionRepository strategyProfileVersionRepository,
        IScheduleRunRepository scheduleRunRepository,
        IAuditLogRepository auditLogRepository)
    {
        _repository = repository;
        _strategyProfileVersionRepository = strategyProfileVersionRepository;
        _scheduleRunRepository = scheduleRunRepository;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<StrategyProfile> CreateStrategyProfileAsync(StrategyProfile input, string? createdBy, int actorUserId, CancellationToken ct = default)
    {
        var code = ValidateCode(input.StrategyProfileCode);
        var name = ValidateName(input.StrategyProfileName);
        var runType = ValidateRunType(input.RunType);

        await EnsureCodeUniqueAsync(code, excludeId: null, ct);

        var now = DateTime.Now;
        var entity = new StrategyProfile
        {
            StrategyProfileCode = code,
            StrategyProfileName = name,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            RunType = runType,
            IsActive = true, // 新建默认启用；停用走 PUT IsActive=false（Q5 不给删除端点）
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedBy = createdBy,
            UpdatedAt = now
        };

        // P1-04：审计前置预检（跨库无 2PC）——审计库不可写则整操作失败、业务零变更。
        await _auditLogRepository.EnsureWritableAsync(ct);

        var created = await _repository.CreateAsync(entity, ct);
        await AuditAsync("Create", created, beforeStatus: null, afterStatus: StatusOf(created.IsActive), actorUserId, createdBy, ct);
        return created;
    }

    public async Task<StrategyProfile> UpdateStrategyProfileAsync(long id, StrategyProfile input, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"策略包不存在：{id}");

        var code = ValidateCode(input.StrategyProfileCode);
        var name = ValidateName(input.StrategyProfileName);
        var runType = ValidateRunType(input.RunType);

        // Q1：StrategyProfileCode 一经创建不可变更（对齐 StrategyProfile 表无重命名语义）——大小写差异亦不可改。
        if (!string.Equals(existing.StrategyProfileCode, code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"StrategyProfileCode 一经创建不可变更：{existing.StrategyProfileCode} → {code}");
        }

        await EnsureCodeUniqueAsync(code, excludeId: id, ct);

        var entity = new StrategyProfile
        {
            Id = existing.Id,
            StrategyProfileCode = existing.StrategyProfileCode, // 冻结既有编码
            StrategyProfileName = name,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            RunType = runType,
            IsActive = input.IsActive, // Q4：IsActive 仅关主表，不级联版本（运行时判主表归 2号位/1号位 转达）
            CreatedBy = existing.CreatedBy,
            CreatedAt = existing.CreatedAt,
            UpdatedBy = operatedBy,
            UpdatedAt = DateTime.Now
        };

        // P1-04：审计前置预检（跨库无 2PC）。
        await _auditLogRepository.EnsureWritableAsync(ct);

        await _repository.UpdateAsync(entity, ct);
        await AuditAsync("Update", entity, beforeStatus: StatusOf(existing.IsActive), afterStatus: StatusOf(entity.IsActive), actorUserId, operatedBy, ct);
        return entity;
    }

    /// <inheritdoc />
    public async Task DeleteStrategyProfileAsync(long id, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"策略包不存在：{id}");

        var versions = await _strategyProfileVersionRepository.GetByStrategyProfileIdAsync(id, ct) ?? [];

        // 历史不可变守恒（同 R1）：只要存在 PUBLISHED 版本就拒绝，只可停用。
        if (versions.Any(v => v.Status == GovernanceVersionStatus.Published))
        {
            throw new InvalidOperationException("该策略包已发布过，不能删除（请先停用版本）");
        }

        // FK 反查阻断：任一版本被 ScheduleRun（运行）引用（ScheduleRun.StrategyProfileVersionId FK，无级联）则拒绝，
        // 避免硬删后 FK 违约。
        var versionIds = versions.Select(v => v.Id).ToList();
        if (versionIds.Count > 0 &&
            await _scheduleRunRepository.CountReferencingStrategyProfileVersionAsync(versionIds, ct) > 0)
        {
            throw new InvalidOperationException("该策略包存在已被运行引用的版本，不能删除");
        }

        // P1-04：审计前置预检 + 删前留痕（行将消失，须先落 AuditLog，ActionCode=Delete）。
        await _auditLogRepository.EnsureWritableAsync(ct);
        await AuditAsync("Delete", existing, beforeStatus: StatusOf(existing.IsActive), afterStatus: "Deleted", actorUserId, operatedBy, ct);

        await _repository.DeleteFamilyAsync(id, ct);
    }

    /// <summary>Q1：编码非空 + 长度 ≤ 50；返回 Trim 后的规范化值。</summary>
    private static string ValidateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("StrategyProfileCode 不能为空");
        }

        var trimmed = code.Trim();
        if (trimmed.Length > MaxStrategyProfileCodeLength)
        {
            throw new InvalidOperationException($"StrategyProfileCode 长度不能超过 {MaxStrategyProfileCodeLength} 字符");
        }

        return trimmed;
    }

    /// <summary>名称非空 + 长度 ≤ 200；返回 Trim 后的规范化值。</summary>
    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("StrategyProfileName 不能为空");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxStrategyProfileNameLength)
        {
            throw new InvalidOperationException($"StrategyProfileName 长度不能超过 {MaxStrategyProfileNameLength} 字符");
        }

        return trimmed;
    }

    /// <summary>Q8：RunType 仅接受五值或 NULL（NULL=适用所有运行类型，冻结 07 §5.5）；非法值抛 InvalidOperationException。</summary>
    private static string? ValidateRunType(string? runType)
    {
        if (string.IsNullOrWhiteSpace(runType))
        {
            return null; // 留空 = 适用所有运行类型
        }

        var trimmed = runType.Trim();
        if (!StrategyProfileRunType.All.Contains(trimmed, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"RunType 非法：{trimmed}（合法值：{string.Join("/", StrategyProfileRunType.All)}，或留空表示适用所有运行类型）");
        }

        return trimmed;
    }

    /// <summary>Q1：编码唯一校验（大小写不敏感，仓库侧 COLLATE 契约）；excludeId 更新时排除自身。</summary>
    private async Task EnsureCodeUniqueAsync(string code, long? excludeId, CancellationToken ct)
    {
        if (await _repository.StrategyProfileCodeExistsAsync(code, excludeId, ct))
        {
            throw new InvalidOperationException($"StrategyProfileCode 已存在：{code}");
        }
    }

    private static string StatusOf(bool isActive) => isActive ? "Active" : "Inactive";

    private async Task AuditAsync(
        string operationType,
        StrategyProfile entity,
        string? beforeStatus,
        string? afterStatus,
        int actorUserId,
        string? operatedBy,
        CancellationToken ct)
    {
        // P1-05 + R-01（方案B）：审计写入失败由 AuditLogRepository.AddAsync 统一落对账日志并原样重抛——fail-closed 保持。
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = operationType,
            EntityType = EntityTypeStrategyProfile,
            EntityId = entity.Id.ToString(),
            VersionCode = null,
            OldValue = beforeStatus,
            NewValue = afterStatus,
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = $"策略包 {entity.StrategyProfileCode}（{entity.StrategyProfileName}）"
        }, ct);
    }
}