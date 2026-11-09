/**
 * APS V1 4号位 — 甘特页 B2/B10/B5/B6-B8 前端收口探针反向破坏自证（2026-11-05）
 *
 * 背景：5号位 2026-11-05 回执《甘特冻结核对 B1 至 B11 逐条答复》——四项已落码，
 *   前端据回执改了 6 处：类型层加 5 个键、详情加 MES 工单号/下发状态、关键路径改三态、
 *   版本悬停补 B10 三键、撤 B6/B7/B8 三个展示位、清单与 CSV 加两列。
 *   新增/改写静态探针：GT85 / GT109 / GT111 / GT113 / GT114 / GT115 / GT116（GT06 守卫项同步收窄）。
 *
 * 用法：node scripts/selfverify-gantt-b2b10.mjs
 *   逐条改坏 ⇒ 对应 GT 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-a-group.mjs：
 *   ① 变异锚点必须命中代码处（MISSING-MUTATION-ANCHOR = 这一条没验过）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 被搜字面量必须**彻底消失**（改名要改到调用形一起断，不能只加后缀）
 *   ④ 字面量出现多次时要一次全改（all）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const SCHEDULE_TYPES = 'src/api/aps-v1/types/schedule.ts'
const COMMON_TYPES = 'src/api/aps-v1/types/common.ts'
const MOCK = 'src/api/aps-v1/__mocks__/fixtures.ts'

const cases = [
  /* ===== GT113：MES 工单号 / 下发状态（详情 + 清单 + CSV 三处同源）===== */
  {
    // 类型层去掉工单号键 ⇒ 应转红
    id: 'GT113',
    file: SCHEDULE_TYPES,
    from: 'mesWorkOrderNo?: string',
    to: 'mesWorkOrderNoX?: string'
  },
  {
    // 类型层去掉下发状态键 ⇒ 应转红
    id: 'GT113',
    file: SCHEDULE_TYPES,
    from: 'dispatchStatus?: string',
    to: 'dispatchStatusX?: string'
  },
  {
    // 详情抽屉不再读工单号 ⇒ 应转红
    id: 'GT113',
    file: GANTT,
    from: 'selectedTask.mesWorkOrderNo',
    to: 'selectedTask.mesWorkOrderNoX',
    all: true
  },
  {
    // 详情抽屉不再读下发状态 ⇒ 应转红
    id: 'GT113',
    file: GANTT,
    from: 'selectedTask.dispatchStatus',
    to: 'selectedTask.dispatchStatusX',
    all: true
  },
  {
    // 任务清单不再显示工单号列 ⇒ 应转红
    id: 'GT113',
    file: GANTT,
    from: 'row.mesWorkOrderNo',
    to: 'row.mesWorkOrderNoX',
    all: true
  },
  {
    // CSV 不再导出工单号列 ⇒ 应转红
    id: 'GT113',
    file: GANTT,
    from: "  'MES工单号',\n",
    to: ''
  },
  {
    // 缺键文案从中性「未建单」改成断言式 ⇒ 应转红
    id: 'GT113',
    file: GANTT,
    from: '未建单',
    to: '没有工单',
    all: true
  },

  /* ===== GT114：关键路径三态 ===== */
  {
    // 退回「二值 + 缺省 '-'」写法（未判定被当成不是关键路径）⇒ 应转红
    id: 'GT114',
    file: GANTT,
    from: 'selectedTask.isCriticalPath === true',
    to: 'selectedTask.isCriticalPath'
  },
  {
    // 「判定为否」分支被摘掉 ⇒ 应转红
    id: 'GT114',
    file: GANTT,
    from: 'selectedTask.isCriticalPath === false',
    to: 'selectedTask.isCriticalPathX === false'
  },
  {
    // 三态文案「未判定」被改掉 ⇒ 应转红
    id: 'GT114',
    file: GANTT,
    from: '未判定',
    to: '未知',
    all: true
  },

  /* ===== GT115：类型层新增键齐全 ===== */
  {
    // 版本三键少一个 ⇒ 应转红
    id: 'GT115',
    file: SCHEDULE_TYPES,
    from: 'dataCutoffTime?: IsoDateTime',
    to: 'dataCutoffTimeX?: IsoDateTime'
  },
  {
    // 触发方式键被改名 ⇒ 应转红
    id: 'GT115',
    file: SCHEDULE_TYPES,
    from: 'runType?: string',
    to: 'runTypeX?: string'
  },

  /* ===== GT116：B6/B7/B8/B4 四个展示位不得复活 ===== */
  {
    // 数量-时间拆分展示位复活（代码里重新读 taskSegments）⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: 'shareCount: t.taskShare?.length ?? 1,',
    to: 'shareCount: t.taskShare?.length ?? 1,\n      taskSegments: t.taskSegments,'
  },
  {
    // 事实类型展示位复活（重新用 factType + 中文映射表）⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: 'shareCount: t.taskShare?.length ?? 1,',
    to: 'shareCount: t.taskShare?.length ?? 1,\n      factTypeLabel: FACT_TYPE_LABELS,'
  },
  {
    // 公共码表里把已删的 FACT_TYPE_LABELS 加回来 ⇒ 应转红
    id: 'GT116',
    file: COMMON_TYPES,
    from: 'export type FactType =',
    to: "export const FACT_TYPE_LABELS: Record<FactType, string> = { FACT: '事实' }\nexport type FactType ="
  },
  {
    // 上游 Stage 阻挡展示位复活 ⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: 'shareCount: t.taskShare?.length ?? 1,',
    to: 'shareCount: t.taskShare?.length ?? 1,\n      upstreamStageBlocked: t.upstreamStageBlocked,'
  },
  {
    // 跨 Domain 阻挡展示位复活（B4：后端 V1 不做、恒缺键 ⇒ 展示位不得复活）⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: '<!-- U04：TaskShare 一个 Task 承接多 Order -->',
    to:
      '<ElAlert v-if="selectedTask.crossDomainBlocked" type="error" :closable="false">\n                    <template #title>外排程域共享设备阻挡</template>\n                  </ElAlert>\n\n                  <!-- U04：TaskShare 一个 Task 承接多 Order -->'
  },
  {
    // 跨域阻挡的死样式被加回来 ⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: '/* U04：TaskShare */',
    to: '.u14-alert {\n  margin-top: 4px;\n}\n\n/* U04：TaskShare */'
  },
  {
    // 样式死代码被加回来 ⇒ 应转红
    id: 'GT116',
    file: GANTT,
    from: '/* Section 16：MES 下发资格 */',
    to: '.segment-section {\n  padding: 12px;\n}\n\n/* Section 16：MES 下发资格 */'
  },
  {
    // mock 又开始造 B8 的数（mock 必须与真实契约同形）⇒ 应转红
    id: 'GT116',
    file: MOCK,
    from: '      mesWorkOrderNo: i % 5 === 0 ?',
    to: '      taskSegments: undefined,\n      mesWorkOrderNo: i % 5 === 0 ?'
  },
  {
    // mock 又开始造 B4 跨域阻挡（真实后端恒缺键）⇒ 应转红
    id: 'GT116',
    file: MOCK,
    from: '      mesWorkOrderNo: i % 5 === 0 ?',
    to: '      crossDomainBlocked: true,\n      mesWorkOrderNo: i % 5 === 0 ?'
  },
  {
    // mock 又开始造 B7 事实类型（真实后端恒缺键）⇒ 应转红
    id: 'GT116',
    file: MOCK,
    from: '      mesWorkOrderNo: i % 5 === 0 ?',
    to: "      factType: 'FACT',\n      mesWorkOrderNo: i % 5 === 0 ?"
  },
  {
    // mock 又开始造 B8 上游 Stage 阻挡（真实后端恒缺键）⇒ 应转红
    id: 'GT116',
    file: MOCK,
    from: '      mesWorkOrderNo: i % 5 === 0 ?',
    to: '      upstreamStageBlocked: true,\n      mesWorkOrderNo: i % 5 === 0 ?'
  },

  /* ===== GT85：导出列只取后端已给字段 ===== */
  {
    // CSV 里混进未接入列（执行状态）⇒ 应转红
    id: 'GT85',
    file: GANTT,
    from: "  'MES工单号',\n",
    to: "  'MES工单号',\n  '执行状态',\n"
  },
  {
    // CSV 的下发状态列被摘掉 ⇒ 应转红
    id: 'GT85',
    file: GANTT,
    from: "  '下发状态',\n",
    to: ''
  },

  /* ===== GT109：版本项悬停（B10 三键 + 不新增元信息条）===== */
  {
    // 悬停不再带触发来源 ⇒ 应转红
    id: 'GT109',
    file: GANTT,
    from: "`触发来源：${v.triggeredBy || '未提供'}`,",
    to: "`触发来源：未提供`,"
  },
  {
    // 悬停不再带数据截止时间 ⇒ 应转红
    id: 'GT109',
    file: GANTT,
    from: "`数据截止时间：${v.dataCutoffTime ? formatUtcDateTimeSec(v.dataCutoffTime) : '未提供'}`,",
    to: "`数据截止时间：未提供`,"
  },
  {
    // 把数据截止时间提升成页面可见行（元信息条）⇒ 应转红
    id: 'GT109',
    file: GANTT,
    from: '<ElDescriptionsItem label="关键路径">',
    to: '<ElDescriptionsItem label="数据截止时间">\n                      数据截止时间\n                    </ElDescriptionsItem>\n                    <ElDescriptionsItem label="关键路径">'
  },

  /* ===== GT111：TaskShare 中性文案 / TaskSegments 展示位已撤 ===== */
  {
    // TaskShare 缺数据改成断言式文案 ⇒ 应转红
    id: 'GT111',
    file: GANTT,
    from: '未提供订单份额数据（TaskShare）',
    to: '单 Order 任务'
  },
  {
    // TaskSegments 中性占位又被加回来（展示位复活）⇒ 应转红
    id: 'GT111',
    file: GANTT,
    from: '<!-- Section 十二 / U20：设备故障影响',
    to:
      '<div class="segment-section">未提供数量-时间拆分数据（TaskSegments）</div>\n                  <!-- Section 十二 / U20：设备故障影响'
  },

  /* ===== GT117：delayReasons 行级 objectType（5号位 2026-11-05 Q2-a 放宽落码）===== */
  {
    // 类型层去掉 objectType 可选键 ⇒ 应转红
    id: 'GT117',
    file: SCHEDULE_TYPES,
    from: "objectType?: 'TASK' | 'ORDER'",
    to: "objectTypeX?: 'TASK' | 'ORDER'"
  },
  {
    // 清单「主要原因」不再标订单级（退回裸原因码）⇒ 应转红
    id: 'GT117',
    file: GANTT,
    from: "  return top.objectType === 'ORDER' ? `${text}（订单级）` : text",
    to: '  return text'
  },
  {
    // 抽屉三处「订单级」标签全摘掉 ⇒ 应转红
    id: 'GT117',
    file: GANTT,
    from: 'v-if="r.objectType === \'ORDER\'"',
    to: 'v-if="false"',
    all: true
  },
  {
    // mock 不再造订单级事实（与真契约不同形）⇒ 应转红
    id: 'GT117',
    file: MOCK,
    from: "objectType: 'ORDER',",
    to: "objectTypeX: 'ORDER',"
  },
  {
    // mock 的 4 条任务级事实键全摘 ⇒ 应转红
    id: 'GT117',
    file: MOCK,
    from: "objectType: 'TASK',",
    to: "objectTypeX: 'TASK',",
    all: true
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'gantt' },
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
