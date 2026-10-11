import { documentKey } from '../components/util';
import type {
  CapacityLoad,
  ExceptionMessage,
  Forecast,
  MrpRun,
  MrpRunDetail,
  PeggingLink,
  PlannedOrder,
  CalendarEvent,
  CalendarKind,
  CalendarQuery,
  CalendarResponse,
} from '../api/types';
import { decorateOrder } from './masterdata';
import type { RunCriteria } from '../api/types';

// ---------- small deterministic helpers (stable sample data) ----------
let seed = 20261009;
function rand() {
  seed = (seed * 1103515245 + 12345) & 0x7fffffff;
  return seed / 0x7fffffff;
}
function pick<T>(arr: T[]): T {
  return arr[Math.floor(rand() * arr.length)];
}
function round(n: number, dp = 0) {
  const f = Math.pow(10, dp);
  return Math.round(n * f) / f;
}
function isoDate(d: Date) {
  return d.toISOString().slice(0, 10);
}
function addDays(base: Date, days: number) {
  const d = new Date(base);
  d.setDate(d.getDate() + days);
  return d;
}

const ITEMS = [
  { code: 'FG-PUMP-200', level: 0, name: 'Centrifugal Pump 200' },
  { code: 'FG-PUMP-400', level: 0, name: 'Centrifugal Pump 400' },
  { code: 'SA-MOTOR-15', level: 1, name: 'Motor Assembly 1.5kW' },
  { code: 'SA-HOUSING-A', level: 1, name: 'Pump Housing Assembly' },
  { code: 'SA-IMPELLER', level: 2, name: 'Impeller Sub-assembly' },
  { code: 'RM-STEEL-SHT', level: 3, name: 'Stainless Steel Sheet' },
  { code: 'RM-BEARING-62', level: 3, name: 'Ball Bearing 6204' },
  { code: 'RM-SEAL-KIT', level: 3, name: 'Mechanical Seal Kit' },
  { code: 'RM-COPPER-WIRE', level: 3, name: 'Copper Winding Wire' },
  { code: 'RM-SHAFT-SS', level: 2, name: 'SS Drive Shaft' },
];
const WAREHOUSES = ['WH-MAIN', 'WH-PROD', 'WH-DXB'];
const WORKCENTERS = ['WC-MACH', 'WC-ASSY', 'WC-WIND', 'WC-TEST'];

const TODAY = new Date('2026-10-09T06:30:00Z');

const VENDOR_BY_ITEM: Record<string, [string, PlannedOrder['vendorSource']]> = {
  'RM-STEEL-SHT': ['V-STEELCO', 'b1-preferred'],
  'RM-BEARING-62': ['V-SKFGULF', 'settings-override'],
  'RM-COPPER-WIRE': ['V-COPPERME', 'default'],
};
function mockVendor(code: string, isProd: boolean): Pick<PlannedOrder, 'vendorCode' | 'vendorSource'> {
  if (isProd) return { vendorCode: null, vendorSource: null };
  const v = VENDOR_BY_ITEM[code];
  return v ? { vendorCode: v[0], vendorSource: v[1] } : { vendorCode: null, vendorSource: null };
}

function makePlannedOrders(runId: string): PlannedOrder[] {
  const orders: PlannedOrder[] = [];
  const count = 42;
  for (let i = 0; i < count; i++) {
    const item = pick(ITEMS);
    const isProd = item.level < 3;
    const due = addDays(TODAY, Math.floor(rand() * 60) - 5);
    const lead = isProd ? 5 + Math.floor(rand() * 8) : 10 + Math.floor(rand() * 20);
    const release = addDays(due, -lead);
    orders.push({
      id: `${runId}-PO-${(i + 1).toString().padStart(3, '0')}`,
      itemCode: item.code,
      warehouseCode: pick(WAREHOUSES),
      type: isProd ? 'ProductionOrder' : 'PurchaseOrder',
      quantity: round((isProd ? 10 : 50) * (0.5 + rand() * 4)),
      dueDate: isoDate(due),
      releaseDate: isoDate(release),
      lowLevelCode: item.level,
      firmed: rand() > 0.82,
      b1DocEntry: rand() > 0.85 ? 13000 + i : null,
      ...mockVendor(item.code, isProd),
    });
  }
  return orders.sort((a, b) => a.lowLevelCode - b.lowLevelCode || a.releaseDate.localeCompare(b.releaseDate));
}

