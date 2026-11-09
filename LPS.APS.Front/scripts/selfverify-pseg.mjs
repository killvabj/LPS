/**
 * APS V1 4号位 — 需求优先级「轮 ⊃ 段」分组「反向破坏自证」（2026-11-09）
 *
 * 用法：node scripts/selfverify-pseg.mjs
 *   逐条把 PSEG01-PSEG08 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *
 * 纪律同 selfverify-priority-fields.mjs：
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

const EDITOR = 'src/api/aps-v1/prioritySegmentEditor.ts'
const VIEW_EDITOR = 'src/views/Aps/components/PrioritySegmentEditor.vue'

const cases = [
  {
    // 模板不再按轮次分组渲染（改回扁平列表）⇒ PSEG01 转红
    id: 'PSEG01',
    file: VIEW_EDITOR,
    from: '<div v-for="g in groups" :key="groupKey(g.round)" class="round-group">',
    to: '<div v-for="g in flatGroups" :key="groupKey(g.round)" class="round-group">'
  },
  {
    // computed 不再走分组纯函数（退化成别的取数）⇒ PSEG01 转红
    id: 'PSEG01',
    file: VIEW_EDITOR,
    from: 'const groups = computed(() => groupSegmentsByRound(rows.value))',
    to: 'const groups = computed(() => groupSegmentsByRoundLegacy(rows.value))'
  },
  {
    // 顶部说明去掉「三轮不混池」⇒ PSEG02 转红
    id: 'PSEG02',
    file: VIEW_EDITOR,
    from: '不同轮次不混池，供给池按轮次顺序消耗',
    to: '所有轮次统一排，供给池一起消耗'
  },
  {
    // 空轮次提示删掉（该轮又变隐形）⇒ PSEG03 转红
    id: 'PSEG03',
    file: VIEW_EDITOR,
    from: '本轮暂无段位。若有需求落到本轮，将按需求键兜底排序。',
    to: '本轮暂无段位。'
  },
  {
    // 组头不再显示本轮段数 ⇒ PSEG03 转红
    id: 'PSEG03',
    file: VIEW_EDITOR,
    from: '<ElTag size="small" type="info" effect="plain">本轮 {{ g.segments.length }} 段</ElTag>',
    to: '<ElTag size="small" type="info" effect="plain">轮次组</ElTag>'
  },
  {
    // 后端没给轮次时不再归第 1 轮（回到「看不出归属」的未声明态）⇒ PSEG04 转红
    id: 'PSEG04',
    file: EDITOR,
    from: 'competitionRound: s.competitionRound ?? 1,',
    to: 'competitionRound: s.competitionRound ?? undefined,'
  },
  {
    // 轮次短名少一项（下拉只有两轮）⇒ PSEG04 转红
    id: 'PSEG04',
    file: EDITOR,
    from: "  { value: 3, label: '第 3 轮' }\n]",
    to: ']'
  },
  {
    // 段卡「归入」下拉不再用短名常量 ⇒ PSEG04 转红
    id: 'PSEG04',
    file: VIEW_EDITOR,
    from: 'v-for="r in COMPETITION_ROUND_SHORT"',
    to: 'v-for="r in COMPETITION_ROUND_LONG"'
  },
  {
    // 序列化键名写错（后端 PrioritySegment 带 Disallow ⇒ 整块反序列化失败）⇒ PSEG05 转红
    id: 'PSEG05',
    file: EDITOR,
    from: 'SegmentOrder: s.orderInSegment,',
    to: 'SegmentOrder: Number(s.orderInSegment),'
  },
  {
    // 稳定排序键整个不写了（D 类 V1 固定值丢失）⇒ PSEG05 转红
    id: 'PSEG05',
    file: EDITOR,
    from: '      StableTieBreakFields: [FIXED_TIE_BREAK_FIELD]\n',
    to: ''
  },
  {
    // 上移/下移不再限制同轮（段序跨轮交错，破坏轮内先后）⇒ PSEG06 转红
    id: 'PSEG06',
    file: VIEW_EDITOR,
    from: 'while (j >= 0 && j < list.length && !sameRound(list[j], seg)) j += delta',
    to: 'while (j >= 0 && j < list.length) j += delta',
    all: true
  },
  {
    // 改归属轮次后仍留在原位（跨轮交错）⇒ PSEG07 转红
    id: 'PSEG07',
    file: VIEW_EDITOR,
    from: 'list.splice(last + 1, 0, seg)',
    to: 'list.splice(i, 0, seg)',
    all: true
  },
  {
    // 三轮归属说明少一条 ⇒ PSEG08 转红
    id: 'PSEG08',
    file: EDITOR,
    from: "  2: '与既有上层履约链相连、且有真实 SH 身份的需求',\n",
    to: ''
  },
  {
    // 组头不再展示归属说明 ⇒ PSEG08 转红
    id: 'PSEG08',
    file: EDITOR,
    from: "scope: COMPETITION_ROUND_SCOPE[r.value] ?? ''",
    to: "scope: ''"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'pseg' },
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
