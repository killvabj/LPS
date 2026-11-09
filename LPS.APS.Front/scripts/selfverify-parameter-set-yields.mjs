/**
 * APS V1 4号位 — 采购良率专属编辑器 反向破坏自证脚本
 *
 * 用法：node scripts/selfverify-parameter-set-yields.mjs
 *   逐条把 PY01-PY08 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 三条自证工具自身的坑（本脚本已规避，别的自证脚本也照此写）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments，
 *      改注释等于没改 ⇒ 探针仍绿 ⇒ 误判成「探针不敏感」）。
 *   ② 一个探针 id 下可能有多行断言，判红要看该 id 的**所有行**（任一行 ❌ 即算红）。
 *   ③ 变异锚点要**唯一**：字符串 replace 命中多处就会改错那一处 ⇒ 探针仍绿 ⇒ 误判。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const YIELDS = 'src/api/aps-v1/parameterSetYields.ts'
const STORE = 'src/store/modules/aps/parameterSets.ts'
const VIEW = 'src/views/Aps/ParameterSets.vue'
const EDITOR = 'src/views/Aps/components/PlanningYieldEditor.vue'
const MATAPI = 'src/api/aps-v1/materialLookup.ts'

const cases = [
  {
    id: 'PY01',
    file: YIELDS,
    from: "PLANNING_YIELDS_PATH = ['PlanningYields']",
    to: "PLANNING_YIELDS_PATH = ['PlanningYieldsX']"
  },
  {
    id: 'PY01',
    file: YIELDS,
    from: 'YieldPercent?: number',
    to: 'YieldPercentX?: number'
  },
  {
    id: 'PY02',
    file: STORE,
    from: 'if (isPlanningYieldRow(row.block, row.path)) return',
    to: '// 剔行被删掉'
  },
  {
    id: 'PY03',
    file: STORE,
    from: 'writePlanningYields(buffer.value.workingBlocks, items)',
    to: '// 写入被删掉'
  },
  {
    id: 'PY03',
    file: STORE,
    from: '    writePlanningYields(buffer.value.workingBlocks, items)\n    buffer.value.dirty.procurement = true',
    to: '    writePlanningYields(buffer.value.workingBlocks, items)\n    // 置脏被删掉'
  },
  {
    id: 'PY03',
    file: STORE,
    from: 'const planningYields = computed<PlanningYieldItem[]>(() =>',
    to: 'const planningYieldsX = computed<PlanningYieldItem[]>(() =>'
  },
  {
    id: 'PY04',
    file: VIEW,
    from: "import PlanningYieldEditor from './components/PlanningYieldEditor.vue'",
    to: "import PlanningYieldEditor from './components/PlanningYieldEditorX.vue'"
  },
  {
    id: 'PY04',
    file: VIEW,
    from: "<template v-if=\"blockKey === 'procurement'\">",
    to: "<template v-if=\"blockKey === 'supply'\">"
  },
  {
    id: 'PY05',
    file: MATAPI,
    from: "        url: '/api/governance/lookups/materials',\n        params: { activeOnly: true }",
    to: "        url: '/api/governance/lookups/materialsX',\n        params: { activeOnly: true }"
  },
  {
    id: 'PY05',
    file: EDITOR,
    from: "opts.push({ code: cur, label: `${cur}（物料字典里没有）`, known: false })",
    to: "opts.push({ code: 'MAT001', label: 'MAT001', known: false })"
  },
  {
    id: 'PY06',
    file: EDITOR,
    from: ':max="100"',
    to: ':max="1000"'
  },
  {
    id: 'PY06',
    file: EDITOR,
    from: 'const safe = Number.isFinite(p) && p > 0 && p <= 100 ? p : 100',
    to: 'const safe = Number.isFinite(p) && p > 0 ? p : 100'
  },
  {
    id: 'PY07',
    file: YIELDS,
    from: "const material = String(item?.MaterialId ?? '').trim().toLowerCase()",
    to: "const material = String(item?.MaterialId ?? '').trim()"
  },
  {
    id: 'PY07',
    file: YIELDS,
    from: 'seen.get(key)',
    to: 'seen.get(keyX)'
  },
  {
    id: 'PY08',
    file: EDITOR,
    from: 'departmentLookupApi.listDepartments()',
    to: "stageLeadTimeParamsApi.list('stage-lead-time-params')"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-yields' },
    encoding: 'utf8'
  })
  return (r.stdout || '') + (r.stderr || '')
}

let allOk = true
for (const c of cases) {
  const p = join(ROOT, c.file)
  const orig = readFileSync(p, 'utf8')
  // 变异锚点里的 \n 按**文件实际换行符**替换（仓库里 CRLF / LF 混用）
  const eol = orig.includes('\r\n') ? '\r\n' : '\n'
  const from = c.from.split('\n').join(eol)
  const to = c.to.split('\n').join(eol)
  if (!orig.includes(from)) {
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR`)
    allOk = false
    continue
  }
  writeFileSync(p, orig.replace(from, to), 'utf8')
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
    .filter((l) => l.includes('❌') && !l.includes(c.id + ' 静态'))
    .map((l) => l.trim().slice(0, 46))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
