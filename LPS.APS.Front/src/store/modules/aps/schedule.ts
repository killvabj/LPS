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
 *
 * 2026-11-20：任务清单请求参数补 `departmentIds`（依据《APS_V1_P01-P13 全页面业务设计统一收口
 * 核对稿 v0.2》§4 P05「第一屏：…授权部门、资源设备或人工槽、Stage/工序/品番、任务状态及 MES
 * 查询条件」）。后端 `GET /api/schedule/tasks/{planVersionId}` 已支持 `departmentIds`（逗号串），
 * 前端此前没暴露该筛选 ⇒ 补进 `TaskListRequest` / 单飞 key / 取数透传三处。
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
  type PlanVersionSummaryDto,
  type ResourceLoadResultDto,
  type TaskListResultDto
} from '@/api/aps-v1'
import { pickDefaultVersion } from './order'

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

/**
 * P12 资源负荷三率取数参数（5号位 2026-11-17 r14239；矩阵 A6/A7）
 *
 * ⚠️ `from` / `to` **必填**（后端缺任一 ⇒ 真 HTTP 400）—— 类型上就是必填。
 * ⚠️ **窗口上限 31 天**（后端 `spanDays > 30` ⇒ 抛错 ⇒ 真 400）⇒ 调用方自己夹。
 */
export interface ResourceLoadRequest {
  from: string
  to: string
  resourceIds?: number[]
}

/**
 * P05 任务清单取数参数（5号位 2026-11-16 矩阵 A4 / r14237）
 *
 * ⚠️ `from` / `to` 传**与甘特体完全相同**的那一对（窗口谓词是同一件：
 *   `PlannedEndTime >= from AND PlannedStartTime <= to`，计划时刻 NULL 的任务恒保留）
 *   ⇒ 两个页面的行数才对得上。
 */
export interface TaskListRequest {
  from?: string
  to?: string
  resourceIds?: number[]
  stageCodes?: string[]
  /** 生产部门 Id（后端收逗号串）；2026-11-20 补：P05 第一屏要能按授权部门筛 */
  departmentIds?: number[]
  keyword?: string
  status?: string
  /** 三态：undefined=不过滤 / true=有 MES 工单号 / false=无 MES 工单号（**非**「无需 MES」分类） */
  hasWorkOrder?: boolean
  sortBy?: string
  sortDir?: string
  pageIndex?: number
  pageSize?: number
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
  /**
   * P12 资源负荷三率（设备 × 业务日）；5号位 2026-11-17 r14239。
   *
   * 与 `buckets`（负荷**量**）是**两个端点、两套口径**，不合并：
   *  - buckets：资源 × 时间桶的负荷**量**（无分母 ⇒ 不能算率）
   *  - resource-load：设备 × 业务日的**率**（分母 = 三级继承解析出的可用小时）
   */
  const resourceLoad = ref<ResourceLoadResultDto | null>(null)
  const resourceLoadLoading = ref(false)
  const resourceLoadError = ref<string | null>(null)

  /**
   * P05 任务清单（服务端分页；矩阵 A4 / r14237）。
   *
   * ⚠️ **与甘特体是两套读法，不合并**：
   *  - 甘特体 `/gantt`：整窗**图**（游标分页，已加载数 ≤ limit）
   *  - 任务清单 `/tasks`：**列表**（offset 分页，`total` = 服务端全量命中）
   *   ⇒ 同一批任务的两种表达；数字不同不是 bug，是两个口径，必须各自标清。
   */
  const taskList = ref<TaskListResultDto | null>(null)
  const taskListLoading = ref(false)
  const taskListError = ref<string | null>(null)

