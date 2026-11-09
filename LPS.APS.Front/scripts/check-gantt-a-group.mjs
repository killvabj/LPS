#!/usr/bin/env node
/**
 * 甘特页 A 组三项实看（2026-11-03）
 *
 * 为什么要有这个脚本：静态探针只能证明代码写了，**证明不了浏览器里那三个新能力真的能用**。
 *   已知教训：「有数据 ≠ 路径走过」——列表有数据不代表那条路径被点过。
 *
 * 它做什么（走真实登录表单 → 真实页面 → 真实点击）：
 *   ① A1 按订单 / 按物料分组：切「维度」下拉 ⇒ 泳道行标签应变成订单号 / 物料名
 *      （**不能只看条子还在不在** —— 条子在不维度切错也可能照样渲染 ⇒ 必须看泳道文案）
 *   ② A3 只看延期 / 只看工序：点复选框、选工序 ⇒ 工具栏「剩余 N 条」计数应下降
 *   ③ A4 任务号标签：条宽撑不下 ⇒ `.dhx-tail-label` 在条右侧显示**完整**任务号；
 *      条宽撑得下 ⇒ 只显示条内 `.dhx-bar-label`（两处互斥，每条只出现一次）
 *
 * 用法：
 *   node --experimental-websocket scripts/check-gantt-a-group.mjs
 *   node --experimental-websocket scripts/check-gantt-a-group.mjs --url http://localhost:4001
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务（既定约束：不启动 dev server）
 *   - 登录一次即可（dev 登录限流 5 次/分钟）
 *   - ⚠️ 必须 --no-proxy-server：本机系统代理会把 Chrome 绕到**已部署那套** ⇒ 假绿
 *     （见记忆 feedback_e2e_proxy_misroute）；并加「页面脚本来自本机 dev」的守卫
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
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/gantt-a'))
const CDP_PORT = 9300 + Math.floor(Math.random() * 90)
const USER = process.env.GANTT_USER || 'admin'
const PWD = process.env.GANTT_PWD || 'Admin@123456'

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
const profileDir = join(os.tmpdir(), 'cdp-gantt-a-' + Date.now())
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1920,1080',
  // ⚠️ 必须：不加会被本机系统代理绕到「已部署那套」⇒ 实看的不是本地代码（假绿）
  '--no-proxy-server',
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

const results = []
function judge(name, ok, detail) {
  results.push({ name, ok, detail })
  console.log(`${ok ? '✅' : '❌'} ${name} — ${detail}`)
}

/** 打开某个 Element Plus 下拉并选中文字匹配的选项（真实点击）。 */
async function pickSelect(selectExpr, optionText) {
  const opened = await safe(`(()=>{const s=${selectExpr}; if(!s) return false;
    const w=s.querySelector('.el-select__wrapper')||s; w.click(); return true})()`)
  if (!opened) return false
  for (let i = 0; i < 40; i++) {
    await sleep(200)
    const hit = await safe(`(()=>{const items=[...document.querySelectorAll('.el-select-dropdown__item')];
      const it=items.find(e=>(e.innerText||'').trim()===${JSON.stringify(optionText)});
      if(!it) return false; it.click(); return true})()`)
    if (hit) {
      await sleep(1500)
      return true
    }
  }
  return false
}

/** 只打开某个 Element Plus 下拉（返回选项文字清单）。 */
async function openSelect(selectExpr) {
  const opened = await safe(`(()=>{const s=${selectExpr}; if(!s) return false;
    const w=s.querySelector('.el-select__wrapper')||s; w.click(); return true})()`)
  if (!opened) return null
  for (let i = 0; i < 40; i++) {
    await sleep(200)
    const items = await safe(`(()=>{const it=[...document.querySelectorAll('.el-select-dropdown__item')];
      return it.length?it.map(e=>(e.innerText||'').trim()):null})()`)
    if (Array.isArray(items) && items.length) return items
  }
  return null
}

