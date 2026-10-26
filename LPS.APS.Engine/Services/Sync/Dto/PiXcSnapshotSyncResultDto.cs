namespace LPS.APS.Engine.Services.Sync.Dto;

/// <summary>
/// sp_SyncPiXcSnapshot 执行结果
/// </summary>
public class PiXcSnapshotSyncResultDto
{
    /// <summary>批次号（PIXC_yyyyMMdd_HHmmss）</summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>本次落库的 XC 事实行数（PI × 物料 × 厂 × 仓）</summary>
    public int RowsAffected { get; set; }

    /// <summary>错误信息（null 表示成功）</summary>
    public string? ErrorMessage { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}
