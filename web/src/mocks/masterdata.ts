// In-memory stand-in for the master data, lookups, run defaults and preview endpoints (offline mock mode).
import type {
  BulkItemRequest, DataSource, ItemGroupInfo, ItemPlanningMethod, LeadTimeSource, LotSizingRule, MasterB1Values, MasterItemPage,
  MasterItemQuery, MasterItemRow, MasterOverride, MasterSupplierPage, MasterSupplierQuery, MasterSupplierRow, OverridableField,
  PlannedOrder, RunCriteria, RunPreview, SupplierLookup,
} from '../api/types';
import * as adm from './admin';

export const MOCK_WAREHOUSES = ['WH-MAIN', 'WH-PROD', 'WH-DXB'];
const err = (m: string) => new adm.MockHttpError(400, m);

const GROUPS: { code: string; name: string; lt: number }[] = [
  { code: '100', name: 'Finished goods', lt: 0 },
  { code: '101', name: 'Sub-assemblies', lt: 3 },
  { code: '102', name: 'Raw materials', lt: 5 },
  { code: '103', name: 'Components', lt: 8 },
  { code: '104', name: 'Packaging', lt: 0 },
];

const SUPPLIERS: SupplierLookup[] = [
  { cardCode: 'V-STEELCO', cardName: 'SteelCo Industrial' }, { cardCode: 'V-SKFGULF', cardName: 'SKF Gulf Trading' },
  { cardCode: 'V-COPPERME', cardName: 'Copper Middle East' }, { cardCode: 'V-SEALTEK', cardName: 'SealTek Components' },
  { cardCode: 'V-PACKPRO', cardName: 'PackPro Packaging' }, { cardCode: 'V-FASTENER', cardName: 'Fastener World' },
  { cardCode: 'V-ELECTRA', cardName: 'Electra Motors LLC' }, { cardCode: 'V-POLYMER', cardName: 'Polymer Plastics' },
  { cardCode: 'V-BEARINGX', cardName: 'BearingX Europe' }, { cardCode: 'V-CASTALU', cardName: 'CastAlu Foundry' },
  { cardCode: 'V-COATING', cardName: 'Gulf Coating Services' }, { cardCode: 'V-LUBRIC', cardName: 'Lubric Chemicals' },
];

// Seeded, deterministic generator so the data set is stable between reloads.
let seed = 424242;
const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
const ri = (a: number, b: number) => a + Math.floor(rnd() * (b - a + 1));

interface BaseItem { itemCode: string; itemName: string; groupCode: string; procurement: 'Buy' | 'Make'; b1: MasterB1Values }

const mkB1 = (o: Partial<MasterB1Values>, group: string): MasterB1Values => ({
  planningMethod: 'Mrp', lotSizing: 'LotForLot', leadTimeDays: 0,
  groupLeadTimeDays: GROUPS.find((g) => g.code === group)?.lt ?? 0,
  minOrderQty: 0, orderMultiple: 0, safetyStock: 0, reorderPoint: 0, maxStockLevel: 0, fixedLotSize: 0, periodOfSupplyDays: 0,
  supplier: null, supplierName: null, ...o,
});

const supplierName = (c: string | null) => (c ? SUPPLIERS.find((s) => s.cardCode === c)?.cardName ?? null : null);

