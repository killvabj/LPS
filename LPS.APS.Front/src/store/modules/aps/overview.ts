/**
 * APS V1 4号位 — 排产总览 Pinia store（P01）
 *
 * 2026-11-20 **整块重做**（依据《APS_V1_P01-P13 全页面业务设计统一收口核对稿 v0.2
 * §4 P01「系统首页：最近FULL公告＋运行异常＋快捷入口」）：
 *   - 页面**只保留三块**：①最近一次 FULL 运行信息 ②「本次运行需关注」（仅失败/部分成功）
 *     ③业务快捷入口（只显示可访问入口）。
 *   - **撤除**：订单摘要 KPI 行（按期/延迟/未排产＝交付KPI驾驶舱）、资源负荷块、
 *     待确认候选版本窄条、「当前生效版本」卡片切换（版本卡切换随订单摘要/资源负荷一起撤）。
 *     依据：稿 §4 P01「不做订单延期数、设备负荷、采购缺口汇总卡；不构建首页筛选体系；
 *     不提供运行恢复/发布按钮；不做九套首页」。
 *
 * 取数（三个读面，各自独立降级）：
 *   1. GET /api/overview/active-plan   → 各 Domain **当前生效**版本（5号位）
 *   2. GET /api/governance/runs        → 最近一次 **FULL** 运行（3号位）
 *   3. GET /api/domain-status/{runId}  → 该 Run 的**各域本次结果**（本次生成的版本、失败原因）
 *
 * ⚠️ 「最近一次 FULL」不依赖端点默认排序：数组端点排序未声明 ⇒ 前端**显式过滤
 *    `runType === 'FULL_SCHEDULE'`** 并按 `startedAt` 倒序取第一条（手工/局部/仿真 Run 一律不算）。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ApiError,
  overviewApi,
  runApi,
  type ActiveVersionPanel,
  type RunDomainStatusItem,
  type ScheduleRunGovDto
} from '@/api/aps-v1'

/** 单个端点的失败信息（用于页面按块提示，而不是整页报错） */
export interface OverviewSectionError {
  /** 中文块名，直接可展示 */
  section: string
  message: string
}

const toMessage = (err: unknown, fallback: string): string => {
  if (err instanceof ApiError) return `[${err.code}] ${err.message}`
  return (err as Error)?.message ?? fallback
}

export const useOverviewStore = defineStore('aps.overview', () => {
  const loading = ref(false)
  const errors = ref<OverviewSectionError[]>([])

  /** 各 Domain 当前生效（ACTIVE）版本 */
  const activeVersions = ref<ActiveVersionPanel[]>([])
  /** 最近一次 FULL 运行（null = 无记录 / 取不到） */
  const latestFullRun = ref<ScheduleRunGovDto | null>(null)
  /** 该 Run 的各域结果（本次生成的版本、失败原因） */
  const runDomainStatuses = ref<RunDomainStatusItem[]>([])

  /** 是否存在当前生效版本 */
  const hasActivePlan = computed(() => activeVersions.value.length > 0)

  async function load(): Promise<void> {
    loading.value = true
    errors.value = []
    const failed = (section: string, err: unknown, fallback: string) =>
      errors.value.push({ section, message: toMessage(err, fallback) })

    // ---- 1. 当前生效版本 ----
    try {
      activeVersions.value = await overviewApi.getActivePlans()
    } catch (err) {
      activeVersions.value = []
      failed('当前生效版本', err, '加载当前生效版本失败')
    }

    // ---- 2. 最近一次 FULL 运行 ----
    const run = await loadLatestFullRun(failed)
    latestFullRun.value = run

    // ---- 3. 该 Run 的各域结果 ----
    runDomainStatuses.value = []
    if (run) {
      try {
        runDomainStatuses.value = await overviewApi.getRunDomainStatus(run.id)
      } catch (err) {
        runDomainStatuses.value = []
        // 404 = 该 Run 没登记要跑哪些排程域，不是服务故障
        if (!(err instanceof ApiError && err.code === 404)) {
          failed('本次运行结果', err, '加载本次运行结果失败')
        }
      }
    }

    loading.value = false
  }

  /**
   * 最近一次 FULL 运行。
   *
   * 优先用服务端分页端点（`runType` 可在服务端过滤）；该端点未部署时降级到数组端点，
   * 前端自行过滤 `FULL_SCHEDULE` 并按 `startedAt` 倒序（**不依赖端点默认顺序**）。
   */
  async function loadLatestFullRun(
    failed: (s: string, e: unknown, f: string) => void
  ): Promise<ScheduleRunGovDto | null> {
    try {
      const page = await runApi.listRunsPaged({ page: 1, pageSize: 5, runType: 'FULL_SCHEDULE' })
      const rows = (page.items ?? []).filter((r) => r.runType === 'FULL_SCHEDULE')
      if (rows.length) return rows[0]
    } catch {
      // 分页端点未部署 ⇒ 走数组端点
    }
    try {
      const rows = (await runApi.listRuns(50)).filter((r) => r.runType === 'FULL_SCHEDULE')
      if (!rows.length) return null
      return [...rows].sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1))[0]
    } catch (err) {
      failed('最近一次运行', err, '加载最近一次运行失败')
      return null
    }
  }

  return {
    loading,
    errors,
    activeVersions,
    latestFullRun,
    runDomainStatuses,
    hasActivePlan,
    load
  }
})
