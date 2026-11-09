/**
 * APS V1 4号位 — 审核 217fa2 前端可自改项 探针反向破坏自证（2026-11-07）
 *
 * 背景：0号位《APS_V1_Front_UI_Audit_217fa2_v1_1_FULL.md》里 4 项属「前端可直接修复」：
 *   B-01 甘特任务清单列序重排 + 补「计划数量/计划加工量」（CSV 早有、表格漏显示）
 *   B-02 甘特 TaskShare 三态（原判据 `length > 1` 把「恰好 1 条」误判成缺数据）
 *   B-03 任务详情提示里硬编码的「默认 95%」良率已撤（权威口径待 1/2/5 确认）
 *   C-01 订单列表补「APS 预计完成时间」主列（原先只在详情抽屉里有）
 *   D-02 候选影响摘要「新增延期」→「新增延期任务」（后端 newDelayCount = Task 数）
 *
 * 用法：node scripts/selfverify-audit-ui.mjs
 *   逐条改坏 ⇒ 对应 AU 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-tab.mjs：
 *   ① 变异锚点必须命中代码处
 *   ② 锚点里的 \n 按文件实际换行符替换
 *   ③ 被搜字面量必须彻底消失
 *   ④ 字面量出现多次时要一次全改
 *
 * GROUP=audit-ui 是纯静态组（不登录、后端没起也能跑），故自证不受 dev 登录频控影响。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const ORDER = 'src/views/Aps/Order.vue'
const CAND = 'src/views/Aps/Candidate.vue'

const cases = [
  /* ===== AU01：任务清单列序 ===== */
  {
    // 把「任务号」列复制到表头最前 ⇒ 列序不再「主业务列在前、追溯键在尾」⇒ 应转红
    id: 'AU01',
    file: GANTT,
    from: `        <ElTable :data="taskListPaged" size="small" border max-height="520" style="width: 100%">
`,
    to: `        <ElTable :data="taskListPaged" size="small" border max-height="520" style="width: 100%">
          <ElTableColumn prop="taskNo" label="任务号" min-width="150" show-overflow-tooltip />
`
  },

  /* ===== AU02：计划数量/计划加工量两列 + CSV 头/值条数一致 ===== */
  {
    // CSV 表头删掉「单位」⇒ 表头 23 列 vs 取值 24 条 ⇒ 导出整列错位 ⇒ 应转红
    id: 'AU02',
    file: GANTT,
    from: `  '计划数量',
  '单位',
`,
    to: `  '计划数量',
`
  },
  {
    // 表格里把「计划数量」列改名 ⇒ 两列不齐（数量补齐这条被回退）⇒ 应转红
    id: 'AU02',
    file: GANTT,
    from: `<ElTableColumn label="计划数量" width="120" align="right">`,
    to: `<ElTableColumn label="数量" width="120" align="right">`
  },

  /* ===== AU03：TaskShare 三态 ===== */
  {
    // 判据退回 `> 1` ⇒ 恰好 1 条份额又被归到「未提供」⇒ 应转红
    id: 'AU03',
    file: GANTT,
    from: `selectedTask.taskShare.length > 0`,
    to: `selectedTask.taskShare.length > 1`
  },
  {
    // 1 条的文案被改掉 ⇒ 两态文案不齐 ⇒ 应转红
    // ⚠️ 锚点必须咬到**代码**那一句：同一句话也写在上面那段 JSDoc 注释里，
    //    只替换字面量会改到注释 ⇒ 探针读的是剥注释后的代码 ⇒ 假绿。
    id: 'AU03',
    file: GANTT,
    from: `  if (n <= 1) return '订单份额 — 本任务由 1 个订单承接'`,
    to: `  if (n <= 1) return '订单份额 — 本任务由一个订单承接'`
  },

  /* ===== AU04：硬编码「默认 95%」 ===== */
  {
    // 提示里把「默认 95%」写回去 ⇒ 前端又拿未确认的良率当排产事实 ⇒ 应转红
    id: 'AU04',
    file: GANTT,
    from: `:content="\`计划加工 = 净需求 ÷ 良率；两者不可混用\`"`,
    to: `:content="\`计划加工 = 净需求 ÷ 良率（默认 95%）；两者不可混用\`"`
  },

  /* ===== AU05：订单列表「APS 预计完成时间」列 ===== */
  {
    // 列表列被删（退回只在详情抽屉里）⇒ 应转红
    id: 'AU05',
    file: ORDER,
    from: `        <ElTableColumn label="APS 预计完成时间" width="150">
          <template #default="{ row }">
            <span v-if="row.plannedCompletionTime">
              {{ formatUtcDateTime(row.plannedCompletionTime) }}
            </span>
            <span v-else class="muted" title="该订单在当前版本没有排产任务">未排产</span>
          </template>
        </ElTableColumn>
`,
    to: ''
  },

  /* ===== AU06：候选「新增延期任务」标签 ===== */
  {
    // 标签退回「新增延期」⇒ 又和「受影响订单数」混成订单数 ⇒ 应转红
    id: 'AU06',
    file: CAND,
    from: `>新增延期任务</span`,
    to: `>新增延期</span`
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'audit-ui' },
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
