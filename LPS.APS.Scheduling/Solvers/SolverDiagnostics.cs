using System.Threading;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **求解器性能计数器（诊断用，非契约）**。
///
/// 【为什么在这里而不在 Core】
///   0号位 2026-10-08《未命名的Markdown文件 (2)(1).md》§九 要求性能压测输出
///   「BatchPlanCandidate 试跑次数 / Routing 试跑次数 / CloneOccupancy 次数 / CloneTaskList 次数 /
///     SlotSearch 次数 / Phase4 传播 Task 数」。
///   但把 Phase 级字段加进 `DomainSolveResult` / `SolveSummary` 属于 **Core 契约变更**（Core 归 2号位）
///   ⇒ 本类放在 1号位 自己的 `LPS.APS.Scheduling` 内，**不触碰任何契约 DTO**，
///     由压测工装（`LPS.APS.Tests/Benchmarks/`）直接读取。
///
/// 【并发语义（为什么用 AsyncLocal 而不是全局 static long）】
///   `Counters` 对象存在 <see cref="AsyncLocal{T}"/> 里，由 <see cref="BeginScope"/> 在**调用方**
///   建立 ⇒ `await SolveAsync(...)` 时 ExecutionContext 把该引用流进求解器内部，
///   内部只**改对象字段**（引用不变）⇒ 调用方读同一个对象即得本次数。
///   · 并发求解（Web 端多 Domain 并行）各持**各自**的 Counters ⇒ 互不污染；
///   · **未开 scope 时**（正常生产路径）所有 `Count*` 调用只做一次 null 判断 ⇒ 近乎零开销、零行为影响。
///   ⇒ 计数器**只增不减**、**不参与任何业务判定**，纯观测。
///
/// 【明确不做的】
///   · 不统计耗时（耗时由压测工装用 `Stopwatch` 在 Phase 边界测，避免热点内取时钟）；
///   · 不写 `SolveTraceNotes`（trace 体积约束 + 与 2号位 的 ReasonCode 命名待定项）。
/// </summary>
public static class SolverDiagnostics
{
    /// <summary>本次求解的计数器（字段为 `long`，仅供单线程求解内自增；读取前请确保求解已结束）。</summary>
    internal sealed class Counters
    {
        /// <summary>`SelectBestBatchPlan` 里每个**批方案候选**触发一次完整 `RunBatchPlan` 试跑。</summary>
        public long BatchPlanCandidateTrials;

        /// <summary>`RunBatchPlan` 里每个（执行批 × Routing 候选）触发一次候选试排。</summary>
        public long RoutingTrials;

        /// <summary>`CloneOccupancy` 调用次数（每次复制全资源占用表）。</summary>
        public long CloneOccupancy;

        /// <summary>`CloneTaskList` 调用次数（每次复制全量已排任务表，O(N)）。</summary>
        public long CloneTaskList;

        /// <summary>`CloneShares` 调用次数（每次重建全量份额字典，O(N)）。</summary>
        public long CloneShares;

        /// <summary>
        /// 2026-10-19 性能定位：`TryMergeDemandIntoStageBatch` 摘除段累计摘除的 Task 数 **Σk**
        ///   （= Σ|锚批|）。与 <see cref="DetachListLen"/> 一起算出「每次调用摘几个 / 列表多长」，
        ///   用于判定「逐个 `RemoveAt` 的搬移量 ≈ k·n/2」到底是不是耗时主因。纯观测。
        /// </summary>
        public long DetachTaskCount;

        /// <summary>
        /// 2026-10-19 性能定位：摘除时 `scheduledTasks` 的累计长度 **Σn**。纯观测。
        /// </summary>
        public long DetachListLen;

        /// <summary>
        /// 2026-10-20 十三刀：摘除段的「预建下标表 / 全表扫描回落」二选一**已取消**（一律全表扫描），
        ///   原 `DetachFallbackCount / NoIndex / NullId / Missing / DupPos` 五个计数器随之删除。
        /// </summary>
        /// <summary>
        /// 2026-10-20 六次定位：摘除段**占用窗二分未命中**、回落 `List.FindIndex` 线性扫的次数。
        ///   与 <see cref="Hotspot.DetachOccupancy"/> 的调用次数比 ⇒ 命中率。若命中率低，说明
        ///   「占用表按 `(Start, End)` 有序」这条不变式在摘除侧不成立，9.5 µs/次 是 O(W) 线性扫而非 O(log W)。纯观测。
        /// </summary>
        public long DetachOccupancyBinaryMiss;

        /// <summary>
        /// 2026-10-20 六次定位：`ResourceProductTimeline.Remove` **二分未命中**、回落线性扫的次数。
        ///   与 <see cref="Hotspot.DetachTimeline"/> 的调用次数比 ⇒ 命中率。纯观测。
        /// </summary>
        public long DetachTimelineBinaryMiss;

        /// <summary>2026-10-20 六次定位：`EnsureOwned` **真的做了 COW 深拷**的次数（= 拷贝份数）。纯观测。</summary>
        public long DetachOccupancyCopies;

        /// <summary>2026-10-20 六次定位：`DetachOccupancy` 每次触达的资源**占用窗列表长度之和** ΣW。纯观测。</summary>
        public long DetachOccupancyWindowLen;


        /// <summary>2026-10-20 十三刀：`BuildTaskIndexById` 已删除，原 `TaskIndexBuilds / TaskIndexNullResults` 计数随之删除。</summary>
        /// <summary>2026-10-19 二次定位：`RunBatchPlan` 的**总调用次数**（两个调用点：`:846` 多批落定路径 + `:1641` 批方案候选试排）。纯观测。</summary>
        public long RunBatchPlanInvocations;

        /// <summary>
        /// 2026-10-20 十一次定位：`scheduledTasks` 被**整表替换**（`Clear() + AddRange(winnerState.Tasks)`）的次数。
        ///   这是「跨 `RunBatchPlan` 复用索引」是否可行的决定性事实：整表替换会强制重建索引，
        ///   若它 ≈ `RunBatchPlan` 调用次数（9,366）⇒ 跨调用缓存无收益；若远小于 ⇒ 收益巨大。纯观测。
        /// </summary>
        public long TaskTableReplaced;

        /// <summary>2026-10-20 十一次定位：`scheduledTasks` 被**尾部追加**（`AddRange`）的次数。纯观测。</summary>
        public long TaskTableAppended;

        /// <summary>
        /// 2026-10-20 二十九次定位：批分组**整表重建**的触发原因计数（互斥，合计 = 重建次数）。
        ///   [0] 缓存标记失效 / [1] 任务表变短 / [2] 长度不短但前缀失配。纯观测。
        /// </summary>
        public readonly long[] BatchGroupRebuildReasons = new long[3];

        /// <summary>
        /// 2026-10-20 二十九次定位：批分组索引**采纳前对齐**的结果计数。
        ///   [0] 快路径（只追加）/ [1] 双指针对齐成功 / [2] 形状不符退回 /
        ///   [3] 前提不成立（快照失效 / 归属表不符 / 元素不在桶里）；
        ///   [4] 摘除落在**真表**（快照镜像的那张）/ [5] 摘除落在**试排副本**；
        ///   [6] 试排副本与共享快照已分叉 ⇒ 独立建一份（二十八刀，不动共享缓存）；
        ///   [7] 副本比快照**短** / [8] 前缀逐引用不符（二十八刀定位；这两条是 [6] 的细分）。纯观测。
        /// </summary>
        public readonly long[] BatchGroupReconcileOutcomes = new long[9];

        /// <summary>
        /// 2026-10-20 十二次定位（**已量完撤除**）：逐位比对证明整表替换 15,365 次中 15,307 次内容完全相同，
        ///   但该结论没能转化成收益（比对本身的 O(N) 与省下的 O(N) 等量）⇒ 比对代码与计数一并删除，
        ///   结论留在 `PhaseTwoInitialScheduler` 采纳段注释里。
        /// </summary>

        /// <summary>
        /// 2026-10-20 十二次定位：`ComputeDetachPositions` 被调用次数 = 摘除段**真正需要定位**的次数。
        ///   这条读数直接决定了 2026-10-20 十三刀的撤销决定：当时对比的是「建表 17,774 次 vs 消费 3,080 次」。
        ///   保留原因：日后若改回索引方案，先看这个比值。纯观测。
        /// </summary>
        public long DetachPosCalls;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-19 三次定位：锚点枚举的「规模」**（回答「`stageBatches.Count` 到底多大」）
        //   此前只量到**平均值**（`TryMergeStageBatch 次数 / BatchPlanTrial 次数` ≈ 5.2 / 12.9 / 30.0），
        //   但那是**比值**、且分母含未进合批分支的试排 ⇒ 既不是真实均值、更没有**最大值/分布**。
        //   ⇒ 本组计数器在**擂台入口**直接登记 `stageBatches.Count` 本体。纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>进入锚点擂台（`stageBatches.Count &gt; 0`）的次数。</summary>
        public long AnchorLoopEntries;

        /// <summary>Σ `stageBatches.Count`（每次进入擂台的锚批数之和）—— 除以 <see cref="AnchorLoopEntries"/> 得**真实平均**。</summary>
        public long AnchorBatchCountSum;

        /// <summary>`stageBatches.Count` 的**最大值**（单次擂台的锚批数峰值）。</summary>
        public long AnchorBatchCountMax;

        /// <summary>
        /// `stageBatches.Count` 分布直方图。分桶上界见 <see cref="AnchorBucketBounds"/>（升序，左闭右开）；
        ///   末桶 = ≥ 最后一项。下标 <c>i</c> 对应「`count &lt; AnchorBucketBounds[i]`」，末桶为溢出桶。
        /// </summary>
        public readonly long[] AnchorBatchBuckets = new long[AnchorBucketBounds.Length + 1];

        /// <summary>锚点枚举规模直方图的分桶上界（升序）；末桶为溢出桶（≥ 最后一项）。</summary>
        internal static readonly int[] AnchorBucketBounds = { 1, 2, 3, 5, 10, 20, 50, 100, 200, 500, 1000 };

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-19 三次定位：候选扫描规模**（第二条 O(N)）
        //   `FindMergeableStageBatches` 里 `foreach (var group in groups.Values)`（`:5907`）
        //   —— 每条需求都把**全部**批分组扫一遍，与「几个锚批能活下来」无关。
        //   本组量的是**扫了多少个**（不是活下来几个）。纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>`FindMergeableStageBatches` 的**候选扫描**次数（= 进入该方法且走到扫描段的次数）。</summary>
        public long MergeScanCalls;

