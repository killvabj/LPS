/**
 * 反向破坏自证：缺失规则查询页 撤提示块 + 服务端分页（2026-10-29 方案 A 落码后重写）
 * 覆盖探针：GROUP=setup-unc 的 SET-UNC10 / SET-UNC14 / SET-UNC15
 *
 * 用法：node scripts/selfverify-setup-uncovered-paging.mjs
 *   （逐条把源码改坏 → 对应探针必须转红 → 立刻还原；全红才算脚本有效）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'

const VIEW = 'src/views/Aps/SetupUncovered.vue'
const STORE = 'src/store/modules/aps/setupUncovered.ts'
const API = 'src/api/aps-v1/setup.ts'

/** 把第一个匹配替换掉（命中不到锚点 ⇒ 返回 null，脚本自身失效） */
function replaceOnce(src, from, to) {
  const i = src.indexOf(from)
  if (i < 0) return null
  return src.slice(0, i) + to + src.slice(i + from.length)
}

/** 只在指定函数体内替换（同名语句多处出现时必须用它，否则改错位置） */
function replaceInFunction(src, signature, from, to) {
  const start = src.indexOf(signature)
  if (start < 0) return null
  let depth = 0
  let opened = false
  let end = -1
  for (let i = start; i < src.length; i++) {
    if (src[i] === '{') {
      depth++
      opened = true
    } else if (src[i] === '}') {
      depth--
      if (opened && depth === 0) {
        end = i + 1
        break
      }
    }
  }
  if (end < 0) return null
  const body = src.slice(start, end)
  if (!body.includes(from)) return null
  return src.slice(0, start) + body.replace(from, to) + src.slice(end)
}

const GROUP = 'setup-unc'

