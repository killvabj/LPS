/**
 * APS V1 4号位 — 参数集 5 主题块「可新增字段」白名单
 *
 * 派活来源：3号位 2026-10-25《锚点资源编码补录入 · 空块不补键确认与新建参数集首键拍板回执 v1.0》
 *   §C-AN-1：空块（无任何键的块，即 `{}`）上开放「新增字段 / 新增首项」编辑动作；
 *             用户**显式新增一个字段并填入真实值**后，该字段才进入块 JSON。
 *   §C-AN-3：**不注入任何默认值** —— 前端不预填默认枚举 / 空结构 / 约定键名；
 *             空块在用户完成首个真实输入前保持空、**仍被 `EMPTY_*` 拦截**。
 *
 * 真源（只读引用；字段名不得由前端自造）：
 *   lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs
 *     LockBlock:161-184 · SupplyBlock:187-209 · ProcurementBlock:220-258
 *     SolverStrategyBlock:264-319 · CandidateGuardrailBlock:323-335
 *   数值区间（发布前校验口径）：
 *     SolverStrategyValidator.cs · CandidateGuardrailValidator.cs
 *     GovernanceVersionService.cs:1356-1372（OverdueMargin 0~100 / ≥0）
 *
 * 三条红线：
 *  ① 键名 = C# DTO 属性名。后端 `JsonOptions { PropertyNameCaseInsensitive = true }`
 *     ⇒ **写错大小写不报错、只会静默取默认值** ⇒ 必须逐字一致。
 *  ② 枚举值 = **数字序号**（后端无 `JsonStringEnumConverter` ⇒ System.Text.Json 按序号读写）。
 *     枚举项的展示名与序号在 `parameterSetEnums.ts`（唯一真源），本表只声明「这个字段是枚举」。
 *  ③ 本表**只列标量字段**：
 *     - 仓库落点（`Inventory.WarehousePriority` / `WarehousePriority` /
 *       `DefaultPurchaseLt` / `ArrivalToUsableOffsets`）不在此列；
 *     - `AnchorResourceCode` 有专属自由文本录入项，不在此列；
 *     - 数组类（`Procurement.PlanningYields`）有专属编辑器，不在此列。
 *   2026-11-09 起「以 0号位 11-04 工作簿为标准」：工作簿未收录的字段不显示在维护面，
 *   见 `NOT_IN_WORKBOOK_FIELDS`（工作簿查无的采购 ETA 两项 + 需求成熟度两项，
 *   以及「默认采购提前期 / 到货可用偏移」两个专属编辑器已撤）。
 */

import type { BlockKey } from './types/rule'

/**
 * 维护责任分类（0号位 2026-11-04《APS_V1_规则参数集71项逐字段业务解释与V1处置》）
 *
 * A 必须配置 / B 按需配置 ⇒ 用户编辑
 * C 1号位算法内部 / E 由其他表替代 / F V1不使用 / H 待核证 ⇒ 维护面隐藏
 * D V1固定 ⇒ 展示但禁改（**含「新增字段」入口：用户不得写入冻结值**）
 *
 * **只影响 UI 暴露**：隐藏行的值仍在快照里，`buildPutBody` 原样回写（C 类 1号位要读、
 * D 类后端校验要读，删了会造成装载/校验缺字段）。
 */
export type MaintenanceClass = 'A' | 'B' | 'C' | 'D' | 'E' | 'F' | 'H'

/** 需要从维护面隐藏的分类（D 不在此列：D 是只读展示） */
export const HIDDEN_MAINTENANCE_CLASSES: readonly MaintenanceClass[] = ['C', 'E', 'F', 'H']

/**
 * **0号位 2026-11-04 工作簿未收录**的字段 ⇒ 维护面不显示（用户 2026-11-09 定：以工作簿为标准）。
 *
 * 与 C/E/F/H 的区别：这些字段在 DTO 里真实存在、后端也可能有消费点，只是**工作簿没收录**
 * （工作簿 §范围 自述「你已删除、不再出现在文件中的字段均不恢复」）。
 * 处置与 C/E/F/H 完全一致：**页面不渲染、不进「新增字段」候选；载荷原样保留并回写**。
 *
 * 复核依据：`APS_V1_规则参数集71项逐字段盘点与0号位配置工作台_20261104.xlsx`
 * 7 个 sheet 全文 `OverdueMargin` / `MaturityStatus` / `DefaultPurchaseLt` / `ArrivalToUsable` 零命中。
 */
