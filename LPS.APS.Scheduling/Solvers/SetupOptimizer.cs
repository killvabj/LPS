namespace LPS.APS.Scheduling.Solvers;

using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

/// <summary>
/// Setup 换型规则解析骨架（v1.2 收口版规则模型）。
///
/// 【口径来源】《APS V1 Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916_最终收口版》：
/// - §1.1/§20.2 废止属性式换型（Mold/Tool/Material/Color SetupAttribute），旧 SelectBestCandidate/CalculateSetupTime 已删除；
/// - §2/§3 换型键 =「当前 Task 自身 OperationCode + 当前 Resource + 前产品 FromMaterial + 后产品 ToMaterial」，方向性，
///   不读上一 Task 工序（废止 FromOperation/ToOperation 模型）；
/// - §5 命中三层：EXACT 产品对 → DEFAULT 工序+设备 → 无规则 0 分钟 + 记「规则缺失」解释；
/// - §6 同产品 A→A 默认 0 分钟，允许显式规则覆盖；
/// - §1.2/§20.3 禁止 RoutingOperation.SetupTime / MES SetupTime fallback；
/// - §14.3/0号位 20260917 裁决：「生产日」= Resource Calendar 连续可用生产窗口（不按自然日切，跨零点不断开）；
///   无上一产品 → 初始设备状态 Setup=0（INITIAL_SETUP_STATE 追踪说明，非 ReasonCode），与「规则缺失降级」严格分开；
/// - §8.2 裁决项1 已由 0号位 终裁（20260918）：0号位 只冻结红线——**必须有界搜索、不得因 Setup 搜索破坏
///   夜间约 15 分钟总性能目标**；具体预算/邻域尝试次数由 1号位 按性能标定提「默认值+允许范围」
///   （标定基准：10 万 Task / 90 天 / 夜间约 15 分钟），3号位 纳入 ParameterSetVersion 版本化治理；
///   500（有界搜索预算）/50（最大邻域尝试）仅作 1号位 测试初值，不得写成 0号位 冻结值；
///   「单生产日保护阈值」删除（语义未定义；未来确需时由 1号位 提五件套：中文技术含义/触发条件/
///   为什么现有总预算不足/建议默认值/性能测试依据，再由 3号位 纳入治理，不提前造字段）；
///   （旧 SetupLookAheadSize=5 / DefaultSetupMinutes=30 是已废止旧锚，不得沿用——3号位 20260917 §三）。
///
/// 【数据通道】r13376（3号位 2026-09-18）已落地 `SetupTransitionRuleSnapshot`（FrozenStrategySnapshot 第⑦块，
/// 随 RuleSetVersionId 锚冻结）；2号位 按 Domain 裁剪投影进 DomainSolveRequest 后，Phase1 经
/// <see cref="BuildRuleLookups"/> 适配为字典传入 <see cref="ResolveSetup"/>；本骨架不自建规则读取、不读 3号位 数据库。
///
/// 【当前状态】骨架 + 纯函数 + 快照适配，五阶段流程尚未接线（接线依赖 2号位 把第⑦块透传进 DomainSolveRequest）。
/// 夜间 FULL 接线时按 0号位 红线设计：有界搜索（500/50 测试初值起步）+ 总耗时不破 15 分钟夜间目标，
/// 标定后把「默认值+允许范围」回传 3号位 入 ParameterSetVersion。
/// </summary>
public class SetupOptimizer
{
    /// <summary>
    /// EXACT 规则键：当前工序 + 当前设备 + 前产品 + 后产品（方向性，(A,B) ≠ (B,A)）。
    /// ProductionDepartment/Stage 作为规则裁剪与唯一性上下文由 2号位/3号位 治理，不进运行时查找键（v1.2 §19.3）。
    /// </summary>
    public readonly record struct SetupExactKey(string OperationCode, int ResourceId, int FromMaterialId, int ToMaterialId);

    /// <summary>DEFAULT 规则键：当前工序 + 当前设备（v1.2 §四）。</summary>
    public readonly record struct SetupDefaultKey(string OperationCode, int ResourceId);

    /// <summary>Setup 解析结果类型（0号位 20260917 Q4 裁决：初始状态与规则缺失严格分开，不得合并分支）。</summary>
    public enum SetupOutcome
    {
        /// <summary>命中 EXACT 产品转换规则（§5 第一优先）。</summary>
        ExactHit,
        /// <summary>命中当前工序+设备默认规则（§5 第二优先）。</summary>
        DefaultHit,
        /// <summary>同产品连续 A→A 无显式规则，默认 0 分钟（§6）。</summary>
        SameProductZero,
        /// <summary>无上一产品 → 初始设备状态，Setup=0，记 INITIAL_SETUP_STATE 追踪说明（§14.3，非 ReasonCode）。</summary>
        InitialState,
        /// <summary>有前后产品但 EXACT/DEFAULT 均缺失 → 规则缺失降级 Setup=0，必须记「换型规则缺失」解释（§5 第三优先）。</summary>
        RuleMissing
    }

    /// <summary>
    /// SetupOutcome → 落库值（大写 4 态字符串）。契约源：0号位 20260922 裁决
    /// （Task.SetupSource 保留，枚举统一 4 态，不采用 5 值；SAME_PRODUCT/NONE 不作正式值）。
    /// 映射：ExactHit→EXACT、DefaultHit→DEFAULT、SameProductZero→DEFAULT
    /// （同产品连续无显式规则 = 业务默认不换型，非独立来源）、
    /// RuleMissing→SETUP_RULE_MISSING_ZERO_FALLBACK、InitialState→INITIAL_SETUP_STATE。
    /// 2号位 只承载原样落库，枚举→4 态转换在 1号位（本方法）。
    /// </summary>
    public static string SetupOutcomeToSource(SetupOutcome outcome) => outcome switch
    {
        SetupOutcome.ExactHit => "EXACT",
        SetupOutcome.DefaultHit => "DEFAULT",
        SetupOutcome.SameProductZero => "DEFAULT",
        SetupOutcome.RuleMissing => RuleMissingZeroFallbackType,
        SetupOutcome.InitialState => InitialSetupStateType,
        _ => RuleMissingZeroFallbackType
    };