function makePegging(orders: PlannedOrder[]): PeggingLink[] {
  const links: PeggingLink[] = [];
  const demandSources = ['SalesOrder', 'Forecast', 'DependentDemand', 'SafetyStock'];
  for (const o of orders) {
    const n = 1 + Math.floor(rand() * 3);
    for (let i = 0; i < n; i++) {
      const src = o.lowLevelCode === 0 ? pick(['SalesOrder', 'Forecast']) : pick(demandSources);
      links.push({
        plannedOrderId: o.id,
        demandSource: src,
        quantity: round(o.quantity / n),
        demandDueDate: o.dueDate,
        demandDocKey:
          src === 'SalesOrder'
            ? `SO ${17000 + Math.floor(rand() * 400)}`
            : src === 'DependentDemand'
            ? `${pick(ITEMS.filter((x) => x.level < o.lowLevelCode) ?? ITEMS).code}`
            : src === 'Forecast'
            ? `FC ${o.itemCode}`
            : 'SS policy',
        parentItemCode:
          src === 'DependentDemand'
            ? pick(ITEMS.filter((x) => x.level < o.lowLevelCode) ?? ITEMS).code
            : null,
      });
    }
  }
  return links;
}

function makeExceptions(orders: PlannedOrder[]): ExceptionMessage[] {
  const out: ExceptionMessage[] = [];
  const templates: Record<string, (o: PlannedOrder) => string> = {
    Expedite: (o) => `Move in planned order to cover demand ${o.dueDate}`,
    Defer: (o) => `Planned order earlier than needed; defer to ${o.dueDate}`,
    Cancel: () => `Demand removed; planned supply no longer required`,
    PastDue: (o) => `Due date ${o.dueDate} is in the past`,
    ReleaseInPast: (o) => `Release date ${o.releaseDate} is before today`,
    CapacityOverload: () => `Work center overloaded in planning bucket`,
  };
  const types = Object.keys(templates) as (keyof typeof templates)[];
  let idx = 0;
  for (const o of orders) {
    if (rand() > 0.55) continue;
    const t = o.releaseDate < isoDate(TODAY) ? 'ReleaseInPast' : pick(types as string[]);
    idx++;
    const ex: ExceptionMessage = {
      id: `EXC-${idx.toString().padStart(3, '0')}`,
      type: t as ExceptionMessage['type'],
      itemCode: o.itemCode,
      warehouseCode: o.warehouseCode,
      message: templates[t](o),
      relatedDate: t === 'ReleaseInPast' ? o.releaseDate : o.dueDate,
      sourceDocKey: o.b1DocEntry ? `OWOR ${o.b1DocEntry}` : null,
      quantity: o.quantity,
      plannedOrderId: o.id,
      sourceLine: null, currentDate: null, proposedDate: null,
      actions: [], actionStatus: null, actionBy: null, actionAt: null, actionNote: null,
    };
    // Execute actions (deterministic, no rand() so the sample data stays stable).
    const today = isoDate(TODAY);
    const isProd = o.type === 'ProductionOrder';
    if (t === 'ReleaseInPast' || t === 'Expedite' || t === 'Defer') {
      if (t === 'ReleaseInPast') {
        if (!o.firmed) ex.actions = ['ReleaseOrder'];
      } else {
        ex.sourceDocKey = isProd ? `OWOR:${3300 + idx}` : `OPOR:${4500 + idx}`;
        ex.sourceLine = isProd ? null : idx % 3;
        if (t === 'Expedite') {
          ex.proposedDate = o.dueDate < today ? today : o.dueDate;
          ex.currentDate = isoDate(addDays(new Date(ex.proposedDate), 4 + (idx % 5)));
          ex.actions = ['MoveDueDate'];
        } else {
          ex.currentDate = o.dueDate;
          ex.proposedDate = isoDate(addDays(new Date(o.dueDate < today ? today : o.dueDate), 6));
          ex.actions = ['MoveDueDate', 'Close'];
        }
      }
    }
    out.push(ex);
  }
  return out;
}