const BASE: BaseItem[] = (() => {
  const out: BaseItem[] = [];
  const add = (code: string, name: string, group: string, proc: 'Buy' | 'Make', o: Partial<MasterB1Values>) =>
    out.push({ itemCode: code, itemName: name, groupCode: group, procurement: proc, b1: mkB1({ ...o, supplierName: supplierName(o.supplier ?? null) }, group) });
  // the ten items the sample runs already use
  add('FG-PUMP-200', 'Centrifugal Pump 200', '100', 'Make', { leadTimeDays: 6, safetyStock: 10 });
  add('FG-PUMP-400', 'Centrifugal Pump 400', '100', 'Make', { leadTimeDays: 8, safetyStock: 5 });
  add('SA-MOTOR-15', 'Motor Assembly 1.5kW', '101', 'Make', { leadTimeDays: 5 });
  add('SA-HOUSING-A', 'Pump Housing Assembly', '101', 'Make', { leadTimeDays: 0 });
  add('SA-IMPELLER', 'Impeller Sub-assembly', '101', 'Make', { leadTimeDays: 4 });
  add('RM-STEEL-SHT', 'Stainless Steel Sheet', '102', 'Buy', { leadTimeDays: 14, supplier: 'V-STEELCO', minOrderQty: 100, orderMultiple: 50, lotSizing: 'MinMaxMultiple', safetyStock: 200 });
  add('RM-BEARING-62', 'Ball Bearing 6204', '103', 'Buy', { leadTimeDays: 10, supplier: 'V-SKFGULF', minOrderQty: 200, safetyStock: 400, reorderPoint: 600 });
  add('RM-SEAL-KIT', 'Mechanical Seal Kit', '103', 'Buy', { leadTimeDays: 0, supplier: null, minOrderQty: 25 });
  add('RM-COPPER-WIRE', 'Copper Winding Wire', '102', 'Buy', { leadTimeDays: 21, supplier: 'V-COPPERME', orderMultiple: 100 });
  add('RM-SHAFT-SS', 'SS Drive Shaft', '103', 'Buy', { leadTimeDays: 12, supplier: 'V-CASTALU', minOrderQty: 20 });
  const kinds: [string, string, string, 'Buy' | 'Make'][] = [
    ['RM', 'Aluminium Plate', '102', 'Buy'], ['RM', 'Brass Rod', '102', 'Buy'], ['CP', 'Hex Bolt M8', '103', 'Buy'],
    ['CP', 'O-Ring 50mm', '103', 'Buy'], ['CP', 'Gasket Kit', '103', 'Buy'], ['PK', 'Carton Box Large', '104', 'Buy'],
    ['PK', 'Pallet Wrap', '104', 'Buy'], ['SA', 'Control Panel', '101', 'Make'], ['FG', 'Booster Pump 100', '100', 'Make'],
    ['RM', 'Lubricant 5L', '102', 'Buy'],
  ];
  const methods: ItemPlanningMethod[] = ['Mrp', 'Mrp', 'Mrp', 'ReorderPoint', 'MinMax'];
  const lots: LotSizingRule[] = ['LotForLot', 'LotForLot', 'FixedQuantity', 'MinMaxMultiple', 'PeriodOfSupply'];
  for (let i = 11; i <= 60; i++) {
    const [pre, nm, g, proc] = kinds[i % kinds.length];
    const buy = proc === 'Buy';
    const noLt = rnd() < 0.2;
    const sup = buy && rnd() > 0.12 ? SUPPLIERS[ri(0, SUPPLIERS.length - 1)].cardCode : null;
    const lot = lots[ri(0, lots.length - 1)];
    add(`${pre}-${String(1000 + i)}`, `${nm} ${String.fromCharCode(65 + (i % 8))}${i}`, g, proc, {
      leadTimeDays: noLt ? 0 : ri(2, 30), supplier: sup, planningMethod: methods[ri(0, methods.length - 1)], lotSizing: lot,
      minOrderQty: buy ? ri(0, 4) * 25 : 0, orderMultiple: rnd() < 0.3 ? ri(1, 4) * 10 : 0,
      safetyStock: rnd() < 0.5 ? ri(1, 10) * 10 : 0, reorderPoint: rnd() < 0.3 ? ri(5, 20) * 10 : 0,
      maxStockLevel: rnd() < 0.25 ? ri(30, 80) * 10 : 0, fixedLotSize: lot === 'FixedQuantity' ? ri(2, 10) * 50 : 0,
      periodOfSupplyDays: lot === 'PeriodOfSupply' ? ri(1, 4) * 7 : 0,
    });
  }
  return out;
})();

