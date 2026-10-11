import { KeyboardEvent, useEffect, useId, useRef, useState } from 'react';
import { ApiError } from '../api/client';
import { useExecuteException, useExecutePreview } from '../api/hooks';
import type { ExceptionAction, ExceptionMessage, ExecutePreview } from '../api/types';
import { ExceptionPill, Loading } from './ui';

export const ACTION_LABEL: Record<ExceptionAction, string> = {
  ReleaseOrder: 'Release the planned order to SAP B1',
  MoveDueDate: 'Move the due date',
  Close: 'Close the SAP B1 order',
};

const localToday = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
const humanize = (k: string) => k.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase());
const show = (v: string | number | null | undefined) => (v === null || v === undefined || v === '' ? '—' : String(v));

function CompareTable({ p }: { p: ExecutePreview }) {
  // Either side can be absent (a ReleaseOrder has no current state, a Close may have no proposed one): never assume an object.
  const current: Record<string, string | number | null | undefined> = p.current ?? {};
  const proposed: Record<string, string | number | null | undefined> = p.proposed ?? {};
  const keys = Array.from(new Set([...Object.keys(current), ...Object.keys(proposed)]));
  if (!keys.length) return null;
  return (
    <table className="data" aria-label="Current versus proposed">
      <thead><tr><th className="nosort">Field</th><th className="nosort">Current</th><th className="nosort">After execute</th></tr></thead>
      <tbody>
        {keys.map((k) => {
          const changed = k in proposed && show(current[k]) !== show(proposed[k]);
          return (
            <tr key={k}>
              <td>{humanize(k)}</td>
              <td>{show(current[k])}</td>
              <td style={changed ? { fontWeight: 700 } : undefined}>{k in p.proposed ? show(p.proposed[k]) : '—'}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

export function ExecuteDialog({ runId, exception: ex, onClose }: { runId: string; exception: ExceptionMessage; onClose: () => void }) {
  const actions = ex.actions ?? [];
  const today = localToday();
  const startDate = ex.proposedDate && ex.proposedDate >= today ? ex.proposedDate : today;
  const [action, setAction] = useState<ExceptionAction>(actions[0]);
  const [newDate, setNewDate] = useState(startDate);
  const [note, setNote] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const uid = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const exec = useExecuteException(runId);

  const dateOk = action !== 'MoveDueDate' || (/^\d{4}-\d{2}-\d{2}$/.test(newDate) && newDate >= today);
  const preview = useExecutePreview(runId, ex.id, { action, newDate: action === 'MoveDueDate' ? newDate : undefined }, dateOk);
  const p = preview.data;
  const blockers = p?.blockers ?? [];
  const done = exec.isSuccess;
  const previewFresh = !!p && !preview.isFetching && p.action === action;
  const canRun = dateOk && previewFresh && p!.canExecute && blockers.length === 0 && confirmed && !exec.isPending && !done;

  // Focus the dialog on open, restore focus to the opener on close.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    dialogRef.current?.querySelector<HTMLElement>('input, textarea, button')?.focus();
    return () => opener?.focus?.();
  }, []);

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape' && !exec.isPending) { e.stopPropagation(); onClose(); return; }
    if (e.key !== 'Tab') return;
    const f = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>('input:not(:disabled), textarea, button:not(:disabled)') ?? []);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  };

  const run = () => {
    if (!canRun) return;
    exec.mutate({ id: ex.id, body: { action, newDate: action === 'MoveDueDate' ? newDate : undefined, note: note.trim(), confirm: true } });
  };

  const err = exec.error as Error | null;
  const conflict = err instanceof ApiError && err.status === 409;

  return (
    <div className="overlay" onClick={() => !exec.isPending && onClose()}>
      <div ref={dialogRef} className="dialog" style={{ maxWidth: 640, maxHeight: 'calc(100vh - 40px)', display: 'flex', flexDirection: 'column' }}
        role="dialog" aria-modal="true" aria-labelledby={`${uid}-t`} onClick={(e) => e.stopPropagation()} onKeyDown={onKeyDown}>
        <div className="d-h">
          <h3 id={`${uid}-t`}>Execute in SAP Business One</h3>
          <div className="muted" style={{ marginTop: 4, fontSize: 13 }}>
            <ExceptionPill type={ex.type} /> <span className="mono">{ex.itemCode}</span> · {ex.warehouseCode}
          </div>
        </div>
        <div className="d-b" style={{ overflowY: 'auto' }}>
          {done ? (
            <div className="alert ok" role="status"><strong>Done.</strong> {exec.data!.message}</div>
          ) : (
            <>
              {actions.length > 1 && (
                <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
                  <legend className="lbl" style={{ padding: 0, marginBottom: 6 }}>Action</legend>
                  {actions.map((a) => (
                    <label key={a} style={{ display: 'flex', gap: 8, alignItems: 'center', padding: '3px 0', fontSize: 13 }}>
                      <input type="radio" name={`${uid}-a`} checked={action === a} onChange={() => { setAction(a); setConfirmed(false); }} />
                      {ACTION_LABEL[a]}
                    </label>
                  ))}
                </fieldset>
              )}
              {action === 'MoveDueDate' && (
                <div className="field">
                  <label htmlFor={`${uid}-d`}>New due date</label>
                  <input id={`${uid}-d`} type="date" min={today} value={newDate} aria-invalid={!dateOk}
                    onChange={(e) => { setNewDate(e.target.value); setConfirmed(false); }} style={{ maxWidth: 200 }} />
                  {!dateOk && <span className="hint err" role="alert" style={{ color: 'var(--danger)', fontSize: 12 }}>Pick a date that is not before today.</span>}
                </div>
              )}
              <div className="field">
                <label htmlFor={`${uid}-n`}>Note (optional)</label>
                <textarea id={`${uid}-n`} rows={2} value={note} maxLength={500} placeholder="e.g. agreed with supplier" onChange={(e) => setNote(e.target.value)} />
              </div>

              <div aria-live="polite" aria-busy={preview.isFetching}>
                {dateOk && preview.isLoading && <Loading label="Checking SAP Business One…" />}
                {preview.isError && <div className="alert danger" role="alert">{(preview.error as Error).message}</div>}
                {p && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 10, opacity: preview.isFetching ? 0.6 : 1 }}>
                    <div><strong>{p.title}</strong><div className="muted">{p.summary}</div></div>
                    <div className="table-wrap"><CompareTable p={p} /></div>
                    {p.warnings.map((w, i) => <div key={`w${i}`} className="alert warn"><strong>Warning:</strong> {w}</div>)}
                    {p.blockers.map((b, i) => <div key={`b${i}`} className="alert danger" role="alert"><strong>Cannot execute:</strong> {b}</div>)}
                  </div>
                )}
              </div>

              <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', fontSize: 13, fontWeight: 600 }}>
                <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} style={{ marginTop: 2 }} />
                I understand this changes SAP Business One
              </label>

              {exec.isError && (
                <div className="alert danger" role="alert">
                  {err!.message}
                  {conflict && <div style={{ marginTop: 6 }}><strong>Re-run MRP</strong> to refresh the plan, then try again: the SAP Business One document or this exception has changed since the plan was made.</div>}
                </div>
              )}
            </>
          )}
        </div>
        <div className="d-f">
          {done ? (
            <button type="button" className="btn primary" onClick={onClose}>Close</button>
          ) : (
            <>
              <button type="button" className="btn" onClick={onClose} disabled={exec.isPending}>Cancel</button>
              <button type="button" className="btn primary" onClick={run} disabled={!canRun}>
                {exec.isPending ? 'Executing…' : 'Execute'}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
