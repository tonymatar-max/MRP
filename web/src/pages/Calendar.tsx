import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useCalendar } from '../api/hooks';
import type { CalendarEvent, CalendarKind } from '../api/types';
import { Empty, ErrorState, PageHeader } from '../components/ui';
import { RunPicker } from '../components/RunPicker';
import { useSelectedRun } from '../components/RunContext';
import { fmtNum } from '../components/util';
import { useRuns } from '../api/hooks';

/** Planned-order ids are GUIDs: useful for the link, meaningless to read. */
const isGuid = (v: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);

const KINDS: { kind: CalendarKind; label: string }[] = [
  { kind: 'Demand', label: 'Demand' },
  { kind: 'Forecast', label: 'Forecast' },
  { kind: 'Supply', label: 'Supply' },
  { kind: 'PlannedRelease', label: 'Planned release' },
  { kind: 'PlannedDue', label: 'Planned due' },
  { kind: 'Exception', label: 'Exception' },
  { kind: 'CapacityOverload', label: 'Capacity overload' },
];
const KIND_LABEL = Object.fromEntries(KINDS.map((k) => [k.kind, k.label])) as Record<CalendarKind, string>;
const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

type View = 'month' | 'week' | 'agenda';

// ----- date helpers: all dates are 'YYYY-MM-DD' strings, math done in UTC to avoid DST drift -----
const parse = (iso: string) => new Date(iso + 'T00:00:00Z');
const iso = (d: Date) => d.toISOString().slice(0, 10);
const addDays = (s: string, n: number) => iso(new Date(parse(s).getTime() + n * 86400000));
const mondayOf = (s: string) => addDays(s, -((parse(s).getUTCDay() + 6) % 7));
const monthStart = (s: string) => s.slice(0, 8) + '01';
const addMonths = (s: string, n: number) => { const d = parse(monthStart(s)); d.setUTCMonth(d.getUTCMonth() + n); return iso(d); };
const monthEnd = (s: string) => addDays(addMonths(s, 1), -1);
const localToday = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };
const longDay = (s: string) => parse(s).toLocaleDateString(undefined, { day: 'numeric', month: 'long', timeZone: 'UTC' });
const fullDay = (s: string) => parse(s).toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
const monthTitle = (s: string) => parse(s).toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' });
const plural = (n: number) => `${n} event${n === 1 ? '' : 's'}`;

function eventLink(e: CalendarEvent): { to: string; text: string } | null {
  if (e.kind === 'PlannedRelease' || e.kind === 'PlannedDue') return { to: `/planned-orders?order=${encodeURIComponent(e.ref)}`, text: 'View order' };
  if (e.kind === 'Exception') return { to: '/exceptions', text: 'Exceptions' };
  if (e.kind === 'CapacityOverload') return { to: '/capacity', text: 'Capacity' };
  return null;
}

function useDebounced<T>(v: T, ms: number) {
  const [d, setD] = useState(v);
  useEffect(() => { const t = window.setTimeout(() => setD(v), ms); return () => window.clearTimeout(t); }, [v, ms]);
  return d;
}