function makeCapacity(): CapacityLoad[] {
  const out: CapacityLoad[] = [];
  for (let b = 0; b < 8; b++) {
    const bucketStart = isoDate(addDays(TODAY, b * 7));
    for (const wc of WORKCENTERS) {
      const avail = wc === 'WC-TEST' ? 120 : 160;
      const required = round(avail * (0.55 + rand() * 0.9), 1);
      out.push({
        workCenterCode: wc,
        bucketStart,
        requiredHours: required,
        availableHours: avail,
        overloaded: required > avail,
      });
    }
  }
  return out;
}

function buildRunDetail(run: MrpRun): MrpRunDetail {
  const plannedOrders = makePlannedOrders(run.id);
  const pegging = makePegging(plannedOrders);
  const exceptions = makeExceptions(plannedOrders);
  const capacityLoads = makeCapacity();
  return {
    ...run,
    plannedOrderCount: plannedOrders.length,
    exceptionCount: exceptions.length,
    plannedOrders,
    pegging,
    exceptions,
    capacityLoads,
  };
}

// ---------- seeded runs ----------
export const runs: MrpRun[] = [
  {
    id: 'RUN-2026-1009-A',
    runAt: '2026-10-09T05:12:00Z',
    runBy: 'tony.matar@seidor.com',
    horizonStart: '2026-10-09',
    horizonEnd: '2026-12-08',
    bucketDays: 7,
    notes: 'Nightly regenerative run — all warehouses',
    plannedOrderCount: 42,
    exceptionCount: 0,
  },
  {
    id: 'RUN-2026-1008-A',
    runAt: '2026-10-08T05:10:00Z',
    runBy: 'tony.matar@seidor.com',
    horizonStart: '2026-10-08',
    horizonEnd: '2026-12-07',
    bucketDays: 7,
    notes: 'Nightly regenerative run',
    plannedOrderCount: 39,
    exceptionCount: 0,
  },
  {
    id: 'RUN-2026-1007-B',
    runAt: '2026-10-07T14:40:00Z',
    runBy: 'planner1@cobalt.local',
    horizonStart: '2026-10-07',
    horizonEnd: '2026-11-06',
    bucketDays: 1,
    notes: 'Ad-hoc net-change run, WH-PROD only',
    plannedOrderCount: 17,
    exceptionCount: 0,
  },
];

const detailCache = new Map<string, MrpRunDetail>();
export function getRunDetail(id: string): MrpRunDetail | undefined {
  const run = runs.find((r) => r.id === id);
  if (!run) return undefined;
  if (!detailCache.has(id)) {
    const detail = buildRunDetail(run);
    run.plannedOrderCount = detail.plannedOrderCount;
    run.exceptionCount = detail.exceptionCount;
    detail.plannedOrders = detail.plannedOrders.map(decorateOrder);
    detail.criteria = null;
    detailCache.set(id, detail);
  }
  return detailCache.get(id);
}

