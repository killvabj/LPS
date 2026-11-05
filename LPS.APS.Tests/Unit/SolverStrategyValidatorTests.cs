using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// SolverStrategyValidator / CandidateGuardrailValidator 单元测试（阶段 E-4）
/// 纯内容校验：不依赖物理落点/DDL，直接对内存构造的 Block 断言合法性。
/// 设计意图：P0-02 裁决后接线到参数集发布路径时，本测试即发布前校验的验收证据；
/// 防止 On-time 越界、负拆分、Guardrail 时序倒挂等"隐形无效配置"越过发布门槛。
/// </summary>
public class SolverStrategyValidatorTests
{
    private readonly SolverStrategyValidator _validator = new();

    [Fact]
    public void E4_默认对象_校验通过()
    {
        // Arrange —— SolverStrategyBlock 默认值（Mode=Forward、TargetPercent=0、Split=3、Setup=30/5）
        var block = new SolverStrategyBlock();

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue(result.GetErrorMessage());
    }

    [Fact]
    public void E4_合法配置_校验通过()
    {
        // Arrange —— 冻结示例级合法配置（MIXED + On-time 95 + 拆分 3 + 换型 30min）
        var block = new SolverStrategyBlock
        {
            Mode = SolverStrategyMode.Mixed,
            OnTimeTarget = new OnTimeTargetParams { TargetPercent = 95, IsPrimaryObjective = true },
            Split = new SplitParams { MaxOptimizationSplitCount = 3, LimitMandatorySplit = false, MinBatchQty = 1 },
            Setup = new SetupParams(),
            StageOverlap = new StageOverlapParams { AllowOverlap = false, TransferBatchQty = 10, ThresholdQty = 5, ThresholdPercent = 20 }
        };

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue(result.GetErrorMessage());
    }

    [Fact]
    public void E4_Auto模式_校验通过()
    {
        // Arrange —— 0号位 2026-10-07 裁决：Mode=Auto 合法（1号位 按求解上下文决定最终方向）
        var block = new SolverStrategyBlock { Mode = SolverStrategyMode.Auto };

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue(result.GetErrorMessage());
    }

    [Fact]
    public void E4_OnTimeTargetPercent_超100_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.OnTimeTarget.TargetPercent = 101;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("TargetPercent"));
    }

    [Fact]
    public void E4_OnTimeTargetPercent_负值_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.OnTimeTarget.TargetPercent = -1;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("TargetPercent"));
    }

    [Fact]
    public void E4_SplitMaxOptimizationSplitCount_负值_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.Split.MaxOptimizationSplitCount = -1;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MaxOptimizationSplitCount"));
    }

    [Fact]
    public void E4_StageOverlapThresholdPercent_越界_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.StageOverlap.ThresholdPercent = 120;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ThresholdPercent"));
    }

    [Fact]
    public void E4_SolverStrategyMode_数字越界_拒绝()
    {
        // Arrange —— 数字越界反序列化场景（System.Text.Json 对数字枚举越界不抛异常，需校验器拦截）
        var block = new SolverStrategyBlock();
        block.Mode = (SolverStrategyMode)99;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SolverStrategyMode"));
    }

    [Fact]
    public void E4_BottleneckMode_数字越界_拒绝()
    {
        // Arrange —— 数字越界反序列化场景（4号位 2026-10-22 G1：越界 BottleneckMode 会静默按 Auto 执行，发布期须拦截）
        var block = new SolverStrategyBlock();
        block.BottleneckMode = (DynamicBottleneckMode)99;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("DynamicBottleneckMode"));
    }

    [Fact]
    public void E4_BottleneckMode_合法四值_校验通过()
    {
        // Arrange —— 合法四值逐一通过
        var values = new[]
        {
            DynamicBottleneckMode.Auto,
            DynamicBottleneckMode.PreferAnchor,
            DynamicBottleneckMode.ForceAnchor,
            DynamicBottleneckMode.NotAnchor
        };

        foreach (var mode in values)
        {
            // Act
            var result = _validator.Validate(new SolverStrategyBlock { BottleneckMode = mode });

            // Assert
            result.IsValid.Should().BeTrue($"BottleneckMode={mode} 应合法：{result.GetErrorMessage()}");
        }
    }

    [Fact]
    public void E4_SetupSearchBudget_低于100_拒绝()
    {
        // Arrange —— 有界搜索预算低于允许下限（1号位 2026-09-20 提值：[100, 5000]）
        var block = new SolverStrategyBlock();
        block.Setup.SetupSearchBudget = 99;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SetupSearchBudget"));
    }

    [Fact]
    public void E4_SetupSearchBudget_高于5000_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.Setup.SetupSearchBudget = 5001;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SetupSearchBudget"));
    }

    [Fact]
    public void E4_SetupMaxNeighborhoodTries_低于10_拒绝()
    {
        // Arrange —— 最大邻域尝试次数低于允许下限（[10, 500]）
        var block = new SolverStrategyBlock();
        block.Setup.SetupMaxNeighborhoodTries = 9;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SetupMaxNeighborhoodTries"));
    }

    [Fact]
    public void E4_SetupMaxNeighborhoodTries_高于500_拒绝()
    {
        // Arrange
        var block = new SolverStrategyBlock();
        block.Setup.SetupMaxNeighborhoodTries = 501;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SetupMaxNeighborhoodTries"));
    }
}

