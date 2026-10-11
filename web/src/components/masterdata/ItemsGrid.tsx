import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../api/client';
import { useItemGroups, useMasterItems, usePutMasterItem } from '../../api/hooks';
import type { DataSource, MasterItemQuery, MasterItemRow, OverridableField } from '../../api/types';
import { SupplierSearch } from '../SupplierSearch';
import { useDebounced } from '../useDebounced';
import { ErrorState, Loading } from '../ui';
import { BulkEditDialog } from './BulkEditDialog';
import { CellButton, InlineInput } from './cells';
import { ItemEditPanel } from './ItemEditPanel';
import { fmtVal, leadTimeTooltip, LT_SOURCE_LABEL, METHOD_LABEL, overrideBody } from './helpers';
import { LeadTimeEditor } from './LeadTimeEditor';

type Quick = NonNullable<MasterItemQuery['filter']>;
const QUICK: { id: Quick; label: string }[] = [
  { id: 'all', label: 'All' }, { id: 'overridden', label: 'Overridden' }, { id: 'noSupplier', label: 'No supplier' }, { id: 'noLeadTime', label: 'No lead time' },
];
type SortKey = NonNullable<MasterItemQuery['sort']>;
const NUM_COLS: { f: OverridableField; label: string }[] = [
  { f: 'minOrderQty', label: 'Min order' }, { f: 'orderMultiple', label: 'Multiple' }, { f: 'safetyStock', label: 'Safety stock' },
  { f: 'reorderPoint', label: 'Reorder pt' }, { f: 'maxStockLevel', label: 'Max level' },
];

