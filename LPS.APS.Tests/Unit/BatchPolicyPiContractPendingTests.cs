using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 类别整改函 §二·02**：「真实 PI 身份/自由量未知**另归**；**不得按 policy null 判 MISSING**」。
///
/// <para>本类锁：C 桶无策略、**有真实 PI** 但缺 `(PI, MaterialId, StageCode)` 的 Stage 合法自由量时，
/// 形成结果必须带 `IsPiContractPending=true`（供调用方**单列统计**），而不是混进「策略缺配」。</para>
///
/// <para>纯内存，不触库。</para>
/// </summary>
public class BatchPolicyPiContractPendingTests
{
    [Fact]
    public void 有PI但Stage自由量不可证_标记为契约待核而不混入策略缺配()
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D-PI",
            PlanVersionId = 1L,
            DomainKey = "D",
            DemandKey = "D-PI",
            DemandSequence = 1,
            AllocationSequence = 1,
            MaterialId = 880001,
            FactoryId = 1,
            FactoryCode = "F1",
            StartStageCode = "S1",
            NetOutputQty = 10m,
            PlannedProcessQty = 10m,
            ProductionInstructionNo = "PI-880001",
            UOM = "PS"
        };
        var facts = new[]
        {
            new PiRemainingFact
            {
                ProductionInstructionNo = "PI-880001",
                MaterialId = 880001,
                FactoryId = 1,
                PiQuantity = 100m,
                PiRemainingQty = 100m
            }
        };

        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            demand, policy: null, piFacts: facts, piStageContext: null);

        Assert.False(formation.IsLegal);
        Assert.True(formation.IsPiContractPending);   // 单列统计的依据
        Assert.True(formation.IsMissingPolicy);       // 出口码仍走冻结值域（不改 15 码）
    }
}
