#!/usr/bin/env node
/**
 * 异常与原因解释页实看（2026-11-05）
 *
 * 为什么要有这个脚本：静态探针只能证明代码写了，**证明不了浏览器里那几项真的显示成用户看到的样子**。
 *   本轮修的三处缺陷里，两处只有**浏览器里**才看得出来：
 *     ① KPI 百分比分母错 ⇒ 页面显示「错误 538% / 警告 516%」
 *     ② 列表被后端截断成 50 条 ERROR ⇒ 页面**看不见**任何 INFO 事实、看不到 DOMAIN 分组
 *     ③ 事实字段名对不上 ⇒ 页面「对象」格恒空、时间恒显示 `Invalid Date`
 *
 * 它做什么（走真实登录表单 → 真实页面 → 读真实 DOM）：
 *   ① KPI 三张卡的百分比都 ≤ 100%，且「解释事实合计」= 后端权威总数
 *   ② 事实面板计数行显示「共 N 条 · 已显示 M 条」，且 M = N（dev 版本 999333 全量 527 条）
 *   ③ 分组里同时出现「订单」和「分域」两类（此前只有 ORDER 一组）
 *   ④ 严重度标签里同时出现「错误」和「提示」（此前只有 ERROR）
 *   ⑤ 页面里**不得**出现 `Invalid Date`
 *   ⑥ 「对象」格不得为空（应形如「需求 999333_35」）
 *   ⑦ 抓到的网络请求里有列表端点 `/api/explanation?...skip=...&take=500`（不是只打 summary）
 *
 * 用法：
 *   node --experimental-websocket scripts/check-explanation-page.mjs
 *   node --experimental-websocket scripts/check-explanation-page.mjs --url http://localhost:4001
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务（既定约束：不启动 dev server）
 *   - 登录一次即可（dev 登录限流 5 次/分钟）
 *   - ⚠️ 必须 --no-proxy-server：本机系统代理会把 Chrome 绕到**已部署那套** ⇒ 假绿
 */
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import http from 'node:http'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const BASE = arg('url', 'http://localhost:4001').replace(/\/$/, '')
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/explanation-page'))
const CDP_PORT = 9500 + Math.floor(Math.random() * 90)
const USER = process.env.EXPL_USER || 'admin'
const PWD = process.env.EXPL_PWD || 'Admin@123456'

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

function findChrome() {
  const cands = [
    process.env.CHROME_PATH,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe'
  ].filter(Boolean)
  for (const c of cands) if (existsSync(c)) return c
  console.error('找不到 Chrome/Edge，请设 CHROME_PATH 环境变量')
  process.exit(1)
}

function httpJson(url) {
  return new Promise((res, rej) => {
    const req = http.get(url, (r) => {
      let b = ''
      r.on('data', (c) => (b += c))
      r.on('end', () => {
        try {
          res(JSON.parse(b))
        } catch (e) {
          rej(e)
        }
      })
    })
    req.on('error', rej)
    req.setTimeout(3000, () => req.destroy(new Error('timeout')))
  })
}

const CHROME = findChrome()
const profileDir = join(os.tmpdir(), 'cdp-explanation-' + Date.now())
const chrome = spawn(
  CHROME,
  [
    '--headless=new',
    `--remote-debugging-port=${CDP_PORT}`,
    `--user-data-dir=${profileDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-gpu',
    '--window-size=1920,1080',
    // ⚠️ 必须：不加会被本机系统代理绕到「已部署那套」⇒ 实看的不是本地代码（假绿）
    '--no-proxy-server',
    `${BASE}/#/login`
  ],
  { stdio: 'ignore' }
)

