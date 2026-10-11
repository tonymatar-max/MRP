import { useState } from 'react';
import { useRun, useRuns } from '../api/hooks';
import { Empty, ErrorState, Kpi, Loading, PageHeader } from '../components/ui';
import { RunDialog } from '../components/RunDialog';
import { RunWizard } from '../components/wizard/RunWizard';
import { CriteriaPanel } from '../components/CriteriaPanel';
import { RunPicker } from '../components/RunPicker';
import { AskAiButton, SummariseButton, SummaryCard, useRunSummary } from '../components/AiAssistant';
import { useSelectedRun } from '../components/RunContext';
import { EXCEPTION_META, fmtDate, fmtDateTime } from '../components/util';
import type { ExceptionType, RunCriteria } from '../api/types';

export default function Dashboard() {
  const [wizard, setWizard] = useState<{ initial: RunCriteria | null } | null>(null);
  const [quick, setQuick] = useState(false);
  const { runId, setRunId } = useSelectedRun();
  const runs = useRuns();
  const run = useRun(runId);
  const summary = useRunSummary(runId);

  const d = run.data;
  const count = (t: string) => d?.plannedOrders.filter((p) => p.type === t).length ?? 0;
  const pos = count('PurchaseOrder');
  const prs = count('PurchaseRequest');
  const trs = count('TransferRequest');
  const prods = d?.plannedOrders.filter((p) => p.type === 'ProductionOrder').length ?? 0;
  const overloaded = new Set(d?.capacityLoads.filter((c) => c.overloaded).map((c) => c.workCenterCode)).size;
  const byType = (Object.keys(EXCEPTION_META) as ExceptionType[]).map((t) => ({
    t,
    n: d?.exceptions.filter((e) => e.type === t).length ?? 0,
  }));

  return (
    <>
      <PageHeader
        crumb="Overview"
        title="Dashboard"
        actions={<><RunPicker /><SummariseButton onClick={() => void summary.go()} busy={summary.busy} /><AskAiButton /><button type="button" className="btn ghost sm" onClick={() => setQuick(true)}>Quick run</button><button type="button" className="btn primary" onClick={() => setWizard({ initial: null })}>Run MRP</button></>}
      />
      <div className="page">
        {runs.data && runs.data.length === 0 && (
          <div className="card"><Empty title="No MRP runs yet">Click <strong>Run MRP</strong> to plan your first run.</Empty></div>
        )}
        {run.isLoading && <Loading />}
        {run.isError && <ErrorState error={run.error} />}
        {d && (
          <>
            <div className="muted" style={{ marginBottom: 12 }}>
              {d.id} · horizon {fmtDate(d.horizonStart)} → {fmtDate(d.horizonEnd)} · {d.bucketDays}-day buckets · by {d.runBy}
            </div>
            <SummaryCard s={summary} />
            <CriteriaPanel criteria={d.criteria} onRerun={(c) => setWizard({ initial: c })} />
            <div className="kpi-grid">
              <Kpi label="Planned POs" value={pos} meta="Purchase orders" />
              {(prs > 0 || d.criteria?.purchasing?.document === 'Request') && <Kpi label="Purchase requests" value={prs} meta="Planned requests" />}
              {(trs > 0 || d.criteria?.transfers?.enabled) && <Kpi label="Transfers" value={trs} meta="Transfer requests" />}
              <Kpi label="Planned production" value={prods} meta="Production orders" />
              <Kpi label="Exceptions" value={d.exceptions.length} tone={d.exceptions.length ? 'warn' : 'ok'} meta="Planner actions" />
              <Kpi label="Overloaded WCs" value={overloaded} tone={overloaded ? 'danger' : 'ok'} meta="Work centers" />
            </div>
            <div className="grid-2">
              <div className="card">
                <div className="card-h"><h3>Exceptions by type</h3></div>
                <div className="card-b chips">
                  {byType.map(({ t, n }) => (
                    <span key={t} className={`pill ${EXCEPTION_META[t].tone}`}>{EXCEPTION_META[t].label}: {n}</span>
                  ))}
                </div>
              </div>
              <div className="card">
                <div className="card-h"><h3>Recent runs</h3></div>
                <div className="table-wrap">
                  <table className="data">
                    <thead><tr><th className="nosort">Run</th><th className="nosort">When</th><th className="nosort num">Orders</th></tr></thead>
                    <tbody>
                      {(runs.data ?? []).map((r) => (
                        <tr key={r.id} onClick={() => setRunId(r.id)} style={{ cursor: 'pointer', fontWeight: r.id === runId ? 700 : 400 }}>
                          <td className="mono">{r.id}</td>
                          <td>{fmtDateTime(r.runAt)}</td>
                          <td className="num">{r.plannedOrderCount}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </>
        )}
      </div>
      {wizard && <RunWizard initial={wizard.initial} onClose={() => setWizard(null)} onQuick={() => { setWizard(null); setQuick(true); }} />}
      {quick && <RunDialog onClose={() => setQuick(false)} />}
    </>
  );
}
