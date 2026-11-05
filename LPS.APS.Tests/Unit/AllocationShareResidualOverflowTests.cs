using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **末位补差溢出 ⇒ 负份额**（2026-10-28 PV190 run36 实跑失败的回归）。
///
/// 实跑原文（`d:/tmp/run36_full.log:37763`）：
///   `IsSuccess=False, Scheduled=0, Unscheduled=26203,
///    Error=[Phase5] 硬约束校验失败: Task 14b81910-… 的 ShareQty &lt;= 0: -0.4780`
/// ⇒ **一个负份额把整域 26,203 个 Task 全废掉**（不是排不下，是硬校验一票否决）。
///
/// 成因：
///   `GenerateAllocationShares` 旧实现对**每个非末位 Task 独立**做
///   `Math.Round(closureTarget * c / total, 3)`，再把**全部**舍入误差一次性压给排序末位的那个 Task
///   （`closureTarget - Σ前 N−1`），**没有下界**。逐条量化的误差同号累加时，
///   前 N−1 条之和会**超过** `closureTarget` ⇒ 末位算出负数。
///
/// 本类构型（数字全部精确可算，无随机、无近似）：
///   · 同 1 个 Allocation：需求 `D1` 声明 `NetOutputQty = 2.0`，未排量 `U = 0.5`
///     ⇒ 闭合目标（甲式 `Q − U`）`closureTarget = 1.5`；
///   · 1000 个末端 Task，每个 `Quantity = 0.002`（由同一个锚点需求 `D1` 承担，合计 2.0 = Q）；
///   · 末端构成合计 `totalContribution = 2.0` **大于** `closureTarget`
///     ⇒ 分摊比 0.75，必须缩放 —— 这是「部分落定」的**正常路径**（`PhaseFiveCompression.cs:2000`）；
///   · 逐条精确值 `1.5 × 0.002 / 2.0 = 0.0015`（正好落在千分位的 `.5` 上）
///     ⇒ `Math.Round(0.0015, 3)` 用 ToEven 一致进位到 **0.002**（δ = +0.0005，1000 条同号）。
///
///   ⇒ 旧实现：前 999 条 = 999 × 0.002 = **1.998** ＞ `closureTarget = 1.5`
///   ⇒ 末位 = `1.5 − 1.998` = **−0.498**（与实跑的 −0.4780 同量级、同形态）
///   ⇒ `ValidateHardResult` 第 2 项 `share.ComponentQty <= 0`（`:2570`）判失败 ⇒ 整域清零。
///
/// 修复：量化的对象由「本条份额」改为「**前缀累计份额**」——
///   任意前缀漂移恒 ≤ 0.0005（旧实现是 ≤ 0.0005×i）；前缀单调 ⇒ `Math.Round` 单调 ⇒ 份额恒 ≥ 0；
///   逐 Allocation 的 Σ 仍由末位补差**精确**闭合。分摊公式／闭合目标／排序键／末位归属**一律未动**。
///
/// ⚠ 本类**只调纯函数**：不起 Solver、不读日历、**不碰数据库**。
/// </summary>
public class AllocationShareResidualOverflowTests
{
    private const int TaskCount = 1000;
    private const decimal TaskQty = 0.002m;          // 每个末端 Task 的构成
    private const decimal DeclaredQty = 2.0m;        // 需求 D1 的声明净产出量（Q）
    private const decimal UnscheduledQty = 0.5m;     // D1 的未排量（U）
    private const decimal ClosureTarget = DeclaredQty - UnscheduledQty;   // 甲式闭合目标 = 1.5

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0);

    private static LogicalProductionDemand Demand(string key, long alloc, decimal netQty) => new()
    {
        LogicalDemandKey = key,
        PlanVersionId = 1L,
        DomainKey = "DOMAIN",
        AllocationSequence = alloc,
        DemandKey = key,
        MaterialId = 1,
        FactoryId = 1,
        NetOutputQty = netQty,
        PlannedProcessQty = netQty,
        DemandSequence = (int)alloc,
        StartStageCode = "S1",
        StartOperationCode = "OP1",
    };

    private static FinalTaskDraft Task(string id, string sourceDraftId, decimal qty, int minuteOffset) => new()
    {
        FinalDraftId = id,
        SourceDraftId = sourceDraftId,
        MaterialId = 1,
        FactoryId = 1,
        StageCode = "S1",
        OperationCode = "OP1",
        Quantity = qty,
        PlannedProcessQty = qty,
        PlannedStartTime = Base.AddMinutes(minuteOffset),
        PlannedEndTime = Base.AddMinutes(minuteOffset + 1),
    };

    private static DomainSolveRequest Request() => new()
    {
        LogicalProductionDemands = new List<LogicalProductionDemand>
        {
            Demand("D1", 1L, DeclaredQty),
        },
    };

    /// <summary>1000 个末端 Task：同一锚点需求 `D1`、同一 Stage/Operation、时间递增（定序稳定）。</summary>
    private static List<FinalTaskDraft> Tasks() =>
        Enumerable.Range(0, TaskCount)
            .Select(i => Task($"T{i:D4}", "D1", TaskQty, i))
            .ToList();

    private static Dictionary<long, decimal> Unscheduled() => new() { [1L] = UnscheduledQty };

    private static List<UnscheduledTaskResult> UnscheduledRows() => new()
    {
        new() { DraftId = "D1", Reason = "PHASE2_UNSCHEDULED_FOR_TEST", UnscheduledQty = UnscheduledQty }
    };

    // ════════════════════════════════════════════════════════════════════
    //  1. 鉴别性：**旧算法**在这条构型上必产负份额（证明修复是必要的，不是「结果一致性」护栏）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 按**旧实现**逐字重算（非末位逐条 `Math.Round(…,3)`，末位 `closureTarget − Σ前`），
    /// 断言末位落到负数 —— 这正是 run36 的失败形态。
    /// 本用例**不依赖生产代码**，因此即便日后有人把算法改回去，它也照样能指认。
    /// </summary>
    [Fact]
    public void 鉴别性_旧的逐条独立量化必把末位压成负数()
    {
        decimal closureTarget = ClosureTarget;      // 1.5
        const decimal c = TaskQty;                  // 0.002
        var total = TaskCount * c;                  // totalContribution = 2.0

        decimal alreadyAllocated = 0m;
        decimal lastShare = 0m;

        for (int i = 0; i < TaskCount; i++)
        {
            decimal shareQty = i == TaskCount - 1
                ? closureTarget - alreadyAllocated                          // 旧实现的末位补差：无下界
                : Math.Round(closureTarget * c / total, 3);                 // 旧实现：逐条独立量化

            alreadyAllocated += shareQty;
            if (i == TaskCount - 1) lastShare = shareQty;
        }

        Assert.True(lastShare < 0m,
            $"旧算法末位应落到负数（run36 形态），实际 {lastShare}");
        Assert.Equal(closureTarget, alreadyAllocated);   // 旧实现 Σ 也是准的 —— 所以失败**不是** check #1 造成的
    }

    // ════════════════════════════════════════════════════════════════════
    //  2. 修复后：任一条份额都不得 ≤ 0，且逐 Allocation 的 Σ 仍精确闭合
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void 修复后_任一条份额都不得为0或负_且Σ精确等于闭合目标()
    {
        var tasks = Tasks();
        var request = Request();

        var shares = new PhaseFiveCompression()
            .GenerateAllocationSharesForTest(tasks, request, new ConstraintContext(), new(), Unscheduled());

        // (a) 硬校验第 2 项的不变量：不存在 ≤ 0 的份额
        Assert.Empty(shares.Where(s => s.ComponentQty <= 0m));

        // (b) 硬校验第 1 项的不变量：逐 Allocation 的 Σ 精确等于甲式闭合目标（不是「小于容差」）
        Assert.Equal(ClosureTarget, shares.Sum(s => s.ComponentQty));

        // (c) 每条都不超过本 Task 的真实构成（未放大）
        var byTask = shares.GroupBy(s => s.FinalDraftId)
                           .ToDictionary(g => g.Key, g => g.Sum(s => s.ComponentQty));
        foreach (var t in tasks)
        {
            Assert.True(byTask[t.FinalDraftId] <= t.Quantity + 0.001m,
                $"{t.FinalDraftId}: ΣShare={byTask[t.FinalDraftId]} > Quantity={t.Quantity} + 0.001");
        }
    }

    /// <summary>修复后用**真实硬校验**驱动 ⇒ 必须放行（run36 那条整域清零的出口不能再走到）。</summary>
    [Fact]
    public void 修复后_真实硬校验放行()
    {
        var tasks = Tasks();
        var request = Request();
        var constraints = new ConstraintContext();

        var shares = new PhaseFiveCompression()
            .GenerateAllocationSharesForTest(tasks, request, constraints, new(), Unscheduled());

        var (isValid, error) = PhaseFiveCompression.ValidateHardResultForTest(
            tasks, shares, new List<FinalTaskPeggingDraft>(), request, constraints,
            new HashSet<string>(), new(), UnscheduledRows());

        Assert.True(isValid, error);
    }
}
