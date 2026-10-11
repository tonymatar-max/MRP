import { useState } from 'react';
import { META } from '../api/client';
import { useForecasts, useUpsertForecast } from '../api/hooks';
import type { Forecast } from '../api/types';
import { Dialog, ErrorState, Loading, PageHeader } from '../components/ui';
import { fmtDate, fmtNum } from '../components/util';

const blank = (): Forecast => ({
  id: '',
  itemCode: META.ITEMS[0].code,
  warehouseCode: META.WAREHOUSES[0],
  periodStart: new Date().toISOString().slice(0, 10),
  quantity: 0,
  consumed: 0,
});

export default function Forecasts() {
  const q = useForecasts();
  const save = useUpsertForecast();
  const [edit, setEdit] = useState<Forecast | null>(null);

  const [formErr, setFormErr] = useState('');
  const submit = () => {
    if (!edit) return;
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
              <select value={edit.itemCode} onChange={(e) => setEdit({ ...edit, itemCode: e.target.value })}>{META.ITEMS.map((i) => <option key={i.code}>{i.code}</option>)}</select></div>
            <div className="field"><label>Warehouse</label>
              <select value={edit.warehouseCode} onChange={(e) => setEdit({ ...edit, warehouseCode: e.target.value })}>{META.WAREHOUSES.map((w) => <option key={w}>{w}</option>)}</select></div>
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
