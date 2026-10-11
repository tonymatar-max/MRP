import { KeyboardEvent, useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { usePreviewDetails, usePreviewDetailsExport } from '../../api/hooks';
import type { DetailsCell, DetailsColumn, DetailsRow, PreviewDetailsRequest, PreviewMetric, RunCriteria } from '../../api/types';
import { useDebounced } from '../useDebounced';
import { fmtDate, fmtNum } from '../util';

const FOCUSABLE = 'input:not(:disabled), button:not(:disabled), [href], [tabindex]:not([tabindex="-1"])';

function Cell({ col, v }: { col: DetailsColumn; v: DetailsCell }) {
  if (v === null || v === undefined || v === '') return <span className="muted">—</span>;
  switch (col.type) {
    case 'number': return <>{typeof v === 'number' ? fmtNum(v) : String(v)}</>;
    case 'date': return <>{fmtDate(String(v))}</>;
    case 'flag': return v
      ? <span className="pill danger" role="img" aria-label="Yes"><span className="dot" aria-hidden="true" /></span>
      : <span className="muted" role="img" aria-label="No">–</span>;
    case 'badge': {
      const t = String(v);
      const tone = t === 'None' ? 'warn' : t === 'Master data' || t === 'Buy' ? 'info' : '';
      return <span className={`pill ${tone}`}>{t}</span>;
    }
    default: return <>{String(v)}</>;
  }
}

function compare(a: DetailsCell, b: DetailsCell, type: DetailsColumn['type']): number {
  if (a === null || a === undefined || a === '') return b === null || b === undefined || b === '' ? 0 : 1; // blanks last
  if (b === null || b === undefined || b === '') return -1;
  if (type === 'number') return Number(a) - Number(b);
  if (type === 'flag') return Number(a) - Number(b);
  return String(a).localeCompare(String(b), undefined, { numeric: true });
}

function save(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Drill-down behind a Review number (docs/preview-details-contract.md). A non-blocking right-hand panel so other numbers stay clickable. */
export function PreviewDetailsPanel({ metric, criteria, onClose }: { metric: PreviewMetric; criteria: RunCriteria; onClose: () => void }) {
  const uid = useId();
  const ref = useRef<HTMLElement>(null);
  const [search, setSearch] = useState('');
  const debounced = useDebounced(search.trim(), 300);
  const [sort, setSort] = useState<{ key: string; dir: 'asc' | 'desc' } | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);
  const exporter = usePreviewDetailsExport();

  const req: PreviewDetailsRequest = { criteria, metric, search: debounced, take: 200 };
  const q = usePreviewDetails(req);
  const d = q.data;

  // A different number starts from a clean slate (search and sort belong to one list).
  useEffect(() => { setSearch(''); setSort(null); setExportError(null); }, [metric]);
  useEffect(() => { ref.current?.querySelector<HTMLElement>('input')?.focus(); }, []);

  const rows = useMemo<DetailsRow[]>(() => {
    if (!d) return [];
    const col = d.columns.find((c) => c.key === sort?.key);
    if (!sort || !col) return d.rows;
    const out = [...d.rows].sort((a, b) => compare(a[sort.key], b[sort.key], col.type));
    return sort.dir === 'desc' ? out.reverse() : out;
  }, [d, sort]);

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); onClose(); return; }
    if (e.key !== 'Tab') return;
    const f = Array.from(ref.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    const active = document.activeElement;
    if (!ref.current?.contains(active)) { e.preventDefault(); first.focus(); }
    else if (e.shiftKey && active === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && active === last) { e.preventDefault(); first.focus(); }
  };

  const download = () => {
    setExportError(null);
    exporter.mutate({ criteria, metric, search: debounced }, {
      onSuccess: (r) => save(r.blob, r.filename),
      onError: (e) => setExportError((e as Error).message),
    });
  };

  const toggleSort = (key: string) => setSort((s) => (s?.key !== key ? { key, dir: 'asc' } : s.dir === 'asc' ? { key, dir: 'desc' } : null));
  const switching = q.isFetching && !!d && d.metric !== metric;
  const err = q.isError ? (q.error as Error).message : null;

  return createPortal(
    <aside ref={ref} className="details-panel" role="dialog" aria-modal="false" aria-labelledby={`${uid}-t`} aria-describedby={`${uid}-d`} onKeyDown={onKeyDown}>
      <div className="dp-h">
        <div style={{ minWidth: 0 }}>
          <h3 id={`${uid}-t`}>{d && d.metric === metric ? d.title : 'Loading…'}</h3>
          <div id={`${uid}-d`} className="muted dp-desc">{d && d.metric === metric ? d.description : ''}</div>
        </div>
        <button type="button" className="btn sm" onClick={onClose} aria-label="Close details">Close</button>
      </div>

      <div className="dp-b" aria-busy={q.isFetching}>
        {d && !switching && (
          <ul className="dp-summary" aria-label="Summary">
            {d.summary.map((s) => <li key={s.label}><span className="dp-sv">{s.value}</span><span className="dp-sl">{s.label}</span></li>)}
          </ul>
        )}
        <div className="field">
          <label htmlFor={`${uid}-s`}>Search</label>
          <input id={`${uid}-s`} type="search" className="search" value={search} placeholder="Item, document, supplier…" onChange={(e) => setSearch(e.target.value)} />
        </div>

        {err && <div className="alert danger" role="alert">{err}</div>}
        {!d && q.isLoading && <div className="center-state" style={{ padding: 24 }}><div className="spinner" />Loading the records…</div>}
        {d && (
          <div className="table-wrap dp-table" style={{ opacity: q.isFetching ? 0.6 : 1 }}>
            <table className="data" aria-label={d.title}>
              <thead>
                <tr>
                  {d.columns.map((c) => {
                    const on = sort?.key === c.key;
                    return (
                      <th key={c.key} className={c.type === 'number' ? 'num' : undefined} scope="col"
                        aria-sort={on ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
                        <button type="button" className="dp-sort" onClick={() => toggleSort(c.key)}>
                          {c.label}{on && <span className="arrow" aria-hidden="true">{sort!.dir === 'asc' ? '▲' : '▼'}</span>}
                        </button>
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={i}>
                    {d.columns.map((c) => <td key={c.key} className={c.type === 'number' ? 'num' : undefined}><Cell col={c} v={r[c.key]} /></td>)}
                  </tr>
                ))}
                {!rows.length && <tr><td colSpan={d.columns.length} className="muted" style={{ textAlign: 'center', padding: 24 }}>{debounced ? 'Nothing matches this search.' : 'No records.'}</td></tr>}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="dp-f">
        <div className="dp-count" role="status" aria-live="polite">
          {d && <>Showing {fmtNum(d.shown)} of {fmtNum(d.matched)}{d.matched !== d.total && <> (total {fmtNum(d.total)})</>}
            {d.truncated && <span className="hint"> · refine the search or export to see everything</span>}</>}
        </div>
        {exportError && <span className="hint err" role="alert">{exportError}</span>}
        <button type="button" className="btn primary sm" onClick={download} disabled={!d || exporter.isPending}>
          {exporter.isPending ? 'Preparing…' : 'Download CSV'}
        </button>
      </div>
    </aside>,
    document.body,
  );
}
