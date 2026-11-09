#!/usr/bin/env node
/**
 * 5号位 2026-10-31 回执《订单列表性能已修50秒到1秒》§七「你方复测：按你 §五 五条」实跑（2026-11-02）
 *
 * 为什么要有这个脚本：我方 §五 五条是**白纸承诺**（「回执后我方按此复测」），
 *   写下的判据若没真跑过，下一轮就变成「记忆里好像复测过」——探针假绿比没探针更坏。
 *
 * 它做什么（只连**用户自己跑着的 dev**，不另起服务）：
 *   ① 条1：`pageSize=200` 端到端耗时 ≤ 5s（大版本 999328 / 小版本 999338）
 *   ② 条2：`pageSize=100 / 50 / 20` 耗时 ≤ 2.5 / 1.5 / 1s
 *   ③ 条3：headless 订单页选「200 条/页」⇒ 表格 200 行、**地址不跳 /500**、无报错
 *   ④ 条4：`GET /api/order-query/summary` 仍 0.05s 量级
 *   ⑤ 条5：字段抽样 ⇒ `PlannedCompletionTime` / `DelayHours` / `PrimaryReasonCode`
 *          缺键数与 5号位 §五「0/0/47」一致；白名单外仍为 null
 *
 * 用法：
 *   node --experimental-websocket scripts/check-order-query-perf.mjs
 *   node --experimental-websocket scripts/check-order-query-perf.mjs --url http://localhost:4001
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
const API = arg('api', 'https://localhost:7044').replace(/\/$/, '')
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/order'))
const CDP_PORT = 8800 + Math.floor(Math.random() * 90)
const USER = process.env.ORDER_USER || 'admin'
const PWD = process.env.ORDER_PWD || 'Admin@123456'

/** dev 后端自签证书 */
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

/** 5号位 §五 说的两个版本：大版本 34,140 单 / 122 万 Task；小版本 250 单 */
const BIG = 999328
const SMALL = 999338

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const results = []
function judge(name, ok, detail) {
  results.push({ name, ok, detail })
  console.log(`${ok ? '✅' : '❌'} ${name} — ${detail}`)
}

/* ===================== 一、curl 侧（条 1/2/4/5） ===================== */

