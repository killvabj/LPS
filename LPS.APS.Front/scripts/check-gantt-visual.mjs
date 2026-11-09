#!/usr/bin/env node
/**
 * 甘特页「看得见、点得中」实看（2026-11-02）
 *
 * 为什么要有这个脚本：领导看图判「这个用户用不了」——静态探针只能证明代码写了，
 *   **证明不了浏览器里那根条到底多宽、悬浮提示出不出来、缩放档位切不切得动**。
 *   已知教训：「有数据 ≠ 路径走过」（列表有数据不代表那条写入路径被点过）。
 *
 * 它做什么（走真实登录表单 → 真实页面 → 真实鼠标事件）：
 *   ① 量一根条的实际像素宽度（保底 14px 是否生效）
 *   ② 看条内任务号是否按宽度分级出现（宽条有字、窄条无字）
 *   ③ 鼠标悬停 → 悬浮提示是否出现、内容是否含业务字段
 *   ④ 切「小时」刻度 → 轴上是否变成小时列、条是否变宽
 *   ⑤ 切「资源负荷」页签 → 热力图是否渲染、行数对不对
 *   ⑥ 任务 KPI 的两个口径（窗口内 / 版本共 N 条）是否摆明面，且都跟后端真值一致
 *   ⑦ 截图落盘（人眼复核用）
 *
 * 用法：
 *   node --experimental-websocket scripts/check-gantt-visual.mjs
 *   node --experimental-websocket scripts/check-gantt-visual.mjs --url http://localhost:4001 --shot .tmp/gantt
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
import net from 'node:net'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const BASE = arg('url', 'http://localhost:4001').replace(/\/$/, '')
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/gantt'))
const USER = process.env.GANTT_USER || 'admin'
const PWD = process.env.GANTT_PWD || 'Admin@123456'
const API = process.env.VERIFY_API || 'https://localhost:7044'

// dev 后端是自签证书 ⇒ 关掉校验（与其它脚本一致）
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

/** 取一个 token：优先复用 VERIFY_TOKEN，避免再吃 dev 的 5 次/分钟登录限流 */
async function apiToken() {
  if (process.env.VERIFY_TOKEN) return process.env.VERIFY_TOKEN
  const r = await fetch(`${API}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ userCode: USER, password: PWD })
  })
  const j = await r.json().catch(() => null)
  const t = j?.data?.accessToken
  if (!t) throw new Error('登录失败：HTTP ' + r.status)
  return t
}

async function apiGet(token, path) {
  const r = await fetch(API + path, { headers: { Authorization: 'Bearer ' + token } })
  return (await r.json().catch(() => null))?.data
}

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
const profileDir = join(os.tmpdir(), 'cdp-gantt-' + Date.now())
// ⚠️ 端口 + URL 标记双保险（11-04 踩坑）：
//    固定端口会撞上**上一轮残留的 headless Chrome**（taskkill 漏掉的孤儿进程）⇒
//    读到别人（上一轮）的页面状态 ⇒ 出现「关键词框里凭空多出 admin、全表过滤为 0」的假象。
//    ⇒ ① 端口先探测占用再用  ② URL 带随机标记，只认带标记的那个 target
const MARK = 'ganttprobe' + Date.now().toString(36)
async function portFree(port) {
  return new Promise((res) => {
    const s = net.createConnection({ host: '127.0.0.1', port })
    s.on('connect', () => { s.destroy(); res(false) })
    s.on('error', () => res(true))
    setTimeout(() => { s.destroy(); res(true) }, 800)
  })
}
let CDP_PORT = 0
for (let i = 0; i < 40; i++) {
  const p = 9000 + Math.floor(Math.random() * 900)
  if (await portFree(p)) { CDP_PORT = p; break }
}
if (!CDP_PORT) { console.error('没找到空闲调试端口'); process.exit(1) }
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1920,1080',
  // 必须关系统代理：本机代理会把页面里的 API 请求绕到**已部署那套**⇒ 页面显示的是旧包 ⇒ 假绿
  '--no-proxy-server',
  `${BASE}/?${MARK}#/login`
], { stdio: 'ignore' })

