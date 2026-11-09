/**
 * APS V1 4号位 — 甘特图大数据量第 0 步 探针反向破坏自证（2026-10-29）
 *
 * 背景：dev 压测版本 121 万任务，仅 7 天窗就 80MB / 13.6 万条、服务端 5.5s；前端 30s 超时先放弃、
 *   后端继续算 ⇒ 同端点其它请求被拖到 60~230s，版本下拉拿不到数据。第 0 步：
 *   一律窗口加载（不再整版本全量）+ 同键单飞 + 可取消 + 加载量明示。
 *
 * 用法：node scripts/selfverify-gantt-window.mjs
 *   逐条改坏 ⇒ 对应 GT 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * GROUP=gantt 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const STORE = 'src/store/modules/aps/schedule.ts'
const HTTP = 'src/api/aps-v1/http.ts'
const SCHEDULE_API = 'src/api/aps-v1/schedule.ts'
const DHX = 'src/components/DhxGantt/src/DhxGantt.vue'
const TYPES = 'src/api/aps-v1/types/schedule.ts'

const cases = [
  /* ===== GT36：一律带时间窗加载 ===== */
  {
    // 退回裸调 loadCurrent()（整版本 / 8000 默认页）⇒ 应转红
    id: 'GT36',
    file: GANTT,
    from: 'loadCurrent(currentGanttWindow())',
    to: 'loadCurrent()',
    all: true
  },
  {
    // 版本切换不带窗口 ⇒ 应转红
    id: 'GT36',
    file: GANTT,
    from: 'selectVersion(v, currentGanttWindow())',
    to: 'selectVersion(v)'
  },
  {
    // 删掉「窗口变了 → 按新窗重取」的 watch ⇒ 换窗口仍显示旧数据，应转红
    // ⚠️ 2026-11-03：窗口改由「时间尺度」驱动 ⇒ watch 的观察对象从 timeRange 变成 ganttWindow；
    //    锚点必须跟着源码走，否则本条会报 MISSING-MUTATION-ANCHOR（= 变成没验过）。
    id: 'GT36',
    file: GANTT,
    from: `watch(ganttWindow, () => {
  if (currentVersionId.value !== null) {
    void scheduleStore.loadCurrent(currentGanttWindow())
    void scheduleStore.loadBuckets(currentBucketWindow())
  }
})
`,
    to: ''
  },

  /* ===== GT37：同键单飞 + limit/signal + 换窗口中止 ===== */
  {
    // 不再传 limit ⇒ 后端按自身缺省返回，应转红（首屏与追加两条路径都要改）
    id: 'GT37',
    file: STORE,
    from: 'limit: GANTT_PAGE_LIMIT',
    to: 'limit: 0',
    all: true
  },
  {
    // 不传取消信号 ⇒ 切窗口后旧请求仍在飞，应转红（两条路径）
    id: 'GT37',
    file: STORE,
    from: 'signal: ctrl.signal',
    to: 'signal: undefined',
    all: true
  },
  {
    // 去掉「同键单飞」判断 ⇒ 并发点击会双发，应转红
    id: 'GT37',
    file: STORE,
    from: 'if (pendingKey === key && pendingPromise) return pendingPromise',
    to: 'if (pendingKey === key) return pendingPromise'
  },
  {
    // 不建 AbortController ⇒ 无法中止，应转红（两条路径）
    id: 'GT37',
    file: STORE,
    from: 'const ctrl = new AbortController()',
    to: 'const ctrl = { signal: { aborted: false } } as AbortController',
    all: true
  },

  /* ===== GT38：取消不弹错、不跳 500 ===== */
  {
    // 去掉取消短路（取消会被当网络层错误 ⇒ 跳 /500）⇒ 应转红
    id: 'GT38',
    file: HTTP,
    from: 'axios.isCancel(error as unknown)',
    to: 'axios.isCancelNever(error as unknown)'
  },
  {
    // 不透传 AbortSignal ⇒ 调用方取消不了，应转红
    id: 'GT38',
    file: HTTP,
    from: 'signal: opts.signal,',
    to: 'signal: undefined,',
    all: true
  },

  /* ===== GT39：加载量明示 + 窗口空态 ===== */
  {
    // hasMore 判定反了 ⇒ 有下一页也不提示，应转红
    id: 'GT39',
    file: GANTT,
    from: 'const hasMoreInWindow = computed(() => ganttData.value?.hasMore === true)',
    to: 'const hasMoreInWindow = computed(() => ganttData.value?.hasMore === false)'
  },
  {
    // 删掉「已加载 N 条」计数 ⇒ 用户把不完整图当完整图，应转红
    id: 'GT39',
    file: GANTT,
    from: '已加载前 {{ stats.total }} 条',
    to: '已加载条数'
  },
  {
    // 窗口空态退回「当前版本没有排程结果」⇒ 误导用户以为版本空，应转红
    id: 'GT39',
    file: GANTT,
    from: "    title: '当前时间范围没有排程任务',",
    to: "    title: '当前版本没有排程结果',"
  },

  /* ===== GT40：离开页面取消在飞请求 ===== */
  {
    // 卸载时不取消 ⇒ 前端放弃、后端白算，应转红
    id: 'GT40',
    file: GANTT,
    from: '  scheduleStore.cancelGanttLoad()\n})',
    to: '  void 0\n})'
  },

  /* ===== GT41–GT45：第 1 步纵向懒加载（游标追加）===== */
  {
    // 追加改成替换 ⇒ 每次翻页只剩一页，用户永远看不到后续资源，应转红
    id: 'GT41',
    file: STORE,
    from: 'tasks: [...d.tasks, ...newTasks],',
    to: 'tasks: newTasks,'
  },
  {
    // 去掉 taskId 去重 ⇒ 重叠页会重复渲染同一条任务，应转红
    id: 'GT41',
    file: STORE,
    from: 'const seenTasks = new Set(d.tasks.map((t) => t.taskId))',
    to: 'const seenTasks = new Set<number>()'
  },
  {
    // 游标参数名写错 ⇒ 后端按首页返回，翻页永远拿同一批，应转红
    id: 'GT42',
    file: SCHEDULE_API,
    from: "params.append('afterTaskId', String(window.after.taskId))",
    to: "params.append('afterTask', String(window.after.taskId))"
  },
  {
    // 不接滚动到底事件 ⇒ 滑到底不加载，应转红
    id: 'GT43',
    file: GANTT,
    from: '        @near-bottom="handleGanttNearBottom"\n',
    to: ''
  },
  {
    // 不提示「向下滚动继续加载」⇒ 用户不知道还有下一页，应转红
    id: 'GT43',
    file: GANTT,
    from: '向下滚动继续加载',
    to: '往下翻翻看'
  },
  {
    // 组件不抛 nearBottom ⇒ 页面永远收不到滚动信号，应转红
    id: 'GT44',
    file: DHX,
    from: "emits('nearBottom')",
    to: "emits('neverScroll')"
  },
  {
    // 追加后不恢复滚动位置 ⇒ 滑到底加载完弹回顶部，无法连续下滑，应转红
    id: 'GT44',
    file: DHX,
    from: 'el.scrollTop = prevScrollTop',
    to: 'el.scrollTop = 0'
  },
  {
    // 追加请求不被中止 ⇒ 换窗后旧页数据会回灌，应转红
    id: 'GT45',
    file: STORE,
    from: 'if (moreCtrl && !moreCtrl.signal.aborted) moreCtrl.abort()',
    to: 'if (false) moreCtrl.abort()',
    all: true
  },

  /* ===== GT46–GT50：第 2 步 负荷带聚合端点（5号位 r13972）===== */
  {
    // 把「实际生效桶宽」改成必填 ⇒ 丢掉后端整键省略语义，应转红
    id: 'GT46',
    file: TYPES,
    from: 'bucketWidth?: GanttBucketUnit',
    to: 'bucketWidth: GanttBucketUnit'
  },
  {
    // 把「桶起点」改成必填 ⇒ 未排桶行（缺键）无法表达，应转红
    id: 'GT46',
    file: TYPES,
    from: 'bucketStart?: IsoDateTime',
    to: 'bucketStart: IsoDateTime'
  },
  {
    // from 改成可选 ⇒ 前端会发出无 from 请求（后端 400），应转红
    id: 'GT47',
    file: SCHEDULE_API,
    from: 'from: string',
    to: 'from?: string'
  },
  {
    // resourceIds 改成逗号拼接 ⇒ 后端 422，应转红
    id: 'GT47',
    file: SCHEDULE_API,
    from: "for (const id of opts.resourceIds ?? []) params.append('resourceIds', String(id))",
    to: "for (const id of []) params.append('resourceIds', (opts.resourceIds ?? []).join(','))"
  },
  {
    // 去掉同键单飞 ⇒ 并发会双发，应转红
    id: 'GT48',
    file: STORE,
    from: 'if (pendingBucketsKey === key && pendingBucketsPromise) return pendingBucketsPromise',
    to: 'if (pendingBucketsKey === key) return pendingBucketsPromise'
  },
  {
    // 不中止上一请求 ⇒ 换窗后旧桶数据回灌，应转红
    id: 'GT48',
    file: STORE,
    from: 'if (bucketsCtrl && !bucketsCtrl.signal.aborted) bucketsCtrl.abort()',
    to: 'if (false) bucketsCtrl.abort()'
  },
  {
    // 离页时不中止负荷带请求 ⇒ 前端放弃后端白算，应转红
    id: 'GT48',
    file: STORE,
    from: `function cancelBucketsLoad(): void {
    if (bucketsCtrl && !bucketsCtrl.signal.aborted) bucketsCtrl.abort()`,
    to: `function cancelBucketsLoad(): void {
    if (false) bucketsCtrl.abort()`
  },
  {
    // 图层改用请求的 bucket 而非实际生效的 bucketWidth ⇒ 降级后画错桶宽，应转红
    id: 'GT49',
    file: STORE,
    from: 'buckets.value?.bucketWidth',
    to: 'buckets.value?.bucket'
  },
  {
    // 按窗口 from 裁首桶 ⇒ 丢掉窗头跨窗任务的负荷（周桶锚=周一，首桶可早于 from），应转红
    id: 'GT50',
    file: STORE,
    from: 'if (row.bucketStart == null) continue',
    to: "if (row.bucketStart == null || row.bucketStart >= bucketsWindow.value!.from) continue"
  },
  {
    // 改用 domainKey 分组 ⇒ 版本域恒相同，所有资源并成一组，应转红
    id: 'GT50',
    file: STORE,
    from: 'map.set(row.resourceId, [row])',
    to: 'map.set(row.domainKey as unknown as number, [row])'
  },
  {
    // 未排桶行判反 ⇒ 真正未排桶的行被当成有桶行，应转红
    id: 'GT50',
    file: STORE,
    from: '(buckets.value?.items ?? []).filter((r) => r.bucketStart == null)',
    to: '(buckets.value?.items ?? []).filter((r) => r.bucketStart != null)'
  },

  /* ===== GT51–GT57：第 2 步 负荷带图层落地（2026-10-30）===== */
  {
    // 不注册图层 ⇒ 负荷带画不出来，应转红
    id: 'GT51',
    file: DHX,
    from: 'loadBandLayerId = ganttApi.addTaskLayer((task: DhxGanttTask) => renderLoadBand(task)) as',
    to: 'loadBandLayerId = null as'
  },
  {
    // 图层不只画泳道行 ⇒ 任务行上也画，语义错，应转红
    //（锚点带函数头：同一句 `task.type !== 'project'` 在泳道占用图层里也有一处）
    id: 'GT51',
    file: DHX,
    from: "const renderLoadBand = (task: DhxGanttTask) => {\n  if (task.type !== 'project') return null",
    to: 'const renderLoadBand = (task: DhxGanttTask) => {\n  if (false) return null'
  },
  {
    // 去掉图层 CSS 类名 ⇒ 画出来没样式，应转红
    id: 'GT51',
    file: DHX,
    from: "cell.className = 'dhx-load-band-cell'",
    to: "cell.className = 'dhx-load-band-cell-x'"
  },
  {
    // 页面不接线 ⇒ 图层拿不到数据，应转红
    id: 'GT52',
    file: GANTT,
    from: ':load-bands="loadBands"',
    to: ':load-bands="[]"'
  },
  {
    // 负荷带不取自桶分组 ⇒ 画的是别的东西，应转红
    id: 'GT52',
    file: GANTT,
    from: 'for (const group of scheduleStore.bucketGroups) {',
    to: 'for (const group of []) {'
  },
  {
    // 不判 -1 ⇒ 桶侧未分配行落不到未分配泳道，应转红
    id: 'GT53',
    file: GANTT,
    from: 'return t.resourceId == null || t.resourceId === UNASSIGNED_RESOURCE_ID',
    to: 'return t.resourceId == null'
  },
  {
    // 不建未分配泳道 ⇒ 那批任务无处可去，应转红
    id: 'GT53',
    file: GANTT,
    from: 'if (hasUnassignedTasks.value) {',
    to: 'if (false) {'
  },
  {
    // 窗口边界改回 toISOString ⇒ 折成 UTC，数据窗比可见轴早 8 小时，应转红
    id: 'GT54',
    file: GANTT,
    from: 'function wallClock(d: dayjs.Dayjs): string {\n  return d.format(\'YYYY-MM-DDTHH:mm:ss\')\n}',
    to: 'function wallClock(d: dayjs.Dayjs): string {\n  return d.toISOString()\n}'
  },
  {
    // 去掉「未排期」KPI ⇒ 未排桶任务静默丢，应转红
    id: 'GT55',
    file: GANTT,
    from: '<ElTooltip v-if="unscheduledLoad.count > 0" placement="bottom" :show-after="100">',
    to: '<ElTooltip v-if="false" placement="bottom" :show-after="100">'
  },
  {
    // 桶宽改用请求值而非后端实际生效值 ⇒ 降级后画错，应转红
    id: 'GT56',
    file: GANTT,
    from: 'const unit = scheduleStore.effectiveBucketWidth',
    to: 'const unit = scheduleStore.bucketUnit'
  },
  {
    // 按窗口 from 裁首桶 ⇒ 丢掉窗头跨窗任务的负荷，应转红
    id: 'GT56',
    file: GANTT,
    from: '      if (!row.bucketStart) continue',
    to: '      if (!row.bucketStart || row.bucketStart >= dateRange.value.from) continue'
  },
  {
    // 去掉「仅按资源视图」门控 ⇒ 在无法归组的维度上硬画，应转红
    id: 'GT57',
    file: GANTT,
    from: "const loadBandVisible = computed(() => groupBy.value === 'resource')",
    to: "const loadBandVisible = computed(() => true)"
  },

  /* ===== GT100：KPI 行不显示任务数/版本总数（2026-11-04 二改）===== */
  {
    // 把「任务 N」加回 KPI 行 ⇒ 与「任务清单」页签徽标同屏重复，应转红
    id: 'GT100',
    file: GANTT,
    from: `资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`,
    to: `任务 <strong>{{ stats.total }}</strong>
          | 资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`
  },
  {
    // 把「版本共 N 条（整个计划区间）」加回 KPI 行 ⇒ 整个计划区间的数摆在按窗口看的工具栏，应转红
    id: 'GT100',
    file: GANTT,
    from: `资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`,
    to: `<template v-if="versionTotalTasks != null">
            · 版本共 <strong>{{ versionTotalTasks }}</strong> 条（整个计划区间）
          </template>
          | 资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`
  },
  {
    // 把口径标注（stat-scope）加回来 ⇒ 用户又看到同屏两个任务数，应转红
    id: 'GT100',
    file: GANTT,
    from: `资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`,
    to: `<span class="stat-scope">（{{ windowLabel }} 窗口内）</span>
          资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道`
  },
  {
    // 把资源/泳道整段删掉 ⇒ KPI 被清空（连带把该断的东西删了），应转红
    id: 'GT100',
    file: GANTT,
    from: `资源 <strong>{{ ganttData?.resources.length ?? 0 }}</strong> | 泳道
          <strong>{{ lanes.length }}</strong`,
    to: `资源数已下线`
  },

  /* ===== GT101：同页三个任务数不重复（11-04 用户截图反馈：剩余 2431 与 域 2431 重复） ===== */
  {
    // 域分项恒显示（单域时它 ≡ 清单条数 ⇒ 屏上两个一样的数）⇒ 应转红
    id: 'GT101',
    file: GANTT,
    from: '<template v-if="availableDomains.length > 1"',
    to: '<template v-if="availableDomains.length >= 0"'
  },
  {
    // 把工具栏「剩余 N 条」加回来（与域计数之和恒等 ⇒ 纯重复）⇒ 应转红
    id: 'GT101',
    file: GANTT,
    from: `<span v-if="keyword.trim()" class="search-hint"> 命中 {{ filteredTasks.length }} 条 </span>`,
    to: `<span v-if="keyword.trim()" class="search-hint"> 命中 {{ filteredTasks.length }} 条 </span>
        <span v-if="keyword.trim()" class="search-hint"> 剩余 {{ filteredTasks.length }} 条 </span>`
  },
  {
    // Tab 徽标改回窗口总数（而不是当前生效条数）⇒ 应转红
    id: 'GT101',
    file: GANTT,
    from: '>{{ taskListTotal }}</span',
    to: '>{{ ganttData?.totalTasks ?? 0 }}</span'
  },

  /* ===== GT102：切版本清掉上一版的资源筛选（11-04 用户反馈：TZP 在甘特上没有任务） ===== */
  {
    // 切版本时不清资源多选 ⇒ resourceIds 直接发给后端 ⇒ 新版本没这些资源 ⇒ 后端回 0 条
    id: 'GT102',
    file: GANTT,
    from: `  selectedResourceIds.value = []
  void scheduleStore.loadCurrent(currentGanttWindow())`,
    to: `  void scheduleStore.loadCurrent(currentGanttWindow())`
  },
  {
    // 切版本不重取数据 ⇒ 沿用上一版窗口的数据
    id: 'GT102',
    file: GANTT,
    from: `  void scheduleStore.loadCurrent(currentGanttWindow())
  void scheduleStore.loadBuckets(currentBucketWindow())
})
`,
    to: `  void scheduleStore.loadBuckets(currentBucketWindow())
})
`
  },

  /* ===== GT103：窗口空时提示任务真实区间 + 一键跳转 ===== */
  {
    // 不用桶聚合的 firstStart（改用不存在的字段）⇒ 算不出任务真实区间 ⇒ 应转红
    id: 'GT103',
    file: GANTT,
    from: 'it.firstStart',
    to: 'it.start',
    all: true
  },
  {
    // 提示条不写任务所在区间（用户仍分不清是「没数据」还是「没看对时间」）⇒ 应转红
    id: 'GT103',
    file: GANTT,
    from: '该版本 {{ versionTotalTasks ?? 0 }} 条任务排在 {{ taskSpanText }}，不在当前时间窗（{{',
    to: '该版本有任务，不在当前时间窗（{{'
  },
  {
    // 去掉「跳到任务所在时间」按钮 ⇒ 用户只能自己猜时间窗 ⇒ 应转红
    id: 'GT103',
    file: GANTT,
    from: '            跳到任务所在时间',
    to: '            跳过去'
  },
  {
    // 跳转函数不设窗口 ⇒ 点了按钮也还在原窗口 ⇒ 应转红
    id: 'GT103',
    file: GANTT,
    from: `  ganttWindow.value = {
    from: wallClock(from),
    to: wallClock(to),
    spanDays: Math.max(1, to.diff(from, 'day') + 1)
  }`,
    to: `  void from
  void to`
  },

  /* ===== GT104：撤「排程域」筛选，域并入版本下拉（11-04 用户拍板） ===== */
  {
    // 版本标签丢掉所属域 ⇒ 用户选版本时又看不到域 ⇒ 应转红
    id: 'GT104',
    file: GANTT,
    from: "  const domain = v.domainKey ? domainLabel(v.domainKey) : ''",
    to: "  const domain = ''"
  },
  {
    // 把「排程域」筛选下拉加回来（单域时恒 1 个选项、选了等于没选）⇒ 应转红
    id: 'GT104',
    file: GANTT,
    from: '        <!-- 域信息已并入「版本」下拉（每个版本自带所属域）⇒ 不再单设「排程域」筛选 -->\n        <DomainUnavailableBadge />',
    to: '        <span class="label">排程域</span>\n        <DomainUnavailableBadge />'
  },
  {
    // 过滤链重新按域收窄 ⇒ 域筛选复活 ⇒ 应转红
    id: 'GT104',
    file: GANTT,
    from: '  let tasks = ganttData.value?.tasks ?? []',
    to: '  let tasks = (ganttData.value?.tasks ?? []).filter((t) => domainFilter.value.includes(t.domainKey))'
  },

  /* ===== E19（10-29 改判据）不在本脚本 =====
   * E19 位于 `verifyExplanationFreeze`（GROUP=e-freeze，每个用例都要起新进程登录一次），
   * 逐条自证会把 dev 登录频控（5 次/分钟）打满 ⇒ 改为手工单次验证：
   *   把 `      handleError(err)` 改成 `      error.value = String(err)` → run
   *   `GROUP=e-freeze` → E19 转红（keepsLast=false）→ 还原。
   */
]

