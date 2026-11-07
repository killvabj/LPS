namespace LPS.APS.Core.Constants;

/// <summary>
/// `TaskDispatch` 下发行 · 受控词表（状态 ＋ 拒绝码）。
///
/// 来源：5号位 2026-11-06《回落码进度——两闸并行／幂等不靠事务／FailureCode 九码》§四；
/// 2号位 2026-11-07 回件 §四 收讫。**扩充走归一：新码先落 <see cref="MesRejectUnmapped"/> 攒样本再定**，
/// 不得在代码里就地新增码值。
/// </summary>
/// <remarks>开发者：2号位</remarks>
public static class TaskDispatchStatus
{
    /// <summary>待建单意图。**每轮先清后写**（`PeggingOrchestrator` 落库段），不是事实、不跨轮累积。</summary>
    public const string Pending = "PENDING";

    /// <summary>已被下发消费方抢占、正在建单。崩在「抢占」与「回写」之间时，下轮同号重发，MES 按参考号幂等返原单。</summary>
    public const string Processing = "PROCESSING";

    /// <summary>MES 工单已存在（A 桶继承旧号／B 桶绑定原号／C 桶建单成功回写）。**是事实，不得删**。</summary>
    public const string Created = "CREATED";

    /// <summary>MES **确定拒绝**。只有确定拒绝才落本态；结果未知一律保持重试（见 <see cref="TaskDispatchFailureCode.MesTransport"/>）。</summary>
    public const string Failed = "FAILED";
}

/// <summary>
/// `TaskDispatch.FailureCode` 九码受控词表（列宽 `nvarchar(100)`，本表每码均在限内）。
///
/// **两态语义是本词表的全部要点**：只有 MES **确定拒绝**才落 `FAILED`；
/// **超时／断连／5xx = 结果未知 ⇒ 保持重试**，绝不落 `FAILED`。
/// 把未知判成失败，会让人工去改一张**可能已经建成**的单，反而造出双单。
/// </summary>
/// <remarks>开发者：2号位</remarks>
public static class TaskDispatchFailureCode
{
    /// <summary>PI 不存在／已取消。落 `FAILED`。</summary>
    public const string InvalidPi = "INVALID_PI";

    /// <summary>超可开量（MES 回当前可开量）。落 `FAILED`。</summary>
    public const string QtyMismatch = "QTY_MISMATCH";

    /// <summary>MES 无该工艺路线／工单路径。落 `FAILED`。</summary>
    public const string RoutingNotFound = "ROUTING_NOT_FOUND";

    /// <summary>物料在 MES 不存在／未上线。落 `FAILED`。</summary>
    public const string MaterialNotFound = "MATERIAL_NOT_FOUND";

    /// <summary>机台／人工资源不可承接。落 `FAILED`。</summary>
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";

    /// <summary>PI／工单状态不允许建单。落 `FAILED`。</summary>
    public const string StateConflict = "STATE_CONFLICT";

    /// <summary>参考号冲突**且查无原单**。落 `FAILED`，并**即时上报＝契约破口**（正常路径下不该出现）。</summary>
    public const string DupReferenceConflict = "DUP_REFERENCE_CONFLICT";

    /// <summary>拒绝但码不在本表内。落 `FAILED`，`FailureReason` 存 MES 原文（列 `nvarchar(max)`，不截断）。</summary>
    public const string MesRejectUnmapped = "MES_REJECT_UNMAPPED";

    /// <summary>
    /// 超时／断连／5xx ＝ **结果未知**。
    /// ⚠ **不落 `FAILED`，保持重试**——这是本词表唯一不落失败态的码，
    /// 也是 A5 人工治理入口的硬约束：人工放行只对「确定拒绝」开放。
    /// </summary>
    public const string MesTransport = "MES_TRANSPORT";

    /// <summary>全部九码（供发布/落库前校验，防止就地新增未受控码值）。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        InvalidPi, QtyMismatch, RoutingNotFound, MaterialNotFound, ResourceNotFound,
        StateConflict, DupReferenceConflict, MesRejectUnmapped, MesTransport,
    };

    /// <summary>
    /// 该码是否代表「MES 确定拒绝」⇒ 可以落 `FAILED`。
    /// **`MES_TRANSPORT` 返回 false**：结果未知，必须保持重试。
    /// </summary>
    public static bool IsDeterministicRejection(string? code)
        => !string.IsNullOrEmpty(code)
           && All.Contains(code)
           && !string.Equals(code, MesTransport, StringComparison.Ordinal);
}
