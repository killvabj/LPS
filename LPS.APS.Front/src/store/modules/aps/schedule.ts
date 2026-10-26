/**
 * APS V1 4号位 — 排程数据 Pinia store
 *
 * 状态：
 *  - versions：计划版本列表
 *  - currentVersionId：当前选中的版本
 *  - ganttData：当前版本的甘特图数据
 *  - （2026-10-17：summary 状态已移除 —— 端点长期 500 且无页面消费，详见 loadCurrent 注释）
 *  - loading/error：UI 态（4号位文档第 20 节：UI 态 ≠ 业务态）
 *
 * in-flight 去重（10-09 dev 浏览器反馈"两次 gantt/46"教训）：
 *  - pendingVersionId/pendingPromise：selectVersion 同 id 并发点击共享同一 Promise，
 *    避免缓存检查 `ganttData.value` 在飞行中还是 null 导致的并发请求
 *
 * 懒加载契约（10-10）：
 *  - setCurrentVersion(id)：仅更新 currentVersionId ref，**不触发** /api/schedule/gantt 拉取
 *  - selectVersion(id)：更新 + 触发 fetch（Schedule / Gantt 页用）
 *  - 拆分理由：Explanation 页只需要 currentVersionId 渲染高亮，不需要 gantt 数据（避免切版本无意义拖一份大甘特图）
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { scheduleApi, ApiError, type GanttDataDto, type PlanVersionSummaryDto } from '@/api/aps-v1'

export const useScheduleStore = defineStore('aps.schedule', () => {
  // ===== state =====
  const versions = ref<PlanVersionSummaryDto[]>([])
  const currentVersionId = ref<number | null>(null)
  const ganttData = ref<GanttDataDto | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)

  // ===== in-flight 去重 =====
  /** 同 id 并发点击共享同一 Promise；解决 `ganttData.value` 在飞行中还是 null 导致的并发请求 */
  let pendingVersionId: number | null = null
  let pendingPromise: Promise<void> | null = null

  // ===== getters =====
  const currentVersion = computed(
    () => versions.value.find((v) => v.id === currentVersionId.value) ?? null
  )
  /** 4号位文档第 6 节：UI 态，业务层不应依赖此标志 */
  const isEmpty = computed(() => !ganttData.value || ganttData.value.tasks.length === 0)

  /**
   * 异常与原因解释页 version-bar 用的优先级分组（10-09 重构：替代硬截 `versions.slice(0, 3)`）
   *  - activeVersion：当前生效版本（PMC 关注的"真·当前"）
   *  - recentComputed：最近 3 条算完待确认（夜间批 + 白天手工的产物，对比参考）
   *  - failedVersions：失败版本（数量通常 0~1，PMC 排查用）
   *
   * 2026-10-16：词表对齐冻结 07 §4.2 —— 原 Computed→CANDIDATE、原 ComputeFailed→FAILED
   * （2号位 已统一，后端永不返旧值，「或 ComputeFailed」分支是死代码，已删）
   */
  const activeVersion = computed(() => versions.value.find((v) => v.status === 'ACTIVE') ?? null)
  const recentComputed = computed(() =>
    versions.value.filter((v) => v.status === 'CANDIDATE').slice(0, 3)
  )
  const failedVersions = computed(() =>
    versions.value.filter((v) => v.status === 'FAILED').slice(0, 2)
  )
  /**
   * 渲染列表（按业务优先级去重）：
   *   1) ACTIVE（置顶，PMC 一眼定位"现在跑哪版"）
   *   2) 最近 3 条算完待确认（CANDIDATE）
   *   3) 失败版本（最多 2 条）
   *   4) 当前已选版本兜底（避免"选了之后按钮列表看不到"）
   */
  const displayedVersions = computed<PlanVersionSummaryDto[]>(() => {
    const cur = currentVersionId.value
      ? (versions.value.find((v) => v.id === currentVersionId.value) ?? null)
      : null
    const out: PlanVersionSummaryDto[] = []
    const seen = new Set<number>()
    for (const v of [activeVersion.value, ...recentComputed.value, ...failedVersions.value, cur]) {
      if (v && !seen.has(v.id)) {
        out.push(v)
        seen.add(v.id)
      }
    }
    return out
  })

  // ===== actions =====
  async function loadVersions(take = 30): Promise<void> {
    loading.value = true
    error.value = null
    try {
      versions.value = await scheduleApi.getVersions(take)
      // 默认选中第一个 ACTIVE 版本（4号位文档第 4 节）
      const active = versions.value.find((v) => v.status === 'ACTIVE')
      currentVersionId.value = active?.id ?? versions.value[0]?.id ?? null
    } catch (err) {
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  async function selectVersion(planVersionId: number): Promise<void> {
    // 1) 精确缓存命中：选中的版本 == 当前选中 && gantt 缓存是该版本（DTO 自带 planVersionId）
    if (
      planVersionId === currentVersionId.value &&
      ganttData.value?.planVersionId === planVersionId
    ) {
      return
    }
    // 2) in-flight 去重：同 id 并发点击复用同一 Promise（避免连点同一按钮触发两次 gantt 请求）
    if (pendingVersionId === planVersionId && pendingPromise) {
      return pendingPromise
    }
    currentVersionId.value = planVersionId
    pendingVersionId = planVersionId
    pendingPromise = loadCurrent().finally(() => {
      pendingVersionId = null
      pendingPromise = null
    })
    await pendingPromise
  }

  /**
   * 仅更新当前版本 ref（10-10 增；给 Explanation / Rule 之类不需甘特图的页用）
   *  - 不触发 /api/schedule/gantt 拉取
   *  - 不参与 pendingVersionId / pendingPromise 去重（无异步）
   *  - 不更新 ganttData（保持上次甘特页缓存，下次回甘特页还能复用）
   *  - 同 id 幂等
   */
  function setCurrentVersion(planVersionId: number | null): void {
    if (planVersionId === currentVersionId.value) return
    currentVersionId.value = planVersionId
  }

  async function loadCurrent(): Promise<void> {
    const id = currentVersionId.value
    if (id === null) return
    loading.value = true
    error.value = null
    try {
      // 2026-10-17：只取甘特本体。summary 端点原长期 500（从 `PlanVersion` 取不存在的
      //  `DurationSeconds` 列），已于同日修复，dev 至今 HTTP 200。
      // 2026-10-19 复核后仍不接，两条理由：
      //   ① 页面的「任务总数 / 延期数」从甘特体现算（见 Gantt.vue `stats`），不缺数据源；
      //   ② summary 的 `delayedTasks` 延期口径与甘特体不一致 —— 它用裸 datetime 比较
      //      （`PlannedEndTime > CustomerDueDate`），甘特体用日期比较
      //      （`CAST(PlannedEndTime AS date) > CustomerDueDate`，2026-10-17 按 4号位 Q1 修正）。
      //      dev v2 实测：summary 191 / 甘特体 179，差的 12 条正是「交日当天 00:00 之后完工」。
      //      接进来会让同一个页面显示两个互相矛盾的延期数。已出函 5号位，修正后再接。
      try {
        ganttData.value = await scheduleApi.getGantt(id)
      } catch (err) {
        // 保留上一次成功的 ganttData，不重置
        handleError(err)
      }
    } finally {
      loading.value = false
    }
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    versions.value = []
    currentVersionId.value = null
    ganttData.value = null
    error.value = null
  }

  return {
    versions,
    currentVersionId,
    ganttData,
    loading,
    error,
    currentVersion,
    isEmpty,
    activeVersion,
    recentComputed,
    failedVersions,
    displayedVersions,
    loadVersions,
    selectVersion,
    setCurrentVersion,
    loadCurrent,
    reset
  }
})
