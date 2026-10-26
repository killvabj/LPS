using System.Data;
using System.Diagnostics;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services;

/// <summary>
/// 计算阶段进度记录器（运行可观测性，2号位）。
/// <para>
/// 解决的问题：长阶段（求解器 7 分钟、PI Position 逐行落库 91 秒、BFS 展开）内部一句话都不打，
/// 从外部看与「死机」完全一样；上线后也没人会去 tail 服务器日志。
/// </para>
/// <para>
/// 做法：每个阶段一行写进 <c>ComputeStageProgress</c>，由**后台心跳**每 5 秒刷新一次。
/// 计算路径（Begin/Report）只改内存，**不做任何数据库往返**，因此不会拖慢算法；
/// 即使算法正在死循环，心跳照常刷新 ⇒ 「在算」与「卡死」可凭心跳年龄区分。
/// </para>
/// <para>
/// 判读口径：有 STARTED 无 DONE ＋ 心跳在动 ⇒ 在算；心跳 ≥180 秒不动 ⇒ 卡死。
/// 表缺失时自动降级为「只写日志」，绝不因观测设施影响主流程。
/// </para>
/// <para>
/// 每轮刷库另在 `Detail` 尾部带一段「托管堆 / 工作集 / Gen2 次数」（<see cref="MemoryTag"/>），
/// 用于事后回答「内存是不是真有那么高」——托管堆≈RSS 是真实占用，托管堆≪RSS 是工作集未归还。
/// </para>
/// </summary>
public sealed class ComputeStageProgressMonitor : IAsyncDisposable
{
    private const string UpsertProc = "dbo.usp_RecordComputeStageProgress";
    // 心跳间隔：**同一阶段反复 UPDATE 同一行**，所以数据的行数＝阶段数，与跑多久无关。
    //   取 5 秒的依据：判「卡死」的红线是心跳 ≥180 秒不动，5 秒有 36 倍余量；
    //   再密只是把同一条 UPDATE 打得更勤，换不来判断力。
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    // 内存采样用：进程对象复用，避免每 5 秒 new 一个 Process。
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    /// <summary>当前异步流程的进度记录器。调用方在自身辖区按 <c>Begin</c> 打点即可，不必逐层传参。</summary>
    private static readonly AsyncLocal<ComputeStageProgressMonitor?> Ambient = new();

    public static ComputeStageProgressMonitor? Current => Ambient.Value;

    private readonly DatabaseConnectionManager _db;
    private readonly ILogger? _logger;
    private readonly string _runTag;
    private readonly long? _scheduleRunId;
    private readonly int _planVersionId;
    private readonly string? _domainKey;

    private readonly object _gate = new();
    private readonly List<StageState> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private StageState? _current;
    private int _seq;
    private bool _disabled;
    private bool _warnedDisabled;

