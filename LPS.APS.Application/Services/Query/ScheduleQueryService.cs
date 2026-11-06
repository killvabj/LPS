using Dapper;
using System.Data;
using LPS.APS.Application.Services.Query.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// 排程结果查询服务实现（**5号位**——头注原标「3号位」系历史署名，Owner 2026-10-19 裁定归属 5号位；
/// 消费方 CandidateController/Governance 等仍属 3号位，其页面数据语义变化照常知会）
///
/// 架构红线：
///   - 只读 APS_Production 库（已落盘数据），不触碰推演期内存沙盘
///   - 不包含任何排程/重排逻辑，纯查询投影
///   - Dapper 直连，避免 EF 跟踪开销（大结果集场景）
/// </summary>
public class ScheduleQueryService : IScheduleQueryService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDataScopeService _dataScopeService;
    private readonly ILogger<ScheduleQueryService> _logger;

    public ScheduleQueryService(
        DatabaseConnectionManager connectionManager,
        IDataScopeService dataScopeService,
        ILogger<ScheduleQueryService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanVersionSummaryDto>> GetVersionsAsync(
        int userId,
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        if (allowed is { Count: 0 })
            return Array.Empty<PlanVersionSummaryDto>();

        var sql = @"SELECT TOP (@Take)
                pv.Id, pv.VersionCode, pv.VersionCategory,
                pv.DomainKey,
                pv.PlanHorizonStart, pv.PlanHorizonEnd,
                pv.Status, pv.ComputedAt,
                pv.TotalTasks, pv.CreatedAt,
                pv.SourceScheduleRunId, pv.ActivatedAt,
                r.DataCutoffTime, r.RunType, r.TriggeredBy,
                CASE WHEN pv.VersionCategory = 'CANDIDATE' OR pv.Status = 'ARCHIVED'
                     THEN (
                         SELECT TOP 1 b.Id
                         FROM PlanVersion b
                         WHERE b.DomainKey = pv.DomainKey
                           AND b.Status = 'ARCHIVED'
                           AND b.ActivatedAt <= pv.CreatedAt
                         ORDER BY b.ActivatedAt DESC, b.Id DESC
                     )
                     ELSE NULL
                END AS BasePlanVersionId
              FROM PlanVersion pv
              LEFT JOIN ScheduleRun r ON r.Id = pv.SourceScheduleRunId   -- B10(11-05)：§77A第6项 DataCutoffTime/触发方式
              WHERE pv.ArchivedAt IS NULL";

        object parameters;
        if (allowed == null)
        {
            parameters = new { Take = take };
        }
        else
        {
            sql += " AND pv.DomainKey IN @ScopeDomainKeys";
            parameters = new { Take = take, ScopeDomainKeys = allowed.ToList() };
        }

        sql += " ORDER BY pv.CreatedAt DESC";

        var rows = await _connectionManager.QueryAsync<PlanVersionSummaryDto>(
            sql, parameters, db: DatabaseId.APS);

        return rows.ToList();
    }

    /// <inheritdoc />
    /// <summary>
    /// 窗口/资源过滤谓词——**明细计数、明细体、buckets 三处共用的单源**（Q1 收敛教训：口径修复必须单源，不抄第三份副本）。
    /// NULL 计划时间恒保留；`from/to` 为 naive 比较（列无时区，Z 在绑参时已抹平——桶条同源定稿 2026-10-30）。
    /// </summary>
    private const string GanttWindowPredicate = @"
                AND (@From IS NULL OR t.PlannedEndTime IS NULL OR t.PlannedEndTime >= @From)
                AND (@To IS NULL OR t.PlannedStartTime IS NULL OR t.PlannedStartTime <= @To)
                AND (@HasResources = 0 OR t.ResourceId IN @ResourceIds)";

    public async Task<GanttDataDto> GetGanttAsync(
        int userId,
        int planVersionId,
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<int>? resourceIds = null,
        int? limit = null,
        int? afterResourceId = null,
        DateTime? afterStartAt = null,
        long? afterTaskId = null,
        CancellationToken cancellationToken = default)
    {
        // 10-29 分页契约（4号位 三步走·第 1 步；复函定稿）：单次上限恒存在。
        // ⚠ 不传 limit ≠ 全量——默认 8,000；「裸调压百万」这条路径就此终结。
        const int DefaultLimit = 8000;
        const int MaxLimit = 20000;
        // 游标/排序的 NULL 时刻归一下界：SQL datetime 下限 1753-01-01（实库无更早日期，语义与 0001 等同）
        var sqlDateFloor = new DateTime(1753, 1, 1);
        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);
        var hasResources = resourceIds is { Count: > 0 };
        // 11-06 排序改时间序（B 案裁决）：游标=二元组(归一start, taskId)。
        // afterResourceId **保留入参但被忽略**——旧前端原样回传三参不报错（软兼容，不要求前后端同批上线）；
        // 有 afterTaskId 即视为翻页（afterStartAt 缺省按 1753 归一下界）。
        var hasCursor = afterTaskId.HasValue;

        // 1. 版本信息（含 DomainKey 做范围校验；同时取 Status / BasePlanVersionId 供前端区分版本类型）
        var version = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string VersionCode, string DomainKey, DateTime PlanHorizonStart, DateTime PlanHorizonEnd, string Status, int? BasePlanVersionId)>(
            @"SELECT pv.Id, pv.VersionCode, pv.DomainKey, pv.PlanHorizonStart, pv.PlanHorizonEnd,
                     pv.Status,
                     CASE WHEN pv.VersionCategory = 'CANDIDATE' OR pv.Status = 'ARCHIVED'
                          THEN (
                              SELECT TOP 1 b.Id
                              FROM PlanVersion b
                              WHERE b.DomainKey = pv.DomainKey
                                AND b.Status = 'ARCHIVED'
                                AND b.ActivatedAt <= pv.CreatedAt
                              ORDER BY b.ActivatedAt DESC, b.Id DESC
                          )
                          ELSE NULL
                     END AS BasePlanVersionId
              FROM PlanVersion pv WHERE pv.Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);

        if (version.Id == 0)
        {
            // 版本不存在：VersionExists=false（显式位，不靠 versionCode 空串隐式判断）
            return new GanttDataDto { PlanVersionId = planVersionId, VersionExists = false, IsEmpty = true };
        }

        if (!IsDomainAllowed(allowed, version.DomainKey))
        {
            // 越权（fail-closed）：ScopeDenied=true，前端据此显示"无权限"而非"无数据"
            return new GanttDataDto { PlanVersionId = planVersionId, VersionExists = true, ScopeDenied = true, IsEmpty = true };
        }

        // 2. 资源行（仅返回该版本里被 Task 使用到的资源，减少前端噪声）
        //    F-G4：资源 DomainKey 不再以 FACTORY_{id} 命名规则推导，改用 PlanVersion.DomainKey（版本唯一权威域）
        //    factoryName / productionDepartmentName / stage 由域主数据 JOIN 投影
        var resources = await _connectionManager.QueryAsync<GanttResourceDto>(
            $@"SELECT DISTINCT
                r.Id AS ResourceId,
                r.ResourceCode,
                r.ResourceName,
                r.FactoryId,
                r.ProductionDepartmentId,
                @DomainKey AS DomainKey,
                f.Name AS FactoryName,
                pd.DeptName AS ProductionDepartmentName,
                pd.StageCode AS Stage
              FROM Resource r
              INNER JOIN [Task] t ON t.ResourceId = r.Id
              LEFT JOIN Factory f ON f.Id = r.FactoryId
              LEFT JOIN ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
              WHERE t.PlanVersionId = @Id
                -- 与任务条同口径过滤：资源行只保留过滤后仍可见的任务所用资源
                AND (@From IS NULL OR t.PlannedEndTime IS NULL OR t.PlannedEndTime >= @From)
                AND (@To IS NULL OR t.PlannedStartTime IS NULL OR t.PlannedStartTime <= @To)
                AND (@HasResources = 0 OR t.ResourceId IN @ResourceIds){DepartmentPredicate("t")}
              -- 10-31 4号位 缺陷修复：资源行排序必须与任务游标同一把尺（资源 Id 升序）。
              -- 前端照抄 resources[] 顺序摆泳道、任务流按游标序 → 两序不一致时首屏泳道全空。
              -- INNER JOIN 保证本清单无 NULL ResourceId，故与游标键 ISNULL(t.ResourceId,-1) 完全等价；
              -- 桶体本来就是尺②（ORDER BY g.ResourceId），改后同页三层（泳道/任务/负荷带）同序。
              ORDER BY ResourceId",
            WithDepartmentScope(new
            {
                Id = planVersionId,
                DomainKey = version.DomainKey,
                From = from,
                To = to,
                HasResources = hasResources,
                ResourceIds = resourceIds
            }, departmentCodes),
            db: DatabaseId.APS);

        // 3. 任务条
        //    G2-b 档①：DomainKey = COALESCE(Order.DomainKey, PlanVersion.DomainKey)
        //              netQty = Task.Quantity（落盘即净合格产出 NetOutputQty）
        //              plannedProcessQty 取 Task.PlannedProcessQty（DDL 08:4319，2号位已落盘）；
        //              mesEligible / crossDomainBlocked 仍无落盘来源，保持保守默认
        //              （4号位 2026-10-17 R2/R3/R4/R6 已落实；R5 DelayReasons 待事实粒度对齐）
        // 3b. 过滤口径总数（同谓词 COUNT、不带游标）——分页器/护栏用；
        //     实测 121 万行域 COUNT 毫秒级（IX_Task_Query 首列 PlanVersionId）。
        var totalCount = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            $@"SELECT COUNT(*)
                FROM [Task] t
                WHERE t.PlanVersionId = @Id{GanttWindowPredicate}{DepartmentPredicate("t")}",
            WithDepartmentScope(new
            {
                Id = planVersionId,
                From = from,
                To = to,
                HasResources = hasResources,
                ResourceIds = resourceIds
            }, departmentCodes),
            db: DatabaseId.APS);

        var tasks = await _connectionManager.QueryAsync<GanttTaskDto>(
            $@"SELECT TOP (@Fetch)
                t.Id                AS TaskId,
                t.TaskNo,
                t.OrderId,
                o.OrderNo,
                t.MaterialId,
                m.MaterialCode,
                m.MaterialName,
                t.ResourceId,
                t.OperationCode,
                t.OperationSeq,
                t.Quantity,
                t.UOM,
                t.PlannedStartTime,
                t.PlannedEndTime,
                t.Status,
                t.SetupSource                 AS SetupSource,
                t.MESWorkOrderNo              AS MESWorkOrderNo,   -- B2①(11-05) 07:3687 透传；JSON 键=mESWorkOrderNo
                t.ProductionDepartmentId      AS ProductionDepartmentId,
                pdn.DeptName                  AS DepartmentName,
                t.StageCode                   AS StageCode,
                t.TaskType                    AS TaskType,
                t.RouteCode                   AS RouteCode,
                t.PathId                      AS PathId,
                t.ManualSlotId                AS ManualSlotId,
                t.MTS_InstructionNo           AS MTS_InstructionNo,
                t.StageExecutionBatchDraftKey AS StageExecutionBatchKey,
                t.StageExecutionBatchQty      AS StageExecutionBatchQty,
                CAST(NULL AS BIT)             AS IsCriticalPath,   -- B5(11-05) 未判定：列恒写死false，不拿假false冒充判定（真值归1号位，已标转办）
                -- Duration 库值单位=小时，与冻结 07 字段说明 `:3696`「单位：小时」一致（v5.1.16 `:3703` 同位同文）。
                --   落库口径（2号位 PeggingOrchestrator，`Duration-2` 2026-10-26 起）：
                --   命中＝`(EffectiveProcessMinutes + SetupTime) / 60`（工时，不含跨日历中断）；
                --   缺值＝回落 `PlannedEnd-Start` 的 TotalMinutes / 60（**不再加 Setup**，跨度本就含）。
                --   ⇒ 该列是「工时」而非墙钟跨度；跨窗 Task 的值会比 PlannedEnd-Start 小（少了中断段）。
                -- 读取侧**不得再 /60**（0号位 2026-10-26 正式回复 §:60 明确要求）。由前端按小时展示（4号位 R6/Q3）。
                t.Duration                    AS Duration,
                o.CustomerDueDate             AS CustomerDueDate,
                -- 延期判定按【日期】比较：CustomerDueDate 为 DATE，PlannedEndTime 为 DATETIME2，
                -- 直接比较会隐式提升为「完工时刻 vs 交日零点」，导致交期当天完工被判延期（4号位 Q1）。
                CAST(CASE
                    WHEN t.PlannedEndTime IS NOT NULL
                     AND o.CustomerDueDate IS NOT NULL
                     AND {DelayedOnDate("t.PlannedEndTime", "o.CustomerDueDate")}
                    THEN 1 ELSE 0
                END AS BIT) AS IsDelayed,
                CAST(CASE
                    WHEN t.PlannedEndTime IS NOT NULL
                     AND o.CustomerDueDate IS NOT NULL
                     AND {DelayedOnDate("t.PlannedEndTime", "o.CustomerDueDate")}
                    THEN DATEDIFF(MINUTE, CAST(o.CustomerDueDate AS datetime2), t.PlannedEndTime) / 60.0
                    ELSE NULL
                END AS DECIMAL(18,4)) AS DelayHours,
                COALESCE(o.DomainKey, pv.DomainKey) AS DomainKey,
                t.Quantity          AS NetQty,
                t.PlannedProcessQty AS PlannedProcessQty,
                CAST(NULL AS BIT)   AS MesEligible,   -- 未判定（资格判定未接入）；不是表列，纯占位。改可空以区分「判定为否」
                CAST(NULL AS NVARCHAR(4000)) AS MesIneligibleReasons,
                CAST(NULL AS BIT)   AS CrossDomainBlocked,   -- 未判定；不是表列，纯占位。改可空以区分「判定为否」
                CAST(NULL AS NVARCHAR(4000)) AS CrossDomainBlockReason
              FROM [Task] t
              LEFT JOIN [Order] o ON o.Id = t.OrderId
              LEFT JOIN Material m ON m.Id = t.MaterialId
              LEFT JOIN ProductionDepartment pdn ON pdn.Id = t.ProductionDepartmentId
              LEFT JOIN PlanVersion pv ON pv.Id = @Id
              WHERE t.PlanVersionId = @Id{GanttWindowPredicate}{DepartmentPredicate("t")}
                -- 11-06 B 案：全序键=（归一计划开始时刻, TaskId）——人工/设备按时间穿插，首屏即见双视图
                -- （实测 44,702 无资源任务全部=人工槽、真未分配=0，「垫底/置顶 -1 桶」任一固定法都会把一边埋没）；
                -- 未排(NULL 开始)归一 1753-01-01 排最前，同 buckets 未排桶行口径；游标谓词与排序键同式。
                -- ⚠ 游标时刻必须 CONVERT(datetime2(7),字符串)：Dapper 把 DateTime 参数绑成 SQL datetime
                -- （百纳秒被毫秒舍入），等值比较会失配、页边界重复/漏行（11-06 E2E 抓到并修复）
                AND (@HasCursor = 0
                     OR ISNULL(t.PlannedStartTime, '17530101') > CONVERT(datetime2(7), @CurStartS, 121)
                     OR (ISNULL(t.PlannedStartTime, '17530101') = CONVERT(datetime2(7), @CurStartS, 121) AND t.Id > @CurTask))
              ORDER BY ISNULL(t.PlannedStartTime, '17530101'), t.Id",
            WithDepartmentScope(new
            {
                Id = planVersionId,
                From = from,
                To = to,
                HasResources = hasResources,
                ResourceIds = resourceIds,
                Fetch = effectiveLimit + 1,          // 多取1行判溢出：hasMore 零额外查询、永不撒谎
                HasCursor = hasCursor,
                CurStartS = (afterStartAt ?? sqlDateFloor).ToString("yyyy-MM-dd HH:mm:ss.fffffff"),   // Dapper 将 DateTime 默认绑定为 SQL datetime（下限1753），MinValue 会 SqlTypeException 溢出
                CurTask = afterTaskId ?? 0L
            }, departmentCodes),
            db: DatabaseId.APS);

        // 溢出裁剪：SQL 多取了 1 行——>limit 即还有下一页，裁掉探针行并回游标
        var fetched = tasks.ToList();
        var hasMore = fetched.Count > effectiveLimit;
        var taskList = hasMore ? fetched.Take(effectiveLimit).ToList() : fetched;
        var last = taskList.Count > 0 ? taskList[^1] : null;

        // 3c. 页内富化（2026-10-31 字段饥饿函 A2/A9）：
        //   taskShare  ← AllocationTaskShare（真实数据，IX_ATS_Task 可 seek）
        //   delayReasons ← ScheduleExplanationFact（ObjectType='TASK'；**当前全库 TASK 级事实=0**，写方 2号位 尚未产出，
        //                  接线即生效、有值即透出，不伪造）
        // 传参用 STRING_SPLIT 单参（页内最多 limit 个 TaskId，IN 参数化会撞 SQL Server 2100 上限）
        if (taskList.Count > 0)
        {
            var pageIds = string.Join(",", taskList.Select(t => t.TaskId));

            // 显式 HASH JOIN（实测 1.3s→0.12s）：IN(子查询) 会让 8,000 个 id 逐个 seek，改成等值连接强制一次扫描
            var shareRows = await _connectionManager.QueryAsync<GanttShareRow>(
                @"SELECT s.TaskId, s.RootOrderId AS OrderId, o.OrderNo, s.ShareQty
                    FROM (SELECT CAST(value AS bigint) AS TaskId FROM STRING_SPLIT(@Ids, ',')) v
                    JOIN AllocationTaskShare s ON s.TaskId = v.TaskId
                    LEFT JOIN [Order] o ON o.Id = s.RootOrderId
                    ORDER BY s.TaskId, s.AllocationSequence
                    OPTION (HASH JOIN)",
                new { Ids = pageIds }, db: DatabaseId.APS);

            var dispatchRows = await _connectionManager.QueryAsync<TaskDispatchRow>(
                @"SELECT d.SourceTaskId, d.DispatchStatus
                    FROM (SELECT CAST(value AS bigint) AS TaskId FROM STRING_SPLIT(@Ids, ',')) v
                    JOIN TaskDispatch d ON d.SourceTaskId = v.TaskId
                    OPTION (RECOMPILE)",
                new { Ids = pageIds },
                CommandType.Text, DatabaseId.APS, commandTimeout: 30);
            var dispatchById = dispatchRows
                .GroupBy(d => d.SourceTaskId).ToDictionary(g => g.Key, g => g.Last().DispatchStatus);

            var sharesByTask = shareRows
                .GroupBy(s => s.TaskId)
                .ToDictionary(g => g.Key, g =>
                {
                    // 占比按同任务内份额归一分母（ShareQty/ΣShareQty），不除以 Task 数量——
                    // 实库存在"单份额 50 / 任务 100"形态，除任务量会得出误导性的 0.5
                    var sum = g.Sum(x => x.ShareQty);
                    return (IReadOnlyList<GanttTaskShareDto>)g.Select(x => new GanttTaskShareDto
                    {
                        OrderId = x.OrderId,
                        OrderNo = x.OrderNo,
                        ShareQty = x.ShareQty,
                        ShareRatio = sum == 0 ? 0 : x.ShareQty / sum
                    }).ToList();
                });

            var reasonRows = await _connectionManager.QueryAsync<GanttReasonRow>(
                @"SELECT f.TaskId, f.ReasonCode, f.Severity, f.ImpactHours, f.ObjectType
                    FROM (SELECT CAST(value AS bigint) AS TaskId FROM STRING_SPLIT(@Ids, ',')) v
                    JOIN ScheduleExplanationFact f ON f.TaskId = v.TaskId
                    -- 11-05 Q2-a 裁定：放宽为「挂在 Task 上的事实」= TASK 级 ∪ ORDER 级带 TaskId
                    -- （2号位现况把任务号顺挂在 ORDER 级事实：ORDER 589 行含 11 行 TaskId）。
                    -- 这不是新造第二套事实——同表同行原样透传，仅把行级事实层级 objectType 如实带给前端标注；
                    -- F1(01:1927) 的「主要原因」本就是订单语义口径，订单级原因挂任务位不越义。DOMAIN 级无 TaskId 天然不入。
                    WHERE f.ObjectType IN ('TASK', 'ORDER')
                    ORDER BY f.TaskId, f.Severity, f.ReasonCode
                    OPTION (HASH JOIN)",
                new { Ids = pageIds }, db: DatabaseId.APS);

            // IssueCategory/Description/RelatedObjectRef 三键在 ScheduleExplanationFact 无载体（只存在于 EvidenceJson），
            // 本期不给（缺键），不以空串占位（4号位 字段饥饿函 A9）。
            var reasonsByTask = reasonRows
                .GroupBy(r => r.TaskId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<GanttDelayReasonDto>)g.Select(x => new GanttDelayReasonDto
                {
                    ReasonCode = x.ReasonCode,
                    Severity = x.Severity,
                    ImpactHours = x.ImpactHours,
                    ObjectType = x.ObjectType   // 11-05 Q2：'TASK'/'ORDER'，前端据此标注层级
                }).ToList());

            foreach (var task in taskList)
            {
                if (sharesByTask.TryGetValue(task.TaskId, out var shares)) task.TaskShare = shares;
                if (reasonsByTask.TryGetValue(task.TaskId, out var reasons)) task.DelayReasons = reasons;
                if (dispatchById.TryGetValue(task.TaskId, out var ds)) task.DispatchStatus = ds;
            }
        }

        var resourceList = resources.ToList();

        // B1(11-05)：资源不可用窗口——口径与总览 R6 完全同源（本级 ResourceCalendarSlot AvailableFlag=0，
            // 不含部门/工厂继承 OFF；10-16 已裁口径 b 未采用，两页保持一套，合并口径若需另提）。
            // 窗口取请求 [from,to]（缺省回落版本 PlanHorizon 由资源查询同源，这里用请求值/为空则全版本）。
            var resIds = resourceList.Where(r => r.ResourceId > 0).Select(r => r.ResourceId).ToList();
            if (resIds.Count > 0)
            {
                var offRows = (await _connectionManager.QueryAsync<ResourceOffWindowRow>(
                    @"SELECT rc.ResourceId, rc.StartTime AS [From], rc.EndTime AS [To], rc.Remark AS Reason
                        FROM ResourceCalendarSlot rc
                       WHERE rc.AvailableFlag = 0
                         AND rc.ResourceId IN @ResIds
                         AND (@From IS NULL OR rc.EndTime > @From)
                         AND (@To IS NULL OR rc.StartTime < @To)
                       ORDER BY rc.ResourceId, rc.StartTime",
                    new { ResIds = resIds, From = from, To = to },
                    CommandType.Text, DatabaseId.APS, commandTimeout: 30)).ToList();
                var offByRes = offRows.GroupBy(w => w.ResourceId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var res in resourceList)
                {
                    if (!offByRes.TryGetValue(res.ResourceId, out var wins) || wins.Count == 0) continue;
                    res.UnavailableWindows = wins.Select(w => new GanttUnavailableWindowDto
                    {
                        From = w.From, To = w.To, Reason = w.Reason,
                        ImpactedTaskIds = taskList
                            .Where(x => x.ResourceId == res.ResourceId
                                     && x.PlannedStartTime is not null
                                     && x.PlannedStartTime < w.To
                                     && (x.PlannedEndTime ?? x.PlannedStartTime) > w.From)
                            .Select(x => x.TaskId).ToList()
                    }).ToList();
                }
            }


        return new GanttDataDto
        {
            PlanVersionId      = version.Id,
            VersionCode        = version.VersionCode,
            PlanVersionStatus  = version.Status,
            BasePlanVersionId  = version.BasePlanVersionId,
            PlanHorizonStart   = version.PlanHorizonStart,
            PlanHorizonEnd     = version.PlanHorizonEnd,
            Resources          = resourceList,
            Tasks              = taskList,
            ScopeDenied        = false,
            VersionExists      = true,
            // 10-29 分页契约：limit 回显 / 过滤口径总数 / 溢出标志 / 游标（hasMore 时必给）
            Limit              = effectiveLimit,
            TotalTasks         = totalCount,
            HasMore            = hasMore,
            NextAfter          = hasMore && last is not null
                ? new GanttCursorDto
                {
                    ResourceId = last.ResourceId ?? -1,
                    PlannedStartTime = last.PlannedStartTime ?? sqlDateFloor,
                    TaskId = last.TaskId
                }
                : null,
            // 空态标志：区分"该版本（在过滤条件下）确实没有任务"
            IsEmpty            = taskList.Count == 0 && resourceList.Count == 0
        };
    }

    /// <summary>
    /// 甘特「资源×时间桶」聚合（三步走·第 2 步；口径=2026-10-30 复函九条定稿）。
    /// 桶条同源：共用 <see cref="GanttWindowPredicate"/>、naive 比较不转时区；
    /// 钳制归桶（起点早于窗头归首桶）+ 未排桶（bucketStart=null）⇒ **Σ taskCount ≡ totalTasks 恒等**。
    /// 降级三闸：稀疏输出 → >50,000 行 day→week→month → 月桶仍超抛 400（真 400 走全局 Filter，同明细体惯例）。
    /// </summary>
    public async Task<GanttBucketsDto> GetGanttBucketsAsync(
        int userId,
        int planVersionId,
        DateTime? from,
        DateTime? to,
        string? bucket,
        IReadOnlyList<int>? resourceIds,
        CancellationToken cancellationToken = default)
    {
        const int MaxBucketRows = 50000;

        // 入参校验（400 走全局 Filter，同 resourceIds[] 拦截惯例）：from/to 必填、bucket 值域
        if (from is null || to is null)
            throw new InvalidOperationException("buckets 端点要求 from 与 to 均必填（naive 比较，不传时区换算）");
        var requested = (bucket ?? "day").ToLowerInvariant();
        if (requested is not ("day" or "week" or "month"))
            throw new InvalidOperationException($"bucket 仅支持 day|week|month，收到：{bucket}");

        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);
        var hasResources = resourceIds is { Count: > 0 };

        var version = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string VersionCode, string DomainKey)>(
            "SELECT pv.Id, pv.VersionCode, pv.DomainKey FROM PlanVersion pv WHERE pv.Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);

        if (version.Id == 0)
            return new GanttBucketsDto { PlanVersionId = planVersionId, VersionExists = false, IsEmpty = true,
                                         Bucket = requested, BucketWidth = requested };
        if (!IsDomainAllowed(allowed, version.DomainKey))
            return new GanttBucketsDto { PlanVersionId = planVersionId, VersionCode = version.VersionCode,
                                         VersionExists = true, ScopeDenied = true, IsEmpty = true,
                                         Bucket = requested, BucketWidth = requested };

        var totalTasks = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            $@"SELECT COUNT(*)
                FROM [Task] t
                WHERE t.PlanVersionId = @Id{GanttWindowPredicate}{DepartmentPredicate("t")}",
            WithDepartmentScope(
                new { Id = planVersionId, From = from, To = to, HasResources = hasResources, ResourceIds = resourceIds },
                departmentCodes),
            db: DatabaseId.APS);

        // 桶表达式：未排（start NULL）→ 独立行(null)；否则**钳制** GREATEST(start,from) 后按自然日历截断
        // 周锚=周一（DATEDIFF 以 1900-01-01 周一为基，与 DATEFIRST 无关）
        static string BucketExpr(string unit, string clamped) => unit switch
        {
            "day" => $"DATEADD(day, DATEDIFF(day, 0, {clamped}), 0)",
            "week" => $"DATEADD(week, DATEDIFF(week, 0, {clamped}), 0)",
            _ => $"DATEADD(month, DATEDIFF(month, 0, {clamped}), 0)",
        };
        var clamped = "(CASE WHEN t.PlannedStartTime < @From THEN @From ELSE t.PlannedStartTime END)";

        IReadOnlyList<GanttBucketRowDto> rows;
        var effective = requested;
        while (true)
        {
            var bucketStartExpr = $"CASE WHEN t.PlannedStartTime IS NULL THEN CAST(NULL AS datetime2) ELSE {BucketExpr(effective, clamped)} END";
            rows = (await _connectionManager.QueryAsync<GanttBucketRowDto>(
                $@"SELECT g.ResourceId,
                          r.ResourceCode,
                          r.ResourceName,
                          f.Name  AS FactoryName,
                          pd.DeptName AS ProductionDepartmentName,
                          pd.StageCode AS Stage,
                          @DomainKey AS DomainKey,
                          g.BucketStart,
                          g.TaskCount,
                          g.LoadHours,
                          g.FirstStart,
                          g.LastEnd
                     FROM (
                         SELECT ISNULL(t.ResourceId, -1) AS ResourceId,
                                {bucketStartExpr} AS BucketStart,
                                COUNT(*)              AS TaskCount,
                                -- Duration 库值已是小时 ⇒ 直接求和，不再 /60
                                SUM(t.Duration)       AS LoadHours,
                                MIN(t.PlannedStartTime) AS FirstStart,
                                MAX(t.PlannedEndTime)   AS LastEnd
                         FROM [Task] t
                         WHERE t.PlanVersionId = @Id{GanttWindowPredicate}{DepartmentPredicate("t")}
                         GROUP BY ISNULL(t.ResourceId, -1),
                                  {bucketStartExpr}
                     ) g
                     LEFT JOIN Resource r ON r.Id = g.ResourceId
                     LEFT JOIN Factory f ON f.Id = r.FactoryId
                     LEFT JOIN ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
                     ORDER BY g.ResourceId, g.BucketStart",
                WithDepartmentScope(
                    new { Id = planVersionId, From = from, To = to, HasResources = hasResources,
                          ResourceIds = resourceIds, DomainKey = version.DomainKey },
                    departmentCodes),
                db: DatabaseId.APS)).ToList();

            if (rows.Count <= MaxBucketRows || effective == "month") break;
            effective = effective == "day" ? "week" : "month";   // ②降级链 day→week→month
        }

        if (rows.Count > MaxBucketRows)
            throw new InvalidOperationException(
                $"桶行数 {rows.Count} 仍超上限 {MaxBucketRows}（月桶口径），请用 resourceIds 收窄资源范围后重试");

        return new GanttBucketsDto
        {
            PlanVersionId      = version.Id,
            VersionCode        = version.VersionCode,
            VersionExists      = true,
            ScopeDenied        = false,
            IsEmpty            = rows.Count == 0,
            Bucket             = requested,
            BucketWidth        = effective,
            BucketDowngraded   = effective != requested,
            From               = System.DateTime.SpecifyKind(from.Value, DateTimeKind.Unspecified),  // 回显 naive（无 Z），与明细同款
            To                 = System.DateTime.SpecifyKind(to.Value, DateTimeKind.Unspecified),
            TotalTasks         = totalTasks,
            Items              = rows
        };
    }

    /// <inheritdoc />
    public async Task<ScheduleSummaryDto> GetSummaryAsync(
        int userId,
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);

        var versionKey = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string DomainKey)>(
            "SELECT Id, DomainKey FROM PlanVersion WHERE Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);

        if (versionKey.Id == 0)
        {
            // 版本不存在：VersionExists=false，调用方可与"存在但无排程结果"区分（4号位 10-17 §六）
            return new ScheduleSummaryDto { PlanVersionId = planVersionId, VersionExists = false };
        }

        if (!IsDomainAllowed(allowed, versionKey.DomainKey))
        {
            // 越权（fail-closed）：ScopeDenied=true
            return new ScheduleSummaryDto { PlanVersionId = planVersionId, VersionExists = true, ScopeDenied = true };
        }

        var summary = await _connectionManager.QueryFirstOrDefaultAsync<ScheduleSummaryDto>(
            $@"SELECT
                pv.Id                AS PlanVersionId,
                pv.VersionCode,
                pv.Status,
                -- ComputeDurationSeconds 不再取 pv.DurationSeconds：该列只存在于 ScheduleRun，
                -- PlanVersion 无此列（冻结 08 DDL:4020 仅 ScheduleRun.DurationSeconds）。
                -- 原 SQL 引用不存在列 → 端点必然 500（4号位 10-10/10-17 两次报障）。
                -- DTO 字段保留为可空，契约不变；如需「求解耗时」请另走 ScheduleRun 维度端点。

                (SELECT COUNT(*) FROM [Task] t
                    WHERE t.PlanVersionId = @Id{DepartmentPredicate("t")})
                    AS TotalTasks,
                (SELECT COUNT(*) FROM [Task] t
                    WHERE t.PlanVersionId = @Id AND t.PlannedStartTime IS NOT NULL{DepartmentPredicate("t")})
                    AS ScheduledTasks,
                (SELECT COUNT(*) FROM [Task] t
                    WHERE t.PlanVersionId = @Id AND t.PlannedStartTime IS NULL{DepartmentPredicate("t")})
                    AS UnscheduledTasks,
                (SELECT COUNT(*) FROM [Task] t
                    INNER JOIN [Order] o ON o.Id = t.OrderId
                    WHERE t.PlanVersionId = @Id
                      AND t.PlannedEndTime IS NOT NULL
                      AND o.CustomerDueDate IS NOT NULL
                      AND {DelayedOnDate("t.PlannedEndTime", "o.CustomerDueDate")}{DepartmentPredicate("t")})
                    AS DelayedTasks,   -- 10-19 补 Q1 日期判据（原裸 datetime=191，与甘特体 179 分叉 12 条）

                -- ⚠ TotalOrders 是**版本级事实**，不随部门范围收窄（订单本身不带部门）；其余计数均 Task 派生
                (SELECT COUNT(*) FROM [Order] WHERE PlanVersionId = @Id)
                    AS TotalOrders,
                (SELECT COUNT(DISTINCT o.Id) FROM [Order] o
                    INNER JOIN [Task] t ON t.OrderId = o.Id
                    WHERE o.PlanVersionId = @Id
                      AND t.PlannedEndTime IS NOT NULL
                      AND o.CustomerDueDate IS NOT NULL
                      AND {DelayedOnDate("t.PlannedEndTime", "o.CustomerDueDate")}{DepartmentPredicate("t")})
                    AS DelayedOrders,   -- 同上一并补（v2 恰好两口径同值=1，换版本即分叉）

                (SELECT MIN(t.PlannedStartTime) FROM [Task] t
                    WHERE t.PlanVersionId = @Id{DepartmentPredicate("t")})
                    AS FirstTaskStart,
                (SELECT MAX(t.PlannedEndTime) FROM [Task] t
                    WHERE t.PlanVersionId = @Id{DepartmentPredicate("t")})
                    AS LastTaskEnd
              FROM PlanVersion pv
              WHERE pv.Id = @Id",
            WithDepartmentScope(new { Id = planVersionId }, departmentCodes),
            db: DatabaseId.APS);

        if (summary is null)
            return new ScheduleSummaryDto { PlanVersionId = planVersionId, VersionExists = false };

        // 版本存在且未越权：标 VersionExists=true（计数可能全 0 = 该版本确无排程结果，与"版本不存在"可区分）
        summary.VersionExists = true;
        summary.ScopeDenied = false;
        return summary;
    }

    /// <inheritdoc />
    public async Task<CandidateComparisonDto> GetCandidateComparisonAsync(
        int userId,
        int candidatePlanVersionId,
        int basePlanVersionId,
        CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);

        // 1. 双版本概要 + 存在性/状态校验（缺失 → 404；候选非 CANDIDATE → 400）
        var versions = (await _connectionManager.QueryAsync<PlanVersionBriefDto>(
            @"SELECT Id AS PlanVersionId, VersionCode, VersionCategory, Status, ComputedAt
              FROM PlanVersion
              WHERE Id IN (@CandidateId, @BaseId)",
            new { CandidateId = candidatePlanVersionId, BaseId = basePlanVersionId },
            db: DatabaseId.APS)).ToList();

        var candidateSummary = versions.FirstOrDefault(v => v.PlanVersionId == candidatePlanVersionId)
            ?? throw new KeyNotFoundException($"候选计划版本不存在：{candidatePlanVersionId}");
        var baseSummary = versions.FirstOrDefault(v => v.PlanVersionId == basePlanVersionId)
            ?? throw new KeyNotFoundException($"基础计划版本不存在：{basePlanVersionId}");

        if (!string.Equals(candidateSummary.VersionCategory, "CANDIDATE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"候选版本身份非 CANDIDATE（当前：{candidateSummary.VersionCategory}）");
        }

        // F-G4：业务范围校验（越权视为不存在 → 404，不泄露版本存在性）
        var domainKeys = (await _connectionManager.QueryAsync<(int Id, string DomainKey)>(
            "SELECT Id, DomainKey FROM PlanVersion WHERE Id IN (@CandidateId, @BaseId)",
            new { CandidateId = candidatePlanVersionId, BaseId = basePlanVersionId },
            db: DatabaseId.APS)).ToList();
        if (domainKeys.Count < 2 || !domainKeys.All(d => IsDomainAllowed(allowed, d.DomainKey)))
        {
            throw new KeyNotFoundException("候选或基础计划版本不存在或超出当前用户业务范围");
        }

        var parameters = WithDepartmentScope(
            new { CandidateId = candidatePlanVersionId, BaseId = basePlanVersionId }, departmentCodes);

        // 2. 任务变化汇总（主键差集：OrderId+MaterialId+OperationSeq+OperationCode+RouteCode+PathId）
        //    ⚠ 部门范围必须**内外一视同仁**（0号位 2026-10-31 R11-3b 裁决）：
        //    内层"另一版本有没有同键任务"的存在性判定，直接决定外层 NOT EXISTS 成不成立，
        //    因此它本身就是可见行计数的一部分 —— 只滤主侧别名会让权限外任务把可见增/减吞掉
        //    （同键任务 A部门→B部门 时，顶层看到 0/0，等于泄漏"另一部门有该任务"）。
        //    @ScopeHasDepartments = 0 时谓词整体短路 ⇒ 未配部门范围的用户行为不变。
        var added = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM [Task] t_c
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND NOT EXISTS (
                   SELECT 1 FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")})",
            parameters);

        var removed = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM [Task] t_b
               WHERE t_b.PlanVersionId = @BaseId{DepartmentPredicate("t_b")}
                 AND NOT EXISTS (
                   SELECT 1 FROM [Task] t_c
                   WHERE t_c.PlanVersionId = @CandidateId{TaskKeyMatchFragment}{DepartmentPredicate("t_c")})",
            parameters);

        var timeShifted = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM [Task] t_c
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND EXISTS (
                   SELECT 1 FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")}
                     AND {NullSafeNeq("t_c", "t_b", "PlannedStartTime")}
                     AND {NullSafeNeq("t_c", "t_b", "PlannedEndTime")})",
            parameters);

        var resourceChanged = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM [Task] t_c
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND EXISTS (
                   SELECT 1 FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")}
                     AND {NullSafeNeq("t_c", "t_b", "ResourceId")})",
            parameters);

        var impactedOrders = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM (
                 SELECT DISTINCT OrderId FROM (
                   SELECT t_c.OrderId
                   FROM [Task] t_c
                   WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                     AND (
                       NOT EXISTS (
                         SELECT 1 FROM [Task] t_b
                         WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")})
                       OR EXISTS (
                         SELECT 1 FROM [Task] t_b
                         WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")}
                           AND {NullSafeNeq("t_c", "t_b", "PlannedStartTime")}
                           AND {NullSafeNeq("t_c", "t_b", "PlannedEndTime")}
                           AND {NullSafeNeq("t_c", "t_b", "ResourceId")})
                     )
                   UNION
                   SELECT t_b.OrderId
                   FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{DepartmentPredicate("t_b")}
                     AND NOT EXISTS (
                       SELECT 1 FROM [Task] t_c
                       WHERE t_c.PlanVersionId = @CandidateId{TaskKeyMatchFragment}{DepartmentPredicate("t_c")})
                 ) changed
               ) cnt",
            parameters);

        var newDelays = await CountScalarAsync(
            $@"SELECT COUNT(*)
               FROM [Task] t_c
               INNER JOIN [Order] o ON o.Id = t_c.OrderId
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND t_c.PlannedEndTime IS NOT NULL
                 AND o.CustomerDueDate IS NOT NULL
                 AND {DelayedOnDate("t_c.PlannedEndTime", "o.CustomerDueDate")}
                 AND NOT EXISTS (
                   SELECT 1
                   FROM [Task] t_b
                   INNER JOIN [Order] ob ON ob.Id = t_b.OrderId
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")}
                     AND t_b.PlannedEndTime IS NOT NULL
                     AND ob.CustomerDueDate IS NOT NULL
                     AND {DelayedOnDate("t_b.PlannedEndTime", "ob.CustomerDueDate")})",
            parameters);

        // 3. 汇总组装（crossDomain 计数未落盘，保守返 0；estimatedOnlyCount 归 2号位 返 null —— v1.2 §三 3.3 / §九-b）
        return new CandidateComparisonDto
        {
            TaskChangeSummary = new TaskChangeSummaryDto
            {
                Added = added,
                Removed = removed,
                TimeShifted = timeShifted,
                ResourceChanged = resourceChanged,
                CrossDomainBlocked = 0
            },
            ImpactSummary = new ImpactSummaryDto
            {
                ImpactedOrderCount = impactedOrders,
                NewDelayCount = newDelays,
                CrossDomainImpacted = 0,
                EstimatedOnlyCount = null
            },
            BaseSummary = baseSummary,
            CandidateSummary = candidateSummary
        };
    }

    /// <inheritdoc />
    public async Task<CandidateSummaryDto> GetCandidateSummaryAsync(
        int userId,
        int candidateRunId,
        CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);

        var run = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string Status)>(
            "SELECT Id, Status FROM ScheduleRun WHERE Id = @Id",
            new { Id = candidateRunId },
            db: DatabaseId.APS);

        if (run.Id == 0)
            throw new KeyNotFoundException($"候选运行不存在：{candidateRunId}");

        var pv = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string DomainKey, string CreatedBy, DateTime CreatedAt)>(
            @"SELECT Id, DomainKey, CreatedBy, CreatedAt
              FROM PlanVersion
              WHERE SourceScheduleRunId = @RunId AND VersionCategory = 'CANDIDATE'",
            new { RunId = candidateRunId },
            db: DatabaseId.APS);

        if (pv.Id == 0 || !IsDomainAllowed(allowed, pv.DomainKey))
            throw new KeyNotFoundException($"候选运行不存在或超出当前用户业务范围：{candidateRunId}");

        var counts = await _connectionManager.QueryFirstOrDefaultAsync<(int OrderCount, int TaskCount)>(
            $@"SELECT
                -- 订单数是版本级事实，不随部门范围收窄；任务数按可见 Task 集合计
                (SELECT COUNT(*) FROM [Order] WHERE PlanVersionId = @PvId) AS OrderCount,
                (SELECT COUNT(*) FROM [Task] t WHERE t.PlanVersionId = @PvId{DepartmentPredicate("t")}) AS TaskCount",
            WithDepartmentScope(new { PvId = pv.Id }, departmentCodes),
            db: DatabaseId.APS);

        return new CandidateSummaryDto
        {
            CandidateId = candidateRunId,
            RunStatus = run.Status,
            OrderCount = counts.OrderCount,
            TaskCount = counts.TaskCount,
            CreatedBy = pv.CreatedBy,
            CreatedAt = pv.CreatedAt
        };
    }

    /// <inheritdoc />
    public async Task<CandidateDiffDto> GetCandidateDiffAsync(
        int userId,
        int candidateRunId,
        CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAllowedDomainKeysAsync(userId, cancellationToken);
        var departmentCodes = await ResolveAllowedDepartmentCodesAsync(userId, cancellationToken);

        var run = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, int? BasePlanVersionId)>(
            "SELECT Id, BasePlanVersionId FROM ScheduleRun WHERE Id = @Id",
            new { Id = candidateRunId },
            db: DatabaseId.APS);

        if (run.Id == 0)
            throw new KeyNotFoundException($"候选运行不存在：{candidateRunId}");

        var pv = await _connectionManager.QueryFirstOrDefaultAsync<(int Id, string DomainKey)>(
            "SELECT Id, DomainKey FROM PlanVersion WHERE SourceScheduleRunId = @RunId AND VersionCategory = 'CANDIDATE'",
            new { RunId = candidateRunId },
            db: DatabaseId.APS);

        if (pv.Id == 0 || !IsDomainAllowed(allowed, pv.DomainKey))
            throw new KeyNotFoundException($"候选运行不存在或超出当前用户业务范围：{candidateRunId}");

        if (run.BasePlanVersionId is not int baseId)
            throw new InvalidOperationException($"候选运行 {candidateRunId} 无基础计划版本（BasePlanVersionId 为空）");

        var parameters = WithDepartmentScope(new { CandidateId = pv.Id, BaseId = baseId }, departmentCodes);

        var addedOrders = (await _connectionManager.QueryAsync<int?>(
            $@"SELECT DISTINCT t_c.OrderId
               FROM [Task] t_c
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND NOT EXISTS (
                   SELECT 1 FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")})",
            parameters, db: DatabaseId.APS))
            .Where(x => x.HasValue).Select(x => x!.Value).ToList();

        var removedOrders = (await _connectionManager.QueryAsync<int?>(
            $@"SELECT DISTINCT t_b.OrderId
               FROM [Task] t_b
               WHERE t_b.PlanVersionId = @BaseId{DepartmentPredicate("t_b")}
                 AND NOT EXISTS (
                   SELECT 1 FROM [Task] t_c
                   WHERE t_c.PlanVersionId = @CandidateId{TaskKeyMatchFragment}{DepartmentPredicate("t_c")})",
            parameters, db: DatabaseId.APS))
            .Where(x => x.HasValue).Select(x => x!.Value).ToList();

        var changedTasks = (await _connectionManager.QueryAsync<ChangedTaskDto>(
            $@"SELECT
                 t_c.OrderId, t_c.MaterialId, t_c.OperationSeq, t_c.OperationCode,
                 t_c.RouteCode, t_c.PathId, t_c.ResourceId,
                 t_c.PlannedStartTime, t_c.PlannedEndTime
               FROM [Task] t_c
               WHERE t_c.PlanVersionId = @CandidateId{DepartmentPredicate("t_c")}
                 AND EXISTS (
                   SELECT 1 FROM [Task] t_b
                   WHERE t_b.PlanVersionId = @BaseId{TaskKeyMatchFragment}{DepartmentPredicate("t_b")}
                     AND {NullSafeNeq("t_c", "t_b", "PlannedStartTime")}
                     AND {NullSafeNeq("t_c", "t_b", "PlannedEndTime")}
                     AND {NullSafeNeq("t_c", "t_b", "ResourceId")})",
            parameters, db: DatabaseId.APS)).ToList();

        return new CandidateDiffDto
        {
            BasePlanVersionId = baseId,
            CandidatePlanVersionId = pv.Id,
            AddedOrders = addedOrders,
            RemovedOrders = removedOrders,
            ChangedTasks = changedTasks
        };
    }

    /// <summary>跨版本 Task 自然主键匹配片段（H1 差集判定：OrderId+MaterialId+OperationSeq+OperationCode+RouteCode+PathId）</summary>
    private const string TaskKeyMatchFragment = @"
        AND t_b.OrderId       = t_c.OrderId
        AND t_b.MaterialId    = t_c.MaterialId
        AND t_b.OperationSeq  = t_c.OperationSeq
        AND t_b.OperationCode = t_c.OperationCode
        AND t_b.RouteCode     = t_c.RouteCode
        AND t_b.PathId        = t_c.PathId";

    /// <summary>
    /// 跨版本「NULL 安全不相等」片段：一空一非空，或两者均非空且值不等；两边都为空 ⇒ 不命中。
    ///
    /// ⚠ 不能用 `(a IS NULL) &lt;&gt; (b IS NULL)` 的直白写法 —— T-SQL **不允许把两个布尔谓词相互比较**，
    ///    该写法在 SQL Server 上是语法错误（报「'&lt;' 附近有语法错误」），整条语句无法编译执行。
    ///    这里必须先把 NULL 与否落成 0/1 整数再比。
    /// </summary>
    private static string NullSafeNeq(string leftAlias, string rightAlias, string column)
        => $@"(
                       (CASE WHEN {leftAlias}.{column} IS NULL THEN 1 ELSE 0 END)
                         <> (CASE WHEN {rightAlias}.{column} IS NULL THEN 1 ELSE 0 END)
                       OR {leftAlias}.{column} <> {rightAlias}.{column})";

    /// <summary>
    /// 解析当前用户有效范围并映射为可落 SQL 的 DomainKey 约束（F-G4）。
    /// 返回 null=全局放行；空集合=拒绝全部；否则 DomainKey ∈ 集合。
    /// 仅 Domain 维度可无损映射到 PlanVersion.DomainKey（DomainDefinition 为唯一权威映射源）。
    /// Factory / ProductFamily / Department 等维度不得靠命名规则（如 FACTORY_{id}）推导权威 DomainKey
    /// （0号位 2026-09-07 F-G4 裁决：Business Scope ≠ Domain Scope 别名）。
    /// 仅授权这些维度而无 Domain 授权时按拒绝处理（fail-closed 安全默认）；
    /// 其正式查询闭环随普通查询迁 5号位 后由 5→2 依据 DomainDefinition / ProductionDepartment 真值落地。
    /// </summary>
    private async Task<IReadOnlySet<string>?> ResolveAllowedDomainKeysAsync(int userId, CancellationToken cancellationToken)
        => ResolveAllowedDomainKeys(await _dataScopeService.ResolveScopeAsync(userId, cancellationToken));

    private static IReadOnlySet<string>? ResolveAllowedDomainKeys(DataScopeContext scope)
    {
        if (scope.IsGlobal)
            return null;
        if (scope.IsEmpty)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var domain = scope.GetValues(DataScopeTypes.Domain);
        if (domain is { Count: > 0 })
            return new HashSet<string>(domain, StringComparer.OrdinalIgnoreCase);

        // Factory / ProductFamily / Department 无法直接映射为权威 DomainKey（F-G4）——拒绝全部（fail-closed）
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDomainAllowed(IReadOnlySet<string>? allowed, string domainKey)
        => allowed == null || allowed.Contains(domainKey);

    /// <summary>
    /// 部门维度范围解析（F-G4 三维之一；2026-10-30 R11-3b 接线，**本轮只接本维**）。
    ///
    /// 语义：
    ///   - 用户**配置了** Department 范围 ⇒ Task 明细只保留其授权 `ProductionDepartment.DeptCode` 下的行；
    ///   - 用户**未配置**该维度（或 Global）⇒ 不构成约束（返回 null，谓词整体短路）；
    ///   - 授权值两端 Trim 归一（库值可能带前后空格，直接比对会恒判不中），纯空白视同未配置。
    ///
    /// ⚠ 授权值是 **`DeptCode` 字符串**，与 `Task.ProductionDepartmentId`(int) 不同域；这里**按 Code 直接比**
    ///    （`ProductionDepartment` 主档自带 `DeptCode`），不引入 Id↔Code 转换服务，也不新增表/字段。
    /// ⚠ 只作用于 **Task 派生的行/计数**；版本级 `Order` 计数（如 Summary.TotalOrders）保持版本口径 ——
    ///    「订单数是否也按部门收窄」是另一个口径决定，未定前不擅自改。
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveAllowedDepartmentCodesAsync(
        int userId, CancellationToken cancellationToken)
    {
        var scope = await _dataScopeService.ResolveScopeAsync(userId, cancellationToken);
        if (scope.IsGlobal)
            return null;

        var codes = scope.GetValues(DataScopeTypes.Department);
        if (codes is null || codes.Count == 0)
            return null;

        var normalized = codes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : normalized;
    }

    /// <summary>
    /// 部门范围谓词片段（**必须与 <see cref="WithDepartmentScope"/> 成对使用**，否则参数缺失会报错）。
    /// `@ScopeHasDepartments = 0` 时整体短路 ⇒ 未配置该维度的用户，行为与本改造前完全一致。
    /// 别名用 `_scpd`，避免与各查询里已有的 `pd`（资源所属部门）别名冲突。
    /// </summary>
    private static string DepartmentPredicate(string taskAlias)
        => $@"
                AND (@ScopeHasDepartments = 0 OR EXISTS (
                        SELECT 1 FROM ProductionDepartment _scpd
                        WHERE _scpd.Id = {taskAlias}.ProductionDepartmentId
                          AND _scpd.DeptCode IN @ScopeDepartmentCodes))";

    /// <summary>把部门范围两个参数并入既有参数对象（Dapper 的 DynamicParameters 可叠加匿名对象）。</summary>
    private static DynamicParameters WithDepartmentScope(object parameters, IReadOnlyList<string>? departmentCodes)
    {
        var dp = new DynamicParameters();
        dp.AddDynamicParams(parameters);
        var hasScope = departmentCodes is { Count: > 0 };
        dp.Add("ScopeHasDepartments", hasScope ? 1 : 0);
        dp.Add("ScopeDepartmentCodes", hasScope ? departmentCodes! : Array.Empty<string>());
        return dp;
    }

    /// <summary>
    /// 「延期」判定单一判据（**本服务所有延期口径 SQL 必须走此方法**）：按**日期**比较。
    /// PlannedEndTime 为 DATETIME2、CustomerDueDate 为 DATE，裸比较会隐式提升为「完工时刻 vs 交日零点」，
    /// 交期当天完工被误判延期（Q1 裁决 2026-10-17；summary×2/candidate×2 漏改于 2026-10-19 补齐，
    /// dev v2 实测裸判据 191 vs 日期判据 179、差额=12 条交期当天完工，4号位 10-19 §五.2）。
    /// </summary>
    private static string DelayedOnDate(string endTimeColumn, string dueDateColumn)
        => $"CAST({endTimeColumn} AS date) > {dueDateColumn}";

    /// <summary>标量 COUNT 查询（只读 APS_Production；T=int 无约束时 T? 仅为注解，须显式 int? 类型参数才能 ?? 0 兜底）</summary>
    private async Task<int> CountScalarAsync(string sql, object parameters)
        => await _connectionManager.QueryFirstOrDefaultAsync<int?>(sql, parameters, db: DatabaseId.APS) ?? 0;

    /// <summary>TaskDispatch 页内富化行（B2② 下发状态；非契约 DTO）</summary>
    private sealed class TaskDispatchRow
    {
        public long SourceTaskId { get; set; }
        public string DispatchStatus { get; set; } = string.Empty;
    }

    /// <summary>ResourceCalendarSlot 本级 OFF 窗口行（B1；与 Overview 同型；非契约 DTO）</summary>
    private sealed class ResourceOffWindowRow
    {
        public int ResourceId { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>AllocationTaskShare 页内富化行（Dapper 映射用；非契约 DTO）</summary>
    private sealed class GanttShareRow
    {
        public long TaskId { get; set; }
        public long OrderId { get; set; }
        public string? OrderNo { get; set; }
        public decimal ShareQty { get; set; }
    }

    /// <summary>ScheduleExplanationFact 页内富化行（ObjectType='TASK'；非契约 DTO）</summary>
    private sealed class GanttReasonRow
    {
        public long TaskId { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string? Severity { get; set; }
        public decimal? ImpactHours { get; set; }
        public string ObjectType { get; set; } = string.Empty;
    }
}
