using System.Data;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using ParameterSetVersion = LPS.APS.Core.Entities.APS.ParameterSetVersion;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 0号位 裁决5 仓库引用校验（INVALID_WAREHOUSE_REF）单测聚焦：
/// 依据 4号位 2026-10-22 来文《参数集仓库编码引用校验死路》与 0号位 裁决5，
/// 校验错误须按 Supply / Procurement 落点报**正确的 FieldName**（来文指出恒写 Procurement/Warehouse 未区分）。
/// 覆盖：Supply 落点=Supply/Warehouse、Procurement 三条落点=Procurement/Warehouse、
///       字典内合法码不误报、空数组（方案乙泄压路径）跳过校验。
/// 依赖：DatabaseConnectionManager 为具体类、查询方法 virtual 可拦截（与 BomBatch/CtpEvaluate 同模式）。
/// </summary>
public class WarehouseReferenceValidationTests
{
    private static Mock<DatabaseConnectionManager> CreateConnectionMock(IReadOnlyList<string> dictionaryCodes)
    {
        var options = new DatabaseOptions
        {
            APS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Production;" },
            ODS = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=MES_Integration;" },
            Auth = new DatabaseConnectionOptions { ConnectionString = "Server=localhost;Database=APS_Auth;" },
        };
        var mock = new Mock<DatabaseConnectionManager>(Options.Create(options));
        mock.Setup(c => c.QueryAsync<string>(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(), It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .ReturnsAsync(dictionaryCodes.ToList());
        return mock;
    }

    private static IParameterSetVersionRepository ParamRepoReturning(ParameterSetVersion version)
    {
        var repo = new Mock<IParameterSetVersionRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);
        return repo.Object;
    }

    private static GovernanceVersionService CreateService(
        Mock<DatabaseConnectionManager> connection,
        ParameterSetVersion version)
    {
        return new GovernanceVersionService(
            Mock.Of<IRuleSetVersionRepository>(),
            ParamRepoReturning(version),
            Mock.Of<IStrategyProfileRepository>(),
            Mock.Of<IStrategyProfileVersionRepository>(),
            Mock.Of<IAuditLogRepository>(),
            connection.Object);
    }

    /// <summary>构造带 Supply/Procurement 仓库块的最小可发布校验对象（theme JSON 全空，快照即内容真相）。</summary>
    private static ParameterSetVersion DraftVersion(string contentSnapshotJson) => new()
    {
        Id = 1,
        Status = GovernanceVersionStatus.Draft,
        VersionCode = "V1",
        ContentSnapshotJson = contentSnapshotJson
    };

    private const string ContentWithSupplyAndProcurementMissing =
        """
        {
          "Supply": {
            "Inventory": { "IsEnabled": true, "WarehousePriority": ["WH_SUP_MISSING"] },
            "PiSort": { "SortBy": 0 }
          },
          "Procurement": {
            "WarehousePriority": ["WH_PROC_MISSING"],
            "DefaultPurchaseLt": [{ "WarehouseCode": "WH_LT_MISSING", "DefaultLtDays": 3.0 }],
            "ArrivalToUsableOffsets": [{ "WarehouseCode": "WH_OFF_MISSING", "OffsetHours": 4.0 }]
          }
        }
        """;

    private static IEnumerable<ValidationError> WarehouseErrors(PublishValidationResult result)
        => result.Errors.Where(e => e.Code == "INVALID_WAREHOUSE_REF");

    [Fact]
    public async Task SupplyOriginInvalid_Reports_FieldName_SupplyWarehouse()
    {
        var connection = CreateConnectionMock(["010398"]);
        var version = DraftVersion(ContentWithSupplyAndProcurementMissing);
        var service = CreateService(connection, version);

        var result = await service.ValidateParameterSetVersionForPublishAsync(version.Id, default);

        var supplyErr = WarehouseErrors(result).Single(e => e.Message.Contains("WH_SUP_MISSING"));
        supplyErr.FieldName.Should().Be("Supply/Warehouse");
        // Supply 落点不得误报到 Procurement
        supplyErr.FieldName.Should().NotBe("Procurement/Warehouse");
    }

    [Fact]
    public async Task ProcurementOriginInvalid_Reports_FieldName_ProcurementWarehouse()
    {
        var connection = CreateConnectionMock(["010398"]);
        var version = DraftVersion(ContentWithSupplyAndProcurementMissing);
        var service = CreateService(connection, version);

        var result = await service.ValidateParameterSetVersionForPublishAsync(version.Id, default);

        var proc = WarehouseErrors(result);
        // Procurement 三条落点（WarehousePriority / DefaultPurchaseLt / ArrivalToUsableOffsets）缺码全报 Procurement/Warehouse
        foreach (var code in new[] { "WH_PROC_MISSING", "WH_LT_MISSING", "WH_OFF_MISSING" })
        {
            proc.Single(e => e.Message.Contains(code)).FieldName.Should().Be("Procurement/Warehouse");
        }
    }

    [Fact]
    public async Task AllReferencedCodesInDictionary_NoWarehouseError()
    {
        var connection = CreateConnectionMock(["010398", "010399"]);
        var content =
            """
            {
              "Supply": {
                "Inventory": { "IsEnabled": true, "WarehousePriority": ["010398"] },
                "PiSort": { "SortBy": 0 }
              },
              "Procurement": {
                "WarehousePriority": ["010399"],
                "DefaultPurchaseLt": [{ "WarehouseCode": "010398", "DefaultLtDays": 3.0 }]
              }
            }
            """;
        var version = DraftVersion(content);
        var service = CreateService(connection, version);

        var result = await service.ValidateParameterSetVersionForPublishAsync(version.Id, default);

        WarehouseErrors(result).Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyWarehouseArrays_SkipValidation_方案乙泄压()
    {
        // 方案乙：干净可发布示例的仓库四落点置空数组（FrozenStrategySnapshotFixtureProvider 缺省），
        // referenced 采集为空 → 跳过仓库校验（referenced.Count==0 return），不阻断发布。
        var connection = CreateConnectionMock(["010398"]);
        var content =
            """
            {
              "Supply": {
                "Inventory": { "IsEnabled": true, "WarehousePriority": [] },
                "PiSort": { "SortBy": 0 }
              },
              "Procurement": {
                "WarehousePriority": [],
                "DefaultPurchaseLt": [],
                "ArrivalToUsableOffsets": []
              }
            }
            """;
        var version = DraftVersion(content);
        var service = CreateService(connection, version);

        var result = await service.ValidateParameterSetVersionForPublishAsync(version.Id, default);

        WarehouseErrors(result).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadyExampleParameterSetContent_PassesFullPublishValidation_方案乙种子白名单()
    {
        // 方案乙最终产出：新建「可发布干净示例参数集」的 ContentSnapshotJson 全量内容
        // （仓库四落点空数组 + 其余五块完整合法，供用户 fork 起点）。
        // 断言 IsValid=true：证明该内容**整包可通过 ValidateParameterSetVersionForPublishAsync**，
        // 作为种子 SQL 写入前的白名单——而非仅仓库校验通过。
        var connection = CreateConnectionMock(["010398"]);
        var content =
            """
            {"Lock":{"Trigger":{"UseRemainingTimeThreshold":true,"RemainingTimeThresholdHours":24.0,"ProtectDelayed":true,"ProtectVipTier":true,"VipTierValue":"VIP","ProtectedOrderTypes":["SO"],"AllowPmcManualProtection":true},"Sticky":{"RequireReleaseRecord":true,"ProtectUntilCompletion":true,"ProtectUntilSupplyInvalid":false}},"Supply":{"Inventory":{"IsEnabled":true,"WarehousePriority":[],"RequireFactoryContext":true,"RequireProductFamilyContext":false},"PiSort":{"SortBy":0,"UseStablePiNoTieBreak":true}},"Procurement":{"WarehousePriority":[],"DefaultPurchaseLt":[],"OverdueMargin":{"MarginPercent":0.10,"MinimumExtraDays":2},"ArrivalToUsableOffsets":[],"PlanningYields":[]},"SolverStrategy":{"Mode":0,"BottleneckMode":0,"OnTimeTarget":{"TargetPercent":95,"IsPrimaryObjective":true},"Split":{"MaxOptimizationSplitCount":3,"LimitMandatorySplit":false,"MinBatchQty":1.0},"Setup":{"SetupSearchBudget":500,"SetupMaxNeighborhoodTries":50},"StageOverlap":{"AllowOverlap":false,"TransferBatchQty":100.0,"ThresholdQty":50.0,"ThresholdPercent":0.5},"AllowMerge":false},"CandidateGuardrail":{"NormalMs":60000,"SoftMs":90000,"LocalHardMs":180000,"ImpactedTaskWarningPercent":30,"MaxRepairAttempts":5,"MaxPropagationRounds":10,"ResourceTopN":5,"SplitAlternatives":3,"WarnOnlyOnMaxImpacted":true}}
            """;
        var version = DraftVersion(content);
        var service = CreateService(connection, version);

        var result = await service.ValidateParameterSetVersionForPublishAsync(version.Id, default);

        result.IsValid.Should().BeTrue(
            "详细校验错误: " + string.Join(" | ", result.Errors.Select(e => $"{e.Code}:{e.Message}")));
    }
}