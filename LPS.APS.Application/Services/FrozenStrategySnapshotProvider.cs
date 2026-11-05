using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Application.Models;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 冻结策略快照提供者实现（阶段 B-3：3号位 Application 装配）
/// 按已冻结的 StrategyProfileVersionId 一次装配完整 FrozenStrategySnapshot。
/// 契约语义（C2-1/C2-2）：2号位 Run 启动时按指定 VersionId 调用一次，本 Run 内存使用，不逐笔调用。
/// 装配流程：StrategyProfileVersion → RuleSetVersion + ParameterSetVersion → 六块 Snapshot。
/// </summary>
/// <remarks>
/// B-5 缓存（清单 #51 性能红线 / 契约 C2-4）：Snapshot 进程级缓存，一次装载后 Run 内不刷新、跨 Run 复用。
/// <para>
/// **B-6（2026-10-30，2号位）：缓存键 = (VersionId, 绑定内容指纹)**。原键只有 VersionId，其安全性依赖
/// 「PUBLISHED 版本业务内容不可变」——那只是**注释级假设**：<c>GovernanceVersionService.EnsureUpdatable</c>
/// 只在治理接口路径生效，直连 SQL 可原地改内容且版本号不变，现网已发生三次
/// （<c>AuditLog</c> 8391 / 8462 / 9486）⇒ 同进程内重取恒返回旧内容、静默无痕，须重启进程才生效。
/// 补上内容维度后，「内容变了」必然换键，**无需重启即可见新值**；VersionId 仍在键内，
/// 不同版本之间照样不污染。指纹＝库内 <c>HASHBYTES('SHA2_256', RSV 正文 ‖ PSV 正文)</c>，
/// 见 <see cref="ISnapshotContentFingerprintReader"/>——只回传 64 字节十六进制串，不回读正文，
/// 故命中路径仍只有一次极小的库内往返（不是每 Task，而是每次取快照一次）。
/// 同时改绑到不同子版本也会换指纹（指纹覆盖绑定的两个维度），不再需要靠 VersionId 变化来兜底。
/// </para>
/// <para>
/// 未注入 <see cref="ISnapshotContentFingerprintReader"/> 时（仅既有单测的裸 <c>new</c> 构造路径）退回
/// 旧语义（键 = VersionId），并打一次警告日志——生产 DI 恒注入，该回退不代表生产行为。
/// </para>
/// 开发者：3号位（B-5）／2号位（B-6 内容指纹）
/// </remarks>
public class FrozenStrategySnapshotProvider : IFrozenStrategySnapshotProvider
{
    /// <summary>
    /// B-5/B-6 快照缓存：key = (StrategyProfileVersionId, 绑定内容指纹)。
    /// 契约 C2-4 要求 Cache Key 必须含 VersionId（不同版本不污染）——VersionId 仍在键内；
    /// 新增的内容维度解决「同 VersionId 原地改内容」的静默陈旧（见类注释 B-6）。
    /// 值 = 装配完成后的 Snapshot JSON 快照（不可变字符串）——命中时反序列化重建独立对象，
    /// 杜绝共享可变 Block 实例被消费者就地修改后污染所有并发/后续 Run（code-review MEDIUM：不可变性）。
    /// </summary>
    internal static readonly ConcurrentDictionary<(long VersionId, string Fingerprint), string> Cache = new();

    /// <summary>
    /// 未注入指纹读取器时使用的占位指纹（仅既有单测的裸 <c>new</c> 构造路径会走到）。
    /// 用定值而非空串，是为了让「无读取器」与「读取器返回空」两种情形在键上可区分。
    /// </summary>
    private const string NoFingerprintReaderKey = "(no-fingerprint-reader)";

    /// <summary>回退旧语义的告警只打一次（避免每域/每 Run 刷日志）</summary>
    private static int _legacyKeyWarned;