    /// <summary>Setup 追踪统一 TraceType（0号位 20260917 正式回复 §5.1：TIME_CALCULATION）。</summary>
    public const string TraceType = "TIME_CALCULATION";

    /// <summary>初始设备状态解释类型符号锚（ExplainTrace 内部类型，非 ScheduleExplanationFact.ReasonCode——0号位 裁决项4 §5.1，INFO 级）。</summary>
    public const string InitialSetupStateType = "INITIAL_SETUP_STATE";

    /// <summary>DEFAULT 回退解释类型符号锚（0号位 正式回复 §5.2，INFO 级；定名 DEFAULT_SETUP_FALLBACK——2号位 20260920 回执建议，1号位 采纳，待三方词表终对齐）。</summary>
    public const string DefaultSetupFallbackType = "DEFAULT_SETUP_FALLBACK";

    /// <summary>换型规则缺失 0 分钟兜底解释类型符号锚（0号位 裁决项4 §5.3，WARNING 级；同时进 4号位「换型规则缺失/0分钟兜底」数据质量查询）。
    /// ContextData 需含 productionDepartmentId/stageCode/operationCode/resourceId/fromMaterialId/toMaterialId/setupMinutes——
    /// Dept/Stage 不在运行时查找键内（2号位 已按 Domain 裁剪），由调用方（Phase 接线时）从 Task 上下文补齐。</summary>
    public const string RuleMissingZeroFallbackType = "SETUP_RULE_MISSING_ZERO_FALLBACK";

    /// <summary>Setup 解析结果：分钟数 + 命中类型 + 追踪三元组（TraceLevel/ExplanationType/TraceMessage，按 0号位 §五口径；ExplainTrace 载体待 2号位 DTO 落地后写入）。</summary>
    public readonly record struct SetupResolution(
        decimal SetupMinutes,
        SetupOutcome Outcome,
        string? TraceMessage,
        string? TraceLevel = null,
        string? ExplanationType = null);

    /// <summary>
    /// 解析当前 Task 的换型时间（v1.2 §5 三层确定性命中，无额外 fallback）。
    /// </summary>
    /// <param name="operationCode">当前要排的 Task 自身小工序（不是上一 Task 的工序）。</param>
    /// <param name="resourceId">当前候选/实际设备。</param>
    /// <param name="fromMaterialId">该设备上一相邻 Task 的产品；null = 无可追溯上一产品（初始设备状态）。</param>
    /// <param name="toMaterialId">当前 Task 产品。</param>
    /// <param name="exactRules">EXACT 产品转换规则（2号位 装载，Phase1 适配传入）。</param>
    /// <param name="defaultRules">DEFAULT 工序+设备规则（同上）。</param>
    public SetupResolution ResolveSetup(
        string operationCode,
        int resourceId,
        int? fromMaterialId,
        int toMaterialId,
        IReadOnlyDictionary<SetupExactKey, decimal> exactRules,
        IReadOnlyDictionary<SetupDefaultKey, decimal> defaultRules)
        => ResolveSetupCore(operationCode, resourceId, fromMaterialId, toMaterialId, exactRules, defaultRules);

    /// <summary>纯函数解析核心（item1 接线阶段二：供 Phase2/Phase4 静态复用，实例方法 ResolveSetup 委托于此，行为一致）。</summary>
    internal static SetupResolution ResolveSetupCore(
        string operationCode,
        int resourceId,
        int? fromMaterialId,
        int toMaterialId,
        IReadOnlyDictionary<SetupExactKey, decimal> exactRules,
        IReadOnlyDictionary<SetupDefaultKey, decimal> defaultRules)
    {
        // 初始设备状态：无上一产品 → 不存在转换关系，Setup=0（不人为构造虚拟前产品，§0.1-4/§14.3）。
        // 追踪规格：0号位 正式回复 §5.1（TraceType=TIME_CALCULATION / TraceLevel=INFO / INITIAL_SETUP_STATE）。
        if (fromMaterialId is null)
        {
            return new SetupResolution(0m, SetupOutcome.InitialState,
                "当前资源无可追溯上一产品，按初始设备状态处理，Setup=0。",
                "INFO", InitialSetupStateType);
        }

        var from = fromMaterialId.Value;

        // 第一优先：EXACT 产品对（同产品 A→A 显式规则可覆盖默认 0，§6）。
        if (exactRules.TryGetValue(new SetupExactKey(operationCode, resourceId, from, toMaterialId), out var exactMinutes))
        {
            return new SetupResolution(exactMinutes, SetupOutcome.ExactHit, null);
        }

        // 同产品连续且无显式规则 → 0 分钟（§6；只比较前后产品，与上一工序无关）。
        if (from == toMaterialId)
        {
            return new SetupResolution(0m, SetupOutcome.SameProductZero, null);
        }

        // 第二优先：当前工序+当前设备默认规则（§四）。
        // 追踪规格：0号位 正式回复 §5.2（正常 fallback，INFO 轻量追踪；符号采 2号位 20260920 建议 DEFAULT_SETUP_FALLBACK）。
        if (defaultRules.TryGetValue(new SetupDefaultKey(operationCode, resourceId), out var defaultMinutes))
        {
            return new SetupResolution(defaultMinutes, SetupOutcome.DefaultHit,
                "未命中明确产品转换规则，使用当前工序/设备默认换型时间。",
                "INFO", DefaultSetupFallbackType);
        }

        // 第三优先：无规则 → 0 分钟 + 必须记录缺失解释（§5；禁止任何额外 fallback）。
        // 追踪规格：0号位 正式回复 §5.3（WARNING + SETUP_RULE_MISSING_ZERO_FALLBACK，进 4号位 数据质量查询）。
        return new SetupResolution(0m, SetupOutcome.RuleMissing,
            $"当前部门/Stage/工序[{operationCode}]/设备[{resourceId}]/前后产品[{from}→{toMaterialId}]未维护Setup规则，本次按0分钟计算。",
            "WARNING", RuleMissingZeroFallbackType);
    }

