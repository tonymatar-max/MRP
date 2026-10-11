import { useEffect, useId, useRef, useState } from 'react';
import { useSupplierLookup } from '../api/hooks';
import type { DataSource, SupplierLookup } from '../api/types';
import { useDebounced } from './useDebounced';

/** Type-ahead over GET /lookups/suppliers (debounced). Calls onPick with the chosen business partner. */
export function SupplierSearch({
  source, onPick, onCancel, placeholder = 'Search supplier code or name…', label, autoFocus, id,
}: {
  source: DataSource; onPick: (s: SupplierLookup) => void; onCancel?: () => void;
  placeholder?: string; label?: string; autoFocus?: boolean; id?: string;
}) {
  const auto = useId();
  const inputId = id ?? auto;
  const [text, setText] = useState('');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const debounced = useDebounced(text, 250);
  const q = useSupplierLookup(source, debounced.trim(), open);
  const rows = q.data ?? [];
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => { if (autoFocus) ref.current?.focus(); }, [autoFocus]);
  useEffect(() => { setActive(-1); }, [debounced]);

  const pick = (s: SupplierLookup) => { onPick(s); setText(''); setOpen(false); };
  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive((a) => Math.min(rows.length - 1, a + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(0, a - 1)); }
    else if (e.key === 'Enter') {
      e.preventDefault();
      const r = rows[active >= 0 ? active : rows.length === 1 ? 0 : -1];
      if (r) pick(r);
    } else if (e.key === 'Escape') { if (open) { e.stopPropagation(); setOpen(false); } onCancel?.(); }
  };

  return (
    <div className="typeahead">
      {label && <label htmlFor={inputId} className="sr-only">{label}</label>}
      <input
        id={inputId} ref={ref} role="combobox" aria-expanded={open} aria-controls={`${inputId}-list`} aria-autocomplete="list"
        aria-activedescendant={active >= 0 ? `${inputId}-o${active}` : undefined}
        autoComplete="off" placeholder={placeholder} value={text}
        onChange={(e) => { setText(e.target.value); setOpen(true); }}
        onFocus={() => setOpen(true)} onBlur={() => setOpen(false)} onKeyDown={onKey}
      />
      {open && (
        <ul id={`${inputId}-list`} role="listbox" className="typeahead-list">
          {q.isFetching && !rows.length && <li className="typeahead-empty">Searching…</li>}
          {q.isError && <li className="typeahead-empty err">{(q.error as Error).message}</li>}
          {!q.isFetching && !q.isError && !rows.length && <li className="typeahead-empty">No matching supplier</li>}
          {rows.map((r, i) => (
            <li key={r.cardCode} id={`${inputId}-o${i}`} role="option" aria-selected={i === active}
              className={i === active ? 'active' : ''} onMouseDown={(e) => { e.preventDefault(); pick(r); }}>
              <span className="mono">{r.cardCode}</span> {r.cardName}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
