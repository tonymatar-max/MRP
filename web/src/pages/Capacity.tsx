import { useMemo, useState } from 'react';
import { Bar, BarChart, CartesianGrid, Cell, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useRun } from '../api/hooks';
import { ErrorState, Loading, PageHeader, Pill } from '../components/ui';
import { RunPicker } from '../components/RunPicker';
import { useSelectedRun } from '../components/RunContext';
import { fmtDate, fmtNum } from '../components/util';

export default function Capacity() {
  const { runId } = useSelectedRun();
  const run = useRun(runId);
  const loads = run.data?.capacityLoads ?? [];
  const wcs = useMemo(() => [...new Set(loads.map((l) => l.workCenterCode))], [loads]);
  const [sel, setSel] = useState('');
  const wc = sel || wcs[0] || '';
  const rows = loads.filter((l) => l.workCenterCode === wc).sort((a, b) => a.bucketStart.localeCompare(b.bucketStart));
  const chart = rows.map((r) => ({ ...r, label: fmtDate(r.bucketStart).slice(0, 6) }));

  return (
    <>
      <PageHeader crumb="Planning" title="Capacity (CRP)" actions={<RunPicker />} />
      <div className="page">
        {run.isLoading && <Loading />}
        {run.isError && <ErrorState error={run.error} />}
        {run.data && (
          <>
            <div className="chips" style={{ marginBottom: 14 }}>
              {wcs.map((w) => {
                const over = loads.some((l) => l.workCenterCode === w && l.overloaded);
                return <button key={w} className={`chip ${w === wc ? 'on' : ''}`} onClick={() => setSel(w)}>{w}{over ? ' ⚠' : ''}</button>;
              })}
            </div>
            <div className="card" style={{ marginBottom: 16 }}>
              <div className="card-h">
                <h3>{wc} — required vs available hours</h3>
                <div className="bar-legend">
                  <span><span className="sw" style={{ background: 'var(--accent)' }} />Required</span>
                  <span><span className="sw" style={{ background: 'var(--danger)' }} />Overloaded</span>
                  <span><span className="sw" style={{ background: 'var(--brand-3)' }} />Available</span>
                </div>
              </div>
              <div className="card-b" style={{ height: 320 }}>
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={chart}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--line)" />
                    <XAxis dataKey="label" />
                    <YAxis />
                    <Tooltip />
                    <Legend />
                    <Bar dataKey="availableHours" name="Available" fill="var(--brand-3)" radius={[4, 4, 0, 0]} />
                    <Bar dataKey="requiredHours" name="Required" fill="var(--accent)" radius={[4, 4, 0, 0]}>
                      {chart.map((c, i) => <Cell key={i} fill={c.overloaded ? 'var(--danger)' : 'var(--accent)'} />)}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="card">
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th className="nosort">Bucket</th><th className="nosort num">Required (h)</th><th className="nosort num">Available (h)</th><th className="nosort num">Utilisation</th><th className="nosort">Status</th></tr></thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.bucketStart}>
                        <td>{fmtDate(r.bucketStart)}</td>
                        <td className="num">{fmtNum(r.requiredHours)}</td>
                        <td className="num">{fmtNum(r.availableHours)}</td>
                        <td className="num">{Math.round((r.requiredHours / r.availableHours) * 100)}%</td>
                        <td>{r.overloaded ? <Pill tone="danger">Overloaded</Pill> : <Pill tone="ok">OK</Pill>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </>
        )}
      </div>
    </>
  );
}
