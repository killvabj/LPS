/**
 * APS V1 4号位 — 参数集「仓库引用」四处落点的读写与判定（纯函数）
 *
 * 落点（后端真源：`GovernanceVersionService.cs:1019-1034` 的采集段 +
 *  DTO `FrozenStrategySnapshot.cs:198/223/225/229/236-238/248-250`）：
 *
 *   ① Supply.Inventory.WarehousePriority      IReadOnlyList<string>            有序仓库码
 *   ② Procurement.WarehousePriority            IReadOnlyList<string>            有序仓库码
 *   ③ Procurement.DefaultPurchaseLt[]           {WarehouseCode, MaterialId?, DefaultLtDays}
 *   ④ Procurement.ArrivalToUsableOffsets[]     {WarehouseCode, OffsetHours}
 *
 * 校验端（后端 `AddWarehouseReferenceErrors`，`GovernanceVersionService.cs:1061-1062`）：
 *   引用编码不在权威仓库事实源 `ext_MES_ProcessCode_View` ⇒ 报 `INVALID_WAREHOUSE_REF`，
 *   fieldName 分开报：**Supply/Warehouse**（①②③④ 里属 Supply 的那份）与 **Procurement/Warehouse**。
 *
 * ⚠️ 键名一律用后端 DTO 的 PascalCase（`WarehouseCode` / `MaterialId` / `DefaultLtDays` / `OffsetHours`）：
 *   后端反序列化虽是大小写不敏感（`JsonOptions.PropertyNameCaseInsensitive = true`，
 *   `GovernanceVersionService.cs:1371-1374`），但库里存量内容是 PascalCase ⇒ 写回保持同款，
 *   免得 diff 里出现「只有大小写变了」的噪声。
 *
 * ⚠️ 本模块只做**结构化读写与判定**，不造默认值、不猜业务语义（真实码适用性由 5号位 判定）。
 *   末段的「仓库字典属性」是**展示层格式化**（文案 + 原材料库分组），不参与任何读写判定。
 */

import type { BlockKey, ParameterSetBlocks } from './types/rule'

/* ===== 四处落点的路径（相对 5 主题块）===== */

/** ① 供应参数 → 库存可用 → 仓库优先级（有序码列表） */
export const SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriority'] as const
/** ② 采购参数 → 仓库优先级（有序码列表） */
export const PROCUREMENT_WAREHOUSE_PRIORITY_PATH = ['WarehousePriority'] as const
/** ③ 采购参数 → 默认采购提前期（对象数组） */
export const DEFAULT_PURCHASE_LT_PATH = ['DefaultPurchaseLt'] as const
/** ④ 采购参数 → 到货可用偏移（对象数组） */
export const ARRIVAL_TO_USABLE_OFFSETS_PATH = ['ArrivalToUsableOffsets'] as const

/* ===== 数组项形状（与后端 DTO 对齐）===== */

/** `PurchaseLtRule`：按收货仓库 +（可选）物料维度定默认采购提前期 */
export interface PurchaseLtItem {
  WarehouseCode?: string
  /** 空 = 仓库级默认 */
  MaterialId?: string | null
  DefaultLtDays?: number
}

/** `WarehouseOffsetRule`：到货时间 → 可用时间偏移 */
export interface ArrivalOffsetItem {
  WarehouseCode?: string
  OffsetHours?: number
}

/* ===== 通用读写 ===== */

type JsonRecord = Record<string, unknown>

function asRecord(v: unknown): JsonRecord | null {
  return v && typeof v === 'object' && !Array.isArray(v) ? (v as JsonRecord) : null
}

/** 沿路径取末级对象；中间节点缺失 ⇒ 返回 null（不做隐式创建） */
function resolveParent(
  blocks: ParameterSetBlocks,
  block: BlockKey,
  path: readonly string[],
  create: boolean
): JsonRecord | null {
  let cur = asRecord(blocks[block])
  if (!cur) {
    if (!create) return null
    cur = {}
    blocks[block] = cur
  }
  for (let i = 0; i < path.length - 1; i++) {
    const key = path[i]
    const next = asRecord(cur[key])
    if (!next) {
      if (!create) return null
      const created: JsonRecord = {}
      cur[key] = created
      cur = created
    } else {
      cur = next
    }
  }
  return cur
}

