/**
 * APS V1 4号位 — 排程域写权限门控「反向破坏自证」脚本
 *
 * 用法：node scripts/selfverify-domain-permission.mjs
 *   逐条把 D05b/c/d/e 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 自证工具自身的坑（与 selfverify-warehouse-ref.mjs 同口径）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针 stripComments 后读不到）。
 *   ② 同 id 多行断言判红看**所有行**（本组每个 id 只有一行，仍按通用写法处理）。
 *   ③ 锚点里的 \n 按**文件实际换行符**替换（CRLF / LF 混用）。
 *   ④ 变异必须让被搜字面量彻底消失（D05d 直接删掉权限码调用，不加后缀）。
 *   ⑤ 同一字面量出现多次要 `all: true`（`:content="writeDeniedReason"` 有两处）。
 *
 * 每条的隔离性：
 *   - D05b 断 canWrite 里的角色码；D05d 断 canWrite 里的权限码 ⇒ 两次变异互不干扰。
 *   - D05e 断 writeDeniedReason 的两种中文原因 ⇒ D05c 改绑定不影响它。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const FILE = 'src/views/Aps/Domain.vue'

const cases = [
  {
    id: 'D05b',
    file: FILE,
    // 把角色码判定换成 true（canWrite 里只剩权限码 ⇒ 按角色码的判据必须转红）
    from:
      'const canWrite = computed(\n  () => apsAuth.roles.includes(\'aps.admin.system\') && apsAuth.has(\'aps.domain.edit\')',
    to: 'const canWrite = computed(\n  () => true && apsAuth.has(\'aps.domain.edit\')'
  },
  {
    id: 'D05d',
    file: FILE,
    // 整段删掉权限码调用（不留子串，避免 includes 型判据照绿）
    from:
      'const canWrite = computed(\n  () => apsAuth.roles.includes(\'aps.admin.system\') && apsAuth.has(\'aps.domain.edit\')',
    to: 'const canWrite = computed(\n  () => apsAuth.roles.includes(\'aps.admin.system\')'
  },
  {
    id: 'D05c',
    file: FILE,
    all: true,
    // 两处 tooltip 都换成写死文案（不再绑定动态原因）
    from: ':content="writeDeniedReason"',
    to: 'content="需要系统管理员角色"'
  },
  {
    id: 'D05e',
    file: FILE,
    // 去掉「是系统管理员但没权限」那一路文案 ⇒ 两态分说判据必须转红
    from: "return '还没有排程域维护权限，请联系管理员开通'",
    to: "return '还没有权限'"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'domain-permission' },
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
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR`)
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
