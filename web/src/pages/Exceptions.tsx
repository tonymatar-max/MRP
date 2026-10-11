import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useRun } from '../api/hooks';
import type { ExceptionMessage, ExceptionType } from '../api/types';
import { Empty, ErrorState, ExceptionPill, Loading, PageHeader, Pill } from '../components/ui';
import { RunPicker } from '../components/RunPicker';
import { AskAiButton } from '../components/AiAssistant';
import { useSelectedRun } from '../components/RunContext';
import { useAuth } from '../components/Auth';
import { ExecuteDialog } from '../components/ExecuteDialog';
import { EXCEPTION_META, fmtDate, fmtDateTime, fmtNum } from '../components/util';

type Filter = 'Open' | 'Done' | 'All';
const FILTERS: Filter[] = ['Open', 'Done', 'All'];
const NO_ADMIN = 'Only an administrator can execute';
const isDone = (e: ExceptionMessage) => e.actionStatus === 'Done';

function ActionCell({ e, isAdmin, onExecute }: { e: ExceptionMessage; isAdmin: boolean; onExecute: (e: ExceptionMessage) => void }) {
  if (isDone(e)) {
    const text = `by ${e.actionBy ?? 'unknown'}${e.actionAt ? ` on ${fmtDateTime(e.actionAt)}` : ''}`;
    return (
      <span title={e.actionNote || undefined} tabIndex={0}
        aria-label={`Done ${text}${e.actionNote ? `. Note: ${e.actionNote}` : ''}`} style={{ display: 'inline-flex', flexDirection: 'column', gap: 2 }}>
        <Pill tone="ok">Done</Pill>
        <span className="muted" style={{ fontSize: 12 }}>{text}</span>
      </span>
    );
  }
  if (!e.actions?.length) return <span className="muted">—</span>;
  if (!isAdmin) {
    return (
      <span title={NO_ADMIN}>
        <button type="button" className="btn sm" aria-disabled="true" aria-label={`Execute (${NO_ADMIN})`} onClick={(ev) => ev.preventDefault()} style={{ opacity: 0.5, cursor: 'not-allowed' }}>
          Execute
        </button>
      </span>
    );
  }
  return <button type="button" className="btn sm primary" onClick={() => onExecute(e)} aria-label={`Execute for ${e.itemCode}, ${EXCEPTION_META[e.type].label}`}>Execute</button>;
}

export default function Exceptions() {
  const { runId } = useSelectedRun();
  const run = useRun(runId);
  const { isAdmin } = useAuth();
  const [filter, setFilter] = useState<Filter>('Open');
  const [executing, setExecuting] = useState<ExceptionMessage | null>(null);
  const all = run.data?.exceptions ?? [];
  const list = all.filter((e) => (filter === 'All' ? true : filter === 'Done' ? isDone(e) : !isDone(e)));
  const doneCount = all.filter(isDone).length;

  return (
    <>
      <PageHeader crumb="Planning" title="Exceptions" actions={<><RunPicker /><AskAiButton /></>} />
      <div className="page">
        {run.data && all.length > 0 && (
          <div className="toolbar" role="group" aria-label="Filter exceptions by status"><div className="seg">
            {FILTERS.map((f) => (
              <button key={f} type="button" className={filter === f ? 'on' : ''} aria-pressed={filter === f} onClick={() => setFilter(f)}>
                {f}{f === 'Done' ? ` (${doneCount})` : f === 'Open' ? ` (${all.length - doneCount})` : ` (${all.length})`}
              </button>
            ))}
          </div></div>
        )}
        {run.isLoading && <Loading />}
        {run.isError && <ErrorState error={run.error} />}
        {run.data && all.length === 0 && <div className="card"><Empty title="No exceptions for this run" /></div>}
        {run.data && all.length > 0 && list.length === 0 && <div className="card"><Empty title={`No ${filter.toLowerCase()} exceptions`} /></div>}
        {(Object.keys(EXCEPTION_META) as ExceptionType[]).map((t) => {
          const rows = list.filter((e) => e.type === t);
          if (!rows.length) return null;
          return (
            <div className="card" key={t} style={{ marginBottom: 16 }}>
              <div className="card-h"><h3><ExceptionPill type={t} /> <span className="muted">{rows.length}</span></h3></div>
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th className="nosort">Item</th><th className="nosort">Whs</th><th className="nosort">Message</th><th className="nosort">Date</th><th className="nosort num">Qty</th><th className="nosort">Order</th><th className="nosort">Action</th></tr></thead>
                  <tbody>
                    {rows.map((e) => (
                      <tr key={e.id}>
                        <td className="mono">{e.itemCode}</td>
                        <td>{e.warehouseCode}</td>
                        <td style={{ whiteSpace: 'normal' }}>{e.message}</td>
                        <td>{fmtDate(e.relatedDate)}</td>
                        <td className="num">{fmtNum(e.quantity)}</td>
                        <td>{e.plannedOrderId ? <Link to={`/planned-orders?focus=${encodeURIComponent(e.plannedOrderId)}`}>View order →</Link> : '—'}</td>
                        <td>
                          <ActionCell e={e} isAdmin={isAdmin} onExecute={setExecuting} />
                          {!isDone(e) && !e.actions?.length && (t === 'PastDue' || t === 'CapacityOverload' || t === 'NotPlanned') && (
                            <div className="muted" style={{ fontSize: 12 }}>No automatic action: review the item</div>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          );
        })}
      </div>
      {executing && runId && <ExecuteDialog runId={runId} exception={executing} onClose={() => setExecuting(null)} />}
    </>
  );
}
