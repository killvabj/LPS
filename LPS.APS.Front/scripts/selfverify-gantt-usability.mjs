/**
 * APS V1 4号位 — 甘特页「对标知名排产系统」一轮（缩放 / 条宽 / 悬浮 / 负荷视图）探针反向破坏自证（2026-11-02）
 *
 * 背景：领导看图后判「这个用户用不了」⇒ 本轮补齐：时间轴缩放（小时/天/周/月）、
 *   短任务条最小心宽 + 条内任务号、悬浮提示、资源负荷热力图视图。
 *   新增静态探针 GT72–GT79（见 scripts/verify-acceptance.mjs）。
 *   2026-11-03 晚补：GT98（悬浮提示只挂右侧时间轴）、GT99（资源编码=名称时不重复显示）。
 *
 * 用法：node scripts/selfverify-gantt-usability.mjs
 *   逐条改坏 ⇒ 对应 GT 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-tab.mjs：
 *   ① 变异锚点必须命中代码处
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失
 *   ⑤ 字面量出现多次时要一次全改
 *
 * GROUP=gantt 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const DHX = 'src/components/DhxGantt/src/DhxGantt.vue'
// 2026-11-04：资源「编码 | 名称」取值助手抽到公共模块（排产总览页资源表同一套口径）
const RES = 'src/utils/apsResource.ts'

const cases = [
  /* ===== GT72：缩放层级 4 档 + ext.zoom.init + 不劫持滚轮 ===== */
  {
    // 档位名改掉 ⇒ 「小时档」消失 ⇒ 应转红
    id: 'GT72',
    file: DHX,
    from: "name: '小时'",
    to: "name: 'hh'"
  },
  {
    // 改成劫持滚轮（滚轮就不再能横向滚动时间轴）⇒ 应转红
    id: 'GT72',
    file: DHX,
    from: 'trigger: null',
    to: "trigger: 'wheel'"
  },
  {
    // 按档位名设置的入口被摘掉 ⇒ 页面下拉就失效 ⇒ 应转红
    id: 'GT72',
    file: DHX,
    from: 'setZoomLevelByName',
    to: 'setZoomLevelX',
    all: true
  },

  /* ===== GT72：组件对外只暴露「按名设档 / 滚到某天 / 取可见行起点」 ===== */
  {
    // 把 zoomIn/zoomOut 又加回对外 API ⇒ ＋/－ 步进入口可能复活 ⇒ 应转红
    id: 'GT72',
    file: DHX,
    from: 'defineExpose({ showDate, setZoomLevelByName, visibleRowStart })',
    to: 'defineExpose({ showDate, zoomIn, zoomOut, setZoomLevelByName, visibleRowStart })'
  },

  /* ===== GT73：页面刻度控件与组件接线（2026-11-03：刻度只留下拉一个入口）===== */
  {
    // 组件的 zoom-change 回抛没接 ⇒ 页面档位显示不动 ⇒ 应转红
    id: 'GT73',
    file: GANTT,
    from: '@zoom-change="onZoomChange"',
    to: ''
  },
  {
    // 「＋」步进按钮又被塞回工具栏（与「时间尺度」下拉同一条路径的第二个入口）⇒ 应转红
    id: 'GT73',
    file: GANTT,
    from: '<span class="label">时间尺度</span>',
    to: '<span class="label">时间尺度</span>\n<ElButton text @click="zoomIn">＋</ElButton>'
  },
  {
    // 「－」步进按钮又被塞回工具栏 ⇒ 应转红
    id: 'GT73',
    file: GANTT,
    from: '<span class="label">时间尺度</span>',
    to: '<span class="label">时间尺度</span>\n<ElButton text @click="zoomOut">－</ElButton>'
  },

  /* ===== GT74：「时间尺度」单一控件（2026-11-03 窗口+刻度合并后重写）=====
   * 合并前：GT74 锁「刻度与时间窗解耦」、GT75 锁「刻度粗于窗口自动放大」；
   * 合并后两者都被同一条规则取代 —— 一档尺度 = 一个 (窗口跨度, 刻度粒度) 组合。
   */
  {
    // 刻度又回到「写死某一档」的旧耦合写法 ⇒ 应转红
    id: 'GT74',
    file: GANTT,
    from: 'scaleUnit: zoomUnit.value',
    to: "scaleUnit: 'day'"
  },
  {
    // 「小时」档跨度被改坏（1 天 → 9 天）⇒ 一档不再等于一个 (跨度, 粒度) 组合 ⇒ 应转红
    // ⚠️ 锚点带缩进 + 后一行 ⇒ 只命中 SCALE_SPAN_DAYS（ZOOM_MIN_SPAN_DAYS 已随「全览」撤销）
    id: 'GT74',
    file: GANTT,
    from: '  hour: 1,\n  day: 7,',
    to: '  hour: 9,\n  day: 7,'
  },
  {
    // 「时间范围」radio 又被塞回来 ⇒ 同一个轴写两遍（改刻度后 radio 取消选中，看着像失灵）⇒ 应转红
    id: 'GT74',
    file: GANTT,
    from: '<span class="label">时间尺度</span>',
    to: '<span class="label">时间尺度</span>\n<ElRadioGroup model-value="day" />'
  },

  /* ===== GT75：换「时间尺度」时窗口同步换（跨度=该档跨度，起点锚在当前可见行）===== */
  {
    // 换尺度后不再重算窗口（窗口与尺度脱钩 ⇒ 刻度换了、数据窗没换）⇒ 应转红
    // ⚠️ 锚点 `applyScaleWindow(unit)` 只命中调用点；定义处是 `applyScaleWindow(unit: DhxGanttScaleUnit)`
    id: 'GT75',
    file: GANTT,
    from: 'applyScaleWindow(unit)',
    to: 'applyScaleWindowX(unit)'
  },
  {
    // 窗口跨度不再取该档跨度 ⇒ 应转红
    id: 'GT75',
    file: GANTT,
    from: 'const spanDays = SCALE_SPAN_DAYS[unit] ?? 7',
    to: 'const spanDays = 7'
  },
  {
    // 窗口起点不再锚在当前可见行（用户视野被甩出画面）⇒ 应转红
    // ⚠️ 变异必须让字面量**彻底消失**：改成 visibleRowStartX 仍是子串 ⇒ 正则照命中（假绿）
    id: 'GT75',
    file: GANTT,
    from: 'visibleRowStart',
    to: 'vrStart'
  },

  /* ===== GT76：条最小心宽保底 + 标签撑不下写条右侧 ===== */
  {
    // 去掉最小心宽 ⇒ 4 小时的条又只剩 12px（领导说的「用户用不了」）⇒ 应转红
    id: 'GT76',
    file: DHX,
    from: 'min-width: 14px;',
    to: 'min-width: 0px;'
  },
  {
    // 不再按条宽分流（撑不下也硬塞条内）⇒ 应转红
    id: 'GT76',
    file: DHX,
    from: "if (!barLabelFits(task)) return ''",
    to: "if (false) return ''"
  },
  {
    // 条外标签层没挂 ⇒ 窄条上什么都不显示 ⇒ 应转红
    id: 'GT76',
    file: DHX,
    from: 'addTaskLayer((task: DhxGanttTask) => renderOuterLabel(task))',
    to: 'addTaskLayer(() => null)'
  },
  {
    // 条外标签样式没定位 ⇒ 标签会堆在左上角 ⇒ 应转红
    id: 'GT76',
    file: DHX,
    from: ':deep(.dhx-bar-outer-label) {',
    to: ':deep(.dhx-bar-outer-label-x) {'
  },

  /* ===== GT77：悬浮提示 ===== */
  {
    // tooltip 插件没开 ⇒ 悬停无提示 ⇒ 应转红
    id: 'GT77',
    file: DHX,
    from: 'ganttApi.plugins({ marker: true, tooltip: true })',
    to: 'ganttApi.plugins({ marker: true })'
  },
  {
    // 提示里少一行业务信息（订单）⇒ 应转红
    id: 'GT77',
    file: GANTT,
    from: 'lines.push(`订单：${t.orderNo}`)',
    to: ''
  },

  /* ===== GT78：资源负荷视图 + 无产能口径不标超载 ===== */
  {
    // 「暂无产能口径，暂不标超载」的提示被删（后人会误以为已能判超载）⇒ 应转红
    // ⚠️ 同一句话在脚本注释里也有一份 ⇒ 必须一次全改（all），否则模板里那份还在、探针照绿
    id: 'GT78',
    file: GANTT,
    from: '暂无产能口径，暂不标超载。',
    to: '暂无产能口径。',
    all: true
  },
  {
    // 只删模板里那一份（注释那份留着）⇒ 探针必须照样转红
    //    （判据锚在模板元素上，不锚「文件里有这句话」）
    id: 'GT78',
    file: GANTT,
    from: '<span class="load-note-warn">暂无产能口径，暂不标超载。</span>',
    to: '<span class="load-note-warn">暂无产能口径。</span>'
  },
  {
    // 热力图算没了 ⇒ 应转红
    id: 'GT78',
    file: GANTT,
    from: 'const loadHeatmap = computed',
    to: 'const loadHeatmapX = computed'
  },

  /* ===== GT79：负荷带深浅全局归一 ===== */
  {
    // 退回「按行内归一」⇒ 每条泳道看着都"很满"、横向比较是假的 ⇒ 应转红
    id: 'GT79',
    file: DHX,
    from: 'if (band.taskCount > maxCount) maxCount = band.taskCount',
    to: 'maxCount = bands.reduce((m, b) => Math.max(m, b.taskCount), 0)'
  },

  /* ===== GT80：换刻度后视口锚回当前可见行 ===== */
  {
    // 锚点行不按「当前可见行」取（退回全局最早任务）⇒ 仍可能甩到任务区间外 ⇒ 应转红
    id: 'GT80',
    file: DHX,
    from: "querySelectorAll('.gantt_row')",
    to: "querySelectorAll('.gantt_scale_cell')"
  },
  {
    // 不再把视口拉回去（去掉 showDate）⇒ 切档后条子被虚拟渲染裁空 ⇒ 应转红
    // ⚠️ 文件里有两处 showDate?.(target)（收拢函数 + 页面「回到今天」），要一次全改
    id: 'GT80',
    file: DHX,
    from: 'ganttApi.showDate?.(target)',
    to: 'void target',
    all: true
  },
  {
    // 下拉换档入口不收尾（少了锚回）⇒ 应转红
    id: 'GT80',
    file: DHX,
    from: `  zoom.setLevel(name)
  afterZoomLevelChanged()`,
    to: `  zoom.setLevel(name)`
  },

  /* ===== GT98：悬浮提示只挂右侧时间轴（左侧行名列不弹）===== */
  {
    // 改挂到左侧网格行 ⇒ 左侧又会弹出与右侧重复的黑框 ⇒ 应转红
    id: 'GT98',
    file: DHX,
    from: "selector: '.gantt_task_line',",
    to: "selector: '.gantt_row',"
  },
  {
    // 不再摘掉插件默认范围（默认范围同时命中左侧网格行）⇒ 应转红
    id: 'GT98',
    file: DHX,
    from: 'tooltips.detach(`[${attr}]:not(.gantt_task_row)`)',
    to: 'void attr'
  },
  {
    // 接线被摘掉（函数定义了但没跑）⇒ 应转红
    id: 'GT98',
    file: DHX,
    from: "ganttApi.attachEvent('onGanttReady', () => applyTooltipScope())",
    to: 'void applyTooltipScope'
  },

  /* ===== GT99：资源编码与名称相同时不重复显示 ===== */
  {
    // 去重条件反了（name===code 才给两段）⇒ 泳道标题又变「763-2 | 763-2」⇒ 应转红
    // 2026-11-04：取值助手已抽到公共模块 utils/apsResource.ts（甘特页 + 总览页同一套口径）
    id: 'GT99',
    file: RES,
    from: 'if (name && name !== code) return [code, name].filter(Boolean)',
    to: 'if (name && name === code) return [code, name].filter(Boolean)'
  },
  {
    // 泳道标题退回直接拼编码+名称 ⇒ 应转红
    id: 'GT99',
    file: GANTT,
    from: "const identity = resourceCodeNameParts(r)[0] ?? ''",
    to: "const identity = [r.resourceCode, r.resourceName].filter(Boolean).join(' | ')"
  },
  {
    // 资源名称又塞回行标签（占宽度把部门挤成半截）⇒ 应转红
    id: 'GT99',
    file: GANTT,
    from: 'label: laneLabel(identity, r.factoryName, r.productionDepartmentName),',
    to: 'label: laneLabel(`${identity} | ${r.resourceName}`, r.factoryName, r.productionDepartmentName),'
  },
  {
    // 名称不再进悬停（行标签删了、悬停也没有 ⇒ 名称彻底看不到）⇒ 应转红
    id: 'GT99',
    file: GANTT,
    from: '...(lane.resourceName ? [`设备名称：${lane.resourceName}`] : []),',
    to: ''
  },

  /* ===== GT105：小时档可读性（11-05 用户截图标红：小时刻度挤成一团）=====
   * 实测 2 天 = 48 列 × 28px，「00:00」文字宽 36~40px ⇒ 每格溢出。
   * 修法 = 1 天窗 + 列宽下限 44px + 单日标签不写重复起止。
   */
  {
    // 小时列宽退回 28px（窄屏必然挤成一团）⇒ 应转红
    id: 'GT105',
    file: DHX,
    from: 'min_column_width: 44',
    to: 'min_column_width: 28'
  },
  {
    // 单日窗口仍拼「起 ~ 止」⇒ 又出现「11-05 ~ 11-05（1 天）」⇒ 应转红
    id: 'GT105',
    file: GANTT,
    from: '  if (span === 1) return `${from}（1 天）`\n',
    to: ''
  },
  {
    // 图例把「延期」加回来 ⇒ 应转红
    id: 'GT106',
    file: GANTT,
    from:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n',
    to:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n' +
      '          <span class="legend-item"><i class="legend-swatch is-delay"></i>延期</span>\n'
  },
  {
    // 图例把「资源负荷（深浅=任务数）」加回来 ⇒ 应转红
    id: 'GT106',
    file: GANTT,
    from:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n',
    to:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n' +
      '          <span class="legend-item"><i class="legend-swatch is-load"></i>资源负荷（深浅=任务数）</span>\n'
  },
  {
    // 图例把「资源占用区间」加回来 ⇒ 应转红
    id: 'GT106',
    file: GANTT,
    from:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n',
    to:
      '          <span v-for="item in legendItems" :key="item.label" class="legend-item">\n' +
      '            <i class="legend-swatch" :style="{ background: item.color }"></i>{{ item.label }}\n' +
      '          </span>\n' +
      '          <span class="legend-item"><i class="legend-swatch is-lane"></i>资源占用区间</span>\n'
  },
  {
    // 「插单影响分析」入口加回来 ⇒ 应转红
    id: 'GT107',
    file: GANTT,
    from: '          <span>任务清单（{{ windowLabel }}）</span>\n',
    to:
      '          <span>任务清单（{{ windowLabel }}）</span>\n' +
      '          <ElButton type="info" plain>插单影响分析</ElButton>\n'
  },
  {
    // 工具栏退回纵向（按钮又独占一行）⇒ 应转红
    id: 'GT107',
    file: GANTT,
    from: '.toolbar {\n  display: flex;\n  align-items: center;\n',
    to: '.toolbar {\n  display: flex;\n  flex-direction: column;\n  align-items: center;\n'
  },
  {
    // 「白天人工调整」被挪到搜索框之前（按钮不再跟在搜索框后面）⇒ 应转红
    id: 'GT107',
    file: GANTT,
    from: '<div class="toolbar-left">\n',
    to: '<div class="toolbar-left">\n        <span>白天人工调整</span>\n'
  },
  {
    // 导出清单又出现在工具栏里 ⇒ 应转红
    id: 'GT108',
    file: GANTT,
    from:
      '        <span v-if="keyword.trim()" class="search-hint"> 命中 {{ filteredTasks.length }} 条 </span>\n',
    to:
      '        <span v-if="keyword.trim()" class="search-hint"> 命中 {{ filteredTasks.length }} 条 </span>\n' +
      '        <ElButton>导出清单</ElButton>\n'
  },
  {
    // 清单页头按钮不再触发导出 ⇒ 应转红
    id: 'GT108',
    file: GANTT,
    from: '@click="exportTaskList"',
    to: '@click="noopExport"'
  },
  {
    // 「共 N 条 · 排序 · 同源」说明行加回来 ⇒ 应转红
    id: 'GT108',
    file: GANTT,
    from: '          <span>任务清单（{{ windowLabel }}）</span>\n',
    to:
      '          <span>任务清单（{{ windowLabel }}）</span>\n' +
      '          <span class="task-list-hint">共 {{ taskListTotal }} 条 · 按计划开始时间排序 · 与图上条数同源（筛选/搜索后一致）</span>\n'
  },

  /* ===== GT109：版本项悬停信息（2026-11-05）===== */
  {
    // 悬停又改回只显示标签 ⇒ 版本信息（Base/Run/时间）全丢 ⇒ 应转红
    id: 'GT109',
    file: GANTT,
    from: ':title="versionOptionTitle(v)"',
    to: ':title="versionOptionLabel(v)"'
  },
  {
    // 悬停里不再给生成时间 ⇒ 应转红
    id: 'GT109',
    file: GANTT,
    from: "`生成时间：${v.computedAt ? formatUtcDateTimeSec(v.computedAt) : '未提供'}`",
    to: '`生成时间：未接入`'
  },
  {
    // 悬停里不再给基线版本（Base）⇒ 应转红
    // 2026-11-05：原「把 DTO 没有载体的 DataCutoff 硬塞进页面」一条已废 ——
    //   B10 已给 DataCutoffTime / RunType / TriggeredBy 载体，数据截止的回归自证
    //   移入 selfverify-gantt-b2b10.mjs（GT109 三条）。
    id: 'GT109',
    file: GANTT,
    from: 'v.basePlanVersionId',
    to: 'v.basePlanVersionIdX',
    all: true
  },

  /* ===== GT110：任务清单三列（客户交期 / 延期时长 / 主要原因）===== */
  {
    // 客户交期列被删 ⇒ 应转红
    id: 'GT110',
    file: GANTT,
    from: '<ElTableColumn label="客户交期" width="110">',
    to: '<ElTableColumn label="客户交期X" width="110">'
  },
  {
    // CSV 少「主要原因」列 ⇒ 导出与表格不一致 ⇒ 应转红
    id: 'GT110',
    file: GANTT,
    from: "  '主要原因',\n  '延期',",
    to: "  '原因',\n  '延期',"
  },

  /* ===== GT112：甘特左表物料口径 = materialCode（2026-11-05）===== */
  {
    // 左表首列物料又退回 materialName ⇒ 与清单/导出口径不一致 ⇒ 应转红
    id: 'GT112',
    file: GANTT,
    from: 'const material = t.materialCode?.trim()',
    to: 'const material = t.materialName?.trim()'
  },

  /* ===== GT111：缺数据只说「未提供」===== */
  {
    // 退回「单段加工（未拆分）」⇒ 断言后端从未提供的拆分事实 ⇒ 应转红
    // 2026-11-05 B8：拆分展示位整块撤除 ⇒ 原锚点文案已不在，改从设备故障影响段前注入
    id: 'GT111',
    file: GANTT,
    from: '<!-- Section 十二 / U20：设备故障影响',
    to:
      '<div class="segment-section">单段加工（未拆分）</div>\n                  <!-- Section 十二 / U20：设备故障影响'
  },
  {
    // 退回「单 Order 任务（无 TaskShare）」⇒ 应转红
    id: 'GT111',
    file: GANTT,
    from: '<Icon icon="vi-mdi:link-variant" /> 未提供订单份额数据（TaskShare）',
    to: '<Icon icon="vi-mdi:link-variant" /> 单 Order 任务（无 TaskShare）'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'gantt' },
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
    out = run()
  } finally {
    writeFileSync(p, orig, 'utf8')
  }
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态'))
  const red = mine.some((l) => l.includes('❌'))
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
