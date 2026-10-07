-- ============================================================================
-- APS dev seed: CTP 评估 DomainDefinition + Material.ProductFamilyId（框架预填版 v2 · fail-closed）
-- 文件：LPS.APS.Web/Sql/APS_CTP_domain_seed_202610xx.sql
-- Owner：3号位（SQL 翻译，按 5号位 业务定义表机械翻译）
-- 日期：2026-09-30（预填框架 v2）；执行日期：5号位 业务定义交付（2026-10-08）后
-- 来源：4号位 函《4号位-2026-09-30-CTP评估主数据迁移-给3号位.md》§三 模板
--       + 真实 DDL v5.1.8.2（表结构以此为准）
--
-- Why：dev 主数据缺 ProductFamily → DomainDefinition 映射，撤下拉后任意物料
--      resolve 命中数=0 → 400「无法自动确定 DomainKey」。
-- What：按 5号位 业务定义表逐行翻译：① UPDATE Material.ProductFamilyId 关联；
--       ② INSERT DomainDefinition（FAMILY / FACTORY_FAMILY 两种 Scope）。
-- DB：  APS_Production. UPDATE + INSERT，无 DDL（红线 #6 合规）。
--
-- ⚠️ v2 修复（2026-09-30，首跑报错 515 后）：
--   v1 缺产品族时仅 PRINT 警告、未中止 → 子查询返 NULL → DomainDefinition.ProductFamilyId
--   NOT NULL 违反 → 515 ×7。v2 改为 **fail-closed**：7 个产品族任一缺失 → 整段跳过
--   UPDATE/INSERT，PRINT 缺失清单后安全退出（对齐 OPM seed 先例「主数据空则跳过」）。
--   产品族就绪（5号位 定义落库）后重跑即正常，幂等可重复。
--
-- ⚠️ 预填状态（重要）：
--   本文件为「框架 + 样例值」占位。值（物料→产品族→域键→ScopeType→工厂）来自
--   4号位 函 §三 样例，**最终以 5号位 业务定义表为准**——5号位 表到位后只改值不改结构。
--
-- ⚠️ 与 4号位 函 §三 模板的四处差异（按真实 DDL 修正，非 5号位 定义范畴）：
--   1) DomainDefinition.DomainName 为 NOT NULL 无默认 → INSERT 必须补 DomainName。
--   2) DomainKey 有 UNIQUE 约束（UQ_DomainDefinition_DomainKey）→ 4号位 函样例
--      BJ_FAMILY_INJECTION 出现 3 次（M-002/M-003/M-007）会**违反唯一约束**。
--      执行前必须核对 5号位 表：同产品族可共享域键，不同产品族不可共用同一 DomainKey。
--   3) ProductFamily 有 Code（UNIQUE）+ Name 两列 → 匹配键由 5号位 表决定
--      （框架预填用 Name 子查询，若 5号位 表给 Code 则换 Code 匹配）。
--   4) Material.ProductFamilyId 为 NULL 可空，UPDATE 直接回填（幂等：仅当为 NULL 时写）。
--
-- Idempotent：产品族存在守卫 + UPDATE 仅 NULL + DomainDefinition IF NOT EXISTS。
-- NOTE：文件为 UTF-8 无 BOM，须 -f 65001 执行。
-- 执行命令：
--   sqlcmd -S <server> -U <user> -P <pwd> -C -b -f 65001 -i APS_CTP_domain_seed_202610xx.sql
--
-- ⚠️ WARNING: dev-only seed; NEVER run against production. ⚠️
-- ============================================================================

SET NOCOUNT ON;

USE APS_Production;
GO

-- ---------------------------------------------------------------------------
-- 0. 前置自检：7 个产品族是否已存在（5号位 业务定义）。
--    fail-closed：任一缺失 → 打印缺失清单 + 跳过全部 UPDATE/INSERT（不报 515）。
-- ---------------------------------------------------------------------------
DECLARE @missingFamilies NVARCHAR(MAX) = N'';
SELECT @missingFamilies = @missingFamilies + v.Name + N'; '
  FROM (VALUES (N'电机壳体'),(N'机柜'),(N'控制箱'),(N'标准泵'),(N'包装套件'),(N'机架焊接件'),(N'电路板组件')) AS v(Name)
  LEFT JOIN ProductFamily pf ON pf.Name = v.Name
 WHERE pf.Id IS NULL;

IF @missingFamilies <> N''
BEGIN
    PRINT N'-- ⚠️ CTP dev seed 跳过：ProductFamily 缺失（5号位 业务定义未落库），缺失清单：' + @missingFamilies;
    PRINT N'--    本脚本 fail-closed：产品族未就绪前不执行任何 UPDATE/INSERT（避免 515 错误）。';
    PRINT N'--    请 5号位 业务定义（2026-10-08）落 ProductFamily 后重跑本脚本。';