    /// <summary>JSON 反序列化设置（与装配路径一致，PascalCase 属性名大小写不敏感）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// **仅 Stage 固定交接等待规则块使用**的反序列化选项：在通用 <see cref="JsonOptions"/> 之上放行「字符串枚举名」。
    ///
    /// 【为什么必须单列（2026-10-29 实读实证）】现网 PSV **2365** 顶层实际写入的是
    /// <c>"TimeBasis":"ELAPSED"</c>（**字符串**，用 <c>JSON_QUERY</c> 直读该行确认），
    /// 而通用 JsonOptions 不含 <c>JsonStringEnumConverter</c> ⇒ 直接反序列化抛
    /// <c>JsonException: The JSON value could not be converted to StageHandoffTimeBasis</c>，
    /// 被本方法包成「格式无效 / 子块装载失败」 ⇒ **唯一带真值的参数集会装载失败**，
    /// 表现为「补了装载反而把生产默认包打挂」（比不补更糟）。
    ///
    /// 只在**本块**放开，不改通用选项：六块既有载荷形态零影响（零回归）。
    /// 数值写法 <c>0/1</c> 同样接受（<see cref="JsonStringEnumConverter"/> 默认 <c>allowIntegerValues=true</c>）
    /// ⇒ 对历史上可能已存在的两种写法都宽容，只在**真正损坏**时抛。
    ///
    /// ⚠ 同源缺陷（**未修，归 3号位**）：发布链 <c>GovernanceVersionService.ValidateStageHandoffWaitRulesJson</c>
    /// （<c>:1246</c>）用同一份不含转换器的 JsonOptions 反序列化 ⇒ 以字符串枚举发布的版本必然报
    /// <c>INVALID_JSON</c>。即「3号位 自己写入的写法，过不了 3号位 自己的发布链」。已另行报告。
    /// </summary>
    private static readonly JsonSerializerOptions StageHandoffJsonOptions = new(JsonOptions)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepo;
    private readonly IRuleSetVersionRepository _ruleSetVersionRepo;
    private readonly IParameterSetVersionRepository _parameterSetVersionRepo;

    /// <summary>
    /// 可空 logger（2026-10-14 新增）：**可选可空**参数，`new` 调用方（含既有单测）不传即 null，
    /// 不破编译、不需改测试；生产 DI（Scrutor）会自动注入。
    /// </summary>
    private readonly ILogger<FrozenStrategySnapshotProvider>? _logger;

    /// <summary>
    /// B-6 内容指纹读取器（2026-10-30）：**可选可空**——理由同 <see cref="_logger"/>，
    /// 既有单测的裸 <c>new</c> 构造不传即 null，退回旧键语义；生产 DI 恒注入。
    /// </summary>
    private readonly ISnapshotContentFingerprintReader? _contentFingerprintReader;

    public FrozenStrategySnapshotProvider(
        IStrategyProfileVersionRepository strategyProfileVersionRepo,
        IRuleSetVersionRepository ruleSetVersionRepo,
        IParameterSetVersionRepository parameterSetVersionRepo,
        ILogger<FrozenStrategySnapshotProvider>? logger = null,
        ISnapshotContentFingerprintReader? contentFingerprintReader = null)
    {
        _strategyProfileVersionRepo = strategyProfileVersionRepo;
        _ruleSetVersionRepo = ruleSetVersionRepo;
        _parameterSetVersionRepo = parameterSetVersionRepo;
        _logger = logger;
        _contentFingerprintReader = contentFingerprintReader;
    }

    /// <summary>清空快照缓存（供测试隔离；生产代码不调用）</summary>
    internal static void ClearCache() => Cache.Clear();

