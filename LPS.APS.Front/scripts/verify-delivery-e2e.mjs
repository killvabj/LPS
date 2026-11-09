#!/usr/bin/env node
/**
 * 交付包端到端校验：用**本地产物**打**真服务器后端**，走真实登录表单，判定 API 是否命中且 200。
 *
 * 为什么不能直接在 localhost 上验（这是本脚本存在的主要理由）：
 *   产物里编的是**绝对地址** `http://10.116.2.75:8081`，且服务器实跑的是 **CORS 白名单策略**
 *   （`Program.cs:325` 非 Development ⇒ `"Default"`，AllowedOrigins 只含 `http://10.116.2.75:8080` 等）。
 *   若用 `http://localhost:<port>` 起站点，浏览器 Origin 不在白名单 ⇒ 预检返回 204 但**无 ACAO**
 *   ⇒ 被浏览器拦掉 ⇒ 会误判成"包坏了"，其实是来源不对。
 *
 *   本脚本用 Chrome 的 `--host-resolver-rules=MAP 10.116.2.75 <本机IP>`，
 *   把**浏览器侧的来源伪装成生产同款** `http://10.116.2.75:8080`：
 *     - 本机 :8080 静态伺服 dist-pro 产物（模拟 IIS 站点根）
 *     - 本机 :8081 反代到真服务器 10.116.2.75:8081（Origin 原样透传 ⇒ 白名单命中）
 *     - 反代是 Node 发的请求，**不受 Chrome 的 host-resolver-rules 影响** ⇒ 真的打到服务器
 *
 * 局限（说清免得误用）：
 *   这一步验的是"产物 + 后端 + 来源"这一整条链，**唯一没验的是 IIS 铺站点根之后从公网地址真访问**，
 *   那一段只能等 2号位 部署完再验。
 *
 * 用法：
 *   node --experimental-websocket scripts/verify-delivery-e2e.mjs
 *   node --experimental-websocket scripts/verify-delivery-e2e.mjs --api http://10.116.2.75:8081 --ip 10.116.3.123
 *
 * 环境变量：
 *   CHROME_PATH   指定 chrome/msedge 可执行文件（默认按常见安装路径探测）
 */
import { spawn, execFileSync } from 'node:child_process'
import { createReadStream, statSync, existsSync, readdirSync, readFileSync } from 'node:fs'
import { join, extname, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import http from 'node:http'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const DIST = join(ROOT, 'dist-pro')

const SITE_PORT = Number(process.env.DELIVERY_SITE_PORT || 8080)
const PROXY_PORT = Number(process.env.DELIVERY_PROXY_PORT || 8081)
const SITE_HOST = '10.116.2.75' // 生产同款主机名 —— 来源必须等于它才能过 CORS 白名单
const SITE_ORIGIN = `http://${SITE_HOST}:${SITE_PORT}`

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const REAL_BACKEND = arg('api', 'http://10.116.2.75:8081')
const MY_IP = arg('ip', pickLocalIp())
const CDP_PORT = 8800 + Math.floor(Math.random() * 90)

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.map': 'application/json'
}

/** 挑一个本机 IPv4：优先与服务器同网段（10.116.x），否则取私有地址。 */
function pickLocalIp() {
  const cands = []
  for (const list of Object.values(os.networkInterfaces())) {
    for (const ni of list || []) {
      if (ni.family === 'IPv4' && !ni.internal) cands.push(ni.address)
    }
  }
  const same = cands.find((a) => a.startsWith('10.116.'))
  if (same) return same
  const priv = cands.find((a) => /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(a))
  return priv || cands[0] || '127.0.0.1'
}

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

// ---- 1) 静态站（模拟 IIS 站点根）
const site = http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0])
  if (p === '/') p = '/index.html'
  let file = join(DIST, p)
  if (!existsSync(file) || !statSync(file).isFile()) file = join(DIST, 'index.html')
  res.writeHead(200, { 'Content-Type': MIME[extname(file)] || 'application/octet-stream' })
  createReadStream(file).pipe(res)
})

// ---- 2) API 反代（浏览器 Origin 已是白名单来源，原样透传给真服务器）
const api = http.createServer((req, res) => {
  const chunks = []
  req.on('data', (c) => chunks.push(c))
  req.on('end', () => {
    const proxyReq = http.request(
      REAL_BACKEND + req.url,
      { method: req.method, headers: { ...req.headers, host: new URL(REAL_BACKEND).host } },
      (proxyRes) => {
        res.writeHead(proxyRes.statusCode, proxyRes.headers)
        proxyRes.pipe(res)
      }
    )
    proxyReq.on('error', (e) => { res.writeHead(502); res.end(String(e)) })
    proxyReq.end(Buffer.concat(chunks))
  })
})