    /// <summary>
    /// 把 2号位/3号位 冻结的 <see cref="SetupTransitionRuleSnapshot"/> 列表（FrozenStrategySnapshot 第⑦块，
    /// r13376 落地）适配为运行时 EXACT/DEFAULT 查找字典。
    /// - 只取 IsActive 语义已由装载端保证（快照只含有效规则），此处按 RuleType 分流；
    /// - EXACT 要求 From/To 均有值，DEFAULT 忽略产品字段；
    /// - 同键重复时取第一条（唯一性由 3号位 发布前冲突校验保证，Solver 不随机选规则——v1.2 §十）。
    /// </summary>
    public static (Dictionary<SetupExactKey, decimal> Exact, Dictionary<SetupDefaultKey, decimal> Default) BuildRuleLookups(
        IEnumerable<SetupTransitionRuleSnapshot>? rules)
    {
        var exact = new Dictionary<SetupExactKey, decimal>();
        var def = new Dictionary<SetupDefaultKey, decimal>();

        if (rules is null) return (exact, def);

        foreach (var rule in rules)
        {
            if (string.Equals(rule.RuleType, "EXACT", StringComparison.Ordinal))
            {
                if (rule.FromMaterialId is null || rule.ToMaterialId is null)
                    continue;   // EXACT 缺产品 → 无效行，防御性跳过

                exact.TryAdd(new SetupExactKey(rule.OperationCode, rule.ResourceId, rule.FromMaterialId.Value, rule.ToMaterialId.Value),
                    rule.SetupMinutes);
            }
            else if (string.Equals(rule.RuleType, "DEFAULT", StringComparison.Ordinal))
            {
                def.TryAdd(new SetupDefaultKey(rule.OperationCode, rule.ResourceId), rule.SetupMinutes);
            }
            // 其它 RuleType 值不识别，防御性忽略。
        }

        return (exact, def);
    }

    /// <summary>
    /// 生产日窗口切分（0号位 20260917 Q1 裁决）：同一 Resource 的日历 Slot 按 Start 排序，
    /// 取 IsAvailable=true 的连续无间断区间合并为一个「连续可用生产窗口」；跨自然日零点不断开；
    /// 遇停机/非可用/班次断点结束当前窗口。生产日只是搜索边界，不重置设备上一产品状态（v1.2 §14.3）。
    /// </summary>
    /// <param name="slots">单一 Resource 的日历 Slot（Start, End, IsAvailable）。</param>
    /// <returns>合并后的连续生产窗口列表（按时间升序，空输入返回空列表）。</returns>
    public static List<(DateTime Start, DateTime End)> BuildProductionWindows(
        IEnumerable<(DateTime Start, DateTime End, bool IsAvailable)> slots)
    {
        var windows = new List<(DateTime Start, DateTime End)>();

        foreach (var slot in slots.Where(s => s.IsAvailable).OrderBy(s => s.Start))
        {
            if (windows.Count > 0 && windows[^1].End == slot.Start)
            {
                // 相邻且时间无间断 → 并入当前窗口（含跨零点场景）。
                windows[^1] = (windows[^1].Start, slot.End);
            }
            else if (windows.Count > 0 && slot.Start < windows[^1].End)
            {
                // 重叠可用段 → 扩展窗口末端（容错日历数据重叠）。
                if (slot.End > windows[^1].End)
                    windows[^1] = (windows[^1].Start, slot.End);
            }
            else
            {
                // 间断（停机/班次断点）→ 开新窗口。
                windows.Add((slot.Start, slot.End));
            }
        }

        return windows;
    }

    /// <summary>
    /// P1-02 item1 接线（阶段二）：动态 Setup 感知的槽查找（v1.2 §12：Setup 从初始 Resource/时间槽候选评价阶段就参与）。
    /// 鸡生蛋问题——Setup 取决于槽起点的前产品、前产品取决于槽位置、槽位置取决于 Setup 总时长——用有界迭代解：
    /// 以 floor 处前产品估算 Setup → 搜槽 → 用槽起点真实前产品重解析 → 更短则原窗必容得下（收敛返回）；
    /// 更长则以新值重搜。maxIterations 内未收敛则按最后解析值终搜一次（残余偏差由 Phase4 邻接重算/夜间 FULL 修正）。
    /// </summary>
    /// <param name="floor">最早占用开始时间（物料/依赖等下界，占用口径与 Phase2/Phase4 FindForwardSlot 用法一致）。</param>
    /// <param name="processDuration">加工时长（不含 Setup）。</param>
    /// <param name="findSlot">各 Phase 自己的日历感知槽查找：(最早开始, 含 Setup 总时长) → 占用窗；null = 无可行槽。</param>
    /// <returns>(占用窗[含Setup], Setup分钟, 解析结果)；null = 无可行槽。追踪三元组的写出待 ExplainTrace 载体（2号位 DTO）落地。</returns>
    internal static (TimeWindow Slot, decimal SetupMinutes, SetupResolution Resolution)? FindSlotWithDynamicSetup(
        DateTime floor,
        TimeSpan processDuration,
        int resourceId,
        string operationCode,
        int toMaterialId,
        ResourceProductTimeline timeline,
        IReadOnlyDictionary<SetupExactKey, decimal> exactRules,
        IReadOnlyDictionary<SetupDefaultKey, decimal> defaultRules,
        Func<DateTime, TimeSpan, TimeWindow?> findSlot,
        int maxIterations = 3)
    {
        var resolution = ResolveSetupCore(operationCode, resourceId,
            timeline.GetPrevMaterial(resourceId, floor), toMaterialId, exactRules, defaultRules);

        for (int i = 0; i < maxIterations; i++)
        {
            var slot = findSlot(floor, processDuration + TimeSpan.FromMinutes((double)resolution.SetupMinutes));
            if (slot == null) return null;

            var atSlot = ResolveSetupCore(operationCode, resourceId,
                timeline.GetPrevMaterial(resourceId, slot.Value.Start), toMaterialId, exactRules, defaultRules);

            if (atSlot.SetupMinutes <= resolution.SetupMinutes)
            {
                // 解析值 ≤ 搜索值：占用窗按真实 Setup 重算，必落在已验证的日历窗/空闲段内 → 收敛
                var occ = new TimeWindow(slot.Value.Start,
                    slot.Value.Start + TimeSpan.FromMinutes((double)atSlot.SetupMinutes) + processDuration);
                return (occ, atSlot.SetupMinutes, atSlot);
            }

            resolution = atSlot;   // 变大：以新 Setup 重搜
        }

        // 有界未收敛：按最后解析值终搜一次
        var finalSlot = findSlot(floor, processDuration + TimeSpan.FromMinutes((double)resolution.SetupMinutes));
        if (finalSlot == null) return null;
        var finalOcc = new TimeWindow(finalSlot.Value.Start,
            finalSlot.Value.Start + TimeSpan.FromMinutes((double)resolution.SetupMinutes) + processDuration);
        return (finalOcc, resolution.SetupMinutes, resolution);
    }
}