const cases = [
  /* ---------- SET-UNC10：KPI 条撤除（反向锁） ---------- */
  {
    id: 'SET-UNC10 KPI 条被加回来（页面）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '    <!-- 统计列表 -->',
        '    <ElRow class="kpi-row"><ElCol :span="6"><ElCard class="kpi-card"><div class="kpi-value">0</div><div class="kpi-label">涉及部门</div></ElCard></ElCol></ElRow>\n    <!-- 统计列表 -->'
      ),
    probe: 'SET-UNC10'
  },
  {
    id: 'SET-UNC10 前端 distinct 分组 getter 复活（store）',
    file: STORE,
    mutate: (s) =>
      replaceOnce(
        s,
        '  // ===== actions =====',
        '  const statsByStage = computed(() => ({}))\n\n  // ===== actions ====='
      ),
    probe: 'SET-UNC10'
  },
  {
    id: 'SET-UNC10 后端 KPI 计数（departmentCount）被渲染到页面',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '    <!-- 统计列表 -->',
        '    <div class="muted">涉及部门 {{ departmentCount }}</div>\n    <!-- 统计列表 -->'
      ),
    probe: 'SET-UNC10'
  },
  {
    id: 'SET-UNC10 store 改回用前端数组长度当总数（stats.value.length）',
    file: STORE,
    mutate: (s) =>
      replaceOnce(
        s,
        '  // ===== actions =====',
        '  const statCount = computed(() => stats.value.length)\n\n  // ===== actions ====='
      ),
    probe: 'SET-UNC10'
  },

  /* ---------- SET-UNC14：提示块撤除（反向锁） ---------- */
  {
    id: 'SET-UNC14 四态说明提示块被加回来',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '    <!-- 统计列表 -->',
        '    <ElAlert type="info" class="legend-alert" title="换型来源四态说明" />\n    <!-- 统计列表 -->'
      ),
    probe: 'SET-UNC14'
  },
  {
    id: 'SET-UNC14 SETUP_SOURCE_META 引用被加回来（死引用回归）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        "import { runApi, setupApi } from '@/api/aps-v1'",
        "import { runApi, setupApi, SETUP_SOURCE_META } from '@/api/aps-v1'"
      ),
    probe: 'SET-UNC14'
  },
  {
    id: 'SET-UNC14 四态说明文案被加回页面',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '查询未命中换型规则的产品转换（按 0 分钟兜底）',
        '换型来源四态说明：查询未命中换型规则的产品转换'
      ),
    probe: 'SET-UNC14'
  },

  /* ---------- SET-UNC15：服务端分页（10-29 方案 A） ---------- */
  {
    id: 'SET-UNC15 页面改回内存切片（pagedStats 复活）',
    file: VIEW,
    mutate: (s) => replaceOnce(s, ':data="stats"', ':data="pagedStats"'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 页面重新引入 stats.value.slice（本地分页回归）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'const page = ref(1)',
        'const pagedStats = computed(() => stats.value.slice(0, 20))\nconst page = ref(1)'
      ),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 分页器 total 改回取前端数组长度',
    file: VIEW,
    mutate: (s) => replaceOnce(s, ':total="total"', ':total="stats.length"'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 翻页不再重新请求（只在本地改页码）',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(s, 'async function onPageChange(', 'await fetchPage()', 'await Promise.resolve()'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 改每页条数不回第 1 页',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(s, 'async function onSizeChange(', 'page.value = 1', 'page.value = 2'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 查询后不回到第 1 页',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(s, 'async function loadStats()', 'page.value = 1', 'page.value = 0'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 重置后页码不归位',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(s, 'function handleReset()', 'page.value = 1', 'page.value = 0'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 序号跨页连续被撤（改回每页从 1 开始）',
    file: VIEW,
    mutate: (s) => replaceOnce(s, ':index="rowIndex"', ''),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 每页条数默认值被改',
    file: VIEW,
    mutate: (s) => replaceOnce(s, 'const pageSize = ref(20)', 'const pageSize = ref(0)'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 取数口不再把页码交给 store（fetchPage 不传 page）',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(
        s,
        'async function fetchPage(',
        '}, page.value, pageSize.value)',
        '}, 1, 20)'
      ),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 api 全量方法偷偷传分页参（CSV 只导当前页）',
    file: API,
    mutate: (s) =>
      replaceInFunction(
        s,
        'async getUncoveredStats(',
        'params: { ...query } as Record<string, unknown>',
        'params: { ...query, pageIndex: 1 } as Record<string, unknown>'
      ),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 store 列表改回全量端点（分页失效）',
    file: STORE,
    mutate: (s) =>
      replaceInFunction(s, 'async function loadStats(', 'getUncoveredStatsPage(', 'getUncoveredStats('),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 store total 不再取后端值（改成本页条数）',
    file: STORE,
    mutate: (s) =>
      replaceInFunction(s, 'async function loadStats(', 'total.value = page.total', 'total.value = page.items.length'),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 store 的 CSV 改走分页端点（静默漏导）',
    file: STORE,
    mutate: (s) =>
      replaceInFunction(s, 'async function exportCsv(', 'getUncoveredStats(', 'getUncoveredStatsPage('),
    probe: 'SET-UNC15'
  },
  {
    id: 'SET-UNC15 页面导出不再 await store 的导出结果',
    file: VIEW,
    mutate: (s) => replaceOnce(s, 'await uncoveredStore.exportCsv()', 'uncoveredStore.exportCsv()'),
    probe: 'SET-UNC15'
  },

  /* ---------- SET-UNC16：排产批次下拉 + 默认最近一次（10-29） ---------- */
  {
    id: 'SET-UNC16 批次列表不再从后端拉（runApi.listRuns 被删）',
    file: VIEW,
    mutate: (s) =>
      replaceInFunction(s, 'async function loadRuns(', 'runApi.listRuns(', 'Promise.resolve('),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 改回手填数字（ElInputNumber 复活）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '        <ElFormItem label="部门">',
        '        <ElFormItem label="旧输入"><ElInputNumber v-model="page" /></ElFormItem>\n        <ElFormItem label="部门">'
      ),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 下拉不再绑定 queryForm.runId',
    file: VIEW,
    mutate: (s) => replaceOnce(s, 'v-model="queryForm.runId"', 'v-model="page"'),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 批次标签丢掉状态',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        '${RUN_STATUS_LABEL[r.status] ?? r.status} · ',
        ''
      ),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 批次时间改回裸 dayjs（治理域本地口径丢失）',
    file: VIEW,
    mutate: (s) => replaceOnce(s, 'formatLocalMonthDayHM(r.startedAt)', "dayjs(r.startedAt).format('MM-DD HH:mm')"),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 批次排序方向反了（最早的排第一）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        'new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()',
        'new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime()'
      ),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 进页默认改回写死 Run=1',
    file: VIEW,
    mutate: (s) => replaceInFunction(s, 'onMounted(', 'queryForm.runId = def.id', 'queryForm.runId = 1'),
    probe: 'SET-UNC16'
  },
  {
    id: 'SET-UNC16 默认取第一个批次（可能落到还没跑完的批次 → 空表）',
    file: VIEW,
    mutate: (s) =>
      replaceOnce(
        s,
        "runOptions.value.find((r) => r.status === 'COMPLETED' || r.status === 'PARTIAL_SUCCESS') ??\n    runOptions.value[0]",
        'runOptions.value[0]'
      ),
    probe: 'SET-UNC16'
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