export const NOT_IN_WORKBOOK_FIELDS: Partial<Record<BlockKey, readonly string[]>> = {
  lock: ['Trigger.ProtectMaturityStatus', 'Trigger.MaturityStatusValues'],
  procurement: ['OverdueMargin.MarginPercent', 'OverdueMargin.MinimumExtraDays']
}

/** 该路径是否属于「工作簿未收录」（⇒ 维护面隐藏） */
export function isNotInWorkbookField(block: BlockKey, path: string): boolean {
  return NOT_IN_WORKBOOK_FIELDS[block]?.includes(path) === true
}

/** 字段的值形态 */
export type BlockFieldKind =
  /** 是 / 否 */
  | 'BOOL'
  /** 数字 */
  | 'NUMBER'
  /** 单行文本 */
  | 'TEXT'
  /** 下拉枚举（值为数字序号） */
  | 'ENUM'
  /** 文本清单（每行一项 ⇒ 字符串数组） */
  | 'TEXT_LIST'

export interface BlockFieldDef {
  /** 块内点分路径，键名与 C# DTO 属性名逐字一致（如 `Trigger.ProtectDelayed`） */
  path: string
  /** 中文标签（用户看到的就是这个） */
  label: string
  /** 值形态 */
  kind: BlockFieldKind
  /** 数值下界（后端发布校验口径；缺省 = 不夹） */
  min?: number
  /** 数值上界 */
  max?: number
  /** 步长 */
  step?: number
  /** 一句话业务说明（对话框里给用户看） */
  hint?: string
  /** 维护责任分类；**缺省 = A/B（用户可编辑）**。只有 C/D/E/F/H 需要显式标注 */
  maintenance?: MaintenanceClass
}

/* ===== ① 锁定策略（LockBlock）=====
 * 2026-11-07 后端已接通需求保护消费链：`FrozenStrategySnapshotProvider.cs:177` 把参数集版本内容
 * `ParameterSetVersion.*.$.Lock` 反序列化成 `snapshot.Lock`，`PeggingOrchestrator.cs:13279` 取该块
 * 交给 `DemandProtectionTriggerEvaluator` 逐项判定，产出每条需求的 `ProtectionStatus`（影响需求排序）
 * ⇒ 本块 **Trigger.* 触发条件类 8 项已生效**，页面不再标「暂未生效」。
 * **不露面的**：
 *   - `Trigger.AllowPmcManualProtection`（#7）—— **0号位 终裁（2026-11-10）：整体挪到 V1.1，V1 不做**
 *     ⇒ 维护面隐藏（F 类）：表格不渲染、也不进「新增字段」候选；**载荷原样保留回写**
 *       （现网快照里的 `true` 不动，V1 自动需求保护维持 2026-10-29 G4 基线不变）。
 *   - `Sticky.*` 三项（仅 DTO 声明，运行期零读取）。
 *
 * 取值域（提示词按真实字典写，别再写「如 SO / WO」）：
 *   - `DemandProtectionTriggerEvaluator.cs:167` 对 `CustomerTier` 做 **Ordinal 精确等值**比较
 *     ⇒ `VipTierValue` 必须与订单上 `CustomerTier` 原值逐字相同；
 *     实库值域已由 2号位 2026-11-06 校准收敛为 `VIP` / `GENERAL`
 *     （旧 A/B/C、KEY_ACCOUNT/STANDARD 不再是合法维护值，见 prioritySegmentEditor.ts:157-165）。
 *   - `DemandProtectionTriggerEvaluator.cs:184` 对 `OrderType` 做 **Ordinal 集合包含**比较
 *     ⇒ `ProtectedOrderTypes` 必须是实库订单类型原值；
 *     实库值为 `PRODUCTION_INSTRUCTION` / `SALES_ORDER`（`OrderQueryDtos.cs:24`），
 *     工作簿样例 `["SO"]` 是**示例值、直接发布必然不命中**（0号位 2026-11-04 处置建议明写「不用示例 SO 直接发布」）。
 */

