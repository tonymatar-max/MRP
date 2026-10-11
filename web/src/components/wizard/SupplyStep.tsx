import { PriorityControl } from '../PriorityControl';
import { Check, NumField, StepProps } from './parts';

export function SupplyStep({ c, set }: StepProps) {
  const sp = c.supply;
  const setS = (p: Partial<typeof sp>) => set((x) => ({ ...x, supply: { ...x.supply, ...p } }));
  const setL = (p: Partial<typeof c.leadTime>) => set((x) => ({ ...x, leadTime: { ...x.leadTime, ...p } }));
  return (
    <div className="form-stack">
      <p className="step-intro">What already covers demand, and how long things take.</p>
      <Check id="wz-oh" label="On-hand stock" checked={sp.onHand} onChange={(v) => setS({ onHand: v })} hint="Stock in the selected warehouses reduces what has to be planned." />
      <Check id="wz-opo" label="Open purchase orders" checked={sp.openPurchaseOrders} onChange={(v) => setS({ openPurchaseOrders: v })} hint="Purchase orders already placed count as supply on their due date." />
      <Check id="wz-opr" label="Open production orders" checked={sp.openProductionOrders} onChange={(v) => setS({ openProductionOrders: v })} hint="Production orders already released count as supply on their due date." />
      <div className="wz-group">
        <h4 className="section-title" style={{ marginTop: 0 }}>Lead time rule</h4>
        <PriorityControl idPrefix="wz-lt" priority={c.leadTime.priority} mode={c.leadTime.mode}
          onPriority={(p) => setL({ priority: p })} onMode={(m) => setL({ mode: m })} />
        <div style={{ maxWidth: 260, marginTop: 14 }}>
          <NumField id="wz-lt-def" label="Default lead time (days)" min={0} max={730} value={c.leadTime.defaultDays} onChange={(n) => setL({ defaultDays: n })}
            hint="Used when neither the supplier, the item nor its group has a lead time." />
        </div>
      </div>
    </div>
  );
}
