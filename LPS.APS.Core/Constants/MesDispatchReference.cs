namespace LPS.APS.Core.Constants;

/// <summary>
/// MES 下发幂等参考号——**两方唯一实现，禁止各自拼接**。
///
/// 契约出处：5号位 2026-11-09《FULL-07 出站通道答甲、接口签名定稿》§三（定稿）。
///   · 形态 `{PI}|{Stage}|{PV}`。
///   · 消费侧（我方落库段）与适配侧（5号位 `BusinessRules` HTTP 实现）**必须都调本函数**，防格式漂移。
///   · **免 DDL**：不新增列，按 `(PI, Stage, SourcePlanVersionId)` 组合键推导（其 §四）。
///
/// ⚠ 粒度：参考号的粒度是**组合**，不是行。实测 `TaskDispatch` 的 `PENDING` 段
///   `(PI, Stage, PV)` 组合**非行级唯一**（52 个组合多行、最大单组合 69 行）
///   ⇒ 出站必须**按组合聚合、一组一次调用**；逐行调用会造出 69 张重复工单。
/// </summary>
public static class MesDispatchReference
{
    /// <summary>按 `{PI}|{Stage}|{PV}` 生成幂等参考号。两方唯一实现。</summary>
    public static string Compute(string pi, string stage, int planVersionId)
        => $"{pi}|{stage}|{planVersionId}";
}