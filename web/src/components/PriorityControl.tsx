import { useEffect, useRef } from 'react';
import type { LeadTimeLevel, LeadTimeMode } from '../api/types';

export const LEVEL_LABEL: Record<LeadTimeLevel, string> = { Supplier: 'Supplier', Item: 'Item', ItemGroup: 'Item group' };
const LEVEL_HELP: Record<LeadTimeLevel, string> = {
  Supplier: 'Lead time entered for the item’s supplier (Master data > Suppliers)',
  Item: 'The item’s own lead time (master data override, else SAP B1)',
  ItemGroup: 'The SAP B1 item group lead time',
};

const EXAMPLE: Record<LeadTimeLevel, number> = { Supplier: 21, Item: 7, ItemGroup: 5 };
export function workedExample(priority: LeadTimeLevel[], mode: LeadTimeMode) {
  const win = mode === 'Longest' ? priority.reduce((a, c) => (EXAMPLE[c] > EXAMPLE[a] ? c : a)) : priority[0];
  return { days: EXAMPLE[win], winner: win };
}

/** Reorder control for the lead-time priority (up/down buttons, keyboard operable) with mode toggle and a worked example. */
export function PriorityControl({
  idPrefix, priority, mode, onPriority, onMode,
}: {
  idPrefix: string; priority: LeadTimeLevel[]; mode: LeadTimeMode;
  onPriority: (p: LeadTimeLevel[]) => void; onMode: (m: LeadTimeMode) => void;
}) {
  const focusNext = useRef<string | null>(null);
  useEffect(() => {
    if (!focusNext.current) return;
    document.getElementById(focusNext.current)?.focus();
    focusNext.current = null;
  }, [priority]);

  const move = (i: number, d: -1 | 1) => {
    const j = i + d;
    if (j < 0 || j >= priority.length) return;
    const next = [...priority];
    [next[i], next[j]] = [next[j], next[i]];
    const l = priority[i];
    // keep focus on the same button, or on its twin when the item reaches an edge
    const edge = j === 0 || j === priority.length - 1;
    focusNext.current = `${idPrefix}-${l}-${edge ? (d < 0 ? 'down' : 'up') : d < 0 ? 'up' : 'down'}`;
    onPriority(next);
  };
  const ex = workedExample(priority, mode);
  const exText = priority.map((l) => `${LEVEL_LABEL[l].toLowerCase()} ${EXAMPLE[l]}d`).join(', ');

  return (
    <div className="priority">
      <div className="priority-lists">
        <div>
          <div id={`${idPrefix}-label`} className="section-title">Which lead time wins (top = highest priority)</div>
          <ol className="priority-list" aria-labelledby={`${idPrefix}-label`}>
            {priority.map((l, i) => (
              <li key={l}>
                <span className="rank" aria-hidden="true">{i + 1}</span>
                <span className="pl-text"><strong>{LEVEL_LABEL[l]}</strong><span className="hint">{LEVEL_HELP[l]}</span></span>
                <button type="button" className="btn sm" id={`${idPrefix}-${l}-up`} disabled={i === 0}
                  aria-label={`Move ${LEVEL_LABEL[l]} up (currently position ${i + 1} of ${priority.length})`} onClick={() => move(i, -1)}>▲</button>
                <button type="button" className="btn sm" id={`${idPrefix}-${l}-down`} disabled={i === priority.length - 1}
                  aria-label={`Move ${LEVEL_LABEL[l]} down (currently position ${i + 1} of ${priority.length})`} onClick={() => move(i, 1)}>▼</button>
              </li>
            ))}
          </ol>
        </div>
        <fieldset className="mode-set">
          <legend className="section-title">Mode</legend>
          <label className="radio-row"><input type="radio" name={`${idPrefix}-mode`} checked={mode === 'FirstAvailable'} onChange={() => onMode('FirstAvailable')} />
            <span><strong>First available</strong><span className="hint">Use the first level in the order above that has a value.</span></span></label>
          <label className="radio-row"><input type="radio" name={`${idPrefix}-mode`} checked={mode === 'Longest'} onChange={() => onMode('Longest')} />
            <span><strong>Longest</strong><span className="hint">Use the longest of the lead times found (safest).</span></span></label>
        </fieldset>
      </div>
      <p className="example" aria-live="polite">
        <strong>Example:</strong> {exText} &rarr; <strong>{ex.days} days</strong> ({LEVEL_LABEL[ex.winner].toLowerCase()} {mode === 'Longest' ? 'is the longest' : 'comes first'}).
        Make items have no supplier, so Supplier is skipped for them.
      </p>
    </div>
  );
}
