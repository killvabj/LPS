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
 * 时间窗加载（2026-10-29 第 0 步，甘特百万任务压死后端）：
 *  - 一律带 from/to（+ limit GANTT_PAGE_LIMIT）请求，不再整版本全量；同一「版本+窗口」单飞
 *  - 换窗口 / 换版本 / 离开页面 → AbortController 中止上一请求（前端放弃，后端不再白算）
 *  - 旧请求晚到的响应直接丢弃（pendingKey / loadedKey 比对），不会把旧窗口数据盖回新窗口
 *
 * 游标追加（2026-10-29 第 1 步）：
 *  - 后端按 `(ResourceId, PlannedStartTime, TaskId)` 升序切页 ⇒ 首屏只覆盖前几个资源，
 *    其余泳道看似空白；`loadMore()` 用 `nextAfter` 原样回填追加下一页，**不重排**
 *  - 追加按 taskId / resourceId 去重；后端若「说还有却没推进游标」立即停住，不死循环
 *
 * 负荷带（2026-10-30 第 2 步接 5号位 r13972 的 `/buckets` 聚合端点）：
 *  - 与甘特本体**两个独立端点、两套在飞句柄**，失败互不影响；
 *  - 同一「版本+窗口+桶宽+资源」单飞；换参中止上一请求；旧响应不回灌；
 *  - 图层按 `effectiveBucketWidth`（后端 `bucketWidth`）画，不是按请求的 `bucket`；
 *  - `bucketGroups` 不按窗口裁首桶（周桶锚=周一，首桶可早于 from）、不按 domainKey 分组；
 *  - 未排桶行（`bucketStart` 缺键）走 `unscheduledBucketRows`，不静默丢。
 *
 * 懒加载契约（10-10）：
 *  - setCurrentVersion(id)：仅更新 currentVersionId ref，**不触发** /api/schedule/gantt 拉取
 *  - selectVersion(id)：更新 + 触发 fetch（Schedule / Gantt 页用）
 *  - 拆分理由：Explanation 页只需要 currentVersionId 渲染高亮，不需要 gantt 数据（避免切版本无意义拖一份大甘特图）
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  scheduleApi,
  ApiError,
  type GanttBucketRowDto,
  type GanttBucketUnit,
  type GanttBucketsDto,
  type GanttCursor,
  type GanttDataDto,
  type PlanVersionSummaryDto
} from '@/api/aps-v1'

/**
 * 单次请求任务条上限。
 *
 * 后端缺省 8000 / 上限 20000（5号位 2026-10-29 复函 §二）；前端 2026-11-06 主动降到 4000：
 * dev 实测同一版本同一窗口 limit 500→8000 只差 0.65s（大头是后端固定成本，与页大小无关），
 * 而 4000 比 8000 少一半体积（3.5MB vs 6.9MB）——跨厂区专线看时的传输余量更稳。
 * 再往下调没有意义：游标翻页每页也要 3s，页太小反而让泳道更空、追加更频繁。
 */
export const GANTT_PAGE_LIMIT = 4000

/** 甘特加载窗口（UTC ISO 字符串，与页面时间轴同一口径） */
export interface GanttWindow {
  from: string
  to: string
  resourceIds?: number[]
}

/**
 * 负荷带（资源×时间桶）取数参数（5号位 2026-10-30 r13972）
 *
 * ⚠️ `from` / `to` **必填**（后端缺任一 ⇒ 真 HTTP 400）—— 在类型上就是必填，
 *   调用方漏传编译期即报错，不会发出无参请求。
 */
export interface GanttBucketRequest {
  from: string
  to: string
  /** 缺省 day */
  bucket?: GanttBucketUnit
  resourceIds?: number[]
}