/// <summary>
/// P1-02 item1 接线（阶段二）：资源级「已排产品时间线」。
/// 维护 ResourceId → 按占用结束时间升序的 (End, MaterialId) 列表，支撑 v1.2 语义
/// 「FromMaterial = 当前设备上一相邻 Task 的产品」——按**时间邻接**查询（最晚 End ≤ 我的占用起点），
/// 而非按放置顺序（任务可能被排进中间空档，放置序 ≠ 时间序）。
///
/// 口径：
/// - 锁定继承 Task（Firm/Frozen/Execution）计入——它们是「可追溯上一产品」（v1.2 §14.3）；
/// - ExternalDomain ResourceBlocks 不计入——阻挡块不是 Task、无产品语义（查不到更早 Task 时按初始设备状态）；
/// - 放置/移动/合并时由调用方同步 Place/Remove（与 resourceOccupancy 成对维护）。
/// 性能：二分查询 O(log n) + 有序插入；10万 Task 规模标定如现瓶颈再换结构（性能标定阶段处理）。
/// </summary>
internal sealed class ResourceProductTimeline
{
    // ── 2026-10-20 八次定位：COW 深拷 ⇒「不可变基线 + 小增量覆盖」（性能，语义不变）──
    //
    // 【改前的结构】`_byResource` 一份有序 list + `_pristine`（克隆时的冻结快照）+
    //   `_sharedAsPristine`。只要本实例被 `Clone()` 过，**每次写入**都要把该资源**整条** list
    //   深拷一遍才能改。实测（16k 档 resources=32 / FinalTask=22,296）：
    //     · `TimelinePlace` **17,924 ms = ΣPhase 的 17.6%**（896,341 次 × **20.00 µs**）；
    //     · 试加 `_detachedFromSnapshots` 逐资源脱钩后，拷贝率仍 **94.8%**（锚点循环每轮都
    //       `Clone()` 一次、随即又写，脱钩登记每次都被清空）⇒ 等于没省。已撤回该尝试。
    //     · 单资源约 541 条登记 ⇒ 写一个 Task 拷 ~8.6 KB；全程约 **7 GB** 短命垃圾
    //       ⇒ GC 暂停 18.5 s（**另 18.1%**），运行时间被 GC 与拷贝双重吃掉。
    //
    // 【现在的结构】
    //   · `_base`  —— **发布后只读**的基线（资源 Id → 按 `End` 升序的 list）。
    //                 「压缩」时**整表换成一个新字典对象**，旧克隆仍持旧对象 ⇒ 天然隔离。
    //   · `_added` —— 本实例相对基线的**增量**（追加序，不排序），写入 O(1)、**零拷贝**。
    //   · `_removed`— 本实例相对基线的**墓碑**（按 `(End, MaterialId)` 精确匹配，可重复计数）。
    //   写入 = 往小列表尾部追加；读取 = 二分 `_base` + 线性扫小增量。
    //   增量累积到阈值就「压缩」回 `_base`（避免增量无限变长、`Clone()` 传播成本上升）。
    //
    // 【隔离怎么保证】`Clone()` **O(1)**：子克隆直接共享 `_base` / `_added` / `_removed` 三个引用，
    //   并把父实例的 `_deltaShared` 置 true。父实例此后**第一次**写入时先 `EnsureOwnDelta()`
    //   把这一层小字典（含内层小 list）拷成自己的 —— **一次性**、只拷增量、不拷基线。
    //   子克隆写完则与自己无关；父克隆继续写则父拿新副本，子看旧副本。层层如此。
    //
    // ⚠ 只影响**快慢**：`GetPrevMaterial` 读到的内容、各候选之间的隔离性、以及「快照永不被改写」
    //   这条不变式逐字不变（同 `End` 的先后口径原样保留，见 `Compact`/`GetPrevMaterial` 注释）。
    private Dictionary<int, List<(DateTime End, int MaterialId)>> _base = new();

    /// <summary>本实例相对基线的增量（追加序）。</summary>
    private Dictionary<int, List<(DateTime End, int MaterialId)>> _added = new();