const overrides = new Map<string, MasterOverride>();
const supplierLt = new Map<string, { leadTimeDays: number | null; notes: string; updatedBy: string; updatedAt: string }>([
  ['V-STEELCO', { leadTimeDays: 21, notes: 'Mill lead time', updatedBy: 'admin', updatedAt: '2026-10-01T08:00:00Z' }],
  ['V-SKFGULF', { leadTimeDays: 9, notes: '', updatedBy: 'admin', updatedAt: '2026-10-01T08:00:00Z' }],
  ['V-ELECTRA', { leadTimeDays: 30, notes: 'Shipped by sea', updatedBy: 'planner', updatedAt: '2026-10-03T09:30:00Z' }],
]);

const FIELDS: OverridableField[] = [
  'planningMethod', 'lotSizing', 'leadTimeDays', 'minOrderQty', 'orderMultiple', 'safetyStock', 'reorderPoint', 'maxStockLevel',
  'fixedLotSize', 'periodOfSupplyDays', 'supplier',
];
const NUMERIC: OverridableField[] = ['leadTimeDays', 'minOrderQty', 'orderMultiple', 'safetyStock', 'reorderPoint', 'maxStockLevel', 'fixedLotSize', 'periodOfSupplyDays'];
const METHODS = ['Mrp', 'ReorderPoint', 'MinMax', 'None'];
const LOTS = ['LotForLot', 'FixedQuantity', 'MinMaxMultiple', 'PeriodOfSupply'];

const guardSource = (source?: DataSource) => {
  if (source === 'b1-sql' && !adm.b1Configured()) throw err('The SAP B1 database is not configured. Set it up in Settings first.');
};

function effectiveOf(b: BaseItem): MasterB1Values {
  const o = overrides.get(b.itemCode);
  const e: MasterB1Values = { ...b.b1 };
  if (o) {
    for (const f of FIELDS) {
      const v = o[f];
      if (v !== null && v !== undefined) (e as unknown as Record<string, unknown>)[f] = v;
    }
    e.supplierName = supplierName(e.supplier);
  }
  return e;
}

/** Lead time under the CURRENT default priority / mode (Settings), like the server's lead-time column. */
export function resolveLeadTime(b: BaseItem, eff: MasterB1Values) {
  const p = adm.planningSettings();
  const sup = b.procurement === 'Buy' && eff.supplier ? supplierLt.get(eff.supplier)?.leadTimeDays ?? null : null;
  const item = eff.leadTimeDays > 0 ? eff.leadTimeDays : null;
  const grp = eff.groupLeadTimeDays > 0 ? eff.groupLeadTimeDays : null;
  const val: Record<string, number | null> = { Supplier: sup, Item: item, ItemGroup: grp };
  const avail = p.leadTimePriority.filter((l) => val[l] != null);
  let days: number | null = null;
  let source: LeadTimeSource = 'None';
  if (avail.length) {
    const win = p.leadTimeMode === 'Longest' ? avail.reduce((a, c) => (val[c]! > val[a]! ? c : a)) : avail[0];
    days = val[win]; source = win;
  } else if (p.defaultLeadTimeDays > 0) { days = p.defaultLeadTimeDays; source = 'Default'; }
  return { days, source, supplierDays: sup, itemDays: item, groupDays: grp };
}

function toRow(b: BaseItem): MasterItemRow {
  const o = overrides.get(b.itemCode) ?? null;
  const eff = effectiveOf(b);
  return {
    itemCode: b.itemCode, itemName: b.itemName, groupCode: b.groupCode, groupName: GROUPS.find((g) => g.code === b.groupCode)!.name,
    procurement: b.procurement, b1: { ...b.b1 }, override: o ? { ...o } : null, effective: eff,
    leadTime: { ...resolveLeadTime(b, eff), supplierItemCount: b.procurement === 'Buy' && eff.supplier ? BASE.filter((x) => x.procurement === 'Buy' && effectiveOf(x).supplier === eff.supplier).length : 0 },
    overriddenFields: o ? FIELDS.filter((f) => o[f] !== null && o[f] !== undefined) : [],
  };
}

export const itemRow = (code: string): MasterItemRow | undefined => { const b = BASE.find((x) => x.itemCode === code); return b && toRow(b); };

