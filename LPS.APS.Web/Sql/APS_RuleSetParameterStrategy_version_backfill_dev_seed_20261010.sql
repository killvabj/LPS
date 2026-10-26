-- =====================================================================
-- APS dev seed: backfill PUBLISHED version rows for RuleSet / ParameterSet / StrategyProfile
-- that have IsActive=1 but zero version rows (4号位 P1 2026-10-10 ruleSet#1727)
-- Date: 2026-10-10  |  Author: 3-hao (rule/parameter governance)
--
-- Why: RuleSet master has 666 active rows but only 14 have a PUBLISHED version
--      (652 missing); same gap for ParameterSet (651) / StrategyProfile (653).
--      Detail chain GET /published-version -> 404, fallback /versions -> [] ->
--      frontend throws "无可用版本". Backfill one PUBLISHED version per active
--      master row missing any PUBLISHED version.
-- What: (1) RuleSetVersion + (2) ParameterSetVersion: one 'V1' PUBLISHED row per
--      active master lacking PUBLISHED (and lacking any 'V1' to avoid UQ clash).
--      ContentSnapshotJson = NULL (established dev pattern; see existing PUBLISHED
--      rows). (3) StrategyProfileVersion: one 'V1' PUBLISHED row per active profile
--      lacking PUBLISHED, referencing a representative PUBLISHED RuleSetVersion +
--      ParameterSetVersion (FK both NOT NULL) -- dev placeholder linkage only.
-- DB:   APS_Production. Data INSERT only, no DDL (red-line #6 compliant).
--      Schema v5.1.8.4 actual columns (ContentSnapshotJson nullable).
-- Idempotent: guarded by NOT EXISTS PUBLISHED per master row; VersionCode 'V1'
--      guarded against pre-existing 'V1' rows to respect UQ (master, VersionCode).
-- NOTE: file must be executed with  -f 65001  (UTF-8)  on sqlcmd.
-- WARNING: dev-only seed; NEVER run against production.
-- =====================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

-- ---------------------------------------------------------------------
-- 1) RuleSetVersion: one PUBLISHED 'V1' per active RuleSet without PUBLISHED
-- ---------------------------------------------------------------------
INSERT INTO dbo.RuleSetVersion (
    RuleSetId, VersionCode, Status,
    EffectiveFrom, EffectiveTo, PublishedAt, PublishedBy, ApprovedAt, ApprovedBy,
    CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDefault, Remarks, ContentSnapshotJson)
SELECT r.Id, N'V1', N'PUBLISHED',
       NULL, NULL,
       GETDATE(), N'seed', GETDATE(), N'seed',
       GETDATE(), N'seed', NULL, NULL, 0,
       N'Seed backfill 2026-10-10 (4号位 P1 ruleSet#1727): initial PUBLISHED version for dev联调',
       NULL
  FROM dbo.RuleSet r
 WHERE r.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.RuleSetVersion v
                    WHERE v.RuleSetId = r.Id AND v.Status = N'PUBLISHED')
   AND NOT EXISTS (SELECT 1 FROM dbo.RuleSetVersion v
                    WHERE v.RuleSetId = r.Id AND v.VersionCode = N'V1');

-- ---------------------------------------------------------------------
-- 2) ParameterSetVersion: one PUBLISHED 'V1' per active ParameterSet without PUBLISHED
-- ---------------------------------------------------------------------
INSERT INTO dbo.ParameterSetVersion (
    ParameterSetId, VersionCode, Status,
    EffectiveFrom, EffectiveTo, PublishedAt, PublishedBy, ApprovedAt, ApprovedBy,
    CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDefault, Remarks, ContentSnapshotJson)
SELECT p.Id, N'V1', N'PUBLISHED',
       NULL, NULL,
       GETDATE(), N'seed', GETDATE(), N'seed',
       GETDATE(), N'seed', NULL, NULL, 0,
       N'Seed backfill 2026-10-10 (4号位 P1 ruleSet#1727): initial PUBLISHED version for dev联调',
       NULL
  FROM dbo.ParameterSet p
 WHERE p.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.ParameterSetVersion v
                    WHERE v.ParameterSetId = p.Id AND v.Status = N'PUBLISHED')
   AND NOT EXISTS (SELECT 1 FROM dbo.ParameterSetVersion v
                    WHERE v.ParameterSetId = p.Id AND v.VersionCode = N'V1');

-- ---------------------------------------------------------------------
-- 3) StrategyProfileVersion: one PUBLISHED 'V1' per active profile without PUBLISHED,
--    referencing representative PUBLISHED RuleSetVersion + ParameterSetVersion (FK NOT NULL)
-- ---------------------------------------------------------------------
DECLARE @rsvId BIGINT = (SELECT TOP 1 Id FROM dbo.RuleSetVersion WHERE Status = N'PUBLISHED' ORDER BY Id);
DECLARE @psvId BIGINT = (SELECT TOP 1 Id FROM dbo.ParameterSetVersion WHERE Status = N'PUBLISHED' ORDER BY Id);

INSERT INTO dbo.StrategyProfileVersion (
    StrategyProfileId, VersionCode, RuleSetVersionId, ParameterSetVersionId, Status,
    EffectiveFrom, EffectiveTo, IsDefault,
    PublishedAt, PublishedBy, ApprovedAt, ApprovedBy,
    CreatedAt, CreatedBy)
SELECT s.Id, N'V1', @rsvId, @psvId, N'PUBLISHED',
       NULL, NULL, 0,
       GETDATE(), N'seed', GETDATE(), N'seed',
       GETDATE(), N'seed'
  FROM dbo.StrategyProfile s
 WHERE s.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.StrategyProfileVersion v
                    WHERE v.StrategyProfileId = s.Id AND v.Status = N'PUBLISHED')
   AND NOT EXISTS (SELECT 1 FROM dbo.StrategyProfileVersion v
                    WHERE v.StrategyProfileId = s.Id AND v.VersionCode = N'V1');

-- ---------------------------------------------------------------------
-- Verify echo: residual gaps must be 0
-- ---------------------------------------------------------------------
PRINT N'-- Residual active-without-PUBLISHED (must be 0) --';
SELECT 'RuleSet' AS Domain, COUNT(*) AS Missing
  FROM dbo.RuleSet r
 WHERE r.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.RuleSetVersion v WHERE v.RuleSetId = r.Id AND v.Status = N'PUBLISHED')
UNION ALL
SELECT 'ParameterSet', COUNT(*)
  FROM dbo.ParameterSet p
 WHERE p.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.ParameterSetVersion v WHERE v.ParameterSetId = p.Id AND v.Status = N'PUBLISHED')
UNION ALL
SELECT 'StrategyProfile', COUNT(*)
  FROM dbo.StrategyProfile s
 WHERE s.IsActive = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.StrategyProfileVersion v WHERE v.StrategyProfileId = s.Id AND v.Status = N'PUBLISHED');

PRINT N'-- ruleSet#1727 versions (must have 1 PUBLISHED) --';
SELECT Id, VersionCode, Status,
       CASE WHEN ContentSnapshotJson IS NULL THEN 'NULL' ELSE 'HAS-SNAPSHOT' END AS Snapshot
  FROM dbo.RuleSetVersion WHERE RuleSetId = 1727 ORDER BY Id;
