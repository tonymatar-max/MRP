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
import { documentKey, fmtDate, fmtNum, isPast, ORDER_TYPES, orderTypeLabel, orderTypeTag, sortBy, SortDir } from '../components/util';

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
  const tr = useT();
  if (o.type === 'TransferRequest') return <>{tr('Transfer, {n} d', { n: o.leadTimeDays ?? '?' })}</>;
  if (o.leadTimeDays == null || !o.leadTimeSource || o.leadTimeSource === 'None')
    return <span className="muted" title={tr('No lead time found for this item')}>—</span>;
  return <>{tr('{n} d', { n: o.leadTimeDays })} <span className={`lt-badge ${o.leadTimeSource}`} title={tr('Lead time source: {s}', { s: tr(LT_LABEL[o.leadTimeSource]) })}>{tr(LT_LABEL[o.leadTimeSource])}</span></>;
}
function Vendor({ o }: { o: PlannedOrder }) {
  const tr = useT();
  const src = (v: NonNullable<PlannedOrder['vendorSource']>) => (
    <span className={`vendor-badge ${v}`} title={tr('Vendor source: {s}', { s: tr(SRC_LABEL[v]) })}>{tr(SRC_LABEL[v])}</span>
  );
  if (o.type === 'PurchaseRequest' && o.vendorCode)
    return <><bdi className="mono">{o.vendorCode}</bdi>{o.vendorSource && <> {src(o.vendorSource)}</>}</>;
  if (o.type !== 'PurchaseOrder') return <span className="muted">—</span>;
  if (!o.vendorSource) return <span className="vendor-badge none" title={tr('No vendor configured: this purchase order will fail to release')}>{tr('No vendor')}</span>;
  return <><bdi className="mono">{o.vendorCode}</bdi> {src(o.vendorSource)}</>;
}