  /**
   * **全量**版本列表（异常与原因解释页的版本下拉用；2026-11-17）。
   *
   * ⚠️ 与 `versions`（甘特页用，take=30）**分开存**，两个原因：
   *   ① 语义不同：`displayedVersions` 是给甘特工具栏设计的「ACTIVE + 最近 3 条 CANDIDATE
   *      + 最多 2 条 FAILED」三档截断；解释页要的是「把已有版本都列出来，用户自己选」
   *      （用户 11-17 定调）⇒ 不能继承那套截断（否则失败批次里最有料的两版根本点不到）。
   *   ② `loadVersions` 每次都会把 `currentVersionId` 重置成「第一个 ACTIVE」；
   *      解释页进来时选中的版本（可能来自运行页入参）会被它冲掉 ⇒ 这里只读列表、不动选中态。
   */
  const allVersions = ref<PlanVersionSummaryDto[]>([])
  const allVersionsLoading = ref(false)
  const allVersionsError = ref<string | null>(null)

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
  /** P12 负荷率在飞句柄 + 单飞键（与甘特本体、负荷带各自独立） */
  let resourceLoadCtrl: AbortController | null = null
  let pendingResourceLoadKey: string | null = null
  let pendingResourceLoadPromise: Promise<void> | null = null
  let loadedResourceLoadKey: string | null = null
  /** P05 任务清单在飞句柄 + 单飞键（与甘特本体、负荷带、负荷率各自独立） */
  let taskListCtrl: AbortController | null = null
  let pendingTaskListKey: string | null = null
  let pendingTaskListPromise: Promise<void> | null = null
  let loadedTaskListKey: string | null = null

  function windowKey(id: number, win?: GanttWindow | null): string {
    return win ? `${id}|${win.from}|${win.to}|${(win.resourceIds ?? []).join(',')}` : `${id}|`
  }

  function bucketsKey(id: number, req: GanttBucketRequest, unit: GanttBucketUnit): string {
    return `${id}|${req.from}|${req.to}|${unit}|${(req.resourceIds ?? []).join(',')}`
  }

  function resourceLoadKey(id: number, req: ResourceLoadRequest): string {
    return `${id}|${req.from}|${req.to}|${(req.resourceIds ?? []).join(',')}`
  }