/// <summary>
/// CandidateGuardrailValidator 单元测试（阶段 E-4）
/// 覆盖：60/90/180 阈值时序关系（Normal &lt;= Soft &lt;= LocalHard）、各 Ms 为正、警戒百分比 0~100、计数非负。
/// </summary>
public class CandidateGuardrailValidatorTests
{
    private readonly CandidateGuardrailValidator _validator = new();

    [Fact]
    public void E4_默认对象_校验通过()
    {
        // Arrange —— 默认值 60/90/180（DTO 注释）
        var block = new CandidateGuardrailBlock();

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue(result.GetErrorMessage());
    }

    [Fact]
    public void E4_合法配置_校验通过()
    {
        // Arrange —— 冻结示例级合法配置
        var block = new CandidateGuardrailBlock
        {
            NormalMs = 60_000,
            SoftMs = 90_000,
            LocalHardMs = 180_000,
            ImpactedTaskWarningPercent = 30,
            MaxRepairAttempts = 5,
            MaxPropagationRounds = 10,
            ResourceTopN = 5,
            SplitAlternatives = 3,
            WarnOnlyOnMaxImpacted = true
        };

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeTrue(result.GetErrorMessage());
    }

    [Fact]
    public void E4_NormalMs_非正_拒绝()
    {
        // Arrange
        var block = new CandidateGuardrailBlock();
        block.NormalMs = 0;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("NormalMs"));
    }

    [Fact]
    public void E4_NormalMs大于SoftMs_拒绝()
    {
        // Arrange —— 时序倒挂：正常超时晚于软超时
        var block = new CandidateGuardrailBlock();
        block.NormalMs = 100_000;
        block.SoftMs = 90_000;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("NormalMs"));
    }

    [Fact]
    public void E4_SoftMs大于LocalHardMs_拒绝()
    {
        // Arrange —— 时序倒挂：软超时晚于硬超时
        var block = new CandidateGuardrailBlock();
        block.SoftMs = 200_000;
        block.LocalHardMs = 180_000;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SoftMs"));
    }

    [Fact]
    public void E4_ImpactedTaskWarningPercent_越界_拒绝()
    {
        // Arrange
        var block = new CandidateGuardrailBlock();
        block.ImpactedTaskWarningPercent = 101;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ImpactedTaskWarningPercent"));
    }

    [Fact]
    public void E4_MaxRepairAttempts_负值_拒绝()
    {
        // Arrange
        var block = new CandidateGuardrailBlock();
        block.MaxRepairAttempts = -1;

        // Act
        var result = _validator.Validate(block);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MaxRepairAttempts"));
    }
}

/// <summary>
/// Stage 固定交接等待规则校验单元测试（合同 v1.1 §五：纯 JSON 六项下限）
/// 覆盖六项校验 + H-3 缺配置语义（无源不阻断）+ 反向不拒绝/仅禁误命中。纯内容校验，零 DB。
/// </summary>
public class StageHandoffWaitRulesValidatorTests
{
    /// <summary>一条合法的有效规则（合成数据，非业务真值）</summary>
    private static StageHandoffWaitRule ValidRule() => new()
    {
        FactoryCode = "BJ",
        FromStageCode = "BJ_MACH",
        ToStageCode = "BJ_FINAL",
        WaitMinutes = 40,
        TimeBasis = StageHandoffTimeBasis.WORK,
        IsActive = true
    };

