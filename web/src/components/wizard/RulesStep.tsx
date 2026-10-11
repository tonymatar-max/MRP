import { Check, NumField, StepProps } from './parts';

export function RulesStep({ c, set }: StepProps) {
  const h = c.horizon;
  const setH = (p: Partial<typeof h>) => set((x) => ({ ...x, horizon: { ...x.horizon, ...p } }));
  return (
    <div className="form-stack">
      <p className="step-intro">How far ahead to plan and which rules to apply.</p>
      <div className="form-row-3">
        <div className="field"><label htmlFor="wz-start">Start date <span className="hint">empty = today</span></label>
          <input id="wz-start" type="date" value={h.startDate ?? ''} onChange={(e) => setH({ startDate: e.target.value || null })} /></div>
        <NumField id="wz-days" label="Horizon (days)" min={1} max={1095} value={h.days} onChange={(n) => setH({ days: n })} hint="1 to 1095" />
        <div className="field"><label htmlFor="wz-bucket">Bucket size</label>
          <select id="wz-bucket" value={h.bucketDays} onChange={(e) => setH({ bucketDays: +e.target.value })}>
            <option value={1}>Daily</option><option value={7}>Weekly</option><option value={30}>Monthly</option>
            {![1, 7, 30].includes(h.bucketDays) && <option value={h.bucketDays}>{h.bucketDays} days</option>}
          </select></div>
      </div>
      <Check id="wz-moq" label="Apply minimum order quantity" checked={c.lotSizing.applyMinOrder} onChange={(v) => set((x) => ({ ...x, lotSizing: { ...x.lotSizing, applyMinOrder: v } }))}
        hint="Round small requirements up to the item’s minimum order." />
      <Check id="wz-mult" label="Apply order multiples" checked={c.lotSizing.applyMultiples} onChange={(v) => set((x) => ({ ...x, lotSizing: { ...x.lotSizing, applyMultiples: v } }))}
        hint="Round quantities up to a multiple of the item’s order multiple." />
      <Check id="wz-cal" label="Use the working calendar" checked={c.calendar.useWorkingCalendar} onChange={(v) => set((x) => ({ ...x, calendar: { useWorkingCalendar: v } }))}
        hint="Skip weekends and holidays when scheduling. Untick to treat every day as a working day." />
      <Check id="wz-crp" label="Run capacity planning (CRP)" checked={c.capacity.enabled} onChange={(v) => set((x) => ({ ...x, capacity: { enabled: v } }))}
        hint="Compare required hours with available hours per work centre and flag overloads." />
    </div>
  );
}