function downloadCsv(text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a');
  a.href = url; a.download = 'master-data-items.csv';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function ItemsGrid({ source }: { source: DataSource }) {
  const [search, setSearch] = useState('');
  const dSearch = useDebounced(search, 300);
  const [group, setGroup] = useState('');
  const [supplier, setSupplier] = useState('');
  const [proc, setProc] = useState<'' | 'Buy' | 'Make'>('');
  const [filter, setFilter] = useState<Quick>('all');
  const [sort, setSort] = useState<SortKey>('itemCode');
  const [dir, setDir] = useState<'asc' | 'desc'>('asc');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(100);
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [editing, setEditing] = useState<{ code: string; field: OverridableField } | null>(null);
  const [panel, setPanel] = useState<string | null>(null);
  const [bulk, setBulk] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [busyCell, setBusyCell] = useState<string | null>(null);

  const query: MasterItemQuery = useMemo(() => ({
    source, search: dSearch.trim() || undefined, group: group || undefined, supplier: supplier || undefined,
    procurement: proc, filter, sort, dir, page, pageSize,
  }), [source, dSearch, group, supplier, proc, filter, sort, dir, page, pageSize]);
  const q = useMasterItems(query);
  const groups = useItemGroups(source);
  const put = usePutMasterItem();

  useEffect(() => { setPage(1); }, [source, dSearch, group, supplier, proc, filter, pageSize]);
  const rows = q.data?.items ?? [];
  // Rows seen on any page, so bulk edit can see the suppliers of items selected on other pages.
  const seen = useRef(new Map<string, MasterItemRow>());
  rows.forEach((r) => seen.current.set(r.itemCode, r));
  const total = q.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const panelRow = panel ? rows.find((r) => r.itemCode === panel) : undefined;

  const flash = (ok: boolean, text: string) => { setMsg({ ok, text }); if (ok) setTimeout(() => setMsg((m) => (m?.text === text ? null : m)), 4000); };
  const save = (row: MasterItemRow, field: OverridableField, value: number | string | null) => {
    setEditing(null);
    setBusyCell(`${row.itemCode}|${field}`);
    put.mutate({ code: row.itemCode, override: overrideBody(row, { [field]: value }) }, {
      onSuccess: () => { setBusyCell(null); flash(true, `${row.itemCode} saved.`); },
      onError: (e) => { setBusyCell(null); flash(false, `${row.itemCode}: ${(e as Error).message}`); },
    });
  };
  const saveNumber = (row: MasterItemRow, field: OverridableField, text: string) => {
    if (text.trim() === '') return save(row, field, null);
    const n = Number(text);
    if (!Number.isFinite(n) || n < 0) return flash(false, `${row.itemCode}: ${field} must be a number of 0 or more.`);
    save(row, field, n);
  };

  const sortTh = (key: SortKey, label: string, cls = '') => (
    <th className={cls} aria-sort={sort === key ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button type="button" className="th-btn" onClick={() => { if (sort === key) setDir(dir === 'asc' ? 'desc' : 'asc'); else { setSort(key); setDir('asc'); } }}>
        {label}{sort === key && <span className="arrow">{dir === 'asc' ? '▲' : '▼'}</span>}
      </button>
    </th>
  );

  const numCell = (r: MasterItemRow, f: OverridableField) => {
    const isEd = editing?.code === r.itemCode && editing.field === f;
    const ovr = r.overriddenFields.includes(f);
    const eff = (r.effective as unknown as Record<string, number>)[f];
    const b1 = (r.b1 as unknown as Record<string, number>)[f];
    if (isEd) return <InlineInput numeric initial={String(eff)} label={`${f} for ${r.itemCode}`} onSave={(v) => saveNumber(r, f, v)} onCancel={() => setEditing(null)} />;
    return (
      <CellButton overridden={ovr} busy={busyCell === `${r.itemCode}|${f}`} label={`Edit ${f} of ${r.itemCode}, now ${eff}`}
        title={ovr ? `Overridden. SAP B1 value: ${fmtVal(b1)}` : 'Click to edit'} onClick={() => setEditing({ code: r.itemCode, field: f })}>
        {fmtVal(eff)}
      </CellButton>
    );
  };

  const exportHref = api.masterItemsExportHref({ ...query });
  const allSel = rows.length > 0 && rows.every((r) => sel.has(r.itemCode));
  const toggle = (c: string) => setSel((s) => { const n = new Set(s); if (n.has(c)) n.delete(c); else n.add(c); return n; });
  const [supName, setSupName] = useState('');

  return (
    <>
      <div className="toolbar">
        <div className="field"><label htmlFor="md-search">Search</label>
          <input id="md-search" className="search" placeholder="Item code or name…" value={search} onChange={(e) => setSearch(e.target.value)} /></div>
        <div className="field"><label htmlFor="md-group">Group</label>
          <select id="md-group" value={group} onChange={(e) => setGroup(e.target.value)}>
            <option value="">All</option>{(groups.data ?? []).map((g) => <option key={g.code} value={g.code}>{g.name}</option>)}
          </select></div>
        <div className="field"><label htmlFor="md-proc">Type</label>
          <select id="md-proc" value={proc} onChange={(e) => setProc(e.target.value as '' | 'Buy' | 'Make')}>
            <option value="">Buy and make</option><option value="Buy">Buy</option><option value="Make">Make</option>
          </select></div>
        <div className="field supplier-filter"><label htmlFor="md-sup">Supplier</label>
          {supplier ? (
            <span className="chip on removable"><span className="mono">{supplier}</span>{supName ? ` ${supName}` : ''}
              <button type="button" aria-label="Clear supplier filter" onClick={() => { setSupplier(''); setSupName(''); }}>×</button></span>
          ) : <SupplierSearch id="md-sup" source={source} placeholder="Any supplier" onPick={(s) => { setSupplier(s.cardCode); setSupName(s.cardName); }} />}
        </div>
        <div className="spacer" />
        {exportHref
          ? <a className="btn" href={exportHref} download>Export CSV</a>
          : <button type="button" className="btn" onClick={() => downloadCsv(api.exportMockCsv(query))}>Export CSV</button>}
        <button type="button" className="btn primary" disabled={!sel.size} onClick={() => setBulk(true)}>Bulk edit ({sel.size})</button>
      </div>
      <div className="toolbar">
        <div className="chips" role="group" aria-label="Quick filters">
          {QUICK.map((f) => <button key={f.id} type="button" className={`chip ${filter === f.id ? 'on' : ''}`} aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>)}
        </div>
        <div className="spacer" />
        {sel.size > 0 && <button type="button" className="btn ghost sm" onClick={() => setSel(new Set())}>Clear selection</button>}
        <span className="muted">{total.toLocaleString()} item{total === 1 ? '' : 's'}</span>
      </div>
      {msg && <div className={`alert ${msg.ok ? 'ok' : 'danger'}`} role={msg.ok ? 'status' : 'alert'} style={{ marginBottom: 12 }}>{msg.text}</div>}
      <div className="card">
        {q.isLoading && <Loading />}
        {q.isError && <ErrorState error={q.error} />}
        {q.data && (
          <div className="table-wrap" style={{ opacity: q.isFetching ? 0.6 : 1 }}>
            <table className="data md-grid">
              <thead>
                <tr>
                  <th className="nosort"><input type="checkbox" aria-label="Select all items on this page" checked={allSel}
                    onChange={() => setSel((s) => { const n = new Set(s); rows.forEach((r) => (allSel ? n.delete(r.itemCode) : n.add(r.itemCode))); return n; })} /></th>
                  {sortTh('itemCode', 'Code')}{sortTh('itemName', 'Name')}
                  <th className="nosort">Group</th><th className="nosort">Type</th><th className="nosort">Supplier</th>
                  {sortTh('leadTime', 'Lead time', 'num')}
                  {NUM_COLS.map((c) => <th key={c.f} className="nosort num">{c.label}</th>)}
                  <th className="nosort">Method</th><th className="nosort"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => {
                  const supOvr = r.overriddenFields.includes('supplier');
                  const supEd = editing?.code === r.itemCode && editing.field === 'supplier';
                  const ltOvr = r.overriddenFields.includes('leadTimeDays');
                  const ltEd = editing?.code === r.itemCode && editing.field === 'leadTimeDays';
                  return (
                    <tr key={r.itemCode} className={sel.has(r.itemCode) ? 'sel' : ''}>
                      <td><input type="checkbox" aria-label={`Select ${r.itemCode}`} checked={sel.has(r.itemCode)} onChange={() => toggle(r.itemCode)} /></td>
                      <td className="mono">{r.itemCode}{r.overriddenFields.length > 0 && <span className="ovr-dot" title={`Overridden: ${r.overriddenFields.join(', ')}`} aria-label="Has overrides" />}</td>
                      <td className="name-cell" title={r.itemName}>{r.itemName}</td>
                      <td>{r.groupName}</td>
                      <td><span className={`type-tag ${r.procurement === 'Make' ? 'prod' : 'po'}`}>{r.procurement}</span></td>
                      <td>
                        {r.procurement === 'Make' ? <span className="muted">—</span> : supEd ? (
                          <div className="cell-edit-sup">
                            <SupplierSearch source={source} autoFocus label={`Supplier for ${r.itemCode}`} onCancel={() => setEditing(null)}
                              onPick={(s) => save(r, 'supplier', s.cardCode)} />
                            {supOvr && <button type="button" className="btn sm" onClick={() => save(r, 'supplier', null)}>Revert to B1</button>}
                          </div>
                        ) : (
                          <CellButton overridden={supOvr} busy={busyCell === `${r.itemCode}|supplier`} label={`Edit supplier of ${r.itemCode}, now ${r.effective.supplier ?? 'none'}`}
                            title={supOvr ? `Overridden. SAP B1 supplier: ${r.b1.supplier ?? 'none'}` : 'Click to edit'} onClick={() => setEditing({ code: r.itemCode, field: 'supplier' })}>
                            {r.effective.supplier ? <><span className="mono">{r.effective.supplier}</span> <span className={`src-badge ${supOvr ? 'ovr' : 'b1'}`}>{supOvr ? 'override' : 'B1'}</span></> : <span className="no-sup">None</span>}
                          </CellButton>
                        )}
                      </td>
                      <td className="num">
                        <CellButton overridden={ltOvr} busy={busyCell === `${r.itemCode}|leadTimeDays`}
                          label={`Edit lead times of ${r.itemCode}; effective lead time ${r.leadTime.days ?? 'none'} days`}
                          title={`${leadTimeTooltip(r)}${ltOvr ? ` SAP B1 item lead time: ${fmtVal(r.b1.leadTimeDays)}.` : ''}`}
                          onClick={() => setEditing({ code: r.itemCode, field: 'leadTimeDays' })}>
                          {r.leadTime.days == null ? '—' : `${r.leadTime.days} d`} <span className={`lt-badge ${r.leadTime.source}`}>{LT_SOURCE_LABEL[r.leadTime.source]}</span>
                        </CellButton>
                        {ltEd && <LeadTimeEditor row={r} source={source} onClose={() => setEditing(null)} onFlash={flash} />}
                      </td>
                      {NUM_COLS.map((c) => <td key={c.f} className="num">{numCell(r, c.f)}</td>)}
                      <td title={r.overriddenFields.includes('planningMethod') ? `Overridden. SAP B1: ${METHOD_LABEL[r.b1.planningMethod]}` : undefined}
                        className={r.overriddenFields.includes('planningMethod') ? 'ovr-text' : ''}>{METHOD_LABEL[r.effective.planningMethod]}</td>
                      <td className="row-actions"><button type="button" className="btn sm" aria-label={`Edit all fields of ${r.itemCode}`} onClick={() => setPanel(r.itemCode)}>Edit</button></td>
                    </tr>
                  );
                })}
                {!rows.length && <tr><td colSpan={14} className="center-state">No items match these filters.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
        <div className="card-f pager">
          <label htmlFor="md-ps" className="hint">Rows per page</label>
          <select id="md-ps" value={pageSize} onChange={(e) => setPageSize(+e.target.value)}>{[50, 100, 200].map((n) => <option key={n}>{n}</option>)}</select>
          <span className="spacer" />
          <span className="hint">Page {page} of {pages}</span>
          <button type="button" className="btn sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <button type="button" className="btn sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      </div>
      <p className="hint" style={{ marginTop: 10 }}>
        Click a supplier, lead time, quantity or stock cell to edit it; press Enter to save, Escape to cancel. An empty value reverts that field to SAP B1.
        The lead time column shows the effective lead time under the default priority and mode in Settings; click it to edit the item&apos;s own lead time or its supplier&apos;s (a supplier lead time applies to every item from that supplier).
      </p>
      {panelRow && <ItemEditPanel row={panelRow} source={source} onClose={() => setPanel(null)} onMessage={flash} />}
      {bulk && <BulkEditDialog codes={[...sel]} rows={[...sel].map((c) => seen.current.get(c)).filter((r): r is MasterItemRow => !!r)} source={source} onClose={() => setBulk(false)}
        onDone={(n, note) => { setBulk(false); setSel(new Set()); flash(true, `${n ? `Updated ${n} item${n === 1 ? '' : 's'}.` : ''}${note ?? ''}`.trim()); }} />}
    </>
  );
}
