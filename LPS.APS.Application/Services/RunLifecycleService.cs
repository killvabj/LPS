using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.DTOs.Scope;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Application.Services.Query;
using Microsoft.Extensions.Logging;
using AuditLog = LPS.APS.Core.Entities.Auth.AuditLog;
using PlanVersion = LPS.APS.Core.Entities.APS.PlanVersion;

namespace LPS.APS.Application.Services;

/// <summary>
/// ScheduleRun 运行生命周期治理服务（3号位，P0-08）
/// 边界：仅 3号位生命周期治理（ExpectedDomainKeysJson 冻结规则 / Candidate 最小确认与激活 / FAILED 恢复新建 / Run 引用追溯）；
///       不重写 2号位已冻结的运行状态执行逻辑（SchedulingOrchestrator / ScheduleRunService / DomainSchedulingJob 不动）。
/// DDL 依据：冻结 DDL v5.1.2（ScheduleRun §3.1 / PlanVersion §3.2 / UQ_PlanVersion_OneActivePerDomain）。
/// 语义：配置/状态错误一律抛 InvalidOperationException，不静默降级；旧 FAILED 记录绝不动（不回改 RUNNING）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class RunLifecycleService : IRunLifecycleService
{
    /// <summary>全量排程 RunType（Domain 数 ≥ 1）</summary>
    private const string FullScheduleRunType = "FULL_SCHEDULE";
    /// <summary>运行状态：FAILED</summary>
    private const string ScheduleRunFailedStatus = "FAILED";
    /// <summary>运行状态：RUNNING</summary>
    private const string ScheduleRunRunningStatus = "RUNNING";
    // ══ PlanVersion 两个字段的取值常量（**2026-10-15 按冻结文档对齐词表**，详见台账 §T-1037）══
    //   VersionCategory = **身份**（出生即定、终身不变）：DAILY_BASELINE / CANDIDATE / RECOVERY …
    //   Status          = **进度/生命周期**（冻结 07 §4.2:3532）：BUILDING / CANDIDATE / ACTIVE / ARCHIVED / FAILED
    //   ⚠️ 两者各有一个 "CANDIDATE"，**同值不同义**（前者=候选类计划，后者=算完待采用），故常量分开命名。
    //   原 D7 词表 Created / Computed / ComputeFailed 已改名为 BUILDING / CANDIDATE / FAILED；
    //   `Computing` 保留（冻结未细分，但它是发令枪的领取闸门，见 SchedulingOrchestrator.RunSchedulingAutoAsync）。
    /// <summary>版本类别（VersionCategory）：CANDIDATE —— 候选类计划。**身份**，出生即定、终身不变。</summary>
    private const string PlanVersionCategoryCandidate = "CANDIDATE";
    /// <summary>计划版本状态（Status）：BUILDING —— 壳已建、待发令枪领取。</summary>
    private const string PlanVersionBuildingStatus = "BUILDING";
    /// <summary>计划版本状态（Status）：Computing —— 本域已被领取、正在计算（BUILDING 的子状态，冻结词表未细分）。</summary>
    private const string PlanVersionComputingStatus = "Computing";
    /// <summary>计划版本状态（Status）：CANDIDATE —— 计算成功、待人工确认采用（白天候选终态；夜间全量成功直接是 ACTIVE）。</summary>
    private const string PlanVersionReadyStatus = "CANDIDATE";
    /// <summary>计划版本状态：ACTIVE（每域单一正式采用版本）—— 正式采用记在 Status，与冻结 DDL 08:4057 唯一索引谓词同列</summary>
    private const string PlanVersionActiveStatus = "ACTIVE";
    /// <summary>计划版本状态：ARCHIVED（被同域新版本顶替）</summary>
    private const string PlanVersionArchivedStatus = "ARCHIVED";
    /// <summary>INSERT_ORDER_WHATIF RunType：仅组合 CTP / INSERT_IMPACT_ANALYSIS，永远不得激活（实施包十九）</summary>
    private const string InsertOrderWhatifRunType = "INSERT_ORDER_WHATIF";
    /// <summary>最小人工确认审计操作类型（P0-04 激活硬前置）</summary>
    private const string ConfirmCandidateOperation = "ConfirmCandidate";
    /// <summary>
    /// 计划版本状态（Status）：FAILED（G8 域级状态判定）。
    /// 与 <c>SchedulingOrchestrator</c> 的失败写点**同值** —— 2026-10-15 之前写点写 `ComputeFailed`、这里读 `FAILED`，
    /// 导致失败域永远判不出来（落到 `_ => RUNNING`）；词表对齐后该 bug 自愈。
    /// </summary>
    private const string PlanVersionFailedStatus = "FAILED";
    /// <summary>运行状态：COMPLETED（G8 终态判定）</summary>
    private const string ScheduleRunCompletedStatus = "COMPLETED";
    /// <summary>运行状态：PARTIAL_SUCCESS（G8 终态判定）</summary>
    private const string ScheduleRunPartialSuccessStatus = "PARTIAL_SUCCESS";
    /// <summary>域级状态：COMPLETED（成功）</summary>
    private const string RunDomainCompletedStatus = "COMPLETED";
    /// <summary>域级状态：CANDIDATE（待人工确认）</summary>
    private const string RunDomainCandidateStatus = "CANDIDATE";
    /// <summary>域级状态：RUNNING（计算中）</summary>
    private const string RunDomainRunningStatus = "RUNNING";
    /// <summary>域级状态：FAILED（失败根因域）</summary>
    private const string RunDomainFailedStatus = "FAILED";
    /// <summary>域级状态：BLOCKED（因上游失败被阻断）</summary>
    private const string RunDomainBlockedStatus = "BLOCKED";
    /// <summary>域级状态：NOT_STARTED（未参与本次）</summary>
    private const string RunDomainNotStartedStatus = "NOT_STARTED";
    /// <summary>白天候选运行类型（B-1：3号位 运行治理创建入口，0号位 2026-08-29 裁决3）</summary>
    private static readonly string[] DaytimeCandidateRunTypes =
    [
        StrategyProfileRunType.ManualReschedule,
        StrategyProfileRunType.LocalReschedule,
        StrategyProfileRunType.InsertOrderWhatIf,
    ];
    /// <summary>RunType×Purpose 冻结合法组合（实施包 §十九；CTP/INSERT_IMPACT_ANALYSIS 仅组合 INSERT_ORDER_WHATIF 且永远不得激活）</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> LegalPurposeByRunType =
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            [StrategyProfileRunType.InsertOrderWhatIf] = [StrategyProfilePurpose.Ctp, StrategyProfilePurpose.InsertImpactAnalysis],
            [StrategyProfileRunType.LocalReschedule] = [StrategyProfilePurpose.InsertReschedule, StrategyProfilePurpose.ManualAdjustment],
            [StrategyProfileRunType.ManualReschedule] = [StrategyProfilePurpose.ManualAdjustment],
        };
    /// <summary>白天候选运行创建审计操作类型（B-1）</summary>
    private const string CreateCandidateRunOperation = "CreateCandidateRun";

    private readonly IScheduleRunRepository _scheduleRunRepo;
    private readonly IPlanVersionRepository _planVersionRepo;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepo;
    private readonly IRuleSetVersionRepository _ruleSetVersionRepo;
    private readonly IParameterSetVersionRepository _parameterSetVersionRepo;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IDataScopeService _dataScopeService;
    private readonly IDomainDefinitionService _domainDefinitionService;
    private readonly IMasterDataLookupRepository _masterDataLookup;
    private readonly IOverviewQueryRepository _overviewQueryRepo;
    private readonly ISchedulingOrchestrator _schedulingOrchestrator;
    private readonly IScheduleRunService _scheduleRunService;
    private readonly ILogger<RunLifecycleService> _logger;

    public RunLifecycleService(
        IScheduleRunRepository scheduleRunRepo,
        IPlanVersionRepository planVersionRepo,
        IStrategyProfileVersionRepository strategyProfileVersionRepo,
        IRuleSetVersionRepository ruleSetVersionRepo,
        IParameterSetVersionRepository parameterSetVersionRepo,
        IAuditLogRepository auditLogRepository,
        IDataScopeService dataScopeService,
        IDomainDefinitionService domainDefinitionService,
        IMasterDataLookupRepository masterDataLookup,
        IOverviewQueryRepository overviewQueryRepo,
        ISchedulingOrchestrator schedulingOrchestrator,
        IScheduleRunService scheduleRunService,
        ILogger<RunLifecycleService> logger)
    {
        _scheduleRunRepo = scheduleRunRepo;
        _planVersionRepo = planVersionRepo;
        _strategyProfileVersionRepo = strategyProfileVersionRepo;
        _ruleSetVersionRepo = ruleSetVersionRepo;
        _parameterSetVersionRepo = parameterSetVersionRepo;
        _auditLogRepository = auditLogRepository;
        _dataScopeService = dataScopeService;
        _domainDefinitionService = domainDefinitionService;
        _masterDataLookup = masterDataLookup;
        _overviewQueryRepo = overviewQueryRepo;
        _schedulingOrchestrator = schedulingOrchestrator;
        _scheduleRunService = scheduleRunService;
        _logger = logger;
    }

    /// <summary>校验 ScheduleRun.ExpectedDomainKeysJson 冻结规则（P0-08；配置错误抛异常，不静默降级）</summary>
    public async Task ValidateExpectedDomainKeysAsync(int scheduleRunId, int actorUserId, CancellationToken ct = default)
    {
        var run = await _scheduleRunRepo.GetByIdAsync(scheduleRunId, ct)
            ?? throw new InvalidOperationException($"ScheduleRun 不存在：{scheduleRunId}");

        ValidateDomainKeys(run.RunType, run.ExpectedDomainKeysJson, $"ScheduleRun {scheduleRunId}");

        // B3：业务范围硬校验（F-G4 Domain 维度）—— 校验人仅可操作其授权 Domain 的 Run
        await EnsureDomainsInScopeAsync(actorUserId, ParseExpectedDomainKeys(run.ExpectedDomainKeysJson, scheduleRunId), ct);
    }

    /// <summary>
    /// ExpectedDomainKeysJson 冻结规则（FULL_SCHEDULE → Domain 数 ≥ 1；RESCHEDULE 类 → 恰 1 Domain）。
    /// JSON 数组格式由 DDL CHECK ISJSON 兜底，此处做语义校验：空/缺失/非数组/含空 DomainKey/数量越界一律抛异常。
    /// </summary>
    private static void ValidateDomainKeys(string runType, string? expectedDomainKeysJson, string displayName)
    {
        if (string.IsNullOrWhiteSpace(expectedDomainKeysJson))
        {
            throw new InvalidOperationException($"{displayName} 的 ExpectedDomainKeysJson 为空/缺失（运行启动须冻结预期 Domain 集合）");
        }

        List<string>? domains;
        try
        {
            domains = System.Text.Json.JsonSerializer.Deserialize<List<string>>(expectedDomainKeysJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"{displayName} 的 ExpectedDomainKeysJson 不是合法 JSON 数组：{ex.Message}", ex);
        }

        if (domains == null)
        {
            throw new InvalidOperationException($"{displayName} 的 ExpectedDomainKeysJson 反序列化结果为空");
        }

        if (domains.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{displayName} 的 ExpectedDomainKeysJson 含空 DomainKey（预期 Domain 不可为空）");
        }

        if (runType == FullScheduleRunType)
        {
            if (domains.Count < 1)
            {
                throw new InvalidOperationException($"{displayName} 为 FULL_SCHEDULE，预期 Domain 数须 ≥ 1（当前 {domains.Count}）");
            }

            // P1-01：FULL 场景重复 DomainKey 拒绝（预期 Domain 集合不可重复）
            if (domains.Distinct().Count() != domains.Count)
            {
                throw new InvalidOperationException($"{displayName} 为 FULL_SCHEDULE，预期 Domain 集合含重复 DomainKey（须去重后唯一）");
            }
        }
        else
        {
            // RESCHEDULE 类（Candidate）：恰 1 Domain
            if (domains.Count != 1)
            {
                throw new InvalidOperationException($"{displayName} 为 {runType}（RESCHEDULE 类/Candidate），预期 Domain 数须恰为 1（当前 {domains.Count}）");
            }
        }
    }

    /// <summary>
    /// Candidate 最小人工确认（P0-08 / 二轮复审 P0-05 / P0-06）。
    /// 语义：确认**仅记录确认事实，不写 ActivatedAt/ActivatedBy、不转 ACTIVE、不预检同域 ACTIVE**；
    ///       Base ACTIVE 存在时仍可正常确认。确认事实唯一落点是 ConfirmCandidate 审计记录，
    ///       ActivateCandidateAsync 以该审计作为"已完成最小人工确认"的硬前置。
    /// </summary>
    public async Task ConfirmCandidateAsync(int planVersionId, int actorUserId, string actor, string? remark, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("确认人（Actor）不能为空");
        }

        var version = await _planVersionRepo.GetByIdAsync(planVersionId, ct)
            ?? throw new InvalidOperationException($"计划版本不存在：{planVersionId}");

        EnsureCandidateConfirmable(version);

        // 5e：业务范围校验（F-G4）—— 确认人仅可操作其 DataScopePolicy 授权 Domain，
        //     以及该 Domain 所属 ProductFamily / Factory 两维（缺口A／R11-3B；与创建路径 `CreateCandidateRunAsync` 同口径）。
        //     2026-10-30（2号位）：原仅判 Domain 一维，现改走 EnsureDomainsInScopeAsync（内部已含两维，判定不变只是补齐）。
        await EnsureDomainsInScopeAsync(actorUserId, [version.DomainKey!], ct);

        // 仅记审计：Actor / ConfirmedAt / CandidatePlanVersionId(=planVersionId) / 必要 Remark
        // D7：审计状态记真实 Status（候选排程完成态 = Computed），不记旧 CANDIDATE 词表
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = ConfirmCandidateOperation,
            EntityType = "PlanVersion",
            EntityId = planVersionId.ToString(),
            OldValue = PlanVersionReadyStatus,
            NewValue = PlanVersionReadyStatus,
            UserId = actorUserId,
            UserCode = actor,
            OccurredAt = DateTime.Now,
            Remark = $"确认候选版本（CandidatePlanVersionId={planVersionId}）"
                + (string.IsNullOrWhiteSpace(remark) ? string.Empty : $"：{remark}"),
        }, ct);
    }

    /// <summary>
    /// 激活 Candidate（确认后正式采用：CANDIDATE → ACTIVE，原子替换同域旧 ACTIVE）。
    /// 前置（硬校验，缺失/不满足一律抛 InvalidOperationException）：
    ///   a) DomainKey 非空（V1 必填）；
    ///   b) 已完成最小人工确认（存在 ConfirmCandidate 审计记录，二轮复审 P0-04）；
    ///   c) 来源 Run 可激活（SourceScheduleRunId 非空且 RunType != INSERT_ORDER_WHATIF，二轮复审 P0-03：
    ///      INSERT_ORDER_WHATIF 仅组合 CTP / INSERT_IMPACT_ANALYSIS，二者永远不得激活）。
    /// 采用边界（二轮复审 P0-06）：原子替换——单事务内归档同域既有 ACTIVE（→ARCHIVED + ArchivedAt）
    ///       再将本 Candidate 置 ACTIVE；UQ_PlanVersion_OneActivePerDomain 红线保留，不删除。
    /// </summary>
    public async Task ActivateCandidateAsync(int planVersionId, int actorUserId, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("激活人（Actor）不能为空");
        }

        var version = await _planVersionRepo.GetByIdAsync(planVersionId, ct)
            ?? throw new InvalidOperationException($"计划版本不存在：{planVersionId}");

        EnsureCandidateConfirmable(version);

        // 5e：业务范围校验（F-G4）—— 激活人仅可操作其 DataScopePolicy 授权 Domain，
        //     以及该 Domain 所属 ProductFamily / Factory 两维（缺口A／R11-3B；与创建路径 `CreateCandidateRunAsync` 同口径）。
        //     2026-10-30（2号位）：原仅判 Domain 一维，现改走 EnsureDomainsInScopeAsync（内部已含两维，判定不变只是补齐）。
        await EnsureDomainsInScopeAsync(actorUserId, [version.DomainKey!], ct);

        // R11-3b 对象级（0号位 裁决 v1.1）：「真实变更范围」复核 —— 激活人还须对本版本**实际落到**的
        // Order 工厂/产品族、Task 生产部门逐值持权（fail-closed），防「域名义二维在授权内、真实影响扩大越权」。
        // 与创建时 `EnsureDomainsInScopeAsync`（域声明名义维度）互补：创建判「可能修改范围」、激活判「真实变更范围」。
        await EnsureObjectScopeDimensionsInScopeAsync(actorUserId, version.Id, ct);

        // P0-04：已完成最小人工确认 —— 校验存在 ConfirmCandidate 审计记录
        await EnsureConfirmedAsync(planVersionId, ct);

        // P0-03：来源 Run 可激活 —— SourceScheduleRunId 非空 + RunType != INSERT_ORDER_WHATIF
        await EnsureSourceRunActivatableAsync(version, ct);

        // P0-08 项2（R11-3B）：Base 必须仍是当前 ACTIVE —— 经来源 Run 冻结的 BasePlanVersionId 重查当前 ACTIVE，
        // 防「读旧 Base 快照 → 另一候选/新 ACTIVE 已顶替 → 本候选仍激活」的并发窗口（Base 陈旧/被顶替禁直接采用）。
        // 数据源 = ScheduleRun.BasePlanVersionId（创建时冻结，权威落盘）；读表经 _scheduleRunRepo.GetByIdAsync（Dapper 已映射）。
        await EnsureBaseStillActiveAsync(version, ct);

        var activatedAt = DateTime.Now;
        // 口径回退（2026-10-15，B）：激活写的是 **Status** —— 冻结字段说明 07 §4.2「正式采用=ACTIVE」、
        // 冻结 DDL 08:8723 逐字／08:4057 唯一索引谓词 `WHERE Status = 'ACTIVE'`、我方 2026-09-03 词表件 §一.2 四处同口径。
        // VersionCategory 是**出生即定的版本类别**，激活**不得翻转它**（A 正是翻它，故作废）。
        version.Status = PlanVersionActiveStatus;
        version.ActivatedAt = activatedAt;
        version.ActivatedBy = actor;

        // 预检（F-G4 跨库兜底）：状态写前先确认审计库可写，失败不等状态落库即抛
        await _auditLogRepository.EnsureWritableAsync(ct);

        // P0-06：原子替换（同域既有 ACTIVE 归档 + 本版本置 ACTIVE，单事务）
        await _planVersionRepo.ReplaceActiveAsync(version, actor, activatedAt, ct);

        // P1-05 + R-01（0号位 复审 v1.1，方案B）：审计写入失败由 AuditLogRepository.AddAsync 统一落对账日志
        // （AuditReconcile：完整审计负载 JSON 供运维人工补录）并原样重抛——fail-closed 保持、可追溯性不降级。
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = "ActivateCandidate",
            EntityType = "PlanVersion",
            EntityId = planVersionId.ToString(),
            OldValue = PlanVersionReadyStatus,
            NewValue = PlanVersionActiveStatus,
            UserId = actorUserId,
            UserCode = actor,
            OccurredAt = activatedAt,
            Remark = $"候选版本正式采用（CANDIDATE → ACTIVE，原子替换同域旧 ACTIVE）：{planVersionId}",
        }, ct);
    }

    /// <summary>P0-04：校验该 Candidate 已完成最小人工确认（存在 ConfirmCandidate 审计记录）</summary>
    private async Task EnsureConfirmedAsync(int planVersionId, CancellationToken ct)
    {
        var logs = await _auditLogRepository.GetByEntityAsync("PlanVersion", planVersionId.ToString(), ct);
        var confirmed = logs.Any(l => l.ActionCode == ConfirmCandidateOperation);
        if (!confirmed)
        {
            throw new InvalidOperationException($"计划版本 {planVersionId} 未完成最小人工确认（缺 ConfirmCandidate 审计），不可激活");
        }
    }

    /// <summary>P0-03：来源 Run 可激活校验（SourceScheduleRunId 非空 + RunType != INSERT_ORDER_WHATIF）</summary>
    private async Task EnsureSourceRunActivatableAsync(PlanVersion version, CancellationToken ct)
    {
        if (!version.SourceScheduleRunId.HasValue)
        {
            throw new InvalidOperationException($"计划版本 {version.Id} 的 SourceScheduleRunId 为空，无法证明来源 Run 可激活（拒绝激活）");
        }

        var run = await _scheduleRunRepo.GetByIdAsync(version.SourceScheduleRunId.Value, ct);
        if (run == null)
        {
            throw new InvalidOperationException($"计划版本 {version.Id} 的来源 ScheduleRun {version.SourceScheduleRunId.Value} 不存在，拒绝激活");
        }

        if (run.RunType == InsertOrderWhatifRunType)
        {
            throw new InvalidOperationException(
                $"计划版本 {version.Id} 的来源 Run（{run.Id}）为 {InsertOrderWhatifRunType}"
                + "（实施包十九：INSERT_ORDER_WHATIF 仅组合 CTP / INSERT_IMPACT_ANALYSIS，永远不得激活）");
        }
    }

    /// <summary>
    /// P0-08 项2（R11-3B）：Base 必须仍是当前 ACTIVE —— 经来源 Run 冻结的 BasePlanVersionId 重查当前 ACTIVE。
    /// 语义：候选激活前，其 Base（创建时冻结的当前 ACTIVE）若已被同域新 ACTIVE 顶替（ARCHIVED），则禁止直接采用本候选
    /// （Base 陈旧/已变更禁直接采用），防「读旧 Base 快照 → 他人已激活 → 本候选仍激活」的并发竞态窗口。
    /// 数据源：来源 ScheduleRun.BasePlanVersionId（创建时权威落盘，见 DomainSolveRequest 备注）；读表经 _scheduleRunRepo。
    /// 兼容：无 BasePlanVersionId（如容错恢复旧链）→ 跳过重查（保持既有激活语义，不新增阻断）。
    /// </summary>
    private async Task EnsureBaseStillActiveAsync(PlanVersion version, CancellationToken ct)
    {
        var run = await _scheduleRunRepo.GetByIdAsync(version.SourceScheduleRunId!.Value, ct);
        if (run == null || !run.BasePlanVersionId.HasValue)
        {
            // 来源 Run 缺失或未冻结 Base 锚点：无 Base 可比对，跳过（不阻断既有激活路径）。
            return;
        }

        var baseVersion = await _planVersionRepo.GetByIdAsync(run.BasePlanVersionId.Value, ct);
        if (baseVersion == null)
        {
            throw new InvalidOperationException($"计划版本 {version.Id} 的冻结 Base（{run.BasePlanVersionId.Value}）已不存在，拒绝激活");
        }

        // 重查当前 ACTIVE：Base 必须是该域当前正式采用版本，否则已被顶替（陈旧），拒绝直接采用。
        var currentActive = await _planVersionRepo.GetActiveByDomainKeyAsync(version.DomainKey!, ct);
        if (currentActive == null || currentActive.Id != baseVersion.Id)
        {
            throw new InvalidOperationException(
                $"计划版本 {version.Id} 的冻结 Base（{baseVersion.Id}）已非该域当前 ACTIVE"
                + $"（当前 ACTIVE={currentActive?.Id.ToString() ?? "无"}），Base 已被顶替/变更，拒绝直接采用");
        }
    }

    /// <summary>
    /// 候选确认/激活前置校验（2026-10-15 词表对齐后：候选身份在 VersionCategory，Status 走冻结词表）。
    /// 门禁：VersionCategory 必须 CANDIDATE，且 Status 必须为 CANDIDATE（算完待采用）才可确认/激活；DomainKey 非空（V1 必填语义）。
    /// </summary>
    private static void EnsureCandidateConfirmable(PlanVersion version)
    {
        if (version.VersionCategory != PlanVersionCategoryCandidate)
        {
            throw new InvalidOperationException(
                $"计划版本 {version.Id} 的 VersionCategory 为 {version.VersionCategory}，仅 CANDIDATE 可确认/激活");
        }

        if (version.Status != PlanVersionReadyStatus)
        {
            throw new InvalidOperationException(
                $"计划版本 {version.Id} 状态为 {version.Status}，仅排程完成（Status=CANDIDATE，即算完待采用）可确认/激活");
        }

        if (string.IsNullOrWhiteSpace(version.DomainKey))
        {
            throw new InvalidOperationException($"计划版本 {version.Id} 的 DomainKey 为空（V1 必填语义，无法按域确认/激活）");
        }
    }

    /// <summary>
    /// 业务范围校验（5e：F-G4 Domain 维度）。写入人仅可操作其 DataScopePolicy 授权 Domain；
    /// 未授权一律抛 <see cref="ScopeViolationException"/>（安全默认，失败关闭，Web 层映射 403）。
    /// </summary>
    private Task EnsureDomainInScopeAsync(int actorUserId, string domainKey, CancellationToken ct)
        => _dataScopeService.EnsureInScopeAsync(actorUserId, DataScopeTypes.Domain, domainKey, ct);

    /// <summary>B3：多 Domain 业务范围硬校验（F-G4 Domain 维度，fail-closed）。用于校验/恢复等多 Domain 场景。</summary>
    private async Task EnsureDomainsInScopeAsync(int actorUserId, IReadOnlyList<string> domainKeys, CancellationToken ct)
    {
        foreach (var domainKey in domainKeys)
        {
            await _dataScopeService.EnsureInScopeAsync(actorUserId, DataScopeTypes.Domain, domainKey, ct);
        }

        // 缺口A（R11-3B 项1）：Domain 所属维度（ProductFamily / Factory）同批硬校验
        await EnsureDomainBusinessDimensionsInScopeAsync(actorUserId, domainKeys, ct);
    }

    /// <summary>
    /// 缺口A（R11-3B 项1）：Domain 所属 ProductFamily / Factory 两维业务范围硬校验。
    /// 语义＝**范围C**（与 Department 同解，0号位 2026-10-29 口径）：**只约束已配该维度的用户**；
    /// 该维度完全未配 → 不构成拒绝理由（多维度范围本就是交集，未配＝不参与交集）。
    /// 维度目标值：经 <see cref="IDomainDefinitionService"/>（域定义＝权威源）取 Id，再经主档换算为 Code（DataScope 值域）；
    /// 不落 Base 快照、不造第二真相源。FactoryId 为 null（FAMILY 类跨全厂域）→ 该域原生不涉工厂维度，跳过。
    /// </summary>
    private async Task EnsureDomainBusinessDimensionsInScopeAsync(int actorUserId, IReadOnlyList<string> domainKeys, CancellationToken ct)
    {
        if (domainKeys.Count == 0)
        {
            return;
        }

        var domains = await _domainDefinitionService.GetActiveDomainsAsync(ct);
        if (domains is null || domains.Count == 0)
        {
            // 域集为空＝治理配置缺口（Domain 维度已由 EnsureInScopeAsync 管控），不臆断维度值，仅记录。
            _logger.LogWarning("启用域集为空，跳过 ProductFamily/Factory 维度范围校验");
            return;
        }

        var ctx = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (ctx.IsGlobal)
        {
            return;
        }

        foreach (var domainKey in domainKeys)
        {
            var domain = domains.FirstOrDefault(d => string.Equals(d.DomainKey, domainKey, StringComparison.Ordinal));
            if (domain is null)
            {
                _logger.LogWarning("Domain={DomainKey} 不在启用域集，跳过 ProductFamily/Factory 维度范围校验", domainKey);
                continue;
            }

            var productFamilyCode = await _masterDataLookup.GetProductFamilyCodeAsync(domain.ProductFamilyId, ct);
            EnsureDimensionInScope(ctx, DataScopeTypes.ProductFamily, productFamilyCode, domainKey);

            if (domain.FactoryId.HasValue)
            {
                var factoryCode = await _masterDataLookup.GetFactoryCodeAsync(domain.FactoryId.Value, ct);
                EnsureDimensionInScope(ctx, DataScopeTypes.Factory, factoryCode, domainKey);
            }
        }
    }

    /// <summary>
    /// 单维度判定（范围C）：该维度未配 → 放行；已配 → 目标 Code 必须落入授权集。
    /// 已配但主档解析不出 Code（域指向脏 Id）→ 拒绝（fail-closed，不因数据脏而放行）。
    /// </summary>
    private static void EnsureDimensionInScope(DataScopeContext ctx, string scopeType, string? targetCode, string domainKey)
    {
        if (ctx.GetValues(scopeType) is null)
        {
            return; // 范围C：未配该维度 → 该维度不构成约束
        }

        if (string.IsNullOrWhiteSpace(targetCode))
        {
            throw new ScopeViolationException(scopeType, $"Domain={domainKey} 的 {scopeType} 主档编码无法解析");
        }

        if (!ctx.Allows(scopeType, targetCode))
        {
            throw new ScopeViolationException(scopeType, targetCode);
        }
    }

    /// <summary>
    /// R11-3b 对象级「真实变更范围」复核（0号位 裁决 v1.1；激活/发布前硬校验，fail-closed）。
    /// 语义：激活人除满足创建时的「域声明名义维度」（<see cref="EnsureDomainsInScopeAsync"/>），还须对本版本<b>实际落到</b>的
    /// 对象维度逐值持权 —— 工厂/产品族（Order.FactoryId/ProductFamilyId）、生产部门（Task.ProductionDepartmentId）。
    /// 与创建路径互补：创建判「可能修改范围」、激活判「真实变更范围」（求解后实际影响扩大 → 激活前拒绝，403，不静默剔越权 Task）。
    /// 边界（裁决要点落位）：
    ///   * 工厂维度只取 Order.FactoryId（主体计划厂）；不含 Pegging 引用外厂只读 Supply（只读引用≠写权）；
    ///   * 跨厂 SH 的 SourceFactoryId 仅识别源厂履约域，不扩大工厂维度；
    ///   * FAMILY 域定义 FactoryId 为空不影响：对象级工厂取自 Order.FactoryId（NOT NULL），与域定义无关；
    ///   * NULL 部门 Task（历史兼容）不入部门维度判定，其计数非 0 时记 Warning（不静默），见下方；
    ///   * 该版本无任何 Order/Task → 空集合，按「无对象可越权」放行。
    /// 主档换算复用 <see cref="IMasterDataLookupRepository"/>（Id→Code，权威映射），已配但解析不出 → fail-closed 拒绝。
    /// </summary>
    private async Task EnsureObjectScopeDimensionsInScopeAsync(int actorUserId, int planVersionId, CancellationToken ct)
    {
        var ctx = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);
        if (ctx.IsGlobal)
        {
            return; // Global：全维度放行，无需对象级复核
        }

        var dims = await _overviewQueryRepo.GetPlanVersionObjectScopeDimensionsAsync(planVersionId, ct);

        // M1：NULL 部门 Task 逃逸部门维度（历史兼容数据，生产链 V1 要求新 Task 不留 NULL）。
        //     fail-open 子集不静默 —— 记 Warning，供运营留意；不按此拒绝（避免误阻断合法历史激活，二选一取 Warning）。
        if (dims.NullDepartmentTaskCount > 0)
        {
            _logger.LogWarning(
                "计划版本 {PlanVersionId} 有 {Count} 条 Task 的 ProductionDepartmentId 为 NULL（历史兼容数据），"
                + "这些 Task 不参与对象级 Department 维度判定，可能逃逸该维度授权检查",
                planVersionId, dims.NullDepartmentTaskCount);
        }

        // 工厂维度（Order.FactoryId）
        if (dims.FactoryIds.Count > 0)
        {
            var factoryCodes = await ResolveCodesAsync(
                dims.FactoryIds, id => _masterDataLookup.GetFactoryCodeAsync(id, ct));
            foreach (var (id, code) in factoryCodes)
            {
                EnsureResolvedDimensionInScope(ctx, DataScopeTypes.Factory, code, id, "Order.FactoryId");
            }
        }

        // 产品族维度（Order.ProductFamilyId）
        if (dims.ProductFamilyIds.Count > 0)
        {
            var familyCodes = await ResolveCodesAsync(
                dims.ProductFamilyIds, id => _masterDataLookup.GetProductFamilyCodeAsync(id, ct));
            foreach (var (id, code) in familyCodes)
            {
                EnsureResolvedDimensionInScope(ctx, DataScopeTypes.ProductFamily, code, id, "Order.ProductFamilyId");
            }
        }

        // 生产部门维度（Task.ProductionDepartmentId，v5.0 真实生产部门）
        if (dims.DepartmentIds.Count > 0)
        {
            var deptCodes = await ResolveCodesAsync(
                dims.DepartmentIds, id => _masterDataLookup.GetDepartmentCodeAsync(id, ct));
            foreach (var (id, code) in deptCodes)
            {
                EnsureResolvedDimensionInScope(ctx, DataScopeTypes.Department, code, id, "Task.ProductionDepartmentId");
            }
        }
    }

    /// <summary>
    /// 主档 Id 集合 → Code 集合批查（保持顺序，逐 id 换算）；解析不出的 id 由其调用方 fail-closed。
    /// 返回 (Id, Code) 序列，Code 为 null 时由 <see cref="EnsureResolvedDimensionInScope"/> 拒绝。
    /// </summary>
    private async Task<IReadOnlyList<(int Id, string? Code)>> ResolveCodesAsync(
        IReadOnlyList<int> ids,
        Func<int, Task<string?>> resolveAsync)
    {
        var result = new List<(int, string?)>(ids.Count);
        foreach (var id in ids)
        {
            var code = await resolveAsync(id);
            result.Add((id, code));
        }

        return result;
    }

    /// <summary>
    /// 单维度值判定（范围C）：该维度未配 → 放行；已配 → 目标 Code 必须落入授权集。
    /// Code 解析不出（对象指向脏主档）→ 拒绝（fail-closed，不因数据脏而放行）。
    /// </summary>
    private static void EnsureResolvedDimensionInScope(DataScopeContext ctx, string scopeType, string? targetCode, int objectId, string sourceColumn)
    {
        if (ctx.GetValues(scopeType) is null)
        {
            return; // 范围C：该维度未配 → 不构成约束
        }

        if (string.IsNullOrWhiteSpace(targetCode))
        {
            throw new ScopeViolationException(scopeType, $"对象 {sourceColumn}={objectId} 的主档编码无法解析");
        }

        if (!ctx.Allows(scopeType, targetCode))
        {
            throw new ScopeViolationException(scopeType, targetCode);
        }
    }

    /// <summary>
    /// FAILED 恢复（P0-08 + P1-03）：为 FAILED FULL_SCHEDULE Run 新建一条 RUNNING 重跑，同一事务为每个预期 Domain
    /// 各建一个 RECOVERY 壳（一域一壳），随后内联全量执行并一次性收口；继承策略包版本与 Domain 基线；绝不动旧记录。
    /// </summary>
    public async Task<int> RecoverFailedRunAsync(
        int failedScheduleRunId,
        int actorUserId,
        string actorUserCode,
        CancellationToken ct = default)
    {
        // 冻结主体：新 Run 的 TriggeredBy 必须写<b>恢复人登录名</b>，装载侧业务范围判定按它解析。
        // 空值不可接受——写死/写空都会让「配了 Department 范围的用户恢复 FULL_SCHEDULE 失败 Run」被整轮拒，等于堵死唯一官方恢复通道。
        if (string.IsNullOrWhiteSpace(actorUserCode))
        {
            throw new InvalidOperationException(
                "恢复操作人登录名（actorUserCode）不能为空：新 Run 的 TriggeredBy 作为冻结主体参与业务范围判定");
        }

        var failed = await _scheduleRunRepo.GetByIdAsync(failedScheduleRunId, ct)
            ?? throw new InvalidOperationException($"ScheduleRun 不存在：{failedScheduleRunId}");

        if (failed.Status != ScheduleRunFailedStatus)
        {
            throw new InvalidOperationException($"ScheduleRun {failedScheduleRunId} 状态为 {failed.Status}，仅 FAILED 可恢复（旧记录不可回改 RUNNING）");
        }

        // P1-03 §5.1：仅 FULL_SCHEDULE 可恢复。非 FULL（候选/局部重排）恢复会丢 BasePlanVersionId 基线
        //（恢复 INSERT 不继承该列），需求侧订单不钉基线、退化为按本 PV 装，恢复语义不正确。fail-closed 拒绝。
        if (failed.RunType != FullScheduleRunType)
        {
            throw new InvalidOperationException(
                $"ScheduleRun {failedScheduleRunId} 的 RunType 为 {failed.RunType}，仅 {FullScheduleRunType} 可恢复（非全量运行无法继承 Base 基线）");
        }

        // 新建前先校验继承基线合法性（避免插入后再因基线不合法产生孤立 RUNNING 记录）
        ValidateDomainKeys(failed.RunType, failed.ExpectedDomainKeysJson, $"ScheduleRun {failedScheduleRunId} 继承基线");

        // B3：业务范围硬校验（F-G4 Domain 维度）—— 恢复人仅可操作其授权 Domain 的 Run（fail-closed）
        var domainKeys = ParseExpectedDomainKeys(failed.ExpectedDomainKeysJson, failedScheduleRunId);
        await EnsureDomainsInScopeAsync(actorUserId, domainKeys, ct);

        // P1-03 §3.2/§3.3：一域一壳（N 个），窗口继承失败 Run 既有 PlanVersion，缺失兜底今天 ~ +90 天（不得静默平移）。
        var failedPlanVersions = await _planVersionRepo.GetByScheduleRunIdAsync(failedScheduleRunId, ct);
        var shells = new List<RecoveryShellSpec>(domainKeys.Count);
        foreach (var domainKey in domainKeys)
        {
            var pv = failedPlanVersions.FirstOrDefault(p => string.Equals(p.DomainKey, domainKey, StringComparison.Ordinal));
            shells.Add(new RecoveryShellSpec
            {
                DomainKey = domainKey,
                PlanHorizonStart = pv is not null && pv.PlanHorizonStart != default ? pv.PlanHorizonStart : DateTime.Today,
                PlanHorizonEnd = pv is not null && pv.PlanHorizonEnd != default ? pv.PlanHorizonEnd : DateTime.Today.AddDays(90),
            });
        }

        // 同一事务原子写 Run + N 壳（任一失败整体回滚，不产生孤立 RUNNING）
        var newRunId = await _scheduleRunRepo.InsertForRecoveryWithShellsAsync(failed, shells, actorUserCode.Trim(), ct);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = "RecoverFailedRun",
            EntityType = "ScheduleRun",
            EntityId = failedScheduleRunId.ToString(),
            OldValue = ScheduleRunFailedStatus,
            NewValue = ScheduleRunRunningStatus,
            UserId = actorUserId,
            OccurredAt = DateTime.Now,
            Remark = $"由 FAILED 运行 {failedScheduleRunId} 恢复，新建 RUNNING 运行 {newRunId}（一域一壳 {shells.Count} 个，继承 StrategyProfileVersionId 与 ExpectedDomainKeysJson 基线）",
        }, ct);

        // P1-03 §2：内联全量执行并一次性收口（与候选路径一致，恢复 = 交互式立即重算）。
        // 注意超时预算：多域耗时 ≈ N × 单域（FULL 单域 ≈ 5.4 分钟），端点须放宽超时；排队/巡检兜底另行立项（发令枪/巡检在 2号位侧）。
        var runResult = await _schedulingOrchestrator.ExecuteRunAsync(newRunId, ct);
        if (!runResult.IsSuccess)
        {
            // ExecuteRunAsync 不抛业务异常，失败以返回结果表达（Run 已收口 PARTIAL_SUCCESS / FAILED）；
            // 此处仅记录，供运维/追溯定位，不改变 Run 终态。
            _logger.LogWarning(
                "FAILED 恢复执行未全部成功：NewRunId={NewRunId} IsSuccess={IsSuccess} ScheduledCount={ScheduledCount} Error={Error}",
                newRunId, runResult.IsSuccess, runResult.ScheduledCount, runResult.ErrorMessage);
        }

        return newRunId;
    }

    /// <summary>
    /// 白天候选运行创建（B-1：0号位 2026-08-29 裁决3——白天候选 ScheduleRun 创建归 3号位 运行治理侧；
    /// 冻结 运行类型 × 用途 × 策略版本，交 2号位 主流程执行收口）。
    /// 校验顺序（任一失败抛 InvalidOperationException，不静默降级）：见契约草案 §三。
    /// </summary>
    public async Task<CandidateRunCreatedResult> CreateCandidateRunAsync(CandidateRunCreateSpec spec, int actorUserId, CancellationToken ct = default)
    {
        // 1. Actor 必填
        if (string.IsNullOrWhiteSpace(spec.Actor))
        {
            throw new InvalidOperationException("操作人（Actor）不能为空");
        }

        // 2. RunType ∈ 白天候选类（FULL_SCHEDULE / SIMULATION 拒绝）
        if (!DaytimeCandidateRunTypes.Contains(spec.RunType))
        {
            throw new InvalidOperationException(
                $"RunType={spec.RunType} 不属于白天候选运行类（{string.Join("/", DaytimeCandidateRunTypes)}），拒绝创建");
        }

        // 3. Purpose ∈ RunType 冻结合法组合（实施包 §十九）
        if (!LegalPurposeByRunType.TryGetValue(spec.RunType, out var legalPurposes))
        {
            throw new InvalidOperationException($"RunType={spec.RunType} 未配置冻结合法用途组合，拒绝创建");
        }

        if (!legalPurposes.Contains(spec.Purpose))
        {
            throw new InvalidOperationException(
                $"RunType={spec.RunType} 的用途 {spec.Purpose} 不在冻结合法组合（{string.Join("/", legalPurposes)}），拒绝创建（实施包 §十九）");
        }

        // 3a. ScopeJsonV2 范围载荷校验（M1：八码映射 + PriorityMode 轴 + 单一真相；GANTT/EQUIPMENT/RESOURCE_CALENDAR + EXPEDITE 拒绝）
        ScopeJsonV2Validator.Validate(spec.Scope, spec.RunType, spec.Purpose);

        // 3a'. BusinessScope 装配（真空④契约定案 §三/§四：3号位 生成权）。
        // 服务端权威解析 actor 业务范围（经 IDataScopeService.ResolveScopeAsync），依 Trigger 判定维度装配进 ScopeJsonV2，
        // **覆盖** 前端/调用方可能自传的 BusinessScope 值（范围=授权边界，不得由调用方自定）。
        // 中断级（EQUIPMENT_FAILURE / RESOURCE_CALENDAR_CHANGE）= Global；订单类=ProductFamily；Task 局部=ResourceOrgGroup（DataScopeTypes 常量）。
        if (spec.Scope is not null)
        {
            spec.Scope.BusinessScope = await BuildBusinessScopeAsync(actorUserId, spec.Scope.Trigger, ct);
        }

        // 4. DomainKey 必填（白天候选严格单 Domain）
        if (string.IsNullOrWhiteSpace(spec.DomainKey))
        {
            throw new InvalidOperationException("目标 DomainKey 不能为空（白天候选运行严格单 Domain）");
        }

        // 4a. B3：业务范围硬校验（F-G4 Domain 维度）—— 创建人仅可操作其授权 Domain（fail-closed）
        await EnsureDomainInScopeAsync(actorUserId, spec.DomainKey, ct);

        // 4a'. 缺口A（R11-3B 项1）：Domain 所属 ProductFamily / Factory 两维范围硬校验（范围C：仅约束已配该维度者）
        await EnsureDomainBusinessDimensionsInScopeAsync(actorUserId, [spec.DomainKey], ct);

        // 5. Base ACTIVE 解析与校验（Base ACTIVE 只读锚定：白天 Candidate 必须基于当前 ACTIVE，不修改 Base）
        PlanVersion? baseVersion;
        if (spec.BasePlanVersionId.HasValue)
        {
            baseVersion = await _planVersionRepo.GetByIdAsync(spec.BasePlanVersionId.Value, ct);
            if (baseVersion == null)
            {
                throw new InvalidOperationException($"Base 计划版本不存在：{spec.BasePlanVersionId.Value}");
            }

            if (baseVersion.Status != PlanVersionActiveStatus)
            {
                throw new InvalidOperationException(
                    $"Base 计划版本 {baseVersion.Id} 状态为 {baseVersion.Status}（需 Status=ACTIVE），白天候选必须基于 ACTIVE（Base 只读）");
            }

            if (!string.Equals(baseVersion.DomainKey, spec.DomainKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Base 计划版本 {baseVersion.Id} 的 DomainKey={baseVersion.DomainKey} 与目标 {spec.DomainKey} 不一致");
            }
        }
        else
        {
            baseVersion = await _planVersionRepo.GetActiveByDomainKeyAsync(spec.DomainKey, ct);
            if (baseVersion == null)
            {
                throw new InvalidOperationException(
                    $"Domain={spec.DomainKey} 无当前 ACTIVE 计划版本，白天候选运行必须基于 Base ACTIVE（拒绝创建）");
            }
        }

        // 6. 默认策略版本解析（有效窗口 0 缺失 / 多歧义拒绝——红线 #4：禁止盲目取第一个）
        // AE-007 / Q8（2026-10-29 落码）：精确 RunType 优先，该时点无任何精确生效默认时才回退
        // RunType IS NULL（适用所有）兜底；两层不混合。生效窗口下沉到仓储 SQL 判定，
        // 避免「精确层存在但已过期」错误地挡住兜底。同层多默认仍判歧义拒绝创建。
        var now = spec.DataCutoffTime ?? DateTime.Now;
        var effectiveDefaults = await _strategyProfileVersionRepo
            .GetDefaultByRunTypeOrFallbackAsync(spec.RunType, now, ct);

        if (effectiveDefaults.Count == 0)
        {
            throw new InvalidOperationException($"RunType={spec.RunType} 无当前有效默认 PUBLISHED 策略包版本（拒绝创建）");
        }

        if (effectiveDefaults.Count > 1)
        {
            throw new InvalidOperationException(
                $"RunType={spec.RunType} 存在 {effectiveDefaults.Count} 个当前有效默认 PUBLISHED 策略包版本，歧义拒绝创建");
        }

        var strategyProfileVersionId = effectiveDefaults[0].Id;

        // 7. Candidate 壳计划窗口：复制 Base 的（不修改 Base），缺省 今天~+90 天
        var createSpec = new CandidateRunCreateSpec
        {
            RunType = spec.RunType,
            Purpose = spec.Purpose,
            DomainKey = spec.DomainKey,
            BasePlanVersionId = spec.BasePlanVersionId ?? baseVersion.Id,
            DataCutoffTime = spec.DataCutoffTime,
            Scope = spec.Scope,
            Actor = spec.Actor,
            PlanHorizonStart = baseVersion.PlanHorizonStart != default ? baseVersion.PlanHorizonStart : DateTime.Today,
            PlanHorizonEnd = baseVersion.PlanHorizonEnd != default ? baseVersion.PlanHorizonEnd : DateTime.Today.AddDays(90),
        };

        // 8. 单事务原子写 Run + Candidate 壳（任一失败整体回滚，不产生孤立 RUNNING 运行）
        var result = await _scheduleRunRepo.CreateCandidateRunAsync(createSpec, strategyProfileVersionId, spec.Actor, ct);

        // 9. 审计（契约点 P1：Purpose 本轮仅审计不落库）
        await _auditLogRepository.AddAsync(new AuditLog
        {
            ActionCode = CreateCandidateRunOperation,
            EntityType = "ScheduleRun",
            EntityId = result.NewScheduleRunId.ToString(),
            OldValue = "-",
            NewValue = ScheduleRunRunningStatus,
            UserId = actorUserId,
            UserCode = spec.Actor,
            OccurredAt = DateTime.Now,
            Remark = $"白天候选运行创建：RunType={spec.RunType}, Purpose={spec.Purpose}, Domain={spec.DomainKey}, "
                + $"BasePlanVersionId={createSpec.BasePlanVersionId}, StrategyProfileVersionId={strategyProfileVersionId}, "
                + $"NewPlanVersionId={result.NewPlanVersionId}",
        }, ct);

        // 10. 触发接缝（P1-02）：建 Run + 候选壳后正式接通 3→2 Application Service 契约，
        //     调 2号位 RunSchedulingAndFinalizeAsync 执行并收口 Run（RUNNING → COMPLETED / FAILED）。
        // P1-03 §5.4 兜底：同步耗时（单域≈5.4min）期间应用池回收/发版/OOM/客户端断连 → 异常直接穿出、
        //     Run 永久卡 RUNNING（发令枪只领 FULL_SCHEDULE、恢复端点只收 FAILED，两条路都接不着）。
        //     此处落 FAILED 即被既有恢复通道接住。用无取消令牌绕过已取消的请求 ct（客户端断连也能落库）。
        try
        {
            await _schedulingOrchestrator.RunSchedulingAndFinalizeAsync(
                result.NewPlanVersionId, result.NewScheduleRunId, strategyProfileVersionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "候选运行执行中断，落 FAILED 供恢复：ScheduleRunId={RunId} PlanVersionId={PlanVersionId}",
                result.NewScheduleRunId, result.NewPlanVersionId);
            await _scheduleRunService.FailAsync(result.NewScheduleRunId, 0, ex.Message, CancellationToken.None);
            throw;
        }

        return result;
    }

    /// <summary>
    /// 装配 BusinessScopeDto（真空④契约定案 §1.3 维度映射，3号位 生成权）。
    /// 经 <see cref="IDataScopeService.ResolveScopeAsync"/> 解析 actor 有效业务范围，依 Trigger 判定操作类型维度：
    /// 订单类（NEW_ORDER_CTP / NEW_ORDER_IMPACT / NEW_ORDER_INSERT / EXISTING_ORDER_ADVANCE）= ProductFamily（产品族 PMC）；
    /// Task 局部（GANTT_ADJUSTMENT / DOMAIN_MANUAL_RESCHEDULE）= ResourceOrgGroup（一般计划员）；
    /// 中断级（EQUIPMENT_FAILURE / RESOURCE_CALENDAR_CHANGE）= Global（不涉用户业务范围）。
    /// Global 放行 → IsGlobal=true；全局范围扩展解析出的维度键集合 → ScopeType/ScopeValues（业务键字符串，与 EnsureInScopeAsync 的 value 同口径）；
    /// 无任何授权（DataScopeContext.Empty）→ IsGlobal=false + 空集（按无授权 fail-safe，交 1号位 范围不足返回）。
    /// </summary>
    private async Task<BusinessScopeDto> BuildBusinessScopeAsync(int actorUserId, BusinessTriggerType trigger, CancellationToken ct)
    {
        var scopeType = trigger switch
        {
            BusinessTriggerType.NewOrderCtp or BusinessTriggerType.NewOrderImpact
                or BusinessTriggerType.NewOrderInsert or BusinessTriggerType.ExistingOrderAdvance => DataScopeTypes.ProductFamily,
            BusinessTriggerType.GanttAdjustment or BusinessTriggerType.DomainManualReschedule => DataScopeTypes.ResourceOrgGroup,
            _ => DataScopeTypes.Global,
        };

        var ctx = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);

        if (scopeType == DataScopeTypes.Global || ctx.IsGlobal)
        {
            return new BusinessScopeDto { IsGlobal = true, ScopeType = scopeType };
        }

        var values = ctx.GetValues(scopeType);
        return new BusinessScopeDto
        {
            IsGlobal = false,
            ScopeType = scopeType,
            ScopeValues = values is null ? Array.Empty<string>() : values.ToArray(),
        };
    }

    /// <summary>Run 引用追溯（P0-08）：ScheduleRun → 策略包版本 → 规则集/参数集版本 + 关联 PlanVersion 状态与结果</summary>
    public async Task<RunReferenceTrace> GetRunReferenceTraceAsync(int scheduleRunId, CancellationToken ct = default)
    {
        var run = await _scheduleRunRepo.GetByIdAsync(scheduleRunId, ct)
            ?? throw new InvalidOperationException($"ScheduleRun 不存在：{scheduleRunId}");

        long? strategyProfileVersionId = run.StrategyProfileVersionId;
        string? strategyProfileVersionCode = null;
        long? ruleSetVersionId = null;
        string? ruleSetVersionCode = null;
        long? parameterSetVersionId = null;
        string? parameterSetVersionCode = null;

        if (strategyProfileVersionId.HasValue)
        {
            var spv = await _strategyProfileVersionRepo.GetByIdAsync(strategyProfileVersionId.Value, ct);
            if (spv != null)
            {
                strategyProfileVersionCode = spv.VersionCode;
                ruleSetVersionId = spv.RuleSetVersionId;
                parameterSetVersionId = spv.ParameterSetVersionId;

                if (ruleSetVersionId.HasValue)
                {
                    var ruleSet = await _ruleSetVersionRepo.GetByIdAsync(ruleSetVersionId.Value, ct);
                    ruleSetVersionCode = ruleSet?.VersionCode;
                }

                if (parameterSetVersionId.HasValue)
                {
                    var parameterSet = await _parameterSetVersionRepo.GetByIdAsync(parameterSetVersionId.Value, ct);
                    parameterSetVersionCode = parameterSet?.VersionCode;
                }
            }
        }

        var planVersion = await _planVersionRepo.GetLatestByScheduleRunIdAsync(scheduleRunId, ct);

        return new RunReferenceTrace
        {
            ScheduleRunId = run.Id,
            RunType = run.RunType,
            Status = run.Status,
            StrategyProfileVersionId = strategyProfileVersionId,
            StrategyProfileVersionCode = strategyProfileVersionCode,
            RuleSetVersionId = ruleSetVersionId,
            RuleSetVersionCode = ruleSetVersionCode,
            ParameterSetVersionId = parameterSetVersionId,
            ParameterSetVersionCode = parameterSetVersionCode,
            ExpectedDomainKeysJson = run.ExpectedDomainKeysJson,
            PlanVersionId = planVersion?.Id ?? 0,
            PlanVersionStatus = planVersion?.Status,
            DataCutoffTime = run.DataCutoffTime,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            ErrorMessage = run.ErrorMessage,
        };
    }

    /// <summary>
    /// Run 域级状态汇总（G8：3号位文档 §十六 FULL 失败链）。
    /// 判定依据：ExpectedDomainKeysJson（预期 Domain 集） + 该 Run 的 PlanVersion 集合。
    /// 被阻断语义：Run 已终态且存在 FAILED 域时，无 PlanVersion 的预期域标记 BLOCKED（非根因），
    /// 满足"上游失败 → 下游本次不得发布新 ACTIVE → 展示被阻断原因所需元数据"。
    /// </summary>
    public async Task<IReadOnlyList<RunDomainStatusDto>> GetRunDomainStatusAsync(int scheduleRunId, CancellationToken ct = default)
    {
        var run = await _scheduleRunRepo.GetByIdAsync(scheduleRunId, ct)
            ?? throw new InvalidOperationException($"ScheduleRun 不存在：{scheduleRunId}");

        var domainKeys = ParseExpectedDomainKeys(run.ExpectedDomainKeysJson, scheduleRunId);
        var planVersions = await _planVersionRepo.GetByScheduleRunIdAsync(scheduleRunId, ct);

        var hasFailedDomain = planVersions.Any(pv => pv.Status == PlanVersionFailedStatus);
        var isTerminal = run.Status is ScheduleRunCompletedStatus
            or ScheduleRunPartialSuccessStatus
            or ScheduleRunFailedStatus;

        var result = new List<RunDomainStatusDto>(domainKeys.Count);
        foreach (var domainKey in domainKeys)
        {
            var pv = planVersions.FirstOrDefault(p => string.Equals(p.DomainKey, domainKey, StringComparison.Ordinal));

            var dto = new RunDomainStatusDto
            {
                DomainKey = domainKey,
                StartedAt = run.StartedAt,
            };

            if (pv == null)
            {
                if (isTerminal && hasFailedDomain)
                {
                    dto.Status = RunDomainBlockedStatus;
                    dto.Reason = "上游 Domain 失败，本次未生成 PlanVersion（被阻断）";
                }
                else if (isTerminal)
                {
                    dto.Status = RunDomainNotStartedStatus;
                }
                else
                {
                    dto.Status = RunDomainRunningStatus;
                }

                result.Add(dto);
                continue;
            }

            dto.PlanVersionId = pv.Id;
            dto.PlanVersionCode = pv.VersionCode;
            dto.ComputedAt = pv.ComputedAt;
            dto.ActivatedAt = pv.ActivatedAt;

            // 口径（2026-10-15，B + 词表对齐）：**只看 Status，不再拿 VersionCategory 兜底**。
            //   · 原 bug：先判 VersionCategory=='CANDIDATE'，而每一行已采用的 VersionCategory 恒为 CANDIDATE
            //     ⇒ 「ACTIVE ⇒ COMPLETED」分支永不可达，激活后域级视图永远显示「候选待确认」。
            //   · 词表对齐后 Status 自己就能表达「算完待采用」（CANDIDATE），兜底不仅多余，
            //     还会把夜间「算完但尚未采用」的 DAILY_BASELINE 误判成候选 —— 故整条删掉。
            //   · 身份（VersionCategory）仍用于激活门禁 EnsureCandidateConfirmable，只是不再参与本判定。
            dto.Status = pv.Status switch
            {
                // 已正式采用（ACTIVE）或被同域新版本顶替（ARCHIVED）：该域本次已完成并落定
                PlanVersionActiveStatus or PlanVersionArchivedStatus => RunDomainCompletedStatus,
                // 域级计算失败（与 SchedulingOrchestrator 的失败写点同值）
                PlanVersionFailedStatus => RunDomainFailedStatus,
                // 算完但尚未采用 ⇒ 待人工确认
                PlanVersionReadyStatus => RunDomainCandidateStatus,
                // 其余（BUILDING 待领 / Computing 在算）：仍在进行
                _ => RunDomainRunningStatus,
            };

            if (pv.Status == PlanVersionFailedStatus)
            {
                dto.Reason = string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "域级计算失败"
                    : run.ErrorMessage;
            }

            result.Add(dto);
        }

        return result;
    }

    /// <summary>解析 ExpectedDomainKeysJson 为 DomainKey 有序列表（非空元素；损坏/缺失抛异常，不静默降级）</summary>
    private static List<string> ParseExpectedDomainKeys(string? json, int scheduleRunId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"ScheduleRun {scheduleRunId} 的 ExpectedDomainKeysJson 为空/缺失");
        }

        try
        {
            var domains = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"ScheduleRun {scheduleRunId} 的 ExpectedDomainKeysJson 不是合法 JSON 数组：{ex.Message}", ex);
        }
    }
}
