using LPS.APS.Core.Interfaces;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// O77 / 2026-11-16 5号位 会签：Command 第三列 `ProductionDepartmentCode`。
/// 目的不是“能构造”，而是钉住：
///   ① MES 组装端口的 Holon 部门码必须能从 Command 尾部取到；
///   ② 该列是可选尾参，旧调用不传时为 null，不破坏既有构造。
/// </summary>
public class MesDispatchCommandContractTests
{
    [Fact(DisplayName = "O77 Command 尾部携带 ProductionDepartmentCode")]
    public void Command_CarriesProductionDepartmentCode()
    {
        var command = new MesDispatchCommand(
            ReferenceNo: "REF-1",
            ProductionInstructionNo: "PI-1",
            StageCode: "CN_ASSY",
            SourcePlanVersionId: 999507,
            MaterialCode: "MAT-1",
            Quantity: 1m,
            Uom: "PCS",
            FactoryCode: "CN",
            ResourceCode: null,
            PlannedStartTime: new DateTime(2026, 11, 17, 8, 0, 0),
            PlannedEndTime: new DateTime(2026, 11, 17, 9, 0, 0),
            SourceTaskIds: new long[] { 1 },
            InstructionType: "PRODUCTION_INSTRUCTION",
            WorkOrderType: "ASSEMBLY",
            ProductionDepartmentCode: "CN_ASSY_制造3-2课761HL");

        Assert.Equal("CN_ASSY_制造3-2课761HL", command.ProductionDepartmentCode);
    }

    [Fact(DisplayName = "O77 第三列为可选尾参：旧调用不传时为 null")]
    public void Command_ProductionDepartmentCode_DefaultsToNull()
    {
        var command = new MesDispatchCommand(
            "REF-2", "PI-2", "BJ_MACH", 999507, "MAT-2", 1m, "PCS", "BJ", null,
            new DateTime(2026, 11, 17, 8, 0, 0), new DateTime(2026, 11, 17, 9, 0, 0),
            new long[] { 2 });

        Assert.Null(command.ProductionDepartmentCode);
    }
}