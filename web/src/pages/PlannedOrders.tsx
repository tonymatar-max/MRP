import { Fragment, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { useReleaseOrders, useRun } from '../api/hooks';
import type { PlannedOrder, ReleaseResult } from '../api/types';
import { Dialog, ErrorState, Llc, Loading, PageHeader, Pill } from '../components/ui';
import { useAuth } from '../components/Auth';
import { RunPicker } from '../components/RunPicker';
import { AskAiButton, ExplainButton } from '../components/AiAssistant';
import { useSelectedRun } from '../components/RunContext';
import { useT } from '../i18n';
import { fmtDate, fmtNum, isPast, ORDER_TYPES, orderTypeLabel, orderTypeTag, sortBy, SortDir } from '../components/util';

function Pegging({ runId, id }: { runId: string; id: string }) {
  const tr = useT();
  const q = useQuery({ queryKey: ['peg', runId, id], queryFn: () => api.getPegging(runId, id) });
  if (q.isLoading) return <span className="muted">{tr('Loading pegging…')}</span>;
  if (!q.data?.length) return <span className="muted">{tr('No pegging.')}</span>;
  return (
    <>
      <div className="section-title">{tr('Pegging — why this order exists')}</div>
      <div className="pegging-grid">
        {q.data.map((p, i) => (
          <div className="peg" key={i}>
            <div className="src">{p.demandSource}</div>
            <div>{p.demandDocKey}</div>
            <div className="muted">
              {tr('{qty} needed {date}', { qty: fmtNum(p.quantity), date: fmtDate(p.demandDueDate) })}
              {p.parentItemCode ? ` · ${tr('parent {item}', { item: p.parentItemCode })}` : ''}
            </div>
          </div>
        ))}
      </div>
    </>
  );
}

const SRC_LABEL = { 'b1-preferred': 'B1', 'settings-override': 'override', 'master-data': 'master data', default: 'default' } as const;
const LT_LABEL = { Supplier: 'Supplier', Item: 'Item', ItemGroup: 'Group', Default: 'Default', None: 'None' } as const;
function LeadTime({ o }: { o: PlannedOrder }) {
  if (o.type === 'TransferRequest') return <>Transfer, {o.leadTimeDays ?? '?'} d</>;
  if (o.leadTimeDays == null || !o.leadTimeSource || o.leadTimeSource === 'None')
    return <span className="muted" title="No lead time found for this item">—</span>;
  return <>{o.leadTimeDays} d <span className={`lt-badge ${o.leadTimeSource}`} title={`Lead time source: ${o.leadTimeSource}`}>{LT_LABEL[o.leadTimeSource]}</span></>;
}
function Vendor({ o }: { o: PlannedOrder }) {
  if (o.type === 'PurchaseRequest' && o.vendorCode)
    return <><span className="mono">{o.vendorCode}</span>{o.vendorSource && <> <span className={`vendor-badge ${o.vendorSource}`} title={`Vendor source: ${o.vendorSource}`}>{SRC_LABEL[o.vendorSource]}</span></>}</>;
  if (o.type !== 'PurchaseOrder') return <span className="muted">—</span>;
  if (!o.vendorSource) return <span className="vendor-badge none" title="No vendor configured: this purchase order will fail to release">No vendor</span>;
  return <><span className="mono">{o.vendorCode}</span> <span className={`vendor-badge ${o.vendorSource}`} title={`Vendor source: ${o.vendorSource}`}>{SRC_LABEL[o.vendorSource]}</span></>;
}

export default function PlannedOrders() {
  const tr = useT();
  const { runId } = useSelectedRun();
  const { isAdmin } = useAuth();
  const run = useRun(runId);
  const release = useReleaseOrders(runId ?? '');
  const [params] = useSearchParams();
  const [item, setItem] = useState('');
  const [wh, setWh] = useState('');
  const [type, setType] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [sortKey, setSortKey] = useState<keyof PlannedOrder>('releaseDate');
  const [dir, setDir] = useState<SortDir>('asc');
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [open, setOpen] = useState<string | null>(params.get('order') ?? params.get('focus'));
  const [toast, setToast] = useState('');
  // Full result of the last release: a failure's reason must never be cut off, so it gets its own dialog.
  const [releaseResult, setReleaseResult] = useState<ReleaseResult | null>(null);

  useEffect(() => { setOpen(params.get('order') ?? params.get('focus')); }, [params]);

  const all = run.data?.plannedOrders ?? [];
  const warehouses = useMemo(() => [...new Set(all.flatMap((o) => (o.fromWarehouse ? [o.warehouseCode, o.fromWarehouse] : [o.warehouseCode])))].sort(), [all]);
  const rows = useMemo(() => {
    const f = all.filter(
      (o) =>
        (!item || o.itemCode.toLowerCase().includes(item.toLowerCase())) &&
        (!wh || o.warehouseCode === wh || o.fromWarehouse === wh) &&
        (!type || o.type === type) &&
        (!from || o.releaseDate >= from) &&
        (!to || o.releaseDate <= to),
    );
    return sortBy(f, sortKey, dir);
  }, [all, item, wh, type, from, to, sortKey, dir]);

  const th = (key: keyof PlannedOrder, label: string, cls = '') => (
    <th className={cls} onClick={() => { if (sortKey === key) setDir(dir === 'asc' ? 'desc' : 'asc'); else { setSortKey(key); setDir('asc'); } }}>
      {tr(label)}{sortKey === key && <span className="arrow">{dir === 'asc' ? '▲' : '▼'}</span>}
    </th>
  );

  const toggle = (id: string) => setSel((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const allSel = rows.length > 0 && rows.every((r) => sel.has(r.id));

  const noVendorSel = useMemo(
    () => all.filter((o) => sel.has(o.id) && o.type === 'PurchaseOrder' && !o.vendorSource).length,
    [all, sel],
  );
  const doRelease = () => {
    const ids = [...sel];
    const warnNv = noVendorSel ? ` ${noVendorSel} selected purchase order(s) have no vendor and will fail to release.` : '';
    release.mutate(ids, {
      onSuccess: (r) => {
        const failed = r.failures?.length ?? 0;
        setToast(
          r.writebackEnabled === false
            ? `Firmed ${r.released} order(s). SAP B1 write-back is off, nothing was created in B1.`
            : failed > 0
              ? `Pushed ${r.pushedToB1 ?? 0} to SAP B1, ${failed} failed. See the details.`
              : `Released ${r.released} order(s): created in SAP B1`,
        );
        if (warnNv) setToast((t) => t + warnNv);
        if (r.writebackEnabled !== false && failed > 0) setReleaseResult(r);
        setSel(new Set());
        setTimeout(() => setToast(''), 8000);
      },
    });
  };

  return (
    <>
      <PageHeader
        crumb="Planning"
        title="Planned Orders"
        actions={<><RunPicker /><AskAiButton /><button className="btn primary" disabled={!isAdmin || !sel.size || release.isPending} title={!isAdmin ? tr('Only administrators can release orders to SAP B1') : noVendorSel ? tr('{n} selected purchase order(s) have no vendor and will fail to release', { n: noVendorSel }) : undefined} onClick={doRelease}>
          {release.isPending ? tr('Releasing…') : tr('Release to SAP B1 ({n})', { n: sel.size })}
        </button></>}
      />
      <div className="page">
        <div className="toolbar">
          <div className="field"><label>{tr('Item')}</label><input className="search" placeholder={tr('Search item…')} value={item} onChange={(e) => setItem(e.target.value)} /></div>
          <div className="field"><label>{tr('Warehouse')}</label>
            <select value={wh} onChange={(e) => setWh(e.target.value)}><option value="">{tr('All')}</option>{warehouses.map((w) => <option key={w}>{w}</option>)}</select></div>
          <div className="field"><label>{tr('Type')}</label>
            <select value={type} onChange={(e) => setType(e.target.value)}><option value="">{tr('All')}</option>{ORDER_TYPES.map((t) => <option key={t} value={t}>{tr(orderTypeLabel(t))}</option>)}</select></div>
          <div className="field"><label>{tr('Release from')}</label><input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></div>
          <div className="field"><label>{tr('Release to')}</label><input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div>
          <div className="spacer" />
          <span className="muted">{tr('{n} of {total} orders', { n: rows.length, total: all.length })}</span>
        </div>
        <div className="card">
          {run.isLoading && <Loading />}
          {run.isError && <ErrorState error={run.error} />}
          {run.data && (
            <div className="table-wrap">
              <table className="data">
                <thead>
                  <tr>
                    <th className="nosort"><input type="checkbox" checked={allSel} onChange={() => setSel(allSel ? new Set() : new Set(rows.map((r) => r.id)))} /></th>
                    {th('lowLevelCode', 'LLC')}{th('itemCode', 'Item')}{th('warehouseCode', 'Whs')}{th('type', 'Type')}{th('vendorCode', 'Vendor')}{th('leadTimeDays', 'Lead time', 'num')}
                    {th('quantity', 'Qty', 'num')}{th('releaseDate', 'Release')}{th('dueDate', 'Due')}{th('firmed', 'Status')}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((o) => (
                    <Fragment key={o.id}>
                      <tr onClick={() => setOpen(open === o.id ? null : o.id)} style={{ cursor: 'pointer' }}>
                        <td onClick={(e) => e.stopPropagation()}><input type="checkbox" checked={sel.has(o.id)} onChange={() => toggle(o.id)} /></td>
                        <td><Llc level={o.lowLevelCode} /></td>
                        <td className="mono">{o.itemCode}</td>
                        <td>{o.type === 'TransferRequest' && o.fromWarehouse ? <span title={`Transfer from ${o.fromWarehouse} to ${o.warehouseCode}`}>{o.fromWarehouse} → {o.warehouseCode}</span> : o.warehouseCode}</td>
                        <td><span className={`type-tag ${orderTypeTag(o.type)}`}>{tr(orderTypeLabel(o.type))}</span></td>
                        <td><Vendor o={o} /></td>
                        <td className="num"><LeadTime o={o} /></td>
                        <td className="num">{fmtNum(o.quantity)}</td>
                        <td style={isPast(o.releaseDate) ? { color: 'var(--danger)', fontWeight: 600 } : undefined}>{fmtDate(o.releaseDate)}</td>
                        <td>{fmtDate(o.dueDate)}</td>
                        <td>
                          {o.firmed ? <Pill tone="ok">{tr('Firmed')}{o.b1DocEntry ? ` · ${o.b1DocEntry}` : ''}</Pill> : <Pill>{tr('Planned')}</Pill>}
                          <ExplainButton label={`${o.itemCode} ${orderTypeLabel(o.type)}`}
                            question={`Why was this planned order created? Planned order id ${o.id}: ${orderTypeLabel(o.type)} for item ${o.itemCode}, quantity ${fmtNum(o.quantity)}, warehouse ${o.warehouseCode}, due ${o.dueDate}. Show which demand it covers, how its quantity and dates were set (lead time, lot sizing), and anything I should double-check before releasing it.`} />
                        </td>
                      </tr>
                      {open === o.id && (
                        <tr className="expand-row"><td colSpan={11}><Pegging runId={runId!} id={o.id} /></td></tr>
                      )}
                    </Fragment>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
      {releaseResult && <ReleaseResultDialog result={releaseResult} orders={all} onClose={() => setReleaseResult(null)} />}
      {toast && <div className="toast" role="status">{toast}</div>}
    </>
  );
}

/** Every failure with SAP B1's complete message, selectable and copyable. */
function ReleaseResultDialog({ result, orders, onClose }: { result: ReleaseResult; orders: PlannedOrder[]; onClose: () => void }) {
  const byId = new Map(orders.map((o) => [o.id, o]));
  const label = (id: string | null) => {
    const o = id ? byId.get(id) ?? result.plannedOrders?.find((x) => x.id === id) : undefined;
    return o ? `${o.itemCode} (${orderTypeLabel(o.type)}, qty ${fmtNum(o.quantity)}, warehouse ${o.type === 'TransferRequest' && o.fromWarehouse ? `${o.fromWarehouse} to ${o.warehouseCode}` : o.warehouseCode})` : 'Release';
  };
  const created = (result.plannedOrders ?? []).filter((o) => o.b1DocEntry);
  const failures = result.failures ?? [];
  const text = failures.map((f) => `${label(f.plannedOrderId)}: ${f.error}`).join('\n\n');
  return (
    <Dialog
      title="Release to SAP B1: result"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={() => void navigator.clipboard?.writeText(text)}>Copy details</button>
          <button type="button" className="btn primary" onClick={onClose}>Close</button>
        </>
      }
    >
      <p><strong>{created.length}</strong> created in SAP B1, <strong style={{ color: 'var(--danger)' }}>{failures.length}</strong> failed.</p>
      {created.length > 0 && (
        <ul>{created.map((o) => <li key={o.id}>{label(o.id)}: SAP B1 document {o.b1DocEntry}</li>)}</ul>
      )}
      {failures.map((f, i) => (
        <div key={i} style={{ margin: '10px 0' }}>
          <div style={{ fontWeight: 600 }}>{label(f.plannedOrderId)}</div>
          <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', userSelect: 'text', background: 'var(--page)', padding: 8, borderRadius: 6, margin: '4px 0 0' }}>{f.error}</pre>
        </div>
      ))}
      <p className="muted">Failed orders stay firmed; fix the cause and release them again. Orders already created are skipped on retry.</p>
    </Dialog>
  );
}
