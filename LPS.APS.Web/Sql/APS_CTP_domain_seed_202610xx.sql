-- ============================================================================
-- APS dev seed: CTP 评估 DomainDefinition（真实主数据版 v3 · fail-closed · 幂等）
-- 文件：LPS.APS.Web/Sql/APS_CTP_domain_seed_202610xx.sql
-- Owner：3号位（DomainDefinition 治理 / CTP 全栈 Owner）
-- 日期：2026-10-09（重写：废弃 09-30 假数据框架 v2）
-- 依据：
--   · 0号位 2026-10-09 裁决（升级报告 v1.2 §四）：一产品族一 Domain（方案 a）；
--     不同 ProductFamily 不得共用同一 DomainKey；DomainKey 非 ProductFamily.Code 别名，
--     而是 APS 内部稳定业务计算域标识（3号位 治理）；Factory 维度不自动拆域，
--     仅业务计算/计划发布边界确实独立才用 Factory+ProductFamily（FACTORY_FAMILY）。
--   · dev 实查（2026-10-09，10.116.2.75/APS_Production 只读）：
--       - ProductFamily 真实 1~10：CYL/FRL/VALVE/LGJ/XC/JS/WKQ/TZP/MJ/QT（IsActive=1）
--       - Factory 真实：CN(2)/BJ(3)/CN6注(4)/SH(5)/TJ(6)（IsActive=1）
--       - DomainDefinition 现有 3 键（历史运行兼容，**保持不动**）：
--           FAMILY_X     FAMILY          PF=1 CYL
--           BJ_FAMILY_Y  FACTORY_FAMILY  PF=2 FRL, F=BJ(3)
--           TJ_FAMILY_Y  FACTORY_FAMILY  PF=2 FRL, F=TJ(6)
--       - Material.ProductFamilyId 真实映射已存量 ~27 万条（CYL 24.1万/FRL 1.1万/
--         TZP 4.5千/MJ 4.3千/VALVE 4千/XC 2千/QT 1.9千/JS 1.3千/WKQ 50/LGJ 48）
--         → **无需 UPDATE Material**（与 09-30 假数据版 v2 不同，本版不写 Material）。
--   · 4号位 函《4号位-2026-09-30-CTP评估主数据迁移-给3号位(1).md》§三 示例（M-001..M-007）
--     系前端硬编码演示假数据，**不入本脚本**（真实冒烟基于真实 dev 主数据）。
--   · 真实 DDL v5.1.8.4 §2.4aa DomainDefinition（UQ_DomainDefinition_DomainKey、
--     ScopeType CHECK FAMILY/FACTORY_FAMILY、FAMILY 须 FactoryId NULL）。
--
-- Why：CYL/FRL 之外 8 个真实产品族（VALVE/LGJ/XC/JS/WKQ/TZP/MJ/QT）无任何
--      DomainDefinition → 该族物料 resolve 命中数=0 → 400「无法自动确定 DomainKey」。
-- What：仅 INSERT 8 条 DomainDefinition（ScopeType=FAMILY，全工厂共享域，FactoryId NULL），
--        对应 8 个无域真实产品族。DB：APS_Production。INSERT，无 DDL（红线 #6 合规）。
--
-- 域键命名（3号位 治理，符合裁决「非 Code 别名」）：
--   · 前缀 FAMILY_ 为域类型标记（对齐既有 FAMILY_X 前缀风格），非 ProductFamily.Code 直用；
--   · 产品族段取可读英文（VALVE/LGJ/XC/JS/WKQ/TZP/MJ/QT），稳定业务计算域标识；
--   · 若 0号位/业务 后续调整命名，仅改本文件数据值（域键为治理数据，非接口契约，零成本）。
--
-- ScopeType：全部 FAMILY（FactoryId NULL）——按裁决「Factory 维度不自动拆域」；
--   FRL 既有 BJ/TJ 两厂 FACTORY_FAMILY 系历史已存业务边界，保持不动（不新增、不删改、
--   不合并）；其余 8 族未举证独立计算/发布边界 → 默认全厂共享域。
--
-- Idempotent：8 产品族存在守卫（fail-closed 防 515）+ 每域键 IF NOT EXISTS（幂等可重跑）。
--   ⚠️ 幂等锚 = DomainKey（一产品族一域 → 域键全局唯一），非 (ProductFamilyId+ScopeType)。
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
-- 0. 前置自检：8 个真实产品族（Code）是否已存在。
--    fail-closed：任一缺失 → 打印缺失清单 + 跳过全部 INSERT（不报 515）。
-- ---------------------------------------------------------------------------
DECLARE @missingFamilies NVARCHAR(MAX) = N'';
SELECT @missingFamilies = @missingFamilies + v.Code + N'; '
  FROM (VALUES (N'VALVE'),(N'LGJ'),(N'XC'),(N'JS'),(N'WKQ'),(N'TZP'),(N'MJ'),(N'QT')) AS v(Code)
  LEFT JOIN ProductFamily pf ON pf.Code = v.Code
 WHERE pf.Id IS NULL;

IF @missingFamilies <> N''
BEGIN
    PRINT N'-- ⚠️ CTP dev seed 跳过：真实 ProductFamily 缺失（dev 主数据未就绪），缺失清单：' + @missingFamilies;
    PRINT N'--    本脚本 fail-closed：产品族未就绪前不执行任何 INSERT（避免 515 错误）。';
    PRINT N'--    请补齐 dev ProductFamily 主数据后重跑本脚本。';
