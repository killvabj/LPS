/**
 * APS V1 4号位 — 甘特页「甘特图 / 任务清单」Tab 改造 探针反向破坏自证（2026-10-27）
 *
 * 背景：用户诉求「甘特页要整个的甘特图，任务清单别占版面」⇒ 任务清单从甘特下方并排卡片
 *   改成同页 Tab（甘特默认、占满整屏；清单切过去才出现），数据仍是同一份 gantt 整包
 *   （后端只有 /api/schedule/gantt/{id}，没有独立任务端点 ⇒ 拆独立页要再拉一次）。
 *
 * 用法：node scripts/selfverify-gantt-tab.mjs
 *   逐条改坏 ⇒ 对应 GT 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 五条纪律同 selfverify-parameter-inert.mjs：
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
const ADJUST_DLG = 'src/views/Aps/components/GanttAdjustDialog.vue'

const cases = [
  /* ===== GT19：清单与甘特同源 + 列齐全 ===== */
  {
    // 清单数据源被改成空数组（列表永远是空）⇒ 违反「同源」，应转红
    id: 'GT19',
    file: GANTT,
    from: 'ganttData.value?.tasks',
    to: '[]',
    all: true
  },
  {
    // 删掉「订单号」列（整段 ElTableColumn 三行）⇒ 冻结要求的列不齐，应转红
    // ⚠️ 必须整段删：只删首行的话 `#default="{ row }"` 那行还留着 row.orderNo ⇒ 探针假绿
    id: 'GT19',
    file: GANTT,
    from: `          <ElTableColumn label="订单号" min-width="140" show-overflow-tooltip>
            <template #default="{ row }">{{ row.orderNo ?? '—' }}</template>
          </ElTableColumn>
`,
    to: ''
  },

  /* ===== GT20：清单改成同页 Tab，不再与甘特并排 ===== */
  {
    // 清单不再受 Tab 控制 ⇒ 又变回与甘特同时占版面，应转红
    id: 'GT20',
    file: GANTT,
    from: `v-show="viewTab === 'tasks'" class="task-list-pane"`,
    to: 'class="task-list-pane"'
  },
  {
    // 旧的并排卡片名加回来 ⇒ 出现「上下并排」回退形态，应转红
    id: 'GT20',
    file: GANTT,
    from: 'class="task-list-pane"',
    to: 'class="task-list-card"'
  },

  /* ===== GT21：默认甘特 + 切回重算尺寸 ===== */
  {
    // 默认视图改成清单 ⇒ 用户进页面先看到清单而不是整屏甘特，应转红
    // 2026-11-02：viewTab 多了第三档 'load'（资源负荷视图）⇒ 锚点跟着变
    id: 'GT21',
    file: GANTT,
    from: "const viewTab = ref<'gantt' | 'load' | 'tasks'>('gantt')",
    to: "const viewTab = ref<'gantt' | 'load' | 'tasks'>('tasks')"
  },
  {
    // 去掉切回甘特的 resize 补偿 ⇒ 甘特会缩成 0 宽（空白图），应转红
    id: 'GT21',
    file: GANTT,
    from: "  if (v === 'gantt') nextTick(() => window.dispatchEvent(new Event('resize')))\n",
    to: ''
  },

  /* ===== GT22：提示与 Tab 同一行 ===== */
  {
    // 提示退回独立 ElAlert 块（不与 Tab 同行）⇒ 应转红
    id: 'GT22',
    file: GANTT,
    from: '      <span v-if="error" class="view-bar-note is-error"',
    to: '      <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />\n      <span v-if="error" class="view-bar-note is-error"'
  },
  {
    // 提示类名改掉（不再挂在 view-bar 上）⇒ 应转红
    // 2026-10-31：页面里有两个 class="view-bar-note"（空态 / 已加载提示），只改第一个改不红 ⇒ 全改
    id: 'GT22',
    file: GANTT,
    from: 'class="view-bar-note"',
    to: 'class="view-bar-hint"',
    all: true
  },

  /* ===== GT23：软目标时间默认预填 ===== */
  {
    // 去掉「软目标时间默认取当前计划开始时间」的预填 ⇒ 工具栏入口提交必被拦，应转红
    // 2026-10-31：预填改到构造行时做（表格分页后仍逐行带 targetTime）
    id: 'GT23',
    file: ADJUST_DLG,
    from: "          : t.plannedStartTime || ''",
    to: "          : ''"
  },

  /* ===== GT24：真实 Domain 取色不再灰回退 ===== */
  {
    // 条形取色改回「色表查不到就灰回退」⇒ 真实域一律灰，应转红
    id: 'GT24',
    file: GANTT,
    from: '    return domainColor(task.domainKey)',
    to: '    return DOMAIN_COLORS[task.domainKey] ?? \'#94a3b8\''
  },
  {
    // 工具栏域标签取色改回灰回退 ⇒ 应转红
    id: 'GT24',
    file: GANTT,
    from: '            :style="{ color: domainColor(dk) }"',
    to: '            :style="{ color: DOMAIN_COLORS[dk] ?? \'#94a3b8\' }"'
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