function killChrome() {
  try { execFileSync('taskkill', ['/pid', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' }) } catch {}
}
const watchdog = setTimeout(() => { console.error('WATCHDOG 超时'); killChrome(); process.exit(2) }, 300000)

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
  try { return await evaluate(expr) } catch (e) { return { __err: String(e.message).slice(0, 200) } }
}
async function shot(name, clip) {
  const params = { format: 'png' }
  if (clip) params.clip = { ...clip, scale: clip.scale || 2 }
  const r = await send('Page.captureScreenshot', params)
  mkdirSync(SHOT_DIR, { recursive: true })
  const p = join(SHOT_DIR, name + '.png')
  writeFileSync(p, Buffer.from(r.data, 'base64'))
  return p
}

/** 取某元素的包围盒（用于局部放大截图）。 */
async function rectOf(selector) {
  return await safe(`(()=>{const e=document.querySelector('${selector}');
    if(!e) return null; const r=e.getBoundingClientRect();
    return {x:r.left,y:r.top,width:r.width,height:r.height}})()`)
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
      target = l.find((t) => t.type === 'page' && t.url.includes(MARK))
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
    if (m.method === 'Network.responseReceived') {
      const r = m.params.response
      if (r.url.includes('/api/')) netLog.push({ url: r.url, status: r.status })
    }
  }
  await send('Runtime.enable')
  await send('Page.enable')
  await send('Network.enable')

  // ---- 登录（真实表单）
  // ⚠️ 只认**登录页那两个表单框**（placeholder='admin or test'）：甘特页也有 .el-input__inner（关键词框/页大小），
  //    认错位置会把用户名打进关键词框 ⇒ 全表被搜成 0 条（11-04 踩过的坑）。
  for (let i = 0; i < 40; i++) {
    if ((await safe(`[...document.querySelectorAll('.el-input__inner')].filter(e=>(e.placeholder||'')==='admin or test').length`)) >= 2) break
    await sleep(500)
  }
  const inputs = await safe(`[...document.querySelectorAll('.el-input__inner')].filter(e=>(e.placeholder||'')==='admin or test').length`)
  if (typeof inputs !== 'number' || inputs < 2) {
    console.log('页面状态：' + JSON.stringify(await safe(`({href:location.href,text:(document.body.innerText||'').slice(0,200)})`)))
    throw new Error('没渲染出登录表单')
  }
  await evaluate(`(()=>{const ins=[...document.querySelectorAll('.el-input__inner')].filter(e=>(e.placeholder||'')==='admin or test');
    const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);
      el.dispatchEvent(new Event('input',{bubbles:true}))};
    set(ins[0],'${USER}');set(ins[1],'${PWD}');
    const b=[...document.querySelectorAll('button')].find(e=>e.className.includes('el-button--primary'));if(b)b.click();return 1})()`)
  for (let i = 0; i < 30; i++) {
    await sleep(1000)
    const h = await safe('location.hash')
    if (h && !h.includes('/login')) break
  }
  console.log('登录后 hash = ' + (await safe('location.hash')))

  // ---- 进甘特页
  await evaluate(`location.hash = '#/aps/gantt'`)
  let bars = 0
  for (let i = 0; i < 120; i++) {
    await sleep(1000)
    bars = (await safe(`document.querySelectorAll('.gantt_task_line').length`)) || 0
    if (bars > 0) break
  }
  await sleep(4000)
  console.log('甘特条数 = ' + bars)
  if (!bars) {
    console.log('页面文本：' + String(await safe(`(document.body.innerText||'').replace(/\\s+/g,' ').slice(0,400)`)))
    console.log('控制台：\n  ' + consoleLog.slice(0, 15).join('\n  '))
    throw new Error('甘特没渲染出条')
  }

  // ① 条宽
  const widths = await safe(`(()=>{const w=[...document.querySelectorAll('.gantt_task_line')]
      .map(e=>e.getBoundingClientRect().width).filter(x=>x>0).sort((a,b)=>a-b);
    if(!w.length) return null;
    return {n:w.length,min:w[0],p50:w[Math.floor(w.length/2)],max:w[w.length-1],under14:w.filter(x=>x<13.9).length}})()`)
  judge('① 任务条最小心宽保底 14px', !!widths && widths.min >= 13.9 && widths.under14 === 0,
    widths ? `n=${widths.n} min=${widths.min.toFixed(1)} p50=${widths.p50.toFixed(1)} max=${widths.max.toFixed(1)} <14px 的条=${widths.under14}` : '没量到条')

  // ② 条内任务号分级：标签只允许出现在宽度够的条上（窄条硬写会溢出盖住相邻条）
  // ⚠️ 泳道条（type=project，区间条）本来就不写条内文字 ⇒ 统计「宽条缺号」时要排除它
  const labels = await safe(`(()=>{const ls=[...document.querySelectorAll('.gantt_task_line')];
    const taskBars=ls.filter(e=>!e.classList.contains('dhx-task-lane'));
    const labelled=taskBars.filter(e=>e.querySelector('.dhx-bar-label'));
    const wide=taskBars.filter(e=>e.getBoundingClientRect().width>=56);
    return {bars:ls.length, taskBars:taskBars.length, withLabel:labelled.length, wide:wide.length,
      wideNoLabel:wide.filter(e=>!e.querySelector('.dhx-bar-label')).length,
      narrowWithLabel:labelled.filter(e=>e.getBoundingClientRect().width<56).length,
      sample:labelled.slice(0,3).map(e=>e.querySelector('.dhx-bar-label').textContent.trim())}})()`)
  judge('② 条内任务号按宽度分级（窄条不写字）', !!labels && labels.narrowWithLabel === 0,
    labels ? `条=${labels.bars}（任务条 ${labels.taskBars}） 带号=${labels.withLabel} 宽条=${labels.wide} 宽条缺号=${labels.wideNoLabel} 窄条带号=${labels.narrowWithLabel}` : '没量到条')

  // ③ 悬浮提示
  const rect = await safe(`(()=>{const es=[...document.querySelectorAll('.gantt_task_line')];
    const e=es.sort((a,b)=>b.getBoundingClientRect().width-a.getBoundingClientRect().width)[0];
    if(!e) return null; const r=e.getBoundingClientRect();
    return {x:r.left+r.width/2,y:r.top+r.height/2}})()`)
  let tip = null
  if (rect) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: rect.x, y: rect.y })
    await sleep(1200)
    tip = await safe(`(()=>{const t=document.querySelector('.gantt_tooltip');
      return t?{text:(t.innerText||'').replace(/\\s+/g,' ').trim().slice(0,300),visible:t.offsetParent!==null}:null})()`)
  }
  judge('③ 悬浮提示出现且含业务字段', !!tip && tip.visible && /任务：/.test(tip.text),
    tip ? `text=${JSON.stringify(tip.text.slice(0, 160))}` : '没出现悬浮提示')

  // ③b 左侧行名列**不**弹悬浮提示（2026-11-03 用户反馈）
  // 根因：tooltip 插件默认选择器 `[task_id]:not(.gantt_task_row)` 也命中左侧网格行 `.gantt_row`
  //   ⇒ 左右两侧弹同一份黑框（左侧那份遮行名）。修法见 DhxGantt.vue 的 applyTooltipScope。
  const gridRect = await safe(`(()=>{const rs=[...document.querySelectorAll('.gantt_grid .gantt_row_task')];
    const e=rs[2]||rs[0]; if(!e) return null; const r=e.getBoundingClientRect();
    return {x:r.left+r.width/2,y:r.top+r.height/2,txt:(e.innerText||'').replace(/\\s+/g,' ').trim().slice(0,30)}})()`)
  let gridTip = null
  if (gridRect) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: gridRect.x, y: gridRect.y })
    await sleep(1200)
    gridTip = await safe(`(()=>{const t=document.querySelector('.gantt_tooltip');
      return t?{text:(t.innerText||'').replace(/\\s+/g,' ').trim().slice(0,120),visible:t.getBoundingClientRect().height>0}:null})()`)
  }
  judge('③b 左侧行名列不弹悬浮提示（右侧任务条照常弹）',
    !!gridRect && (!gridTip || !gridTip.visible),
    gridRect
      ? `行=${JSON.stringify(gridRect.txt)} 左侧提示=${gridTip ? 'visible=' + gridTip.visible : '无提示元素（已隐藏/未创建）'}`
      : '没找到左侧行名列')

  // ③c 资源泳道标题不出现「编码 | 编码」这种重复段（2026-11-03 用户截图反馈）
  //   dev 种子数据里 resourceName === resourceCode ⇒ 拼接时必须去重
  const laneLabels = await safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const ss=root&&root.__vueParentComponent&&root.__vueParentComponent.setupState;
    const lanes=ss&&Array.isArray(ss.lanes)?ss.lanes.slice(0,20):[];
    return lanes.map(l=>String(l.label||''))})()`)
  const dupLabels = (Array.isArray(laneLabels) ? laneLabels : [])
    .map((s) => s.split('|').map((x) => x.trim()))
    .filter((segs) => segs.some((v, i) => i > 0 && v && v === segs[i - 1]))
  judge('③c 资源泳道标题不出现重复段（编码=名称时只显示一段）',
    Array.isArray(laneLabels) && laneLabels.length > 0 && dupLabels.length === 0,
    Array.isArray(laneLabels)
      ? `取样 ${laneLabels.length} 条，重复的=${JSON.stringify(dupLabels.slice(0, 3))} 首条=${JSON.stringify(laneLabels[0])}`
      : '没取到泳道标题')

  // ⑥ KPI 行不再显示任务数（同屏不重复）+ 任务清单页签条数＝后端窗口任务数（2026-11-04 二改）
  //   用户截图圈出「任务 3334（… 窗口内） · 版本共 13201 条（整个计划区间）」要求去掉：
  //     「任务 N」与「任务清单」页签徽标是同一个数（同屏重复）；
  //     「版本共 13201」是整个计划区间的口径，摆在按窗口看的工具栏只会让人对不上。
  //   改判：KPI 行**不得**出现任务数/版本总数；任务数改由「任务清单」页签徽标显示，
  //        且（未加筛选时）必须等于后端窗口任务总数 —— 证明这个数不是前端自己编的。
  const kpi = await safe(`(()=>{
    const root=document.querySelector('.aps-gantt-page');
    const ss=root&&root.__vueParentComponent&&root.__vueParentComponent.setupState;
    const st=document.querySelector('.view-bar-right .stat');
    const tab=[...document.querySelectorAll('.view-tab')].find(e=>(e.innerText||'').includes('任务清单'));
    const badge=tab?tab.querySelector('.view-tab-count'):null;
    const dr=ss&&ss.dateRange?{from:ss.dateRange.from,to:ss.dateRange.to}:null;
    return {
      statText: st?(st.innerText||'').replace(/\\s+/g,' ').trim():null,
      badgeText: badge?(badge.innerText||'').trim():null,
      taskListTotal: ss&&ss.taskListTotal!= null?ss.taskListTotal:null,
      filteredCount: ss&&ss.filteredTasks?ss.filteredTasks.length:null,
      keyword: ss&&ss.keyword?String(ss.keyword).trim():'',
      onlyDelayed: ss&&ss.onlyDelayed===true,
      hasMore: ss&&ss.hasMoreInWindow===true,
      currentVersionId: ss&&ss.currentVersionId!= null?ss.currentVersionId:null,
      dateRange: dr}})()`)
  let kpiTruth = null
  try {
    const token = await apiToken()
    let win = null
    if (kpi?.dateRange?.from && kpi?.dateRange?.to && kpi?.currentVersionId != null) {
      const q = `from=${encodeURIComponent(kpi.dateRange.from)}&to=${encodeURIComponent(kpi.dateRange.to)}`
      win = await apiGet(token, `/api/schedule/gantt/${kpi.currentVersionId}?${q}`)
    }
    kpiTruth = {
      backendWindowTotal: win ? win.totalTasks ?? null : null,
      backendTaskCount: win && Array.isArray(win.tasks) ? win.tasks.length : null
    }
  } catch (e) {
    kpiTruth = { error: String(e.message).slice(0, 120) }
  }
  // KPI 行不得再有任务数 / 版本总数
  const kpiNoTaskCount = !!kpi?.statText && !/任务\s*\d/.test(kpi.statText)
  const kpiNoVersionTotal = !!kpi?.statText && !/版本共/.test(kpi.statText)
  // 未加筛选时，任务清单条数必须＝后端窗口任务总数
  const noFilter = !kpi?.keyword && kpi?.onlyDelayed !== true
  const badgeNum = (() => {
    const m = /\d+/.exec(kpi?.badgeText || '')
    return m ? Number(m[0]) : null
  })()
  const listMatchesBackend =
    noFilter &&
    badgeNum != null &&
    kpi?.taskListTotal === badgeNum &&
    (kpiTruth?.backendWindowTotal == null || badgeNum === kpiTruth.backendWindowTotal)
  judge('⑥ KPI 行无任务数/版本总数（同屏不重复），任务数只在任务清单页签且等于后端窗口任务数',
    !!(kpiNoTaskCount && kpiNoVersionTotal && listMatchesBackend),
    `KPI=${JSON.stringify(kpi?.statText)} | 页签徽标=${kpi?.badgeText}` +
      ` | 页面 清单=${kpi?.taskListTotal}/生效=${kpi?.filteredCount}` +
      ` | 后端 窗口=${kpiTruth?.backendWindowTotal}/返回条数=${kpiTruth?.backendTaskCount}` +
      ` | 无任务数=${kpiNoTaskCount} 无版本总数=${kpiNoVersionTotal} 清单对上后端=${listMatchesBackend}` +
      (kpiTruth?.error ? ` | 取真值失败=${kpiTruth.error}` : ''))

  const shotTip = await shot('02-tooltip')

  const shotGantt = await shot('01-gantt')
  const toolbarRect = await rectOf('.gantt-toolbar') || await rectOf('.view-bar')
  if (toolbarRect) await shot('01b-toolbar', toolbarRect)

  // ④ 切「小时」刻度
  const zoomInfo = await safe(`(()=>{
    const btns=[...document.querySelectorAll('button')].filter(b=>/刻度|小时|天|周|月/.test(b.innerText||''));
    return {n:btns.length, texts:btns.slice(0,10).map(b=>(b.innerText||'').trim())}})()`)
  console.log('刻度相关按钮：' + JSON.stringify(zoomInfo))
  // 刻度下拉是 ElSelect：点开再选「小时」
  const sel = await safe(`(()=>{const s=document.querySelector('.zoom-select');
    if(!s) return null; const r=s.getBoundingClientRect();
    return {x:r.left+r.width/2,y:r.top+r.height/2,text:(s.innerText||'').trim(),
      html:s.outerHTML.slice(0,200)}})()`)
  console.log('刻度下拉：' + JSON.stringify(sel))
  let zoomed = null
  if (sel) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: sel.x, y: sel.y })
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: sel.x, y: sel.y, button: 'left', clickCount: 1 })
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: sel.x, y: sel.y, button: 'left', clickCount: 1 })
    await sleep(1200)
    const opt = await safe(`(()=>{const os=[...document.querySelectorAll('.el-select-dropdown__item')];
      const h=os.find(o=>(o.innerText||'').trim()==='小时');
      return {n:os.length, texts:os.map(o=>(o.innerText||'').trim()),
        hit:h?{x:(r=>r.left+r.width/2)(h.getBoundingClientRect()),y:(r=>r.top+r.height/2)(h.getBoundingClientRect())}:null}})()`)
    console.log('下拉项：' + JSON.stringify(opt))
    if (opt && opt.hit) {
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: opt.hit.x, y: opt.hit.y })
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: opt.hit.x, y: opt.hit.y, button: 'left', clickCount: 1 })
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: opt.hit.x, y: opt.hit.y, button: 'left', clickCount: 1 })
      await sleep(4000)
      zoomed = await safe(`(()=>{const hs=[...document.querySelectorAll('.gantt_scale_cell')].map(e=>(e.innerText||'').trim());
        const lines=[...document.querySelectorAll('.gantt_task_line')];
        const ws=lines.map(e=>e.getBoundingClientRect().width).sort((a,b)=>a-b);
        const labels=[...document.querySelectorAll('.dhx-bar-label')].map(e=>e.textContent.trim());
        const wideTaskBars=lines.filter(e=>!e.classList.contains('dhx-task-lane')
          && e.getBoundingClientRect().width>=56).length;
        const rows=document.querySelectorAll('.gantt_row').length;
        return {scale:hs.slice(0,8), hasHour:hs.some(t=>/\\d{1,2}:\\d{2}/.test(t)),
          lineCount:lines.length, rows, minW:ws[0],p50:ws[Math.floor(ws.length/2)],max:ws[ws.length-1],
          labels:labels.length, wideTaskBars, labelSample:labels.slice(0,3),
          sampleHtml:lines.slice(0,2).map(e=>e.outerHTML.slice(0,220)),
          sampleStyle:lines.slice(0,2).map(e=>e.getAttribute('style'))}})()`)
    }
  }
  judge('④ 切到「小时」刻度后轴上出现小时列', !!zoomed && zoomed.hasHour,
    zoomed ? JSON.stringify(zoomed).slice(0, 600) : '没切成功')
  // ② 条内任务号（小时刻度下宽条应写字）
  // ⚠️ 判据必须**随数据走**：条内文字有 56px 门槛（BAR_LABEL_MIN_WIDTH），
  //   dev 的任务多为 1 小时。2026-11-05 起小时档窗口=1 天，1 小时条约 56px（临界），
  //   视口窄时仍可能一条宽条都没有 ⇒ 「0 个带号」是正确的。
  //   只有「存在 ≥56px 的任务条却没写字」才是缺陷（泳道条 type=project 不写字，不算）。
  judge('② 条内任务号（小时刻度下宽条应写字）',
    !!zoomed && (zoomed.labels > 0 || zoomed.wideTaskBars === 0),
    zoomed
      ? `带号条=${zoomed.labels} 宽任务条(≥56px)=${zoomed.wideTaskBars} 样例=${JSON.stringify(zoomed.labelSample)}`
      : '没切成功刻度，无法验')
  const shotZoom = await shot('03-zoom-hour')

  // ⑤ 资源负荷页签
  const tab = await safe(`(()=>{const all=[...document.querySelectorAll('.view-tab')];
    const b=all.find(x=>(x.innerText||'').includes('资源负荷'));
    if(!b) return {none:true, tabs:all.map(x=>(x.innerText||'').trim()),
      hasText:(document.body.innerText||'').includes('资源负荷')};
    const r=b.getBoundingClientRect();
    return {x:r.left+r.width/2,y:r.top+r.height/2,text:(b.innerText||'').trim()}})()`)
  console.log('页签：' + JSON.stringify(tab))
  let heat = null
  if (tab) {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: tab.x, y: tab.y })
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: tab.x, y: tab.y, button: 'left', clickCount: 1 })
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: tab.x, y: tab.y, button: 'left', clickCount: 1 })
    await sleep(3000)
    heat = await safe(`(()=>{const g=document.querySelector('.load-grid');
      const pane=document.querySelector('.load-pane');
      if(!g) return {empty:true, html:(pane?pane.innerText:'').slice(0,200)};
      const cells=g.querySelectorAll('.load-cell').length;
      const labels=g.querySelectorAll('.load-row-label').length;
      const vals=g.querySelectorAll('.load-value').length;
      return {cells,labels,vals,note:(document.querySelector('.load-note')||{}).innerText||''}})()`)
  }
  judge('⑤ 资源负荷页签渲染热力图', !!heat && !heat.empty && heat.cells > 0 && heat.labels > 0,
    heat ? `格=${heat.cells} 资源行=${heat.labels} 值格=${heat.vals} 说明=${JSON.stringify(heat.note || heat.html)}` : '没渲染出热力图')
  const shotLoad = await shot('04-load')

  // ⑦ 同屏任务数不重复（2026-11-04 用户截图反馈：勾「只看延期」后「剩余 2431」与「其他 2431」并排打架）
  //   收敛后的期望：
  //     ① 任务清单 Tab 徽标 == 当前生效条数（filteredTasks）——清单必须跟着筛选走；
  //     ② 勾上「只看延期」后生效条数收窄，且剩下的**全部**是延期任务（筛选真的生效）；
  //     ③ 工具栏不再出现「剩余 N 条」（它与域计数之和恒等 ⇒ 纯重复）；
  //     ④ 单域时不显示域分项（它恒等于清单条数 ⇒ 并排两个一样的数）；多域时各域之和 == 生效条数。
  const readCounts = () => safe(`(()=>{
    const root=document.querySelector('.aps-gantt-page');
    const ss=root&&root.__vueParentComponent&&root.__vueParentComponent.setupState;
    const tab=[...document.querySelectorAll('.view-tab')].find(e=>(e.innerText||'').includes('任务清单'));
    const badge=tab?tab.querySelector('.view-tab-count'):null;
    const bar=document.querySelector('.gantt-toolbar')||document.querySelector('.view-bar');
    const ft=(ss&&ss.filteredTasks)?ss.filteredTasks:[];
    const delayed=ft.filter(t=>t.isDelayed===true).length;
    const doms=[...document.querySelectorAll('.stat-domain')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
    return {
      badge: badge?(badge.innerText||'').trim():null,
      badgeTitle: badge?(badge.getAttribute('title')||''):null,
      effective: ft.length, delayedInEffective: delayed,
      loaded: (ss&&ss.ganttData&&ss.ganttData.tasks)?ss.ganttData.tasks.length:null,
      onlyDelayed: ss&&ss.onlyDelayed===true,
      domainCount: (ss&&ss.availableDomains)?ss.availableDomains.length:null,
      domTexts: doms,
      toolbarText: bar?(bar.innerText||'').replace(/\\s+/g,' '):null
    }})()`)
  const before = await readCounts()
  const clicked = await safe(`(()=>{
    const lb=[...document.querySelectorAll('label')].find(l=>(l.innerText||'').includes('只看延期'));
    if(!lb) return {none:true};
    const el=lb.querySelector('input')||lb; el.click(); return {clicked:true};})()`)
  await sleep(3500)
  const after = await readCounts()
  const shotDelayed = await shot('05-only-delayed')
  const bNum = (s) => (s && /\d+/.test(s) ? Number(/\d+/.exec(s)[0]) : null)
  const badgeBefore = bNum(before?.badge)
  const badgeAfter = bNum(after?.badge)
  const listFollowsFilter = badgeAfter != null && badgeAfter === after?.effective
  const filterNarrowed =
    after?.onlyDelayed === true &&
    after?.effective > 0 &&
    after?.effective < (after?.loaded ?? Infinity) &&
    after?.delayedInEffective === after?.effective
  const noRemainHint = !!after?.toolbarText && !/剩余\s*\d+\s*条/.test(after.toolbarText)
  const domainPartOk =
    after?.domainCount != null &&
    ((after.domainCount <= 1 && after.domTexts.length === 0) ||
      (after.domainCount > 1 &&
        after.domTexts.length === after.domainCount &&
        after.domTexts.reduce((s, t) => s + (Number(/\d+/.exec(t)?.[0] ?? 0)), 0) === after.effective))
  judge('⑦ 勾「只看延期」后：清单徽标=生效条数且全为延期任务 + 工具栏无「剩余 N 条」 + 域分项不与清单重复',
    !!(listFollowsFilter && filterNarrowed && noRemainHint && domainPartOk),
    `前 徽标=${before?.badge}(${badgeBefore})/生效=${before?.effective} | 后 徽标=${after?.badge}(${badgeAfter})/生效=${after?.effective}` +
      `/其中延期=${after?.delayedInEffective}/已加载=${after?.loaded}/onlyDelayed=${after?.onlyDelayed}` +
      ` | 域数=${after?.domainCount} 域分项=${JSON.stringify(after?.domTexts)}` +
      ` | 清单跟随=${listFollowsFilter} 收窄=${filterNarrowed} 无剩余=${noRemainHint} 域分项=${domainPartOk}` +
      (clicked?.none ? ' | 没找到「只看延期」复选框' : ''))

  // ⑧ 切到「任务排在窗口之外」的版本（TZP：96 条排在 10-30~11-02，而默认窗是今天起 7 天）
  //    ⇒ 窗口内必然 0 条；页面必须**说清任务在哪儿**并给一键跳转，点了之后清单徽标要等于后端真值。
  //    （11-04 用户反馈「TZP 在甘特图上没有任务，总览里却有 96 条」）
  const token = await apiToken()
  const versions = await apiGet(token, '/api/schedule/versions?take=50')
  const vlist = Array.isArray(versions) ? versions : (versions?.items ?? [])
  const tzp = vlist.find((v) => String(v.versionCode ?? '').includes('TZP'))
  if (!tzp) {
    judge('⑧ 空窗版本（TZP）提示 + 一键跳转', false, '版本列表里没找到 TZP')
  } else {
    const tzpId = tzp.id
    const tzpTotal = tzp.totalTasks
    // ⚠️ ⑦ 勾上的「只看延期」要撤掉：TZP 的 96 条**都不是延期任务** ⇒ 留着会让徽标恒 0，
    //    那是筛选生效的正常结果，不是本条要验的东西（别把筛选残留当成缺陷）。
    await evaluate(`(()=>{const ss=document.querySelector('.aps-gantt-page').__vueParentComponent.setupState;
      ss.onlyDelayed=false; ss.operationFilter=[]; ss.keyword=''; return 1})()`)
    await sleep(1500)
    await evaluate(`(()=>{const ss=document.querySelector('.aps-gantt-page').__vueParentComponent.setupState;
      ss.selectedVersionId=${tzpId}; return 1})()`)
    await sleep(9000)
    const emptyState = await safe(`(()=>{
      const ss=document.querySelector('.aps-gantt-page').__vueParentComponent.setupState;
      const note=document.querySelector('.view-bar-note');
      const btn=[...document.querySelectorAll('.view-bar-note button')].find(b=>/跳到/.test(b.innerText||''));
      return {loaded:(ss.ganttData&&ss.ganttData.tasks||[]).length, effective:ss.filteredTasks.length,
        versionTotalTasks: ss.versionTotalTasks, span: ss.taskSpanText, windowLabel: ss.windowLabel,
        note:(note?(note.innerText||''):'').replace(/\\s+/g,' ').trim(), hasBtn: !!btn}})()`)
    const shotEmpty = await shot('06-empty-window-hint')
    const noteOk =
      !!emptyState?.note &&
      /条任务排在/.test(emptyState.note) &&
      /不在当前时间窗/.test(emptyState.note)
    judge('⑧-1 空窗时提示「该版本 N 条任务排在 <真实区间>，不在当前时间窗」并给出跳转按钮',
      !!(noteOk && emptyState?.hasBtn),
      `note=${JSON.stringify(emptyState?.note)} 按钮=${emptyState?.hasBtn} 区间=${emptyState?.span} 窗口=${emptyState?.windowLabel}`)

    const btnRect = await safe(`(()=>{const b=[...document.querySelectorAll('.view-bar-note button')].find(x=>/跳到/.test(x.innerText||''));
      if(!b) return null; const r=b.getBoundingClientRect();
      return {x:r.left+r.width/2,y:r.top+r.height/2}})()`)
    let afterJump = null
    if (btnRect) {
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: btnRect.x, y: btnRect.y })
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: btnRect.x, y: btnRect.y, button: 'left', clickCount: 1 })
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: btnRect.x, y: btnRect.y, button: 'left', clickCount: 1 })
      await sleep(10000)
      afterJump = await safe(`(()=>{
        const ss=document.querySelector('.aps-gantt-page').__vueParentComponent.setupState;
        const tab=[...document.querySelectorAll('.view-tab')].find(e=>(e.innerText||'').includes('任务清单'));
        const badge=tab&&tab.querySelector('.view-tab-count');
        return {loaded:(ss.ganttData&&ss.ganttData.tasks||[]).length, effective:ss.filteredTasks.length,
          badge: badge?(badge.innerText||'').trim():null, windowLabel: ss.windowLabel}})()`)
      await shot('07-after-jump-to-task-span')
    }
    const jumpBadge = bNum(afterJump?.badge)
    judge('⑧-2 点「跳到任务所在时间」后：窗口挪到任务区间，清单徽标 = 后端版本任务总数',
      !!(afterJump && jumpBadge === tzpTotal && afterJump.effective === tzpTotal),
      `徽标=${afterJump?.badge}(${jumpBadge}) 生效=${afterJump?.effective} 已加载=${afterJump?.loaded} 后端总数=${tzpTotal} 窗口=${afterJump?.windowLabel}`)
  }

  console.log('\nAPI 调用：')
  const seen = new Set()
  for (const n of netLog) {
    const k = n.url + n.status
    if (seen.has(k)) continue
    seen.add(k)
    console.log(`  ${n.status}  ${n.url.replace(BASE, '')}`)
  }
  console.log('\n截图：\n  ' + [shotGantt, shotTip, shotZoom, shotLoad, shotDelayed].join('\n  '))
  if (consoleLog.length) console.log('\n控制台（前 10 条）：\n  ' + consoleLog.slice(0, 10).join('\n  '))

  const failed = results.filter((r) => !r.ok)
  console.log(`\n判定：${results.length - failed.length}/${results.length} 通过`)
  if (failed.length) process.exitCode = 1
}

main()
  .catch((e) => { console.error('ERR', e.stack || e.message); process.exitCode = 1 })
  .finally(async () => {
    clearTimeout(watchdog)
    try { ws && ws.close() } catch {}
    killChrome()
    setTimeout(() => process.exit(process.exitCode || 0), 500)
  })
