import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { useRunDefaults, useStatus, useTriggerRun } from '../../api/hooks';
import type { RunCriteria } from '../../api/types';
import { useSelectedRun } from '../RunContext';
import { DemandStep } from './DemandStep';
import { ReviewStep } from './ReviewStep';
import { RulesStep } from './RulesStep';
import { ScopeStep } from './ScopeStep';
import { SupplyStep } from './SupplyStep';

const STEPS = ['Scope', 'Demand', 'Supply and lead times', 'Planning rules', 'Review'];

/** Returns the problems that block leaving a step (empty = ok). */
export function validateStep(step: number, c: RunCriteria, allWarehouses: string[] = []): string[] {
  const e: string[] = [];
  if (step === 0) {
    const chosen = c.scope.warehouses.length ? c.scope.warehouses : allWarehouses;
    if (c.level === 'Warehouse' && c.transfers.enabled) {
      if (chosen.length < 2) e.push('Transfers need at least two warehouses: select two or more warehouses, or turn transfers off.');
      if (c.transfers.sourceWarehouses.some((w) => !chosen.includes(w))) e.push('"Transfer from" can only list warehouses that are being planned.');
      if (!Number.isFinite(c.transfers.leadTimeDays) || c.transfers.leadTimeDays < 0 || c.transfers.leadTimeDays > 90) e.push('Transfer time must be between 0 and 90 days.');
    }
    if (c.scope.procurement === 'Make' && c.scope.suppliers.length) e.push('Suppliers only apply to purchased items: choose "Buy and make" or "Buy only", or clear the suppliers.');
  } else if (step === 1) {
    const d = c.demand;
    if (d.salesOrders && d.salesOrderFrom && d.salesOrderTo && d.salesOrderFrom > d.salesOrderTo) e.push('The sales order ship date window ends before it starts.');
    if (!Number.isFinite(d.forecastLookbackDays) || d.forecastLookbackDays < 0 || d.forecastLookbackDays > 365) e.push('Forecast lookback must be between 0 and 365 days.');
  } else if (step === 2) {
    const l = c.leadTime;
    if (!Number.isFinite(l.defaultDays) || l.defaultDays < 0) e.push('Default lead time must be 0 days or more.');
  } else if (step === 3) {
    const h = c.horizon;
    if (!Number.isFinite(h.days) || h.days < 1 || h.days > 1095) e.push('Horizon must be between 1 and 1095 days.');
    if (!h.bucketDays || h.bucketDays < 1) e.push('Choose a bucket size.');
  }
  return e;
}

/** Runs recorded before the planning level existed carry no level/purchasing/transfers; fill them in. */
export function withPlanningDefaults(c: RunCriteria): RunCriteria {
  return {
    ...c,
    level: c.level ?? 'Warehouse',
    purchasing: { document: c.purchasing?.document ?? 'Order', grouping: c.purchasing?.grouping ?? 'Supplier' },
    transfers: c.transfers ?? { enabled: false, sourceWarehouses: [], leadTimeDays: 1 },
  };
}

