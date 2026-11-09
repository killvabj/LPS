#!/usr/bin/env node
/**
 * 排产总览页「看得懂、口径对得上」实看（2026-11-04）
 *
 * 为什么要有这个脚本：静态探针只能证明代码写了，**证明不了浏览器里那块数字到底显示了什么**。
 *   本轮改的全是「口径」类问题（统计范围 / 共几个域 / 来源哪次排程运行 / 资源表到底有哪些列），
 *   这类问题恰恰只能靠实看 + 与后端返回值逐项比对才能证伪。
 *
 * 它做什么（真实登录表单 → 真实页面 → 与后端返回值逐项比对）：
 *   ① KPI 卡的「统计范围」是否写明了域 + 版本，且与 active-plan 第一条一致
 *   ② 版本卡的「任务数」是否与后端 totalTasks 一致，且**页面没有**「订单数」列（53/55 打架的那列）
 *   ②b 版本卡的「任务数」是否标明范围（整个计划区间内）——与甘特页「窗口内」不是一个口径
 *   ③ 排程域状态面板头是否写明「共 N 个排程域 · 来源排程运行 X」，N/X 与后端一致
 *   ④ 资源表是不是 4 列（资源 / 任务数 / 计划工时 / 不可用窗口），数值与后端一致，
 *      且「资源」列不出现「编码 | 编码」这种重复段
 *   ⑤ 待确认候选版本：0 条时不显示诱导语；有条时按钮能跳到 /aps/candidate
 *   ⑥ 页面无错误告警
 *
 * 用法：
 *   node --experimental-websocket scripts/check-overview-visual.mjs
 *   node --experimental-websocket scripts/check-overview-visual.mjs --url http://localhost:4001 --shot .tmp/overview
 *
 * 说明：
 *   - 只连**用户自己跑着的 dev**（默认 4001），不另起服务（既定约束：不启动 dev server）
 *   - 必须带 --no-proxy-server（本机系统代理会把请求绕到已部署那套 ⇒ 假绿）
 */
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import http from 'node:http'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const BASE = arg('url', 'http://localhost:4001').replace(/\/$/, '')
const SHOT_DIR = resolve(ROOT, arg('shot', '.tmp/overview'))
const API = process.env.VERIFY_API || 'https://localhost:7044'
const USER = process.env.OV_USER || 'admin'
const PWD = process.env.OV_PWD || 'Admin@123456'
const CDP_PORT = 8920 + Math.floor(Math.random() * 70)

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

/** 直接打后端取真值（用来逐项比对页面显示的数） */
async function apiGet(path, token) {
  const r = await fetch(API + path, { headers: { Authorization: 'Bearer ' + token } })
  const j = await r.json().catch(() => null)
  return j?.data
}
async function apiLogin() {
  const r = await fetch(API + '/api/auth/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ userCode: USER, password: PWD })
  })
  const j = await r.json().catch(() => null)
  const t = j?.data?.accessToken
  if (!t) throw new Error('登录失败：HTTP ' + r.status)
  return t
}