    public async Task<FrozenStrategySnapshot> GetFrozenStrategySnapshotAsync(
        long strategyProfileVersionId,
        CancellationToken ct)
    {
        // B-5/B-6 缓存命中（契约 C2-4：Cache Key 必须含 VersionId；本键 = (VersionId, 内容指纹)）。
        // 指纹取不到（版本或其子版本缺失）时 cacheKey 为 null ⇒ 本次既不通读缓存、也不写缓存，
        // 直接走完整装载路径把真实错误抛出来（那里口径完整、带版本号）。
        // 命中时从 JSON 快照反序列化重建独立对象并刷新 FrozenAt（冻结时点=本次调用）——
        // 每次返回独立实例，消费者就地修改返回对象不会污染缓存（code-review MEDIUM：不可变性）。
        var fingerprint = await ResolveContentFingerprintAsync(strategyProfileVersionId, ct);
        (long VersionId, string Fingerprint)? cacheKey =
            fingerprint is null ? null : (strategyProfileVersionId, fingerprint);

        if (cacheKey is { } hitKey && Cache.TryGetValue(hitKey, out var cachedJson))
        {
            var cachedSnapshot = JsonSerializer.Deserialize<FrozenStrategySnapshot>(cachedJson, JsonOptions)
                ?? throw new InvalidOperationException($"策略包版本 {strategyProfileVersionId} 的缓存快照反序列化失败");
            cachedSnapshot.FrozenAt = DateTime.Now;
            return cachedSnapshot;
        }

        // 1. 获取策略包版本（含 RuleSetVersionId + ParameterSetVersionId）
        var strategyProfileVersion = await _strategyProfileVersionRepo.GetByIdAsync(strategyProfileVersionId, ct)
            ?? throw new InvalidOperationException($"策略包版本不存在：{strategyProfileVersionId}");

        // P0-03 收口：补父策略包版本装载前置校验——仅 PUBLISHED 可装载（新 Run 首次装载防御；
        // 历史重放命中 B-5 缓存路径不校验，进程重启后 miss 路径仅校验 PUBLISHED 不校验时间窗口，可重放历史不可变内容）
        if (strategyProfileVersion.Status != GovernanceVersionStatus.Published)
        {
            throw new InvalidOperationException($"策略包版本 {strategyProfileVersionId} 状态为 {strategyProfileVersion.Status}，非 PUBLISHED，Snapshot 装载失败");
        }

        // 2. 获取规则集版本和参数集版本
        var ruleSetVersion = await _ruleSetVersionRepo.GetByIdAsync(strategyProfileVersion.RuleSetVersionId, ct)
            ?? throw new InvalidOperationException($"规则集版本不存在：{strategyProfileVersion.RuleSetVersionId}");

        var parameterSetVersion = await _parameterSetVersionRepo.GetByIdAsync(strategyProfileVersion.ParameterSetVersionId, ct)
            ?? throw new InvalidOperationException($"参数集版本不存在：{strategyProfileVersion.ParameterSetVersionId}");

        // 3. P1-01 防御：仅 PUBLISHED 且处于有效区间内的版本可装载（防止 Run 装载未发布/失效版本）
        EnsureLoadable(ruleSetVersion, strategyProfileVersionId, "规则集");
        EnsureLoadable(parameterSetVersion, strategyProfileVersionId, "参数集");

        // 4. 装配 Snapshot（六块 + 三 VersionId 元数据）
        // 方案 A（0号位 §7.2）：六块统一从 ContentSnapshotJson 结构化子块反序列化（契约 §6.10.5）。
        // 缺失/损坏一律装载失败（P0-02 六块统一），不静默回退空 Block——
        // 避免"数据库显示本 Run 使用某版本、程序却按空规则执行"的版本追溯失真。
        var snapshot = new FrozenStrategySnapshot
        {
            StrategyProfileVersionId = strategyProfileVersionId,
            RuleSetVersionId = ruleSetVersion.Id,
            ParameterSetVersionId = parameterSetVersion.Id,
            FrozenAt = DateTime.Now,

            // ① Demand Priority（来自 RuleSetVersion.ContentSnapshotJson.DemandPriority 子块）
            DemandPriority = DeserializeRuleSetBlock(ruleSetVersion, strategyProfileVersionId),

            // ②~④ 来自 ParameterSetVersion.ContentSnapshotJson 子块
            Lock = DeserializeParameterSetBlock<LockBlock>(parameterSetVersion, "Lock", strategyProfileVersionId),
            Supply = DeserializeParameterSetBlock<SupplyBlock>(parameterSetVersion, "Supply", strategyProfileVersionId),
            Procurement = DeserializeParameterSetBlock<ProcurementBlock>(parameterSetVersion, "Procurement", strategyProfileVersionId),

            // ⑤ Solver Strategy（P0-02 收口：真实来源，替代原空对象）
            SolverStrategy = DeserializeParameterSetBlock<SolverStrategyBlock>(parameterSetVersion, "SolverStrategy", strategyProfileVersionId),

            // ⑥ Candidate Guardrail（P0-02 收口：真实来源，替代原空对象）
            CandidateGuardrail = DeserializeParameterSetBlock<CandidateGuardrailBlock>(parameterSetVersion, "CandidateGuardrail", strategyProfileVersionId),

            // ⑦ Setup 换型规则（重构方案 S-4：RuleSetVersion.ContentSnapshotJson.SetupTransitionRules 子块 → 第⑦块）
            // 原由 PeggingOrchestrator.LoadSetupTransitionRulesAsync 从独立物理表装载；重构后随六块统一经快照通道。
            // 缺子块/为空 = 无 Setup 规则（消费端三层命中按 0 分钟 + 追踪兜底，与旧空行语义一致，**损坏除外**）。
            // 2026-10-14：JSON 损坏改为 fail-fast 抛异常（原 fail-open 会把「数据事故」静默降级成「合法缺配」，
            // 见 SetupTransitionRuleProjector.ExtractRules 注释）；
            SetupTransitionRules = DeserializeSetupTransitionRules(ruleSetVersion),

            // Stage 固定交接等待规则（合同 v1.1 §四/§五；独立顶层子块，**不属六块**）。
            // 2026-10-29 落：此前本 Provider 从不按该键取值 ⇒ `FrozenStrategySnapshot.StageHandoffWaitRules`
            //   恒为默认空 List（miss 建空、`:Cache` 序列化空、hit 反序列化回的也是空）⇒ PSV 2365 已注入的
            //   37 行真值**一次都进不来**。断点由 1号位 2026-10-28《消费侧回执》§1.2 定位、其 §三 第 1 项点名我方补。
            // ⚠ 语义与六块**刻意不同**，见 DeserializeStageHandoffWaitRules 方法注释（缺块 = 无规则，不是装载失败）。
            StageHandoffWaitRules = DeserializeStageHandoffWaitRules(parameterSetVersion),

            // ⑧ 批量策略 Batch Policy（2026-10-17 装载侧表直读切换；PM 2026-10-15 §T-1038）：
            //   **本字段不再由本 Provider 填充** —— 发布侧自 2026-10-15 起停止向
            //   `ParameterSetVersion.ContentSnapshotJson.BatchPolicy` 注入，Batch 的现时唯一治理源
            //   为 `TaskSplitRuleConfig` 表，由 `PeggingOrchestrator.LoadBatchPolicyRulesAsync`
            //   按 **Domain 求解前**直读（生效时点 = `DomainBatchEvaluationTime`）。
            //   ⇒ 保持空集合是**终态契约**而非「缺配」：字段保留只为不改 `FrozenStrategySnapshot` 形态与
            //   1号位 消费侧签名（其只读 `request.StrategySnapshot.BatchPolicies / BatchPolicyStates`）。
            //   ⚠ 由此本快照**不再承载 ⑧ 块**，进程级缓存也不再锁住 Batch 内容（「维护即生效」成立）。
            BatchPolicy = []
        };

        // B-5：写入缓存（失败路径已在上述反序列化抛异常退出，不会写入坏快照）。
        // 序列化为 JSON 字符串快照（不可变）；命中路径反序列化重建，FrozenAt 在命中处覆盖为本次时点。
        // B-6：按 (VersionId, 指纹) 落库，并顺带清掉同 VersionId 的其它指纹条目——
        // 同版本内容每变一次就多一个键，不清会让字典随「原地改次数 × 版本数」无界增长。
        if (cacheKey is { } storeKey)
        {
            StoreCacheEntry(storeKey, JsonSerializer.Serialize(snapshot));
        }

        return snapshot;
    }

