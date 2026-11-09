/**
 * APS V1 4号位 — 策略包「设为默认」探针反向破坏自证（2026-10-26）
 *
 * 背景：`POST /api/governance/strategy-profile/version/{id}/set-default` 已由 3号位 落码，
 *      前端接入按钮 ⇒ 新增 SC11（全链） / SC12（草稿弹窗不再有失效勾选框）。
 *
 * 用法：node scripts/selfverify-strategy-default.mjs
 *   逐条把判据依赖的源码改坏 ⇒ SC11 / SC12 必须转红 ⇒ 再还原。
 *   探针假绿比没探针更坏。
 *
 * ⚠️ 五条纪律（同 selfverify-governance-delete.mjs）：
 *   ① 变异锚点必须命中代码处，不能命中注释（探针 stripComments）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失（追加 X 仍是子串 ⇒ 照绿）
 *   ⑤ 字面量出现多次时要一次全改
 *
 * 纯静态组 `strategy-config-static` 不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const API_SP = 'src/api/aps-v1/strategyProfile.ts'
const TYPES_SP = 'src/api/aps-v1/types/strategyProfile.ts'
const STORE_SP = 'src/store/modules/aps/strategyProfile.ts'
const PAGE_SP = 'src/views/Aps/StrategyProfiles.vue'
const DIALOG_SP = 'src/views/Aps/components/StrategyDraftDialog.vue'

const cases = [
  /* ===== SC11：设默认全链（api + store + 页面） ===== */
  {
    // API 路径写错 ⇒ 调用必 404
    id: 'SC11',
    file: API_SP,
    from: 'url: `/api/governance/strategy-profile/version/${versionId}/set-default`',
    to: 'url: `/api/governance/strategy-profile/version/${versionId}/set-defaultX`'
  },
  {
    // store 的「仅 PUBLISHED 可设默认」前置被放宽到 DRAFT
    id: 'SC11',
    file: STORE_SP,
    from: "if (target.status !== 'PUBLISHED') {",
    to: "if (target.status !== 'DRAFT') {"
  },
  {
    // store 的「失败回列表核对」被抹掉（写接口报错≠没写成）
    id: 'SC11',
    file: STORE_SP,
    from: 'if (after?.isDefault) {',
    to: "if (after?.status === 'PUBLISHED') {"
  },
  {
    // 权限码文案被抹掉（权限门控判据断链）
    id: 'SC11',
    file: STORE_SP,
    from: "error.value = '当前操作者无设为默认权限（需权限码 aps.strategy.edit）'",
    to: "error.value = '当前操作者无设为默认权限'"
  },
  {
    // 页面按钮与 store 脱钩
    id: 'SC11',
    file: PAGE_SP,
    from: 'const ok = await store.setDefault(row.id)',
    to: 'const ok = await store.setDefaultX(row.id)'
  },
  {
    // 二次确认按钮文案被抹掉（破坏性/生产影响操作必须确认）
    id: 'SC11',
    file: PAGE_SP,
    from: "confirmButtonText: '确认设为默认',",
    to: "confirmButtonText: '确认设置',"
  },
  {
    // 页面缺权限门控提示
    id: 'SC11',
    file: PAGE_SP,
    from: "ElMessage.warning('当前操作者无设为默认权限，请联系系统管理员授予')",
    to: "ElMessage.warning('当前操作者无权限')"
  },

  /* ===== SC12：草稿弹窗不得再有失效的「设为默认」勾选框 ===== */
  {
    // 入参重新塞回 isDefault（后端会忽略/冻结 ⇒ 用户勾了也没用）
    id: 'SC12',
    file: DIALOG_SP,
    from: 'effectiveTo: form.effectiveTo ?? null',
    to: 'effectiveTo: form.effectiveTo ?? null,\n    isDefault: false'
  },
  {
    // 类型层重新声明 isDefault（同上，制造可写假象）
    id: 'SC12',
    file: TYPES_SP,
    from: '  effectiveTo?: IsoDateTime | null\n}',
    to: '  effectiveTo?: IsoDateTime | null\n  isDefault: boolean\n}'
  },
  {
    // 页面重新传 has-published（勾选框显隐依赖）
    id: 'SC12',
    file: PAGE_SP,
    from: '      :submitting="actionRunning"',
    to: '      :submitting="actionRunning"\n      :has-published="hasPublished"'
  },
  {
    // 对话框不再解释「发布≠默认」（移除唯一一处说明）
    id: 'SC12',
    file: DIALOG_SP,
    from: '请在版本列表点「设为默认」',
    to: '请在版本列表点「设成默认」'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'strategy-config-static' },
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
    .filter((l) => l.includes('❌') && !mine.includes(l))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
