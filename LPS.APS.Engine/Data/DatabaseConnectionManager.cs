using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Reflection;
using Dapper;
using LPS.APS.Engine.Configuration;

namespace LPS.APS.Engine.Data;

/// <summary>
/// 数据库标识枚举
/// 对应文档中的物理隔离架构：APS本地库 + ODS集成防腐层 + Auth权限库
/// </summary>
public enum DatabaseId
{
    /// <summary>
    /// APS本地库（APS_Production）
    /// 职责：排程计算、业务数据落地、快照归档、主数据、订单、任务、Pegging
    /// </summary>
    APS,

    /// <summary>
    /// ODS集成防腐层（MES_Integration）
    /// 职责：BOM展开请求/结果（存储过程级数据库编程）、契约视图
    /// </summary>
    ODS,

    /// <summary>
    /// Auth权限库（APS_Auth）
    /// 职责：RBAC权限管理、审批流、审计日志、数据范围策略
    /// </summary>
    Auth
}

/// <summary>
/// 数据库连接管理器（三库架构）
/// 管理APS本地库、ODS集成防腐层和Auth权限库的连接
/// </summary>
public class DatabaseConnectionManager : IDisposable
{
    private readonly DatabaseOptions _options;
    private readonly SemaphoreSlim _apsSemaphore;
    private readonly SemaphoreSlim _odsSemaphore;
    private readonly SemaphoreSlim _authSemaphore;
    private readonly ILogger<DatabaseConnectionManager>? _logger;
    private SqlConnection? _apsConnection;
    private SqlConnection? _odsConnection;
    private SqlConnection? _authConnection;
    private bool _disposed = false;

