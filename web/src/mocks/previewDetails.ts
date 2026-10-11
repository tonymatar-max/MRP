// Mock of POST /runs/preview/details (+ /export). See docs/preview-details-contract.md.
// Row counts come from the mock preview() so `total` always equals the number that was clicked.
import type {
  DetailsColumn, DetailsRow, MasterItemRow, PreviewDetails, PreviewDetailsRequest, PreviewMetric, RunCriteria,
} from '../api/types';
import { META } from './data';
import { itemSets, preview } from './masterdata';
import * as adm from './admin';

const TODAY = '2026-10-09';
const fmt = (n: number) => n.toLocaleString('en-US', { maximumFractionDigits: 1 });
const addDays = (iso: string, d: number) => {
  const t = new Date(`${iso}T00:00:00Z`); t.setUTCDate(t.getUTCDate() + d); return t.toISOString().slice(0, 10);
};
const hash = (i: number, salt: number) => { let h = (i + 1) * 2654435761 + salt * 40503; h ^= h >>> 15; return Math.abs(h) % 100000; };

type Cols = DetailsColumn[];
const col = (key: string, label: string, type: DetailsColumn['type'] = 'text'): DetailsColumn => ({ key, label, type });

const ITEM_COLS: Cols = [
  col('itemCode', 'Item'), col('itemName', 'Name'), col('group', 'Group'), col('type', 'Type', 'badge'),
  col('planningMethod', 'Planning', 'badge'), col('supplier', 'Supplier'), col('supplierSource', 'Supplier source', 'badge'),
  col('leadTimeDays', 'Lead time (days)', 'number'), col('leadTimeSource', 'Lead time source', 'badge'),
];
const DOC_COLS: Cols = [
  col('document', 'Document'), col('itemCode', 'Item'), col('itemName', 'Name'), col('warehouse', 'Warehouse'),
  col('quantity', 'Quantity', 'number'), col('dueDate', 'Due', 'date'), col('overdue', 'Overdue', 'flag'),
];
const STOCK_COLS: Cols = [
  col('itemCode', 'Item'), col('itemName', 'Name'), col('warehouse', 'Warehouse'),
  col('onHand', 'On hand', 'number'), col('committed', 'Committed', 'number'), col('onOrder', 'On order', 'number'),
];

interface Def { title: string; description: string; columns: Cols; rows: DetailsRow[]; summary: { label: string; value: string }[] }

const METHOD_LABEL: Record<string, string> = { Mrp: 'MRP', ReorderPoint: 'Reorder point', MinMax: 'Min/Max', None: 'None' };

function itemRows(c: RunCriteria, list: MasterItemRow[]): DetailsRow[] {
  const s = itemSets(c);
  return list.map((r) => {
    const lt = s.leadOf(r);
    return {
      itemCode: r.itemCode, itemName: r.itemName, group: r.groupName, type: r.procurement === 'Buy' ? 'Buy' : 'Make',
      planningMethod: METHOD_LABEL[r.effective.planningMethod] ?? r.effective.planningMethod,
      supplier: r.effective.supplier ? `${r.effective.supplier} ${r.effective.supplierName ?? ''}`.trim() : null,
      supplierSource: r.effective.supplier ? (r.override?.supplier ? 'Master data' : 'SAP B1') : null,
      leadTimeDays: lt.days, leadTimeSource: lt.source === 'None' ? 'None' : lt.source,
    };
  });
}
function itemSummary(rows: DetailsRow[]) {
  const cnt = (f: (r: DetailsRow) => boolean) => rows.filter(f).length;
  return [
    { label: 'Items', value: fmt(rows.length) },
    { label: 'Buy', value: fmt(cnt((r) => r.type === 'Buy')) },
    { label: 'Make', value: fmt(cnt((r) => r.type === 'Make')) },
    { label: 'No supplier', value: fmt(cnt((r) => r.type === 'Buy' && !r.supplier)) },
    { label: 'No lead time', value: fmt(cnt((r) => r.type === 'Buy' && r.leadTimeSource === 'None')) },
  ];
}

