namespace LPS.APS.Core.Dto;

/// <summary>
/// 物料×阶段→默认生产部门 上下文（PM 裁定：最小 B）。
/// 2号位裁剪当前 Domain 涉及的 (MaterialId, StageCode) 传入；1号位按 (MaterialId, StageCode)
/// 锁 ProductionDepartmentId 后过滤 Routing 三件套，不得重新推导部门。
///
/// 本 DTO 同时承担「该物料本次需经过的 Stage 列表」的载体作用（1号位 由此知悉 Stage 集合）。
/// </summary>
public sealed class MaterialStageDepartmentContextDto
{
    public int MaterialId { get; init; }

    /// <summary>必须存在于 StageDict 的合法大工艺阶段码</summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>该物料在该阶段下的默认生产部门 Id</summary>
    public int ProductionDepartmentId { get; init; }
}

/// <summary>
/// 某物料在**本次 BOM 上下文**中的**完整有序 Stage 链**（PM《BOM取用_Pegging_Stage_Routing完整链路说明》§七，2026-09-28：
/// 「Stage 顺序只能以 `StageDetail.StageSeq` / `APS_BOM_STAGE_PATH_RAW.StageSeq` 为权威，
///  禁止从 RoutingOperation 按 Stage 名/SortHint/字母序猜工艺先后」）。
///
/// 【为什么需要】1号位 消费「供给阈值 Stage（`LogicalProductionDemand.RequiredStageCode`）」时，
///   必须判断「做到该阶段为止」= 保留该阶段**及之前**的全部 Stage、丢弃之后的 —— 这需要 Stage 先后顺序。
///   同样，「无 Routing 阶段保留前后依赖」（`StageLeadTimes`）也需要顺序。
///   本链即为此提供**原始有序事实**，不预判消费侧的建图形态（StageDependency / 有序列表均可）。
///
/// 【形态（1号位 2026-09-28 回执 §二 撤回上报的硬前提）】
///   ① **每个物料一条完整有序链**（不是「一个集合 + 另一张映射表」）；
///   ② **每步带 `StageSeq` 数值**（不是只有数组下标隐含先后）。
///   `Stages` 已按 `StageSeq` 升序排列，`StageSeq` 亦随步携带，两条前提同时满足。
///
/// 【粒度 —— 2026-10-16 J12-02 订正】**每物料恰好一条真实链**，按 `StageScopeType` 隔离后收敛：
///   作为子件被父件消耗的物料取 **EDGE** 链、须自身完工的取 **ROOT** 链（`07 v5.1.13 :724` 两式）；
///   同 scope 内多条不一致 ⇒ **取最长**（`:722`／`:415`）并告警。
///   ⚠ 原口径「ROOT ∪ EDGE 取并集 + 同 StageCode 取 `MIN(StageSeq)`」**已废除** —— 它是**静默并集**，
///   违反 `07 :416`／`:723`「必须按 `StageScopeType` 区分查询（不得混查EDGE+ROOT）」。
///   收敛在载体侧（2号位 `EffectiveStageChainResolver`）完成，故本 DTO **不承载** scope 字段。
/// 【谁产出/谁消费】2号位 从本次 BOM 上下文装载 → 1号位 消费。
/// </summary>
public sealed class StageSequenceChain
{
    public int MaterialId { get; init; }

    /// <summary>已按 `StageSeq` 升序排列的完整 Stage 链</summary>
    public IReadOnlyList<StageSequenceStep> Stages { get; init; } = Array.Empty<StageSequenceStep>();
}

/// <summary>Stage 链上的一步：阶段码 + 顺序号 + 该阶段的生产部门</summary>
public sealed class StageSequenceStep
{
    /// <summary>大工艺阶段码（如 CN_MACH / CN_SURF）</summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>顺序号（越小越前）。权威来源 = `APS_BOM_STAGE_PATH_RAW.StageSeq`。</summary>
    public int StageSeq { get; init; }