END
ELSE
BEGIN
    -- -----------------------------------------------------------------------
    -- 1. Material.ProductFamilyId 回填（按 5号位 表第 2 列 → ProductFamily）
    --    仅回填 NULL（幂等）；ProductFamilyId 非空则不动（已有归族不动）
    -- -----------------------------------------------------------------------
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'电机壳体')
     WHERE MaterialCode = N'M-001' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'机柜')
     WHERE MaterialCode = N'M-002' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'控制箱')
     WHERE MaterialCode = N'M-003' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'标准泵')
     WHERE MaterialCode = N'M-004' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'包装套件')
     WHERE MaterialCode = N'M-005' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'机架焊接件')
     WHERE MaterialCode = N'M-006' AND ProductFamilyId IS NULL;
    UPDATE Material SET ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'电路板组件')
     WHERE MaterialCode = N'M-007' AND ProductFamilyId IS NULL;

    -- -----------------------------------------------------------------------
    -- 2. DomainDefinition seed（按 5号位 表第 3~6 列 → DomainDefinition）
    --    ⚠️ 预填值含 DomainName 补列 + 样例域键；执行前按 5号位 表核对：
    --        - 域键唯一性（UQ_DomainDefinition_DomainKey）
    --        - ScopeType/FactoryId 合法性（CK：FAMILY 必须 FactoryId NULL；
    --          FACTORY_FAMILY 必须非 NULL）
    -- -----------------------------------------------------------------------

    -- M-001 电机壳体-A → 电机壳体（FAMILY，全工厂归域）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'电机壳体'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_INJECTION', N'电机壳体', N'FAMILY', (SELECT Id FROM ProductFamily WHERE Name = N'电机壳体'), NULL, 1, 1);

    -- M-002 机柜上盖-B → 机柜（FAMILY）⚠️ 样例域键 BJ_FAMILY_INJECTION 与 M-003/M-007 冲突（唯一约束），执行前核对
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'BJ_FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'机柜'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'BJ_FAMILY_INJECTION', N'机柜', N'FAMILY', (SELECT Id FROM ProductFamily WHERE Name = N'机柜'), NULL, 1, 1);

    -- M-003 控制箱-C → 控制箱（FACTORY_FAMILY，成都厂）⚠️ 样例域键与 M-002 冲突，执行前核对
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'BJ_FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'控制箱'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'BJ_FAMILY_INJECTION', N'控制箱', N'FACTORY_FAMILY',
            (SELECT Id FROM ProductFamily WHERE Name = N'控制箱'),
            (SELECT Id FROM Factory WHERE Code = N'F-CDG-02'), 1, 1);

    -- M-004 标准泵-D → 标准泵（FAMILY）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'标准泵'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_INJECTION', N'标准泵', N'FAMILY', (SELECT Id FROM ProductFamily WHERE Name = N'标准泵'), NULL, 1, 1);

    -- M-005 包装套件-E → 包装套件（FACTORY_FAMILY，苏州厂）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'包装套件'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_INJECTION', N'包装套件', N'FACTORY_FAMILY',
            (SELECT Id FROM ProductFamily WHERE Name = N'包装套件'),
            (SELECT Id FROM Factory WHERE Code = N'F-SUZ-01'), 1, 1);

    -- M-006 机架焊接件-F → 机架焊接件（FAMILY）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'机架焊接件'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_INJECTION', N'机架焊接件', N'FAMILY', (SELECT Id FROM ProductFamily WHERE Name = N'机架焊接件'), NULL, 1, 1);

    -- M-007 电路板组件-G → 电路板组件（FACTORY_FAMILY，成都厂）⚠️ 样例域键与 M-002/M-003 冲突，执行前核对
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition
                   WHERE DomainKey = N'BJ_FAMILY_INJECTION' AND ProductFamilyId = (SELECT Id FROM ProductFamily WHERE Name = N'电路板组件'))
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'BJ_FAMILY_INJECTION', N'电路板组件', N'FACTORY_FAMILY',
            (SELECT Id FROM ProductFamily WHERE Name = N'电路板组件'),
            (SELECT Id FROM Factory WHERE Code = N'F-CDG-02'), 1, 1);
END
GO

-- ---------------------------------------------------------------------------
-- 3. Verify echo（对齐 4号位 函 §六 自测 SQL：每组合 HitCount=1，歧义=空集）
--    产品族缺失时此段仍执行（只读，安全）——返回空结果集即预期（无映射可查）。
-- ---------------------------------------------------------------------------
SELECT m.MaterialCode, f.Code AS FactoryCode, COUNT(*) AS HitCount
FROM Material m
CROSS JOIN Factory f
LEFT JOIN DomainDefinition d ON d.ProductFamilyId = m.ProductFamilyId AND d.IsActive = 1
  AND ((d.ScopeType = N'FAMILY' AND d.FactoryId IS NULL)
    OR (d.ScopeType = N'FACTORY_FAMILY'
        AND EXISTS (SELECT 1 FROM Factory f2 WHERE f2.Id = d.FactoryId AND f2.Code = f.Code)))
WHERE m.MaterialCode IN (N'M-001',N'M-002',N'M-003',N'M-004',N'M-005',N'M-006',N'M-007')
  AND f.Code IN (N'F-SUZ-01',N'F-CDG-02')
  AND f.IsActive = 1
GROUP BY m.MaterialCode, f.Code
HAVING COUNT(*) <> 1;
GO

-- 附：域键唯一性自检（UQ_DomainDefinition_DomainKey 违反排查）
-- SELECT DomainKey, COUNT(*) AS Cnt FROM DomainDefinition
--  WHERE DomainKey IN (N'FAMILY_INJECTION', N'BJ_FAMILY_INJECTION')
--  GROUP BY DomainKey HAVING COUNT(*) > 1;