    /// <summary>
    /// 取本策略包版本当前的内容指纹（B-6）。
    /// 未注入读取器 ⇒ 退回旧的「仅 VersionId」键语义（<see cref="NoFingerprintReaderKey"/> 占位），
    /// 并打一次警告：生产 DI 恒注入，该回退只应出现在裸 <c>new</c> 构造的测试里。
    /// </summary>
    private async Task<string?> ResolveContentFingerprintAsync(long strategyProfileVersionId, CancellationToken ct)
    {
        if (_contentFingerprintReader is null)
        {
            if (Interlocked.Exchange(ref _legacyKeyWarned, 1) == 0 && _logger is not null)
            {
                _logger.LogWarning(
                    "未注入 {Reader}：快照缓存退回「仅按 VersionId」的旧键语义——" +
                    "同一 VersionId 原地改内容后，本进程内重取仍返回旧值（须重启方可生效）。生产 DI 不应出现此情形。",
                    nameof(ISnapshotContentFingerprintReader));
            }

            return NoFingerprintReaderKey;
        }

        try
        {
            return await _contentFingerprintReader.GetStrategyProfileContentFingerprintAsync(strategyProfileVersionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取指纹是**新引入的一次库往返**，不能让它成为新的全链失败点：
            // 失败时返回 null ⇒ 本次不读缓存、也不写缓存（照样完整装载，只是不复用）。
            // 取向刻意是「宁可慢、不可陈旧」，也**不是**退回旧键——旧键正是要修的静默陈旧。
            _logger?.LogWarning(ex,
                "策略包版本 {StrategyProfileVersionId} 取内容指纹失败，本次跳过快照缓存（功能不受影响，仅不复用）。",
                strategyProfileVersionId);
            return null;
        }
    }

    /// <summary>
    /// 写入缓存并清掉同 VersionId 下其它指纹的条目（B-6 有界性）：
    /// 同版本内容每变一次就换一个键，不清理会让字典随「原地改次数 × 版本数」无界增长。
    /// 只清同 VersionId，不动其它版本（契约 C2-4：不同版本不互相污染）。
    /// </summary>
    private static void StoreCacheEntry((long VersionId, string Fingerprint) key, string json)
    {
        var stale = Cache.Keys
            .Where(k => k.VersionId == key.VersionId && !string.Equals(k.Fingerprint, key.Fingerprint, StringComparison.Ordinal))
            .ToList();

        foreach (var staleKey in stale)
        {
            Cache.TryRemove(staleKey, out _);
        }

        Cache[key] = json;
    }

    /// <summary>
    /// 装载前置校验（P0-03 收口）：仅 PUBLISHED 版本可装载。
    /// 语义：Provider 只读历史不可变内容（契约 §6.10.5）——移除 EffectiveFrom/EffectiveTo 当前时间窗口检查，
    /// 该职责归新 Run 准入（ResolveDefaultStrategyProfileVersionAsync 已按生效窗口过滤默认版本）；
    /// 历史版本按显式 VersionId 重放不受当前时间影响。非 PUBLISHED 拒绝（未发布内容不可作为 Run 冻结来源）。
    /// </summary>
    private static void EnsureLoadable(
        LPS.APS.Core.Entities.APS.RuleSetVersion version,
        long strategyProfileVersionId,
        string kind)
    {
        if (version.Status != GovernanceVersionStatus.Published)
        {
            throw new InvalidOperationException($"策略包版本 {strategyProfileVersionId} 的{kind}版本状态为 {version.Status}，非 PUBLISHED，Snapshot 装载失败");
        }
    }

    /// <summary>
    /// 装载前置校验（P0-03 收口）：仅 PUBLISHED 版本可装载（同 RuleSet 语义）。
    /// 移除 EffectiveFrom/EffectiveTo 当前时间窗口检查——历史版本按显式 VersionId 重放不受当前时间影响。
    /// </summary>
    private static void EnsureLoadable(
        LPS.APS.Core.Entities.APS.ParameterSetVersion version,
        long strategyProfileVersionId,
        string kind)
    {
        if (version.Status != GovernanceVersionStatus.Published)
        {
            throw new InvalidOperationException($"策略包版本 {strategyProfileVersionId} 的{kind}版本状态为 {version.Status}，非 PUBLISHED，Snapshot 装载失败");
        }
    }

    /// <summary>从 RuleSetVersion.ContentSnapshotJson 反序列化 DemandPriority 子块（P0-02 六块统一失败）</summary>
    private DemandPriorityBlock DeserializeRuleSetBlock(
        LPS.APS.Core.Entities.APS.RuleSetVersion version,
        long strategyProfileVersionId)
    {
        if (string.IsNullOrWhiteSpace(version.ContentSnapshotJson))
        {
            throw new InvalidOperationException($"规则集版本 {version.Id} 的 ContentSnapshotJson 为空/缺失，Snapshot 装载失败");
        }

        try
        {
            using var doc = JsonDocument.Parse(version.ContentSnapshotJson);
            if (!doc.RootElement.TryGetProperty("DemandPriority", out var blockElement))
            {
                throw new InvalidOperationException($"规则集版本 {version.Id} 的 ContentSnapshotJson 缺少 DemandPriority 子块，Snapshot 装载失败");
            }

            return blockElement.Deserialize<DemandPriorityBlock>(JsonOptions)
                ?? throw new InvalidOperationException($"规则集版本 {version.Id} 的 DemandPriority 子块反序列化结果为空，Snapshot 装载失败");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"规则集版本 {version.Id} 的 ContentSnapshotJson 格式无效，Snapshot 装载失败", ex);
        }
    }