    [Fact]
    public void R05_空列表_校验通过()
    {
        // 缺配置（空 List / 键缺失反序列化为空）= 无源不阻断全 36 对（H-3）
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void R05_合法规则_校验通过()
    {
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([ValidRule()]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void R05_FactoryCode为空_拒绝()
    {
        var rule = ValidRule();
        rule.FactoryCode = " ";
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule]);
        errors.Should().Contain(e => e.Contains("FactoryCode"));
    }

    [Fact]
    public void R05_From等于To_拒绝()
    {
        var rule = ValidRule();
        rule.ToStageCode = rule.FromStageCode;
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule]);
        errors.Should().Contain(e => e.Contains("FromStageCode 必须 ≠ ToStageCode"));
    }

    [Fact]
    public void R05_WaitMinutes为负_拒绝()
    {
        var rule = ValidRule();
        rule.WaitMinutes = -1;
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule]);
        errors.Should().Contain(e => e.Contains("WaitMinutes 不能为负"));
    }

    [Fact]
    public void R05_TimeBasis枚举越界_拒绝()
    {
        var rule = ValidRule();
        rule.TimeBasis = (StageHandoffTimeBasis)99;
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule]);
        errors.Should().Contain(e => e.Contains("TimeBasis 必须为 WORK/ELAPSED"));
    }

    [Fact]
    public void R05_重复三键_拒绝()
    {
        var rule = ValidRule();
        var dup = ValidRule(); // 同 (Factory,From,To) 且 IsActive=true
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule, dup]);
        errors.Should().Contain(e => e.Contains("重复键"));
    }

    [Fact]
    public void R05_反向成对_互不拒绝()
    {
        // A→B 与 B→A 为两条独立可配置规则（真实双向合法）——反向不拒，仅禁误命中（v1.1 §二 修订1）
        var ab = ValidRule();
        var ba = new StageHandoffWaitRule
        {
            FactoryCode = "BJ",
            FromStageCode = ab.ToStageCode,
            ToStageCode = ab.FromStageCode,
            WaitMinutes = 50,
            TimeBasis = StageHandoffTimeBasis.ELAPSED,
            IsActive = true
        };
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([ab, ba]);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void R05_重复三键_仅IsActive命中()
    {
        // 重复键仅对 IsActive=true 判；IsActive=false（缺省）不参与
        var rule = ValidRule();
        var inactive = ValidRule();
        inactive.IsActive = null; // 缺省非有效规则，不参与重复键
        var errors = SolverStrategyValidator.ValidateStageHandoffWaitRules([rule, inactive]);
        errors.Should().BeEmpty();
    }

    // === 发布链枚举放开三分例（2026-10-29：2号位 通报 StageHandoffWaitRules 发布链同源缺陷、3号位 采甲方案处置） ===
    // 缺陷：发布链用通用 JsonOptions（无 JsonStringEnumConverter）反序列化，实库 TimeBasis 以字符串名（如 "ELAPSED"）存储 ⇒ 抛 JsonException→INVALID_JSON。
    // 处置（本应验证）：StageHandoffJsonOptions 单列 +JsonStringEnumConverter，收「字符串名 + 数值 0/1」两种写法，仅真正损坏才抛。

    /// <summary>字符串名写法（实库 PSV 2365 实际存储形态），应能被 甲方案 选项反序列化为 ELAPSED，不抛异常。</summary>
    [Fact]
    public void 发布链_TimeBasis字符串名_可反序列化()
    {
        var json = """[{"TimeBasis":"ELAPSED"}]""";
        var rules = System.Text.Json.JsonSerializer.Deserialize<List<StageHandoffWaitRule>>(json, GovernanceVersionService.StageHandoffJsonOptions);
        rules!.Should().ContainSingle(r => r.TimeBasis == StageHandoffTimeBasis.ELAPSED);
    }

    /// <summary>数值 0/1 写法（原通用 JsonOptions 唯一能收的形态），甲方案 选项仍兼容，不应回归。</summary>
    [Fact]
    public void 发布链_TimeBasis数值_可反序列化()
    {
        var json = """[{"TimeBasis":0}]""";
        var rules = System.Text.Json.JsonSerializer.Deserialize<List<StageHandoffWaitRule>>(json, GovernanceVersionService.StageHandoffJsonOptions);
        rules!.Should().ContainSingle(r => r.TimeBasis == StageHandoffTimeBasis.WORK);
    }

    /// <summary>真正损坏的枚举值，应抛 JsonException（发布链 catch → INVALID_JSON），与缺陷前行为一致。</summary>
    [Fact]
    public void 发布链_TimeBasis非法字符串_抛JsonException()
    {
        var json = """[{"TimeBasis":"BOGUS"}]""";
        Action act = () => System.Text.Json.JsonSerializer.Deserialize<List<StageHandoffWaitRule>>(json, GovernanceVersionService.StageHandoffJsonOptions);
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    // === 发布链 `[]` 零行 fail-closed（队列1，2026-10-29；与装载侧 STAGE_HANDOFF_WAIT_BLOCK_EMPTY 同批同词表） ===

    [Fact]
    public void 发布链_零行空数组_报BLOCK_EMPTY()
    {
        var result = new LPS.APS.Core.DTOs.Governance.PublishValidationResult();
        GovernanceVersionService.ValidateStageHandoffWaitRulesJson("[]", result);
        result.Errors.Should().Contain(e => e.Code == "STAGE_HANDOFF_WAIT_BLOCK_EMPTY");
    }

    [Fact]
    public void 发布链_缺省null或空白_放行不报BLOCK_EMPTY()
    {
        var result = new LPS.APS.Core.DTOs.Governance.PublishValidationResult();
        GovernanceVersionService.ValidateStageHandoffWaitRulesJson(null!, result);
        GovernanceVersionService.ValidateStageHandoffWaitRulesJson("   ", result);
        result.Errors.Should().NotContain(e => e.Code == "STAGE_HANDOFF_WAIT_BLOCK_EMPTY");
    }

    [Fact]
    public void 发布链_合法数组_放行不报BLOCK_EMPTY()
    {
        var result = new LPS.APS.Core.DTOs.Governance.PublishValidationResult();
        var json = """[{"FactoryCode":"BJ","FromStageCode":"S1","ToStageCode":"S2","WaitMinutes":10,"TimeBasis":"WORK","IsActive":true}]""";
        GovernanceVersionService.ValidateStageHandoffWaitRulesJson(json, result);
        result.Errors.Should().NotContain(e => e.Code == "STAGE_HANDOFF_WAIT_BLOCK_EMPTY");
    }
}
