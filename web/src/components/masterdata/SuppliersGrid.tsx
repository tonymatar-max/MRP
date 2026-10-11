import { useEffect, useMemo, useState } from 'react';
import { useMasterSuppliers, usePutMasterSupplier } from '../../api/hooks';
import type { DataSource, MasterSupplierQuery, MasterSupplierRow } from '../../api/types';
import { useDebounced } from '../useDebounced';
import { ErrorState, Loading } from '../ui';
import { CellButton, InlineInput } from './cells';

type Quick = NonNullable<MasterSupplierQuery['filter']>;
const QUICK: { id: Quick; label: string }[] = [
  { id: 'all', label: 'All' }, { id: 'withLeadTime', label: 'With lead time' }, { id: 'withoutLeadTime', label: 'Without' }, { id: 'inUse', label: 'In use' },
];

export function SuppliersGrid({ source }: { source: DataSource }) {
  const [search, setSearch] = useState('');
  const dSearch = useDebounced(search, 300);
  const [filter, setFilter] = useState<Quick>('all');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(100);
  const [editing, setEditing] = useState<{ code: string; field: 'lt' | 'notes' } | null>(null);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const query: MasterSupplierQuery = useMemo(() => ({ source, search: dSearch.trim() || undefined, filter, page, pageSize }), [source, dSearch, filter, page, pageSize]);
  const q = useMasterSuppliers(query);
  const put = usePutMasterSupplier();
  useEffect(() => { setPage(1); }, [source, dSearch, filter, pageSize]);

  const rows = q.data?.suppliers ?? [];
  const total = q.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const flash = (ok: boolean, text: string) => { setMsg({ ok, text }); if (ok) setTimeout(() => setMsg((m) => (m?.text === text ? null : m)), 4000); };

  const save = (r: MasterSupplierRow, patch: { leadTimeDays?: number | null; notes?: string }) => {
    setEditing(null);
    put.mutate({ cardCode: r.cardCode, leadTimeDays: patch.leadTimeDays !== undefined ? patch.leadTimeDays : r.leadTimeDays, notes: patch.notes ?? r.notes }, {
      onSuccess: () => flash(true, `${r.cardCode} saved.`),
      onError: (e) => flash(false, `${r.cardCode}: ${(e as Error).message}`),
    });
  };
  const saveLt = (r: MasterSupplierRow, text: string) => {
    if (text.trim() === '') return save(r, { leadTimeDays: null });
    const n = Number(text);
    if (!Number.isFinite(n) || n < 0) return flash(false, `${r.cardCode}: lead time must be a number of 0 or more.`);
    save(r, { leadTimeDays: n });
  };

  return (
    <>
      <div className="toolbar">
        <div className="field"><label htmlFor="ms-search">Search</label>
          <input id="ms-search" className="search" placeholder="Supplier code or name…" value={search} onChange={(e) => setSearch(e.target.value)} /></div>
        <div className="chips" role="group" aria-label="Quick filters" style={{ alignSelf: 'flex-end', paddingBottom: 4 }}>
          {QUICK.map((f) => <button key={f.id} type="button" className={`chip ${filter === f.id ? 'on' : ''}`} aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>)}
        </div>
        <div className="spacer" />
        <span className="muted">{total.toLocaleString()} supplier{total === 1 ? '' : 's'}</span>
      </div>
      {msg && <div className={`alert ${msg.ok ? 'ok' : 'danger'}`} role={msg.ok ? 'status' : 'alert'} style={{ marginBottom: 12 }}>{msg.text}</div>}
      <div className="card">
        {q.isLoading && <Loading />}
        {q.isError && <ErrorState error={q.error} />}
        {q.data && (
          <div className="table-wrap" style={{ opacity: q.isFetching ? 0.6 : 1 }}>
            <table className="data md-grid">
              <thead><tr>
                <th className="nosort">Code</th><th className="nosort">Name</th><th className="nosort num">Lead time (days)</th>
                <th className="nosort num">Items using it</th><th className="nosort">Notes</th>
              </tr></thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.cardCode}>
                    <td className="mono">{r.cardCode}</td>
                    <td>{r.cardName}</td>
                    <td className="num">
                      {editing?.code === r.cardCode && editing.field === 'lt'
                        ? <InlineInput numeric initial={r.leadTimeDays == null ? '' : String(r.leadTimeDays)} label={`Lead time days for ${r.cardCode}`} onSave={(v) => saveLt(r, v)} onCancel={() => setEditing(null)} />
                        : <CellButton overridden={r.leadTimeDays != null} label={`Edit lead time of ${r.cardCode}, now ${r.leadTimeDays ?? 'none'}`} title="Click to edit; empty clears it" onClick={() => setEditing({ code: r.cardCode, field: 'lt' })}>
                            {r.leadTimeDays == null ? '-' : r.leadTimeDays}
                          </CellButton>}
                    </td>
                    <td className="num">{r.itemCount}</td>
                    <td className="name-cell">
                      {editing?.code === r.cardCode && editing.field === 'notes'
                        ? <InlineInput initial={r.notes} label={`Notes for ${r.cardCode}`} onSave={(v) => save(r, { notes: v })} onCancel={() => setEditing(null)} />
                        : <CellButton label={`Edit notes of ${r.cardCode}`} title="Click to edit" onClick={() => setEditing({ code: r.cardCode, field: 'notes' })}>{r.notes || <span className="muted">Add a note</span>}</CellButton>}
                    </td>
                  </tr>
                ))}
                {!rows.length && <tr><td colSpan={5} className="center-state">No suppliers match these filters.</td></tr>}
              </tbody>
            </table>
          </div>
        )}
        <div className="card-f pager">
          <label htmlFor="ms-ps" className="hint">Rows per page</label>
          <select id="ms-ps" value={pageSize} onChange={(e) => setPageSize(+e.target.value)}>{[50, 100, 200].map((n) => <option key={n}>{n}</option>)}</select>
          <span className="spacer" />
          <span className="hint">Page {page} of {pages}</span>
          <button type="button" className="btn sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <button type="button" className="btn sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      </div>
      <p className="hint" style={{ marginTop: 10 }}>SAP Business One holds no supplier lead time, so it is entered here. When the lead time rule includes Supplier, this value is used for every item bought from that supplier.</p>
    </>
  );
}
