import { useRuns } from '../api/hooks';
import { useSelectedRun } from './RunContext';
import { useT } from '../i18n';
import { fmtDateTime } from './util';

export function RunPicker() {
  const tr = useT();
  const { data: runs } = useRuns();
  const { runId, setRunId } = useSelectedRun();
  if (!runs || runs.length === 0) return null;
  return (
    <select value={runId ?? ''} onChange={(e) => setRunId(e.target.value)} title={tr('Select MRP run')} aria-label={tr('Select MRP run')}>
      {runs.map((r) => (
        <option key={r.id} value={r.id}>
          {[fmtDateTime(r.runAt), r.runBy, tr('{n} orders', { n: r.plannedOrderCount }), r.notes ? (r.notes.length > 40 ? `${r.notes.slice(0, 40)}…` : r.notes) : '']
            .filter(Boolean).join(' · ')}
        </option>
      ))}
    </select>
  );
}