/** Decorates a mock planned order with lead time and (for purchases) a master-data vendor. */
export function decorateOrder(po: PlannedOrder): PlannedOrder {
  const r = itemRow(po.itemCode);
  if (!r) return po;
  const out: PlannedOrder = { ...po, leadTimeDays: r.leadTime.days, leadTimeSource: r.leadTime.source };
  if (po.type === 'PurchaseOrder' && r.override?.supplier) { out.vendorCode = r.override.supplier; out.vendorSource = 'master-data'; }
  return out;
}

const matches = (r: MasterItemRow, q: { search?: string; group?: string; supplier?: string; procurement?: string; filter?: string }) => {
  const s = q.search?.trim().toLowerCase();
  if (s && !r.itemCode.toLowerCase().includes(s) && !r.itemName.toLowerCase().includes(s)) return false;
  if (q.group && r.groupCode !== q.group) return false;
  if (q.supplier && r.effective.supplier !== q.supplier) return false;
  if (q.procurement && r.procurement !== q.procurement) return false;
  if (q.filter === 'overridden' && !r.overriddenFields.length) return false;
  if (q.filter === 'noSupplier' && !(r.procurement === 'Buy' && !r.effective.supplier)) return false;
  if (q.filter === 'noLeadTime' && r.leadTime.days != null) return false;
  return true;
};

export function listItems(q: MasterItemQuery): MasterItemPage {
  adm.requireSignedIn(); guardSource(q.source);
  let rows = BASE.map(toRow).filter((r) => matches(r, q));
  const dir = q.dir === 'desc' ? -1 : 1;
  const key = q.sort ?? 'itemCode';
  rows.sort((a, b) => {
    const c = key === 'leadTime' ? (a.leadTime.days ?? -1) - (b.leadTime.days ?? -1) : String(a[key]).localeCompare(String(b[key]));
    return c * dir;
  });
  const pageSize = q.pageSize ?? 100, page = Math.max(1, q.page ?? 1);
  const total = rows.length;
  rows = rows.slice((page - 1) * pageSize, page * pageSize);
  return { total, page, pageSize, items: rows };
}

function validateOverride(o: Partial<Record<string, unknown>>) {
  for (const f of NUMERIC) {
    const v = o[f];
    if (v != null && (typeof v !== 'number' || !Number.isFinite(v) || v < 0)) throw err(`${f} must be a number of 0 or more.`);
  }
  if (o.planningMethod != null && !METHODS.includes(String(o.planningMethod))) throw err('Unknown planning method.');
  if (o.lotSizing != null && !LOTS.includes(String(o.lotSizing))) throw err('Unknown lot sizing rule.');
  if (o.supplier != null && o.supplier !== '' && !SUPPLIERS.some((s) => s.cardCode === o.supplier)) throw err(`Unknown supplier "${String(o.supplier)}". Pick an existing business partner.`);
}

const blank = (): MasterOverride => ({
  planningMethod: null, lotSizing: null, leadTimeDays: null, minOrderQty: null, orderMultiple: null, safetyStock: null,
  reorderPoint: null, maxStockLevel: null, fixedLotSize: null, periodOfSupplyDays: null, supplier: null, notes: '',
});
function store(code: string, o: MasterOverride) {
  const has = FIELDS.some((f) => o[f] !== null && o[f] !== undefined) || !!o.notes;
  if (has) overrides.set(code, { ...o, updatedBy: adm.currentUserName(), updatedAt: new Date().toISOString() });
  else overrides.delete(code);
}
const base = (code: string) => { const b = BASE.find((x) => x.itemCode === code); if (!b) throw new adm.MockHttpError(404, `Item ${code} not found.`); return b; };

export function putItem(code: string, body: Partial<MasterOverride>): MasterItemRow {
  adm.requireSignedIn();
  const b = base(code);
  validateOverride(body);
  const o = blank();
  for (const f of FIELDS) (o as unknown as Record<string, unknown>)[f] = body[f] === '' ? null : body[f] ?? null;
  o.notes = body.notes ?? '';
  store(code, o);
  return toRow(b);
}