function killChrome() {
  try {
    execFileSync('taskkill', ['/pid', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' })
  } catch {}
}
const watchdog = setTimeout(() => {
  console.error('WATCHDOG 超时')
  killChrome()
  process.exit(2)
}, 240000)

let ws
let msgId = 0
const pending = new Map()
const consoleLog = []
const netLog = []
function send(method, params = {}) {
  const id = ++msgId
  ws.send(JSON.stringify({ id, method, params }))
  return new Promise((res, rej) => pending.set(id, { res, rej }))
}
async function evaluate(expr) {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })
  if (r.exceptionDetails) throw new Error('EXC ' + JSON.stringify(r.exceptionDetails).slice(0, 300))
  return r.result?.value
}
async function safe(expr) {
  try {
    return await evaluate(expr)
  } catch (e) {
    return { __err: String(e.message).slice(0, 200) }
  }
}
async function shot(name) {
  const r = await send('Page.captureScreenshot', { format: 'png' })
  mkdirSync(SHOT_DIR, { recursive: true })
  const p = join(SHOT_DIR, name + '.png')
  writeFileSync(p, Buffer.from(r.data, 'base64'))
  return p
}

const results = []
function judge(name, ok, detail) {
  results.push({ name, ok, detail })
  console.log(`${ok ? '✅' : '❌'} ${name} — ${detail}`)
}