        /// <summary>Σ 每次扫描遍历的批分组数（`groups.Count`）—— 「每条需求 O(批数)」的成本来源。</summary>
        public long MergeScanGroupSum;

        /// <summary>单次扫描遍历的批分组数**最大值**。</summary>
        public long MergeScanGroupMax;

        /// <summary>时间槽搜索次数（正排 `FindFirstAvailableSlot` + 倒排 `FindBackwardSlot`）。</summary>
        public long SlotSearches;

        /// <summary>
        /// 2026-10-25（2号位《PV195 零任务定位》§4.3-2）：**找槽尝试次数** = `FindForwardSlot` /
        /// `FindBackwardSlot` 的**入口调用次数**（正排/倒排同一口径）。
        ///   与 <see cref="SlotSearches"/> 的区别（这正是 2号位 指出的口径问题）：
        ///   `SlotSearches` 在**两个方向上含义不同** —— 倒排计的是 `FindBackwardSlot` 入口，
        ///   正排计的是**内层** `FindFirstAvailableSlot`（只有当某个日历窗长 ≥ 任务时长时才会被调到）。
        ///   ⇒ 正排下 `SlotSearches == 0` 有两种完全不同的含义：「一个窗都装不下」与「根本没试」。
        ///   本计数器把「发起过几次找槽」独立出来，单工序前后相减即可得「本工序尝试了几次」。
        /// </summary>
        public long SlotSearchAttempts;

        /// <summary>Phase4 传播过程中实际访问（重排/改期）的 Task 次数。</summary>
        public long Phase4PropagatedTasks;

        /// <summary>Phase5 `CompactGaps`（空隙压实）**门控放行并进入优化体**的次数（0 = 未进入）。</summary>
        public long Phase5CompactionRuns;

        /// <summary>Phase5 `OptimizeSetupSequences`（Setup 序列优化）**门控放行并进入优化体**的次数。</summary>
        public long Phase5SetupOptimizationRuns;

        /// <summary>
        /// Phase5 `OptimizeSegment`（**资源 × 连续生产窗口 × 固定锚点间可移动段**）**真正进入有界局部优化体**
        ///   的段数 —— 即「段内 ≥2 个 Task 且全部可移动」的段。
        ///   与 <see cref="Phase5SetupOptimizationRuns"/> 的区别：后者只说明「整 Run 未被否决」，
        ///   本计数说明「**确实有一个可重排的段被送进优化体**」（P1-02 验收：混合方向下 FORWARD 段必须可达）。
        /// </summary>
        public long Phase5SetupSegmentsOptimized;

        /// <summary>
        /// V1_3 F-01：合批落定失败 / 交期违约时，按插入身份回滚占用表**未命中**的条目数累计。
        ///   **正常恒为 0** —— 非 0 说明有代码路径绕过 <c>AddOccupancyWindow</c> 的 `insertLog` 直写占用表
        ///   （⇒ 该窗不会被回滚 ⇒ 幽灵占用）。纯观测、不参与任何业务判定。
        /// </summary>
        public long OccupancyRollbackUnmatched;

        /// <summary>
        /// V1_3 F-03：B-009 合法量门禁**因输入整体未投影而不可评估**的批形成次数累计
        ///   （`piFacts is null`）。纯观测、不参与任何业务判定；**非 0 即表示生产态该门禁不可达**。
        /// </summary>
        public long PiLegalQuantityGateUnevaluated;

        /// <summary>V1_3 F-03：B-009 合法量门禁**判越限/事实不可信并 Fail Closed**的次数。纯观测。</summary>
        public long PiLegalQuantityGateBlocked;

        /// <summary>
        /// 2026-10-12 通电补：**有策略路径**（`ApplyPiLegalQuantityGate`）**完整评估后过闸**的次数（= 放行档）。
        ///   【为什么必须补】（2号位 2026-10-12《门禁通电接线回执》§三，理由成立）：
        ///     ① 不补则「放行」档**只在缺策略库态**（走 B-010 兜底）读得出来 ⇒ 3号位 修好 ⑧块、C 桶改走
        ///        有策略路径的那一刻，该验收指标**当场失效**（陷阱式指标）；
        ///     ② 「放行 = 评估总数 − 拦截」**推不出** —— 评估总数本身没有计数器。
        ///   口径：**本方法完整评估后过闸**才 +1；`!IsLegal` / A/B 豁免（`IsContinuation || NoSplitMerge`）/
        ///     `piFacts is null` 三条**早退不计**（那不是「放行」，是「门禁不适用」）。
        ///   ⇒ 真实「放行」档 = 本项 + <see cref="PiLegalQuantityGatePassedViaB010Fallback"/>。纯观测。
        /// </summary>
        public long PiLegalQuantityGatePassed;

        /// <summary>
        /// 2026-10-12 通电补：**B-010 缺策略保守兜底路径**（`TryFormConservativePiBatch`）被同一门禁
        ///   `EvaluatePiLegalQuantityGate` 判**未满足**的次数。
        ///   【为什么必须单列】<see cref="PiLegalQuantityGateBlocked"/> 只在**有策略路径**
        ///   （`ApplyPiLegalQuantityGate`）自增；C 桶缺策略走 B-010 分支时**此前一次都不计** ⇒
        ///   验收按「拦截」档读计数器会把 B-010 的拦截读成 0（2号位 2026-10-12《门禁通电信号》§四 即按此口径验收）。
        ///   ⇒ 真实「拦截」档 = <see cref="PiLegalQuantityGateBlocked"/> + 本项。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGateBlockedViaB010Fallback;

        /// <summary>
        /// 2026-10-12 通电补：**B-010 缺策略保守兜底路径**真正**过闸并成批**（= 放行档）的次数。
        ///   与 <see cref="PiLegalQuantityGateBlockedViaB010Fallback"/> 对称，使「放行 / 拦截」两档可直接读。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGatePassedViaB010Fallback;

        /// <summary>
        /// 2026-10-22（`QuantityAuthority = ORDER_ONLY`）：B-009 门禁**按数量权威分级主动不适用**并放行的批形成次数。
        ///   口径：`piFacts` 非空（本 Run 确实载入了 PI 权威事实集）且该需求被判 `ORDER_ONLY`
        ///   ⇒ 按 B-010「合法无 PI 规划需求」放行，**不是**「放行」，是「门禁不适用」。
        ///   ⇒ 不进 <see cref="PiLegalQuantityGatePassed"/> 的放行档。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGateSkippedOrderOnly;

        /// <summary>
        /// 2026-10-22：`ORDER_ONLY` 因**本次 Run 的 PI 权威事实集为空**而被**拒绝采信**、回落到
        ///   「有 PI 号就走门禁」（Fail Closed）的批形成次数。
        ///   【为什么必须单列】事实集整体为空/未投影时，**每条**带 PI 号的需求都会被分类成 `ORDER_ONLY`；
        ///   若照单放行，整道硬门禁会被静默关掉 = 降低目标。⇒ 本项**非 0 即表示本次 Run 的 PI 事实装载有问题**，
        ///   必须与「装载链没跑」一起查（同 `PeggingOrchestrator` 装载侧的告警）。
        ///   口径分歧见 0号位 2026-10-22 待裁项。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiLegalQuantityGateOrderOnlyRejectedEmptyFacts;

        /// <summary>
        /// R-1/R-2（2026-10-12，B-011）：**经济软区间偏离**被登记的**批数**（**软**偏离，**不**构成硬失败）。
        ///   经济 `Min/Max` 自 2026-10-12 起是**软优化输入**（03 §60 / 07 §4651「**不得再将「Qty 大于
        ///   经济 Max」本身作为硬冲突条件**」）⇒ 某批落在经济区间外只**登记偏离**，**不再**判
        ///   `BATCH_POLICY_CONFLICT`（旧口径 = 04 BP-R1 的「假硬不可排」）。纯观测、不参与任何业务判定。
        /// </summary>
        public long EconomicBatchDeviations;

        /// <summary>
        /// B-013 / 06 v1.36 §281（2026-10-13）：**Batch Policy 三态之 `INVALID`** 被消费的需求数
        ///   （「本键**有** Batch Policy 记录但**不满足发布/有效化资格**」⇒ 06 §281 逐字
        ///   「**不得假装 MISSING 兜底，应暴露 Issue 等待治理**」⇒ Fail Closed、**不**走 B-010 `Q_C` 兜底）。
        ///
        /// ⚠ **当前恒 0（载体不可达，如实登记）**：投影端 `FrozenStrategySnapshot.cs:294` 自述本块
        ///   「仅业务键 + 参数，**不含审计字段**（…IsActive —— 投影时**只取有效规则**）」⇒ INVALID 行在上游
        ///   已被滤掉。⇒ **恒 0 是「载体未到」而非「无 INVALID」**；待 2号位 按最小技术合同修正传递状态后
        ///   才具观测意义。非 0 即说明载体已到位。纯观测、不参与任何业务判定。
        /// </summary>
        public long BatchPolicyInvalids;

        /// <summary>
        /// **2026-10-13 新增**（0号位《未命名的Markdown文件4.md》§3.3）：本 Run 中判为
        /// **Batch Policy 状态不可获得**（`BatchPolicyMatchState.Unknown`）而 Fail Closed 的需求数。
        ///
        /// ⚠ **当前必然非 0**（与 <see cref="BatchPolicyInvalids"/> 恒 0 恰好相反）：现行载荷**无状态位**、
        ///   投影**只取有效规则** ⇒ 1号位 **对任一非命中键都无法证明「完全缺配」** ⇒ 一律判 `Unknown`
        ///   （0号位 §3.2 逐字：「从空集合无法区分『真正无规则』和『存在无效规则但未传入』」）。
        ///   ⇒ 本计数是**「输入契约缺陷规模」的观测量**：**归零即说明 2号位 可信状态载体已到位**。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long BatchPolicyUnknowns;

