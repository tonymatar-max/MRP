import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useItemGroups } from '../../api/hooks';
import type { DataSource, PlanningMethod, Procurement } from '../../api/types';
import { Check, ChipToggle, StepProps, SupplierMulti } from './parts';
import { PlanningSection } from './PlanningSection';

export const parseCodes = (text: string) => [...new Set(text.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean))];

export function ScopeStep({ c, set }: StepProps) {
  const s = c.scope;
  const wh = useQuery({ queryKey: ['warehouses', c.source], queryFn: () => api.getWarehouses(c.source) });
  const groups = useItemGroups(c.source);
  const [codesText, setCodesText] = useState(s.itemCodes.join('\n'));
  const setScope = (p: Partial<typeof s>) => set((x) => ({ ...x, scope: { ...x.scope, ...p } }));
  // none selected = every warehouse in the lookup
  const effective = s.warehouses.length ? s.warehouses : wh.data ?? [];
  const setWarehouses = (v: string[]) => set((x) => {
    const universe = v.length ? v : wh.data ?? [];
    const src = x.transfers.sourceWarehouses.filter((w) => universe.includes(w));
    return { ...x, scope: { ...x.scope, warehouses: v }, transfers: { ...x.transfers, sourceWarehouses: src } };
  });
  const company = c.level === 'Company';

  return (
    <div className="form-stack">
      <p className="step-intro">Choose which items get planned. The whole bill of materials still drives demand, so components of selected make items are included when you tick the option below.</p>
      <PlanningSection c={c} set={set} warehouses={effective} />
      <div className="field" style={{ maxWidth: 300 }}>
        <label htmlFor="wz-source">Data source</label>
        <select id="wz-source" value={c.source} onChange={(e) => set((x) => ({ ...x, source: e.target.value as DataSource, scope: { ...x.scope, warehouses: [], itemGroups: [], suppliers: [] }, transfers: { ...x.transfers, sourceWarehouses: [] } }))}>
          <option value="mock">Mock sample data</option>
          <option value="b1-sql">SAP B1 (SQL)</option>
        </select>
      </div>
      <div className="field">
        <span className="lbl" id="wz-wh">{company ? 'Warehouses included (stock is pooled)' : 'Warehouses to plan'} <span className="hint">none selected = all</span></span>
        {wh.isError && <span className="hint err">{(wh.error as Error).message}</span>}
        <ChipToggle label={company ? 'Warehouses included' : 'Warehouses to plan'} options={wh.data ?? []} value={s.warehouses} onChange={setWarehouses} />
      </div>
      <div className="field">
        <span className="lbl">Item groups <span className="hint">none selected = all</span></span>
        {groups.isError && <span className="hint err">{(groups.error as Error).message}</span>}
        <ChipToggle label="Item groups" options={(groups.data ?? []).map((g) => g.code)} value={s.itemGroups} onChange={(v) => setScope({ itemGroups: v })}
          labelFor={(code) => { const g = groups.data?.find((x) => x.code === code); return g ? `${g.name} (${g.itemCount})` : code; }} />
      </div>
      <div className="field">
        <span className="lbl">Suppliers <span className="hint">only items whose supplier is one of these; none = all</span></span>
        <SupplierMulti source={c.source} value={s.suppliers} onChange={(v) => setScope({ suppliers: v })} />
      </div>
      <div className="form-row">
        <div className="field">
          <label htmlFor="wz-search">Item search</label>
          <input id="wz-search" value={s.itemSearch} placeholder="Contains, in item code or name" onChange={(e) => setScope({ itemSearch: e.target.value })} />
        </div>
        <div className="field">
          <label htmlFor="wz-proc">Procurement</label>
          <select id="wz-proc" value={s.procurement} onChange={(e) => setScope({ procurement: e.target.value as Procurement })}>
            <option value="Both">Buy and make</option><option value="Buy">Buy only</option><option value="Make">Make only</option>
          </select>
        </div>
      </div>
      <div className="field">
        <label htmlFor="wz-codes">Explicit item codes <span className="hint">paste a list, one per line or separated by commas; empty = no restriction</span></label>
        <textarea id="wz-codes" rows={3} value={codesText} placeholder="FG-PUMP-200&#10;RM-STEEL-SHT"
          onChange={(e) => { setCodesText(e.target.value); setScope({ itemCodes: parseCodes(e.target.value) }); }} />
        {s.itemCodes.length > 0 && <span className="hint">{s.itemCodes.length} code{s.itemCodes.length === 1 ? '' : 's'} recognised</span>}
      </div>
      <div className="field">
        <span className="lbl">Planning methods <span className="hint">none selected = all</span></span>
        <ChipToggle<PlanningMethod> label="Planning methods" options={['Mrp', 'ReorderPoint', 'MinMax']} value={s.planningMethods}
          onChange={(v) => setScope({ planningMethods: v })} labelFor={(m) => (m === 'Mrp' ? 'MRP' : m === 'ReorderPoint' ? 'Reorder point' : 'Min / max')} />
      </div>
      <Check id="wz-comp" label="Also plan components of selected make items" checked={s.includeComponents} onChange={(v) => setScope({ includeComponents: v })}
        hint="Every component below the selected make items is planned too, so their purchases appear." />
    </div>
  );
}