    /// <summary>
    /// 该阶段对应的生产部门 Id。
    /// 【来源】`MaterialStageDeptContext`（PM《Stage、生产部门、Routing、Dependency、StageLeadTimeParam 接口裁决回复》2026-09-28 §三/§四/§六：
    ///   「StageCode 不能直接决定生产部门，必须通过 MaterialStageDeptContext 确定」「Stage 不是部门，Stage 必须经过 Master 裁决」）。
    /// 【为什么必须随链带出】PM §三 明确 2号位 提供的业务事实是
    ///   `MaterialId + StageCode + StageSeq + ProductionDepartmentId` 四条一组（即 PM 命名的 `EffectiveStagePath`），
    ///   不是只有码与序号。
    /// 【null 语义】无 MSC 映射 ⇒ 本阶段部门未知（不得凭空推导）。1号位 按既有最小B 口径记
    ///   `MISSING_PRODUCTION_DEPARTMENT_CONTEXT` 并置 Unscheduled，不得由 1号位 自行猜部门。
    /// </summary>
    public int? ProductionDepartmentId { get; init; }

    // ────────────────────────────────────────────────────────────────────────
    // 2026-11-25 新增：**Stage 实际执行厂**（0号位 2026-11-24 裁决 v1.2 §四；
    //   2026-11-25 总表 §一「每段Stage精确通过 StageDict.StageCode→FactoryCode→FactoryId
    //   验证 ProductionDepartment.FactoryId，传实际Stage执行厂、部门、匹配状态」）。
    //
    // 【为什么必须查字典，不能按 StageCode 前缀推】
    //   实测（2026-11-25）：`StageDict` 里 `CN6_ASSY` / `CN6_MACH` / `CN6_OUTS` 的厂是 **`CN6课`**，
    //   而 `Factory` 主档**没有 `CN6` 这个 Code**（只有 CN=2 / BJ=3 / CN6课=4 / SH=5 / TJ=6）。
    //   按前缀推会得到 `CN6` ⇒ 查不到厂。规模：`CN6_MACH` 在 BOM 12,349 行 / MSDC 182,268 行。
    //   ⚠ 别和 `StageLeadTimeParam` 搞混——那张表的厂码口径**是**阶段码前缀（`CN6`），
    //     两张表口径相反且各自都对，统一成一个则另一边立刻错。
    //
    // 【来源】`StageFactoryCode` = `ext_MES_StageDict.FactoryCode`（ODS 同义词，只读）；
    //         `StageFactoryId`   = `Factory.Code` 等值匹配得到的 `Id`；
    //         `DepartmentFactoryId` = `ProductionDepartment.FactoryId`，**仅用于校验**，不参与取厂。
    //
    // 【null 语义】查不到就是查不到，**不猜、不回落 `Demand.FactoryId`／`SH.SourceFactoryId`**。
    //   具体成因见 <see cref="FactoryMatchStatus"/>，消费侧据此走局部异常分支。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>`StageDict.FactoryCode`（如 `CN6课`）。查不到阶段码时为 null。</summary>
    public string? StageFactoryCode { get; init; }

    /// <summary>`Factory.Code → Factory.Id` 解析结果。解析不出为 null。</summary>
    public int? StageFactoryId { get; init; }

    /// <summary>`ProductionDepartment.FactoryId`——用于与 <see cref="StageFactoryId"/> 校验，不参与取厂。</summary>
    public int? DepartmentFactoryId { get; init; }

    /// <summary>工厂匹配状态，取值见 <see cref="StageFactoryMatchStatus"/>。默认空串＝未投影。</summary>
    public string FactoryMatchStatus { get; init; } = string.Empty;
}

/// <summary>
/// <see cref="StageSequenceStep.FactoryMatchStatus"/> 的取值。
///
/// 【实测分布 2026-11-25】（`MaterialStageDeptContext` 1,066,449 行）
///   · 可解析到 FactoryId 的：1,066,448 行；
///   · **部门厂 ≠ Stage 厂：0 行** ⇒ 今日事实上全部是 <see cref="Match"/>；
///   · 唯一 1 行不可解析的是 `SYN-STAGE-1`（不在 StageDict、BOM 里也没有，死数据）。
///
/// 【为什么还要保留其余状态】今天的「0」是数据现状，不是契约保证：一旦 3号位/5号位 改主数据，
///   静默取一个厂会让排程结果无声偏移。用状态位把「没有合法映射」显性化，是裁决的原话要求。
/// </summary>
public static class StageFactoryMatchStatus
{
    /// <summary>
    /// 未投影／无判定。两种情形共用：
    ///   ① 老载体（本字段未填）；
    ///   ② **链级定义**（<see cref="StageChainDefinition.Stages"/>）——链可被多个物料共用，
    ///      部门是 (物料,阶段) 级事实、不属于链，故**只给厂、不判定部门**。
    /// 消费侧应按「无部门判定」处理，**不得**当 MATCH，也**不得**当「该阶段无部门」。
    /// </summary>
    public const string NotProjected = "";

