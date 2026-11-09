/**
 * APS V1 4号位 — Setup 主数据下拉「远程补拉 + 本地收窄」探针反向破坏自证（2026-10-28）
 *
 * 背景：用户实测「下拉模糊搜索不起作用」。
 * 根因：ElSelect 同时传 filterable + remote + remote-method 时**完全不做本地过滤**
 *      （element-plus useSelect.updateOptions 首行直接 return）；composable 的 search*
 *      又是 merge 补拉（TOP 200 之外的项追加，不替换）⇒ 打字后下拉一条不少。
 * 修法：每个下拉独立 query，展示项按 label/value 收窄（filterLookupOptions），
 *      后端补拉只负责把首屏 200 条之外的数据拉进来。
 *
 * 用法：node scripts/selfverify-setup-lookup-filter.mjs
 *   逐条把判据依赖的源码改坏 ⇒ ST10j / ST10c 必须转红 ⇒ 再还原。
 *
 * 五条纪律（同 selfverify-ruleset-publish.mjs）：
 *   ① 变异锚点必须命中代码处，不能命中注释（探针 stripComments）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失（追加 X 仍是子串 ⇒ 照绿）
 *   ⑤ 字面量出现多次时要一次全改
 *
 * GROUP=setup 需要登录（verifySetupPages 走真后端）⇒ 先取一次 token 用 VERIFY_TOKEN
 * 复用，避免连跑撞 5 次/分钟 的登录频控。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const COMPOSABLE = 'src/views/Aps/composables/useSetupMasterData.ts'
const VIEW_EXACT_DIALOG = 'src/views/Aps/components/SetupExactDialog.vue'
const VIEW_DEFAULT_DIALOG = 'src/views/Aps/components/SetupDefaultDialog.vue'
const VIEW_UNCOVERED = 'src/views/Aps/SetupUncovered.vue'

const cases = [
  /* ===== ST10j：收窄 helper 真的在过滤 ===== */
  {
    id: 'ST10j',
    file: COMPOSABLE,
    from:
      '  return options.filter(\n' +
      '    (o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q)\n' +
      '  )',
    to: '  return options'
  },
  /* ===== ST10j：明确产品转换 Dialog 部门下拉退回全量 ===== */
  {
    id: 'ST10j',
    file: VIEW_EXACT_DIALOG,
    from:
      'const departmentOptions = computed(() =>\n' +
      '  filterLookupOptions(masterData.departments.value, departmentQuery.value)\n' +
      ')',
    to: 'const departmentOptions = masterData.departments'
  },
  /* ===== ST10j：默认换型规则 Dialog 设备下拉退回全量 ===== */
  {
    id: 'ST10j',
    file: VIEW_DEFAULT_DIALOG,
    from: 'return filterLookupOptions(enriched, resourceQuery.value)',
    to: 'return enriched'
  },
  /* ===== ST10j：缺失规则查询页的产品下拉不再把关键词落进 query ===== */
  {
    id: 'ST10j',
    file: VIEW_UNCOVERED,
    from: 'function onSearchMaterial(kw: string): void {\n  materialQuery.value = kw\n',
    to: 'function onSearchMaterial(kw: string): void {\n'
  },
  /* ===== ST10c：后产品下拉脱离远程搜索 ===== */
  {
    id: 'ST10c',
    file: VIEW_EXACT_DIALOG,
    from:
      '          :remote-method="onSearchToMaterial"\n' +
      '          :loading="searching.material || masterData.loading.value"\n' +
      '          style="width: 100%"\n' +
      '        >\n' +
      '          <ElOption\n' +
      '            v-for="opt in toMaterialOptions"',
    to:
      '          :loading="searching.material || masterData.loading.value"\n' +
      '          style="width: 100%"\n' +
      '        >\n' +
      '          <ElOption\n' +
      '            v-for="opt in toMaterialOptions"'
  }
]

function getToken() {
  const r = spawnSync(
    'curl',
    [
      '-sk',
      '-X',
      'POST',
      'https://localhost:7044/api/auth/login',
      '-H',
      'Content-Type: application/json',
      '-d',
      '{"userCode":"admin","password":"Admin@123456"}'
    ],
    { encoding: 'utf8' }
  )
  try {
    return JSON.parse(r.stdout || '{}')?.data?.accessToken ?? ''
  } catch {
    return ''
  }
}

const token = getToken()
if (!token) {
  console.log('未能取得 VERIFY_TOKEN（dev 后端未启动？）——改用探针自己登录')
}

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: {
      ...process.env,
      GROUP: 'setup',
      ...(token ? { VERIFY_TOKEN: token } : {})
    },
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
  console.log(`${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | ${c.file}`)
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
