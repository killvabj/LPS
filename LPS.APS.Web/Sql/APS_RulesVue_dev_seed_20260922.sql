-- =====================================================================
-- APS dev seed: Rules.vue integration - RuleSet#1287 / ParameterSet#881
-- full PUBLISHED version chain (R1 of 3-hao urge memo 2026-09-22)
-- Date: 2026-09-22  |  Author: 3-hao (rule/parameter governance)
--
-- Why: RuleSet master has 484 rows but only 17 version rows; the first
--      list card #1287 (TEST-RS-20260921114805243) had no version ->
--      detail GET /rule-set/{id}/published-version returns 404.
-- What: insert PUBLISHED RuleSetVersion for 1287 + ParameterSetVersion
--       for 881, reusing verified snapshot JSON verbatim from
--       RS-DEMO-V2 (RuleSetVersion Id=478) / PS-DEMO-V3 (ParameterSetVersion Id=807).
--       EffectiveFrom/To = NULL (no window constraint, always in window).
-- DB:   APS_Production. Data INSERT only, no DDL (red-line #6 compliant).
--       Schema v5.1.8.2, ContentSnapshotJson (plan-A) single carrier.
-- Idempotent: guarded by (RuleSetId, VersionCode) / (ParameterSetId, VersionCode).
-- NOTE: file must be executed with  -f 65001  (UTF-8)  on sqlcmd.
-- WARNING: dev-only seed; NEVER run against production.
-- =====================================================================

SET NOCOUNT ON;

DECLARE @rsId   BIGINT = 1287;   -- RuleSet (TEST-RS-20260921114805243, first list card)
DECLARE @psId   BIGINT = 881;    -- ParameterSet (TEST-PS-20260921114805243, paired suffix)
DECLARE @snap   NVARCHAR(MAX);
DECLARE @rsvId  BIGINT;
DECLARE @psvId  BIGINT;

-- ---------------------------------------------------------------------
-- 1) RuleSetVersion for 1287 (PUBLISHED; snapshot reused from RS-DEMO-V2=478)
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM RuleSetVersion WHERE RuleSetId = @rsId AND VersionCode = N'V1')
BEGIN
    SELECT @snap = ContentSnapshotJson FROM RuleSetVersion WHERE Id = 478; -- RS-DEMO-V2

    INSERT INTO RuleSetVersion (
        RuleSetId, VersionCode, Status,
        EffectiveFrom, EffectiveTo, PublishedAt, PublishedBy, ApprovedAt, ApprovedBy,
        CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDefault, Remarks, ContentSnapshotJson)
    VALUES (
        @rsId, N'V1', N'PUBLISHED',
        NULL, NULL,
        GETDATE(), N'seed', GETDATE(), N'seed',
        GETDATE(), N'seed', NULL, NULL, 0,
        N'RulesVue seed R1: full PUBLISHED snapshot (reused RS-DEMO-V2 DemandPriority)',
        @snap);

    SET @rsvId = SCOPE_IDENTITY();
END
ELSE
BEGIN
    SELECT @rsvId = Id FROM RuleSetVersion WHERE RuleSetId = @rsId AND VersionCode = N'V1';
END

-- ---------------------------------------------------------------------
-- 2) ParameterSetVersion for 881 (PUBLISHED; snapshot reused from PS-DEMO-V3=807)
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM ParameterSetVersion WHERE ParameterSetId = @psId AND VersionCode = N'V1')
BEGIN
    SELECT @snap = ContentSnapshotJson FROM ParameterSetVersion WHERE Id = 807; -- PS-DEMO-V3

    INSERT INTO ParameterSetVersion (
        ParameterSetId, VersionCode, Status,
        EffectiveFrom, EffectiveTo, PublishedAt, PublishedBy, ApprovedAt, ApprovedBy,
        CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDefault, Remarks, ContentSnapshotJson)
    VALUES (
        @psId, N'V1', N'PUBLISHED',
        NULL, NULL,
        GETDATE(), N'seed', GETDATE(), N'seed',
        GETDATE(), N'seed', NULL, NULL, 0,
        N'RulesVue seed R1: full PUBLISHED snapshot (reused PS-DEMO-V3 five blocks)',
        @snap);

    SET @psvId = SCOPE_IDENTITY();
END
ELSE
BEGIN
    SELECT @psvId = Id FROM ParameterSetVersion WHERE ParameterSetId = @psId AND VersionCode = N'V1';
END

-- ---------------------------------------------------------------------
-- 3) Verify echo
-- ---------------------------------------------------------------------
PRINT N'-- RuleSet 1287 versions --';
SELECT Id, VersionCode, Status,
       CASE WHEN ContentSnapshotJson IS NULL THEN 'NULL' ELSE 'HAS-SNAPSHOT' END AS Snapshot
  FROM RuleSetVersion WHERE RuleSetId = @rsId ORDER BY Id;

PRINT N'-- ParameterSet 881 versions --';
SELECT Id, VersionCode, Status,
       CASE WHEN ContentSnapshotJson IS NULL THEN 'NULL' ELSE 'HAS-SNAPSHOT' END AS Snapshot
  FROM ParameterSetVersion WHERE ParameterSetId = @psId ORDER BY Id;