const LOCK_FIELDS: BlockFieldDef[] = [
  {
    path: 'Trigger.UseRemainingTimeThreshold',
    label: '按剩余时间触发保护',
    kind: 'BOOL',
    hint: '剩余时间小于正常提前期时触发保护'
  },
  {
    path: 'Trigger.RemainingTimeThresholdHours',
    label: '剩余时间阈值（小时）',
    kind: 'NUMBER',
    min: 0.01,
    step: 1,
    hint: '开启「按剩余时间触发保护」时必须大于 0'
  },
  { path: 'Trigger.ProtectDelayed', label: '保护已延期需求', kind: 'BOOL' },
  { path: 'Trigger.ProtectVipTier', label: '保护 VIP 客户', kind: 'BOOL' },
  {
    path: 'Trigger.VipTierValue',
    label: 'VIP 客户标识',
    kind: 'TEXT',
    hint: '客户等级英文码：VIP 或 GENERAL；按精确匹配判定，大小写要一致（可下拉选，也可手填）'
  },
  {
    path: 'Trigger.ProtectedOrderTypes',
    label: '指定订单类型',
    kind: 'TEXT_LIST',
    hint: '订单类型多选：SALES_ORDER（客户订单）/ PRODUCTION_INSTRUCTION（生产指示）；只能从清单里选，填错永远不命中'
  },
  {
    path: 'Trigger.ProtectMaturityStatus',
    label: '按需求成熟度触发保护',
    kind: 'BOOL',
    hint: '成熟度命中下方清单中任一值时触发保护'
  },
  {
    path: 'Trigger.MaturityStatusValues',
    label: '需求成熟度触发值',
    kind: 'TEXT_LIST',
    hint: '需求成熟度多选：PRE_CONFIRMED（事前确认）/ FORECAST（预测）；只能从清单里选，填错永远不命中'
  },
  {
    path: 'Trigger.AllowPmcManualProtection',
    label: '允许 PMC 人工强保护',
    kind: 'BOOL',
    // 0号位 终裁（2026-11-10）：#7「允许 PMC 人工强保护」整体挪到 V1.1，V1 不做
    //   ⇒ 维护面隐藏（F 类 = V1 不使用），载荷原样保留回写（现网快照里的 true 不动）
    maintenance: 'F'
  },
  {
    path: 'Sticky.RequireReleaseRecord',
    label: '手工释放需留记录',
    kind: 'BOOL',
    maintenance: 'D'
  },
  {
    path: 'Sticky.ProtectUntilCompletion',
    label: '保护持续到订单完成',
    kind: 'BOOL',
    maintenance: 'H'
  },
  {
    path: 'Sticky.ProtectUntilSupplyInvalid',
    label: '保护持续到供应失效',
    kind: 'BOOL',
    maintenance: 'H'
  }
]

/* ===== ② 供应参数（SupplyBlock）===== */

const SUPPLY_FIELDS: BlockFieldDef[] = [
  {
    path: 'Inventory.IsEnabled',
    label: '启用库存可用判定',
    kind: 'BOOL',
    maintenance: 'E'
  },
  {
    path: 'Inventory.RequireFactoryContext',
    label: '必须带工厂上下文',
    kind: 'BOOL',
    maintenance: 'E'
  },
  {
    path: 'Inventory.RequireProductFamilyContext',
    label: '必须带产品族上下文',
    kind: 'BOOL',
    maintenance: 'E'
  },
  { path: 'PiSort.SortBy', label: 'PI 排序方式', kind: 'ENUM' },
  {
    path: 'PiSort.UseStablePiNoTieBreak',
    label: '并列时按 PI 号稳定排序',
    kind: 'BOOL',
    maintenance: 'D'
  }
]

/* ===== ③ 采购参数（ProcurementBlock）===== */

const PROCUREMENT_FIELDS: BlockFieldDef[] = [
  {
    path: 'OverdueMargin.MarginPercent',
    label: '逾期修正比例',
    kind: 'NUMBER',
    min: 0,
    max: 100,
    step: 0.01,
    hint: '后端发布校验要求 0~100'
  },
  {
    path: 'OverdueMargin.MinimumExtraDays',
    label: '逾期最小加天',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    hint: '后端发布校验要求不小于 0'
  }
]

