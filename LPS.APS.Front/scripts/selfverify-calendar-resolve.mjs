/**
 * APS V1 4号位 — 日历线新探针「反向破坏自证」脚本（2026-10-26）
 *
 * 用法：node scripts/selfverify-calendar-resolve.mjs
 *   逐条把 RC32 / RC34 / RC34b / RC34b2 / PWC35a / PWC35b / PWC35c 的判据改坏
 *   ⇒ 对应探针必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * ⚠️ 沿用 selfverify-warehouse-ref.mjs 的五条纪律：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments）
 *   ② 一个探针 id 下可能有多行断言 ⇒ 判红看该 id 的**所有行**
 *   ③ 变异锚点里的 \n 按**文件实际换行符**替换（仓库里 CRLF / LF 混用）
 *   ④ 被搜字面量必须**彻底消失**（改注释名仍为子串 ⇒ 探针照绿）
 *   ⑤ 被搜字面量出现**多次**时要一次全改（否则改一处、另一处还在 ⇒ 探针照绿）
 *
 * ⚠️ 动态探针（RC34b / RC34b2 / PWC35c）的变异对象是**探针脚本自身**（判据写错时必须转红），
 *    静态探针的变异对象是**被测源码**。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const PANEL = 'src/views/Aps/components/ResolvePreviewPanel.vue'
const PAGE = 'src/views/Aps/ResourceCalendar.vue'
const API_RC = 'src/api/aps-v1/resourceCalendar.ts'
const STORE_RC = 'src/store/modules/aps/resourceCalendar.ts'
const API_PWC = 'src/api/aps-v1/productionWorkCalendar.ts'
const PAGE_PWC = 'src/views/Aps/ProductionWorkCalendar.vue'
const PROBE = 'scripts/verify-acceptance.mjs'

const cases = [
  /* ===== RC32：渲染口径搬到 ResolvePreviewPanel 组件后，判据也必须跟着搬 ===== */
  {
    id: 'RC32',
    group: 'r-resource-calendar',
    file: PANEL,
    from: ':data="rows"',
    to: ':data="resolveRows"'
  },
  {
    // 少一级来源标签（Factory 改名 ⇒ 三级来源判定缺一 ⇒ 必须转红）
    id: 'RC32',
    group: 'r-resource-calendar',
    file: PANEL,
    from: "  Factory: { label: '继承自工厂', tag: 'warning' },",
    to: "  Fac: { label: '继承自工厂', tag: 'warning' },"
  },
  {
    // 少一个状态标签（MISSING 改名）
    id: 'RC32',
    group: 'r-resource-calendar',
    file: PANEL,
    from: "  MISSING: {",
    to: "  MISSINGX: {"
  },
  {
    // 页面不再把设备侧预览喂给面板
    id: 'RC32',
    group: 'r-resource-calendar',
    file: PAGE,
    from: ':preview="resolvePreview"',
    to: ':preview="null"'
  },

  /* ===== RC34：人工槽三级合成可视区 + 授权维度 ===== */
  {
    id: 'RC34',
    group: 'r-resource-calendar',
    file: API_RC,
    from: "url: '/api/manual-capacity/resolve-preview'",
    to: "url: '/api/manual-capacity/resolve-previewX'"
  },
  {
    id: 'RC34',
    group: 'r-resource-calendar',
    file: STORE_RC,
    from: 'async function loadManualResolvePreview(',
    to: 'async function loadManualResolvePreviewX('
  },
  {
    // Tab3 不挂人工槽面板（设备侧仍挂 ⇒ 只让 RC34 转红）
    id: 'RC34',
    group: 'r-resource-calendar',
    file: PAGE,
    from: ':preview="manualResolvePreview"',
    to: ':preview="null"'
  },
  {
    // 「授权」列改名 ⇒ 授权加班例外维度不再呈现
    // ⚠️ all=true：面板里「授权加班」出现**两处**（列标签 + 下方说明），
    //    只改一处、另一处还在 ⇒ 探针照绿（纪律⑤）。
    id: 'RC34',
    group: 'r-resource-calendar',
    file: PANEL,
    from: '授权加班',
    to: '加班',
    all: true
  },
  {
    // isAuthorized 判定被抹掉
    id: 'RC34',
    group: 'r-resource-calendar',
    file: PANEL,
    from: 'authorized: w.isAuthorized === true',
    to: 'authorized: false'
  },
  {
    // types 里人工槽 DTO 被删
    id: 'RC34',
    group: 'r-resource-calendar',
    file: 'src/api/aps-v1/types/resourceCalendar.ts',
    from: 'export interface ManualCapacityResolvePreviewDto {',
    to: 'export interface ManualCapacityResolvePreviewDtoX {'
  },

  /* ===== RC34b / RC34b2：动态探针（变异对象 = 探针脚本自身） ===== */
  {
    // 端点不通（空 manualSlotIds ⇒ 后端 400）时探针必须转红
    id: 'RC34b',
    group: 'r-resource-calendar',
    file: PROBE,
    from: "query: { manualSlotIds: '18643', startDate: '2026-10-01', endDate: '2026-10-07' }",
    to: "query: { manualSlotIds: '', startDate: '2026-10-01', endDate: '2026-10-07' }"
  },
  {
    // source 集合判错（大小写）时必须转红
    id: 'RC34b',
    group: 'r-resource-calendar',
    file: PROBE,
    from: "          it.windows.every((w) => ['Factory', 'Department', 'Resource'].includes(w.source))\n      )\n    assert(\n      'RC34b 动态",
    to: "          it.windows.every((w) => ['FACTORY', 'DEPARTMENT', 'RESOURCE'].includes(w.source))\n      )\n    assert(\n      'RC34b 动态"
  },
  {
    // 查不存在的设备 ⇒ 空数组 ⇒ 探针必须转红（证明非空断言不是摆设）
    id: 'RC34b2',
    group: 'r-resource-calendar',
    file: PROBE,
    from: "query: { resourceIds: '11829', startDate: '2026-10-01', endDate: '2026-10-02' }",
    to: "query: { resourceIds: '999999999', startDate: '2026-10-01', endDate: '2026-10-02' }"
  },
  {
    // isAuthorized 判定被抹掉 ⇒ 探针必须转红
    id: 'RC34b2',
    group: 'r-resource-calendar',
    file: PROBE,
    from: 'it.windows.every((w) => typeof w.isAuthorized === \'boolean\')',
    to: "it.windows.every((w) => typeof w.isAuthorized === 'number')"
  },

  /* ===== PWC35：onlyEffective 只看生效行 ===== */
  {
    id: 'PWC35a',
    group: 'pwc',
    file: API_PWC,
    from: 'onlyEffective: query.onlyEffective ?? false',
    to: 'onlyEffective: false'
  },
  {
    id: 'PWC35a',
    group: 'pwc',
    file: 'src/api/aps-v1/types/productionWorkCalendar.ts',
    from: 'onlyEffective?: boolean',
    to: 'onlyEffectiveX?: boolean'
  },
  {
    id: 'PWC35b',
    group: 'pwc',
    file: PAGE_PWC,
    from: 'active-text="只看生效行"',
    to: 'active-text="只看有效行"'
  },
  {
    id: 'PWC35b',
    group: 'pwc',
    file: PAGE_PWC,
    from: 'onlyEffective: filterOnlyEffective.value',
    to: 'onlyEffective: false'
  },
  {
    // 动态判据写反（生效行数必须 ≤ 全量）⇒ 必须转红
    id: 'PWC35c',
    group: 'pwc',
    file: PROBE,
    from: 'effItems.length <= allItems.length &&',
    to: 'effItems.length >= allItems.length &&'
  }
]