    /// <summary>
    /// 本实例相对基线的墓碑：`(End, MaterialId)` → 还需隐藏的出现次数。
    /// ⚠ 用字典不用列表：`GetPrevMaterial` 是**读热路径**（实测数百万次），
    ///   若逐条线性扫墓碑，4k 档 ΣPhase 会从 5.1 s 涨到 7.8 s（实测）。
    /// </summary>
    private Dictionary<int, Dictionary<(DateTime End, int MaterialId), int>> _removed = new();

    /// <summary>增量条目总数（`_added` + `_removed` 全部内层 list 的元素和），用于触发压缩。</summary>
    private int _deltaCount;

    /// <summary>
    /// 本实例的增量字典是否正被**子克隆**共享（`Clone()` 后置 true）。
    /// 共享期间本实例若要写，先 <see cref="EnsureOwnDelta"/> 把这一层小字典拷成自己的（一次性）。
    /// </summary>
    private bool _deltaShared;

    /// <summary>
    /// 2026-10-20 二十三刀：本实例**内层增量 list**（`_added[rid]`）已确认为独有的资源 Id 集合。
    ///   <see cref="EnsureOwnDelta"/> 只分离外层字典；内层容器仍是共享引用时，
    ///   首次改写前要靠 <see cref="EnsureAddedOwned"/> 拷成独有并登记到这里。分离/压缩时清空。
    /// </summary>
    private readonly HashSet<int> _deltaAddedOwned = new();

    /// <summary>2026-10-20 二十三刀：本实例**墓碑内层字典**（`_removed[rid]`）已确认为独有的资源 Id 集合。</summary>
    private readonly HashSet<int> _deltaRemovedOwned = new();

    /// <summary>
    /// 增量超过该规模就压缩回基线：压缩成本 O(受影响资源 × 该资源条目数)，摊到 256 次写入上可忽略；
    /// 同时把 `Clone()` 之后父实例 `EnsureOwnDelta()` 的拷贝量限制在 256 条以内。
    /// </summary>
    private const int CompactThreshold = 256;

    /// <summary>从既有 Task 集合构建（Phase2 Schedule 入口重置 / Phase4 兜底重跑时用）。</summary>
    public static ResourceProductTimeline FromTasks(IEnumerable<FinalTaskDraft> tasks)
    {
        var timeline = new ResourceProductTimeline();
        foreach (var t in tasks)
        {
            // 非资源 Task（ResourceId=NULL）不占资源，不进产品时间线
            if (t.ResourceId is not int rid) continue;
            timeline.Place(rid, t.PlannedEndTime, t.MaterialId);
        }
        timeline.Compact();   // 建完压回基线：后续读取只付「二分 + 小增量」，不再扫大增量
        return timeline;
    }

    /// <summary>
    /// 拷贝（C桶候选内择优用）：每个候选 Path 必须在**同一初始上下文**上试排，互不污染；
    /// 试排结果择优后再在真实上下文上重跑选中候选落定（0号位 2026-10-07 裁决 Q-2：
    /// 「候选评价必须看到当前 Domain 真实的资源占用、Calendar、已排 Task…」，故不能只跑孤立试算）。
    ///
    /// ── 2026-10-08 性能（0号位《未命名的Markdown文件 (2)(1).md》§7.1/§7.2 + §十 允许的「增量 Delta /
    ///    Copy-on-write」）──
    /// 旧实现**每次**深拷 `_byResource` 的**每一个** list。实测本方法在 42,261 Task 场景被调用
    /// **94,940 次**（= 批方案候选试跑 14,046 + 候选路径试排 80,894），累计 17,778 ms = **ΣPhase 的 13.1%**，
    /// 且因「每次 O(全部资源 × 全部占用窗)」而随 N 呈 **O(N²)**（任务 ×1.97 ⇒ 本项耗时 ×3.30，实测）。
    ///
    /// 新实现改为**惰性深拷**：`Clone()` 只做 **O(资源数)** 的浅拷（list 仍共享）+ 一份**冻结快照**；
    /// 谁的 list 真被写、谁才付费深拷该 list。
    ///
    /// ── 2026-10-20 八次定位（本次）──
    /// 「惰性深拷」仍不够：克隆后**每次写入**都要把该资源**整条** list（约 541 条）拷一遍，
    /// 实测 `TimelinePlace` 20.00 µs/次、全程 17.9 s = ΣPhase 的 17.6%，并产生约 7 GB 垃圾。
    /// 现改为**不可变基线 + 小增量覆盖**：`Clone()` **O(1)**（三个引用直接共享），
    /// 写入往小增量尾部追加（O(1)、零拷贝），读取二分基线 + 扫小增量。
    /// **语义零变化**：`GetPrevMaterial` 读到的内容、各候选之间的隔离性与旧实现逐字相同
    /// （隔离规则见 <see cref="EnsureOwnDelta"/>，对「源被克隆后再写」「克隆再被克隆」都会分离增量层）。
    /// </summary>
    public ResourceProductTimeline Clone()
    {
        // 增量太大就先压回基线：压缩后本实例的增量归零 ⇒ 下面共享出去的增量层必然很小。
        if (_deltaCount > CompactThreshold) Compact();

        // 通知本实例：你的增量层已被子克隆共享 ⇒ 你下次写入前先 `EnsureOwnDelta()` 分离
        _deltaShared = true;

        // O(1)：三个引用直接共享。基线只读；增量层**父子两边都要**标成「共享」
        //   （漏标子侧 = 子克隆会直接改父实例的增量小字典 ⇒ 各候选互相污染、结果漂移）。
        return new ResourceProductTimeline
        {
            _base = _base,
            _added = _added,
            _removed = _removed,
            _deltaCount = _deltaCount,
            _deltaShared = true,
        };
    }