        /// <summary>
        /// V1_3 F-04：本 Run **实际生效**的 <c>SolverBatchBudget</c> 快照 —— 版本号
        ///   （<c>SolverBatchBudget/v1</c>）、取源（`…:SplitParams` / `…:default`）与两个**生效值**。
        ///   供「Run 快照 / 追踪证据」逐 Run 复现「到底用了什么技术预算」。
        ///   ⚠ 只记录**实际生效值**；业务旧列（`BatchPolicyRuleSnapshot.MaxOptimizationSplitCount` /
        ///     `MaxBatchCandidates`）**不参与**，改动它们不得改变本快照（B-007 单源可追溯）。
        /// </summary>
        public string? SolverBatchBudgetVersion;

        /// <summary>V1_3 F-04：本 Run 生效技术预算的**取源**标识（单源可追溯）。</summary>
        public string? SolverBatchBudgetSource;

        /// <summary>V1_3 F-04：本 Run 生效的 `MaxOptimizationSplitCount`。</summary>
        public long SolverBatchBudgetMaxOptimizationSplitCount;

        /// <summary>V1_3 F-04：本 Run 生效的 `MaxBatchCandidates`。</summary>
        public long SolverBatchBudgetMaxBatchCandidates;

        /// <summary>
        /// V1_4 NEW-05：MIXED「倒排部分成功后失败 ⇒ 转正排」时，按插入身份撤销的**倒排残留占用窗**数累计。
        ///   **正常 ≥0 且只在倒排确实写过窗后失败时非 0**（例：倒排最后一道成功、上一道失败）。
        ///   与 <see cref="OccupancyRollbackUnmatched"/> 的区别：后者是「该撤却撤不掉」的**异常**计数，
        ///   本项是「**确实撤销了多少**」的**正常**计数 —— 若本项长期恒 0 而 MIXED 走回落，说明回落路径**未撤销**（旧缺陷）。
        ///   纯观测、不参与任何业务判定。
        /// </summary>
        public long MixedBackwardRollbackWindows;

        /// <summary>
        /// V1_4 NEW-04：本 Run 内**新增 Stage 执行批**对 (PI × 物料 × Stage) 余额的累计登记次数
        ///   （<c>PiStageCommitLedger.Commit</c> 成功次数）。纯观测、不参与任何业务判定。
        /// </summary>
        public long PiStageLedgerCommits;

        /// <summary>V1_4 NEW-04：本 Run 内累计登记被**按身份回滚**（候选试排隔离 / 需求未落定）的次数。纯观测。</summary>
        public long PiStageLedgerRollbacks;

        // ══════════════════════════════════════════════════════════════════
        // 2026-10-25：**运行中进度探针**（2号位 2026-10-25《单域可复现样本》§7.2 请求）。
        //   目的：让一个跑 50 分钟的 Run **不是黑盒** —— 后台线程轮询 `SolverDiagnostics.LiveCounters`
        //   即可读到「当前 Phase / Phase2 处理到第几条 / 共几条」，不必等求解返回。
        //   口径说明：
        //     · 本组字段**纯观测**，不参与任何业务判定、不进任何出口；
        //     · `Phase2DemandsProcessed` 计的是「主循环**已进入**处理的需求条数」（单调递增，
        //       到 `Phase2DemandsTotal` 即本阶段收口）—— 一条需求内部耗多久它照旧不动，
        //       这正是本条要暴露的东西（「63,620 条里卡在第 N 条」比「一点反应都没有」有用）；
        //     · 只支持**单 Run 串行**（V1 求解本就串行）；并发多 Run 时后者覆盖前者。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 进度序列号：**每次阶段推进 / 进度写入即 +1**（`SetPhase`、`SetPhase2Demand*`、`SetPhase4Unit*`）。
        ///   只服务于 <see cref="SolverLiveSnapshot"/> 的**撕裂自检** —— `SolverLiveSnapshot` 是逐字段拼的，
        ///   不是原子快照；读取方前后各取一次本值，不等即知「这次读到的是混合值」。**纯观测。**
        /// </summary>
        public long Sequence;

        /// <summary>Phase2 待处理需求**总条数**（进主循环前拍一次）。</summary>
        public long Phase2DemandsTotal;

        /// <summary>Phase2 主循环**已进入**处理的需求条数（每进一条 +1，单调递增）。</summary>
        public long Phase2DemandsProcessed;

        /// <summary>Phase4 待修复单元总条数（进 Phase4 前拍一次；未进 Phase4 时保持 0）。</summary>
        public long Phase4UnitsTotal;

        /// <summary>Phase4 已处理的修复单元条数。</summary>
        public long Phase4UnitsProcessed;

        /// <summary>
        /// 当前阶段名（`Phase1` / `Phase2` / `Phase3` / `Phase4` / `Phase5` / `Idle`）。
        ///   由 <see cref="SolverDiagnostics.SetPhase"/> 在 Phase 边界写入，供运行中轮询。
        /// </summary>
        public string CurrentPhase = "Idle";

        /// <summary>Phase 1（硬约束构建）耗时 ms。</summary>
        public long Phase1Ms;

        /// <summary>Phase 2（初始有限产能排程）耗时 ms。</summary>
        public long Phase2Ms;

        /// <summary>Phase 3（可行性与延期诊断）耗时 ms。</summary>
        public long Phase3Ms;

        /// <summary>Phase 4（有界局部修复）耗时 ms。</summary>
        public long Phase4Ms;

        /// <summary>Phase 5（压缩空隙与最终评价）耗时 ms。</summary>
        public long Phase5Ms;

        // ══════════════════════════════════════════════════════════════════
        // **热点累计耗时**（ticks；含嵌套，故各项之和会大于 Phase2Ms —— 用途是「谁最慢」，
        //   不是「把 Phase2 拆完」）。用 `Stopwatch.GetTimestamp()` 零分配采集：
        //   热点方法每次调用都可能执行 9.5 万次以上，`new Stopwatch()` 会自己成为热点。
        // ══════════════════════════════════════════════════════════════════
        private readonly long[] _hotspotTicks = new long[HotspotCount];

        /// <summary>
        /// 2026-10-19 性能定位：各热点的**调用次数**（与 <see cref="_hotspotTicks"/> 同索引）。
        ///   有次数才能把「累计 ms」换算成「每次多少 µs」——判定「是不是单次开销过大」必须靠它。
        /// </summary>
        private readonly long[] _hotspotCalls = new long[HotspotCount];

        /// <summary>热点数量（= <c>SolverDiagnostics.Hotspot</c> 枚举项数）。</summary>
        public const int HotspotCount = 66;

        internal void AddHotspotTicks(int hotspot, long ticks)
        {
            if ((uint)hotspot < HotspotCount)
            {
                _hotspotTicks[hotspot] += ticks;
                _hotspotCalls[hotspot]++;
            }
        }

        /// <summary>取热点累计耗时（ms）。</summary>
        public double HotspotMs(int hotspot)
            => _hotspotTicks[hotspot] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>2026-10-19 性能定位：取热点**调用次数**（配合 <see cref="HotspotMs"/> 算每次耗时）。</summary>
        public long HotspotCallCount(int hotspot)
            => (uint)hotspot < HotspotCount ? _hotspotCalls[hotspot] : 0L;
    }