export function bulkItems(req: BulkItemRequest): { updated: number } {
  adm.requireSignedIn();
  if (!req.itemCodes.length) throw err('Select at least one item.');
  validateOverride(req.set);
  if (!Object.keys(req.set).length && !req.clear.length) throw err('Nothing to change: set a value or clear a field.');
  for (const c of req.itemCodes) {
    base(c);
    const o = { ...(overrides.get(c) ?? blank()) };
    for (const [k, v] of Object.entries(req.set)) (o as unknown as Record<string, unknown>)[k] = v;
    for (const k of req.clear) (o as unknown as Record<string, unknown>)[k] = null;
    store(c, o);
  }
  return { updated: req.itemCodes.length };
}

export function deleteItem(code: string) { adm.requireSignedIn(); base(code); overrides.delete(code); }

const csvCell = (v: unknown) => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
export function exportCsv(q: MasterItemQuery): string {
  const rows = listItems({ ...q, page: 1, pageSize: 100000 }).items;
  const head = ['ItemCode', 'ItemName', 'Group', 'Type', 'Supplier', 'LeadTimeDays', 'LeadTimeSource', 'MinOrderQty', 'OrderMultiple', 'SafetyStock', 'ReorderPoint', 'MaxStockLevel', 'PlanningMethod', 'Overridden'];
  const lines = rows.map((r) => [r.itemCode, r.itemName, r.groupName, r.procurement, r.effective.supplier, r.leadTime.days, r.leadTime.source,
    r.effective.minOrderQty, r.effective.orderMultiple, r.effective.safetyStock, r.effective.reorderPoint, r.effective.maxStockLevel,
    r.effective.planningMethod, r.overriddenFields.join('|')].map(csvCell).join(','));
  return [head.join(','), ...lines].join('\r\n');
}

// ----- suppliers -----
const itemCountOf = (card: string) => BASE.filter((b) => effectiveOf(b).supplier === card).length;
const supRow = (s: SupplierLookup): MasterSupplierRow => {
  const l = supplierLt.get(s.cardCode);
  return { cardCode: s.cardCode, cardName: s.cardName, leadTimeDays: l?.leadTimeDays ?? null, notes: l?.notes ?? '', itemCount: itemCountOf(s.cardCode), updatedBy: l?.updatedBy, updatedAt: l?.updatedAt };
};
export function listSuppliers(q: MasterSupplierQuery): MasterSupplierPage {
  adm.requireSignedIn(); guardSource(q.source);
  const s = q.search?.trim().toLowerCase();
  let rows = SUPPLIERS.map(supRow).filter((r) =>
    (!s || r.cardCode.toLowerCase().includes(s) || r.cardName.toLowerCase().includes(s)) &&
    (q.filter === 'withLeadTime' ? r.leadTimeDays != null : q.filter === 'withoutLeadTime' ? r.leadTimeDays == null : q.filter === 'inUse' ? r.itemCount > 0 : true));
  const pageSize = q.pageSize ?? 100, page = Math.max(1, q.page ?? 1), total = rows.length;
  rows = rows.slice((page - 1) * pageSize, page * pageSize);
  return { total, page, pageSize, suppliers: rows };
}
export function putSupplier(card: string, body: { leadTimeDays: number | null; notes: string }): MasterSupplierRow {
  adm.requireSignedIn();
  const s = SUPPLIERS.find((x) => x.cardCode === card);
  if (!s) throw new adm.MockHttpError(404, `Supplier ${card} not found.`);
  if (body.leadTimeDays != null && (!Number.isFinite(body.leadTimeDays) || body.leadTimeDays < 0)) throw err('Lead time must be a number of 0 or more.');
  if (body.leadTimeDays == null && !body.notes) supplierLt.delete(card);
  else supplierLt.set(card, { leadTimeDays: body.leadTimeDays, notes: body.notes ?? '', updatedBy: adm.currentUserName(), updatedAt: new Date().toISOString() });
  return supRow(s);
}

