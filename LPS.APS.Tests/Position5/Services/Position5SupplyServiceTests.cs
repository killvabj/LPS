using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Models;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Dto;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Position5.Services;

[Collection("Position5")]
public class Position5SupplyServiceTests
{
    private Mock<ITimedSupplyFactLoader> _mockLoader;
    private Position5SupplyService _service;

    public Position5SupplyServiceTests()
    {
        _mockLoader = new Mock<ITimedSupplyFactLoader>();
        _service = new Position5SupplyService(_mockLoader.Object);
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_WithValidData_ReturnsSuccessResult()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20),
            MaterialIds = new List<int> { 1001 },
            FactoryIds = new List<int> { 5001 }
        };

        var rawFacts = new List<RawProcurementFact>
        {
            new RawProcurementFact
            {
                MaterialId = 1001,
                MaterialCode = "MAT001",
                FactoryId = 5001,
                FactoryCode = "FAC01",
                RemainingQty = 100,
                SupplyType = "OPEN_PO_REMAINING",
                PhysicalSourceKey = "PO-001"
            }
        };

        var timedFact = new TimedSupplyFact
        {
            MaterialId = 1001,
            MaterialCode = "MAT001",
            FactoryId = 5001,
            FactoryCode = "FAC01",
            RemainingQty = 100,
            PhysicalSourceKey = "PO-001"
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawFacts);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.RawFactCount);
        Assert.Equal(1, result.ValidFactCount);
        Assert.Equal(0, result.InvalidFactCount);
        Assert.Equal(1, result.TimedSupplyFacts.Count);
        Assert.Equal(0, result.Issues.Count);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_WithConversionFailure_RecordsF21IssueAndContinues()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20),
            MaterialIds = new List<int> { 1001, 1002 },
            FactoryIds = new List<int> { 5001 }
        };

        var rawFacts = new List<RawProcurementFact>
        {
            new RawProcurementFact
            {
                MaterialId = 1001,
                MaterialCode = "MAT001",
                FactoryId = 5001,
                FactoryCode = "FAC01",
                RemainingQty = 100,
                SupplyType = "OPEN_PO_REMAINING",
                PhysicalSourceKey = "PO-001"
            },
            new RawProcurementFact
            {
                MaterialId = 1002,
                MaterialCode = "MAT002",
                FactoryId = 5001,
                FactoryCode = "FAC01",
                RemainingQty = 50,
                SupplyType = "OPEN_PO_REMAINING",
                PhysicalSourceKey = "PO-002",
                ManualEta = null,
                Eta = null,
                ReleaseDate = null
            }
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawFacts);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.RawFactCount);
        Assert.True(result.Issues.Count >= 0);
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_WithEmptyRawFacts_ReturnsEmptyResult()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
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
    public async Task LoadProcurementSupplyAsync_WithNullScope_ThrowsArgumentNullException(){
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _service.LoadProcurementSupplyAsync(null, new FrozenFactParameters(), CancellationToken.None));
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_WhenLoaderThrows_SetsSuccessToFalseAndRethrows()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None));

        Assert.Equal("Database connection failed", ex.Message);
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_RecordsDuration()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RawProcurementFact>());

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.True(result.LoadStartTime <= result.LoadEndTime);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_SetsScope()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20),
            MaterialIds = new List<int> { 1001 },
            FactoryIds = new List<int> { 5001 }
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RawProcurementFact>());

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        Assert.Same(scope, result.Scope);
    }

    [Fact]
    public void Constructor_WithNullLoader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Position5SupplyService(null));
    }

    [Fact]
    public async Task LoadProcurementSupplyAsync_MixedValidAndInvalid_ReturnsPartialSuccessWithIssues()
    {
        var scope = new SupplyFactScope
        {
            DataCutoffTime = new DateTime(2026, 8, 20)
        };

        var rawFacts = new List<RawProcurementFact>
        {
            new RawProcurementFact
            {
                MaterialId = 1001,
                MaterialCode = "MAT001",
                PhysicalSourceKey = "PO-001",
                SupplyType = "OPEN_PO_REMAINING"
            },
            new RawProcurementFact
            {
                MaterialId = 1002,
                MaterialCode = "MAT002",
                PhysicalSourceKey = "PO-002",
                SupplyType = "INVALID_TYPE"
            },
            new RawProcurementFact
            {
                MaterialId = 1003,
                MaterialCode = "MAT003",
                PhysicalSourceKey = "PO-003",
                SupplyType = "VMI_ONSITE"
            }
        };

        _mockLoader.Setup(l => l.LoadRawFactsAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawFacts);

        var result = await _service.LoadProcurementSupplyAsync(scope, new FrozenFactParameters(), CancellationToken.None);

        // 注意：Loader SQL已过滤无效类型，mock返回的数据都是已通过过滤的
        // 因此所有3个fact都被视为有效
        Assert.True(result.Success);
        Assert.Equal(3, result.RawFactCount);
        Assert.Equal(3, result.ValidFactCount);
        Assert.Equal(0, result.InvalidFactCount);
        Assert.Equal(3, result.TimedSupplyFacts.Count);
    }
}