    /// <summary>热点标识（索引须与 <see cref="Counters.HotspotCount"/> 对齐）。</summary>
    internal static class Hotspot
    {
        /// <summary>候选试排里的 `ProductTimeline.Clone()`。</summary>
        public const int TimelineClone = 0;
        /// <summary>`CloneOccupancy`（复制全资源占用表）。</summary>
        public const int CloneOccupancy = 1;
        /// <summary>`new List&lt;FinalTaskDraft&gt;(scheduledTasks)`（复制全量任务表，O(N)）。</summary>
        public const int CloneTaskList = 2;
        /// <summary>`CloneShares`（重建全量份额字典，O(N)）。</summary>
        public const int CloneShares = 3;
        /// <summary>时间槽搜索（正排 `FindFirstAvailableSlot` + 倒排 `FindBackwardSlot`）。</summary>
        public const int SlotSearch = 4;
        /// <summary>`RunBatchPlan` 整体（批方案候选试跑，含其内部候选路径试排）。</summary>
        public const int BatchPlanTrial = 5;
        /// <summary>`RunDemandSchedule`（单条候选路径的完整试排）。</summary>
        public const int DemandSchedule = 6;
        /// <summary>Phase4 传播循环整体。</summary>
        public const int Phase4Propagation = 7;
        /// <summary>
        /// `FindMergeableStageBatches` 首段「按 `ExecutionBatchDraftKey` 把**全量** `scheduledTasks` 重新分组」
        ///   （O(N) 扫描 + 每个批键一个 `List` 的分配）。该段在**每个 Routing 候选试跑**里被整表重建一次
        ///   ⇒ 实测是 `DemandSchedule` 热点里最大的**未埋点**成本（2026-10-19 定位）。
        /// </summary>
        public const int BatchGroupIndex = 8;
        /// <summary>`ScheduleDemandOperations`（单条需求全部工序的完整排程；`DemandSchedule` 的最大子项）。</summary>
        public const int ScheduleDemandOps = 9;
        /// <summary>
        /// `TryMergeDemandIntoStageBatch`（多工序 Stage 合批候选：移出锚批 → 以锚身份+两需求量之和**重跑完整链**）。
        ///   在锚点循环内逐锚调用（`TryMergeOrSchedule` 的 `stageBatches` 擂台），实测 2026-10-19 单次求解
        ///   调用 84,110 次（= `CloneOccupancy` 115,521 − `CloneTaskList` 31,411）。
        /// </summary>
        public const int TryMergeStageBatch = 10;
        /// <summary>
        /// `TryMergeDemandIntoStageBatch` 的**摘除段**：全量扫描 `scheduledTasks` 找锚批 Task（`ids.Contains`）
        ///   + 逐个 `RemoveAt`（O(N) 内存搬移）+ 摘占用窗/时间线/份额。调用 84,110 次 ⇒ 约 9.4 亿次迭代。
        /// </summary>
        public const int MergeBatchDetach = 11;
        /// <summary>
        /// 2026-10-19 二次定位（2026-10-20 十三刀后改名 `DetachLocate`，原 `DetachIndexBuild`）：
        ///   `MergeBatchDetach` 的**第 1 段** —— **定位**锚批 Task 的下标（现为 `ids` 全表扫描）
        ///   + `removedTasks` 登记 + `CountDetachShape`。**不含** `RemoveAt` 搬移（见 <see cref="DetachRemoveAt"/>）。
        ///   ⚠ 撤销下标表后本段只对**落定路径**（`detachTaskList: true`）执行，实测 3,080 次 × 约 0.88 ms ≈ 2.7 s。
        /// </summary>
        public const int DetachLocate = 12;
        /// <summary>
        /// 2026-10-19 二次定位：摘除段的 **`RemoveAt` 搬移循环**（嵌套在 <see cref="DetachLocate"/> 计时区间内，
        ///   故本项已计入第 1 段总读数，此处只是**再切一刀**看「定位」与「搬移」各占多少）。
        ///   形状实测：每次摘 k=3 个、列表 n≈10,568 ⇒ 理论搬移 ≈ k·n·8B ≈ 250 KB/次。
        /// </summary>
        public const int DetachRemoveAt = 13;
        /// <summary>
        /// 2026-10-19 二次定位：`MergeBatchDetach` 的**第 3 段**（逐 Task 摘占用窗 / 时间线登记 / 份额血缘）。
        ///   ⚠ **已停用**，理由同 <see cref="DetachEnsureOwned"/>（实测 1,512 ms ≈ ΣPhase 的 1.7%）。
        ///   ⚠ **已停用**：一次性定位已完成（实测 1,512 ms ≈ ΣPhase 的 1.7%，**不是**摘除段主体）。
        ///   逐 Task 埋点自身开销会污染 `MergeBatchDetach` 总读数，故已从热路径移除。槽位保留以免全仓常量重新编号。
        /// </summary>
        public const int DetachBookkeeping = 14;
        /// <summary>
        /// 2026-10-19 二次定位：`MergeBatchDetach` 的**第 4 段** —— 收尾快照
        ///   （`CaptureOccupancyResourceIds` + `occupancyInsertLog` + `ProductTimeline.Clone()`）。
        /// </summary>
        public const int DetachTail = 15;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-19 四次定位：Phase2 里「`BatchPlanTrial` 之外」的那 31%**
        //   16k 档实测：`Phase2Ms` 235,169 ms，而 `BatchPlanTrial` 只占 160,557 ms
        //   ⇒ **74,612 ms（≈ ΣPhase 的 31%）完全没名字**，是当时全表最大的一块。
        //   本节 2 个槽把 `Phase2Ms` 拆开：`SchedulePreamble`（循环外）＋ `LandingRun`（循环内最大嫌疑）。
        //   剩余残差 = `Phase2Ms − SchedulePreamble − 循环内各已埋项`（循环后聚合仅 3 行，可忽略）。
        //   纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// `PhaseTwoInitialScheduler.Schedule` 里**胜出批方案在真实表上的落定跑**
        ///   （`RunBatchPlan` 的**第 2 个调用点**，`PhaseTwoInitialScheduler.cs:833`）。
        ///   ⚠ 与 <see cref="BatchPlanTrial"/> **不是同一处**：后者是候选择优循环内的**试排**（克隆表上）。
        ///   全仓 `RunBatchPlan` 只有 2 个调用点；16k 档实测 `RunBatchPlan总次数=13,105`、
        ///   `BatchPlanTrial=9,366` ⇒ **本项 ≈ 3,739 次，此前从未被计时**。
        /// </summary>
        public const int LandingRun = 16;

        /// <summary>
        /// `Schedule` 的**循环外准备段**：方法入口 → `foreach (var demand in sortedDemands)`（`:268`）之前。
        ///   含排序、索引/字典预建、锁定批解析等（约 240 行）。
        /// </summary>
        public const int SchedulePreamble = 17;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-20 五次定位：`DemandSchedule` 内部那 51% 的空白**
        //   16k 档实测：`DemandSchedule` 95,447 ms / 35,548 次（每次 2,685 µs），
        //   而已埋的子项只有 `TryMergeStageBatch` 18,580 + `ScheduleDemandOps` 13,946
        //   ⇒ **约 67,000 ms（≈ ΣPhase 的 51%）完全没名字**，是当前全表最大的一块。
        //   本节 3 个槽把「每次试排都重扫当前全量状态」的三处嫌疑分别计时。
        //   纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// `FindMergeableStageBatches` 的**主循环** `foreach (var group in groups.Values)`：
        ///   每条需求都把**全量**执行批分组扫一遍（实测 16k 档 35,548 次试排 × 平均 4,226 组
        ///   = **1.5 亿次**分组迭代），而真正活下来的锚批平均只有 4.9 个。
        ///   ⚠ 与 <see cref="BatchGroupIndex"/> **不是同一段**：后者只是「把任务表分组成字典」这一次 O(N)，
        ///   本项是「拿这份字典逐候选全扫」。
        /// </summary>
        public const int MergeAnchorScan = 18;

        /// <summary>
        /// `FindMergeableTasks`（**单工序** Merge 目标查找）：对全量 `scheduledTasks` 逐条做
        ///   `demandByKey` 反查 + 多次字符串相等比较。多工序路径不走这里（直接返回空表）。
        /// </summary>
        public const int MergeFindTasks = 19;

        /// <summary>
        /// `TryMergeOrSchedule` 锚点循环内的**资源效率评价段**（R-2 ④.6）：
        ///   逐锚点 3 次 `ResourceEfficiencyEvaluator.Evaluate`（+ 2 次纯设备口径），全部 O(任务数)。
        ///   ⚠ 其中 `Evaluate(separateProduced)` 的入参**逐锚点完全相同**（独立排程侧不随锚点变化）。
        /// </summary>
        public const int MergeEffEval = 20;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-20 六次定位：`MergeBatchDetach` 内部的超线性嫌疑**
        //   16k 档实测 `MergeBatchDetach` 8,605 ms，而 8k 档只有 1,324 ms
        //   —— 数据翻倍却涨 **6.5 倍**，明显超线性（同族特征见 2026-10-19 那次「线性扫占用窗」）。
        //   已知子项只覆盖 `DetachIndexBuild` 76 + `DetachRemoveAt` 20 + `DetachTail` 746
        //   ⇒ **约 7,760 ms 没名字**，全在第 2/3/4 段那个 `foreach (var (_, t) in removedTasks)` 里。
        //   本节 3 个槽把它按「占用窗 / 产品时间线 / 份额血缘」三件事切开。纯观测。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 摘除段第 2 件事：**占用窗**——`EnsureOwned`（COW 脱钩）+ `FindOccupancyWindowIndex`
        ///   （二分；未命中回落 `List.FindIndex` 全窗线性扫）+ `windows.RemoveAt`。
        /// </summary>
        public const int DetachOccupancy = 21;

        /// <summary>
        /// 摘除段第 3 件事：**产品时间线**——`constraints.ProductTimeline.Remove(rid, End, MaterialId)`
        ///   （内部先 `Mutable` 做 COW 判定，可能深拷该资源整条 list，再二分定位 `RemoveAt`）。
        /// </summary>
        public const int DetachTimeline = 22;

        /// <summary>
        /// 摘除段第 4 件事：**份额血缘**——`allocationTaskShare.Remove(t.FinalDraftId)`（只动字典）。
        /// </summary>
        public const int DetachShare = 23;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-20 七次定位：`ScheduleDemandOperationsCore` 那 25% 的黑盒**
        //   16k 档 `ScheduleDemandOps` 21,900 ms / 202,117 次（每次 108 µs），
        //   内部只有 `SlotSearch`（2,365 ms）有名字 ⇒ 剩下约 19 s 完全没拆开。
        //   本节按「方向 + 两处有序插入」切四刀。纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>`ScheduleBackward` 全程（倒排；每成功一道即写占用窗 + 产品时间线）。</summary>
        public const int ScheduleBackward = 24;

        /// <summary>`ScheduleForward` 全程（正排）。</summary>
        public const int ScheduleForward = 25;

        /// <summary>
        /// `AddOccupancyWindow` 全程：`EnsureOwned`（COW 可能深拷整条资源列表）+ 二分定位 +
        ///   `List.Insert` 搬移。⚠ 正排追加到末尾 ⇒ `Insert` 退化 O(1)；**倒排从头部插入** ⇒ 每次搬整条列表。
        /// </summary>
        public const int OccInsert = 26;

        /// <summary>
        /// `ResourceProductTimeline.Place` 全程：`Mutable`（COW 深拷）+ 二分 + `List.Insert` 搬移。
        /// </summary>
        public const int TimelinePlace = 27;

        /// <summary>
        /// `ResourceProductTimeline.GetPrevMaterial` 全程：基线二分 + 墓碑跳过 + 增量扫描。
        ///   2026-10-20 八次定位：写入提速后要确认**读侧没变慢**。
        /// </summary>
        public const int TimelineRead = 28;

        /// <summary>
        /// `FindForwardSlot` 全程：日历遍历 + 逐窗 `FindFirstAvailableSlot`。
        ///   2026-10-20 九次定位：`ScheduleForward` 里除 `SlotSearch` 外还有一大块没名字，先定它。
        ///   ⚠ 2026-10-20 第五刀（同日后）：原「**每次调用都 `OrderBy` 排序 + 分配**」已消除 ——
        ///     日历改为「首次自检、结果缓存」（`PhaseTwoInitialScheduler.GetAscendingCalendar`）⇒ 本槽此后只量找槽本身。
        /// </summary>
        public const int FindForwardSlot = 29;

        /// <summary>`HasConflict` 全程：二分定区间 + 区内重叠判。</summary>
        public const int HasConflict = 30;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-20 十次定位：占用窗 COW「拷」还是「搬」**
        //   16k 实测 `DetachOccupancy` 5,199 ms / 416,409 次（每次 12.49 µs），
        //   而「拷 541 个引用 + 二分 10 步 + `RemoveAt` 搬 270 个引用」按常识只有 ~1 µs
        //   ⇒ 差了一个数量级，怀疑**列表长度有长尾**或**拷贝不是主项**。先切两刀再动手。
        //   本节 4 个槽纯观测，不参与任何业务判定。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>`DetachOccupancy` 里的 `EnsureOwnedCore`（COW 深拷该资源整条占用窗列表）。</summary>
        public const int DetachCopy = 31;

