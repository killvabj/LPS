-- ============================================================================
-- APS_Auth dev 种子引导（admin 打通 RBAC「鸡生蛋」）
-- 文件：LPS.APS.Web/Sql/APS_Auth_v1.3_admin_bootstrap_20260910.sql
-- Owner：3号位（认证 / 用户 / 角色 / 权限 / Domain 治理）
-- 日期：2026-09-10
-- 前置：APS_Auth 已按 v1.3 冻结 DDL 重建
--        （13 表 + 7 系统角色 + 34 权限码 + admin 占位 + Global Scope）
-- 用途：解决「鸡生蛋」——
--       1) admin 的 PasswordHash 是 TEMP_PASSWORD_HASH 占位符，登录必败；
--       2) 即便密码正确，admin 也未挂任何权限码，调 RbacController（要求 aps.auth.user.edit）被 403。
--       本脚本用 SQL 绕过 API 初始化 admin，之后 4号位 全部走 POST/PUT /api/rbac/* 自行建测试数据。
-- 说明：
--       测试用户（viewer/pmc/rule_admin/rule_publisher）由 4号位 用 RBAC API 创建，
--       密码在服务端 CreateUserAsync 内自动 PBKDF2 哈希，3号位 不代生成。
--       sys_admin 复用 admin（admin 已挂 aps.admin.system + 全 34 码 + Global），不新建。
--
-- ⚠️ PRODUCTION MUST REMOVE ⚠️
--   本脚本仅供 dev 联调，生产环境严禁使用；admin 万能账号 + 统一密码必须在上生产前清理。
-- ============================================================================

USE APS_Auth;
GO

-- ============================================================================
-- 段 A：替换 admin 密码占位符（明文 Admin@123456 的 PBKDF2-SHA256 哈希）
-- 幂等：仅当 PasswordHash 仍为占位符时替换，防止覆盖已正确设置的哈希。
-- ============================================================================
IF EXISTS (SELECT 1 FROM [User] WHERE LoginName = 'admin' AND PasswordHash = 'TEMP_PASSWORD_HASH')
BEGIN
    UPDATE [User]
    SET PasswordHash = N'PBKDF2$210000$LaGDpA++sOI+YKZN1Cqbxw==$+BO+u7az9neNxRxzV0bYjZoAejura6fqhXgvvz8YHQw=',
        UpdatedAt = GETDATE()
    WHERE LoginName = 'admin';
END
GO

-- ============================================================================
-- 段 B：给 aps.admin.system 挂全部 34 个 V1 功能权限码（幂等，差集补齐）
-- 使 admin 登录后 permissions 含 aps.auth.user.edit（RbacController 全控制器策略），
-- 从而可调用 POST/PUT /api/rbac/* 创建测试用户、分配角色/权限/Scope。
-- ============================================================================
DECLARE @SysAdminRoleId INT = (SELECT Id FROM [Role] WHERE RoleCode = 'aps.admin.system');

IF @SysAdminRoleId IS NOT NULL
BEGIN
    INSERT INTO RolePermission (RoleId, PermissionId)
    SELECT @SysAdminRoleId, p.Id
    FROM [Permission] p
    WHERE NOT EXISTS (
        SELECT 1 FROM RolePermission rp
        WHERE rp.RoleId = @SysAdminRoleId AND rp.PermissionId = p.Id
    );
END
GO