/**
 * 反向破坏自证：⑧ 两个页面瘦身（规则版本对比退出菜单 + 缺失规则查询出口改跳规则集页）
 * 覆盖探针：GROUP=menu M23 / GROUP=rules RP11、RP12、RP13 / GROUP=setup-unc SET-UNC12
 * 用法：node scripts/selfverify-setup-menu-trim.mjs（逐条改坏 → 该探针必须转红 → 立刻还原）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const RULES = 'src/views/Aps/Rules.vue'
const ROUTER = 'src/router/modules/aps.ts'
const SETUP_DIFF = 'src/views/Aps/SetupDiff.vue'
const UNCOVERED = 'src/views/Aps/SetupUncovered.vue'

/** 在某个路由块内删掉第一条 hidden: true */
function dropHiddenInRoute(path) {
  return (s) => {
    const i = s.indexOf(`path: '${path}'`)
    if (i < 0) return null
    const seg = s.slice(i, i + 900)
    if (!/hidden: true,/.test(seg)) return null
    const mutated = seg.replace(/^\s*hidden: true,\r?\n/m, '')
    return s.slice(0, i) + mutated + s.slice(i + seg.length)
  }
}

const cases = [
  {
    id: 'M23 去掉 setup/diff 的 hidden',
    group: 'menu',
    file: ROUTER,
    mutate: dropHiddenInRoute('/aps/setup/diff'),
    probe: 'M23 静态'
  },
  {
    id: 'RP11 下钻不带 query（退回无参跳）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace("void router.push({ path: '/aps/setup/diff', query })", "void router.push({ path: '/aps/setup/diff' })"),
    probe: 'RP11 静态'
  },
  {
    id: 'RP12 SetupDiff 不读 ruleSetId 参数',
    group: 'rules',
    file: SETUP_DIFF,
    mutate: (s) => s.replace('route.query.ruleSetId', 'route.query.ruleSetIdNope'),
    probe: 'RP12 静态'
  },
  {
    id: 'RP13 规则集页不认深链（去掉 applySetupDeepLink）',
    group: 'rules',
    file: RULES,
    mutate: (s) => s.replace('async function applySetupDeepLink', 'async function applySetupDeepLinkOff'),
    probe: 'RP13 静态'
  },
  {
    id: 'RP13 深链里用 router.replace 清参（会重建组件、销毁弹窗）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace(
        "  const ruleSetCode = typeof q.ruleSetCode === 'string' ? q.ruleSetCode : ''",
        "  const ruleSetCode = typeof q.ruleSetCode === 'string' ? q.ruleSetCode : ''\n  void router.replace({ path: '/aps/rules' })"
      ),
    probe: 'RP13 静态'
  },
  {
    id: 'RP14 改完 ⑦ 不重跑字段级 diff（版本对比页签看不到变更行 → 蓝色入口不出现）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace(
        '  await refreshSetupSummary()\n  await refreshDiff()\n}',
        '  await refreshSetupSummary()\n}'
      ),
    probe: 'RP14 静态'
  },
  {
    id: 'RP14 refreshDiff 不真正调 loadDiff（重跑函数变空壳）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace('  await rulesStore.loadDiff(base, target)', '  await Promise.resolve()'),
    probe: 'RP14 静态'
  },
  {
    id: 'SET-UNC12 出口退回只跳 /aps/setup/default',
    group: 'setup-unc',
    file: UNCOVERED,
    mutate: (s) =>
      s.replace("void router.push({ path: '/aps/rules', query })", "void router.push({ path: '/aps/setup/default', query })"),
    probe: 'SET-UNC12 静态'
  }
]

let allRed = true
for (const c of cases) {
  const original = readFileSync(c.file, 'utf8')
  const mutated = c.mutate(original)
  if (mutated == null || mutated === original) {
    console.log(`✗ ${c.id}：变异锚点未命中（脚本自身失效）`)
    allRed = false
    continue
  }
  writeFileSync(c.file, mutated, 'utf8')
  let red = false
  try {
    const r = spawnSync(process.execPath, ['scripts/verify-acceptance.mjs'], {
      env: { ...process.env, GROUP: c.group },
      encoding: 'utf8'
    })
    const line = String(r.stdout || '')
      .split('\n')
      .find((l) => l.includes(c.probe))
    red = !!line && line.includes('❌')
    console.log(`${red ? '✓ RED' : '✗ 仍是绿的'}  ${c.id} → ${c.probe}`)
    if (!red) console.log(`    实际行：${line ?? '(未输出)'}`)
    if (!red) allRed = false
  } finally {
    writeFileSync(c.file, original, 'utf8')
  }
}

console.log(allRed ? '\n全部变异均转红 ✅' : '\n存在未转红的变异 ❌')
process.exit(allRed ? 0 : 1)
