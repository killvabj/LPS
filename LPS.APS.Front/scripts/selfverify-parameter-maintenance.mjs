/**
 * APS V1 4号位 — 参数集「维护责任分类」过滤反向破坏自证脚本
 *
 * 用法：node scripts/selfverify-parameter-maintenance.mjs
 *   逐条把 PM01-PM06 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *
 * 变异锚点必须命中代码、不能命中注释（探针读源码时 stripComments）。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const CATALOG = 'src/api/aps-v1/parameterBlockFields.ts'
const STORE = 'src/store/modules/aps/parameterSets.ts'
const VIEW = 'src/views/Aps/ParameterSets.vue'
const DRAFT = 'src/api/aps-v1/draftBuffer.ts'
const DIFF = 'src/api/aps-v1/parameterDiff.ts'

const cases = [
  /* PM01 分类登记 */
  {
    id: 'PM01',
    file: CATALOG,
    from: "{ path: 'AllowMerge', label: '允许合批', kind: 'BOOL', maintenance: 'E' }",
    to: "{ path: 'AllowMerge', label: '允许合批', kind: 'BOOL', maintenance: 'F' }"
  },
  {
    id: 'PM01',
    file: CATALOG,
    from: "HIDDEN_MAINTENANCE_CLASSES: readonly MaintenanceClass[] = ['C', 'E', 'F', 'H']",
    to: "HIDDEN_MAINTENANCE_CLASSES: readonly MaintenanceClass[] = ['C', 'E', 'F']"
  },
  /* PM02 flattenBlocks 输出 */
  {
    id: 'PM02',
    file: DRAFT,
    from: 'editable: !hidden && !fixed && isEditable(keyPath)',
    to: 'editable: !fixed && isEditable(keyPath)'
  },
  {
    id: 'PM02',
    file: DRAFT,
    from: 'const hidden = isHiddenBlockField(block, keyPath)',
    to: 'const hidden = false'
  },
  /* PM03 rowsGrouped 过滤 */
  {
    id: 'PM03',
    file: STORE,
    from: '      if (row.hidden) return',
    to: '      if (false) return'
  },
  /* PM04 新增候选排除 */
  {
    id: 'PM04',
    file: CATALOG,
    from: '!isHiddenBlockField(block, f.path) &&',
    to: ''
  },
  /* PM05 D 类只读展示 */
  {
    id: 'PM05',
    file: VIEW,
    from: 'v-if="row.fixed"',
    to: 'v-if="false"'
  },
  {
    id: 'PM05',
    file: VIEW,
    from: 'v-if="!actions.canEdit || !row.editable"',
    to: 'v-if="!actions.canEdit"'
  },
  /* PM06 载荷不受过滤影响 */
  {
    id: 'PM06',
    file: DRAFT,
    from: 'JSON.stringify(blocks.candidateGuardrail)',
    to: 'String(blocks.candidateGuardrail)'
  },
  {
    id: 'PM06',
    file: DRAFT,
    from: '  return rows\n}',
    to: '  return rows.filter(() => true)\n}'
  },
  {
    id: 'PM06',
    file: DIFF,
    from: 'flattenBlocks(normalizeBlocksForDiff(source))',
    to: 'flattenBlocksX(normalizeBlocksForDiff(source))'
  },
  /* PM07 D 类不进「新增字段」候选（2026-11-07；工作台 7 项 D 类） */
  {
    id: 'PM07',
    file: CATALOG,
    from: '!isFixedValueBlockField(block, f.path) &&',
    to: ''
  },
  {
    id: 'PM07',
    file: CATALOG,
    from: "export function isFixedValueBlockField(block: BlockKey, path: string): boolean {\n  return findBlockField(block, path)?.maintenance === 'D'",
    to: "export function isFixedValueBlockField(block: BlockKey, path: string): boolean {\n  return findBlockField(block, path)?.maintenance === 'A'"
  },
  /* PM08 D 类 6 项逐条锁定 */
  {
    id: 'PM08',
    file: CATALOG,
    from: "    path: 'Sticky.RequireReleaseRecord',\n    label: '手工释放需留记录',\n    kind: 'BOOL',\n    maintenance: 'D'",
    to: "    path: 'Sticky.RequireReleaseRecord',\n    label: '手工释放需留记录',\n    kind: 'BOOL',\n    maintenance: 'A'"
  },
  /* PM09 取值域提示写真实字典码 */
  {
    id: 'PM09',
    file: CATALOG,
    from: "hint: '客户等级英文码：VIP 或 GENERAL；按精确匹配判定，大小写要一致'",
    to: "hint: '开启「保护 VIP 客户」时填写'"
  },
  {
    id: 'PM09',
    file: CATALOG,
    from: "hint: '每行一个订单类型英文码：SALES_ORDER（客户订单）或 PRODUCTION_INSTRUCTION（生产指示）'",
    to: "hint: '每行一个订单类型（如 SO / WO）'"
  },
  /* PM10 R1 台账 K1~K12 与目录逐项一致 */
  {
    id: 'PM10',
    file: CATALOG,
    from: "'OverdueMargin.MinimumExtraDays': {\n      k: 'K8',",
    to: "'OverdueMargin.MinimumExtraDaysX': {\n      k: 'K8',"
  },
  {
    id: 'PM10',
    file: CATALOG,
    from: "'Sticky.RequireReleaseRecord': {\n      k: 'K2',\n      cls: 'D',",
    to: "'Sticky.RequireReleaseRecord': {\n      k: 'K2',\n      cls: 'E',"
  },
  {
    id: 'PM10',
    file: CATALOG,
    from:
      "    'StageOverlap.ThresholdQty': {\n      k: 'K11',\n      cls: 'F',\n      treatment: 'V1 不使用 ⇒ 隐藏'\n    },\n",
    to: ''
  },
  /* PM11 标注文案不得是假承诺 */
  {
    id: 'PM11',
    file: CATALOG,
    from:
      "note: '这一项已决定后续受控废弃：排产运行期不会读取它，配置后不会改变排产结果，也不会自动生效。'",
    to:
      "note: '该参数暂未生效：排产运行期还没读取它，配置后不会改变排产结果；后端接入后自动生效，不用重配。'"
  },
  {
    id: 'PM11',
    file: CATALOG,
    from: '等这一项正式开放并接入后自动生效，不用重配。',
    to: '等后端接入后自动生效，不用重配。'
  },
  {
    id: 'PM11',
    file: VIEW,
    from: '{{ addFieldInertAnnotation.note }}',
    to: '该参数暂未生效：排产运行期还没读取它，配置后不会改变排产结果；后端接入后自动生效，不用重配。'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-maintenance' },
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
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR`)
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
    .filter((l) => l.includes('❌') && !l.includes(c.id + ' 静态'))
    .map((l) => l.trim().slice(0, 46))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