function run(group) {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: group },
    encoding: 'utf8'
  })
  return (r.stdout || '') + (r.stderr || '')
}

let allOk = true
for (const c of cases) {
  const p = join(ROOT, c.file)
  const orig = readFileSync(p, 'utf8')
  const eol = orig.includes('\r\n') ? '\r\n' : '\n'
  const from = c.from.split('\n').join(eol)
  const to = c.to.split('\n').join(eol)
  if (!orig.includes(from)) {
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR — ${c.from.slice(0, 60)}`)
    allOk = false
    continue
  }
  writeFileSync(p, c.all ? orig.split(from).join(to) : orig.replace(from, to), 'utf8')
  let out = ''
  try {
    out = run(c.group || 'gantt')
  } finally {
    writeFileSync(p, orig, 'utf8')
  }
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态'))
  const red = mine.some((l) => l.includes('❌'))
  if (!mine.length) {
    // 探针没跑到本条 ⇒ 子进程可能崩了；把尾部输出打出来便于定位（不是断言失败，是「没验过」）
    console.log(`---- ${c.id} 子进程未产出本条断言，尾部输出 ----`)
    console.log(out.split('\n').slice(-25).join('\n'))
  }
  const others = out
    .split('\n')
    .filter((l) => l.includes('❌') && !mine.includes(l) && !l.includes('失败：') && !l.includes('结果'))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
