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

        /// <summary>
        /// R08（2026-10-29）：Phase2 **实际落下**的 Task 中，Setup 解析结果为 `InitialState` 的个数
        /// —— 即「本资源取到 `fromMaterialId = null`，按初始设备状态算 0 分钟」的落点位。
        ///
        /// <para><b>为什么只数 Phase2 落点、不数 `ResolveSetupCore` 调用</b>：后者在
        /// <see cref="SetupOptimizer.FindSlotWithDynamicSetup"/> 的有界迭代里被反复调用（试排/收敛重解），
        /// 数出来是「试排次数」而不是「最终排程里有多少个 0 分钟 Setup」，读数会骗人。</para>
        ///
        /// <para><b>读数含义的边界（不得当过硬的证据）</b>：`InitialState` 把
        /// 「真的没有前产品（计划期首件）」和「有前产品但追溯断链」写成同一种结果 ——
        /// 本计数**不能**区分这两者，只能告诉 4号位 「有这么多落点用的是这条不确定路径」。</para>
        /// </summary>
        public long SetupInitialStatePlacements;

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
        /// 2026-11-06 P2-A：外层批方案采纳命中**纯追加快路径**的次数
        ///   （胜者表长度 ≥ 旧表，且旧表与胜者表前缀**逐引用同序**）。
        ///   判据：`本次 ÷ (本次 + OuterAdoptFallback)` = 快路径覆盖率。纯观测，不参与任何判定。
        /// </summary>
        public long OuterAdoptPureAppend;

        /// <summary>2026-11-06 P2-A：外层批方案采纳**回落到旧路径**（`Clear + AddRange`）的次数。纯观测。</summary>
        public long OuterAdoptFallback;

        /// <summary>2026-11-06 P2-A：快路径实际追加的 Task 条数累计。纯观测。</summary>
        public long OuterAdoptAppendedTasks;

        /// <summary>
        /// 2026-11-06 P2-A：命中快路径时**旧表长度**的最小 / 最大值（0号位 v2.0 §三.5 要求记录）。
        ///   最小初值 <see cref="long.MaxValue"/>，从未命中则保持初值（判读时先看计数是否为 0）。
        /// </summary>
        public long OuterAdoptOldCountMin = long.MaxValue;

        /// <summary>2026-11-06 P2-A：命中快路径时**旧表长度**的最大值。纯观测。</summary>
        public long OuterAdoptOldCountMax;

        /// <summary>
        /// 2026-11-07 W1 候选级诊断（0号位 v3.0 §3.2「伪绿规则①」）：`RunDemandSchedule` 进入
        /// **合批路径**（`allowMerge=true`）的次数。判据：本值 &gt; 0 才说明 Merge **真的被尝试过**；
        /// 只看最终结果是独立 Task 就宣称「Merge 失败后回退」属于伪绿。纯观测，不参与判定。
        /// </summary>
        public long MergeAttempts;

        /// <summary>合批路径**命中**（并入既有 Task / 改由合并链承接）的次数。</summary>
        public long MergeHits;

        /// <summary>合批路径**未命中**（返回空/未产生合并）的次数。与 <see cref="MergeAttempts"/> 之差即命中数。</summary>
        public long MergeMisses;

        /// <summary>
        /// 2026-11-07 W2（0号位 v3.0 §2.2「拒绝/省略原因至少内部可辨」）：多工序需求走到合批分支，
        /// **一个合法锚点都没有**（`FindMergeableStageBatches` 返回空，且去掉身份守卫后仍为空）
        /// 的次数 ⇒ 标签 `NOT_ELIGIBLE`。纯观测。
        /// </summary>
        public long MergeNotEligible;

        /// <summary>
        /// 2026-11-07 W2：**有锚点，但全部被执行批身份守卫挡掉**的次数 ⇒ 标签 `IDENTITY_UNREPRESENTABLE`。
        /// 判据：同一输入下把 `requireIdentityPreserving` 关掉重扫，锚点数 &gt; 0。
        /// ⇒ 这与 `NOT_ELIGIBLE` 是**完全不同**的结论：前者是载体表达力问题（归 2号位），后者是业务不成立。
        /// 纯观测。
        /// </summary>
        public long MergeIdentityUnrepresentable;

        /// <summary>
        /// 2026-11-07 v5.0 §1.4.1：**找到真实合批锚点**的需求批次数（`stageBatches.Count &gt; 0`）。
        /// 判据链：`AnchorFound → TrialStarted → (HardRejectedCalendar | ComparedRejected | Adopted)`。
        /// 只有这一整条链都成立，才能说「合并真的试过并因为某个具体原因没成」。
        /// 纯观测。
        /// </summary>
        public long MergeAnchorFound;

        /// <summary>v5.0 §1.4.1：真正**开始重排**（进 `TryMergeDemandIntoStageBatch`）的次数。</summary>
        public long MergeTrialStarted;

        /// <summary>v5.0 §1.4.1：试排**因硬约束（日历/资源）产不出任何 Task** 的次数。</summary>
        public long MergeHardRejectedCalendar;

        /// <summary>v5.0 §1.4.1：试排产出了合法合批方案，但在冻结层序比较中**落败**的次数。</summary>
        public long MergeComparedRejected;

        // ─────────────────────────────────────────────────────────────────────
        // 2026-11-10 A4（0号位《Batch_Code_Review_and_Action_Plan v1.0》§4-A4）
        //   —— 多 Op 擂台必须区分**三个不同事实**：
        //     ① `MergeTrialStarted`  = 试排了几次（锚点擂台出手次数）
        //     ② `MergePhysicalFeasible` = 其中**物理可行**（`TryMergeDemandIntoStageBatch` 返回非 null）
        //     ③ `MergeFinallyAdopted` = 擂台结束后**真的采纳合批**的需求批次数（`bestAnchorIndex >= 0`）
        //   0号位 逐字要求「出具 physicalFeasible、comparedWins、finallyAdopted 三个**不同**计数」。
        //   恒等式（可当场验算）：`MergePhysicalFeasible = MergeTrialStarted − MergeHardRejectedCalendar`。
        //   ⚠ 三者**不是**同一个事实的三种说法：一个需求可以试排 5 个锚、2 个物理可行、
        //     1 个比较胜出，最后仍可能因甲方案选中的那个被判平 ⇒ 最终不采纳 ⇒ `MergeFinallyAdopted = 0`。
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>A4：**物理可行**的锚批试排次数（试排产出了合法合批方案，与择优胜负无关）。</summary>
        public long MergePhysicalFeasible;

        /// <summary>A4：物理可行且**本次比较胜出**（`mergeWins == true`）的次数。不等同于最终采纳。</summary>
        public long MergeComparedWins;

        /// <summary>A4：擂台结束后**真正采纳合批**的需求批次数（`bestAnchorIndex &gt;= 0`）。</summary>
        public long MergeFinallyAdopted;

        // ─────────────────────────────────────────────────────────────────────
        // 2026-11-10 A6（0号位《Batch_Code_Review_and_Action_Plan v1.0》§4-A6 / §7.5）
        //   —— **单 Op** 锚表扫描的三个量。此前只有多 Op 的 `MergeScanCalls` /
        //     `MergeScanGroupSum` / `MergeScanGroupMax`，单 Op 一侧完全是黑盒，
        //     导致「快路到底省了多少次扫描」无法量化。
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>A6：进入 `ScanMergeCandidateRange` 的次数（= 单 Op 真的扫了一次历史 Task 表）。</summary>
        public long SingleOpScanCalls;

        /// <summary>A6：单 Op 扫描过的**历史 Task 元素总数**（每次扫描的元素数累计）。</summary>
        public long SingleOpScannedTaskCount;

        /// <summary>A6：单 Op 扫描后**留下的候选锚批个数**累计（不含被 PI/身份/路径挡掉的）。</summary>
        public long SingleOpCandidateCount;

        /// <summary>
        /// 2026-11-06 BATCH-02（0号位 e31a6fb 指令）：**「1 批」作为待试排候选被纳入**的次数。
        ///   判据：本值应 ≈ `AllowSplit=true` 的需求数；若长期远小于它，说明整批仍被某处排除。
        ///   纯观测，不参与任何判定。
        /// </summary>
        public long BatchOneBatchCandidate;

        /// <summary>
        /// 2026-11-06 BATCH-02.2：**被技术预算挤掉**的候选种数累计。
        ///   ⚠ 语义边界：被挤掉 ≠ 物理不合法。它只说明「这一轮没去试排」，不得当作排不下的原因。
        /// </summary>
        public long BatchCandidateOmittedByBudget;

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

        /// <summary>
        /// 2026-11-09 Step 1：产品时间线 `Place`（写入）调用次数。纯观测。
        ///   用途：区分「写得更多了」与「每次写更贵了」—— 分桶画像里 `TimelinePlace` 耗时涨 13.9 倍，
        ///   但只有次数才能判定是哪种。
        /// </summary>
        public long TimelinePlaceCalls;

        /// <summary>
        /// 2026-11-09 Step 1：按<b>调用来源</b>分桶的写入次数（下标 = <see cref="TimelinePlaceSource"/>）。纯观测。
        ///   分桶画像已证明「每条需求写入次数 27 倍增长而试排次数不变」⇒ 必须定位到具体调用点。
        /// </summary>
        public readonly long[] TimelinePlaceBySource = new long[8];

        /// <summary>
        /// 2026-11-09：合批锚点擂台按「同 PI / 跨 PI / PI 缺失」分类的计数（应 0号位 资料要求）。
        ///   下标 = <c>类别 * 4 + 指标</c>（类别 0=同PI / 1=跨PI / 2=PI缺失；指标见 <see cref="MergePiMetric"/>）。
        ///   用途：回答「跨 PI 的合批候选是不是白白消耗了大量完整试排」。纯观测，不参与任何判定。
        /// </summary>
        public readonly long[] MergePiStats = new long[12];

        /// <summary>2026-11-09：本次求解收到的需求总条数 / 其中 `ProductionInstructionNo` 非空白的条数。纯观测。</summary>
        public long SingleOpMergeSkippedNoPi;

        /// <summary>
        /// 2026-11-10 P0-03：单工序锚批因**已承接份额中存在跨 PI 成员**被拒绝的次数（纯观测）。
        ///   只看 `SourceDraftId` 不足以证明整批同 PI —— 一个 Task 可能此前已吸收多条需求。
        /// </summary>
        public long SingleOpAnchorMixedPiRejected;

        public long PiFillTotal;
        public long PiFillPresent;

        /// <summary>
        /// 2026-11-09：「PI 缺失」类的**成因分解**（把 99.4% 拆开，避免把"查表失败"误算成"没填 PI"）。
        ///   [0] 当前需求自己没有 PI；[1] 锚批代表需求在 `demandByKey` 里查不到；[2] 锚批代表需求有键但 PI 为空。
        /// </summary>
        public readonly long[] MergePiMissingReason = new long[3];

        /// <summary>
        /// 2026-11-09：累计在**合批试排内部**消耗的 `BatchPlanTrial` 毫秒数。
        ///   用途：`BatchPlanTrial` 热点会嵌套在合批试排里；用它做差，才能把
        ///   「拆批方案试排」拆成「合批里的」与「独立路径上的」两部分。纯观测。
        /// </summary>
        public double BatchTrialMsInMerge;

        /// <summary>
        /// 2026-11-09 Step 1：产品时间线 `Compact`（增量压缩回基线）运行次数。纯观测。
        ///   `Compact` 会整表复制外层资源字典并归并受影响资源的整条 list ⇒ 是「每次更贵」的头号嫌疑。
        /// </summary>
        public long TimelineCompactRuns;

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

        /// <summary>2026-10-26 A-4 第二刀：`RollbackInsertedOccupancyWindows` 的**调用次数**（形状峰值的分母）。纯观测。</summary>
        public long OccupancyRollbackCalls;

        /// <summary>2026-10-26 A-4 第二刀：单次回滚**日志条数 `k`** 的峰值。搬移量 ≈ `k × W`，两个都要看。</summary>
        public long OccupancyRollbackInsertMax;

        /// <summary>2026-10-26 A-4 第二刀：单次回滚**被触及资源窗表长度 `W`** 的峰值（同一资源上的窗条数）。</summary>
        public long OccupancyRollbackWindowMax;

        /// <summary>
        /// 2026-10-27 形状探针：`CloneOccupancy` 克隆时**源字典键数**（资源数）的峰值 / 累计值。
        ///   【为什么必须量】PV190 直测 `CloneOccupancy` = 202,119 次 × **696 µs/次**，
        ///     而该方法的全部工作只有一句 `new Dictionary&lt;int, List&lt;TimeWindow&gt;&gt;(source)`
        ///     —— 写时复制的**浅拷**，不碰任何窗表内容。696 µs 只能来自「字典本身很大」。
        ///     键数不读出来，「缩小拷贝」往哪个方向改全是猜的（且空间/时间两条路的修法相反）。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long CloneOccupancyKeyMax;
        public long CloneOccupancyKeySum;

        /// <summary>
        /// 2026-11-07 v5.0 A1：`CloneOccupancy` 源字典**键数**的幂次桶（只记分布，不记样本）。
        ///   桶 `i`（i ≥ 1）覆盖 `[2^(i-1), 2^i - 1]`，桶 0 = 0 键。
        ///   有了它才算得出 P50/P95/P99；旧字段只有峰值，回答不了「大多数时候拷多长」。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long[] CloneOccupancyKeyBuckets = new long[33];

        /// <summary>2026-10-27 形状探针：`new List&lt;FinalTaskDraft&gt;(scheduledTasks)` 克隆时**任务表长度**的峰值。</summary>
        public long CloneTaskListLenMax;

        /// <summary>2026-11-07 v5.0 A1：`CloneTaskList` **任务表长度**的幂次桶（桶定义同 <see cref="CloneOccupancyKeyBuckets"/>）。</summary>
        public long[] CloneTaskListLenBuckets = new long[33];

        /// <summary>2026-11-07 v5.0 A1：`CloneTaskList` 的**调用次数**（分布百分比的分母）。</summary>
        public long CloneTaskListCalls;

        /// <summary>
        /// 2026-11-07 v5.0 A3：Routing 候选**总条数**（每条候选 = 一次完整试排）。
        ///   【为什么先量这个】A3 允许记忆化的前提是「七项冻结输入全相等」。
        ///     若候选之间工序序列本就条条不同，那么记忆化**一分钱也省不到** ——
        ///     先证明重复存在，再谈缓存，否则等于凭感觉改算法。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long RoutingCandidateTotal;

        /// <summary>2026-11-07 v5.0 A3：**工序序列与本需求前面某条候选完全相同**的候选条数（= 可记忆化的上界）。</summary>
        public long RoutingCandidateDuplicateOps;

        /// <summary>2026-11-07 v5.0 A3：每需求候选条数的幂次桶（桶定义同 <see cref="CloneOccupancyKeyBuckets"/>）。</summary>
        public long[] RoutingCandidatePerDemandBuckets = new long[33];

        /// <summary>2026-11-07 v5.0 A3：单条候选的工序数峰值。</summary>
        public long RoutingCandidateOpsMax;

        /// <summary>
        /// 2026-10-27 形状探针：批分组**覆盖层尾部长度**（`AppendBatchGroupsOverlayTail` 的 `tasks.Count - from`）
        ///   的峰值 / 累计值。判据用途：该步骤是 O(尾部)，2,301 µs/次 只可能来自尾部很长 ⇒ 先量长度。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long BatchGroupOverlayTailMax;
        public long BatchGroupOverlayTailSum;

        /// <summary>2026-10-27 形状探针：覆盖层**回滚键数**（`counts.Count + created.Count`）的峰值。纯观测。</summary>
        public long BatchGroupOverlayRestoreKeyMax;

        /// <summary>
        /// 2026-10-27 形状探针：进 `BuildBatchGroupsIndex` 时 **`scheduledTasks.Count` 与快照长度之差** 的峰值。
        ///   判据用途：差值就是前缀比对 + 尾部追加要扫的长度；它也决定了 `IsPrefixSameReferences` 的 O(N)。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long BatchGroupPrefixGapMax;

        /// <summary>
        /// 2026-11-06 顾问复核：每次 `PhaseThreeDiagnostics.Diagnose` 入口的 `constraints.LockedTasks.Count`
        ///   累计值与峰值。判据用途：`DiagnoseDelayReason` 对**每条延期需求**做一次
        ///   `LockedTasks.Values.FirstOrDefault(...)` 线性扫描 ⇒ 总工作量 = 延期需求数 × 本值。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        public long DiagnoseLockedTasksSum;
        public long DiagnoseLockedTasksMax;

        /// <summary>
        /// 2026-11-06 顾问复核：**外部/上游 ResourceBlock 条数**的累计值与峰值。
        ///   ⚠ 口径已随第2步整改变更：原先在每条延期需求的 `HasResourceBlockOverlap` 里各登记一次
        ///   （Sum = 延期需求数 × 块数）；整改后改为每次 `Diagnose()` 建索引时登记一次，
        ///   ⇒ Sum = 各次 Diagnose 的块数之和，除以 `Phase3Diagnose` 调用次数即每次平均块数。
        ///   峰值仍为单次最大值，可直接当「本域块规模」读。纯观测。
        /// </summary>
        public long DiagnoseExternalBlockSum;
        public long DiagnoseExternalBlockMax;
        public long DiagnoseUpstreamBlockSum;
        public long DiagnoseUpstreamBlockMax;

        /// <summary>
        /// 2026-10-27 诊断：候选并行的**执行形状**。
        ///   【为什么必须量】run24（候选并行 ON）的 `p2_done` @600s 只比 run23（OFF）高 17%，
        ///     而几乎所有热点「单次耗时」齐刷刷掉 18~19% —— 这是「这台机器这一次跑快了」的形状，
        ///     不是并行的形状（真并行只会压低**外层** `BatchPlanTrial`，压不动 `LandingRun` 这类区外项）。
        ///   并行的唯一硬证据：**候选耗时之和 ≫ `Parallel.For` 墙钟**。
        ///     比值 ≈ 1 ⇒ 根本没并行；比值 ≈ 候选数 ⇒ 真并行。
        ///   纯观测，不参与任何业务判定。
        /// </summary>
        /// <summary>
        /// 2026-10-27 四十四刀：**外层**（`SelectBestBatchPlan` 的批方案候选）并行/串行派发次数。
        ///   【为什么单独计数】四十三刀栽过一次 —— Unit 全绿、但 `[SHAPE] 并行批=0`，
        ///     说明并行分支一次都没进过，所谓「并行」只是纸面。这次把外层派发也钉成读数，
        ///     **没有这两个数就不许说「并行生效了」**。
        /// </summary>
        public long OuterBatchPlanParallel;
        public long OuterBatchPlanSerial;

        public long CandidateParallelDispatches;   // `parallelCandidates == true` 的批数
        public long CandidateSerialDispatches;     // 走串行分支的批数
        public long CandidateDispatchCountSum;     // 上述两类的候选数之和
        public long CandidateTrialWorkTicks;       // 各候选 `RunCandidate` 耗时之和
        public long CandidateParallelWallTicks;    // `Parallel.For` 本体墙钟之和

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
        /// 2026-11-02：**完全缺配的合法无 PI 来源 C 桶**按 v1.15 §1320 取 `Q_C` 作经济软 Preferred
        ///   并成功形成一个执行批的次数。
        ///   口径：`ProductionInstructionNo` 空 + `IsMissingPolicy` + `NetOutputQty &gt; 0`
        ///   ⇒ 出一个 `Q_C` 批（**不进** PI 合法量门禁 —— §1319「无PI来源时不得猜造PI或借用PI数量公式」）。
        ///   与 <see cref="PiLegalQuantityGatePassedViaB010Fallback"/> **分列**：那条是「有 PI、过了门禁」，
        ///   本条是「无 PI、门禁**不适用**」。纯观测、不参与任何业务判定。
        /// </summary>
        public long BatchSoftPreferredNoPi;

        /// <summary>
        /// 2026-11-03：**Stage 交接等待**（`StageHandoffWaitRules`）落地后的可观测性。全部纯观测、不参与判定。
        ///   `Total` = 载荷里的规则条数；`Active` = `IsActive==true` 的条数（每轮**覆写**，非累加）；
        ///   `Matched` = 命中次数（同一工序可能多次）；`Applied` = ELAPSED 真加进起点的次数；
        ///   `SkippedWork` = WORK 基准因缺日历级别而跳过的次数；`AppliedMinutes` = 累计加进起点的分钟数。
        /// </summary>
        public long StageHandoffRulesTotal;
        public long StageHandoffRulesActive;
        public long StageHandoffMatched;
        public long StageHandoffApplied;
        public long StageHandoffSkippedWork;
        public long StageHandoffAppliedMinutes;

        /// <summary>
        /// 2026-11-03：`ResolvePiStageContext` 的**分因**计数（定位 `PI_LEGAL_QTY_CONTRACT_PENDING`）。
        ///   返回 null 有四种互斥成因，此前全被并成一句「缺 Stage 合法自由量上下文」，无法归因。
        ///   `PiStageIndexEntries` / `PiStageIndexDistinctPi` 是输入规模（每轮**覆写**）；
        ///   其余为调用次数累加。
        /// </summary>
        public long PiStageIndexEntries;
        public long PiStageIndexDistinctPi;
        public long PiStageResolveCalls;
        public long PiStageNull_IndexEmpty;
        public long PiStageNull_PiMissing;
        public long PiStageNull_StageMissing;
        public long PiStageNull_KeyNotFound;
        public long PiStageResolveHit;

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

        // ── A-6（2026-11-02 落码）：三个需求口账计数器 ──
        //   背景：2026-10-25 我方《FAMILY_X 四份对账回执》认领 A-6 时就点了这三个名字
        //   （`DemandsReceived` / `DemandQtySum` / `DemandsWithLandedTask`），当轮**没有落码** ——
        //   2号位 2026-11-02 回执 §五.3「全仓零命中，请确认是还没落、还是改了名」。
        //   如实答：**是没落**，不是改名。此处补齐。
        //   ⚠ 只在本程序集 / 测试宿主内可见（**不进** `SolverLiveSnapshot` —— 那是 2号位 的对外契约，
        //     往 public record 加字段＝破坏性变更）。
        //   纯观测：不参与任何业务判定。

        /// <summary>
        /// 本次求解**收到的需求条数**（按 `LogicalDemandKey` 去重；同一需求重复下发只算一条）。
        ///   取的是 **Solver 入口入参**，不是 `SolveSummary.TotalDrafts`（后者是 **Task 条数**，口径不同）。
        /// </summary>
        public long DemandsReceived;

        /// <summary>
        /// 本次求解**收到需求的净量合计**（`NetOutputQty`，按 `LogicalDemandKey` 去重后求和）。
        ///   口径：**需求侧净量**，不是已排量、也不是 Task 加工量。
        /// </summary>
        public decimal DemandQtySum;

        /// <summary>
        /// **至少落了一条 `FinalTask` 的需求条数**（按 `SourceDraftId` 归属回需求键后去重）。
        ///   ⚠ 锁定任务（`SourceDraftId = 既有 Task.DraftId`）**不计入** —— 它不是需求。
        ///   ⚠ 与 `DemandsReceived` 之差 ≠ 「未排需求数」：硬失败需求与部分落地的需求都在里面。
        /// </summary>
        public long DemandsWithLandedTask;

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

        /// <summary>
        /// 热点数量（= <c>SolverDiagnostics.Hotspot</c> 里**常量编号的最大值 + 1**）。
        ///
        /// <para>⚠ **加新槽必须同步加这个数**。2026-11-06 实测踩过：槽位已编到 76，这里还停在 68，
        /// 结果 <see cref="AddHotspotTicks"/> 的 `&lt; HotspotCount` 护栏把 68~76 这 9 个槽
        /// **静默丢弃**（不报错、不计数、恒为 0），而 <see cref="HotspotMs"/> 无护栏直接越界抛异常。
        /// 表现就是「埋点写了、编译过了、读数全 0」，极难察觉。</para>
        ///
        /// <para>已被 <c>LPS.APS.Tests/Unit/SolverDiagnosticsSlotTests.cs</c> 钉死：
        /// 反射取最大槽号 + 1 必须等于本值，否则单测红。</para>
        /// </summary>
        public const int HotspotCount = 78;

        // 2026-10-27 四十二刀：**并发安全**（候选并行前置条件）。
        //   原来用 `+=` / `++`，多线程同时写会丢更新 —— 而这两个数组正是判断「改快了没有」的唯一依据，
        //   丢更新会让读数**偏小**，等于把优化效果说小。改 `Interlocked` 后读数为真值。
        //   ⚠ 计数器是**只写遥测**：改它不可能影响任何排程判定（最坏只是数字偏一点），
        //     所以这一步对「结果逐位相同」零风险。
        internal void AddHotspotTicks(int hotspot, long ticks)
        {
            if ((uint)hotspot < HotspotCount)
            {
                System.Threading.Interlocked.Add(ref _hotspotTicks[hotspot], ticks);
                System.Threading.Interlocked.Increment(ref _hotspotCalls[hotspot]);
            }
        }

        /// <summary>
        /// 取热点累计耗时（ms）。越界返回 0（与 <see cref="HotspotCallCount"/> 同口径）——
        /// 原先这里无护栏，槽号超过 <see cref="HotspotCount"/> 会让打印探针抛
        /// <c>IndexOutOfRangeException</c> 并连带吞掉后面所有分项读数。
        /// </summary>
        public double HotspotMs(int hotspot)
            => (uint)hotspot < HotspotCount
                ? _hotspotTicks[hotspot] * 1000.0 / System.Diagnostics.Stopwatch.Frequency
                : 0d;

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

        /// <summary>
        /// 2026-10-26 A-4 第二刀：`RollbackInsertedOccupancyWindows` **整个方法**的墙钟（含内部分配与清理）。
        ///   为什么加这个槽：回滚段原先**落在所有子计时之外** —— `MergeBatchDetach` 在调用点之前就已
        ///   `HotspotEnd`，回滚在其后开始 ⇒ 剖面里它只是「`TryMergeStageBatch` 的未分解余量」，没有名字。
        ///   2号位 2026-10-26 现场只能靠托管栈采样（21 次中 15 次落此方法），拿不到秒数。
        ///   纯观测：不改任何判定、不参与业务。
        /// </summary>
        public const int OccupancyRollback = 66;

        /// <summary>
        /// 2026-10-27 覆盖层拆解：`RestoreBatchGroupsOverlay` 单独计时（`BatchGroupTrialOverlay` 的**第一半**）。
        ///   为什么加这个槽：PV190 实测 `BatchGroupTrialOverlay` = 62,906 次 × **2,301 µs = 144,761 ms（≈24%）**，
        ///     是当前**唯一**超过 20% 的具名单项。而该覆盖层由两个**理论上都是 O(尾部)**的步骤组成
        ///     （① 撤销上一批并进去的尾巴 ② 并入本批尾巴）—— 慢成这样必有一半的「尾部」其实很长。
        ///   拆开后 `AppendBatchGroupsOverlayTail` 的耗时 = 本槽 − 上面的 `BatchGroupTrialOverlay` 差值，
        ///   不必再猜是哪一半。纯观测：不改任何判定、不参与业务。
        /// </summary>
        public const int BatchGroupOverlayRestore = 67;

        // ── 2026-11-06 性能顾问复核：Phase3/Phase5 诊断链此前**只有 Phase 级墙钟**，没有方法级槽。
        //    Run 999401 实测：JS 的 Phase5 扣掉已测槽后残差 30 分 36 秒，首次 Phase3 30 分 33 秒；
        //    QT 残差 15 分 38 秒，首次 Phase3 15 分 37 秒 —— 两次诊断几乎同长。
        //    `PhaseFiveCompression.cs:460` 确有第二次 `new PhaseThreeDiagnostics().Diagnose(...)`，
        //    但外层**无任何计时**。以下七槽即为把这一次（以及 Phase3 内的三处扫描）拆开。
        //    **纯观测**：不改任何判定、不改出口、不参与业务。未开 scope 时 `HotspotStart()` 返回 0 ⇒ 零开销。──

        /// <summary>
        /// `PhaseThreeDiagnostics.Diagnose` **整个方法**（Phase3 首诊与 Phase5 末诊**都会累加**，
        ///   故本槽次数 = 诊断调用次数，用于直接证实「是不是调了两次」）。
        ///   要分开看各自耗时：Phase5 那一次另计在 <see cref="Phase5FinalDiagnose"/>（嵌套于本槽）。
        /// </summary>
        public const int Phase3DiagnoseTotal = 68;

        /// <summary>
        /// Phase5 末端 `finalDiagnostics = new PhaseThreeDiagnostics().Diagnose(...)` 的墙钟。
        ///   判据：本槽 vs 首次 `Phase3Ms` 是否同量级 ⇒ 二次诊断是否被重复付费。
        ///   **业务上不许删**（Phase4/5 会改 Task 资源与时间，旧解释事实已过时）。
        /// </summary>
        public const int Phase5FinalDiagnose = 69;

        /// <summary>
        /// Phase5 在二次诊断**之后**、返回之前的**结果对象组装**（`FinalTaskDraft` / `AllocationTaskShare` /
        ///   `PhysicalPeggingDraft` / `ExplanationFact` 投影）。
        ///   为什么量它：CYL Phase5 残差 6 分 56 秒，而 CYL 首次 Phase3 只有 5 秒 ⇒ CYL 的残差**不是**二次诊断，
        ///     446 万条 Task 的对象构造才是主嫌。本槽即用来证实/否证这一点。
        /// </summary>
        public const int Phase5ResultAssembly = 70;

        /// <summary>Phase3 `Diagnose` 内：**延期需求**循环（每轮含一次 `DiagnoseDelayReason`）。次数 = 延期需求数。</summary>
        public const int Phase3DelayedLoop = 71;

        /// <summary>
        /// `DiagnoseDelayReason` 第 1 步的**锁命中判定**。
        ///   整改前 = `constraints.LockedTasks.Values.FirstOrDefault(...)` 全表线性扫
        ///   （总量 = 延期需求数 × 锁定表长）；2026-11-06 第1步整改后 = `HashSet.Contains` O(1)。
        ///   整改后本槽应≈0，若仍占量则说明索引没被复用。次数同 <see cref="Phase3DelayedLoop"/>。
        /// </summary>
        public const int Phase3LockScan = 72;

        /// <summary>
        /// `DiagnoseDelayReason` 第 2 步的**资源阻挡块检索**。
        ///   整改前 = 每次调用重建外部+上游 ResourceBlock 列表再 Task×Block 嵌套扫（无条件付费）；
        ///   2026-11-06 第2步整改后 = 只查自己 `ResourceId` 的桶，重建移到 `Diagnose()` 入口一次。
        ///   整改后本槽应≈0；若仍占量先查索引是否真的传进来了。次数同 <see cref="Phase3DelayedLoop"/>。
        /// </summary>
        public const int Phase3BlockOverlap = 73;

        /// <summary>
        /// `Diagnose()` 里**全域**那一次 <c>CalculateResourceUtilization</c>（每个诊断调用一次）。
        ///   底层 <c>foreach (var window in calendar)</c> **无二分**，遍历整本资源日历。
        ///   2026-11-06 第3步：原先本槽与「每需求那一次」混在一起累加，无法拆分；
        ///   现已拆开，逐需求那次单列在 <see cref="Phase3UtilizationPerDemand"/>。
        ///   顾问 §四.3 指出「逐需求利用率」与「全域利用率」不是同一业务口径，口径问题另议，本槽只量耗时。
        /// </summary>
        public const int Phase3Utilization = 74;

        /// <summary>
        /// Phase5 结果组装里的 `StageTimingNodeBuilder.Build`（`DemandCompletions` 末端/纯内存时间节点）。
        ///   顾问 §三点五 点名要求单列：它此前落在 Phase5 未计时的尾部里。
        ///   **不改求值次序** —— 由同文件私有包装 `TimedStageTimingBuild` 在原位置调用（对象初始化器成员求值次序不变）。
        /// </summary>
        public const int Phase5StageTimingBuild = 75;

        /// <summary>
        /// `DiagnoseDelayReason` 里**逐延期需求**那一次 `CalculateResourceUtilization`（0号位 指令第3步的测量对象）。
        ///   次数 = 延期需求数。判据：本槽 ÷ <see cref="Phase3Utilization"/> ⇒ 逐需求利用率到底值不值得缓存。
        ///   **先测后改**：本槽读数出来之前不许动这段逻辑，更不许拿全域利用率顶替逐需求
        ///   （分子/资源集合/统计上界都可能不同，会改 `CAPACITY` 判定）。
        /// </summary>
        public const int Phase3UtilizationPerDemand = 76;

        /// <summary>
        /// 2026-11-06 P2-A：**外层批方案采纳段**的墙钟（前缀校验 + 索引对齐 + 追加/替换）。
        ///   与 <see cref="BatchGroupAdoptReconcile"/>（嵌套在内，只覆盖索引对齐那一步）分开：
        ///   本槽才是采纳总价，两者的差 ≈ 前缀校验 + 真正搬元素的时间。
        ///   A/B 判据：本槽 ÷ `DemandSchedule` 或 Phase2Ms，看采纳占多少。
        /// </summary>
        public const int OuterAdopt = 77;
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

        // 2026-11-05：阶段一换就**立刻**打一行信标，不等下一个 15 秒 tick。
        //   阶段切换只在 Phase1..Phase5 边界发生（全程 ≤5 次），不在热路径上。
        Volatile.Read(ref _activeBeacon)?.Tick();
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

    /// <summary>
    /// A-6（2026-11-02）：登记需求口账三件套。**求解收尾时一次性写入**（跑完再取，运行中不轮询）。
    ///   纯观测，不参与任何业务判定；未开作用域时零开销返回。
    /// </summary>
    /// <param name="demandsReceived">按 `LogicalDemandKey` 去重后的需求条数。</param>
    /// <param name="demandQtySum">同口径去重后 `NetOutputQty` 之和。</param>
    /// <param name="demandsWithLandedTask">至少落了一条 `FinalTask` 的需求条数。</param>
    internal static void SetDemandAccounting(long demandsReceived, decimal demandQtySum, long demandsWithLandedTask)
    {
        if (Current.Value is not { } c) return;
        c.DemandsReceived = demandsReceived;
        c.DemandQtySum = demandQtySum;
        c.DemandsWithLandedTask = demandsWithLandedTask;
        Interlocked.Increment(ref c.Sequence);
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
    /// R11（2026-10-29）：**最后一次取消/超时记录**。
    ///
    /// <para><b>为什么只记「阶段 + 时刻」，不记「超时 / 人工取消」</b>：两者在求解器内部**完全同形**
    /// （都是 `OperationCanceledException`，`CancellationToken` 本身不携带来源）——
    /// 是超时还是人工取消，**只有触发方的 `CancellationTokenSource` 知道**。
    /// 求解器替调用方猜，等于把不可知的事实编一个值出来；那比不分类更糟。</para>
    ///
    /// <para><b>三类分类的落点</b>（0号位 R11 第 4 条）：</para>
    /// <list type="bullet">
    ///   <item><b>超时 / 人工取消</b> —— 走本记录：求解器给出**在哪一阶段被中断**，类别由调用方按自己的 token 源判定；
    ///     `OperationCanceledException` 仍**原样上抛**（不吞、不改契约）。</item>
    ///   <item><b>硬不可行</b> —— 不走这里：走 `DomainSolveResult.Success = false`，
    ///     `ErrorMessage` 带 `[HARD_INFEASIBLE]` 前缀，且 `SolveTraceNotes` 里有一条
    ///     `HARD_INFEASIBLE_*` 的 Error 级痕迹。</item>
    /// </list>
    ///
    /// <para>纯观测：不参与任何业务判定，不加字段到 Core 契约 DTO。</para>
    /// </summary>
    public sealed record SolverCancellationRecord(string Phase, DateTime ObservedAtUtc);

    private static SolverCancellationRecord? _lastCancellation;

    /// <summary>最后一次取消记录（任意线程可读；从未取消过则 `null`）。</summary>
    public static SolverCancellationRecord? LastCancellation => Volatile.Read(ref _lastCancellation);

    internal static void RecordCancellation(string phase)
        => Volatile.Write(ref _lastCancellation, new SolverCancellationRecord(phase, DateTime.UtcNow));

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

    // ══════════════════════════════════════════════════════════════════
    // 运行中进度信标（2026-11-05）
    //
    // 【为什么要有它】Run 999401 实测：第 1 域进入「⑤ 求解器」后，**日志 15 分钟一行不动**。
    //   宿主侧（2号位 `ComputeStageProgressMonitor`）只有「本阶段在跑」的心跳，看不到求解器内部
    //   处理到第几条需求、试排了多少次 ⇒「在算」与「卡死」只能靠 CPU 使用率去猜。
    //   `LiveCounters` 其实早就能跨线程读，但**全仓没有任何人定时去读**，等于没有。
    //
    // 【为什么走 Console 不走文件】本程序集零 I/O 红线（不读库、不写库）。既有 `DumpShapeProbe`
    //   （同文件、`APS_CLONE_SHAPE=1`）已经用 Console 且实测能落进 `dotnet test` 的控制台日志，
    //   本信标沿用同一通道、同一开关风格 ⇒ 不新开 I/O 口子。
    //
    // 【为什么全 ASCII】实测控制台重定向会把中文写成「锟斤拷」且**不可恢复**（Run 999401 日志即如此）。
    //   ASCII 一个字节都不会丢，是唯一稳的写法。
    //
    // 【边界】纯观测，不参与任何业务判定。默认关（`APS_SOLVER_BEACON=1` 才开）；
    //   未开时 `StartBeacon()` 返回 null，热路径只多一次静态 bool 读。回调内异常一律吞掉。
    // ══════════════════════════════════════════════════════════════════

    private static readonly bool _beaconEnabled = string.Equals(
        Environment.GetEnvironmentVariable("APS_SOLVER_BEACON"), "1", StringComparison.Ordinal);

    /// <summary>当前活跃信标（进程内单槽；V1 求解串行）。供 <see cref="SetPhase"/> 在阶段切换时立刻打一行。</summary>
    private static ProgressBeacon? _activeBeacon;

    /// <summary>
    /// 开一个 30 秒一次的进度信标；未设 <c>APS_SOLVER_BEACON=1</c> 时返回 <c>null</c>（零开销）。
    /// 调用方 <c>using var beacon = SolverDiagnostics.StartBeacon();</c> 即可。
    /// </summary>
    internal static IDisposable? StartBeacon() => _beaconEnabled ? new ProgressBeacon() : null;

    /// <summary>10 秒一次把 <see cref="LiveSnapshot"/> 打成一行 ASCII。独立线程，不占求解线程。</summary>
    private sealed class ProgressBeacon : IDisposable
    {
        // 2026-11-05：15s → 10s。用户要「看得见百分比」，15 秒一行的粒度在小时级跑批里太稀。
        //   间隔可用 `APS_SOLVER_BEACON_SEC` 覆盖（正整数秒）；非法值退回 10s。
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("APS_SOLVER_BEACON_SEC"), out var sec) && sec > 0
                ? sec
                : 10);

        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly Timer _timer;

        public ProgressBeacon()
        {
            // 首次 tick 立刻打一行（dueTime=0），让「到底进没进求解器、进的哪一阶段」马上可见。
            _timer = new Timer(_ => Tick(), state: null, dueTime: TimeSpan.Zero, period: Interval);
            Volatile.Write(ref _activeBeacon, this);
        }

        // internal（非 private）：外层 `SetPhase` 要在阶段切换时立刻调它。
        //   C# 里嵌套类能访问外层的私有成员，**反向不行** ⇒ 这里放开到 internal。
        internal void Tick()
        {
            try
            {
                var s = LiveSnapshot;
                if (s is null) return;

                var t = _sw.Elapsed;

                // 2026-11-05：加百分比 —— 用户明确要「看得见百分之多少」。
                //   分母为 0 时打 `?` 而不是 0.0%，避免「还没开始」被误读成「卡在 0%」。
                var p2Pct = s.Phase2DemandsTotal > 0
                    ? (100d * s.Phase2DemandsProcessed / s.Phase2DemandsTotal).ToString("F1") + "%"
                    : "?";
                var p4Pct = s.Phase4UnitsTotal > 0
                    ? (100d * s.Phase4UnitsProcessed / s.Phase4UnitsTotal).ToString("F1") + "%"
                    : "?";

                Console.WriteLine(
                    $"[SOLVER-BEACON] +{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
                    + $" phase={s.CurrentPhase}"
                    + $" p2={s.Phase2DemandsProcessed}/{s.Phase2DemandsTotal}({p2Pct})"
                    + $" p4={s.Phase4UnitsProcessed}/{s.Phase4UnitsTotal}({p4Pct})"
                    + $" trials={s.BatchPlanCandidateTrials}"
                    + $" slotSearch={s.SlotSearches}"
                    + $" attempts={s.SlotSearchAttempts}"
                    + $" clone={s.CloneOccupancy}"
                    + $" heapMb={GC.GetTotalMemory(false) / 1024d / 1024d:F0}"
                    + $" gc2={GC.CollectionCount(2)}"
                    + $" seq={s.Sequence}");
            }
            catch
            {
                // 观测失败不许影响求解。
            }
        }

        public void Dispose()
        {
            // 先摘静态引用，再停定时器：避免「已 Dispose 的实例仍被打一行」。
            if (ReferenceEquals(Volatile.Read(ref _activeBeacon), this))
                Volatile.Write(ref _activeBeacon, null);

            try { _timer.Dispose(); } catch { /* 收尾不抛 */ }
        }
    }

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

    // ══════════════════════════════════════════════════════════════════
    // 2026-11-05 需求分类耗时画像
    //
    // 【为什么要有它】用户问「哪些类的订单最耗时间」。现有热点槽（`DemandSchedule` 等）只有
    //   **全局合计**，看不出「哪一类需求把时间吃了」—— 是工序多的、批量大的、有指定路径的，
    //   还是根需求/子需求差别。没有分类就没法做针对性优化，只能说「慢」。
    //
    // 【分类维度】每次试排按**单一维度**各记一笔（不是笛卡尔积）：工序数 / 数量档 /
    //   `DemandGoal` / `QuantityAuthority` / 根需求 / 是否指定 PathId / 连续性 / 是否禁拆合 /
    //   目标 Stage / 工厂。每个维度独立出边际分布 ⇒ 一眼看出哪个维度有区分度。
    //
    // 【口径】记的是 `RunDemandSchedule` 的墙钟，**每候选试排各记一次**（一次需求可能被
    //   多个候选各试排一次）⇒ 数值是「为这类需求做的活」，不是「这类需求排一次要多久」；
    //   均摊看 `均` 列，总量看 `ms` 列。
    //
    // 【开销】开关默认关（`APS_DEMAND_PROFILE=1` 才开），关时只多一次静态 bool 读。
    //   开时每次试排一次 lock + 10 次字典查；相对 `RunDemandSchedule`（毫秒级）可忽略。
    //   纯观测：不改任何业务判定，不写 `SolveTraceNotes`，不动契约 DTO。
    // ══════════════════════════════════════════════════════════════════

    private static readonly bool _demandProfileEnabled = string.Equals(
        Environment.GetEnvironmentVariable("APS_DEMAND_PROFILE"), "1", StringComparison.Ordinal);

    /// <summary>一个分桶的累计：次数 / 累计 ticks / 单次峰值 ticks。</summary>
    internal sealed class DemandProfileSlot
    {
        internal long Count;
        internal long Ticks;
        internal long MaxTicks;
    }

    private static readonly object _demandProfileLock = new();
    private static readonly Dictionary<string, Dictionary<string, DemandProfileSlot>> _demandProfiles
        = new(StringComparer.Ordinal);

    /// <summary>记一笔需求试排耗时（按多个单一维度各记一次）。纯观测，异常全吞。</summary>
    internal static void ProfileDemand(LogicalProductionDemand d, int opsCount, long ticks)
    {
        if (!_demandProfileEnabled || ticks <= 0L) return;

        try
        {
            lock (_demandProfileLock)
            {
                Bump("ops", OpsBucket(opsCount), ticks);
                Bump("qty", QtyBucket(d.PlannedProcessQty), ticks);
                Bump("goal", TextBucket(d.DemandGoal), ticks);
                Bump("auth", TextBucket(d.QuantityAuthority), ticks);
                Bump("root", d.IsRootDemand ? "根需求" : "子需求", ticks);
                Bump("path", d.PathId.HasValue ? "指定" : "未指定", ticks);
                Bump("cont", d.IsContinuation ? "续单" : "新单", ticks);
                Bump("split", d.NoSplitMerge ? "禁拆合" : "可拆合", ticks);
                Bump("tgtstage", TextBucket(d.RequiredStageCode), ticks);
                Bump("factory", TextBucket(d.FactoryCode), ticks);
            }
        }
        catch
        {
            // 观测失败不许影响求解。
        }
    }

    private static void Bump(string dim, string bucket, long ticks)
    {
        if (!_demandProfiles.TryGetValue(dim, out var buckets))
        {
            buckets = new Dictionary<string, DemandProfileSlot>(StringComparer.Ordinal);
            _demandProfiles[dim] = buckets;
        }

        if (!buckets.TryGetValue(bucket, out var slot))
        {
            slot = new DemandProfileSlot();
            buckets[bucket] = slot;
        }

        slot.Count++;
        slot.Ticks += ticks;
        if (ticks > slot.MaxTicks) slot.MaxTicks = ticks;
    }

    private static string OpsBucket(int n)
        => n <= 0 ? "0" : n == 1 ? "1" : n == 2 ? "2" : n <= 4 ? "3-4" : "5+";

    private static string QtyBucket(decimal q)
        => q <= 0m ? "<=0" : q <= 1m ? "<=1" : q <= 10m ? "<=10" : q <= 100m ? "<=100"
         : q <= 1000m ? "<=1000" : ">1000";

    private static string TextBucket(string? s) => string.IsNullOrWhiteSpace(s) ? "(空)" : s!;

    /// <summary>
    /// 2026-11-07 v5.0 A1：把幂次桶还原成 P50/P95/P99 + 非空桶清单。
    ///   ⚠ 分位是**上界**：桶只知道 `[2^(i-1), 2^i - 1]`，所以 `P95≤顶沿`，不是精确值。
    ///   桶 0 的顶沿记 0；桶 `i≥1` 的顶沿 = `2^i - 1`。
    ///   纯观测，不参与任何业务判定。
    /// </summary>
    private static string DescribeShapeBuckets(long[] buckets, long total, long max)
    {
        if (buckets is null || total <= 0)
        {
            return $"(无样本) 峰值={max:N0}";
        }

        long cum = 0;
        var p50 = -1;
        var p95 = -1;
        var p99 = -1;

        for (var i = 0; i < buckets.Length; i++)
        {
            cum += buckets[i];
            if (p50 < 0 && cum * 2 >= total) p50 = i;                 // ≥50%
            if (p95 < 0 && cum * 100 >= total * 95) p95 = i;          // ≥95%
            if (p99 < 0 && cum * 100 >= total * 99) p99 = i;          // ≥99%
        }

        static long Top(int i) => i <= 0 ? 0 : (1L << i) - 1;   // 桶 i 的上沿 = 2^i - 1

        var sb = new System.Text.StringBuilder();
        sb.Append($"P50≤{Top(p50):N0}  P95≤{Top(p95):N0}  P99≤{Top(p99):N0}  Max={max:N0}");
        sb.Append("  ｜ 非空桶: ");

        var any = false;
        var listed = 0;
        for (var i = 0; i < buckets.Length && listed < 14; i++)
        {
            if (buckets[i] <= 0) continue;
            any = true;
            listed++;
            var lo = i <= 0 ? 0 : 1L << (i - 1);
            var hi = i <= 0 ? 0 : (1L << i) - 1;
            var pct = 100d * buckets[i] / total;
            sb.Append($"[{lo:N0}-{hi:N0}]={pct:N1}% ");
        }

        if (!any) sb.Append("(全空，计数未接线?)");
        else if (listed >= 14) sb.Append("…(仅列前 14 个非空桶)");

        return sb.ToString();
    }

    /// <summary>把需求分类画像打进当前输出（Run 结束、`Scope.Dispose` 时一次）。</summary>
    private static void DumpDemandProfiles()
    {
        if (!_demandProfileEnabled) return;

        try
        {
            Dictionary<string, Dictionary<string, DemandProfileSlot>> copy;
            lock (_demandProfileLock)
            {
                copy = new Dictionary<string, Dictionary<string, DemandProfileSlot>>(StringComparer.Ordinal);
                foreach (var kv in _demandProfiles)
                {
                    var inner = new Dictionary<string, DemandProfileSlot>(StringComparer.Ordinal);
                    foreach (var b in kv.Value)
                    {
                        inner[b.Key] = new DemandProfileSlot
                        {
                            Count = b.Value.Count,
                            Ticks = b.Value.Ticks,
                            MaxTicks = b.Value.MaxTicks,
                        };
                    }

                    copy[kv.Key] = inner;
                }

                // 一次 Run 只出一份；清空避免多 Run 累加（进程内可能跑多个域）。
                _demandProfiles.Clear();
            }

            var ticksToMs = 1000d / System.Diagnostics.Stopwatch.Frequency;

            Console.WriteLine("[PROFILE] ================= 需求分类耗时画像 =================");

            foreach (var dim in copy)
            {
                var rows = new List<KeyValuePair<string, DemandProfileSlot>>(dim.Value);
                long dimTicks = 0L;
                long dimCount = 0L;
                foreach (var r in rows) { dimTicks += r.Value.Ticks; dimCount += r.Value.Count; }

                Console.WriteLine(
                    $"[PROFILE] 维度 {dim.Key}：{dimCount:N0} 次试排，合计 {dimTicks * ticksToMs:N0}ms");

                rows.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));
                foreach (var r in rows)
                {
                    var ms = r.Value.Ticks * ticksToMs;
                    var pct = dimTicks > 0 ? 100d * r.Value.Ticks / dimTicks : 0d;
                    var avgMs = r.Value.Count > 0 ? ms / r.Value.Count : 0d;
                    var maxMs = r.Value.MaxTicks * ticksToMs;

                    Console.WriteLine(
                        $"[PROFILE]   {dim.Key,-9} {r.Key,-22} {r.Value.Count,9:N0} 次"
                        + $" {ms,12:N0}ms ({pct,5:F1}%) 均 {avgMs,9:N2}ms 峰 {maxMs,9:N0}ms");
                }
            }
        }
        catch (Exception ex)
        {
            // 观测失败不改变求解行为。
            Console.WriteLine($"[PROFILE] 需求画像输出异常（已吞）: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 2026-10-27 形状探针输出。**只有环境变量 `APS_CLONE_SHAPE=1` 才输出**，默认一行不打。
    ///
    /// 【为什么用 `Console` 而不是走宿主夹具的转储】宿主 `RealDomainFullRunTest.DumpSolverCounters`
    ///   只固定打印 14 个热点槽，新增的计数器**没有入口**；而本程序集是零 I/O 的（不许开文件），
    ///   所以只能走 `Console`。已实测 `Console` 输出会落进 `dotnet test` 的 console.log
    ///   （同文件 `:857` 的「入参捕获钩子异常」走的就是这条路）。
    /// 纯观测，不参与任何业务判定。
    /// </summary>
    private static void DumpShapeProbe(Counters c)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("APS_CLONE_SHAPE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var occCalls = c.CloneOccupancy;
            var occMean = occCalls > 0 ? c.CloneOccupancyKeySum / (double)occCalls : 0d;
            var occMs = c.HotspotMs(Hotspot.CloneOccupancy);
            var occUs = occCalls > 0 ? occMs * 1000d / occCalls : 0d;

            var taskCalls = c.CloneTaskList;
            var taskMs = c.HotspotMs(Hotspot.CloneTaskList);
            var taskUs = taskCalls > 0 ? taskMs * 1000d / taskCalls : 0d;

            Console.WriteLine(
                $"[SHAPE] CloneOccupancy: {occCalls:N0} 次, 平均键数={occMean:N1}, 峰值键数={c.CloneOccupancyKeyMax:N0}, "
                + $"总耗时={occMs:N0}ms, 每次={occUs:N1}µs "
                + $"⇒ 每键每次={occUs / Math.Max(occMean, 1d):N3}µs/键");
            Console.WriteLine(
                "[SHAPE] CloneOccupancy 键数分布（幂次桶, 上界=桶上沿）: "
                + DescribeShapeBuckets(c.CloneOccupancyKeyBuckets, c.CloneOccupancy, c.CloneOccupancyKeyMax));

            Console.WriteLine(
                $"[SHAPE] CloneTaskList: {taskCalls:N0} 次, 峰值长度={c.CloneTaskListLenMax:N0}, "
                + $"总耗时={taskMs:N0}ms, 每次={taskUs:N1}µs");
            Console.WriteLine(
                "[SHAPE] CloneTaskList 长度分布（幂次桶, 上界=桶上沿）: "
                + DescribeShapeBuckets(c.CloneTaskListLenBuckets, c.CloneTaskListCalls, c.CloneTaskListLenMax));

            // ── 2026-11-07 v5.0 A3：冗余试排到底有多冗余 ──────────────────
            //   判据：`工序重复条数 ÷ 候选总条数` 就是**记忆化的收益上界**；
            //     若接近 0 ⇒ 候选条条不同，记忆化一分钱省不到，别做。
            var candTotal = c.RoutingCandidateTotal;
            var candDup = c.RoutingCandidateDuplicateOps;
            Console.WriteLine(
                $"[SHAPE] Routing 候选: 总条数={candTotal:N0}, 工序序列重复={candDup:N0}"
                + $" ({(candTotal > 0 ? 100d * candDup / candTotal : 0d):F1}%), 单条工序数峰值={c.RoutingCandidateOpsMax:N0}");
            Console.WriteLine(
                "[SHAPE] 每需求候选条数分布（幂次桶, 上界=桶上沿）: "
                + DescribeShapeBuckets(c.RoutingCandidatePerDemandBuckets, candTotal, c.RoutingCandidateOpsMax));

            // ── 候选并行到底有没有真并行（2026-10-27 诊断）──────────────────
            //   判据：候选耗时之和 ÷ Parallel.For 墙钟。
            //     ≈1  ⇒ 一点没并行（Parallel.For 退化成串行）；
            //     ≈候选数 ⇒ 满并行。
            //   再配 `Environment.ProcessorCount`：若它返回 1，`MaxDegreeOfParallelism` 就是 1，
            //     Parallel.For 会老老实实串行跑 —— 这是最容易犯又最看不出来的错。
            Console.WriteLine(
                $"[SHAPE] 批方案候选并行（外层）: 并行派发={c.OuterBatchPlanParallel:N0}, "
                + $"串行派发={c.OuterBatchPlanSerial:N0}");
            if (c.OuterBatchPlanParallel == 0 && c.OuterBatchPlanSerial > 0
                && string.Equals(Environment.GetEnvironmentVariable("APS_P2_PARALLEL_CANDIDATES"), "1", StringComparison.Ordinal))
            {
                Console.WriteLine(
                    "[SHAPE] ⚠ 开关已开但**并行派发 0 次** ⇒ 回退条件把并行全挡掉了"
                    + "（最可能：`_bgReal.SnapshotValid == false` 或 `candidates.Count != 2`）。别拿这个谈提速。");
            }

            var parBatches = c.CandidateParallelDispatches;
            var serBatches = c.CandidateSerialDispatches;
            var workMs = c.CandidateTrialWorkTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            var wallMs = c.CandidateParallelWallTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            var candPerBatch = parBatches + serBatches > 0
                ? c.CandidateDispatchCountSum / (double)(parBatches + serBatches)
                : 0d;
            Console.WriteLine(
                $"[SHAPE] 候选并行: 并行批={parBatches:N0}, 串行批={serBatches:N0}, 平均候选数={candPerBatch:N2}, "
                + $"ProcessorCount={Environment.ProcessorCount}, "
                + $"候选耗时合计={workMs:N0}ms, Parallel.For 墙钟={wallMs:N0}ms, "
                + $"加速比={(wallMs > 0d ? workMs / wallMs : 0d):N2}×");

            // ── GC 到底吃了多少墙钟（2026-10-27 诊断）──────────────────────
            //   【为什么要量这个】「并行没效果是不是 GC 太频繁」是最大的猜疑，但**猜不算数**。
            //     两个硬指标一次说清：
            //       · 总停顿 = GC 真正拿走的墙钟（`GC.GetTotalPauseDuration`，.NET 7+）
            //       · 总分配 = 整场 Run `new` 出来的字节数（与串行/并行无关，活一样多就该一样大）
            //   判据：总停顿 ÷ Phase2 墙钟 < 5% ⇒ GC 洗清，问题在别处（多半是 Parallel.For 没生效）；
            //         > 20% ⇒ GC 是主嫌，`DOTNET_gcServer=1` 值得一试。
            //   纯观测，不参与任何业务判定。
            var pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            var allocGb = GC.GetTotalAllocatedBytes(precise: false) / 1024d / 1024d / 1024d;
            Console.WriteLine(
                $"[SHAPE] GC: 服务器GC={System.Runtime.GCSettings.IsServerGC}, "
                + $"回收次数 Gen0={GC.CollectionCount(0):N0} Gen1={GC.CollectionCount(1):N0} Gen2={GC.CollectionCount(2):N0}, "
                + $"总分配={allocGb:N1}GB, 总停顿={pauseMs:N0}ms, "
                + $"当前托管堆={GC.GetTotalMemory(false) / 1024d / 1024d:N0}MB");

            // ── 批分组覆盖层拆解（2026-10-27）──────────────────────────────
            //   覆盖层 = 撤销上一批 + 并入本批，两半都应是 O(尾部)。先把「尾部到底多长」量出来，
            //   再决定修哪一半 —— 覆盖层实测占全程 ~24%，是当前唯一超过 20% 的具名单项。
            var ovlCalls = c.HotspotCallCount(Hotspot.BatchGroupTrialOverlay);
            var ovlMs = c.HotspotMs(Hotspot.BatchGroupTrialOverlay);
            var rstMs = c.HotspotMs(Hotspot.BatchGroupOverlayRestore);
            var appMs = ovlMs - rstMs;
            var tailSum = c.BatchGroupOverlayTailSum;
            Console.WriteLine(
                $"[SHAPE] 覆盖层拆解: 合计 {ovlMs:N0}ms/{ovlCalls:N0} 次"
                + $" ⇒ 撤销={rstMs:N0}ms, 并入={appMs:N0}ms; "
                + $"尾部长度 峰值={c.BatchGroupOverlayTailMax:N0} 平均={(ovlCalls > 0 ? tailSum / (double)ovlCalls : 0d):N1}, "
                + $"回滚键数 峰值={c.BatchGroupOverlayRestoreKeyMax:N0}, "
                + $"表长−快照长 峰值={c.BatchGroupPrefixGapMax:N0}");

            // ── 2026-11-06 P2-A：外层批方案采纳的「纯追加 / 回退」拆解 ──────────────
            //   判据：快路径覆盖率 = 纯追加 ÷ (纯追加 + 回退)。覆盖率低说明本次优化基本打不中，
            //   应回看 §三 的保守条件是否过严，而不是硬推。
            var outerAppend = c.OuterAdoptPureAppend;
            var outerFallback = c.OuterAdoptFallback;
            var outerTotal = outerAppend + outerFallback;
            Console.WriteLine(
                $"[SHAPE] P2-A 外层采纳: 纯追加={outerAppend:N0} 次 / 回退={outerFallback:N0} 次"
                + (outerTotal > 0 ? $" ⇒ 覆盖率 {outerAppend * 100d / outerTotal:F1}%" : " ⇒ 本 Run 未发生采纳")
                + $", 追加 Task 累计={c.OuterAdoptAppendedTasks:N0} 条"
                + $", 旧表长度 最小={(outerAppend > 0 ? c.OuterAdoptOldCountMin.ToString("N0") : "—")}"
                + $"/最大={c.OuterAdoptOldCountMax:N0}"
                + $", 采纳段墙钟={c.HotspotMs(Hotspot.OuterAdopt):N0}ms");

            // ── 全 68 槽分项（2026-10-27）──────────────────────────────────────
            //   【为什么必须打全】宿主夹具 `RealDomainFullRunTest.DumpSolverCounters` 只固定打印 14 个槽，
            //     导致 PV190 剖面里每次都有一大块「没名字」（run14 里 `BatchPlanTrial` 334.8 s 中
            //     130 s 无名）。名字不在那 14 个里的槽（`BatchAdopt` / `BatchGroupIndex` /
            //     `MergeEffEval` / `OccupancyRollback` …）全部**测了但看不见**。
            //   这里用反射枚举 `Hotspot` 的常量 ⇒ **新增槽自动出现**，不需要再改这份清单。
            //   纯观测；只在 `APS_CLONE_SHAPE=1` 时执行，且一处 Run 只跑一次。
            var slots = typeof(Hotspot)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(int))
                .Select(f => (Name: f.Name, Slot: (int)f.GetRawConstantValue()!))
                .ToList();

            // 2026-11-06：槽号 ≥ HotspotCount 的会**静默丢弃**（AddHotspotTicks 有护栏）。
            //   这种情况下面所有读数都是 0，看起来像「没耗时」而不是「没记上」，必须吼出来。
            var maxSlot = slots.Count == 0 ? -1 : slots.Max(s => s.Slot);
            if (maxSlot >= Counters.HotspotCount)
            {
                Console.WriteLine(
                    $"[SHAPE] ⚠ 槽号越界：最大槽={maxSlot}，但 Counters.HotspotCount={Counters.HotspotCount} "
                    + "⇒ 这些槽**一个数都没记上**，下方读数为 0 不代表没耗时。先改 HotspotCount。");
            }

            var totalNamed = slots.Sum(s => c.HotspotMs(s.Slot));
            Console.WriteLine($"[SHAPE] 全槽分项（共 {slots.Count} 槽, 累计 {totalNamed:N0}ms, 含嵌套）:");
            foreach (var (name, slot) in slots
                         .Select(s => (s.Name, s.Slot, Ms: c.HotspotMs(s.Slot)))
                         .Where(x => x.Ms >= 1d)
                         .OrderByDescending(x => x.Ms)
                         .Select(x => (x.Name, x.Slot)))
            {
                var ms = c.HotspotMs(slot);
                var calls = c.HotspotCallCount(slot);
                var perCall = calls > 0 ? ms * 1000d / calls : 0d;
                Console.WriteLine($"[SHAPE]   {name,-26} {ms,12:N0}ms / {calls,12:N0} 次 = {perCall,10:N1}µs/次");
            }

            // ── 2026-11-06 顾问复核：Phase3/Phase5 诊断链的输入规模 ──────────────
            //   【为什么单列】上面那七槽只给「花了多久」，不给「每次扫多大」。
            //     顾问 §四 的判据是「延期需求数 × 全阻挡块数」「延期需求数 × 锁定表长」，
            //     所以两个乘数必须各自有数，否则无法区分「调用太少但每次巨大」和「调用太多」。
            //   纯观测；只在 `APS_CLONE_SHAPE=1` 时执行，且一处 Run 只跑一次。
            var diagCalls = c.HotspotCallCount(Hotspot.Phase3DiagnoseTotal);
            var delayedCalls = c.HotspotCallCount(Hotspot.Phase3DelayedLoop);
            var blockCalls = c.HotspotCallCount(Hotspot.Phase3BlockOverlap);
            Console.WriteLine(
                $"[SHAPE] 诊断链输入: Diagnose调用={diagCalls:N0} 次"
                + $", 延期需求={delayedCalls:N0} 条"
                + $", LockedTasks 累计={c.DiagnoseLockedTasksSum:N0}/峰值={c.DiagnoseLockedTasksMax:N0}"
                + $" ⇒ 整改前锁扫描总量≈{c.DiagnoseLockedTasksSum * delayedCalls / Math.Max(diagCalls, 1):N0}"
                + "（整改后实际=0，仅在耗时上体现）");
            Console.WriteLine(
                $"[SHAPE] 阻挡块输入: 外部块 累计={c.DiagnoseExternalBlockSum:N0}/峰值={c.DiagnoseExternalBlockMax:N0}"
                + $", 上游块 累计={c.DiagnoseUpstreamBlockSum:N0}/峰值={c.DiagnoseUpstreamBlockMax:N0}"
                + $" ⇒ 整改前块扫描总量≈{(c.DiagnoseExternalBlockSum + c.DiagnoseUpstreamBlockSum) * delayedCalls / Math.Max(diagCalls, 1):N0}"
                + "（整改后实际=0，仅在耗时上体现）"
                + $" ｜ 定向检索次数={blockCalls:N0}");
            // 第3步测量：全域利用率 vs 逐需求利用率，两者同一槽号混算过 ⇒ 现已拆开，此处给出占比。
            var utilDomainMs = c.HotspotMs(Hotspot.Phase3Utilization);
            var utilPerDemandMs = c.HotspotMs(Hotspot.Phase3UtilizationPerDemand);
            var utilTotal = utilDomainMs + utilPerDemandMs;
            Console.WriteLine(
                $"[SHAPE] 利用率: 全域={utilDomainMs:N0}ms / {c.HotspotCallCount(Hotspot.Phase3Utilization):N0} 次"
                + $", 逐需求={utilPerDemandMs:N0}ms / {c.HotspotCallCount(Hotspot.Phase3UtilizationPerDemand):N0} 次"
                + (utilTotal > 0 ? $" ⇒ 逐需求占利用率 {utilPerDemandMs * 100d / utilTotal:F1}%" : string.Empty)
                + (c.Phase3Ms > 0 ? $"、占首次 Phase3 {utilTotal * 100d / c.Phase3Ms:F1}%" : string.Empty));
        }
        catch (Exception ex)
        {
            // 观测失败不改变求解行为。
            Console.WriteLine($"[SHAPE] 形状探针输出异常（已吞）: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
            // 2026-10-27 形状探针：默认环境变量关闭 ⇒ 生产/CI 零变化。
            DumpShapeProbe(Counters);
            // 2026-11-05 需求分类耗时画像：同样默认关（`APS_DEMAND_PROFILE=1` 才开）。
            DumpDemandProfiles();
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
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.BatchPlanCandidateTrials);
    }

    internal static void CountRoutingTrial()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.RoutingTrials);
    }

    /// <summary>
    /// R08（2026-10-29）：登记一次「Task 按 `InitialState`（`fromMaterialId = null`）落下」。
    ///   只在本工程内 / 测试可见（**不进** <see cref="SolverLiveSnapshot"/> —— 那是 2号位 运行中探针的公开契约）。
    /// </summary>
    internal static void CountSetupInitialStatePlacement()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.SetupInitialStatePlacements);
    }

    internal static void CountCloneOccupancy()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.CloneOccupancy);
    }

    /// <summary>
    /// 2026-10-27 形状探针：登记 `CloneOccupancy` 源字典的**键数**（资源数）。
    ///   判据用途：`new Dictionary&lt;K,V&gt;(source)` 的耗时 ≈ O(容量)，键数直接决定 696 µs 是否合理。
    ///   纯观测；调用点在热路径上，故只做 2 次 long 写，不分配、不加锁。
    /// </summary>
    internal static void CountCloneOccupancyKeyShape(int keyCount)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.CloneOccupancyKeySum, keyCount);
        if (keyCount > c.CloneOccupancyKeyMax) c.CloneOccupancyKeyMax = keyCount;
        System.Threading.Interlocked.Increment(ref c.CloneOccupancyKeyBuckets[ShapeBucketOf(keyCount)]);
    }

    /// <summary>2026-10-27 形状探针：登记 `new List&lt;FinalTaskDraft&gt;(scheduledTasks)` 的**任务表长度**。纯观测。</summary>
    internal static void CountCloneTaskListShape(int taskCount)
    {
        if (Current.Value is not { } c) return;
        if (taskCount > c.CloneTaskListLenMax) c.CloneTaskListLenMax = taskCount;
        System.Threading.Interlocked.Increment(ref c.CloneTaskListCalls);
        System.Threading.Interlocked.Increment(ref c.CloneTaskListLenBuckets[ShapeBucketOf(taskCount)]);
    }

    /// <summary>
    /// 2026-11-07 v5.0 A3：登记一条 Routing 候选。
    ///   <paramref name="position"/> = 它在本需求候选表中的下标（0 起）；
    ///   <paramref name="duplicateOps"/> = 工序序列是否与本需求前面某条候选逐字相同。
    ///   纯观测，不参与任何业务判定。
    /// </summary>
    internal static void CountRoutingCandidate(int position, int opsCount, bool duplicateOps)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.RoutingCandidateTotal);
        if (duplicateOps) System.Threading.Interlocked.Increment(ref c.RoutingCandidateDuplicateOps);
        if (opsCount > c.RoutingCandidateOpsMax) c.RoutingCandidateOpsMax = opsCount;
        System.Threading.Interlocked.Increment(
            ref c.RoutingCandidatePerDemandBuckets[ShapeBucketOf(position + 1)]);
    }

    /// <summary>
    /// 2026-11-07 v5.0 A1：把长度映射到幂次桶号。
    ///   `v ≤ 0` ⇒ 0；否则桶 `i = ⌊log2 v⌋ + 1` ⇒ 桶 i 覆盖 `[2^(i-1), 2^i - 1]`。
    ///   为什么不上真直方图：热路径上不能分配、不能加锁；幂次桶只需一次 `Interlocked.Increment`。
    /// </summary>
    private static int ShapeBucketOf(int v)
    {
        if (v <= 0) return 0;
        var i = System.Numerics.BitOperations.Log2((uint)v) + 1;
        return i < 33 ? i : 32;
    }

    /// <summary>
    /// 2026-11-06 顾问复核：登记一次 `PhaseThreeDiagnostics.Diagnose` 入口的 `LockedTasks.Count`。
    ///   与 <see cref="Hotspot.Phase3DelayedLoop"/> 的次数相乘 = 锁定全表扫描的总条数。
    ///   纯观测，不改任何判定。
    /// </summary>
    internal static void CountDiagnoseLockedTasks(int lockedTasks)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.DiagnoseLockedTasksSum, lockedTasks);
        if (lockedTasks > c.DiagnoseLockedTasksMax) c.DiagnoseLockedTasksMax = lockedTasks;
    }

    /// <summary>
    /// 2026-11-06 顾问复核：登记一次 `HasResourceBlockOverlap` 入口的外部/上游 ResourceBlock 条数。
    ///   与该方法调用次数相乘 = 块列表重建 + 嵌套扫描的总规模。纯观测，不改任何判定。
    /// </summary>
    internal static void CountDiagnoseResourceBlocks(int externalBlocks, int upstreamBlocks)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.DiagnoseExternalBlockSum, externalBlocks);
        System.Threading.Interlocked.Add(ref c.DiagnoseUpstreamBlockSum, upstreamBlocks);
        if (externalBlocks > c.DiagnoseExternalBlockMax) c.DiagnoseExternalBlockMax = externalBlocks;
        if (upstreamBlocks > c.DiagnoseUpstreamBlockMax) c.DiagnoseUpstreamBlockMax = upstreamBlocks;
    }

    /// <summary>
    /// 2026-10-27 诊断：登记一次候选调度（`parallel == true` 走 `Parallel.For`，否则串行 for）。
    ///   配套 <see cref="AddCandidateTrialWorkTicks"/> / <see cref="AddCandidateParallelWallTicks"/>：
    ///   三者合起来才能判「并行到没到」。纯观测。
    /// </summary>
    /// <summary>
    /// 2026-10-27 四十四刀：`SelectBestBatchPlan` 每次派发登记一次（并行 / 串行）。
    ///   ⚠ 这两个数任一为 0 而另一个很大 ⇒ 说明开关或回退条件把并行路径整个挡掉了，先查那里再谈提速。
    /// </summary>
    internal static void CountOuterBatchPlanDispatch(bool parallel)
    {
        if (Current.Value is not { } c) return;
        if (parallel)
            System.Threading.Interlocked.Increment(ref c.OuterBatchPlanParallel);
        else
            System.Threading.Interlocked.Increment(ref c.OuterBatchPlanSerial);
    }

    internal static void CountCandidateDispatch(bool parallel, int candidateCount)
    {
        if (Current.Value is not { } c) return;
        if (parallel)
            System.Threading.Interlocked.Increment(ref c.CandidateParallelDispatches);
        else
            System.Threading.Interlocked.Increment(ref c.CandidateSerialDispatches);
        System.Threading.Interlocked.Add(ref c.CandidateDispatchCountSum, candidateCount);
    }

    /// <summary>2026-10-27 诊断：累加**单个候选** `RunCandidate` 的耗时（Stopwatch ticks）。纯观测。</summary>
    internal static void AddCandidateTrialWorkTicks(long ticks)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.CandidateTrialWorkTicks, ticks);
    }

    /// <summary>2026-10-27 诊断：累加 `Parallel.For` 本体墙钟（Stopwatch ticks）。纯观测。</summary>
    internal static void AddCandidateParallelWallTicks(long ticks)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.CandidateParallelWallTicks, ticks);
    }

    /// <summary>2026-10-27 形状探针：登记批分组覆盖层的**尾部长度**（`tasks.Count - from`）。纯观测。</summary>
    internal static void CountBatchGroupOverlayTailShape(int tailLen)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Add(ref c.BatchGroupOverlayTailSum, tailLen);
        if (tailLen > c.BatchGroupOverlayTailMax) c.BatchGroupOverlayTailMax = tailLen;
    }

    /// <summary>2026-10-27 形状探针：登记覆盖层回滚要处理的**键数**（`counts.Count + created.Count`）。纯观测。</summary>
    internal static void CountBatchGroupOverlayRestoreShape(int keyCount)
    {
        if (Current.Value is not { } c) return;
        if (keyCount > c.BatchGroupOverlayRestoreKeyMax) c.BatchGroupOverlayRestoreKeyMax = keyCount;
    }

    /// <summary>2026-10-27 形状探针：登记 `scheduledTasks.Count - snapshot.Count`（前缀比对 + 尾部追加要扫的长度）。纯观测。</summary>
    internal static void CountBatchGroupPrefixGapShape(int gap)
    {
        if (Current.Value is not { } c) return;
        if (gap > c.BatchGroupPrefixGapMax) c.BatchGroupPrefixGapMax = gap;
    }

    internal static void CountCloneTaskList()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.CloneTaskList);
    }

    internal static void CountCloneShares()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.CloneShares);
    }

    /// <summary>2026-10-19 性能定位：累计摘除 Task 数 Σk 与摘除时列表长度 Σn。</summary>
    internal static void CountDetachShape(int removedCount, int listLength)
    {
        if (Current.Value is { } c)
        {
            System.Threading.Interlocked.Add(ref c.DetachTaskCount, removedCount);
            System.Threading.Interlocked.Add(ref c.DetachListLen, listLength);
        }
    }

    /// <summary>2026-10-20 六次定位：占用窗二分未命中（回落线性扫）。纯观测。</summary>
    internal static void CountDetachOccupancyBinaryMiss()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.DetachOccupancyBinaryMiss);
    }

    /// <summary>2026-10-20 六次定位：摘除段触达一个资源时登记「是否真拷 + 该资源窗列表长度」。纯观测。</summary>
    internal static void CountDetachOccupancyShape(int windowCount, bool copied)
    {
        if (Current.Value is not { } c) return;

        System.Threading.Interlocked.Add(ref c.DetachOccupancyWindowLen, windowCount);
        if (copied) System.Threading.Interlocked.Increment(ref c.DetachOccupancyCopies);
    }

    /// <summary>2026-10-20 六次定位：产品时间线二分未命中（回落线性扫）。纯观测。</summary>
    internal static void CountDetachTimelineBinaryMiss()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.DetachTimelineBinaryMiss);
    }


    /// <summary>2026-10-19 二次定位：`RunBatchPlan` 总调用次数。</summary>
    internal static void CountRunBatchPlanInvocation()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.RunBatchPlanInvocations);
    }

    /// <summary>
    /// 2026-10-19 三次定位：登记**一次锚点擂台的规模**（`stageBatches.Count`）。
    ///   在擂台入口（`TryMergeOrSchedule` 的 `for (anchorIndex…)` 之前）调用一次。
    ///   纯观测、不参与任何业务判定；未开 scope 时只做一次 null 判断。
    /// </summary>
    internal static void CountAnchorLoop(int stageBatchCount)
    {
        if (Current.Value is not { } c) return;

        System.Threading.Interlocked.Increment(ref c.AnchorLoopEntries);
        System.Threading.Interlocked.Add(ref c.AnchorBatchCountSum, stageBatchCount);
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

        System.Threading.Interlocked.Increment(ref c.AnchorBatchBuckets[bucket]);
    }

    /// <summary>
    /// 2026-10-19 三次定位：登记**一次候选扫描的规模**（`FindMergeableStageBatches` 里
    ///   `groups.Count` —— 即「这一遍扫了多少个批分组」）。
    ///   纯观测、不参与任何业务判定；未开 scope 时只做一次 null 判断。
    /// </summary>
    internal static void CountMergeScan(int groupCount)
    {
        if (Current.Value is not { } c) return;

        System.Threading.Interlocked.Increment(ref c.MergeScanCalls);
        System.Threading.Interlocked.Add(ref c.MergeScanGroupSum, groupCount);
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
        System.Threading.Interlocked.Increment(ref c.BatchGroupReconcileOutcomes[outcome]);
    }

    /// <summary>
    /// 2026-10-20 二十九次定位：登记**一次批分组整表重建**及其触发原因
    ///   （0 = 缓存失效 / 1 = 任务表变短 / 2 = 前缀失配）。纯观测，不参与任何判定。
    /// </summary>
    internal static void CountBatchGroupRebuildReason(int reason)
    {
        if (Current.Value is not { } c) return;
        if ((uint)reason >= (uint)c.BatchGroupRebuildReasons.Length) return;
        System.Threading.Interlocked.Increment(ref c.BatchGroupRebuildReasons[reason]);
    }

    /// <summary>2026-10-20 十一次定位：`scheduledTasks` 整表替换次数。</summary>
    internal static void CountTaskTableReplaced()
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.TaskTableReplaced);
    }

    /// <summary>2026-10-20 十一次定位：`scheduledTasks` 尾部追加次数。</summary>
    internal static void CountTaskTableAppended()
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.TaskTableAppended);
    }

    /// <summary>2026-11-07 W1：登记一次合批**尝试**。纯观测。</summary>
    internal static void CountMergeAttempt()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeAttempts);
    }

    /// <summary>2026-11-07 W1：登记一次合批**命中**。纯观测。</summary>
    internal static void CountMergeHit()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeHits);
    }

    /// <summary>2026-11-09 Step 1：产品时间线写入次数（纯观测，不参与判定）。按调用来源分桶。</summary>
    internal static void CountTimelinePlace(TimelinePlaceSource source = TimelinePlaceSource.Unknown)
    {
        if (Current.Value is { } c)
        {
            System.Threading.Interlocked.Increment(ref c.TimelinePlaceCalls);
            var idx = (int)source;
            if (idx >= 0 && idx < c.TimelinePlaceBySource.Length)
            {
                System.Threading.Interlocked.Increment(ref c.TimelinePlaceBySource[idx]);
            }
        }
    }

    /// <summary>2026-11-09 Step 1：产品时间线压缩次数（纯观测，不参与判定）。</summary>
    internal static void CountTimelineCompact()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.TimelineCompactRuns);
    }

    /// <summary>2026-11-09：合批擂台分类统计的四个指标（下标就用作 `MergePiStats` 的偏移）。</summary>
    internal enum MergePiMetric
    {
        /// <summary>本类锚批被拿到擂台上的次数。</summary>
        Anchor = 0,

        /// <summary>真正进入完整试排（`TryMergeDemandIntoStageBatch`）的次数。</summary>
        Trial = 1,

        /// <summary>完整试排累计耗时（`Stopwatch` ticks）。</summary>
        Ticks = 2,

        /// <summary>最终被选为擂台胜者的次数。</summary>
        Adopted = 3,
    }

    /// <summary>2026-11-10 P0-03：单工序锚批因「已承接份额含跨 PI 成员」被拒（纯观测）。</summary>
    internal static void CountSingleOpAnchorMixedPiRejected()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.SingleOpAnchorMixedPiRejected);
    }

    /// <summary>2026-11-09：单工序合批因「当前需求无 PI」被 O(1) 短路跳过的次数（纯观测）。</summary>
    internal static void CountSingleOpMergeSkippedNoPi()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.SingleOpMergeSkippedNoPi);
    }

    /// <summary>2026-11-09：累加「合批试排内部的拆批方案试排」耗时（纯观测）。</summary>
    internal static void AddBatchTrialMsInMerge(double delta)
    {
        if (Current.Value is { } c) c.BatchTrialMsInMerge += delta;
    }

    /// <summary>2026-11-09：记录需求 PI 填充率（一次性，纯观测）。</summary>
    internal static void SetPiFill(long total, long present)
    {
        if (Current.Value is { } c)
        {
            c.PiFillTotal = total;
            c.PiFillPresent = present;
        }
    }

    /// <summary>2026-11-09：「PI 缺失」成因分解（0=需求自身无PI / 1=锚批查不到 / 2=锚批PI为空）。纯观测。</summary>
    internal static void CountMergePiMissing(int reason)
    {
        if (Current.Value is { } c && reason >= 0 && reason < 3)
        {
            System.Threading.Interlocked.Increment(ref c.MergePiMissingReason[reason]);
        }
    }

    /// <summary>2026-11-09：按类别累加合批擂台统计（纯观测，不参与任何判定）。</summary>
    internal static void CountMergePi(int piClass, MergePiMetric metric, long delta = 1)
    {
        if (Current.Value is { } c)
        {
            var idx = piClass * 4 + (int)metric;
            if (idx >= 0 && idx < c.MergePiStats.Length)
            {
                System.Threading.Interlocked.Add(ref c.MergePiStats[idx], delta);
            }
        }
    }

    /// <summary>2026-11-07 W1：登记一次合批**未命中**。纯观测。</summary>
    internal static void CountMergeMiss()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeMisses);
    }

    /// <summary>2026-11-07 W2：登记一次「一个合法锚点都没有」（`NOT_ELIGIBLE`）。纯观测。</summary>
    internal static void CountMergeNotEligible()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeNotEligible);
    }

    /// <summary>2026-11-07 W2：登记一次「有锚点但被身份守卫全部挡掉」（`IDENTITY_UNREPRESENTABLE`）。纯观测。</summary>
    internal static void CountMergeIdentityUnrepresentable()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeIdentityUnrepresentable);
    }

    /// <summary>v5.0 §1.4.1：登记一次「找到真实合批锚点」。纯观测。</summary>
    internal static void CountMergeAnchorFound()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeAnchorFound);
    }

    /// <summary>v5.0 §1.4.1：登记一次「真正开始重排」。纯观测。</summary>
    internal static void CountMergeTrialStarted()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeTrialStarted);
    }

    /// <summary>v5.0 §1.4.1：登记一次「试排因硬约束产不出 Task」。纯观测。</summary>
    internal static void CountMergeHardRejectedCalendar()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeHardRejectedCalendar);
    }

    /// <summary>v5.0 §1.4.1：登记一次「合法合批方案在层序比较中落败」。纯观测。</summary>
    internal static void CountMergeComparedRejected()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeComparedRejected);
    }

    /// <summary>A4：登记一次「锚批试排**物理可行**」。纯观测。</summary>
    internal static void CountMergePhysicalFeasible()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergePhysicalFeasible);
    }

    /// <summary>A4：登记一次「物理可行且本次比较**胜出**」。纯观测。</summary>
    internal static void CountMergeComparedWins()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeComparedWins);
    }

    /// <summary>A4：登记一次「擂台结束后**最终采纳合批**」。纯观测。</summary>
    internal static void CountMergeFinallyAdopted()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.MergeFinallyAdopted);
    }

    /// <summary>A6：登记一次单 Op 锚表扫描及其规模。纯观测。</summary>
    internal static void CountSingleOpScan(long scannedTaskCount, long candidateCount)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.SingleOpScanCalls);
        System.Threading.Interlocked.Add(ref c.SingleOpScannedTaskCount, scannedTaskCount);
        System.Threading.Interlocked.Add(ref c.SingleOpCandidateCount, candidateCount);
    }

    /// <summary>
    /// 2026-11-06 BATCH-02：登记一次「1 批作为候选被纳入」。纯观测，不参与判定。
    /// </summary>
    internal static void CountBatchOneBatchCandidate()
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.BatchOneBatchCandidate);
    }

    /// <summary>
    /// 2026-11-06 BATCH-02.2：登记**被技术预算挤掉**的候选种数。
    ///   ⚠ 这不是「物理不合法」的计数 —— 只表示这一轮没去试排。纯观测。
    /// </summary>
    internal static void CountBatchCandidateOmittedByBudget(int omitted)
    {
        if (Current.Value is not { } c) return;
        if (omitted <= 0) return;
        System.Threading.Interlocked.Add(ref c.BatchCandidateOmittedByBudget, omitted);
    }

    /// <summary>
    /// 2026-11-06 P2-A：登记一次**外层批方案采纳**（纯追加快路径 / 回落旧路径）。
    ///   <paramref name="oldCount"/> 只在快路径时参与最小/最大旧表长度统计（0号位 v2.0 §三.5）；
    ///   <paramref name="appended"/> 为本次实际追加条数。纯观测，不参与任何判定。
    ///   最小/最大的比较未加锁 —— 与既有的 `DiagnoseLockedTasksMax` 同款做法，
    ///   多写者下最坏只是统计值偏一点，不影响任何业务判定。
    /// </summary>
    internal static void CountOuterAdopt(bool pureAppend, int oldCount, int appended)
    {
        if (Current.Value is not { } c) return;

        if (pureAppend)
        {
            System.Threading.Interlocked.Increment(ref c.OuterAdoptPureAppend);
            System.Threading.Interlocked.Add(ref c.OuterAdoptAppendedTasks, appended);
            if (oldCount < c.OuterAdoptOldCountMin) c.OuterAdoptOldCountMin = oldCount;
            if (oldCount > c.OuterAdoptOldCountMax) c.OuterAdoptOldCountMax = oldCount;
        }
        else
        {
            System.Threading.Interlocked.Increment(ref c.OuterAdoptFallback);
        }
    }

    /// <summary>2026-10-20 十二次定位：`ComputeDetachPositions` 被调用次数。</summary>
    internal static void CountDetachPosCall()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.DetachPosCalls);
    }

    internal static void CountSlotSearch()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.SlotSearches);
    }

    /// <summary>
    /// 2026-10-25（2号位 §4.3-2）：找槽**尝试**计数（`FindForwardSlot` / `FindBackwardSlot` 入口各一次）。
    ///   与 <see cref="CountSlotSearch()"/> 的区别见 <see cref="Counters.SlotSearchAttempts"/>。纯观测。
    /// </summary>
    internal static void CountSlotSearchAttempt(Counters? cached)
    {
        if (cached is not null) System.Threading.Interlocked.Increment(ref cached.SlotSearchAttempts);
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
        if (cached is not null) System.Threading.Interlocked.Increment(ref cached.SlotSearches);
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
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.Phase4PropagatedTasks);
    }

    /// <summary>Phase5 `CompactGaps` 门控放行（进入压实优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5CompactionRun()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.Phase5CompactionRuns);
    }

    /// <summary>Phase5 `OptimizeSetupSequences` 门控放行（进入序列优化体）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupOptimizationRun()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.Phase5SetupOptimizationRuns);
    }

    /// <summary>Phase5 `OptimizeSegment` 真正进入有界局部优化体（可重排段）时 +1。纯观测，不参与判定。</summary>
    internal static void CountPhase5SetupSegmentOptimized()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.Phase5SetupSegmentsOptimized);
    }

    /// <summary>V1_3 F-01：按插入身份回滚占用表时**未命中**条目数 +n。纯观测，不参与判定。</summary>
    internal static void CountOccupancyRollbackUnmatched(int n)
    {
        if (n > 0 && Current.Value is { } c) System.Threading.Interlocked.Add(ref c.OccupancyRollbackUnmatched, n);
    }

    /// <summary>
    /// 2026-10-26 A-4 第二刀：登记一次回滚的**形状** —— 日志条数 `k` 与**被触及资源窗表长度 `W`** 的峰值。
    ///   为什么要 `W`：搬移量是 `k × W`，只登记 `k` 会误判「日志小就不贵」。整链重跑一次通常只插 3~15 个窗，
    ///   但被触及资源上若有**上万条窗**，逐条 `RemoveAt` 仍是 k 次整表搬移 —— 这正是「小 k 也慢」的唯一解释路径。
    ///   纯观测，不参与任何判定；未开 scope 时只做一次 `null` 判断。
    ///   ⚠ **不进** <see cref="SolverLiveSnapshot"/>：那是 2号位 运行中探针在读的公开 record，加字段即改契约。
    /// </summary>
    internal static void CountOccupancyRollbackShape(int insertCount, int maxWindowLength)
    {
        if (Current.Value is not { } c) return;

        System.Threading.Interlocked.Increment(ref c.OccupancyRollbackCalls);
        if (insertCount > c.OccupancyRollbackInsertMax) c.OccupancyRollbackInsertMax = insertCount;
        if (maxWindowLength > c.OccupancyRollbackWindowMax) c.OccupancyRollbackWindowMax = maxWindowLength;
    }

    /// <summary>本 Run 回滚日志条数峰值（未开 scope ⇒ 0）。纯观测。</summary>
    internal static long OccupancyRollbackInsertMax => Current.Value?.OccupancyRollbackInsertMax ?? 0;

    /// <summary>本 Run 回滚被触及资源窗表长度峰值（未开 scope ⇒ 0）。纯观测。</summary>
    internal static long OccupancyRollbackWindowMax => Current.Value?.OccupancyRollbackWindowMax ?? 0;

    /// <summary>本 Run 回滚调用次数（未开 scope ⇒ 0）。纯观测。</summary>
    internal static long OccupancyRollbackCalls => Current.Value?.OccupancyRollbackCalls ?? 0;

    /// <summary>
    /// 本 Run 某热点槽的累计毫秒（未开 scope ⇒ 0）。纯观测。
    ///   给「逐需求自报」用：在擂台前后各取一次差，即得**该需求**的回滚耗时，而不是全 Run 聚合值。
    /// </summary>
    internal static double HotspotMsTotal(int hotspot) => Current.Value?.HotspotMs(hotspot) ?? 0;

    /// <summary>V1_4 NEW-05：MIXED 回落正排时按身份撤销的倒排残留占用窗数 +n。纯观测，不参与判定。</summary>
    internal static void CountMixedBackwardRollbackWindows(int n)
    {
        if (n > 0 && Current.Value is { } c) System.Threading.Interlocked.Add(ref c.MixedBackwardRollbackWindows, n);
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账 <c>Commit</c> 次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerCommit()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiStageLedgerCommits);
    }

    /// <summary>V1_4 NEW-04：本 Run 累计登记账按身份回滚次数 +1。纯观测，不参与判定。</summary>
    internal static void CountPiStageLedgerRollback()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiStageLedgerRollbacks);
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁因输入未投影而不可评估 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateUnevaluated()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGateUnevaluated);
    }

    /// <summary>V1_3 F-03：B-009 合法量门禁判越限/事实不可信并 Fail Closed +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlocked()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGateBlocked);
    }

    /// <summary>
    /// 2026-10-12：**有策略路径**完整评估后**过闸** +1（= 放行档）。
    ///   只在 `ApplyPiLegalQuantityGate` 走完全部判据、未 Conflict 时调用；三条早退不计。纯观测，不参与判定。
    /// </summary>
    internal static void CountPiLegalQuantityGatePassed()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGatePassed);
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径被同一门禁判未满足 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateBlockedViaB010Fallback()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGateBlockedViaB010Fallback);
    }

    /// <summary>2026-10-12：B-010 缺策略兜底路径过闸并成批 +1。纯观测，不参与判定。</summary>
    /// <summary>
    /// 2026-11-02：**完全缺配的合法无 PI 来源 C 桶**按 v1.15 §1320 取 Q_C 作经济软 Preferred 并成批 +1。
    /// 纯观测，不参与任何业务判定。
    /// </summary>
    internal static void CountBatchSoftPreferredNoPi()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.BatchSoftPreferredNoPi);
    }

    // ── 2026-11-03：Stage 交接等待 / PI Stage 上下文 的观测入口（全部纯观测）──────────────
    internal static void SetStageHandoffRuleShape(int total, int active)
    {
        if (Current.Value is { } c)
        {
            System.Threading.Interlocked.Exchange(ref c.StageHandoffRulesTotal, total);
            System.Threading.Interlocked.Exchange(ref c.StageHandoffRulesActive, active);
        }
    }

    internal static void CountStageHandoffMatched()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.StageHandoffMatched);
    }

    internal static void CountStageHandoffApplied(int minutes)
    {
        if (Current.Value is { } c)
        {
            System.Threading.Interlocked.Increment(ref c.StageHandoffApplied);
            if (minutes > 0) System.Threading.Interlocked.Add(ref c.StageHandoffAppliedMinutes, minutes);
        }
    }

    internal static void CountStageHandoffSkippedWork()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.StageHandoffSkippedWork);
    }

    internal static void SetPiStageIndexShape(int entries, int distinctPi)
    {
        if (Current.Value is { } c)
        {
            System.Threading.Interlocked.Exchange(ref c.PiStageIndexEntries, entries);
            System.Threading.Interlocked.Exchange(ref c.PiStageIndexDistinctPi, distinctPi);
        }
    }

    internal enum PiStageResolveMiss { IndexEmpty, PiMissing, StageMissing, KeyNotFound }

    internal static void CountPiStageResolve(PiStageResolveMiss? miss)
    {
        if (Current.Value is not { } c) return;
        System.Threading.Interlocked.Increment(ref c.PiStageResolveCalls);
        switch (miss)
        {
            case null: System.Threading.Interlocked.Increment(ref c.PiStageResolveHit); break;
            case PiStageResolveMiss.IndexEmpty: System.Threading.Interlocked.Increment(ref c.PiStageNull_IndexEmpty); break;
            case PiStageResolveMiss.PiMissing: System.Threading.Interlocked.Increment(ref c.PiStageNull_PiMissing); break;
            case PiStageResolveMiss.StageMissing: System.Threading.Interlocked.Increment(ref c.PiStageNull_StageMissing); break;
            case PiStageResolveMiss.KeyNotFound: System.Threading.Interlocked.Increment(ref c.PiStageNull_KeyNotFound); break;
        }
    }

    internal static void CountPiLegalQuantityGatePassedViaB010Fallback()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGatePassedViaB010Fallback);
    }

    /// <summary>2026-10-22：`QuantityAuthority=ORDER_ONLY` 使 B-009 门禁**不适用**并放行 +1。纯观测，不参与判定。</summary>
    internal static void CountPiLegalQuantityGateSkippedOrderOnly()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGateSkippedOrderOnly);
    }

    /// <summary>
    /// 2026-10-22：`ORDER_ONLY` 因 PI 权威事实集**整体为空**被拒绝采信（回落 Fail Closed）+1。
    ///   纯观测，不参与判定 —— **非 0 即表示本次 Run 的 PI 事实装载有问题**。
    /// </summary>
    internal static void CountPiLegalQuantityGateOrderOnlyRejectedEmptyFacts()
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Increment(ref c.PiLegalQuantityGateOrderOnlyRejectedEmptyFacts);
    }

    /// <summary>
    /// R-1/R-2（2026-10-12，B-011）：经济软区间偏离的批数登记 +<paramref name="n"/>（**软**偏离、非硬失败）。
    /// 纯观测，不参与判定 —— 让「经济软参数被如实消费」可观测（10 §19「只有一种合法选择时记录限制来源」）。
    /// </summary>
    internal static void CountBatchPolicyInvalid(int n = 1)
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Add(ref c.BatchPolicyInvalids, n);
    }

    /// <summary>
    /// 2026-10-13：Batch Policy **状态不可获得**（输入契约缺陷）的需求数 —— 见
    /// <see cref="SolverRunCounters.BatchPolicyUnknowns"/>。纯观测，不参与判定。
    /// </summary>
    internal static void CountBatchPolicyUnknown(int n = 1)
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Add(ref c.BatchPolicyUnknowns, n);
    }

    internal static void CountEconomicBatchDeviation(int n = 1)
    {
        if (Current.Value is { } c) System.Threading.Interlocked.Add(ref c.EconomicBatchDeviations, n);
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