    private ComputeStageProgressMonitor(
        DatabaseConnectionManager db,
        ILogger? logger,
        string runTag,
        long? scheduleRunId,
        int planVersionId,
        string? domainKey)
    {
        _db = db;
        _logger = logger;
        _runTag = runTag;
        _scheduleRunId = scheduleRunId;
        _planVersionId = planVersionId;
        _domainKey = domainKey;
        _loop = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>
    /// 开启一组进度记录。RunTag 优先用 ScheduleRunId；无 Run（单测/手工）时用时间戳短串。
    /// </summary>
    public static ComputeStageProgressMonitor Start(
        DatabaseConnectionManager db,
        ILogger? logger,
        int planVersionId,
        long? scheduleRunId,
        string? domainKey)
    {
        var runTag = scheduleRunId is > 0
            ? scheduleRunId.Value.ToString()
            : $"PV{planVersionId}-{DateTime.Now:yyyyMMddHHmmss}";
        var monitor = new ComputeStageProgressMonitor(db, logger, runTag, scheduleRunId, planVersionId, domainKey);
        Ambient.Value = monitor;
        return monitor;
    }

    /// <summary>
    /// 进入下一阶段：把当前阶段标为 DONE，并开始新阶段。**纯内存操作，不阻塞计算**。
    /// </summary>
    public void Begin(string stageName, long? total = null, string? detail = null)
    {
        if (_disabled) return;
        lock (_gate)
        {
            if (_current is { Status: "STARTED" } prev)
            {
                prev.Status = "DONE";
                prev.EndedAt = DateTime.Now;
                prev.ElapsedMs = (long)(prev.EndedAt.Value - prev.StartedAt).TotalMilliseconds;
            }

            _seq++;
            _current = new StageState
            {
                Seq = _seq,
                Name = stageName,
                Status = "STARTED",
                StartedAt = DateTime.Now,
                Processed = 0,
                Total = total,
                Detail = detail
            };
            _pending.Add(_current);
        }

        _logger?.LogInformation("[进度] 阶段 {Seq} 开始: {Stage}", _seq, stageName);
    }

    /// <summary>
    /// 补记一个**已经跑完**的阶段（现有埋点多在段末才拿到耗时）。**纯内存操作，不阻塞计算**。
    /// 写入后该行即为 DONE，不参与心跳；StartedAt 按时长倒推，故表里时间对齐。
    /// </summary>
    public void Completed(string stageName, long elapsedMs, long? processed = null, long? total = null, string? detail = null)
    {
        if (_disabled) return;
        var endedAt = DateTime.Now;
        lock (_gate)
        {
            _seq++;
            _pending.Add(new StageState
            {
                Seq = _seq,
                Name = stageName,
                Status = "DONE",
                StartedAt = endedAt.AddMilliseconds(-elapsedMs),
                EndedAt = endedAt,
                ElapsedMs = elapsedMs,
                Processed = processed,
                Total = total,
                Detail = Truncate(detail, 400)
            });
        }
    }

    /// <summary>上报处理进度（纯内存，可高频调用）。</summary>
    public void Report(long processed, long? total = null, string? detail = null)
    {
        if (_disabled) return;
        lock (_gate)
        {
            if (_current == null) return;
            _current.Processed = processed;
            if (total.HasValue) _current.Total = total;
            if (detail != null) _current.Detail = Truncate(detail, 400);
        }
    }

    /// <summary>把当前阶段标为失败（仍会落一行 FAILED，便于事后定位停在哪步）。</summary>
    public void Fail(string? reason)
    {
        lock (_gate)
        {
            if (_current is not { Status: "STARTED" }) return;
            _current.Status = "FAILED";
            _current.EndedAt = DateTime.Now;
            _current.ElapsedMs = (long)(_current.EndedAt.Value - _current.StartedAt).TotalMilliseconds;
            _current.Detail = Truncate(reason, 400);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await FlushAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>把待写阶段刷进库。单写者，故无需并发控制；异常一律吞掉并降级。</summary>
    private async Task FlushAsync(CancellationToken token)
    {
        if (_disabled) return;

        List<StageState> batch;
        lock (_gate)
        {
            batch = _pending.Select(s => s.Snapshot()).ToList();
        }
        if (batch.Count == 0) return;

        // 内存采样：每轮刷库取一次，拼在 `Detail` 尾部。用于事后判「托管堆 vs RSS」——
        //   托管堆≈RSS ⇒ 活对象真有这么高（设计问题）；托管堆≪RSS ⇒ 工作集未归还（假象）。
        //   取 `GetTotalMemory(false)`（不强制 GC）：含未回收对象，是**上界**口径，
        //   但不会在热路径触发 GC；给的是趋势与量级，不宣称绝对值。
        var memTag = MemoryTag();

        var flushed = new List<int>();
        foreach (var s in batch)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                await _db.ExecuteAsync(
                    UpsertProc,
                    new
                    {
                        RunTag = _runTag,
                        ScheduleRunId = _scheduleRunId,
                        PlanVersionId = _planVersionId,
                        DomainKey = _domainKey,
                        StageNo = s.Seq,
                        StageName = s.Name,
                        Status = s.Status,
                        ProcessedQty = s.Processed,
                        TotalQty = s.Total,
                        Detail = WithMemoryTag(s.Detail, memTag),
                        StartedAt = s.StartedAt,
                        ElapsedMs = s.ElapsedMs
                    },
                    commandType: CommandType.StoredProcedure,
                    db: DatabaseId.APS,
                    commandTimeout: 30).ConfigureAwait(false);
                if (s.Status != "STARTED") flushed.Add(s.Seq);
            }
            catch (Exception ex)
            {
                if (!_warnedDisabled)
                {
                    _warnedDisabled = true;
                    _logger?.LogWarning(
                        "[进度] 写入 ComputeStageProgress 失败，本次运行降级为仅日志（表/存储过程可能未部署）：{Message}",
                        ex.Message);
                    _disabled = true;
                }
                return;
            }
        }

        if (flushed.Count > 0)
        {
            lock (_gate)
            {
                _pending.RemoveAll(p => flushed.Contains(p.Seq));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* 收尾不抛 */ }

        lock (_gate)
        {
            if (_current is { Status: "STARTED" } cur)
            {
                cur.Status = "DONE";
                cur.EndedAt = DateTime.Now;
            }
        }
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);

        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = null;
    }

    private static string? Truncate(string? s, int max)
        => s == null || s.Length <= max ? s : s.Substring(0, max);

    /// <summary>
    /// 一行内存读数：托管堆 / 进程工作集 / Gen2 回收次数。
    /// <para>
    /// 托管堆取 <c>GC.GetTotalMemory(false)</c> —— 含未回收对象，是**上界**口径；
    /// 不用 <c>true</c>（强制 GC）是因为那会在计算热路径上触发回收，本末倒置。
    /// </para>
    /// <para>
    /// 判读：托管堆 ≈ RSS ⇒ 活对象真有这么高；托管堆 ≪ RSS ⇒ 工作集没归还 OS，属假象。
    /// </para>
    /// </summary>
    private static string MemoryTag()
    {
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024;
        var rssMb = CurrentProcess.WorkingSet64 / 1024 / 1024;
        return $"heap={heapMb}MB;rss={rssMb}MB;gc2={GC.CollectionCount(2)}";
    }

    /// <summary>把内存读数并进 `Detail` 尾部，总长仍受 400 字上限约束（与 <see cref="Truncate"/> 同一口径）。</summary>
    private static string? WithMemoryTag(string? detail, string memTag)
    {
        const int max = 400;
        if (string.IsNullOrEmpty(detail)) return Truncate(memTag, max);
        var room = max - memTag.Length - 1;
        return room <= 0 ? Truncate(memTag, max) : Truncate(detail, room) + "|" + memTag;
    }

    private sealed class StageState
    {
        public int Seq;
        public string Name = string.Empty;
        public string Status = "STARTED";
        public DateTime StartedAt;
        public DateTime? EndedAt;
        public long? ElapsedMs;
        public long? Processed;
        public long? Total;
        public string? Detail;

        public StageState Snapshot() => new()
        {
            Seq = Seq,
            Name = Name,
            Status = Status,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            ElapsedMs = ElapsedMs,
            Processed = Processed,
            Total = Total,
            Detail = Detail
        };
    }
}
