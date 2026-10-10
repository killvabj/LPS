using System;
using System.Collections.Generic;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **V1_3 F-02 / F-03 判别性单测**（0号位 2026-10-10《APS_V1_3_20261010.md》§3，REWORK/BLOCKED）。
///
/// 复审判词（§7「1号位必须整改」，逐字）：
///   「F-02/F-03 在 2/5号位 供给正式事实后消费**四项 PI 限量**，核对**唯一真实 MTS PI**、
///     **单 Stage 合法自由量**、**同 PI 同 Stage 新增 Batch 累计**；
///     **不能以 PI Remaining 冒充 Stage Free**；**位置未知须显式未满足**。」
///
/// 本文件把上述四条判据**逐条**落成反证：
///   · 唯一性（0 行 = 缺失 / ≥2 行 = 来源不唯一，**不得取首条**）→ ①②③
///   · Stage 位置未知（上下文缺失 / PI·物料不匹配 / StageCode 空）→ ④⑤⑥
///   · 四项限量 min 取值的**逐项**判别（PI Original / PI Remaining / StageFree − 已占 / 配置 Max）→ ⑦⑧⑨⑩
///   · 边界相等合法、越限**不静默截断** → ⑪⑫
///   · 正常 Policy 路径同样过门禁；输入未投影则**登记缺口不拦截** → ⑬⑭
///   · B-010 兜底与 B-009 共用同一门禁（可靠 Stage 自由量 ⇒ 成立）→ ⑮
///   · 跨工序**不重复计数**（门禁消费需求总量一次）→ ⑯
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class PiLegalQuantityGateTests
{
    private const int MaterialId = 1;
    private const string PiNo = "PI-1";
    private const string Stage = "STAGE1";

    private static PiRemainingFact Fact(
        string piNo = PiNo, int materialId = MaterialId,
        decimal original = 100m, decimal remaining = 100m)
        => new()
        {
            ProductionInstructionNo = piNo,
            MaterialId = materialId,
            PiQuantity = original,
            PiReceivedQty = original - remaining,
            PiRemainingQty = remaining
        };

    private static PhaseTwoInitialScheduler.PiStageQuantityContext Ctx(
        string piNo = PiNo, int materialId = MaterialId, string stage = Stage,
        decimal stageFree = 100m, decimal committed = 0m)
        => new(piNo, materialId, stage, stageFree, committed);

    private static LogicalProductionDemand Demand(
        decimal netQty = 10m, string? piNo = PiNo, int materialId = MaterialId)
        => new()
        {
            LogicalDemandKey = "D1",
            ProductionInstructionNo = piNo,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = "D1",
            MaterialId = materialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = netQty,
            PlannedProcessQty = netQty,
            RequiredAvailableTime = new DateTime(2026, 9, 21, 0, 0, 0),
            DemandSequence = 1
        };

    private static BatchPolicyRuleSnapshot Policy(decimal? max = null, decimal? min = null)
        => new()
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = 100,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            AllowSplit = true,
            AllowMerge = false
        };

    // ══════════════════════════════════════════════════════════════════
    //  ①②③ 唯一性：禁止取首条
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ① 无明确 PI 来源 ⇒ B-009 的 PI 量限**不适用**（与 B-010 末句「无明确 PI 来源不适用该兜底」同向）⇒ 通过。
    /// ② 非正数量不构成批 ⇒ 不适用 ⇒ 通过。
    /// </summary>
    [Fact]
    public void F02_无明确PI来源或非正数量_门禁不适用()
    {
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, productionInstructionNo: null, materialId: MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(), configuredMax: null));

        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, productionInstructionNo: "   ", materialId: MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(), configuredMax: null));

        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            0m, productionInstructionNo: PiNo, materialId: MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(), configuredMax: null));
    }

    /// <summary>
    /// ③ **唯一性**：0 行 = 事实缺失 ⇒ 显式未满足；≥2 行 = **来源不唯一** ⇒ 显式未满足
    ///   （旧实现 `break` 取首条 ⇒ 多行时**静默选定一条**，本用例在旧实现下红）。
    /// </summary>
    [Fact]
    public void F02_PI权威行必须唯一_0行缺失与多行不唯一均显式未满足()
    {
        // 0 行（PI 号不匹配）
        var none = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact(piNo: "PI-OTHER") }, stageContext: Ctx(), configuredMax: null);
        Assert.NotNull(none);
        Assert.Contains("找不到 PI 权威行", none);

        // 0 行（物料不匹配）
        var none2 = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact(materialId: 999) }, stageContext: Ctx(), configuredMax: null);
        Assert.NotNull(none2);
        Assert.Contains("找不到 PI 权威行", none2);

        // ≥2 行 ⇒ 不得任取其一
        var dup = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact(original: 100m), Fact(original: 500m) },
            stageContext: Ctx(), configuredMax: null);
        Assert.NotNull(dup);
        Assert.Contains("不唯一", dup);
        Assert.Contains("不得任取其一", dup);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ④⑤⑥ 位置未知须显式未满足（F-02 核心：不得以 PI Remaining 冒充 Stage Free）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ④ **上下文缺失 ⇒ 位置未知**。这是 F-02 的**核心判据**：即便 PI 剩余量（1000）远大于需求量（10），
    ///   **也不得**据此放行 —— 因为「Stage 合法自由量」与「PI 剩余量」是**两个不同的量**。
    ///   （旧实现只卡 `PiRemainingQty`，把二者等同 ⇒ 本用例在旧实现下**通过**（= 缺陷），现实现**拒绝**。）
    /// </summary>
    [Fact]
    public void F02_缺Stage合法自由量上下文_即便PI剩余量充足也显式未满足()
    {
        var reason = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact(original: 1_000m, remaining: 1_000m) },
            stageContext: null, configuredMax: null);

        Assert.NotNull(reason);
        Assert.Contains("缺 Stage 合法自由量上下文", reason);
        Assert.Contains("位置未知", reason);
        Assert.Contains("不得以 PI Remaining 冒充 Stage 自由量", reason);
    }

    /// <summary>⑤ 上下文与需求**不匹配**（PI 号或物料）⇒ 位置不可信 ⇒ 显式未满足。</summary>
    [Fact]
    public void F02_上下文PI或物料不匹配_位置不可信()
    {
        var wrongPi = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(piNo: "PI-OTHER"), configuredMax: null);
        Assert.NotNull(wrongPi);
        Assert.Contains("位置不可信", wrongPi);

        var wrongMat = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(materialId: 999), configuredMax: null);
        Assert.NotNull(wrongMat);
        Assert.Contains("位置不可信", wrongMat);
    }

    /// <summary>⑥ 上下文**缺 StageCode**（空白）⇒ 位置未知 ⇒ 显式未满足。</summary>
    [Fact]
    public void F02_上下文缺StageCode_位置未知()
    {
        var reason = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            10m, PiNo, MaterialId,
            piFacts: new[] { Fact() }, stageContext: Ctx(stage: "  "), configuredMax: null);

        Assert.NotNull(reason);
        Assert.Contains("缺 StageCode", reason);
        Assert.Contains("位置未知", reason);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑦⑧⑨⑩ 四项限量：**逐项**判别 min 的取值来源
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⑦ **PI Original** 是最紧项 ⇒ 越限。其余三项均给足 ⇒ 越限只可能来自 PI Original。
    /// </summary>
    [Fact]
    public void F03_最紧项为PI_Original_按它判越限()
    {
        var facts = new[] { Fact(original: 40m, remaining: 100m) };   // Original 40 最紧
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            40m, PiNo, MaterialId, facts, Ctx(stageFree: 100m), configuredMax: null));
        var over = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            41m, PiNo, MaterialId, facts, Ctx(stageFree: 100m), configuredMax: null);
        Assert.NotNull(over);
        Assert.Contains("PI Original 40", over);
    }

    /// <summary>⑧ **PI Remaining** 是最紧项 ⇒ 按它判越限。</summary>
    [Fact]
    public void F03_最紧项为PI_Remaining_按它判越限()
    {
        var facts = new[] { Fact(original: 100m, remaining: 30m) };  // Remaining 30 最紧
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            30m, PiNo, MaterialId, facts, Ctx(stageFree: 100m), configuredMax: null));
        var over = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            31m, PiNo, MaterialId, facts, Ctx(stageFree: 100m), configuredMax: null);
        Assert.NotNull(over);
        Assert.Contains("PI Remaining 30", over);
    }

    /// <summary>
    /// ⑨ **StageFree − 同PI同Stage已占量** 是最紧项 ⇒ 按它判越限。
    ///   B-009 的「**同 PI 同 Stage 新增 Batch 累计**」正是靠 <c>AlreadyCommittedQty</c> 落地：
    ///   自由量 100、已占 70 ⇒ 可用 30 ⇒ 31 即越限（若忽略已占量，31 ≤ 100 会被误放行）。
    /// </summary>
    [Fact]
    public void F03_同PI同Stage已占量参与累计_最紧时按Stage自由余额判越限()
    {
        var ctx = Ctx(stageFree: 100m, committed: 70m);             // 余额 30
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            30m, PiNo, MaterialId, new[] { Fact() }, ctx, configuredMax: null));
        var over = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            31m, PiNo, MaterialId, new[] { Fact() }, ctx, configuredMax: null);
        Assert.NotNull(over);
        Assert.Contains("Stage 合法自由 30", over);
        Assert.Contains("同PI同Stage已占 70", over);
    }

    /// <summary>
    /// ⑨b 已占量**超过**自由量 ⇒ 余额钳到 0（**不为负**）⇒ 任何正需求都越限。
    /// </summary>
    [Fact]
    public void F03_已占量超自由量_余额钳零_任何正需求均越限()
    {
        var reason = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            1m, PiNo, MaterialId, new[] { Fact() },
            Ctx(stageFree: 50m, committed: 80m), configuredMax: null);
        Assert.NotNull(reason);
        Assert.Contains("Stage 合法自由 0", reason);
    }

    /// <summary>⑩ **配置 Max** 是最紧项 ⇒ 按它判越限（正常 Policy 路径把 `MaxExecutionBatchQty` 传进门禁）。</summary>
    [Fact]
    public void F03_最紧项为配置Max_按它判越限()
    {
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            25m, PiNo, MaterialId, new[] { Fact() }, Ctx(stageFree: 100m), configuredMax: 25m));
        var over = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            26m, PiNo, MaterialId, new[] { Fact() }, Ctx(stageFree: 100m), configuredMax: 25m);
        Assert.NotNull(over);
        Assert.Contains("配置 Max 25", over);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑪⑫ 边界相等合法 / 越限不静默截断
    // ══════════════════════════════════════════════════════════════════

    /// <summary>⑪ **边界相等合法**：`Q == cap` ⇒ 通过（判据是**严格大于**才越限）。</summary>
    [Fact]
    public void F03_需求量恰等于合法量_边界合法()
    {
        Assert.Null(PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            80m, PiNo, MaterialId,
            new[] { Fact(original: 100m, remaining: 80m) }, Ctx(stageFree: 80m), configuredMax: 80m));
    }

    /// <summary>
    /// ⑫ **不静默截断**：越限时返回的是**拒绝原因**（不是把 Q 截到合法量后放行）。
    ///   判据：越限调用返回**非 null**，且消息明示「不静默截断 Q」；同时**不**返回「截断后的数量」——
    ///   门禁的返回值类型是 `string?`，**没有任何数量出口** ⇒ 结构上不可能截断。
    /// </summary>
    [Fact]
    public void F03_越限显式记录未满足_结构上不可能静默截断()
    {
        var reason = PhaseTwoInitialScheduler.EvaluatePiLegalQuantityGate(
            100m, PiNo, MaterialId,
            new[] { Fact(original: 100m, remaining: 40m) }, Ctx(stageFree: 100m), configuredMax: null);

        Assert.NotNull(reason);
        Assert.Contains("PI_LEGAL_QTY_OVER_LIMIT", reason);
        Assert.Contains("显式记录未满足", reason);
        Assert.Contains("不静默截断 Q", reason);
        // 返回类型即 `string?`：合法量**没有**任何回传给调用方的通道 ⇒ 无从截断。
        Assert.IsType<string>(reason);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑬⑭ 端到端：正常 Policy 路径过门禁 / 输入未投影登记缺口
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⑬ **正常 Policy 路径**同样受 B-009 门禁约束（F-03：不再只给 B-010 兜底装门禁）：
    ///   · 未越限 ⇒ `IsLegal=true`，批照常产出；
    ///   · 越限 ⇒ `IsLegal=false`（`Conflict`），**0 批**、原因带 `PI_LEGAL_QTY_OVER_LIMIT`；
    ///   · 且 `SolverDiagnostics.PiLegalQuantityGateBlocked` 计数 +1（可观测）。
    /// </summary>
    [Fact]
    public void F03_正常Policy路径同样过B009门禁_越限则冲突且计数()
    {
        var policy = Policy(max: 100m);
        var facts = new[] { Fact(original: 100m, remaining: 100m) };
        var ctx = Ctx(stageFree: 40m);      // Stage 自由 40 最紧

        using var scope = SolverDiagnostics.BeginScope();

        var ok = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 40m), policy, piFacts: facts, piStageContext: ctx);
        Assert.True(ok.IsLegal, ok.ConflictReason);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);

        var blocked = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 41m), policy, piFacts: facts, piStageContext: ctx);
        Assert.False(blocked.IsLegal);
        Assert.Empty(blocked.Batches);
        Assert.Contains("PI_LEGAL_QTY_OVER_LIMIT", blocked.ConflictReason);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGateBlocked);
    }

    /// <summary>
    /// ⑭ **输入未投影**（`piFacts is null`）：门禁**不可评估** ⇒ **不拦截**（拦截会把每条 PI 需求都打死、
    ///   发明一条冻结文档未要求的失败模式，违反 §8「不要求重新冻结业务」），
    ///   但必须用计数器**显式登记缺口**（不静默）⇒ `PiLegalQuantityGateUnevaluated` +1。
    ///   ⚠ 本用例同时如实固定「生产态该门禁当前不可达」这一事实（调用点传 `piFacts: null`）。
    /// </summary>
    [Fact]
    public void F03_输入未投影_不拦截但显式登记缺口()
    {
        using var scope = SolverDiagnostics.BeginScope();

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 10m), Policy(max: 100m), piFacts: null, piStageContext: null);

        Assert.True(formation.IsLegal, formation.ConflictReason);
        Assert.NotEmpty(formation.Batches);
        Assert.Equal(1, scope.Counters.PiLegalQuantityGateUnevaluated);
        Assert.Equal(0, scope.Counters.PiLegalQuantityGateBlocked);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑮ B-010 兜底与 B-009 共用同一门禁
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⑮ **B-010 缺策略兜底**：前置①（明确 PI 来源）②（Policy 完全无匹配）③（Stage 合法自由量可靠）
    ///   **同时**成立 ⇒ 按 `Q_C` 组织**一个** Stage 执行批候选（Q 原值、不伪造 Min/Max/Preferred）。
    ///   ⇒ 与 ⑬ 对照：**同一门禁**（`EvaluatePiLegalQuantityGate`）在两条路径上口径一致。
    /// </summary>
    [Fact]
    public void F03_B010缺策略兜底_前置三项齐备时产一批_Q原值()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 30m), policy: null,
            piFacts: new[] { Fact(original: 100m, remaining: 80m) },
            piStageContext: Ctx(stageFree: 80m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var single = Assert.Single(formation.Batches);
        Assert.Equal(30m, single.NetOutputQty);
        Assert.Equal(30m, single.PlannedProcessQty);
    }

    /// <summary>
    /// ⑮b 前置③**不成立**（缺 Stage 上下文）⇒ 兜底**不适用** ⇒ `IsPiContractPending`、0 批
    ///   （不得凭空造批；也不得回落到笼统的 `BATCH_POLICY_MISSING`）。
    /// </summary>
    [Fact]
    public void F03_B010前置三不成立_契约待核且不造批()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 30m), policy: null,
            piFacts: new[] { Fact() }, piStageContext: null);

        Assert.False(formation.IsLegal);
        Assert.True(formation.IsPiContractPending);
        Assert.Empty(formation.Batches);
        Assert.Contains("B010_PI_QUANTITY_CONTRACT_PENDING", formation.ConflictReason);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⑯ 跨工序不重复计数
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⑯ **跨工序不重复计数**：门禁消费的是**需求总量**（`Σ 批 NetOutputQty`）**一次**，
    ///   不是「每道工序各算一次」。判据：Stage 自由量恰等于需求量时**合法**（= 只算了一次）；
    ///   若按工序数（本例 2 道）重复累计，需求量 30 会变成 60 &gt; 30 而被误判越限 ⇒ 本用例红。
    /// </summary>
    [Fact]
    public void F03_跨工序不重复计数_门禁只消费需求总量一次()
    {
        // PlannedProcessQty = 60（两道工序各 30），NetOutputQty = 30
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            ProductionInstructionNo = PiNo,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = "D1",
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = 30m,
            PlannedProcessQty = 60m,
            RequiredAvailableTime = new DateTime(2026, 9, 21, 0, 0, 0),
            DemandSequence = 1
        };

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, Policy(max: 100m),
            piFacts: new[] { Fact(original: 100m, remaining: 100m) },
            piStageContext: Ctx(stageFree: 30m));      // 恰等于需求总量

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var total = 0m;
        foreach (var b in formation.Batches) total += b.NetOutputQty;
        Assert.Equal(30m, total);                      // 门禁比较的就是这 30（不是 60）
    }
}
