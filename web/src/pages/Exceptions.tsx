import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useNewExceptions, useRun } from '../api/hooks';
import type { ExceptionMessage, ExceptionType } from '../api/types';
import { Empty, ErrorState, ExceptionPill, Loading, PageHeader, Pill } from '../components/ui';
import { RunPicker } from '../components/RunPicker';
import { AskAiButton, ExplainButton, TriageButton } from '../components/AiAssistant';
import { useSelectedRun } from '../components/RunContext';
import { useAuth } from '../components/Auth';
import { ExecuteDialog } from '../components/ExecuteDialog';
import { useT } from '../i18n';
import { EXCEPTION_META, fmtDate, fmtDateTime, fmtNum } from '../components/util';

type Filter = 'Open' | 'New' | 'Done' | 'All';
const NO_ADMIN = 'Only an administrator can execute';
const isDone = (e: ExceptionMessage) => e.actionStatus === 'Done';

function ActionCell({ e, isAdmin, onExecute }: { e: ExceptionMessage; isAdmin: boolean; onExecute: (e: ExceptionMessage) => void }) {
  const tr = useT();
  if (isDone(e)) {
    const text = tr('by {user}', { user: e.actionBy ?? tr('unknown') }) + (e.actionAt ? ` ${tr('on {date}', { date: fmtDateTime(e.actionAt) })}` : '');
    return (
      <span title={e.actionNote || undefined} tabIndex={0}
        aria-label={`Done ${text}${e.actionNote ? `. Note: ${e.actionNote}` : ''}`} style={{ display: 'inline-flex', flexDirection: 'column', gap: 2 }}>
        <Pill tone="ok">{tr('Done')}</Pill>
        <span className="muted" style={{ fontSize: 12 }}>{text}</span>
      </span>
    );
  }
  if (!e.actions?.length) return <span className="muted">—</span>;
  if (!isAdmin) {
    return (
      <span title={tr(NO_ADMIN)}>
        <button type="button" className="btn sm" aria-disabled="true" aria-label={`${tr('Execute')} (${tr(NO_ADMIN)})`} onClick={(ev) => ev.preventDefault()} style={{ opacity: 0.5, cursor: 'not-allowed' }}>
          {tr('Execute')}
        </button>
      </span>
    );
  }
  return <button type="button" className="btn sm primary" onClick={() => onExecute(e)} aria-label={`Execute for ${e.itemCode}, ${EXCEPTION_META[e.type].label}`}>{tr('Execute')}</button>;
}

export default function Exceptions() {
  const tr = useT();
  const { runId } = useSelectedRun();
  const run = useRun(runId);
  const { isAdmin } = useAuth();
  const [filter, setFilter] = useState<Filter>('Open');
  const [executing, setExecuting] = useState<ExceptionMessage | null>(null);
  const fresh = useNewExceptions(runId);
  const filters: Filter[] = fresh.hasPrevious ? ['Open', 'New', 'Done', 'All'] : ['Open', 'Done', 'All'];
  const active: Filter = filters.includes(filter) ? filter : 'Open';
  const all = run.data?.exceptions ?? [];
  const list = all.filter((e) => (active === 'All' ? true : active === 'Done' ? isDone(e) : active === 'New' ? !isDone(e) && fresh.isNew(e) : !isDone(e)));
  const doneCount = all.filter(isDone).length;

  return (
    <>
      <PageHeader crumb="Planning" title="Exceptions" actions={<><RunPicker /><TriageButton /><AskAiButton /></>} />
      <div className="page">
        {run.data && all.length > 0 && (
          <div className="toolbar" role="group" aria-label={tr('Filter exceptions by status')}><div className="seg">
            {filters.map((f) => (
              <button key={f} type="button" className={active === f ? 'on' : ''} aria-pressed={active === f} onClick={() => setFilter(f)} title={f === 'New' ? tr('Not in the run before this one') : undefined}>
                {tr(f)}{f === 'Done' ? ` (${doneCount})` : f === 'Open' ? ` (${all.length - doneCount})` : f === 'New' ? ` (${fresh.count})` : ` (${all.length})`}
              </button>
            ))}
          </div></div>
        )}
        {run.isLoading && <Loading />}
        {run.isError && <ErrorState error={run.error} />}
        {run.data && all.length === 0 && <div className="card"><Empty title="No exceptions for this run" /></div>}
        {run.data && all.length > 0 && list.length === 0 && <div className="card"><Empty title={tr('No {kind} exceptions', { kind: tr(active).toLowerCase() })} /></div>}
        {(Object.keys(EXCEPTION_META) as ExceptionType[]).map((t) => {
          const rows = list.filter((e) => e.type === t);
          if (!rows.length) return null;
          return (
            <div className="card" key={t} style={{ marginBottom: 16 }}>
              <div className="card-h"><h3><ExceptionPill type={t} /> <span className="muted">{rows.length}</span></h3></div>
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th className="nosort">{tr('Item')}</th><th className="nosort">{tr('Whs')}</th><th className="nosort">{tr('Message')}</th><th className="nosort">{tr('Date')}</th><th className="nosort num">{tr('Qty')}</th><th className="nosort">{tr('Order')}</th><th className="nosort">{tr('Action')}</th></tr></thead>
                  <tbody>
                    {rows.map((e) => (
                      <tr key={e.id}>
                        <td className="mono">{e.itemCode}{fresh.hasPrevious && !isDone(e) && fresh.isNew(e) && <> <Pill tone="info">{tr('New')}</Pill></>}</td>
                        <td>{e.warehouseCode}</td>
                        <td style={{ whiteSpace: 'normal' }}>{e.message}</td>
                        <td>{fmtDate(e.relatedDate)}</td>
                        <td className="num">{fmtNum(e.quantity)}</td>
                        <td>{e.plannedOrderId ? <Link to={`/planned-orders?focus=${encodeURIComponent(e.plannedOrderId)}`}>{tr('View order')} →</Link> : '—'}</td>
                        <td>
                          <ActionCell e={e} isAdmin={isAdmin} onExecute={setExecuting} />
                          <ExplainButton label={`${EXCEPTION_META[e.type].label} for ${e.itemCode}`}
                            question={`Explain this ${EXCEPTION_META[e.type].label} exception for item ${e.itemCode}${e.warehouseCode ? ` in warehouse ${e.warehouseCode}` : ''} (${e.message}). Why does it happen, what demand or supply is behind it, and what do you suggest I do?`} />
                          {!isDone(e) && !e.actions?.length && (t === 'PastDue' || t === 'CapacityOverload' || t === 'NotPlanned') && (
                            <div className="muted" style={{ fontSize: 12 }}>{tr('No automatic action: review the item')}</div>
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
