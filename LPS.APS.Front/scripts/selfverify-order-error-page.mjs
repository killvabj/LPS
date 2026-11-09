/**
 * 订单页「出错不整页跳 /500」探针反向破坏自证（2026-11-02）
 *
 * 背景：5号位 2026-10-31 回执《订单列表性能已修 50 秒到 1 秒》§六-2 ——
 *   「超时改页内错误提示、不整页跳 /500：**我方支持你无条件做**」。
 *   本轮前端落地：请求可声明 `noErrorRedirect` ⇒ 失败不跳页，抛 ApiError 由页面错误位呈现。
 *   新增静态探针 OD20a/OD20b/OD20c（见 scripts/verify-acceptance.mjs，GROUP=order-static）。
 *
 * 用法：node scripts/selfverify-order-error-page.mjs
 *   逐条改坏 ⇒ 对应 OD20x 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-usability.mjs：
 *   ① 变异锚点必须命中代码处
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失
 *   ⑤ 字面量出现多次时要一次全改
 *
 * GROUP=order-static 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const HTTP = 'src/api/aps-v1/http.ts'
const ORDER_API = 'src/api/aps-v1/order.ts'
const ORDER_STORE = 'src/store/modules/aps/order.ts'
const ORDER_VIEW = 'src/views/Aps/Order.vue'

const cases = [
  /* ===== OD20a：开关存在 + 5xx / 网络层两个分支都受守卫 + 大白话提示 ===== */
  {
    // 开关本身没声明 ⇒ 调用方写了也传不进去 ⇒ 应转红
    id: 'OD20a',
    file: HTTP,
    from: '  noErrorRedirect?: boolean',
    to: '  // noErrorRedirect?: boolean'
  },
  {
    // 5xx 分支去掉守卫 ⇒ 仍然整页跳 /500 ⇒ 应转红
    id: 'OD20a',
    file: HTTP,
    from: `      if (status && status >= 500) {
        if (noErrorRedirect) {
          const plain = plainErrorMessage(error, status)
          return Promise.reject(new ApiError(plain.message, plain.code))
        }
        redirectToError(500)`,
    to: `      if (status && status >= 500) {
        redirectToError(500)`
  },
  {
    // 网络层（超时 / 断网）分支去掉守卫 ⇒ 超时照样整页跳 ⇒ 应转红
    id: 'OD20a',
    file: HTTP,
    from: `      if (!status) {
        if (noErrorRedirect) {
          const plain = plainErrorMessage(error)
          return Promise.reject(new ApiError(plain.message, plain.code))
        }
        redirectToError(500)`,
    to: `      if (!status) {
        redirectToError(500)`
  },
  {
    // 提示语退回 axios 英文原文 ⇒ 用户看到「timeout of 30000ms exceeded」⇒ 应转红
    id: 'OD20a',
    file: HTTP,
    from: "请求超时，请稍后重试",
    to: 'timeout of 30000ms exceeded'
  },

  /* ===== OD20b：列表 / 详情 / 汇总三个调用都开开关 ===== */
  {
    // 列表漏开 ⇒ 列表超时仍然整页跳 ⇒ 应转红
    id: 'OD20b',
    file: ORDER_API,
    from: `      params: { ...query },
      // 查不动时**留在页内**（页面已有「加载失败：…」错误条）——
      // 整页跳 /500 会把用户已填的筛选、勾选、页码全丢掉。
      // 5号位 2026-10-31 回执 §六-2 支持前端这么改。
      noErrorRedirect: true`,
    to: '      params: { ...query }'
  },
  {
    // 详情漏开 ⇒ 抽屉里报错时整页跳 ⇒ 应转红
    id: 'OD20b',
    file: ORDER_API,
    from: `      silent: true,
      // 详情失败在抽屉里显示（\`detailError\`），不整页跳 /500
      noErrorRedirect: true`,
    to: '      silent: true'
  },
  {
    // 汇总漏开 ⇒ KPI 聚合超时时整页跳 ⇒ 应转红
    id: 'OD20b',
    file: ORDER_API,
    from: `      silent: true,
      // 聚合失败只让 KPI 整块空态（store 侧处理），不整页跳 /500
      noErrorRedirect: true`,
    to: '      silent: true'
  },

  /* ===== OD20c：页内确实有地方显示这个错误 ===== */
  {
    // 页面错误条被摘掉 ⇒ 开关开了也没地方显示 ⇒ 应转红
    id: 'OD20c',
    file: ORDER_VIEW,
    from: `    <ElAlert
      v-if="listError"`,
    to: `    <ElAlert
      v-if="neverListError"`
  },
  {
    // store 不再把错误写进 listError ⇒ 页面永远空白 ⇒ 应转红
    id: 'OD20c',
    file: ORDER_STORE,
    from: '      handleError(listError, err)',
    to: '      void err'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'order-static' },
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
  writeFileSync(p, c.all ? orig.split(from).join(to) : orig.replace(from, to), 'utf8')
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
    .filter((l) => l.includes('❌') && !mine.includes(l) && !l.includes('失败：') && !l.includes('结果'))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
