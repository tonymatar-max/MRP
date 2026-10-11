import { ReactNode, useRef, useState } from 'react';

/** The text input shown while a grid cell is edited: Enter saves, Escape cancels, leaving the cell saves a changed value. */
export function InlineInput({ initial, numeric, label, onSave, onCancel, placeholder }: {
  initial: string; numeric?: boolean; label: string; onSave: (v: string) => void; onCancel: () => void; placeholder?: string;
}) {
  const [v, setV] = useState(initial);
  const done = useRef(false);
  const finish = (save: boolean) => {
    if (done.current) return;
    done.current = true;
    if (save && v !== initial) onSave(v); else onCancel();
  };
  return (
    <input
      className="cell-input" autoFocus aria-label={label} value={v} placeholder={placeholder}
      type={numeric ? 'number' : 'text'} min={numeric ? 0 : undefined} step={numeric ? 'any' : undefined}
      onFocus={(e) => e.currentTarget.select()}
      onChange={(e) => setV(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') { e.preventDefault(); finish(true); }
        else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(false); }
      }}
      onBlur={() => finish(true)}
    />
  );
}

/** A read-only cell that turns into an editor on click or Enter/Space. */
export function CellButton({ children, onClick, label, title, overridden, busy }: {
  children: ReactNode; onClick: () => void; label: string; title?: string; overridden?: boolean; busy?: boolean;
}) {
  return (
    <button type="button" className={`cell-btn ${overridden ? 'ovr' : ''} ${busy ? 'busy' : ''}`} onClick={onClick} title={title} aria-label={label}>
      {children}
    </button>
  );
}