/** A document never holds more lines than this (PlannedOrderWriteback.MaxLinesPerDocument); a bigger group becomes several. */
const MAX_LINES_PER_DOCUMENT = 100;

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
  const [status, setStatus] = useState<'' | 'Planned' | 'Firmed'>('');
  const [grouped, setGrouped] = useState(true);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
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
        (!status || (status === 'Firmed') === o.firmed) &&
        (!from || o.releaseDate >= from) &&
        (!to || o.releaseDate <= to),
    );
    return sortBy(f, sortKey, dir);
  }, [all, item, wh, type, status, from, to, sortKey, dir]);

  const th = (key: keyof PlannedOrder, label: string, cls = '') => (
    <th className={cls} aria-sort={sortKey === key ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button type="button" className="th-btn" onClick={() => { if (sortKey === key) setDir(dir === 'asc' ? 'desc' : 'asc'); else { setSortKey(key); setDir('asc'); } }}>
        {tr(label)}{sortKey === key && <span className="arrow" aria-hidden="true">{dir === 'asc' ? '▲' : '▼'}</span>}
      </button>
    </th>
  );

  const toggle = (id: string) => setSel((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });

  // The selection can only ever hold orders of THIS run that are in the filtered list: a new run clears it, and a
  // filter that hides an order drops it, so what is counted and released is always what is on screen.
  useEffect(() => { setSel(new Set()); setCollapsed(new Set()); }, [runId]);
  const visibleIds = useMemo(() => new Set(rows.map((r) => r.id)), [rows]);
  useEffect(() => {
    setSel((s) => {
      const n = new Set([...s].filter((id) => visibleIds.has(id)));
      return n.size === s.size ? s : n;
    });
  }, [visibleIds]);
  // THE set the Release button, the counters and the API call all use: selected, in the visible list, not yet in SAP B1.
  const picked = useMemo(() => rows.filter((r) => sel.has(r.id) && !r.b1DocEntry), [rows, sel]);
  const noVendorSel = useMemo(() => picked.filter((o) => o.type === 'PurchaseOrder' && !o.vendorSource).length, [picked]);
  // How the server will combine these orders when released (the run's own setting), and what the page shows for it.
  const grouping = run.data?.criteria?.purchasing?.grouping ?? 'Supplier';
  const canGroup = grouping !== 'None';
  const sections = useMemo(() => {
    if (!grouped || !canGroup) return null;
    const byKey = new Map<string, PlannedOrder[]>();
    for (const o of rows) {
      const k = documentKey(o, grouping);
      if (k) byKey.set(k, [...(byKey.get(k) ?? []), o]);
    }
    const seen = new Set<string>();
    const out: ({ key: string; members: PlannedOrder[] } | { row: PlannedOrder })[] = [];
    for (const o of rows) {
      const k = documentKey(o, grouping);
      if (k && (byKey.get(k)?.length ?? 0) > 1) {
        if (!seen.has(k)) { seen.add(k); out.push({ key: k, members: byKey.get(k)! }); }
      } else out.push({ row: o });
    }
    return out;
  }, [rows, grouped, canGroup, grouping]);

  /** How many SAP B1 documents the selected orders will become (orders already in SAP B1 are skipped). */
  const docCount = useMemo(() => {
    const keys = new Set<string>();
    // Lines of one group beyond the per-document cap spill into further documents, like the server does.
    const perKey = new Map<string, number>();
    for (const o of picked) {
      const k = documentKey(o, grouping);
      if (k) perKey.set(k, (perKey.get(k) ?? 0) + 1); else keys.add(o.id);
    }
    let n = keys.size;
    for (const c of perKey.values()) n += Math.ceil(c / MAX_LINES_PER_DOCUMENT);
    return n;
  }, [picked, grouping]);

  const groupTitle = (o: PlannedOrder) => {
    const date = grouping === 'SupplierAndDate' || o.type === 'TransferRequest' ? ` · ${fmtDate(o.dueDate)}` : '';
    if (o.type === 'TransferRequest')
      return <>{tr(orderTypeLabel(o.type))} · <bdi>{o.fromWarehouse} → {o.warehouseCode}</bdi>{date}</>;
    const what = o.type === 'PurchaseRequest' ? tr('Purchase request') : tr('Purchase order');
    return <>{what} · {o.vendorCode ? <bdi>{o.vendorCode}</bdi> : tr('any supplier')}{date}</>;
  };
  // Header select-all: only rows that can be released and are actually showing (not inside a collapsed group).
  const shown = useMemo(() => {
    if (!sections) return rows;
    return sections.flatMap((sec) => ('row' in sec ? [sec.row] : collapsed.has(sec.key) ? [] : sec.members));
  }, [sections, rows, collapsed]);
  const shownSel = useMemo(() => shown.filter((r) => !r.b1DocEntry), [shown]);
  const pickedShown = shownSel.filter((r) => sel.has(r.id)).length;
  const allSel = shownSel.length > 0 && pickedShown === shownSel.length;
  const toggleAll = () => setSel((s0) => {
    const n = new Set(s0);
    for (const r of shownSel) { if (allSel) n.delete(r.id); else n.add(r.id); }
    return n;
  });
  const toggleGroup = (members: PlannedOrder[]) => setSel((s0) => {
    const n = new Set(s0);
    const free = members.filter((m) => !m.b1DocEntry);
    const every = free.every((m) => n.has(m.id));
    for (const m of free) { if (every) n.delete(m.id); else n.add(m.id); }
    return n;
  });
  const toggleCollapsed = (key: string) => setCollapsed((c) => { const n = new Set(c); n.has(key) ? n.delete(key) : n.add(key); return n; });

  const doRelease = () => {
    const ids = picked.map((o) => o.id);
    if (!ids.length) return;
    const warnNv = noVendorSel ? ` ${tr('{n} selected purchase order(s) have no vendor and will fail to release.', { n: noVendorSel })}` : '';
    release.mutate(ids, {
      onSuccess: (r) => {
        const failed = r.failures?.length ?? 0;
        // With write-back off nothing is created in SAP B1, so the "will fail" vendor warning would only contradict it.
        setToast(
          r.writebackEnabled === false
            ? tr('Firmed {n} order(s). SAP B1 write-back is off, nothing was created in B1.', { n: r.released })
            : (failed > 0
              ? tr('Pushed {n} to SAP B1, {f} failed. See the details.', { n: r.pushedToB1 ?? 0, f: failed })
              : r.documentsCreated != null
                ? tr('Released {n} order(s) in {d} SAP B1 document(s).', { n: r.released, d: r.documentsCreated })
                : tr('Released {n} order(s): created in SAP B1', { n: r.released })) + warnNv,
        );
        if (r.writebackEnabled !== false && failed > 0) setReleaseResult(r);
        setSel(new Set());
        setTimeout(() => setToast(''), 8000);
      },
    });
  };

  const renderRow = (o: PlannedOrder, nested = false, last = false) => (
        <Fragment key={o.id}>
          <tr className={nested ? `in-group${last ? ' group-end' : ''}` : undefined} tabIndex={0} aria-expanded={open === o.id}
            onClick={() => setOpen(open === o.id ? null : o.id)} style={{ cursor: 'pointer' }}
            onKeyDown={(e) => { if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); setOpen(open === o.id ? null : o.id); } }}>
            <td onClick={(e) => e.stopPropagation()}>
              <input type="checkbox" checked={sel.has(o.id) && !o.b1DocEntry} disabled={!!o.b1DocEntry}
                title={o.b1DocEntry ? tr('Already in SAP B1') : undefined}
                aria-label={tr('Select {item}, {type}', { item: o.itemCode, type: tr(orderTypeLabel(o.type)) })} onChange={() => toggle(o.id)} />
            </td>
            <td><Llc level={o.lowLevelCode} /></td>
            <td className="mono">{o.itemCode}</td>
            <td>{o.type === 'TransferRequest' && o.fromWarehouse ? <bdi title={tr('Transfer from {a} to {b}', { a: o.fromWarehouse, b: o.warehouseCode })}>{o.fromWarehouse} → {o.warehouseCode}</bdi> : o.warehouseCode}</td>
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
            <tr className={`expand-row${nested && last ? ' group-end' : ''}`}><td colSpan={11}><Pegging runId={runId!} id={o.id} /></td></tr>
          )}
        </Fragment>
  );

  return (
    <>
      <PageHeader
        crumb="Planning"
        title="Planned Orders"
        actions={<><RunPicker /><AskAiButton /><button className="btn primary" disabled={!isAdmin || !picked.length || release.isPending} title={!isAdmin ? tr('Only administrators can release orders to SAP B1') : noVendorSel ? tr('{n} selected purchase order(s) have no vendor and will fail to release', { n: noVendorSel }) : undefined} onClick={doRelease}>
          {release.isPending ? tr('Releasing…') : tr('Release to SAP B1 ({n})', { n: picked.length })}
        </button></>}
      />
      <div className="page">
        <div className="toolbar">
          <div className="field"><label htmlFor="pl-item">{tr('Item')}</label><input id="pl-item" className="search" placeholder={tr('Search item…')} value={item} onChange={(e) => setItem(e.target.value)} /></div>
          <div className="field"><label htmlFor="pl-wh">{tr('Warehouse')}</label>
            <select id="pl-wh" value={wh} onChange={(e) => setWh(e.target.value)}><option value="">{tr('All')}</option>{warehouses.map((w) => <option key={w}>{w}</option>)}</select></div>
          <div className="field"><label htmlFor="pl-type">{tr('Type')}</label>
            <select id="pl-type" value={type} onChange={(e) => setType(e.target.value)}><option value="">{tr('All')}</option>{ORDER_TYPES.map((t) => <option key={t} value={t}>{tr(orderTypeLabel(t))}</option>)}</select></div>
          <div className="field"><label htmlFor="pl-status">{tr('Status')}</label>
            <select id="pl-status" value={status} onChange={(e) => setStatus(e.target.value as '' | 'Planned' | 'Firmed')}>
              <option value="">{tr('All')}</option><option value="Planned">{tr('Planned')}</option><option value="Firmed">{tr('Firmed')}</option>
            </select></div>
          <div className="field"><label htmlFor="pl-from">{tr('Release from')}</label><input id="pl-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></div>
          <div className="field"><label htmlFor="pl-to">{tr('Release to')}</label><input id="pl-to" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div>
          {canGroup && (
            <label className="check-inline" title={tr('Show the orders that will share one SAP B1 document together')}>
              <input type="checkbox" checked={grouped} onChange={(e) => setGrouped(e.target.checked)} /> {tr('Group by document')}
            </label>
          )}
          <div className="spacer" />
          {picked.length > 0 && <span className="muted">{tr('{n} selected → {d} document(s)', { n: picked.length, d: docCount })}</span>}
          {sections && sortKey !== 'releaseDate' && <span className="muted">{tr('Groups follow their first line in the sort')}</span>}
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
                    <th className="nosort"><input type="checkbox" checked={allSel} aria-label={tr('Select all shown orders')}
                      ref={(el) => { if (el) el.indeterminate = pickedShown > 0 && !allSel; }} onChange={toggleAll} /></th>
                    {th('lowLevelCode', 'LLC')}{th('itemCode', 'Item')}{th('warehouseCode', 'Whs')}{th('type', 'Type')}{th('vendorCode', 'Vendor')}{th('leadTimeDays', 'Lead time', 'num')}
                    {th('quantity', 'Qty', 'num')}{th('releaseDate', 'Release')}{th('dueDate', 'Due')}{th('firmed', 'Status')}
                  </tr>
                </thead>
                <tbody>
                  {sections
                    ? sections.map((sec) => {
                        if ('row' in sec) return renderRow(sec.row);
                        const n = sec.members.length;
                        const free = sec.members.filter((m) => !m.b1DocEntry);
                        const picks = free.filter((m) => sel.has(m.id)).length;
                        const closed = collapsed.has(sec.key);
                        const docs = Math.ceil(free.length / MAX_LINES_PER_DOCUMENT);
                        const m0 = sec.members[0];
                        const name = m0.type === 'TransferRequest' ? `${m0.fromWarehouse} → ${m0.warehouseCode}` : (m0.vendorCode || tr('any supplier'));
                        return (
                          <Fragment key={sec.key}>
                            <tr className="group-row">
                              <td>
                                <input type="checkbox" checked={free.length > 0 && picks === free.length} disabled={free.length === 0}
                                  aria-label={tr('Select all {n} lines of this document', { n })}
                                  ref={(el) => { if (el) el.indeterminate = picks > 0 && picks < free.length; }} onChange={() => toggleGroup(sec.members)} />
                              </td>
                              <td colSpan={10}>
                                <div className="group-head">
                                  <button type="button" className="btn ghost sm" aria-expanded={!closed}
                                    aria-label={tr(closed ? 'Expand {name}' : 'Collapse {name}', { name })} onClick={() => toggleCollapsed(sec.key)}>{closed ? '▸' : '▾'}</button>
                                  <strong>{groupTitle(m0)}</strong>
                                  <span className="muted">· {tr('{n} lines', { n })}{picks > 0 ? ` · ${tr('{k} of {n} selected', { k: picks, n: free.length })}` : ''}</span>
                                  <span className="hint">→ {free.length === 0
                                    ? tr('already in SAP B1')
                                    : docs > 1
                                      ? tr('{d} SAP B1 documents (at most {m} lines each)', { d: docs, m: MAX_LINES_PER_DOCUMENT })
                                      : tr('one SAP B1 document')}
                                    {free.length > 0 && free.length < n ? ` · ${tr('{n} already in SAP B1', { n: n - free.length })}` : ''}</span>
                                </div>
                              </td>
                            </tr>
                            {!closed && sec.members.map((m, i) => renderRow(m, true, i === n - 1))}
                          </Fragment>
                        );
                      })
                    : rows.map((o) => renderRow(o))}
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