    /// <param name="options">三库连接配置</param>
    /// <param name="logger">日志（DI 注入）；直接 <c>new</c> 的调用点走下面的单参重载。</param>
    public DatabaseConnectionManager(
        IOptions<DatabaseOptions> options,
        ILogger<DatabaseConnectionManager>? logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (!_options.IsValid())
        {
            throw new InvalidOperationException("数据库配置无效，请检查APS、ODS和Auth连接配置");
        }

        _apsSemaphore = new SemaphoreSlim(1, 1);
        _odsSemaphore = new SemaphoreSlim(1, 1);
        _authSemaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// 单参构造重载（无日志）。
    ///
    /// ⚠️ 本重载存在**不是**为了「写起来方便」，而是 **Moq / Castle DynamicProxy 的硬约束**：
    /// <c>new Mock&lt;DatabaseConnectionManager&gt;(args)</c> 走 <c>CreateClassProxy(..., constructorArguments)</c>，
    /// 按**实参个数与类型精确匹配**构造函数，**不会**替调用方补 C# 可选参数的默认值。
    /// 故只留 `(IOptions, ILogger? = null)` 一个签名时，测试里 `Mock&lt;T&gt;(Options.Create(...))`（**1 个实参**）
    /// 匹配失败 ⇒ 代理类型造不出来 ⇒ <c>Mock.Object</c> 取值即抛 ArgumentException
    /// （2026-10-08 r13561 曾因此致 24 个用例由绿转红，见台账 §T-1008q）。
    /// 保留本重载 ⇒ 1 参 mock 与直接 <c>new</c> 都能绑定，**无需改任何测试**。
    ///
    /// ⇒ 通用结论（1号位 2026-10-08 提出、本号位复核采纳）：**「可选参数不影响 Moq」不成立**；
    ///    凡以 <c>Mock&lt;T&gt;(args)</c> 构造的类，若要兼容可选参数，就必须同时保留等价的**定参重载**。
    /// </summary>
    public DatabaseConnectionManager(IOptions<DatabaseOptions> options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 获取数据库连接（默认APS库）
    /// </summary>
    public async Task<IDbConnection> GetConnectionAsync(DatabaseId db = DatabaseId.APS)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DatabaseConnectionManager));
        }

        var (semaphore, connOptions) = GetDbResources(db);

        await semaphore.WaitAsync();
        try
        {
            switch (db)
            {
                case DatabaseId.APS:
                    if (_apsConnection == null || _apsConnection.State != ConnectionState.Open)
                    {
                        _apsConnection?.Dispose();
                        _apsConnection = new SqlConnection(connOptions.BuildConnectionString());
                        await _apsConnection.OpenAsync();
                    }
                    return _apsConnection;

                case DatabaseId.ODS:
                    if (_odsConnection == null || _odsConnection.State != ConnectionState.Open)
                    {
                        _odsConnection?.Dispose();
                        _odsConnection = new SqlConnection(connOptions.BuildConnectionString());
                        await _odsConnection.OpenAsync();
                    }
                    return _odsConnection;

                case DatabaseId.Auth:
                    if (_authConnection == null || _authConnection.State != ConnectionState.Open)
                    {
                        _authConnection?.Dispose();
                        _authConnection = new SqlConnection(connOptions.BuildConnectionString());
                        await _authConnection.OpenAsync();
                    }
                    return _authConnection;

                default:
                    throw new ArgumentOutOfRangeException(nameof(db), db, "不支持的数据库标识");
            }
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// 获取指定数据库的信号量和连接配置
    /// </summary>
    private (SemaphoreSlim semaphore, DatabaseConnectionOptions connOptions) GetDbResources(DatabaseId db)
    {
        return db switch
        {
            DatabaseId.APS => (_apsSemaphore, _options.APS),
            DatabaseId.ODS => (_odsSemaphore, _options.ODS),
            DatabaseId.Auth => (_authSemaphore, _options.Auth),
            _ => throw new ArgumentOutOfRangeException(nameof(db), db, "不支持的数据库标识")
        };
    }

    /// <summary>
    /// 释放连接
    /// </summary>
    public void ReleaseConnection(DatabaseId db = DatabaseId.APS)
    {
        var (semaphore, _) = GetDbResources(db);
        semaphore.Release();
    }

    /// <summary>
    /// 丢弃指定库的缓存连接（连接状态已不可信时调用，例如回滚失败留下未结束的事务）。
    /// 只处置并置空引用，下次 <see cref="GetConnectionAsync"/> 会重建；**不影响信号量** ——
    /// 调用方仍须按配对调用 <see cref="ReleaseConnection(DatabaseId)"/>。
    /// </summary>
    private void DiscardConnection(DatabaseId db)
    {
        switch (db)
        {
            case DatabaseId.APS:
                _apsConnection?.Dispose();
                _apsConnection = null;
                break;
            case DatabaseId.ODS:
                _odsConnection?.Dispose();
                _odsConnection = null;
                break;
            case DatabaseId.Auth:
                _authConnection?.Dispose();
                _authConnection = null;
                break;
        }
    }

    /// <summary>
    /// 执行SQL查询（返回列表）
    /// </summary>
    /// <param name="commandTimeout">命令超时时间（秒），null表示使用配置的默认超时</param>
    public virtual async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, DatabaseId db = DatabaseId.APS, int? commandTimeout = null)
    {
        var (_, connOptions) = GetDbResources(db);
        var timeout = commandTimeout ?? connOptions.CommandTimeout;

        var connection = await GetConnectionAsync(db);
        try
        {
            return await connection.QueryAsync<T>(sql, parameters, commandType: commandType, commandTimeout: timeout);
        }
        finally
        {
            ReleaseConnection(db);
        }
    }

    /// <summary>
    /// 执行SQL查询（返回单个对象）
    /// </summary>
    /// <param name="commandTimeout">命令超时时间（秒），null表示使用配置的默认超时</param>
    public virtual async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? parameters = null, CommandType commandType = CommandType.Text, DatabaseId db = DatabaseId.APS, int? commandTimeout = null)
    {
        var (_, connOptions) = GetDbResources(db);
        var timeout = commandTimeout ?? connOptions.CommandTimeout;

        var connection = await GetConnectionAsync(db);
        try
        {
            return await connection.QueryFirstOrDefaultAsync<T>(sql, parameters, commandType: commandType, commandTimeout: timeout);
        }
        finally
        {
            ReleaseConnection(db);
        }
    }

    /// <summary>
    /// 执行非查询SQL（INSERT、UPDATE、DELETE）
    /// </summary>
    /// <param name="commandTimeout">命令超时时间（秒），null表示使用配置的默认超时</param>
    public virtual async Task<int> ExecuteAsync(string sql, object? parameters = null, CommandType commandType = CommandType.Text, DatabaseId db = DatabaseId.APS, int? commandTimeout = null)
    {
        var (_, connOptions) = GetDbResources(db);
        var timeout = commandTimeout ?? connOptions.CommandTimeout;

        var connection = await GetConnectionAsync(db);
        try
        {
            return await connection.ExecuteAsync(sql, parameters, commandType: commandType, commandTimeout: timeout);
        }
        finally
        {
            ReleaseConnection(db);
        }
    }

    /// <summary>
    /// 执行存储过程
    /// </summary>
    public async Task<IEnumerable<T>> ExecuteStoredProcedureAsync<T>(string procedureName, object? parameters = null, DatabaseId db = DatabaseId.APS)
    {
        return await QueryAsync<T>(procedureName, parameters, CommandType.StoredProcedure, db);
    }

    /// <summary>
    /// 执行批量插入（使用SqlBulkCopy）
    /// 对应文档中的SqlBulkCopy极速推送/拉取场景
    /// </summary>
    public virtual async Task BulkInsertAsync(DataTable dataTable, string tableName, DatabaseId db = DatabaseId.APS)
    {
        var (_, connOptions) = GetDbResources(db);
        var connection = await GetConnectionAsync(db);
        try
        {
            using var bulkCopy = new SqlBulkCopy((SqlConnection)connection, SqlBulkCopyOptions.TableLock, null)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = connOptions.CommandTimeout,
                BatchSize = 50000
            };

            // 映射列
            foreach (DataColumn column in dataTable.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(dataTable);
        }
        finally
        {
            ReleaseConnection(db);
        }
    }

    /// <summary>
    /// 批量插入（SqlBulkCopy）到调用方提供的事务连接（参与该事务，回滚即撤销）。
    /// 供 BOM 接货在 ExecuteInTransactionAsync 内写 OrderBomRequestLink 使用，
    /// 避免在事务持有同一库信号量期间再经 GetConnectionAsync 取连接造成死锁。
    /// </summary>
    public async Task BulkInsertToTransactionAsync(
        DataTable dataTable,
        string tableName,
        SqlConnection destinationConnection,
        SqlTransaction destinationTransaction,
        int timeoutSeconds = 600)
    {
        using var bulkCopy = new SqlBulkCopy(destinationConnection, SqlBulkCopyOptions.Default, destinationTransaction)
        {
            DestinationTableName = tableName,
            BulkCopyTimeout = timeoutSeconds,
            BatchSize = 50000
        };

        foreach (DataColumn column in dataTable.Columns)
        {
            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulkCopy.WriteToServerAsync(dataTable);
    }

    /// <summary>
    /// 批量插入（SqlBulkCopy）**泛型版**：把一组「属性名＝列名」的行对象流式推给目标表，参与调用方事务。
    /// </summary>
    /// <remarks>
    /// 【为什么加】2026-10-29 实测：Dapper 的 `ExecuteAsync(sql, IEnumerable)` 是**逐项执行**——
    ///   看着像批量，实际是一行一次网络往返（实测 `[Task]` 833 行/s、`[Pegging]` 1,484 行/s）。
    ///   `PlanVersionId=999328` 一轮落库 411 万行 ⇒ 落库段约 57 分钟，其中绝大部分是往返开销。
    ///   同机试点（`#temp`，`[Task]` 全索引）实测：逐条 779~904 行/s、`SqlBulkCopy` 55,157 行/s
    ///   ⇒ 慢在往返，不在索引维护（堆表 904 vs 带索引 779，仅差 14%）。
    /// 【默认选项为什么是 CheckConstraints｜TableLock】
    ///   `SqlBulkCopy` 默认**不校验** CHECK／FK——若沿用 `SqlBulkCopyOptions.Default`，
    ///   `CK_AllocationTaskShare_Qty`、`CK_PSA_AllocatedQty`、`FK_SEF_ScheduleRun` 会被**静默绕过**，
    ///   等于把现有的 fail-closed 拆掉。故默认显式打开约束校验；调用方如确知目标表无约束且要极限吞吐，
    ///   可显式传 `SqlBulkCopyOptions.Default`（须在调用处写明理由）。
    /// 【分块】只在内存里控峰值（默认 5 万行一块），不改变事务边界：全部块仍在同一个 `transaction` 内，
    ///   任一块失败即整事务回滚，与逐条写入的原子性一致。
    /// 【`onChunkDone`】仅供进度日志用；回调抛异常会中断落库，回调内不要做会失败的事。
    /// </remarks>
    public async Task BulkInsertToTransactionAsync<TRow>(
        IReadOnlyList<TRow> rows,
        string tableName,
        IDbConnection connection,
        IDbTransaction transaction,
        SqlBulkCopyOptions options = SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock,
        int chunkRows = 50_000,
        int timeoutSeconds = 600,
        Action<int>? onChunkDone = null)
        where TRow : class
    {
        if (rows.Count == 0) return;
        if (connection is not SqlConnection sqlConnection)
            throw new InvalidOperationException(
                $"BulkInsertToTransactionAsync 需要 SqlConnection，实得 {connection?.GetType().FullName ?? "null"}");
        if (transaction is not SqlTransaction sqlTransaction)
            throw new InvalidOperationException(
                $"BulkInsertToTransactionAsync 需要同连接的 SqlTransaction，实得 {transaction?.GetType().FullName ?? "null"}");

        var columns = ResolveBulkColumns(rows);
        for (var offset = 0; offset < rows.Count; offset += chunkRows)
        {
            var take = Math.Min(chunkRows, rows.Count - offset);
            using var table = BuildBulkDataTable(rows, offset, take, columns);

            using var bulkCopy = new SqlBulkCopy(sqlConnection, options, sqlTransaction)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = timeoutSeconds,
                BatchSize = 0   // 0＝整块一次提交
            };
            foreach (var column in columns)
                bulkCopy.ColumnMappings.Add(column.Name, column.Name);

            await bulkCopy.WriteToServerAsync(table);
            onChunkDone?.Invoke(offset + take);
        }
    }

    /// <summary>
    /// 批量插入**带 IDENTITY 的表**，并回传「源行序号 → 数据库生成的 Id」。参与调用方事务。
    /// </summary>
    /// <remarks>
    /// 【为什么不能直接 SqlBulkCopy】`SqlBulkCopy` 拿不回生成的 Id；而 `INSERT … OUTPUT INSERTED.Id`
    ///   在批里**引用不到源表列**（实测报「无法绑定 `src.__i`」）⇒ 只能走 `MERGE`。
    ///   本方法：① 同连接建 `#temp` 暂存表（`SELECT TOP 0 * INTO`，列结构自动与目标表逐列同源）
    ///   ② 加一列 `__i`（源行序号）③ `SqlBulkCopy` 灌暂存表 ④ 一条 `MERGE … ON 1=0 … OUTPUT INSERTED.Id, src.__i`
    ///   ⑤ 按 `__i` 回填并返回数组 ⑥ 删暂存表。
    /// 【正确性】返回值的下标 = 传入 `rows` 的下标；不依赖任何自然键
    ///   （`[Task].TaskNo` 在同 PlanVersionId 内只有 296,081 个不同值／121 万行，重复 75.6%，不能当键）。
    /// 【实测】10 万行 2,142 ms ⇒ 46,685 行/s，且 `COUNT(*)=COUNT(DISTINCT Id)=100,000`（配对无错位）。
    /// 【前提】`TRow` 的属性名必须与目标表列名逐一对应（`Id` 不在其中——由数据库生成）。
    /// 【⚠ 硬前提：目标表的 NOT NULL 列必须**全部**出现在 `TRow` 上】暂存表由 `SELECT TOP 0 * INTO` 建，
    ///   而 `SELECT INTO` **不复制 DEFAULT 约束** ⇒ 目标表里「NOT NULL 但有默认值」的列（如 `[Task]`
    ///   的 `IsLocked` / `IsCriticalPath` / `CreatedAt` / `UpdatedAt`）在暂存表里会变成「NOT NULL 且无默认值」。
    ///   本方法在灌数据**之前**会先查暂存表的非空列，缺列即抛并点名缺哪几列——
    ///   否则只会收到一条指着 `#BulkStage_xxx` 的 SQL 错误，根本看不出缺的是哪一列。
    /// </remarks>
    public async Task<long[]> BulkInsertWithIdentityAsync<TRow>(
        IReadOnlyList<TRow> rows,
        string targetTable,
        IDbConnection connection,
        IDbTransaction transaction,
        int chunkRows = 50_000,
        int timeoutSeconds = 1800,
        Action<int>? onChunkDone = null)
        where TRow : class
    {
        if (rows.Count == 0) return Array.Empty<long>();
        if (connection is not SqlConnection sqlConnection)
            throw new InvalidOperationException(
                $"BulkInsertWithIdentityAsync 需要 SqlConnection，实得 {connection?.GetType().FullName ?? "null"}");
        if (transaction is not SqlTransaction sqlTransaction)
            throw new InvalidOperationException(
                $"BulkInsertWithIdentityAsync 需要同连接的 SqlTransaction，实得 {transaction?.GetType().FullName ?? "null"}");

        var columns = ResolveBulkColumns(rows);
        var stage = $"#BulkStage_{Guid.NewGuid():N}";
        var columnList = string.Join(", ", columns.Select(c => c.Name));
        var sourceValues = string.Join(", ", columns.Select(c => $"src.{c.Name}"));

        // ① 暂存表：结构逐列同源（含 IDENTITY／类型／可空性），只多一列 __i
        var ddl = $@"
SELECT TOP 0 * INTO [{stage}] FROM {targetTable};
ALTER TABLE [{stage}] ADD __i int NOT NULL;";
        Execute(ddl, sqlConnection, sqlTransaction);

        try
        {
            // ①' 预检：暂存表没有 DEFAULT（`SELECT INTO` 不复制默认约束），所以目标表的每个 NOT NULL 列
            //     都必须在行对象里出现，否则灌数据时只会得到一条指着 `#BulkStage_xxx` 的错误。
            var missingColumns = FindMissingNotNullColumns(sqlConnection, sqlTransaction, stage, columns);
            if (missingColumns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"批量插入 {targetTable}：行对象缺少 {missingColumns.Count} 个 NOT NULL 列" +
                    $"（{string.Join(", ", missingColumns)}）。暂存表由 SELECT INTO 建立、不继承 DEFAULT，" +
                    "故这些列必须由行对象显式提供。");
            }

            // ② 灌暂存表——**分块**：一次把 121 万行建成一个 `DataTable` 会占 1~2 GB，
            //    块大小只控内存峰值，不改变事务边界（全部块仍在同一 tx 内）。
            //    暂存表无约束，用 Default 求吞吐；约束由 ④ 的 MERGE 对目标表全程校验。
            for (var offset = 0; offset < rows.Count; offset += chunkRows)
            {
                var take = Math.Min(chunkRows, rows.Count - offset);
                using var stageTable = BuildBulkDataTable(rows, offset, take, columns, includeIndex: true);
                using var bulkCopy = new SqlBulkCopy(sqlConnection, SqlBulkCopyOptions.Default, sqlTransaction)
                {
                    DestinationTableName = $"[{stage}]",
                    BulkCopyTimeout = timeoutSeconds,
                    BatchSize = 0
                };
                foreach (var column in columns)
                    bulkCopy.ColumnMappings.Add(column.Name, column.Name);
                bulkCopy.ColumnMappings.Add("__i", "__i");
                await bulkCopy.WriteToServerAsync(stageTable);
                onChunkDone?.Invoke(offset + take);
            }

            // ③ 一条 MERGE 拿回 (Id, __i)：`ON 1=0` ⇒ 只走 WHEN NOT MATCHED 的 INSERT，
            //    但 OUTPUT 里可以引用源列 `src.__i`，这正是 `INSERT … OUTPUT` 做不到的那一步。
            var mergeSql = $@"
MERGE INTO {targetTable} WITH (TABLOCK) AS t
USING [{stage}] AS src ON 1 = 0
WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({sourceValues})
OUTPUT INSERTED.Id, src.__i;";

            var ids = new long[rows.Count];
            var filled = new bool[rows.Count];
            using (var command = sqlConnection.CreateCommand())
            {
                command.Transaction = sqlTransaction;
                command.CommandText = mergeSql;
                command.CommandTimeout = timeoutSeconds;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var index = reader.GetInt32(1);
                    ids[index] = id;
                    filled[index] = true;
                }
            }

            // ④ 行数必须一一对上——对不上说明 MERGE 没插全，宁可让整事务回滚也不返回错位映射。
            for (var i = 0; i < filled.Length; i++)
            {
                if (!filled[i])
                    throw new InvalidOperationException(
                        $"{targetTable} 批量插入回传的 Id 少了第 {i} 行（共 {rows.Count} 行）⇒ 拒绝返回错位的 Id 映射");
            }

            return ids;
        }
        finally
        {
            // ⑤ 暂存表清理：即便 MERGE 抛错也要删（事务回滚不会删 #temp）
            try { Execute($"DROP TABLE IF EXISTS [{stage}];", sqlConnection, sqlTransaction); }
            catch { /* 清理失败不掩盖原始异常 */ }
        }
    }

    private readonly record struct BulkColumn(string Name, Type Type, PropertyInfo Property);

    /// <summary>
    /// 取列定义。默认按 `TRow` 的声明类型；当 `TRow` 就是 `object`（调用方用 `List&lt;object&gt;` 攒匿名行）时，
    /// 退回按**首行的运行时类型**——否则反射到 `object` 上会得到 0 列，`SqlBulkCopy` 会灌进一张空表而不报错。
    /// 这类调用一旦混入异构行，列映射必然错位 ⇒ 逐行校验类型，不同即抛。
    /// </summary>
    private static BulkColumn[] ResolveBulkColumns<TRow>(IReadOnlyList<TRow> rows) where TRow : class
    {
        var declared = typeof(TRow);
        var actual = declared == typeof(object) ? rows[0].GetType() : declared;
        if (actual != declared)
        {
            for (var i = 1; i < rows.Count; i++)
            {
                if (rows[i].GetType() != actual)
                    throw new InvalidOperationException(
                        $"BulkInsert 的行对象类型不一致：第 0 行是 {actual.FullName}，第 {i} 行是 {rows[i].GetType().FullName}" +
                        "⇒ 列映射会错位，拒绝继续");
            }
        }

        var columns = actual.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => new BulkColumn(
                p.Name,
                Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType,
                p))
            .ToArray();

        if (columns.Length == 0)
            throw new InvalidOperationException(
                $"BulkInsert 的行对象类型 {actual.FullName} 没有任何可读属性 ⇒ 无法建立列映射");

        return columns;
    }

    private static DataTable BuildBulkDataTable<TRow>(
        IReadOnlyList<TRow> rows, int offset, int count, BulkColumn[] columns, bool includeIndex = false)
    {
        var table = new DataTable();
        foreach (var column in columns)
            table.Columns.Add(column.Name, column.Type);
        if (includeIndex)
            table.Columns.Add("__i", typeof(int));

        for (var i = 0; i < count; i++)
        {
            var source = rows[offset + i];
            var row = table.NewRow();
            for (var c = 0; c < columns.Length; c++)
                row[c] = columns[c].Property.GetValue(source) ?? DBNull.Value;
            if (includeIndex)
                row[columns.Length] = offset + i;   // 全局行序：分块灌暂存表时必须是**源集合**里的绝对下标
            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>暂存表里「NOT NULL 且非 IDENTITY」、但行对象没提供的列名。</summary>
    private static List<string> FindMissingNotNullColumns(
        SqlConnection connection, SqlTransaction transaction, string stage, BulkColumn[] columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // ⚠ 必须是 `tempdb..`（**两个点**）：`OBJECT_ID('tempdb.' + @stage)` 在 @stage 是**变量**时恒返回 NULL
        //   （`tempdb.#x` 被解析成「库.架构」），预检会因此变成永不触发的死代码 —— 2026-10-29 实测踩到，
        //   由 `BulkInsertWithIdentityAsync_RejectsRowsMissingNotNullColumns` 断言守住。
        // `__i` 是本方法自己加的序号列（调用方**必须**不提供它），故排除，否则每张表都会被误报「缺 1 列」。
        command.CommandText =
            "SELECT name FROM tempdb.sys.columns WHERE object_id = OBJECT_ID('tempdb..' + @stage) " +
            "AND is_nullable = 0 AND is_identity = 0 AND name <> '__i'";
        command.Parameters.AddWithValue("@stage", stage);

        var provided = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!provided.Contains(name)) missing.Add(name);
        }
        return missing;
    }

    private static void Execute(string sql, SqlConnection connection, SqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 执行事务操作
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> operation, DatabaseId db = DatabaseId.APS)
    {
        var connection = await GetConnectionAsync(db);
        try
        {
            if (connection is SqlConnection sqlConnection)
            {
                using var transaction = await sqlConnection.BeginTransactionAsync();
                try
                {
                    var result = await operation(connection, transaction);
                    await transaction.CommitAsync();
                    return result;
                }
                catch (Exception ex)
                {
                    // 【2026-10-08 修复】原实现直接 `await transaction.RollbackAsync()`：一旦**回滚自己**抛异常
                    // （本事务可能已插入百万级行——BOM 接货一批 680 万行——回滚要逐行 undo，本身就可能
                    // 跑很久乃至超时），该异常会**顶替原始异常**向外传播。
                    // ⚠️ 与恢复模式无关：2026-10-08 当时 APS_Production 是 FULL（日志近满 ⇒ 回滚代价更大），
                    //    但即便改成 SIMPLE，只要回滚自身抛错，顶替效应就照样发生 ⇒ 此修法必须保留。
                    // 后果：调用方只看到「执行超时」而看不到真正的原因（业务逻辑异常）。
                    // 2026-10-08 BOM 接货连续两次「超时」即此：真因是 ToDictionary 重复键（毫秒即抛），
                    // 却被约 9 分钟的巨额回滚超时盖住，根因因此被排查了两轮。
                    // ⇒ 现在：先留痕原始异常 → 尽力回滚 → **始终重抛原始异常**；回滚失败则丢弃该连接
                    //   （带着未结束的事务复用同一 SqlConnection 会毒化后续所有调用，且可能持锁阻塞全库读）。
                    _logger?.LogError(ex, "事务内操作失败 (db={Db})，开始回滚；本事务已插入的数据将全部撤销", db);
                    try
                    {
                        await transaction.RollbackAsync();
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger?.LogError(rollbackEx,
                            "事务回滚失败 (db={Db})：原始异常见上一条（本条的 message 不含真因）。" +
                            "已丢弃该连接、下次调用重建；若服务端仍残留活动事务需人工确认，否则会阻塞全库读取。", db);
                        DiscardConnection(db);
                    }
                    throw;
                }
            }
            else
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    var result = await operation(connection, transaction);
                    transaction.Commit();
                    return result;
                }
                catch (Exception ex)
                {
                    // 同上（SqlConnection 分支）：回滚失败不得顶替原始异常。
                    _logger?.LogError(ex, "事务内操作失败 (db={Db})，开始回滚；本事务已插入的数据将全部撤销", db);
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger?.LogError(rollbackEx,
                            "事务回滚失败 (db={Db})：原始异常见上一条（本条的 message 不含真因）。已丢弃该连接。", db);
                        DiscardConnection(db);
                    }
                    throw;
                }
            }
        }
        finally
        {
            ReleaseConnection(db);
        }
    }

    /// <summary>
    /// 跨库流式传输：从源库 ExecuteReader 流式读取，通过 SqlBulkCopy 写入目标库
    /// 适用于百万级行跨库搬运（如 ODS.MES_APS_BOM_Workset → APS.APS_BOM_RAW）
    /// ⚠️ 全程流式处理，内存占用与行数无关
    /// </summary>
    public async Task BulkCopyFromReaderAsync(
        string sourceSql,
        object? sourceParameters,
        DatabaseId sourceDb,
        string destinationTable,
        DatabaseId destinationDb,
        IDictionary<string, string>? columnMappings = null,
        int batchSize = 10000,
        int timeoutSeconds = 600)
    {
        var sourceConnection = await GetConnectionAsync(sourceDb);
        var destConnection = await GetConnectionAsync(destinationDb);
        try
        {
            var sqlSourceConn = (SqlConnection)sourceConnection;
            var sqlDestConn = (SqlConnection)destConnection;

            using var command = new SqlCommand(sourceSql, sqlSourceConn);
            command.CommandTimeout = timeoutSeconds;

            if (sourceParameters != null)
            {
                foreach (var prop in sourceParameters.GetType().GetProperties())
                {
                    command.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(sourceParameters) ?? DBNull.Value);
                }
            }

            using var reader = await command.ExecuteReaderAsync();
            using var bulkCopy = new SqlBulkCopy(sqlDestConn)
            {
                DestinationTableName = destinationTable,
                BatchSize = batchSize,
                BulkCopyTimeout = timeoutSeconds
            };

            if (columnMappings != null)
            {
                foreach (var mapping in columnMappings)
                {
                    bulkCopy.ColumnMappings.Add(mapping.Key, mapping.Value);
                }
            }
            else
            {
                // 自动映射同名列
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    bulkCopy.ColumnMappings.Add(name, name);
                }
            }

            await bulkCopy.WriteToServerAsync(reader);
        }
        finally
        {
            ReleaseConnection(sourceDb);
            ReleaseConnection(destinationDb);
        }
    }

    /// <summary>
    /// 跨库流式传输（参与目标库事务版）：从源库流式读取，SqlBulkCopy 写入调用方提供的事务连接上的目标表。
    /// 与 BulkCopyFromReaderAsync 的区别：目标连接/事务由调用方传入（用于包在 ExecuteInTransactionAsync 内），
    /// 本方法只负责打开并释放「源库」连接；⚠️ 若 sourceDb == destinationDb 会因同库信号量重入死锁，调用方须保证跨库。
    /// </summary>
    public async Task BulkCopyFromReaderToTransactionAsync(
        string sourceSql,
        object? sourceParameters,
        DatabaseId sourceDb,
        string destinationTable,
        SqlConnection destinationConnection,
        SqlTransaction destinationTransaction,
        IDictionary<string, string>? columnMappings = null,
        int batchSize = 10000,
        int timeoutSeconds = 600)
    {
        var sourceConnection = await GetConnectionAsync(sourceDb);
        try
        {
            var sqlSourceConn = (SqlConnection)sourceConnection;

            using var command = new SqlCommand(sourceSql, sqlSourceConn);
            command.CommandTimeout = timeoutSeconds;

            if (sourceParameters != null)
            {
                foreach (var prop in sourceParameters.GetType().GetProperties())
                {
                    command.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(sourceParameters) ?? DBNull.Value);
                }
            }

            using var reader = await command.ExecuteReaderAsync();
            using var bulkCopy = new SqlBulkCopy(destinationConnection, SqlBulkCopyOptions.Default, destinationTransaction)
            {
                DestinationTableName = destinationTable,
                BatchSize = batchSize,
                BulkCopyTimeout = timeoutSeconds
            };

            if (columnMappings != null)
            {
                foreach (var mapping in columnMappings)
                {
                    bulkCopy.ColumnMappings.Add(mapping.Key, mapping.Value);
                }
            }
            else
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    bulkCopy.ColumnMappings.Add(name, name);
                }
            }

            await bulkCopy.WriteToServerAsync(reader);
        }
        finally
        {
            ReleaseConnection(sourceDb);
        }
    }

    /// <summary>
    /// 测试数据库连接
    /// </summary>
    public async Task<bool> TestConnectionAsync(DatabaseId db = DatabaseId.APS)
    {
        try
        {
            var connection = await GetConnectionAsync(db);
            try
            {
                await connection.ExecuteScalarAsync<int>("SELECT 1");
                return true;
            }
            finally
            {
                ReleaseConnection(db);
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _apsConnection?.Dispose();
            _odsConnection?.Dispose();
            _authConnection?.Dispose();
            _apsSemaphore?.Dispose();
            _odsSemaphore?.Dispose();
            _authSemaphore?.Dispose();
            _disposed = true;
        }
    }
}
