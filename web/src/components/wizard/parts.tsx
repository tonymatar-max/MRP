import { ReactNode, useState } from 'react';
import type { DataSource, RunCriteria } from '../../api/types';
import { SupplierSearch } from '../SupplierSearch';

export interface StepProps {
  c: RunCriteria;
  set: (updater: (c: RunCriteria) => RunCriteria) => void;
}

export function Check({ id, label, checked, onChange, hint, disabled }: { id: string; label: string; checked: boolean; onChange: (v: boolean) => void; hint?: ReactNode; disabled?: boolean }) {
  return (
    <div className="check-row">
      <input id={id} type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} aria-describedby={hint ? `${id}-h` : undefined} />
      <label htmlFor={id}>{label}{hint && <span id={`${id}-h`} className="hint">{hint}</span>}</label>
    </div>
  );
}

export function NumField({ id, label, value, onChange, min, max, hint }: { id: string; label: string; value: number; onChange: (n: number) => void; min?: number; max?: number; hint?: ReactNode }) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="number" min={min} max={max} value={Number.isFinite(value) ? value : ''} onChange={(e) => onChange(e.target.value === '' ? NaN : +e.target.value)} aria-describedby={hint ? `${id}-h` : undefined} />
      {hint && <span id={`${id}-h`} className="hint">{hint}</span>}
    </div>
  );
}

export function ChipToggle<T extends string>({ label, options, value, onChange, labelFor }: {
  label: string; options: T[]; value: T[]; onChange: (v: T[]) => void; labelFor?: (o: T) => string;
}) {
  const toggle = (o: T) => onChange(value.includes(o) ? value.filter((x) => x !== o) : [...value, o]);
  return (
    <div role="group" aria-label={label} className="chips">
      {options.map((o) => (
        <button key={o} type="button" className={`chip ${value.includes(o) ? 'on' : ''}`} aria-pressed={value.includes(o)} onClick={() => toggle(o)}>
          {labelFor ? labelFor(o) : o}
        </button>
      ))}
    </div>
  );
}

/** Multi-select of suppliers: chips for the chosen ones plus a type-ahead to add more. */
export function SupplierMulti({ source, value, onChange }: { source: DataSource; value: string[]; onChange: (v: string[]) => void }) {
  const [names, setNames] = useState<Record<string, string>>({});
  return (
    <div className="multi">
      {value.length > 0 && (
        <div className="chips" style={{ marginBottom: 8 }}>
          {value.map((v) => (
            <span key={v} className="chip on removable">
              <span className="mono">{v}</span>{names[v] ? ` ${names[v]}` : ''}
              <button type="button" aria-label={`Remove supplier ${v}`} onClick={() => onChange(value.filter((x) => x !== v))}>×</button>
            </span>
          ))}
        </div>
      )}
      <SupplierSearch source={source} label="Add supplier" placeholder="Type to add a supplier…"
        onPick={(s) => { setNames((n) => ({ ...n, [s.cardCode]: s.cardName })); if (!value.includes(s.cardCode)) onChange([...value, s.cardCode]); }} />
    </div>
  );
}