export function importSuppliers(rows: { line: number; cardCode: string; leadTimeDays: string; notes: string }[], dryRun: boolean) {
  adm.requireSignedIn();
  if (!rows.length) throw err('The file has no rows.');
  const seen = new Set<string>(); const results: { line: number; cardCode: string; status: 'change' | 'same' | 'error'; message: string; before: number | null; after: number | null }[] = [];
  for (const r of rows) {
    const code = r.cardCode.trim(); const bad = (m: string) => results.push({ line: r.line, cardCode: code, status: 'error', message: m, before: null, after: null });
    if (!code) { bad('The supplier code is empty.'); continue; }
    const s = SUPPLIERS.find((x) => x.cardCode.toLowerCase() === code.toLowerCase());
    if (!s) { bad('This supplier is not in the data source.'); continue; }
    if (seen.has(s.cardCode)) { bad('Duplicate: only the first one is used.'); continue; }
    seen.add(s.cardCode);
    const cur = supplierLt.get(s.cardCode); let lt = cur?.leadTimeDays ?? null; const t = r.leadTimeDays.trim();
    if (t) { const n = Number(t); if (!Number.isInteger(n) || n < 0 || n > 3650) { bad(`Lead time '${t}' must be a whole number from 0 to 3650.`); continue; } lt = n; }
    const notes = r.notes.trim() || cur?.notes || '';
    if (lt === (cur?.leadTimeDays ?? null) && notes === (cur?.notes ?? '')) { results.push({ line: r.line, cardCode: s.cardCode, status: 'same', message: 'Already has these values.', before: cur?.leadTimeDays ?? null, after: lt }); continue; }
    results.push({ line: r.line, cardCode: s.cardCode, status: 'change', message: cur ? 'Updated.' : 'New lead time.', before: cur?.leadTimeDays ?? null, after: lt });
    if (!dryRun) supplierLt.set(s.cardCode, { leadTimeDays: lt, notes, updatedBy: adm.currentUserName(), updatedAt: new Date().toISOString() });
  }
  const willChange = results.filter((x) => x.status === 'change').length;
  return { dryRun, applied: dryRun ? 0 : willChange, willChange, same: results.filter((x) => x.status === 'same').length, errors: results.filter((x) => x.status === 'error').length, results };
}

// ----- lookups -----
export function lookupGroups(source: DataSource): ItemGroupInfo[] {
  adm.requireSignedIn(); guardSource(source);
  return GROUPS.map((g) => ({ code: g.code, name: g.name, leadTimeDays: g.lt || null, itemCount: BASE.filter((b) => b.groupCode === g.code).length }));
}
export function lookupSuppliers(source: DataSource, search = '', take = 50): SupplierLookup[] {
  adm.requireSignedIn(); guardSource(source);
  const s = search.trim().toLowerCase();
  return SUPPLIERS.filter((x) => !s || x.cardCode.toLowerCase().includes(s) || x.cardName.toLowerCase().includes(s)).slice(0, take);
}

// ----- run defaults and preview -----
export function runDefaults(source: DataSource): RunCriteria {
  adm.requireSignedIn(); guardSource(source);
  const p = adm.planningSettings();
  return {
    source,
    level: p.defaultLevel ?? 'Warehouse',
    purchasing: { document: p.purchaseDocument ?? 'Order' },
    transfers: { enabled: false, sourceWarehouses: [], leadTimeDays: p.transferLeadTimeDays ?? 1 },
    scope: { warehouses: [], itemGroups: [], suppliers: [], itemSearch: '', itemCodes: [], procurement: 'Both', planningMethods: [], includeComponents: true },
    horizon: { startDate: null, days: p.horizonDays, bucketDays: p.bucketDays },
    demand: {
      salesOrders: true, salesOrderFrom: null, salesOrderTo: null, forecasts: true, forecastConsumption: p.forecastConsumption,
      forecastLookbackDays: p.forecastLookbackDays, productionComponents: true, pastDue: 'FoldToStart', safetyStock: true,
    },
    supply: { onHand: true, openPurchaseOrders: true, openProductionOrders: true },
    leadTime: { priority: [...p.leadTimePriority], mode: p.leadTimeMode, defaultDays: p.defaultLeadTimeDays },
    lotSizing: { applyMinOrder: true, applyMultiples: true },
    calendar: { useWorkingCalendar: true },
    capacity: { enabled: true },
  };
}

