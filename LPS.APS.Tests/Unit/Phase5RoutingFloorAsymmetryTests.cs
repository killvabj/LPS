using System.Collections.Generic;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **取图口径不对称 ⇒ 压实把下游拉到上游之前**（2026-10-28 PV190 run39 实跑失败的回归）。
///
/// 实跑原文（`d:/tmp/run39_full.log`）：
///   `IsSuccess=False, FinalTask=0,
///    Error=[Phase5] 硬约束校验失败: TaskDependency违反时间约束: 下游Task eb793726-… 开始时间
///    2026-10-22 14:37:09 早于上游Task e5079302-… 结束时间 2026-10-22 14:38:10 + Lag 0分钟`
///
/// 成因（**两条解析链**，本类把差异钉死）：
///   · **压实侧**（`CompactGaps` / `OptimizeSetupSequences` 算 floor）用
///     <see cref="ConstraintContext.TryGetRoutingGraph"/>——按**任务自身** `(RouteCode, PathId)` 精确取，
///     缺任一 ⇒ **false**（`PhaseOneConstraintBuilder.cs` 明文：不得退回「按物料猜唯一 Path」，Q-3 红线）。
///   · **校验侧**（`GenerateTaskDependencies` 建边）用 `TryGetGraphForBatch` → 最后回落
///     <see cref="ConstraintContext.TryGetSingleRoutingGraph"/>——**单路径物料即使任务没带 PathId 也取得到图**。
///   ⇒ 同一批 Task，校验方建得出边、压实方看不见前序 ⇒ 前拉越过上游。
///
/// ⚠ 本类**只调 `ConstraintContext` 的纯查询**：不起 Solver、不读日历、**不碰数据库**。
/// 它证明的是「两条口径确实会分叉」；生产侧的处置（此类 Task 登记为不可移动）见
/// `CompactGaps` / `OptimizeSetupSequences` 中的 `immovable.Add`。
/// </summary>
public class Phase5RoutingFloorAsymmetryTests
{
    private const int MaterialId = 1;
    private const string DemandKey = "D1";

    /// <summary>单路径物料：只有 `("DEFAULT", 1)` 一张图，边 OP10 → OP20。</summary>
    private static ConstraintContext SinglePathConstraints()
    {
        var graph = new RoutingGraph();
        graph.Dependencies[OperationNodeKey.Of("S2", "OP20")] = new List<DependencyEdge>
        {
            new()
            {
                From = OperationNodeKey.Of("S1", "OP10"),
                To = OperationNodeKey.Of("S2", "OP20"),
                DependencyType = "ES",
                LagTime = 0m
            }
        };

        var constraints = new ConstraintContext();
        constraints.RoutingGraphs[MaterialId] = new Dictionary<RoutePathKey, RoutingGraph>
        {
            [RoutePathKey.Of("DEFAULT", 1)] = graph
        };
        return constraints;
    }

    // ════════════════════════════════════════════════════════════════════
    //  1. 鉴别性：**同一物料、同一任务身份**，两条口径给出相反结果
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 任务没带路径身份（RouteCode 空 / PathId null）时的分叉：
    ///   · 压实侧 `TryGetRoutingGraph` ⇒ **false**（看不到任何前序 ⇒ 无 floor）
    ///   · 校验侧 `TryGetDemandRoutingGraph` ⇒ **true**（回落单路径 ⇒ 建得出边）
    /// 这正是 run39 那条违规的可达前提。
    /// </summary>
    [Fact]
    public void 任务无路径身份时_压实侧取不到图而校验侧取得到()
    {
        var constraints = SinglePathConstraints();

        // 压实侧入口（= `CompactGaps` 实际调用方式：任务自身的 RouteCode / PathId）
        Assert.False(
            constraints.TryGetRoutingGraph(MaterialId, null, null, out _),
            "任务无路径身份时，压实侧必须 Fail Closed（不猜），这是 Q-3 红线要求的");

        // 校验边生成侧入口（= `GenerateTaskDependencies` → `TryGetDemandRoutingGraph` 的回落链）
        Assert.True(
            constraints.TryGetDemandRoutingGraph(DemandKey, MaterialId, null, null, out var resolved),
            "校验侧在单路径物料上必须能取到图（否则它建不出边，run39 的违规也不会指向这类 Task）");
        Assert.NotNull(resolved);
        Assert.True(resolved!.Dependencies.Count > 0, "校验侧的图必须带依赖边");
    }

    /// <summary>
    /// 反向保护：任务**带**路径身份时两条口径必须一致（否则「冻结」范围会被错误放大，压实白白失效）。
    /// </summary>
    [Fact]
    public void 任务带路径身份时_两侧取到的是同一张图()
    {
        var constraints = SinglePathConstraints();

        Assert.True(constraints.TryGetRoutingGraph(MaterialId, "DEFAULT", 1, out var byTask));
        Assert.True(constraints.TryGetDemandRoutingGraph(DemandKey, MaterialId, "DEFAULT", 1, out var byDemand));
        Assert.Same(byTask, byDemand);
    }
}