/* ===== ④ 求解器策略（SolverStrategyBlock）===== */

const SOLVER_FIELDS: BlockFieldDef[] = [
  { path: 'Mode', label: '求解方向', kind: 'ENUM' },
  { path: 'AllowMerge', label: '允许合批', kind: 'BOOL', maintenance: 'E' },
  { path: 'BottleneckMode', label: '瓶颈锚点方式', kind: 'ENUM' },
  {
    path: 'OnTimeTarget.TargetPercent',
    label: '准时率目标',
    kind: 'NUMBER',
    min: 0,
    max: 100,
    step: 1,
    hint: '后端发布校验要求 0~100'
  },
  {
    path: 'OnTimeTarget.IsPrimaryObjective',
    label: '准时率作为首要目标',
    kind: 'BOOL',
    maintenance: 'D'
  },
  {
    path: 'Split.MaxOptimizationSplitCount',
    label: '最大优化拆分次数',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'Split.LimitMandatorySplit',
    label: '限制强制拆分',
    kind: 'BOOL',
    maintenance: 'D'
  },
  {
    path: 'Split.MinBatchQty',
    label: '最小批量',
    kind: 'NUMBER',
    min: 0.01,
    step: 0.1,
    hint: '后端发布校验要求大于 0',
    maintenance: 'H'
  },
  {
    path: 'Setup.SetupSearchBudget',
    label: '换型搜索预算（次）',
    kind: 'NUMBER',
    min: 100,
    max: 5000,
    step: 10,
    maintenance: 'C'
  },
  {
    path: 'Setup.SetupMaxNeighborhoodTries',
    label: '最大邻域尝试次数',
    kind: 'NUMBER',
    min: 10,
    max: 500,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'StageOverlap.AllowOverlap',
    label: '允许工序重叠',
    kind: 'BOOL',
    maintenance: 'D'
  },
  {
    path: 'StageOverlap.TransferBatchQty',
    label: '转序批量',
    kind: 'NUMBER',
    min: 0,
    step: 0.1,
    maintenance: 'F'
  },
  {
    path: 'StageOverlap.ThresholdQty',
    label: '重叠阈值数量',
    kind: 'NUMBER',
    min: 0,
    step: 0.1,
    maintenance: 'F'
  },
  {
    path: 'StageOverlap.ThresholdPercent',
    label: '重叠阈值比例',
    kind: 'NUMBER',
    min: 0,
    max: 100,
    step: 0.01,
    maintenance: 'F'
  },
  {
    path: 'BottleneckUtilizationThreshold',
    label: '瓶颈利用率阈值',
    kind: 'NUMBER',
    min: 0.01,
    max: 1,
    step: 0.01,
    hint: '0~1 之间的小数（不是百分数）'
  },
  {
    path: 'CapacityShortageUtilizationThreshold',
    label: '产能短缺利用率阈值',
    kind: 'NUMBER',
    min: 0.01,
    max: 1,
    step: 0.01,
    hint: '0~1 之间的小数（不是百分数）'
  }
]

/* ===== ⑤ 候选版本守门（CandidateGuardrailBlock）===== */

const GUARDRAIL_FIELDS: BlockFieldDef[] = [
  {
    path: 'NormalMs',
    label: '正常超时（毫秒）',
    kind: 'NUMBER',
    min: 1,
    step: 1000,
    maintenance: 'C'
  },
  {
    path: 'SoftMs',
    label: '软超时（毫秒）',
    kind: 'NUMBER',
    min: 1,
    step: 1000,
    hint: '不得小于正常超时',
    maintenance: 'C'
  },
  {
    path: 'LocalHardMs',
    label: '局部硬超时（毫秒）',
    kind: 'NUMBER',
    min: 1,
    step: 1000,
    hint: '不得小于软超时',
    maintenance: 'C'
  },
  {
    path: 'ImpactedTaskWarningPercent',
    label: '受影响任务警戒比例',
    kind: 'NUMBER',
    min: 0,
    max: 100,
    step: 1
  },
  {
    path: 'MaxRepairAttempts',
    label: '最大修复次数',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'MaxPropagationRounds',
    label: '最大传播轮数',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'ResourceTopN',
    label: '资源 TopN',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'SplitAlternatives',
    label: '拆分备选数',
    kind: 'NUMBER',
    min: 0,
    step: 1,
    maintenance: 'C'
  },
  {
    path: 'WarnOnlyOnMaxImpacted',
    label: '超阈值仅告警不停止',
    kind: 'BOOL',
    maintenance: 'D'
  }
]

