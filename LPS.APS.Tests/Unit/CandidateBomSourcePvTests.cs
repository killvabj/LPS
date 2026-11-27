using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 候选运行的 **BOM／需求侧取数源 PV**（3号位 2026-11-27 协助函 Z2-20261127-BL1 甲案）。
///
/// <para><b>要防的错</b>：候选 PV 壳里没有 <c>[Order]</c>、也没有 <c>OrderBomRequestLink</c>——
/// 若仍按「本 PV」解析 BOM 批次身份，必然 <c>NoLinkedBatch</c> ⇒ Fail-Closed，候选运行恒失败。</para>
///
/// <para><b>要防的第二个错（反向）</b>：把**全量跑**也改成 Base ⇒ 等于把非候选运行的需求源挪走，
/// 属越界改动。故全量跑必须**逐字保持自身 PV**。</para>
/// </summary>
public class CandidateBomSourcePvTests
{
    private static PeggingExecutionRequest Req(int pv, bool isCandidate, int? basePv) => new()
    {
        PlanVersionId = pv,
        IsCandidate = isCandidate,
        BasePlanVersionId = basePv
    };

    [Fact(DisplayName = "候选 + 有 Base ⇒ 取 Base（需求侧来源）")]
    public void CandidateWithBase_UsesBase()
    {
        PeggingOrchestrator.BomSourcePlanVersionId(Req(999650, isCandidate: true, basePv: 999634))
            .Should().Be(999634, "候选壳无 [Order]/链接，只能按需求侧 Base 解析 BOM 批次身份");
    }

    [Fact(DisplayName = "全量跑（非候选）⇒ 取自身，零回归")]
    public void FullRun_UsesOwn()
    {
        PeggingOrchestrator.BomSourcePlanVersionId(Req(999634, isCandidate: false, basePv: 999600))
            .Should().Be(999634, "全量跑必须逐字保持自身 PV，不得被 Base 顶替");
    }

    [Fact(DisplayName = "候选但 Base 为空 ⇒ 回落自身（不得取 null）")]
    public void CandidateWithoutBase_FallsBackToOwn()
    {
        PeggingOrchestrator.BomSourcePlanVersionId(Req(999650, isCandidate: true, basePv: null))
            .Should().Be(999650, "无 Base 时没有需求侧来源可取，回落自身，交后续 Fail-Closed 判定");
    }
}
