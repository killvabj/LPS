using LPS.APS.Core.Authorization;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 部门维度业务范围守卫实现（队列3，3号位 治理域）
/// 判定链：D=∅ 放行 → 无 Run 跳过（手动/联调）→ 读冻结 Run 取主体 → 系统主体不参与 →
///         按登录名解析范围 → Global 放行 → Department 维度未配（范围C）放行 → Id→DeptCode 后判 S∩D≠∅。
/// </summary>
/// <remarks>
/// 边界（钉死，改动须先改契约）：
/// 1) 主体<b>只认 ScheduleRun.TriggeredBy</b>（登录名），不读 HTTP/请求上下文 ⇒ <b>同一 Run 内</b>判定结论恒定；
///    恢复＝新建 Run（不是重跑同一 Run），其 TriggeredBy 写<b>恢复人登录名</b>（不再写死 'Recover'），故不构成主体断链；
/// 2) 该维度未配置 ≠ 拒绝（范围C）；配置了但主档解析不出 Code ⇒ fail-closed 拒绝（不放过无法证明的值）；
/// 3) 「按 Id 解析失败」与「按 Code 不命中」分开记日志，便于定位是主档缺行还是授权值不同源；
/// 4) 不新增表/状态值/审计表，不写库，不扩 IMasterDataLookupRepository 接口。
/// </remarks>
public sealed class DepartmentScopeGuard : IDepartmentScopeGuard, IDomainBoundaryScopeGuard
{
    /// <summary>越界固定码（编排层/前端按码区分，不依赖文案）</summary>
    public const string ViolationCode = "DEPT_SCOPE_VIOLATION";

    /// <summary>主体无法解析固定码</summary>
    public const string ActorUnresolvedCode = "DEPT_SCOPE_ACTOR_UNRESOLVED";

    /// <summary>部门<b>边界</b>越界固定码（边界层＝全集子集，与入口闸的交集码区分）</summary>
    public const string DepartmentBoundaryViolationCode = "DEPT_SCOPE_BOUNDARY_VIOLATION";

    /// <summary>工厂<b>边界</b>越界固定码</summary>
    public const string FactoryBoundaryViolationCode = "FACTORY_SCOPE_BOUNDARY_VIOLATION";

    /// <summary>系统主体取值（ScheduleRun.TriggeredBy 的非用户来源）</summary>
    private static readonly HashSet<string> SystemSubjects =
        new(StringComparer.OrdinalIgnoreCase) { "Hangfire", "API", "Agent" };

    private readonly IScheduleRunRepository _scheduleRunRepository;
    private readonly IDataScopeService _dataScopeService;
    private readonly IMasterDataLookupRepository _masterDataLookup;
    private readonly ILogger<DepartmentScopeGuard> _logger;