    /// <summary>
    /// 确保本实例的增量字典**为本实例独有**（写前调用）。
    ///
    /// 不变式：**任何实例都不得改写一个可能被别人（子克隆）共享的小字典**。
    /// `Clone()` 把 `_added` / `_removed` 直接共享给子克隆并把本实例的 `_deltaShared` 置 true
    /// ⇒ 本实例只要写入就必须先把这一层分离出来（**一次性**、只拷增量、**不拷基线**）。
    /// 基线 `_base` 不需要分离：它发布后只读，压缩时整体换成新对象。
    /// </summary>
    private void EnsureOwnDelta()
    {
        if (!_deltaShared) return;

        // ── 2026-10-20 二十三刀：**外层浅拷、内层按资源惰性拷**（隔离性逐字不变）──
        //   改前：每次「克隆后的首写」都把整层增量**深拷**一遍（外层 ≤256 条 + 每条的内层 list/dict）。
        //   20 次定位实测（16k 档）：`EnsureOwnDelta` **2,113 ms = ΣPhase 的 7.1%**，
        //   208,487 次真拷 × **10.1 µs**（`TimelinePlace` 2,745 ms 里占 77%）。
        //   而一次写入通常只碰 1~2 个资源 ⇒ 只拷外层字典（≤256 个引用），内层留到
        //   <see cref="EnsureAddedOwned"/> / <see cref="EnsureRemovedOwned"/> 里按资源、按容器拷。
        //   **隔离性论证**：任何内层容器在被本实例**改写前**，要么已是本实例独有（登记集合已含该资源），
        //   要么刚被拷成独有的一份 ⇒ 「不得改写共享容器」这条不变式不变。
        _added = new Dictionary<int, List<(DateTime End, int MaterialId)>>(_added);
        _removed = new Dictionary<int, Dictionary<(DateTime End, int MaterialId), int>>(_removed);
        _deltaAddedOwned.Clear();
        _deltaRemovedOwned.Clear();
        _deltaShared = false;
    }

    /// <summary>
    /// 2026-10-20 二十三刀：确保 `resourceId` 的**增量内层 list** 为本实例独有（每资源只拷一次）。
    ///   `EnsureOwnDelta` 只分离了外层字典；内层 `List` 在本次分离后仍可能与子克隆共享
    ///   ⇒ 首次改写该资源前必须先拷。已独有则 O(1) 直接返回。
    ///   ⚠ 与墓碑分开登记：`Place` 只碰增量、`Remove` 的墓碑分支只碰墓碑，
    ///     若共用一个登记集合就会「拷了一个、另一个被误认为已独有」。
    /// </summary>
    private void EnsureAddedOwned(int resourceId)
    {
        if (!_deltaAddedOwned.Add(resourceId)) return;

        if (_added.TryGetValue(resourceId, out var add) && add is not null)
        {
            _added[resourceId] = new List<(DateTime End, int MaterialId)>(add);
        }
    }

    /// <summary>2026-10-20 二十三刀：确保 `resourceId` 的**墓碑内层字典**为本实例独有（见 <see cref="EnsureAddedOwned"/>）。</summary>
    private void EnsureRemovedOwned(int resourceId)
    {
        if (!_deltaRemovedOwned.Add(resourceId)) return;

        if (_removed.TryGetValue(resourceId, out var rem) && rem is not null)
        {
            _removed[resourceId] = new Dictionary<(DateTime End, int MaterialId), int>(rem);
        }
    }

    /// <summary>深拷一层增量字典（**含内层小 list** —— 写入要原地改内层 list，只拷字典不够）。</summary>
    private static Dictionary<int, List<(DateTime End, int MaterialId)>> CopyDelta(
        Dictionary<int, List<(DateTime End, int MaterialId)>> src)
    {
        var dst = new Dictionary<int, List<(DateTime End, int MaterialId)>>(src.Count);
        foreach (var kv in src)
            dst[kv.Key] = new List<(DateTime End, int MaterialId)>(kv.Value);
        return dst;
    }

    /// <summary>深拷一层墓碑字典（内层计数字典同样要独立，否则父子互相清零）。</summary>
    private static Dictionary<int, Dictionary<(DateTime End, int MaterialId), int>> CopyDelta(
        Dictionary<int, Dictionary<(DateTime End, int MaterialId), int>> src)
    {
        var dst = new Dictionary<int, Dictionary<(DateTime End, int MaterialId), int>>(src.Count);
        foreach (var kv in src)
            dst[kv.Key] = new Dictionary<(DateTime End, int MaterialId), int>(kv.Value);
        return dst;
    }

