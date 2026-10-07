-- ============================================================================
-- Setup 换型 dev 种子（契约 §十 #3 角色绑定 + §五 dev seed 要求）
-- 文件：LPS.APS.Web/Sql/APS_Setup_dev_seed_20260920.sql
-- Owner：3号位（认证 / 用户 / 角色 / 权限 / Setup 治理）
-- 日期：2026-09-20
-- 前置：
--   1) APS_Auth 已按 v1.3 冻结 DDL 重建（Role 含 aps.viewer.management / aps.planner /
--      aps.admin.aps / aps.admin.system）；
--   2) 应用至少启动过一次（PermissionSeedService 已将 aps.setup.view/edit/publish 落库）；
--   3) APS_Production 已按冻结 DDL v5.1.2+ 建表（RuleSet / RuleSetVersion / ScheduleRun）。
-- 用途：
--   段 A：Setup 换型 3 权限码 → 4 角色绑定（4号位 auth.ts MOCK_ROLE_PRESETS + verify-rbac.mjs 84 组合期望）
--   段 B：≥1 个 DRAFT RuleSetVersion（契约 §五 #1，写端点前置条件）
--   段 C：≥1 个 COMPLETED ScheduleRun（契约 §五 #2 的 runId 来源）
-- 幂等：全部差集补齐 / 存在即跳过；可重复执行。
-- 执行命令（⚠️ 必须 -f 65001：本文件为 UTF-8 无 BOM，sqlcmd 默认按 GBK OEM 读，
--   无该参数时段 A 后半（VALUES 派生表内变量）会报 137「必须声明标量变量」）：
--   sqlcmd -S <server> -U <user> -P <pwd> -C -b -f 65001 -i APS_Setup_dev_seed_20260920.sql
--   已执行：2026-09-20（3号位 sqlcmd 落库 dev，段A 8 绑定 / 段B DEV_SETUP+RSV-DRAFT / 段C COMPLETED Run）。
--
-- ⚠️ PRODUCTION MUST REMOVE ⚠️
--   本脚本仅供 dev 联调（同 bootstrap 脚本口径）；生产环境严禁执行。
-- ============================================================================

USE APS_Auth;
GO

-- ============================================================================
-- 段 A：Setup 换型角色绑定矩阵（契约 §十，与 bootstrap 段 B 同范式：幂等差集补齐）
--   aps.viewer.management → view
--   aps.planner           → view
--   aps.admin.aps         → view + edit + publish
--   aps.admin.system      → view + edit + publish
-- ============================================================================
DECLARE @SetupViewPermId INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.view');
DECLARE @SetupEditPermId INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.edit');
DECLARE @SetupPublishPermId INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.publish');

-- 仅 view 的角色（viewer.management / planner）
IF @SetupViewPermId IS NOT NULL
BEGIN
    INSERT INTO RolePermission (RoleId, PermissionId)
    SELECT r.Id, @SetupViewPermId
    FROM [Role] r
    WHERE r.RoleCode IN ('aps.viewer.management', 'aps.planner')
      AND NOT EXISTS (
          SELECT 1 FROM RolePermission rp
          WHERE rp.RoleId = r.Id AND rp.PermissionId = @SetupViewPermId
      );
END
GO

-- 全 3 码的角色（admin.aps / admin.system）
DECLARE @SetupViewPermId2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.view');
DECLARE @SetupEditPermId2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.edit');
DECLARE @SetupPublishPermId2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.setup.publish');

INSERT INTO RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermId
FROM [Role] r
CROSS JOIN (VALUES (@SetupViewPermId2), (@SetupEditPermId2), (@SetupPublishPermId2)) AS p(PermId)
WHERE r.RoleCode IN ('aps.admin.aps', 'aps.admin.system')
  AND p.PermId IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermId
  );
GO

-- ============================================================================
-- 段 B：≥1 个 DRAFT RuleSetVersion（契约 §五 #1）
--   RuleSetVersion.RuleSetId 为 NOT NULL FK → 先幂等确保 ≥1 RuleSet（RuleSetCode UNIQUE 防重）。
-- ============================================================================
USE APS_Production;
GO

IF NOT EXISTS (SELECT 1 FROM [RuleSet] WHERE RuleSetCode = 'DEV_SETUP')
BEGIN
    INSERT INTO [RuleSet] (RuleSetCode, RuleSetName, Description, IsActive, CreatedAt, CreatedBy)
    VALUES ('DEV_SETUP', N'Setup联调规则集', N'dev seed（Setup 换型联调用，生产删除）', 1, GETDATE(), 'seed');
END
GO

DECLARE @DevRuleSetId BIGINT = (SELECT Id FROM [RuleSet] WHERE RuleSetCode = 'DEV_SETUP');

-- 幂等：库中已存在任一 DRAFT 版本即跳过；VersionCode 带日期后缀避免与已发布版本 UQ(RuleSetId, VersionCode) 冲突。
IF @DevRuleSetId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [RuleSetVersion] WHERE Status = 'DRAFT')
BEGIN
    INSERT INTO [RuleSetVersion] (RuleSetId, VersionCode, Status, CreatedAt, CreatedBy)
    VALUES (@DevRuleSetId, 'DEV-SETUP-DRAFT-' + CONVERT(VARCHAR(8), GETDATE(), 112), 'DRAFT', GETDATE(), 'seed');
END
GO

-- ============================================================================
-- 段 C：≥1 个 COMPLETED ScheduleRun（契约 §五 #2 的 runId 来源）
--   ⚠️ 术语偏差标注：ScheduleRun.Status 冻结四态 = RUNNING/COMPLETED/PARTIAL_SUCCESS/FAILED（v5.1.2 §3.1），
--      并无 "ACTIVE" 态（DDL 中 "ACTIVE" 指 BasePlanVersionId 关联的当前 ACTIVE 计划版本）。
--      契约 §五 「ACTIVE Run」按 COMPLETED 落库——uncovered-stats 的 runId 来源，404 语义一致。
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM [ScheduleRun] WHERE Status = 'COMPLETED' AND RunType = 'FULL_SCHEDULE')
BEGIN
    INSERT INTO [ScheduleRun]
        (RunType, Status, TriggeredBy, DataCutoffTime, ExpectedDomainKeysJson,
         StartedAt, CompletedAt, CreatedAt)
    VALUES
        ('FULL_SCHEDULE', 'COMPLETED', 'seed', GETDATE(), '[]',
         DATEADD(day, -1, GETDATE()), GETDATE(), GETDATE());
END
GO
