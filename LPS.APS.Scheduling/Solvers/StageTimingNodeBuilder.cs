using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 「无 Routing Stage」纯内存时间节点构造器 —— 0号位 2026-09-29 裁决 §十四第 4/5/9 项（④⑤⑨）。
///
/// 【它解决什么】
///   末端（或整条）无 Routing 的 Stage（如 `BJ_FINAL` 完工）**没有对应的真实 Operation Task**，
///   其提前期无法由 <see cref="FinalTaskDraft"/> 表达；而 0号位 §9.2 明文**禁止**为此伪造
///   假 RoutingOperation / Task.Id / TaskNo / MESWorkOrder。
///   ⇒ 本类产出**纯内存**的 <see cref="DemandCompletionFact"/>，随
///   <see cref="DomainSolveResult.DemandCompletions"/> 出口，**绝不进 <see cref="DomainSolveResult.FinalTasks"/>**。
///
/// 【④ 纯内存节点】产出的 <see cref="StageTimingSegmentFact.NodeType"/> 恒为 `"STAGE_TIMING"`；
///   不建 Task、不生成 TaskNo、不进 MES 下发、不进 Execution Batch、不作为真实 Operation。
/// 【⑤ 只认契约】只接受 0号位 定的 4 个 `MatchLevel`；其余（含旧名 `MATERIAL`/`FAMILY`）= 契约违例 ⇒ 兜底。
///   命中级（含 `STAGE_LEADTIME_MISSING`）的**值一律原样透传，我方不做符号判断**。
///
///   ⚠️ **2026-09-29 口径更正（2号位 r13519 载入 Core 契约，取代当日早先口径）**：
///   此前「命中到参数行但折算值 ≤ 0 **同样视同未命中**、同样给 72h 兜底」**已作废**。
///   **命中就是命中** —— 折算值为 0（或负）**按命中产出**：`LeadTimeHours = 0`、
///   `MatchLevel` = 实际命中级（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00` ⇒ 值 0 + `FACTORY_STAGE_DEFAULT`）。
///   ⇒ 消费侧认 `MatchLevel`，但**不要**假定「值 0 ⇒ 一定是兜底」：值 0 也可能是一条真实命中的参数。
///   **0 是参数行写下的值，装载层与消费侧都不得替参数侧改判。**
///
///   注：0号位「**不得默认 0 小时**」管的是**未命中时不得拿 0 顶上**（那是静默丢提前期），
///   与「参数行明确写了 0」是两回事 —— 后者是**真实命中**，透传即可。
/// 【⑨ 时间出口】末端真实 `Task.End` + 其**之后**各无 Routing Stage 的提前期之和 = `CompletionTime`/`AvailableTime`。
///
/// 【边界】本类只用 `DomainSolveRequest` / `ConstraintContext` 的既有数据，**不查库、不查参数表**
///   （0号位 §4.4 末句：1号位 只消费已解析的 StageLeadTimeFact）。
///
/// 【可见性说明】⚠ **2026-10-14 更正**：本处旧文曾称「<c>Scheduling</c> 无 <c>InternalsVisibleTo(Tests)</c>」
///   —— 该断言**已过期**：`LPS.APS.Scheduling.csproj` 现含 `<c>&lt;InternalsVisibleTo Include="LPS.APS.Tests" /&gt;</c>`
///   （故 <see cref="TracePathUndetermined"/>、<see cref="TraceCompletionTimeUnavailable"/> 等
///   <c>internal</c> 成员**可被单测直接引用**）。
///   尽管如此，**纯逻辑入口 <see cref="BuildCore"/> 仍保持 public 且只收公开类型** —— 这是**有意的接口约束**
///   （纯函数、可脱离 Context 单测），**不是**可见性限制所致；
///   依赖 <c>internal</c> 的 <see cref="ConstraintContext"/> 的装配留在 <see cref="Build"/>（internal）。
/// </summary>
public static class StageTimingNodeBuilder
{
    /// <summary>
    /// 契约码：三级均未命中 / 值不可用。PM 原码；1号位 回执 §四 定为 V1 合法 4 值之一。
    ///
    /// ⚠ 2026-10-15（0号位 第九轮 §3.1/§3.3）**口径更正**：命中本码 = **没有可用的合法参数**
    ///   ⇒ `BuildCore` **不产出时间段、不推进游标**，只留 Issue（不虚造时长）。
    ///   原「72h 兜底」被明禁 —— 逐字（原件 `0号位代码审核意见/归档/20261015/
    ///   APS_V1_20261015_致1号位第九轮代码审核正式复核回复_v1.0.md` `:32`）：
    ///   「对无合法参数、Path不可判、Stage边界不明的情形，保留真实Issue与诊断，**不使用0或72h臆造时间**」。
    ///   ℹ **出处更正（2026-10-26 自查）**：本处旧文写「72h 是 1号位 侧早先自行加的」，**不准确**。
    ///   72 出自 0号位 2026-09-29 那次答复（「三级均未命中 ⇒ 记入 `STAGE_LEADTIME_MISSING`，并按 3 天兜底」），
    ///   我方 09-29 件《…字段载体落码提请_v1.0_20260929》§四 是按「**据转述登记**」落的法（该节明写
    ///   「未见书面件 …… 正式口径以 0号位 书面件为准」）。而 0号位 2026-10-19《规则与参数引擎…任务单》`:15`
    ///   把这条定性为「**仅为历史证据，不得独自覆盖当前正式正文**」，并把「3天/72小时具体计算单位、
    ///   StageTimingNode、标记/提示/**真实排程消费**」列为**待核项** ⇒ 72 只作标记、**不进时间累计**。
    /// </summary>
    // 可见性：`internal`（2026-10-29 R09-③）—— Phase1 的 `BuildRoutelessLeadSteps` 要用**同一个**
    // 缺 LT 判据，不能自己另写一份字符串常量（两处口径一旦分叉，「排程约束」与「事后描述」就对不上）。
    internal const string MissingLevel = "STAGE_LEADTIME_MISSING";

    /// <summary>④ 纯内存节点的显式标记（0号位 §9.3；2号位 写库口的判据）。</summary>
    private const string StageTimingNodeType = "STAGE_TIMING";

    /// <summary>
    /// 追溯码（**决策说明层**，非冻结 `ReasonCode` 字典）：路径未唯一确定 ⇒ 无 Routing Stage 提前期
    /// 取自候选路径并集，`CompletionTime` 只是候选口径。见 <see cref="Build"/>。
    /// </summary>
    internal const string TracePathUndetermined = "STAGE_TIMING_PATH_UNDETERMINED";

    /// <summary>
    /// 追溯码（**决策说明层**，非冻结 `ReasonCode` 字典）：**既无末端真实 Task、又无任何无 Routing Stage
    /// 提前期**进入 `CompletionTime` ⇒ 该时间退化为 `request.PlanningStart`，**不是**已排/预计完工/供给可用时间。
    ///
    /// 【为什么单列】0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》§三.2 逐字：
    ///   「`BuildCore`仍用`terminal?.PlannedEndTime ?? PlanningStart`作游标，在`terminal==null`且
    ///    `segments.Count==0`时仍可能生成`CompletionTime=PlanningStart`的`DemandCompletionFact`，
    ///    而r13685新增`SolveTraceNote`仅在`!PathDetermined && segments.Count>0`时触发。请补此可见反例…」
    ///   判词**成立**（源码已核）。本码把它从「静默」改成「显式留痕」。
    ///   ⚠ 与「仅有 StageLeadTime 的业务 Stage」（`segments.Count>0`，0号位 §10.3 认可）**不可混为一类**。
    /// </summary>
    internal const string TraceCompletionTimeUnavailable = "DEMAND_COMPLETION_TIME_UNAVAILABLE";

