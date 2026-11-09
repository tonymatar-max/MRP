import { useMemo, useState, type ReactNode } from 'react';
import { useRun, useRuns } from '../api/hooks';
import { diffRuns } from '../api/compare';
import type { MrpRun } from '../api/types';
import { Empty, ErrorState, Loading, PageHeader, Pill } from '../components/ui';
import { useSelectedRun } from '../components/RunContext';
import { useT } from '../i18n';
import { EXCEPTION_META, fmtDate, fmtDateTime, fmtNum, orderTypeLabel } from '../components/util';

const runLabel = (r: MrpRun) => [r.name, fmtDateTime(r.runAt), r.runBy].filter(Boolean).join(' · ');

function Section({ title, count, children }: { title: string; count: number; children: ReactNode }) {
  if (count === 0) return null;
  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <div className="card-h"><h3>{title} <span className="muted">{count}</span></h3></div>
      <div className="table-wrap">{children}</div>
    </div>
  );
}

function Delta({ a, b }: { a: ReactNode; b: ReactNode }) {
  return <>{a} <span className="muted">→</span> <strong>{b}</strong></>;
}

export default function Compare() {
  const tr = useT();
  const { data: runs } = useRuns();
  const { runId } = useSelectedRun();
  const [picked, setPicked] = useState<{ older?: string; newer?: string }>({});
  // Default: the selected run against the run before it.
  const idx = runs && runId ? runs.findIndex((r) => r.id === runId) : -1;
  const newerId = picked.newer ?? runs?.[Math.max(idx, 0)]?.id;
  const olderId = picked.older ?? (idx >= 0 ? runs?.[idx + 1]?.id : runs?.[1]?.id);
  const a = useRun(olderId), b = useRun(newerId);
  const diff = useMemo(() => (a.data && b.data ? diffRuns(a.data, b.data) : null), [a.data, b.data]);

  const picker = (label: string, value: string | undefined, set: (v: string) => void) => (
    <label className="field" style={{ minWidth: 260, flex: 1 }}>
      <span>{label}</span>
      <select value={value ?? ''} onChange={(e) => set(e.target.value)} aria-label={label}>
        {(runs ?? []).map((r) => <option key={r.id} value={r.id}>{runLabel(r)}</option>)}
      </select>
    </label>
  );

  return (
    <>
      <PageHeader crumb="Planning" title="Compare runs" />
      <div className="page">
        {runs && runs.length < 2 && <div className="card"><Empty title="Two runs are needed to compare">{tr('Run MRP again to see what changed.')}</Empty></div>}
        {runs && runs.length >= 2 && (
          <div className="toolbar" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
            {picker(tr('Earlier run'), olderId, (v) => setPicked((p) => ({ ...p, older: v })))}
            {picker(tr('Later run'), newerId, (v) => setPicked((p) => ({ ...p, newer: v })))}
          </div>
        )}
        {(a.isLoading || b.isLoading) && <Loading />}
        {(a.isError || b.isError) && <ErrorState error={a.error ?? b.error} />}
        {olderId && olderId === newerId && <div className="card"><Empty title="Pick two different runs" /></div>}
        {diff && olderId !== newerId && (
          <>
            <div className="kpi-grid">
              {([
                [tr('Orders added'), diff.orders.added.length], [tr('Orders removed'), diff.orders.removed.length], [tr('Orders changed'), diff.orders.changed.length],
                [tr('Exceptions added'), diff.exceptions.added.length], [tr('Exceptions gone'), diff.exceptions.removed.length],
                [tr('Overloads added'), diff.capacity.added.length], [tr('Overloads gone'), diff.capacity.removed.length],
              ] as [string, number][]).map(([label, n]) => (
                <div className="kpi" key={label}><div className="top">{label}</div><div className="val">{n}</div></div>
              ))}
            </div>
            <p className="muted">{tr('{n} planned orders and {m} exceptions are unchanged.', { n: diff.orders.same, m: diff.exceptions.same })}</p>

            <Section title={tr('Planned orders added')} count={diff.orders.added.length}>
              <table className="data"><thead><tr><th>{tr('Item')}</th><th>{tr('Type')}</th><th>{tr('Whs')}</th><th className="num">{tr('Qty')}</th><th>{tr('Due')}</th></tr></thead><tbody>
                {diff.orders.added.map((o) => <tr key={o.id}><td className="mono">{o.itemCode}</td><td>{orderTypeLabel(o.type)}</td><td>{o.warehouseCode}</td><td className="num">{fmtNum(o.quantity)}</td><td>{fmtDate(o.dueDate)}</td></tr>)}
              </tbody></table>
            </Section>
            <Section title={tr('Planned orders removed')} count={diff.orders.removed.length}>
              <table className="data"><thead><tr><th>{tr('Item')}</th><th>{tr('Type')}</th><th>{tr('Whs')}</th><th className="num">{tr('Qty')}</th><th>{tr('Due')}</th></tr></thead><tbody>
                {diff.orders.removed.map((o) => <tr key={o.id}><td className="mono">{o.itemCode}</td><td>{orderTypeLabel(o.type)}</td><td>{o.warehouseCode}</td><td className="num">{fmtNum(o.quantity)}</td><td>{fmtDate(o.dueDate)}</td></tr>)}
              </tbody></table>
            </Section>
            <Section title={tr('Planned orders changed')} count={diff.orders.changed.length}>
              <table className="data"><thead><tr><th>{tr('Item')}</th><th>{tr('Type')}</th><th>{tr('Whs')}</th><th className="num">{tr('Qty')}</th><th>{tr('Due')}</th><th>{tr('Supplier')}</th></tr></thead><tbody>
                {diff.orders.changed.map((c) => (
                  <tr key={c.after.id}>
                    <td className="mono">{c.after.itemCode}</td><td>{orderTypeLabel(c.after.type)}</td><td>{c.after.warehouseCode}</td>
                    <td className="num">{c.fields.includes('quantity') ? <Delta a={fmtNum(c.before.quantity)} b={fmtNum(c.after.quantity)} /> : fmtNum(c.after.quantity)}</td>
                    <td>{c.fields.includes('due date') ? <Delta a={fmtDate(c.before.dueDate)} b={fmtDate(c.after.dueDate)} /> : fmtDate(c.after.dueDate)}</td>
                    <td>{c.fields.includes('supplier') ? <Delta a={c.before.vendorCode ?? '—'} b={c.after.vendorCode ?? '—'} /> : (c.after.vendorCode ?? '—')}</td>
                  </tr>
                ))}
              </tbody></table>
            </Section>

            {(['added', 'removed'] as const).map((k) => (
              <Section key={k} title={k === 'added' ? tr('Exceptions added') : tr('Exceptions gone')} count={diff.exceptions[k].length}>
                <table className="data"><thead><tr><th>{tr('Type')}</th><th>{tr('Item')}</th><th>{tr('Whs')}</th><th>{tr('Message')}</th></tr></thead><tbody>
                  {diff.exceptions[k].map((e) => <tr key={e.id}><td><Pill tone={EXCEPTION_META[e.type].tone}>{EXCEPTION_META[e.type].label}</Pill></td><td className="mono">{e.itemCode}</td><td>{e.warehouseCode}</td><td style={{ whiteSpace: 'normal' }}>{e.message}</td></tr>)}
                </tbody></table>
              </Section>
            ))}
            <Section title={tr('Exceptions changed')} count={diff.exceptions.changed.length}>
              <table className="data"><thead><tr><th>{tr('Type')}</th><th>{tr('Item')}</th><th>{tr('Whs')}</th><th>{tr('What changed')}</th></tr></thead><tbody>
                {diff.exceptions.changed.map((c) => (
                  <tr key={c.after.id}><td><Pill tone={EXCEPTION_META[c.after.type].tone}>{EXCEPTION_META[c.after.type].label}</Pill></td><td className="mono">{c.after.itemCode}</td><td>{c.after.warehouseCode}</td>
                    <td style={{ whiteSpace: 'normal' }}>
                      {c.fields.includes('proposed date') && <div>{tr('Proposed date')}: <Delta a={fmtDate(c.before.proposedDate)} b={fmtDate(c.after.proposedDate)} /></div>}
                      {c.fields.includes('quantity') && <div>{tr('Qty')}: <Delta a={fmtNum(c.before.quantity)} b={fmtNum(c.after.quantity)} /></div>}
                      {c.fields.includes('message') && <div>{c.after.message}</div>}
                    </td></tr>
                ))}
              </tbody></table>
            </Section>

            <Section title={tr('Capacity overloads')} count={diff.capacity.added.length + diff.capacity.removed.length + diff.capacity.changed.length}>
              <table className="data"><thead><tr><th>{tr('Work center')}</th><th>{tr('Week of')}</th><th>{tr('Status')}</th><th className="num">{tr('Required h')}</th><th className="num">{tr('Available h')}</th></tr></thead><tbody>
                {([['added', 'danger', 'New overload'], ['changed', 'warn', 'Still overloaded'], ['removed', 'ok', 'No longer overloaded']] as const).flatMap(([k, tone, label]) =>
                  diff.capacity[k].map((c) => (
                    <tr key={`${k}${c.workCenterCode}${c.bucketStart}`}>
                      <td className="mono">{c.workCenterCode}</td><td>{fmtDate(c.bucketStart)}</td><td><Pill tone={tone}>{tr(label)}</Pill></td>
                      <td className="num">{c.before && c.after ? <Delta a={fmtNum(c.before.requiredHours)} b={fmtNum(c.after.requiredHours)} /> : fmtNum((c.after ?? c.before)!.requiredHours)}</td>
                      <td className="num">{c.before && c.after ? <Delta a={fmtNum(c.before.availableHours)} b={fmtNum(c.after.availableHours)} /> : fmtNum((c.after ?? c.before)!.availableHours)}</td>
                    </tr>)))}
              </tbody></table>
            </Section>

            {diff.orders.added.length + diff.orders.removed.length + diff.orders.changed.length + diff.exceptions.added.length
              + diff.exceptions.removed.length + diff.exceptions.changed.length + diff.capacity.added.length + diff.capacity.removed.length + diff.capacity.changed.length === 0
              && <div className="card"><Empty title="These two runs are identical" /></div>}
          </>
        )}
      </div>
    </>
  );
}