/** 5 块字段目录（键 = 参数集 5 主题块） */
export const BLOCK_FIELD_CATALOG: Record<BlockKey, BlockFieldDef[]> = {
  lock: LOCK_FIELDS,
  supply: SUPPLY_FIELDS,
  procurement: PROCUREMENT_FIELDS,
  solverStrategy: SOLVER_FIELDS,
  candidateGuardrail: GUARDRAIL_FIELDS
}

/** 取某块的字段清单 */
export function listBlockFields(block: BlockKey): BlockFieldDef[] {
  return BLOCK_FIELD_CATALOG[block] ?? []
}

/** 按路径取字段定义 */
export function findBlockField(block: BlockKey, path: string): BlockFieldDef | undefined {
  return listBlockFields(block).find((f) => f.path === path)
}

/**
 * 该路径在块 JSON 里是否已存在（**只看叶子键在不在**，不看值）。
 *
 * 用于「已配过的字段不再重复出现在候选清单里」——
 * 已有值的字段由通用参数表直接编辑，不必再从新增入口选。
 */
export function hasBlockField(blockObj: unknown, path: string): boolean {
  if (!blockObj || typeof blockObj !== 'object' || Array.isArray(blockObj)) return false
  let cur: unknown = blockObj
  for (const key of path.split('.')) {
    if (!cur || typeof cur !== 'object' || Array.isArray(cur)) return false
    if (!Object.prototype.hasOwnProperty.call(cur, key)) return false
    cur = (cur as Record<string, unknown>)[key]
  }
  return true
}

/**
 * 该路径是否在维护面隐藏：C/E/F/H 分类，或**工作簿未收录**（见 `NOT_IN_WORKBOOK_FIELDS`）。
 * 目录外新键一律 false（不误伤）。
 */
export function isHiddenBlockField(block: BlockKey, path: string): boolean {
  if (isNotInWorkbookField(block, path)) return true
  const cls = findBlockField(block, path)?.maintenance
  return cls !== undefined && HIDDEN_MAINTENANCE_CLASSES.includes(cls)
}

/** 该路径是否属于 D 类「V1 固定」（只读展示、禁改） */
export function isReadonlyBlockField(block: BlockKey, path: string): boolean {
  return findBlockField(block, path)?.maintenance === 'D'
}

/**
 * D 类「V1 固定」⇒ **用户不得写入**，含「新增字段」入口。
 *
 * 与 `isReadonlyBlockField` 同源判定、语义不同：
 *   - `isReadonlyBlockField`：已在快照里的行只读展示（行存在才谈得上只读）；
 *   - 本函数：连候选清单都不给 —— 否则用户可在「新增字段」里把冻结值写成别的
 *     （例：`WarnOnlyOnMaxImpacted` 写成 false、`StageOverlap.AllowOverlap` 写成 true），
 *     与 0号位 2026-11-04 工作簿 D 类「V1 固定、不得随意改动」和
 *     3号位 2026-11-05 来函「D 类只读展示、禁改」相抵触。
 *
 * 值由后端 DTO 默认值承载（如 `IsPrimaryObjective=true` / `WarnOnlyOnMaxImpacted=true` /
 * `AllowOverlap=false` / `LimitMandatorySplit=false`），不靠用户填。
 */
export function isFixedValueBlockField(block: BlockKey, path: string): boolean {
  return findBlockField(block, path)?.maintenance === 'D'
}

/**
 * 某块里**尚未配置**的字段（新增入口的候选清单）
 *  - 不列维护面隐藏分类：否则用户能从「新增字段」把刚隐藏的字段加回来
 *  - 不列 D 类「V1 固定」：值已冻结，用户写入即改冻结语义（见 isFixedValueBlockField）
 */
