namespace LPS.APS.Core.Constants;

/// <summary>
/// MES 下发幂等参考号——**两方唯一实现，禁止各自拼接**。
///
/// 契约出处：5号位 2026-11-09《FULL-07 出站通道答甲、接口签名定稿》§三（定稿）。
///   · 形态 `{PI}|{Stage}|{PV}`。
///   · 消费侧（我方落库段）与适配侧（5号位 `BusinessRules` HTTP 实现）**必须都调本函数**，防格式漂移。
///   · **免 DDL**：不新增列，按 `(PI, Stage, SourcePlanVersionId)` 组合键推导（其 §四）。
///
/// ⚠ 粒度：参考号的粒度是**组合**，不是行、也不是批。
///   · `TaskDispatch` 的 `PENDING` 段 `(PI, Stage, PV)` 组合**非行级唯一**：2026-11-12 12:2x 实测
///     150 个组合／364 行／204 个 `TaskNo` 批——多行是「**多批 × 多工序**」的必然形态，不是重复。
///   · 11-05 终稿 §2.1：**同一波次（PV）内，同组 N 个 `TaskNo` 归并 1 次调用、共用 1 个新工单号**
///     ⇒ 出站必须**按组合聚合、一组一次调用**；逐行调用会按行数造重复工单
///     （最大单组合 69 行 = 23 批 × 3 工序 ⇒ 69 张）。
///   · ⇒ 参考号**不含批次**；组量另算＝**组内各批批量之和**（既不是行级求和，也不是单批 MAX）。
/// </summary>
public static class MesDispatchReference
{
    /// <summary>按 `{PI}|{Stage}|{PV}` 生成幂等参考号。两方唯一实现。</summary>
    public static string Compute(string pi, string stage, int planVersionId)
        => $"{pi}|{stage}|{planVersionId}";
}