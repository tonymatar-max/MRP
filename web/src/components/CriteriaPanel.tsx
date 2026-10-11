import type { RunCriteria } from '../api/types';
import { LEVEL_LABEL } from './PriorityControl';
import { fmtDate } from './util';

const yn = (b: boolean) => (b ? 'Yes' : 'No');
const list = (a: string[], all = 'All') => (a.length ? a.join(', ') : all);

function rows(c: RunCriteria): [string, string][] {
  const s = c.scope, d = c.demand;
  return [
    ['Data source', c.source === 'b1-sql' ? 'SAP B1 (SQL)' : 'Mock sample data'],
    ['Planning level', c.level === 'Company' ? 'Company (stock pooled)' : 'Warehouse (each planned separately)'],
    ['Purchasing document', c.purchasing?.document === 'Request' ? 'Purchase request' : 'Purchase order'],
    ['Documents on release', ({ None: 'No grouping', SupplierAndDate: 'Per supplier and due date', Supplier: 'One per supplier' } as Record<string, string>)[c.purchasing?.grouping ?? 'Supplier']],
    ['Warehouses', list(s.warehouses)],
    ...(c.level === 'Warehouse' ? [['Transfers', c.transfers?.enabled ? `On, from ${list(c.transfers.sourceWarehouses, 'every selected warehouse')}, ${c.transfers.leadTimeDays} d` : 'Off'] as [string, string]] : []),
    ['Item groups', list(s.itemGroups)],
    ['Suppliers', list(s.suppliers)],
    ['Item search', s.itemSearch || '—'],
    ['Item codes', s.itemCodes.length ? `${s.itemCodes.length} listed (${s.itemCodes.slice(0, 4).join(', ')}${s.itemCodes.length > 4 ? ', …' : ''})` : 'No restriction'],
    ['Procurement', s.procurement === 'Both' ? 'Buy and make' : s.procurement],
    ['Planning methods', list(s.planningMethods)],
    ['Plan components', yn(s.includeComponents)],
    ['Sales orders', d.salesOrders ? `Yes${d.salesOrderFrom || d.salesOrderTo ? ` (${d.salesOrderFrom ? fmtDate(d.salesOrderFrom) : 'any'} to ${d.salesOrderTo ? fmtDate(d.salesOrderTo) : 'any'})` : ''}` : 'No'],
    ['Forecasts', d.forecasts ? `Yes (consumption ${d.forecastConsumption}, lookback ${d.forecastLookbackDays} d)` : 'No'],
    ['Production components', yn(d.productionComponents)],
    ['Past-due demand', d.pastDue === 'FoldToStart' ? 'Plan as due now' : 'Ignore'],
    ['Safety stock', yn(d.safetyStock)],
    ['Supply', [c.supply.onHand && 'on hand', c.supply.openPurchaseOrders && 'open POs', c.supply.openProductionOrders && 'open production orders'].filter(Boolean).join(', ') || 'None'],
    ['Lead time rule', `${c.leadTime.priority.map((l) => LEVEL_LABEL[l]).join(' > ')}, ${c.leadTime.mode === 'Longest' ? 'longest' : 'first available'}, default ${c.leadTime.defaultDays} d`],
    ['Start / horizon', `${c.horizon.startDate ? fmtDate(c.horizon.startDate) : 'Today'} / ${c.horizon.days} d, ${c.horizon.bucketDays}-day buckets`],
    ['Lot sizing', `min order ${yn(c.lotSizing.applyMinOrder).toLowerCase()}, multiples ${yn(c.lotSizing.applyMultiples).toLowerCase()}`],
    ['Working calendar', yn(c.calendar.useWorkingCalendar)],
    ['Capacity planning', yn(c.capacity.enabled)],
  ];
}

export function CriteriaPanel({ criteria, onRerun }: { criteria: RunCriteria | null | undefined; onRerun: (c: RunCriteria) => void }) {
  return (
    <details className="criteria">
      <summary>Criteria</summary>
      {criteria ? (
        <>
          <dl className="criteria-list">
            {rows(criteria).map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v}</dd></div>)}
          </dl>
          <button type="button" className="btn sm" onClick={() => onRerun(criteria)}>Re-run with these criteria</button>
        </>
      ) : <p className="muted" style={{ margin: '8px 0 0' }}>Run before criteria were recorded.</p>}
    </details>
  );
}