export function validateCriteria(c: RunCriteria) {
  adm.requireSignedIn(); guardSource(c.source);
  if (!Number.isFinite(c.horizon.days) || c.horizon.days < 1 || c.horizon.days > 1095) throw err('Horizon must be between 1 and 1095 days.');
  if (c.scope.itemGroups.some((g) => !/^\d+$/.test(g))) throw err('Item groups must be numeric codes.');
  if (c.level === 'Warehouse' && c.transfers?.enabled) {
    const chosen = c.scope.warehouses.length ? c.scope.warehouses : MOCK_WAREHOUSES;
    if (chosen.length < 2) throw err('Transfers need at least two warehouses.');
    if (c.transfers.sourceWarehouses.some((w) => !chosen.includes(w))) throw err('Every transfer source must be one of the selected warehouses.');
  }
}

/** Items in scope plus the components of selected make items (a crude BOM: components = lower group codes). */
function scopeItems(c: RunCriteria): MasterItemRow[] {
  const all = BASE.map(toRow);
  const s = c.scope;
  const search = s.itemSearch.trim().toLowerCase();
  const direct = all.filter((r) =>
    (!s.itemGroups.length || s.itemGroups.includes(r.groupCode)) &&
    (!s.suppliers.length || (r.effective.supplier != null && s.suppliers.includes(r.effective.supplier))) &&
    (!search || r.itemCode.toLowerCase().includes(search) || r.itemName.toLowerCase().includes(search)) &&
    (!s.itemCodes.length || s.itemCodes.includes(r.itemCode)) &&
    (s.procurement === 'Both' || r.procurement === s.procurement) &&
    (!s.planningMethods.length || s.planningMethods.includes(r.effective.planningMethod as never)));
  const set = new Map(direct.map((r) => [r.itemCode, r]));
  if (s.includeComponents && direct.some((r) => r.procurement === 'Make'))
    for (const r of all) if (r.procurement === 'Buy' && s.procurement !== 'Make' && !s.suppliers.length) set.set(r.itemCode, r);
  return [...set.values()];
}

export interface ItemSets {
  all: MasterItemRow[]; scope: MasterItemRow[]; buy: MasterItemRow[]; make: MasterItemRow[]; noSupplier: MasterItemRow[]; noLeadTime: MasterItemRow[];
  lt: { supplier: MasterItemRow[]; item: MasterItemRow[]; itemGroup: MasterItemRow[]; defaulted: MasterItemRow[] };
  /** Lead time (days, source) as the preview sees it for a row, honouring the criteria's default days. */
  leadOf: (r: MasterItemRow) => { days: number | null; source: string };
}
/** The item lists behind the preview numbers (same selection as preview()). */
export function itemSets(c: RunCriteria): ItemSets {
  validateCriteria(c);
  const scope = scopeItems(c);
  const buy = scope.filter((i) => i.procurement === 'Buy');
  const leadOf = (r: MasterItemRow) => {
    const s = resolveLeadTime(base(r.itemCode), r.effective);
    return s.days == null && c.leadTime.defaultDays > 0 ? { days: c.leadTime.defaultDays, source: 'Default' } : { days: s.days, source: s.source };
  };
  const by = (src: string) => buy.filter((i) => leadOf(i).source === src);
  return {
    all: BASE.map(toRow), scope, buy, make: scope.filter((i) => i.procurement === 'Make'),
    noSupplier: buy.filter((i) => !i.effective.supplier),
    noLeadTime: buy.filter((i) => leadOf(i).source === 'None'),
    lt: { supplier: by('Supplier'), item: by('Item'), itemGroup: by('ItemGroup'), defaulted: by('Default') },
    leadOf,
  };
}

