#!/usr/bin/env node
/**
 * 订单页「出错不整页跳 /500」实看（2026-11-02）
 *
 * 为什么要有这个脚本：静态探针只能证明代码写了 `noErrorRedirect`，
 *   **证明不了浏览器里出错时用户真的还留在订单页**（而不是被 `window.location.replace('/500')` 踢走）。
 *   已知教训：「有数据 ≠ 路径走过」——同一条教训的错误处理分支同样适用。
 *
 * 它做什么（走真实登录表单 → 真实订单页 → 真实断网 → 真实点「重置」重新触发列表）：
 *   ① 正常态：列表有行（先证明页面本身是通的，后面的失败才有对照）
 *   ② 断网 + 点「重置」⇒ 列表请求失败
 *   ③ 断言：**hash 仍是 #/aps/order**（没被踢到 #/500）+ 页内出现「加载失败：…」错误条
 *   ④ 断言：错误条文案是大白话（「网络连接失败」/「请求超时」），不是 axios 英文原文
 *   ⑤ 恢复网络 + 再点「重置」⇒ 列表重新有行、错误条消失（证明可自愈，不卡死）
 *
 * 用法：
 *   node --experimental-websocket scripts/check-order-error-page.mjs
 *   node --experimental-websocket scripts/check-order-error-page.mjs --url http://localhost:4001
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务（既定约束：不启动 dev server）
 *   - 登录一次即可（dev 登录限流 5 次/分钟）
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
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/order'))
const CDP_PORT = 8900 + Math.floor(Math.random() * 90)
const USER = process.env.ORDER_USER || 'admin'
const PWD = process.env.ORDER_PWD || 'Admin@123456'

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
      r.on('end', () => { try { res(JSON.parse(b)) } catch (e) { rej(e) } })
    })
    req.on('error', rej)
    req.setTimeout(3000, () => req.destroy(new Error('timeout')))
  })
}

const CHROME = findChrome()
const profileDir = join(os.tmpdir(), 'cdp-order-' + Date.now())
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1920,1080',
  `${BASE}/#/login`
], { stdio: 'ignore' })

function killChrome() {
  try { execFileSync('taskkill', ['/pid', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' }) } catch {}
}
const watchdog = setTimeout(() => { console.error('WATCHDOG 超时'); killChrome(); process.exit(2) }, 300000)

let ws
let msgId = 0
const pending = new Map()
const consoleLog = []
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

const results = []
function judge(name, ok, detail) {
  results.push({ name, ok, detail })
  console.log(`${ok ? '✅' : '❌'} ${name} — ${detail}`)
}

/** 页面里读订单列表当前状态：行数 + 错误条文案 + 当前 hash */
const READ_STATE = `(()=>{const rows=document.querySelectorAll('.el-table__body-wrapper tbody tr');
  const alert=[...document.querySelectorAll('.el-alert')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
  const loading=!!document.querySelector('.el-loading-mask');
  return {hash:location.hash, rows:rows.length, alerts:alert, loading}})()`

/** 点「重置」按钮（触发一次列表重新拉取） */
const CLICK_RESET = `(()=>{const b=[...document.querySelectorAll('button')].find(e=>(e.innerText||'').trim()==='重置');
  if(!b) return 'no-button'; b.click(); return 'clicked'})()`

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
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
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

  // ---- 进订单页
  await evaluate(`location.hash = '#/aps/order'`)
  let st = null
  for (let i = 0; i < 60; i++) {
    await sleep(1000)
    st = await safe(READ_STATE)
    if (st && st.rows > 0) break
  }
  console.log('订单页初始状态 = ' + JSON.stringify(st))
  judge('① 正常态：订单列表有数据（先证明页面本身是通的）', !!st && st.rows > 0, `行数=${st?.rows}`)

  // ---- 断网 → 点「重置」触发一次失败请求
  await send('Network.emulateNetworkConditions', {
    offline: true, latency: 0, downloadThroughput: 0, uploadThroughput: 0
  })
  console.log('已切离线')
  await sleep(500)
  const clicked = await safe(CLICK_RESET)
  console.log('点「重置」= ' + clicked)
  let failed = null
  for (let i = 0; i < 20; i++) {
    await sleep(1000)
    failed = await safe(READ_STATE)
    if (failed && (failed.alerts?.length || failed.rows === 0)) break
  }
  console.log('断网后状态 = ' + JSON.stringify(failed))

  const alertText = (failed?.alerts || []).join(' | ')
  judge(
    '② 出错时仍在订单页（没被踢到 #/500）',
    !!failed && !String(failed.hash).includes('/500'),
    `hash=${failed?.hash}`
  )
  judge(
    '③ 页内出现「加载失败：…」错误条（不是空白页）',
    !!failed && /加载失败/.test(alertText),
    `错误条=${JSON.stringify(alertText.slice(0, 120))}`
  )
  judge(
    '④ 错误文案是大白话（不是 axios 英文原文）',
    /网络连接失败|请求超时|服务暂时不可用/.test(alertText) &&
      !/timeout of|ERR_NETWORK|Request failed with status/i.test(alertText),
    `文案=${JSON.stringify(alertText.slice(0, 120))}`
  )
  await shot('01-offline-error')

  // ---- 恢复网络 → 再点「重置」⇒ 应自愈
  await send('Network.emulateNetworkConditions', {
    offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1
  })
  console.log('已恢复网络')
  await sleep(800)
  await safe(CLICK_RESET)
  let back = null
  for (let i = 0; i < 30; i++) {
    await sleep(1000)
    back = await safe(READ_STATE)
    if (back && back.rows > 0) break
  }
  console.log('恢复后状态 = ' + JSON.stringify(back))
  judge(
    '⑤ 恢复网络后重试即自愈（列表重新有行、错误条消失）',
    !!back && back.rows > 0 && !(back.alerts || []).some((a) => /加载失败/.test(a)),
    `行数=${back?.rows} 错误条=${JSON.stringify((back?.alerts || []).join('|').slice(0, 80))}`
  )
  await shot('02-recovered')
}

main()
  .then(() => {
    const bad = results.filter((r) => !r.ok)
    console.log('\n' + (bad.length ? `未通过 ${bad.length} 项` : '全部通过'))
    if (consoleLog.length) console.log('控制台（供参考）：\n  ' + consoleLog.slice(0, 12).join('\n  '))
    killChrome()
    process.exit(bad.length ? 1 : 0)
  })
  .catch((e) => {
    console.error('失败：' + e.message)
    if (consoleLog.length) console.log('控制台：\n  ' + consoleLog.slice(0, 12).join('\n  '))
    killChrome()
    process.exit(2)
  })