export function listAbsentFields(block: BlockKey, blockObj: unknown): BlockFieldDef[] {
  return listBlockFields(block).filter(
    (f) =>
      !isHiddenBlockField(block, f.path) &&
      !isFixedValueBlockField(block, f.path) &&
      !hasBlockField(blockObj, f.path)
  )
}

/* ===== 运行期无消费者（页面标「暂未生效」） ===== */

/**
 * 运行期暂无消费者的字段 —— 页面标「暂未生效」的唯一依据。
 *
 * 依据（按时间序）：
 *  - 3号位 2026-10-26《参数集无运行时消费者字段核证回执》（13 项一致 / 1 项纠正），
 *    我方逐项回源码复核：这些字段只有发布校验，主链
 *    Application / Scheduling / Engine / BusinessRules 零读取。
 *  - 3号位 2026-11-07 回执 §一：10 项撤标逐条核证通过、**22→12 认账**；
 *    §二 **R1 台账已落**（自有文档目录，0号位 定「不并入冻结《开发实施包 v2.4》」）——
 *    12 项逐项出处见 `INERT_FIELD_LEDGER`（K1~K12）。
 *  - 3号位 2026-11-07 回执 §三 **R2「接线清单回告」条款已采纳**：
 *    任何字段接线落 dev 时，3号位 在回执／台账里列出「新接线字段清单 + 读数点（文件:行号）」，
 *    前端据此撤标；**终审以源码对照兜底**（见 scripts/verify-acceptance.mjs 的 P43）。
 *
 * 2026-11-07 撤标 10 项（22 → 12）：后端已接线的字段必须移出本表，
 *   否则页面会对**真生效**的字段说「配了没用」——那比不标更坏。
 *   - 锁定策略 8 项 `Trigger.*`（UseRemainingTimeThreshold / RemainingTimeThresholdHours /
 *     ProtectDelayed / ProtectVipTier / VipTierValue / ProtectedOrderTypes /
 *     ProtectMaturityStatus / MaturityStatusValues）
 *     ⇒ `FrozenStrategySnapshotProvider.cs:177` 把参数集版本内容 `$.Lock` 装进 `snapshot.Lock`，
 *       `PeggingOrchestrator.cs:13279` 取该块 → `DemandProtectionTriggerEvaluator.cs:122-197`
 *       逐项判定 → 产出 `ProtectionStatus`（`PeggingOrchestrator.cs:13344`）→ 影响需求排序。
 *   - 候选版本守门 2 项 `ResourceTopN` / `WarnOnlyOnMaxImpacted`
 *     ⇒ `PhaseFourLocalRepair.cs:1435` / `:184` 读 `request.StrategySnapshot.CandidateGuardrail.*`。
 *   - 留存的 12 项逐条复核过：仅 DTO 声明 / 仅发布校验 / 尚未接线 ⇒ 运行期仍无消费者。
 *     （`MinimumExtraDays` 虽被写进 `FrozenFactParameters.OverdueMargin`
 *     （`PeggingOrchestrator.cs:6490`），但唯一读者 `TimedSupplyFactCalculator.ApplyOverdueMargin`
 *     在生产路径零调用点，且 `AvailableTimeCalculator.cs:28-29` 明令不得接通
 *     ⇒ 仍算未消费，且按 0号位 10-25 采购执行单裁定**受控废弃**（不是「等接入」）。）
 *
 * 两条红线：
 *  ① **只收「本轮不接入」**：`OverdueMargin.MarginPercent` 真生效（`AvailableTimeCalculator.cs:176`），
 *     `OnTimeTarget.TargetPercent` 与 `CandidateGuardrail.NormalMs/SoftMs/LocalHardMs/MaxRepairAttempts`
 *     属「待激活」（注释先行、后续会接线）⇒ 一律不进本表，否则接线后标注变假信息。
 *  ② 后端接线后**逐项删掉**对应行，页面标注随之消失 —— 这张表是临时的，不是白名单。
 */
