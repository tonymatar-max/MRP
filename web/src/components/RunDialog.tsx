import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useStatus, useTriggerRun } from '../api/hooks';
import { api } from '../api/client';
import type { DataSource } from '../api/types';
import { Dialog } from './ui';
import { useSelectedRun } from './RunContext';

export function RunDialog({ onClose }: { onClose: () => void }) {
  const status = useStatus().data;
  const trigger = useTriggerRun();
  const { setRunId } = useSelectedRun();
  const [horizonDays, setHorizonDays] = useState(status?.horizonDays ?? 60);
  const [bucketDays, setBucketDays] = useState(status?.bucketDays ?? 7);
  const [source, setSource] = useState<DataSource>(status?.defaultSource ?? 'mock');
  const [warehouses, setWarehouses] = useState<string[]>([]); // empty = all warehouses
  const whQuery = useQuery({ queryKey: ['warehouses', source], queryFn: () => api.getWarehouses(source) });

  const toggle = (w: string) =>
    setWarehouses((cur) => (cur.includes(w) ? cur.filter((x) => x !== w) : [...cur, w]));

  const run = () =>
    trigger.mutate(
      { horizonDays, bucketDays, source, warehouses },
      {
        onSuccess: (r) => {
          setRunId(r.id);
          onClose();
        },
      },
    );

  return (
    <Dialog
      title="Run MRP"
      onClose={onClose}
      footer={
        <>
          <button className="btn" onClick={onClose}>Cancel</button>
          <button className="btn primary" disabled={trigger.isPending} onClick={run}>
            {trigger.isPending ? 'Running…' : 'Run MRP'}
          </button>
        </>
      }
    >
      <div className="form-row">
        <div className="field">
          <label>Horizon (days)</label>
          <input type="number" min={7} max={365} value={horizonDays} onChange={(e) => setHorizonDays(+e.target.value)} />
        </div>
        <div className="field">
          <label>Bucket size</label>
          <select value={bucketDays} onChange={(e) => setBucketDays(+e.target.value)}>
            <option value={1}>Daily</option>
            <option value={7}>Weekly</option>
            <option value={30}>Monthly</option>
          </select>
        </div>
      </div>
      <div className="field">
        <label>Data source</label>
        <select value={source} onChange={(e) => setSource(e.target.value as DataSource)}>
          <option value="mock">Mock sample data</option>
          <option value="b1-sql">SAP B1 (SQL)</option>
        </select>
      </div>
      <div className="field">
        <label>Warehouses (none selected = all)</label>
        <div className="chips">
          {(whQuery.data ?? []).map((w) => (
            <button key={w} type="button" className={`chip ${warehouses.includes(w) ? 'on' : ''}`} onClick={() => toggle(w)}>
              {w}
            </button>
          ))}
        </div>
      </div>
      {trigger.isError && <div style={{ color: 'var(--danger)' }}>{(trigger.error as Error).message}</div>}
    </Dialog>
  );
}
