/**
 * APS V1 4号位 — 整版发布权限收口 + ⑦ 只读汇总 探针反向破坏自证（2026-10-27）
 *
 * 背景：0号位「规则集多主题协作与统一发布」v1.0（丙案）落地 ⇒ 新增 RP01-RP07：
 *   - Setup 侧整版发布端点前端不封装、不调用（整版发布只走 aps.rule.publish）
 *   - 版本下拉显示「规则集编码 · 名称 / 版本号」；?ruleSetVersionId= 指定了就必须是那一版
 *   - 规则集页只读汇总 ⑦，发布前 ①/⑦ 两块都要人工核对
 *
 * 用法：node scripts/selfverify-ruleset-publish.mjs
 *   逐条把判据依赖的源码改坏 ⇒ 对应 RP 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 五条纪律（同 selfverify-strategy-default.mjs）：
 *   ① 变异锚点必须命中代码处，不能命中注释（探针 stripComments）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 锚点里的 \n 按文件实际换行符替换（CRLF / LF 混用）
 *   ④ 被搜字面量必须彻底消失（追加 X 仍是子串 ⇒ 照绿）
 *   ⑤ 字面量出现多次时要一次全改
 *
 * GROUP=rules 是纯静态组，不登录，无 dev 登录频控问题。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const API_SETUP = 'src/api/aps-v1/setup.ts'
const STORE_SETUP_DIFF = 'src/store/modules/aps/setupDiff.ts'
const VIEW_RULES = 'src/views/Aps/Rules.vue'
const VIEW_DEFAULT = 'src/views/Aps/SetupDefault.vue'
const COMPOSABLE_LABELS = 'src/views/Aps/composables/ruleSetVersionLabels.ts'

const cases = [
  /* ===== RP01：Setup 侧整版发布三处全撤 ===== */
  {
    // API 层把「只凭 aps.setup.publish 发整版」的调用封装加回来
    id: 'RP01',
    file: API_SETUP,
    from: '  /* ===== 13. 发布：前端不封装 =====',
    to:
      '  async publishRuleSetVersion(id: number): Promise<void> {\n' +
      '    await apsHttp.post<void>({ url: `/api/governance/rule-set-versions/${id}/publish` })\n' +
      '  },\n\n' +
      '  /* ===== 13. 发布：前端不封装 ====='
  },
  {
    // store 重新长出 publish action（页面就有地方能发整版）
    id: 'RP01',
    file: STORE_SETUP_DIFF,
    from: 'export const useSetupDiffStore = defineStore(',
    to:
      'async function publish(): Promise<void> {}\n\n' +
      'export const useSetupDiffStore = defineStore('
  },

  /* ===== RP02：版本下拉显示规则集身份，取不到时只退版本号 ===== */
  {
    // 取不到身份时编造规则集名（而不是如实退版本号）
    id: 'RP02',
    file: COMPOSABLE_LABELS,
    from: 'if (!code) return v.versionCode',
    to: "if (!code) return `未命名规则集 / ${v.versionCode}`"
  },
  {
    // 把旧的 GET /rule-sets 依赖加回来（planner 无 aps.rule.view ⇒ 下拉又只剩版本号）
    id: 'RP02',
    file: COMPOSABLE_LABELS,
    from: 'export function formatRuleSetVersionLabel',
    to:
      "export async function loadRuleSetLabels() {\n  await ruleApi.listRuleSets()\n}\n\n" +
      'export function formatRuleSetVersionLabel'
  },

  /* ===== RP03：?ruleSetVersionId= 指定了就必须是那一版 ===== */
  {
    // 指定版本找不到时报错文案被抹（分支仍在，但用户无从判断为什么空选）
    id: 'RP03',
    file: VIEW_DEFAULT,
    from: "ElMessage.warning('指定的规则集版本不存在或已删除，请重新选择版本')",
    to: "ElMessage.warning('选择无效')"
  },

  /* ===== RP04：规则集页 ⑦ 页签只读（只调两个 GET） ===== */
  {
    // 页面改调写端点 ⇒ 规则集页偷偷变成了维护入口
    id: 'RP04',
    file: VIEW_RULES,
    from: 'setupApi.listDefaultRules(versionId)',
    to: 'setupApi.listDefaultRulesX(versionId)'
  },

  /* ===== RP05：两个非持久化确认勾选共同门控发布 ===== */
  {
    // 确认门控退化成「只看发布权限」⇒ 两个勾选框变成装饰
    id: 'RP05',
    file: VIEW_RULES,
    from: '() => canPublishAction.value && confirmPriority.value && confirmSetup.value',
    to: '() => canPublishAction.value'
  },

  /* ===== RP06：⑦ 差异不再直出整串 JSON ===== */
  {
    // 源版本列退回整串 JSON（用户的噩梦：一屏转义字符）
    id: 'RP06',
    file: VIEW_RULES,
    from: 'v-if="row.setupCounts.source"',
    to: 'v-if="row.sourceValue"'
  },

  /* ===== RP07：无查看权限时不允许盲勾 ⑦ ===== */
  {
    // 勾选框去掉禁用条件 ⇒ 没权限也能勾着确认「已核对」
    id: 'RP07',
    file: VIEW_RULES,
    from: ':disabled="!canViewSetup"',
    to: ':disabled="false"'
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'rules' },
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
    .filter(
      (l) =>
        l.includes('❌') &&
        !mine.includes(l) &&
        !l.includes('失败：') &&
        !l.includes('结果')
    )
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
