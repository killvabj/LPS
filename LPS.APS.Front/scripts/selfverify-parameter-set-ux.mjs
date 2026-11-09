/**
 * APS V1 4号位 — 参数集参数表可读性（P28-P34）反向破坏自证脚本
 *
 * 用法：node scripts/selfverify-parameter-set-ux.mjs
 *   逐条把 P28-P34 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 自证自身的坑（本脚本已规避）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码，改注释等于没改）。
 *   ② 同一段代码可能出现在多处：`label: def?.label` 在 flattenBlocks 里有两处
 *      （数组分支 / primitive 分支），只改一处另一处仍绿 ⇒ 用多行锚点 + 唯一形态。
 *   ③ 一个探针 id 下有多行断言，判红只看该 id 的**所有行**（任一行 ❌ 即红）。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const VIEW = 'src/views/Aps/ParameterSets.vue'
const DRAFT = 'src/api/aps-v1/draftBuffer.ts'

const cases = [
  // P28 中文参数名
  {
    id: 'P28',
    file: VIEW,
    from: '<span class="param-label">{{ row.label ?? row.path.join(\'.\') }}</span>',
    to: '<span class="param-label">{{ row.path.join(\'.\') }}</span>'
  },
  {
    id: 'P28',
    file: VIEW,
    from: '<code v-if="row.label" class="code-tag param-key">',
    to: '<code v-if="false" class="code-tag param-key">'
  },
  // P29 中文类型名
  {
    id: 'P29',
    file: VIEW,
    from: "BOOLEAN: '是 / 否',",
    to: "BOOLEAN: 'BOOLEAN',"
  },
  {
    id: 'P29',
    file: VIEW,
    from: 'TYPE_LABELS[row.type]',
    to: 'row.type'
  },
  // P30 删除确认框中文名
  {
    id: 'P30',
    file: VIEW,
    from: '将删除参数「${row.label ?? row.path.join(\'.\')}」',
    to: '将删除参数「${row.path.join(\'.\')}」'
  },
  // P31 步长取目录
  {
    id: 'P31',
    file: VIEW,
    from: ':step="row.step ?? 1"',
    to: ':step="1"'
  },
  // P32 文本清单可编辑
  {
    id: 'P32',
    file: DRAFT,
    from: "const isTextList = def?.kind === 'TEXT_LIST' && value.every((v) => typeof v === 'string')",
    to: 'const isTextList = false'
  },
  {
    id: 'P32',
    file: VIEW,
    from: "v-else-if=\"row.type === 'TEXT_LIST'\"",
    to: "v-else-if=\"row.type === 'STRING'\""
  },
  // P33 目录边界并入
  {
    id: 'P33',
    file: DRAFT,
    from: 'lookupParameterLimits(block, path) ?? { min: def?.min, max: def?.max }',
    to: 'lookupParameterLimits(block, path)'
  },
  // P34 专属编辑器空块的引导行（四条判据各打一次）
  {
    id: 'P34',
    file: VIEW,
    from: 'v-if="rowsGrouped[blockKey].length === 0 && hasWarehouseEditor(blockKey)"',
    to: 'v-if="false"'
  },
  {
    id: 'P34',
    file: VIEW,
    from: 'addFieldExamples(blockKey)',
    to: "''"
  },
  {
    id: 'P34',
    file: VIEW,
    from: '  return listBlockFields(blockKey)\n    .slice(0, 2)\n    .map((f) => f.label)\n    .join(\'、\')',
    to: "  return '某参数'"
  },
  {
    id: 'P34',
    file: VIEW,
    from: '                    <ElButton\n                      plain\n                      type="primary"',
    to: '                    <ElButton\n                      link\n                      type="primary"'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-static' },
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
