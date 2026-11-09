/**
 * 反向破坏自证：⑧ 3号位《联合协调报告》§四 待办 #1/#3/#4 + 治理域本地直显
 * 覆盖探针：GROUP=rules RP15 / RP16 / RP17 / RP18、GROUP=menu M24、GROUP=setup-unc SET-UNC13
 *
 * 用法：node scripts/selfverify-rules-readonly-entry.mjs
 *   （逐条把源码改坏 → 该探针必须转红 → 立刻还原；全红才算脚本有效）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const RULES = 'src/views/Aps/Rules.vue'
const APTS = 'src/router/modules/aps.ts'
const UNCOVERED = 'src/views/Aps/SetupUncovered.vue'
const AUDIT = 'src/views/Aps/Audit.vue'

/** 把第一个匹配替换掉（命中不到锚点 ⇒ 返回 null，脚本自身失效） */
function replaceOnce(src, from, to) {
  const i = src.indexOf(from)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

/** 在 anchor 之后替换第一个 from（用于「同名字符串多处」的定点变异） */
function replaceAfter(src, anchor, from, to) {
  const a = src.indexOf(anchor)
  if (a < 0) return null
  const i = src.indexOf(from, a)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

/** 在 anchor 之前替换最后一个 from（属性常排在 @click 之前） */
function replaceBefore(src, anchor, from, to) {
  const a = src.indexOf(anchor)
  if (a < 0) return null
  const i = src.lastIndexOf(from, a)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

const cases = [
  {
    id: 'RP15 收回只读入口（路由去掉 aps.rule.view）',
    group: 'rules',
    file: APTS,
    mutate: (s) =>
      replaceOnce(
        s,
        "apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit', 'aps.rule.publish']",
        "apsRequiredPermissions: ['aps.rule.edit', 'aps.rule.publish']"
      ),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 只读入口放开后写按钮失去门控（新建规则集不再按 canWrite 禁用）',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, ':disabled="!canWrite"', ':disabled="false"'),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 发布按钮失去发布权限门控',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, ':disabled="!canPublishAction"', ':disabled="false"'),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 发布弹窗「确认发布」失去门控',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, ':disabled="!canConfirmPublish"', ':disabled="false"'),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 canConfirmPublish 不再含 canPublishAction',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      replaceOnce(
        s,
        '() => canPublishAction.value && confirmPriority.value && confirmSetup.value',
        '() => confirmPriority.value && confirmSetup.value'
      ),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 停用弹窗「确认停用」失去门控',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      replaceBefore(
        s,
        '@click="onDisable"',
        ':disabled="!canPublishAction"',
        ':disabled="false"'
      ),
    probe: 'RP15 静态'
  },
  {
    id: 'RP15 ⑦ 增删改误用 aps.rule.edit（应按 aps.setup.edit）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      replaceOnce(
        s,
        "const canEditSetup = computed<boolean>(() => apsAuth.has('aps.setup.edit'))",
        "const canEditSetup = computed<boolean>(() => apsAuth.has('aps.rule.edit'))"
      ),
    probe: 'RP15 静态'
  },
  {
    id: 'RP16 「最近修改」列只剩一块表有（默认换型规则那块删掉）',
    group: 'rules',
    file: RULES,
    mutate: (s) => {
      const col = `<ElTableColumn label="最近修改" width="180">
                          <template #default="{ row }">
                            <div>{{ setupRuleModifiedText(row) }}</div>
                            <div class="muted">{{ setupRuleModifiedBy(row) }}</div>
                          </template>
                        </ElTableColumn>
`
      // 删掉第二处（默认换型规则那块）
      const first = s.indexOf(col)
      if (first < 0) return null
      const second = s.indexOf(col, first + col.length)
      if (second < 0) return null
      return s.slice(0, second) + s.slice(second + col.length)
    },
    probe: 'RP16 静态'
  },
  {
    id: 'RP16 最近修改时间戳不再用本地直显',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      replaceOnce(
        s,
        'return `${rule.updatedAt ? \'更新\' : \'创建\'}于 ${formatLocalDateTime(t)}`',
        'return `${rule.updatedAt ? \'更新\' : \'创建\'}于 ${formatUtcDateTime(t)}`'
      ),
    probe: 'RP16 静态'
  },
  {
    id: 'RP17 深链不再认来源版本 Id',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, "Number(q.ruleSetVersionId ?? '')", 'Number(q.ruleSetVersionIdNope ?? \'\')'),
    probe: 'RP17 静态'
  },
  {
    id: 'RP17 来源版本提示块从模板里删掉',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, 'v-if="deepLinkSourceVersion"', 'v-if="deepLinkSourceVersionOff"'),
    probe: 'RP17 静态'
  },
  {
    id: 'RP18 治理域时间改回 UTC 解析（规则集发布时间）',
    group: 'rules',
    file: RULES,
    mutate: (s) => replaceOnce(s, 'formatLocalDateTime(row.publishedAt)', 'formatUtcDateTime(row.publishedAt)'),
    probe: 'RP18 静态'
  },
  {
    id: 'RP18 治理域时间改回 UTC 解析（参数集生效起止）',
    group: 'rules',
    file: 'src/views/Aps/ParameterSets.vue',
    mutate: (s) => replaceOnce(s, 'formatLocalDateTime(row.effectiveFrom)', 'formatUtcDateTime(row.effectiveFrom)'),
    probe: 'RP18 静态'
  },
  {
    id: 'RP18 审计 OccurredAt 改回 UTC 解析',
    group: 'rules',
    file: AUDIT,
    mutate: (s) => replaceOnce(s, 'formatLocalDateTimeSec(row.occurredAt)', 'formatUtcDateTimeSec(row.occurredAt)'),
    probe: 'RP18 静态'
  },
  {
    id: 'RP18 校验时间被一起改成本地直显（应保留 UTC 解析）',
    group: 'rules',
    file: RULES,
    mutate: (s) =>
      replaceOnce(s, 'formatUtcDateTime(lastValidation.validatedAt)', 'formatLocalDateTime(lastValidation.validatedAt)'),
    probe: 'RP18 静态'
  },
  {
    id: 'M24 死码 aps.setup.publish 加回 setup 路由门控（/aps/setup/diff）',
    group: 'menu',
    file: APTS,
    mutate: (s) =>
      // ⚠️ 源文件是 CRLF ⇒ 锚点用**单行**字符串（跨行锚点在 \r\n 下匹配不到）
      // ⚠️ 'apsRequiredPermissions: ['aps.setup.view']' 在本文件出现两次
      //    （/aps/setup/uncovered 与 /aps/setup/diff），必须定点到 diff 才命中 M24 判据
      replaceAfter(
        s,
        "path: '/aps/setup/diff'",
        "apsRequiredPermissions: ['aps.setup.view']",
        "apsRequiredPermissions: ['aps.setup.view', 'aps.setup.publish']"
      ),
    probe: 'M24 静态'
  },
  {
    id: 'SET-UNC13 出口不再带走来源版本 Id',
    group: 'setup-unc',
    file: UNCOVERED,
    mutate: (s) =>
      replaceOnce(s, 'query.ruleSetVersionId = String(row.ruleSetVersionId)', 'query.ruleSetVersionId = String(0)'),
    probe: 'SET-UNC13 静态'
  },
  {
    id: 'SET-UNC13 无 aps.setup.edit 也放行（补规则的前置拦截没了）',
    group: 'setup-unc',
    file: UNCOVERED,
    mutate: (s) =>
      replaceOnce(s, "if (!apsAuth.has('aps.setup.edit')) {", "if (false) {"),
    probe: 'SET-UNC13 静态'
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
    if (!red) {
      allRed = false
      console.log(`    实际行：${line ?? '(未输出)'}`)
    }
  } finally {
    writeFileSync(c.file, original, 'utf8')
  }
}

console.log(allRed ? '\n全部变异均转红 ✅' : '\n存在未转红的变异 ❌')
process.exit(allRed ? 0 : 1)
