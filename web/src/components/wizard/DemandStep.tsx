import { Check, NumField, StepProps } from './parts';

export function DemandStep({ c, set }: StepProps) {
  const d = c.demand;
  const setD = (p: Partial<typeof d>) => set((x) => ({ ...x, demand: { ...x.demand, ...p } }));
  return (
    <div className="form-stack">
      <p className="step-intro">What drives requirements. Untick a source to leave it out of this run.</p>
      <div className="wz-group">
        <Check id="wz-so" label="Sales orders" checked={d.salesOrders} onChange={(v) => setD({ salesOrders: v })} hint="Open sales order lines, by ship date." />
        {d.salesOrders && (
          <div className="form-row sub">
            <div className="field"><label htmlFor="wz-so-from">Ship date from <span className="hint">optional</span></label>
              <input id="wz-so-from" type="date" value={d.salesOrderFrom ?? ''} onChange={(e) => setD({ salesOrderFrom: e.target.value || null })} /></div>
            <div className="field"><label htmlFor="wz-so-to">Ship date to <span className="hint">optional</span></label>
              <input id="wz-so-to" type="date" value={d.salesOrderTo ?? ''} onChange={(e) => setD({ salesOrderTo: e.target.value || null })} /></div>
          </div>
        )}
      </div>
      <div className="wz-group">
        <Check id="wz-fc" label="Forecasts" checked={d.forecasts} onChange={(v) => setD({ forecasts: v })} hint="Forecast quantities entered on the Forecasts screen." />
        {d.forecasts && (
          <div className="form-row sub">
            <div className="field"><label htmlFor="wz-fc-cons">Forecast consumption</label>
              <select id="wz-fc-cons" value={d.forecastConsumption} onChange={(e) => setD({ forecastConsumption: e.target.value as 'Forward' | 'None' })}>
                <option value="Forward">Forward</option><option value="None">None</option>
              </select>
              <span className="hint">Forward nets the forecast against real sales orders so demand is not counted twice.</span></div>
            <NumField id="wz-fc-look" label="Lookback (days)" min={0} max={365} value={d.forecastLookbackDays} onChange={(n) => setD({ forecastLookbackDays: n })}
              hint="Also count forecast periods that started this many days ago." />
          </div>
        )}
      </div>
      <Check id="wz-pc" label="Components of open production orders" checked={d.productionComponents} onChange={(v) => setD({ productionComponents: v })}
        hint="Components still to be issued to production orders that already exist in SAP B1." />
      <div className="field" style={{ maxWidth: 360 }}>
        <label htmlFor="wz-pd">Past-due demand</label>
        <select id="wz-pd" value={d.pastDue} onChange={(e) => setD({ pastDue: e.target.value as 'FoldToStart' | 'Ignore' })}>
          <option value="FoldToStart">Plan as due now</option><option value="Ignore">Ignore</option>
        </select>
        <span className="hint">Overdue demand is either planned as if it were due on the start date, or left out.</span>
      </div>
      <Check id="wz-ss" label="Keep safety stock" checked={d.safetyStock} onChange={(v) => setD({ safetyStock: v })} hint="Plan so stock never drops below the item’s minimum level." />
    </div>
  );
}
