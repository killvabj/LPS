/**
 * APS V1 4号位 — 锚点资源编码落点「反向破坏自证」脚本
 *
 * 用法：node scripts/selfverify-anchor-resource.mjs
 *   逐条把 A01-A08 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 两条自证工具自身的坑（本脚本已规避，别的自证脚本也照此写）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments，
 *      改注释等于没改 ⇒ 探针仍绿 ⇒ 误判成「探针不敏感」）。
 *   ② 一个探针 id 下可能有多行断言，判红要看该 id 的**所有行**（任一行 ❌ 即算红）。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const cases = [
  {
    id: 'A01',
    file: 'src/api/aps-v1/anchorResource.ts',
    from: "ANCHOR_RESOURCE_CODE_KEY = 'AnchorResourceCode'",
    to: "ANCHOR_RESOURCE_CODE_KEY = 'AnchorResourceCodeX'"
  },
  {
    id: 'A02',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'ensureAnchorResourceCodeKey(blocks)',
    to: 'blocks'
  },
  {
    id: 'A03',
    file: 'src/api/aps-v1/anchorResource.ts',
    from: 'if (Object.keys(ss).length === 0) return',
    to: '// 空块守卫被删掉'
  },
  {
    id: 'A04',
    file: 'src/views/Aps/ParameterSets.vue',
    from: '<ElInput\n                      :model-value="anchorResourceCode"',
    to: '<ElSelect\n                      :model-value="anchorResourceCode"'
  },
  {
    id: 'A04',
    file: 'src/views/Aps/ParameterSets.vue',
    from: '<div class="anchor-field-label">锚点资源编码</div>',
    to: '<div class="anchor-field-label">AnchorResourceCode</div>'
  },
  {
    id: 'A05',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'if (isAnchorResourceCodeRow(row.block, row.path)) return',
    to: '// 剔行被删掉'
  },
  {
    id: 'A06',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'buffer.value.dirty.solverStrategy = true',
    to: '// 置脏被删掉'
  },
  {
    id: 'A07',
    file: 'src/api/aps-v1/anchorResource.ts',
    from: "solverStrategy[ANCHOR_RESOURCE_CODE_KEY] = String(code ?? '').trim()",
    to: "solverStrategy[ANCHOR_RESOURCE_CODE_KEY] = String(code ?? '')"
  },
  {
    id: 'A08',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'function setAnchorResourceCode(code: string): void {',
    to: "function setAnchorResourceCode(code: string): void {\n    throw new Error('x')"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-anchor' },
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