    /// <summary>
    /// 从 RuleSetVersion.ContentSnapshotJson 反序列化 SetupTransitionRules 子块 → 第⑦块快照。
    /// 语义（2026-10-14 起与 ⑧ 块**同轨**）：**子块缺失/null/显式空配置 → 空列表**（Setup 为非必填块，
    /// 版本可不配置换型规则 = 无规则 DEFAULT 兜底）；**JSON 损坏 → 抛异常**（装载失败，不再静默空列表）。
    /// 投影复用 <see cref="SetupTransitionRuleProjector.ProjectFromSnapshot"/>（S-8：并入反序列化，筛 IsActive=true、去审计字段）。
    /// </summary>
    private List<SetupTransitionRuleSnapshot> DeserializeSetupTransitionRules(
        LPS.APS.Core.Entities.APS.RuleSetVersion version)
    {
        return SetupTransitionRuleProjector.ProjectFromSnapshot(version.ContentSnapshotJson);
    }

    /// <summary>
    /// 从 <c>ParameterSetVersion.ContentSnapshotJson</c> 反序列化**顶层** Stage 固定交接等待子块
    /// （合同 v1.1 §四/§五；唯一物理路径 <c>$.StageHandoffWaitRules</c>，**非** <c>$.SolverStrategy.StageHandoffWaitRules</c>）。
    ///
    /// **为什么不复用 <see cref="DeserializeParameterSetBlock{TBlock}"/>（禁止合并）**：
    ///   六块 = P0-02 fail-fast（缺块即装载失败，防「库里显示用了某版本、程序按空规则跑」的版本追溯失真）；
    ///   本块 = H-3「无源不阻断」（键缺失 / 显式 null ⇒ **空列表 = 无交接等待规则**，全 36 对不默认填充）；
    ///   但**空数组 `[]` 不属于「无源」**——它恒为「配了但零行」的非法形态，装载期 fail-closed（契约 C9，3号位 2026-10-29 裁决）。
    ///   现网绝大多数 PSV 根本没有这个块 ⇒ 套六块写法会让**除 2365 外的所有 PSV 装载失败**。
    /// **JSON 损坏仍抛**（数据事故不许静默降级成「合法缺配」，与第⑦块 2026-10-14 定下的纪律一致）。
    ///
    /// 装载侧**不筛 <c>IsActive</c>、不按厂裁剪**：全量原样透传。匹配口径（<c>IsActive==true</c> + 四元组全等）
    /// 归 1号位 消费侧；装载侧先筛行会让「规则存在但未命中」与「规则不存在」重新同形，破坏其独立诊断。
    /// </summary>
    private static List<StageHandoffWaitRule> DeserializeStageHandoffWaitRules(
        LPS.APS.Core.Entities.APS.ParameterSetVersion version)
    {
        // 整个 ContentSnapshotJson 缺失：六块 fail-fast 已在本对象初始化器更早的调用中先行拦截，
        // 本分支仅为防御性收敛（按「无源」处理，不另造抛出点）。
        if (string.IsNullOrWhiteSpace(version.ContentSnapshotJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(version.ContentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(SolverStrategyValidator.StageHandoffWaitRulesBlockName, out var blockElement)
                || blockElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return []; // 无源不阻断（H-3）：缺键 / 显式 null ⇒ 无交接等待规则
            }

            // C9（3号位 2026-10-29 裁决）：**键在、值是空数组** ≠ 「未配」。
            //   要表达「本版不用这块」就删键（或置 null）；留 `[]` 恒为「配了但零行」的非法形态。
            //   放行它会让下游「规则存在但零行」与「根本没配」重新同形（正是当初要求上游取消 OR 并集的原因）。
            //   ⇒ 装载期 fail-closed，错误码随消息带出（与 GovernanceVersionService 的 Code 字段同词表）。
            //   注意**不在此处落 BusinessFactIssue**：装载层无该能力（Application/Engine 对 BusinessFactIssue 零引用），
            //   承载口径待 3号位 定（见 2026-10-29 v2.0 答复 §一.3）；当前仅抛异常，由 Run 装载失败链留痕。
            if (blockElement.ValueKind == JsonValueKind.Array && blockElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    $"STAGE_HANDOFF_WAIT_BLOCK_EMPTY：参数集版本 {version.Id} 的 " +
                    $"{SolverStrategyValidator.StageHandoffWaitRulesBlockName} 子块存在但为零行数组（`[]`）。" +
                    "空数组是「已配置但无任何规则」的非法形态——若本版本不使用该块，请删除该键（缺键或显式 null 均表示未配置，正常放行）。");
            }

            return blockElement.Deserialize<List<StageHandoffWaitRule>>(StageHandoffJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"参数集版本 {version.Id} 的 ContentSnapshotJson 格式无效，StageHandoffWaitRules 子块装载失败", ex);
        }
    }

