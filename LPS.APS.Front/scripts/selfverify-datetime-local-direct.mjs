/**
 * 反向破坏自证：治理域时间「本地直显」口径（3号位 2026-10-28 核证回执 §五）
 * 覆盖探针：GROUP=datetime-local 的 DT01 / DT02 / DT03 / DT04
 * 10-29 追加：DT05（RBAC 四页）/ DT06（域定义页）/ DT07（例外口径不变）
 *
 * 为什么不含 TSRC15：TSRC15 在 verifyTaskSplitRuleConfig() 里，该组必须先登录后端
 * （后端 7044 未起时整组中止）。TSRC15 与 DT02 共用 checkTaskSplitLocalDirect()，
 * 同一套变异在免登录组 DT02 上验证即可覆盖判据本身。
 *
 * 用法：node scripts/selfverify-datetime-local-direct.mjs
 *   （逐条把源码改坏 → 对应探针必须转红 → 立刻还原；全红才算脚本有效）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const EXACT = 'src/views/Aps/SetupExact.vue'
const DEFAULT = 'src/views/Aps/SetupDefault.vue'
const TSRC = 'src/views/Aps/TaskSplitRuleConfig.vue'
const DATETIME = 'src/utils/datetime.ts'
const RBAC_USERS = 'src/views/Aps/RbacUsers.vue'
const RBAC_ROLES = 'src/views/Aps/RbacRoles.vue'
const RBAC_PERMS = 'src/views/Aps/RbacPermissions.vue'
const RBAC_SCOPES = 'src/views/Aps/RbacScopes.vue'
const DOMAIN = 'src/views/Aps/Domain.vue'
const RULES = 'src/views/Aps/Rules.vue'
const PARAMSETS = 'src/views/Aps/ParameterSets.vue'

/** 把第一个匹配替换掉（命中不到锚点 ⇒ 返回 null，脚本自身失效） */
function replaceOnce(src, from, to) {
  const i = src.indexOf(from)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

const GROUP = 'datetime-local'

const cases = [
  /* ---------- DT01：Setup 两页 ⑦ 规则更新时间列 ---------- */
  {
    id: 'DT01 SetupExact 更新时间列改回 UTC 解析',
    file: EXACT,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTimeSec(row.updatedAt ?? row.createdAt)',
        'formatUtcDateTimeSec(row.updatedAt ?? row.createdAt)'
      ),
    probe: 'DT01 静态'
  },
  {
    id: 'DT01 SetupDefault import 改回 UTC 版工具（死引用 ⇒ 页面无法编译）',
    file: DEFAULT,
    mutate: (s) =>
      replaceOnce(
        s,
        "import { formatLocalDateTimeSec } from '@/utils/datetime'",
        "import { formatUtcDateTimeSec } from '@/utils/datetime'"
      ),
    probe: 'DT01 静态'
  },
  {
    id: 'DT01 SetupDefault 更新时间列丢掉 createdAt 兜底（只显示 updatedAt）',
    file: DEFAULT,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTimeSec(row.updatedAt ?? row.createdAt)',
        'formatLocalDateTimeSec(row.updatedAt)'
      ),
    probe: 'DT01 静态'
  },

  /* ---------- DT02：批策略页 5 处 ---------- */
  {
    id: 'DT02 生效起止改回 UTC 解析',
    file: TSRC,
    mutate: (s) => replaceOnce(s, 'formatLocalDate(row.effectiveTo)', 'formatUtcDate(row.effectiveTo)'),
    probe: 'DT02 静态'
  },
  {
    id: 'DT02 表格「更新时间」列改回 UTC 解析',
    file: TSRC,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTime(row.updatedAt)', 'formatUtcDateTime(row.updatedAt)'),
    probe: 'DT02 静态'
  },
  {
    id: 'DT02 抽屉「创建时间」改回 UTC 解析',
    file: TSRC,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTime(list.find((x) => x.id === editingId)?.createdAt)',
        'formatUtcDateTime(list.find((x) => x.id === editingId)?.createdAt)'
      ),
    probe: 'DT02 静态'
  },
  {
    id: 'DT02 抽屉「最后更新」改回 UTC 解析',
    file: TSRC,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTime(list.find((x) => x.id === editingId)?.updatedAt)',
        'formatUtcDateTime(list.find((x) => x.id === editingId)?.updatedAt)'
      ),
    probe: 'DT02 静态'
  },
  {
    id: 'DT02 import 整体回退成 UTC 版工具',
    file: TSRC,
    mutate: (s) =>
      replaceOnce(
        s,
        "import { formatLocalDate, formatLocalDateTime } from '@/utils/datetime'",
        "import { formatUtcDate, formatUtcDateTime } from '@/utils/datetime'"
      ),
    probe: 'DT02 静态'
  },
  {
    id: 'DT02 多出一处裸 dayjs 解析（绕过 util 的口子）',
    file: TSRC,
    mutate: (s) =>
      replaceOnce(
        s,
        '<span class="muted small">{{ formatLocalDateTime(row.updatedAt) }}</span>',
        '<span class="muted small">{{ formatLocalDateTime(row.updatedAt) }}</span>\n            <span>{{ dayjs(row.createdAt).format(\'YYYY-MM-DD\') }}</span>'
      ),
    probe: 'DT02 静态'
  },

  /* ---------- DT03 / DT04：工具实现本体的两套口径不得互换 ---------- */
  {
    id: 'DT03 formatLocalDate 内部改成 UTC 位移（页面看着没变，值仍错 8h）',
    file: DATETIME,
    mutate: (s) =>
      replaceOnce(
        s,
        "return s ? dayjs(s).format('YYYY-MM-DD') : '—'",
        "return s ? dayjs.utc(s).local().format('YYYY-MM-DD') : '—'"
      ),
    probe: 'DT03 静态'
  },
  {
    id: 'DT03 formatLocalDateTime 内部改成 UTC 位移',
    file: DATETIME,
    mutate: (s) =>
      replaceOnce(
        s,
        "return s ? dayjs(s).format('YYYY-MM-DD HH:mm') : '—'",
        "return s ? dayjs.utc(s).local().format('YYYY-MM-DD HH:mm') : '—'"
      ),
    probe: 'DT03 静态'
  },
  {
    id: 'DT04 formatUtcDate 内部被改成裸解析（外部 ODS/MES 源会错 8h）',
    file: DATETIME,
    mutate: (s) =>
      replaceOnce(
        s,
        "return s ? dayjs.utc(s).local().format('YYYY-MM-DD') : '—'",
        "return s ? dayjs(s).format('YYYY-MM-DD') : '—'"
      ),
    probe: 'DT04 静态'
  },

  /* ---------- DT05：RBAC 四页时间列（用户/角色/权限码/业务范围）---------- */
  {
    id: 'DT05 RbacUsers import 改回 UTC 版工具',
    file: RBAC_USERS,
    mutate: (s) =>
      replaceOnce(
        s,
        "import { formatLocalDateTimeSec } from '@/utils/datetime'",
        "import { formatUtcDateTimeSec } from '@/utils/datetime'"
      ),
    probe: 'DT05 静态'
  },
  {
    id: 'DT05 RbacUsers 「最后登录」改回 UTC 解析',
    file: RBAC_USERS,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTimeSec(row.lastLoginTime)',
        'formatUtcDateTimeSec(row.lastLoginTime)'
      ),
    probe: 'DT05 静态'
  },
  {
    id: 'DT05 RbacUsers 「创建时间」改回 UTC 解析',
    file: RBAC_USERS,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTimeSec(row.createdAt)', 'formatUtcDateTimeSec(row.createdAt)'),
    probe: 'DT05 静态'
  },
  {
    id: 'DT05 RbacRoles 「创建时间」改回 UTC 解析',
    file: RBAC_ROLES,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTimeSec(row.createdAt)', 'formatUtcDateTimeSec(row.createdAt)'),
    probe: 'DT05 静态'
  },
  {
    id: 'DT05 RbacPermissions 「创建时间」改回 UTC 解析',
    file: RBAC_PERMS,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTimeSec(row.createdAt)', 'formatUtcDateTimeSec(row.createdAt)'),
    probe: 'DT05 静态'
  },
  {
    id: 'DT05 RbacScopes 「创建时间」改回 UTC 解析',
    file: RBAC_SCOPES,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTimeSec(row.createdAt)', 'formatUtcDateTimeSec(row.createdAt)'),
    probe: 'DT05 静态'
  },

  /* ---------- DT06：域定义页 ---------- */
  {
    id: 'DT06 Domain.vue import 改回 UTC 版工具',
    file: DOMAIN,
    mutate: (s) =>
      replaceOnce(
        s,
        "import { formatLocalDateTime } from '@/utils/datetime'",
        "import { formatUtcDateTime } from '@/utils/datetime'"
      ),
    probe: 'DT06 静态'
  },
  {
    id: 'DT06 Domain.vue 「创建时间」改回 UTC 解析',
    file: DOMAIN,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTime(currentDetail.createdAt)',
        'formatUtcDateTime(currentDetail.createdAt)'
      ),
    probe: 'DT06 静态'
  },
  {
    id: 'DT06 Domain.vue 「更新时间」改回 UTC 解析',
    file: DOMAIN,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatLocalDateTime(currentDetail.updatedAt)',
        'formatUtcDateTime(currentDetail.updatedAt)'
      ),
    probe: 'DT06 静态'
  },
  {
    id: 'DT06 Domain.vue 「扫描时间」改回 UTC 解析',
    file: DOMAIN,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTime(row.scannedAt)', 'formatUtcDateTime(row.scannedAt)'),
    probe: 'DT06 静态'
  },

  /* ---------- DT07：例外口径不变（内存值仍走 UTC）---------- */
  {
    id: 'DT07 ParameterSets 校验时间 ValidatedAt 被改成本地解析',
    file: PARAMSETS,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatUtcDateTime(lastValidation.validatedAt)',
        'formatLocalDateTime(lastValidation.validatedAt)'
      ),
    probe: 'DT07 静态'
  },
  {
    id: 'DT07 ParameterSets 对比时间 ComparedAt 被改成本地解析',
    file: PARAMSETS,
    mutate: (s) =>
      replaceOnce(s, 'formatUtcDateTime(currentDiff.comparedAt)', 'formatLocalDateTime(currentDiff.comparedAt)'),
    probe: 'DT07 静态'
  },
  {
    id: 'DT07 ParameterSets 落库「发布时间」被改成 UTC 解析',
    file: PARAMSETS,
    mutate: (s) =>
      replaceOnce(s, 'formatLocalDateTime(row.publishedAt)', 'formatUtcDateTime(row.publishedAt)'),
    probe: 'DT07 静态'
  },
  {
    id: 'DT07 Rules 校验时间 ValidatedAt 被改成本地解析',
    file: RULES,
    mutate: (s) =>
      replaceOnce(
        s,
        'formatUtcDateTime(lastValidation.validatedAt)',
        'formatLocalDateTime(lastValidation.validatedAt)'
      ),
    probe: 'DT07 静态'
  },
  {
    id: 'DT07 Rules 对比时间 ComparedAt 被改成本地解析',
    file: RULES,
    mutate: (s) =>
      replaceOnce(s, 'formatUtcDateTime(currentDiff.comparedAt)', 'formatLocalDateTime(currentDiff.comparedAt)'),
    probe: 'DT07 静态'
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
      env: { ...process.env, GROUP },
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