/** n rows spread over the in-scope items; quantity splits `totalQty` exactly. */
function docRows(c: RunCriteria, n: number, prefix: string, totalQty: number | null, due: (i: number) => string, overdue: (i: number, due: string) => boolean): DetailsRow[] {
  const items = itemSets(c).scope;
  const whs = c.scope.warehouses.length ? c.scope.warehouses : META.WAREHOUSES;
  if (!n || !items.length) return [];
  const out: DetailsRow[] = [];
  for (let i = 0; i < n; i++) {
    const it = items[i % items.length];
    const d = due(i);
    const qty = totalQty == null ? 5 + (hash(i, 1) % 20) * 5 : Math.floor(totalQty / n) + (i < totalQty % n ? 1 : 0);
    out.push({
      document: `${prefix}:${1000 + i * 3 + (hash(i, 2) % 3)}`, itemCode: it.itemCode, itemName: it.itemName,
      warehouse: whs[hash(i, 3) % whs.length], quantity: qty, dueDate: d, overdue: overdue(i, d),
    });
  }
  return out;
}
function docSummary(rows: DetailsRow[], extra: { label: string; value: string }[] = []) {
  const qty = rows.reduce((a, r) => a + Number(r.quantity), 0);
  const dates = rows.map((r) => String(r.dueDate)).sort();
  return [
    { label: 'Lines', value: fmt(rows.length) }, { label: 'Quantity', value: fmt(qty) },
    ...(dates.length ? [{ label: 'Earliest due', value: dates[0] }, { label: 'Latest due', value: dates[dates.length - 1] }] : []),
    { label: 'Items', value: fmt(new Set(rows.map((r) => r.itemCode)).size) },
    ...extra,
  ];
}

function define(c: RunCriteria, m: PreviewMetric): Def {
  const p = preview(c);
  const s = itemSets(c);
  const items = (title: string, description: string, list: MasterItemRow[]): Def => {
    const rows = itemRows(c, list);
    return { title, description, columns: ITEM_COLS, rows, summary: itemSummary(rows) };
  };
  const future = (i: number) => addDays(TODAY, 1 + (hash(i, 4) % 120));
  const overdueCount = (rows: DetailsRow[]) => ({ label: 'Overdue', value: fmt(rows.filter((r) => r.overdue).length) });
  switch (m) {
    case 'items.total': return items('All items', 'Every item the data source offers, before the scope is applied.', s.all);
    case 'items.inScope': return items('Items in scope', 'The items this run will plan.', s.scope);
    case 'items.buy': return items('Purchased items in scope', 'In-scope items that are bought from a supplier.', s.buy);
    case 'items.make': return items('Manufactured items in scope', 'In-scope items that are produced in-house.', s.make);
    case 'items.noSupplier': return items('Purchased items with no supplier', 'Their planned orders cannot be released to SAP B1 until a supplier is set.', s.noSupplier);
    case 'items.noLeadTime': return items('Purchased items with no lead time', 'No lead time from the supplier, item or item group, and no default: orders are planned to release on their due date.', s.noLeadTime);
    case 'leadTime.supplier': return items('Lead time from the supplier', 'Purchased items whose lead time comes from the supplier.', s.lt.supplier);
    case 'leadTime.item': return items('Lead time from the item', 'Purchased items whose lead time comes from the item itself.', s.lt.item);
    case 'leadTime.itemGroup': return items('Lead time from the item group', 'Purchased items whose lead time comes from their item group.', s.lt.itemGroup);
    case 'leadTime.defaulted': return items('Lead time defaulted', 'Purchased items that fall back to the default lead time for this run.', s.lt.defaulted);
    case 'demand.salesOrders': {
      const n = p.demand.salesOrderLines, od = Math.min(p.demand.pastDueLines, n);
      const rows = docRows(c, n, 'ORDR', p.demand.salesOrderQuantity, (i) => (i < od ? addDays(TODAY, -(5 + hash(i, 5) % 400)) : future(i)), (_i, d) => d < TODAY);
      return { title: 'Sales order lines', description: 'Open sales order lines that drive demand in this run.', columns: DOC_COLS, rows, summary: docSummary(rows, [overdueCount(rows)]) };
    }
    case 'demand.pastDue': {
      const rows = docRows(c, p.demand.pastDueLines, 'ORDR', null, (i) => addDays(TODAY, -(5 + hash(i, 5) % 900)), () => true);
      return { title: 'Overdue demand lines', description: 'Demand that was already due before the plan starts; it is planned as due on day one.', columns: DOC_COLS, rows, summary: docSummary(rows) };
    }
    case 'demand.forecasts': {
      const rows = docRows(c, p.demand.forecastLines, 'FC', null, future, () => false);
      return { title: 'Forecast lines', description: 'Forecast lines that add demand to the plan.', columns: DOC_COLS, rows, summary: docSummary(rows) };
    }
    case 'demand.productionComponents': {
      const rows = docRows(c, p.demand.productionComponentLines, 'OWOR', null, future, () => false);
      return { title: 'Production components', description: 'Components still to be issued to open production orders.', columns: DOC_COLS, rows, summary: docSummary(rows) };
    }
    case 'supply.purchaseOrders': {
      const items2 = s.buy;
      const rows = docRows(c, p.supply.purchaseOrderLines, 'OPOR', null, future, () => false)
        .map((r, i) => (items2.length ? { ...r, itemCode: items2[i % items2.length].itemCode, itemName: items2[i % items2.length].itemName } : r));
      return { title: 'Purchase order lines', description: 'Open purchase order lines counted as incoming supply.', columns: DOC_COLS, rows, summary: docSummary(rows) };
    }
    case 'supply.productionOrders': {
      const items2 = s.make;
      const rows = docRows(c, p.supply.productionOrderLines, 'OWOR', null, future, () => false)
        .map((r, i) => (items2.length ? { ...r, itemCode: items2[i % items2.length].itemCode, itemName: items2[i % items2.length].itemName } : r));
      return { title: 'Production order lines', description: 'Open production orders counted as incoming supply.', columns: DOC_COLS, rows, summary: docSummary(rows) };
    }
    case 'supply.stock': {
      const whs = c.scope.warehouses.length ? c.scope.warehouses : META.WAREHOUSES;
      const rows: DetailsRow[] = [];
      for (let i = 0; i < p.supply.stockRows && s.scope.length; i++) {
        const it = s.scope[i % s.scope.length];
        const onHand = hash(i, 6) % 500;
        rows.push({
          itemCode: it.itemCode, itemName: it.itemName, warehouse: whs[Math.floor(i / s.scope.length) % whs.length],
          onHand, committed: Math.min(onHand, hash(i, 7) % 120), onOrder: hash(i, 8) % 3 === 0 ? 50 + (hash(i, 9) % 10) * 25 : 0,
        });
      }
      const sum = (k: string) => rows.reduce((a, r) => a + Number(r[k]), 0);
      return {
        title: 'Stock rows', description: 'Item by warehouse stock positions used as starting supply.', columns: STOCK_COLS, rows,
        summary: [{ label: 'Rows', value: fmt(rows.length) }, { label: 'On hand', value: fmt(sum('onHand')) }, { label: 'Committed', value: fmt(sum('committed')) },
          { label: 'On order', value: fmt(sum('onOrder')) }, { label: 'Items', value: fmt(new Set(rows.map((r) => r.itemCode)).size) }],
      };
    }
    default: throw new adm.MockHttpError(400, `Unknown metric "${String(m)}".`);
  }
}