    /// <summary>部门厂 = Stage 厂。今日 100% 属此类。</summary>
    public const string Match = "MATCH";

    /// <summary>部门厂 ≠ Stage 厂。**不静默二选一**，保留异常交下游判定。</summary>
    public const string Mismatch = "MISMATCH";

    /// <summary>`StageDict` 有该阶段码，但 `FactoryCode` 在 `Factory` 主档查不到（如误填 `CN6`）。</summary>
    public const string StageFactoryUnresolved = "STAGE_FACTORY_UNRESOLVED";

    /// <summary>阶段码不在 `StageDict`（今日 BOM 侧 0 例）。</summary>
    public const string StageNotInDict = "STAGE_NOT_IN_DICT";

    /// <summary>该步无部门（今日 301,107 行 BOM，全部是 FINAL/OUTS，属**常态**非缺陷）。</summary>
    public const string DepartmentUnresolved = "DEPARTMENT_UNRESOLVED";
}

/// <summary>
/// 无 Routing 阶段的提前期（PM《无Routing Stage统一处理建议》§九/§十，2026-09-28）。
///
/// 【谁产出/谁消费】2号位 装载 → 1号位 消费。
/// 【为什么需要】StagePath 决定阶段是否存在及顺序；Routing 只回答「该 Stage 内有无小工序」。
///   `Routing 不存在 ≠ Stage 不存在` —— 如 BJ_FINAL（完工/出口）、TJ_OUTS（外协）天然无工序，
///   1号位 需为该阶段保留时间与前后依赖，其时长即取自本事实。
/// 【2号位 解析口径】**三级**，从细到粗降级（2026-09-29 用户明确口径，**逐字**）：
///   ① `DEPT_EXACT`            = ProductionDeptCode + FactoryCode + StageCode
///   ② `FACTORY_STAGE_DEFAULT` = FactoryCode + StageCode
///   ③ `GLOBAL_STAGE_DEFAULT`  = **StageCode**（**不带 `IsDefault` 条件** —— 只要该 Stage 有参数行即命中）
///   （旧口径的 MaterialCode 级 / ProductFamilyCode 级 §4.1 定「在V1不作为正常有效匹配级别」，**已移除**。）
///
/// 【走完三级仍未命中】PM 2026-09-29 裁决：「按照三级查找，如果三级均未命中，则记入 STAGE_LEADTIME_MISSING，
///   **并按 3 天兜底**」。⇒ 2号位 **产出一条 `LeadTimeHours = 72` 的 Fact**（不是省略、更不是 0 小时），
///   `MatchLevel` = **`STAGE_LEADTIME_MISSING`**（PM 原码；1号位 回执 §四 把它定为 V1 合法 4 值之一），
///   同时落日志计数。⇒ V1 `MatchLevel` 合法值域 = 下面 `MatchLevel` 属性注释里的 4 个，**多一个都算契约违例**。
///
/// 【兜底只看「算不算命中」，不按成因分治】——**只有一种情形算未命中：三级全不中**（甲/乙/丙 都走同一支）。
///   ⚠️ **2026-09-29 用户更正**：本类此前写有第二种「未命中」情形
///   「**命中到参数行，但折算值 ≤ 0**（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00`）」
///   —— **该口径已作废**。**命中就是命中**：折算值为 0（或负）**按命中产出**
///   （`LeadTimeHours = 0`、`MatchLevel` = 实际命中级，实测那 4 行为 `FACTORY_STAGE_DEFAULT`）。
///   0 是参数行写下的值，装载层不得替参数侧改判；仅单记 `zeroHitCount` 供参数 Owner 查看。
/// </summary>
public sealed class StageLeadTimeFact
{
    public int MaterialId { get; init; }