export default function Calendar() {
  const { runId } = useSelectedRun();
  const runs = useRuns().data;
  const run = runs?.find((r) => r.id === runId);
  const today = localToday();

  const [view, setView] = useState<View>('month');
  const [cursor, setCursor] = useState(today); // any date inside the visible period
  const [kinds, setKinds] = useState<Set<CalendarKind>>(new Set(KINDS.map((k) => k.kind)));
  const [itemText, setItemText] = useState('');
  const item = useDebounced(itemText.trim(), 300);
  const [wh, setWh] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [knownWh, setKnownWh] = useState<string[]>([]);

  // Jump into the run's horizon when the run changes and today is outside it.
  useEffect(() => {
    if (!run) return;
    setSelected(null);
    setWh('');
    setKnownWh([]);
    setCursor((c) => (c < run.horizonStart ? run.horizonStart : c > run.horizonEnd ? run.horizonEnd : c));
  }, [run?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  const range = useMemo(() => {
    if (view === 'week') { const f = mondayOf(cursor); return { from: f, to: addDays(f, 6) }; }
    const ms = monthStart(cursor), me = monthEnd(cursor);
    if (view === 'agenda') return { from: ms, to: me };
    return { from: mondayOf(ms), to: addDays(mondayOf(me), 6) };
  }, [view, cursor]);

  const allKinds = kinds.size === KINDS.length;
  const q = useCalendar(runId, { from: range.from, to: range.to, kinds: allKinds ? undefined : [...kinds], item: item || undefined, warehouse: wh || undefined });
  const data = q.data;

  const byDate = useMemo(() => {
    const m = new Map<string, CalendarEvent[]>();
    for (const e of data?.events ?? []) { const l = m.get(e.date); if (l) l.push(e); else m.set(e.date, [e]); }
    return m;
  }, [data]);
  const nonWorking = useMemo(() => new Map((data?.nonWorking ?? []).map((n) => [n.date, n.reason])), [data]);

  useEffect(() => {
    if (!data) return;
    setKnownWh((prev) => {
      const s = new Set(prev);
      data.events.forEach((e) => e.warehouseCode && s.add(e.warehouseCode));
      return s.size === prev.length ? prev : [...s].sort();
    });
  }, [data]);

  const hStart = data?.horizonStart ?? run?.horizonStart;
  const hEnd = data?.horizonEnd ?? run?.horizonEnd;
  const outside = (d: string) => !!hStart && !!hEnd && (d < hStart || d > hEnd);

  // Navigation: only periods that touch the planning horizon.
  const step = (dir: -1 | 1) => {
    const next = view === 'week' ? addDays(cursor, 7 * dir) : addMonths(cursor, dir);
    setCursor(next);
  };
  const periodFrom = view === 'week' ? mondayOf(cursor) : monthStart(cursor);
  const periodTo = view === 'week' ? addDays(periodFrom, 6) : monthEnd(cursor);
  const canPrev = !hStart || addDays(periodFrom, -1) >= hStart;
  const canNext = !hEnd || addDays(periodTo, 1) <= hEnd;
  const goToday = () => setCursor(hStart && hEnd && (today < hStart || today > hEnd) ? hStart : today);

  const toggleKind = (k: CalendarKind) => setKinds((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });

  const days = useMemo(() => {
    const out: string[] = [];
    for (let d = range.from; d <= range.to; d = addDays(d, 1)) out.push(d);
    return out;
  }, [range]);

  const title = view === 'week' ? `${longDay(range.from)} – ${longDay(range.to)} ${range.to.slice(0, 4)}` : monthTitle(cursor);
  const dayEvents = selected ? byDate.get(selected) ?? [] : [];
  const showOutsideNote = !!hStart && !!hEnd && (range.from < hStart || range.to > hEnd);

  const cell = (d: string, inMonth: boolean) => {
    const ev = byDate.get(d) ?? [];
    const counts = new Map<CalendarKind, number>();
    let danger = false;
    for (const e of ev) { counts.set(e.kind, (counts.get(e.kind) ?? 0) + 1); if (e.kind === 'CapacityOverload' || e.severity === 'danger') danger = true; }
    const nw = nonWorking.get(d);
    const out = outside(d);
    const cls = ['cal-cell', nw ? 'nonwork' : '', d === today ? 'today' : '', inMonth ? '' : 'other', out ? 'outside' : '', selected === d ? 'sel' : ''].join(' ');
    const label = `${longDay(d)}, ${plural(ev.length)}${nw ? `, non-working: ${nw}` : ''}${danger ? ', has overload or critical items' : ''}${d === today ? ', today' : ''}`;
    return (
      <button key={d} type="button" className={cls} aria-label={label} aria-pressed={selected === d} title={nw ? `Non-working day: ${nw}` : undefined}
        onClick={() => setSelected(selected === d ? null : d)}>
        <span className="cal-num">{Number(d.slice(8))}</span>
        {danger && <span className="cal-danger" aria-hidden="true" />}
        {nw && <span className="cal-nw" aria-hidden="true">{nw}</span>}
        <span className="cal-chips" aria-hidden="true">
          {KINDS.filter((k) => counts.has(k.kind)).map((k) => <span key={k.kind} className={`kchip k-${k.kind}`} title={`${k.label}: ${counts.get(k.kind)}`}>{counts.get(k.kind)}</span>)}
        </span>
      </button>
    );
  };

  return (
    <>
      <PageHeader crumb="Planning" title="Calendar" actions={<RunPicker />} />
      <div className="page">
        <div className="toolbar cal-toolbar">
          <div className="cal-nav">
            <button type="button" className="btn" onClick={() => step(-1)} disabled={!canPrev} aria-label={view === 'week' ? 'Previous week' : 'Previous month'}>‹</button>
            <button type="button" className="btn" onClick={goToday}>Today</button>
            <button type="button" className="btn" onClick={() => step(1)} disabled={!canNext} aria-label={view === 'week' ? 'Next week' : 'Next month'}>›</button>
            <h2 className="cal-title" aria-live="polite">{title}</h2>
          </div>
          <div className="spacer" />
          <div className="seg" role="group" aria-label="Calendar view">
            {(['month', 'week', 'agenda'] as View[]).map((v) => (
              <button key={v} type="button" className={`seg-btn ${view === v ? 'on' : ''}`} aria-pressed={view === v} onClick={() => setView(v)}>{v[0].toUpperCase() + v.slice(1)}</button>
            ))}
          </div>
          <div className="field"><label htmlFor="cal-item">Item</label><input id="cal-item" className="search" placeholder="Search item…" value={itemText} onChange={(e) => setItemText(e.target.value)} /></div>
          <div className="field"><label htmlFor="cal-wh">Warehouse</label>
            <select id="cal-wh" value={wh} onChange={(e) => setWh(e.target.value)}>
              <option value="">All</option>
              {(wh && !knownWh.includes(wh) ? [...knownWh, wh] : knownWh).map((w) => <option key={w}>{w}</option>)}
            </select></div>
        </div>

        <div className="chips cal-filters" role="group" aria-label="Event kinds">
          {KINDS.map((k) => (
            <button key={k.kind} type="button" className={`chip kind-chip k-${k.kind} ${kinds.has(k.kind) ? 'on' : ''}`} aria-pressed={kinds.has(k.kind)} onClick={() => toggleKind(k.kind)}>
              <span className="kdot" aria-hidden="true" />{k.label}
            </button>
          ))}
          {!allKinds && <button type="button" className="btn sm ghost" onClick={() => setKinds(new Set(KINDS.map((k) => k.kind)))}>Show all</button>}
        </div>

        {data?.truncated && <div className="alert warn" role="status" style={{ marginBottom: 12 }}>Too many events to show here: the list was cut off. Narrow the filters (kinds, item or warehouse) or switch to a shorter range.</div>}
        {showOutsideNote && <div className="cal-note" role="note">Days outside the run horizon ({hStart} to {hEnd}) are muted: the run has no plan for them.</div>}
        {q.isError && <ErrorState error={q.error} />}
        {!runId && <div className="card"><Empty title="Select an MRP run to see its calendar" /></div>}

        {runId && (
          <div className={`cal-layout ${selected ? 'with-panel' : ''}`}>
            <div className={`card cal-card ${q.isFetching ? 'busy' : ''}`}>
              {view === 'agenda' ? (
                <ol className="agenda" aria-label={`Agenda for ${title}`}>
                  {byDate.size === 0 && !q.isLoading && <li className="center-state">No events in this month for the current filters.</li>}
                  {days.filter((d) => byDate.has(d)).map((d) => {
                    const ev = byDate.get(d)!;
                    const nw = nonWorking.get(d);
                    return (
                      <li key={d} className={`agenda-day ${nw ? 'nonwork' : ''} ${d === today ? 'today' : ''}`}>
                        <button type="button" className="agenda-head" aria-label={`${longDay(d)}, ${plural(ev.length)}`} onClick={() => setSelected(selected === d ? null : d)}>
                          <strong>{fullDay(d)}</strong>
                          {nw && <span className="muted">{nw}</span>}
                          <span className="cal-chips">
                            {KINDS.filter((k) => ev.some((e) => e.kind === k.kind)).map((k) => <span key={k.kind} className={`kchip k-${k.kind}`} title={k.label}>{ev.filter((e) => e.kind === k.kind).length}</span>)}
                          </span>
                        </button>
                        <ul className="agenda-list">
                          {ev.map((e) => <li key={e.id}><EventRow e={e} /></li>)}
                        </ul>
                      </li>
                    );
                  })}
                </ol>
              ) : (
                <div className={`cal-grid ${view}`} role="group" aria-label={`Calendar, ${title}`}>
                  {WEEKDAYS.map((w) => <div key={w} className="cal-dow" aria-hidden="true">{w}</div>)}
                  {days.map((d) => cell(d, view === 'week' || d.slice(0, 7) === cursor.slice(0, 7)))}
                </div>
              )}
              {q.isLoading && <div className="cal-loading"><div className="spinner" />Loading…</div>}
            </div>

            {selected && (
              <aside className="card day-panel" aria-label={`Events on ${fullDay(selected)}`}>
                <div className="card-h">
                  <h3>{fullDay(selected)}</h3>
                  <button type="button" className="btn sm" onClick={() => setSelected(null)}>Close</button>
                </div>
                <div className="card-b">
                  {nonWorking.has(selected) && <div className="alert warn" style={{ marginBottom: 12 }}>Non-working day: {nonWorking.get(selected)}</div>}
                  {outside(selected) && <div className="muted" style={{ marginBottom: 12 }}>Outside the run horizon.</div>}
                  {dayEvents.length === 0 && <p className="muted" style={{ margin: 0 }}>No events on this day{allKinds && !item && !wh ? '.' : ' for the current filters.'}</p>}
                  {KINDS.filter((k) => dayEvents.some((e) => e.kind === k.kind)).map((k) => (
                    <section key={k.kind} className="day-group">
                      <h4><span className={`kchip k-${k.kind}`}>{dayEvents.filter((e) => e.kind === k.kind).length}</span> {k.label}</h4>
                      <ul>{dayEvents.filter((e) => e.kind === k.kind).map((e) => <li key={e.id}><EventRow e={e} /></li>)}</ul>
                    </section>
                  ))}
                </div>
              </aside>
            )}
          </div>
        )}

        <div className="cal-legend" aria-label="Legend">
          {KINDS.map((k) => <span key={k.kind} className="legend-item"><span className={`kchip k-${k.kind}`} aria-hidden="true">●</span>{k.label}</span>)}
          <span className="legend-item"><span className="legend-box nonwork" />Non-working day</span>
          <span className="legend-item"><span className="legend-box today" />Today</span>
          <span className="legend-item"><span className="legend-corner" />Overload / critical</span>
        </div>
      </div>
    </>
  );
}

function EventRow({ e }: { e: CalendarEvent }) {
  const link = eventLink(e);
  return (
    <div className={`ev-row sev-${e.severity}`}>
      <div className="ev-main">
        {e.itemCode && <span className="mono">{e.itemCode}</span>}
        {e.warehouseCode && <span className="muted"> · {e.warehouseCode}</span>}
        {e.quantity > 0 && <span> · {fmtNum(e.quantity)}</span>}
      </div>
      <div className="ev-label">{e.label}</div>
      <div className="ev-ref">
        {e.ref && e.ref !== 'FCST' && !isGuid(e.ref) && <span className="muted">{KIND_LABEL[e.kind] === 'Exception' ? '' : e.ref}</span>}
        {link && <Link to={link.to}>{link.text} →</Link>}
      </div>
    </div>
  );
}