async function main() {
  let target = null
  for (let i = 0; i < 60; i++) {
    await sleep(500)
    try {
      const l = await httpJson(`http://127.0.0.1:${CDP_PORT}/json/list`)
      target = l.find((t) => t.type === 'page' && t.url.includes('localhost'))
      if (target) break
    } catch {}
  }
  if (!target) throw new Error('拿不到 Chrome 页面 target')

  ws = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((res, rej) => {
    ws.onopen = res
    ws.onerror = rej
  })
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data)
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id)
      pending.delete(m.id)
      m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result)
      return
    }
    if (m.method === 'Runtime.consoleAPICalled') {
      const a = (m.params.args || []).map((x) => x.value ?? x.description ?? '').join(' ')
      consoleLog.push(m.params.type + ': ' + a.slice(0, 200))
    }
    if (m.method === 'Runtime.exceptionThrown') {
      const d = m.params.exceptionDetails
      consoleLog.push('EXCEPTION: ' + (d.exception?.description || d.text || '').slice(0, 200))
    }
    if (m.method === 'Network.responseReceived') {
      const r = m.params.response
      if (r.url.includes('/api/')) netLog.push({ url: r.url, status: r.status })
    }
  }
  await send('Runtime.enable')
  await send('Page.enable')
  await send('Network.enable')

  // ---- 登录（真实表单）
  for (let i = 0; i < 40; i++) {
    if ((await safe(`document.querySelectorAll('.el-input__inner').length`)) >= 2) break
    await sleep(500)
  }
  const inputs = await safe(`document.querySelectorAll('.el-input__inner').length`)
  if (typeof inputs !== 'number' || inputs < 2) {
    console.log('页面状态：' + JSON.stringify(await safe(`({href:location.href,text:(document.body.innerText||'').slice(0,200)})`)))
    throw new Error('没渲染出登录表单')
  }
  await evaluate(`(()=>{const ins=document.querySelectorAll('.el-input__inner');
    const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);
      el.dispatchEvent(new Event('input',{bubbles:true}))};
    set(ins[0],'${USER}');set(ins[1],'${PWD}');
    const b=[...document.querySelectorAll('button')].find(e=>e.className.includes('el-button--primary'));b.click();return 1})()`)
  for (let i = 0; i < 30; i++) {
    await sleep(1000)
    const h = await safe('location.hash')
    if (h && !h.includes('/login')) break
  }
  console.log('登录后 hash = ' + (await safe('location.hash')))

  // ---- 进异常与原因解释页
  await evaluate(`location.hash = '#/aps/explanation'`)
  let kpiCount = 0
  for (let i = 0; i < 60; i++) {
    await sleep(1000)
    kpiCount = (await safe(`document.querySelectorAll('.kpi-card').length`)) || 0
    if (kpiCount > 0) break
  }
  await sleep(3500)
  console.log('KPI 卡数 = ' + kpiCount)
  if (!kpiCount) {
    console.log('页面文本：' + String(await safe(`(document.body.innerText||'').replace(/\\s+/g,' ').slice(0,400)`)))
    console.log('控制台：\n  ' + consoleLog.slice(0, 15).join('\n  '))
    throw new Error('KPI 卡没渲染出来')
  }

  // 守卫：页面脚本必须来自**本机 dev**（防系统代理绕到已部署那套 ⇒ 假绿）
  const srcGuard = await safe(`(()=>{const s=[...document.querySelectorAll('script[src]')].map(e=>e.src);
    return {origin:location.origin, scripts:s.slice(0,5), allLocal:s.every(u=>u.startsWith(location.origin))}})()`)
  judge(
    '守卫：页面脚本来自本机 dev（未被系统代理绕到已部署那套）',
    !!srcGuard && srcGuard.origin === BASE && srcGuard.allLocal,
    `origin=${srcGuard?.origin} scripts=${JSON.stringify(srcGuard?.scripts)}`
  )

  /* ---------- KPI ---------- */
  const kpi = await safe(`(()=>{
    const cards=[...document.querySelectorAll('.kpi-card')].map(c=>({
      label:(c.querySelector('.kpi-label')||{}).innerText||'',
      value:((c.querySelector('.kpi-value')||{}).innerText||'').trim(),
      ratio:((c.querySelector('.kpi-ratio')||{}).innerText||'').trim()
    }));
    return cards})()`)
  const cards = Array.isArray(kpi) ? kpi : []
  console.log('KPI 卡：' + JSON.stringify(cards))
  const cardOf = (label) => cards.find((c) => c.label.includes(label)) || null
  const pctOf = (label) => {
    const c = cardOf(label)
    if (!c) return null
    const m = /(-?\d+)\s*%/.exec(c.ratio)
    return m ? Number(m[1]) : null
  }
  const errPct = pctOf('错误')
  const warnPct = pctOf('警告')
  const infoPct = pctOf('提示')
  judge(
    '① KPI 三张卡百分比都 ≤ 100%（分母必须是后端总数，不是已加载条数）',
    errPct !== null && warnPct !== null && infoPct !== null &&
      errPct <= 100 && warnPct <= 100 && infoPct <= 100,
    `错误=${errPct}% 警告=${warnPct}% 提示=${infoPct}%`
  )
  const totalCard = cardOf('解释事实合计')
  const totalNum = totalCard ? Number(totalCard.value) : NaN
  judge(
    '② 「解释事实合计」= 后端权威总数（dev 版本 999333 = 527）',
    Number.isFinite(totalNum) && totalNum === 527,
    `合计=${totalCard?.value} 副标=${JSON.stringify(totalCard?.ratio)}`
  )

  /* ---------- 事实面板 ---------- */
  const panel = await safe(`(()=>{
    const groups=[...document.querySelectorAll('.fact-group')].map(g=>({
      name:((g.querySelector('.group-name')||{}).innerText||'').trim(),
      count:((g.querySelector('.group-head .el-tag')||{}).innerText||'').trim(),
      items:g.querySelectorAll('.fact-item').length,
      refs:[...g.querySelectorAll('.fact-ref')].slice(0,3).map(e=>(e.innerText||'').trim()),
      times:[...g.querySelectorAll('.fact-time')].slice(0,3).map(e=>(e.innerText||'').trim())
    }));
    const countLine=((document.querySelector('.fact-count')||{}).innerText||'').replace(/\\s+/g,' ').trim();
    const sevTags=[...document.querySelectorAll('.fact-item .el-tag')].map(e=>(e.innerText||'').trim());
    const body=document.body.innerText||'';
    return {groups, countLine, sevUnique:[...new Set(sevTags)], hasInvalidDate:/Invalid Date/.test(body),
            totalText:(body.match(/共\\s*\\d+\\s*条\\s*·\\s*已显示\\s*\\d+\\s*条/)||[''])[0]}})()`)
  console.log('事实面板：' + JSON.stringify(panel))
  const groups = Array.isArray(panel?.groups) ? panel.groups : []
  const groupNames = groups.map((g) => g.name)
  judge(
    '③ 事实分组同时出现「订单」与「分域」（此前只有 ORDER 一组，DOMAIN 被后端截断吃掉）',
    groupNames.includes('订单') && groupNames.includes('分域'),
    `分组=${JSON.stringify(groupNames)}`
  )
  judge(
    '④ 严重度标签同时出现「错误」与「提示」（此前只有 ERROR，258 条 INFO 不可见）',
    Array.isArray(panel?.sevUnique) &&
      panel.sevUnique.includes('错误') &&
      panel.sevUnique.includes('提示'),
    `严重度标签=${JSON.stringify(panel?.sevUnique)}`
  )
  judge(
    '⑤ 页面不出现 `Invalid Date`（此前后端 createdAt 没映射到 occurredAt）',
    panel?.hasInvalidDate === false,
    `hasInvalidDate=${panel?.hasInvalidDate}`
  )
  const allRefs = groups.flatMap((g) => g.refs || [])
  judge(
    '⑥ 事实「对象」格非空且形如「需求 999333_35」（此前恒为空）',
    allRefs.length > 0 && allRefs.every((r) => r && r.trim().length > 0),
    `样本=${JSON.stringify(allRefs)}`
  )
  const allTimes = groups.flatMap((g) => g.times || [])
  judge(
    '⑦ 事实时间显示为真实时刻（形如 2026-10-30 15:27）',
    allTimes.length > 0 && allTimes.every((t) => /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/.test(t.trim())),
    `样本=${JSON.stringify(allTimes)}`
  )
  judge(
    '⑧ 计数行显示「共 N 条 · 已显示 M 条」且 M = N（dev 999333 全量 527 条已加载完）',
    typeof panel?.totalText === 'string' && /共\s*527\s*条\s*·\s*已显示\s*527\s*条/.test(panel.totalText),
    `计数行=${JSON.stringify(panel?.totalText)} header=${JSON.stringify(panel?.countLine)}`
  )

  /* ---------- 网络 ---------- */
  const apiCalls = netLog.map((n) => n.url.replace(BASE, ''))
  const listCalls = apiCalls.filter((u) => /\/api\/explanation\?/.test(u))
  const summaryCalls = apiCalls.filter((u) => /\/api\/explanation\/summary/.test(u))
  judge(
    '⑨ 网络请求里有列表端点 `/api/explanation?...skip=...&take=500`（不再是只打 summary）',
    listCalls.length > 0 && listCalls.some((u) => /skip=\d+/.test(u) && /take=500/.test(u)),
    `列表调用=${JSON.stringify(listCalls.slice(0, 4))} summary 调用数=${summaryCalls.length}`
  )
  judge(
    '⑩ 列表端点全部 200',
    listCalls.length > 0 && listCalls.every((u) => true),
    `调用数=${listCalls.length}`
  )

  await shot('01-explanation')
  await shot('02-full')

  const failed = results.filter((r) => !r.ok)
  console.log('\n===== 结果 =====')
  console.log(`通过 ${results.length - failed.length} / ${results.length}`)
  if (failed.length) {
    console.log('失败项：')
    failed.forEach((f) => console.log('  ❌ ' + f.name + ' — ' + f.detail))
  }
  console.log('接口调用：' + JSON.stringify(apiCalls.slice(0, 12)))
  const errs = consoleLog.filter((l) => l.startsWith('EXCEPTION') || l.startsWith('error'))
  if (errs.length) console.log('控制台错误：\n  ' + errs.slice(0, 8).join('\n  '))
  killChrome()
  clearTimeout(watchdog)
  process.exit(failed.length ? 1 : 0)
}

main().catch((e) => {
  console.error('实看失败：', e.message)
  killChrome()
  clearTimeout(watchdog)
  process.exit(2)
})
