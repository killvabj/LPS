/**
 * APS V1 4号位 — 甘特图可读性增强探针反向破坏自证（2026-11-05）
 *
 * 背景：用户对标主流排产系统的甘特图，要求左表拆出「物料」单列、条内显示
 *   「任务号 | 订单号」、已完成打勾、延期标 +Nh、资源泳道浅绿底。
 *   对应静态探针 GT118；同脚本包含 GT119（任务清单 10 新键接线，5号位 2026-11-05 知会）。
 *
 * 用法：node scripts/selfverify-gantt-visual.mjs
 *   逐条改坏 ⇒ GT118 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-a-group.mjs：
 *   ① 变异锚点必须命中代码处（MISSING-MUTATION-ANCHOR = 这一条没验过）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 被搜字面量必须**彻底消失**（改名要改到调用形一起断，不能只加后缀）
 *   ④ 字面量出现多次时要一次全改（all）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const DHX = 'src/components/DhxGantt/src/DhxGantt.vue'
const DHX_TYPES = 'src/components/DhxGantt/src/types.ts'

const cases = [
  /* ===== GT118-b：完成 ✓ / 延期 +Nh =====
   * 原 GT118-a（物料单列）已随 2026-11-05 需求撤除：物料并入左表首列，
   *   合并口径由 GT120 锁定（selfverify-gantt-a-group.mjs）。 */
  {
    // 页面不再给标记类型 ⇒ 应转红
    id: 'GT118',
    file: GANTT,
    from: "barMark: status === 'COMPLETED' ? 'done' : t.isDelayed ? 'delay' : '',",
    to: "barMark: '',"
  },
  {
    // 完成标记文字被摘 ⇒ 应转红
    id: 'GT118',
    file: GANTT,
    from: "          ? '✓'",
    to: "          ? ''"
  },
  {
    // 延期标记不再带小时数 ⇒ 应转红
    id: 'GT118',
    file: GANTT,
    from: '`+${Math.round(t.delayHours)}h`',
    to: "''"
  },
  {
    // 标记文字类型位被删 ⇒ 应转红
    id: 'GT118',
    file: DHX_TYPES,
    from: 'barMarkText?: string',
    to: 'barMarkTextX?: string'
  },
  {
    // 条内标签不再带状态标记 ⇒ 应转红
    id: 'GT118',
    file: DHX,
    from: 'return `<span class="dhx-bar-inner">${label}${mark}</span>`',
    to: 'return label'
  },
  {
    // 标记不再区分已完成/延期配色 ⇒ 应转红
    id: 'GT118',
    file: DHX,
    from: "const markClass = task.barMark === 'done' ? 'is-done' : 'is-delay'",
    to: "const markClass = ''"
  },

  /* ===== GT28：泳道摘要条（16px / N 条 / 延期 M，2026-11-06）===== */
  {
    // 摘要条退回任务条同高（与任务条混在一起）⇒ 应转红
    id: 'GT28',
    file: GANTT,
    from: 'bar_height: 16,',
    to: 'bar_height: 32,'
  },
  {
    // 条上不再写任务计数 ⇒ 应转红
    id: 'GT28',
    file: GANTT,
    from: "barLabel: laneTasks.length ? `${laneTasks.length} 条` : '',",
    to: "barLabel: '',"
  },
  {
    // 延期条数不上条（只有总数看不出延期）⇒ 应转红
    id: 'GT28',
    file: GANTT,
    from: "barMark: delayedCount > 0 ? 'delay' : '',",
    to: "barMark: '',"
  },
  {
    // 泳道条内不再渲染延期标记 ⇒ 应转红
    id: 'GT28',
    file: DHX,
    from: 'class="dhx-lane-mark is-delay"',
    to: 'class="dhx-lane-mark-x"'
  },
  {
    // 窄泳道条条外摘要被撤（短泳道不显示条数）⇒ 应转红
    id: 'GT28',
    file: DHX,
    from: "'dhx-bar-outer-label is-lane'",
    to: "'dhx-bar-outer-label-x'"
  },

  /* ===== GT125：条宽判断含状态标记（2026-11-06 用户截图：44px 条只剩红字）===== */
  {
    // 条宽只量任务号、不计标记（任务号会被 flex 压成 0 宽）⇒ 应转红
    id: 'GT125',
    file: DHX,
    from: 'if (task.barMarkText) need += measureLabelWidth(String(task.barMarkText))',
    to: 'if (false) need += measureLabelWidth(String(task.barMarkText))'
  },

  /* ===== GT118-c：泳道带不再整片刷绿（2026-11-06 A4）===== */
  {
    // 浅色泳道退回旧的浅绿底 ⇒ 应转红
    id: 'GT118',
    file: DHX,
    from: '#eef2f8',
    to: '#d3f1e3'
  },

  /* ===== GT124：窄条条外标签补红色延期小时（2026-11-06）===== */
  {
    // 外挂层不再渲染延期标记（窄条看不到延期）⇒ 应转红
    id: 'GT124',
    file: DHX,
    from: 'class="dhx-outer-mark is-delay"',
    to: 'class="dhx-outer-mark-x"'
  },
  {
    // 延期小时不再是红色（与任务号同色，看不出是延期）⇒ 应转红
    id: 'GT124',
    file: DHX,
    from: 'color: #dc2626;',
    to: 'color: #334155;'
  },
  /* ===== GT95：条标签分隔改竖线（2026-11-06）===== */
  {
    // 分隔符退回「 · 」⇒ 应转红
    id: 'GT95',
    file: GANTT,
    from: "'|' + instruction",
    to: "' · ' + instruction"
  },

  /* ===== GT29：延期条不画红线（2026-11-06 用户指定撤除）===== */
  {
    // 条底红刻度加回来 ⇒ 应转红
    id: 'GT29',
    file: DHX,
    from: ':deep(.gantt_task_line) {\n  min-width: 14px;',
    to: ':deep(.gantt_task_line.dhx-task-warning) {\n  box-shadow: inset 0 -2px 0 rgb(239 68 68 / 80%);\n}\n\n:deep(.gantt_task_line) {\n  min-width: 14px;'
  },
  {
    // 3px 红左缘加回来 ⇒ 应转红
    id: 'GT29',
    file: DHX,
    from: ':deep(.gantt_task_line) {\n  min-width: 14px;',
    to: ':deep(.dhx-task-warning .gantt_task_content) {\n  border-left: 3px solid #ef4444;\n}\n\n:deep(.gantt_task_line) {\n  min-width: 14px;'
  },

  /* ===== GT126：已排产条色（2026-11-06 用户指定 #7FB8B0）===== */
  {
    // 已排产条色退回旧灰蓝 ⇒ 应转红
    id: 'GT126',
    file: GANTT,
    from: "  PLANNED: '#7FB8B0',",
    to: "  PLANNED: '#94a3b8',"
  },

  /* ===== GT119：任务清单信息补全（10 新键 + 4 列 + CSV）===== */
  {
    // 部门名称类型位被删 ⇒ 应转红
    id: 'GT119',
    file: 'src/api/aps-v1/types/schedule.ts',
    from: 'departmentName?: string',
    to: 'departmentNameX?: string'
  },
  {
    // 清单「生产指示号」列被改名（用户看不到）⇒ 应转红
    id: 'GT119',
    file: GANTT,
    from: 'label="生产指示号"',
    to: 'label="指示号"'
  },
  {
    // 清单「人工槽」判据被写死 false（人工槽永远不显示）⇒ 应转红
    id: 'GT119',
    file: GANTT,
    from: 'row.manualSlotId != null',
    to: 'false'
  },
  {
    // CSV 不再导出阶段 ⇒ 应转红
    id: 'GT119',
    file: GANTT,
    from: "t.stageCode ?? '',",
    to: "'',"
  },
  {
    // 把内部句柄「路径」当列展示 ⇒ 应转红
    id: 'GT119',
    file: GANTT,
    from: 'label="阶段"',
    to: 'label="路径"'
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
