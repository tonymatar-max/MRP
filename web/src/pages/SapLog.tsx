import { useState } from 'react';
import { useSapLog } from '../api/hooks';
import type { SapLogEntry, SapPushAction } from '../api/types';
import { useDebounced } from '../components/useDebounced';
import { Empty, ErrorState, Loading, PageHeader, Pill } from '../components/ui';
import { useT } from '../i18n';
import { fmtDateTime, fmtNum } from '../components/util';

type Result = 'all' | 'ok' | 'failed';
const RESULTS: { id: Result; label: string }[] = [{ id: 'all', label: 'All' }, { id: 'ok', label: 'Succeeded' }, { id: 'failed', label: 'Failed' }];

const ACTIONS: { id: SapPushAction; label: string }[] = [
  { id: 'CreatePurchaseOrder', label: 'Create purchase order' },
  { id: 'CreatePurchaseRequest', label: 'Create purchase request' },
  { id: 'CreateTransferRequest', label: 'Create transfer request' },
  { id: 'CreateProductionOrder', label: 'Create production order' },
  { id: 'MoveDueDate', label: 'Move due date' },
  { id: 'CloseDocument', label: 'Close / cancel document' },
  { id: 'UpdateItem', label: 'Update item master' },
  { id: 'Session', label: 'Service Layer session' },
];
const actionLabel = (a: SapPushAction) => ACTIONS.find((x) => x.id === a)?.label ?? a;   // English: also used for the CSV

const csvCell = (v: unknown) => {
  const t = v == null ? '' : String(v);
  return /[",\r\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t;
};
function downloadCsv(rows: SapLogEntry[]) {
  const head = ['When', 'User', 'Action', 'Result', 'Item', 'Warehouse', 'Quantity', 'SAP document', 'What was sent', 'Error'];
  const lines = [head, ...rows.map((r) => [r.at, r.userName, actionLabel(r.action), r.success ? 'OK' : 'Failed', r.itemCode, r.warehouseCode, r.quantity, r.target, r.summary, r.error])];
  const text = '﻿' + lines.map((l) => l.map(csvCell).join(',')).join('\r\n');
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a');
  a.href = url; a.download = 'sap-push-log.csv';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export default function SapLog() {
  const tr = useT();
  const [result, setResult] = useState<Result>('all');
  const [action, setAction] = useState<SapPushAction | ''>('');
  const [item, setItem] = useState('');
  const [user, setUser] = useState('');
  const dItem = useDebounced(item, 300);
  const dUser = useDebounced(user, 300);
  const log = useSapLog({
    success: result === 'all' ? undefined : result === 'ok',
    action, item: dItem.trim() || undefined, user: dUser.trim() || undefined, limit: 500,
  });
  const rows = log.data ?? [];
  const filtered = result !== 'all' || action !== '' || dItem.trim() !== '' || dUser.trim() !== '';
  const failed = rows.filter((r) => !r.success).length;

  return (
    <>
      <PageHeader
        crumb="Data" title="SAP push log"
        actions={<button type="button" className="btn" disabled={!rows.length} onClick={() => downloadCsv(rows)}>{tr('Export CSV')}</button>}
      />
      <div className="page">
        <div className="alert info-note" role="note">
          {tr('Every time Cobalt MRP writes to SAP Business One (release a planned order, move a due date, close a document) it is recorded here, whether it worked or not. Passwords and request bodies are never stored.')}
        </div>
        <div className="toolbar">
          <div className="seg" role="group" aria-label={tr('Filter by result')}>
            {RESULTS.map((r) => (
              <button key={r.id} type="button" className={result === r.id ? 'on' : ''} aria-pressed={result === r.id} onClick={() => setResult(r.id)}>{tr(r.label)}</button>
            ))}
          </div>
          <div className="field">
            <label htmlFor="sl-action">{tr('Action')}</label>
            <select id="sl-action" value={action} onChange={(e) => setAction(e.target.value as SapPushAction | '')}>
              <option value="">{tr('All actions')}</option>
              {ACTIONS.map((a) => <option key={a.id} value={a.id}>{tr(a.label)}</option>)}
            </select>
          </div>
          <div className="field">
            <label htmlFor="sl-item">{tr('Item')}</label>
            <input id="sl-item" className="search" placeholder={tr('Item code')} value={item} onChange={(e) => setItem(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="sl-user">{tr('User')}</label>
            <input id="sl-user" placeholder={tr('User name')} value={user} onChange={(e) => setUser(e.target.value)} />
          </div>
          {log.data && <span className="muted" style={{ marginLeft: 'auto' }}>{tr('{n} entries', { n: rows.length })}{failed ? `, ${tr('{n} failed', { n: failed })}` : ''}</span>}
        </div>

        {log.isLoading && <Loading />}
        {log.isError && <ErrorState error={log.error} />}
        {log.data && rows.length === 0 && <div className="card"><Empty title={filtered ? 'No log entries match these filters' : 'Nothing has been pushed to SAP B1 yet'} /></div>}
        {rows.length > 0 && (
          <div className="card">
            <div className="table-wrap">
              <table className="data">
                <thead>
                  <tr>
                    <th className="nosort">{tr('When')}</th><th className="nosort">{tr('User')}</th><th className="nosort">{tr('Action')}</th><th className="nosort">{tr('Result')}</th>
                    <th className="nosort">{tr('Item')}</th><th className="nosort">{tr('Whs')}</th><th className="nosort num">{tr('Qty')}</th>
                    <th className="nosort">{tr('SAP document')}</th><th className="nosort">{tr('Details')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.id}>
                      <td>{fmtDateTime(r.at)}</td>
                      <td>{r.userName || <span className="muted">—</span>}</td>
                      <td>{tr(actionLabel(r.action))}</td>
                      <td><Pill tone={r.success ? 'ok' : 'danger'}>{r.success ? tr('OK') : tr('Failed')}</Pill></td>
                      <td>{r.itemCode ?? <span className="muted">—</span>}</td>
                      <td>{r.warehouseCode ?? <span className="muted">—</span>}</td>
                      <td className="num">{r.quantity != null ? fmtNum(r.quantity) : <span className="muted">—</span>}</td>
                      <td>{r.target ?? <span className="muted">—</span>}</td>
                      <td style={{ maxWidth: 420, overflowWrap: 'anywhere' }}>
                        {r.summary}
                        {r.error && <div style={{ color: 'var(--danger, #b42318)', marginTop: 2 }}>{r.error}</div>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </>
  );
}
