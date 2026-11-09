/**
 * APS V1 4号位 — 甘特页 A 组三项探针反向破坏自证（2026-11-03）
 *
 * 背景：领导判「用不了」后的差距清单，A 组 = 纯前端、零后端依赖、当天可上的三项：
 *   A1 按订单 / 按物料分组；A3 只看延期 / 只看工序过滤；A4 任务号写在条内。
 *   新增静态探针 GT91–GT96（见 scripts/verify-acceptance.mjs）。
 *
 * 用法：node scripts/selfverify-gantt-a-group.mjs
 *   逐条改坏 ⇒ 对应 GT 必须转红 ⇒ 再还原。探针假绿比没探针更坏。
 *
 * 纪律同 selfverify-gantt-usability.mjs：
 *   ① 变异锚点必须命中代码处（MISSING-MUTATION-ANCHOR = 这一条没验过）
 *   ② 一个 id 下有多行断言 ⇒ 判红看该 id 的所有行
 *   ③ 被搜字面量必须**彻底消失**（改名要改到调用形一起断，不能只加后缀）
 *   ④ 字面量出现多次时要一次全改（all）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const GANTT = 'src/views/Aps/Gantt.vue'
const DHX = 'src/components/DhxGantt/src/DhxGantt.vue'
const DHX_TYPES = 'src/components/DhxGantt/src/types.ts'

const cases = [
  /* ===== GT91：分组维度选项 ===== */
  {
    // 「按订单」选项被摘掉 ⇒ 下拉里没有这个视角 ⇒ 应转红
    id: 'GT91',
    file: GANTT,
    from: "{ value: 'order', label: '按订单' },",
    to: ''
  },
  {
    // 「按物料」选项被摘掉 ⇒ 应转红
    id: 'GT91',
    file: GANTT,
    from: "{ value: 'material', label: '按物料' }",
    to: "{ value: 'materialX', label: '按物料' }"
  },

  /* ===== GT92：泳道行 id 与任务 parent 同源 ===== */
  {
    // 泳道行 id 前缀被改（与 parent 不一致 ⇒ 任务条找不到自己的行 ⇒ 整批条子静默消失）⇒ 应转红
    id: 'GT92',
    file: GANTT,
    from: 'id: `lane-order-${orderNo}`,',
    to: 'id: `lane-ord-${orderNo}`,'
  },
  {
    // 任务 parent 改成另一套写法 ⇒ 与泳道行 id 不同源 ⇒ 应转红
    id: 'GT92',
    file: GANTT,
    from: "case 'order':\n      return `lane-order-${orderGroupValue(task)}`",
    to: "case 'order':\n      return `lane-ord-${task.orderNo ?? '未标注订单'}`"
  },
  {
    // 泳道侧改了取值助手（与 parent 侧断链）⇒ 应转红
    // ⚠️ 只判「文件里出现过 orderGroupValue」的话，改成 orderGroupValueX 探针照绿（假绿）
    id: 'GT92',
    file: GANTT,
    from: 'const key = orderGroupValue(t)',
    to: 'const key = orderGroupValueX(t)'
  },
  {
    // 分组键改取自自造字段（不是后端已给的 orderNo / materialCode）⇒ 应转红
    id: 'GT92',
    file: GANTT,
    from: 'return t.orderNo?.trim() || ORDER_UNLABELED',
    to: "return (t.taskId as unknown as string) || ORDER_UNLABELED"
  },

  /* ===== GT93：工具栏筛选控件 ===== */
  {
    // 「只看延期」复选框被删 ⇒ 应转红
    id: 'GT93',
    file: GANTT,
    from: '<ElCheckbox v-model="onlyDelayed" class="gantt-filter-check">只看延期</ElCheckbox>',
    to: ''
  },
  {
    // 复选框绑到别的状态上 ⇒ 应转红
    id: 'GT93',
    file: GANTT,
    from: 'v-model="onlyDelayed"',
    to: 'v-model="onlyDelayedX"'
  },
  {
    // 工序下拉退化成单选 ⇒ 应转红
    id: 'GT93',
    file: GANTT,
    from: 'v-model="operationFilter"\n          multiple',
    to: 'v-model="operationFilter"'
  },

  /* ===== GT94：过滤真正接入 filteredTasks ===== */
  {
    // 「只看延期」改成按工序筛（不是后端给的 isDelayed）⇒ 应转红
    id: 'GT94',
    file: GANTT,
    from: 'tasks = tasks.filter((t) => t.isDelayed)',
    to: 'tasks = tasks.filter((t) => t.operationCode)'
  },
  {
    // 「只看工序」改成按任务号筛 ⇒ 应转红
    id: 'GT94',
    file: GANTT,
    from: 'operationFilter.value.includes(t.operationCode)',
    to: 'operationFilter.value.includes(t.taskNo)'
  },
  {
    // 工序选项改从「过滤后」取 ⇒ 打开筛选后选项自己消失 ⇒ 应转红
    id: 'GT94',
    file: GANTT,
    from: 'for (const t of ganttData.value?.tasks ?? [])',
    to: 'for (const t of filteredTasks.value)'
  },

  /* ===== GT95：页面层给「工序 | 订单号」标签（任务号不上条）===== */
  {
    // 任务行不再带标签 ⇒ 应转红
    id: 'GT95',
    file: GANTT,
    from: 'barLabel: barLabelOf(t),',
    to: 'barLabel: undefined,'
  },
  {
    // 标签不再带生产指示号（退回只写工序）⇒ 应转红
    id: 'GT95',
    file: GANTT,
    from: 'barLabel: barLabelOf(t),',
    to: 'barLabel: t.taskNo,'
  },
  {
    // 标签函数内部退回任务号（用户要求条上不出现任务号）⇒ 应转红
    id: 'GT95',
    file: GANTT,
    from: 'const instruction = t.mtS_InstructionNo?.trim()',
    to: 'const instruction = t.taskNo?.trim()'
  },
  {
    // 标签函数退回订单号（订单号整片相同，上条等于零信息）⇒ 应转红
    id: 'GT95',
    file: GANTT,
    from: 'const instruction = t.mtS_InstructionNo?.trim()',
    to: 'const instruction = t.orderNo?.trim()'
  },

  /* ===== GT96：任务号一律写在条内（无条尾外挂标签）===== */
  {
    // 条内标签被整块摘掉 ⇒ 条上不写字 ⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: "if (!task || !task.barLabel) return ''",
    to: "if (true || !task.barLabel) return ''"
  },
  {
    // 又出现条尾外挂标签（dhx-tail-label 类名回到条内 span 上）⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: '<span class="dhx-bar-label">${escapeHtml(task.barLabel)}</span>',
    to: '<span class="dhx-tail-label">${escapeHtml(task.barLabel)}</span>'
  },
  {
    // 条宽判断写反（撑得下反而不写）⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: "if (!task || !task.barLabel) return ''",
    to: "if (!task || !task.barLabel || barLabelFits(task)) return ''"
  },
  {
    // 条内 flex 容器被去掉（标记重新掉行、样式对不上）⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: 'class="dhx-bar-inner"',
    to: 'class="dhx-bar-row"'
  },
  {
    // 省略号截断被摘（过窄的任务号会溢出条外）⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: ':deep(.dhx-bar-label) {\n  display: block;\n  overflow: hidden;\n  font-size: 11px;\n  line-height: 26px;\n  text-overflow: ellipsis;',
    to: ':deep(.dhx-bar-label) {\n  display: block;\n  overflow: hidden;\n  font-size: 11px;\n  line-height: 26px;\n  text-overflow: clip;'
  },
  {
    // 条内容重新放开溢出 ⇒ 文字盖到相邻条上 ⇒ 应转红
    id: 'GT96',
    file: DHX,
    from: 'overflow: hidden !important;',
    to: 'overflow: visible !important;'
  },
  {
    // 类型位被删（页面给的标签传不进来）⇒ 应转红
    id: 'GT96',
    file: DHX_TYPES,
    from: 'barLabel?: string',
    to: 'barLabelX?: string'
  },

  /* ===== GT120：左表首列合并物料 ===== */
  {
    // 首列退回「工序 | 订单」⇒ 应转红
    id: 'GT120',
    file: GANTT,
    from: 'text: gridLabelOf(t),',
    to: 'text: barLabelOf(t),'
  },
  {
    // 列标题没带上物料 ⇒ 应转红
    id: 'GT120',
    file: DHX,
    from: "label: '工序 / 指示号 / 物料',",
    to: "label: '工序 / 指示号',"
  },
  {
    // 首列列宽缩回原值（没吃掉物料列让出的宽度）⇒ 应转红
    id: 'GT120',
    file: DHX,
    from: 'width: 340,',
    to: 'width: 300,'
  },
  {
    // 左表总宽没跟着收（列宽合计 ≠ grid_width，右侧留白）⇒ 应转红
    id: 'GT120',
    file: DHX,
    from: 'grid_width = 690',
    to: 'grid_width = 660'
  },
  {
    // 类型位把物料加回来（又在首列之外开一列）⇒ 应转红
    id: 'GT120',
    file: DHX_TYPES,
    from: 'barLabel?: string',
    to: 'barLabel?: string\n  material?: string'
  },

  /* ===== GT121：左表 开始/结束/数量（无时长列）+ 悬停/搜索补执行字段 ===== */
  {
    // 「结束」列被换成「时长」⇒ 泳道 75h 与子任务 1/2h 又混在一列 ⇒ 应转红
    id: 'GT121',
    file: DHX,
    from: "name: 'endText',",
    to: "name: 'duration',"
  },
  {
    // 「数量」列被换成「时长」⇒ 应转红
    id: 'GT121',
    file: DHX,
    from: "name: 'qtyText',",
    to: "name: 'duration',"
  },
  {
    // 任务行「结束」改用开工时间（列在但值错）⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: "endText: t.plannedEndTime ? formatDate(t.plannedEndTime) : '',",
    to: "endText: t.plannedStartTime ? formatDate(t.plannedStartTime) : '',"
  },
  {
    // 任务行「数量」不再优先取计划加工数量（退回净需求）⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from:
      '      qtyText: sharedQtyUnit.value\n' +
      '        ? String(t.plannedProcessQty ?? t.netQty)\n' +
      '        : `${t.plannedProcessQty ?? t.netQty} ${t.uom}`.trim(),',
    to: '      qtyText: String(t.netQty),'
  },
  {
    // 表头单位被写死成「数量」⇒ PS 不再进头部、格子也没单位 ⇒ 应转红
    id: 'GT121',
    file: DHX,
    from: "label: props.qtyUnit ? `数量(${props.qtyUnit})` : '数量',",
    to: "label: '数量',"
  },
  {
    // 「数量」列被改回右对齐 ⇒ 应转红
    id: 'GT121',
    file: DHX,
    from: "      align: 'left',",
    to: "      align: 'right',"
  },
  {
    // 单位混合/空单位的护栏被删 ⇒ 表头可能把少数行说错 ⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: "    else if (unit !== u) return ''\n",
    to: ''
  },
  {
    // 页面不再传 qtyUnit ⇒ 表头永远只有「数量」⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: '        :qty-unit="sharedQtyUnit"\n',
    to: ''
  },
  {
    // 泳道行「结束」清空 ⇒ 泳道整行空一格 ⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: "endText: spanEnd ? formatDate(spanEnd) : ''",
    to: "endText: ''"
  },
  {
    // 悬停里丢掉生产指示号 ⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: 'if (t.mtS_InstructionNo) lines.push(`生产指示号：${t.mtS_InstructionNo}`)',
    to: 'if (t.taskNo) lines.push(`任务号：${t.taskNo}`)'
  },
  {
    // 悬停里丢掉订单号（条上已经不显示，悬停是唯一出口）⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: '  if (t.orderNo) lines.push(`订单：${t.orderNo}`)\n',
    to: ''
  },
  {
    // 搜索栈里摘掉生产指示号 ⇒ 搜指示号搜不到 ⇒ 应转红
    id: 'GT121',
    file: GANTT,
    from: '    t.mtS_InstructionNo,\n    t.mesWorkOrderNo,\n',
    to: ''
  },

  /* ===== GT122：人工泳道按部门拆 + 三段式标签（2026-11-06）===== */
  {
    // 三段分隔符被改回旧写法 ⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: "  return `${name} | ${factory || '—'} | ${department || '—'}`",
    to: "  return `${name} | ${factory || '—'} / ${department || '—'}`"
  },
  {
    // 设备行改回内联拼串（绕过 laneLabel）⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: '        label: laneLabel(identity, r.factoryName, r.productionDepartmentName),',
    to: '        label: `${identity} | ${r.factoryName} / ${r.productionDepartmentName}`,'
  },
  {
    // 人工泳道标签退回旧的一条「未分配资源」⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: "      label: laneLabel('人工', v.factory, v.dept),",
    to: "      label: '未分配资源 | 无指定设备',"
  },
  {
    // 任务归组不再区分人工（全部丢进 lane-unassigned）⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: '      return isLaborTask(task) ? laborLaneId(task) : UNASSIGNED_LANE_ID',
    to: '      return UNASSIGNED_LANE_ID'
  },
  {
    // 人工泳道 id 不再按部门 ⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: '  return `lane-manual-${t.productionDepartmentId ?? 0}`',
    to: '  return UNASSIGNED_LANE_ID'
  },
  {
    // 部门 lookup 只取 200（287 个部门里 87 个拿不到工厂名）⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: 'departmentLookupApi.listDepartments(500)',
    to: 'departmentLookupApi.listDepartments(200)'
  },
  {
    // 部门 lookup 默认值被改成别的 ⇒ 应转红
    id: 'GT122',
    file: 'src/api/aps-v1/departmentLookup.ts',
    from: 'async listDepartments(limit = 200)',
    to: 'async listDepartments(limit = 100)'
  },
  {
    // 聚合行（-1）不再跳过 ⇒ 负荷带按 lane--1 画到不存在的行 ⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: '    if (group.resourceId === UNASSIGNED_RESOURCE_ID) continue\n',
    to: ''
  },
  {
    // 阶段维度不再回落到任务自带 stageCode ⇒ 人工任务全进「未配置」⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: '  return t.stageCode?.trim() || DIM_UNCONFIGURED_LABEL',
    to: '  return DIM_UNCONFIGURED_LABEL'
  },
  {
    // 泳道行顺序不再按任务流首现序（人工重新置顶的旧规则回归）⇒ 应转红
    id: 'GT66',
    file: GANTT,
    from: '    return [...rows, ...laborRows].sort(',
    to: '    return [...rows, ...laborRows].slice().sort('
  },

  {
    // 空泳道「开始」改回 formatDate(undefined) ⇒ 显示假时间 ⇒ 应转红
    id: 'GT122',
    file: GANTT,
    from: "      startText: spanStart ? formatDate(spanStart) : '',",
    to: '      startText: formatDate(spanStart),'
  },
  {
    // 开始列改回受 dhtmlx 日期模板控制 ⇒ 空值会被兜底成当前时间 ⇒ 应转红
    id: 'GT122',
    file: DHX,
    from: "      name: 'startText',",
    to: "      name: 'start_date',"
  },
  {
    // 设备泳道不再限定「已加载资源」⇒ 全量资源铺出大片空泳道 ⇒ 应转红
    id: 'GT66',
    file: GANTT,
    from: '      .filter((r) => loadedResourceIds.has(r.resourceId))\n',
    to: ''
  },

  /* ===== GT123：分组维度不再提供「按 Domain」（2026-11-06）===== */
  {
    // 下拉重新放出「按 Domain」⇒ 单域版本下恒一条泳道 ⇒ 应转红
    id: 'GT123',
    file: GANTT,
    from: "{ value: 'stage', label: '按阶段' }",
    to: "{ value: 'domain', label: '按 Domain' }"
  },

  /* ===== GT97：模板用到的 El* 组件必须 import 过 ===== */
  {
    // 漏 import ElCheckbox ⇒ 控件在页面里静默不渲染（2026-11-03 headless 实抓的真缺陷）⇒ 应转红
    id: 'GT97',
    file: GANTT,
    from: '  ElCheckbox,\n',
    to: ''
  }
]

function run() {
  const r = spawnSync('node', ['scripts/verify-acceptance.mjs'], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'gantt' },
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
  writeFileSync(p, c.all ? orig.split(from).join(to) : orig.replace(from, to), 'utf8')
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
    .filter((l) => l.includes('❌') && !mine.includes(l) && !l.includes('失败：') && !l.includes('结果'))
    .map((l) => l.trim().slice(0, 40))
  console.log(
    `${c.id}: ${red ? 'RED ✅' : 'STILL-GREEN ❌'} | 其它红项=${others.length ? others.join(' / ') : '无'}`
  )
  if (!red) allOk = false
}
console.log(allOk ? '\n自证通过：每条都能被打红' : '\n自证未通过')
process.exit(allOk ? 0 : 1)