    /// <summary>构造</summary>
    public DepartmentScopeGuard(
        IScheduleRunRepository scheduleRunRepository,
        IDataScopeService dataScopeService,
        IMasterDataLookupRepository masterDataLookup,
        ILogger<DepartmentScopeGuard> logger)
    {
        _scheduleRunRepository = scheduleRunRepository ?? throw new ArgumentNullException(nameof(scheduleRunRepository));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _masterDataLookup = masterDataLookup ?? throw new ArgumentNullException(nameof(masterDataLookup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EnsureAsync(
        long? scheduleRunId,
        string domainKey,
        IReadOnlyCollection<int> departmentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departmentIds);

        // ① D=∅：本域没有任何部门会被排产 ⇒ 用户碰不到未授权部门，不是越权（不查 Run，省一次往返）
        if (departmentIds.Count == 0)
        {
            _logger.LogInformation(
                "[部门范围] Domain={DomainKey} D=0（本域无部门可碰），按范围C放行，不出越权码",
                domainKey);
            return;
        }

        // ② 无 Run（手动/联调入口）：没有冻结主体，也就没有 TriggeredBy 可判 ⇒ 跳过并留痕。
        //    取值 null 与 0 同义：0 是本仓既有成文哨兵（手动/联调链路 ScheduleRunId:0，见
        //    SchedulingOrchestrator.ExecuteDomainAsync 调用点），若不归一会把「无 Run」误判成
        //    主体未解析 ⇒ 手动/单域跑批一接 seam 就整域 FAILED（与 'Recover' 同类缺陷）。
        if (scheduleRunId is null || scheduleRunId.Value == 0)
        {
            _logger.LogInformation(
                "[部门范围] Domain={DomainKey} ScheduleRunId={RunId}（null/0＝手动/联调入口，无冻结主体），跳过 Department 维度判定；" +
                "该入口准入由功能权限码把关，不计入已 enforce",
                domainKey, scheduleRunId);
            return;
        }

        // 负值/超界不是「无 Run」哨兵，属调用方缺陷：静默跳过会掩盖缺陷 ⇒ fail-closed（不放过无法证明的值）
        if (scheduleRunId.Value < 0 || scheduleRunId.Value > int.MaxValue)
            throw BuildActorUnresolved($"ScheduleRunId={scheduleRunId.Value} 非法（非 0 哨兵，越出 int 主键值域）");

        var run = await _scheduleRunRepository.GetByIdAsync((int)scheduleRunId.Value, cancellationToken);
        if (run is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 不存在，无法解析部门范围主体");

        var triggeredBy = run.TriggeredBy?.Trim() ?? string.Empty;

        // ③ 系统主体（夜间/接口/代理触发的全量排程）：没有真实用户 ⇒ Department 维度不参与（等同未配）
        if (SystemSubjects.Contains(triggeredBy) &&
            string.Equals(run.RunType, StrategyProfileRunType.FullSchedule, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "[部门范围] Run={RunId} RunType={RunType} 系统主体 TriggeredBy={TriggeredBy}，Department 维度不参与",
                scheduleRunId.Value, run.RunType, triggeredBy);
            return;
        }

        // ④ 主体只认冻结的 TriggeredBy（登录名）—— 不读请求上下文，保证同一 Run 重跑结论恒定
        var scope = await _dataScopeService.ResolveScopeByUserCodeAsync(triggeredBy, cancellationToken);
        if (scope is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 的 TriggeredBy='{triggeredBy}' 无法解析为有效用户");

        if (scope.IsGlobal)
            return;

        // ⑤ 范围C：该维度整体未配置 ⇒ 不构成拒绝理由。
        //    授权值两侧 Trim 归一（库值可能带前后空格，直接比对会恒判不中）；纯空白值视同未配置。
        var allowedCodes = scope.GetValues(DataScopeTypes.Department);
        var allowedNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedCodes is not null)
        {
            foreach (var code in allowedCodes)
            {
                if (!string.IsNullOrWhiteSpace(code)) allowedNormalized.Add(code.Trim());
            }
        }

        if (allowedNormalized.Count == 0)
        {
            _logger.LogDebug(
                "[部门范围] User={User} 未配置 Department 维度，按范围C不构成约束（Domain={DomainKey}）",
                triggeredBy, domainKey);
            return;
        }

        // ⑥ Id→Code：两侧值域不同（D 是 int Id，S 是 DeptCode），经既有主档转换；
        //    单次调用内 memo（同域 Id 上限 166），不扩 IMasterDataLookupRepository 接口
        var codeCache = new Dictionary<int, string?>();
        var resolvedCount = 0;
        var unresolvableIds = new List<int>();

        foreach (var departmentId in departmentIds)
        {
            if (!codeCache.TryGetValue(departmentId, out var deptCode))
            {
                deptCode = await _masterDataLookup.GetDepartmentCodeAsync(departmentId, cancellationToken);
                codeCache[departmentId] = deptCode;
            }

            if (string.IsNullOrWhiteSpace(deptCode))
            {
                unresolvableIds.Add(departmentId);
                continue;
            }

            resolvedCount++;
            if (allowedNormalized.Contains(deptCode.Trim()))
            {
                _logger.LogInformation(
                    "[部门范围] Domain={DomainKey} 命中授权部门 Code={DeptCode}（Run={RunId}, User={User}），放行",
                    domainKey, deptCode, scheduleRunId.Value, triggeredBy);
                return;
            }
        }

        // ⑦ 未命中：两类失败分开记（主档缺行 vs 授权值不同源），统一 fail-closed
        _logger.LogWarning(
            "[部门范围] Domain={DomainKey} 未命中：D={DCount}，按Id解析失败={UnresolvedCount}（Id=[{UnresolvedIds}]），" +
            "按Code不命中={MissCount}，授权Code数={AllowedCount}（Run={RunId}, User={User}）",
            domainKey, departmentIds.Count, unresolvableIds.Count, string.Join(",", unresolvableIds),
            resolvedCount, allowedNormalized.Count, scheduleRunId.Value, triggeredBy);

        throw new ScopeViolationException(
            DataScopeTypes.Department,
            unresolvableIds.Count > 0
                ? $"Domain={domainKey} 有 {unresolvableIds.Count}/{departmentIds.Count} 个部门 Id 无法解析为 DeptCode（fail-closed）"
                : $"Domain={domainKey} 本域 {departmentIds.Count} 个部门均不在授权范围",
            ViolationCode);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 边界层与入口闸（<see cref="EnsureAsync"/>）<b>同时点、不同判据</b>：
    /// 入口闸判交集（命中任一即进），本层判<b>全集子集</b>（每一条都必须命中），任一越界整轮中止。
    /// 主体解析、哨兵、系统主体、范围C、Global 五段语义与入口闸同源，故此处重复实现而不重构既有路径
    /// （既有 <see cref="EnsureAsync"/> 已被单测与 2号位 seam 覆盖，改动风险大于重复成本）。
    /// </remarks>
    public async Task EnsureBoundaryAsync(
        long? scheduleRunId,
        string domainKey,
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> factoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departmentIds);
        ArgumentNullException.ThrowIfNull(factoryIds);

        // ① 本域无任何实体可碰 ⇒ 不越权，直接放行（与入口闸 D=∅ 同解）
        if (departmentIds.Count == 0 && factoryIds.Count == 0)
        {
            _logger.LogDebug(
                "[业务范围边界] Domain={DomainKey} D=0、F=0（本域无实体可碰），放行",
                domainKey);
            return;
        }

        // ② 无 Run（null/0 哨兵＝手动/联调入口）：无冻结主体可判 ⇒ 跳过并留痕，不冒充已 enforce
        if (scheduleRunId is null || scheduleRunId.Value == 0)
        {
            _logger.LogInformation(
                "[业务范围边界] Domain={DomainKey} ScheduleRunId={RunId}（null/0＝手动/联调入口，无冻结主体），跳过边界校验",
                domainKey, scheduleRunId);
            return;
        }

        if (scheduleRunId.Value < 0 || scheduleRunId.Value > int.MaxValue)
            throw BuildActorUnresolved($"ScheduleRunId={scheduleRunId.Value} 非法（非 0 哨兵，越出 int 主键值域）");

        var run = await _scheduleRunRepository.GetByIdAsync((int)scheduleRunId.Value, cancellationToken);
        if (run is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 不存在，无法解析业务范围主体");

        var triggeredBy = run.TriggeredBy?.Trim() ?? string.Empty;

        // ③ 系统主体（夜间/接口/代理触发的全量排程）：没有真实用户 ⇒ 该维度不参与（等同未配）
        if (SystemSubjects.Contains(triggeredBy) &&
            string.Equals(run.RunType, StrategyProfileRunType.FullSchedule, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "[业务范围边界] Run={RunId} RunType={RunType} 系统主体 TriggeredBy={TriggeredBy}，边界校验不参与",
                scheduleRunId.Value, run.RunType, triggeredBy);
            return;
        }

        var scope = await _dataScopeService.ResolveScopeByUserCodeAsync(triggeredBy, cancellationToken);
        if (scope is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 的 TriggeredBy='{triggeredBy}' 无法解析为有效用户");

        if (scope.IsGlobal)
            return;

        await EnsureSubsetInScopeAsync(
            scope, DataScopeTypes.Department, departmentIds,
            (id, ct) => _masterDataLookup.GetDepartmentCodeAsync(id, ct),
            DepartmentBoundaryViolationCode, domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);

        await EnsureSubsetInScopeAsync(
            scope, DataScopeTypes.Factory, factoryIds,
            (id, ct) => _masterDataLookup.GetFactoryCodeAsync(id, ct),
            FactoryBoundaryViolationCode, domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 判据与 <see cref="EnsureBoundaryAsync"/> <b>完全相同</b>（全集子集、fail-closed、范围C、Global、无 Run 跳过）；
    /// 差别只在<b>输入形状</b>——按用途拆成「需求归属／可写生产／采购只读」三组，且带时点（<see cref="ScopeCheckPhase"/>）。
    /// <para>
    /// <b>为什么不拍平成一个集</b>：拍平后同一个域被调两次也无法区分是需求厂越界还是生产厂越界，
    /// 「采购只读厂」还会丢掉整个维度——这正是 0号位《01》`:16` 禁止的做法。
    /// </para>
    /// <para>
    /// <b>错误码不新增</b>（`01 :18`）：仍只用既有两个边界码与主体未解析码，用途写进异常文案与日志。
    /// 主体解析、哨兵、系统主体、范围C、Global 五段语义与 <see cref="EnsureBoundaryAsync"/> 同源，
    /// 按本类既有取舍重复实现而不重构已被单测覆盖的路径。
    /// </para>
    /// </remarks>
    public async Task EnsureBoundaryByPurposeAsync(
        long? scheduleRunId,
        string domainKey,
        ScopeCheckPhase phase,
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> demandFactoryIds,
        IReadOnlyCollection<int> writableFactoryIds,
        IReadOnlyCollection<int> readOnlySupplyFactoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departmentIds);
        ArgumentNullException.ThrowIfNull(demandFactoryIds);
        ArgumentNullException.ThrowIfNull(writableFactoryIds);
        ArgumentNullException.ThrowIfNull(readOnlySupplyFactoryIds);

        // ① 本域无任何实体可碰 ⇒ 不越权，直接放行
        if (departmentIds.Count == 0 && demandFactoryIds.Count == 0
            && writableFactoryIds.Count == 0 && readOnlySupplyFactoryIds.Count == 0)
        {
            _logger.LogDebug(
                "[业务范围边界] Domain={DomainKey} Phase={Phase} D=0、F=0（本域无实体可碰），放行",
                domainKey, phase);
            return;
        }

        // ② 无 Run（null/0 哨兵＝手动/联调入口）：无冻结主体可判 ⇒ 跳过并留痕，不冒充已 enforce
        if (scheduleRunId is null || scheduleRunId.Value == 0)
        {
            _logger.LogInformation(
                "[业务范围边界] Domain={DomainKey} Phase={Phase} ScheduleRunId={RunId}（null/0＝手动/联调入口，无冻结主体），跳过边界校验",
                domainKey, phase, scheduleRunId);
            return;
        }

        if (scheduleRunId.Value < 0 || scheduleRunId.Value > int.MaxValue)
            throw BuildActorUnresolved($"ScheduleRunId={scheduleRunId.Value} 非法（非 0 哨兵，越出 int 主键值域）");

        var run = await _scheduleRunRepository.GetByIdAsync((int)scheduleRunId.Value, cancellationToken);
        if (run is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 不存在，无法解析业务范围主体");

        var triggeredBy = run.TriggeredBy?.Trim() ?? string.Empty;

        // ③ 系统主体（夜间/接口/代理触发的全量排程）：没有真实用户 ⇒ 该维度不参与（等同未配）
        if (SystemSubjects.Contains(triggeredBy) &&
            string.Equals(run.RunType, StrategyProfileRunType.FullSchedule, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "[业务范围边界] Run={RunId} Phase={Phase} RunType={RunType} 系统主体 TriggeredBy={TriggeredBy}，边界校验不参与",
                scheduleRunId.Value, phase, run.RunType, triggeredBy);
            return;
        }

        var scope = await _dataScopeService.ResolveScopeByUserCodeAsync(triggeredBy, cancellationToken);
        if (scope is null)
            throw BuildActorUnresolved($"ScheduleRun={scheduleRunId.Value} 的 TriggeredBy='{triggeredBy}' 无法解析为有效用户");

        if (scope.IsGlobal)
            return;

        // ④ 四组逐项判定，组间不合并：部门 → 需求归属工厂 → 可写生产工厂 → 采购只读工厂
        await EnsureSubsetInScopeAsync(
            scope, DataScopeTypes.Department, departmentIds,
            (id, ct) => _masterDataLookup.GetDepartmentCodeAsync(id, ct),
            DepartmentBoundaryViolationCode, domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);

        await EnsureFactoryPurposeAsync(
            scope, "需求归属工厂", demandFactoryIds, phase,
            domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);

        await EnsureFactoryPurposeAsync(
            scope, "可写生产工厂", writableFactoryIds, phase,
            domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);

        await EnsureFactoryPurposeAsync(
            scope, "采购只读工厂", readOnlySupplyFactoryIds, phase,
            domainKey, scheduleRunId.Value, triggeredBy, cancellationToken);
    }

    /// <summary>
    /// 单组工厂用途的边界判定：复用同一套全集子集判据，只把<b>用途</b>与<b>时点</b>带进日志与异常文案。
    /// </summary>
    private Task EnsureFactoryPurposeAsync(
        DataScopeContext scope,
        string purpose,
        IReadOnlyCollection<int> factoryIds,
        ScopeCheckPhase phase,
        string domainKey,
        long runId,
        string triggeredBy,
        CancellationToken cancellationToken)
        => EnsureSubsetInScopeAsync(
            scope, DataScopeTypes.Factory, factoryIds,
            (id, ct) => _masterDataLookup.GetFactoryCodeAsync(id, ct),
            FactoryBoundaryViolationCode, domainKey, runId, triggeredBy, cancellationToken,
            purpose, phase);

    /// <summary>
    /// <b>全集子集</b>判定：目标集内每一个 Id 解析出的编码都必须落入授权集
    /// （与入口闸「命中任一即放行」相反）。维度整体未配 ⇒ 不参与（范围C）。
    /// </summary>
    private async Task EnsureSubsetInScopeAsync(
        DataScopeContext scope,
        string scopeType,
        IReadOnlyCollection<int> targetIds,
        Func<int, CancellationToken, Task<string?>> resolver,
        string violationCode,
        string domainKey,
        long runId,
        string triggeredBy,
        CancellationToken cancellationToken,
        string? purpose = null,
        ScopeCheckPhase? phase = null)
    {
        if (targetIds.Count == 0)
            return;

        // 范围C：该维度整体未配置（或全部为空白值）⇒ 不构成约束
        var allowedCodes = scope.GetValues(scopeType);
        var allowedNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedCodes is not null)
        {
            foreach (var code in allowedCodes)
            {
                if (!string.IsNullOrWhiteSpace(code)) allowedNormalized.Add(code.Trim());
            }
        }

        if (allowedNormalized.Count == 0)
            return;

        var outOfScope = new List<string>();
        var unresolvableIds = new List<int>();

        foreach (var targetId in targetIds)
        {
            var code = await resolver(targetId, cancellationToken);
            if (string.IsNullOrWhiteSpace(code))
            {
                unresolvableIds.Add(targetId);
                continue;
            }

            if (!allowedNormalized.Contains(code.Trim()))
                outOfScope.Add(code.Trim());
        }

        if (outOfScope.Count == 0 && unresolvableIds.Count == 0)
            return;

        // 用途/时点只进日志与文案，不改判定：无用途时退化为原 scopeType 文案（既有调用点零变化）
        var label = string.IsNullOrWhiteSpace(purpose) ? scopeType : $"{scopeType}／{purpose}";
        var phaseText = phase is null ? "-" : phase.Value.ToString();

        _logger.LogWarning(
            "[业务范围边界] Domain={DomainKey} {ScopeType} 越界：目标 {TargetCount} 个，" +
            "越界 {OutCount} 个（[{OutCodes}]），按Id解析失败 {UnresolvedCount} 个（[{UnresolvedIds}]），" +
            "授权 {AllowedCount} 个（Run={RunId}, User={User}, Phase={Phase}）",
            domainKey, label, targetIds.Count,
            outOfScope.Count, string.Join(",", outOfScope),
            unresolvableIds.Count, string.Join(",", unresolvableIds),
            allowedNormalized.Count, runId, triggeredBy, phaseText);

        throw new ScopeViolationException(
            scopeType,
            unresolvableIds.Count > 0
                ? $"Domain={domainKey} 的 {label} 有 {unresolvableIds.Count}/{targetIds.Count} 个 Id 无法解析为编码（边界 fail-closed）"
                : $"Domain={domainKey} 的 {label} 有 {outOfScope.Count}/{targetIds.Count} 个实体不在授权范围（边界强校验：要求全部落入）",
            violationCode);
    }

    private static ScopeViolationException BuildActorUnresolved(string detail)
        => new(DataScopeTypes.Department, detail, ActorUnresolvedCode);
}