const SEARCH_KEYS = (cols: Cols) => cols.filter((x) => x.type === 'text' || x.type === 'badge').map((x) => x.key);

function build(req: PreviewDetailsRequest, take: number): PreviewDetails {
  adm.requireSignedIn();
  const d = define(req.criteria, req.metric);
  const q = (req.search ?? '').trim().toLowerCase();
  const keys = SEARCH_KEYS(d.columns);
  const matchedRows = q ? d.rows.filter((r) => keys.some((k) => String(r[k] ?? '').toLowerCase().includes(q))) : d.rows;
  const rows = matchedRows.slice(0, take);
  return {
    metric: req.metric, title: d.title, description: d.description, total: d.rows.length, matched: matchedRows.length,
    shown: rows.length, truncated: matchedRows.length > rows.length, summary: d.summary, columns: d.columns, rows,
  };
}

export function previewDetails(req: PreviewDetailsRequest): PreviewDetails {
  const take = Math.min(1000, Math.max(1, req.take ?? 200));
  return build(req, take);
}

const csvCell = (v: unknown) => { const s = v == null ? '' : String(v); return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
export function previewDetailsCsv(req: PreviewDetailsRequest): string {
  const d = build(req, Number.MAX_SAFE_INTEGER);
  const lines = d.rows.map((r) => d.columns.map((c) => csvCell(typeof r[c.key] === 'boolean' ? (r[c.key] ? 'Yes' : 'No') : r[c.key])).join(','));
  return '﻿' + [d.columns.map((c) => csvCell(c.label)).join(','), ...lines].join('\r\n');
}
