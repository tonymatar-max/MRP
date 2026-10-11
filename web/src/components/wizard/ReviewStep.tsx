import { useEffect, useRef, useState } from 'react';
import { usePreviewRun } from '../../api/hooks';
import type { PreviewMetric, RunCriteria } from '../../api/types';
import { fmtNum } from '../util';
import { PreviewDetailsPanel } from './PreviewDetailsPanel';

type Open = (metric: PreviewMetric, el: HTMLElement) => void;

/** A Review number. A non-zero number with a `metric` opens the details panel; zero stays plain text. */
function Stat({ label, value, tone, metric, what, onOpen }: {
  label: string; value: number; tone?: 'warn'; metric?: PreviewMetric; what?: string; onOpen?: Open;
}) {
  const text = fmtNum(value);
  return (
    <div className={`stat ${tone ?? ''}`}>
      <div className="stat-v">
        {metric && value > 0 && onOpen
          ? <button type="button" className="stat-link" aria-label={`Show the ${text} ${what ?? label.toLowerCase()}`}
              onClick={(e) => onOpen(metric, e.currentTarget)}>{text}</button>
          : text}
      </div>
      <div className="stat-l">{label}</div>
    </div>
  );
}

export function ReviewStep({ c, notes, onNotes }: { c: RunCriteria; notes: string; onNotes: (n: string) => void }) {
  const preview = usePreviewRun();
  const last = useRef('');
  const key = JSON.stringify(c);
  useEffect(() => {
    if (last.current === key) return;
    last.current = key;
    preview.mutate(c);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  const p = preview.data;

  const [metric, setMetric] = useState<PreviewMetric | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const open: Open = (m, el) => { opener.current = el; setMetric(m); };
  const close = () => { setMetric(null); const el = opener.current; setTimeout(() => { if (el?.isConnected) el.focus(); }, 0); };
  // A list must never outlive the criteria its count was made for.
  useEffect(() => { setMetric(null); }, [key]);

  return (
    <div className="form-stack">
      <p className="step-intro">A dry run: counts only, nothing is created until you press Run MRP. Click a number to see the records behind it.</p>
      {preview.isPending && <div className="center-state" style={{ padding: 24 }}><div className="spinner" />Counting what this run would plan…</div>}
      {preview.isError && (
        <div className="alert danger" role="alert">
          {(preview.error as Error).message}{' '}
          <button type="button" className="btn sm" onClick={() => preview.mutate(c)}>Try again</button>
        </div>
      )}
      {p && (
        <>
          {p.warnings.length > 0 && (
            <div className="alert warn" role="alert">
              <strong>{p.warnings.length === 1 ? 'Warning' : `${p.warnings.length} warnings`}</strong>
              <ul className="warn-list">{p.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
            </div>
          )}
          <section className="card"><div className="card-h"><h3>Planning</h3></div>
            <dl className="criteria-list card-b" aria-label="Planning criteria">
              <div><dt>Level</dt><dd>{(p.planning?.level ?? c.level) === 'Company' ? 'Company (stock pooled)' : 'Warehouse (each planned separately)'}</dd></div>
              <div><dt>Purchasing document</dt><dd>{(p.planning?.purchaseDocument ?? c.purchasing.document) === 'Request' ? 'Purchase request' : 'Purchase order'}</dd></div>
              <div><dt>Warehouses</dt><dd>{(p.planning?.warehouses ?? c.scope.warehouses).join(', ') || 'All'}</dd></div>
              {(p.planning?.level ?? c.level) === 'Warehouse' && (
                <div><dt>Transfers</dt><dd>{(p.planning?.transfersEnabled ?? c.transfers.enabled)
                  ? `On, from ${(p.planning?.transferSources ?? c.transfers.sourceWarehouses).join(', ') || 'every selected warehouse'}, ${p.planning?.transferLeadTimeDays ?? c.transfers.leadTimeDays} d`
                  : 'Off'}</dd></div>
              )}
            </dl>
          </section>
          <div className="review-grid">
            <section className="card"><div className="card-h"><h3>Items</h3></div><div className="card-b stats">
              <Stat label="In scope" value={p.items.inScope} metric="items.inScope" what="items in scope" onOpen={open} />
              <Stat label="of total" value={p.items.total} metric="items.total" what="items in total" onOpen={open} />
              <Stat label="Buy" value={p.items.buy} metric="items.buy" what="purchased items" onOpen={open} />
              <Stat label="Make" value={p.items.make} metric="items.make" what="manufactured items" onOpen={open} />
              <Stat label="No supplier" value={p.items.noSupplier} metric="items.noSupplier" what="items with no supplier" onOpen={open} tone={p.items.noSupplier ? 'warn' : undefined} />
              <Stat label="No lead time" value={p.items.noLeadTime} metric="items.noLeadTime" what="items with no lead time" onOpen={open} tone={p.items.noLeadTime ? 'warn' : undefined} />
            </div></section>
            <section className="card"><div className="card-h"><h3>Demand</h3></div><div className="card-b stats">
              <Stat label="Sales order lines" value={p.demand.salesOrderLines} metric="demand.salesOrders" what="sales order lines" onOpen={open} />
              <Stat label="Quantity" value={p.demand.salesOrderQuantity} />
              <Stat label="Past due lines" value={p.demand.pastDueLines} metric="demand.pastDue" what="overdue demand lines" onOpen={open} tone={p.demand.pastDueLines ? 'warn' : undefined} />
              <Stat label="Forecast lines" value={p.demand.forecastLines} metric="demand.forecasts" what="forecast lines" onOpen={open} />
              <Stat label="Production components" value={p.demand.productionComponentLines} metric="demand.productionComponents" what="production component lines" onOpen={open} />
            </div></section>
            <section className="card"><div className="card-h"><h3>Supply</h3></div><div className="card-b stats">
              <Stat label="Purchase order lines" value={p.supply.purchaseOrderLines} metric="supply.purchaseOrders" what="purchase order lines" onOpen={open} />
              <Stat label="Production order lines" value={p.supply.productionOrderLines} metric="supply.productionOrders" what="production order lines" onOpen={open} />
              <Stat label="Stock rows" value={p.supply.stockRows} metric="supply.stock" what="stock rows" onOpen={open} />
            </div></section>
            <section className="card"><div className="card-h"><h3>Lead times (purchased items)</h3></div><div className="card-b stats">
              <Stat label="From supplier" value={p.leadTime.fromSupplier} metric="leadTime.supplier" what="items with a supplier lead time" onOpen={open} />
              <Stat label="From item" value={p.leadTime.fromItem} metric="leadTime.item" what="items with an item lead time" onOpen={open} />
              <Stat label="From item group" value={p.leadTime.fromItemGroup} metric="leadTime.itemGroup" what="items with an item group lead time" onOpen={open} />
              <Stat label="Default" value={p.leadTime.defaulted} metric="leadTime.defaulted" what="items on the default lead time" onOpen={open} />
            </div></section>
          </div>
          {p.warnings.length === 0 && <div className="alert ok">No warnings.</div>}
        </>
      )}
      <div className="field">
        <label htmlFor="wz-notes">Notes <span className="hint">saved with the run</span></label>
        <textarea id="wz-notes" rows={2} value={notes} maxLength={500} onChange={(e) => onNotes(e.target.value)} placeholder="e.g. Quarter-end regenerative run, all warehouses" />
      </div>
      {metric && <PreviewDetailsPanel metric={metric} criteria={c} onClose={close} />}
    </div>
  );
}