    /// <summary>大工艺阶段码（如 BJ_FINAL / TJ_OUTS）</summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>
    /// 提前期（**小时；装载侧已折算完成**）。
    ///
    /// 【填值口径（在 2号位 装载侧，不在消费侧）】源行 `LeadTimeHours &gt; 0` ⇒ 直接取小时值；否则（含 NULL）
    ///   由装载侧按 `LeadTimeDays × 24` 折算后填进本字段（`PeggingOrchestrator.LoadStageLeadTimesAsync`）。
    ///   消费侧**只读本字段**，**不得再乘任何系数** —— 再乘一次就是把折算做两遍。
    ///
    /// 【`0` 是合法命中值，不是「缺值」】实测 4 行 `*_FINAL`（BJ/CN/SH/TJ 完工）`LeadTimeDays = 0.00` ⇒
    ///   命中就是命中，按实际命中级产出 `LeadTimeHours = 0`。**「没命中」由 `MatchLevel = STAGE_LEADTIME_MISSING`
    ///   表达，与数值无关**；见到 `0` 不要回头去 `LeadTimeDays` 再折算（那会走出一个本不存在的参考日）。
    ///   （1号位 2026-10-25 R04 会签回执 §一 提请：本注释原「否则」半句与类头 2026-09-29 更正相互打架。）
    /// </summary>
    public decimal LeadTimeHours { get; init; }

    /// <summary>
    /// 命中层级（审计用）。**V1 合法值域 = 恰好 4 个**，多一个都算契约违例：
    /// `DEPT_EXACT` / `FACTORY_STAGE_DEFAULT` / `GLOBAL_STAGE_DEFAULT` / `STAGE_LEADTIME_MISSING`。
    /// 便于回溯「该提前期由哪一级参数命中」，避免同值不同源无法区分。
    ///
    /// ⚠️ 值域是**与 1号位 对齐过的契约**（1号位 回执《无RoutingStage时间出口字段_字段载体落码提请_v1.0》
    ///    §四 明文：「`MatchLevel` 为上述 4 个之外的任何值（含旧名 `MATERIAL` / `FAMILY`）⇒ **契约违例**，
    ///    不用该值，按第 3 条兜底」）。⇒ 装载层**不得**再自起名字，改值域须先与 1号位 对齐。
    ///
    /// ⚠️ 0号位 2026-09-29 §4.3 收敛了前三个值；旧的 `MATERIAL_EXACT` / `PRODUCT_FAMILY_EXACT`
    ///    （本字段历史值 `MATERIAL` / `FAMILY`）**V1 不作为正常命中**，装载层已不再产出——
    ///    遇旧数据由装载层登记兼容告警，不让 1号位 自行解释。
    ///
    /// ⚠️ 第四值 `STAGE_LEADTIME_MISSING`（PM 2026-09-29 裁决「三级均未命中 ⇒ 记入 … 并按 3 天兜底」）。
    ///    **注意它带值**（`LeadTimeHours = 72`）——名字里的 MISSING 指「**参数未命中**」，不是「本条无提前期」。
    ///    它不复用 `GLOBAL_STAGE_DEFAULT`：复用会让审计把兜底误认成「真有全局默认参数」。
    ///    **覆盖面（2026-09-29 用户更正后）**：本值**只覆盖「三级全不中」这一种情形**。
    ///    此前写的第二种情形「命中但折算值 ≤ 0」（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00`）
    ///    **已不归本值** —— 命中就是命中，那 4 行按命中级（`FACTORY_STAGE_DEFAULT`）+ 值 0 产出。
    /// </summary>
    public string MatchLevel { get; init; } = string.Empty;