    /// <summary>0号位 2026-09-29 裁决 §4.2/§4.3：V1 只认的三级命中码（Material / ProductFamily 级不认）。</summary>
    private static readonly HashSet<string> HitLevels = new(StringComparer.Ordinal)
    {
        "DEPT_EXACT",
        "FACTORY_STAGE_DEFAULT",
        "GLOBAL_STAGE_DEFAULT"
    };

    /// <summary>
    /// 装配入口：从 <see cref="ConstraintContext"/> 取出求解范围与依赖图，定出各 Demand 的末端真实 Task，
    /// 再交给纯逻辑 <see cref="BuildCore"/>。
    /// </summary>
    internal static List<DemandCompletionFact> Build(
        DomainSolveRequest request,
        ConstraintContext constraints,
        IReadOnlyList<FinalTaskDraft> allScheduledTasks)
    {
        var tasksByDemand = new Dictionary<string, List<FinalTaskDraft>>(StringComparer.Ordinal);
        foreach (var group in allScheduledTasks.GroupBy(t => t.SourceDraftId, StringComparer.Ordinal))
        {
            tasksByDemand[group.Key] = group.ToList();
        }

        var terminalByDemandKey = new Dictionary<string, FinalTaskDraft>(StringComparer.Ordinal);
        foreach (var demand in request.LogicalProductionDemands)
        {
            tasksByDemand.TryGetValue(demand.LogicalDemandKey, out var demandTasks);
            var terminal = PickTerminalTask(demandTasks, constraints, demand.MaterialId);
            if (terminal != null)
            {
                terminalByDemandKey[demand.LogicalDemandKey] = terminal;
            }
        }

        var pathUndetermined = new List<string>();
        var completionTimeUnavailable = new List<string>();
        var missingLeadTimeStages = new List<(string DemandKey, string StageCode)>();
        var workWindowUnavailableStages = new List<(string DemandKey, string StageCode, string Reason)>();
        var facts = BuildCore(
            request, constraints.EffectiveStages, terminalByDemandKey,
            pathUndetermined, completionTimeUnavailable, missingLeadTimeStages,
            workWindowUnavailableStages, tasksByDemand);

        // ── S2-01（0号位 2026-10-14《…再次整改回执正式审核回复》§二）──
        //   判词逐字：「**对尚未选定Path或实际未排下的需求，不得把任意候选路径并集产生的时间当作权威承诺时间，
        //   也不得据此生成假Task、漏计真实StageLeadTime或静默报完整完成。**」
        //   ⇒ 路径未唯一确定、且**确实有**无 Routing Stage 提前期进入了 `CompletionTime` 时，
        //     该需求的时间只是**候选/诊断**口径 ⇒ 经既有合法追溯通道（`SolveTraceNote`，
        //     与 `DEMAND_GOAL_ABSENT` 同一先例，见 `PhaseTwoInitialScheduler:739`）**显式留痕，绝不静默**。
        //   ⚠ **不自造 `ReasonCode` 枚举**：此位承载的是决策说明层取值（0号位 Q4）。
        //   ⚠ 归一化 ON（V1 现状：全部 op 均为 `DEFAULT/1` ⇒ 该 Route 唯一 Path ⇒ 已确定）时
        //     `pathUndetermined` **恒空** ⇒ **零回归**。仅「归一化 OFF + 真实多 Path」才可能非空。
        foreach (var key in pathUndetermined)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = key,
                ReasonCode = TracePathUndetermined,
                Message = "无 Routing Stage 提前期取自**候选路径并集**（该需求未选定唯一 Path，或整条未排下）"
                          + "⇒ 本需求 `CompletionTime` 为**候选口径**，不得作为权威承诺时间；"
                          + "待真实 Path 事实（2号位/5号位）落地后重算。",
                Level = "Warning"
            });
        }

        // ── 0号位 2026-10-14《…综合正式审核回复 v1.1》§三.2：**不得静默**给出退化的 `CompletionTime` ──
        //   判词：「不得将`PlanningStart`或候选路径并集时间作为正式已排/预计完工/供给可用时间；
        //     不能编造Task或制造业务短缺原因。」⇒ 此位显式留痕，供 2号位 读模型/订单交付/Supply 可用时间侧
        //   识别「该 Demand 的 `CompletionTime` 无真实依据」，不得当作已排承诺。
        foreach (var key in completionTimeUnavailable)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = key,
                ReasonCode = TraceCompletionTimeUnavailable,
                Message = "该 Demand **无末端真实 Task（未排下）**，且其 `StageTimings` 为空 —— "
                          + "或该物料纯无 Routing 且无任何提前期段，或该物料**有真实 Routing** 而其无 Routing "
                          + "Stage 提前期属**候选口径**已按第七轮 §八.3 抑制 ⇒ `CompletionTime`/`AvailableTime` "
                          + "退化为 `PlanningStart`，**不是**已排/预计完工/供给可用时间，"
                          + "**不得**作为承诺或数量闭合依据；真实未排原因见 `UnscheduledTasks`。",
                Level = "Warning"
            });
        }

        // ── N8-01（0号位 2026-10-15 第九轮 §3.1/§3.3）：缺参的 Stage 显式进 Issue，**不虚造时间** ──
        //   冻结依据：规则 S-006「缺参报 STAGE_LEADTIME_MISSING」；《04》场景7「无参数时形成
        //   `STAGE_LEADTIME_MISSING`」「缺失参数进入 Issue/Explanation」；0号位 2026-09-29 §4.4
        //   「不得默认 0 小时 …… 无法证明时显式进入数据/参数 Issue」「也不得删除 Stage」。
        //   ⇒ 该 Stage 仍在范围内、仍被识别（不丢），只是**没有时长**可用，故只留 Issue、不产出时间段。
        foreach (var (demandKey, stageCode) in missingLeadTimeStages)
        {
            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = demandKey,
                ReasonCode = MissingLevel,
                Message = $"Stage `{stageCode}` 无可用 `StageLeadTimeParam`（DEPT_EXACT / "
                          + "FACTORY_STAGE_DEFAULT / GLOBAL_STAGE_DEFAULT 三级均未命中）"
                          + "⇒ 该 Stage 的后续正式时间**无法证明**：**不产出时间段、不推进游标**，"
                          + "**不使用 0 或 72h 臆造时长**。该 Stage **保留在求解范围内、不得删除**；"
                          + "待 3号位 补参数后重算（S-006 /《04》场景7 / 0号位 2026-09-29 §4.4）。",
                Level = "Warning"
            });
        }

        // ── R09-①（2026-10-29 落码）：无可信/不足够 WORK 窗口的 Stage 显式留痕，**不虚造时间** ──
        //   冻结依据：0号位 2026-10-23《04》§3 逐字「无可信或不足够WORK时，不假设7×24/默认8h，不生成确定Stage
        //   完成/下游AvailableTime；受影响份额局部未排、其它合法需求继续Run，数量守恒。」
        //   **复用既有 ReasonCode**（`DEMAND_COMPLETION_TIME_UNAVAILABLE`），不自造新码（0号位 Q4 / 权威 15 码）。
        foreach (var (demandKey, stageCode, reason) in workWindowUnavailableStages)
        {
            var why = reason switch
            {
                "MISSING_DEPARTMENT_CONTEXT" =>
                    "该 (MaterialId, StageCode) 在 `MaterialStageDepartmentContexts` 无行 ⇒ **部门未知**",
                "NO_WORK_WINDOW" =>
                    "按 `(FactoryId, DepartmentId)` 精确取用 `DepartmentWorkWindows` **无任何窗口**（该 scope 该期无合法工时）",
                "INSUFFICIENT_WORK" =>
                    "`DepartmentWorkWindows` 窗口总计**不足以**消耗该 Stage 的有效生产小时",
                _ => reason
            };

            constraints.TraceNotes.Add(new SolveTraceNote
            {
                Key = demandKey,
                ReasonCode = TraceCompletionTimeUnavailable,
                Message = $"Stage `{stageCode}` 无法按合法 WORK 窗口推进：{why}"
                          + "⇒ **不产出时间段、不推进游标**，**不假设 7×24 / 默认 8h**。"
                          + "该需求 `CompletionTime`/`AvailableTime` **不得**作确定承诺或下游可用时间；"
                          + "待 2号位 补齐 G-2 窗口 / 5号位 补齐部门事实后重算。",
                Level = "Warning"
            });
        }

        return facts;
    }

    /// <summary>
    /// 纯逻辑：为每个 Demand 构造 Demand 级完成时间事实。**无副作用、不读 Context、可单测。**
    /// </summary>
    /// <param name="request">求解请求（用其 StageLeadTimes / StageSequenceChains / LogicalProductionDemands / PlanningStart）。</param>
    /// <param name="effectiveStages">本次求解范围内的可达 Stage：MaterialId → StageCode 集合。</param>
    /// <param name="terminalByDemandKey">各 Demand 的末端真实 Task；无真实 Task 的 Demand 不出现在此字典中。</param>
    /// <param name="pathUndeterminedDemandKeys">
    /// 可选收集器（S2-01）：**路径未唯一确定且确实有无 Routing Stage 提前期进入 `CompletionTime`** 的
    /// Demand 键。装配层 <see cref="Build"/> 据此写 `SolveTraceNote`（本方法保持**纯函数、无副作用**）。
    /// </param>
    /// <param name="completionTimeUnavailableDemandKeys">
    /// 可选收集器（S2-01 补）：**既无末端真实 Task、又无任何提前期段** ⇒ `CompletionTime` 退化为
    /// `PlanningStart` 的 Demand 键（0号位 2026-10-14 §三.2）。同样由 <see cref="Build"/> 写 `SolveTraceNote`。
    /// </param>
    /// <param name="missingLeadTimeStages">
    /// 可选收集器（N8-01）：**在范围内、但三级均未命中合法 `StageLeadTimeParam`** 的
    /// `(DemandKey, StageCode)`。这些 Stage **不产出时间段**（不虚造时长），由 <see cref="Build"/>
    /// 写 `STAGE_LEADTIME_MISSING` 的 `SolveTraceNote`（0号位 2026-10-15 第九轮 §3.1/§3.3）。
    /// </param>
    public static List<DemandCompletionFact> BuildCore(
        DomainSolveRequest request,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        IReadOnlyDictionary<string, FinalTaskDraft> terminalByDemandKey,
        ICollection<string>? pathUndeterminedDemandKeys = null,
        ICollection<string>? completionTimeUnavailableDemandKeys = null,
        ICollection<(string DemandKey, string StageCode)>? missingLeadTimeStages = null,
        ICollection<(string DemandKey, string StageCode, string Reason)>? workWindowUnavailableStages = null,
        IReadOnlyDictionary<string, List<FinalTaskDraft>>? tasksByDemandKey = null)
    {
        var result = new List<DemandCompletionFact>();
        if (request.LogicalProductionDemands.Count == 0)
        {
            return result;
        }

        var leadTimeByKey = new Dictionary<(int MaterialId, string StageCode), StageLeadTimeFact>();
        foreach (var fact in request.StageLeadTimes)
        {
            leadTimeByKey[(fact.MaterialId, fact.StageCode)] = fact;
        }

        var chainByMaterial = new Dictionary<int, StageSequenceChain>();
        foreach (var chain in request.StageSequenceChains)
        {
            chainByMaterial[chain.MaterialId] = chain;
        }

        foreach (var demand in request.LogicalProductionDemands)
        {
            terminalByDemandKey.TryGetValue(demand.LogicalDemandKey, out var terminal);

            // 有末端真实 Task ⇒ 从它的 End 起累加；整条需求无真实 Operation Task ⇒ 自计划起点纯提前期累计
            // （0号位 §10.3：整条无真实 Operation Task 时 1号位 仍必须返回 Demand 级 CompletionTime）
            var cursor = terminal?.PlannedEndTime ?? request.PlanningStart;

            var segments = new List<StageTimingSegmentFact>();
            DateTime? requiredStageEnd = null;

            // G-3 `CompletionTimeCertain` 判据①（缺 LT）：本 Demand 是否有 Stage 因三级均未命中而**不产段时间**。
            var leadTimeMissingForDemand = false;

            // R09-① 判据：本 Demand 是否有 Stage 因**无可信/不足够 WORK 窗口**而不产段时间。
            //   与「缺 LT」分开记：成因不同，但两者都必须让 `CompletionTime` **不得**当确定承诺。
            var workWindowUnavailableForDemand = false;

            // ①「严格改法」（2026-10-12 / 2026-10-14 S2-01）：传入本需求**末端真实 Task 的选中路径**。
            //   已确定唯一 Path ⇒ 严格过滤（权威）；仅确定到 Route / 未确定 ⇒ 并集（**候选口径**）。
            //   归一化 ON（两列为空且载荷全 `DEFAULT/1`）⇒ 唯一 Path ⇒ 已确定，零行为变化。
            var routeless = ResolveRoutelessStages(
                demand.MaterialId, effectiveStages, request,
                terminal?.RouteCode, terminal?.PathId);
            var routelessStages = routeless.Stages;

            // ── 0号位 2026-10-14 第七轮正式复核 §八.3 门控（2026-10-15 落码）──
            //   判词逐字：「必须可由**结果本身及稳定身份**区分**权威已排完成、真正纯无Routing且经合法
            //     StageLeadTime形成的时间约束、未排占位/候选诊断**」「有StageTiming片段但**无选定合法Path/
            //     末端Task**时，也可能存在**仅供诊断的时间**」「不接受只写一条Warning或只证明没有FinalTask」。
            //
            //   读码实测的**结构事实**：`PathDetermined` 取 `terminal?.RouteCode` ⇒ `terminal == null` 时
            //   **恒为 false** ⇒ 未排需求的提前期**一律**是候选口径；而此前它照样写进 `StageTimings`
            //   ⇒ 「(ii) 真正纯无Routing的**合法时间约束**」与「(iii) 有Routing但未排的**占位时间**」
            //   在 `DemandCompletionFact` 上**完全同形**（`TerminalFinalDraftId == null` 且 `StageTimings` 非空）
            //   ⇒ 下游无法「由结果本身」区分。
            //
            //   门控判据 = **是否有真实 Routing 在求解范围内**（`HasRoutingInScope`），**不是** `PathDetermined`：
            //     · 未排 **且** 有 Routing（`true`）⇒ 时间无真实依据 ⇒ **不产出** `StageTimings`
            //       ⇒ 该 Fact 落为「无末端 Task + 无时间段」= 0号位 点名的 (iii) **占位/候选诊断**；
            //     · 未排 **且** 纯无 Routing（`false`）⇒ 0号位 §10.3 明文认可的「自起点起的纯提前期累计」
            //       ⇒ **保留**（(ii) 合法时间约束）—— **不**因本门控被取消能力。
            //   ⇒ 门控后三态在 Fact 上**可判**：
            //     (i) `TerminalFinalDraftId != null`；(ii) 二者皆无但 `StageTimings.Count > 0`；
            //     (iii) `TerminalFinalDraftId == null && StageTimings.Count == 0`。
            //   ⚠ **不静默**：被抑制的候选口径由下方既有 `completionTimeUnavailable` 通道留痕
            //     （`DEMAND_COMPLETION_TIME_UNAVAILABLE`），其文案已同步覆盖「含候选口径被抑制」这一支。
            //   ⚠ **零回归**：`terminal != null`（已排下）时本判据恒 false ⇒ 逐字不变；
            //     纯无 Routing 未排 时 `HasRoutingInScope == false` ⇒ 亦不变。
            var suppressCandidateLeadTimes = terminal is null && routeless.HasRoutingInScope;

            if (!suppressCandidateLeadTimes &&
                routelessStages.Count > 0 &&
                chainByMaterial.TryGetValue(demand.MaterialId, out var chain) &&
                chain.Stages.Count > 0)
            {
                var seqByStage = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var step in chain.Stages)
                {
                    seqByStage[step.StageCode] = step.StageSeq;
                }

                int? terminalSeq = null;
                if (terminal != null && seqByStage.TryGetValue(terminal.StageCode, out var terminalStageSeq))
                {
                    terminalSeq = terminalStageSeq;
                }

                // 按 StageSeq 升序（`StageSequenceChain.Stages` 契约已是升序，此处再排一次防上游乱序）；
                // 同 Seq 时按 StageCode 定序，保证结果可复现。
                foreach (var stageCode in routelessStages
                             .Where(seqByStage.ContainsKey)
                             .OrderBy(s => seqByStage[s])
                             .ThenBy(s => s, StringComparer.Ordinal))
                {
                    var seq = seqByStage[stageCode];

                    // 只取末端真实 Task **之后**的 Stage；整条需求无真实 Operation Task 时全取。
                    if (terminalSeq.HasValue && seq <= terminalSeq.Value)
                    {
                        continue;
                    }

                    var (hours, level) = ResolveLeadTime(leadTimeByKey, demand.MaterialId, stageCode);

                    // ── N8-01（0号位 2026-10-15 第九轮 §3.1/§3.3）：缺参**不虚造时间** ──
                    //   逐字：「对无合法参数 …… 的情形，保留真实 Issue 与诊断，**不使用 0 或 72h 臆造时间**」；
                    //   反证要求「缺参数→Issue 不虚造 Time」。与 2026-09-29 §4.4 一致：
                    //   「不得默认 0 小时 …… 对该 Stage 的后续正式时间无法证明时，应显式进入数据/参数 Issue」，
                    //   且「也不得删除 Stage」。
                    //   ⇒ 该 Stage **不产出时间段、不推进游标**（编不出时长），改由 Issue 表示（见 `Build`）。
                    if (string.Equals(level, MissingLevel, StringComparison.Ordinal))
                    {
                        missingLeadTimeStages?.Add((demand.LogicalDemandKey, stageCode));
                        leadTimeMissingForDemand = true;
                        continue;
                    }

                    // ── R09-①（0号位 2026-10-23 §2/§3）：**按部门/工厂合法 WORK 窗口逐片推进** ──
                    //   整改前逐字：`var end = cursor.AddHours((double)hours);` —— 按**墙钟**小时直接推进，
                    //   不看 WORK/OFF，也没有「起始当天合法余时」。这正是 R09-① 点名未落码的那一行。
                    //   整改后：`hours` 按**有效生产小时**在 `(FactoryId, DepartmentId)` 窗口内逐片消耗；
                    //   无可信/不足够 WORK ⇒ **不产段、不推进游标**（不假设 7×24/默认 8h），
                    //   由 `workWindowUnavailableForDemand` ⇒ `CompletionTimeCertain=false` 留痕。
                    // 2026-11-04（2号位 r14030）：FINAL/OUTS 阶段无部门，按工厂级 WORK 日历推进
                    var isLeadTimeOnly = leadTimeByKey.TryGetValue((demand.MaterialId, stageCode), out var ltFact)
                                        && ltFact.IsLeadTimeOnly;
                    if (!TryAdvanceByWorkHours(request, demand, stageCode, cursor, hours, out var end, out var workReason, isLeadTimeOnly))
                    {
                        workWindowUnavailableForDemand = true;
                        if (workWindowUnavailableStages != null)
                        {
                            workWindowUnavailableStages.Add((demand.LogicalDemandKey, stageCode, workReason));
                        }

                        continue;
                    }


                    segments.Add(new StageTimingSegmentFact
                    {
                        NodeType = StageTimingNodeType,
                        StageCode = stageCode,
                        LeadTimeHours = hours,
                        MatchLevel = level,
                        StartTime = cursor,
                        EndTime = end
                    });

                    cursor = end;

                    if (!string.IsNullOrEmpty(demand.RequiredStageCode) &&
                        string.Equals(stageCode, demand.RequiredStageCode, StringComparison.Ordinal))
                    {
                        requiredStageEnd = end;
                    }
                }
            }

            // ── R09-②（2026-10-29 落码）：**结构上无法定位**的无 Routing Stage ──
            //   在求解范围内、却「没有 StageSequenceChain」或「链里根本没有这个 Stage」时，
            //   无法判定它在末端 Task **之前**还是**之后** ⇒ 提前期要么被漏计（时间低估）、
            //   要么本不该计（时间虚高）。两种都**不能声称确定**。
            //   0号位 2026-10-23《04》§1.5 逐字：「`StageDetail.StageSeq` 与真实 Routing、StageTiming
            //   必须连续形成时间约束」⇒ 无 seq 的 Stage 本身就构不成合法时间约束。
            //   Fail Closed：只有「每一个在范围内的无 Routing Stage 都能定位到链上」才允许 `true`。
            //   注：`suppressCandidateLeadTimes` 分支下 `terminal == null` ⇒ 判据③ 已命中，此处不必重复。
            var routelessUnplaceable = false;
            if (!suppressCandidateLeadTimes && routelessStages.Count > 0)
            {
                if (!chainByMaterial.TryGetValue(demand.MaterialId, out var placementChain) ||
                    placementChain.Stages.Count == 0)
                {
                    routelessUnplaceable = true;
                }
                else
                {
                    var placedStages = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var step in placementChain.Stages)
                    {
                        placedStages.Add(step.StageCode);
                    }

                    routelessUnplaceable = routelessStages.Any(s => !placedStages.Contains(s));
                }
            }

            // RequiredStageCompletionTime：做到供给阈值 Stage 为止的完成时间（0号位 §10.3 两个口径都给）
            DateTime? requiredCompletion = null;
            if (!string.IsNullOrEmpty(demand.RequiredStageCode))
            {
                if (requiredStageEnd.HasValue)
                {
                    requiredCompletion = requiredStageEnd;
                }
                else if (terminal != null &&
                         chainByMaterial.TryGetValue(demand.MaterialId, out var requiredChain))
                {
                    var requiredStep = requiredChain.Stages.FirstOrDefault(
                        s => string.Equals(s.StageCode, demand.RequiredStageCode, StringComparison.Ordinal));
                    var terminalStep = requiredChain.Stages.FirstOrDefault(
                        s => string.Equals(s.StageCode, terminal.StageCode, StringComparison.Ordinal));

                    // ── R09-T11（2026-10-29 落码）：EDGE 供给阈值与 ROOT 最终完工**必须区分** ──
                    //   0号位 2026-10-23《04》§3 R09-T11 逐字：「EDGE供给阈值在末端真实Task之前，
                    //   ROOT最终完工更晚 | `RequiredStageCompletionTime` 与 ROOT 区分；
                    //   **既不提前释放不合格供给，也不错误压到末端完工时点**」。
                    //   · `==`：供给阈值 Stage **就是**末端 Stage（同一批 Task）⇒ 时间 = 末端 `Task.End`，正确。
                    //   · 严格早于：该 Stage **自己有真实 Task**（在前序工序里）⇒ 取**它自己的** Task 末，
                    //     不得压到末端完工时点（那是晚释放供给）。
                    //   · 取不到该 Stage 的真实 Task（例如纯无 Routing 且无提前期段）⇒ `null`：
                    //     **不臆造**、也不回落到末端完成时间 —— 宁可不释放供给（Fail Closed）。
                    if (requiredStep != null && terminalStep != null)
                    {
                        if (requiredStep.StageSeq == terminalStep.StageSeq)
                        {
                            requiredCompletion = terminal.PlannedEndTime;
                        }
                        else if (requiredStep.StageSeq < terminalStep.StageSeq)
                        {
                            requiredCompletion = ResolveStageTaskEnd(
                                tasksByDemandKey, demand.LogicalDemandKey, demand.RequiredStageCode);
                        }
                    }
                }
            }

            // ── S2-01 留痕判据（见 Build 的 TracePathUndetermined）──
            //   只有「路径未唯一确定」**且**「确实有提前期段进入了 CompletionTime」才算候选口径；
            //   无 Routing Stage 一个都没有 ⇒ 时间完全来自真实 Task.End ⇒ 与路径无关，**不误报**。
            // 判据②（路径未唯一确定）**就地求值**，不依赖调用方是否传收集器：
            //   `CompletionTimeCertain` 必须与留痕**同源**，不能因为没人订阅留痕就悄悄放弃标注。
            var pathUndeterminedForDemand = !routeless.PathDetermined && segments.Count > 0;
            if (pathUndeterminedForDemand)
            {
                pathUndeterminedDemandKeys?.Add(demand.LogicalDemandKey);
            }

            // ── 0号位 2026-10-14《APS V1｜致1号位综合正式审核回复 v1.1》§三.2 **代码可核风险（判词成立）** ──
            //   判词逐字：「`BuildCore`仍用`terminal?.PlannedEndTime ?? PlanningStart`作游标，在`terminal==null`
            //     且`segments.Count==0`时仍可能生成`CompletionTime=PlanningStart`的`DemandCompletionFact`…」
            //   ⇒ 此时 `cursor` **从未推进**：既无真实 Task 的 `End`，也无任何无 Routing Stage 提前期依据
            //     ⇒ `CompletionTime` = `PlanningStart` 是**退化值**，**不是**已排/预计完工/供给可用时间。
            //   ⚠ 与「整条无真实 Operation Task、但**有** StageLeadTime 链」（`segments.Count>0`，
            //     0号位 §10.3 明文认可：自起点起的纯提前期累计）**结构上不同**，必须分开留痕。
            //   ── 2026-10-15（第七轮 §八.3 门控落地后）本判据**覆盖两支**，两者都是「时间无真实依据」：
            //     · 支A：该物料**纯无 Routing** 且无任何提前期段 ⇒ 游标从未推进；
            //     · 支B：该物料**有真实 Routing 但未排下** ⇒ 其无 Routing Stage 提前期本是**候选口径**，
            //       已被上方 `suppressCandidateLeadTimes` 抑制（`segments` 因此为空）⇒ 同样无权威依据。
            //   ⚠ 两支**共用同一条留痕**（不新造码）：对下游而言「该 Demand 的 `CompletionTime` 不可作权威」
            //     是同一件事；分列会让 2号位 读模型侧出现两个等价的告警键。文案已覆盖两支。
            // 判据③（时间无真实依据）同样**就地求值**。
            var completionTimeUnavailableForDemand = terminal is null && segments.Count == 0;
            if (completionTimeUnavailableForDemand)
            {
                completionTimeUnavailableDemandKeys?.Add(demand.LogicalDemandKey);
            }

            // ── G-3 `CompletionTimeCertain` 接线（0号位 2026-10-28《01_正式回复1号位_R04秒转分钟唯一装配
            //    _WAIT_ONLY_AUTO与R06本地先修_v1.0》§二：认当前生产侧从未置值；同意先落 `false` 的
            //    **缺 LT / 无真实 Task / 路径未定** 三种判据，「**不允许未证 WORK 就先填 `true`**」）──
            //   判据全部是**本方法已有的三个留痕集合**，不新增判据、不新增载体、不新增 ReasonCode：
            //     ① 缺 LT            ⇒ `leadTimeMissingForDemand`（上方 `MissingLevel` 分支）；
            //     ② 路径未唯一确定   ⇒ `pathUndeterminedForDemand`（仅当确有提前期段进入时间）；
            //     ③ 时间无真实依据   ⇒ `completionTimeUnavailableForDemand`（无末端 Task 且无段）；
            //     ④ 无可靠 WORK      ⇒ `workWindowUnavailableForDemand`（R09-① 2026-10-29 落码）。
            //   五判据均**就地求值**、与留痕同源，**不读收集器**（收集器是可选的订阅通道，
            //   没订阅不等于判据不成立）。
            //   **R09-②（2026-10-29 落码）三态转满**：五条判据全不命中 ⇒ 置 **`true`**。
            //   0号位 此前只设了一道闸：「**不允许未证 WORK 就先填 `true`**」——WORK 的证明已由 R09-①
            //   （按 `(FactoryId, DepartmentId)` 合法 WORK 窗口逐片累计）落地 ⇒ 该闸的前提已满足。
            //   `true` 的含义 = 「该 `CompletionTime` 全部由真实 `Task.End` + 已证 WORK 推进的
            //   无 Routing Stage 提前期构成，无缺参、无候选路径、无退化游标、无结构缺口」。
            var completionTimeUncertain = leadTimeMissingForDemand
                || pathUndeterminedForDemand
                || completionTimeUnavailableForDemand
                // R09-①（2026-10-29 落码）：无可信/不足够 WORK 窗口 ⇒ 同样**不得**声称确定完工。
                //   0号位 同件逐字「不生成确定Stage完成/下游AvailableTime」。
                || workWindowUnavailableForDemand
                // R09-②（2026-10-29 落码）：无 Routing Stage 在范围内但链上无位置 ⇒ 时间可能漏计/虚计。
                || routelessUnplaceable;

            result.Add(new DemandCompletionFact
            {
                LogicalDemandKey = demand.LogicalDemandKey,
                MaterialId = demand.MaterialId,
                AllocationSequence = demand.AllocationSequence,
                RequiredStageCode = demand.RequiredStageCode,
                CompletionTime = cursor,
                AvailableTime = cursor, // V1 与 CompletionTime 同值（已对齐）
                CompletionTimeCertain = completionTimeUncertain ? false : (bool?)true,
                RequiredStageCompletionTime = requiredCompletion,
                TerminalFinalDraftId = terminal?.FinalDraftId,
                StageTimings = segments
            });
        }

        return result;
    }

    /// <summary>
    /// R09-①（0号位 2026-10-23《04_APS_V1_R09_…联合技术核证包》§2/§3 逐字）：
    /// 「Stage LT唯一业务维护及计算单位是**有效生产小时**……部门/工厂/全局任何一级命中LT时，
    ///   无Routing Stage按唯一生产部门及所属工厂的合法WORK时间窗口**逐片累计，起始当天合法余时可计入，OFF不计**；
    ///   无Routing不借设备/人工槽日历凑时间。」
    /// 「无可信或不足够WORK时，不假设7×24/默认8h，不生成确定Stage完成/下游AvailableTime。」
    ///
    /// <para>**只吃 G-2 两个载体，绝不复用 `CalendarSlots`（资源槽）**——0号位同件逐字
    /// 「**禁止复用ResourceId资源槽做替代**」：</para>
    /// <list type="bullet">
    ///   <item>部门取源 = <see cref="DomainSolveRequest.MaterialStageDepartmentContexts"/>（**不是**
    ///     `StageSequenceChains[].ProductionDepartmentId`，见 `DomainSolveRequest.cs:223-228`）；
    ///     缺该行 ⇒ 部门未知 ⇒ 返回 <c>false</c>（不猜部门）。</item>
    ///   <item>窗口取源 = <see cref="DomainSolveRequest.DepartmentWorkWindows"/>，**按
    ///     `(FactoryId, DepartmentId)` 精确取用、无任何回落分支**；`DepartmentId == null` 的厂级行
    ///     **不是排程通道**，此处天然不会命中。</item>
    /// </list>
    ///
    /// <para>**返回 false ＝ 时间无法证明**：调用方必须「不产段、不推进游标」，不得伪造确定完工时间。
    /// 三种原因由 <paramref name="reason"/> 带回，仅供留痕，**不新增 ReasonCode**。</para>
    /// </summary>
    /// <param name="request">求解请求（读 `MaterialStageDepartmentContexts` / `DepartmentWorkWindows`）。</param>
    /// <param name="demand">当前需求（取其 `MaterialId` 与 `FactoryId`）。</param>
    /// <param name="stageCode">无 Routing 的大工艺阶段码。</param>
    /// <param name="cursor">推进起点（含「起始当天合法余时」）。</param>
    /// <param name="hours">该 Stage 的有效生产小时（`StageLeadTimeFact.LeadTimeHours`，装载侧已折算）。</param>
    /// <param name="end">推进后的时间；仅当返回 <c>true</c> 时有意义。</param>
    /// <param name="reason">失败原因（`MISSING_DEPARTMENT_CONTEXT` / `NO_WORK_WINDOW` / `INSUFFICIENT_WORK`）。</param>
    internal static bool TryAdvanceByWorkHours(
        DomainSolveRequest request,
        LogicalProductionDemand demand,
        string stageCode,
        DateTime cursor,
        decimal hours,
        out DateTime end,
        out string reason,
        bool isLeadTimeOnly = false)
    {
        end = cursor;
        reason = string.Empty;

        // 0 小时是**合法命中值**（实测 4 行完工类 `LeadTimeDays = 0.00`，见 `StageLeadTimeFact.LeadTimeHours` 注释）
        // ⇒ 不推进、但**时间确定**（不需要任何 WORK 窗口）。
        //
        // ── 2026-11-02 更正：**负值按契约夹到 0，不再拿负值做时钟算术** ──
        //   契约逐字（`LPS.APS.Core/Dto/DomainSolveRequest.cs:99`）：
        //     「折算值为 0（或负）**按命中产出**：`LeadTimeHours = 0`」
        //   ⇒ 消费侧义务是「负值当 0 用」，**不是**「把负值原样加到时钟上」。
        //   改前此处 `end = cursor.AddHours((double)hours)`：`hours = -5` 会让游标**倒退 5 小时**，
        //     排程出现「后段完工早于前段」的时钟倒退（我方 2026-10-31 已向 0号位 认账）。
        //   ⚠ 只夹**时间推进**；`StageLeadTimeFact.LeadTimeHours` 仍如实透传原值、`MatchLevel` 仍不改判
        //     ——「值原样透传、不把命中改写成 MISSING」这条口径**照旧成立**。
        if (hours <= 0m)
        {
            end = cursor;
            return true;
        }

        // ── 部门：只能来自 `MaterialStageDepartmentContexts`，缺则未知（不猜）──
        //   ⚠ 2026-11-04（2号位 r14030 ＋ Resource Calendar 专项 v1.4 `:17`/`:18`）：
        //     `IsLeadTimeOnly=true`（StageDict 的 FINAL/OUTS）在 `ProductionDepartment` 字典里
        //     **没有任何部门**；`:18` 明确「本层 MISSING ⇒ 允许继承**上级**已认证工作窗口」。
        //     ⇒ 这类 Stage **不查部门**，改按**工厂级**（`DepartmentId == null`）WORK 窗口推进。
        //     `IsLeadTimeOnly=false` 时本段**逐字不变**。
        int? departmentId = null;
        if (!isLeadTimeOnly)
        {
            foreach (var ctx in request.MaterialStageDepartmentContexts)
            {
                if (ctx.MaterialId == demand.MaterialId &&
                    string.Equals(ctx.StageCode, stageCode, StringComparison.Ordinal))
                {
                    departmentId = ctx.ProductionDepartmentId;
                    break;
                }
            }

            if (departmentId is null)
            {
                reason = "MISSING_DEPARTMENT_CONTEXT";
                return false;
            }
        }

        // ── 窗口：(FactoryId, DepartmentId) 精确匹配，无回落；厂级行（DepartmentId=null）不得参与 ──
        List<DepartmentWorkWindow>? windows = null;
        foreach (var w in request.DepartmentWorkWindows)
        {
            if (w.FactoryId != demand.FactoryId) continue;
            // IsLeadTimeOnly ⇒ 只取**厂级行**（DepartmentId == null）；其余仍按部门精确匹配
            if (isLeadTimeOnly ? w.DepartmentId is not null : w.DepartmentId != departmentId!.Value)
            {
                continue;
            }

            (windows ??= new List<DepartmentWorkWindow>()).Add(w);
        }

        if (windows is null || windows.Count == 0)
        {
            reason = "NO_WORK_WINDOW";
            return false;
        }

        windows.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        // ── 逐片累计：`segStart = max(窗口起, 游标)` ⇒ 「起始当天合法余时可计入」；窗口外的 OFF 自然不计 ──
        var remaining = hours;
        var t = cursor;
        foreach (var w in windows)
        {
            if (w.End <= t)
            {
                continue;
            }

            var segStart = w.Start > t ? w.Start : t;
            var available = (decimal)(w.End - segStart).TotalHours;
            if (available <= 0m)
            {
                continue;
            }

            if (available >= remaining)
            {
                end = segStart.AddHours((double)remaining);
                return true;
            }

            remaining -= available;
            t = w.End;
        }

        // 窗口用尽仍不够 ⇒ 「不足够WORK」⇒ 不生成确定时间（不回落 8h/7×24）
        reason = "INSUFFICIENT_WORK";
        return false;
    }

    /// <summary>
    /// **R09-③（2026-10-29）**：<see cref="TryAdvanceByWorkHours"/> 的**严格镜像反向版** ——
    /// 从 <paramref name="cursor"/> 往回退 <paramref name="hours"/> 个**有效生产小时**。
    ///
    /// <para><b>为什么需要</b>：Phase2 倒排（`ScheduleBackward`）是把 Due 往前推，
    /// 「中间无 Routing Stage 的提前期」在这里表现为「前一个 Routing Stage 必须**更早**结束」。
    /// 若只在前推方向扣提前期、倒排方向不管，同一份输入换个方向排出来的结果就不一致。</para>
    ///
    /// <para><b>与正向版逐条对称</b>（部门/窗口/无回落口径**全部一致**，不另立规则）：</para>
    /// <list type="bullet">
    ///   <item>部门只来自 `MaterialStageDepartmentContexts`，缺 ⇒ `MISSING_DEPARTMENT_CONTEXT`；</item>
    ///   <item>窗口只按 `(FactoryId, DepartmentId)` **精确**匹配，厂级行不参与，缺 ⇒ `NO_WORK_WINDOW`；</item>
    ///   <item>`hours ≤ 0` **不移动游标**（同正向：0/负值是**合法命中值**，但负值按
    ///     `DomainSolveRequest.cs:99` 契约夹到 0，不得让时钟倒退）；</item>
    ///   <item>窗口倒序遍历，`segEnd = min(窗口止, 游标)` ⇒「截止那一刻所在当天的合法余时」可计入，
    ///     OFF 段自然不计；不足 ⇒ `INSUFFICIENT_WORK`。</item>
    /// </list>
    ///
    /// <para><b>返回 false ＝ 时间无法证明</b>：调用方必须「不改锚点」（等价正向的「不产段、不推进游标」），
    /// 不得伪造确定时间；原因由 <paramref name="reason"/> 带回，**仅供留痕，不新增 ReasonCode**。</para>
    /// </summary>
    /// <param name="request">求解请求（读 `MaterialStageDepartmentContexts` / `DepartmentWorkWindows`）。</param>
    /// <param name="demand">当前需求（取其 `MaterialId` 与 `FactoryId`）。</param>
    /// <param name="stageCode">无 Routing 的大工艺阶段码。</param>
    /// <param name="cursor">回退起点（通常是「后一个工序的开工时间」）。</param>
    /// <param name="hours">该 Stage 的有效生产小时。</param>
    /// <param name="start">回退后的时间；仅当返回 <c>true</c> 时有意义。</param>
    /// <param name="reason">失败原因（与正向版同名，不新增码）。</param>
    internal static bool TryRetreatByWorkHours(
        DomainSolveRequest request,
        LogicalProductionDemand demand,
        string stageCode,
        DateTime cursor,
        decimal hours,
        out DateTime start,
        out string reason,
        bool isLeadTimeOnly = false)
    {
        start = cursor;
        reason = string.Empty;

        // 与正向版同口径：0/负值是合法命中值 ⇒ 不查窗口；但**负值按契约夹到 0，不移动游标**
        //   （`DomainSolveRequest.cs:99`「折算值为 0（或负）**按命中产出**：`LeadTimeHours = 0`」）。
        //   改前此处 `start = cursor.AddHours(-(double)hours)`：`hours = -5` 会让起点**后移 5 小时**。
        if (hours <= 0m)
        {
            start = cursor;
            return true;
        }

        // ── 部门：只能来自 `MaterialStageDepartmentContexts`，缺则未知（不猜）──
        //   2026-11-04：与 `TryAdvanceByWorkHours` **严格镜像**（2号位 r14030 §2.2 要求对称）——
        //     `IsLeadTimeOnly=true`（FINAL/OUTS）不查部门，改按**工厂级** WORK 窗口回退。
        int? departmentId = null;
        if (!isLeadTimeOnly)
        {
            foreach (var ctx in request.MaterialStageDepartmentContexts)
            {
                if (ctx.MaterialId == demand.MaterialId &&
                    string.Equals(ctx.StageCode, stageCode, StringComparison.Ordinal))
                {
                    departmentId = ctx.ProductionDepartmentId;
                    break;
                }
            }

            if (departmentId is null)
            {
                reason = "MISSING_DEPARTMENT_CONTEXT";
                return false;
            }
        }

        // ── 窗口：(FactoryId, DepartmentId) 精确匹配，无回落；厂级行（DepartmentId=null）不得参与 ──
        List<DepartmentWorkWindow>? windows = null;
        foreach (var w in request.DepartmentWorkWindows)
        {
            if (w.FactoryId != demand.FactoryId) continue;
            if (isLeadTimeOnly ? w.DepartmentId is not null : w.DepartmentId != departmentId!.Value)
            {
                continue;
            }

            (windows ??= new List<DepartmentWorkWindow>()).Add(w);
        }

        if (windows is null || windows.Count == 0)
        {
            reason = "NO_WORK_WINDOW";
            return false;
        }

        windows.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        // ── 反向逐片累计：`segEnd = min(窗口止, 游标)` ⇒ 「截止那天已过的合法工时」可计入 ──
        var remaining = hours;
        var t = cursor;
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];

            // 窗口整体在游标之后（含恰好等于）⇒ 一个工时也用不上。
            if (w.Start >= t)
            {
                continue;
            }

            var segEnd = w.End < t ? w.End : t;
            var available = (decimal)(segEnd - w.Start).TotalHours;
            if (available <= 0m)
            {
                continue;
            }

            if (available >= remaining)
            {
                start = segEnd.AddHours(-(double)remaining);
                return true;
            }

            remaining -= available;
            t = w.Start;
        }

        // 窗口用尽仍不够 ⇒ 「不足够WORK」⇒ 不生成确定时间（不回落 8h/7×24）
        reason = "INSUFFICIENT_WORK";
        return false;
    }

    /// <summary>
    /// R09-T11（0号位 2026-10-23《04》§3）：取「供给阈值 Stage 自己的真实 Task 末」。
    /// 用于 EDGE 与 ROOT **严格不同 Stage** 的那一支 —— 此时末端 `Task.End` 是 ROOT 时间，
    /// 拿来当 EDGE 时间就是「错误压到末端完工时点」。
    /// 取不到（无该 Stage 的 Task）⇒ `null`，**不回落到末端完成时间**（不释放未证供给）。
    /// </summary>
    private static DateTime? ResolveStageTaskEnd(
        IReadOnlyDictionary<string, List<FinalTaskDraft>>? tasksByDemandKey,
        string demandKey,
        string? stageCode)
    {
        if (tasksByDemandKey is null || string.IsNullOrEmpty(stageCode))
        {
            return null;
        }

        if (!tasksByDemandKey.TryGetValue(demandKey, out var tasks) || tasks is null)
        {
            return null;
        }

        DateTime? end = null;
        foreach (var task in tasks)
        {
            if (!string.Equals(task.StageCode, stageCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (end is null || task.PlannedEndTime > end.Value)
            {
                end = task.PlannedEndTime;
            }
        }

        return end;
    }

    /// <summary>
    /// 取该 Demand 的**末端真实生产 Task**（0号位 §10.2「唯一末端真实 Task」）。
    /// 规则与 Phase5 <c>GenerateAllocationShares</c> 的末端判定**必须一致** —— 改其一请同步另一处。
    /// </summary>
    private static FinalTaskDraft? PickTerminalTask(
        List<FinalTaskDraft>? tasks,
        ConstraintContext constraints,
        int materialId)
    {
        if (tasks == null || tasks.Count == 0)
        {
            return null;
        }

        if (tasks.Count == 1)
        {
            return tasks[0];
        }

        // 标记所有有 downstream 的 Task（非末端）。节点身份 = (StageCode, OperationCode)（0号位 §5.3）。
        // Path-aware（0号位 2026-10-07 裁决 Q-3）：本列表全属同一需求 ⇒ 路径身份取自首个 Task，不按物料猜。
        var downstream = new HashSet<string>(StringComparer.Ordinal);
        var sample = tasks[0];
        if (constraints.TryGetRoutingGraph(sample.MaterialId, sample.RouteCode, sample.PathId, out var graph))
        {
            foreach (var depList in graph.Dependencies.Values)
            {
                foreach (var dep in depList)
                {
                    var upstream = tasks.FirstOrDefault(t =>
                        t.OperationCode == dep.From.OperationCode &&
                        string.Equals(t.StageCode, dep.From.StageCode, StringComparison.Ordinal));

                    if (upstream != null)
                    {
                        downstream.Add(upstream.FinalDraftId);
                    }
                }
            }
        }

        var terminals = tasks.Where(t => !downstream.Contains(t.FinalDraftId)).ToList();
        if (terminals.Count == 0)
        {
            // 兜底：全部被标为非末端（异常输入）⇒ 退回最晚结束者，**不抛、不静默丢**
            terminals = tasks;
        }

        return terminals
            .OrderByDescending(t => t.PlannedEndTime)
            .ThenBy(t => t.FinalDraftId, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// 本**求解范围**内「有效 Stage 但零 RoutingOperation」的 Stage 集合。
    /// 用求解范围（Phase1 可达 Stage）而非全量 BOM StagePath ——
    /// 否则会把 StartOperation/StartStage 之前已完成、本次不求解的 Stage 也算进来，凭空多出提前期。
    /// </summary>
    /// <remarks>
    /// **路径确定性三档**（0号位 2026-10-14《再次整改回执正式审核回复》§二 S2-01）：
    ///   · **已确定**（`PathDetermined = true`）—— 选中 `RouteCode` 在该物料载荷下**唯一**对应一条 `PathId`
    ///     （显式 `PathId` 命中，或该 `RouteCode` 只有一条 Path）⇒ 按 `(RouteCode, PathId)` **严格过滤**
    ///     ⇒ 该结果**可作权威完成时间**依据。
    ///   · **仅确定到 Route**（`PathDetermined = false`，`RouteCode` 非空但该 Route 有多条 Path）
    ///     ⇒ 按 `RouteCode` 过滤（该 Route 全部 Path 的并集）⇒ **候选口径**。
    ///   · **未确定**（`RouteCode` 空 / 载荷无该 Route 的工序 / 声称的 `PathId` 不在载荷内）
    ///     ⇒ 取该物料**全部** RouteCode 的工序并集 ⇒ **候选口径**。
    ///   ⇒ 后两档**不得**被下游当作**权威承诺时间**（见 <see cref="ResolveRoutelessStages"/> 的
    ///     `PathDetermined` 与 <see cref="Build"/> 的 `TRACE:STAGE_TIMING_PATH_UNDETERMINED` 留痕）。
    ///   归一化 ON（V1 现状：全部 op 均为 `DEFAULT/1`）时，三档**逐元素相同** ⇒ **严格 no-op / 零回归**。
    /// </remarks>
    // 可见性：`internal`（而非 private）—— 供 `LPS.APS.Tests` 经 `InternalsVisibleTo` **直接覆盖**。
    // 生产调用点只有 `BuildCore`。保留 5 参签名（既有已提交单测依赖）。
    internal static List<string> GetRoutelessStages(
        int materialId,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        DomainSolveRequest request,
        string? selectedRouteCode = null,
        long? selectedPathId = null)
        => ResolveRoutelessStages(materialId, effectiveStages, request, selectedRouteCode, selectedPathId)
            .Stages;

    /// <summary>
    /// 「无 Routing Stage」判定结果 + **该判定是否建立在唯一确定的 Path 上**。
    /// `PathDetermined = false` ⇒ <see cref="Stages"/> 只是**候选/诊断**口径，
    /// **不得**被下游当作权威承诺时间（0号位 2026-10-14《…正式审核回复》§二 S2-01）。
    ///
    /// <see cref="HasRoutingInScope"/>（2026-10-15 新增，第七轮 §八.3）：
    ///   本求解范围内**是否存在任何 Routing 工序** —— 它是「**真正纯无 Routing**」与
    ///   「**有真实 Routing 但未排下/路径未定**」的分界，也是 <see cref="BuildCore"/> 门控的判据。
    ///   ⚠ 为什么不复用 `PathDetermined`：`PathDetermined` 取 `terminal?.RouteCode` ⇒ **未排下的需求恒为
    ///     false**（无 terminal 就无 RouteCode），于是「物料根本没有 Routing」与「有 Routing 但没排下」
    ///     被**混成同一档**，无法区分 0号位 第七轮 §八.3 点名的 (ii)/(iii) 两态。
    /// </summary>
    internal readonly record struct RoutelessStageResolution(
        List<string> Stages, bool PathDetermined, bool HasRoutingInScope = false);

    /// <summary>
    /// 见 <see cref="GetRoutelessStages(int, IReadOnlyDictionary{int, HashSet{string}}, DomainSolveRequest, string?, long?)"/>
    /// 的 remarks（路径确定性三档）。
    /// </summary>
    internal static RoutelessStageResolution ResolveRoutelessStages(
        int materialId,
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        DomainSolveRequest request,
        string? selectedRouteCode,
        long? selectedPathId)
    {
        if (!effectiveStages.TryGetValue(materialId, out var inScopeStages) || inScopeStages.Count == 0)
        {
            return new RoutelessStageResolution(new List<string>(), true, false);
        }

        var hasRoute = !string.IsNullOrWhiteSpace(selectedRouteCode);
        long? pathId = null;
        var pathDetermined = hasRoute && TryResolveDeterminedPath(
            materialId, request, selectedRouteCode!, selectedPathId, out pathId);

        var stagesWithRouting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in request.RoutingOperations)
        {
            if (op.MaterialId != materialId || string.IsNullOrEmpty(op.StageCode))
            {
                continue;
            }

            if (hasRoute && !string.Equals(op.RouteCode, selectedRouteCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (pathDetermined && pathId.HasValue && op.PathId != pathId.Value)
            {
                continue;
            }

            stagesWithRouting.Add(op.StageCode!);
        }

        // 本范围内**存在** Routing 工序 ⇒ 「有真实 Routing」（与「纯无 Routing」相对）。
        // 注意：此处按 in-scope 判定，与 `routelessStages` 同源 —— 范围外的 Stage 不计入，
        //   否则会把本次不求解的 Stage 也算成「有 Routing」（与 `GetRoutelessStages` 的口径一致）。
        var hasRoutingInScope = inScopeStages.Any(stage => stagesWithRouting.Contains(stage));

        return new RoutelessStageResolution(
            inScopeStages.Where(stage => !stagesWithRouting.Contains(stage)).ToList(),
            pathDetermined,
            hasRoutingInScope);
    }

    /// <summary>
    /// 判定「该需求的可执行路径是否**唯一确定**」。
    ///   已确定 ⇒ `true`，并给出 `(routeCode, pathId?)`（`pathId` 恒有值 —— 唯一 Path 时自动补全）。
    ///   未确定 ⇒ `false`（`RouteCode` 空 / 载荷无该 Route 的工序 / 声称的 `PathId` 不在载荷内 / 该 Route 多 Path）。
    /// </summary>
    internal static bool TryResolveDeterminedPath(
        int materialId,
        DomainSolveRequest request,
        string selectedRouteCode,
        long? selectedPathId,
        out long? pathId)
    {
        pathId = null;
        if (string.IsNullOrWhiteSpace(selectedRouteCode))
        {
            return false;
        }

        var pathIds = new HashSet<long>();
        foreach (var op in request.RoutingOperations)
        {
            if (op.MaterialId != materialId)
            {
                continue;
            }
            if (!string.Equals(op.RouteCode, selectedRouteCode, StringComparison.Ordinal))
            {
                continue;
            }
            pathIds.Add(op.PathId);
        }

        if (pathIds.Count == 0)
        {
            return false;   // 载荷里没有该 RouteCode 的工序 ⇒ 无从判定
        }

        if (selectedPathId.HasValue)
        {
            if (!pathIds.Contains(selectedPathId.Value))
            {
                return false;   // 声称的 PathId 不在载荷内 ⇒ 契约不一致 ⇒ 不可判定（不按"近似路径"放行）
            }
            pathId = selectedPathId.Value;
            return true;
        }

        if (pathIds.Count == 1)
        {
            pathId = pathIds.First();
            return true;
        }

        return false;   // 该 RouteCode 下多条 Path ⇒ 单凭 RouteCode 不可判定
    }

    /// <summary>
    /// ⑤ 提前期解析：只认契约 4 值；**命中级的值原样透传，不做符号判断**。
    /// 契约违例值（含旧名 `MATERIAL` / `FAMILY`）**不向外透传**，避免下游把它当合法层级审计。
    ///
    /// ⚠️ 2026-09-29 口径更正：**值 ≤ 0 不再视同未命中** —— 命中就是命中，0 是参数行写下的值。
    /// （早先那版带 `fact.LeadTimeHours > 0m` 的守卫即被作废的口径，已删。）
    ///
    /// ⚠️ 2026-10-15：命中 `MissingLevel` 时**返回值不参与时间累计** —— 调用方（`BuildCore`）
    /// 见到该 Level 直接走 Issue 分支、不产出时间段（见 `MissingLevel` 注释）。
    /// 故此处回落值取 `0m` 只是占位，**不是**「按 0 小时排」。
    /// </summary>
    internal static (decimal Hours, string Level) ResolveLeadTime(
        Dictionary<(int MaterialId, string StageCode), StageLeadTimeFact> index,
        int materialId,
        string stageCode)
    {
        if (index.TryGetValue((materialId, stageCode), out var fact))
        {
            var level = fact.MatchLevel;
            var isLegalLevel = !string.IsNullOrEmpty(level) &&
                               (HitLevels.Contains(level) ||
                                string.Equals(level, MissingLevel, StringComparison.Ordinal));

            if (isLegalLevel)
            {
                return (fact.LeadTimeHours, level);
            }
        }

        return (0m, MissingLevel);
    }
}