    /// <summary>
    /// 把增量压缩回基线：基线**整体换新对象**（旧克隆仍持旧对象 ⇒ 隔离不受影响），
    /// 本实例增量清零。只有「受增量影响的资源」的 list 会被重建。
    /// 同 `End` 的先后口径：真序列里 `Place` 的二分插入落在同 `End` 段**首位** ⇒
    /// 后加入者更靠前 ⇒ 合并时增量条目排在基线同 `End` 段**之前**、增量内部按**加入倒序**。
    /// </summary>
    private void Compact()
    {
        // 增量层若正被子克隆共享，先分离：下面要清空自己的增量，不能波及子克隆。
        EnsureOwnDelta();

        if (_deltaCount == 0) return;

        var newBase = new Dictionary<int, List<(DateTime End, int MaterialId)>>(_base);

        var affected = new HashSet<int>();
        foreach (var rid in _added.Keys) affected.Add(rid);
        foreach (var rid in _removed.Keys) affected.Add(rid);

        foreach (var rid in affected)
        {
            // 2026-10-20 二十三刀：合并时 `TryConsume` 会**改写**墓碑内层字典，
            //   ⇒ 必须先确认该资源的墓碑容器是本实例独有的（外层分离不再连带深拷内层）。
            EnsureRemovedOwned(rid);

            _base.TryGetValue(rid, out var baseList);
            _added.TryGetValue(rid, out var addList);
            _removed.TryGetValue(rid, out var remMap);

            var baseCount = baseList?.Count ?? 0;
            var addCount = addList?.Count ?? 0;
            var merged = new List<(DateTime End, int MaterialId)>(baseCount + addCount);

            // 增量按 End 升序、同 End **后加入者在前**（复刻 `Place` 二分插入落段首的次序）
            List<(DateTime End, int MaterialId)>? orderedAdd = null;
            if (addList is not null && addCount > 0)
            {
                var order = new int[addCount];
                for (var i = 0; i < addCount; i++) order[i] = i;
                var src = addList;
                Array.Sort(order, (a, b) =>
                {
                    var c = src[a].End.CompareTo(src[b].End);
                    return c != 0 ? c : b.CompareTo(a);
                });
                orderedAdd = new List<(DateTime End, int MaterialId)>(addCount);
                for (var i = 0; i < addCount; i++) orderedAdd.Add(src[order[i]]);
            }

            // 归并：同 End 时增量条目在前、基线条目在后
            int ai = 0, bi = 0;
            var addLen = orderedAdd?.Count ?? 0;
            while (ai < addLen || bi < baseCount)
            {
                var takeAdd = ai < addLen
                              && (bi >= baseCount || orderedAdd![ai].End <= baseList![bi].End);
                var e = takeAdd ? orderedAdd![ai++] : baseList![bi++];

                // 墓碑按 (End, MaterialId) 逐条消耗；消耗不到就照常保留
                if (remMap is not null && TryConsume(remMap, e)) continue;

                merged.Add(e);
            }

            newBase[rid] = merged;
        }

        _base = newBase;        // 整表换新对象 ⇒ 旧克隆持旧对象，不受影响
        _added.Clear();
        _removed.Clear();
        _deltaCount = 0;
        _deltaAddedOwned.Clear();       // 2026-10-20 二十三刀：增量清空后内层引用全部作废，登记表同清
        _deltaRemovedOwned.Clear();
    }

    /// <summary>从墓碑计数表里消耗一条与 <paramref name="entry"/> 完全相同的登记；消耗到返回 true。</summary>
    private static bool TryConsume(
        Dictionary<(DateTime End, int MaterialId), int> tombstones,
        (DateTime End, int MaterialId) entry)
    {
        if (!tombstones.TryGetValue(entry, out var n) || n <= 0) return false;

        if (n == 1) tombstones.Remove(entry);
        else tombstones[entry] = n - 1;
        return true;
    }

    /// <summary>
    /// 上一相邻 Task 产品：最晚占用结束时间 ≤ occStart 者；null = 无可追溯上一产品（初始设备状态，Setup=0）。
    /// 合并顺序与旧的单条有序 list **逐字等价**（见 <see cref="Compact"/> 的同 `End` 口径）。
    /// </summary>
    public int? GetPrevMaterial(int resourceId, DateTime occStart)
    {
        long sw = SolverDiagnostics.HotspotStart();

        // ① 基线：二分找「最右一个 End <= occStart」，再跳过墓碑（同一条目可能被 Remove 过多次）
        (DateTime End, int MaterialId)? bestBase = null;
        if (_base.TryGetValue(resourceId, out var baseList) && baseList.Count > 0)
        {
            int lo = 0, hi = baseList.Count - 1, ans = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (baseList[mid].End <= occStart) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (ans >= 0)
            {
                _removed.TryGetValue(resourceId, out var remMap);
                var seen = 0;                            // 已越过的「同一条目」次数
                var prev = (End: DateTime.MinValue, MaterialId: -1);
                var first = true;
                while (ans >= 0)
                {
                    var e = baseList[ans];
                    if (first || e.End != prev.End || e.MaterialId != prev.MaterialId) { seen = 0; prev = e; first = false; }

                    if (seen >= CountTombstones(remMap, e)) break;   // 还有未被墓碑覆盖的 ⇒ 就是它
                    seen++;
                    ans--;
                }
                if (ans >= 0) bestBase = baseList[ans];
            }
        }

        // ② 增量：线性扫（很小）。同 End 取**先加入**者（真序列里后加入的排在更前，故最右是它）
        (DateTime End, int MaterialId)? bestAdd = null;
        if (_added.TryGetValue(resourceId, out var addList))
        {
            foreach (var e in addList)
            {
                if (e.End > occStart) continue;
                if (bestAdd is null || e.End > bestAdd.Value.End) bestAdd = e;
            }
        }

        int? result;
        if (bestBase is null) result = bestAdd?.MaterialId;
        else if (bestAdd is null) result = bestBase.Value.MaterialId;
        else
            // 同 End 时基线更靠右（增量插在同 End 段首位）⇒ 取基线
            result = bestAdd.Value.End > bestBase.Value.End
                ? bestAdd.Value.MaterialId
                : bestBase.Value.MaterialId;

        SolverDiagnostics.HotspotEnd(sw, SolverDiagnostics.Hotspot.TimelineRead);
        return result;
    }

    /// <summary>`tombstones` 中与 <paramref name="entry"/> 完全相同的条数（null/空 ⇒ 0）。</summary>
    private static int CountTombstones(
        Dictionary<(DateTime End, int MaterialId), int>? tombstones,
        (DateTime End, int MaterialId) entry)
        => tombstones is not null && tombstones.TryGetValue(entry, out var n) ? n : 0;

