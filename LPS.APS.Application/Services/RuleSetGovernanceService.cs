using RuleSet = LPS.APS.Core.Entities.APS.RuleSet;
using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;

namespace LPS.APS.Application.Services;

/// <summary>
/// 规则集主表治理服务（2026-10-22：补齐 4号位 缺口「主表零写端点」，3号位 应用编排）
/// 镜像 DomainDefinition 主表写范式：校验 → EnsureWritableAsync 前置 → 仓储写 → AuditAsync 落 AuditLog。
/// Q3 已裁（0号位 D1 2026-09-28 方案 A）＝跨域通用，故本服务**无 EnsureDomainAllowsAsync 域校验**。
/// Q1：RuleSetCode 用户填、唯一（大小写不敏感）、创建后不可变更；Q2：两步建版本；Q5：不给删除；Q6：aps.rule.edit。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class RuleSetGovernanceService : IRuleSetGovernanceService
{
    private const string EntityTypeRuleSet = "RuleSet";
    private const string EntityTypeRuleSetVersion = "RuleSetVersion";
    private const int MaxRuleSetCodeLength = 50;   // 冻结 DDL：RuleSetCode NVARCHAR(50) NOT NULL UNIQUE
    private const int MaxRuleSetNameLength = 200;  // 冻结 DDL：RuleSetName NVARCHAR(200) NOT NULL

    private readonly IRuleSetRepository _repository;
    private readonly IRuleSetVersionRepository _ruleSetVersionRepository;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepository;
    private readonly IAuditLogRepository _auditLogRepository;

    public RuleSetGovernanceService(
        IRuleSetRepository repository,
        IRuleSetVersionRepository ruleSetVersionRepository,
        IStrategyProfileVersionRepository strategyProfileVersionRepository,
        IAuditLogRepository auditLogRepository)
    {
        _repository = repository;
        _ruleSetVersionRepository = ruleSetVersionRepository;
        _strategyProfileVersionRepository = strategyProfileVersionRepository;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<RuleSet> CreateRuleSetAsync(RuleSet input, string? createdBy, int actorUserId, CancellationToken ct = default)
    {
        var code = ValidateCode(input.RuleSetCode);
        var name = ValidateName(input.RuleSetName);

        await EnsureCodeUniqueAsync(code, excludeId: null, ct);

        var now = DateTime.Now;
        var entity = new RuleSet
        {
            RuleSetCode = code,
            RuleSetName = name,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
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

    public async Task<RuleSet> UpdateRuleSetAsync(long id, RuleSet input, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"规则集不存在：{id}");

        var code = ValidateCode(input.RuleSetCode);
        var name = ValidateName(input.RuleSetName);

        // Q1：RuleSetCode 一经创建不可变更（对齐 RuleSet 表无重命名语义）——大小写差异亦不可改。
        if (!string.Equals(existing.RuleSetCode, code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"RuleSetCode 一经创建不可变更：{existing.RuleSetCode} → {code}");
        }

        await EnsureCodeUniqueAsync(code, excludeId: id, ct);

        var entity = new RuleSet
        {
            Id = existing.Id,
            RuleSetCode = existing.RuleSetCode, // 冻结既有编码
            RuleSetName = name,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
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
    public async Task DeleteRuleSetAsync(long id, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var existing = await _repository.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException($"规则集不存在：{id}");

        var versions = await _ruleSetVersionRepository.GetByRuleSetIdAsync(id, ct) ?? [];

        // 历史不可变守恒：只要存在 PUBLISHED 版本就拒绝，只可停用（Q5 + R1 例外判定）。
        if (versions.Any(v => v.Status == GovernanceVersionStatus.Published))
        {
            throw new InvalidOperationException("该规则集已发布过，不能删除（请先停用版本）");
        }

        // FK 防护：任一版本被策略包引用（无级联）则拒绝，避免硬删 INSERT 后 FK 违约。
        var versionIds = versions.Select(v => v.Id).ToList();
        if (versionIds.Count > 0 &&
            await _strategyProfileVersionRepository.CountReferencingRuleSetVersionAsync(versionIds, ct) > 0)
        {
            throw new InvalidOperationException("该规则集存在被策略包引用的版本，不能删除");
        }

        // P1-04：审计前置预检 + 删前留痕（行将消失，须先落 AuditLog，ActionCode=Delete）。
        await _auditLogRepository.EnsureWritableAsync(ct);
        await AuditAsync("Delete", existing, beforeStatus: StatusOf(existing.IsActive), afterStatus: "Deleted", actorUserId, operatedBy, ct);

        await _repository.DeleteFamilyAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task DeleteRuleSetVersionAsync(long versionId, string? operatedBy, int actorUserId, CancellationToken ct = default)
    {
        var version = await _ruleSetVersionRepository.GetByIdAsync(versionId, ct)
            ?? throw new InvalidOperationException($"规则集版本不存在：{versionId}");

        // 仅 DRAFT 草稿壳可删；已发布/停用/归档保留（历史不可变守恒）。
        if (version.Status != GovernanceVersionStatus.Draft)
        {
            throw new InvalidOperationException($"仅草稿版本可删除（当前状态：{version.Status}）");
        }

        // FK 防护：被策略包引用则拒绝。
        if (await _strategyProfileVersionRepository.CountReferencingRuleSetVersionAsync([versionId], ct) > 0)
        {
            throw new InvalidOperationException("该版本被策略包引用，不能删除");
        }

        // P1-04：审计前置预检 + 删前留痕（ActionCode=Delete，EntityType=RuleSetVersion）。
        await _auditLogRepository.EnsureWritableAsync(ct);
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = "Delete",
            EntityType = EntityTypeRuleSetVersion,
            EntityId = versionId.ToString(),
            VersionCode = version.VersionCode,
            OldValue = "Draft",
            NewValue = "Deleted",
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = $"规则集版本 {version.VersionCode}（归属规则集 {version.RuleSetId}）"
        }, ct);

        await _ruleSetVersionRepository.DeleteByIdAsync(versionId, ct);
    }

    /// <summary>Q1：编码非空 + 长度 ≤ 50；返回 Trim 后的规范化值。</summary>
    private static string ValidateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("RuleSetCode 不能为空");
        }

        var trimmed = code.Trim();
        if (trimmed.Length > MaxRuleSetCodeLength)
        {
            throw new InvalidOperationException($"RuleSetCode 长度不能超过 {MaxRuleSetCodeLength} 字符");
        }

        return trimmed;
    }

    /// <summary>名称非空 + 长度 ≤ 200；返回 Trim 后的规范化值。</summary>
    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("RuleSetName 不能为空");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxRuleSetNameLength)
        {
            throw new InvalidOperationException($"RuleSetName 长度不能超过 {MaxRuleSetNameLength} 字符");
        }

        return trimmed;
    }

    /// <summary>Q1：编码唯一校验（大小写不敏感，仓库侧 COLLATE 契约）；excludeId 更新时排除自身。</summary>
    private async Task EnsureCodeUniqueAsync(string code, long? excludeId, CancellationToken ct)
    {
        if (await _repository.RuleSetCodeExistsAsync(code, excludeId, ct))
        {
            throw new InvalidOperationException($"RuleSetCode 已存在：{code}");
        }
    }

    private static string StatusOf(bool isActive) => isActive ? "Active" : "Inactive";

    private async Task AuditAsync(
        string operationType,
        RuleSet entity,
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
            EntityType = EntityTypeRuleSet,
            EntityId = entity.Id.ToString(),
            VersionCode = null,
            OldValue = beforeStatus,
            NewValue = afterStatus,
            UserId = actorUserId,
            UserCode = operatedBy,
            OccurredAt = DateTime.Now,
            Remark = $"规则集 {entity.RuleSetCode}（{entity.RuleSetName}）"
        }, ct);
    }
}