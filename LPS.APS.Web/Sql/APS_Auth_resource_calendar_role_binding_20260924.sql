-- ============================================================================
-- ResourceCalendar / ManualCapacity 6 权限码 角色绑定 dev 脚本
-- 文件：LPS.APS.Web/Sql/APS_Auth_resource_calendar_role_binding_20260924.sql
-- Owner：3号位（认证 / 用户 / 角色 / 权限 / 审计 Owner）
-- 日期：2026-09-24
-- 依据：
--   - 5号位 2026-09-24《ResourceCalendar与ManualCapacity权限角色绑定申请》G5
--   - 4号位 2026-09-24《ResourceCalendar六权限码角色绑定缺口》需求件
--   - 冻结文档 v1.3《APS_V1_Resource_Calendar资源日历能力补充冻结方案_v1.3》§八
-- 前置：
--   1) APS_Auth 已按 v1.3 冻结 DDL 重建（Role 含 aps.viewer.management / aps.planner /
--      aps.admin.aps / aps.admin.system）；
--   2) 应用至少启动过一次（PermissionSeedService 已将 6 码落库，见 43 码清单）。
-- 用途（角色映射裁决 2026-09-24，3号位 定）：
--   段 1：view → aps.viewer.management（只读监督，同 Setup 先例）
--   段 2：view + edit → aps.planner（计划员维护建/改窗，但不持 delete；破坏性删除收在 admin）
--   段 3：全 6 码 → aps.admin.aps / aps.admin.system（同 Setup 先例）
--   其余角色不涉及。
-- 幂等：差集补齐 / 存在即跳过；可重复执行。
-- 执行命令（⚠️ 必须 -f 65001：本文件为 UTF-8 无 BOM，sqlcmd 默认按 GBK OEM 读，
--   无该参数时报 137「必须声明标量变量」）：
--   sqlcmd -S <server> -U <user> -P <pwd> -C -b -f 65001 -i APS_Auth_resource_calendar_role_binding_20260924.sql
--
-- ⚠️ PRODUCTION MUST REMOVE ⚠️
--   本脚本仅供 dev 联调；生产环境严禁执行。
-- ============================================================================

USE APS_Auth;
GO

-- ============================================================================
-- 段 1：view（2 码）→ aps.viewer.management
-- ============================================================================
DECLARE @Rcv1 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.view');
DECLARE @Mcv1 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.view');

INSERT INTO RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermId
FROM [Role] r
CROSS JOIN (VALUES (@Rcv1), (@Mcv1)) AS p(PermId)
WHERE r.RoleCode = 'aps.viewer.management'
  AND p.PermId IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermId
  );
GO

-- ============================================================================
-- 段 2：view + edit（4 码）→ aps.planner（2026-09-24 裁决：不含 delete）
-- ============================================================================
DECLARE @Rcv2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.view');
DECLARE @Rce2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.edit');
DECLARE @Mcv2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.view');
DECLARE @Mce2 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.edit');

INSERT INTO RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermId
FROM [Role] r
CROSS JOIN (VALUES (@Rcv2), (@Rce2), (@Mcv2), (@Mce2)) AS p(PermId)
WHERE r.RoleCode = 'aps.planner'
  AND p.PermId IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermId
  );
GO

-- ============================================================================
-- 段 3：全 6 码 → aps.admin.aps / aps.admin.system
-- ============================================================================
DECLARE @Rcv3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.view');
DECLARE @Rce3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.edit');
DECLARE @Rcd3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.resource_calendar.delete');
DECLARE @Mcv3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.view');
DECLARE @Mce3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.edit');
DECLARE @Mcd3 INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.manual_capacity.delete');

INSERT INTO RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermId
FROM [Role] r
CROSS JOIN (VALUES (@Rcv3), (@Rce3), (@Rcd3), (@Mcv3), (@Mce3), (@Mcd3)) AS p(PermId)
WHERE r.RoleCode IN ('aps.admin.aps', 'aps.admin.system')
  AND p.PermId IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermId
  );
GO