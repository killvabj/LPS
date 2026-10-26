-- ============================================================================
-- APS dev seed: RoutingPlanningParam 批参数（真实主数据 · 幂等）
-- 文件：LPS.APS.Web/Sql/APS_RoutingPlanningParam_dev_seed_20261011.sql
-- Owner：3号位（Rule/Parameter/Strategy 治理 + dev seed 数据 Owner）
-- 日期：2026-10-11
-- 依据：
--   · DDL v5.1.8.4 §RoutingPlanningParam：UQ_RoutingParam(MaterialId,RouteCode,PathId,OperationCode) 唯一；
--     v5.1.8.3 必须写入真实 RouteCode/PathId（V1 支持多路径，不落 DEFAULT 伪值）
--   · 单域全流程跑通目标域：FAMILY_VALVE（DomainDefinition Id=422，ProductFamilyId=3）
--   · dev 实查（2026-10-11，10.116.2.75/APS_Production 只读）：
--       - RoutingPlanningParam 当前 0 行（真 seed 缺口，属 3号位 治理域）
--       - VALVE 域（ProductFamilyId=3）RoutingOperation 真实组合 859 条 / 849 物料 / 6 条 Route-Path
-- What：INSERT 批参数 seed，仅 VALVE 域（单域跑通范围）；值采用 DDL 默认语义：
--       MinBatchSize=1、MaxBatchSize=999999、TransferBatchSize=NULL（未启用）、
--       SourceSystem='APS_LOCAL'、MaintainedBy='3号位'、EffectiveFrom='1900-01-01'、EffectiveTo=NULL
-- Idempotent：NOT EXISTS 按 UQ_RoutingParam 守卫，可重跑；不 UPDATE 既有行。
-- DB：APS_Production（dev）。INSERT，无 DDL（红线 #6 合规）。
-- ============================================================================

INSERT INTO RoutingPlanningParam
    (MaterialId, RouteCode, PathId, OperationCode,
     MinBatchSize, MaxBatchSize, TransferBatchSize,
     SourceSystem, MaintainedBy, EffectiveFrom, EffectiveTo)
SELECT ro.MaterialId, ro.RouteCode, ro.PathId, ro.OperationCode,
       1, 999999, NULL,
       'APS_LOCAL', '3号位', '1900-01-01', NULL
FROM RoutingOperation ro
JOIN Material m ON ro.MaterialId = m.Id
WHERE m.ProductFamilyId = 3
  AND NOT EXISTS (
        SELECT 1 FROM RoutingPlanningParam p
        WHERE p.MaterialId    = ro.MaterialId
          AND p.RouteCode     = ro.RouteCode
          AND p.PathId        = ro.PathId
          AND p.OperationCode = ro.OperationCode
  );
GO

SELECT 'RoutingPlanningParam seeded' AS info, COUNT(*) AS cnt FROM RoutingPlanningParam;
SELECT 'VALVE seeded rows' AS info, COUNT(*) AS cnt
FROM RoutingPlanningParam p JOIN Material m ON p.MaterialId = m.Id
WHERE m.ProductFamilyId = 3;
GO
