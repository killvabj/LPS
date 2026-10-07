-- ============================================================================
-- APS dev seed: OPM 治理页 (RoutingOperation) - 6 条工序，OPM 三态 2/2/2
-- 文件：LPS.APS.Web/Sql/APS_OPM_dev_seed_20260929.sql
-- Owner：3号位（RoutingOperation 实体 / OPM Governance T1 Owner）
-- 日期：2026-09-29
-- 来源：4号位 2026-09-29 OPM dev seed 催办（R1 出件），采纳选项「运行时动态查表」。
--
-- Why：4号位 Opm.vue 前端静态 + 端点契约已闭环，但 dev RoutingOperation 表 0 行
--      （数据源 ext_MES_APS_Routing_Operation_View dev 无视图、routing-sync cron NeverFire），
--      缺真实数据端到端验证。
-- What：INSERT 6 条 RoutingOperation（1 个 demo 物料，OPM 分布 FINITE_RESOURCE×2 /
--      UNCONSTRAINED×2 / WAIT_ONLY×2，中文工序名 OP10~OP60，PathId 1/2/3 演示多路径）。
-- DB：  APS_Production. INSERT only，no DDL（红线 #6 合规）。
--
-- ⚠️ FK 主数据口径（重要）：
--      冻结 DDL 只 seed 了 MaterialMapping / MaterialSupplyContext（2号位 管线），
--      Material / ProductionDepartment 主数据来自 ODS ext 视图同步，仓库无 seed。
--      → 本脚本「运行时动态查表」：MaterialId 取第一行，部门取前 3 行（不足回填第 1 行），
--        避免硬编码导致 FK violation；主数据为空则打印提示并整段跳过（不报错）。
--
-- Idempotent：guarded by (MaterialId, OperationCode='OP10')；六行同批插入，可重复执行。
-- 字段偏离说明（dev-only，不参与排程/OPM 治理业务）：
--   1) ProcessType CLEANING/TRANSFER/TESTING/PACKAGING 超出冻结文档口径
--      {MACHINING/ASSEMBLY/INSPECTION}——ProcessType 为辅助分类标签、无 CHECK/FK、不参与
--      BOM↔Routing 对接与 1号位 排程，仅对齐前端 mock 展示。
--   2) StageCode SMT/ASM/PKG 未校验是否在 StageDict（StageCode 无 FK，仅业务约定）。
-- NOTE：文件为 UTF-8 无 BOM，须 -f 65001 执行。
-- 执行命令：
--   sqlcmd -S <server> -U <user> -P <pwd> -C -b -f 65001 -i APS_OPM_dev_seed_20260929.sql
--
-- ⚠️ WARNING: dev-only seed; NEVER run against production. ⚠️
-- ============================================================================

SET NOCOUNT ON;

USE APS_Production;
GO

-- ---------------------------------------------------------------------------
-- 运行时 FK 祖先查表（Material / ProductionDepartment，见头注）
-- ---------------------------------------------------------------------------
DECLARE @materialId INT = (SELECT TOP (1) Id FROM Material ORDER BY Id);

DECLARE @dept TABLE (Seq INT PRIMARY KEY, DeptId INT NOT NULL);
INSERT INTO @dept (Seq, DeptId)
SELECT ROW_NUMBER() OVER (ORDER BY Id), Id
  FROM (SELECT Id FROM ProductionDepartment) d;

DECLARE @dept1 INT = (SELECT DeptId FROM @dept WHERE Seq = 1);
DECLARE @dept2 INT = (SELECT DeptId FROM @dept WHERE Seq = 2);
DECLARE @dept3 INT = (SELECT DeptId FROM @dept WHERE Seq = 3);

-- 部门不足 3 个时回填第 1 个，保证 PathId 1/2/3 均可插入
IF @dept2 IS NULL SET @dept2 = @dept1;
IF @dept3 IS NULL SET @dept3 = @dept1;

IF @materialId IS NULL OR @dept1 IS NULL
BEGIN
    PRINT N'-- ⚠️ OPM dev seed 跳过：dev 缺 Material 或 ProductionDepartment 主数据（FK 祖先）。'
    PRINT N'--    请先打通 ODS 主数据同步或联系 2号位 补齐主数据后再重跑。';
END
ELSE IF NOT EXISTS (SELECT 1 FROM RoutingOperation WHERE MaterialId = @materialId AND OperationCode = N'OP10')
BEGIN
    INSERT INTO RoutingOperation (
        MaterialId, ProductionDepartmentId, RouteCode, PathId,
        OperationCode, OperationName, ProcessType, StageCode, OperationPlanningMode,
        StandardDuration, SetupTime, IsActive, CreatedAt, UpdatedAt)
    VALUES
        (@materialId, @dept1, N'DEFAULT', 1, N'OP10', N'精修',     N'MACHINING', N'SMT', N'FINITE_RESOURCE', 30, 0, 1, GETDATE(), GETDATE()),
        (@materialId, @dept1, N'DEFAULT', 1, N'OP20', N'清洗',     N'CLEANING',  N'SMT', N'UNCONSTRAINED',   15, 0, 1, GETDATE(), GETDATE()),
        (@materialId, @dept1, N'DEFAULT', 1, N'OP30', N'转运',     N'TRANSFER',  N'SMT', N'WAIT_ONLY',        5, 0, 1, GETDATE(), GETDATE()),
        (@materialId, @dept2, N'DEFAULT', 2, N'OP40', N'装配',     N'ASSEMBLY',  N'ASM', N'FINITE_RESOURCE', 45, 0, 1, GETDATE(), GETDATE()),
        (@materialId, @dept2, N'DEFAULT', 2, N'OP50', N'老化测试', N'TESTING',   N'ASM', N'WAIT_ONLY',       60, 0, 1, GETDATE(), GETDATE()),
        (@materialId, @dept3, N'ALT',     3, N'OP60', N'包装',     N'PACKAGING', N'PKG', N'FINITE_RESOURCE', 10, 0, 1, GETDATE(), GETDATE());

    PRINT N'-- Inserted 6 RoutingOperation rows for MaterialId=' + CAST(@materialId AS NVARCHAR(20))
        + N', Depts=[' + ISNULL(CAST(@dept1 AS NVARCHAR(10)), N'?')
        + N',' + ISNULL(CAST(@dept2 AS NVARCHAR(10)), N'?')
        + N',' + ISNULL(CAST(@dept3 AS NVARCHAR(10)), N'?') + N']';
END
ELSE
BEGIN
    PRINT N'-- Already seeded (MaterialId=' + CAST(@materialId AS NVARCHAR(20)) + N' + OP10), skip --';
END
GO

-- ---------------------------------------------------------------------------
-- Verify echo（4号位 V2/V6 核对：COUNT=6 且不重复）
-- ---------------------------------------------------------------------------
SELECT OperationCode, OperationName, ProductionDepartmentId, RouteCode, PathId,
       OperationPlanningMode, StandardDuration, IsActive
  FROM RoutingOperation
 WHERE MaterialId = (SELECT TOP (1) Id FROM Material ORDER BY Id)
 ORDER BY RouteCode, PathId, OperationCode;

SELECT COUNT(*) AS OPM_Seed_RowCount
  FROM RoutingOperation
 WHERE MaterialId = (SELECT TOP (1) Id FROM Material ORDER BY Id)
   AND OperationCode IN (N'OP10', N'OP20', N'OP30', N'OP40', N'OP50', N'OP60');