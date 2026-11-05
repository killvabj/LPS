using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **需求克隆保真**（2号位 2026-10-29 来件《StageHandoffWaitRules 装载侧字段已加…》§三/§四）。
///
/// 来件原话：「**克隆必须是忠实克隆，不得只挑『当前有人读』的字段（漏拷是静默错值，比编译错更难发现）**」。
/// 本文件把这句变成可执行的护栏：**反射枚举 `LogicalProductionDemand` 的全部公开属性**，
/// 逐个核克隆结果 —— 以后 `LogicalProductionDemand` 再加字段（例如 2号位 正在加的 `FactoryCode`），
/// 只要某个克隆没拷，**本用例立刻红**，不必再靠人肉清点。
///
/// 覆盖 Scheduling 内**全部**需求克隆点（`LPS.APS.Scheduling` 只有这两处，
/// 其余 `FactoryId = demand.FactoryId` 都是构造 `FinalTaskDraft`，不是需求克隆）：
///   · `PhaseTwoInitialScheduler.CloneDemandForPartialLock` —— 部分锁定剩余需求；
///   · `PhaseTwoInitialScheduler.CloneDemandWithBatchQty` —— 执行批拆批 / 修复再拆 / 合并需求。
///
/// 生产侧无任何改动，本文件只读 Core DTO；纯内存，不碰库、不跑 Solver。
/// </summary>
public class DemandCloneFidelityTests
{
    /// <summary>两个克隆**唯一允许改**的字段（本批数量）。</summary>
    private static readonly string[] QuantityFields =
    {
        nameof(LogicalProductionDemand.NetOutputQty),
        nameof(LogicalProductionDemand.PlannedProcessQty)
    };

    /// <summary>
    /// 源需求：**每个字段都给一个与默认值不同的值**（否则漏拷与「拷了默认值」不可区分）。
    /// </summary>
    private static LogicalProductionDemand Source() => new()
    {
        LogicalDemandKey = "PLAN7_1",
        PlanVersionId = 7,
        DomainKey = "DOM-CN",
        AllocationSequence = 3,
        DemandKey = "ORD-9-3",
        OrderId = 9001,
        IsRootDemand = true,
        BomNo = "BOM-X1",
        RequestDetailId = 5501,
        RepWorksetId = 6601,
        MaterialId = 1001,
        FactoryId = 5,
        // 2号位 2026-10-29（r13945）新增载体。**必须给非默认值**：`FactoryCode` 默认 `string.Empty`，
        //   若源与克隆都用默认值，「漏拷」与「拷了空串」不可区分 ⇒ 这条检查对该字段会空转。
        FactoryCode = "CN",
        StartStageCode = "CN_PRE",
        RequiredStageCode = "CN_FINAL",
        StartOperationCode = "OP10",
        NetOutputQty = 100m,
        PlannedProcessQty = 120m,
        UOM = "EA",
        RequiredAvailableTime = new DateTime(2026, 10, 20, 8, 0, 0),
        DemandSequence = 42,
        ProductionInstructionNo = "PI-2026-0001",
        IsUnlocated = true,
        PreferredResourceId = 3101,
        FallbackResourceId = 3102,
        IsContinuation = true,
        ContinuationKey = "ORD-9-3|A",
        RouteCode = "R-CN-01",
        PathId = 9,
        NoSplitMerge = true,
        PreferredResourceCode = "EQ-77",
        DemandGoal = DemandGoalValues.CustomerCommitment,
        QuantityAuthority = QuantityAuthorityValues.OrderOnly
    };

    [Fact]
    public void 部分锁定剩余需求克隆_除数量外逐字段保真()
    {
        var src = Source();

        var clone = PhaseTwoInitialScheduler.CloneDemandForPartialLock(src, 30m, 36m);

        Assert.Equal(30m, clone.NetOutputQty);
        Assert.Equal(36m, clone.PlannedProcessQty);
        AssertAllOtherFieldsCopied(src, clone);
    }

    [Fact]
    public void 拆批克隆_除数量外逐字段保真()
    {
        var src = Source();

        var clone = PhaseTwoInitialScheduler.CloneDemandWithBatchQty(src, 25m, 30m);

        Assert.Equal(25m, clone.NetOutputQty);
        Assert.Equal(30m, clone.PlannedProcessQty);
        AssertAllOtherFieldsCopied(src, clone);
    }

    /// <summary>
    /// 反射逐字段比对。**新加字段自动纳入** —— 这是本用例相对「人肉清点」的全部意义。
    /// </summary>
    private static void AssertAllOtherFieldsCopied(LogicalProductionDemand src, LogicalProductionDemand clone)
    {
        var properties = typeof(LogicalProductionDemand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Where(p => !QuantityFields.Contains(p.Name, StringComparer.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        // 自检：属性清单非空且确实包含本次补齐的那几个（防止反射拿到空集导致「永远绿」）。
        var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     nameof(LogicalProductionDemand.DemandGoal),
                     nameof(LogicalProductionDemand.QuantityAuthority),
                     nameof(LogicalProductionDemand.IsRootDemand),
                     nameof(LogicalProductionDemand.BomNo),
                     nameof(LogicalProductionDemand.RequestDetailId),
                     nameof(LogicalProductionDemand.RepWorksetId),
                     nameof(LogicalProductionDemand.RouteCode),
                     nameof(LogicalProductionDemand.PathId),
                     nameof(LogicalProductionDemand.FactoryCode)
                 })
        {
            Assert.Contains(expected, names);
        }

        var mismatched = new List<string>();
        foreach (var property in properties)
        {
            var expected = property.GetValue(src);
            var actual = property.GetValue(clone);
            if (!Equals(expected, actual))
            {
                mismatched.Add($"{property.Name}: 期望 [{expected ?? "null"}] 实得 [{actual ?? "null"}]");
            }
        }

        Assert.True(
            mismatched.Count == 0,
            "克隆漏拷（漏拷是静默错值，比编译错更难发现）：" + Environment.NewLine + string.Join(Environment.NewLine, mismatched));
    }
}