    /// <summary>登记一个已放置 Task 的产品与占用结束时间（追加进本实例增量：O(1)、零拷贝）。</summary>
    public void Place(int resourceId, DateTime occEnd, int materialId)
    {
        // 2026-10-20 八次定位：产品时间线**写入**总成本（含增量分离 + 追加 + 偶发压缩）。纯观测。
        long sw = SolverDiagnostics.HotspotStart();

        // 2026-10-20 二十二次定位：单独量「克隆后首次写入的整层增量分离」。纯观测。
        long swOwn = SolverDiagnostics.HotspotStart();
        EnsureOwnDelta();
        EnsureAddedOwned(resourceId);       // 2026-10-20 二十三刀：内层 list 按资源惰性分离
        SolverDiagnostics.HotspotEnd(swOwn, SolverDiagnostics.Hotspot.EnsureOwnDelta);

        if (!_added.TryGetValue(resourceId, out var add))
        {
            add = new List<(DateTime End, int MaterialId)>();
            _added[resourceId] = add;
        }
        add.Add((occEnd, materialId));

        if (++_deltaCount > CompactThreshold) Compact();

        SolverDiagnostics.HotspotEnd(sw, SolverDiagnostics.Hotspot.TimelinePlace);
    }

    /// <summary>移除一个 Task 的登记（移动/重建前调用；按 End+MaterialId 匹配第一条）。</summary>
    public void Remove(int resourceId, DateTime occEnd, int materialId)
    {
        // 写前先确保增量层为本实例独有（基线只读，无需分离）
        EnsureOwnDelta();

        // ① 本实例刚加的登记：直接在增量里撤掉（真序列里同 `End` 时增量排在基线**之前**
        //    ⇒ 「第一条匹配」必然先命中增量；同一条目多条时删哪条都一样，不影响结果）
        //    ⚠ 2026-10-20 二十三刀：内层 list 的分离推迟到**真要 RemoveAt 的那一刻**
        //      （只是在共享 list 上找下标的话不必拷）。
        if (_added.TryGetValue(resourceId, out var add))
        {
            for (var i = 0; i < add.Count; i++)
            {
                if (add[i].End != occEnd || add[i].MaterialId != materialId) continue;

                EnsureAddedOwned(resourceId);
                _added[resourceId].RemoveAt(i);     // 分离后取自己的那份再删
                _deltaCount--;
                return;
            }
        }

        // ② 基线里的登记：**不改基线**，只登记一条墓碑（否则会改到别处克隆可见的状态）
        if (!_base.TryGetValue(resourceId, out var baseList) || baseList.Count == 0) return;

        // ── 2026-10-19 性能整改（口径沿用）：基线**按 `End` 升序**，用同比较器二分定位
        //   `End == occEnd` 的**首段**，取代整表线性扫描。
        //   ⚠ 三重证据齐备才认二分结果，否则**回落**原线性扫描（语义逐字不变，只影响快慢）：
        //     ① `lo` 命中 `End == occEnd`；
        //     ② `lo` 确为**首个** `End >= occEnd`（即 `lo == 0 || baseList[lo-1].End != occEnd`）；
        //     ③ 只在 `End == occEnd` 的**连续段内**找 `MaterialId` —— 该段覆盖了全部同 `End` 条目
        //        ⇒ 段内没有就是全表没有，可安全提前返回（与线性扫描的「第一条匹配」语义一致）。
        int lo = 0, hi = baseList.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (baseList[mid].End < occEnd) lo = mid + 1;
            else hi = mid;
        }

        var occurrences = 0;
        if (lo < baseList.Count
            && baseList[lo].End == occEnd
            && (lo == 0 || baseList[lo - 1].End != occEnd))
        {
            for (var i = lo; i < baseList.Count && baseList[i].End == occEnd; i++)
                if (baseList[i].MaterialId == materialId) occurrences++;

            if (occurrences == 0) return;   // 段内没有 ⇒ 全表没有
        }
        else
        {
            // 2026-10-20 六次定位：落到这里就是二分没命中 ⇒ 记一次，用于确认「有序」不变式是否真的成立。
            SolverDiagnostics.CountDetachTimelineBinaryMiss();
            for (var i = 0; i < baseList.Count; i++)
            {
                if (baseList[i].End == occEnd && baseList[i].MaterialId == materialId)
                {
                    occurrences = 1;
                    break;
                }
            }
        }

        if (occurrences == 0) return;

        // 已登记的墓碑先抵消这些出现；抵消完仍有剩余 ⇒ 这次才真能删掉一条
        _removed.TryGetValue(resourceId, out var rem);
        var entry = (occEnd, materialId);
        var tombstoned = CountTombstones(rem, entry);
        if (tombstoned >= occurrences) return;

        // 2026-10-20 二十三刀：到这里才真要写墓碑 ⇒ 此刻才把内层字典分离出来（拷内容逐字相同）
        EnsureRemovedOwned(resourceId);
        _removed.TryGetValue(resourceId, out rem);

        if (rem is null)
        {
            rem = new Dictionary<(DateTime End, int MaterialId), int>();
            _removed[resourceId] = rem;
        }
        rem[entry] = tombstoned + 1;

        if (++_deltaCount > CompactThreshold) Compact();
    }
}

/// <summary>
/// P1-02 item1（夜间 FULL）：确定性伪随机源（LCG，Knuth MMIX 常数）。
/// 不用 BCL Random——其算法跨 .NET 版本可变，会破坏「同 Run 重放同结果」（Run 重放/审计/回归依赖确定性）。
/// 种子由 ScheduleRunId/PlanVersionId 派生：同一 Run 重放 → 同一搜索轨迹 → 同一结果。
/// public：性能标定工装（LPS.APS.Tests/Benchmarks/SolverBenchmark）复用同一 LCG，保证基准与生产轨迹一致
/// （Scheduling 无 InternalsVisibleTo(Tests)，故放宽可见性；无签名变化）。
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(long seed)
    {
        _state = unchecked((ulong)seed * 6364136223846793005UL + 1442695040888963407UL);
    }

    public uint NextUInt()
    {
        _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
        return (uint)(_state >> 33);   // 高位质量更好
    }

    /// <summary>[0, maxExclusive) 均匀整数；maxExclusive ≤ 0 返回 0。</summary>
    public int Next(int maxExclusive)
        => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);
}
