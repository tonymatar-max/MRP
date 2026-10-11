import { useRuns } from '../api/hooks';
import { useSelectedRun } from './RunContext';
import { fmtDateTime } from './util';

export function RunPicker() {
  const { data: runs } = useRuns();
  const { runId, setRunId } = useSelectedRun();
  if (!runs || runs.length === 0) return null;
  return (
    <select value={runId ?? ''} onChange={(e) => setRunId(e.target.value)} title="Select MRP run">
      {runs.map((r) => (
        <option key={r.id} value={r.id}>
          {r.id} — {fmtDateTime(r.runAt)}
        </option>
      ))}
    </select>
  );
}