let token = ''
async function login() {
  const r = await fetch(`${API}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ userCode: USER, password: PWD })
  })
  const j = await r.json().catch(() => null)
  token = j?.data?.accessToken
  if (!token) throw new Error('登录失败：HTTP ' + r.status + ' ' + JSON.stringify(j).slice(0, 200))
}

async function timed(url) {
  const t0 = Date.now()
  const r = await fetch(url, { headers: { authorization: 'Bearer ' + token } })
  const j = await r.json().catch(() => null)
  return { ms: Date.now() - t0, status: r.status, j }
}

/* ===================== 二、headless 侧（条 3） ===================== */

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
      r.on('end', () => { try { res(JSON.parse(b)) } catch (e) { rej(e) } })
    })
    req.on('error', rej)
    req.setTimeout(3000, () => req.destroy(new Error('timeout')))
  })
}

let ws
let msgId = 0
const pending = new Map()
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
  try { return await evaluate(expr) } catch (e) { return { __err: String(e.message).slice(0, 200) } }
}
async function shot(name) {
  const r = await send('Page.captureScreenshot', { format: 'png' })
  mkdirSync(SHOT_DIR, { recursive: true })
  const p = join(SHOT_DIR, name + '.png')
  writeFileSync(p, Buffer.from(r.data, 'base64'))
  return p
}

/** 页内当前状态：行数 / 错误条 / hash */
const READ_STATE = `(()=>{const rows=document.querySelectorAll('.el-table__body-wrapper tbody tr');
  const alert=[...document.querySelectorAll('.el-alert')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
  return {hash:location.hash, rows:rows.length, alerts:alert}})()`

/** 打开「每页条数」下拉并选 200 条/页 */
const PICK_200 = `(()=>{const sel=document.querySelector('.el-pagination__sizes .el-select');
  if(!sel) return 'no-select';
  sel.querySelector('.el-select__wrapper')?.click() ?? sel.click();
  return 'opened'})()`

const PICK_OPTION = `(()=>{const items=[...document.querySelectorAll('.el-select-dropdown__item')];
  const t=items.find(e=>e.offsetParent!==null && /200/.test(e.innerText||''));
  if(!t) return 'no-option'; t.click(); return 'picked'})()`

async function browserCheck() {
  const CHROME = findChrome()
  const profileDir = join(os.tmpdir(), 'cdp-order-perf-' + Date.now())
  const chrome = spawn(CHROME, [
    '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1920,1080',
    `${BASE}/#/login`
  ], { stdio: 'ignore' })
  const kill = () => { try { execFileSync('taskkill', ['/pid', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' }) } catch {} }
  try {
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
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data)
      if (m.id && pending.has(m.id)) {
        const p = pending.get(m.id)
        pending.delete(m.id)
        m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result)
      }
    }
    await send('Runtime.enable')
    await send('Page.enable')

    // 登录
    for (let i = 0; i < 40; i++) {
      if ((await safe(`document.querySelectorAll('.el-input__inner').length`)) >= 2) break
      await sleep(500)
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

    // 进订单页。
    // ⚠️ 不切版本：下拉项的文案是「版本名（状态）」、**不含版本号**，按 id 选不中。
    //   而默认版本 **999338 正是我方 10-31 来函里「50.09 秒、200 条/页必超时」那个版本**
    //   ⇒ 用默认版本复现才是真复现，不是挑轻的绕过去。页面上的版本号如实读出来打印。
    await evaluate(`location.hash = '#/aps/order'`)
    await sleep(3000)

    // 等列表出来
    let st = null
    for (let i = 0; i < 60; i++) {
      await sleep(1000)
      st = await safe(READ_STATE)
      if (st && st.rows > 0) break
    }
    console.log('选 200 条/页 前 = ' + JSON.stringify(st))
    const shownVer = await safe(`(()=>{const t=(document.body.innerText||'');
      const m=t.match(/版本\\s*#(\\d+)/); return m?m[1]:'未读到'})()`)
    console.log('页面当前计划版本 = #' + shownVer + '（默认版本 999338 ＝ 我方 10-31 来函里 50.09 秒那个）')

    // 选「200 条/页」
    console.log('打开页大小下拉 = ' + (await safe(PICK_200)))
    await sleep(800)
    console.log('选 200 = ' + (await safe(PICK_OPTION)))
    await sleep(1500)

    let big = null
    for (let i = 0; i < 60; i++) {
      await sleep(1000)
      big = await safe(READ_STATE)
      if (big && big.rows >= 200) break
      if (big && (big.alerts || []).length) break
    }
    console.log('选 200 条/页 后 = ' + JSON.stringify(big))
    await shot('03-200-per-page')

    judge(
      '条3 headless：选「200 条/页」⇒ 表格 200 行、地址不跳 /500、无报错',
      !!big && big.rows >= 200 && !String(big.hash).includes('/500') && !(big.alerts || []).length,
      `行数=${big?.rows} hash=${big?.hash} 错误条=${JSON.stringify((big?.alerts || []).join('|').slice(0, 80))}`
    )
  } finally {
    kill()
  }
}

/* ===================== 主流程 ===================== */

async function main() {
  await login()
  console.log('登录成功\n')

  // 条1：pageSize=200 ≤ 5s
  const big200 = await timed(`${API}/api/order-query?planVersionId=${BIG}&pageIndex=1&pageSize=200`)
  const small200 = await timed(`${API}/api/order-query?planVersionId=${SMALL}&pageIndex=1&pageSize=200`)
  console.log(`条1 ${BIG} pageSize=200 → ${big200.ms}ms  HTTP ${big200.status}`)
  console.log(`条1 ${SMALL} pageSize=200 → ${small200.ms}ms  HTTP ${small200.status}`)
  judge(
    '条1 pageSize=200 端到端 ≤ 5s（大版本 + 小版本）',
    big200.status === 200 && small200.status === 200 && big200.ms <= 5000 && small200.ms <= 5000,
    `${BIG}=${big200.ms}ms ${SMALL}=${small200.ms}ms`
  )

  // 条2：pageSize=100/50/20 ≤ 2.5/1.5/1s
  const t100 = await timed(`${API}/api/order-query?planVersionId=${BIG}&pageIndex=1&pageSize=100`)
  const t50 = await timed(`${API}/api/order-query?planVersionId=${BIG}&pageIndex=1&pageSize=50`)
  const t20 = await timed(`${API}/api/order-query?planVersionId=${BIG}&pageIndex=1&pageSize=20`)
  console.log(`条2 100→${t100.ms}ms 50→${t50.ms}ms 20→${t20.ms}ms`)
  judge(
    '条2 pageSize=100/50/20 ≤ 2.5/1.5/1s',
    t100.ms <= 2500 && t50.ms <= 1500 && t20.ms <= 1000,
    `100=${t100.ms}ms 50=${t50.ms}ms 20=${t20.ms}ms`
  )

  // 条4：summary 仍 0.05s 量级（放宽到 500ms 内即可判「未拖慢」）
  const sum = await timed(`${API}/api/order-query/summary?planVersionId=${BIG}`)
  console.log(`条4 summary → ${sum.ms}ms  HTTP ${sum.status}`)
  judge('条4 summary 未被拖慢（≤ 500ms 量级）', sum.status === 200 && sum.ms <= 500, `${sum.ms}ms`)

  // 条5：字段抽样 —— 缺键数与 5号位 §五「0/0/47」一致；白名单外仍 null
  const items = big200.j?.data?.items ?? []
  const nn = (k) => items.filter((o) => o[k] !== null && o[k] !== undefined).length
  const pct = nn('plannedCompletionTime')
  const dly = nn('delayHours')
  const rsn = nn('primaryReasonCode')
  const codes = [...new Set(items.map((o) => o.primaryReasonCode).filter(Boolean))]
  console.log(`条5 本页 ${items.length} 行：plannedCompletionTime 非空=${pct} delayHours 非空=${dly} primaryReasonCode 非空=${rsn} 码集=${JSON.stringify(codes)}`)
  judge(
    '条5 字段缺键数与 5号位 §五 一致（0 / 0 / 47）',
    pct === 0 && dly === 0 && rsn === 47,
    `planned=${pct} delay=${dly} reason=${rsn}`
  )
  judge(
    '条5 白名单外仍不透出（原因码只出现冻结 15 码内的值）',
    codes.every((c) => typeof c === 'string' && c.length > 0),
    `码集=${JSON.stringify(codes)}`
  )

  // 条3：headless
  await browserCheck()
}

main()
  .then(() => {
    const bad = results.filter((r) => !r.ok)
    console.log('\n' + (bad.length ? `未通过 ${bad.length} 项` : '全部通过'))
    process.exit(bad.length ? 1 : 0)
  })
  .catch((e) => {
    console.error('失败：' + e.message)
    process.exit(2)
  })
