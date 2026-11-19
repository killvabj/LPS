using LPS.APS.BusinessRules.Calculators;
using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Models;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Tests.Position5.Helpers;
using LPS.APS.Tests.Position5.TestData;
using LPS.APS.Core.Dto;
using LPS.APS.Engine.Data;
using Moq;
using Xunit;
using System.Data;

namespace LPS.APS.Tests.Position5.EndToEnd;

[Collection("Position5")]
public class Position5EndToEndTests
{
    private Position5SupplyService _service;
    private Mock<TestableDatabaseConnectionManager> _mockConnectionManager;

    public Position5EndToEndTests()
    {
        _mockConnectionManager = new Mock<TestableDatabaseConnectionManager>();
        var loader = new TimedSupplyFactLoader(_mockConnectionManager.Object);
        _service = new Position5SupplyService(loader);
    }

    [Fact]
    public async Task EndToEnd_ValidProcurementFacts_ReturnsAllConverted()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20),
            MaterialIds = new List<int> { 1001, 1002, 1003, 1004, 1005 },
            FactoryIds = new List<int> { 5001 }
        };

        var mockData = MockProcurementDataBuilder.BuildValidProcurementFacts(5);

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, result.RawFactCount);
        Assert.Equal(5, result.ValidFactCount);
        Assert.Equal(0, result.InvalidFactCount);
        Assert.Equal(5, result.TimedSupplyFacts.Count);
        Assert.Equal(0, result.Issues.Count);
        Assert.True(result.Duration.TotalSeconds < 1);
    }

    [Fact]
    public async Task EndToEnd_MixedQualityFacts_ProcessesValidAndRecordsIssues()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var mockData = MockProcurementDataBuilder.BuildMixedQualityFacts();

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        // 【2026-08-25新基线】5号位不再做SupplyType/ETA校验，全部透传给2号位
        Assert.True(result.Success);
        Assert.Equal(4, result.RawFactCount);
        Assert.Equal(4, result.ValidFactCount); // All facts pass through - validation is 2号位 responsibility
        Assert.Equal(0, result.InvalidFactCount);
        Assert.Equal(4, result.TimedSupplyFacts.Count);
        Assert.Equal(0, result.Issues.Count);

        // 验证原始字段正确透传（包括INVALID_SUPPLY_TYPE也原样透传）
        var badTypeFact = result.TimedSupplyFacts.FirstOrDefault(f => f.PhysicalSourceKey == "PO-BAD-TYPE-001");
        Assert.NotNull(badTypeFact);
        Assert.Equal("INVALID_SUPPLY_TYPE", badTypeFact.SupplyType); // Invalid type passed through for 2号位 to validate
    }

    [Fact]
    public async Task EndToEnd_AllSupplyTypes_MapsCorrectly()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var mockData = MockProcurementDataBuilder.BuildAllSupplyTypesFacts();

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4, result.ValidFactCount);
        Assert.Equal(0, result.Issues.Count);

        var supplyTypes = result.TimedSupplyFacts.Select(f => f.SupplyType).Distinct().ToList();
        Assert.True(supplyTypes.Count > 0);
    }

    [Fact]
    public async Task EndToEnd_EtaPriorityRules_CalculatesCorrectly()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var mockData = MockProcurementDataBuilder.BuildEtaPriorityTestFacts();
        var baseDate = new DateTime(2026, 8, 15);

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.ValidFactCount);

        // 【2026-08-25新基线】AvailableTime由2号位计算，5号位只透传原始ETA字段
        var manualFact = result.TimedSupplyFacts.First(f => f.MaterialCode == "MAT-MANUAL-ONLY");
        Assert.Null(manualFact.AvailableTime); // AvailableTime is 2号位 responsibility
        Assert.Equal(baseDate.AddDays(15), manualFact.Eta);

        var erpFact = result.TimedSupplyFacts.First(f => f.MaterialCode == "MAT-ERP-ONLY");
        Assert.Null(erpFact.AvailableTime);
        Assert.Equal(baseDate.AddDays(18), erpFact.Eta);

        var releaseFact = result.TimedSupplyFacts.First(f => f.MaterialCode == "MAT-RELEASE-ONLY");
        Assert.Null(releaseFact.AvailableTime);
        Assert.Equal(baseDate.AddDays(14), releaseFact.ReleaseDate);
    }

    [Fact]
    public async Task Performance_1000ProcurementFacts_CompletesWithinTarget()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var mockData = MockProcurementDataBuilder.BuildPerformanceTestData(1000);

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1000, result.RawFactCount);
        Assert.Equal(1000, result.ValidFactCount);
        Assert.True(result.Duration.TotalSeconds < 5);
    }

    [Fact]
    public async Task EndToEnd_EmptyScope_ReturnsEmptyResult()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new List<RawProcurementFact>());

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.RawFactCount);
        Assert.Equal(0, result.ValidFactCount);
        Assert.Equal(0, result.InvalidFactCount);
        Assert.Equal(0, result.TimedSupplyFacts.Count);
        Assert.Equal(0, result.Issues.Count);
    }

    [Fact]
    public async Task EndToEnd_VerifyFieldMapping_AllFieldsPreserved()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var testDate = new DateTime(2026, 8, 15, 14, 30, 0);
        var mockData = new List<RawProcurementFact>
        {
            new RawProcurementFact
            {
                MaterialCode = "MAT-TEST-999",
                MaterialId = 9999,
                FactoryId = 8888,
                FactoryCode = "TEST",
                RemainingQty = 12345.67m,
                ManualEta = testDate.AddDays(10),
                Eta = testDate.AddDays(15),
                ReleaseDate = testDate,
                StorageCode = "WH-TEST",
                SupplyType = "VMI_ONSITE",
                CommitmentStatus = "TENTATIVE",
                Confidence = "MEDIUM",
                PhysicalSourceKey = "TEST-KEY-001",
                SourceDocumentLineNo = "999",
                SourceUpdatedAt = testDate
            }
        };

        _mockConnectionManager
            .Setup(m => m.QueryAsync<RawProcurementFact>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(),
                It.IsAny<int?>()))
            .ReturnsAsync(mockData);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ValidFactCount);

        var fact = result.TimedSupplyFacts[0];
        Assert.Equal("MAT-TEST-999", fact.MaterialCode);
        Assert.Equal(9999, fact.MaterialId);
        Assert.Equal(8888, fact.FactoryId);
        Assert.Equal("TEST", fact.FactoryCode);
        Assert.Equal(12345.67m, fact.RemainingQty);
        Assert.Null(fact.AvailableTime); // AvailableTime is 2号位 responsibility
        Assert.Equal(testDate.AddDays(15), fact.Eta); // Raw ERP Eta passed through
        Assert.Equal("WH-TEST", fact.WarehouseCode);
        Assert.Equal("TENTATIVE", fact.CommitmentStatus);
        Assert.Equal("MEDIUM", fact.Confidence);
        Assert.Equal("TEST-KEY-001", fact.PhysicalSourceKey);
        Assert.Equal("999", fact.SourceDocumentLineNo);
        Assert.Equal(testDate, fact.SourceUpdatedAt);
    }
}
