import { useEffect, useState } from 'react';
import { useBulkMasterItems, useSetSupplierLeadTime } from '../../api/hooks';
import type { DataSource, MasterItemRow, OverridableField } from '../../api/types';
import { SupplierSearch } from '../SupplierSearch';
import { FIELD_LABEL, parseDays } from './helpers';

type Mode = 'keep' | 'set' | 'clear';
const FIELDS: OverridableField[] = ['supplier', 'leadTimeDays', 'safetyStock', 'minOrderQty', 'orderMultiple'];

export function BulkEditDialog({ codes, rows, source, onClose, onDone }: {
  codes: string[]; rows: MasterItemRow[]; source: DataSource; onClose: () => void; onDone: (n: number, note?: string) => void;
}) {
  const [mode, setMode] = useState<Record<string, Mode>>({});
  const [val, setVal] = useState<Record<string, string>>({});
  const [supLt, setSupLt] = useState('');
  const [busySup, setBusySup] = useState(false);
  const [error, setError] = useState('');
  const bulk = useBulkMasterItems();
  const putSup = useSetSupplierLeadTime(source);
  // Distinct effective suppliers of the selected purchased items; the rest have none to update.
  const withSup = rows.filter((r) => r.procurement === 'Buy' && r.effective.supplier);
  const suppliers = [...new Set(withSup.map((r) => r.effective.supplier as string))];
  // Every item from those suppliers is affected, not only the selected ones.
  const affected = suppliers.reduce((n, s) => n + (withSup.find((r) => r.effective.supplier === s)?.leadTime.supplierItemCount ?? 0), 0);
  const skipped = codes.length - withSup.length;
  const busy = bulk.isPending || busySup;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  const apply = () => {
    const set: Record<string, number | string> = {};
    const clear: OverridableField[] = [];
    for (const f of FIELDS) {
      const m = mode[f] ?? 'keep';
      if (m === 'clear') clear.push(f);
      else if (m === 'set') {
        const v = (val[f] ?? '').trim();
        if (!v) { setError(`Enter a value for ${FIELD_LABEL[f]}, or choose another action.`); return; }
        if (f === 'supplier') set[f] = v;
        else {
          const n = Number(v);
          if (!Number.isFinite(n) || n < 0) { setError(`${FIELD_LABEL[f]} must be a number of 0 or more.`); return; }
          set[f] = n;
        }
      }
    }
    const lt = parseDays(supLt);
    if (supLt.trim() !== '' && lt.error) { setError(`Supplier ${lt.error}`); return; }
    const doSup = supLt.trim() !== '';
    const hasItem = !!Object.keys(set).length || !!clear.length;
    if (!hasItem && !doSup) { setError('Nothing to change: set a value or clear a field.'); return; }
    if (doSup && !suppliers.length) { setError('None of the selected items has a supplier, so the supplier lead time cannot be applied.'); return; }
    setBusySup(true);
    (async () => {
      try {
        let updated = 0;
        if (hasItem) updated = (await bulk.mutateAsync({ itemCodes: codes, set, clear })).updated;
        let note = '';
        if (doSup) {
          for (const s of suppliers) await putSup.mutateAsync({ cardCode: s, leadTimeDays: lt.value });
          note = ` Supplier lead time set for ${suppliers.length} supplier${suppliers.length === 1 ? '' : 's'} (${affected} item${affected === 1 ? '' : 's'} affected)${skipped ? `; ${skipped} item${skipped === 1 ? '' : 's'} skipped (no supplier)` : ''}.`;
        }
        onDone(updated, note);
      } catch (e) { setError((e as Error).message); } finally { setBusySup(false); }
    })();
  };

  return (
    <div className="overlay" onClick={onClose}>
      <div className="dialog" style={{ maxWidth: 600 }} role="dialog" aria-modal="true" aria-labelledby="bk-title" onClick={(e) => e.stopPropagation()}>
        <div className="d-h"><h3 id="bk-title">Bulk edit {codes.length} item{codes.length === 1 ? '' : 's'}</h3></div>
        <div className="d-b">
          <div className="alert info-note">Changes are stored in Cobalt MRP and used by planning. They do not change SAP Business One. Supplier only applies to purchased items.</div>
          {error && <div className="alert danger" role="alert">{error}</div>}
          {FIELDS.map((f) => {
            const m = mode[f] ?? 'keep';
            return (
              <div className="bulk-row" key={f}>
                <label htmlFor={`bk-${f}-m`}>{FIELD_LABEL[f]}</label>
                <select id={`bk-${f}-m`} value={m} onChange={(e) => { setError(''); setMode((x) => ({ ...x, [f]: e.target.value as Mode })); }}>
                  <option value="keep">Leave unchanged</option><option value="set">Set to…</option><option value="clear">Clear override (use SAP B1)</option>
                </select>
                {m === 'set' && (f === 'supplier'
                  ? <div>{val[f] && <span className="chip on removable" style={{ marginBottom: 6 }}><span className="mono">{val[f]}</span>
                      <button type="button" aria-label="Clear chosen supplier" onClick={() => setVal((x) => ({ ...x, [f]: '' }))}>×</button></span>}
                      <SupplierSearch source={source} label="Supplier to set" onPick={(s) => setVal((x) => ({ ...x, [f]: s.cardCode }))} /></div>
                  : <input type="number" min={0} step="any" aria-label={`${FIELD_LABEL[f]} value`} value={val[f] ?? ''} onChange={(e) => setVal((x) => ({ ...x, [f]: e.target.value }))} />)}
              </div>
            );
          })}
          <div className="bulk-row">
            <label htmlFor="bk-suplt">Supplier lead time (days)</label>
            <input id="bk-suplt" type="number" min={0} step="any" placeholder="Leave unchanged" value={supLt} aria-describedby="bk-suplt-h"
              onChange={(e) => { setError(''); setSupLt(e.target.value); }} />
            <span id="bk-suplt-h" className="hint">
              Applied to each distinct supplier of the selected items ({suppliers.length} supplier{suppliers.length === 1 ? '' : 's'}, {affected} item{affected === 1 ? '' : 's'} affected).
              {skipped > 0 && ` ${skipped} selected item${skipped === 1 ? ' has' : 's have'} no supplier and will be skipped.`}
            </span>
          </div>
        </div>
        <div className="d-f">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="button" className="btn primary" onClick={apply} disabled={busy}>{busy ? 'Applying…' : `Apply to ${codes.length}`}</button>
        </div>
      </div>
    </div>
  );
}
