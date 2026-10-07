-- ============================================================================
-- aps.audit.view 4 角色补绑 dev 脚本（4号位 Audit 页沟通项 §二 答复：附录 A）
-- 文件：LPS.APS.Web/Sql/APS_Auth_audit_view_role_binding_20260920.sql
-- Owner：3号位（认证 / 用户 / 角色 / 权限 / 审计 Owner）
-- 日期：2026-09-20
-- 前置：
--   1) APS_Auth 已按 v1.3 冻结 DDL 重建（Role 含 aps.viewer.management / aps.planner /
--      aps.admin.aps / aps.admin.system）；
--   2) 应用至少启动过一次（PermissionSeedService 已将 aps.audit.view 落库）。
-- 用途：
--   审计为监督能力，aps.audit.view → 4 角色均持（采纳 4号位 frontNew/docs/rbac.md §1.2 audit 行口径）。
--   - aps.admin.system 经 APS_Auth_v1.3_admin_bootstrap_20260910.sql 段 B 已持 → 本脚本自动跳过；
--   - aps.viewer.management / aps.planner / aps.admin.aps 需补绑（否则 09-25 切真实模式后访问
--     /aps/audit 将 403）。
-- 幂等：差集补齐 / 存在即跳过；可重复执行。
--
-- ⚠️ PRODUCTION MUST REMOVE ⚠️
--   本脚本仅供 dev 联调（同 bootstrap 脚本口径）；生产环境严禁执行。
-- ============================================================================

USE APS_Auth;
GO

-- aps.audit.view → 4 角色（admin.system 已有则跳过；与 Setup seed 段 A 同范式：幂等差集补齐）
DECLARE @AuditViewPermId INT = (SELECT Id FROM [Permission] WHERE PermissionCode = 'aps.audit.view');

IF @AuditViewPermId IS NOT NULL
BEGIN
    INSERT INTO RolePermission (RoleId, PermissionId)
    SELECT r.Id, @AuditViewPermId
    FROM [Role] r
    WHERE r.RoleCode IN ('aps.viewer.management', 'aps.planner', 'aps.admin.aps', 'aps.admin.system')
      AND NOT EXISTS (
          SELECT 1 FROM RolePermission rp
          WHERE rp.RoleId = r.Id AND rp.PermissionId = @AuditViewPermId
      );
END
GO
