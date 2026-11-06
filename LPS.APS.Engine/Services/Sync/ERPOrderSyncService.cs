global using Task = System.Threading.Tasks.Task;
using System.Data;
using System.Text.Json;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// ERP订单同步服务（2号位职责）
/// 遵循 Socket-Plug 模式：通过 ODS v_APS_SalesOrder 视图间接拉取，不直连ERP
/// 数据路径：ODS.v_APS_SalesOrder → APS.ERP_Order_Staging → sp_ValidateAndPromoteOrders → APS.Order_Canonical
/// 
/// 同步策略：
/// - 全量同步：每日凌晨，拉取所有未取消/未完成订单（日期窗口过滤延迟到ETL层）
/// - 增量同步：每小时，基于 UpdatedAt 水位线拉取变更订单；**提升整体成功才推进水位线**，
///   整体失败保持原值以便下次重拉（逐行提升失败属正常业务分支，仍推进）
/// - 新BOMNO嗅探：增量同步时检测 Order_Canonical 中不存在的BOMNO，触发实时BOM展开
/// </summary>
public class ERPOrderSyncService : IERPOrderSyncService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IOrderPromotionService _promotionService;
    private readonly IOrderLoadingService _orderLoadingService;
    private readonly ILogger<ERPOrderSyncService> _logger;

    public ERPOrderSyncService(
        DatabaseConnectionManager connectionManager,
        IOrderPromotionService promotionService,
        IOrderLoadingService orderLoadingService,
        ILogger<ERPOrderSyncService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _promotionService = promotionService ?? throw new ArgumentNullException(nameof(promotionService));
        _orderLoadingService = orderLoadingService ?? throw new ArgumentNullException(nameof(orderLoadingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 【白天候选】订单池同步：把 Order_Canonical 的增量订单压进当前 ACTIVE PlanVersion 的 [Order]。
    /// 用户从该订单池选单做插单试排（PM 0923「白天 2号位 自己写 [Order]」）。
    /// 幂等（底层 SP 有 NOT EXISTS 保护）；**失败不阻断 ERP 订单同步主流程**（仅记日志）。
    /// </summary>
    private async Task SyncOrdersToActivePartitionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inserted = await _orderLoadingService.LoadOrdersToActivePlanVersionsAsync(cancellationToken);
            if (inserted > 0)
            {
                _logger.LogInformation("白天订单池同步：新增 {Inserted} 条订单压入 ACTIVE PlanVersion", inserted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "白天订单池同步失败（不阻断订单同步主流程）");
        }
    }

    /// <inheritdoc />
    public async Task FullSyncAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await CleanupOldStagingRecordsAsync();

            var orders = await QueryOrdersFromODSAsync(
                whereClause: "",
                parameters: null);

            if (orders.Count == 0)
            {
                _logger.LogWarning("全量同步：ODS视图未返回任何订单");
                return;
            }

            // 2026-11-05：本轮同步戳。既用于写 staging 的 SyncedAt，也用于收口判定
            //   「本轮是否出现」，两处必须是同一个值。
            var roundStart = DateTime.Now;
            await BulkWriteToStagingAsync(orders, roundStart);
            var (promoted, failed) = await _promotionService.ValidateAndPromoteAsync(cancellationToken);
            await ReconcileOrderLifecycleAsync(roundStart, cancellationToken);
            var newBOMNOs = await DetectNewBOMNOsAsync();

            stopwatch.Stop();
            _logger.LogInformation(
                "全量同步完成: 拉取={Fetched}, 提升={Promoted}, 失败={Failed}, 新BOMNO={NewBOMNO}, 耗时={Elapsed}ms",
                orders.Count, promoted, failed, newBOMNOs.Count, stopwatch.ElapsedMilliseconds);

            if (newBOMNOs.Count > 0)
            {
                _logger.LogWarning("新BOMNO需实时展开: {BOMNOs}",
                    string.Join(", ", newBOMNOs.Take(20)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量同步失败");
            throw;
        }
    }

    /// <summary>
    /// 订单生命周期收口（2026-11-05，用户裁定）。**只在全量同步后执行**——增量只拉变更单，
    /// 「本轮未出现」在增量语义下没有意义。
    ///
    /// 解决两个既有缺陷：
    /// <list type="number">
    ///   <item><b>验证失败行不更新状态</b>：staging 行只要 `SyncStatus='FAILED'`（如 `ORDER_TYPE_UNKNOWN`）
    ///     就不会进入 promote，`Order_Canonical` 的状态因此永不更新。实测 **2,402 单**在 ERP 已
    ///     `CLOSED`/`CANCELLED`，Canonical 仍停在 `Open`。<br/>
    ///     ⇒ 本步只更新 `Status` 一列（`SalesOrderCategory` 含「取消」⇒ `CANCELLED`，否则取 `stg.Status`），
    ///     **不做任何字段覆盖、不绕过校验**。校验失败说的是"这行数据不能用"，不等于"这单的状态不用更新"。</item>
    ///   <item><b>源头消失不闭单</b>：ODS 视图 `v_APS_SalesOrder` 的关闭窗口只有 7 天
    ///     （`InstrCancelDate/Completion >= GETDATE()-7`）。同步一旦停摆超过 7 天，关闭事件永久丢失
    ///     ⇒ 订单永远停在最后一次看到的 `Open`。实测孤儿 **105,760** 单，其中仍为 `Open` 的 **36,468** 单。<br/>
    ///     ⇒ 连续 **2 轮**未出现才置 `CLOSED`（单轮可能只是视图抖动）；中途重新出现则候选自动移除。</item>
    /// </list>
    ///
    /// ⚠️ 本步只改 `Order_Canonical`，**不触碰 `[Order]`**：已装载的 PlanVersion 快照不受影响，
    ///    因此可安全地在求解进行中执行。
    /// </summary>
    private async Task ReconcileOrderLifecycleAsync(DateTime roundStart, CancellationToken cancellationToken)
    {
        try
        {
            // ── ⓪ 先把「本轮戳」对齐到库里实际落盘的值 ──────────────────────
            //   `ERP_Order_Staging.SyncedAt` 是 `datetime2(7)`，而 C# 传入的参数在 Dapper 下
            //   默认按 `datetime` 精度（约 3.33ms）四舍五入 ⇒ 直接拿它做 `>=` 可能比落盘值**偏大**，
            //   导致「本轮出现的单」被算成 0 行（2026-11-05 试算已复现：0 命中，全部 OPEN 被误登记为候选）。
            //   ⇒ 写入后回读该轮次的真实时间戳，后续一律用它。取 10 分钟下限是为了吸收上述精度偏差。
            var effectiveRoundStart = await _connectionManager.QueryFirstOrDefaultAsync<DateTime?>(
                @"SELECT MAX(SyncedAt) FROM dbo.ERP_Order_Staging
                   WHERE SyncedAt >= DATEADD(MINUTE, -10, @RoundStart);",
                new { RoundStart = roundStart }, db: DatabaseId.APS);

            if (effectiveRoundStart == null)
            {
                _logger.LogWarning("订单生命周期收口跳过：本轮未在 ERP_Order_Staging 写入任何行（RoundStart={RoundStart:O}）", roundStart);
                return;
            }

            var stamp = effectiveRoundStart.Value;

            // ⚠️ 必须显式声明 `DbType.DateTime2`：Dapper 对 `DateTime` 默认按 `datetime`（约 3.33ms）传，
            //    会把 `datetime2(7)` 的戳**向上取整** ⇒ `SyncedAt >= @Stamp` 命中 0 行。
            //    2026-11-05 15:35 首次实跑已复现：MAX=…45.8421065 → 传参变 …45.8433333 → 0 命中
            //    ⇒ 232,129 个 Open 单全部被误登记为候选（若再跑一轮就全被关单）。参数必须钉死精度。
            var stampParams = new DynamicParameters();
            stampParams.Add("@Stamp", stamp, DbType.DateTime2);

            // ── ① 验证失败行：只同步 Status ────────────────────────────────
            var statusSynced = await _connectionManager.ExecuteAsync(
                @"UPDATE oc
                     SET oc.Status    = CASE WHEN s.SalesOrderCategory LIKE N'%取消%' THEN N'CANCELLED'
                                             ELSE s.Status END,
                         oc.UpdatedAt = GETDATE()
                    FROM dbo.Order_Canonical oc
                    JOIN dbo.ERP_Order_Staging s ON s.OrderNo = oc.OrderNo
                   WHERE s.SyncStatus = N'FAILED'
                     AND s.SyncedAt  >= @Stamp
                     AND ISNULL(oc.Status, N'') <> (CASE WHEN s.SalesOrderCategory LIKE N'%取消%' THEN N'CANCELLED'
                                                         ELSE s.Status END);",
                stampParams, db: DatabaseId.APS);

            // ── ② 候选表维护：本轮出现过的一律移除（recovered）────────────
            await _connectionManager.ExecuteAsync(
                @"DELETE c
                    FROM dbo.OrderStaleCandidate c
                   WHERE EXISTS (SELECT 1 FROM dbo.ERP_Order_Staging s
                                  WHERE s.OrderNo = c.OrderNo AND s.SyncedAt >= @Stamp);",
                stampParams, db: DatabaseId.APS);

            // ── ③ 本轮未出现且仍为 Open 的 → MissCount + 1 ────────────────
            await _connectionManager.ExecuteAsync(
                @"MERGE dbo.OrderStaleCandidate AS t
                  USING (
                        SELECT oc.OrderNo
                          FROM dbo.Order_Canonical oc
                         WHERE oc.Status = N'Open'
                           AND NOT EXISTS (SELECT 1 FROM dbo.ERP_Order_Staging s
                                            WHERE s.OrderNo = oc.OrderNo AND s.SyncedAt >= @Stamp)
                  ) AS src ON t.OrderNo = src.OrderNo
                  WHEN MATCHED THEN
                       UPDATE SET t.MissCount = t.MissCount + 1, t.LastCheckedAt = GETDATE()
                  WHEN NOT MATCHED BY TARGET THEN
                       INSERT (OrderNo, MissCount, FirstMissedAt, LastCheckedAt)
                       VALUES (src.OrderNo, 1, GETDATE(), GETDATE());",
                stampParams, db: DatabaseId.APS);

            // ── ④ 连续两轮未出现 → 置 CLOSED ─────────────────────────────
            var closed = await _connectionManager.ExecuteAsync(
                @"UPDATE oc
                     SET oc.Status = N'CLOSED', oc.UpdatedAt = GETDATE()
                    FROM dbo.Order_Canonical oc
                    JOIN dbo.OrderStaleCandidate c ON c.OrderNo = oc.OrderNo
                   WHERE c.MissCount >= 2
                     AND oc.Status = N'Open';",
                db: DatabaseId.APS);

            await _connectionManager.ExecuteAsync(
                @"DELETE FROM dbo.OrderStaleCandidate WHERE MissCount >= 2;",
                db: DatabaseId.APS);

            // ── ⑤ 对账：四列必须闭合，落日志可回看 ───────────────────────
            var logParams = new DynamicParameters();
            logParams.Add("@Stamp", stamp, DbType.DateTime2);
            logParams.Add("@StatusSynced", statusSynced, DbType.Int32);
            logParams.Add("@Closed", closed, DbType.Int32);

            await _connectionManager.ExecuteAsync(
                @"INSERT INTO dbo.APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                  SELECT CONVERT(NVARCHAR(50), @Stamp, 121), N'OrderLifecycle.Reconcile',
                         CONCAT(N'staging=', (SELECT COUNT(*) FROM dbo.ERP_Order_Staging),
                                N'；canonical=', (SELECT COUNT(*) FROM dbo.Order_Canonical),
                                N'；本轮状态同步(FAILED行)=', @StatusSynced,
                                N'；本轮关单=', @Closed,
                                N'；候选未关=', (SELECT COUNT(*) FROM dbo.OrderStaleCandidate),
                                -- 对账口径：只比「本轮出现过」的行。历史 staging 行不再被源端返回，
                                --   其状态是最后一次见到的快照，拿来比会产生永远消不掉的假差额
                                --   （2026-11-05 实测：不一致 2,402 = 本轮 1,106 + 历史残留 1,296）。
                                N'；本轮状态不一致=', (SELECT COUNT(*) FROM dbo.Order_Canonical oc
                                                      JOIN dbo.ERP_Order_Staging s ON s.OrderNo = oc.OrderNo
                                                     WHERE s.SyncedAt >= @Stamp
                                                       AND ISNULL(oc.Status,N'') <> ISNULL(s.Status,N''))),
                         N'INFO', GETDATE();",
                logParams,
                db: DatabaseId.APS);

            if (statusSynced > 0 || closed > 0)
            {
                _logger.LogWarning(
                    "订单生命周期收口：FAILED行状态同步 {StatusSynced} 单；连续两轮未出现→关单 {Closed} 单",
                    statusSynced, closed);
            }
        }
        catch (Exception ex)
        {
            // 不阻断主同步（收口是补强，不是主链）；但必须响亮留痕，否则会静默失效。
            _logger.LogError(ex, "订单生命周期收口失败（不阻断全量同步）：{Message}", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task IncrementalSyncAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var lastSyncTime = await GetLastSyncWatermarkAsync();

            var orders = await QueryOrdersFromODSAsync(
                whereClause: "WHERE UpdatedAt > @LastSyncTime",
                parameters: new { LastSyncTime = lastSyncTime });

            if (orders.Count == 0)
            {
                await UpdateSyncWatermarkAsync(DateTime.Now);
                // 无增量订单也压一次（幂等补齐：首次上线 / 上次压单失败时）
                await SyncOrdersToActivePartitionAsync(cancellationToken);
                return;
            }

            var maxUpdatedAt = orders.Max(o => o.UpdatedAt);
            var promotionCompleted = false;

            try
            {
                await BulkWriteToStagingAsync(orders);
                var (promoted, failed) = await _promotionService.ValidateAndPromoteAsync(cancellationToken);
                var newBOMNOs = await DetectNewBOMNOsAsync();

                // 水位线只在**整条增量链**全部成功后推进（0号位 第二十二轮 J22-02 / P1）。
                // 若把本行放在 DetectNewBOMNOsAsync 之前：提升已成功、探测抛异常时，
                // finally 仍会推进水位线，而 catch 日志宣称「整体失败、水位线保持」——
                // 日志与真实重试边界不一致，下次增量不会再拉取同批订单。
                promotionCompleted = true;

                stopwatch.Stop();
                _logger.LogInformation(
                    "增量同步完成: 拉取={Fetched}, 提升={Promoted}, 失败={Failed}, 耗时={Elapsed}ms",
                    orders.Count, promoted, failed, stopwatch.ElapsedMilliseconds);

                if (newBOMNOs.Count > 0)
                {
                    _logger.LogWarning("新BOMNO需实时展开: {BOMNOs}",
                        string.Join(", ", newBOMNOs.Take(20)));
                }
            }
            catch (Exception ex)
            {
                // 整体失败（批量写 Staging、提升、或新 BOMNO 探测任一抛异常）⇒ 水位线保持原值，本批订单下次仍会被拉取。
                // 仅「逐行提升失败」（ValidateAndPromoteAsync 正常返回 failed > 0）才推进水位线，属正常业务分支。
                _logger.LogError(ex,
                    "增量同步整体失败，水位线保持 {LastSyncTime} 不回退推进，本批 {Count} 条订单下次重试",
                    lastSyncTime, orders.Count);
            }
            finally
            {
                if (promotionCompleted)
                {
                    await UpdateSyncWatermarkAsync(maxUpdatedAt);
                }
            }

            // 【白天候选】订单池同步：增量订单压进 ACTIVE PlanVersion 的 [Order]
            await SyncOrdersToActivePartitionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "增量同步失败");
            throw;
        }
    }

    /// <summary>
    /// 直接查询 ODS 库的 v_APS_SalesOrder 视图
    /// 遵守 Socket-Plug 红线：2号位不直连ERP，通过 ODS 视图拉取
    /// ⚠️ 原方案通过 APS SYNONYM → ODS 跨库查询，22万条耗时3分钟+
    ///    改为直连 ODS 库查询，耗时降至约10秒
    /// ⚠️ 全量同步时数据量大（22万条），设置5分钟超时
    /// </summary>
    // ↓ 以下五个 IO 边沿为 protected virtual：供单测覆写，验证水位线推进边界（J22-02 负例）。
    //   生产路径零覆写者，语义与 private 相同。
    protected virtual async Task<List<ERPOrderDto>> QueryOrdersFromODSAsync(string whereClause, object? parameters, int commandTimeout = 300)
    {
        var sql = $@"
            SELECT
                SourceOrderId,
                OrderNo,
                OrderType,
                MaterialCode,
                SourceModel,
                BOMNO,
                FactoryCode,
                SourceWarehouseProcessCode,
                Quantity,
                UOM,
                DueDate,
                OriginalDueDate,
                ReceivedQty,
                Priority,
                Status,
                SourceSystem,
                SourceMasterID,
                TransportMode,
                CustomerName,
                MTS_InstructionNo,
                CustomerCode,
                JPOrderNo,
                SalesOrderCategory,
                DemandMaturityStatus,
                ContractNo,
                CreatedAt,
                UpdatedAt
            FROM v_APS_SalesOrder
            {whereClause}
            ORDER BY UpdatedAt";

        var result = await _connectionManager.QueryAsync<ERPOrderDto>(
            sql,
            parameters,
            db: DatabaseId.ODS,
            commandTimeout: commandTimeout);

        return result.ToList();
    }

    /// <summary>
    /// 批量写入 ERP_Order_Staging 表（SyncStatus = PENDING）
    /// 使用 SqlBulkCopy 高性能写入
    /// ⚠️ 写入前按 OrderNo 去重，避免 ODS 视图返回重复数据导致违反唯一约束
    /// ⚠️ 写入后按「同一 OrderNo 只保留最新一条（PENDING / FAILED / PROCESSED）」收敛：增量同步整体失败时不推进水位线
    ///    （见 IncrementalSyncAsync），下一轮会重新拉取同一批订单。若同一 OrderNo 存在两条 PENDING，
    ///    sp_ValidateAndPromoteOrders 的 MERGE 源侧无去重、按 OrderNo 匹配 ⇒ 8672「多行匹配同一目标行」
    ///    ⇒ 事务整体回滚 ⇒ 每次重试都失败，无法自愈。唯一约束为 (OrderNo, SyncedAt)，
    ///    重试写入新 SyncedAt 不会自动拦截，故须收敛。
    ///
    /// 【为什么 FAILED 也进收敛集】
    ///   · sp_ValidateAndPromoteOrders 的 PHASE 0 只锁 `SyncStatus='PENDING'`，行一旦被判 FAILED
    ///     就永远不再进入提升流程；若收敛只认 PENDING，每次全量同步都会在原处再留一份 FAILED 副本，
    ///     表随同步轮次线性膨胀（2026-11-02 实测：602,826 行 FAILED 只对应 130,953 个单号，平均 4.6 份）。
    ///   · 把 FAILED 纳入收敛**不影响提升到 Order_Canonical**：本语句删的每一行都满足
    ///     「同 OrderNo 存在 SyncedAt/Id 更新的一行」，且 SP 全部判定都写死 `SyncStatus='PENDING'`
    ///     （SP 第 63 行 PHASE 0、第 288 行 1g 及其余各处），删 FAILED 行与提升集合无交集。
    ///   · 只要该单号没有再同步进来更新的行，它最近一次的 FAILED 就原样保留 ⇒ 失败诊断不丢。
    ///
    /// 【为什么 PROCESSED 也进收敛集】（2026-11-02 补，用户指出「Staging 与 Order_Canonical 应当一样多」）
    ///   · 症结：PROCESSED 行**从来没被收敛过**，也没有任何兜底删除会跑到它头上 ——
    ///     唯一的 `CleanupOldStagingRecordsAsync` 只在**全量同步前**删「7 天以上的 PROCESSED」，
    ///     而全量是 00:30 定时任务，生产 `Hangfire:CronEnabled=false` 时根本不跑。
    ///   · 后果：同一单号每被重新同步一次就多一行流水。实测 `P17180982` 在 2026-10-30 的
    ///     10:43 / 10:59 / 11:35 各同步一次 ⇒ **3 行 PROCESSED，数据完全相同**；
    ///     全库同 (OrderNo, SourceOrderId) 多行的有 **247,563 组**，
    ///     `PROCESSED` 653,262 行只对应 259,586 个单号（2.5 倍）。
    ///   · 纳入收敛后，每个 OrderNo 在 Staging 里**恒定只有 1 行**（最近一次同步的结果：
    ///     PROCESSED=已进 Order / FAILED=没进 / PENDING=待校验），Staging 单号数
    ///     ≡ `Order_Canonical` ∩ 源视图 ∪ 失败单，用户要的「两边对得上」才成立。
    ///   · 会不会丢事实：**不会**。被删的是同单号的旧流水行，订单事实在 `Order_Canonical`
    ///     （实测 368,015 行 / 368,015 个单号，**0 重复**）；SP 的判定集合只看 `PENDING`，
    ///     删 PROCESSED 碰不到提升。
    ///
    /// 【为什么是「写入后收敛」，不是「写入前全表清空」（0号位 第二十二轮 J22-01 / P0）】
    ///   · 写入前 `DELETE … WHERE SyncStatus='PENDING'` 无批次／来源约束，会把**其它批次、并发调用
    ///     正在处理**的待校验订单一并删掉；且 DELETE 与 BulkInsert 非原子，插入失败即「清零而新行未就绪」，
    ///     待处理事实凭空消失。
    ///   · 改为插入成功后，只删除「同一 OrderNo 已被更新 PENDING 覆盖」的旧行：
    ///     范围由 OrderNo ＋ SyncedAt 确定，其它订单的 PENDING 一条不碰；插入失败时旧行原样保留。
    ///   · 收敛失败时最坏结果是「同单号两条 PENDING」⇒ 下次提升 8672 ⇒ 下轮再收敛，属**可自愈**，
    ///     不再出现「事实丢失且不可恢复」。
    /// </summary>
    protected virtual async Task BulkWriteToStagingAsync(List<ERPOrderDto> orders, DateTime? syncStamp = null)
    {
        // 按 OrderNo 去重（OrderNo 是 APS 业务主键），保留 UpdatedAt 最新的记录
        var distinctOrders = orders
            .GroupBy(o => o.OrderNo)
            .Select(g => g.OrderByDescending(o => o.UpdatedAt).First())
            .ToList();

        if (distinctOrders.Count < orders.Count)
        {
            _logger.LogWarning(
                "ODS视图存在重复OrderNo，已去重: 原始={Original}, 去重后={Distinct}",
                orders.Count, distinctOrders.Count);
        }

        var dataTable = new DataTable("ERP_Order_Staging");
        dataTable.Columns.Add("SourceOrderId", typeof(string));
        dataTable.Columns.Add("SourceSystem", typeof(string));
        dataTable.Columns.Add("SourceMasterID", typeof(int));
        dataTable.Columns.Add("OrderNo", typeof(string));
        dataTable.Columns.Add("OrderType", typeof(string));
        dataTable.Columns.Add("MaterialCode", typeof(string));
        // 2026-11-06：ERP 原始型号（冻结 07 §3494/§3652、08 DDL:4162 要求逐级透传至 Order_Canonical.SourceModel / [Order].SourceModel）
        dataTable.Columns.Add("Model", typeof(string));
        dataTable.Columns.Add("FactoryCode", typeof(string));
        dataTable.Columns.Add("SourceWarehouseProcessCode", typeof(string));
        dataTable.Columns.Add("Quantity", typeof(decimal));
        dataTable.Columns.Add("UOM", typeof(string));
        dataTable.Columns.Add("DueDate", typeof(DateTime));
        dataTable.Columns.Add("OriginalDueDate", typeof(DateTime));
        // 视图的 CreatedAt 即 ERP 的 InstrIssueDate（订单/生产指示正式下发日期）。
        // 冻结 07 IssueDate=源事实字段，下游 sp_ValidateAndPromoteOrders /
        // sp_SyncOrdersToPartitionTable 均已透传，此前只断在装载这一处。
        dataTable.Columns.Add("IssueDate", typeof(DateTime));
        dataTable.Columns.Add("ReceivedQty", typeof(decimal));
        dataTable.Columns.Add("Priority", typeof(int));
        dataTable.Columns.Add("BOMNO", typeof(string));
        dataTable.Columns.Add("Status", typeof(string));
        // v5.0.3 源事实字段
        dataTable.Columns.Add("TransportMode", typeof(string));
        dataTable.Columns.Add("CustomerName", typeof(string));
        dataTable.Columns.Add("MTS_InstructionNo", typeof(string));
        // v5.0.24 原始字段（由 sp_ValidateAndPromoteOrders 派生标准化）
        dataTable.Columns.Add("CustomerCode", typeof(string));
        dataTable.Columns.Add("JPOrderNo", typeof(string));
        dataTable.Columns.Add("SalesOrderCategory", typeof(string));
        dataTable.Columns.Add("DemandMaturityStatus", typeof(string));
        dataTable.Columns.Add("ContractNo", typeof(string));
        dataTable.Columns.Add("RawData", typeof(string));
        dataTable.Columns.Add("SyncStatus", typeof(string));
        dataTable.Columns.Add("SyncedAt", typeof(DateTime));

        // 2026-11-05：`SyncedAt` 支持由调用方传入「本轮戳」。孤儿收口要以
        //   「本轮是否出现」为判据，必须与调用方手里的同一枚时间戳逐位一致（同一时钟来源），
        //   不在这里各自取 Now（应用机与库机是两个进程，可能漂移）。
        var now = syncStamp ?? DateTime.Now;
        foreach (var order in distinctOrders)  // 使用去重后的数据
        {
            dataTable.Rows.Add(
                order.SourceOrderId,
                order.SourceSystem ?? "ERP",
                order.SourceMasterID.HasValue ? (object)order.SourceMasterID.Value : DBNull.Value,
                order.OrderNo,
                order.OrderType,
                order.MaterialCode,
                (object?)order.SourceModel ?? DBNull.Value,
                order.FactoryCode,
                order.SourceWarehouseProcessCode,
                order.Quantity,
                order.UOM,
                order.DueDate,
                order.OriginalDueDate,
                order.CreatedAt,          // = InstrIssueDate → IssueDate
                order.ReceivedQty,
                order.Priority,
                order.BOMNO,
                order.Status ?? "OPEN",
                // v5.0.3 源事实字段
                (object?)order.TransportMode ?? DBNull.Value,
                (object?)order.CustomerName ?? DBNull.Value,
                (object?)order.MTS_InstructionNo ?? DBNull.Value,
                // v5.0.24 原始字段（由 sp_ValidateAndPromoteOrders 派生标准化）
                (object?)order.CustomerCode ?? DBNull.Value,
                (object?)order.JPOrderNo ?? DBNull.Value,
                (object?)order.SalesOrderCategory ?? DBNull.Value,
                (object?)order.DemandMaturityStatus ?? DBNull.Value,
                (object?)order.ContractNo ?? DBNull.Value,
                JsonSerializer.Serialize(order),
                "PENDING",
                now
            );
        }

        // 先写本次批次：写入失败时上一轮的残留 PENDING 原样保留（不丢事实）。
        await _connectionManager.BulkInsertAsync(dataTable, "ERP_Order_Staging", DatabaseId.APS);

        // 再收敛重复：同一 OrderNo 只保留 SyncedAt 最新的一条（PENDING / FAILED / PROCESSED 同属待收敛集）。
        // 影响范围仅限「本 OrderNo 自己」，不触碰其它批次／并发调用的行；
        // 被收敛的是同单号的旧副本，该订单的最新事实仍在，不会造成订单事实丢失。
        //
        // ⚠ FAILED 为什么必须一起收敛、以及为什么不会影响提升到 Order_Canonical：
        //    见本方法 XML 注释「为什么 FAILED 也进收敛集」。一句话：SP 只读 PENDING，删 FAILED 碰不到提升集合。
        // ⚠ PROCESSED 同理：见 XML 注释「为什么 PROCESSED 也进收敛集」。
        //    SP 的判定集合是 PENDING（第 62 行）、提升源是 VALIDATED（第 382 行），
        //    删 PROCESSED 既碰不到校验也碰不到提升；订单事实在 Order_Canonical（一单一行）。
        var purged = await _connectionManager.ExecuteAsync(
            @"DELETE stale
              FROM ERP_Order_Staging AS stale
              WHERE stale.SyncStatus IN ('PENDING', 'FAILED', 'PROCESSED')
                AND EXISTS (
                    SELECT 1
                    FROM ERP_Order_Staging AS newer
                    WHERE newer.OrderNo    = stale.OrderNo
                      AND newer.SyncStatus IN ('PENDING', 'FAILED', 'PROCESSED')
                      AND (newer.SyncedAt > stale.SyncedAt
                           OR (newer.SyncedAt = stale.SyncedAt AND newer.Id > stale.Id))
                );",
            db: DatabaseId.APS);

        if (purged > 0)
        {
            _logger.LogWarning(
                "写入后收敛同单号旧副本（PENDING/FAILED/PROCESSED）: {Count} 条（已被该单号最新记录覆盖）", purged);
        }
    }

    /// <summary>
    /// 检测新BOMNO（紧急插单支持）
    /// 对比最新Staging批次中的BOMNO与现有Order_Canonical，找出新增的BOMNO
    /// </summary>
    protected virtual async Task<List<string>> DetectNewBOMNOsAsync()
    {
        var sql = @"
            SELECT DISTINCT stg.BOMNO
            FROM ERP_Order_Staging stg
            WHERE stg.SyncStatus IN ('VALIDATED', 'PROCESSED')
              AND stg.ProcessedAt >= DATEADD(HOUR, -2, GETDATE())
              AND stg.BOMNO IS NOT NULL
              AND stg.BOMNO <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM Order_Canonical oc 
                  WHERE oc.BOMNO = stg.BOMNO 
                    AND oc.CreatedAt < DATEADD(HOUR, -2, GETDATE())
              )";

        var result = await _connectionManager.QueryAsync<string>(sql, db: DatabaseId.APS);
        return result.ToList();
    }

    /// <summary>
    /// 获取上次同步水位线（从APS_SyncWatermark表读取）
    /// ⚠️ 首次同步：返回一个很早的日期（2020-01-01），确保全量拉取所有历史订单
    /// ⚠️ 后续同步：返回上次水位线，拉取所有变更（不限制回溯时间）
    /// </summary>
    protected virtual async Task<DateTime> GetLastSyncWatermarkAsync()
    {
        try
        {
            var sql = @"
                SELECT TOP 1 LastSyncTime
                FROM APS_SyncWatermark
                WHERE SyncType = 'ERP_Order'
                ORDER BY LastSyncTime DESC";

            var result = await _connectionManager.QueryFirstOrDefaultAsync<DateTime?>(sql, db: DatabaseId.APS);

            if (result == null)
            {
                _logger.LogWarning("未找到同步水位线，将全量拉取");
                return new DateTime(2020, 1, 1);
            }

            return result.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "水位线查询失败，回退全量拉取");
            return new DateTime(2020, 1, 1);
        }
    }

    /// <summary>
    /// 更新同步水位线
    /// </summary>
    protected virtual async Task UpdateSyncWatermarkAsync(DateTime syncTime)
    {
        try
        {
            var sql = @"
                IF EXISTS (SELECT 1 FROM APS_SyncWatermark WHERE SyncType = 'ERP_Order')
                    UPDATE APS_SyncWatermark
                    SET LastSyncTime = @SyncTime, UpdatedAt = GETDATE()
                    WHERE SyncType = 'ERP_Order'
                ELSE
                    INSERT INTO APS_SyncWatermark (SyncType, LastSyncTime, UpdatedAt)
                    VALUES ('ERP_Order', @SyncTime, GETDATE())";

            await _connectionManager.ExecuteAsync(sql, new { SyncTime = syncTime }, db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新同步水位线失败（非致命错误，下次同步将使用旧水位线）");
        }
    }

    /// <summary>
    /// 清理Staging表历史数据
    /// 策略：
    /// - 全量同步前清理所有残留 PENDING 记录（马上要重新拉全量，旧的无意义）
    /// - **不再按 7 天删除 PROCESSED**（2026-11-02 撤）
    /// - 不删任何失败(FAILED)记录：本函数不做 FAILED 的收敛，表内「每单号一条 FAILED」
    ///   由 BulkWriteToStagingAsync 的写入后收敛维持
    ///
    /// 【为什么撤掉「删 7 天以上 PROCESSED」】
    ///   · 主因：这条是 PROCESSED 唯一的兜底删除，而它**只在全量同步前跑**；全量是 00:30 定时任务，
    ///     生产 `Hangfire:CronEnabled=false` 时根本不跑 ⇒ 兜底长期失效，PROCESSED 无限膨胀
    ///     （实测 653,262 行只对应 259,586 个单号）。
    ///   · 副作用：它把「已掉出 ODS 源视图」的老单的 PROCESSED 行删掉，而这些单不会再有新行补进来
    ///     ⇒ 制造出「`Order_Canonical` 有、`ERP_Order_Staging` 没有」的孤儿。实测 105,760 单，
    ///     全部创建于 2026-10-08～10-19，且抽样到 ODS 视图查 5 个命中 0。
    ///   · 撤掉后增长仍有界：收敛到「每单号 1 行」后，行数上限＝曾同步过的不同单号数，
    ///     与 `Order_Canonical` 同量级，不会无限涨。保留最近一次同步结果也便于排查。
    /// 调用时机：全量同步开始前
    /// </summary>
    private async Task CleanupOldStagingRecordsAsync()
    {
        try
        {
            // ⚠ 只清残留 PENDING：全量马上重新拉一遍，旧 PENDING 若留着会被一并提升，
            //   其中已掉出 ODS 源视图的老单会被「复活」进 Order_Canonical，故必须清。
            const string sql = @"
                DELETE FROM ERP_Order_Staging
                WHERE SyncStatus = 'PENDING';";

            var deletedCount = await _connectionManager.ExecuteAsync(sql, db: DatabaseId.APS);

            if (deletedCount > 0)
            {
                _logger.LogDebug("清理Staging残留PENDING: {Count} 条", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理Staging历史记录失败（非致命）");
        }
    }
}