  function taskListKey(id: number, req: TaskListRequest): string {
    return [
      id,
      req.from ?? '',
      req.to ?? '',
      (req.resourceIds ?? []).join(','),
      (req.stageCodes ?? []).join(','),
      (req.departmentIds ?? []).join(','),
      req.keyword ?? '',
      req.status ?? '',
      req.hasWorkOrder === undefined ? '' : String(req.hasWorkOrder),
      req.sortBy ?? '',
      req.sortDir ?? '',
      req.pageIndex ?? 1,
      req.pageSize ?? ''
    ].join('|')
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
  /**
   * **域内当前生效版本**（后端 `isDefault=true`；矩阵 §0.1 锚表）。
   *
   * ⚠️ 每域至多一条 ⇒ 跨域账号这里会返回**多条**（dev 实测 5 条：BJ_FAMILY_FRL / FAMILY_LGJ /
   *   TJ_FAMILY_FRL / FAMILY_WKQ / FAMILY_VALVE）；CYL 域当前无 ACTIVE（锚表勘正 11-17），
   *   页面按「该域当前无正式计划版本」显示，**不拿 0 或失败冒充**。
   * ⚠️ 后端没给 isDefault 的历史版本 ⇒ 兜底用 `status==='ACTIVE'`（等价语义）。
   */
  const activeVersions = computed<PlanVersionSummaryDto[]>(() => {
    const flagged = versions.value.filter((v) => v.isDefault === true)
    if (flagged.length) return flagged
    return versions.value.filter((v) => v.status === 'ACTIVE')
  })
  const recentComputed = computed(() =>
    versions.value.filter((v) => v.status === 'CANDIDATE').slice(0, 3)
  )
  const failedVersions = computed(() =>
    versions.value.filter((v) => v.status === 'FAILED').slice(0, 2)
  )
  /**
   * 渲染列表（按业务优先级去重）：
   *   1) **全部域内当前生效版本**（isDefault，置顶，PMC 一眼定位"现在跑哪版"）
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
    for (const v of [
      ...activeVersions.value,
      ...recentComputed.value,
      ...failedVersions.value,
      cur
    ]) {
      if (v && !seen.has(v.id)) {
        out.push(v)
        seen.add(v.id)
      }
    }
    return out
  })

  // ===== actions =====
  /**
   * 拉版本列表并按 **ACTIVE 锚表** 选默认版本。
   *
   * ⚠️ `take` 缺省 **100**（不是 30）：后端 ACTIVE 版本可能排在很后面
   *   （dev 实测 2026-11-17：61 个版本里最新 30 个**一个 ACTIVE 都没有**，
   *   ACTIVE 全在 31~61 位）⇒ take=30 时页面会落到 `versions[0]`＝FAILED 的测试版，
   *   用户一进甘特页看到「当前版本没有排程结果」，像系统坏了。
   *   后端 `take` 上限 200，take=100 已覆盖 dev 全部 61 条。
   * ⚠️ 选版规则见 `pickDefaultVersion()`：走 `isDefault` 锚表（多域取 `activatedAt` 最新者），
   *   **禁按 MAX(PV)/MAX(Run)**（5号位 2026-11-16 矩阵 §0.1 给 4号位的硬条款）。
   */
  async function loadVersions(take = 100): Promise<void> {
    loading.value = true
    error.value = null
    try {
      versions.value = await scheduleApi.getVersions(take)
      currentVersionId.value = pickDefaultVersion(versions.value)?.id ?? null
    } catch (err) {
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  /**
   * 拉**全量**版本列表（解释页版本下拉用；take=200 是后端上限）。
   *
   * ⚠️ 不碰 `currentVersionId`：选中态由调用方自己决定（解释页会按 URL 入参选版本）。
   * ⚠️ 后端 `take` 上限 200（超出会被夹成 30）；dev 实测 61 个版本一次取全（X-Total-Count=61）。
   */
  async function loadAllVersions(take = 200): Promise<void> {
    allVersionsLoading.value = true
    allVersionsError.value = null
    try {
      allVersions.value = await scheduleApi.getVersions(take)
    } catch (err) {
      allVersionsError.value = describeError(err)
    } finally {
      allVersionsLoading.value = false
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

  /**
   * 取 P12 负荷率（设备 × 业务日；5号位 2026-11-17 r14239）。
   *
   * 与负荷带同型的纪律：同键单飞、换参中止上一请求、旧响应不回灌、可中止。
   */
  async function loadResourceLoad(request: ResourceLoadRequest): Promise<void> {
    const id = currentVersionId.value
    if (id === null) return
    const key = resourceLoadKey(id, request)
    if (loadedResourceLoadKey === key && !resourceLoadError.value && resourceLoad.value) return
    if (pendingResourceLoadKey === key && pendingResourceLoadPromise)
      return pendingResourceLoadPromise
    if (resourceLoadCtrl && !resourceLoadCtrl.signal.aborted) resourceLoadCtrl.abort()
    const ctrl = new AbortController()
    resourceLoadCtrl = ctrl
    pendingResourceLoadKey = key
    resourceLoadLoading.value = true
    resourceLoadError.value = null
    const p = fetchResourceLoad(id, request, key, ctrl).finally(() => {
      if (pendingResourceLoadKey === key) {
        pendingResourceLoadKey = null
        pendingResourceLoadPromise = null
        resourceLoadLoading.value = false
      }
    })
    pendingResourceLoadPromise = p
    return p
  }

  async function fetchResourceLoad(
    id: number,
    request: ResourceLoadRequest,
    key: string,
    ctrl: AbortController
  ): Promise<void> {
    try {
      const data = await scheduleApi.getResourceLoad(id, {
        from: request.from,
        to: request.to,
        resourceIds: request.resourceIds,
        signal: ctrl.signal
      })
      if (ctrl.signal.aborted || pendingResourceLoadKey !== key) return
      resourceLoad.value = data
      loadedResourceLoadKey = key
    } catch (err) {
      if (ctrl.signal.aborted || pendingResourceLoadKey !== key) return
      resourceLoadError.value = describeError(err)
    }
  }

  /** 中止在飞的负荷率请求（切窗口 / 换版本 / 离开页面） */
  function cancelResourceLoadLoad(): void {
    if (resourceLoadCtrl && !resourceLoadCtrl.signal.aborted) resourceLoadCtrl.abort()
    resourceLoadCtrl = null
    pendingResourceLoadKey = null
    pendingResourceLoadPromise = null
    resourceLoadLoading.value = false
  }

  /**
   * 取 P05 任务清单（服务端分页；矩阵 A4 / r14237）。
   *
   * 与甘特本体、负荷带、负荷率**四条链各自独立**：同键单飞、换参中止上一请求、旧响应不回灌、可中止。
   * ⇒ 任务清单失败不会把甘特图打空，反之亦然。
   */
  async function loadTaskList(request: TaskListRequest): Promise<void> {
    const id = currentVersionId.value
    if (id === null) return
    const key = taskListKey(id, request)
    if (loadedTaskListKey === key && !taskListError.value && taskList.value) return
    if (pendingTaskListKey === key && pendingTaskListPromise) return pendingTaskListPromise
    if (taskListCtrl && !taskListCtrl.signal.aborted) taskListCtrl.abort()
    const ctrl = new AbortController()
    taskListCtrl = ctrl
    pendingTaskListKey = key
    taskListLoading.value = true
    taskListError.value = null
    const p = fetchTaskList(id, request, key, ctrl).finally(() => {
      if (pendingTaskListKey === key) {
        pendingTaskListKey = null
        pendingTaskListPromise = null
        taskListLoading.value = false
      }
    })
    pendingTaskListPromise = p
    return p
  }

  async function fetchTaskList(
    id: number,
    request: TaskListRequest,
    key: string,
    ctrl: AbortController
  ): Promise<void> {
    try {
      const data = await scheduleApi.getTasks(id, {
        from: request.from,
        to: request.to,
        resourceIds: request.resourceIds,
        stageCodes: request.stageCodes,
        departmentIds: request.departmentIds,
        keyword: request.keyword,
        status: request.status,
        hasWorkOrder: request.hasWorkOrder,
        sortBy: request.sortBy,
        sortDir: request.sortDir,
        pageIndex: request.pageIndex,
        pageSize: request.pageSize,
        signal: ctrl.signal
      })
      // 已被换参取代：丢弃这次结果，别把旧筛选盖回当前视图
      if (ctrl.signal.aborted || pendingTaskListKey !== key) return
      taskList.value = data
      loadedTaskListKey = key
    } catch (err) {
      // 主动取消不是错误；旧请求的失败也不该冒到当前状态上
      if (ctrl.signal.aborted || pendingTaskListKey !== key) return
      // ⚠️ 取数失败必须把**上一版的结果**一起清掉：
      //   不清 ⇒ 切版本后取数失败，页面拿上一个版本的行/总数冒充当前版本（拿旧数当新数）。
      taskList.value = null
      loadedTaskListKey = null
      taskListError.value = describeError(err)
    }
  }

  /** 中止在飞的任务清单请求（切窗口 / 切版本 / 离开页面时调用） */
  function cancelTaskListLoad(): void {
    if (taskListCtrl && !taskListCtrl.signal.aborted) taskListCtrl.abort()
    taskListCtrl = null
    pendingTaskListKey = null
    pendingTaskListPromise = null
    taskListLoading.value = false
  }

  /** 离开甘特页 / 切到无关视图时调用：中止在飞请求，避免「前端放弃、后端白算」 */
  function cancelGanttLoad(): void {
    if (abortCtrl && !abortCtrl.signal.aborted) abortCtrl.abort()
    if (moreCtrl && !moreCtrl.signal.aborted) moreCtrl.abort()
    // 负荷带同属甘特页的在飞请求，一并中止（前端放弃、后端不再白算）
    cancelBucketsLoad()
    // P12 负荷率同理
    cancelResourceLoadLoad()
    // P05 任务清单同理
    cancelTaskListLoad()
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
    resourceLoad.value = null
    loadedResourceLoadKey = null
    resourceLoadError.value = null
    taskList.value = null
    loadedTaskListKey = null
    taskListError.value = null
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
    resourceLoad,
    resourceLoadLoading,
    resourceLoadError,
    taskList,
    taskListLoading,
    taskListError,
    effectiveBucketWidth,
    bucketGroups,
    unscheduledBucketRows,
    activeVersion,
    activeVersions,
    recentComputed,
    failedVersions,
    displayedVersions,
    allVersions,
    allVersionsLoading,
    allVersionsError,
    loadVersions,
    loadAllVersions,
    selectVersion,
    setCurrentVersion,
    loadCurrent,
    loadMore,
    loadBuckets,
    cancelBucketsLoad,
    loadResourceLoad,
    cancelResourceLoadLoad,
    loadTaskList,
    cancelTaskListLoad,
    cancelGanttLoad,
    reset
  }
})