/** Mock of the planning level: company collapses warehouses, warehouse+transfers moves some buying into transfers. */
function applyPlanningLevel(detail: MrpRunDetail, crit: RunCriteria) {
  const planWh = crit.scope.warehouses.length ? crit.scope.warehouses : WAREHOUSES;
  const lvl = crit.level ?? 'Warehouse';
  let orders = detail.plannedOrders;
  if (lvl === 'Company') {
    // One warehouse per item: pool every row of an item+type into the default (first included) warehouse.
    const home = planWh[0];
    const merged = new Map<string, PlannedOrder>();
    for (const o of orders) {
      const k = `${o.itemCode}|${o.type}`;
      const m = merged.get(k);
      if (!m) { merged.set(k, { ...o, warehouseCode: home }); continue; }
      m.quantity += o.quantity;
      if (o.releaseDate < m.releaseDate) m.releaseDate = o.releaseDate;
      if (o.dueDate < m.dueDate) m.dueDate = o.dueDate;
    }
    const gone = new Set(orders.map((o) => o.id));
    for (const m of merged.values()) gone.delete(m.id);
    detail.pegging = detail.pegging.filter((p) => !gone.has(p.plannedOrderId));
    orders = [...merged.values()];
  } else if (crit.transfers?.enabled && planWh.length >= 2) {
    const sources = crit.transfers.sourceWarehouses.length ? crit.transfers.sourceWarehouses : planWh;
    const lead = Number.isFinite(crit.transfers.leadTimeDays) ? crit.transfers.leadTimeDays : 1;
    const extra: PlannedOrder[] = [];
    const candidates = orders.filter((o) => o.type === 'PurchaseOrder' && planWh.includes(o.warehouseCode) && sources.some((s) => s !== o.warehouseCode));
    candidates.slice(0, 4).forEach((o, i) => {
      const from = sources.filter((s) => s !== o.warehouseCode)[i % Math.max(1, sources.filter((s) => s !== o.warehouseCode).length)];
      const qty = Math.max(1, Math.round(o.quantity * 0.4));
      o.quantity = Math.max(0, o.quantity - qty);
      const due = new Date(o.dueDate);
      const id = `${o.id}-TR`;
      extra.push({
        id, itemCode: o.itemCode, warehouseCode: o.warehouseCode, fromWarehouse: from, type: 'TransferRequest', quantity: qty,
        dueDate: o.dueDate, releaseDate: isoDate(addDays(due, -lead)), lowLevelCode: o.lowLevelCode, firmed: false, b1DocEntry: null,
        vendorCode: null, vendorSource: null, leadTimeDays: lead, leadTimeSource: null,
      });
      const peg = detail.pegging.find((p) => p.plannedOrderId === o.id);
      if (peg) detail.pegging.push({ ...peg, plannedOrderId: id, quantity: qty });
    });
    orders = [...orders.filter((o) => o.quantity > 0), ...extra];
  }
  if (crit.purchasing?.document === 'Request') {
    orders = orders.map((o) => (o.type === 'PurchaseOrder' ? { ...o, type: 'PurchaseRequest' as const } : o));
  }
  detail.plannedOrders = orders.sort((a, b) => a.lowLevelCode - b.lowLevelCode || a.releaseDate.localeCompare(b.releaseDate));
}

export function createRun(opts: {
  criteria?: RunCriteria;
  horizonDays?: number;
  bucketDays?: number;
  source?: string;
  notes?: string;
}): MrpRun {
  const now = new Date();
  const crit = opts.criteria;
  if (crit) { opts = { ...opts, horizonDays: crit.horizon.days, bucketDays: crit.horizon.bucketDays, source: crit.source }; }
  const horizonDays = opts.horizonDays ?? 60;
  const id = `RUN-${now.toISOString().slice(0, 10).replace(/-/g, '')}-${Math.floor(rand() * 900 + 100)}`;
  const run: MrpRun = {
    id,
    runAt: now.toISOString(),
    runBy: 'tony.matar@seidor.com',
    horizonStart: isoDate(now),
    horizonEnd: isoDate(addDays(now, horizonDays)),
    bucketDays: opts.bucketDays ?? 7,
    notes: opts.notes || `Manual run (${opts.source ?? 'mock'})`,
    plannedOrderCount: 0,
    exceptionCount: 0,
  };
  const detail = buildRunDetail(run);
  detail.plannedOrders = detail.plannedOrders.map(decorateOrder);
  if (crit) {
    const wh = crit.scope.warehouses;
    if (wh.length) detail.plannedOrders = detail.plannedOrders.filter((o) => wh.includes(o.warehouseCode));
    if (crit.scope.procurement !== 'Both') detail.plannedOrders = detail.plannedOrders.filter((o) => (o.type === 'PurchaseOrder') === (crit.scope.procurement === 'Buy'));
    if (crit.scope.itemCodes.length) detail.plannedOrders = detail.plannedOrders.filter((o) => crit.scope.itemCodes.includes(o.itemCode));
    applyPlanningLevel(detail, crit);
    const ids = new Set(detail.plannedOrders.map((o) => o.id));
    detail.pegging = detail.pegging.filter((p) => ids.has(p.plannedOrderId));
    detail.plannedOrderCount = detail.plannedOrders.length;
  }
  detail.criteria = crit ?? null;
  run.plannedOrderCount = detail.plannedOrderCount;
  run.exceptionCount = detail.exceptionCount;
  detailCache.set(id, detail);
  runs.unshift(run);
  return run;
}