        /// <summary>`DetachOccupancy` 里的二分定位 + `RemoveAt` 搬移（不含 COW 深拷）。</summary>
        public const int DetachRemove = 32;

        /// <summary>`AddOccupancyWindow` 里的 `EnsureOwned`（COW 深拷）。</summary>
        public const int OccCopy = 33;

        /// <summary>`AddOccupancyWindow` 里的二分定位 + `List.Insert` 搬移（不含 COW 深拷）。</summary>
        public const int OccInsertPos = 34;

        // ══════════════════════════════════════════════════════════════════
        // **2026-10-20 十次定位：`BatchPlanTrial` 里那约 31.5 s 的黑盒**
        //   16k 实测 `BatchPlanTrial` 59,973 ms，而 `DemandSchedule` 全部调用合计只有 28,450 ms
        //   ⇒ **约 31.5 s 落在 `RunBatchPlan` 内、`RunDemandSchedule` 外**。
        //   本节把它切成「试排前隔离副本 + 方向解析」「择优与采纳胜者状态」两块。纯观测。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>`RunBatchPlan` 单候选试排前的准备：方向解析 + 时间线/占用/任务/份额 四份隔离副本。</summary>
        public const int TrialPrep = 35;

        /// <summary>`RunBatchPlan` 单批的步骤 3/4：候选内择优 + 把胜者状态整表换回真表（O(N) 任务表重填）。</summary>
        public const int BatchAdopt = 36;

        /// <summary>`TrialPrep` 里的 `ResolveDirectionForOps`（AUTO 才真算，否则原样返回）。</summary>
        public const int PrepDirection = 37;

        /// <summary>
        /// `TrialPrep` 里的两张 O(N) 索引懒建：`BuildBatchGroupsIndex` + `BuildTaskIndexById`
        ///   （源 = `scheduledTasks`，16k 档约 2.2 万条；每个 RunBatchPlan 每批只建一次，但按需求反复建）。
        /// </summary>
        public const int PrepIndex = 38;

        /// <summary>
        /// 2026-10-20 二十次定位：`BuildBatchGroupsIndex` 的**整表重建**路径（`Clear()` + 逐任务 `AddToGroup`）。
        ///   用于区分「PrepIndex 那 2.8 s 是重建吃的，还是增量尾巴吃的」。纯观测。
        /// </summary>
        public const int BatchGroupFullRebuild = 39;

        /// <summary>2026-10-20 二十次定位：`BuildBatchGroupsIndex` 的**增量**路径（前缀比对 + 只补尾巴）。纯观测。</summary>
        public const int BatchGroupIncremental = 40;

        /// <summary>
        /// 2026-10-20 二十二次定位：`ResourceProductTimeline.EnsureOwnDelta`（COW 分离：把**整层**增量深拷一份）。
        ///   用于验证「`TimelinePlace` 那 2.9 s 主要花在克隆后的第一次写入分离上」。纯观测。
        /// </summary>
        public const int EnsureOwnDelta = 41;

        /// <summary>
        /// 2026-10-20 二十四次定位：`RepairUnscheduledDemands` 的**准备段**
        ///   （`ScheduledTasks.GroupBy().ToDictionary()` + `BuildContinuityCompletionByPI`）。
        ///   Phase4 5.5 s（ΣPhase 的 18.7%）此前**零埋点**，先切成三块看主体在哪。纯观测。
        /// </summary>
        public const int Phase4Prep = 42;

        /// <summary>2026-10-20 二十四次定位：`RepairUnscheduledDemands` 的 `TryResourceSwitch`（修复主体的放置路径）。纯观测。</summary>
        public const int Phase4TrySwitch = 43;

        /// <summary>2026-10-20 二十四次定位：`RepairUnscheduledDemands` 的收尾记账（占用登记 + 方向留痕 + 成功计数）。纯观测。</summary>
        public const int Phase4Bookkeep = 44;

        // ── 2026-10-20 二十六次定位：Phase5（`PhaseFiveCompression.Compress`）此前**零埋点**，
        //    16k 档约占 ΣPhase 的 13%。按 `Compress` 的方法调用次序切成七段。纯观测，不改任何行为。──

        /// <summary>Phase5 §一：`CompactGaps`（前向保序压实）。</summary>
        public const int Phase5CompactGaps = 45;

        /// <summary>Phase5 §二：`OptimizeSetupSequences`（段内 Setup 序列优化）。</summary>
        public const int Phase5SetupSeq = 46;

        /// <summary>Phase5 §三：`BuildTerminalContributions` + `landedQtyByDemand` 聚合。</summary>
        public const int Phase5Terminal = 47;

        /// <summary>Phase5 §四：`GenerateTaskDependencies`。</summary>
        public const int Phase5Deps = 48;

        /// <summary>Phase5 §五：未排程清单组装（Phase2 未排 ∪ Phase4 未修 ∪ `unscheduledQtyByAllocation` 汇总）。</summary>
        public const int Phase5Unsched = 49;

        /// <summary>Phase5 §六：`GenerateAllocationShares`。</summary>
        public const int Phase5Shares = 50;

        /// <summary>Phase5 §七：`ValidateHardResult`（八项最终硬校验）。</summary>
        public const int Phase5Validate = 51;

        /// <summary>Phase5 未排清单里：Phase 2 未排程需求循环（含三张根因判定）。</summary>
        public const int Phase5UnschedP2 = 52;

        /// <summary>Phase5 未排清单里：Phase 4 仍未排程需求循环。</summary>
        public const int Phase5UnschedP4 = 53;

        /// <summary>Phase5 未排清单里：`unscheduledQtyByAllocation` 汇总（含 `allocationSeqByDemandKey`）。</summary>
        public const int Phase5UnschedAgg = 54;

        /// <summary>
        /// `IsCalendarCoverageInsufficient` 里那张 `request.RoutingOperations` 的逐工序时长扫描
        ///   （每个需求一次全表 LINQ）。用于验证它是不是未排清单 1.2 s 的主体。纯观测。
        /// </summary>
        public const int Phase5CalendarScan = 55;

        /// <summary>Phase5 `CompactGaps` 准备段（占用表 + 不可移动集 + 需求字典 + 非 FORWARD 标记）。</summary>
        public const int Phase5CompactPrep = 56;

        /// <summary>Phase5 `CompactGaps` 索引段（`TaskIndex.Build` + 需求完成时间 + 处理序排序）。</summary>
        public const int Phase5CompactIndex = 57;

        /// <summary>Phase5 `CompactGaps` 主循环（逐 Task 算 floor/ceil + 找槽 + 落地移动）。</summary>
        public const int Phase5CompactLoop = 58;

        /// <summary>
        /// 2026-10-20 二十七次定位：`EnsureOwnedCore` 里**真正发生深拷**的那一条语句
        ///   （`new List&lt;TimeWindow&gt;(original)`）。与 <see cref="OccCopy"/> 相减 = 两次字典查找的成本。
        ///   用来解释「891,525 次 `OccCopy` 共 1.0 s」这个与其工作量不符的读数。纯观测。
        /// </summary>
        public const int OccCopyReal = 59;

        /// <summary>2026-10-20 二十八次定位：`ScheduleForward` 的前置段（`GetMaterialEarliestTime`）。纯观测。</summary>
        public const int ScheduleFwdPreamble = 60;

        /// <summary>2026-10-20 二十八次定位：`ScheduleForward` 的 `SetupOptimizer.FindSlotWithDynamicSetup`（含其内部的 `FindForwardSlot` 回调）。</summary>
        public const int ScheduleFwdSetup = 61;

        /// <summary>2026-10-20 二十八次定位：`ScheduleForward` 找到槽之后的记账段（建 Task + 写占用 + 写时间线 + trace）。</summary>
        public const int ScheduleFwdBookkeep = 62;

        /// <summary>
        /// 2026-10-20 二十七刀：合批**采纳**（整表换回胜者试排表）前把批分组索引缓存就地对齐
        ///   （快照 + 桶 + (物料,工厂) 分桶）。与 <see cref="BatchGroupFullRebuild"/> 对照：
        ///   对齐成功 ⇒ 重建次数应大幅下降。
        /// </summary>
        public const int BatchGroupAdoptReconcile = 63;

        /// <summary>
        /// 2026-10-20 二十八刀：**试排副本**已与共享快照分叉时，独立建一份分组索引
        ///   （不动 `_batchGroupsReuse` / 快照）。与 <see cref="BatchGroupFullRebuild"/> 对照：
        ///   这些次数本以为是「表变短 ⇒ 整表重建」，其实是副本接管了缓存归属。
        /// </summary>
        public const int BatchGroupTrialFresh = 64;

        /// <summary>
        /// 2026-10-20 二十八刀：试排副本尾巴**临时**并进共享批分组索引（覆盖层，跑完回滚）。
        ///   替代「整表重建」（实测 8,334 × 913 µs）与「克隆」（8,334 × 583 µs）。
        /// </summary>
        public const int BatchGroupTrialOverlay = 65;
    }

    private static readonly AsyncLocal<Counters?> Current = new();

    /// <summary>
    /// V1_4 NEW-06：当前**是否已有**计数作用域。供 <c>FiniteCapacitySolver.SolveAsync</c> 判断
    ///   是否需要**自动建立**作用域（生产 Run 也产生可追溯 Run 快照），已开则由调用方作用域沿用。
    /// </summary>
    internal static bool HasScope => Current.Value is not null;

    /// <summary>
    /// 建立计数作用域。**必须在调用 `SolveAsync` 之前**建立（ExecutionContext 在 async 方法入口捕获）。
    /// ⚠ V1_4 NEW-06 起：`SolveAsync` **自身会在未开作用域时自动建立**（生产 Run 亦可追溯）；
    ///   调用方显式建立的作用域仍然优先（压测工装按此读本次 Run 的计数器）。
    /// </summary>
    internal static Scope BeginScope() => new();