export const useScheduleStore = defineStore('aps.schedule', () => {
  // ===== state =====
  const versions = ref<PlanVersionSummaryDto[]>([])
  const currentVersionId = ref<number | null>(null)
  const ganttData = ref<GanttDataDto | null>(null)
  /** 当前 ganttData 对应的加载窗口（空态提示 / KPI 口径用） */
  const ganttWindow = ref<GanttWindow | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)
  /** 正在追加下一页（与整窗重载 loading 分开：追加失败不能清掉已加载数据） */
  const loadingMore = ref(false)
  const moreError = ref<string | null>(null)
  /** 负荷带（资源×时间桶）聚合数据；5号位 2026-10-30 r13972 */
  const buckets = ref<GanttBucketsDto | null>(null)
  /** 最近一次**请求**的桶宽（请求值，非生效值；生效值看 `effectiveBucketWidth`） */
  const bucketUnit = ref<GanttBucketUnit>('day')
  /** 已加载的负荷带取数参数（口径回显用） */
  const bucketsWindow = ref<GanttBucketRequest | null>(null)
  const bucketsLoading = ref(false)
  const bucketsError = ref<string | null>(null)

  // ===== in-flight 去重 =====
  /** 同 id 并发点击共享同一 Promise；解决 `ganttData.value` 在飞行中还是 null 导致的并发请求 */
  let pendingVersionId: number | null = null
  let pendingPromise: Promise<void> | null = null
  /** 在飞请求的「版本+窗口」键与最近一次成功落库的键（缓存命中 / 旧响应丢弃用） */
  let pendingKey: string | null = null
  let loadedKey: string | null = null
  /** 在飞请求的取消句柄：换窗口 / 换版本 / 离开页面时中止 */
  let abortCtrl: AbortController | null = null
  /** 追加下一页的在飞句柄（换窗口 / 重载 / 离开页面时一并中止） */
  let moreCtrl: AbortController | null = null
  /** 负荷带在飞句柄 + 单飞键（与甘特本体相互独立） */
  let bucketsCtrl: AbortController | null = null
  let pendingBucketsKey: string | null = null
  let pendingBucketsPromise: Promise<void> | null = null
  let loadedBucketsKey: string | null = null

  function windowKey(id: number, win?: GanttWindow | null): string {
    return win ? `${id}|${win.from}|${win.to}|${(win.resourceIds ?? []).join(',')}` : `${id}|`
  }

  function bucketsKey(id: number, req: GanttBucketRequest, unit: GanttBucketUnit): string {
    return `${id}|${req.from}|${req.to}|${unit}|${(req.resourceIds ?? []).join(',')}`
  }

  // ===== getters =====
  const currentVersion = computed(
    () => versions.value.find((v) => v.id === currentVersionId.value) ?? null
  )
  /** 4号位文档第 6 节：UI 态，业务层不应依赖此标志 */
  const isEmpty = computed(() => !ganttData.value || ganttData.value.tasks.length === 0)

  /** 还能往后追加：有下一页游标 + 没有在飞的重载/追加 + 无全局错误 */
  const canLoadMore = computed(() => {
    const d = ganttData.value
    return (
      !!d &&
      d.hasMore === true &&
      !!d.nextAfter &&
      !loading.value &&
      !loadingMore.value &&
      !error.value
    )
  })

  /**
   * **实际生效的桶宽**（后端 `bucketWidth` 回显）。
   * ⚠️ 降级链（行数 > 50,000 ⇒ day→week→month）会让它 ≠ 请求的 `bucket` ⇒
   *   负荷带图层**必须按本值画**，不是按 `bucket`。
   */
  const effectiveBucketWidth = computed<GanttBucketUnit | null>(() => {
    const w = buckets.value?.bucketWidth
    return w === 'day' || w === 'week' || w === 'month' ? w : null
  })

  /**
   * 负荷带按资源分组（供图层消费）。
   *
   * 三条口径（4号位 2026-10-30 实测函 §四，均有 dev 实测 / 后端源码依据）：
   *  1. `resourceId = -1` 是**未分配行**，单独成一组，**不与任何资源混画**；
   *  2. **不按窗口 `from` 裁桶**：`bucketStart` 可能早于窗头（周桶锚 = 周一 00:00，
   *     且后端对跨窗头任务做钳制归桶）—— 实测窗 10-20→10-27 的首桶为 `2026-10-19`，
   *     裁掉等于丢掉窗头跨窗任务的负荷；
   *  3. **不按 `domainKey` 分组**：它是**版本域**（`PlanVersion.DomainKey`），
   *     同一版本内所有行恒相同，按它分组会把所有资源并成一组。
   */
  const bucketGroups = computed<Array<{ resourceId: number; rows: GanttBucketRowDto[] }>>(() => {
    const map = new Map<number, GanttBucketRowDto[]>()
    for (const row of buckets.value?.items ?? []) {
      // 未排桶行（bucketStart 缺键）另走 unscheduledBucketRows，不进分组
      if (row.bucketStart == null) continue
      const list = map.get(row.resourceId)
      if (list) list.push(row)
      else map.set(row.resourceId, [row])
    }
    return Array.from(map, ([resourceId, rows]) => ({ resourceId, rows }))
  })

  /**
   * 未排桶行（`bucketStart` 缺键/null ⇒ 该任务 `PlannedStartTime IS NULL`）。
   * ⚠️ **不得静默丢弃** —— 图层要给可见去处（dev 现网无此样本，来源仍按契约处理）。
   */
  const unscheduledBucketRows = computed<GanttBucketRowDto[]>(() =>
    (buckets.value?.items ?? []).filter((r) => r.bucketStart == null)
  )

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

  async function selectVersion(planVersionId: number, window?: GanttWindow): Promise<void> {
    const key = windowKey(planVersionId, window)
    // 1) 精确缓存命中：同版本 + 同窗口 + 缓存无错（DTO 自带 planVersionId）
    if (
      planVersionId === currentVersionId.value &&
      ganttData.value?.planVersionId === planVersionId &&
      loadedKey === key &&
      !error.value
    ) {
      return
    }
    // 2) in-flight 去重：同版本同窗口并发点击复用同一 Promise（避免连点触发两次 gantt 请求）
    if (pendingVersionId === planVersionId && pendingPromise && pendingKey === key) {
      return pendingPromise
    }
    currentVersionId.value = planVersionId
    await trackLoad(planVersionId, window, key)
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

  async function loadCurrent(window?: GanttWindow): Promise<void> {
    const id = currentVersionId.value
    if (id === null) return
    // 缺省窗口 = 上一次成功加载的窗口，避免 watch(currentVersionId) 退化成裸调（拿后端缺省页）
    const win = window ?? ganttWindow.value ?? undefined
    const key = windowKey(id, win)
    if (
      ganttData.value?.planVersionId === id &&
      loadedKey === key &&
      !error.value &&
      !pendingPromise
    ) {
      return
    }
    await trackLoad(id, win, key)
  }

  /** 发起一次加载：同键单飞 + 中止上一窗口/上一版本的在飞请求
   *  只取甘特本体，不接 summary：summary 的 `delayedTasks` 用裸 datetime 比
   *  （`PlannedEndTime > CustomerDueDate`），甘特体按日期比（`CAST(... AS date) > CustomerDueDate`）；
   *  dev v2 实测 summary 191 / 甘特体 179，差的 12 条正是「交日当天 00:00 之后完工」。
   *  接进来同一页会出现两个互相矛盾的延期数（已出函 5号位）。 */
  function trackLoad(id: number, win: GanttWindow | undefined, key: string): Promise<void> {
    if (pendingKey === key && pendingPromise) return pendingPromise
    if (abortCtrl && !abortCtrl.signal.aborted) abortCtrl.abort()
    // 整窗重载会替换 ganttData，旧的追加结果已无意义 ⇒ 一并中止
    if (moreCtrl && !moreCtrl.signal.aborted) moreCtrl.abort()
    moreError.value = null
    const ctrl = new AbortController()
    abortCtrl = ctrl
    pendingVersionId = id
    pendingKey = key
    loading.value = true
    error.value = null
    const p = fetchGantt(id, win, key, ctrl).finally(() => {
      // 只有「仍是当前在飞请求」才收尾；被新请求取代时新请求自己管 loading
      if (pendingKey === key) {
        pendingVersionId = null
        pendingKey = null
        pendingPromise = null
        loading.value = false
      }
    })
    pendingPromise = p
    return p
  }

  async function fetchGantt(
    id: number,
    win: GanttWindow | undefined,
    key: string,
    ctrl: AbortController
  ): Promise<void> {
    try {
      const data = await scheduleApi.getGantt(id, {
        from: win?.from,
        to: win?.to,
        resourceIds: win?.resourceIds,
        limit: GANTT_PAGE_LIMIT,
        signal: ctrl.signal
      })
      // 已取消 / 已被更新的窗口取代：丢弃这次结果，别把旧窗口数据盖回来
      if (ctrl.signal.aborted || pendingKey !== key) return
      ganttData.value = data
      ganttWindow.value = win ?? null
      loadedKey = key
    } catch (err) {
      // 主动取消不是错误（不 toast、不跳 500 页、不清上一次数据）
      if (ctrl.signal.aborted || pendingKey !== key) return
      // 保留上一次成功的 ganttData，不重置
      handleError(err)
    }
  }

  /**
   * 追加下一页（第 1 步纵向懒加载）。
   *  - `nextAfter` 整包回填，**不重排**（后端已按排序键定死）
   *  - 追加而非替换；按 taskId / resourceId 去重
   *  - 被换窗/重载/离开页面取代时丢弃结果（loadedKey 比对）
   */
  async function loadMore(): Promise<void> {
    const d = ganttData.value
    const id = currentVersionId.value
    const key = loadedKey
    if (id === null || !d || key === null) return
    if (!canLoadMore.value) return
    const cursor = d.nextAfter as GanttCursor
    const win = ganttWindow.value
    const after: GanttCursor = {
      resourceId: cursor.resourceId,
      plannedStartTime: cursor.plannedStartTime,
      taskId: cursor.taskId
    }
    moreError.value = null
    loadingMore.value = true
    const ctrl = new AbortController()
    moreCtrl = ctrl
    try {
      const page = await scheduleApi.getGantt(id, {
        from: win?.from,
        to: win?.to,
        resourceIds: win?.resourceIds,
        limit: GANTT_PAGE_LIMIT,
        after,
        signal: ctrl.signal
      })
      // 已取消 / 窗口或版本已换：丢弃这一页
      if (ctrl.signal.aborted || loadedKey !== key) return
      const seenTasks = new Set(d.tasks.map((t) => t.taskId))
      const newTasks = page.tasks.filter((t) => {
        if (seenTasks.has(t.taskId)) return false
        seenTasks.add(t.taskId)
        return true
      })
      const seenResources = new Set(d.resources.map((r) => r.resourceId))
      const newResources = page.resources.filter((r) => {
        if (seenResources.has(r.resourceId)) return false
        seenResources.add(r.resourceId)
        return true
      })
      const next = page.nextAfter ?? null
      const stalled =
        page.hasMore === true &&
        (newTasks.length === 0 ||
          (next !== null &&
            next.resourceId === after.resourceId &&
            next.taskId === after.taskId &&
            next.plannedStartTime === after.plannedStartTime))
      ganttData.value = {
        ...d,
        resources: [...d.resources, ...newResources],
        tasks: [...d.tasks, ...newTasks],
        totalTasks: page.totalTasks ?? d.totalTasks,
        limit: page.limit ?? d.limit,
        hasMore: stalled ? false : page.hasMore,
        nextAfter: stalled ? null : next
      }
      if (stalled) moreError.value = '后续任务无法继续加载，请缩小时间范围后重试'
    } catch (err) {
      if (ctrl.signal.aborted || loadedKey !== key) return
      // 追加失败不清已加载数据，只置局部错误给「重试」
      moreError.value = describeError(err)
    } finally {
      if (moreCtrl === ctrl) moreCtrl = null
      loadingMore.value = false
    }
  }

  /**
   * 取负荷带数据（资源×时间桶；5号位 2026-10-30 r13972）。
   *
   * - `from` / `to` **必填**（`GanttBucketRequest` 类型上必填）⇒ 不存在无参请求；
   * - 同一「版本+窗口+桶宽+资源」**单飞**；换参中止上一请求；旧响应**不回灌**；
   * - 与甘特本体**相互独立**（两个端点、两个在飞句柄），失败互不影响。
   */
  async function loadBuckets(request: GanttBucketRequest): Promise<void> {
    const id = currentVersionId.value
    if (id === null) return
    const unit = request.bucket ?? bucketUnit.value
    const key = bucketsKey(id, request, unit)
    // 已加载同一组合 ⇒ 不重复打后端
    if (loadedBucketsKey === key && !bucketsError.value && buckets.value) return
    // 同键在飞 ⇒ 复用同一 Promise（不双发）
    if (pendingBucketsKey === key && pendingBucketsPromise) return pendingBucketsPromise
    if (bucketsCtrl && !bucketsCtrl.signal.aborted) bucketsCtrl.abort()
    const ctrl = new AbortController()
    bucketsCtrl = ctrl
    pendingBucketsKey = key
    bucketsLoading.value = true
    bucketsError.value = null
    const p = fetchBuckets(id, request, unit, key, ctrl).finally(() => {
      if (pendingBucketsKey === key) {
        pendingBucketsKey = null
        pendingBucketsPromise = null
        bucketsLoading.value = false
      }
    })
    pendingBucketsPromise = p
    return p
  }

  async function fetchBuckets(
    id: number,
    request: GanttBucketRequest,
    unit: GanttBucketUnit,
    key: string,
    ctrl: AbortController
  ): Promise<void> {
    try {
      const data = await scheduleApi.getGanttBuckets(id, {
        from: request.from,
        to: request.to,
        bucket: unit,
        resourceIds: request.resourceIds,
        signal: ctrl.signal
      })
      // 已被换参取代：丢弃这次结果，别把旧窗口的桶盖回来
      if (ctrl.signal.aborted || pendingBucketsKey !== key) return
      buckets.value = data
      bucketUnit.value = data.bucket ?? unit
      bucketsWindow.value = request
      loadedBucketsKey = key
    } catch (err) {
      // 主动取消不是错误；旧请求的失败也不该冒到当前状态上
      if (ctrl.signal.aborted || pendingBucketsKey !== key) return
      bucketsError.value = describeError(err)
    }
  }

  /** 中止在飞的负荷带请求（切窗口 / 换版本 / 离开页面） */
  function cancelBucketsLoad(): void {
    if (bucketsCtrl && !bucketsCtrl.signal.aborted) bucketsCtrl.abort()
    bucketsCtrl = null
    pendingBucketsKey = null
    pendingBucketsPromise = null
    bucketsLoading.value = false
  }

  /** 离开甘特页 / 切到无关视图时调用：中止在飞请求，避免「前端放弃、后端白算」 */
  function cancelGanttLoad(): void {
    if (abortCtrl && !abortCtrl.signal.aborted) abortCtrl.abort()
    if (moreCtrl && !moreCtrl.signal.aborted) moreCtrl.abort()
    // 负荷带同属甘特页的在飞请求，一并中止（前端放弃、后端不再白算）
    cancelBucketsLoad()
    abortCtrl = null
    moreCtrl = null
    pendingVersionId = null
    pendingKey = null
    pendingPromise = null
    loading.value = false
    loadingMore.value = false
  }

  function describeError(err: unknown): string {
    if (err instanceof ApiError) {
      return `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    }
    return (err as Error)?.message ?? '未知错误'
  }

  function handleError(err: unknown): void {
    error.value = describeError(err)
  }

  function reset(): void {
    cancelGanttLoad()
    versions.value = []
    currentVersionId.value = null
    ganttData.value = null
    ganttWindow.value = null
    loadedKey = null
    error.value = null
    moreError.value = null
    buckets.value = null
    bucketsWindow.value = null
    loadedBucketsKey = null
    bucketsError.value = null
  }

  return {
    versions,
    currentVersionId,
    ganttData,
    ganttWindow,
    loading,
    error,
    currentVersion,
    isEmpty,
    loadingMore,
    moreError,
    canLoadMore,
    buckets,
    bucketUnit,
    bucketsWindow,
    bucketsLoading,
    bucketsError,
    effectiveBucketWidth,
    bucketGroups,
    unscheduledBucketRows,
    activeVersion,
    recentComputed,
    failedVersions,
    displayedVersions,
    loadVersions,
    selectVersion,
    setCurrentVersion,
    loadCurrent,
    loadMore,
    loadBuckets,
    cancelBucketsLoad,
    cancelGanttLoad,
    reset
  }
})
