import { useLayoutEffect, useRef, useState } from 'react';
import { usePutMasterItem, useSetSupplierLeadTime } from '../../api/hooks';
import type { DataSource, MasterItemRow } from '../../api/types';
import { overrideBody, parseDays } from './helpers';

/** What the supplier lead time field can do for this row, with the reason when it cannot. */
export function supplierOf(row: MasterItemRow) {
  const code = row.procurement === 'Buy' ? row.effective.supplier : null;
  return { code, name: row.effective.supplierName ?? '', count: row.leadTime.supplierItemCount, hint: row.procurement === 'Make' ? 'Make items have no supplier.' : 'Set a supplier first.' };
}

/**
 * Popover opened from the Items grid lead-time cell: edits the item's own lead time and the supplier's lead time
 * (which applies to every item from that supplier, so changing it asks for a second, explicit confirmation).
 */
export function LeadTimeEditor({ row, source, onClose, onFlash }: {
  row: MasterItemRow; source: DataSource; onClose: () => void; onFlash: (ok: boolean, text: string) => void;
}) {
  const sup = supplierOf(row);
  const itemInitial = String(row.effective.leadTimeDays);
  const supInitial = row.leadTime.supplierDays == null ? '' : String(row.leadTime.supplierDays);
  const [item, setItem] = useState(itemInitial);
  const [supV, setSupV] = useState(supInitial);
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState('');
  const putItem = usePutMasterItem();
  const putSup = useSetSupplierLeadTime(source);
  const busy = putItem.isPending || putSup.isPending;
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  // Fixed positioning so the table's scroll container cannot clip the popover.
  useLayoutEffect(() => {
    const anchor = ref.current?.parentElement?.getBoundingClientRect();
    const w = ref.current?.offsetHeight ?? 0;
    if (!anchor) return;
    setPos({ top: Math.max(8, Math.min(anchor.bottom + 4, window.innerHeight - w - 8)), left: Math.max(8, Math.min(anchor.right - 300, window.innerWidth - 316)) });
  }, [confirming]);

  const itemChanged = item !== itemInitial;
  const supChanged = !!sup.code && supV !== supInitial;
  const effName = sup.name || sup.code || '';

  const commit = async () => {
    setError('');
    const iv = parseDays(item);
    const sv = parseDays(supV);
    if (itemChanged && iv.error) return setError(`This item: ${iv.error}`);
    if (supChanged && sv.error) return setError(`Supplier: ${sv.error}`);
    if (!itemChanged && !supChanged) return onClose();
    if (supChanged && !confirming) return setConfirming(true);
    try {
      if (itemChanged) await putItem.mutateAsync({ code: row.itemCode, override: overrideBody(row, { leadTimeDays: iv.value }) });
      if (supChanged && sup.code) await putSup.mutateAsync({ cardCode: sup.code, leadTimeDays: sv.value });
      onFlash(true, `${row.itemCode} lead time saved${supChanged ? `; supplier ${sup.code} updated for ${sup.count} item${sup.count === 1 ? '' : 's'}` : ''}.`);
      onClose();
    } catch (e) {
      setError((e as Error).message);
      setConfirming(false);
    }
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onClose(); }
    else if (e.key === 'Enter' && !(e.target instanceof HTMLButtonElement)) { e.preventDefault(); if (!busy) void commit(); }
  };
  const id = `lt-${row.itemCode}`;

  return (
    <div ref={ref} className="lt-pop" role="dialog" aria-label={`Lead time for ${row.itemCode}`} onKeyDown={onKey}
      style={pos ? { top: pos.top, left: pos.left } : { visibility: 'hidden', top: 0, left: 0 }}>
      <div className="field">
        <label htmlFor={`${id}-item`}>This item (override)</label>
        <input id={`${id}-item`} className="lt-in" type="number" min={0} step="any" autoFocus value={item}
          onFocus={(e) => e.currentTarget.select()} onChange={(e) => { setItem(e.target.value); setConfirming(false); }} />
        <span className="hint">Empty reverts to SAP B1.</span>
      </div>
      <div className="field">
        <label htmlFor={`${id}-sup`}>{sup.code ? `Supplier ${sup.code} lead time` : 'Supplier lead time'}</label>
        <input id={`${id}-sup`} className="lt-in" type="number" min={0} step="any" disabled={!sup.code} aria-describedby={`${id}-sup-h`}
          value={sup.code ? supV : ''} onChange={(e) => { setSupV(e.target.value); setConfirming(false); }} />
        <span id={`${id}-sup-h`} className="hint">
          {sup.code ? `Applies to ${sup.count} item${sup.count === 1 ? '' : 's'}. Empty clears it.` : sup.hint}
        </span>
      </div>
      {confirming && <div className="alert info-note" role="alert">This changes the lead time for all {sup.count} item{sup.count === 1 ? '' : 's'} from {effName}. Press Enter or Confirm to save.</div>}
      {error && <div className="alert danger" role="alert">{error}</div>}
      <div className="lt-actions">
        <button type="button" className="btn sm" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="button" className="btn primary sm" onClick={() => void commit()} disabled={busy}>{busy ? 'Saving…' : confirming ? 'Confirm' : 'Save'}</button>
      </div>
    </div>
  );
}