// ---- 3) Chrome（把 10.116.2.75 解析到本机）
const CHROME = findChrome()
const profileDir = join(os.tmpdir(), 'cdp-delivery-' + Date.now())
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1680,1000',
  // ⚠️ `--no-proxy-server` 是**必需**的，不是可选优化：
  //   本机若配了系统 HTTP 代理，Chrome 会把 http://10.116.2.75:8080 交给代理，
  //   `--host-resolver-rules` 只管 DNS/直连解析、**管不到代理发起的请求** ⇒
  //   浏览器会去连**真实部署站**（另一套产物，甚至 mock 版），本脚本却照样跑完并报 OK
  //   —— 即「验的是别人的包，却报本地产物通过」的假绿（2026-11-02 实踩）。
  '--no-proxy-server',
  `--host-resolver-rules=MAP ${SITE_HOST} ${MY_IP}`,
  `${SITE_ORIGIN}/#/login`
], { stdio: 'ignore' })

function killChrome() {
  try { execFileSync('taskkill', ['/pid', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' }) } catch {}
}
const watchdog = setTimeout(() => { console.error('WATCHDOG 超时'); killChrome(); process.exit(2) }, 180000)

// ---- CDP
let ws
let msgId = 0
const pending = new Map()
const netLog = []
const consoleLog = []
function send(method, params = {}) {
  const id = ++msgId
  ws.send(JSON.stringify({ id, method, params }))
  return new Promise((res, rej) => pending.set(id, { res, rej }))
}
async function evaluate(expr, timeout) {
  const p = { expression: expr, returnByValue: true, awaitPromise: true }
  if (timeout) p.timeout = timeout
  const r = await send('Runtime.evaluate', p)
  if (r.exceptionDetails) throw new Error('EXC ' + JSON.stringify(r.exceptionDetails).slice(0, 300))
  return r.result?.value
}
async function safe(expr, timeout) {
  try { return await evaluate(expr, timeout) } catch (e) { return { __err: String(e.message).slice(0, 200) } }
}

async function main() {
  if (!existsSync(join(DIST, 'index.html'))) {
    console.error('找不到 dist-pro/index.html —— 先跑 node scripts/build-delivery.mjs')
    process.exit(1)
  }
  await new Promise((r) => site.listen(SITE_PORT, '0.0.0.0', r))
  await new Promise((r) => api.listen(PROXY_PORT, '0.0.0.0', r))
  console.log(`本地产物站 :${SITE_PORT} · 反代 :${PROXY_PORT} → ${REAL_BACKEND}`)
  console.log(`浏览器来源伪装为 ${SITE_ORIGIN}（白名单内），本机 IP = ${MY_IP}`)

  let target = null
  for (let i = 0; i < 60; i++) {
    await sleep(500)
    try {
      const l = await httpJson(`http://127.0.0.1:${CDP_PORT}/json/list`)
      target = l.find((t) => t.type === 'page' && t.url.includes(SITE_HOST))
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
    if (m.method === 'Network.responseReceived') {
      const r = m.params.response
      if (r.url.includes('/api/')) netLog.push({ url: r.url, status: r.status })
    }
    if (m.method === 'Runtime.consoleAPICalled') {
      const a = (m.params.args || []).map((x) => x.value ?? x.description ?? '').join(' ')
      consoleLog.push(m.params.type + ': ' + a.slice(0, 300))
    }
    if (m.method === 'Runtime.exceptionThrown') {
      const d = m.params.exceptionDetails
      consoleLog.push('EXCEPTION: ' + (d.exception?.description || d.text || '').slice(0, 300))
    }
  }
  await send('Runtime.enable')
  await send('Network.enable')

  /** 页面实际长什么样（排查"没渲染出表单"用）。 */
  async function pageState() {
    return await safe(`(()=>({href:location.href,title:document.title,
      bodyLen:(document.body.innerHTML||'').length,
      text:(document.body.innerText||'').replace(/\\s+/g,' ').trim().slice(0,300),
      inputs:document.querySelectorAll('input').length,
      scripts:[...document.querySelectorAll('script[src]')].map(s=>s.src).slice(0,5)}))()`)
  }

  /**
   * 守卫：浏览器加载的**必须**是本地产物那份入口 js。
   *
   * 为什么必须有这条（2026-11-02 实踩）：
   *   本机配了系统 HTTP 代理时，Chrome 把站点请求交给了代理 ⇒ `--host-resolver-rules` 失效
   *   ⇒ 浏览器连到了**真实部署站**（另一套产物）。此时脚本照样能登录、照样能拿到 API 200、
   *   照样报 OK —— 但验的根本不是我们要验的包。**没有这条守卫 = 一台永不失手的假绿机。**
   */
  const expectedEntry = (() => {
    const m = readFileSync(join(DIST, 'index.html'), 'utf8').match(/assets\/index-[A-Za-z0-9_-]+\.js/)
    return m ? m[0].split('/').pop() : ''
  })()
  await sleep(1500)
  const loadedAssets = await safe(`[...document.querySelectorAll('script[src]')].map(s=>s.src.split('/').pop())`)
  console.log('产物入口：本地期望 = ' + (expectedEntry || '(未解析到)') + ' · 浏览器实载 = ' + JSON.stringify(loadedAssets))
  if (expectedEntry && Array.isArray(loadedAssets) && loadedAssets.length && !loadedAssets.includes(expectedEntry)) {
    throw new Error(
      '浏览器加载的不是本地产物（期望 ' + expectedEntry + '，实际 ' + loadedAssets.join(',') +
      '）—— 多半被系统代理绕到了别处；本次结果作废，别当通过'
    )
  }

  // 真实登录表单
  for (let i = 0; i < 40; i++) {
    if ((await safe(`document.querySelectorAll('.el-input__inner').length`)) >= 2) break
    await sleep(500)
  }
  const inputs = await safe(`document.querySelectorAll('.el-input__inner').length`)
  console.log('登录页输入框：' + inputs)
  if (typeof inputs !== 'number' || inputs < 2) {
    console.log('页面状态：' + JSON.stringify(await pageState(), null, 1))
    if (consoleLog.length) console.log('控制台：\n  ' + consoleLog.slice(0, 15).join('\n  '))
    throw new Error('没渲染出登录表单')
  }

  await evaluate(`(()=>{const ins=document.querySelectorAll('.el-input__inner');
    const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);
      el.dispatchEvent(new Event('input',{bubbles:true}))};
    set(ins[0],'admin');set(ins[1],'Admin@123456');
    const b=[...document.querySelectorAll('button')].find(e=>e.className.includes('el-button--primary'));b.click();return 1})()`)

  for (let i = 0; i < 30; i++) {
    await sleep(1000)
    const h = await safe('location.hash')
    if (h && !h.includes('/login')) { console.log('登录后 hash: ' + h); break }
  }
  await sleep(6000)

  const st = await safe(`(()=>({href:location.href,title:document.title,
    text:(document.body.innerText||'').replace(/\\s+/g,' ').trim().slice(0,200),
    menu:[...document.querySelectorAll('.el-menu-item,.el-sub-menu__title')].slice(0,8).map(e=>(e.innerText||'').trim()).filter(Boolean)}))()`)
  console.log('页面状态: ' + JSON.stringify(st, null, 1))

  const seen = new Set()
  for (const n of netLog) {
    const k = n.url + n.status
    if (seen.has(k)) continue
    seen.add(k)
    console.log(`  ${n.status}  ${n.url}`)
  }
  const apiCalls = netLog.filter((n) => n.url.startsWith(REAL_BACKEND))
  const okApi = apiCalls.filter((n) => n.status === 200)
  const bad = netLog.filter((n) => n.status >= 400)
  console.log(`\n判定：API 调用 ${apiCalls.length} 次（200 的 ${okApi.length} 次，另有 OPTIONS 预检 204 属正常）`)
  console.log(`目标主机 ${REAL_BACKEND} = ${apiCalls.length > 0 ? '已命中' : '未命中'}`)
  if (bad.length) {
    console.log('4xx/5xx: ' + JSON.stringify(bad))
    process.exitCode = 1
  }
  if (apiCalls.length === 0 || okApi.length === 0) process.exitCode = 1
  if (process.exitCode) console.error('\n[FAIL] 有 API 未命中目标主机或未返回 200')
  else console.log('\n[OK] 产物 → 真后端 链路通')
}

main()
  .catch((e) => { console.error('ERR', e.stack || e.message); process.exitCode = 1 })
  .finally(async () => {
    clearTimeout(watchdog)
    try { ws && ws.close() } catch {}
    site.close()
    api.close()
    killChrome()
    setTimeout(() => process.exit(process.exitCode || 0), 500)
  })