END
ELSE
BEGIN
    -- -----------------------------------------------------------------------
    -- 1. DomainDefinition seed（8 个真实无域产品族 → FAMILY 全厂共享域）
    --    DomainName/ProductFamilyId 均以 ProductFamily.Code 子查询取值（避免硬编码中文乱码）
    --    SortOrder=100（介于既有 FAMILY_X=99 与 BJ_FAMILY_Y=110 之间；8 键分属不同产品族，
    --    与既有键无同族交集，SortOrder 不参与歧义判定，仅定 resolve 同族多命中时的优先序）
    -- -----------------------------------------------------------------------

    -- VALVE（阀门）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_VALVE')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_VALVE', (SELECT Name FROM ProductFamily WHERE Code = N'VALVE'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'VALVE'), NULL, 1, 100);

    -- LGJ（罗茨机）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_LGJ')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_LGJ', (SELECT Name FROM ProductFamily WHERE Code = N'LGJ'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'LGJ'), NULL, 1, 100);

    -- XC（型材）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_XC')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_XC', (SELECT Name FROM ProductFamily WHERE Code = N'XC'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'XC'), NULL, 1, 100);

    -- JS（减速机）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_JS')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_JS', (SELECT Name FROM ProductFamily WHERE Code = N'JS'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'JS'), NULL, 1, 100);

    -- WKQ（温控器）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_WKQ')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_WKQ', (SELECT Name FROM ProductFamily WHERE Code = N'WKQ'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'WKQ'), NULL, 1, 100);

    -- TZP（特种泵）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_TZP')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_TZP', (SELECT Name FROM ProductFamily WHERE Code = N'TZP'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'TZP'), NULL, 1, 100);

    -- MJ（模具）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_MJ')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_MJ', (SELECT Name FROM ProductFamily WHERE Code = N'MJ'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'MJ'), NULL, 1, 100);

    -- QT（其他）
    IF NOT EXISTS (SELECT 1 FROM DomainDefinition WHERE DomainKey = N'FAMILY_QT')
    INSERT INTO DomainDefinition (DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder)
    VALUES (N'FAMILY_QT', (SELECT Name FROM ProductFamily WHERE Code = N'QT'), N'FAMILY',
            (SELECT Id FROM ProductFamily WHERE Code = N'QT'), NULL, 1, 100);
END
GO

-- ---------------------------------------------------------------------------
-- 2. 域键唯一性自检（UQ_DomainDefinition_DomainKey 违反排查；读，安全）
--    预期：空结果集（无重复域键）。
-- ---------------------------------------------------------------------------
SELECT DomainKey, COUNT(*) AS Cnt FROM DomainDefinition
 WHERE DomainKey LIKE N'FAMILY_%' OR DomainKey IN (N'FAMILY_X', N'BJ_FAMILY_Y', N'TJ_FAMILY_Y')
 GROUP BY DomainKey HAVING COUNT(*) > 1;
GO

-- ---------------------------------------------------------------------------
-- 3. 歧义自检（对齐 4号位 函 §七：真实映射物料 × 真实工厂 每组合 HitCount=1，歧义=空集）
--    选 8 个新域族各 1 条真实映射物料（ProductFamilyId 已存）× CN/BJ/SH/TJ 4 真实工厂。
--    预期：空结果集（每组合恰 1 命中；FRL 族不在此列——其 BJ/TJ 既有 FACTORY_FAMILY
--    边界为历史业务边界，CN/SH 命中 0 属既有行为，非本脚本引入）。
-- ---------------------------------------------------------------------------
SELECT m.MaterialCode, f.Code AS FactoryCode, COUNT(*) AS HitCount
FROM Material m
CROSS JOIN Factory f
LEFT JOIN DomainDefinition d ON d.ProductFamilyId = m.ProductFamilyId AND d.IsActive = 1
  AND ((d.ScopeType = N'FAMILY' AND d.FactoryId IS NULL)
    OR (d.ScopeType = N'FACTORY_FAMILY'
        AND EXISTS (SELECT 1 FROM Factory f2 WHERE f2.Id = d.FactoryId AND f2.Code = f.Code)))
WHERE m.Id IN (
    SELECT MIN(m2.Id) FROM Material m2
     WHERE m2.ProductFamilyId IN (SELECT Id FROM ProductFamily WHERE Code IN (N'VALVE',N'LGJ',N'XC',N'JS',N'WKQ',N'TZP',N'MJ',N'QT'))
     GROUP BY m2.ProductFamilyId)
  AND f.Code IN (N'CN',N'BJ',N'SH',N'TJ')
  AND f.IsActive = 1
GROUP BY m.MaterialCode, f.Code
HAVING COUNT(*) <> 1;
GO

-- 附：落库后核对（4号位 函 §五 验收口径）
-- SELECT DomainKey, DomainName, ScopeType, ProductFamilyId, FactoryId, IsActive, SortOrder
--   FROM DomainDefinition ORDER BY Id;   -- 预期 11 行（既有 3 + 新增 8），无 FAMILY_INJECTION 系
-- SELECT COUNT(*) FROM Material WHERE ProductFamilyId IS NOT NULL;  -- 仍 ~27 万（本脚本不写 Material）
