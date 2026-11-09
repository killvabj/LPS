/**
 * 反向破坏自证：⑦ 换型规则并入规则集页 + 两个维护菜单隐藏
 * 覆盖探针：GROUP=menu M22 / GROUP=rules RP04、RP08、RP09、RP10
 * 用法：node scripts/selfverify-setup-merge.mjs（逐条改坏 → 该探针必须转红 → 立刻还原）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const RULES = 'src/views/Aps/Rules.vue'
const ROUTER = 'src/router/modules/aps.ts'

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
    id: 'M22 去掉 setup/default 的 hidden',
    group: 'menu',
    file: ROUTER,
    mutate: dropHiddenInRoute('/aps/setup/default'),
    probe: 'M22 静态'
  },
  {
    id: 'M22 给分组父节点加 hidden',
    group: 'menu',
    file: ROUTER,
    mutate: (s) => {
      const i = s.indexOf("name: 'ApsStrategyConfig'")
      if (i < 0) return null
      const seg = s.slice(i, i + 400)
      return (
        s.slice(0, i) +
        seg.replace(/title: '策略配置',(\r?\n)/, "title: '策略配置',$1      hidden: true,$1") +
        s.slice(i + seg.length)
      )
    },
    probe: 'M22 静态'
  },
  {
    id: 'RP04 复活 goSetupPage 跳转',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace(
        'async function openExactCreate(',
        'function goSetupPage(kind: string): void {\n  void kind\n}\nasync function openExactCreate('
      ),
    probe: 'RP04 静态'
  },
  {
    id: 'RP08 拆掉 SetupDefaultDialog 挂载',
    group: 'rules',
    file: RULES,
    mutate: (s) => s.replace('<SetupDefaultDialog', '<SetupDefaultDialogOff'),
    probe: 'RP08 静态'
  },
  {
    id: 'RP09 权限码错位成 aps.rule.edit',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace(
        "const canEditSetup = computed<boolean>(() => apsAuth.has('aps.setup.edit'))",
        "const canEditSetup = computed<boolean>(() => apsAuth.has('aps.rule.edit'))"
      ),
    probe: 'RP09 静态'
  },
  {
    id: 'RP10 放开一个写按钮的 disabled',
    group: 'rules',
    file: RULES,
    mutate: (s) => s.replace(':disabled="!setupEditable"', ':disabled="false"'),
    probe: 'RP10 静态'
  },
  {
    id: 'RP10 去掉「没有草稿」提示',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      s.replace(
        "ElMessage.warning('当前没有草稿版本，换型规则只能改草稿；请先点「新建草稿」')",
        "ElMessage.warning('请先新建草稿')"
      ),
    probe: 'RP10 静态'
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
