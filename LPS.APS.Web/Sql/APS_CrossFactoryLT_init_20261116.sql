-- =============================================================================
-- 跨厂LT 标准参数（CrossFactoryLeadTimeRules）上线初始化 —— Sheet4 数据
-- 目标   ：在原有的在用参数集版本（2365 = PS-PROD-V2.0）ContentSnapshotJson 上**原地注入**该块
-- 方式   ：UPDATE 既有版本 JSON（非新建版本）——**按使用户明确指示（2026-11-16）**
-- ⚠ 红线例外登记：对已发布版本原地改写 ContentSnapshotJson 违反「已发布版本内容不可原地改写」红线上限，
--   本脚本仅因用户明确指示而生成，执行前**请先在 0号位 备案本例外**并知悉：
--   (a) 版本追溯/审计退化（库内 2365 已发布但内容被更新）；
--   (b) 进程快照缓存可能让已缓存 Run 仍旧内容，需清缓存/重启服务，新 Run 才读到新 JSON。
-- 幂等   ：$.CrossFactoryLeadTimeRules 已存在则跳过（不重复覆盖）
-- 数据源 ：副本副本APS_V1上线规则参数初始化_0号位及生产部门填报模板_v1.0_20261031(3).xlsx Sheet4（14 条）
-- =============================================================================
SET NOCOUNT ON;

DECLARE @TargetPV BIGINT = 2365;   -- 在用参数集版本 Id（PS-PROD-V2.0，FULL_SCHEDULE 默认策略包绑定）
DECLARE @Updated  NVARCHAR(MAX);

-- 幂等：块已存在则跳过
IF EXISTS (
    SELECT 1 FROM dbo.ParameterSetVersion
    WHERE Id = @TargetPV
      AND JSON_QUERY(ContentSnapshotJson, '$.CrossFactoryLeadTimeRules') IS NOT NULL
)
BEGIN
    PRINT N'[CROSS_FACTORY_LT] 该版本已含 CrossFactoryLeadTimeRules，跳过更新（幂等）。';
    SELECT Id, VersionCode, Status, JSON_VALUE(ContentSnapshotJson,'$.CrossFactoryLeadTimeRules') AS Existing
    FROM dbo.ParameterSetVersion WHERE Id = @TargetPV;
    RETURN;
END

UPDATE dbo.ParameterSetVersion
SET ContentSnapshotJson = JSON_MODIFY(
    ISNULL(ContentSnapshotJson, N'{}'),
    '$.CrossFactoryLeadTimeRules',
    JSON_QUERY(N'[
  { "FromFactoryCode":"CN","ToFactoryCode":"BJ","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"CN","ToFactoryCode":"TJ","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"CN","ToFactoryCode":"CN6","CrossFactoryLeadTimeHours":1,"IsActive":true },
  { "FromFactoryCode":"CN","ToFactoryCode":"SH","CrossFactoryLeadTimeHours":48,"IsActive":true },
  { "FromFactoryCode":"BJ","ToFactoryCode":"CN","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"BJ","ToFactoryCode":"TJ","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"BJ","ToFactoryCode":"CN6","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"BJ","ToFactoryCode":"SH","CrossFactoryLeadTimeHours":48,"IsActive":true },
  { "FromFactoryCode":"TJ","ToFactoryCode":"CN","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"TJ","ToFactoryCode":"BJ","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"TJ","ToFactoryCode":"CN6","CrossFactoryLeadTimeHours":24,"IsActive":true },
  { "FromFactoryCode":"CN6","ToFactoryCode":"CN","CrossFactoryLeadTimeHours":1,"IsActive":true },
  { "FromFactoryCode":"CN6","ToFactoryCode":"SH","CrossFactoryLeadTimeHours":48,"IsActive":true },
  { "FromFactoryCode":"SH","ToFactoryCode":"CN6","CrossFactoryLeadTimeHours":48,"IsActive":true }
]')
)
WHERE Id = @TargetPV;

SELECT Id, VersionCode, Status, JSON_VALUE(ContentSnapshotJson,'$.CrossFactoryLeadTimeRules') AS Injected
FROM dbo.ParameterSetVersion WHERE Id = @TargetPV;

PRINT N'[CROSS_FACTORY_LT] 已在版本 ' + CAST(@TargetPV AS NVARCHAR(20)) + N' ContentSnapshotJson 原地注入 CrossFactoryLeadTimeRules（14 条）。';
GO

-- =============================================================================
-- 说明：
--  1) 若 MANUAL_RESCHEDULE（SP-MR→807 PS-DEMO-V3）也要跨厂LT，把 @TargetPV 改为 807 再跑一遍。
--  2) 生效：需清 FrozenStrategySnapshotProvider 进程缓存/重启服务，新 Run 才读到 14 条跨厂LT。
--  3) 此为「已发布版本原地改写」的红线例外，请先在 0号位 备案后执行。
-- =============================================================================