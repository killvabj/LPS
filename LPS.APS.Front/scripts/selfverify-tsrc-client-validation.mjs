/**
 * 反向破坏自证：批策略页前端校验 + 部门筛选（2026-10-28）
 * 覆盖探针：GROUP=tsrc-static 的 TSRC20 / TSRC21 / TSRC22 / TSRC23
 *
 * 对应后端契约（只读参照，后端零改动）：
 *  - TaskSplitRuleConfigGovernanceService.cs:400 写路径拒绝 NULL 部门（实测 422）
 *  - 同文件 :427-446 PreferredBatchQty 必填且 >0，并须落在 [Min, Max]
 *
 * 用法：node scripts/selfverify-tsrc-client-validation.mjs
 *   （逐条把源码改坏 → 对应探针必须转红 → 立刻还原；全红才算脚本有效）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const VIEW = 'src/views/Aps/TaskSplitRuleConfig.vue'
const TYPES = 'src/api/aps-v1/types/taskSplitRuleConfig.ts'
const API = 'src/api/aps-v1/taskSplitRuleConfig.ts'
const STORE = 'src/store/modules/aps/taskSplitRuleConfig.ts'
const AUDIT = 'src/api/aps-v1/types/audit.ts'

/** 把第一个匹配替换掉（命中不到锚点 ⇒ 返回 null，脚本自身失效） */
function replaceOnce(src, from, to) {
  const i = src.indexOf(from)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

/** 正则替换（首个命中）；未命中返回 null（脚本自身失效）。
 *  .vue 模板是 CRLF ⇒ 跨行锚点用 \r?\n，别用固定 \n */
function replaceRegex(src, re, to) {
  if (!re.test(src)) return null
  return src.replace(re, to)
}

const GROUP = 'tsrc-static'

const cases = [
  /* ---------- TSRC20：部门必填 / 去哨兵 / 历史行解锁 ---------- */
  {
    id: 'TSRC20 部门 FormItem 去掉 required',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'prop="productionDepartmentId" required>',
        'prop="productionDepartmentId">'
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 部门校验文案不再点名「生产部门」（规则被判成别的字段）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        "callback(new Error('请选择生产部门（新写入规则必须明确部门）'))",
        "callback(new Error('请选择部门'))"
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 哨兵常量回归（DEPARTMENT_SENTINEL_DEFAULT 重新出现）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'const SCOPE_VIOLATION_PATTERN = /业务范围越界/',
        'const SCOPE_VIOLATION_PATTERN = /业务范围越界/\nconst DEPARTMENT_SENTINEL_DEFAULT = 0'
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 「物料级默认（暂无部门）」下拉项回归',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'placeholder="请选择生产部门（必填）"',
        'placeholder="请选择生产部门（必填）" data-x="1">\n<ElOption label="物料级默认（暂无部门）" :value="0" /><span'
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 编辑历史无部门行不再解锁部门选择器',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        ':disabled="drawerMode === \'edit\' && editingDeptOriginal !== null"',
        ':disabled="drawerMode === \'edit\'"'
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 历史行补选提示 div 被删',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'class="field-hint"',
        'class="field-hint-gone" data-old="field-hint"'
      ),
    probe: 'TSRC20'
  },
  {
    id: 'TSRC20 .field-hint 样式被删（提示文字不显示）',
    file: VIEW,
    mutate: (s) => replaceOnce(s, '.field-hint {', '.field-hint-removed {'),
    probe: 'TSRC20'
  },

  /* ---------- TSRC21：Preferred 必填 + 区间 ---------- */
  {
    id: 'TSRC21 软目标 FormItem 去掉 required',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(s, 'prop="preferredBatchQty" required>', 'prop="preferredBatchQty">'),
    probe: 'TSRC21'
  },
  {
    id: 'TSRC21 必填判断退化为「只拦 null」（放过 0 / 负数）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(s, 'if (value == null || Number(value) <= 0) {', 'if (value == null) {'),
    probe: 'TSRC21'
  },
  {
    id: 'TSRC21 下界校验（不能小于经济最小批量）被撤',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(s, 'if (min != null && Number(value) < min) {', 'if (false) {'),
    probe: 'TSRC21'
  },
  {
    id: 'TSRC21 上界校验（不能大于经济最大批量）被撤',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(s, 'if (max != null && Number(value) > max) {', 'if (false) {'),
    probe: 'TSRC21'
  },

  /* ---------- TSRC22：生产部门筛选 ---------- */
  {
    id: 'TSRC22 筛参 state 声明被破坏',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'const filterDepartmentId = ref<number | undefined>(undefined)',
        'const filterDepartmentId = 0'
      ),
    probe: 'TSRC22'
  },
  {
    id: 'TSRC22 applyFilter 不再传 productionDepartmentId',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(s, 'q.productionDepartmentId = filterDepartmentId.value', 'void 0'),
    probe: 'TSRC22'
  },
  {
    id: 'TSRC22 重置时不清部门筛参',
    file: VIEW,
    mutate: (s) => replaceOnce(s, '  filterDepartmentId.value = undefined', '  // removed'),
    probe: 'TSRC22'
  },
  {
    id: 'TSRC22 筛选栏生产部门下拉被删',
    file: VIEW,
    mutate: (s) => replaceOnce(s, 'v-model="filterDepartmentId"', 'v-model="filterIsActive"'),
    probe: 'TSRC22'
  },
  {
    id: 'TSRC22 ListQuery 类型丢掉 productionDepartmentId',
    file: TYPES,
    mutate: (s) =>
      replaceOnce(s, 'productionDepartmentId?: number', 'productionDepartmentIdGone?: number'),
    probe: 'TSRC22'
  },

  /* ---------- TSRC23：停用后恢复启用 ---------- */
  {
    id: 'TSRC23 api 端点 URL 改错（activate → enable）',
    file: API,
    mutate: (s) =>
      replaceOnce(
        s,
        '/api/governance/task-split-rule-configs/${id}/activate',
        '/api/governance/task-split-rule-configs/${id}/enable'
      ),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 api 端点方法由 POST 改成 PUT',
    file: API,
    mutate: (s) =>
      replaceOnce(
        s,
        'return apsHttp.post<TaskSplitRuleConfigDto>({\n      url: `/api/governance/task-split-rule-configs/${id}/activate`,',
        'return apsHttp.put<TaskSplitRuleConfigDto>({\n      url: `/api/governance/task-split-rule-configs/${id}/activate`,'
      ),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 store 的 activate 动作改调 deactivate（点启用实际停用）',
    file: STORE,
    mutate: (s) => replaceOnce(s, 'taskSplitRuleConfigApi.activate(id)', 'taskSplitRuleConfigApi.deactivate(id)'),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 store 未在 return 暴露 activate',
    file: STORE,
    mutate: (s) => replaceOnce(s, '    activate,\n    reset,', '    reset,'),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 页面「启用」按钮失去停用/启用互斥分支（v-else 被摘）',
    file: VIEW,
    mutate: (s) =>
      replaceRegex(s, /v-else(\r?\n)(\s*)size="small"/, (m, nl, ws) => `v-if="false"${nl}${ws}size="small"`),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 页面「启用」按钮的权限闸被摘（:disabled="!canEdit" 没了）',
    file: VIEW,
    mutate: (s) =>
      replaceRegex(
        s,
        /:disabled="!canEdit"(\r?\n)(\s*)@click="handleActivate\(row\)"/,
        (m, nl, ws) => `@click="handleActivate(row)"`
      ),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 页面「启用」按钮改调 handleDeactivate',
    file: VIEW,
    mutate: (s) => replaceOnce(s, '@click="handleActivate(row)"', '@click="handleDeactivate(row)"'),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 页面启用二次确认框被摘（确认文案改掉）',
    file: VIEW,
    mutate: (s) => replaceOnce(s, "'是否确认恢复启用该批策略？'", "'是否确认？'"),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 审计动作字典丢 Activate',
    file: AUDIT,
    mutate: (s) => replaceOnce(s, "  'Deactivate',\n  'Activate',\n", "  'Deactivate',\n"),
    probe: 'TSRC23'
  },
  {
    id: 'TSRC23 api 的 update 偷偷传了 isActive（PUT 不该改启停）',
    file: API,
    mutate: (s) =>
      replaceOnce(
        s,
        '        ...MOCK_RULES[idx],\n        materialId: input.materialId,',
        '        ...MOCK_RULES[idx],\n        isActive: true,\n        materialId: input.materialId,'
      ),
    probe: 'TSRC23'
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
