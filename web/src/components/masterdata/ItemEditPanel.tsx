import { useEffect, useState } from 'react';
import { usePutMasterItem, useResetMasterItem } from '../../api/hooks';
import type { DataSource, ItemPlanningMethod, LotSizingRule, MasterItemRow, OverridableField } from '../../api/types';
import { SupplierSearch } from '../SupplierSearch';
import { SupplierLeadTimeSection } from './SupplierLeadTimeSection';
import { ALL_FIELDS, FIELD_LABEL, fmtVal, LOT_LABEL, LT_SOURCE_LABEL, METHOD_LABEL, NUMERIC_FIELDS } from './helpers';

type Draft = Record<OverridableField, string>;

const draftOf = (row: MasterItemRow): Draft => {
  const d = {} as Draft;
  for (const f of ALL_FIELDS) {
    const v = row.override ? (row.override as unknown as Record<string, unknown>)[f] : null;
    d[f] = v === null || v === undefined ? '' : String(v);
  }
  return d;
};

/** Right-hand panel with every field: B1 value next to each input, per-field revert, notes and Reset to SAP B1. */
export function ItemEditPanel({ row, source, onClose, onMessage }: {
  row: MasterItemRow; source: DataSource; onClose: () => void; onMessage: (ok: boolean, text: string) => void;
}) {
  const [d, setD] = useState<Draft>(() => draftOf(row));
  const [notes, setNotes] = useState(row.override?.notes ?? '');
  const [supName, setSupName] = useState('');
  const [error, setError] = useState('');
  const put = usePutMasterItem();
  const reset = useResetMasterItem();
  const busy = put.isPending || reset.isPending;
  useEffect(() => { setD(draftOf(row)); setNotes(row.override?.notes ?? ''); }, [row.itemCode]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  const b1 = row.b1 as unknown as Record<string, unknown>;
  const setF = (f: OverridableField, v: string) => { setError(''); setD((x) => ({ ...x, [f]: v })); };

  const save = () => {
    const body: Record<string, unknown> = { notes };
    for (const f of ALL_FIELDS) {
      const v = d[f].trim();
      if (v === '') continue;
      if (NUMERIC_FIELDS.includes(f)) {
        const n = Number(v);
        if (!Number.isFinite(n) || n < 0) { setError(`${FIELD_LABEL[f]} must be a number of 0 or more.`); return; }
        body[f] = n;
      } else body[f] = v;
    }
    put.mutate({ code: row.itemCode, override: body }, {
      onSuccess: () => { onMessage(true, `${row.itemCode} saved.`); onClose(); },
      onError: (e) => setError((e as Error).message),
    });
  };
  const resetAll = () => reset.mutate(row.itemCode, {
    onSuccess: () => { onMessage(true, `${row.itemCode} reset to SAP B1 values.`); onClose(); },
    onError: (e) => setError((e as Error).message),
  });

  const line = (f: OverridableField, input: React.ReactNode, b1Text: string) => (
    <div className={`ep-row ${d[f] !== '' ? 'ovr' : ''}`} key={f}>
      <div className="ep-label"><label htmlFor={`ep-${f}`}>{FIELD_LABEL[f]}</label></div>
      <div className="ep-input">{input}</div>
      <div className="ep-b1"><span className="hint">SAP B1: {b1Text}</span>
        <button type="button" className="btn ghost sm" disabled={d[f] === ''} onClick={() => setF(f, '')} aria-label={`Revert ${FIELD_LABEL[f]} to SAP B1`}>Revert</button></div>
    </div>
  );

  return (
    <div className="overlay drawer-overlay" onClick={onClose}>
      <aside className="drawer" role="dialog" aria-modal="true" aria-labelledby="ep-title" onClick={(e) => e.stopPropagation()}>
        <div className="d-h">
          <h3 id="ep-title"><span className="mono">{row.itemCode}</span> {row.itemName}</h3>
          <div className="hint">{row.groupName} · {row.procurement === 'Buy' ? 'Purchased' : 'Made in-house'}</div>
        </div>
        <div className="d-b ep-body">
          <div className="alert info-note">Overrides are stored in Cobalt MRP and used by planning. They do not change SAP Business One. Leave a field empty to use the SAP B1 value.</div>
          <div className="ep-lt">Effective lead time: <strong>{row.leadTime.days == null ? 'none' : `${row.leadTime.days} days`}</strong> <span className={`lt-badge ${row.leadTime.source}`}>{LT_SOURCE_LABEL[row.leadTime.source]}</span>
            <span className="hint"> supplier {fmtVal(row.leadTime.supplierDays)}, item {fmtVal(row.leadTime.itemDays)}, group {fmtVal(row.leadTime.groupDays)} (the {LT_SOURCE_LABEL[row.leadTime.source].toLowerCase()} value is used)</span></div>
          {error && <div className="alert danger" role="alert">{error}</div>}
          {row.procurement === 'Buy' && line('supplier',
            <div>
              {d.supplier && <span className="chip on removable" style={{ marginBottom: 6 }}><span className="mono">{d.supplier}</span>{supName ? ` ${supName}` : ''}
                <button type="button" aria-label="Clear supplier override" onClick={() => setF('supplier', '')}>×</button></span>}
              <SupplierSearch id="ep-supplier" source={source} placeholder={`Use SAP B1 (${row.b1.supplier ?? 'none'})`} onPick={(s) => { setSupName(s.cardName); setF('supplier', s.cardCode); }} />
            </div>, row.b1.supplier ? `${row.b1.supplier}${row.b1.supplierName ? ` ${row.b1.supplierName}` : ''}` : 'none')}
          {row.procurement === 'Buy' && <SupplierLeadTimeSection row={row} source={source} onMessage={onMessage} />}
          {(['leadTimeDays', 'minOrderQty', 'orderMultiple', 'safetyStock', 'reorderPoint', 'maxStockLevel'] as OverridableField[]).map((f) =>
            line(f, <input id={`ep-${f}`} type="number" min={0} step="any" value={d[f]} placeholder={fmtVal(b1[f])} onChange={(e) => setF(f, e.target.value)} />, fmtVal(b1[f])))}
          {line('planningMethod',
            <select id="ep-planningMethod" value={d.planningMethod} onChange={(e) => setF('planningMethod', e.target.value)}>
              <option value="">Use SAP B1 ({METHOD_LABEL[row.b1.planningMethod]})</option>
              {(['Mrp', 'ReorderPoint', 'MinMax', 'None'] as ItemPlanningMethod[]).map((m) => <option key={m} value={m}>{METHOD_LABEL[m]}</option>)}
            </select>, METHOD_LABEL[row.b1.planningMethod])}
          {line('lotSizing',
            <select id="ep-lotSizing" value={d.lotSizing} onChange={(e) => setF('lotSizing', e.target.value)}>
              <option value="">Use SAP B1 ({LOT_LABEL[row.b1.lotSizing]})</option>
              {(['LotForLot', 'FixedQuantity', 'MinMaxMultiple', 'PeriodOfSupply'] as LotSizingRule[]).map((m) => <option key={m} value={m}>{LOT_LABEL[m]}</option>)}
            </select>, LOT_LABEL[row.b1.lotSizing])}
          {(['fixedLotSize', 'periodOfSupplyDays'] as OverridableField[]).map((f) =>
            line(f, <input id={`ep-${f}`} type="number" min={0} step="any" value={d[f]} placeholder={fmtVal(b1[f])} onChange={(e) => setF(f, e.target.value)} />, fmtVal(b1[f])))}
          <div className="field">
            <label htmlFor="ep-notes">Notes</label>
            <textarea id="ep-notes" rows={2} value={notes} maxLength={500} onChange={(e) => setNotes(e.target.value)} placeholder="Why is this overridden?" />
            {row.override?.updatedAt && <span className="hint">Last changed by {row.override.updatedBy ?? 'unknown'} on {new Date(row.override.updatedAt).toLocaleString()}</span>}
          </div>
        </div>
        <div className="d-f">
          <button type="button" className="btn danger" disabled={busy || !row.override} onClick={resetAll}>Reset to SAP B1</button>
          <span className="spacer" />
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="button" className="btn primary" onClick={save} disabled={busy}>{put.isPending ? 'Saving…' : 'Save'}</button>
        </div>
      </aside>
    </div>
  );
}
