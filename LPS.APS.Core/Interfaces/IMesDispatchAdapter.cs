namespace LPS.APS.Core.Interfaces;

/// <summary>
/// C 桶（从无到有新建 MES 工单）的**唯一出站口**。
///
/// 契约出处：5号位 2026-11-09《FULL-07 出站通道答甲、接口签名定稿》§二/§三（定稿）。
///
/// 【分工】通道选**甲**：
///   · **接口声明归 2号位**（本文件）；
///   · **HTTP 实现归 5号位**（落 `LPS.APS.BusinessRules`，其库）；未配真实 MES 地址时返回
///     <see cref="MesDispatchKind.Unknown"/>（`NOT_CONFIGURED`），**不发外呼**；
///   · **消费环归 2号位**（`PENDING → PROCESSING → CREATED / FAILED` 的抢占与幂等重发，长在我方落库段）。
///
/// 【只覆盖"新建"】A 桶沿旧单、B 桶绑原号**不经本接口**——那两类在落库段直接写 `CREATED`。
/// 本接口**不做第二套资格判断**（资格归我方落库段）。
///
/// 【三态红线（其 §五）】
///   · `Accepted` → 消费侧落 `CREATED`（**仅 MES 确证工单号才落**）；
///   · `Rejected`  → 消费侧落 `FAILED` ＋ 九码（未枚举一律 `MES_REJECT_UNMAPPED` ＋原文）；
///   · `Unknown`   → **不落 `FAILED`、保持可重试**（超时/断连/5xx/`NOT_CONFIGURED` 一律归此，
///                   **实现侧禁止把传输层故障抛成异常**）。
/// </summary>
public interface IMesDispatchAdapter
{
    /// <summary>
    /// C 桶新建工单。**一次调用 = 一张新 MES 工单**，因此 <paramref name="command"/> 必须是
    /// **按 `(PI, Stage, PV)` 聚合后的组合**，不是单行。
    /// </summary>
    Task<MesDispatchResult> CreateWorkOrderAsync(MesDispatchCommand command, CancellationToken ct);
}

/// <summary>C 桶出站命令（组合粒度）。字段集由 5号位 2026-11-09 §三 定稿。</summary>
public sealed record MesDispatchCommand(
    string ReferenceNo,                     // 由 MesDispatchReference.Compute 生成
    string ProductionInstructionNo,
    string StageCode,                       // PENDING 段实测 100% 有值
    int SourcePlanVersionId,
    string MaterialCode,
    decimal Quantity,                       // 组合级"同 PV 定容"载体量——**不得行级求和**
    string Uom,
    string FactoryCode,
    string? ResourceCode,                   // 设备码；人工槽＝部门码＋槽码组合身份
    DateTime PlannedStartTime,              // 工厂墙钟 naive，无时区换算
    DateTime PlannedEndTime,
    IReadOnlyList<long> SourceTaskIds);     // 组合内成员行，仅作回写映射，实现侧不解读

/// <summary>出站结果三态。</summary>
public enum MesDispatchKind
{
    /// <summary>MES 已确证受理（返回工单号）。</summary>
    Accepted,
    /// <summary>MES 明确拒绝（返回拒绝码）。</summary>
    Rejected,
    /// <summary>无法判定（超时／断连／5xx／未配置端点）。**不得落 `FAILED`**。</summary>
    Unknown
}

/// <summary>出站结果。字段集由 5号位 2026-11-09 §三 定稿。</summary>
public sealed record MesDispatchResult(
    MesDispatchKind Kind,
    string? MesWorkOrderNo,                 // 仅 Accepted 有值
    string? FailureCode,                    // 仅 Rejected 有值；值域＝TaskDispatchCodes 九码
    string? FailureReason,
    string? RawResponse);