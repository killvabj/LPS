/**
 * APS V1 4号位 — 参数集「暂未生效」标注 探针反向破坏自证（2026-10-27）
 *
 * 背景：3号位 2026-10-26《参数集无运行时消费者字段核证回执》（13 项一致 / 1 项纠正）
 *   ⇒ 页面口径 B（字段级「暂未生效」），范围只到「本轮不接入」22 个字段
 *     （2026-11-06 锁块新增成熟度两项，3号位 实施包 v1.0）：
 *     P35 清单精确匹配（且不得混入已消费/待激活字段）
 *     P36 参数表行按 row.inert 标注
 *     P37 新增字段候选标注 + 弹窗大白话说明
 *     P38 整块无效（锁定策略）块标题标注
 *     P39 用户教学文档「不生效」清单与代码真源一致
 *
 * 2026-11-07 扩：后端接上需求保护消费链（`FrozenStrategySnapshotProvider.cs:177` 装参数集
 *   版本内容 `$.Lock` → `PeggingOrchestrator.cs:13279` 取块 → `DemandProtectionTriggerEvaluator`
 *   判定 → `ProtectionStatus` → 需求排序），候选守门两项也早已由 `PhaseFourLocalRepair` 接线
 *   ⇒ 22 项收窄到 12 项，并新增 P43「撤标守门」：
 *     P43 后端已接线的 10 项不得再出现在清单里，且接线证据仍在
 *
 * 用法：node scripts/selfverify-parameter-inert.mjs
 *   逐条改坏 ⇒ 对应 P 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 五条纪律同 selfverify-strategy-default.mjs：
 *   ① 变异锚点必须命中代码处（探针 stripComments 的组不适用本组，但仍避开注释）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失（追加 X 仍是子串 ⇒ 照绿）
 *   ⑤ 字面量出现多次时要一次全改
 *
 * GROUP=parameter-set-static 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const CATALOG = 'src/api/aps-v1/parameterBlockFields.ts'
const DRAFT = 'src/api/aps-v1/draftBuffer.ts'
const PAGE = 'src/views/Aps/ParameterSets.vue'
const GUIDE = 'docs/APS排产系统_规则集与参数集维护_用户操作教学_v1.1_20261022.md'

const cases = [
  /* ===== P35：清单必须精确 ===== */
  {
    // 把真生效的逾期修正比例混进「暂未生效」清单（页面会对着有效字段说谎）
    id: 'P35',
    file: CATALOG,
    from: "  procurement: ['OverdueMargin.MinimumExtraDays'],",
    to: "  procurement: ['OverdueMargin.MinimumExtraDays', 'OverdueMargin.MarginPercent'],"
  },
  {
    // 行上不再按清单赋值（清单还在，但表格永远不标）
    // （2026-11-07 起 `label` 与 `inert` 之间多了 `hint` 一行 ⇒ 只锚 inert 那行）
    id: 'P35',
    file: DRAFT,
    from: '          inert: isInertBlockField(block, keyPath),\n',
    to: ''
  },
  {
    // 清单少一项（2026-11-07 收窄到 12 项后，少一条同样要红）
    id: 'P35',
    file: CATALOG,
    from: "    'Sticky.RequireReleaseRecord',\n",
    to: ''
  },

  /* ===== P36：行级标注 ===== */
  {
    // 行标签条件失效 ⇒ 已配置的无效字段看不出任何异常
    // （2026-11-07：徽标改成「ElTooltip + 逐字段文案」，条件仍是 row.inert）
    id: 'P36',
    file: PAGE,
    from: 'v-if="row.inert"',
    to: 'v-if="false"'
  },
  {
    // 行标签不取目录文案 ⇒ 徽标永远显示不出「受控废弃」这类逐字段标注
    id: 'P36',
    file: PAGE,
    from: '{{ inertAnnotationOfRow(row).tag }}',
    to: '暂未生效'
  },

  /* ===== P37：新增字段候选标注 + 说明 ===== */
  {
    // 候选选项不再标 ⇒ 用户加完才发现无效
    id: 'P37',
    file: PAGE,
    from: 'v-if="isInertBlockField(addFieldBlock, f.path)"',
    to: 'v-if="false"'
  },
  {
    // 弹窗说明不再走逐字段文案 ⇒ K8 会说出假承诺「接入后自动生效」
    id: 'P37',
    file: PAGE,
    from: '{{ addFieldInertAnnotation.note }}',
    to: '{{ addFieldInertAnnotation.tag }}'
  },
  {
    // 目录里的缺省说明被抹 ⇒ 弹窗大白话没了
    id: 'P37',
    file: CATALOG,
    from: '排产运行期还没读取它，配置后不会改变排产结果；后端接入后自动生效，不用重配。',
    to: '该参数暂未生效'
  },

  /* ===== P38：整块无效的块标题标注 ===== */
  {
    // 锁定策略空块时没有任何提示（用户看不到行级标签）
    id: 'P38',
    file: PAGE,
    from: 'v-if="isFullyInertBlock(blockKey)"',
    to: 'v-if="false"'
  },

  /* ===== P39：用户教学文档的「不生效」清单必须与代码真源一致 ===== */
  {
    // 文档 §十一 清单多一个代码里没有的字段 ⇒ 文档与页面口径分家
    id: 'P39',
    file: GUIDE,
    from: '`Trigger.AllowPmcManualProtection`、`Sticky.RequireReleaseRecord`',
    to: '`Trigger.AllowPmcManualProtectionX`、`Sticky.RequireReleaseRecord`'
  },

  /* ===== P43：撤标守门（后端已接线的字段不得再标「暂未生效」） ===== */
  {
    // 把已接线的触发条件塞回清单 ⇒ 页面对着真生效的字段说「配了没用」
    id: 'P43',
    file: CATALOG,
    from: "    'Trigger.AllowPmcManualProtection',\n",
    to: "    'Trigger.AllowPmcManualProtection',\n    'Trigger.ProtectDelayed',\n"
  },
  {
    // 同理：候选守门两项已由 PhaseFourLocalRepair 消费
    id: 'P43',
    file: CATALOG,
    from: '  candidateGuardrail: []',
    to: "  candidateGuardrail: ['ResourceTopN']"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-static' },
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
  writeFileSync(p, orig.replace(from, to), 'utf8')
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
    .filter(
      (l) => l.includes('❌') && !mine.includes(l) && !l.includes('失败：') && !l.includes('结果')
    )
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