    /// <summary>
    /// **2026-10-25 运行中进度探针**（2号位《单域可复现样本》§7.2）：当前 Run 的计数器**进程内引用**。
    ///
    /// 【为什么必须另存一份】`Current` 是 <see cref="AsyncLocal{T}"/> ⇒ **别的线程读不到**
    ///   （2号位 现在的取数方式是「事后托管转储」，因为运行中根本取不到）。本属性是普通静态引用，
    ///   后台线程可以直接轮询 `SolverDiagnostics.LiveCounters?.CurrentPhase` /
    ///   `SolverDiagnostics.LiveCounters?.Phase2DemandsProcessed`，**不必等求解返回**。
    /// 【边界】纯观测，**不参与任何业务判定**；只保证**单 Run 串行**（V1 求解本串行）。
    ///   并发多 Run 时为后者覆盖前者 —— 不承诺多 Run 语义。
    /// ⚠ 与既有 `Active`（`:1014`，`=> Current.Value`）**不是同一个东西**：那个仍是 AsyncLocal，
    ///   只能同线程读；本属性才是跨线程可见的那一个。
    /// </summary>
    private static Counters? _liveCounters;
    private static SolverLiveSnapshot? _lastSnapshot;

    /// <summary>
    /// **运行中进度探针**：当前 Run 的计数器**进程内引用**，任意线程可轮询。
    ///   可见性 = `internal`（本工程 + `LPS.APS.Tests`，经 `InternalsVisibleTo`）。
    ///   **其它程序集请用 public 的 <see cref="LiveSnapshot"/> / <see cref="LastSnapshot"/>。**
    ///   读写均走 `Volatile` ⇒ 跨线程可见性有保证。
    /// </summary>
    internal static Counters? LiveCounters => Volatile.Read(ref _liveCounters);

    /// <summary>
    /// **求解入参捕获钩子**（2026-10-26，为 PV190「最小复现切片」而加）。
    ///
    /// <para>非 <c>null</c> 时，每次 <see cref="FiniteCapacitySolver.SolveAsync"/> 入口**在动的第一件事之前**
    /// 用**原样**的 <see cref="DomainSolveRequest"/> 回调一次。默认 <c>null</c> ⇒ 零副作用。</para>
    ///
    /// <para><b>为什么是钩子、不是直接落盘</b>：本程序集架构红线是**纯内存、零 I/O**
    /// （<see cref="FiniteCapacitySolver"/> 类注释；实测全仓 <c>File.</c>／<c>StreamWriter</c>／
    /// <c>Directory.</c> 命中数 = 0）。序列化与落盘必须由**宿主**做 —— 1号位 不在这里开这个口子。</para>
    ///
    /// <para><b>宿主侧接法（一行，异常已由本类吞掉）</b>：</para>
    /// <code>
    /// SolverDiagnostics.OnSolveRequest = req =&gt;
    ///     File.WriteAllText(@"D:\tmp\pv190.json", JsonSerializer.Serialize(req, options));
    /// </code>
    ///
    /// <para>⚠ 回调在**求解线程同步执行**，且发生在 Phase1 之前 ⇒ 要序列化就在回调里做完，
    /// 别把 request 引用扔到别的线程延后处理（求解可能正在改写其集合）。</para>
    /// </summary>
    public static Action<DomainSolveRequest>? OnSolveRequest
    {
        get => Volatile.Read(ref _onSolveRequest);
        set => Volatile.Write(ref _onSolveRequest, value);
    }

    private static Action<DomainSolveRequest>? _onSolveRequest;

