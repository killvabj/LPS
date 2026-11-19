namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// C 桶下发消费环（2号位，FULL-07）。把 `TaskDispatch` 里的 `PENDING` 组合
/// 按 `(PI, Stage, SourcePlanVersionId)` **聚合**后交给 <see cref="Core.Interfaces.IMesDispatchAdapter"/>。
///
/// 契约出处：
///   · 5号位 2026-11-09《FULL-07 出站通道答甲、接口签名定稿》§四/§五/§六；
///   · 0号位《致2号位 FULL 核心主链直接整改任务书》P0-06。
///
/// 【粒度红线】`PENDING` 段 `(PI,Stage,PV)` 组合**非行级唯一**（实测 52 个组合多行、最大 69 行）
///   ⇒ **一组一次调用**，成功后**整组**回写同一 `MESWorkOrderNo`。逐行调用会造重复工单（违 §3.B.3）。
///
/// 【三态】（其 §五）
///   · `Accepted` → 整组 `CREATED` ＋ `MESWorkOrderNo`；
///   · `Rejected` → 整组 `FAILED` ＋ 九码 ＋ 原文；
///   · `Unknown`  → **整组回 `PENDING`**（保持可重试），写 `FailureReason`，**不落 `FAILED`**。
///
/// 【无适配器时】`IMesDispatchAdapter` 未注册（真实 MES 未接通）⇒ 视为 `NOT_CONFIGURED`，
///   整组回 `PENDING` 并记原因；**不外呼、不伪造成功**。
/// </summary>
public interface IMesDispatchDrainService
{
    /// <summary>消费指定 Run 下的 `PENDING` 组合。`maxGroups` 限制单次处理的组合数（防一次跑爆）。</summary>
    Task<MesDispatchDrainResult> DrainAsync(
        int scheduleRunId,
        int maxGroups = 50,
        string? workOrderType = null,
        CancellationToken ct = default);
}

/// <summary>一次消费的汇总（供验收与日志）。</summary>
public sealed class MesDispatchDrainResult
{
    public int GroupsSeen { get; init; }
    public int GroupsAccepted { get; init; }
    public int GroupsRejected { get; init; }
    public int GroupsUnknown { get; init; }
    public int RowsCreated { get; init; }
    public int RowsFailed { get; init; }
    public int RowsBackToPending { get; init; }
    public bool AdapterMissing { get; init; }

    /// <summary>
    /// 因**必填字段取不到**而跳过、且**保持 `PENDING`** 的组合数（2026-11-12，`O77`）。
    /// 判据：`factoryCode`（附录 A 必填）或 `materialCode`（附录 A 必填）任一为空。
    /// **为什么不发**：缺必填字段送出去只会拿到 MES 的拒收，且我方无从得知是数据缺还是接口错；
    /// 跳过并留 `PENDING` ＋ WARN 日志，等落库侧补齐后下一轮自然重试。
    /// </summary>
    public int GroupsSkippedMissingField { get; init; }
}