export function RunWizard({ onClose, initial, onQuick }: { onClose: () => void; initial?: RunCriteria | null; onQuick?: () => void }) {
  const status = useStatus().data;
  const defaults = useRunDefaults(status?.defaultSource ?? 'mock', !initial);
  const [c, setC] = useState<RunCriteria | null>(initial ? withPlanningDefaults(initial) : null);
  const [step, setStep] = useState(0);
  const [errors, setErrors] = useState<string[]>([]);
  const [notes, setNotes] = useState('');
  const [resetting, setResetting] = useState(false);
  const [resetKey, setResetKey] = useState(0); // remounts steps that keep local text state
  const whList = useQuery({ queryKey: ['warehouses', c?.source], queryFn: () => api.getWarehouses(c!.source), enabled: !!c });
  const allWh = whList.data ?? [];
  const trigger = useTriggerRun();
  const { setRunId } = useSelectedRun();
  const navigate = useNavigate();
  const dialogRef = useRef<HTMLDivElement>(null);
  const headRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => { if (!c && defaults.data) setC(withPlanningDefaults(defaults.data)); }, [c, defaults.data]);
  useEffect(() => { headRef.current?.focus(); }, [step]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !trigger.isPending) onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose, trigger.isPending]);

  const set = useCallback((fn: (x: RunCriteria) => RunCriteria) => { setErrors([]); setC((cur) => (cur ? fn(cur) : cur)); }, []);

  const go = (target: number) => {
    if (!c) return;
    if (target > step) {
      for (let s = step; s < target; s++) {
        const err = validateStep(s, c, allWh);
        if (err.length) { setStep(s); setErrors(err); return; }
      }
    }
    setErrors([]); setStep(target);
  };
  const reset = async () => {
    if (!c) return;
    setResetting(true);
    try { setC(withPlanningDefaults(await api.runDefaults(c.source))); setNotes(''); setErrors([]); setResetKey((k) => k + 1); }
    catch (e) { setErrors([(e as Error).message]); }
    finally { setResetting(false); }
  };
  const run = () => {
    if (!c) return;
    for (let s = 0; s < 4; s++) { const err = validateStep(s, c, allWh); if (err.length) { setStep(s); setErrors(err); return; } }
    trigger.mutate({ criteria: c, notes: notes.trim() || undefined }, {
      onSuccess: (r) => { setRunId(r.id); onClose(); navigate('/'); },
    });
  };

  const last = step === STEPS.length - 1;
  return (
    <div className="overlay">
      <div className="dialog wizard" role="dialog" aria-modal="true" aria-labelledby="wz-title" ref={dialogRef}>
        <div className="d-h wz-head">
          <div className="wz-title-row">
            <h3 id="wz-title">Run MRP</h3>
            <span className="spacer" />
            {onQuick && <button type="button" className="btn ghost sm" onClick={onQuick}>Quick run</button>}
            <button type="button" className="btn ghost sm" onClick={reset} disabled={!c || resetting || trigger.isPending}>{resetting ? 'Resetting…' : 'Reset to defaults'}</button>
            <button type="button" className="btn sm" onClick={onClose} disabled={trigger.isPending} aria-label="Close wizard">Close</button>
          </div>
          <ol className="steps" aria-label="Progress">
            {STEPS.map((s, i) => (
              <li key={s} className={i === step ? 'current' : i < step ? 'done' : ''} aria-current={i === step ? 'step' : undefined}>
                <button type="button" onClick={() => go(i)} disabled={!c || (i > step && i !== step + 1) || trigger.isPending}>
                  <span className="n">{i < step ? '✓' : i + 1}</span><span className="t">{s}</span>
                </button>
              </li>
            ))}
          </ol>
        </div>
        <div className="d-b wz-body">
          {!c && defaults.isLoading && <div className="center-state"><div className="spinner" />Loading defaults…</div>}
          {!c && defaults.isError && <div className="alert danger" role="alert">{(defaults.error as Error).message}</div>}
          {c && (
            <>
              <h4 className="wz-step-title" tabIndex={-1} ref={headRef}>Step {step + 1} of {STEPS.length}: {STEPS[step]}</h4>
              {errors.length > 0 && <div className="alert danger" role="alert"><ul className="warn-list">{errors.map((x, i) => <li key={i}>{x}</li>)}</ul></div>}
              <div key={resetKey}>
                {step === 0 && <ScopeStep c={c} set={set} />}
                {step === 1 && <DemandStep c={c} set={set} />}
                {step === 2 && <SupplyStep c={c} set={set} />}
                {step === 3 && <RulesStep c={c} set={set} />}
                {step === 4 && <ReviewStep c={c} notes={notes} onNotes={setNotes} />}
              </div>
              {trigger.isError && <div className="alert danger" role="alert">{(trigger.error as Error).message}</div>}
            </>
          )}
        </div>
        <div className="d-f">
          <button type="button" className="btn" onClick={onClose} disabled={trigger.isPending}>Cancel</button>
          <span className="spacer" />
          <button type="button" className="btn" disabled={step === 0 || trigger.isPending} onClick={() => go(step - 1)}>Back</button>
          {!last
            ? <button type="button" className="btn primary" disabled={!c} onClick={() => go(step + 1)}>Next</button>
            : <button type="button" className="btn primary" disabled={!c || trigger.isPending} onClick={run}>{trigger.isPending ? 'Running…' : 'Run MRP'}</button>}
        </div>
      </div>
    </div>
  );
}