    /// <summary>
    /// 调用一次入参捕获钩子。**吞掉一切异常** —— 取证是观测，不许把跑批搞挂。
    /// 未设钩子时只做一次 <c>static</c> 读 ⇒ 热路径零影响（本方法只在 SolveAsync 入口调一次，非热路径）。
    /// </summary>
    internal static void CaptureSolveRequest(DomainSolveRequest request)
    {
        var sink = Volatile.Read(ref _onSolveRequest);
        if (sink is null) return;

        try
        {
            sink(request);
        }
        catch (Exception ex)
        {
            // 观测失败不改变求解行为：只留一行痕迹，继续排程。
            Console.WriteLine($"[SolverDiagnostics] 入参捕获钩子异常（已吞，不影响求解）: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>写当前阶段名（供运行中轮询读取）。纯观测。</summary>
    internal static void SetPhase(string phase)
    {
        if (Current.Value is { } c) { c.CurrentPhase = phase; Interlocked.Increment(ref c.Sequence); }
    }

    /// <summary>登记 Phase2 待处理需求总条数（进主循环前拍一次）。纯观测。</summary>
    internal static void SetPhase2DemandTotal(long total)
    {
        if (Current.Value is { } c) { c.Phase2DemandsTotal = total; Interlocked.Increment(ref c.Sequence); }
    }

    /// <summary>Phase2 主循环每进入一条需求 +1。纯观测。</summary>
    internal static void SetPhase2DemandProgress(long processed)
    {
        if (Current.Value is { } c) { c.Phase2DemandsProcessed = processed; Interlocked.Increment(ref c.Sequence); }
    }

    /// <summary>登记 Phase4 待修复单元总条数（进 Phase4 前拍一次）。纯观测。</summary>
    internal static void SetPhase4UnitTotal(long total)
    {
        if (Current.Value is { } c) { c.Phase4UnitsTotal = total; Interlocked.Increment(ref c.Sequence); }
    }

    /// <summary>Phase4 每处理完一个修复单元 +1。纯观测。</summary>
    internal static void SetPhase4UnitProgress(long processed)
    {
        if (Current.Value is { } c) { c.Phase4UnitsProcessed = processed; Interlocked.Increment(ref c.Sequence); }
    }

    // ══════════════════════════════════════════════════════════════════
    // 运行中 / 事后的**对外只读快照**（2号位 2026-10-26《PV190 第二次现场》§7.2）
    //
    // 既然已有 `LiveCounters`，为什么还要这一层：
    //   ① `Counters` 是 internal，跨程序集读不到；把它整块 public 等于把 56 个诊断字段
    //      **冻结成对外契约**（以后删一个探针就成了破坏性变更）⇒ 这里只放固定的 15 个字段。
    //   ② `LiveCounters` 在 `Scope.Dispose` 会被还原（通常是 null）⇒「跑完再取」拿不到。
    //      `LastSnapshot` 就是为此保留的最后一份。
    // 【边界】纯观测，不参与任何业务判定；**不新增调用点**（`Sequence` 复用既有写入点）。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 运行中 / 事后的只读快照（**逐字段拼装，不是原子快照**）。
    ///   撕裂判定：拼装前后 <see cref="Sequence"/> 不等 ⇒ 这是混合值。
    ///   <see cref="LiveSnapshot"/> 内部会自动重取一次；两次仍不稳定就照用（诊断场景可接受）。
    /// </summary>
    public sealed record SolverLiveSnapshot(
        string CurrentPhase,
        long Sequence,
        bool IsRunning,
        DateTime? CompletedAt,
        long Phase2DemandsTotal,
        long Phase2DemandsProcessed,
        long Phase4UnitsTotal,
        long Phase4UnitsProcessed,
        long BatchPlanCandidateTrials,
        long CloneOccupancy,
        long MergeScanCalls,
        long OccupancyRollbackUnmatched,
        long SlotSearches,
        long SlotSearchAttempts,
        long RunBatchPlanInvocations);

    /// <summary>
    /// 运行中快照：**任意线程、任意程序集**可读，不要求 `InternalsVisibleTo`。
    ///   当前没有活跃求解作用域时返回 `null`。
    /// </summary>
    public static SolverLiveSnapshot? LiveSnapshot
    {
        get
        {
            var c = Volatile.Read(ref _liveCounters);
            return c is null ? null : BuildSnapshot(c, isRunning: true, completedAt: null);
        }
    }

    /// <summary>
    /// 上一次求解**结束那一刻**的快照（写于 `Scope.Dispose`）。
    ///   与 <see cref="LiveSnapshot"/> 的唯一区别：求解返回/抛异常之后**仍然可读**。
    ///   进程内以最后一次求解为准（单槽、最后写入者胜；V1 求解串行）。
    /// </summary>
    public static SolverLiveSnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

    /// <summary>拼一份快照；`Sequence` 前后不一致时重取一次，两次仍不一致则照用最后一次。</summary>
    private static SolverLiveSnapshot BuildSnapshot(Counters c, bool isRunning, DateTime? completedAt)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = Volatile.Read(ref c.Sequence);
            var snap = Snapshot(c, before, isRunning, completedAt);
            if (Volatile.Read(ref c.Sequence) == before) return snap;
        }
        return Snapshot(c, Volatile.Read(ref c.Sequence), isRunning, completedAt);
    }

    private static SolverLiveSnapshot Snapshot(Counters c, long sequence, bool isRunning, DateTime? completedAt)
        => new(
            c.CurrentPhase,
            sequence,
            isRunning,
            completedAt,
            c.Phase2DemandsTotal,
            c.Phase2DemandsProcessed,
            c.Phase4UnitsTotal,
            c.Phase4UnitsProcessed,
            c.BatchPlanCandidateTrials,
            c.CloneOccupancy,
            c.MergeScanCalls,
            c.OccupancyRollbackUnmatched,
            c.SlotSearches,
            c.SlotSearchAttempts,
            c.RunBatchPlanInvocations);

    /// <summary>计数作用域：`Counters` 在 <see cref="Dispose"/> 后仍可安全读取（只是不再累加）。</summary>
    internal sealed class Scope : System.IDisposable
    {
        private readonly Counters? _previous;
        private readonly Counters? _previousActive;

        internal Scope()
        {
            Counters = new Counters();
            _previous = Current.Value;
            _previousActive = LiveCounters;
            Current.Value = Counters;
            // 运行中进度探针：把本 Run 的计数器挂到**进程内可见**的静态引用上（Volatile 写 ⇒ 跨线程可见）。
            Volatile.Write(ref _liveCounters, Counters);
        }

        /// <summary>本次作用域的计数器。</summary>
        public Counters Counters { get; }

        public void Dispose()
        {
            Current.Value = _previous;
            // 事先留一份终值：`_liveCounters` 一旦还原（通常是 null），「跑完再取」就再也拿不到了。
            Volatile.Write(ref _lastSnapshot, BuildSnapshot(Counters, isRunning: false, completedAt: DateTime.UtcNow));
            Volatile.Write(ref _liveCounters, _previousActive);
        }
    }

    /// <summary>
    /// 开始一个 Phase 计时。**未开 scope 时返回 `null`** ⇒ 调用方零开销、零行为影响
    /// （Phase 边界由 <see cref="FiniteCapacitySolver.SolveAsync"/> 埋点）。
    /// </summary>
    internal static System.Diagnostics.Stopwatch? StartPhase()
        => Current.Value is null ? null : System.Diagnostics.Stopwatch.StartNew();

    /// <summary>结束 Phase 计时并登记到对应 Phase 槽（<paramref name="phase"/> ∈ 1..5）。</summary>
    internal static void EndPhase(System.Diagnostics.Stopwatch? sw, int phase)
    {
        if (sw is null) return;
        sw.Stop();
        if (Current.Value is not { } c) return;

        switch (phase)
        {
            case 1: c.Phase1Ms = sw.ElapsedMilliseconds; break;
            case 2: c.Phase2Ms = sw.ElapsedMilliseconds; break;
            case 3: c.Phase3Ms = sw.ElapsedMilliseconds; break;
            case 4: c.Phase4Ms = sw.ElapsedMilliseconds; break;
            case 5: c.Phase5Ms = sw.ElapsedMilliseconds; break;
        }
    }

    internal static void CountBatchPlanCandidateTrial()
    {
        if (Current.Value is { } c) c.BatchPlanCandidateTrials++;
    }

    internal static void CountRoutingTrial()
    {
        if (Current.Value is { } c) c.RoutingTrials++;
    }

    internal static void CountCloneOccupancy()
    {
        if (Current.Value is { } c) c.CloneOccupancy++;
    }

    internal static void CountCloneTaskList()
    {
        if (Current.Value is { } c) c.CloneTaskList++;
    }

    internal static void CountCloneShares()
    {
        if (Current.Value is { } c) c.CloneShares++;
    }

    /// <summary>2026-10-19 性能定位：累计摘除 Task 数 Σk 与摘除时列表长度 Σn。</summary>
    internal static void CountDetachShape(int removedCount, int listLength)
    {
        if (Current.Value is { } c)
        {
            c.DetachTaskCount += removedCount;
            c.DetachListLen += listLength;
        }
    }

    /// <summary>2026-10-20 六次定位：占用窗二分未命中（回落线性扫）。纯观测。</summary>
    internal static void CountDetachOccupancyBinaryMiss()
    {
        if (Current.Value is { } c) c.DetachOccupancyBinaryMiss++;
    }

    /// <summary>2026-10-20 六次定位：摘除段触达一个资源时登记「是否真拷 + 该资源窗列表长度」。纯观测。</summary>
    internal static void CountDetachOccupancyShape(int windowCount, bool copied)
    {
        if (Current.Value is not { } c) return;

        c.DetachOccupancyWindowLen += windowCount;
        if (copied) c.DetachOccupancyCopies++;
    }

    /// <summary>2026-10-20 六次定位：产品时间线二分未命中（回落线性扫）。纯观测。</summary>
    internal static void CountDetachTimelineBinaryMiss()
    {
        if (Current.Value is { } c) c.DetachTimelineBinaryMiss++;
    }


    /// <summary>2026-10-19 二次定位：`RunBatchPlan` 总调用次数。</summary>
    internal static void CountRunBatchPlanInvocation()
    {
        if (Current.Value is { } c) c.RunBatchPlanInvocations++;
    }

    /// <summary>
    /// 2026-10-19 三次定位：登记**一次锚点擂台的规模**（`stageBatches.Count`）。
    ///   在擂台入口（`TryMergeOrSchedule` 的 `for (anchorIndex…)` 之前）调用一次。
    ///   纯观测、不参与任何业务判定；未开 scope 时只做一次 null 判断。
    /// </summary>
    internal static void CountAnchorLoop(int stageBatchCount)
    {
        if (Current.Value is not { } c) return;

        c.AnchorLoopEntries++;
        c.AnchorBatchCountSum += stageBatchCount;
        if (stageBatchCount > c.AnchorBatchCountMax) c.AnchorBatchCountMax = stageBatchCount;

        var bounds = Counters.AnchorBucketBounds;
        var bucket = bounds.Length;   // 默认落溢出桶（≥ 最后一项）
        for (var i = 0; i < bounds.Length; i++)
        {
            if (stageBatchCount < bounds[i])
            {
                bucket = i;
                break;
            }
        }

        c.AnchorBatchBuckets[bucket]++;
    }

    /// <summary>
    /// 2026-10-19 三次定位：登记**一次候选扫描的规模**（`FindMergeableStageBatches` 里
    ///   `groups.Count` —— 即「这一遍扫了多少个批分组」）。
    ///   纯观测、不参与任何业务判定；未开 scope 时只做一次 null 判断。
    /// </summary>
    internal static void CountMergeScan(int groupCount)
    {
        if (Current.Value is not { } c) return;

        c.MergeScanCalls++;
        c.MergeScanGroupSum += groupCount;
        if (groupCount > c.MergeScanGroupMax) c.MergeScanGroupMax = groupCount;
    }

    /// <summary>
    /// 2026-10-20 二十九次定位：登记**一次批分组采纳前对齐**的结果
    ///   （0 = 快路径 / 1 = 对齐成功 / 2 = 形状不符 / 3 = 前提不成立）。纯观测，不参与任何判定。
    /// </summary>
    internal static void CountBatchGroupReconcileOutcome(int outcome)
    {
        if (Current.Value is not { } c) return;
        if ((uint)outcome >= (uint)c.BatchGroupReconcileOutcomes.Length) return;
        c.BatchGroupReconcileOutcomes[outcome]++;
    }

    /// <summary>
    /// 2026-10-20 二十九次定位：登记**一次批分组整表重建**及其触发原因
    ///   （0 = 缓存失效 / 1 = 任务表变短 / 2 = 前缀失配）。纯观测，不参与任何判定。
    /// </summary>
    internal static void CountBatchGroupRebuildReason(int reason)
    {
        if (Current.Value is not { } c) return;
        if ((uint)reason >= (uint)c.BatchGroupRebuildReasons.Length) return;
        c.BatchGroupRebuildReasons[reason]++;
    }

    /// <summary>2026-10-20 十一次定位：`scheduledTasks` 整表替换次数。</summary>
    internal static void CountTaskTableReplaced()
    {
        if (Current.Value is not { } c) return;
        c.TaskTableReplaced++;
    }

    /// <summary>2026-10-20 十一次定位：`scheduledTasks` 尾部追加次数。</summary>
    internal static void CountTaskTableAppended()
    {
        if (Current.Value is not { } c) return;
        c.TaskTableAppended++;
    }

    /// <summary>2026-10-20 十二次定位：`ComputeDetachPositions` 被调用次数。</summary>
    internal static void CountDetachPosCall()
    {
        if (Current.Value is { } c) c.DetachPosCalls++;
    }

    internal static void CountSlotSearch()
    {
        if (Current.Value is { } c) c.SlotSearches++;
    }

    /// <summary>
    /// 2026-10-25（2号位 §4.3-2）：找槽**尝试**计数（`FindForwardSlot` / `FindBackwardSlot` 入口各一次）。
    ///   与 <see cref="CountSlotSearch()"/> 的区别见 <see cref="Counters.SlotSearchAttempts"/>。纯观测。
    /// </summary>
    internal static void CountSlotSearchAttempt(Counters? cached)
    {
        if (cached is not null) cached.SlotSearchAttempts++;
    }

    // ══════════════════════════════════════════════════════════════════
    // 2026-10-20 超热路径用的「计数器缓存」重载（纯性能，口径逐字不变）
    //
    // 背景：`AsyncLocal<T>.Value` 每次读约几十纳秒。`FindFirstAvailableSlot` 在 16k 档被调
    //   4,026 万次，每次要读 3 次（`CountSlotSearch` + `HotspotStart` + `HotspotEnd`）
    //   ⇒ 仅取值本身就吃掉秒级。调用方在入口 `Active` 取一次、存进字段，之后走下面这些重载
    //   就只剩一次字段读。
    //
    // 等价性：`Active` 返回的就是 `Current.Value` 的**同一个引用**；三个重载的判空/累加逻辑与
    //   无参版本逐行相同 ⇒ 计数结果**一个不差**。作用域未开时 `Active` 返回 null ⇒ 与改前一样不计数、
    //   `HotspotStart` 返回 0 ⇒ `HotspotEnd` 立刻返回。
    // ⚠ 只在「一次调用期间计数器不会变」的同步热路径用。若中途可能开/关作用域，仍须用无参版本。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>取当前作用域的计数器（未开作用域返回 null）。供热路径入口缓存到字段。</summary>
    internal static Counters? Active => Current.Value;

    /// <summary><see cref="CountSlotSearch()"/> 的缓存重载（语义逐字相同）。</summary>
    internal static void CountSlotSearch(Counters? cached)
    {
        if (cached is not null) cached.SlotSearches++;
    }

    /// <summary><see cref="HotspotStart()"/> 的缓存重载（语义逐字相同）。</summary>
    internal static long HotspotStart(Counters? cached)
        => cached is null ? 0L : System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary><see cref="HotspotEnd(long, int)"/> 的缓存重载（语义逐字相同）。</summary>
    internal static void HotspotEnd(Counters? cached, long startTimestamp, int hotspot)
    {
        if (startTimestamp == 0L) return;
        if (cached is null) return;
        cached.AddHotspotTicks(hotspot, System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp);
    }

    internal static void CountPhase4PropagatedTask()
    {
        if (Current.Value is { } c) c.Phase4PropagatedTasks++;
    }

    /// <summary>Phase5 `CompactGaps` 门控放行（进入压实优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5CompactionRun()
    {
        if (Current.Value is { } c) c.Phase5CompactionRuns++;
    }

    /// <summary>Phase5 `OptimizeSetupSequences` 门控放行（进入序列优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupOptimizationRun()
    {
        if (Current.Value is { } c) c.Phase5SetupOptimizationRuns++;
    }

    /// <summary>Phase5 `OptimizeSegment` 真正进入有界局部优化体（可重排段）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupSegmentOptimized()
    {
        if (Current.Value is { } c) c.Phase5SetupSegmentsOptimized++;
    }

    /// <summary>V1_3 F-01：按插入身份回滚占用表时**未命中**条目数 +n。纯观测，不参与判定。</summary>
    internal static void CountOccupancyRollbackUnmatched(int n)
    {
        if (n > 0 && Current.Value is { } c) c.OccupancyRollbackUnmatched += n;
    }

    /// <summary>V1_4 NEW-05：MIXED 回落正排时按身份撤销的倒排残留占用窗数 +n。纯观测，不参与判定。</summary>
    internal static void CountMixedBackwardRollbackWindows(int n)
    {
        if (n > 0 && Current.Value is { } c) c.MixedBackwardRollbackWindows += n;
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账 <c>Commit</c> 次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerCommit()
    {
        if (Current.Value is { } c) c.PiStageLedgerCommits++;
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账按身份回滚次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerRollback()
    {
        if (Current.Value is { } c) c.PiStageLedgerRollbacks++;
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁因输入未投影而不可评估 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateUnevaluated()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateUnevaluated++;
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁判越限/事实不可信并 Fail Closed +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlocked()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateBlocked++;
    }

    /// <summary>
    /// 2026-10-12：**有策略路径**完整评估后**过闸** +1（= 放行档）。
    ///   只在 `ApplyPiLegalQuantityGate` 走完全部判据、未 Conflict 时调用；三条早退不计。纯观测，不参与判定。
    /// </summary>
    internal static void CountPiLegalQuantityGatePassed()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGatePassed++;
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径被同一门禁判未满足 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlockedViaB010Fallback()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateBlockedViaB010Fallback++;
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径过闸并成批 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGatePassedViaB010Fallback()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGatePassedViaB010Fallback++;
    }

    /// <summary>2026-10-22：`QuantityAuthority=ORDER_ONLY` 使 B-009 门禁**不适用**并放行 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateSkippedOrderOnly()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateSkippedOrderOnly++;
    }

    /// <summary>
    /// 2026-10-22：`ORDER_ONLY` 因 PI 权威事实集**整体为空**被拒绝采信（回落 Fail Closed）+1。
    ///   纯观测，不参与判定 —— **非 0 即表示本次 Run 的 PI 事实装载有问题**。
    /// </summary>
    internal static void CountPiLegalQuantityGateOrderOnlyRejectedEmptyFacts()
    {
        if (Current.Value is { } c) c.PiLegalQuantityGateOrderOnlyRejectedEmptyFacts++;
    }

    /// <summary>
    /// R-1/R-2（2026-10-12，B-011）：经济软区间偏离的批数登记 +<paramref name="n"/>（**软**偏离、非硬失败）。
    /// 纯观测，不参与判定 —— 让「经济软参数被如实消费」可观测（10 §19「只有一种合法选择时记录限制来源」）。
    /// </summary>
    internal static void CountBatchPolicyInvalid(int n = 1)
    {
        if (Current.Value is { } c) c.BatchPolicyInvalids += n;
    }

    /// <summary>
    /// 2026-10-13：Batch Policy **状态不可获得**（输入契约缺陷）的需求数 —— 见
    /// <see cref="SolverRunCounters.BatchPolicyUnknowns"/>。纯观测，不参与判定。
    /// </summary>
    internal static void CountBatchPolicyUnknown(int n = 1)
    {
        if (Current.Value is { } c) c.BatchPolicyUnknowns += n;
    }

    internal static void CountEconomicBatchDeviation(int n = 1)
    {
        if (Current.Value is { } c) c.EconomicBatchDeviations += n;
    }

    /// <summary>
    /// V1_4 NEW-06：**可检索的 Run 预算正式快照**（进程级，**未开 scope 也可读**）。
    ///
    /// 【为什么需要它】V1_3 F-04 把预算快照写进 <see cref="Counters"/>，而 `Counters` 挂在
    ///   <see cref="AsyncLocal{T}"/> 上、**只有调用方显式 <see cref="BeginScope"/> 才存在** ⇒
    ///   正常生产 Run（`FiniteCapacitySolver.SolveAsync` 未自动开 scope）**根本没有任何记录** ⇒
    ///   复审 NEW-06 判「预算诊断不是生产 Run 正式快照」成立。
    ///   ⇒ 本记录**无论是否开 scope 都写**，并携带可追溯的 Run 版本源三元组
    ///     （`ScheduleRunId` / `StrategyProfileVersionId` / `ParameterSetVersionId`）。
    ///
    /// ⚠ 仍**不是**持久化载体：真正的 Run 快照落盘（`ScheduleRun` 表）归 2号位。
    ///   1号位 提供的是**进程内可检索**的正式快照（版本源 + 生效值），供联验 / 日志 / 诊断读取；
    ///   持久化与 Run 上下文完整性属 1↔2 契约（见回执归口）。
    /// </summary>
    internal sealed record SolverRunBudgetSnapshot(
        long? ScheduleRunId,
        long? StrategyProfileVersionId,
        long? ParameterSetVersionId,
        string BudgetVersion,
        string BudgetSource,
        int MaxOptimizationSplitCount,
        int MaxBatchCandidates,
        long ResolvedAtUtcTicks);

    private static SolverRunBudgetSnapshot? _lastRunBudgetSnapshot;

    /// <summary>
    /// V1_4 NEW-06：**按 Run 可检索**的预算正式快照（键 = `DomainSolveRequest.ScheduleRunId`）。
    ///
    /// 【为什么在 `LastRunBudgetSnapshot` 之外还要这一份】`LastRunBudgetSnapshot` 是「最后写入者胜」
    ///   的**进程级单槽** ⇒ 并发求解（生产多域并行 / 测试集合并行）下会被**别的 Run 覆盖**，
    ///   拿到的快照**不再属于你要查的那个 Run** ⇒ 不满足复审判词的「**可检索**」。
    ///   本表按 `ScheduleRunId` 分槽 ⇒ 每个 Run 的快照**互不覆盖**，可事后按 Run Id 精确检索。
    ///   `ConcurrentDictionary` 保证并发写入安全（读-改-写均原子）。
    ///
    /// ⚠ 仍**不是**持久化载体（进程内）；落盘 `ScheduleRun` 归 2号位。
    /// ⚠ `ScheduleRunId` 为 null 或 0 时**不入表**（无 Run 身份 ⇒ 无可检索键），只写单槽。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SolverRunBudgetSnapshot>
        _runBudgetSnapshotsById = new();

    /// <summary>
    /// V1_4 NEW-06：最近一次**已解析**的 Run 预算正式快照（进程级单槽）。
    /// **未开 scope 也非 null**（只要本进程跑过 `ResolveSolverBatchBudget`）。
    /// 并发求解下为「最后写入者胜」—— 需要**按 Run 精确检索**请用
    /// <see cref="TryGetRunBudgetSnapshot"/>（不被并发覆盖）。
    /// </summary>
    internal static SolverRunBudgetSnapshot? LastRunBudgetSnapshot => _lastRunBudgetSnapshot;

    /// <summary>
    /// V1_4 NEW-06：按 <paramref name="scheduleRunId"/> **精确检索**该 Run 的预算正式快照；
    ///   无该 Run 的记录（未跑过 / 未给 Run Id）返回 <c>null</c>。
    ///   与 <see cref="LastRunBudgetSnapshot"/> 不同，本方法的结果**不受其它 Run 并发写入影响**。
    /// </summary>
    internal static SolverRunBudgetSnapshot? TryGetRunBudgetSnapshot(long scheduleRunId)
        => _runBudgetSnapshotsById.TryGetValue(scheduleRunId, out var snapshot) ? snapshot : null;

    /// <summary>V1_4 NEW-06：仅供测试复位进程级快照与按 Run 检索表（避免用例间串味）。</summary>
    internal static void ResetLastRunBudgetSnapshotForTest()
    {
        _lastRunBudgetSnapshot = null;
        _runBudgetSnapshotsById.Clear();
    }

    /// <summary>
    /// V1_3 F-04 + V1_4 NEW-06：登记本 Run **实际生效**的技术预算快照（版本 / 取源 / 两个生效值 +
    ///   Run 版本源三元组）。**三写**：scope 打开时写 <see cref="Counters"/>（按 Run 隔离），
    ///   同时**无条件**写进程级 <see cref="LastRunBudgetSnapshot"/>（单槽，最后写入者胜），
    ///   并按 <paramref name="scheduleRunId"/> 写 <see cref="_runBudgetSnapshotsById"/>（按 Run 可检索）。
    /// </summary>
    internal static void RecordSolverBatchBudget(
        long? scheduleRunId,
        long? strategyProfileVersionId,
        long? parameterSetVersionId,
        string version,
        string source,
        int maxOptimizationSplitCount,
        int maxBatchCandidates)
    {
        var snapshot = new SolverRunBudgetSnapshot(
            scheduleRunId, strategyProfileVersionId, parameterSetVersionId,
            version, source, maxOptimizationSplitCount, maxBatchCandidates,
            DateTime.UtcNow.Ticks);

        _lastRunBudgetSnapshot = snapshot;

        // 按 Run 身份分槽（无 Run 身份不入表）—— 使「未开 scope 也**可按 Run 检索**」成立。
        if (scheduleRunId is { } runId && runId != 0L)
        {
            _runBudgetSnapshotsById[runId] = snapshot;
        }

        if (Current.Value is not { } c) return;
        c.SolverBatchBudgetVersion = version;
        c.SolverBatchBudgetSource = source;
        c.SolverBatchBudgetMaxOptimizationSplitCount = maxOptimizationSplitCount;
        c.SolverBatchBudgetMaxBatchCandidates = maxBatchCandidates;
    }

    // ══════════════════════════════════════════════════════════════════
    // 热点计时（**零分配**）：未开 scope 时 `HotspotStart()` 返回 0 ⇒ `HotspotEnd` 立刻返回
    //   ⇒ 生产路径只多两次比较，无行为影响、无分配。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>开始一次热点计时；未开 scope 返回 0（调用方原样传回 <see cref="HotspotEnd"/>）。</summary>
    internal static long HotspotStart()
        => Current.Value is null ? 0L : System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>结束热点计时并累计到 <paramref name="hotspot"/> 槽。</summary>
    internal static void HotspotEnd(long startTimestamp, int hotspot)
    {
        if (startTimestamp == 0L) return;
        if (Current.Value is not { } c) return;
        c.AddHotspotTicks(hotspot, System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp);
    }
}
