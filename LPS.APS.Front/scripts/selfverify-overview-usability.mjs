#!/usr/bin/env node
/**
 * 排产总览页「逐功能测试」一轮（2026-11-04）探针反向破坏自证
 *
 * 背景：本轮把总览页从「能看」改成「看得懂」——
 *   ① 资源表把恒占位的「负荷率 / 状态」两列换成后端真有的「任务数 / 计划工时」；
 *   ② KPI 写明统计范围（哪个域的哪个版本）；
 *   ③ 排程域状态面板头写明「共 N 个排程域 · 来源 Run X」；
 *   ④ 待确认 Candidate 的「去处理」由死文本改成真链接（按 aps.candidate.confirm 门控）。
 * 对应静态探针 OV14–OV18（见 scripts/verify-acceptance.mjs，GROUP=overview）。
 *
 * 同日追加「查看版本切换」（用户：想看下面两个版本怎么看）：
 *   ⑤ 生效版本卡本身即选择器，点哪张卡，订单摘要 / 资源负荷 / 排程域状态三块就按哪个版本重取；
 *      候选数与版本无关，不跟着切。
 * 对应静态探针 OV20/OV21/OV22 + OV15（口径从 primaryVersion 改为 selectedVersion）。
 *
 * 做法：备份源文件 → 逐个施加一处破坏 → 跑 GROUP=overview →
 *       检查**目标探针**确实转红 → 还原 → 下一条。
 *
 * 纪律（同其它 selfverify）：
 *   ① 变异锚点必须命中代码处（apply 没匹配上 ⇒ 记 MISSING-MUTATION-ANCHOR 视为「没验过」）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 只破坏一处，别顺手改坏其它探针（否则「其它红项」分不清是谁的责任）
 *
 * dev 登录限流 5 次/分钟 ⇒ 本脚本自己登录一次拿 token，后续探针进程全部复用（VERIFY_TOKEN）。
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const BACKUP = join(ROOT, '.selfverify-bak-overview-usability')

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'
const API = process.env.VERIFY_API || 'https://localhost:7044'
async function ensureToken() {
  if (process.env.VERIFY_TOKEN) return
  const r = await fetch(`${API}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ userCode: 'admin', password: 'Admin@123456' })
  })
  const j = await r.json().catch(() => null)
  const t = j?.data?.accessToken
  if (!t) throw new Error('登录失败：HTTP ' + r.status + ' ' + JSON.stringify(j).slice(0, 200))
  process.env.VERIFY_TOKEN = t
}

const VIEW = 'src/views/Aps/Overview.vue'
const STORE = 'src/store/modules/aps/overview.ts'
const FILES = [VIEW, STORE]

const MUTATIONS = [
  {
    target: 'OV14',
    desc: '资源表列头「任务数」改掉（后端真有的列被删掉）',
    file: VIEW,
    apply: (s) => s.replace('<th>任务数</th>', '<th>个数</th>')
  },
  {
    target: 'OV14',
    desc: '资源表任务数单元格不再直读后端 taskCount',
    file: VIEW,
    apply: (s) => s.replace('<td>{{ r.taskCount }}</td>', '<td>{{ r.taskCount + 1 }}</td>')
  },
  {
    target: 'OV14',
    desc: '资源表计划工时不再直读后端 totalPlannedHours',
    file: VIEW,
    apply: (s) => s.replace('<td>{{ fmtHours(r.totalPlannedHours) }}</td>', '<td>{{ fmtHours(0) }}</td>')
  },
  {
    target: 'OV14',
    desc: '负荷率/超载口径提示被摘掉（用户不知道为什么不显示负荷率）',
    file: VIEW,
    apply: (s) => s.replace('负荷率与超载判定暂无口径，暂不显示', '负荷率与超载')
  },
  {
    target: 'OV14',
    desc: '把恒占位的「负荷率」列加回来（两列恒为 -）',
    file: VIEW,
    apply: (s) => s.replace('<th>任务数</th>', '<th>负荷率</th>\n            <th>任务数</th>')
  },
  {
    target: 'OV15',
    desc: 'KPI 统计范围写死「本版本全部订单」（与 3 张生效版本卡对不上号）',
    file: VIEW,
    apply: (s) => s.replace('统计范围：{{ summaryScope }}', '本版本全部订单')
  },
  {
    target: 'OV15',
    desc: 'store 的口径版本不再取 active-plan 第一条（汇总口径漂移）',
    file: STORE,
    apply: (s) => s.replace('activeVersions.value[0] ?? null', 'activeVersions.value[1] ?? null')
  },
  {
    target: 'OV16',
    desc: '域状态面板头不再写总域数（「8 项异常」看不出共几个域）',
    file: VIEW,
    apply: (s) => s.replace('共 {{ domainStatuses.length }} 个排程域', '排程域')
  },
  {
    target: 'OV16',
    desc: '域状态面板头不再写来源排程运行',
    file: VIEW,
    apply: (s) => s.replace('· 来源排程运行 {{ statusRunId }}', '· 来源排程运行')
  },
  {
    target: 'OV17',
    desc: '候选条跳错路由（点了打不开候选确认页）',
    file: VIEW,
    apply: (s) => s.replace("path: '/aps/candidate'", "path: '/aps/candidate-x'")
  },
  {
    target: 'OV17',
    desc: '候选条不再按 aps.candidate.confirm 门控（无权者点了跳 403）',
    file: VIEW,
    apply: (s) => s.replace("'aps.candidate.confirm'", "'aps.candidate.confirmX'")
  },
  {
    target: 'OV17',
    desc: '模板上摘掉权限门控（canConfirmCandidate 不参与判断）',
    file: VIEW,
    apply: (s) =>
      s.replace(
        'v-if="pendingCandidateCount && canConfirmCandidate"',
        'v-if="pendingCandidateCount"'
      )
  },
  {
    target: 'OV17',
    desc: '0 条时的空态文案分支被摘掉',
    file: VIEW,
    apply: (s) => s.replace('pendingCandidateCount === 0', 'pendingCandidateCount === 1')
  },
  {
    target: 'OV17',
    desc: '候选条点击接线断开（按钮点了没反应）',
    file: VIEW,
    apply: (s) => s.replace('@click="goCandidate"', '@click="noop"')
  },
  {
    target: 'OV18',
    desc: '版本卡把 PlanVersion 冗余列 totalOrders 也渲染出来（与实时摘要 53/55 打架）',
    file: VIEW,
    apply: (s) =>
      s.replace(
        '<ElDescriptionsItem label="任务数">',
        '<ElDescriptionsItem label="订单数">\n                {{ v.totalOrders }}\n              </ElDescriptionsItem>\n              <ElDescriptionsItem label="任务数">'
      )
  },
  // ===== 2026-11-04 版本切换（OV20/OV21/OV22）=====
  {
    target: 'OV15',
    desc: '视图的统计范围改回取 primaryVersion（与选中卡对不上号）',
    file: VIEW,
    apply: (s) =>
      s.replace(
        'const selectedVersion = computed(() => overviewStore.selectedVersion)',
        'const selectedVersion = computed(() => overviewStore.primaryVersion)'
      )
  },
  {
    target: 'OV20',
    desc: '版本卡点击接线断开（点了没反应）',
    file: VIEW,
    apply: (s) => s.replace('@click="onSelectVersion(v)"', '@click="noop"')
  },
  {
    target: 'OV20',
    desc: '版本卡键盘不可达（删掉 enter 键接线）',
    file: VIEW,
    apply: (s) => s.replace('@keydown.enter="onSelectVersion(v)"', '@keydown.enter="noop"')
  },
  {
    target: 'OV20',
    desc: '选中卡不再标「查看中」（用户不知道正在看哪个版本）',
    file: VIEW,
    // 锚点必须是模板里那段（文件头注释里也出现「查看中」，只 replace 单词会改到注释 ⇒ 探针仍绿）
    apply: (s) =>
      s.replace(
        'size="small">\n                  查看中\n                </ElTag>',
        'size="small">\n                  已选\n                </ElTag>'
      )
  },
  {
    target: 'OV20',
    desc: '选中卡不再高亮（看不出正在看哪个版本）',
    file: VIEW,
    apply: (s) =>
      s.replace(':class="{ selected: v.planVersionId === selectedVersionId }"', ':class="{}"')
  },
  {
    target: 'OV20',
    desc: 'store 的 selectVersion 签名被改（接线对不上）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'async function selectVersion(planVersionId: number)',
        'async function selectVersion(planVersionId: any)'
      )
  },
  {
    target: 'OV21',
    desc: '订单摘要改回按 primaryVersion 取（切版本后 KPI 不跟着变）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'const planVersionId = selectedVersion.value?.planVersionId',
        'const planVersionId = primaryVersion.value?.planVersionId'
      )
  },
  {
    target: 'OV21',
    desc: '排程域状态改回按 primaryVersion 的 Run 取（切版本后域面板不跟着变）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'selectedVersion.value?.sourceScheduleRunId',
        'primaryVersion.value?.sourceScheduleRunId'
      )
  },
  {
    target: 'OV21',
    desc: '候选数被改成按版本取（候选数其实与版本无关，不该跟着切）',
    file: STORE,
    // 锚点不能写成 `overviewApi.getCandidateSummary()`：eslint --fix 会把调用链折行成
    //   `overviewApi\n  .getCandidateSummary()` ⇒ 老锚点匹配不上（apply 没生效 = 没验过）
    apply: (s) =>
      s.replace(
        '.getCandidateSummary()',
        '.getCandidateSummary(selectedVersion.value?.planVersionId)'
      )
  },
  {
    target: 'OV22',
    desc: '切版本时不重取资源负荷（资源表仍显示旧版本）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'loadOrderSummary(failed), loadResources(failed), loadDomainStatus(failed)',
        'loadOrderSummary(failed), loadDomainStatus(failed)'
      )
  },
  {
    target: 'OV22',
    desc: '切版本时多取候选数（候选数与版本无关，多余往返）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'loadOrderSummary(failed), loadResources(failed), loadDomainStatus(failed)',
        'loadOrderSummary(failed), loadResources(failed), loadCandidate(failed), loadDomainStatus(failed)'
      )
  },
  {
    target: 'OV22',
    desc: '切版本时不清掉上一版的错误（新版本数据旁边挂着旧版报错）',
    file: STORE,
    apply: (s) =>
      s.replace(
        'errors.value = errors.value.filter((e) => !VERSION_SCOPED_SECTIONS.includes(e.section))',
        'errors.value = errors.value'
      )
  },
  // ===== 2026-11-04 任务数口径写明（OV24）=====
  {
    target: 'OV24',
    desc: '版本卡「任务数」不再写范围（用户又把它当窗口内任务数，和甘特页打架）',
    file: VIEW,
    apply: (s) => s.replace('<span class="field-scope">（整个计划区间内）</span>', '')
  },
  {
    target: 'OV24',
    desc: '版本卡「任务数」范围标注误写成「窗口内」（口径反了）',
    file: VIEW,
    // 锚点必须带上模板那段 span：文件头注释里也有一份「（整个计划区间内）」，
    // 裸 replace 会先改到注释 ⇒ 模板没坏 ⇒ 探针照绿（老陷阱：变异锚点命中注释）。
    apply: (s) =>
      s.replace(
        '<span class="field-scope">（整个计划区间内）</span>',
        '<span class="field-scope">（窗口内）</span>'
      )
  },
  {
    target: 'OV24',
    desc: '版本卡「任务数」改读 totalOrders（与甘特页/版本级口径全乱）',
    file: VIEW,
    apply: (s) => s.replace("{{ v.totalTasks ?? '-' }}", "{{ v.totalOrders ?? '-' }}")
  }
]

function backup() {
  rmSync(BACKUP, { recursive: true, force: true })
  mkdirSync(BACKUP, { recursive: true })
  for (const f of FILES) {
    const dst = join(BACKUP, f.replace(/[\\/]/g, '__'))
    writeFileSync(dst, readFileSync(join(ROOT, f), 'utf-8'), 'utf-8')
  }
}

function restore() {
  for (const f of FILES) {
    const src = join(BACKUP, f.replace(/[\\/]/g, '__'))
    if (existsSync(src)) writeFileSync(join(ROOT, f), readFileSync(src, 'utf-8'), 'utf-8')
  }
}

function runProbes() {
  return execFileSync(process.execPath, [join(ROOT, 'scripts/verify-acceptance.mjs')], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'overview' },
    encoding: 'utf-8',
    stdio: ['ignore', 'pipe', 'pipe']
  })
}

function probeRed(output, probeId) {
  const mine = output.split(/\r?\n/).filter((l) => l.includes(probeId))
  return mine.length > 0 && mine.every((l) => l.includes('❌'))
}

backup()
await ensureToken()
let allOk = true
try {
  for (const m of MUTATIONS) {
    const path = join(ROOT, m.file)
    const original = readFileSync(path, 'utf-8')
    // 变异在「统一 LF」的文本上做，写回时还原原文件的换行风格。
    //   起因：eslint --fix 会把 .vue 写成 CRLF ⇒ 多行锚点（`...\n...`）在 CRLF 文件里匹配不上，
    //   apply 没生效却看起来「跑过了」⇒ 必须先把换行归一。
    const eol = original.includes('\r\n') ? '\r\n' : '\n'
    const normalized = original.replace(/\r\n/g, '\n')
    const mutated = m.apply(normalized)
    if (mutated === normalized) {
      console.log(`❌ ${m.target} 破坏未生效（apply 没匹配到目标文本）— ${m.desc}`)
      allOk = false
      continue
    }
    writeFileSync(path, mutated.replace(/\n/g, eol), 'utf-8')
    let out = ''
    try {
      out = runProbes()
    } catch (e) {
      out = (e.stdout || '') + (e.stderr || '')
    } finally {
      writeFileSync(path, original, 'utf-8')
    }
    const red = probeRed(out, m.target)
    const others = out
      .split(/\r?\n/)
      .filter((l) => l.includes('❌') && !l.includes(m.target))
      .map((l) => l.trim().slice(0, 46))
    console.log(
      `${m.target}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | ${m.desc} | 其它红项=${
        others.length ? others.join(' / ') : '无'
      }`
    )
    if (!red) allOk = false
  }
} finally {
  restore()
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
