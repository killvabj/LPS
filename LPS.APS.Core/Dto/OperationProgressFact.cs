namespace LPS.APS.Core.Dto;

/// <summary>
/// 工序进度事实（2号位提供给5号位的输入数据）
///
/// 来源：OperationProgressSnapshot表
/// V1工序识别主字段：OperationName（MES工序名称）
/// </summary>
public sealed class OperationProgressFact
{
    /// <summary>
    /// 工序代码（MES工序编码，不跨大工艺稳定，仅作辅助）
    /// </summary>
    public string OperationCode { get; init; } = string.Empty;

    /// <summary>
    /// 工序名称（MES工序名称，V1工序识别主字段）
    /// </summary>
    public string OperationName { get; init; } = string.Empty;

    /// <summary>
    /// 所属Stage代码
    /// </summary>
    public string StageCode { get; init; } = string.Empty;

    /// <summary>
    /// 累计完成数量
    /// </summary>
    public decimal CumulativeCompletedQty { get; init; }

    /// <summary>
    /// 工序序号
    /// </summary>
    public int OperationSequence { get; init; }

    /// <summary>
    /// 数据来源快照ID
    /// </summary>
    public long? SnapshotId { get; init; }

    /// <summary>
    /// 数据更新时间
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}
