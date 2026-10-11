import { useEffect, useState } from 'react';
import { useSetSupplierLeadTime } from '../../api/hooks';
import type { DataSource, MasterItemRow } from '../../api/types';
import { fmtVal, parseDays } from './helpers';
import { supplierOf } from './LeadTimeEditor';

/** Item panel section: the supplier's lead time (shared by all its items) with its own Save, separate from the item Save. */
export function SupplierLeadTimeSection({ row, source, onMessage }: { row: MasterItemRow; source: DataSource; onMessage: (ok: boolean, text: string) => void }) {
  const sup = supplierOf(row);
  const current = row.leadTime.supplierDays;
  const [text, setText] = useState(current == null ? '' : String(current));
  const [error, setError] = useState('');
  const put = useSetSupplierLeadTime(source);
  useEffect(() => { setText(current == null ? '' : String(current)); setError(''); }, [row.itemCode, sup.code, current]);

  const save = () => {
    const p = parseDays(text);
    if (p.error || !sup.code) return setError(`Supplier ${p.error ?? 'is required.'}`);
    setError('');
    put.mutate({ cardCode: sup.code, leadTimeDays: p.value }, {
      onSuccess: () => onMessage(true, `Supplier ${sup.code} lead time ${p.value == null ? 'cleared' : `set to ${p.value} d`} for ${sup.count} item${sup.count === 1 ? '' : 's'}.`),
      onError: (e) => setError((e as Error).message),
    });
  };
  const used = row.leadTime.source;
  const cell = (k: 'Supplier' | 'Item' | 'ItemGroup', label: string, v: number | null) => (
    <div className={used === k ? 'win' : ''}>{label}<strong>{v == null ? 'not set' : `${fmtVal(v)} d`}</strong>{used === k && <span className="hint">used</span>}</div>
  );
  const dirty = text !== (current == null ? '' : String(current));

  return (
    <section className="ep-sup" aria-labelledby="ep-sup-h">
      <h4 id="ep-sup-h">Supplier lead time</h4>
      {sup.code ? (
        <>
          <div><span className="mono">{sup.code}</span> {sup.name} · currently <strong>{current == null ? 'not set' : `${current} days`}</strong></div>
          <div className="ep-sup-row">
            <label htmlFor="ep-sup-lt">Lead time (days)</label>
            <input id="ep-sup-lt" type="number" min={0} step="any" value={text} placeholder="not set" onChange={(e) => { setText(e.target.value); setError(''); }} />
            <button type="button" className="btn sm" onClick={save} disabled={put.isPending || !dirty}>{put.isPending ? 'Saving…' : 'Save supplier'}</button>
          </div>
          <span className="hint">Applies to {sup.count} item{sup.count === 1 ? '' : 's'} from this supplier. Empty clears it. Saved separately from the item.</span>
        </>
      ) : <span className="hint">{sup.hint} The supplier lead time can be set once the item has a saved supplier.</span>}
      {error && <div className="alert danger" role="alert">{error}</div>}
      <div className="lt-trio" aria-label="Lead times by level">
        {cell('Supplier', 'Supplier', row.leadTime.supplierDays)}{cell('Item', 'Item', row.leadTime.itemDays)}{cell('ItemGroup', 'Group', row.leadTime.groupDays)}
      </div>
    </section>
  );
}