/** 读字符串数组（缺失 / 类型不符 ⇒ 空数组） */
export function readStringArray(
  blocks: ParameterSetBlocks | null | undefined,
  block: BlockKey,
  path: readonly string[]
): string[] {
  if (!blocks) return []
  const parent = resolveParent(blocks, block, path, false)
  if (!parent) return []
  const arr = parent[path[path.length - 1]]
  if (!Array.isArray(arr)) return []
  return arr.filter((v): v is string => typeof v === 'string')
}

/** 写字符串数组（末级键不存在 ⇒ 建；中间节点缺失 ⇒ 建） */
export function writeStringArray(
  blocks: ParameterSetBlocks,
  block: BlockKey,
  path: readonly string[],
  values: string[]
): void {
  const parent = resolveParent(blocks, block, path, true)
  if (!parent) return
  parent[path[path.length - 1]] = values.slice()
}

/** 读对象数组（缺失 / 类型不符 ⇒ 空数组） */
export function readObjectArray<T extends object>(
  blocks: ParameterSetBlocks | null | undefined,
  block: BlockKey,
  path: readonly string[]
): T[] {
  if (!blocks) return []
  const parent = resolveParent(blocks, block, path, false)
  if (!parent) return []
  const arr = parent[path[path.length - 1]]
  if (!Array.isArray(arr)) return []
  return arr.filter((v): v is T => !!v && typeof v === 'object' && !Array.isArray(v))
}

/** 写对象数组 */
export function writeObjectArray<T extends object>(
  blocks: ParameterSetBlocks,
  block: BlockKey,
  path: readonly string[],
  values: T[]
): void {
  const parent = resolveParent(blocks, block, path, true)
  if (!parent) return
  parent[path[path.length - 1]] = values.map((v) => ({ ...v }))
}

/* ===== 仓库字典属性（只用于展示层，不参与任何读写判定）===== */

/**
 * 原材料库的 ERPProperty 分类：KS（课税）+ BS（保税）。
 *
 * 口径来源：5号位 2026-10-25《参数集「仓库编码」三问回执》Q1 —— 采购管道事实里落在
 * 字典内的码 **100% 是原材料库**；dev 实测 `ERPProperty` 分布 KS 61 + BS 22 = **83 码**，
 * 且这 83 码的 `warehouseRole` 全部是「原材料库」，两边互证。
 */
export const RAW_MATERIAL_ERP_PROPERTIES = ['KS', 'BS'] as const

/** 字典项的最小形状（展示层只取这几个字段，其余不用） */
export interface WarehouseDictEntry {
  code?: string | null
  name?: string | null
  /** ERP 属性分类（KS 课税 / BS 保税 / XC 现场 …，部分行为空） */
  erpProperty?: string | null
  /** 仓库角色（中文：原材料库 / 现场库 / ASSY品库 …） */
  warehouseRole?: string | null
  factoryCode?: string | null
}

/** 是否原材料库（ERPProperty ∈ KS/BS；大小写不敏感） */
export function isRawMaterialWarehouse(item: WarehouseDictEntry | null | undefined): boolean {
  const prop = String(item?.erpProperty ?? '')
    .trim()
    .toUpperCase()
  return (RAW_MATERIAL_ERP_PROPERTIES as readonly string[]).includes(prop)
}

/** 仓库属性文本：角色（中文）+ ERP 短码；缺哪项省哪项（12 条 ASSY品库无 ERP 短码） */
export function warehouseAttributeText(item: WarehouseDictEntry | null | undefined): string {
  const role = String(item?.warehouseRole ?? '').trim()
  const prop = String(item?.erpProperty ?? '')
    .trim()
    .toUpperCase()
  if (role && prop) return `${role} ${prop}`
  return role || prop
}

/**
 * 下拉选项文案：`编码（名称）· 角色 短码`。
 *
 * 5号位 Q1 建议②：下拉要把 `warehouseRole`（中文角色）与 `erpProperty`（ERP 短码）
 * 一并显示，让用户自己识别这个码的性质 —— 前端**不替用户做筛选**（全量都给）。
 */
export function warehouseOptionLabel(item: WarehouseDictEntry | null | undefined): string {
  const code = String(item?.code ?? '').trim()
  const name = String(item?.name ?? '').trim()
  const attr = warehouseAttributeText(item)
  const base = name ? `${code}（${name}）` : code
  return attr ? `${base} · ${attr}` : base
}

