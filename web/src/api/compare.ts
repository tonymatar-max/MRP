import type { CapacityLoad, ExceptionMessage, MrpRunDetail, PlannedOrder } from './types';

export interface OrderChange { before: PlannedOrder; after: PlannedOrder; fields: string[] }
export interface ExceptionChange { before: ExceptionMessage; after: ExceptionMessage; fields: string[] }
export interface LoadChange { workCenterCode: string; bucketStart: string; before?: CapacityLoad; after?: CapacityLoad }

export interface RunDiff {
  orders: { added: PlannedOrder[]; removed: PlannedOrder[]; changed: OrderChange[]; same: number };
  exceptions: { added: ExceptionMessage[]; removed: ExceptionMessage[]; changed: ExceptionChange[]; same: number };
  /** Overloaded buckets that appeared, went away, or are still overloaded by a different amount. */
  capacity: { added: LoadChange[]; removed: LoadChange[]; changed: LoadChange[] };
}

const orderKey = (o: PlannedOrder) => [o.type, o.itemCode, o.warehouseCode, o.fromWarehouse ?? ''].join('|');
// Same identity the alert emails use to call an exception "new".
const exKey = (e: ExceptionMessage) => [e.type, e.itemCode, e.warehouseCode ?? '', e.sourceDocKey ?? '', e.sourceLine ?? ''].join('|');

function group<T>(rows: T[], key: (r: T) => string, order: (a: T, b: T) => number): Map<string, T[]> {
  const m = new Map<string, T[]>();
  for (const r of rows) { const k = key(r); (m.get(k) ?? m.set(k, []).get(k)!).push(r); }
  for (const v of m.values()) v.sort(order);
  return m;
}

/** Pairs the i-th order of each (item, warehouse, type) by due date; the leftovers are added or removed. */
export function diffRuns(a: MrpRunDetail, b: MrpRunDetail): RunDiff {
  const byDue = (x: PlannedOrder, y: PlannedOrder) => x.dueDate.localeCompare(y.dueDate);
  const ga = group(a.plannedOrders, orderKey, byDue), gb = group(b.plannedOrders, orderKey, byDue);
  const orders: RunDiff['orders'] = { added: [], removed: [], changed: [], same: 0 };
  for (const k of new Set([...ga.keys(), ...gb.keys()])) {
    const xs = ga.get(k) ?? [], ys = gb.get(k) ?? [];
    for (let i = 0; i < Math.max(xs.length, ys.length); i++) {
      const x = xs[i], y = ys[i];
      if (!x) orders.added.push(y); else if (!y) orders.removed.push(x);
      else {
        const fields = [x.quantity !== y.quantity && 'quantity', x.dueDate !== y.dueDate && 'due date', (x.vendorCode ?? '') !== (y.vendorCode ?? '') && 'supplier']
          .filter(Boolean) as string[];
        if (fields.length) orders.changed.push({ before: x, after: y, fields }); else orders.same++;
      }
    }
  }

  const ea = new Map(a.exceptions.map((e) => [exKey(e), e])), eb = new Map(b.exceptions.map((e) => [exKey(e), e]));
  const exceptions: RunDiff['exceptions'] = { added: [], removed: [], changed: [], same: 0 };
  for (const [k, e] of eb) {
    const o = ea.get(k);
    if (!o) { exceptions.added.push(e); continue; }
    const fields = [o.proposedDate !== e.proposedDate && 'proposed date', o.quantity !== e.quantity && 'quantity', o.message !== e.message && 'message']
      .filter(Boolean) as string[];
    if (fields.length) exceptions.changed.push({ before: o, after: e, fields }); else exceptions.same++;
  }
  for (const [k, e] of ea) if (!eb.has(k)) exceptions.removed.push(e);

  const la = new Map(a.capacityLoads.filter((c) => c.overloaded).map((c) => [`${c.workCenterCode}|${c.bucketStart}`, c]));
  const lb = new Map(b.capacityLoads.filter((c) => c.overloaded).map((c) => [`${c.workCenterCode}|${c.bucketStart}`, c]));
  const capacity: RunDiff['capacity'] = { added: [], removed: [], changed: [] };
  for (const [k, c] of lb) {
    const o = la.get(k);
    const row = { workCenterCode: c.workCenterCode, bucketStart: c.bucketStart, before: o, after: c };
    if (!o) capacity.added.push(row);
    else if (o.requiredHours !== c.requiredHours || o.availableHours !== c.availableHours) capacity.changed.push(row);
  }
  for (const [k, c] of la) if (!lb.has(k)) capacity.removed.push({ workCenterCode: c.workCenterCode, bucketStart: c.bucketStart, before: c });

  const ord = (e: ExceptionMessage, f: ExceptionMessage) => e.type.localeCompare(f.type) || e.itemCode.localeCompare(f.itemCode);
  exceptions.added.sort(ord); exceptions.removed.sort(ord);
  return { orders, exceptions, capacity };
}