/** 在已打开的下拉里点中指定文字的选项。 */
async function clickOption(optionText) {
  const hit = await safe(`(()=>{const items=[...document.querySelectorAll('.el-select-dropdown__item')];
    const it=items.find(e=>(e.innerText||'').trim()===${JSON.stringify(optionText)});
    if(!it) return false; it.click(); return true})()`)
  await sleep(1500)
  return hit
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

  // 守卫：页面脚本必须来自**本机 dev**（防系统代理绕到已部署那套 ⇒ 假绿）
  const srcGuard = await safe(`(()=>{const s=[...document.querySelectorAll('script[src]')].map(e=>e.src);
    return {origin:location.origin, scripts:s.slice(0,5), allLocal:s.every(u=>u.startsWith(location.origin))}})()`)
  judge('守卫：页面脚本来自本机 dev（未被系统代理绕到已部署那套）',
    !!srcGuard && srcGuard.origin === BASE && srcGuard.allLocal,
    `origin=${srcGuard?.origin} scripts=${JSON.stringify(srcGuard?.scripts)}`)

  /* ---------- 通用取数 ---------- */
  const tailTrace = []
  const rawSnapshot = () => safe(`(()=>{
    const grid=[...document.querySelectorAll('#gantt_grid .gantt_row, .gantt_grid .gantt_row')];
    const laneRows=[...document.querySelectorAll('.gantt_task_line.dhx-task-lane')];
    const tail=[...document.querySelectorAll('.dhx-tail-label')];
    const hints=[...document.querySelectorAll('.search-hint')].map(e=>(e.innerText||'').trim()).filter(Boolean);
    return {
      laneRows: laneRows.length,
      gridRows: grid.length,
      gridTexts: grid.slice(0,8).map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim()),
      bars: document.querySelectorAll('.gantt_task_line').length,
      taskBars: document.querySelectorAll('.gantt_task_line:not(.dhx-task-lane)').length,
      tails: tail.length,
      tailSamples: tail.slice(0,4).map(e=>(e.textContent||'').trim()),
      barLabels: document.querySelectorAll('.dhx-bar-label').length,
      hints,
      dim: (()=>{const l=[...document.querySelectorAll('span.label')].find(s=>(s.innerText||'').trim()==='维度');
        const s=l&&l.nextElementSibling; return s?(s.innerText||'').replace(/\\s+/g,' ').trim():null})()
    }})()`)

  /** 取数并记一条尾标签轨迹（用来定位「尾标签在哪一步消失」） */
  const snapshot = async (step) => {
    const r = await rawSnapshot()
    if (r && !r.__err) {
      tailTrace.push({ step, tails: r.tails, bars: r.bars, taskBars: r.taskBars, hints: r.hints })
    }
    return r
  }

  const base = await snapshot('基线')
  console.log('基线：' + JSON.stringify(base))

  // ---- 诊断：控件结构 + 应用内状态（DOM 里的条数是虚拟渲染的，不能当总数）----
  const diag = await safe(`(()=>{
    const boxes=[...document.querySelectorAll('input[type=checkbox]')].map(e=>({
      cls:e.className, p:e.parentElement?.className, pp:e.parentElement?.parentElement?.className,
      txt:(e.parentElement?.parentElement?.innerText||'').trim()
    }));
    const sels=[...document.querySelectorAll('.el-select')].map(s=>({
      aria:s.getAttribute('aria-label'), ph:(s.querySelector('.el-select__placeholder')||{}).innerText||'',
      txt:(s.innerText||'').replace(/\\s+/g,' ').trim().slice(0,40)
    }));
    const root=document.querySelector('.aps-gantt-page');
    let st=null;
    try{ const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
      st= ss?{lanes:Array.isArray(ss.lanes)?ss.lanes.length:null,
               tasks:Array.isArray(ss.filteredTasks)?ss.filteredTasks.length:null,
               groupBy:String(ss.groupBy), onlyDelayed:String(ss.onlyDelayed),
               ops:Array.isArray(ss.operationFilter)?ss.operationFilter.length:null}:null }catch(e){st={err:String(e.message)}}
    return {boxes, sels, st}})()`)
  console.log('诊断：' + JSON.stringify(diag))

  /* ---------- ① A1 按订单 / 按物料分组 ---------- */
  const dimExpr = `(()=>{const l=[...document.querySelectorAll('span.label')].find(s=>(s.innerText||'').trim()==='维度');
    return l?l.nextElementSibling:null})()`
  // 维度下拉的**选项清单**（页面上新增了哪两项，一眼可见）
  // ⚠️ 取法要点：Element Plus 的 popper 是 teleport 到 body 的，多个下拉的选项都同时留在 DOM 里，
  //   直接全局查 .el-select-dropdown__item 会把别的下拉的选项混进来 ⇒ 必须先定位**可见的那个 popper**。
  const dimOptions = await (async () => {
    await safe(`(()=>{const l=[...document.querySelectorAll('span.label')].find(s=>(s.innerText||'').trim()==='维度');
      const s=l&&l.nextElementSibling; if(!s) return 0; (s.querySelector('.el-select__wrapper')||s).click(); return 1})()`)
    await sleep(900)
    const opts = await safe(`(()=>{
      const pops=[...document.querySelectorAll('.el-select-dropdown')];
      const vis=pops.filter(p=>p.getBoundingClientRect().height>0);
      const p=vis[vis.length-1]||pops[pops.length-1];
      if(!p) return null;
      return [...p.querySelectorAll('.el-select-dropdown__item')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
    })()`)
    await shot('00-dim-dropdown')
    // 关掉下拉（点空白 + Esc），避免影响后面 pickSelect 重新打开
    await safe(`(()=>{document.body.click();
      document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); return 1})()`)
    await sleep(600)
    return opts
  })()
  console.log('维度下拉选项：' + JSON.stringify(dimOptions))
  judge('① A1 维度下拉里新增了「按订单」「按物料」两个视角（共 7 项）',
    Array.isArray(dimOptions) && dimOptions.includes('按订单') && dimOptions.includes('按物料')
      && dimOptions.length === 7,
    `选项=${JSON.stringify(dimOptions)}`)

  // 裁剪区域：工具栏（含「维度」下拉）+ 甘特左侧行名列（放大 2 倍才看得清文案）
  const CLIP = { x: 220, y: 150, width: 1000, height: 620 }
  await shot('01-by-resource', CLIP)

  const pickedOrder = await pickSelect(dimExpr, '按订单')
  await sleep(2500)
  const byOrder = await snapshot('按订单')
  console.log('按订单：' + JSON.stringify(byOrder))
  await shot('02-by-order', CLIP)

  const pickedMaterial = await pickSelect(dimExpr, '按物料')
  await sleep(2500)
  const byMaterial = await snapshot('按物料')
  console.log('按物料：' + JSON.stringify(byMaterial))
  await shot('03-by-material', CLIP)

  // 判据：泳道行文案必须真的变了（订单号 / 物料名），而不是只换了条子
  const orderLabel = byOrder.gridTexts.find((t) => /（\d+ 条）/.test(t)) || ''
  const materialLabel = byMaterial.gridTexts.find((t) => /（[^）]*，\d+ 条）/.test(t)) || ''
  judge('① A1 按订单分组：泳道行变成订单号文案',
    pickedOrder && orderLabel.length > 0 && !/资源/.test(orderLabel),
    `维度=${byOrder.dim} 泳道文案=${JSON.stringify(orderLabel)} 泳道行=${byOrder.laneRows}`)
  judge('① A1 按物料分组：泳道行变成物料名/编码文案',
    pickedMaterial && materialLabel.length > 0 && /，/.test(materialLabel),
    `维度=${byMaterial.dim} 泳道文案=${JSON.stringify(materialLabel)} 泳道行=${byMaterial.laneRows}`)

  // 回到按资源（下面 A3 的基线）
  await pickSelect(dimExpr, '按资源')
  await sleep(2500)

  /* ---------- ② A3 只看延期 / 只看工序 ---------- */
  // ⚠️ 计数口径：DOM 里的条数是**虚拟渲染**的（只看得到十几行），
  //   拿它当总数会把「1934 条」比成「15 条」⇒ 必须取应用内 filteredTasks 的真值，
  //   再用工具栏「剩余 N 条」交叉核对用户看到的数字。
  const appState = () => safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
    if(!ss) return null;
    return {lanes:Array.isArray(ss.lanes)?ss.lanes.length:null,
            tasks:Array.isArray(ss.filteredTasks)?ss.filteredTasks.length:null,
            delayed:Array.isArray(ss.ganttData?.tasks)?ss.ganttData.tasks.filter(t=>t.isDelayed).length:null,
            groupBy:String(ss.groupBy), onlyDelayed:String(ss.onlyDelayed),
            ops:Array.isArray(ss.operationFilter)?ss.operationFilter.slice():null}})()`)
  const st0 = await appState()
  const total = st0?.tasks ?? null
  console.log('筛选前应用态：' + JSON.stringify(st0))

  // 点「只看延期」
  const checked = await safe(`(()=>{const c=document.querySelector('.gantt-filter-check');
    if(!c) return false; const i=c.querySelector('input'); if(!i) return false; i.click(); return true})()`)
  await sleep(2500)
  const afterDelayed = await snapshot('只看延期')
  const stDelayed = await appState()
  const delayedCount = stDelayed?.tasks ?? null
  const backendDelayed = stDelayed?.delayed ?? null
  console.log('只看延期：' + JSON.stringify({ total, delayedCount, backendDelayed }))

  judge('② A3 只看延期：勾上后应用内条数 = 后端判定延期的任务数（纯前端收敛）',
    checked && stDelayed?.onlyDelayed === 'true' && delayedCount !== null && backendDelayed !== null
      && delayedCount === backendDelayed,
    `勾选=${checked} 总数=${total} 延期后=${delayedCount} 后端标记延期=${backendDelayed}`)

  // 选一道工序（AND 关系 ⇒ 应再降）
  const opItems = await openSelect(
    `document.querySelector('.el-select[aria-label="工序筛选"]')||[...document.querySelectorAll('.el-select')].find(s=>(s.innerText||'').includes('全部工序'))`
  )
  const firstOp = Array.isArray(opItems) ? opItems.find((t) => /（\d+ 条）/.test(t)) : null
  let opPicked = false
  if (firstOp) {
    opPicked = await clickOption(firstOp)
    await sleep(2500)
  }
  const afterOp = await snapshot('只看延期+工序')
  const stOp = await appState()
  const opCount = stOp?.tasks ?? null
  console.log('只看延期+工序：' + JSON.stringify({ opCount, ops: stOp?.ops, hints: afterOp.hints }))

  judge('② A3 只看工序：与「只看延期」AND ⇒ 真值条数再降',
    opPicked && Array.isArray(stOp?.ops) && stOp.ops.length === 1 && opCount !== null
      && delayedCount !== null && opCount < delayedCount,
    `选工序=${opPicked} 工序=${JSON.stringify(stOp?.ops)} 延期后=${delayedCount} 再降后=${opCount}`)

  // 取消两个筛选 ⇒ 必须回到原总数（筛选是纯前端收敛，不改数据）
  await safe(`document.querySelector('.gantt-filter-check input')?.click()`)
  await sleep(1200)
  await safe(`(()=>{const s=document.querySelector('.el-select[aria-label="工序筛选"]');
    if(!s) return 0; const w=s.querySelector('.el-select__wrapper')||s; w.click(); return 1})()`)
  await sleep(800)
  await safe(`(()=>{const items=[...document.querySelectorAll('.el-select-dropdown__item')];
    const on=items.filter(e=>e.classList.contains('is-selected'));
    on.forEach(e=>e.click()); return on.length})()`)
  await sleep(2000)
  const stRestored = await appState()
  judge('② A3 取消两个筛选后回到原总数（纯前端收敛，不改数据）',
    stRestored?.tasks === total && stRestored?.onlyDelayed === 'false'
      && Array.isArray(stRestored?.ops) && stRestored.ops.length === 0,
    `还原后=${JSON.stringify(stRestored)} 原总数=${total}`)

  /* ---------- ③ A4 条尾任务号标签 ---------- */
  // ⚠️ 必须在**有任务行可见**的状态下量：尾标签挂在任务行上，
  //   过滤到只剩几条时任务行会被虚拟渲染挤出视口 ⇒ 那时量到 0 是正常现象，不是缺陷。
  await evaluate(`(()=>{const sc=document.querySelector('.gantt_ver_scroll'); if(sc) sc.scrollTop=0; return 1})()`)
  await sleep(2000)
  const tailInfo = await snapshot('尾标签')
  const tails = tailInfo.tailSamples
  // 每个可见任务条只出现一次任务号：条内撑得下 ⇒ .dhx-bar-label；撑不下 ⇒ .dhx-tail-label
  judge('③ A4 任务号每条只出现一次（条内已显示时条尾不再显示）',
    tailInfo.tails > 0 &&
      tailInfo.tails + tailInfo.barLabels === tailInfo.taskBars &&
      tails.length > 0,
    `条内=${tailInfo.barLabels} 条尾=${tailInfo.tails} 可见任务行=${tailInfo.taskBars} 样本=${JSON.stringify(tails)}`)
  // dev 任务号是 28 字符（PEGG-…）；条尾标签必须完整显示，不能是尾 8 位
  judge('③ A4 条尾标签内容 = 完整任务号（不再截尾 8 位）',
    tails.length > 0 && tails.every((t) => t.length > 8),
    `样本长度=${tails.map((t) => t.length).join(',')} 样本=${JSON.stringify(tails)}`)

  /* ---------- ③b 用户会切到「小时」看细节 ⇒ 同一标签规则必须成立 ---------- */
  const hourPicked = await pickSelect(
    `[...document.querySelectorAll('.el-select')].find(s=>(s.innerText||'').trim()==='天')`,
    '小时'
  )
  await sleep(2500)
  await evaluate(`(()=>{const sc=document.querySelector('.gantt_ver_scroll'); if(sc) sc.scrollTop=0; return 1})()`)
  await sleep(1500)
  const hourInfo = await snapshot('小时')
  const gridLineColor = await safe(`(()=>{const c=document.querySelector('.gantt_task_cell');
    return c?getComputedStyle(c).borderRightColor:null})()`)
  console.log('小时尺度：' + JSON.stringify({ ...hourInfo, gridLineColor }))
  judge('③b 小时尺度下任务号标签仍每条只出现一次（条内+条尾=可见任务行）',
    hourPicked && hourInfo.tails > 0 && hourInfo.tails + hourInfo.barLabels === hourInfo.taskBars,
    `切换=${hourPicked} 条内=${hourInfo.barLabels} 条尾=${hourInfo.tails} 可见任务行=${hourInfo.taskBars} 网格线=${gridLineColor}`)
  // 切回「天」，④ 需要在 7 天窗口里拉长一条任务
  await pickSelect(
    `[...document.querySelectorAll('.el-select')].find(s=>(s.innerText||'').trim()==='小时')`,
    '天'
  )
  await sleep(2500)

  /* ---------- ④ 任务号标签二选一（撑得下→条内；撑不下→条右侧完整） ---------- */
  // dev 种子任务都是 25 秒 ⇒ 条再宽也撑不下 28 字符任务号（基线实测 条内=0、条尾=16）。
  // 所以这里**只在浏览器里临时把一个任务的时长拉长**（纯前端，不写后端），
  // 验证「撑得下时条内显示、条尾不重复」；测完立刻还原。
  const widePrepare = await safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
    if(!ss||!Array.isArray(ss.ganttData?.tasks)||!ss.ganttData.tasks.length) return null;
    const t=ss.ganttData.tasks[0]; if(!t.plannedStartTime||!t.plannedEndTime) return null;
    const start=new Date(String(t.plannedStartTime).replace(' ','T'));
    if(Number.isNaN(start.getTime())) return null;
    const pad=n=>String(n).padStart(2,'0');
    const end=new Date(start.getTime()+4*24*3600*1000);
    const endText=end.getFullYear()+'-'+pad(end.getMonth()+1)+'-'+pad(end.getDate())
      +' '+pad(end.getHours())+':'+pad(end.getMinutes())+':'+pad(end.getSeconds());
    const prev={id:t.taskId, taskNo:t.taskNo, plannedEndTime:t.plannedEndTime};
    t.plannedEndTime=endText;
    return {...prev, widenedTo:endText}})()`)
  await evaluate(`(()=>{const sc=document.querySelector('.gantt_ver_scroll'); if(sc) sc.scrollTop=0; return 1})()`)
  await sleep(2500)
  const wideInfo = await safe(`(()=>{const labels=[...document.querySelectorAll('.dhx-bar-label')]
      .map(e=>(e.textContent||'').trim());
    const tails=[...document.querySelectorAll('.dhx-tail-label')];
    return {taskBars:document.querySelectorAll('.gantt_task_line:not(.dhx-task-lane)').length,
            barLabels:labels.length, barSamples:labels.slice(0,4),
            tails:tails.length, tailSamples:tails.slice(0,4).map(e=>(e.textContent||'').trim())}})()`)
  console.log('拉长一条任务后：' + JSON.stringify(wideInfo))
  judge('④ 条宽撑得下 ⇒ 任务号显示在条内（条尾不再重复）',
    !!widePrepare && !!wideInfo && wideInfo.barLabels >= 1
      && wideInfo.barSamples.includes(widePrepare.taskNo)
      && wideInfo.barLabels + wideInfo.tails === wideInfo.taskBars,
    `任务=${JSON.stringify(widePrepare)} 条内=${JSON.stringify(wideInfo)}`)

  // 还原（不在页面上留脏数据）
  const restored = await safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
    if(!ss||!Array.isArray(ss.ganttData?.tasks)) return false;
    const t=ss.ganttData.tasks.find(x=>String(x.taskId)===${JSON.stringify(String(widePrepare?.id ?? ''))});
    if(!t) return false;
    t.plannedEndTime=${JSON.stringify(widePrepare?.plannedEndTime ?? '')}; return true})()`)
  await sleep(2500)
  const afterRestore = await snapshot('还原')
  judge('④ 还原后条内标签消失、回到条尾标签',
    restored && afterRestore.barLabels === 0 && afterRestore.tails === afterRestore.taskBars,
    `还原=${restored} 还原后=${JSON.stringify({barLabels:afterRestore.barLabels,tails:afterRestore.tails,taskBars:afterRestore.taskBars})}`)

  await shot('01-a-group')
  console.log('每步尾标签数：' + JSON.stringify(tailTrace))
  console.log('控制台（前 20 条）：\n  ' + consoleLog.slice(0, 20).join('\n  '))

  const failed = results.filter((r) => !r.ok)
  console.log('\n===== 结果 =====')
  console.log(`通过 ${results.length - failed.length} / ${results.length}`)
  if (failed.length) {
    console.log('失败项：')
    failed.forEach((f) => console.log('  ❌ ' + f.name + ' — ' + f.detail))
  }
  console.log('接口调用：' + JSON.stringify(netLog.slice(0, 12).map((n) => n.url.replace(BASE, ''))))
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