    /// <summary>
    /// 本阶段是否**只需时间、不需生产部门**（`StageDict.StageCategory ∈ {FINAL, OUTS}`）。
    ///
    /// 【0号位 2026-11-04《定向试跑方案 v1.1》§9.2 逐字】
    ///   「`FINAL` 是出厂/最终入库的虚拟时间阶段，**不需要生产部门**、不下发 MES Operation Task，但有独立 LT。
    ///     `OUTS` 外协也是时间占用型 Stage：应计算其 LT、参与先后依赖，但 **V1 不因此构造内部生产 Task**。」
    ///
    /// 【为什么需要这个字段】`ProductionDepartment` 字典 24 个阶段码里**没有 FINAL/OUTS**，
    ///   所以这两类阶段永远拿不到部门。消费侧 `StageTimingNodeBuilder` 的流程是：
    ///     `:596` `if (hours &lt;= 0m) { end = cursor; return true; }`  ← LT=0 在此短路，不查部门
    ///     `:609` 查 `MaterialStageDepartmentContext`
    ///     `:618` 查不到 ⇒ `MISSING_DEPARTMENT_CONTEXT`，返回 false ⇒ **该段不产时间节点、游标不推进**
    ///   ⇒ **FINAL（四厂 LT=0.00h）恰好走短路分支，碰巧无影响；OUTS（五厂 LT=40.00h）必然失败。**
    ///   本字段就是让消费侧在 OUTS 上也能区分「本来就不需要部门」与「数据缺部门」，
    ///   前者跳过部门查找、后者仍报错 —— **不能用「有 LT 但无部门」去猜，那会把真缺的也放过去**。
    ///
    /// 【值域与来源】布尔，由**装载侧**按 `ext_MES_StageDict.StageCategory` 填：
    ///   `FINAL` / `OUTS` ⇒ true，其余（ASSY/MACH/SURF/CAST/FORGE/EXTRU/MOLD/DRAW）⇒ false。
    ///   同义词 `ext_MES_StageDict` 已存在（`sp_SyncInventorySnapshot` 的 XC 资格守卫在用 `StageCategory='SURF'`），
    ///   APS 侧不落字典表、每次装载现算。
    ///
    /// 【配套】消费侧改动归 1号位（`LPS.APS.Scheduling`）：
    ///   在 `:609` 之前加「`IsLeadTimeOnly` ⇒ 直接 `end = cursor` 推进并 return true，不查部门」。
    ///   本字段是 `LPS.APS.Core` 契约扩展，**默认 false 保持既有行为**，加字段本身不改变任何现有结果。
    /// </summary>
    public bool IsLeadTimeOnly { get; init; }
}

/// <summary>
/// **去重后的合法 Stage 链目录**（0号位 2026-11-24 裁决 v1.2 §3；2026-11-25 总表 §一 P0 第一项）。
///
/// 【它解决什么】旧载体 <see cref="StageSequenceChain"/> 以 `MaterialId` 为键，一个物料只能装一条链，
///   同物料多条合法链只能整料隔离（289 条 `STAGE_CHAIN_ROLE_CONFLICT` 的直接成因）。
///   本类按**链**去重，一条链一个 <see cref="ChainRefId"/>，与物料解耦。
///
/// 【与旧载体并存】`DomainSolveRequest.StageSequenceChains` 保留，但降级为
///   「某物料在整个 Run 内**只有唯一等价链**时才出」的兼容口径。
/// </summary>
public sealed class StageChainDefinition
{
    /// <summary>
    /// 链的稳定标识 = `<c>{StageScopeType}:{ChainSignature}</c>`（如 `ROOT:10:CN_MACH|20:CN_SURF`）。
    ///
    /// 【为什么是确定性字符串而不是自增】同一链在任何 Run 里算出同一个值，便于跨 Run 对拍；
    ///   自增 Id 跨 Run 无意义，对拍还得再引一个签名。
    /// </summary>
    public string ChainRefId { get; init; } = string.Empty;

    /// <summary>`ROOT` / `EDGE`（`EffectiveStageChainResolver.ScopeRoot` / `ScopeEdge`）。</summary>
    public string StageScopeType { get; init; } = string.Empty;

    /// <summary>
    /// 链签名 = `StageSeq:StageCode` 按升序 `|` 连接（`EffectiveStageChainResolver.BuildChainKey`）。
    /// 与 `ChainRefId` 的后半段恒等，单列出来是为了让消费侧不必解析 `ChainRefId`。
    /// </summary>
    public string ChainSignature { get; init; } = string.Empty;

    /// <summary>已按 `StageSeq` 升序排列的完整 Stage 链（含工厂/部门富化）。</summary>
    public IReadOnlyList<StageSequenceStep> Stages { get; init; } = Array.Empty<StageSequenceStep>();
}

/// <summary>
/// **逐需求的链绑定**（0号位 2026-11-24 裁决 v1.2 §3「一 Demand 一行」「不同父边来源必须有真实 BindingKey」）。
///
/// 【键】`LogicalDemandKey` ＋ `BindingKey`。同一需求有多条父边时用不同 `BindingKey` 区分，
///   不得把多父边塞进 `StageSegmentsJson` 冒充身份。
/// </summary>
public sealed class DemandStageChainBinding
{
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>同一需求内区分不同父边/供给关联的键。当前一个需求一条，形如 `<c>{scope}|{BOMNO}|{RepWorksetId}</c>`。</summary>
    public string BindingKey { get; init; } = string.Empty;

