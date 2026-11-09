/**
 * APS V1 4号位 — 参数集仓库四处落点「反向破坏自证」脚本
 *
 * 用法：node scripts/selfverify-warehouse-ref.mjs
 *   逐条把 P21-P27 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 三条自证工具自身的坑（本脚本已规避，别的自证脚本也照此写）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments，
 *      改注释等于没改 ⇒ 探针仍绿 ⇒ 误判成「探针不敏感」）。
 *      —— 典型：想制造「前端硬编码夹具码」不能加注释，得加一行真的常量。
 *   ② 一个探针 id 下可能有多行断言（P22/P24/P25/P27 各有两条），
 *      判红要看该 id 的**所有行**（任一行 ❌ 即算红）。
 *   ③ 变异锚点里的 \n 按**文件实际换行符**替换（仓库里 CRLF / LF 混用）。
 *   ④ 变异必须让**被搜的字面量彻底消失**：`DefaultLtDays` → `DefaultLtDaysX`
 *      仍是子串 ⇒ 探针照绿（`includes` 型判据的通病）。
 *   ⑤ 被搜字面量在文件里出现**多次**时要一次全改（`all: true` ⇒ replaceAll），
 *      否则改掉一处、另一处还在 ⇒ 探针照绿。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const cases = [
  {
    id: 'P21',
    file: 'src/api/aps-v1/warehouseRefs.ts',
    from: "SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriority']",
    to: "SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriorityX']"
  },
  {
    id: 'P21',
    file: 'src/api/aps-v1/warehouseRefs.ts',
    from: 'DefaultLtDays?: number',
    to: 'LtDays?: number'
  },
  {
    id: 'P22',
    file: 'src/api/aps-v1/warehouseDictionary.ts',
    from: "url: '/api/governance-query/warehouses'",
    to: "url: '/api/governance-query/warehousesX'"
  },
  {
    id: 'P22',
    file: 'src/api/aps-v1/warehouseRefs.ts',
    from: "export const SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriority'] as const",
    to: "export const SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriority'] as const\nexport const FIXTURE_SAMPLE = 'PS_READY'"
  },
  {
    // P39：把已隐藏的供应落点编辑器加回页面 ⇒ 必须转红（P23 只判三个采购编辑器，不受影响）
    id: 'P39',
    file: 'src/views/Aps/ParameterSets.vue',
    from: '<template v-if="blockKey === \'procurement\'">',
    to: '<WarehouseRefEditor v-if="blockKey === \'supply\'" kind="supply-priority" title="库存可用仓库优先级" :disabled="!actions.canEdit" />\n                  <template v-if="blockKey === \'procurement\'">'
  },
  {
    id: 'P23',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'function setSupplyWarehousePriority(',
    to: 'function setSupplyWarehousePriorityX('
  },
  {
    // 置脏语句在 store 里有多处（writeStringArray / writeObjectArray / 各 setter）⇒ 必须全改，
    //   只改第一处时探针的正则仍能命中其余 `= true` ⇒ 假绿（本脚本坑 ⑤）
    id: 'P23',
    file: 'src/store/modules/aps/parameterSets.ts',
    all: true,
    from: 'buffer.value.dirty[block] = true',
    to: 'buffer.value.dirty[block] = false'
  },
  {
    // 制造「仓库码可自由文本录入」：往收货仓库列里塞一个 ElInput（原锚点曾是物料列的 ElInput，
    //   2026-10-26 物料列改下拉后该锚点已不存在 ⇒ 改为直接注入自由文本框）
    id: 'P24',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: '<ElTableColumn label="收货仓库" min-width="220">',
    to: '<ElTableColumn label="收货仓库" min-width="220"><ElInput :model-value="row.WarehouseCode ?? \'\'" />'
  },
  {
    id: 'P24',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'store.warehouseDict.map((w) => {',
    to: 'warehouseDictItems.map((w) => {'
  },
  {
    // 物料列退回自由文本
    id: 'P24',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'onChangePurchaseLtMaterial($index, v)',
    to: 'onChangePurchaseLt($index, { MaterialId: String(v) })'
  },
  {
    // 写回值从 masterId 改成物料编码（最危险的退化：不报错、永久不生效）。
    //   `String(m.masterId …)` 在文件里出现多处（选项构建 + 远程搜索去重）⇒ 必须全改（坑 ⑤）
    id: 'P24',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    all: true,
    from: "String(m.masterId ?? '')",
    to: "String(m.code ?? '')"
  },
  {
    // 物料字典来源被掏空（下拉变自造清单）
    id: 'P24',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'materialLookupApi.listActiveMaterials()',
    to: 'Promise.resolve([])'
  },
  {
    id: 'P25',
    file: 'src/views/Aps/ParameterSets.vue',
    all: true,
    from: '个仓库编码在仓库字典里找不到',
    to: '个仓库编码在仓库字典里没有'
  },
  {
    id: 'P25',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'async function checkForkSourceWarehouseRefs(sourceVersionId: number): Promise<void> {',
    to: "async function checkForkSourceWarehouseRefs(sourceVersionId: number): Promise<void> {\n    throw new Error('x')"
  },
  {
    id: 'P26',
    file: 'src/store/modules/aps/parameterSets.ts',
    from: 'if (isWarehouseRefRow(row.block, row.path)) return',
    to: '// 剔行被删掉'
  },
  {
    id: 'P27',
    file: 'src/api/aps-v1/validationMessages.ts',
    from: "WAREHOUSE_REF_CODE = 'INVALID_WAREHOUSE_REF'",
    to: "WAREHOUSE_REF_CODE = 'INVALID_WAREHOUSE_REFX'"
  },
  {
    id: 'P27',
    file: 'src/api/aps-v1/validationMessages.ts',
    all: true,
    from: '请改成字典里的真实仓库编码',
    to: '请改成真实编码'
  },
  {
    // P35：属性文案不再取 warehouseRole（只剩 ERP 短码）
    id: 'P35',
    file: 'src/api/aps-v1/warehouseRefs.ts',
    from: "String(item?.warehouseRole ?? '')",
    to: "String(item?.name ?? '')"
  },
  {
    // P35：编辑器不再用带属性的文案函数
    id: 'P35',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'warehouseOptionLabel(w)',
    to: "String(w.code ?? '')"
  },
  {
    // P36：分组标签被抹掉（其余两处仍在 ⇒ 分组渲染数从 3 降到 2）
    id: 'P36',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'label="原材料库"',
    to: 'label="其它仓库"'
  },
  {
    // P36：采购类两个落点只留一个（默认采购提前期不再分组）
    id: 'P36',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: "props.kind === 'procurement-priority' || props.kind === 'purchase-lt'",
    to: "props.kind === 'procurement-priority'"
  },
  {
    // P37：把供应库存仓库优先级也拉进分组（5号位 明确它不分组）
    //   —— 只加 supply-priority、保留 purchase-lt ⇒ P36 的落点判定仍成立，只让 P37 转红
    id: 'P37',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: "props.kind === 'procurement-priority' || props.kind === 'purchase-lt'",
    to: "props.kind === 'procurement-priority' || props.kind === 'purchase-lt' || props.kind === 'supply-priority'"
  },
  {
    // P38：下拉只给 6 位码（按码长截断 = 硬过滤的一种）
    id: 'P38',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: "const code = String(w.code ?? '').trim()",
    to: "const code = String(w.code ?? '').trim().slice(0, 6)"
  },
  {
    // P38：下拉按属性硬过滤（把全量字典筛成只剩原材料库）
    id: 'P38',
    file: 'src/views/Aps/components/WarehouseRefEditor.vue',
    from: 'store.warehouseDict.map((w) => {',
    to: "store.warehouseDict.filter((w) => w.erpProperty === 'KS').map((w) => {"
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-warehouse' },
    encoding: 'utf8'
  })
  return (r.stdout || '') + (r.stderr || '')
}

let allOk = true
for (const c of cases) {
  const p = join(ROOT, c.file)
  const orig = readFileSync(p, 'utf8')
  // 变异锚点里的 \n 按**文件实际换行符**替换（仓库里 CRLF / LF 混用）
  const eol = orig.includes('\r\n') ? '\r\n' : '\n'
  const from = c.from.split('\n').join(eol)
  const to = c.to.split('\n').join(eol)
  if (!orig.includes(from)) {
    console.log(`${c.id} (${c.file}): MISSING-MUTATION-ANCHOR`)
    allOk = false
    continue
  }
  writeFileSync(p, c.all ? orig.replaceAll(from, to) : orig.replace(from, to), 'utf8')
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
