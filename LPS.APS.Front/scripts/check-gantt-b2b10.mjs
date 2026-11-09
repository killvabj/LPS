#!/usr/bin/env node
/**
 * 甘特页 B2/B5/B10 实看（2026-11-05）
 *
 * 为什么要有这个脚本：静态探针只能证明代码写了，**证明不了浏览器里那几项真的显示成用户看到的样子**。
 *   已知教训：「有数据 ≠ 路径走过」——键在 DTO 里不代表页面把它渲染出来了。
 *
 * 它做什么（走真实登录表单 → 真实页面 → 真实点击）：
 *   ① B10 版本下拉悬停：选项 title 里应同时有「数据截止时间」「触发方式」「触发来源」三项
 *      （且数据截止时间/触发方式在 dev 上应是真实值，不是「未提供」）
 *   ② B2 详情抽屉：应有「MES 工单号」「下发状态」两行；dev 无 MES 工单/下发记录 ⇒ 显示
 *      「未建单」「未下发」（中性文案，不许显示成假值）
 *   ③ B5 关键路径：三态。后端整键缺省 ⇒ 必须显示「未判定」，
 *      **绝不能显示成「非关键路径」**（那等于拿恒 false 冒充判定结果）
 *   ④ B6/B7/B8 撤除位：抽屉里**不得再出现**「事实类型」「数量-时间拆分」「上游 Stage」
 *   ⑤ 任务清单表：表头应有「MES 工单号」「下发状态」两列
 *   ⑥ 缺键语义真值：应用内任务的 isCriticalPath/mesWorkOrderNo/dispatchStatus 必须是
 *      **缺键（undefined）**，不是 false —— 否则前端三态就是假的
 *
 * 用法：
 *   node --experimental-websocket scripts/check-gantt-b2b10.mjs
 *   node --experimental-websocket scripts/check-gantt-b2b10.mjs --url http://localhost:4001
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
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/gantt-b2b10'))
const CDP_PORT = 9400 + Math.floor(Math.random() * 90)
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
const profileDir = join(os.tmpdir(), 'cdp-gantt-b2b10-' + Date.now())
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
/** 真实鼠标点击（比 el.click() 更接近人手） */
async function clickAt(x, y) {
  await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none', buttons: 0 })
  await sleep(120)
  await send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1 })
  await sleep(60)
  await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount: 1 })
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

  /* ---------- 应用内状态 ---------- */
  const appState = () => safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
    if(!ss) return null;
    const vs=Array.isArray(ss.versions)?ss.versions:[];
    const ts=Array.isArray(ss.ganttData?.tasks)?ss.ganttData.tasks:[];
    const t0=ts[0]||null;
    return {
      versions: vs.length,
      version0: vs[0]?{id:vs[0].id, hasCutoff:'dataCutoffTime' in vs[0], cutoff:vs[0].dataCutoffTime??null,
        hasRunType:'runType' in vs[0], runType:vs[0].runType??null,
        hasTriggeredBy:'triggeredBy' in vs[0], triggeredBy:vs[0].triggeredBy??null}:null,
      tasks: ts.length,
      task0Keys: t0?{isCriticalPath:'isCriticalPath' in t0, isCriticalPathVal:t0.isCriticalPath??null,
        mesWorkOrderNo:'mesWorkOrderNo' in t0, mesWorkOrderNoVal:t0.mesWorkOrderNo??null,
        dispatchStatus:'dispatchStatus' in t0, dispatchStatusVal:t0.dispatchStatus??null}:null
    }})()`)
  const st = await appState()
  console.log('应用内状态：' + JSON.stringify(st))

  /* ---------- ① B10 版本下拉悬停 ---------- */
  const versionSelExpr = `(()=>{const l=[...document.querySelectorAll('.aps-gantt-page span.label')]
    .find(s=>(s.innerText||'').trim()==='版本'); return l?l.nextElementSibling:null})()`
  await safe(`(()=>{const s=${versionSelExpr}; if(!s) return false;
    const w=s.querySelector('.el-select__wrapper')||s; w.click(); return true})()`)
  await sleep(1200)
  const optTitles = await safe(`(()=>{
    const pops=[...document.querySelectorAll('.el-select-dropdown')];
    const vis=pops.filter(p=>p.getBoundingClientRect().height>0);
    const p=vis[vis.length-1]||pops[pops.length-1];
    if(!p) return null;
    return [...p.querySelectorAll('.el-select-dropdown__item')].map(e=>({
      text:(e.innerText||'').replace(/\\s+/g,' ').trim(), title:e.getAttribute('title')||''}));
  })()`)
  await shot('01-version-dropdown')
  await safe(`(()=>{document.body.click();
    document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); return 1})()`)
  await sleep(600)
  console.log('版本下拉选项：' + JSON.stringify(optTitles))

  const titles = Array.isArray(optTitles) ? optTitles.map((o) => o.title).filter(Boolean) : []
  const withCutoff = titles.find((t) => t.includes('数据截止时间'))
  const withRunType = titles.find((t) => t.includes('触发方式'))
  const withTrig = titles.find((t) => t.includes('触发来源'))
  judge('① B10 版本下拉选项的 title 同时含「数据截止时间」「触发方式」「触发来源」',
    titles.length > 0 && !!withCutoff && !!withRunType && !!withTrig,
    `有 title 的选项数=${titles.length} 截止=${!!withCutoff} 方式=${!!withRunType} 来源=${!!withTrig}`)
  judge('①b B10 数据截止时间 / 触发方式 在 dev 上是真实值（不是「未提供」）',
    !!withCutoff && !withCutoff.includes('数据截止时间：未提供')
      && !!withRunType && !withRunType.includes('触发方式：未提供'),
    `样本=${JSON.stringify((withCutoff || '').split('|').slice(0, 3))} / ${JSON.stringify((withRunType || '').split('|').slice(0, 3))}`)
  judge('①c B10 应用内版本对象确实带 dataCutoffTime / runType / triggeredBy 键',
    !!st?.version0 && st.version0.hasCutoff && st.version0.hasRunType && st.version0.hasTriggeredBy,
    `version0=${JSON.stringify(st?.version0)}`)

  /* ---------- ②③④ 点任务条 → 抽屉 ---------- */
  // dev 种子任务条只有几像素宽 ⇒ 优先用**真实鼠标**点最宽的一条；
  // 点不动就在浏览器里临时把一条任务拉长（纯前端，不写后端），测完还原。
  const barGeom = await safe(`(()=>{
    const bs=[...document.querySelectorAll('.gantt_task_line:not(.dhx-task-lane)')];
    let best=null;
    for(const b of bs){const r=b.getBoundingClientRect();
      if(r.width>0&&r.height>0&&(!best||r.width>best.w)) best={w:r.width,h:r.height,x:r.x+r.width/2,y:r.y+r.height/2};}
    return best})()`)
  let drawerOpen = false
  if (barGeom) {
    await clickAt(barGeom.x, barGeom.y)
    await sleep(1500)
    drawerOpen = !!(await safe(`document.querySelectorAll('.aps-task-drawer').length`))
  }
  if (!drawerOpen) {
    console.log('最宽条点不动，临时拉长一条再点：' + JSON.stringify(barGeom))
    await safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
      const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
      if(!ss||!Array.isArray(ss.ganttData?.tasks)||!ss.ganttData.tasks.length) return null;
      const t=ss.ganttData.tasks[0]; if(!t.plannedStartTime||!t.plannedEndTime) return null;
      const start=new Date(String(t.plannedStartTime).replace(' ','T'));
      if(Number.isNaN(start.getTime())) return null;
      const pad=n=>String(n).padStart(2,'0');
      const end=new Date(start.getTime()+4*24*3600*1000);
      const endText=end.getFullYear()+'-'+pad(end.getMonth()+1)+'-'+pad(end.getDate())
        +' '+pad(end.getHours())+':'+pad(end.getMinutes())+':'+pad(end.getSeconds());
      t.plannedEndTime=endText; return true})()`)
    await evaluate(`(()=>{const sc=document.querySelector('.gantt_ver_scroll'); if(sc) sc.scrollTop=0; return 1})()`)
    await sleep(2500)
    const g2 = await safe(`(()=>{
      const bs=[...document.querySelectorAll('.gantt_task_line:not(.dhx-task-lane)')];
      let best=null;
      for(const b of bs){const r=b.getBoundingClientRect();
        if(r.width>0&&r.height>0&&(!best||r.width>best.w)) best={w:r.width,h:r.height,x:r.x+r.width/2,y:r.y+r.height/2};}
      return best})()`)
    if (g2) {
      await clickAt(g2.x, g2.y)
      await sleep(1500)
    }
    drawerOpen = !!(await safe(`document.querySelectorAll('.aps-task-drawer').length`))
  }
  judge('② 真实点击任务条 ⇒ 任务详情抽屉打开',
    drawerOpen, `最宽条=${JSON.stringify(barGeom)}`)

  const drawer = await safe(`(()=>{const d=document.querySelector('.aps-task-drawer');
    if(!d) return null;
    const rows=[...d.querySelectorAll('.el-descriptions__label')].map(l=>{
      const cell=l.parentElement;
      const c=cell&&cell.querySelector('.el-descriptions__content');
      return {label:(l.innerText||'').trim(), value:((c&&c.innerText)||'').trim()};
    });
    return {rows, text:(d.innerText||'').replace(/\\s+/g,' ').slice(0,3000)}})()`)
  const rows = Array.isArray(drawer?.rows) ? drawer.rows : []
  const rowOf = (label) => rows.find((r) => r.label === label) || null
  const rowText = drawer?.text || ''
  console.log('抽屉行：' + JSON.stringify(rows.map((r) => r.label + '=' + r.value)))
  await shot('02-drawer')

  judge('②b 抽屉含「MES 工单号」行，且 dev 无工单时显示「未建单」（中性文案，不编造工单号）',
    !!rowOf('MES 工单号') && rowOf('MES 工单号').value.includes('未建单'),
    `行=${JSON.stringify(rowOf('MES 工单号'))}`)
  judge('②c 抽屉含「下发状态」行，且 dev 无下发记录时显示「未下发」',
    !!rowOf('下发状态') && rowOf('下发状态').value.includes('未下发'),
    `行=${JSON.stringify(rowOf('下发状态'))}`)
  judge('③ B5 关键路径三态：缺键时显示「未判定」（不得显示「非关键路径」）',
    !!rowOf('关键路径') && rowOf('关键路径').value.includes('未判定')
      && !rowOf('关键路径').value.includes('非关键路径'),
    `行=${JSON.stringify(rowOf('关键路径'))}`)
  judge('④ B6/B7/B8 撤除位：抽屉里不再出现「事实类型」「数量-时间拆分」「上游 Stage」',
    !/事实类型/.test(rowText) && !/数量-时间拆分/.test(rowText) && !/上游\s*Stage/i.test(rowText),
    `命中事实类型=${/事实类型/.test(rowText)} 命中拆分=${/数量-时间拆分/.test(rowText)} 命中上游=${/上游\s*Stage/i.test(rowText)}`)
  judge('⑥ 缺键语义真值：应用内任务里 isCriticalPath / mesWorkOrderNo / dispatchStatus 都是缺键（不是 false）',
    !!st?.task0Keys && st.task0Keys.isCriticalPath === false && st.task0Keys.mesWorkOrderNo === false
      && st.task0Keys.dispatchStatus === false,
    `task0Keys=${JSON.stringify(st?.task0Keys)}`)

  // 关抽屉
  await safe(`(()=>{const b=document.querySelector('.aps-task-drawer [aria-label="关闭 Drawer"]');
    if(b){b.click(); return true} document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); return false})()`)
  await sleep(900)

  /* ---------- ⑤ 任务清单表两列 ---------- */
  const tabClicked = await safe(`(()=>{const b=[...document.querySelectorAll('button.view-tab')]
    .find(e=>(e.innerText||'').trim().startsWith('任务清单')); if(!b) return false; b.click(); return true})()`)
  await sleep(2000)
  const tableInfo = await safe(`(()=>{
    const ths=[...document.querySelectorAll('.el-table__header th')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
    const body=(document.querySelector('.el-table__body')||{}).innerText||'';
    return {ths, bodySample:body.replace(/\\s+/g,' ').slice(0,600), bodyLen:body.length}})()`)
  console.log('任务清单表头：' + JSON.stringify(tableInfo?.ths))
  const ths = Array.isArray(tableInfo?.ths) ? tableInfo.ths : []
  judge('⑤ 任务清单表头含「MES 工单号」「下发状态」两列',
    tabClicked && ths.includes('MES 工单号') && ths.includes('下发状态'),
    `表头=${JSON.stringify(ths)}`)
  judge('⑤b 任务清单行里下发状态显示「未下发」（dev 无下发记录 ⇒ 中性文案）',
    typeof tableInfo?.bodySample === 'string' && tableInfo.bodySample.includes('未下发'),
    `样本=${JSON.stringify(tableInfo?.bodySample?.slice(0, 200))}`)
  await shot('03-task-list')

  await shot('04-final')
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
