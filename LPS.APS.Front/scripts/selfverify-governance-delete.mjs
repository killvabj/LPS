/**
 * APS V1 4号位 — 治理三族「删除」探针反向破坏自证（2026-10-26 建；2026-10-30 口径反转重写）
 *
 * 现状（2026-10-30 路 A 恢复）：0号位 批准「最窄删除口径」⇒ 3号位 回执称三个主表 DELETE
 *   路由摘掉 `[NonAction]` 重新注册（版本级保持不开放），dev 上打不存在的 id 回 400「…不存在」
 *   （请求已到 service 层，区别于 405=方法被摘 / 404=路径不在）。
 *   ⇒ 探针 R38-R41 锁「三族删除入口必须存在（含 store 回列表核对、页面入口）」，R44 锁「主表可达」，
 *     R44b 锁「版本级仍不可达」。
 *
 * 用法：node scripts/selfverify-governance-delete.mjs
 *   逐条把判据依赖的源码改坏 ⇒ 对应探针必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * ⚠️ 沿用 selfverify-calendar-resolve.mjs 的五条纪律：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments）
 *   ② 一个探针 id 下可能有多行断言 ⇒ 判红看该 id 的**所有行**
 *   ③ 变异锚点里的 \n 按**文件实际换行符**替换（仓库里 CRLF / LF 混用）
 *   ④ 被搜字面量必须**彻底消失**（改注释名仍为子串 ⇒ 探针照绿）
 *   ⑤ 被搜字面量出现**多次**时要一次全改（否则改一处、另一处还在 ⇒ 探针照绿）
 *
 * ⚠️ 动态探针（R44）的变异对象是**探针脚本自身**。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const API_PS = 'src/api/aps-v1/parameterSet.ts'
const STORE_RULE = 'src/store/modules/aps/rules.ts'
const STORE_PS = 'src/store/modules/aps/parameterSets.ts'
const STORE_SP = 'src/store/modules/aps/strategyProfile.ts'
const PAGE_PS = 'src/views/Aps/ParameterSets.vue'
const PAGE_SP = 'src/views/Aps/StrategyProfiles.vue'
const PAGE_RULE = 'src/views/Aps/Rules.vue'
const PROBE = 'scripts/verify-acceptance.mjs'

const cases = [
  /* ===== R38：api 层必须保留 DELETE ===== */
  {
    // 把参数集 DELETE 方法改名 ⇒ 探针按名找不到 ⇒ R38 必须转红
    id: 'R38',
    group: 'rules',
    file: API_PS,
    from: 'async deleteParameterSet(parameterSetId: number)',
    to: 'async purgeParameterSet(parameterSetId: number)'
  },

  /* ===== R39：store 层必须保留 removeXxx（含回列表核对） ===== */
  {
    // 把 removeRuleSet 改名 ⇒ fnBody 取不到函数体 ⇒ R39 必须转红
    id: 'R39',
    group: 'rules',
    file: STORE_RULE,
    from: 'async function removeRuleSet(ruleSetId: number)',
    to: 'async function dropRuleSet(ruleSetId: number)'
  },
  {
    // 去掉「失败回列表核对真值」的 stillThere 判据 ⇒ R39 必须转红（删完不核对就是假绿）
    id: 'R39',
    group: 'rules',
    file: STORE_RULE,
    from: 'const stillThere = ruleSets.value.some((r) => r.ruleSetId === ruleSetId)',
    to: 'const stillThere = true'
  },

  /* ===== R40：三个页面都必须有删除入口 ===== */
  {
    // 参数集页把 store 调用彻底改名（不能只追加后缀 —— `removeParameterSetX` 仍含原串 ⇒ 假绿，纪律④）
    id: 'R40',
    group: 'rules',
    file: PAGE_PS,
    from: 'paramStore.removeParameterSet(parameterSetId)',
    to: 'paramStore.purgeParameterSet(parameterSetId)'
  },
  {
    // 策略包页把删除处理函数改名（模板与脚本一起改）⇒ R40 必须转红
    id: 'R40',
    group: 'rules',
    file: PAGE_SP,
    from: 'onDeleteProfile',
    to: 'onPurgeProfile',
    all: true
  },

  /* ===== R41：撤掉删除后「停用」出口必须还在 ===== */
  {
    // 参数集页停用开关被抹掉 ⇒ 只能删不能停（现在连删都没了 ⇒ 必须转红）
    // ⚠️ all=true：`editParameterSetForm.isActive` 在页面里出现多次（纪律⑤）
    id: 'R41',
    group: 'rules',
    file: PAGE_PS,
    from: 'editParameterSetForm.isActive',
    to: 'editParameterSetForm.isActiveX',
    all: true
  },
  {
    // 策略包页停用开关被抹掉
    id: 'R41',
    group: 'rules',
    file: PAGE_SP,
    from: 'editProfileForm.isActive',
    to: 'editProfileForm.isActiveX',
    all: true
  },
  {
    // 规则集停用按钮被抹掉
    // ⚠️ 必须彻底改名：追加 X 仍是原子串 ⇒ 探针照绿（纪律④，本轮踩过）
    // ⚠️ all=true：`openDisableDialog` 在页面里出现 2 次（纪律⑤）
    id: 'R41',
    group: 'rules',
    file: PAGE_RULE,
    from: 'openDisableDialog',
    to: 'openRetireDialog',
    all: true
  },

  /* ===== R45-R47：停用行默认折叠 + 开关必须还在（2026-10-30）===== */
  {
    // 开关默认值改成 true ⇒ 默认又显示停用行 ⇒ R45 必须转红
    id: 'R45',
    group: 'rules',
    file: STORE_RULE,
    from: 'const showInactive = ref(false)',
    to: 'const showInactive = ref(true)'
  },
  {
    // 判据改成「只留停用行」⇒ 与探针要求的 `isActive !== false` 不符 ⇒ R46 必须转红
    id: 'R46',
    group: 'rules',
    file: STORE_RULE,
    from: 'if (!showInactive.value) arr = arr.filter((r) => r.isActive !== false)',
    to: 'if (!showInactive.value) arr = arr.filter((r) => r.isActive === false)'
  },
  {
    // 开关文案彻底改掉 ⇒ 页面上不再有「显示已停用」⇒ R47 必须转红
    id: 'R47',
    group: 'rules',
    file: PAGE_RULE,
    from: '显示已停用（{{ inactiveCount }}）',
    to: '显示停用条目（{{ inactiveCount }}）'
  },
  {
    // 策略包页列表回退成直出全量 ⇒ 「防回退」那一条必须转红
    id: 'R47',
    group: 'rules',
    file: PAGE_SP,
    from: 'v-for="p in visibleProfiles"',
    to: 'v-for="p in profiles"'
  },

  /* ===== R44 / R44b：动态（变异对象 = 探针脚本自身） ===== */
  {
    // 主表判据改成「必须 405」⇒ dev 实际 400 ⇒ 探针必须转红
    // （证明它比的是真实状态码，不是「只要不是 200 就算过」的摆设）
    id: 'R44',
    group: 'governance-delete',
    file: PROBE,
    from: "(r.status === 400 || r.status === 404) && (msg.includes('不存在') || msg === ''),",
    to: 'r.status === 405,'
  },
  {
    // 版本级判据改成「必须 200」⇒ dev 实际 405/404 ⇒ R44b 必须转红
    id: 'R44b',
    group: 'governance-delete',
    file: PROBE,
    from:
      '`R44b 动态：DELETE ${t.path} 必须仍不可达（版本级不开放；405/404）`,\n      r.status === 405 || r.status === 404,',
    to: '`R44b 动态：DELETE ${t.path} 必须仍不可达（版本级不开放；405/404）`,\n      r.status === 200,'
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
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR — ${c.from.slice(0, 60)}`)
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
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态') || l.includes(c.id + ' 动态'))
  const red = mine.some((l) => l.includes('❌'))
  const others = out
    .split('\n')
    .filter((l) => l.includes('❌') && !mine.includes(l) && !/^❌\s*失败/.test(l.trim()))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
