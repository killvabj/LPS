/**
 * APS V1 4号位 — 段位文本字段候选值反向破坏自证（2026-11-04）
 *
 * 背景：用户截图问「订单类型 这里为啥是空值」——
 *   `FIELD_VALUE_SUGGESTIONS[3]` 原本是空数组，下拉点开只有「无数据」；
 *   延期状态 / 客户等级的旧提示值（Delayed/OnTrack、VIP/A/B/C）与冻结词表一个都对不上。
 * 现在按冻结 07 v5.1.16 的值域补齐，并用 RP19 锁住。
 *
 * 用法：node scripts/selfverify-priority-values.mjs
 *   逐条把 RP19 依赖的源码改坏 ⇒ RP19 必须转红 ⇒ 再还原。
 *
 * 纪律：
 *   ① 变异锚点必须命中代码处，不能命中注释（探针 stripComments）
 *   ② 被搜字面量必须彻底消失（改成子串仍会照绿）
 *   ③ 同一字面量出现多次时一次全改（`all: true`）
 *
 * GROUP=rules 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const SRC_EDITOR = 'src/api/aps-v1/prioritySegmentEditor.ts'
const VIEW_EDITOR = 'src/views/Aps/components/PrioritySegmentEditor.vue'

const cases = [
  {
    // 订单类型候选退回空数组（就是用户截图看到的「无数据」）
    id: 'RP19',
    file: SRC_EDITOR,
    from:
      "  3: [\n" +
      "    { value: 'SALES_ORDER', label: '客户订单（SALES_ORDER）' },\n" +
      "    { value: 'PRODUCTION_INSTRUCTION', label: '生产指示（PRODUCTION_INSTRUCTION）' }\n" +
      '  ]',
    to: '  3: []'
  },
  {
    // 客户等级候选混进旧值 KEY_ACCOUNT（2026-11-06 收敛后只认 VIP/GENERAL）
    id: 'RP19',
    file: SRC_EDITOR,
    from: "  2: [\n    { value: 'VIP', label: 'VIP' },",
    to: "  2: [\n    { value: 'KEY_ACCOUNT', label: 'KEY_ACCOUNT' },\n    { value: 'VIP', label: 'VIP' },"
  },
  {
    // 客户等级用回旧错值 A（2026-11-06 收敛后只认 VIP/GENERAL）
    id: 'RP19',
    file: SRC_EDITOR,
    from: "    { value: 'VIP', label: 'VIP' },",
    to: "    { value: 'A', label: 'A' },"
  },
  {
    // 客户区分用自造短码 JP（后端值域是 JAPAN）
    id: 'RP19',
    file: SRC_EDITOR,
    from: "    { value: 'JAPAN', label: '日本（JAPAN）' },",
    to: "    { value: 'JP', label: '日本（JP）' },"
  },
  {
    // 模板只显示英文码、不给中文标签
    id: 'RP19',
    file: VIEW_EDITOR,
    from: ':label="v.label"',
    to: ':label="v.value"',
    all: true
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'rules' },
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
  writeFileSync(p, c.all ? orig.replaceAll(from, to) : orig.replace(from, to), 'utf8')
  let out = ''
  try {
    out = run()
  } finally {
    writeFileSync(p, orig, 'utf8')
  }
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态'))
  const red = mine.some((l) => l.includes('❌'))
  if (!mine.length) {
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