export function preview(c: RunCriteria): RunPreview {
  validateCriteria(c);
  const items = scopeItems(c);
  const buy = items.filter((i) => i.procurement === 'Buy');
  const whFactor = c.scope.warehouses.length ? Math.max(1, c.scope.warehouses.length) / 3 : 1;
  const n = (x: number) => Math.round(x * Math.min(1, whFactor));
  const hz = Math.min(1, c.horizon.days / 180) || 0.1;
  const lt = { fromSupplier: 0, fromItem: 0, fromItemGroup: 0, defaulted: 0 };
  let noLt = 0;
  for (const i of buy) {
    const src = resolveLeadTime(base(i.itemCode), i.effective);
    const eff = src.days == null && c.leadTime.defaultDays > 0 ? 'Default' : src.source;
    if (eff === 'Supplier') lt.fromSupplier++; else if (eff === 'Item') lt.fromItem++; else if (eff === 'ItemGroup') lt.fromItemGroup++;
    else if (eff === 'Default') lt.defaulted++; else noLt++;
  }
  const noSupplier = buy.filter((i) => !i.effective.supplier).length;
  const d = c.demand;
  const demand = {
    salesOrderLines: d.salesOrders ? n(Math.round(items.length * 1.6 * hz)) : 0,
    salesOrderQuantity: d.salesOrders ? n(Math.round(items.length * 22 * hz)) : 0,
    pastDueLines: d.salesOrders && d.pastDue === 'FoldToStart' ? n(Math.ceil(items.length / 14)) : 0,
    forecastLines: d.forecasts ? n(Math.round(items.filter((i) => i.groupCode === '100').length * 4 * hz)) : 0,
    productionComponentLines: d.productionComponents ? n(Math.round(items.length * 0.5)) : 0,
  };
  const warnings: string[] = [];
  if (!items.length) warnings.push('No items match this scope: the run would plan nothing.');
  if (noSupplier) warnings.push(`${noSupplier} purchased item${noSupplier === 1 ? '' : 's'} in scope ${noSupplier === 1 ? 'has' : 'have'} no supplier: their orders cannot be released to SAP B1.`);
  if (noLt) warnings.push(`${noLt} purchased item${noLt === 1 ? '' : 's'} in scope ${noLt === 1 ? 'has' : 'have'} no lead time and the default is 0 days: orders will be planned to release on their due date.`);
  if (!c.supply.onHand) warnings.push('On-hand stock is ignored: expect much larger planned quantities.');
  if (!c.demand.salesOrders && !c.demand.forecasts) warnings.push('Neither sales orders nor forecasts drive demand.');
  const planWh = c.scope.warehouses.length ? c.scope.warehouses : MOCK_WAREHOUSES;
  if (c.level === 'Company' && planWh.length > 1) warnings.push(`Company level: stock in ${planWh.length} warehouses is pooled; orders are received in each item's default warehouse.`);
  if (c.level === 'Warehouse' && c.transfers.enabled && planWh.length < 2) warnings.push('Transfers are on but only one warehouse is selected.');
  return {
    items: { total: BASE.length, inScope: items.length, buy: buy.length, make: items.length - buy.length, noSupplier, noLeadTime: noLt },
    demand,
    supply: {
      purchaseOrderLines: c.supply.openPurchaseOrders ? n(Math.round(buy.length * 0.6)) : 0,
      productionOrderLines: c.supply.openProductionOrders ? n(Math.round((items.length - buy.length) * 0.5)) : 0,
      stockRows: c.supply.onHand ? n(items.length * 3) : 0,
    },
    leadTime: lt, warnings,
    planning: {
      level: c.level, warehouses: planWh, purchaseDocument: c.purchasing.document,
      transfersEnabled: c.level === 'Warehouse' && c.transfers.enabled,
      transferSources: c.transfers.sourceWarehouses.length ? c.transfers.sourceWarehouses : planWh,
      transferLeadTimeDays: c.transfers.leadTimeDays,
    },
  };
}
