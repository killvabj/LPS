/**
 * APS V1 4号位 — 参数集空块「新增字段」反向破坏自证脚本
 *
 * 用法：node scripts/selfverify-parameter-set-fields.mjs
 *   逐条把 PF01-PF08 的判据改坏 ⇒ 对应探针必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏，所以每条都要自证能失败。
 *
 * ⚠️ 两条自证工具自身的坑（本脚本已规避，别的自证脚本也照此写）：
 *   ① 变异锚点必须命中**代码处**，不能命中注释（探针读源码时会 stripComments，
 *      改注释等于没改 ⇒ 探针仍绿 ⇒ 误判成「探针不敏感」）。
 *   ② 一个探针 id 下可能有多行断言，判红要看该 id 的**所有行**（任一行 ❌ 即算红）。
 *   ③ 变异锚点要**唯一**：`buffer.value.dirty[block] = true` 在 store 里有两处
 *      （addBlockField 缩进 6 空格 / writeWarehouseArray 缩进 4 空格），
 *      只用字符串 replace 会改错那一处 ⇒ 探针仍绿 ⇒ 误判。
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

const cases = [
  {
    id: 'PF01',
    file: CATALOG,
    from: "path: 'Trigger.ProtectDelayed'",
    to: "path: 'Trigger.ProtectDelayedX'"
  },
  {
    id: 'PF01',
    file: CATALOG,
    from: 'lock: LOCK_FIELDS',
    to: 'lock: LOCK_FIELDSX'
  },
  {
    id: 'PF02',
    file: CATALOG,
    from: "{ path: 'WarnOnlyOnMaxImpacted', label: '超阈值仅告警不停止', kind: 'BOOL' }",
    to: "{ path: 'PlanningYields', label: '采购良率', kind: 'TEXT' }"
  },
  {
    id: 'PF03',
    file: CATALOG,
    from: "{ path: 'Mode', label: '求解方向', kind: 'ENUM' }",
    to: "{ path: 'Mode', label: '求解方向', kind: 'ENUM', default: 0 }"
  },
  {
    id: 'PF03',
    file: VIEW,
    from: 'addFieldBool.value === undefined ? null : addFieldBool.value',
    to: 'addFieldBool.value'
  },
  {
    id: 'PF04',
    file: VIEW,
    from: ':disabled="!actions.canEdit"\n                      @click="openAddFieldDialog(blockKey)"',
    to: ':disabled="false"\n                      @click="openAddFieldDialog(blockKey)"'
  },
  {
    id: 'PF05',
    file: STORE,
    from: '      buffer.value.dirty[block] = true',
    to: '      // 置脏被删掉'
  },
  {
    id: 'PF06',
    file: STORE,
    from: '  function addBlockField(block: BlockKey, path: string[], value: JsonValue): boolean {',
    to:
      '  function addBlockField(block: BlockKey, path: string[], value: JsonValue): boolean {\n    await parameterSetApi.listParameterSetVersions(1)'
  },
  {
    id: 'PF07',
    file: CATALOG,
    from: "{ path: 'Mode', label: '求解方向', kind: 'ENUM' }",
    to: "{ path: 'Mode', label: '求解方向', kind: 'TEXT' }"
  },
  {
    id: 'PF08',
    file: CATALOG,
    from: '!hasBlockField(blockObj, f.path)',
    to: 'true'
  },
  {
    id: 'PF09',
    file: STORE,
    from: 'removePath(buffer.value.workingBlocks, block, path)',
    to: '// 删除动作被删掉'
  },
  {
    id: 'PF09',
    file: STORE,
    from: 'pruneAnchorOnlyBlock(buffer.value.workingBlocks, block)',
    to: '// 锚点空壳剪枝被删掉'
  },
  {
    id: 'PF09',
    file: DRAFT,
    from: '  delete target[lastKey]',
    to: '  // 删叶子被删掉'
  },
  {
    id: 'PF09',
    file: DRAFT,
    from: 'if (Object.keys(child).length === 0) {',
    to: 'if (false) {'
  },
  {
    id: 'PF09',
    file: VIEW,
    from: 'v-if="row.editable"',
    to: 'v-if="true"'
  },
  {
    id: 'PF09',
    file: VIEW,
    from: ':disabled="!actions.canEdit"\n                            @click="onRemoveField(row)"',
    to: ':disabled="false"\n                            @click="onRemoveField(row)"'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'parameter-set-fields' },
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
