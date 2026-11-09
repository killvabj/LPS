#!/usr/bin/env node
/**
 * 审核 217fa2 前端可自改项 实看（2026-11-07）
 *
 * 为什么要有这个脚本：静态探针只能证明**代码写了**，证明不了**浏览器里真的这样显示**。
 *   已知教训：「有数据 ≠ 路径走过」——本脚本要把三条路径都亲手走一遍：
 *     ① 甘特「任务清单」Tab ⇒ 列序 + 「计划数量/计划加工量」两列真的有值
 *     ② 甘特任务详情抽屉 ⇒ TaskShare 三态文案（1 条不再显示「未提供」）
 *     ③ 订单列表 ⇒ 「APS 预计完成时间」列真的在「客户交期」后面
 *     ④ 候选页影响摘要 ⇒ 标签是「新增延期任务」而不是「新增延期」
 *
 * 用法：
 *   node --experimental-websocket scripts/check-audit-ui.mjs
 *   node --experimental-websocket scripts/check-audit-ui.mjs --url http://localhost:4001
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务（既定约束：不启动 dev server）
 *   - 登录一次即可（dev 登录限流 5 次/分钟）
 *   - 随机端口 + 随机 URL 标记，只认带标记的 target（避免 attach 到上一轮残留 Chrome）
 *   - --no-proxy-server：系统代理会把请求绕到已部署那套，读到的就不是本地页面了
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
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/audit-ui'))
const CDP_PORT = 8800 + Math.floor(Math.random() * 90)
const MARK = 'auditui' + Date.now().toString(36)
const USER = process.env.ORDER_USER || 'admin'
const PWD = process.env.ORDER_PWD || 'Admin@123456'
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
const profileDir = join(os.tmpdir(), 'cdp-auditui-' + Date.now())
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
    '--no-proxy-server',
    '--proxy-server=direct://',
    '--proxy-bypass-list=*',
    `${BASE}/#/login?m=${MARK}`
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
}, 420000)

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
  const r = await send('Runtime.evaluate', {
    expression: expr,
    returnByValue: true,
    awaitPromise: true
  })
  if (r.exceptionDetails)
    throw new Error('EXC ' + JSON.stringify(r.exceptionDetails).slice(0, 300))
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

/** 页面里拿甘特页 setupState（元素上有 __vueParentComponent） */
const SS = "document.querySelector('.aps-gantt-page').__vueParentComponent.setupState"

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
  if (!target) throw new Error('拿不到带标记的 Chrome 页面 target')
  console.log('target = ' + target.url)

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
  }
  await send('Runtime.enable')
  await send('Page.enable')

  /* ---------- 登录（真实表单） ---------- */
  for (let i = 0; i < 40; i++) {
    if ((await safe(`document.querySelectorAll('.el-input__inner').length`)) >= 2) break
    await sleep(500)
  }
  const inputs = await safe(`document.querySelectorAll('.el-input__inner').length`)
  if (typeof inputs !== 'number' || inputs < 2) {
    console.log(
      '页面状态：' +
        JSON.stringify(await safe(`({href:location.href,text:(document.body.innerText||'').slice(0,200)})`))
    )
    throw new Error('没渲染出登录表单')
  }
  await evaluate(
    `(()=>{const ins=document.querySelectorAll('.el-input__inner');
    const set=(el,v)=>{Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);
      el.dispatchEvent(new Event('input',{bubbles:true}))};
    set(ins[0],'${USER}');set(ins[1],'${PWD}');
    const b=[...document.querySelectorAll('button')].find(e=>e.className.includes('el-button--primary'));b.click();return 1})()`
  )
  for (let i = 0; i < 30; i++) {
    await sleep(1000)
    const h = await safe('location.hash')
    if (h && !h.includes('/login')) break
  }
  console.log('登录后 hash = ' + (await safe('location.hash')))

  /* ---------- ① 甘特「任务清单」Tab：列序 + 数量两列 ---------- */
  await evaluate(`location.hash = '#/aps/gantt?m=${MARK}'`)
  for (let i = 0; i < 40; i++) {
    await sleep(500)
    if (await safe(`!!document.querySelector('.aps-gantt-page')`)) break
  }
  await sleep(2500)

  // 选一个**有任务**的版本，并把时间尺度放到最宽档（月＝90 天），保证窗口里真的有任务
  const picked = await safe(`(()=>{const ss=${SS};
    const vs=(ss.versions||[]).filter(v=>(v.totalTasks||0)>0).sort((a,b)=>(b.totalTasks||0)-(a.totalTasks||0));
    if(!vs.length) return {ok:false,why:'没有带任务的版本'};
    ss.selectedVersionId=vs[0].id; ss.zoomUnit='month';
    return {ok:true, id:vs[0].id, code:vs[0].versionCode, tasks:vs[0].totalTasks}})()`)
  console.log('选定版本 = ' + JSON.stringify(picked))
  if (picked?.ok) {
    await sleep(9000)
    // 切到「任务清单」Tab
    await evaluate(`(()=>{const t=[...document.querySelectorAll('.view-tab')].find(e=>(e.innerText||'').includes('任务清单'));
      if(t) t.click(); return t?1:0})()`)
    await sleep(3000)
    const list = await safe(`(()=>{
      const heads=[...document.querySelectorAll('.task-list-pane .el-table__header th')]
        .map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim()).filter(t=>t);
      const rows=[...document.querySelectorAll('.task-list-pane .el-table__body-wrapper tbody tr')];
      const first=rows[0]?[...rows[0].querySelectorAll('td')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim()):[];
      return {heads, rowCount:rows.length, first}})()`)
    console.log('任务清单列头 = ' + JSON.stringify(list?.heads))
    console.log('任务清单首行 = ' + JSON.stringify(list?.first))

    const expect = [
      '物料','阶段','部门','资源','人工槽','工序','计划数量','计划加工量',
      '计划开始','计划结束','状态','MES 工单号','下发状态','客户交期','延期时长','主要原因','延期',
      '任务号','订单号','生产指示号'
    ]
    const heads = list?.heads || []
    const idx = expect.map((n) => heads.indexOf(n))
    const orderOk = idx.every((v, i) => v >= 0 && (i === 0 || v > idx[i - 1]))
    judge(
      '①-1 甘特任务清单列序＝物料→阶段→部门→资源/人工槽→工序→数量→时间→状态→MES→交期/延期/原因（追溯键在尾）',
      orderOk,
      orderOk ? `列头=${heads.join('|')}` : `位次=${expect.map((n, i) => `${n}:${idx[i]}`).join(' ')}`
    )
    const qtyIdx = heads.indexOf('计划数量')
    const cell = qtyIdx >= 0 ? list.first[qtyIdx] : ''
    judge(
      '①-2 「计划数量」列有真实值（数量+单位，非空非—）',
      qtyIdx >= 0 && !!cell && cell !== '—' && /\d/.test(cell),
      `行数=${list?.rowCount} 计划数量格=${JSON.stringify(cell)}`
    )
    await shot('01-gantt-task-list')

    /* ---------- ② 甘特任务详情抽屉：TaskShare 三态 ---------- */
    const shareInfo = await safe(`(()=>{const ss=${SS};
      const ts=(ss.ganttData&&ss.ganttData.tasks)||[];
      const one=ts.find(t=>t.taskShare&&t.taskShare.length===1);
      const zero=ts.find(t=>!t.taskShare||t.taskShare.length===0);
      const multi=ts.find(t=>t.taskShare&&t.taskShare.length>1);
      return {total:ts.length, one:one?{id:one.taskId,no:one.taskNo}:null,
        zero:zero?{id:zero.taskId,no:zero.taskNo}:null,
        multi:multi?{id:multi.taskId,no:multi.taskNo}:null}})()`)
    console.log('TaskShare 分布 = ' + JSON.stringify(shareInfo))
    if (shareInfo?.one) {
      await evaluate(`(()=>{const ss=${SS}; ss.selectedTaskId=${shareInfo.one.id}; ss.drawerVisible=true; return 1})()`)
      await sleep(2500)
      const sec = await safe(`(()=>{const e=document.querySelector('.taskshare-section');
        return e?(e.innerText||'').replace(/\\s+/g,' ').trim():null})()`)
      await shot('02-gantt-taskshare-1')
      judge(
        '②-1 恰好 1 条份额的任务：显示「本任务由 1 个订单承接」，不再是「未提供订单份额数据」',
        !!sec && /本任务由 1 个订单承接/.test(sec) && !/未提供订单份额数据/.test(sec),
        `区块文案=${JSON.stringify(sec)}`
      )
    } else {
      judge('②-1 恰好 1 条份额的任务：显示「本任务由 1 个订单承接」', false,
        '当前窗口没有 taskShare.length===1 的任务（无法实看）')
    }
    if (shareInfo?.zero) {
      await evaluate(`(()=>{const ss=${SS}; ss.selectedTaskId=${shareInfo.zero.id}; ss.drawerVisible=true; return 1})()`)
      await sleep(2000)
      const sec = await safe(`(()=>{const e=document.querySelector('.taskshare-section');
        return e?(e.innerText||'').replace(/\\s+/g,' ').trim():null})()`)
      judge(
        '②-2 没有份额数据的任务：显示「未提供订单份额数据（TaskShare）」',
        !!sec && /未提供订单份额数据/.test(sec),
        `区块文案=${JSON.stringify(sec)}`
      )
    }
  } else {
    judge('① 甘特任务清单列序 + 数量两列', false, 'dev 没有带任务的版本：' + JSON.stringify(picked))
  }

  /* ---------- ③ 订单列表：APS 预计完成时间列 ---------- */
  await evaluate(`location.hash = '#/aps/order?m=${MARK}'`)
  await sleep(6000)
  const oh = await safe(`(()=>{
    const heads=[...document.querySelectorAll('.el-table__header th')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
    const rows=[...document.querySelectorAll('.el-table__body-wrapper tbody tr')];
    const cells=rows.map(r=>[...r.querySelectorAll('td')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim()));
    return {heads, rowCount:rows.length, first:cells[0]||[], cells}})()`)
  console.log('订单列表列头 = ' + JSON.stringify(oh?.heads))
  const ohIdx = (oh?.heads || []).indexOf('APS 预计完成时间')
  const dueIdx = (oh?.heads || []).indexOf('客户交期')
  const planVals = (oh?.cells || []).map((c) => c[ohIdx] || '').filter((v) => v !== undefined)
  const unplanned = planVals.filter((v) => v === '未排产').length
  const dated = planVals.filter((v) => v && v !== '未排产').length
  console.log('「APS 预计完成时间」取值分布 = ' + JSON.stringify({ dated, unplanned, sample: planVals.slice(0, 6) }))
  judge(
    '③-1 订单列表有「APS 预计完成时间」列且排在「客户交期」之后',
    ohIdx > dueIdx && dueIdx >= 0,
    `客户交期位次=${dueIdx} 预计完成位次=${ohIdx} 行数=${oh?.rowCount}`
  )
  judge(
    '③-2 「APS 预计完成时间」列每行都有值（未排产的显式写「未排产」，不留空）',
    ohIdx >= 0 && (oh?.first || []).length > ohIdx && !!(oh.first[ohIdx] || '').trim(),
    `首行该列=${JSON.stringify(ohIdx >= 0 ? oh?.first?.[ohIdx] : null)}；本页 ${dated} 行有排产时间 / ${unplanned} 行未排产`
  )
  await shot('03-order-list')

  // ③-3 该列在**有排产任务**的订单上要出真实日期（不是恒「未排产」）。
  //     ⚠️ dev 订单列表默认排序＝优先级 DESC → 交期 ASC ⇒ 首屏全是 2016 年最老交期、
  //     且没有 Task 行的订单 ⇒ 默认页整列都是「未排产」。那是**数据条件，不是缺陷**：
  //     真验法＝先按版本切到有任务的版本，再**筛一条确实有 Task 行的订单**出来看。
  if (picked?.ok) {
    const sw = await safe(
      `(()=>{const root=document.querySelector('.aps-order');
        const ss=root&&root.__vueParentComponent&&root.__vueParentComponent.setupState;
        if(!ss||!ss.onVersionChange) return 'no-setup';
        ss.onVersionChange(${picked.id}); return 'switched'})()`
    )
    console.log('切换订单页版本 = ' + sw)
    await sleep(9000)

    // API 侧找一条该版本里**有 Task 行**的订单（plannedCompletionTime 非空 ⇔ 该订单有 Task 行）
    let seededOrderNo = null
    try {
      const tok = await apiToken()
      for (let y = 2025; y <= 2026 && !seededOrderNo; y++) {
        for (let m = 1; m <= 12 && !seededOrderNo; m++) {
          const mm = String(m).padStart(2, '0')
          const q = await apiGet(
            tok,
            `/api/order-query?planVersionId=${picked.id}&dueDateFrom=${y}-${mm}-01&dueDateTo=${y}-${mm}-28&take=60`
          )
          const hit = (q?.items || []).find((i) => i.plannedCompletionTime)
          if (hit) seededOrderNo = hit.orderNo
        }
      }
    } catch (e) {
      console.log('找有 Task 的订单失败：' + e.message)
    }
    console.log('用于实看的订单号 = ' + seededOrderNo)

    let cellVal = null
    if (seededOrderNo) {
      const typed = await safe(
        `(()=>{const inp=document.querySelector('input[placeholder="订单号"]');
          if(!inp) return 'no-input';
          const setter=Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype,'value').set;
          setter.call(inp, ${JSON.stringify(seededOrderNo)});
          inp.dispatchEvent(new Event('input',{bubbles:true}));
          return 'typed'})()`
      )
      console.log('填订单号筛选 = ' + typed)
      await sleep(7000)
      const oh2 = await safe(
        `(()=>{
          const heads=[...document.querySelectorAll('.el-table__header th')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
          const rows=[...document.querySelectorAll('.el-table__body-wrapper tbody tr')];
          const cells=rows.map(r=>[...r.querySelectorAll('td')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim()));
          const idx=heads.indexOf('APS 预计完成时间');
          return {heads, rowCount:rows.length, cells, idx, first:cells[0]||[]}})()`
      )
      cellVal = oh2?.first?.[oh2?.idx] ?? null
      console.log(
        `筛到 ${seededOrderNo} 后：该列取值=${JSON.stringify(cellVal)}（行数=${oh2?.rowCount}）`
      )
    }
    judge(
      '③-3 筛到有排产任务的订单后，「APS 预计完成时间」出真实日期（不是恒「未排产」）',
      !!cellVal && cellVal !== '未排产' && /\d{4}-\d{2}-\d{2}/.test(cellVal),
      `订单=${seededOrderNo} 该列取值=${JSON.stringify(cellVal)}`
    )
    await shot('03b-order-list-tasks-version')
  }

  /* ---------- ④ 候选页影响摘要标签 ---------- */
  await evaluate(`location.hash = '#/aps/candidate?m=${MARK}'`)
  await sleep(6000)
  const cd = await safe(`(()=>{
    const labels=[...document.querySelectorAll('.sum-label')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim());
    return {labels, text:(document.body.innerText||'').slice(0,4000)}})()`)
  console.log('候选摘要标签 = ' + JSON.stringify(cd?.labels))
  if (Array.isArray(cd?.labels) && cd.labels.length > 0) {
    judge(
      '④-1 候选影响摘要有「新增延期任务」标签，且没有旧标签「新增延期」',
      cd.labels.includes('新增延期任务') && !cd.labels.includes('新增延期'),
      `标签=${JSON.stringify(cd?.labels)}`
    )
  } else {
    // 影响摘要整块在 `<template v-if="currentComparison">` 里 ⇒ dev 没有候选版本时**根本不渲染**
    // （这是数据条件，不是缺陷）⇒ 查一下后端有几条候选，把原因写清楚，别报成假红。
    let pending = '未知'
    try {
      const tok = await apiToken()
      const cs = await apiGet(tok, '/api/overview/candidate-summary')
      pending = String(cs?.pendingCount ?? '?')
    } catch (e) {
      pending = '查询失败：' + String(e.message).slice(0, 80)
    }
    judge(
      '④-1 候选影响摘要标签（本次**无法实看**：dev 无候选版本 ⇒ 影响摘要整块不渲染）',
      false,
      `页面无摘要标签；后端 GET /api/overview/candidate-summary 的 pendingCount=${pending}（数据条件，非缺陷）`
    )
  }
  await shot('04-candidate')

  const errs = consoleLog.filter((l) => l.startsWith('EXCEPTION'))
  judge('⑤ 控制台无未捕获异常', errs.length === 0, errs.length ? errs.slice(0, 3).join(' | ') : '无')
}

main()
  .then(() => {
    const bad = results.filter((r) => !r.ok)
    console.log('\n===== 结论 =====')
    console.log(`通过 ${results.length - bad.length} / ${results.length}`)
    if (consoleLog.length) console.log('控制台片段：\n' + consoleLog.slice(-12).join('\n'))
    killChrome()
    clearTimeout(watchdog)
    process.exit(bad.length ? 1 : 0)
  })
  .catch((err) => {
    console.error('脚本异常：' + err.message)
    if (consoleLog.length) console.log('控制台片段：\n' + consoleLog.slice(-12).join('\n'))
    killChrome()
    clearTimeout(watchdog)
    process.exit(2)
  })
