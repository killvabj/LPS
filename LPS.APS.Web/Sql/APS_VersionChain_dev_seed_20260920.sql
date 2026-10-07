-- ============================================================================
-- ParameterSet / RuleSet 版本链 dev 种子（4号位 Audit 页沟通项 §四 答复：附录 B）
-- 文件：LPS.APS.Web/Sql/APS_VersionChain_dev_seed_20260920.sql
-- Owner：3号位（规则参数认证权限与运行生命周期 / 版本治理）
-- 日期：2026-09-20
-- 前置（附录 B 依赖，须全满足）：
--   1) APS_Production 已按冻结 DDL v5.1.x 建表（RuleSetVersion / ParameterSetVersion，
--      CK 六态含 ARCHIVED）；本库非 APS_Auth —— 与 aps.audit.view 脚本（APS_Auth）不同库；
--   2) RuleSet / ParameterSet 主表各至少 1 行（RuleSet 经 APS_Setup_dev_seed_20260920.sql 段 B 已
--      确保 ≥1：DEV_SETUP；ParameterSet 经 4号位 09-20 探测 ≥459 行）；
--   3) 应用至少启动过一次（不影响本脚本，VersionCode/Status 直接 INSERT）。
-- 用途（4号位 缺口 B「ParameterSet / RuleSet 维护页」演示前置）：
--   段 1：防 dev 库重建丢失 —— 幂等补种 TEST-RSV-SEED-001 / TEST-PSV-SEED-001（DRAFT 各 1 条，
--         与 09-18 G4 种子同式，重建后重跑即恢复）；
--   段 2：1 个 RuleSet + 1 个 ParameterSet 各补 DRAFT + PUBLISHED + 历史（ARCHIVED）3 版本链
--         （VersionCode 前缀 CHAIN-*，供 diff / history / validate 走查与 U21/U22 类验收演示）。
-- ⚠️ 字段级 diff 差异上限：RuleSetVersion / ParameterSetVersion 含 ContentSnapshotJson 等快照列，
--    冻结 DDL v5.1.7 无这些列（实体 vs DDL 漂移，属 P0-01/P0-02 方案 A 收口、待 2号位 DDL）。
--    故版本链数据先补足，「版本链状态/元数据可见」可验收；字段级 diff 需 2号位 落快照列后才有真实差异。
-- 幂等：存在即跳过 / 前缀判存；可重复执行。
--
-- ⚠️ PRODUCTION MUST REMOVE ⚠️
--   本脚本仅供 dev 联调（同 bootstrap 脚本口径）；生产环境严禁执行。
-- ============================================================================

USE APS_Production;
GO

-- 段 1：防重建丢失 —— 幂等补种 TEST-*-SEED-001（与 09-18 G4 种子同式）
IF NOT EXISTS (SELECT 1 FROM [dbo].[RuleSetVersion] WHERE [VersionCode] = N'TEST-RSV-SEED-001')
    INSERT INTO [dbo].[RuleSetVersion] ([RuleSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'TEST-RSV-SEED-001', N'DRAFT' FROM [dbo].[RuleSet];

IF NOT EXISTS (SELECT 1 FROM [dbo].[ParameterSetVersion] WHERE [VersionCode] = N'TEST-PSV-SEED-001')
    INSERT INTO [dbo].[ParameterSetVersion] ([ParameterSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'TEST-PSV-SEED-001', N'DRAFT' FROM [dbo].[ParameterSet];

-- 段 2：版本链种子（DRAFT + PUBLISHED + 历史 ARCHIVED，VersionCode 前缀 CHAIN-，幂等判存）
IF NOT EXISTS (SELECT 1 FROM [dbo].[RuleSetVersion] WHERE [VersionCode] LIKE N'CHAIN-RSV-%')
BEGIN
    INSERT INTO [dbo].[RuleSetVersion] ([RuleSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-RSV-001-ARCHIVED', N'ARCHIVED' FROM [dbo].[RuleSet] ORDER BY Id;
    INSERT INTO [dbo].[RuleSetVersion] ([RuleSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-RSV-002-PUBLISHED', N'PUBLISHED' FROM [dbo].[RuleSet] ORDER BY Id;
    INSERT INTO [dbo].[RuleSetVersion] ([RuleSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-RSV-003-DRAFT', N'DRAFT' FROM [dbo].[RuleSet] ORDER BY Id;
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[ParameterSetVersion] WHERE [VersionCode] LIKE N'CHAIN-PSV-%')
BEGIN
    INSERT INTO [dbo].[ParameterSetVersion] ([ParameterSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-PSV-001-ARCHIVED', N'ARCHIVED' FROM [dbo].[ParameterSet] ORDER BY Id;
    INSERT INTO [dbo].[ParameterSetVersion] ([ParameterSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-PSV-002-PUBLISHED', N'PUBLISHED' FROM [dbo].[ParameterSet] ORDER BY Id;
    INSERT INTO [dbo].[ParameterSetVersion] ([ParameterSetId], [VersionCode], [Status])
    SELECT TOP 1 Id, N'CHAIN-PSV-003-DRAFT', N'DRAFT' FROM [dbo].[ParameterSet] ORDER BY Id;
END
GO