const CHROME = findChrome()
const profileDir = join(os.tmpdir(), 'cdp-ov-' + Date.now())
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
  const token = await apiLogin()
  const activePlans = (await apiGet('/api/overview/active-plan', token)) || []
  const primary = Array.isArray(activePlans) ? activePlans[0] : null
  if (!primary) throw new Error('dev 无 ACTIVE 版本，无法比对')
  const summary = await apiGet(`/api/order-query/summary?planVersionId=${primary.planVersionId}`, token)
  const resources = (await apiGet(
    `/api/overview/resource-bottleneck?planVersionId=${primary.planVersionId}&topN=10`,
    token
  )) || []
  const candidate = await apiGet('/api/overview/candidate-summary', token)
  const domains = primary.sourceScheduleRunId
    ? (await apiGet(`/api/domain-status/${primary.sourceScheduleRunId}`, token)) || []
    : []
  const issueExpected = domains.filter((d) => d.status === 'FAILED' || d.status === 'BLOCKED').length
  console.log(
    `后端真值：primary=${primary.planVersionId} ${primary.versionCode} 订单=${summary?.totalCount} 资源行=${resources.length} 域=${domains.length} 异常=${issueExpected} 候选=${candidate?.pendingCount}`
  )

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
  }
  await send('Runtime.enable')
  await send('Page.enable')

  // ---- 登录（真实表单）
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

  // ---- 进总览页
  await evaluate(`location.hash = '#/aps/overview'`)
  await sleep(6000)

  const dom = await safe(`(()=>{
    const t=(e)=>(e.innerText||'').replace(/\\s+/g,' ').trim();
    const kpis=[...document.querySelectorAll('.kpi-card')].map(e=>({
      label:t(e.querySelector('.kpi-label')),
      value:t(e.querySelector('.kpi-value')),
      ratio:t(e.querySelector('.kpi-ratio'))}));
    const panels=[...document.querySelectorAll('.panel')].map(p=>({
      head:t(p.querySelector('.panel-header')),
      versionCards:[...p.querySelectorAll('.version-card')].map(v=>t(v)),
      issueItems:p.querySelectorAll('.issue-item').length,
      resHead:[...p.querySelectorAll('.resource-table thead th')].map(x=>t(x)),
      resRows:[...p.querySelectorAll('.resource-table tbody tr')].map(r=>[...r.querySelectorAll('td')].map(c=>t(c)))
    }));
    return {
      kpis, panels,
      candidate:t(document.querySelector('.candidate-bar')),
      candidateHasLink:!!document.querySelector('.candidate-bar .candidate-link'),
      alerts:[...document.querySelectorAll('.el-alert')].map(t)
    };
  })()`)

  if (!dom || dom.__err) {
    console.log('页面文本：' + String(await safe(`(document.body.innerText||'').replace(/\\s+/g,' ').slice(0,400)`)))
    throw new Error('总览页没渲染出来')
  }

  // ① KPI：合计值与后端一致 + 统计范围写明（域 + 版本）
  const totalCard = (dom.kpis || []).find((k) => k.label === '订单合计')
  const scopeHit = !!totalCard && /统计范围/.test(totalCard.ratio) && totalCard.ratio.includes(primary.versionCode)
  judge(
    '① KPI 订单合计与后端一致且标明统计范围（域 + 版本）',
    !!totalCard && totalCard.value === String(summary?.totalCount) && scopeHit,
    `合计=${totalCard?.value}（后端 ${summary?.totalCount}） 副标题=${JSON.stringify(totalCard?.ratio)}`
  )
  const kpiCounts = (dom.kpis || []).filter((k) => k.label !== '订单合计')
  judge(
    '①b 按期/延迟/未排产三卡与后端一致',
    kpiCounts.length === 3 &&
      kpiCounts[0].value === String(summary?.onTimeCount) &&
      kpiCounts[1].value === String(summary?.delayedCount) &&
      kpiCounts[2].value === String(summary?.unscheduledCount),
    `页面=${JSON.stringify(kpiCounts.map((k) => [k.label, k.value]))} 后端=${JSON.stringify([
      summary?.onTimeCount,
      summary?.delayedCount,
      summary?.unscheduledCount
    ])}`
  )

  // ② 版本卡：任务数与后端一致 + 没有「订单数」这列
  const verPanel = (dom.panels || []).find((p) => p.head.includes('当前生效版本'))
  const verCards = verPanel?.versionCards || []
  const taskOk = verCards.length === activePlans.length &&
    verCards.every((c, i) => c.includes('任务数') && c.includes(String(activePlans[i].totalTasks)))
  const noOrderCol = verCards.every((c) => !c.includes('订单数'))
  judge(
    '② 版本卡显示任务数（与后端一致），且不显示与实时摘要打架的「订单数」',
    taskOk && noOrderCol,
    `卡片=${verCards.length}/${activePlans.length} 任务数一致=${taskOk} 无订单数列=${noOrderCol} 首卡=${JSON.stringify(verCards[0]?.slice(0, 120))}`
  )

  // ②b 版本卡任务数标明范围（2026-11-04 加）
  //   用户反馈「总览页 13201 条 vs 甘特页 3334 条对不上」⇒ 两个口径不同，
  //   但总览页这个数必须自己讲清是「整个计划区间内」，否则用户还会当成「窗口内」。
  const scopeTagged = verCards.every(
    (c) => c.includes('任务数') && c.includes('整个计划区间内') && !c.includes('窗口内')
  )
  judge(
    '②b 版本卡「任务数」标明范围（整个计划区间内），不与甘特页窗口内口径混淆',
    verCards.length > 0 && scopeTagged,
    `逐卡范围标注=${verCards.map((c) => c.includes('整个计划区间内')).join(',')} 首卡=${JSON.stringify(verCards[0]?.slice(0, 160))}`
  )

  // ③ 排程域状态面板头：共 N 个排程域 + 来源 Run X
  const domPanel = (dom.panels || []).find((p) => p.head.includes('排程域状态'))
  const headHit = !!domPanel && domPanel.head.includes(`共 ${domains.length} 个排程域`)
  const runHit = !!domPanel && domPanel.head.includes(`来源排程运行 ${primary.sourceScheduleRunId}`)
  const issueHit = !!domPanel && domPanel.head.includes(`${issueExpected} 项异常`)
  judge(
    '③ 域状态面板头标明「共 N 个排程域 · 来源排程运行 X · N 项异常」',
    headHit && runHit && issueHit,
    `面板头=${JSON.stringify(domPanel?.head?.slice(0, 120))} 期望 共${domains.length}个/Run${primary.sourceScheduleRunId}/异常${issueExpected}`
  )

  // ④ 资源表：4 列 + 数值与后端一致 + 资源列无重复段
  const resPanel = (dom.panels || []).find((p) => p.head.includes('资源负荷'))
  const head = resPanel?.resHead || []
  const rows = resPanel?.resRows || []
  const colsOk =
    head.length === 4 && head[1] === '任务数' && head[2] === '计划工时（小时）' && !head.includes('负荷率')
  const rowsOk =
    rows.length === resources.length &&
    rows.every((r, i) => r[1] === String(resources[i].taskCount) && r[2] !== '-' && r[2] !== '')
  const dup = rows
    .map((r) => r[0].split('|').map((x) => x.trim()))
    .filter((segs) => segs.some((v, i) => i > 0 && v && v === segs[i - 1]))
  judge(
    '④ 资源表 4 列（资源/任务数/计划工时（小时）/不可用窗口），数值与后端一致且资源名不重复',
    colsOk && rowsOk && dup.length === 0,
    `列头=${JSON.stringify(head)} 行=${rows.length}/${resources.length} 数值一致=${rowsOk} 重复段=${JSON.stringify(dup.slice(0, 3))} 首行=${JSON.stringify(rows[0])}`
  )

  // ⑤ 候选条：0 条不给诱导语；有条时是真链接
  //   ⚠️ 局部变量别叫 pending —— 模块顶层的 CDP 消息 Map 也叫 pending，主函数作用域里会遮蔽它
  const pendingCount = candidate?.pendingCount ?? 0
  const candOk =
    pendingCount === 0
      ? dom.candidate.includes('暂无待确认候选') && !dom.candidateHasLink
      : dom.candidateHasLink && dom.candidate.includes(String(pendingCount))
  judge(
    '⑤ 待确认候选版本：0 条时只显示空态、不给诱导入口',
    candOk,
    `后端 pendingCount=${pendingCount} 页面=${JSON.stringify(dom.candidate?.slice(0, 80))} 有链接=${dom.candidateHasLink}`
  )

  // ⑥ 无错误告警
  judge('⑥ 页面无分块错误告警', (dom.alerts || []).length === 0, `告警=${JSON.stringify(dom.alerts)}`)

  // ⑦ 版本切换：点「当前生效版本」卡片 ⇒ 订单摘要 / 资源负荷 / 域状态跟着换
  //   （2026-11-04 加；原先三块死钉在 active-plan 第一条，用户看得见另外两版却点不动）
  const cardTexts = await safe(
    `[...document.querySelectorAll('.version-card')].map(e=>(e.innerText||'').replace(/\\s+/g,' ').trim())`
  )
  const cardCount = Array.isArray(cardTexts) ? cardTexts.length : 0
  if (cardCount < 2) {
    judge('⑦ 可点版本卡切换查看的版本', false, `版本卡只有 ${cardCount} 张，无法验证切换`)
  } else {
    const target = activePlans[1]
    const targetSummary = await apiGet(`/api/order-query/summary?planVersionId=${target.planVersionId}`, token)
    const targetResources =
      (await apiGet(`/api/overview/resource-bottleneck?planVersionId=${target.planVersionId}&topN=10`, token)) ||
      []

    // 点第二张卡（按版本号定位，避免依赖卡片顺序）
    const clicked = await safe(`(()=>{
      const cards=[...document.querySelectorAll('.version-card')];
      const c=cards.find(e=>(e.innerText||'').includes(${JSON.stringify(target.versionCode)}));
      if(!c) return false; c.click(); return true;
    })()`)

    // 等数据换成目标版本（KPI 合计变成目标版本的 totalCount）
    let kpiAfter = null
    for (let i = 0; i < 20; i++) {
      await sleep(500)
      kpiAfter = await safe(
        `(()=>{const c=[...document.querySelectorAll('.kpi-card')].find(e=>(e.innerText||'').includes('订单合计'));
          return c ? (c.innerText||'').replace(/\\s+/g,' ').trim() : null})()`
      )
      if (kpiAfter && kpiAfter.includes(String(targetSummary?.totalCount))) break
    }

    const after = await safe(`(()=>{
      const t=(e)=>(e.innerText||'').replace(/\\s+/g,' ').trim();
      const cards=[...document.querySelectorAll('.version-card')];
      const sel=cards.find(c=>c.classList.contains('selected'));
      const resPanel=[...document.querySelectorAll('.panel')].find(p=>t(p.querySelector('.panel-header')).includes('资源负荷'));
      const domPanel=[...document.querySelectorAll('.panel')].find(p=>t(p.querySelector('.panel-header')).includes('排程域状态'));
      return {
        selectedCard: sel ? t(sel) : null,
        selectedCount: cards.filter(c=>c.classList.contains('selected')).length,
        scope: t([...document.querySelectorAll('.kpi-card')].find(e=>t(e).includes('订单合计'))?.querySelector('.kpi-ratio')),
        resHead: resPanel ? [...resPanel.querySelectorAll('.resource-table thead th')].map(x=>t(x)) : [],
        resRows: resPanel ? [...resPanel.querySelectorAll('.resource-table tbody tr')].map(r=>[...r.querySelectorAll('td')].map(c=>t(c))) : [],
        domHead: domPanel ? t(domPanel.querySelector('.panel-header')) : null,
        alerts: [...document.querySelectorAll('.el-alert')].map(t)
      };
    })()`)

    const scopeOk = !!after?.scope && after.scope.includes(target.versionCode)
    const rowsOk =
      Array.isArray(after?.resRows) &&
      after.resRows.length === targetResources.length &&
      after.resRows.every((r, i) => r[1] === String(targetResources[i].taskCount))
    const selOk = after?.selectedCount === 1 && !!after.selectedCard?.includes(target.versionCode)
    judge(
      '⑦ 点版本卡可切换查看的版本（订单摘要/资源负荷/域状态随之换）',
      !!clicked && scopeOk && rowsOk && selOk,
      `点了=${clicked} 统计范围=${JSON.stringify(after?.scope)} 资源行=${after?.resRows?.length}/${targetResources.length} 数值一致=${rowsOk} 选中卡=${selOk} 域面板头=${JSON.stringify(after?.domHead?.slice(0, 60))} 告警=${after?.alerts?.length}`
    )
    await shot('03-version-switched')
  }

  const shotPath = await shot('01-overview')
  // 资源表在首屏之下 ⇒ 滚过去再截一张（人眼复核用）
  await evaluate(`(()=>{const e=document.querySelector('.resource-table');
    if(e) e.scrollIntoView({block:'center'}); return 1})()`)
  await sleep(1200)
  const shotRes = await shot('02-resource')
  console.log('\n截图：\n  ' + [shotPath, shotRes].join('\n  '))
  if (consoleLog.length) console.log('\n控制台（前 8 条）：\n  ' + consoleLog.slice(0, 8).join('\n  '))

  const failed = results.filter((r) => !r.ok)
  console.log(`\n判定：${results.length - failed.length}/${results.length} 通过`)
  if (failed.length) process.exitCode = 1
}

main()
  .catch((e) => {
    console.error('ERR', e.stack || e.message)
    process.exitCode = 1
  })
  .finally(async () => {
    clearTimeout(watchdog)
    try {
      ws && ws.close()
    } catch {}
    killChrome()
    setTimeout(() => process.exit(process.exitCode || 0), 500)
  })
