import { useState } from 'react';
import { useForecasts, useMasterItems, useStatus, useUpsertForecast, useWarehouses } from '../api/hooks';
import { useDebounced } from '../components/useDebounced';
import type { Forecast } from '../api/types';
import { Dialog, ErrorState, Loading, PageHeader } from '../components/ui';
import { fmtDate, fmtNum } from '../components/util';

const blank = (): Forecast => ({
  id: '',
  itemCode: '',
  warehouseCode: '',
  periodStart: new Date().toISOString().slice(0, 10),
  quantity: 0,
  consumed: 0,
});

export default function Forecasts() {
  const q = useForecasts();
  const save = useUpsertForecast();
  const [edit, setEdit] = useState<Forecast | null>(null);

  // Item and warehouse choices come from the planning data source (SAP B1 or the mock sample), not fixed lists.
  const source = useStatus().data?.defaultSource ?? 'mock';
  const whs = useWarehouses(source).data ?? [];
  const dItem = useDebounced(edit?.itemCode ?? '', 250);
  const items = useMasterItems({ source, search: dItem, pageSize: 20 }).data?.items ?? [];

  const [formErr, setFormErr] = useState('');
  const submit = () => {
    if (!edit) return;
    if (!edit.itemCode.trim()) { setFormErr('Enter the item code.'); return; }
    if (!edit.warehouseCode) { setFormErr('Choose the warehouse.'); return; }
    if (!Number.isFinite(edit.quantity) || edit.quantity < 0) { setFormErr('Quantity must be 0 or more.'); return; }
    if (!edit.periodStart) { setFormErr('Choose the period start date.'); return; }
    setFormErr('');
    save.mutate(edit, { onSuccess: () => setEdit(null) });
  };

  return (
    <>
      <PageHeader crumb="Planning" title="Forecasts" actions={<button className="btn primary" onClick={() => { setFormErr(''); save.reset(); setEdit(blank()); }}>Add forecast</button>} />
      <div className="page">
        <div className="card">
          {q.isLoading && <Loading />}
          {q.isError && <ErrorState error={q.error} />}
          {q.data && (
            <div className="table-wrap">
              <table className="data">
                <thead><tr><th className="nosort">Item</th><th className="nosort">Whs</th><th className="nosort">Period</th><th className="nosort num">Forecast</th><th className="nosort num">Consumed</th><th className="nosort num">Remaining</th><th className="nosort" /></tr></thead>
                <tbody>
                  {q.data.map((f) => (
                    <tr key={f.id}>
                      <td className="mono">{f.itemCode}</td>
                      <td>{f.warehouseCode}</td>
                      <td>{fmtDate(f.periodStart)}</td>
                      <td className="num">{fmtNum(f.quantity)}</td>
                      <td className="num">{fmtNum(f.consumed)}</td>
                      <td className="num"><strong>{fmtNum(Math.max(0, f.quantity - f.consumed))}</strong></td>
                      <td><button className="btn sm" onClick={() => { setFormErr(''); save.reset(); setEdit({ ...f }); }}>Edit</button></td>
                    </tr>
                  ))}
                  {q.data.length === 0 && <tr><td colSpan={7} className="muted" style={{ textAlign: 'center', padding: 20 }}>No forecasts yet. Click Add forecast.</td></tr>}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
      {edit && (
        <Dialog
          title={edit.id ? 'Edit forecast' : 'Add forecast'}
          onClose={() => setEdit(null)}
          footer={<><button className="btn" onClick={() => setEdit(null)}>Cancel</button><button className="btn primary" disabled={save.isPending} onClick={submit}>Save</button></>}
        >
          <div className="form-row">
            <div className="field"><label>Item</label>
              <input list="fc-items" autoComplete="off" value={edit.itemCode} placeholder="Type to search items" onChange={(e) => setEdit({ ...edit, itemCode: e.target.value })} />
              <datalist id="fc-items">{items.map((i) => <option key={i.itemCode} value={i.itemCode}>{i.itemName}</option>)}</datalist></div>
            <div className="field"><label>Warehouse</label>
              <select value={edit.warehouseCode} onChange={(e) => setEdit({ ...edit, warehouseCode: e.target.value })}>
                <option value="">Choose…</option>{whs.map((w) => <option key={w}>{w}</option>)}</select></div>
          </div>
          {(formErr || save.isError) && <div className="alert danger" role="alert">{formErr || (save.error as Error).message}</div>}
          <div className="form-row">
            <div className="field"><label>Period start</label><input type="date" value={edit.periodStart} onChange={(e) => setEdit({ ...edit, periodStart: e.target.value })} /></div>
            <div className="field"><label>Quantity</label><input type="number" value={edit.quantity} onChange={(e) => setEdit({ ...edit, quantity: +e.target.value })} /></div>
          </div>
        </Dialog>
      )}
    </>
  );
}