export const INERT_FIELD_PATHS: Readonly<Record<BlockKey, readonly string[]>> = {
  lock: [
    'Trigger.AllowPmcManualProtection',
    'Sticky.RequireReleaseRecord',
    'Sticky.ProtectUntilCompletion',
    'Sticky.ProtectUntilSupplyInvalid'
  ],
  supply: [
    'Inventory.IsEnabled',
    'Inventory.RequireFactoryContext',
    'Inventory.RequireProductFamilyContext'
  ],
  procurement: ['OverdueMargin.MinimumExtraDays'],
  solverStrategy: [
    'OnTimeTarget.IsPrimaryObjective',
    'Split.LimitMandatorySplit',
    'StageOverlap.ThresholdQty',
    'StageOverlap.ThresholdPercent'
  ],
  candidateGuardrail: []
}

/** 该字段运行期是否无消费者（行上据此渲染「暂未生效」标签） */
export function isInertBlockField(block: BlockKey, path: string): boolean {
  return (INERT_FIELD_PATHS[block] ?? []).includes(path)
}

/* ===== R1 台账 K1~K12（3号位 2026-11-07）===== */

/**
 * 12 项「运行期无消费者」字段的台账对照 —— 3号位 2026-11-07 回执 §五 Q1 分类表。
 *
 * 出处（**按 K 编号引用**，3号位 11-09 回执 §四 B4 采方案②）：引「K1~K12（3号位 11-07 回执 §五）」即准 ——
 *   R1 台账原文在 3号位 自有目录（`开发文档/工作日志/3号位/文档阅读/12_…R1参数集未生效字段台账_20261107.md`），
 *   **不在前端仓内**（我方仓只有 `lps/` + `frontNew/`），故此处不写文件路径引用；
 *   分类与字段路径已落在下方代码里，K 编号回指即准
 *   （经 0号位 定：**保持 3号位 自有台账，不并入冻结《开发实施包 v2.4》** —— 冻结文档禁擅改）。
 *
 * `cls` = 0号位 11-04《71 项逐字段业务解释与 V1 处置》已定的维护责任分类：
 *   `A` 必须配置 / `D` V1固定 / `E` 由其他表替代 / `F` V1不使用 / `H` 待核证；
 *   `DEPRECATED` = 受控废弃（`MinimumExtraDays`，0号位 10-25 采购执行单裁定，
 *   后端注释见 `AvailableTimeCalculator.cs:28`）。
 *   **A/B 类在字段目录里不标 `maintenance`** ⇒ 目录里查不到 A 才是对的。
 */
export interface InertFieldLedgerEntry {
  /** 台账编号（K1~K12） */
  k: string
  /** 0号位 已定分类 */
  cls: 'A' | 'D' | 'E' | 'F' | 'H' | 'DEPRECATED'
  /** 3号位 §五 给的页面处置（我方按此落地） */
  treatment: string
}

/** 12 项台账（键＝块 + 字段路径；与 `INERT_FIELD_PATHS` 逐项一一对应） */
export const INERT_FIELD_LEDGER: Readonly<
  Record<BlockKey, Readonly<Record<string, InertFieldLedgerEntry>>>
> = {
  lock: {
    'Trigger.AllowPmcManualProtection': {
      k: 'K1',
      cls: 'F',
      treatment:
        '0号位 终裁（2026-11-10）：#7「允许 PMC 人工强保护」整体移 V1.1，V1 不实现 ⇒ 页面隐藏（F 类），载荷原样保留回写'
    },
    'Sticky.RequireReleaseRecord': {
      k: 'K2',
      cls: 'D',
      treatment: 'V1 固定项，不开放配置'
    },
    'Sticky.ProtectUntilCompletion': {
      k: 'K3',
      cls: 'H',
      treatment: '维持现状（隐藏），待 R02 判据后再定'
    },
    'Sticky.ProtectUntilSupplyInvalid': {
      k: 'K4',
      cls: 'H',
      treatment: '维持现状（隐藏），待 R02 判据后再定'
    }
  },
  supply: {
    'Inventory.IsEnabled': {
      k: 'K5',
      cls: 'E',
      treatment: '由 InventoryAvailabilityRule 承载 ⇒ 隐藏'
    },
    'Inventory.RequireFactoryContext': {
      k: 'K6',
      cls: 'E',
      treatment: '由 InventoryAvailabilityRule 承载 ⇒ 隐藏'
    },
    'Inventory.RequireProductFamilyContext': {
      k: 'K7',
      cls: 'E',
      treatment: '由 InventoryAvailabilityRule 承载 ⇒ 隐藏'
    }
  },
  procurement: {
    'OverdueMargin.MinimumExtraDays': {
      k: 'K8',
      cls: 'DEPRECATED',
      treatment: '后续受控废弃（非预留位）⇒ 标「受控废弃」，不得写「接入后自动生效」'
    }
  },
  solverStrategy: {
    'OnTimeTarget.IsPrimaryObjective': {
      k: 'K9',
      cls: 'D',
      treatment: 'V1 固定 true，不开放配置'
    },
    'Split.LimitMandatorySplit': {
      k: 'K10',
      cls: 'D',
      treatment: 'V1 固定 false，不开放配置'
    },
    'StageOverlap.ThresholdQty': {
      k: 'K11',
      cls: 'F',
      treatment: 'V1 不使用 ⇒ 隐藏'
    },
    'StageOverlap.ThresholdPercent': {
      k: 'K12',
      cls: 'F',
      treatment: 'V1 不使用 ⇒ 隐藏'
    }
  },
  candidateGuardrail: {}
}