/** 登录一次拿 token（dev 登录限流 5/min/IP，连跑十几组必然 429）。
 *  登录体字段 = { userCode, password }（后端 LoginRequestDto，不是 username）。 */
async function loginOnce() {
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'
  const res = await fetch('https://localhost:7044/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userCode: 'admin', password: 'Admin@123456' })
  })
  const text = await res.text()
  let json = null
  try {
    json = JSON.parse(text)
  } catch {
    /* 非 JSON */
  }
  const token = json?.data?.accessToken
  if (!token) {
    console.log(`登录失败：HTTP ${res.status} ${text.slice(0, 200)}`)
    process.exit(1)
  }
  console.log('已登录 admin（token 将复用给全部用例）\n')
  return token
}

function run(group, token) {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: group, VERIFY_TOKEN: token },
    encoding: 'utf8'
  })
  return (r.stdout || '') + (r.stderr || '')
}

let allOk = true
const TOKEN = await loginOnce()
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
    out = run(c.group, TOKEN)
  } finally {
    writeFileSync(p, orig, 'utf8')
  }
  // 该 id 的所有行（动态探针的断言行以「RC34b 动态」/「RC34b2 动态」开头，静态行以「… 静态」结尾）
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态') || l.includes(c.id + ' 动态'))
  const red = mine.some((l) => l.includes('❌'))
  const others = out
    .split('\n')
    .filter((l) => l.includes('❌') && !mine.includes(l))
    .map((l) => l.trim().slice(0, 46))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