export function releaseOrders(runId: string, ids: string[]) {
  const detail = getRunDetail(runId);
  if (!detail) return { released: 0, plannedOrders: [] };
  let docEntry = 14000;
  const released: PlannedOrder[] = [];
  const grouping = detail.criteria?.purchasing?.grouping ?? 'Supplier';
  const byKey = new Map<string, number>();
  const docs = new Set<number>();
  for (const po of detail.plannedOrders) {
    if (ids.includes(po.id)) {
      po.firmed = true;
      if (!po.b1DocEntry) {
        // Same rule as the server: orders with the same key share one document.
        const key = documentKey(po, grouping);
        if (key && byKey.has(key)) po.b1DocEntry = byKey.get(key);
        else { po.b1DocEntry = docEntry++; if (key) byKey.set(key, po.b1DocEntry); }
      }
      docs.add(po.b1DocEntry as number);
      released.push(po);
    }
  }
  return { released: released.length, documentsCreated: docs.size, plannedOrders: released };
}

export const forecasts: Forecast[] = (() => {
  const out: Forecast[] = [];
  let i = 0;
  for (const item of ITEMS.filter((x) => x.level === 0)) {
    for (let p = 0; p < 4; p++) {
      const qty = round(80 + rand() * 120);
      out.push({
        id: `FC-${(++i).toString().padStart(3, '0')}`,
        itemCode: item.code,
        warehouseCode: 'WH-MAIN',
        periodStart: isoDate(addDays(TODAY, p * 30)),
        quantity: qty,
        consumed: round(qty * rand() * 0.8),
      });
    }
  }
  return out;
})();

export const META = { ITEMS, WAREHOUSES, WORKCENTERS };

// ---------- calendar ----------
const MOCK_HOLIDAYS: Record<string, string> = { '2026-12-02': 'National Day', '2026-12-03': 'National Day holiday' };
const NOUN: Record<PlannedOrder['type'], string> = { PurchaseOrder: 'purchase order', PurchaseRequest: 'purchase request', ProductionOrder: 'production order', TransferRequest: 'transfer request' };
const calCache = new Map<string, CalendarEvent[]>();