    /// <summary>父需求键（EDGE 侧有意义）。当前载体未提供逐父边需求键 ⇒ 暂为 null。</summary>
    public string? ParentDemandKey { get; init; }

    /// <summary>绑定的链；<see cref="StageChainBindingStatus.NoChain"/> 的状态下为 null。</summary>
    public string? ChainRefId { get; init; }

    /// <summary>`ROOT` / `EDGE`。</summary>
    public string StageScopeType { get; init; } = string.Empty;

    /// <summary>本需求所属请求的 `ResolvedBOMNO`。</summary>
    public string? ResolvedBOMNO { get; init; }

    /// <summary>本需求所属 Workset（`OrderBomRequestLink.RepWorksetId`）。</summary>
    public long? RepWorksetId { get; init; }

    /// <summary>本次装载的 BOM 批次标识，便于与源数据对拍。</summary>
    public string? BatchNo { get; init; }

    /// <summary>解析状态，取值见 <see cref="StageChainBindingStatus"/>。</summary>
    public string Status { get; init; } = StageChainBindingStatus.Resolved;

    /// <summary>
    /// **候选链签名**（1号位 2026-11-25 回执 §Q4 点名要的「歧义在哪几条链」）。
    ///
    /// 【内容】本需求在选择时**所有出现过的**候选链签名（`StageSeq:StageCode|…`），含最终选中的那条。
    ///   来源＝`EffectiveStageChainResolver.Resolution` 的 `ChainKey` ＋ `RejectedChainKeys`，
    ///   装载侧现成就有、不额外查库。
    ///
    /// 【为空的情形】无任何候选（`NO_CHAIN` / `NO_ANCHOR`）⇒ 空表；正常单链 ⇒ 只含 1 条。
    /// 【口径】只报**签名**，不重复报各链的阶段明细 —— 明细在 `StageChainDefinitions` 里按 `ChainRefId` 取。
    /// </summary>
    public IReadOnlyList<string> CandidateChainSignatures { get; init; } = Array.Empty<string>();

    /// <summary>候选链条数（`CandidateChainSignatures.Count` 的便捷投影，便于快速过滤 >1 的歧义需求）。</summary>
    public int CandidateChainCount { get; init; }
}

/// <summary>
/// <see cref="DemandStageChainBinding.Status"/> 取值。
///
/// 【前四个不是我方自造的词表】与 `EffectiveStageChainResolver.RequestIdentityState`
///   一一对应（`Anchored`→<see cref="Resolved"/>、`ParentEdgeScoped`→<see cref="ParentEdgeScoped"/>、
///   `UnverifiedRequestIdentity`→<see cref="UnverifiedIdentity"/>、`NoAnchor`→<see cref="NoAnchor"/>），
///   只有 <see cref="Conflict"/> 是新增的。1号位 若希望直接用枚举原名，改这一个常量类即可。
///
/// 【消费侧要求】`NoChain` 的三种（<see cref="NoAnchor"/> / <see cref="Conflict"/> / 无绑定行）
///   成因不同、未排原因也不同，**不得**合并成一类静默处理。
/// </summary>
public static class StageChainBindingStatus
{
    /// <summary>锚定到真实请求键，唯一合法链。</summary>
    public const string Resolved = "RESOLVED";

    /// <summary>EDGE 只能按父边收窄，**非请求级可信**（J16-01）。</summary>
    public const string ParentEdgeScoped = "PARENT_EDGE_SCOPED";

    /// <summary>有 BOMNO 但缺第二层锚点，请求实例身份未验证（J15-01 状态 b）。</summary>
    public const string UnverifiedIdentity = "UNVERIFIED_IDENTITY";

    /// <summary>三键全空 ⇒ 无权威请求身份（J15-01 状态 c）。</summary>
    public const string NoAnchor = "NO_ANCHOR";

    /// <summary>请求身份未经证明且候选链内容不一致（J16-01 准入），或物料级角色冲突。**仅隔离本需求，不影响邻单**。</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>本需求本次没有可用链（本批无该物料 StagePath 行 / 首选 scope 无链）。</summary>
    public const string NoChain = "NO_CHAIN";
}