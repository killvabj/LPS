/**
 * APS V1 4号位 — 运行治理 API（页面 9：运行与下发）
 *
 * @owner 3号位（runs 列表 / recover / run/candidate）+ 5号位（active-plan / domain-status）
 * @see 4号位文档第 15-16 节（页面 9）
 *
 * 2026-10-16 重整（依据《4号位致3号位/5号位·运行页联调缺口回执》）：
 *  - 删除 `getStatus()` 聚合体取数（后端 `GET /api/governance/runs` 只返 `ScheduleRunGov[]` 裸数组，
 *    端点说明已写明「不做跨域综合统计」）→ 改为 `listRuns()`，另接 5号位两个数据面
 *  - 删除 `dispatch()`：`POST /api/mes/dispatch` 请求体只有 `factoryCode`，且端点说明是
 *    「触发 MES 能力释放路由登记，不触发 2号位 排程」—— 是登记 stub，不是下发；
 *    冻结 §十 要求的链路是 4号位 → 5号位 → 2号位，前端直调也违反该链路 ⇒ MES 区块整块下线
 *  - 删除全部 mock 桩与 `APS_USE_MOCK` 分支
 */

import { apsHttp } from './http'
import type {
  RecoverFailedRunInput,
  RecoverFailedRunResult,
  ScheduleRunGovDto,
  TriggerRescheduleInput,
  TriggerRescheduleResult
} from './types'

export const runApi = {
  /**
   * Run 列表（3号位 `GET /api/governance/runs`）
   *  - 返回 `ScheduleRunGov[]` 裸数组，**没有任何跨域聚合**（端点说明原文）
   *  - take 缺省 = 后端不限制；页面显式传 take，避免 dev 全表拉取
   *  - ⚠️ 后端 WhenWritingNull 会省掉 null 字段（completedAt / expectedDomainKeysJson / errorMessage …）
   */
  async listRuns(take = 50): Promise<ScheduleRunGovDto[]> {
    const rows = await apsHttp.get<ScheduleRunGovDto[]>({
      url: '/api/governance/runs',
      params: { take }
    })
    return rows ?? []
  },

  /**
   * 校验 ScheduleRun.ExpectedDomainKeysJson 冻结规则（v1.4 §十九.2）
   *  - P0-08：FULL_SCHEDULE ≥ 1 Domain；RESCHEDULE 恰 1 Domain
   *  - 后端：POST /api/governance/run/{scheduleRunId}/validate-domain-keys（空 body）
   *  - 返回 200 → 通过；400 → 冻结规则冲突，提示用户无法恢复
   *  - 前端在 recoverFailedRun 之前先调一次（预校验），避免空恢复直接 400
   */
  async validateDomainKeys(scheduleRunId: number): Promise<{ valid: boolean; message: string }> {
    try {
      await apsHttp.post<unknown>({
        url: `/api/governance/run/${scheduleRunId}/validate-domain-keys`,
        data: {}
      })
      return { valid: true, message: 'DomainKey 冻结规则校验通过' }
    } catch (err) {
      // 400 → 校验失败；其它 → 视为无效（兜底）
      const apiErr = err as { code?: number; message?: string }
      return {
        valid: false,
        message: apiErr?.message ?? `校验失败 (code=${apiErr?.code ?? '?'})`
      }
    }
  },

  /** FAILED Run 恢复（§十九.2 / U19）：必须新建 ScheduleRun，不得改回 RUNNING
   *  后端：POST /api/governance/run/{failedScheduleRunId}/recover
   */
  async recoverFailedRun(input: RecoverFailedRunInput): Promise<RecoverFailedRunResult> {
    return apsHttp.post<RecoverFailedRunResult>({
      url: `/api/governance/run/${input.originalRunId}/recover`,
      data: input
    })
  },

  /**
   * 候选重排触发（§十九.2 / 文档第 6 节 / 12 节 / 17 节）
   *  - 从 Gantt Drawer / Resource Drawer / "批量人工重排" 入口发起，针对选中 Task/Resource 的 Domain
   *  - P0-03：根据 input.runType 创建 LOCAL_RESCHEDULE（小范围）或 MANUAL_RESCHEDULE（较大范围）
   *  - 成功后前端跳到 Candidate 对比页（页面5）确认采用
   *  2026-09-17 3号位 回执后确认（@see 3号位回执_致4号位_治理端点对接确认_v1.0_20260917.md §四）：
   *   - 端点：POST /api/governance/run/candidate（GovernanceController.cs L627）
   *   - 权限码：aps.plan.run（aps.run.reschedule / aps.run.candidate 均不存在）
   *   - 入参：CreateCandidateRunRequest（字段 RunType / Purpose / DomainKey / BasePlanVersionId? / DataCutoffTime? / Remark?）
   *   - objectRefIds 砍掉（后端无此字段）；reason → Remark
   */
  async triggerReschedule(input: TriggerRescheduleInput): Promise<TriggerRescheduleResult> {
    // 字段映射为后端 CreateCandidateRunRequest（camelCase）
    //  - remark **停传**（2026-09-21 3号位 回执 §一.3：CandidateRunCreateSpec 无 Remark 属性，后端维持丢弃）
    //  - scope 条件透传（§10A；缺省不发 → 后端静默放行，旧调用兼容）
    const data: Record<string, unknown> = {
      runType: input.runType,
      purpose: input.purpose,
      domainKey: input.domainKey,
      basePlanVersionId: input.basePlanVersionId,
      dataCutoffTime: input.dataCutoffTime
    }
    if (input.scope) {
      data.scope = input.scope
    }
    return apsHttp.post<TriggerRescheduleResult>({
      url: '/api/governance/run/candidate',
      data
    })
  }
}