function buildCalendarEvents(runId: string): CalendarEvent[] {
  const d = getRunDetail(runId)!;
  const ev: CalendarEvent[] = [];
  let n = 0;
  const push = (e: Omit<CalendarEvent, 'id'>) => ev.push({ id: `${runId}-cal-${++n}`, ...e });
  for (const o of d.plannedOrders) {
    const isProd = o.type === 'ProductionOrder';
    const noun = NOUN[o.type];
    push({ date: o.releaseDate, kind: 'PlannedRelease', itemCode: o.itemCode, warehouseCode: o.warehouseCode, quantity: o.quantity,
      label: `Release ${noun}`, ref: o.id, severity: o.releaseDate < isoDate(TODAY) ? 'danger' : 'info' });
    push({ date: o.dueDate, kind: 'PlannedDue', itemCode: o.itemCode, warehouseCode: o.warehouseCode, quantity: o.quantity,
      label: isProd ? 'Production order due' : o.type === 'TransferRequest' ? 'Transfer arrives' : o.type === 'PurchaseRequest' ? 'Purchase request needed' : 'Purchase receipt due', ref: o.id, severity: 'info' });
  }
  for (const p of d.pegging) {
    if (p.demandSource !== 'SalesOrder') continue;
    const o = d.plannedOrders.find((x) => x.id === p.plannedOrderId);
    push({ date: p.demandDueDate, kind: 'Demand', itemCode: o?.itemCode ?? '', warehouseCode: o?.warehouseCode ?? '', quantity: p.quantity,
      label: `Sales order line due`, ref: p.demandDocKey.replace(' ', ':'), severity: 'info' });
  }
  for (const f of forecasts) {
    push({ date: f.periodStart, kind: 'Forecast', itemCode: f.itemCode, warehouseCode: f.warehouseCode, quantity: Math.max(0, f.quantity - f.consumed),
      label: 'Forecast period starts (net of consumption)', ref: 'FCST', severity: 'info' });
  }
  // open supply documents, deterministic from the order list
  d.plannedOrders.forEach((o, i) => {
    if (i % 3 !== 0 || o.type === 'TransferRequest') return;
    push({ date: isoDate(addDays(new Date(o.dueDate), (i % 5) - 2)), kind: 'Supply', itemCode: o.itemCode, warehouseCode: o.warehouseCode,
      quantity: Math.round(o.quantity / 2), label: o.type === 'ProductionOrder' ? 'Open production order due' : 'Open purchase order due',
      ref: o.type === 'ProductionOrder' ? `OWOR:${14500 + i}` : `OPOR:${9100 + i}`, severity: 'info' });
  });
  for (const x of d.exceptions) {
    push({ date: x.relatedDate, kind: 'Exception', itemCode: x.itemCode, warehouseCode: x.warehouseCode, quantity: x.quantity, label: x.message,
      ref: x.type, severity: x.type === 'PastDue' || x.type === 'ReleaseInPast' || x.type === 'Cancel' ? 'danger' : 'warn' });
  }
  for (const c of d.capacityLoads) {
    if (!c.overloaded) continue;
    push({ date: c.bucketStart, kind: 'CapacityOverload', itemCode: '', warehouseCode: '', quantity: 0,
      label: `${c.workCenterCode} needs ${c.requiredHours} h of ${c.availableHours} h`, ref: c.workCenterCode, severity: 'danger' });
  }
  return ev;
}

export function getCalendar(runId: string, q: CalendarQuery): CalendarResponse {
  const run = runs.find((r) => r.id === runId);
  if (!run) throw new Error(`Run ${runId} not found`);
  if (!calCache.has(runId)) calCache.set(runId, buildCalendarEvents(runId));
  const kinds = q.kinds?.length ? new Set<CalendarKind>(q.kinds) : null;
  const item = q.item?.toLowerCase();
  let events = calCache.get(runId)!.filter((e) =>
    e.date >= q.from && e.date <= q.to && (!kinds || kinds.has(e.kind)) &&
    (!item || e.itemCode.toLowerCase().includes(item)) && (!q.warehouse || e.warehouseCode === q.warehouse));
  const truncated = events.length > 5000;
  if (truncated) events = events.slice(0, 5000);
  const nonWorking: { date: string; reason: string }[] = [];
  for (let t = new Date(q.from + 'T00:00:00Z'); isoDate(t) <= q.to; t = new Date(t.getTime() + 86400000)) {
    const k = isoDate(t);
    const dow = t.getUTCDay();
    if (MOCK_HOLIDAYS[k]) nonWorking.push({ date: k, reason: MOCK_HOLIDAYS[k] });
    else if (dow === 0 || dow === 6) nonWorking.push({ date: k, reason: 'Weekend' });
  }
  return { from: q.from, to: q.to, horizonStart: run.horizonStart, horizonEnd: run.horizonEnd, truncated, nonWorking, events };
}
