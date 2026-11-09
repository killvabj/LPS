/**
 * APS V1 4号位 — 需求优先级新增字段维护面「反向破坏自证」（2026-11-07）
 *
 * 用法：node scripts/selfverify-priority-fields.mjs
 *   逐条把 RP20-RP23 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *
 * 覆盖：
 *   RP20 展示字典（DemandField 0~16 / ConditionOperator 含 Contains）
 *   RP21 编辑器开放 12 字段，1/12/0/4/5 保持关闭
 *   RP22 Contains 只给物料编码 + 数值控件类型
 *   RP23 参数集锁定块成熟度两项 + DNF 条件组负断言
 *   RP24 出荷指示入物流日相对天数/具体日期双模式（3号位 Q4 回执）
 *
 * 纪律同 selfverify-warehouse-ref.mjs：
 *   ① 变异锚点必须命中代码处，不能命中注释（探针读源码时会 stripComments）
 *   ② 变异必须让被搜字面量彻底消失（不能改成仍被 includes/正则命中的子串）
 *   ③ 同一字面量出现多次时一次全改（all: true）
 *   ④ 一个探针 id 下可能有多行断言，任一行 ❌ 即算红
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const DISPLAY = 'src/api/aps-v1/prioritySegment.ts'
const EDITOR = 'src/api/aps-v1/prioritySegmentEditor.ts'
const VIEW_EDITOR = 'src/views/Aps/components/PrioritySegmentEditor.vue'
const BLOCK_FIELDS = 'src/api/aps-v1/parameterBlockFields.ts'

const cases = [
  {
    // 展示字典的「包含」序号写成 8（后端是 7）⇒ RP20 转红
    id: 'RP20',
    file: DISPLAY,
    from: "  { value: 7, label: '包含（前缀匹配）' }",
    to: "  { value: 8, label: '包含（前缀匹配）' }"
  },
  {
    // 把已挂起的「需求成熟度(12)」加进编辑器 ⇒ RP21/R27 转红
    id: 'RP21',
    file: EDITOR,
    from: "  { value: 13, label: '库存消耗日（天）', kind: 'NUMBER' },",
    to: "  { value: 12, label: '需求成熟度', kind: 'TEXT' },\n  { value: 13, label: '库存消耗日（天）', kind: 'NUMBER' },"
  },
  {
    // 把已归参数集的「延期状态(1)」加回编辑器 ⇒ RP21/R27 转红（3号位 Q1~Q4 回执 §二）
    id: 'RP21',
    file: EDITOR,
    from: "  { value: 2, label: '客户等级', kind: 'TEXT' },",
    to: "  { value: 1, label: '延期状态', kind: 'TEXT' },\n  { value: 2, label: '客户等级', kind: 'TEXT' },"
  },
  {
    // 物料编码不再允许「包含」⇒ RP22 转红
    id: 'RP22',
    file: EDITOR,
    from: "  { value: 15, label: '物料编码', kind: 'TEXT', allowContains: true },",
    to: "  { value: 15, label: '物料编码', kind: 'TEXT' },"
  },
  {
    // 库存消耗日退回日期框（真实语义是小数天数）⇒ RP22 转红
    id: 'RP22',
    file: EDITOR,
    from: "  { value: 13, label: '库存消耗日（天）', kind: 'NUMBER' },",
    to: "  { value: 13, label: '库存消耗日（天）', kind: 'DATE' },"
  },
  {
    // 出荷指示入物流日不再标记相对天数 ⇒ RP24 转红
    id: 'RP24',
    file: EDITOR,
    from: "  { value: 9, label: '出荷指示入物流日', kind: 'DATE', allowRelativeOffset: true },",
    to: "  { value: 9, label: '出荷指示入物流日', kind: 'DATE' },"
  },
  {
    // 相对天数扩到「等于」等非范围算子（后端只认 LT/LTE/GT/GTE）⇒ RP24 转红
    id: 'RP24',
    file: EDITOR,
    from: 'return isRelativeOffsetField(field) && [2, 3, 4, 5].includes(operator)',
    to: 'return isRelativeOffsetField(field) && [0, 2, 3, 4, 5].includes(operator)'
  },
  {
    // 双模式控件退回单一名称（整数偏移入口失锁）⇒ RP24 转红
    id: 'RP24',
    file: VIEW_EDITOR,
    from: '<ElRadioButton value="offset">按天数偏移</ElRadioButton>',
    to: '<ElRadioButton value="offset">按天偏移</ElRadioButton>'
  },
  {
    // 参数集目录里把成熟度开关路径改名 ⇒ RP23 转红
    id: 'RP23',
    file: BLOCK_FIELDS,
    from: "    path: 'Trigger.ProtectMaturityStatus',",
    to: "    path: 'Trigger.ProtectMaturityStatusX',"
  },
  {
    // 提前把一个 DNF 条件组键写进编辑器代码 ⇒ RP23 负断言转红
    id: 'RP23',
    file: EDITOR,
    from: "  { value: 16, label: '数量', kind: 'NUMBER' }",
    to: "  { value: 16, label: '数量', kind: 'NUMBER' },\n  { value: 99, label: 'DNF', kind: 'TEXT', group: 'TriggerConditionGroups' }"
  },
  /* R48（2026-11-07；0号位工作台第 56 条 D 类 V1 固定） */
  {
    // 稳定排序键常量改成别的值 ⇒ R48 转红
    id: 'R48',
    file: EDITOR,
    from: "export const FIXED_TIE_BREAK_FIELD = 'DEMANDKEY'",
    to: "export const FIXED_TIE_BREAK_FIELD = 'DEMANDKEYX'"
  },
  {
    // 组件读入不再归一为 DEMANDKEY（存量空值会漏出去）⇒ R48 转红
    id: 'R48',
    file: VIEW_EDITOR,
    from: 'stableTieBreakFields: [FIXED_TIE_BREAK_FIELD]',
    to: 'stableTieBreakFields: []',
    all: true
  },
  {
    // 序列化不再写 StableTieBreakFields（D 类固定值丢失）⇒ R48 转红
    id: 'R48',
    file: EDITOR,
    from: 'StableTieBreakFields: [FIXED_TIE_BREAK_FIELD]',
    to: 'StableTieBreakFields: []'
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
