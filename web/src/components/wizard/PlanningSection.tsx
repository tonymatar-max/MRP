import type { PlanningLevel, PurchaseDocument } from '../../api/types';
import { Check, ChipToggle, NumField, StepProps } from './parts';

interface CardOption<T extends string> { value: T; title: string; text: string }

/** A group of large radio cards. Native radios, so arrow keys and the label click work. */
function RadioCards<T extends string>({ legend, name, options, value, onChange }: {
  legend: string; name: string; options: CardOption<T>[]; value: T; onChange: (v: T) => void;
}) {
  return (
    <fieldset className="radio-cards">
      <legend className="lbl">{legend}</legend>
      <div className="radio-cards-grid">
        {options.map((o) => (
          <label key={o.value} className={`radio-card ${value === o.value ? 'on' : ''}`}>
            <input type="radio" name={name} value={o.value} checked={value === o.value} onChange={() => onChange(o.value)} />
            <span><strong>{o.title}</strong><span className="hint">{o.text}</span></span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}

const LEVELS: CardOption<PlanningLevel>[] = [
  { value: 'Company', title: 'Company level', text: 'Pool the stock of the selected warehouses and plan each item once. Creates purchase orders or purchase requests. No transfers.' },
  { value: 'Warehouse', title: 'Warehouse level', text: 'Plan each warehouse separately. Can transfer stock between the selected warehouses before buying.' },
];
const DOCS: CardOption<PurchaseDocument>[] = [
  { value: 'Order', title: 'Purchase order', text: 'Planned purchases are released as purchase orders to a vendor.' },
  { value: 'Request', title: 'Purchase request', text: 'Planned purchases are released as purchase requests; no vendor needed yet.' },
];

/** `warehouses` is the effective list: the selected ones, or the whole lookup when none are selected. */
export function PlanningSection({ c, set, warehouses }: StepProps & { warehouses: string[] }) {
  const t = c.transfers;
  const company = c.level === 'Company';
  const canTransfer = warehouses.length >= 2;
  const setT = (p: Partial<typeof t>) => set((x) => ({ ...x, transfers: { ...x.transfers, ...p } }));
  const sources = t.sourceWarehouses.length ? t.sourceWarehouses : warehouses;
  const toggleSource = (v: string[]) => {
    if (!v.length) return; // at least one supplying warehouse; empty in the payload would mean "all"
    setT({ sourceWarehouses: v.length === warehouses.length ? [] : v });
  };

  return (
    <div className="form-stack">
      <RadioCards legend="Planning level" name="wz-level" options={LEVELS} value={c.level}
        onChange={(v) => set((x) => ({ ...x, level: v }))} />
      <RadioCards legend="Purchasing document" name="wz-doc" options={DOCS} value={c.purchasing.document}
        onChange={(v) => set((x) => ({ ...x, purchasing: { document: v } }))} />
      {!company && (
        <div className="wz-group">
          <Check id="wz-tr" label="Create transfer requests when another warehouse has the quantity"
            checked={t.enabled} disabled={!canTransfer && !t.enabled} onChange={(v) => setT({ enabled: v })}
            hint={canTransfer
              ? 'A warehouse that needs an item is supplied from another selected warehouse with surplus stock before anything is bought or produced.'
              : undefined} />
          {!canTransfer && (
            <div className="alert info-note" role="note">Transfers need at least two warehouses. Select two or more under Warehouses to plan{t.enabled ? ', or turn transfers off' : ''}.</div>
          )}
          {t.enabled && (
            <div className="form-row sub">
              <div className="field">
                <span className="lbl" id="wz-trsrc">Transfer from <span className="hint">warehouses that may supply a transfer</span></span>
                <ChipToggle label="Transfer from" options={warehouses} value={sources} onChange={toggleSource} />
              </div>
              <div style={{ maxWidth: 180 }}>
                <NumField id="wz-trdays" label="Transfer time (days)" min={0} max={90} value={t.leadTimeDays} onChange={(n) => setT({ leadTimeDays: n })}
                  hint="The request goes out this many days before it is needed." />
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
