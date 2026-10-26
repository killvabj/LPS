using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Entities.Auth;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Application.Services.Query;
using LPS.APS.Application.Services.Query.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Web.Controllers;

/// <summary>
/// 治理版本管理 API（阶段 A-9：3号位 Web 层实现）
/// 提供 RuleSetVersion / ParameterSetVersion 的 CRUD + 发布接口。
/// 红线：发布接口仅接受 DRAFT/SUBMITTED/APPROVED 状态（六态状态机 R01 验收）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
[ApiController]
[Route("api/[controller]")]
public class GovernanceController : ControllerBase
{
    private readonly IGovernanceVersionService _governanceService;
    private readonly IRuleSetVersionRepository _ruleSetVersionRepo;
    private readonly IParameterSetVersionRepository _parameterSetVersionRepo;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepo;
    private readonly IRuleSetRepository _ruleSetRepo;
    private readonly IParameterSetRepository _parameterSetRepo;
    private readonly IStrategyProfileRepository _strategyProfileRepo;
    private readonly IDomainDependencyRepository _domainDependencyRepo;
    private readonly IAuditLogRepository _auditLogRepo;
    private readonly IRunLifecycleService _runLifecycleService;
    private readonly IScheduleRunRepository _scheduleRunRepo;
    private readonly IScheduleQueryService _queryService;
    private readonly IDomainDefinitionGovernanceService _domainDefinitionService;
    private readonly IRuleSetGovernanceService _ruleSetGovernanceService;
    private readonly IParameterSetGovernanceService _parameterSetGovernanceService;
    private readonly IStrategyProfileGovernanceService _strategyProfileGovernanceService;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<GovernanceController> _logger;

    public GovernanceController(
        IGovernanceVersionService governanceService,
        IRuleSetVersionRepository ruleSetVersionRepo,
        IParameterSetVersionRepository parameterSetVersionRepo,
        IStrategyProfileVersionRepository strategyProfileVersionRepo,
        IRuleSetRepository ruleSetRepo,
        IParameterSetRepository parameterSetRepo,
        IStrategyProfileRepository strategyProfileRepo,
        IDomainDependencyRepository domainDependencyRepo,
        IAuditLogRepository auditLogRepo,
        IRunLifecycleService runLifecycleService,
        IScheduleRunRepository scheduleRunRepo,
        IScheduleQueryService queryService,
        IDomainDefinitionGovernanceService domainDefinitionService,
        IRuleSetGovernanceService ruleSetGovernanceService,
        IParameterSetGovernanceService parameterSetGovernanceService,
        IStrategyProfileGovernanceService strategyProfileGovernanceService,
        IDataScopeService dataScopeService,
        ILogger<GovernanceController> logger)
    {
        _governanceService = governanceService;
        _ruleSetVersionRepo = ruleSetVersionRepo;
        _parameterSetVersionRepo = parameterSetVersionRepo;
        _strategyProfileVersionRepo = strategyProfileVersionRepo;
        _ruleSetRepo = ruleSetRepo;
        _parameterSetRepo = parameterSetRepo;
        _strategyProfileRepo = strategyProfileRepo;
        _domainDependencyRepo = domainDependencyRepo;
        _auditLogRepo = auditLogRepo;
        _runLifecycleService = runLifecycleService;
        _scheduleRunRepo = scheduleRunRepo;
        _queryService = queryService;
        _domainDefinitionService = domainDefinitionService;
        _ruleSetGovernanceService = ruleSetGovernanceService;
        _parameterSetGovernanceService = parameterSetGovernanceService;
        _strategyProfileGovernanceService = strategyProfileGovernanceService;
        _dataScopeService = dataScopeService;
        _logger = logger;
    }

    /// <summary>解析当前登录用户 Id（无效时返回 0 → 范围解析为拒绝全部，安全默认）</summary>
    private int GetCurrentUserId()
        => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    /// <summary>解析当前登录用户工号（审计真实 Actor，P1-04 强制后端取身份，禁止信任请求体）</summary>
    private string GetCurrentUserCode()
        => User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    #region RuleSetVersion CRUD

