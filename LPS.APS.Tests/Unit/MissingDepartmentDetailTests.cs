using System.Collections.Generic;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-03｜`MISSING_PRODUCTION_DEPARTMENT_CONTEXT` **人可读明细**的正反例。
///
/// 立此文件的起因：该出口此前**只给泛化 Reason、不给明细** ⇒ PV 999329 未排逐条 CSV 里
///   **2,143 行全是「无明细」**，数据方拿到只有 `DraftId`，无法定位「要补
///   `MaterialStageDeptContext` 的哪一行」。本文件守住修复后的三条输出：
///   ① 物料号；② 缺部门上下文的 Stage 列表；③ 该物料现有部门行（区分「整条没有」与「有但不含该 Stage」）。
///
/// 该明细**纯文案**，不改 `Reason`、不改 `Success`、不改排程结果。
/// </summary>
public class MissingDepartmentDetailTests
{
    private const int MaterialId = 880001;
    private const int OtherMaterial = 880002;

    private static DomainSolveRequest Request(params MaterialStageDepartmentContextDto[] depts)
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = "D1",
                    MaterialId = MaterialId,
                    FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = 10m,
                    PlannedProcessQty = 10m,
                    DemandSequence = 1
                }
            },
            MaterialStageDepartmentContexts = depts
        };

    private static ConstraintContext ContextWithMissingStages(params string[] stages)
    {
        var ctx = new ConstraintContext();
        ctx.MissingDepartmentContextStages[MaterialId] = new List<string>(stages);
        return ctx;
    }

    /// <summary>
    /// 正例①：物料**一条部门行都没有** ⇒ 明细必须点名「整条补」，而不是含糊说「缺部门」。
    /// </summary>
    [Fact]
    public void 无任何部门行_明细点名整条补()
    {
        var request = Request();
        var detail = PhaseFiveCompression.BuildMissingDepartmentDetail(
            "D1", new Dictionary<string, LogicalProductionDemand>(), request, ContextWithMissingStages("STAGE1"));

        Assert.Contains($"物料 {MaterialId}", detail);
        Assert.Contains("STAGE1", detail);
        Assert.Contains("一条部门上下文行都没有", detail);
        Assert.Contains("整条补", detail);
    }

    /// <summary>
    /// 正例②：物料有部门行、但**不含缺的那个 Stage** ⇒ 明细要区分开，指出「有但不含」，
    /// 否则数据方会误以为整条要重灌。
    /// </summary>
    [Fact]
    public void 有部门行但不含该Stage_明细指出只补缺的那个()
    {
        var request = Request(
            new MaterialStageDepartmentContextDto { MaterialId = MaterialId, StageCode = "STAGE0", ProductionDepartmentId = 991 });
        var detail = PhaseFiveCompression.BuildMissingDepartmentDetail(
            "D1", new Dictionary<string, LogicalProductionDemand>(), request, ContextWithMissingStages("STAGE1"));

        Assert.Contains("现有部门行 = 991", detail);
        Assert.Contains("但不含上述 Stage", detail);
    }

    /// <summary>
    /// 正例③：多个 Stage 缺 ⇒ 全部列出（不是只报第一个）。
    /// </summary>
    [Fact]
    public void 多个Stage缺失_全部列出()
    {
        var request = Request();
        var detail = PhaseFiveCompression.BuildMissingDepartmentDetail(
            "D1", new Dictionary<string, LogicalProductionDemand>(),
            request, ContextWithMissingStages("STAGE1", "STAGE2", "STAGE3"));

        Assert.Contains("STAGE1/STAGE2/STAGE3", detail);
    }

    /// <summary>
    /// 反例：**别的物料**的部门行不得串进来（键域必须是 MaterialId）。
    /// </summary>
    [Fact]
    public void 其他物料的部门行不得串入()
    {
        var request = Request(
            new MaterialStageDepartmentContextDto { MaterialId = OtherMaterial, StageCode = "STAGE9", ProductionDepartmentId = 777 });
        var detail = PhaseFiveCompression.BuildMissingDepartmentDetail(
            "D1", new Dictionary<string, LogicalProductionDemand>(), request, ContextWithMissingStages("STAGE1"));

        Assert.DoesNotContain("777", detail);
        Assert.Contains("一条部门上下文行都没有", detail);
    }

    /// <summary>
    /// 反例：回查不到需求时不得崩，必须给一句可读的兜底文案。
    /// </summary>
    [Fact]
    public void 回查不到需求_不崩且给兜底文案()
    {
        var detail = PhaseFiveCompression.BuildMissingDepartmentDetail(
            "NOT_EXIST", new Dictionary<string, LogicalProductionDemand>(), Request(), new ConstraintContext());

        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Contains("缺生产部门上下文", detail);
    }
}