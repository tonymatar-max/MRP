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
        {run.data && loads.length === 0 && (
          <div className="card">
            <div className="card-b">
              <h3 style={{ marginBottom: 8 }}>No capacity load in this run</h3>
              <p className="muted" style={{ marginBottom: 10 }}>
                Capacity settings only say how many hours a work center <em>has</em>. The hours it <em>needs</em> come from planned production orders whose bill of materials has resource lines.
                Settings changes also only apply to runs made after you save them.
              </p>
              <ul className="muted" style={{ paddingInlineStart: 20, margin: 0, display: 'grid', gap: 4 }}>
                <li>
                  Capacity planning in this run: <strong>{run.data.criteria?.capacity?.enabled === false ? 'off (turn it on in the Run MRP wizard, step 4)' : 'on'}</strong>
                </li>
                <li>
                  Planned production orders in this run: <strong>{run.data.plannedOrders.filter((o) => o.type === 'ProductionOrder').length}</strong>
                  {run.data.plannedOrders.some((o) => o.type === 'ProductionOrder') ? '' : ' (no manufactured item needed a new order, so there is nothing to load)'}
                </li>
                <li>
                  In SAP B1, open the Production BOM of each manufactured item and add a <strong>Resource</strong> line with its time. Items without one put no load on any work center.
                </li>
                <li>The resource code on the BOM line must exist in the SAP B1 resource master.</li>
                <li>After fixing any of this, run MRP again.</li>
              </ul>
            </div>
          </div>
        )}
        {run.data && loads.length > 0 && (
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
                        <td className="num">{r.availableHours > 0 ? `${Math.round((r.requiredHours / r.availableHours) * 100)}%` : '—'}</td>
                        <td>{r.availableHours <= 0 ? <Pill tone="danger">No capacity set</Pill> : r.overloaded ? <Pill tone="danger">Overloaded</Pill> : <Pill tone="ok">OK</Pill>}</td>
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