    /// <summary>从 ParameterSetVersion.ContentSnapshotJson 反序列化指定子块（P0-02 六块统一失败）</summary>
    private TBlock DeserializeParameterSetBlock<TBlock>(
        LPS.APS.Core.Entities.APS.ParameterSetVersion version,
        string blockName,
        long strategyProfileVersionId)
        where TBlock : class
    {
        if (string.IsNullOrWhiteSpace(version.ContentSnapshotJson))
        {
            throw new InvalidOperationException($"参数集版本 {version.Id} 的 ContentSnapshotJson 为空/缺失，Snapshot 装载失败");
        }

        try
        {
            using var doc = JsonDocument.Parse(version.ContentSnapshotJson);
            if (!doc.RootElement.TryGetProperty(blockName, out var blockElement))
            {
                throw new InvalidOperationException($"参数集版本 {version.Id} 的 ContentSnapshotJson 缺少 {blockName} 子块，Snapshot 装载失败");
            }

            return blockElement.Deserialize<TBlock>(JsonOptions)
                ?? throw new InvalidOperationException($"参数集版本 {version.Id} 的 {blockName} 子块反序列化结果为空，Snapshot 装载失败");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"参数集版本 {version.Id} 的 ContentSnapshotJson 格式无效，Snapshot 装载失败", ex);
        }
    }
}
