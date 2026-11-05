using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **v1.15 `:1320`「合法无 PI 来源」软 Preferred** 的正反例单测（1号位，2026-11-02）。
///
/// 立此文件的起因（2号位 2026-11-02《缺配键清单463键与B010前置举证》§三）：
///   我方 `TryFormConservativePiBatch` 过去把「有明确 PI」当成整条缺策略兜底的**总前提**，
///   无 PI 直接 `return null` ⇒ 落到 `BATCH_POLICY_MISSING` **硬未排**。
///
/// 冻结文档逐字（本地 v1.15 正文，`项目文档/PM冻结文档/20261031/01_APS_V1_…v1.15_20261029_….md`）：
///   · `:1311`「`MinExecutionBatchQty`、`MaxExecutionBatchQty`、`PreferredBatchQty` 在V1统一解释为
///     经济性**软**参数…**不得把软经济区间用作C桶硬失败**」；
///   · `:1319`「**仅对**有真实且唯一ERP PI来源的C桶，Stage新增批**硬数量**按…共同约束…
///     **无PI来源时不得猜造PI或借用PI数量公式**」；
///   · `:1320`「**完全缺配的合法C桶**（**包含明确PI来源与合法无PI来源**）按本业务键MISSING处理，
///     取本次 `LogicalProductionDemand.NetOutputQty` 中的C桶Stage可安排净产出 `Q_C` 作为
///     **仅本Run经济软Preferred建议**，进行有界合法候选评价…**无真实PI的新规划任务不得直接MES下发**」。
///   ⇒ **PI 只约束「硬数量」；软 Preferred 对无 PI 同样成立。**
/// 0号位 2026-10-31《联合核证结论与未关闭事项_v1.0》`:16` 同口径：
///   「合法MISSING使用Q_C软Preferred，不以NoRouting直接判MISSING或Fail Closed」。
///
/// 本文件与 `ExecutionBatchDraftTests` 的分工：
///   · `ExecutionBatchDraftTests` 继续守**有 PI** 的 B-010 量限链与「`Missing` ≠ `Unknown`」出口码；
///   · 本文件专守**无 PI** 的软 Preferred 链及其**边界**（不为救而救、不劫持有策略路径、不吞 A/B）。
/// </summary>
public class NoPiBatchSoftPreferredTests
{
    private const int MaterialId = 700001;
    private const int DeptId = 900;
    private const string Stage = "STAGE1";

    private static LogicalProductionDemand Demand(
        decimal netQty,
        decimal? procQty = null,
        string? piNo = null,
        bool isContinuation = false,
        bool noSplitMerge = false)
        => new()
        {
            LogicalDemandKey = "D1",
            ProductionInstructionNo = piNo,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = "D1",
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = netQty,
            PlannedProcessQty = procQty ?? netQty,
            DemandSequence = 1,
            IsContinuation = isContinuation,
            NoSplitMerge = noSplitMerge
        };

    // ── 正例 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 正例①：**无 PI + 完全缺配 + 正净产出** ⇒ 按 `Q_C` 出**一个**软 Preferred 批。
    /// 反证性：旧实现（PI 为总前提）在本用例下 `IsMissingPolicy=true`、0 批 ⇒ **红**。
    /// </summary>
    [Fact]
    public void 无PI_完全缺配_正数量_按QC出单批()
    {
        var f = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 30m), policy: null);

        Assert.True(f.IsLegal, f.ConflictReason);
        Assert.False(f.IsMissingPolicy);
        Assert.False(f.IsPiContractPending);

        var batch = Assert.Single(f.Batches);
        Assert.Equal(30m, batch.NetOutputQty);   // Q_C 原值：不扩大、不截断
        Assert.Equal(1, batch.Ordinal);          // 缺策略 ⇔ 无 Min/Max/Preferred ⇒ 不虚构拆批
    }

    /// <summary>
    /// 正例②：`Q_C` 取 `NetOutputQty`，**不得**机械拿 `PlannedProcessQty` 顶替（`:1320` 逐字禁止）。
    /// </summary>
    [Fact]
    public void 无PI_批数量取NetOutputQty_不用PlannedProcessQty顶替()
    {
        var f = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 30m, procQty: 47m), policy: null);

        var batch = Assert.Single(f.Batches);
        Assert.Equal(30m, batch.NetOutputQty);
        Assert.Equal(47m, batch.PlannedProcessQty);   // 加工量照原样保留，但**不**当成 Q_C
    }

    // ── 反例（边界：不许为救而救）────────────────────────────────────────────

    /// <summary>
    /// 反例①：**无 PI 但非正净产出** ⇒ 构不成合法批（`:1320` 只管「合法 C 桶」）⇒ 仍 Fail Closed。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 无PI_非正数量_仍FailClosed(int qty)
    {
        var f = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: qty), policy: null);

        Assert.False(f.IsLegal);
        Assert.True(f.IsMissingPolicy);
        Assert.Empty(f.Batches);
        Assert.Contains("BATCH_POLICY_MISSING", f.ConflictReason);
    }

    /// <summary>
    /// 反例②：**有 PI** 的情形**不得**被无 PI 分支吞掉 —— 仍走 B-010 量限链。
    /// 本用例 `piFacts = null`（PI 权威剩余量未投影）⇒ `PiQuantityContractPending`、0 批。
    /// 反证性：若无 PI 分支写成「只要缺策略就放行」，本用例会变成 `IsLegal=true` ⇒ **红**。
    /// </summary>
    [Fact]
    public void 有PI_仍走B010量限链_不被无PI分支吞掉()
    {
        var f = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 30m, piNo: "PI-1"), policy: null, piFacts: null);

        Assert.False(f.IsLegal);
        Assert.True(f.IsPiContractPending);
        Assert.True(f.IsMissingPolicy);   // 与 Missing 同类：Phase4 不得进普通 Local Repair
        Assert.Empty(f.Batches);
    }

    /// <summary>
    /// 反例③：**A/B（既存执行批）不受本兜底影响** —— `DecideExecutionBatchPlan` 在策略判定**之前**
    ///   就返回 `Legal(1)`（B-003：A/B 不参加普通自由拆合批），不会走到「缺策略」判定。
    ///   ⇒ 本用例恒 1 批，且与无 PI 分支无关（分支只可能被 C 桶走到）。
    /// </summary>
    [Fact]
    public void AB既存执行批_不受缺策略兜底影响()
    {
        var f = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(netQty: 30m, isContinuation: true), policy: null);

        Assert.True(f.IsLegal, f.ConflictReason);
        Assert.Single(f.Batches);
    }

    /// <summary>
    /// 反例④：**策略真的存在时，兜底不得劫持** —— 走正常策略路径，批数由经济参数决定。
    /// 本夹具 `Max = 6`、`Qty = 30` ⇒ 基线 5 批；若被无 PI 软兜底劫持（恒 1 批）⇒ **红**。
    /// </summary>
    [Fact]
    public void 有策略时_走正常路径_不被无PI软兜底劫持()
    {
        var policy = new BatchPolicyRuleSnapshot
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = DeptId,
            MinExecutionBatchQty = null,
            MaxExecutionBatchQty = 6m,
            AllowSplit = true,
            AllowMerge = false
        };

        var f = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(netQty: 30m), policy);

        Assert.True(f.IsLegal, f.ConflictReason);
        Assert.Equal(5, f.Batches.Count);
        Assert.Equal(30m, f.Batches.Sum(b => b.NetOutputQty));   // 逐分不丢
    }
}
