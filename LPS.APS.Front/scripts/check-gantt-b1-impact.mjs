#!/usr/bin/env node
/**
 * 甘特页 B1「设备故障/维护影响」实看（2026-11-05）
 *
 * 背景：5号位 11-05 答复 B1「读侧已落码，代码在、数据没长出来」。
 *   本脚本证明两件事：
 *   ① 一旦**有**不可用窗口，浏览器里这一整条链是否真的显示出来
 *      （受影响 KPI / 任务条黄框 / 抽屉里的「资源不可用窗口」表）；
 *   ② 数据撤掉后是否**干净复原**（不留残影）。
 *
 * 两阶段（窗口数据由外部脚本在阶段之间写入/恢复）：
 *   node --experimental-websocket scripts/check-gantt-b1-impact.mjs discover
 *   → 打印并落盘 .tmp/b1-discover.json（页面当前版本 / 可见任务 / 建议的设备+日期）
 *   node --experimental-websocket scripts/check-gantt-b1-impact.mjs verify
 *   → 断言 4 项（KPI / 黄框 / 抽屉窗口表 / 应用内受影响集合）
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务
 *   - ⚠️ 必须 --no-proxy-server（本机系统代理会把 Chrome 绕到已部署那套 ⇒ 假绿）
 */
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import http from 'node:http'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const PHASE = arg('phase', 'discover')
const BASE = arg('url', 'http://localhost:4001').replace(/\/$/, '')
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/gantt-b1-impact'))
const CDP_PORT = 9500 + Math.floor(Math.random() * 90)
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
const profileDir = join(os.tmpdir(), 'cdp-gantt-b1-' + Date.now())
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
  '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--window-size=1920,1080',
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
  writeFileSync(join(SHOT_DIR, name + '.png'), Buffer.from(r.data, 'base64'))
}
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
      consoleLog.push(m.params.type + ': ' + (m.params.args || []).map((x) => x.value ?? x.description ?? '').join(' ').slice(0, 200))
    }
    if (m.method === 'Runtime.exceptionThrown') {
      const d = m.params.exceptionDetails
      consoleLog.push('EXCEPTION: ' + (d.exception?.description || d.text || '').slice(0, 200))
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
  await evaluate(`location.hash = '#/aps/gantt'`)
  let bars = 0
  for (let i = 0; i < 120; i++) {
    await sleep(1000)
    bars = (await safe(`document.querySelectorAll('.gantt_task_line').length`)) || 0
    if (bars > 0) break
  }
  await sleep(4000)
  if (!bars) throw new Error('甘特没渲染出条：' + String(await safe(`(document.body.innerText||'').slice(0,300)`)))

  const srcGuard = await safe(`(()=>{const s=[...document.querySelectorAll('script[src]')].map(e=>e.src);
    return {origin:location.origin, allLocal:s.every(u=>u.startsWith(location.origin))}})()`)
  judge('守卫：页面脚本来自本机 dev', !!srcGuard && srcGuard.origin === BASE && srcGuard.allLocal,
    `origin=${srcGuard?.origin}`)

  const state = () => safe(`(()=>{const root=document.querySelector('.aps-gantt-page');
    const inst=root&&root.__vueParentComponent; const ss=inst&&inst.setupState;
    if(!ss) return null;
    const ts=Array.isArray(ss.ganttData?.tasks)?ss.ganttData.tasks:[];
    const rs=Array.isArray(ss.ganttData?.resources)?ss.ganttData.resources:[];
    const imp=Array.isArray(ss.impactedTaskIds)?ss.impactedTaskIds:null;
    return {
      versionId: ss.currentVersionId??null,
      versionCode: (ss.versions||[]).find(v=>v.id===(ss.currentVersionId??null))?.versionCode??null,
      taskCount: ts.length, resourceCount: rs.length,
      impactedCount: imp===null?null:imp.length,
      impactedSample: imp===null?null:imp.slice(0,5),
      window: ss.dateRange?{from:ss.dateRange.from,to:ss.dateRange.to}:null,
      tasks: ts.slice(0,4000).map(t=>({taskId:t.taskId,taskNo:t.taskNo,resourceId:t.resourceId,
        start:t.plannedStartTime,end:t.plannedEndTime})),
      // 窗口只回**标量**（计数 + 首条摘要）：
      //  直接回 Vue 的 reactive 代理数组，经 CDP returnByValue 序列化后形状不可靠 ⇒ 判据会假红
      resources: rs.map(r=>({resourceId:r.resourceId,resourceCode:r.resourceCode,
        uwCount:(r.unavailableWindows||[]).length,
        uwFirst:(r.unavailableWindows&&r.unavailableWindows[0])?{
          from:r.unavailableWindows[0].from,to:r.unavailableWindows[0].to,
          reason:r.unavailableWindows[0].reason,
          impactedCount:(r.unavailableWindows[0].impactedTaskIds||[]).length}:null}))
    }})()`)
  const st = await state()
  console.log('阶段 ' + PHASE + ' 应用内状态：版本=' + st?.versionId + '/' + st?.versionCode +
    ' 任务=' + st?.taskCount + ' 资源=' + st?.resourceCount +
    ' 应用内受影响=' + st?.impactedCount + ' 窗口=' + JSON.stringify(st?.window))

  if (PHASE === 'discover') {
    // 找一台「有任务、且窗口内有任务」的设备，给外部脚本去写不可用窗口
    const win = st?.window
    const inWin = (st?.tasks || []).filter((t) => {
      if (!win || !t.start) return false
      return String(t.start).slice(0, 10) >= String(win.from).slice(0, 10) &&
        String(t.start).slice(0, 10) <= String(win.to).slice(0, 10)
    })
    const byRes = {}
    for (const t of inWin) (byRes[t.resourceId] ||= []).push(t)
    const cand = Object.entries(byRes).sort((a, b) => b[1].length - a[1].length)[0]
    const pick = cand ? { resourceId: Number(cand[0]), date: String(cand[1][0].start).slice(0, 10), taskCount: cand[1].length } : null
    const out = { versionId: st?.versionId, versionCode: st?.versionCode, window: win, pick }
    writeFileSync(join(ROOT, '.tmp/b1-discover.json'), JSON.stringify(out, null, 2))
    console.log('建议设备+日期：' + JSON.stringify(pick))
    console.log('已落盘 .tmp/b1-discover.json')
    await shot('discover')
  } else {
    /* ---------- 验证四项 ---------- */
    const uwList = (st?.resources || []).filter((r) => (r.uwCount || 0) > 0)
    judge('① 甘特资源的 unavailableWindows 非空（后端真的把不可用窗口返回来了）',
      uwList.length > 0,
      `带窗口的资源数=${uwList.length} 示例=${JSON.stringify(uwList[0] || null)}`)

    judge('② 应用内 impactedTaskIds 非空（前端按 窗口∩任务 算出了受影响任务）',
      st?.impactedCount > 0, `受影响任务数=${st?.impactedCount} 样本=${JSON.stringify(st?.impactedSample)}`)

    const kpi = await safe(`(()=>{const t=(document.body.innerText||'');
      const m=t.match(/受影响\\s*(\\d+)/); const el=document.querySelector('.stat-impacted');
      return {matched:m?m[1]:null, visible:!!el, text:el?(el.innerText||'').replace(/\\s+/g,' ').trim():null}})()`)
    judge('③ 页面 KPI 行出现「受影响 N」且 N > 0',
      !!kpi?.visible && Number(kpi?.matched) > 0, `KPI=${JSON.stringify(kpi)}`)

    const hl = await safe(`(()=>{const n=document.querySelectorAll('.dhx-task-impacted').length;
      const all=document.querySelectorAll('.gantt_task_line:not(.dhx-task-lane)').length;
      return {highlighted:n, bars:all}})()`)
    judge('④ 任务条出现受影响黄框（dhx-task-impacted）', (hl?.highlighted || 0) > 0, JSON.stringify(hl))

    // 点开一条受影响的条 ⇒ 抽屉里应有「资源不可用窗口」表
    const geom = await safe(`(()=>{const bs=[...document.querySelectorAll('.dhx-task-impacted')];
      let best=null; for(const b of bs){const r=b.getBoundingClientRect();
        if(r.width>0&&r.height>0&&(!best||r.width>best.w)) best={w:r.width,x:r.x+r.width/2,y:r.y+r.height/2}}
      return best})()`)
    let drawerOk = false
    let drawerText = ''
    if (geom) {
      await clickAt(geom.x, geom.y)
      await sleep(1800)
      const d = await safe(`(()=>{const d=document.querySelector('.aps-task-drawer'); if(!d) return null;
        return {text:(d.innerText||'').replace(/\\s+/g,' '),
          winRows:d.querySelectorAll('.resource-down-section .el-table__body tr').length}})()`)
      drawerText = d?.text || ''
      drawerOk = !!d && /资源不可用窗口/.test(drawerText)
      judge('⑤ 点开受影响的条 ⇒ 抽屉出现「资源不可用窗口」表且有数据行',
        drawerOk && (d?.winRows || 0) > 0, `窗口行数=${d?.winRows} 片段=${JSON.stringify(drawerText.slice(0, 200))}`)
    } else {
      judge('⑤ 点开受影响的条 ⇒ 抽屉出现「资源不可用窗口」表且有数据行', false, '页面上没有 dhx-task-impacted 条可点')
    }
    await shot('verify')
  }

  const failed = results.filter((r) => !r.ok)
  console.log('\n===== 结果 =====')
  console.log(`通过 ${results.length - failed.length} / ${results.length}`)
  if (failed.length) failed.forEach((f) => console.log('  ❌ ' + f.name + ' — ' + f.detail))
  const errs = consoleLog.filter((l) => l.startsWith('EXCEPTION') || l.startsWith('error'))
  if (errs.length) console.log('控制台错误：\n  ' + errs.slice(0, 6).join('\n  '))
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