/** 下拉分段结果：原材料库分组 + 其余平铺 */
export interface WarehouseOptionSections<T> {
  /** 原材料库分组（不分组时为空数组） */
  raw: T[]
  /** 平铺段 */
  flat: T[]
}

/**
 * 把下拉选项拆成「原材料库分组 + 其余」两段。
 *
 * `grouped=false` 时**不分组**（整份平铺，raw 为空）—— 分组只是排版，
 * **两段合起来必须等于原列表**（一码不少）：这是「给全量、不硬过滤」的可测口径。
 */
export function splitWarehouseOptions<T extends { raw: boolean }>(
  options: T[],
  grouped: boolean
): WarehouseOptionSections<T> {
  if (!grouped) return { raw: [], flat: options.slice() }
  return {
    raw: options.filter((o) => o.raw),
    flat: options.filter((o) => !o.raw)
  }
}

/* ===== 通用表剔除判定 ===== */

/**
 * 该行是否是「仓库引用」落点（通用参数表里要剔除，交给专属编辑器）。
 *
 * 判定用 **块 + 点分路径** 精确匹配，不用 includes 子串 —— 子串匹配会误伤
 * `WarehousePriority` 之外的近似键，也会误伤别的块里的同名键。
 */
export function isWarehouseRefRow(block: BlockKey, path: readonly string[]): boolean {
  const p = path.join('.')
  if (block === 'supply') return p === SUPPLY_WAREHOUSE_PRIORITY_PATH.join('.')
  if (block === 'procurement') {
    return (
      p === PROCUREMENT_WAREHOUSE_PRIORITY_PATH.join('.') ||
      p === DEFAULT_PURCHASE_LT_PATH.join('.') ||
      p === ARRIVAL_TO_USABLE_OFFSETS_PATH.join('.')
    )
  }
  return false
}

/* ===== 仓库引用采集与判定 ===== */

/** 引用分组：Supply 与 Procurement 分开（对齐后端 fieldName 的分开报法） */
export interface WarehouseRefGroups {
  /** Supply.Inventory.WarehousePriority */
  supply: string[]
  /** Procurement 三个落点合起来 */
  procurement: string[]
}

function pushCode(target: string[], code: unknown): void {
  if (typeof code !== 'string') return
  const c = code.trim()
  if (!c) return
  if (!target.some((x) => x.toLowerCase() === c.toLowerCase())) target.push(c)
}

/** 采集 5 主题块里引用到的全部仓库编码（按 Supply / Procurement 分组、去重） */
export function collectWarehouseRefs(
  blocks: ParameterSetBlocks | null | undefined
): WarehouseRefGroups {
  const groups: WarehouseRefGroups = { supply: [], procurement: [] }
  if (!blocks) return groups

  for (const code of readStringArray(blocks, 'supply', SUPPLY_WAREHOUSE_PRIORITY_PATH)) {
    pushCode(groups.supply, code)
  }
  for (const code of readStringArray(blocks, 'procurement', PROCUREMENT_WAREHOUSE_PRIORITY_PATH)) {
    pushCode(groups.procurement, code)
  }
  for (const item of readObjectArray<PurchaseLtItem>(
    blocks,
    'procurement',
    DEFAULT_PURCHASE_LT_PATH
  )) {
    pushCode(groups.procurement, item.WarehouseCode)
  }
  for (const item of readObjectArray<ArrivalOffsetItem>(
    blocks,
    'procurement',
    ARRIVAL_TO_USABLE_OFFSETS_PATH
  )) {
    pushCode(groups.procurement, item.WarehouseCode)
  }
  return groups
}

/** 字典里不存在的编码（大小写不敏感，与后端 `OrdinalIgnoreCase` 一致） */
export function unknownWarehouseCodes(
  codes: string[],
  dictionaryCodes: Iterable<string>
): string[] {
  const known = new Set<string>()
  for (const c of dictionaryCodes) known.add(String(c).trim().toLowerCase())
  const missing: string[] = []
  for (const c of codes) {
    const key = String(c).trim().toLowerCase()
    if (!key || known.has(key)) continue
    if (!missing.some((x) => x.toLowerCase() === key)) missing.push(c)
  }
  return missing
}

/** 两组合并的未知编码 */
export function unknownWarehouseCodesOf(
  groups: WarehouseRefGroups,
  dictionaryCodes: Iterable<string>
): string[] {
  return unknownWarehouseCodes([...groups.supply, ...groups.procurement], dictionaryCodes)
}