    /// <summary>
    /// 新建规则集主表（POST /rule-set；2026-10-22 补齐 4号位 缺口/T4）。
    /// Q1：RuleSetCode 用户填、唯一（大小写不敏感）、创建后不可变更；Q2：两步——主表仅建壳，前端随后自动跟建空白 DRAFT 版本；
    /// Q3＝方案 A 跨域通用（无排程域字段）；Q6＝挂 aps.rule.edit（RuleMaintain）。无 DELETE 端点（Q5，一律 IsActive 停用）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost("rule-set")]
    public async Task<IActionResult> CreateRuleSet([FromBody] RuleSet entity, CancellationToken ct)
    {
        try
        {
            var created = await _ruleSetGovernanceService.CreateRuleSetAsync(entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            // 主表此时尚无任何版本，CreatedAtAction 无合法 Location（GetRuleSetVersion 需 versionId）；
            // 曾传 null → INSERT 后 MVC 生成 Location 失败抛 500（4号位 2026-10-25 F1）。改回 200 + body。
            return Ok(ApiResponse<RuleSet>.Success(created));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集新建失败：{RuleSetCode}", entity.RuleSetCode);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 编辑规则集主表（PUT /rule-set/{id}）：改 Name/Description/IsActive；RuleSetCode 冻结不可改；不符 → 400。
    /// Q4：IsActive=0 仅关主表、不级联版本（运行时装载判主表归 2号位/1号位 转达项）；Q6＝aps.rule.edit。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPut("rule-set/{id}")]
    public async Task<IActionResult> UpdateRuleSet(long id, [FromBody] RuleSet entity, CancellationToken ct)
    {
        try
        {
            var updated = await _ruleSetGovernanceService.UpdateRuleSetAsync(id, entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<RuleSet>.Success(updated));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集更新失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 新建参数集主表（POST /parameter-set；2026-10-25 补齐 4号位 缺口，照 rule-set 10-22 范式）。
    /// Q1＝编码用户填、唯一（大小写不敏感）、创建后不可变更；Q2＝两步——主表仅建壳，前端随后自动跟建空白 DRAFT 版本；
    /// Q3＝方案 A 跨域通用（无排程域字段）；Q5＝不给 DELETE；Q6＝挂 aps.parameter.edit（ParameterEdit）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPost("parameter-set")]
    public async Task<IActionResult> CreateParameterSet([FromBody] ParameterSet entity, CancellationToken ct)
    {
        try
        {
            var created = await _parameterSetGovernanceService.CreateParameterSetAsync(entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            // 主表此时尚无任何版本，CreatedAtAction 无合法 Location（GetParameterSetVersion 需 versionId）；改回 200 + body（同 rule-set）。
            return Ok(ApiResponse<ParameterSet>.Success(created));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集新建失败：{ParameterSetCode}", entity.ParameterSetCode);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 编辑参数集主表（PUT /parameter-set/{id}）：改 Name/Description/IsActive；ParameterSetCode 冻结不可改；不符 → 400。
    /// Q4：IsActive=0 仅关主表、不级联版本（运行时装载判主表归 2号位/1号位 转达项）；Q6＝aps.parameter.edit。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPut("parameter-set/{id}")]
    public async Task<IActionResult> UpdateParameterSet(long id, [FromBody] ParameterSet entity, CancellationToken ct)
    {
        try
        {
            var updated = await _parameterSetGovernanceService.UpdateParameterSetAsync(id, entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<ParameterSet>.Success(updated));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集更新失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 新建策略包主表（POST /strategy-profile；2026-10-25 补齐 4号位 缺口，照 rule-set 10-22 范式）。
    /// Q1＝编码用户填、唯一、创建后不可变更；Q2＝**只建壳，不能自动跟建版本**（StrategyProfileVersion.RuleSetVersionId/ParameterSetVersionId 均必填 FK，
    /// 无「空白首版」合法状态 ⇒ 首版在「新建草稿」里选两个已发布版本）；Q3＝跨域通用；Q5＝不给 DELETE；
    /// Q6＝aps.strategy.edit（StrategyEdit）；Q8＝RunType 仅五值或 NULL（冻结 07 §5.5）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPost("strategy-profile")]
    public async Task<IActionResult> CreateStrategyProfile([FromBody] StrategyProfile entity, CancellationToken ct)
    {
        try
        {
            var created = await _strategyProfileGovernanceService.CreateStrategyProfileAsync(entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            // 主表此时尚无任何版本，CreatedAtAction 无合法 Location；改回 200 + body（同 rule-set）。
            return Ok(ApiResponse<StrategyProfile>.Success(created));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包新建失败：{StrategyProfileCode}", entity.StrategyProfileCode);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 编辑策略包主表（PUT /strategy-profile/{id}）：改 Name/Description/RunType/IsActive；StrategyProfileCode 冻结不可改；不符 → 400。
    /// Q4：IsActive=0 仅关主表、不级联版本（运行时装载判主表归 2号位/1号位 转达项）；Q6＝aps.strategy.edit。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPut("strategy-profile/{id}")]
    public async Task<IActionResult> UpdateStrategyProfile(long id, [FromBody] StrategyProfile entity, CancellationToken ct)
    {
        try
        {
            var updated = await _strategyProfileGovernanceService.UpdateStrategyProfileAsync(id, entity, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<StrategyProfile>.Success(updated));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包更新失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 物理删除参数集整族（DELETE /parameter-set/{id}，2026-10-26 同 R1 规则集删除口径）：无任何 PUBLISHED（已发布）版本才可删
    /// （纯新建未发布族）；含 PUBLISHED（已发布）仍只可停用（历史不可变守恒）。被策略包版本引用则拒绝（FK 反查阻断）。
    /// 审计前置（ActionCode=Delete）后整族删（单事务）。权限 Q6＝aps.parameter.edit。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpDelete("parameter-set/{id}")]
    public async Task<IActionResult> DeleteParameterSet(long id, CancellationToken ct)
    {
        try
        {
            await _parameterSetGovernanceService.DeleteParameterSetAsync(id, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<bool>.Success(true));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集删除失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 物理删除策略包整族（DELETE /strategy-profile/{id}，2026-10-26 同 R1 规则集删除口径）：无任何 PUBLISHED（已发布）版本才可删
    /// （纯新建未发布族）；含 PUBLISHED（已发布）仍只可停用（历史不可变守恒）。被 ScheduleRun（运行）引用则拒绝（FK 反查阻断）。
    /// 审计前置（ActionCode=Delete）后整族删（单事务）。权限 Q6＝aps.strategy.edit。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpDelete("strategy-profile/{id}")]
    public async Task<IActionResult> DeleteStrategyProfile(long id, CancellationToken ct)
    {
        try
        {
            await _strategyProfileGovernanceService.DeleteStrategyProfileAsync(id, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<bool>.Success(true));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包删除失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 物理删除规则集整族（DELETE /rule-set/{id}，R1 2026-10-25 例外 Q5）：无任何 PUBLISHED 版本才可删（纯新建未发布族）；
    /// 有 PUBLISHED 仍只可停用（历史不可变守恒）。被策略包引用则拒绝（FK 防护）。审计前置后整族删（单事务）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpDelete("rule-set/{id}")]
    public async Task<IActionResult> DeleteRuleSet(long id, CancellationToken ct)
    {
        try
        {
            await _ruleSetGovernanceService.DeleteRuleSetAsync(id, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<bool>.Success(true));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集删除失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 物理删除单个草稿版本（DELETE /rule-set/version/{id}，R1 2026-10-25）：仅 Status=DRAFT 可删（清空白草稿壳）；
    /// 已发布/停用/归档拒绝。被策略包引用则拒绝。审计前置后删单行。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpDelete("rule-set/version/{versionId}")]
    public async Task<IActionResult> DeleteRuleSetVersion(long versionId, CancellationToken ct)
    {
        try
        {
            await _ruleSetGovernanceService.DeleteRuleSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct);
            return Ok(ApiResponse<bool>.Success(true));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本删除失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 获取规则集的所有版本（rule-set 同族 2026-10-15：摘要 DTO，镜像参数集 R2——不含 ContentSnapshotJson/DemandPriorityJson 大 JSON 载体；
    /// 2026-10-15 4号位 来文 Q2：加补传输层——改调摘要查询，防将来规则集快照变大后版本下拉同方式不可用）
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/{ruleSetId}/versions")]
    public async Task<IActionResult> GetRuleSetVersions(long ruleSetId, CancellationToken ct)
    {
        var summaries = await _ruleSetVersionRepo.GetSummariesByRuleSetIdAsync(ruleSetId, ct);
        return Ok(ApiResponse<IReadOnlyList<RuleSetVersionSummaryDto>>.Success(summaries));
    }

    /// <summary>获取规则集版本详情（P0-01：ContentSnapshotJson 投影回 DemandPriorityJson，前端 API 兼容）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/version/{versionId}")]
    public async Task<IActionResult> GetRuleSetVersion(long versionId, CancellationToken ct)
    {
        var version = await _governanceService.GetRuleSetVersionAsync(versionId, ct);
        if (version == null)
        {
            return NotFound(ApiResponse.Fail(404, $"规则集版本不存在：{versionId}"));
        }
        return Ok(ApiResponse<RuleSetVersion>.Success(version));
    }

    /// <summary>创建规则集版本（P0-02：Service 强制初始状态 DRAFT——入参 Status 一律被忽略覆盖；内容归一化到 ContentSnapshotJson 持久化）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost("rule-set/version")]
    public async Task<IActionResult> CreateRuleSetVersion([FromBody] RuleSetVersion version, CancellationToken ct)
    {
        var created = await _governanceService.CreateRuleSetVersionAsync(version, GetCurrentUserCode(), GetCurrentUserId(), ct);
        return CreatedAtAction(nameof(GetRuleSetVersion), new { versionId = created.Id }, ApiResponse<RuleSetVersion>.Success(created));
    }

    /// <summary>更新规则集版本（P0-02：状态机约束——已发布/失效/归档拒绝原地修改；Status/治理字段冻结，禁止越权改状态）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPut("rule-set/version/{versionId}")]
    public async Task<IActionResult> UpdateRuleSetVersion(long versionId, [FromBody] RuleSetVersion version, CancellationToken ct)
    {
        try
        {
            await _governanceService.UpdateRuleSetVersionAsync(versionId, version, GetCurrentUserId(), ct);
            return Ok(ApiResponse<RuleSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本更新失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region ParameterSetVersion CRUD

    /// <summary>
    /// 获取参数集的所有版本（R2 修复：摘要 DTO，不含 ContentSnapshotJson 大 JSON 载体，防 PSV 1305 序列化 500；
    /// 2026-10-15 4号位 来文：加补传输层——改调摘要查询，不再 SELECT * 整列搬 ContentSnapshotJson 大 LOB，PSV 1305 37s → 摘要列 10ms 级）
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/{parameterSetId}/versions")]
    public async Task<IActionResult> GetParameterSetVersions(long parameterSetId, CancellationToken ct)
    {
        var summaries = await _parameterSetVersionRepo.GetSummariesByParameterSetIdAsync(parameterSetId, ct);
        return Ok(ApiResponse<IReadOnlyList<ParameterSetVersionSummaryDto>>.Success(summaries));
    }

    /// <summary>获取参数集版本详情（P0-01：ContentSnapshotJson 五子块投影回五主题 JSON，前端 API 兼容）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/version/{versionId}")]
    public async Task<IActionResult> GetParameterSetVersion(long versionId, CancellationToken ct)
    {
        var version = await _governanceService.GetParameterSetVersionAsync(versionId, ct);
        if (version == null)
        {
            return NotFound(ApiResponse.Fail(404, $"参数集版本不存在：{versionId}"));
        }
        return Ok(ApiResponse<ParameterSetVersion>.Success(version));
    }

    /// <summary>创建参数集版本（P0-02：Service 强制初始状态 DRAFT——入参 Status 一律被忽略覆盖；五主题 JSON 归一化到 ContentSnapshotJson 持久化）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPost("parameter-set/version")]
    public async Task<IActionResult> CreateParameterSetVersion([FromBody] ParameterSetVersion version, CancellationToken ct)
    {
        var created = await _governanceService.CreateParameterSetVersionAsync(version, GetCurrentUserCode(), GetCurrentUserId(), ct);
        return CreatedAtAction(nameof(GetParameterSetVersion), new { versionId = created.Id }, ApiResponse<ParameterSetVersion>.Success(created));
    }

    /// <summary>更新参数集版本（P0-02：状态机约束——已发布/失效/归档拒绝原地修改；Status/治理字段冻结，禁止越权改状态）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPut("parameter-set/version/{versionId}")]
    public async Task<IActionResult> UpdateParameterSetVersion(long versionId, [FromBody] ParameterSetVersion version, CancellationToken ct)
    {
        try
        {
            await _governanceService.UpdateParameterSetVersionAsync(versionId, version, GetCurrentUserId(), ct);
            return Ok(ApiResponse<ParameterSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集版本更新失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region 发布端点（R01/R02 验收）

    /// <summary>
    /// 发布规则集版本（六态状态机 R01 验收）
    /// 红线：仅 DRAFT/SUBMITTED/APPROVED 可发布；PUBLISHED 拒绝（历史不可覆盖）；A-6 不变量自动处理。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RulePublish)]
    [HttpPost("rule-set/version/{versionId}/publish")]
    public async Task<IActionResult> PublishRuleSetVersion(long versionId, [FromBody] PublishRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.PublishRuleSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct, changeReason: request?.ChangeReason);
            _logger.LogInformation("规则集版本已发布：{VersionId}，发布人：{PublishedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"规则集版本 {versionId} 已发布"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本发布失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 发布参数集版本（六态状态机 R02 验收）
    /// 红线：仅 DRAFT/SUBMITTED/APPROVED 可发布；新 Run 可引用新版本、旧 Run 引用不变；A-6 不变量自动处理。
    /// 授权：D-1 独立权限 ParameterPublish（0号位 §3.2 参数发布 aps.parameter.publish，不因规则发布自动获得）
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterPublish)]
    [HttpPost("parameter-set/version/{versionId}/publish")]
    public async Task<IActionResult> PublishParameterSetVersion(long versionId, [FromBody] PublishRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.PublishParameterSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct, changeReason: request?.ChangeReason);
            _logger.LogInformation("参数集版本已发布：{VersionId}，发布人：{PublishedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"参数集版本 {versionId} 已发布"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集版本发布失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region 版本停用（G2：Retired↔DISABLED，3-4联调）

    /// <summary>
    /// 停用规则集版本（SUBMITTED/APPROVED/PUBLISHED → DISABLED）
    /// 红线：DRAFT 拒绝（草稿直接编辑/删除）；DISABLED/ARCHIVED 幂等保护拒绝。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost("rule-set/version/{versionId}/disable")]
    public async Task<IActionResult> DisableRuleSetVersion(long versionId, [FromBody] DisableRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.DisableRuleSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), request?.Reason, ct);
            _logger.LogInformation("规则集版本已停用：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"规则集版本 {versionId} 已停用"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本停用失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 停用参数集版本（SUBMITTED/APPROVED/PUBLISHED → DISABLED）
    /// 红线：DRAFT 拒绝（草稿直接编辑/删除）；DISABLED/ARCHIVED 幂等保护拒绝。
    /// 授权：C-5/D-1 参数停用改挂 ParameterEdit（0号位 §3.2 参数停用操作授权 aps.parameter.edit）
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPost("parameter-set/version/{versionId}/disable")]
    public async Task<IActionResult> DisableParameterSetVersion(long versionId, [FromBody] DisableRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.DisableParameterSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), request?.Reason, ct);
            _logger.LogInformation("参数集版本已停用：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"参数集版本 {versionId} 已停用"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集版本停用失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 停用策略包版本（SUBMITTED/APPROVED/PUBLISHED → DISABLED）
    /// 红线：DRAFT 拒绝；DISABLED/ARCHIVED 幂等保护拒绝。
    /// IsDefault=1 停用时自动清默认标志（避免 DISABLED 默认残留于 ResolveDefault 范围）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPost("strategy-profile/version/{versionId}/disable")]
    public async Task<IActionResult> DisableStrategyProfileVersion(long versionId, [FromBody] DisableRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.DisableStrategyProfileVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), request?.Reason, ct);
            _logger.LogInformation("策略包版本已停用：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"策略包版本 {versionId} 已停用"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包版本停用失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region 默认版本设置与解析（D-3，Q-C：同族原子切换 + 读侧歧义不随机取）

    /// <summary>
    /// 设置规则集版本为同族默认（D-3，2026-10 受控开发，契约 06 §D组 D-1）。
    /// 默认标记 IsDefault 属治理元数据，仅 PUBLISHED 版本可置位；切换经仓储 SetDefaultAsync 单事务原子
    /// 「清同族其它 + 置目标」，避免 UQ_RuleSetVersion_DefaultPublished 过滤唯一索引冲突；写审计。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost("rule-set/version/{versionId}/set-default")]
    public async Task<IActionResult> SetDefaultRuleSetVersion(long versionId, CancellationToken ct)
    {
        try
        {
            await _governanceService.SetDefaultRuleSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct);
            _logger.LogInformation("规则集版本设为默认：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"规则集版本 {versionId} 已设为默认"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本设为默认失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 设置参数集版本为同族默认（D-3，2026-10 受控开发，契约 06 §D组 D-2）。
    /// 同规则集语义；切换经 IParameterSetVersionRepository.SetDefaultAsync 单事务原子化（D-3 修正 SPV
    /// ClearDefaultFlag + Update 两条独立 SQL 非原子缺陷），避免 UQ_ParameterSetVersion_DefaultPublished 冲突。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterEdit)]
    [HttpPost("parameter-set/version/{versionId}/set-default")]
    public async Task<IActionResult> SetDefaultParameterSetVersion(long versionId, CancellationToken ct)
    {
        try
        {
            await _governanceService.SetDefaultParameterSetVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct);
            _logger.LogInformation("参数集版本设为默认：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"参数集版本 {versionId} 已设为默认"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集版本设为默认失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 设置策略包版本为同族默认（C-7 §9.2 显式治理操作，D-3）。
    /// 仅 PUBLISHED 版本可置位；清同 Profile 其它默认再置位，避免 UQ_StrategyProfileVersion_DefaultPublished 冲突；写审计。
    /// 服务层内部对已是默认则幂等早退（不重复清默认+置位与审计）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPost("strategy-profile/version/{versionId}/set-default")]
    public async Task<IActionResult> SetDefaultStrategyProfileVersion(long versionId, CancellationToken ct)
    {
        try
        {
            await _governanceService.SetDefaultStrategyProfileVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct);
            _logger.LogInformation("策略包版本设为默认：{VersionId}，操作人：{OperatedBy}", versionId, GetCurrentUserCode());
            return Ok(ApiResponse.Ok($"策略包版本 {versionId} 已设为默认"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包版本设为默认失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 读侧默认解析：规则集同族当前有效、无歧义的 PUBLISHED 默认版本（D-3，Q-C，契约 06 §D组 D-4）。
    /// 无 RunType（RS 族解析）；镜象 SPV 三态判定：候选按生效窗过滤后 0→404、1→返回、&gt;1→400（红线 #4 禁止随机取，不以最新发布代替）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/{ruleSetId}/default")]
    public async Task<IActionResult> ResolveDefaultRuleSet([FromRoute] long ruleSetId, [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.ResolveDefaultRuleSetVersionAsync(ruleSetId, asOf, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"RuleSetId={ruleSetId} 无当前有效默认 PUBLISHED 规则集版本"));
            }
            return Ok(ApiResponse<RuleSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>
    /// 读侧默认解析：参数集同族当前有效、无歧义的 PUBLISHED 默认版本（D-3，Q-C，契约 06 §D组 D-5）。
    /// 无 RunType（PS 族解析）；三态判定同规则集：0→404、1→返回、&gt;1→400（不随机取、不以最新发布代替）。
    /// </summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/{parameterSetId}/default")]
    public async Task<IActionResult> ResolveDefaultParameterSet([FromRoute] long parameterSetId, [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.ResolveDefaultParameterSetVersionAsync(parameterSetId, asOf, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"ParameterSetId={parameterSetId} 无当前有效默认 PUBLISHED 参数集版本"));
            }
            return Ok(ApiResponse<ParameterSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region 版本差异对比与溯源（A-8）

    /// <summary>对比两个规则集版本的差异（阶段 A-8：版本溯源）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/version/diff")]
    public async Task<IActionResult> CompareRuleSetVersions([FromQuery] long sourceVersionId, [FromQuery] long targetVersionId, CancellationToken ct)
    {
        try
        {
            var result = await _governanceService.CompareRuleSetVersionsAsync(sourceVersionId, targetVersionId, ct);
            return Ok(ApiResponse<VersionDiffResult>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "规则集版本对比失败：{SourceVersionId} vs {TargetVersionId}", sourceVersionId, targetVersionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>对比两个参数集版本的差异（阶段 A-8：版本溯源）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/version/diff")]
    public async Task<IActionResult> CompareParameterSetVersions([FromQuery] long sourceVersionId, [FromQuery] long targetVersionId, CancellationToken ct)
    {
        try
        {
            var result = await _governanceService.CompareParameterSetVersionsAsync(sourceVersionId, targetVersionId, ct);
            return Ok(ApiResponse<VersionDiffResult>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "参数集版本对比失败：{SourceVersionId} vs {TargetVersionId}", sourceVersionId, targetVersionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>对比两个策略包版本的差异（G5/D3：策略包对比页；裸 DTO，同 D1/D2）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/version/diff")]
    public async Task<IActionResult> CompareStrategyProfileVersions([FromQuery] long sourceVersionId, [FromQuery] long targetVersionId, CancellationToken ct)
    {
        try
        {
            var result = await _governanceService.CompareStrategyProfileVersionsAsync(sourceVersionId, targetVersionId, ct);
            return Ok(ApiResponse<VersionDiffResult>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包版本对比失败：{SourceVersionId} vs {TargetVersionId}", sourceVersionId, targetVersionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion

    #region 发布前校验（A-5）

    /// <summary>校验规则集版本是否可发布（阶段 A-5：发布前完整校验）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/version/{versionId}/validate")]
    public async Task<IActionResult> ValidateRuleSetVersionForPublish(long versionId, CancellationToken ct)
    {
        var result = await _governanceService.ValidateRuleSetVersionForPublishAsync(versionId, ct);
        return Ok(ApiResponse<PublishValidationResult>.Success(result));
    }

    /// <summary>校验参数集版本是否可发布（阶段 A-5：发布前完整校验）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/version/{versionId}/validate")]
    public async Task<IActionResult> ValidateParameterSetVersionForPublish(long versionId, CancellationToken ct)
    {
        var result = await _governanceService.ValidateParameterSetVersionForPublishAsync(versionId, ct);
        return Ok(ApiResponse<PublishValidationResult>.Success(result));
    }

    #endregion

    #region StrategyProfileVersion（P0-06：策略包版本治理完整闭环）

    /// <summary>获取策略包的所有版本</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/{strategyProfileId}/versions")]
    public async Task<IActionResult> GetStrategyProfileVersions(long strategyProfileId, CancellationToken ct)
    {
        // 4号位 2026-10-14 审计 R2/R3：列表投影带版本编码与父级主表 Id（前端直显编码 + 编辑定位父级，消除 N+1）
        var versions = await _strategyProfileVersionRepo.GetByStrategyProfileIdWithCodesAsync(strategyProfileId, ct);
        return Ok(ApiResponse<IReadOnlyList<StrategyProfileVersionListItem>>.Success(versions));
    }

    /// <summary>获取策略包版本详情</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/version/{versionId}")]
    public async Task<IActionResult> GetStrategyProfileVersion(long versionId, CancellationToken ct)
    {
        var version = await _strategyProfileVersionRepo.GetByIdAsync(versionId, ct);
        if (version == null)
        {
            return NotFound(ApiResponse.Fail(404, $"策略包版本不存在：{versionId}"));
        }
        return Ok(ApiResponse<StrategyProfileVersion>.Success(version));
    }

    /// <summary>创建策略包版本（P0-02：Service 强制初始状态 DRAFT——入参 Status 一律被忽略覆盖）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPost("strategy-profile/version")]
    public async Task<IActionResult> CreateStrategyProfileVersion([FromBody] StrategyProfileVersion version, CancellationToken ct)
    {
        var created = await _governanceService.CreateStrategyProfileVersionAsync(version, GetCurrentUserCode(), GetCurrentUserId(), ct);
        return CreatedAtAction(nameof(GetStrategyProfileVersion), new { versionId = created.Id }, ApiResponse<StrategyProfileVersion>.Success(created));
    }

    /// <summary>更新策略包版本（P0-02：状态机约束——已发布/失效/归档拒绝原地修改；Status/治理字段/引用字段冻结，禁止越权改状态）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyEdit)]
    [HttpPut("strategy-profile/version/{versionId}")]
    public async Task<IActionResult> UpdateStrategyProfileVersion(long versionId, [FromBody] StrategyProfileVersion version, CancellationToken ct)
    {
        try
        {
            await _governanceService.UpdateStrategyProfileVersionAsync(versionId, version, GetCurrentUserId(), ct);
            return Ok(ApiResponse<StrategyProfileVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "策略包版本更新失败：{VersionId}", versionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>发布策略包版本（P0-06：DRAFT/SUBMITTED/APPROVED → PUBLISHED；发布前强制校验）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyPublish)]
    [HttpPost("strategy-profile/version/{versionId}/publish")]
    public async Task<IActionResult> PublishStrategyProfileVersion(long versionId, [FromBody] PublishRequest request, CancellationToken ct)
    {
        try
        {
            await _governanceService.PublishStrategyProfileVersionAsync(versionId, GetCurrentUserCode(), GetCurrentUserId(), ct, changeReason: request?.ChangeReason);
            return Ok(ApiResponse.Ok($"策略包版本 {versionId} 发布成功"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>校验策略包版本是否可发布（P0-06）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/version/{versionId}/validate")]
    public async Task<IActionResult> ValidateStrategyProfileVersionForPublish(long versionId, CancellationToken ct)
    {
        var result = await _governanceService.ValidateStrategyProfileVersionForPublishAsync(versionId, ct);
        return Ok(ApiResponse<PublishValidationResult>.Success(result));
    }

    /// <summary>解析当前有效默认 PUBLISHED 策略包（P0-06：跨号位冻结语义；歧义报错不随机取）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/default")]
    public async Task<IActionResult> ResolveDefaultStrategyProfile([FromQuery] string? runType, [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.ResolveDefaultStrategyProfileVersionAsync(runType, asOf, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"RunType={runType} 无当前有效默认 PUBLISHED 策略包"));
            }
            return Ok(ApiResponse<StrategyProfileVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>Run 引用追溯（P0-06：策略包版本 → 父 Profile + 规则集/参数集版本）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/version/{versionId}/trace")]
    public async Task<IActionResult> GetRunStrategyProfileTrace(long versionId, CancellationToken ct)
    {
        try
        {
            var trace = await _governanceService.GetRunStrategyProfileTraceAsync(versionId, ct);
            return Ok(ApiResponse<RunStrategyProfileTrace>.Success(trace));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse.Fail(404, ex.Message));
        }
    }

    #endregion

    #region 运行生命周期（P0-08：ScheduleRun 治理）

    /// <summary>校验 ScheduleRun.ExpectedDomainKeysJson 冻结规则（P0-08：FULL≥1 / RESCHEDULE 恰1）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleMaintain)]
    [HttpPost("run/{scheduleRunId}/validate-domain-keys")]
    public async Task<IActionResult> ValidateExpectedDomainKeys(int scheduleRunId, CancellationToken ct)
    {
        try
        {
            await _runLifecycleService.ValidateExpectedDomainKeysAsync(scheduleRunId, GetCurrentUserId(), ct);
            return Ok(ApiResponse.Ok($"ScheduleRun {scheduleRunId} 的 ExpectedDomainKeysJson 冻结规则校验通过"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ExpectedDomainKeysJson 冻结规则校验失败：{ScheduleRunId}", scheduleRunId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>Candidate 最小人工确认（P0-08：仅记录 Actor/ConfirmedAt/CandidatePlanVersionId/Remark，不转 ACTIVE）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.CandidateConfirm)]
    [HttpPost("plan-version/{planVersionId}/confirm-candidate")]
    public async Task<IActionResult> ConfirmCandidate(int planVersionId, [FromBody] ConfirmCandidateRequest request, CancellationToken ct)
    {
        try
        {
            await _runLifecycleService.ConfirmCandidateAsync(planVersionId, GetCurrentUserId(), GetCurrentUserCode(), request?.Remark, ct);
            return Ok(ApiResponse.Ok($"候选版本 {planVersionId} 已确认（待激活）"));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "候选版本业务范围越界：{PlanVersionId}", planVersionId);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "候选版本确认失败：{PlanVersionId}", planVersionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>激活 Candidate（P0-08：CANDIDATE → ACTIVE，每域单一正式采用版本）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.CandidateActivate)]
    [HttpPost("plan-version/{planVersionId}/activate-candidate")]
    public async Task<IActionResult> ActivateCandidate(int planVersionId, [FromBody] ActivateCandidateRequest request, CancellationToken ct)
    {
        try
        {
            await _runLifecycleService.ActivateCandidateAsync(planVersionId, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse.Ok($"候选版本 {planVersionId} 已正式采用（ACTIVE）"));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "候选版本业务范围越界：{PlanVersionId}", planVersionId);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "候选版本激活失败：{PlanVersionId}", planVersionId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>候选 vs 基础 计划版本对比复合查询（H1；U10 候选对比页；v1.2 §三）</summary>
    /// <remarks>开发者：3号位；estimatedOnlyCount 归 2号位 边界返 null；crossDomain 计数未落盘保守返 0（v1.2 §九-b）；reasons[] 不在 v1.2 返回</remarks>
    [Authorize(Policy = PermissionCodes.CandidateView)]
    [HttpGet("plan-version/{candidatePlanVersionId}/compare-with/{basePlanVersionId}")]
    public async Task<IActionResult> CompareCandidateWithBase(
        int candidatePlanVersionId,
        int basePlanVersionId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _queryService.GetCandidateComparisonAsync(GetCurrentUserId(), candidatePlanVersionId, basePlanVersionId, ct);
            return Ok(ApiResponse<CandidateComparisonDto>.Success(result));
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "候选对比失败：候选/基础版本不存在");
            return NotFound(ApiResponse<CandidateComparisonDto>.Fail(404, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "候选对比失败：候选版本状态校验未过");
            return BadRequest(ApiResponse<CandidateComparisonDto>.Fail(400, ex.Message));
        }
    }

    /// <summary>FAILED 恢复（P0-08：为 FAILED ScheduleRun 新建一条 RUNNING 重跑，继承基线；旧记录不动）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanRun)]
    [HttpPost("run/{failedScheduleRunId}/recover")]
    public async Task<IActionResult> RecoverFailedRun(int failedScheduleRunId, CancellationToken ct)
    {
        try
        {
            var newRunId = await _runLifecycleService.RecoverFailedRunAsync(failedScheduleRunId, GetCurrentUserId(), ct);
            return Ok(ApiResponse<object>.Success(new { NewScheduleRunId = newRunId }, $"FAILED 运行 {failedScheduleRunId} 已恢复，新建运行 {newRunId}"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "FAILED 运行恢复失败：{ScheduleRunId}", failedScheduleRunId);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>白天候选运行创建（B-1：0号位 2026-08-29 裁决3——4号位 → 3号位 入口；冻结 RunType×Purpose×策略版本，交 2号位 主流程执行收口）</summary>
    /// <remarks>开发者：3号位；触发 2号位 主流程留契约接缝（契约草案 §四 方案 A/B/C，待 2号位/0号位 裁定）</remarks>
    [Authorize(Policy = PermissionCodes.PlanRun)]
    [HttpPost("run/candidate")]
    public async Task<IActionResult> CreateCandidateRun([FromBody] CreateCandidateRunRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _runLifecycleService.CreateCandidateRunAsync(new CandidateRunCreateSpec
            {
                RunType = request?.RunType ?? string.Empty,
                Purpose = request?.Purpose ?? string.Empty,
                DomainKey = request?.DomainKey ?? string.Empty,
                BasePlanVersionId = request?.BasePlanVersionId,
                DataCutoffTime = request?.DataCutoffTime,
                Scope = request?.Scope,
                Actor = GetCurrentUserCode(),
            }, GetCurrentUserId(), ct);
            return Ok(ApiResponse<CandidateRunCreatedResult>.Success(result, $"白天候选运行创建成功：RunId={result.NewScheduleRunId}, CandidatePlanVersionId={result.NewPlanVersionId}"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "白天候选运行创建失败：RunType={RunType}, Domain={Domain}", request?.RunType, request?.DomainKey);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>Run 引用追溯（P0-08：ScheduleRun → 策略包版本 → 规则集/参数集版本 + 关联 PlanVersion 状态）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("run/{scheduleRunId}/trace")]
    public async Task<IActionResult> GetRunReferenceTrace(int scheduleRunId, CancellationToken ct)
    {
        try
        {
            var trace = await _runLifecycleService.GetRunReferenceTraceAsync(scheduleRunId, ct);
            return Ok(ApiResponse<RunReferenceTrace>.Success(trace));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse.Fail(404, ex.Message));
        }
    }

    #endregion

    #region 主表列表与运行支撑（G1/G7：3-4联调）

    /// <summary>规则集主表列表（G1/A1：4号位规则集列表页；R2 起返回含最新版本摘要的列表 DTO）</summary>
    /// <remarks>开发者：3号位。activeOnly 三态语义（契约 v1.0 2026-10-10）：true=仅查 IsActive=1；false=仅查 IsActive=0（≠查全部）；缺省=不过滤全量。dev DB 当前无停用数据，传 false 恒返 0（客观状态非 bug）。</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-sets")]
    public async Task<IActionResult> GetRuleSets(
        [FromQuery] bool? activeOnly = null,
        [FromQuery] string? keyword = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var (skip, take) = ResolvePaging(page, pageSize);
        var result = await _ruleSetRepo.GetListWithVersionAsync(activeOnly, keyword, skip, take, ct);
        return Ok(ApiResponse<IReadOnlyList<RuleSetListItemDto>>.Success(result));
    }

    /// <summary>参数集主表列表（G1/A8：4号位参数集列表页）</summary>
    /// <remarks>开发者：3号位。activeOnly 三态语义（契约 v1.0 2026-10-10）：true=仅查 IsActive=1；false=仅查 IsActive=0（≠查全部）；缺省=不过滤全量。dev DB 当前无停用数据，传 false 恒返 0（客观状态非 bug）。</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-sets")]
    public async Task<IActionResult> GetParameterSets(
        [FromQuery] bool? activeOnly = null,
        [FromQuery] string? keyword = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var (skip, take) = ResolvePaging(page, pageSize);
        var result = await _parameterSetRepo.GetListWithVersionAsync(activeOnly, keyword, skip, take, ct);
        return Ok(ApiResponse<IReadOnlyList<ParameterSetListItemDto>>.Success(result));
    }

    /// <summary>策略包主表列表（G1/A12：4号位策略包列表页）</summary>
    /// <remarks>开发者：3号位。activeOnly 三态语义（契约 v1.0 2026-10-10）：true=仅查 IsActive=1；false=仅查 IsActive=0（≠查全部）；缺省=不过滤全量。dev DB 当前无停用数据，传 false 恒返 0（客观状态非 bug）。</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profiles")]
    public async Task<IActionResult> GetStrategyProfiles(
        [FromQuery] bool? activeOnly = null,
        [FromQuery] string? keyword = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var (skip, take) = ResolvePaging(page, pageSize);
        var result = await _strategyProfileRepo.GetListAsync(activeOnly, keyword, skip, take, ct);
        return Ok(ApiResponse<IReadOnlyList<StrategyProfile>>.Success(result));
    }

    /// <summary>规则集当前生效 PUBLISHED 版本直达（G3/C2：当前 Published 版本展示；生效窗口过滤；多候选报错）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.RuleView)]
    [HttpGet("rule-set/{ruleSetId}/published-version")]
    public async Task<IActionResult> GetPublishedRuleSetVersion(long ruleSetId, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.GetPublishedRuleSetVersionAsync(ruleSetId, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"规则集 {ruleSetId} 无当前生效 PUBLISHED 版本"));
            }
            return Ok(ApiResponse<RuleSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>参数集当前生效 PUBLISHED 版本直达（G3/C2；R2 修复：快照载体经投影置空不外泄，五主题子块保留）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.ParameterView)]
    [HttpGet("parameter-set/{parameterSetId}/published-version")]
    public async Task<IActionResult> GetPublishedParameterSetVersion(long parameterSetId, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.GetPublishedParameterSetVersionAsync(parameterSetId, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"参数集 {parameterSetId} 无当前生效 PUBLISHED 版本"));
            }
            return Ok(ApiResponse<ParameterSetVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>策略包当前生效 PUBLISHED 版本直达（G3/C2）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.StrategyView)]
    [HttpGet("strategy-profile/{strategyProfileId}/published-version")]
    public async Task<IActionResult> GetPublishedStrategyProfileVersion(long strategyProfileId, CancellationToken ct)
    {
        try
        {
            var version = await _governanceService.GetPublishedStrategyProfileVersionAsync(strategyProfileId, ct);
            if (version == null)
            {
                return NotFound(ApiResponse.Fail(404, $"策略包 {strategyProfileId} 无当前生效 PUBLISHED 版本"));
            }
            return Ok(ApiResponse<StrategyProfileVersion>.Success(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>域依赖关系查询（G7：4号位失败链 / 域依赖展示；数据源为 2号位 sp_ScanDomainDependency 扫描结果，只读）
    /// 2026-10-22 4号位 缺口修复：补数据范围收窄（第二道防线）——ResolveScope → 入参 403 → allowedDomains 下推仓储收窄。</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("domain-dependencies")]
    public async Task<IActionResult> GetDomainDependencies(
        [FromQuery] string domainCode,
        [FromQuery] string direction = "downstream",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domainCode))
        {
            return BadRequest(ApiResponse.Fail(400, "domainCode 必填"));
        }

        var scope = await _dataScopeService.ResolveScopeAsync(GetCurrentUserId(), ct);

        // 入参必须落在授权范围（Global 全放行，无范围全拒绝）；越界 → 403（对照 A，RBAC 之外第二道防线）
        var trimmedDomainCode = domainCode.Trim();
        if (!scope.Allows(DataScopeTypes.Domain, trimmedDomainCode))
        {
            return StatusCode(403, ApiResponse.Fail(403, "Domain 范围越界"));
        }

        var allowedDomains = scope.GetValues(DataScopeTypes.Domain);
        var result = await _domainDependencyRepo.GetByDomainAsync(trimmedDomainCode, direction, ct, allowedDomains);
        return Ok(ApiResponse<IReadOnlyList<DomainDependency>>.Success(result));
    }

    /// <summary>治理审计日志查询（G6：实体类型/实体ID/时间范围 可空组合，按操作时间倒序）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.AuditView)]
    [HttpGet("audit-logs")]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] string? entityType = null,
        [FromQuery] string? entityId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? take = 200,
        CancellationToken ct = default)
    {
        var result = await _auditLogRepo.QueryAsync(entityType, entityId, from, to, take, ct);
        return Ok(ApiResponse<IReadOnlyList<AuditLog>>.Success(result));
    }

    /// <summary>Run 域级状态汇总（G8：FULL 失败链 成功/失败/被阻断 区分；3号位文档 §十六）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("run/{scheduleRunId}/domain-status")]
    public async Task<IActionResult> GetRunDomainStatus(int scheduleRunId, CancellationToken ct)
    {
        try
        {
            var result = await _runLifecycleService.GetRunDomainStatusAsync(scheduleRunId, ct);
            return Ok(ApiResponse<IReadOnlyList<RunDomainStatusDto>>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Run 域级状态汇总失败：{ScheduleRunId}", scheduleRunId);
            return NotFound(ApiResponse.Fail(404, ex.Message));
        }
    }

    /// <summary>Run 历史列表查询（G4：0号位 2026-08-29 裁决——3号位 只读 ScheduleRun 运行事实封装查询；状态回填由 2号位 运行收口负责，本端点不修改运行结果语义）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns(
        [FromQuery] int? take = null,
        [FromQuery] string? status = null,
        [FromQuery] string? runType = null,
        CancellationToken ct = default)
    {
        var result = await _scheduleRunRepo.GetListAsync(take, status, runType, ct);
        return Ok(ApiResponse<IReadOnlyList<ScheduleRunGov>>.Success(result));
    }

    /// <summary>解析 1 基页码到 skip/take（page=1 → skip=0；pageSize 上限 500 防全表拉取）</summary>
    private static (int? Skip, int? Take) ResolvePaging(int? page, int? pageSize)
    {
        if (!page.HasValue && !pageSize.HasValue)
        {
            return (null, null);
        }

        var pageValue = Math.Max(page ?? 1, 1);
        var sizeValue = Math.Clamp(pageSize ?? 20, 1, 500);
        return ((pageValue - 1) * sizeValue, sizeValue);
    }

    #endregion

    #region DomainDefinition 治理（E-1：3号位 CRUD + 启用/停用 + 当前有效集合查询）

    /// <summary>域定义列表（含停用；G-D01）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("domain-definition")]
    public async Task<IActionResult> GetDomainDefinitions(CancellationToken ct)
    {
        // N-03（0号位 复审 v1.1）：用户态列表读——按操作人 Domain Scope 过滤（Global 全量 / 非 Global 仅授权域 / 无授权空列表）。
        var result = await _domainDefinitionService.GetAllAsync(GetCurrentUserId(), ct);
        return Ok(ApiResponse<IReadOnlyList<DomainDefinition>>.Success(result));
    }

    /// <summary>当前有效域集合（G-D09/G-D10：2号位归域执行唯一事实源）</summary>
    /// <remarks>开发者：3号位｜N-03：信任调用（2号位 归域执行唯一事实源），不做用户 Domain Scope 过滤——与用户态列表/详情严格分离。</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("domain-definition/active")]
    public async Task<IActionResult> GetActiveDomainDefinitions(CancellationToken ct)
    {
        var result = await _domainDefinitionService.GetActiveAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<DomainDefinition>>.Success(result));
    }

    /// <summary>域定义详情（G-D01）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("domain-definition/{id}")]
    public async Task<IActionResult> GetDomainDefinition(int id, CancellationToken ct)
    {
        try
        {
            // N-03（0号位 复审 v1.1）：用户态详情读——非 Global 用户目标域不在授权范围 → 403（防通过 Id 窥探他域定义）。
            var entity = await _domainDefinitionService.GetByIdAsync(id, GetCurrentUserId(), ct);
            if (entity == null)
            {
                return NotFound(ApiResponse.Fail(404, $"域定义不存在：{id}"));
            }
            return Ok(ApiResponse<DomainDefinition>.Success(entity));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "域定义详情业务范围越权：{Id}", id);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
    }

    /// <summary>产品族下拉列表（Domain 维护页数据源；方案 A：前端 productFamilyId 数字选择器）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("product-families")]
    public async Task<IActionResult> GetProductFamilies(CancellationToken ct)
    {
        var result = await _domainDefinitionService.GetProductFamiliesAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<ProductFamily>>.Success(result));
    }

    /// <summary>工厂下拉列表（Domain 维护页数据源；方案 A：前端 factoryId 数字选择器）</summary>
    /// <remarks>开发者：3号位</remarks>
    [Authorize(Policy = PermissionCodes.PlanView)]
    [HttpGet("factories")]
    public async Task<IActionResult> GetFactories(CancellationToken ct)
    {
        var result = await _domainDefinitionService.GetFactoriesAsync(ct);
        return Ok(ApiResponse<IReadOnlyList<Factory>>.Success(result));
    }

    /// <summary>新建域定义（G-D02：唯一性 / ScopeType / 引用合法性校验 + 审计；新建默认启用）</summary>
    /// <remarks>开发者：3号位｜2026-10-25 权限修正：RuleMaintain→DomainMaintain（冻结 §4.1 系统管理员维护，勿再误挂规则码）</remarks>
    [Authorize(Policy = PermissionCodes.DomainMaintain)]
    [HttpPost("domain-definition")]
    public async Task<IActionResult> CreateDomainDefinition([FromBody] DomainDefinition entity, CancellationToken ct)
    {
        try
        {
            var created = await _domainDefinitionService.CreateAsync(entity, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return CreatedAtAction(nameof(GetDomainDefinition), new { id = created.Id }, ApiResponse<DomainDefinition>.Success(created));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "域定义业务范围越权：{DomainKey}", entity.DomainKey);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "域定义新建失败：{DomainKey}", entity.DomainKey);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>编辑域定义（G-D03~G-D05：DomainKey 不可变更 + 校验）</summary>
    /// <remarks>开发者：3号位｜2026-10-25 权限修正：RuleMaintain→DomainMaintain（冻结 §4.1 系统管理员维护）</remarks>
    [Authorize(Policy = PermissionCodes.DomainMaintain)]
    [HttpPut("domain-definition/{id}")]
    public async Task<IActionResult> UpdateDomainDefinition(int id, [FromBody] DomainDefinition entity, CancellationToken ct)
    {
        try
        {
            var updated = await _domainDefinitionService.UpdateAsync(id, entity, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse<DomainDefinition>.Success(updated));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "域定义业务范围越权：{Id}", id);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "域定义更新失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>启用域定义（G-D16 反向）</summary>
    /// <remarks>开发者：3号位｜2026-10-25 权限修正：RuleMaintain→DomainMaintain（冻结 §4.1 系统管理员维护）</remarks>
    [Authorize(Policy = PermissionCodes.DomainMaintain)]
    [HttpPost("domain-definition/{id}/enable")]
    public async Task<IActionResult> EnableDomainDefinition(int id, [FromQuery] string? operatedBy, CancellationToken ct)
    {
        try
        {
            var updated = await _domainDefinitionService.SetActiveAsync(id, true, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse<DomainDefinition>.Success(updated));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "域定义业务范围越权：{Id}", id);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "域定义启用失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    /// <summary>停用域定义</summary>
    /// <remarks>开发者：3号位｜2026-10-25 权限修正：RuleMaintain→DomainMaintain（冻结 §4.1 系统管理员维护）</remarks>
    [Authorize(Policy = PermissionCodes.DomainMaintain)]
    [HttpPost("domain-definition/{id}/disable")]
    public async Task<IActionResult> DisableDomainDefinition(int id, [FromQuery] string? operatedBy, CancellationToken ct)
    {
        try
        {
            var updated = await _domainDefinitionService.SetActiveAsync(id, false, GetCurrentUserId(), GetCurrentUserCode(), ct);
            return Ok(ApiResponse<DomainDefinition>.Success(updated));
        }
        catch (ScopeViolationException ex)
        {
            _logger.LogWarning(ex, "域定义业务范围越权：{Id}", id);
            return StatusCode(403, ApiResponse.Fail(403, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "域定义停用失败：{Id}", id);
            return BadRequest(ApiResponse.Fail(400, ex.Message));
        }
    }

    #endregion
}

/// <summary>发布请求 DTO（3号位 Web 层契约）</summary>
/// <remarks>开发者：3号位</remarks>
public class PublishRequest
{
    /// <summary>变更原因（G10：发布时写入版本 Remarks，审计可追溯）</summary>
    public string? ChangeReason { get; set; }
}

/// <summary>版本停用请求 DTO（G2：3-4联调）</summary>
/// <remarks>开发者：3号位</remarks>
public class DisableRequest
{
    /// <summary>停用原因（写入版本 Remarks + 审计 Remarks，可空）</summary>
    public string? Reason { get; set; }
}

/// <summary>Candidate 确认请求 DTO（P0-08）</summary>
/// <remarks>开发者：3号位</remarks>
public class ConfirmCandidateRequest
{
    /// <summary>必要备注（可空）</summary>
    public string? Remark { get; set; }
}

/// <summary>Candidate 激活请求 DTO（P0-08）</summary>
/// <remarks>开发者：3号位</remarks>
public class ActivateCandidateRequest
{
}

/// <summary>白天候选运行创建请求 DTO（B-1）</summary>
/// <remarks>开发者：3号位</remarks>
public class CreateCandidateRunRequest
{
    /// <summary>运行类型（白天候选类：MANUAL_RESCHEDULE / LOCAL_RESCHEDULE / INSERT_ORDER_WHATIF）</summary>
    public string? RunType { get; set; }

    /// <summary>用途（须 ∈ RunType 冻结合法组合：CTP / INSERT_IMPACT_ANALYSIS / INSERT_RESCHEDULE / MANUAL_ADJUSTMENT）</summary>
    public string? Purpose { get; set; }

    /// <summary>目标 Domain（严格单 Domain）</summary>
    public string? DomainKey { get; set; }

    /// <summary>所基于的当前 ACTIVE 计划版本（可选；缺省按 DomainKey 解析当前 ACTIVE）</summary>
    public int? BasePlanVersionId { get; set; }

    /// <summary>本次运行统一数据切片边界（可选；缺省 now）</summary>
    public DateTime? DataCutoffTime { get; set; }

    /// <summary>备注（契约位保留，B-1 本轮不参与业务逻辑；R1 P2 结论：前端停传、后端维持丢弃）</summary>
    public string? Remark { get; set; }

    /// <summary>局部重排范围载荷（ScopeJsonV2；承载 ScheduleRun.ScopeJson）。缺省 null = 不承载结构化范围（旧调用兼容）</summary>
    public ScopeJsonV2? Scope { get; set; }
}