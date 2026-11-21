/**
 * APS V1 4号位 — 异常与原因解释页三处真缺陷修复的探针反向破坏自证（2026-11-05）
 *
 * 背景：dev 实测发现「异常与原因解释」页三处真缺陷，本轮前端修掉：
 *   ① KPI 百分比分母取「已加载条数」⇒ dev 上出现「错误 538% / 警告 516%」
 *      （分母 50 条已加载，分子 269/258 是 Summary 端点的全量计数）
 *   ② 排程解释事实列表静默截断：后端 `ExplanationQueryRepository.cs:113` 把 Summary.items
 *      写死成 `QueryAsync(..., severity:"ERROR", take:50)` ⇒ 只看得见 50 条 ERROR，
 *      258 条 INFO 与其它 ObjectType 分组全部不可见，且页面上没有任何提示
 *   ③ 事实 DTO 字段名对不上：后端返 `id`/`createdAt`/四个来源 Id，前端按
 *      `factId`/`occurredAt`/`objectRef` 取 ⇒ 「对象」格恒空、时间恒 `Invalid Date`
 *
 * 新增/改写探针：E27 / E28 / E29 / E30（E31 动态）+ E23 的「加载更多」判据收窄到业务事实问题面板。
 *
 * 2026-11-21（v0.2 稿 §P08「一张异常清单＋行内展开详情」）：E27/E29/E30 的变异锚点改挂
 *   新落点（统计行主体由「需求」改为「异常」；滚动加载改为分页 ⇒ E30 变异锚点改成分页总数）。
 *
 * 用法：node scripts/selfverify-explanation-kpi-facts.mjs
 *   逐条改坏 ⇒ 对应 E 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-b2b10.mjs：
 *   ① 变异锚点必须命中代码处（MISSING-MUTATION-ANCHOR = 这一条没验过）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 被搜字面量必须**彻底消失**（改名要改到调用形一起断，不能只加后缀）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const VUE = 'src/views/Aps/Explanation.vue'
const API = 'src/api/aps-v1/explanation.ts'
const TYPES = 'src/api/aps-v1/types/explanation.ts'
const STORE = 'src/store/modules/aps/explanation.ts'

const cases = [
  /* ===== E27：KPI 百分比分母 ===== */
  {
    // DTO 去掉 totalCount ⇒ E27 转红
    id: 'E27',
    file: TYPES,
    from: '  totalCount: number',
    to: '  totalCountX: number'
  },
  {
    // store 的 totalFacts 退回「已加载条数」⇒ E27 转红（这正是原始 bug）
    //   2026-11-17 起 totalFacts 允许为 null（后端没返 totalCount 时页面显示「未提供」）⇒ 锚点跟着改
    id: 'E27',
    file: STORE,
    from: 'const totalFacts = computed<number | null>(() => data.value?.totalCount ?? null)',
    to: 'const totalFacts = computed(() => data.value?.scheduleExplanationFacts?.length ?? 0)'
  },
  {
    // view 计数行改用已加载条数 ⇒ E27 转红（2026-11-10 KPI 移除后，计数行是总数唯一的落点）
    id: 'E27',
    file: VUE,
    from: '共 {{ totalFactsText }} 条 · 已显示 {{ loadedFactsCount }} 条',
    to: '共 {{ loadedFactsCount }} 条 · 已显示 {{ loadedFactsCount }} 条'
  },

  /* ===== E22：分类卡栅格响应式 ===== */
  {
    // 栅格从 auto-fit 改回固定两列 ⇒ E22 转红（窄屏会溢出）
    id: 'E22',
    file: VUE,
    from: 'repeat(auto-fit, minmax(220px, 1fr))',
    to: 'repeat(2, 1fr)'
  },

  /* ===== E28：列表端点分页加载 ===== */
  {
    // 又用回 Summary.items（50 条 ERROR）⇒ E28 转红
    id: 'E28',
    file: API,
    from: 'scheduleExplanationFacts: facts,',
    to: 'scheduleExplanationFacts: summary.items ?? [],'
  },
  {
    // 列表端点调用被摘掉 ⇒ E28 转红（两处都改，否则 listFacts 里那处仍匹配 ⇒ 假绿）
    id: 'E28',
    file: API,
    from: "url: '/api/explanation',",
    to: "url: '/api/explanation-disabled',",
    all: true
  },
  {
    // skip 参数不传（分页失效 ⇒ 每页都拿前 N 条）⇒ E28 转红
    id: 'E28',
    file: API,
    from: 'params: { planVersionId, skip, take: Math.min(take, EXPLANATION_FACT_PAGE_SIZE) }',
    to: 'params: { planVersionId, take: Math.min(take, EXPLANATION_FACT_PAGE_SIZE) }'
  },
  {
    // 单页上限被放大（后端会夹到 500 ⇒ 前端以为拿全了）⇒ E28 转红
    id: 'E28',
    file: API,
    from: 'export const EXPLANATION_FACT_PAGE_SIZE = 500',
    to: 'export const EXPLANATION_FACT_PAGE_SIZE = 5000'
  },
  {
    // store 的「继续加载」改成覆盖式（把已有条冲掉）⇒ E28 转红
    id: 'E28',
    file: STORE,
    from: 'data.value.scheduleExplanationFacts = [...data.value.scheduleExplanationFacts, ...batch]',
    to: 'data.value.scheduleExplanationFacts = batch'
  },

  /* ===== E29：事实字段映射 ===== */
  {
    // 不做 id→factId 映射 ⇒ E29 转红
    id: 'E29',
    file: API,
    from: 'factId: r.id,',
    to: 'factId: 0,'
  },
  {
    // 不做 createdAt→occurredAt 映射 ⇒ E29 转红
    id: 'E29',
    file: API,
    from: 'occurredAt: r.createdAt,',
    to: 'occurredAt: undefined as unknown as string,'
  },
  {
    // objectRef 不派生（恒空 ⇒ 页面「对象」格空）⇒ E29 转红
    id: 'E29',
    file: API,
    from: 'objectRef: deriveObjectRef(r, evidence),',
    to: "objectRef: '',"
  },
  {
    // 需求号不参与派生 ⇒ E29 转红
    id: 'E29',
    file: API,
    from: "const demandKey = evidenceStr(evidence, 'LogicalDemandKey')",
    to: "const demandKey = ''"
  },
  {
    // 页面不再展示证据摘要 ⇒ E29 转红
    //   2026-11-21（v0.2 稿 §P08 并表）：清单行变量统一为 `row` ⇒ 锚点跟着改
    id: 'E29',
    file: VUE,
    from: 'v-if="row.evidenceSummary"',
    to: 'v-if="false"',
    all: true
  },

  /* ===== E30：计数行 + 继续加载 + 客户端分页 ===== */
  {
    // 计数行被删（又变回静默截断，用户看不出被截断）⇒ E30 转红
    id: 'E30',
    file: VUE,
    from: '共 {{ totalFactsText }} 条 · 已显示 {{ loadedFactsCount }} 条',
    to: '共 {{ totalFactsText }} 条'
  },
  {
    // 「继续加载」按钮去掉 ⇒ E30 转红
    id: 'E30',
    file: VUE,
    from: '@click="onLoadMoreFacts"',
    to: '@click="() => {}"'
  },
  {
    // 客户端分页失效（总数算 0 ⇒ 用户翻不动后面的事实）⇒ E30 转红
    //   2026-11-21（v0.2 稿 §P08 并表）：原先判据是「分组内独立滚动」的 .fact-list，
    //   该清单块已撤（任务/资源/工段级并进一张表）⇒ 改判「分页条总数」。
    id: 'E30',
    file: VUE,
    from: ':total="filteredRows.length"',
    to: ':total="0"'
  },
  {
    // 累计加载上限被撤（dev 最大版本 37 万条 ⇒ 会把浏览器拖死）⇒ E30 转红
    id: 'E30',
    file: API,
    from: 'export const EXPLANATION_FACT_LOAD_MAX = 5000',
    to: 'export const EXPLANATION_FACT_LOAD_MAX = 99999999'
  },

  /* ===== E23 收窄：业务事实问题面板不得有加载更多类按钮 ===== */
  {
    // 往业务事实问题面板里插「加载更多」按钮 ⇒ E23 转红
    id: 'E23',
    file: VUE,
    from: '<span> 业务事实问题 </span>',
    to: '<span> 业务事实问题 </span>\n            <ElButton size="small" @click="() => {}">加载更多</ElButton>'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'e-freeze' },
    encoding: 'utf8'
  })
  return (r.stdout || '') + (r.stderr || '')
}

let allOk = true
for (const c of cases) {
  const p = join(ROOT, c.file)
  const orig = readFileSync(p, 'utf8')
  const eol = orig.includes('\r\n') ? '\r\n' : '\n'
  const from = c.from.split('\n').join(eol)
  const to = c.to.split('\n').join(eol)
  if (!orig.includes(from)) {
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR — ${c.from.slice(0, 60)}`)
    allOk = false
    continue
  }
  writeFileSync(p, c.all ? orig.split(from).join(to) : orig.replace(from, to), 'utf8')
  let out = ''
  try {
    out = run()
  } finally {
    writeFileSync(p, orig, 'utf8')
  }
  const mine = out.split('\n').filter((l) => l.includes(c.id + ' 静态'))
  const red = mine.some((l) => l.includes('❌'))
  const others = out
    .split('\n')
    .filter((l) => l.includes('❌') && !mine.includes(l) && !l.includes('失败：') && !l.includes('结果'))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
