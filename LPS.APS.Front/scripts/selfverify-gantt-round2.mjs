#!/usr/bin/env node
/**
 * 甘特页「第二轮优化」探针反向破坏自证（2026-11-03）
 *
 * 逐条破坏 Gantt.vue / verify-acceptance.mjs ⇒ 对应 GT 断言必须转红 ⇒ 还原 ⇒ 全绿。
 *
 * 覆盖：
 *   GT75 换「时间尺度」时窗口同步换（2026-11-03 窗口+刻度合并后重写）
 *   GT81 关键词搜索（输入框 / 清单与甘特同源）
 *   GT82 窗口只由「时间尺度」驱动（2026-11-03 撤「全览」后重写：真实起止标签 / 换档重取数 / 「全览」不得复活）
 *   GT84 导出 CSV（BOM / 转义 / 同源 / 下载）
 *   GT85 导出列不含未接入字段
 *   GT87 换尺度 ⇒ 窗口按该档跨度重算（锚可见行 + 回显不改窗口）
 *
 * ⚠️ 原 GT83「全览跨度取任务 ∪ 负荷桶并集」、GT86「全览用桶内真实起止」随「全览」入口一并撤销。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const GANTT_VIEW = join(ROOT, 'src', 'views', 'Aps', 'Gantt.vue')

const ORIG = new Map()
for (const f of [GANTT_VIEW]) {
  ORIG.set(f, readFileSync(f, 'utf-8'))
}

function restore() {
  for (const [f, s] of ORIG) writeFileSync(f, s)
}
function run() {
  try {
    return execFileSync(process.execPath, ['scripts/verify-acceptance.mjs'], {
      cwd: ROOT,
      env: { ...process.env, GROUP: 'gantt' },
      encoding: 'utf-8'
    })
  } catch (e) {
    return (e.stdout || '') + (e.stderr || '')
  }
}
function state(out, id) {
  const line = out.split('\n').find((l) => l.includes(id + ' '))
  if (!line) return 'MISS'
  if (line.includes('✅')) return 'PASS'
  if (line.includes('❌')) return 'FAIL'
  return 'MISS'
}
const IDS = ['GT75', 'GT81', 'GT82', 'GT84', 'GT85', 'GT87']
function states(out) {
  return IDS.map((id) => state(out, id))
}

const CASES = [
  {
    name: 'GT75 破坏：换尺度后不再重算窗口（窗口与尺度脱钩）',
    expect: { GT75: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        // ⚠️ 锚点 `applyScaleWindow(unit)` 只命中调用点；定义处是 `applyScaleWindow(unit: DhxGanttScaleUnit)`
        files.get(GANTT_VIEW).replace('applyScaleWindow(unit)', 'applyScaleWindowX(unit)')
      )
    }
  },
  {
    name: 'GT81 破坏：工具栏搜索框不再绑定 keyword',
    expect: { GT81: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace('<ElInput\n          v-model="keyword"', '<ElInput\n          v-model="keywordX"')
      )
    }
  },
  {
    name: 'GT81 破坏：任务清单不再走 matchKeyword（与甘特不同源）',
    expect: { GT81: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace(
          'return kw ? matchKeyword(t, resourceMap) : true',
          'return true'
        )
      )
    }
  },
  {
    name: 'GT82 破坏：窗口标签不再显示实际起止（拿别的东西冒充窗口）',
    expect: { GT82: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        // 2026-11-03 合并后没有「预设 radio」了 ⇒ 窗口状态只由 windowLabel 表达
        files.get(GANTT_VIEW).replace(
          "const from = dayjs(dateRange.value.from).format('MM-DD')",
          "const from = dayjs('2026-01-01').format('MM-DD')"
        )
      )
    }
  },
  {
    name: 'GT82 破坏：把「全览」入口塞回工具栏（同一轴的第二个入口 ⇒ 用户又看不懂哪个在管窗口）',
    expect: { GT82: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace(
          '<span class="label">时间尺度</span>',
          '<span class="label">时间尺度</span>\n<ElButton size="default" text @click="applyFitWindow">全览</ElButton>'
        )
      )
    }
  },
  {
    name: 'GT82 破坏：换档后不按新窗口重取数（后端按窗过滤 ⇒ 还是旧窗的数据）',
    expect: { GT82: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        // ⚠️ 只改 watch(ganttWindow) 里那一处：`loadCurrent(currentGanttWindow())` 在文件里出现多次，
        //    直接 replace 会命中更靠前的调用点 ⇒ watch 块没坏 ⇒ 探针假绿
        files
          .get(GANTT_VIEW)
          .replace(
            'watch(ganttWindow, () => {\n  if (currentVersionId.value !== null) {\n    void scheduleStore.loadCurrent(currentGanttWindow())',
            'watch(ganttWindow, () => {\n  if (currentVersionId.value !== null) {\n    void scheduleStore.loadCurrent()'
          )
      )
    }
  },
  {
    name: 'GT87 破坏：换尺度时窗口跨度不再取该档跨度（一档不再等于一个 (跨度, 粒度) 组合）',
    expect: { GT87: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace('const spanDays = SCALE_SPAN_DAYS[unit] ?? 7', 'const spanDays = 7')
      )
    }
  },
  {
    name: 'GT87 破坏：收窄窗口不再锚在当前可见行（用户正在看的任务被甩出画面）',
    expect: { GT87: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace(
          'const anchor = ganttRef.value?.visibleRowStart?.()',
          'const anchor = undefined'
        )
      )
    }
  },
  {
    name: 'GT87 破坏：去掉「程序回显不改窗口」守卫（改窗口→组件回抛→改窗口 死循环）',
    expect: { GT87: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace('if (!userChanged) return', 'if (false) return')
      )
    }
  },
  {
    name: 'GT84 破坏：导出 CSV 去掉 UTF-8 BOM（Excel 中文乱码）',
    expect: { GT84: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace("new Blob(['\\uFEFF' + lines.join('\\r\\n')]", "new Blob([lines.join('\\r\\n')]")
      )
    }
  },
  {
    name: 'GT84 破坏：CSV 单元格不再转义（引号/逗号破表）',
    expect: { GT84: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace('.replace(/"/g, \'""\')', '')
      )
    }
  },
  {
    name: 'GT85 破坏：导出列塞入后端未接入的字段（MES 工单号）',
    expect: { GT85: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files.get(GANTT_VIEW).replace("  '单位'\n] as const", "  '单位',\n  'MES工单号'\n] as const")
      )
    }
  }
]

let ok = 0
let bad = 0

restore()
{
  const out = run()
  const st = states(out)
  const green = st.every((s) => s === 'PASS')
  console.log((green ? '✅' : '❌') + ` 基线：破坏前 ${IDS.length} 条应全绿 → ${st.join(',')}`)
  green ? ok++ : (bad++, console.log(out))
}

for (const c of CASES) {
  restore()
  const files = new Map(ORIG)
  c.break(files)
  let applied = 0
  for (const [f, s] of files) {
    if (s !== ORIG.get(f)) {
      writeFileSync(f, s)
      applied++
    }
  }
  if (applied === 0) {
    console.log(`❌ ${c.name} —— 破坏未生效`)
    bad++
    continue
  }
  const out = run()
  const want = c.expect
  const pass = Object.entries(want).every(([id, v]) => state(out, id) === v)
  console.log(
    (pass ? '✅' : '❌') +
      ` ${c.name} ⇒ ${Object.entries(want)
        .map(([id, v]) => `${id}=${state(out, id)}（期望 ${v}）`)
        .join(' ')}`
  )
  if (!pass) {
    bad++
    console.log(out.split('\n').filter((l) => l.includes('GT7') || l.includes('GT8')).join('\n'))
  } else ok++
  restore()
}

restore()
{
  const out = run()
  const st = states(out)
  const green = st.every((s) => s === 'PASS')
  console.log((green ? '✅' : '❌') + ` 还原后：应回到全绿 → ${st.join(',')}`)
  green ? ok++ : bad++
}

console.log(`\n自证：通过 ${ok} / 失败 ${bad}`)
process.exit(bad > 0 ? 1 : 0)
