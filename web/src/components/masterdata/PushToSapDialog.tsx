import { useEffect, useMemo, useState } from 'react';
import { usePushPreview, usePushToSap } from '../../api/hooks';
import type { PushExecuteResult, PushPreview, PushPreviewField } from '../../api/types';
import { Dialog, ErrorState, Loading, Pill } from '../ui';

const COBALT_ONLY_LABEL: Record<string, string> = {
  reorderPoint: 'reorder point', planningMethod: 'planning method', lotSizing: 'lot sizing', periodOfSupplyDays: 'period of supply',
};

const keyOf = (code: string, field: string) => `${code}|${field}`;

/**
 * Pushes the planner's Cobalt overrides to the SAP B1 item master. Always a preview first (it reads the live items), then an
 * explicit confirmation. Only the ticked fields are written, and SAP B1 must still hold the value the preview showed.
 */
export function PushToSapDialog({ codes, onClose, onDone }: { codes: string[]; onClose: () => void; onDone: (summary: string) => void }) {
  const preview = usePushPreview();
  const push = usePushToSap();
  const [data, setData] = useState<PushPreview | null>(null);
  const [error, setError] = useState('');
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [clear, setClear] = useState(true);
  const [ack, setAck] = useState(false);
  const [result, setResult] = useState<PushExecuteResult | null>(null);

  useEffect(() => {
    preview.mutate(codes, {
      onSuccess: (p) => {
        setData(p);
        setTicked(new Set(p.items.flatMap((i) => i.fields.filter((f) => f.status === 'change').map((f) => keyOf(i.itemCode, f.field)))));
      },
      onError: (e) => setError((e as Error).message),
    });
    // The preview is taken once when the dialog opens; "Preview again" reloads it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const count = ticked.size;
  const itemsToPush = useMemo(() => (data?.items ?? [])
    .map((i) => ({ itemCode: i.itemCode, fields: i.fields.filter((f) => ticked.has(keyOf(i.itemCode, f.field))).map((f) => ({ field: f.field, expectedSap: f.sap, value: f.cobalt })) }))
    .filter((i) => i.fields.length > 0), [data, ticked]);

  const toggle = (k: string) => setTicked((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });
  const run = () => push.mutate({ items: itemsToPush, clearOverrides: clear }, {
    onSuccess: (r) => setResult(r),
    onError: (e) => setError((e as Error).message),
  });
  const finish = () => {
    if (result) onDone(`${result.pushed} item${result.pushed === 1 ? '' : 's'} pushed to SAP B1${result.failed ? `, ${result.failed} failed` : ''}.`);
    else onClose();
  };

  const rowTone = (f: PushPreviewField) => (f.status === 'blocked' ? 'danger' : f.status === 'same' ? 'ok' : 'info');
  const rowLabel = (f: PushPreviewField) => (f.status === 'blocked' ? 'Blocked' : f.status === 'same' ? 'Already in SAP B1' : 'Will change');

  return (
    <Dialog
      title="Push to SAP B1"
      onClose={finish}
      footer={<>
        <button type="button" className="btn" onClick={finish}>{result ? 'Close' : 'Cancel'}</button>
        {!result && (
          <button type="button" className="btn primary" disabled={!data?.ready || count === 0 || !ack || push.isPending} onClick={run}>
            {push.isPending ? 'Pushing…' : `Push ${count} change${count === 1 ? '' : 's'} to SAP B1`}
          </button>
        )}
      </>}
    >
      {!data && !error && <Loading />}
      {error && <ErrorState error={new Error(error)} />}

      {data && !data.ready && <div className="alert danger" role="alert">{data.notReadyReason}</div>}

      {data && !result && (
        <>
          <p className="hint">
            Your Cobalt overrides are compared with the live SAP B1 item master. Only the changes you tick are written, and only if SAP B1 still holds the value shown here.
            SAP B1 has no field for reorder point, planning method, lot sizing, period of supply or supplier lead time, so those stay in Cobalt.
          </p>
          <div className="table-wrap" style={{ maxHeight: 340, overflow: 'auto' }}>
            <table className="data">
              <thead><tr><th className="nosort"><span className="sr-only">Push</span></th><th className="nosort">Item</th><th className="nosort">Field</th><th className="nosort">Cobalt</th><th className="nosort">SAP B1 now</th><th className="nosort">Result</th></tr></thead>
              <tbody>
                {data.items.map((i) => {
                  if (!i.found || i.error) return <tr key={i.itemCode}><td /><td className="mono">{i.itemCode}</td><td colSpan={4}><Pill tone="danger">Problem</Pill> {i.error}</td></tr>;
                  if (i.fields.length === 0) return <tr key={i.itemCode}><td /><td className="mono">{i.itemCode}</td><td colSpan={4} className="muted">No overrides that SAP B1 can hold{i.cobaltOnly.length ? ` (${i.cobaltOnly.map((c) => COBALT_ONLY_LABEL[c] ?? c).join(', ')} stay in Cobalt)` : ''}.</td></tr>;
                  return i.fields.map((f, n) => {
                    const k = keyOf(i.itemCode, f.field);
                    return (
                      <tr key={k}>
                        <td><input type="checkbox" aria-label={`Push ${f.label} of ${i.itemCode}`} disabled={f.status !== 'change'} checked={ticked.has(k)} onChange={() => toggle(k)} /></td>
                        <td className="mono">{n === 0 ? <>{i.itemCode}{i.itemName && <div className="muted" style={{ fontFamily: 'inherit', fontSize: 12 }}>{i.itemName}</div>}</> : ''}</td>
                        <td>{f.label}</td>
                        <td><strong>{f.cobalt}</strong></td>
                        <td>{f.sap ?? <span className="muted">—</span>}</td>
                        <td style={{ whiteSpace: 'normal' }}><Pill tone={rowTone(f)}>{rowLabel(f)}</Pill>{f.message && f.status === 'blocked' ? <div className="muted" style={{ fontSize: 12 }}>{f.message}</div> : null}</td>
                      </tr>
                    );
                  });
                })}
              </tbody>
            </table>
          </div>
          <label className="check-inline" style={{ marginTop: 10, alignSelf: 'flex-start' }}>
            <input type="checkbox" checked={clear} onChange={(e) => setClear(e.target.checked)} />
            Remove the Cobalt override after pushing (SAP B1 then holds the value; recommended)
          </label>
          <label className="check-inline" style={{ alignSelf: 'flex-start' }}>
            <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} />
            I understand this changes SAP Business One item master data.
          </label>
        </>
      )}

      {result && (
        <div className="table-wrap" style={{ maxHeight: 340, overflow: 'auto' }}>
          <div className={`alert ${result.failed ? 'danger' : 'ok'}`} role="status" style={{ marginBottom: 8 }}>
            {result.pushed} item{result.pushed === 1 ? '' : 's'} pushed{result.failed ? `, ${result.failed} failed` : ''}. Every push is recorded in the SAP push log.
          </div>
          <table className="data">
            <thead><tr><th className="nosort">Item</th><th className="nosort">Result</th></tr></thead>
            <tbody>
              {result.items.map((r) => (
                <tr key={r.itemCode}>
                  <td className="mono">{r.itemCode}</td>
                  <td style={{ whiteSpace: 'normal' }}><Pill tone={r.success ? 'ok' : 'danger'}>{r.success ? 'OK' : 'Failed'}</Pill> {r.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Dialog>
  );
}