/* ===== 「暂未生效」标注（徽标文案 + 一句大白话）===== */

/**
 * 页面上给「运行期无消费者」字段的标注。
 *
 * 为什么按字段分文案：**「暂未生效」不是一句话能说清的事**。
 *  - 「待接入」：后端接线后自动生效 —— 说「后端接入后自动生效，不用重配」是对的；
 *  - 「受控废弃」：根本不会再接入（K8）—— 说「接入后自动生效」是**假承诺**；
 *  - 「V1 固定」：冻结值、不开放修改（K2/K9/K10）—— 说「接入后自动生效」是**假承诺**。
 *
 * K1（`Trigger.AllowPmcManualProtection`）原为「开放＋排程接线中」，但 0号位 2026-11-10 终裁
 *   把它整体移 V1.1、V1 不实现 ⇒ 字段已在维护面隐藏（F 类），**不再有徽标文案**。
 */
export interface InertAnnotation {
  /** 行上徽标文案 */
  tag: string
  /** 悬停/弹窗里的一句大白话（≤2 行） */
  note: string
}

/** 缺省标注：待接入 */
const DEFAULT_INERT_ANNOTATION: InertAnnotation = {
  tag: '暂未生效',
  note: '排产运行期还没读取它，配置后不会改变排产结果；后端接入后自动生效，不用重配。'
}

/** 逐字段标注覆盖（3号位 2026-11-07 回执 §五 K 表的页面处置口径；
 *  K1 已随 0号位 11-10 终裁移 V1.1 ⇒ 字段隐藏、不再出徽标） */
const INERT_ANNOTATIONS: Partial<Record<BlockKey, Record<string, InertAnnotation>>> = {
  lock: {
    'Sticky.RequireReleaseRecord': {
      tag: '暂未生效',
      note: 'V1 固定项：不开放修改；运行期没有读取点，配了也不会改变排产结果。'
    }
  },
  procurement: {
    'OverdueMargin.MinimumExtraDays': {
      tag: '受控废弃',
      note: '这一项已决定后续受控废弃：排产运行期不会读取它，配置后不会改变排产结果，也不会自动生效。'
    }
  },
  solverStrategy: {
    'OnTimeTarget.IsPrimaryObjective': {
      tag: '暂未生效',
      note: 'V1 固定项：不开放修改；运行期没有读取点，配了也不会改变排产结果。'
    },
    'Split.LimitMandatorySplit': {
      tag: '暂未生效',
      note: 'V1 固定项：不开放修改；运行期没有读取点，配了也不会改变排产结果。'
    }
  }
}

/** 该字段的「暂未生效」标注（徽标文案 + 一句大白话说明） */
export function inertAnnotationOf(block: BlockKey, path: string): InertAnnotation {
  return INERT_ANNOTATIONS[block]?.[path] ?? DEFAULT_INERT_ANNOTATION
}

/** 该块的字段是否**全部**无消费者（锁定策略整块；块标题上标一次，免得空块没有任何提示） */
export function isFullyInertBlock(block: BlockKey): boolean {
  const fields = listBlockFields(block)
  return fields.length > 0 && fields.every((f) => isInertBlockField(block, f.path))
}
