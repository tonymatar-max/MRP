import { Component, FormEvent, ReactNode, useEffect, useMemo, useRef, useState } from 'react';
import { Navigate, useSearchParams } from 'react-router-dom';
import { API_BASE, USE_MOCK, api } from '../api/client';
import {
  useAutomationStatus, useSchedules, useCreateUser, useDeleteUser, useRunTemplates, useSaveSettings, useSetUserPassword, useSettings, useUsers,
} from '../api/hooks';
import type {
  AiSettings, AppSettings, NotificationSettings, ScheduleSettings, ScheduleView, DataSource, NonWorkingDate, ResourceInfo, Weekday, DbSettings, ItemVendor, OkResult, PlanningSettings, Role, ServiceLayerSettings,
} from '../api/types';
import { useAuth } from '../components/Auth';
import { PriorityControl } from '../components/PriorityControl';
import { LicencePanel } from '../components/Licence';
import { BackupPanel } from '../components/BackupPanel';
import { normaliseSettings } from '../api/normalise';
import { Dialog, ErrorState, Loading, PageHeader, Pill } from '../components/ui';

type TabId = 'cobalt' | 'b1' | 'sl' | 'planning' | 'automation' | 'ai' | 'users' | 'licence' | 'backup';
const TABS: { id: TabId; label: string }[] = [
  { id: 'cobalt', label: 'Cobalt database' },
  { id: 'b1', label: 'SAP B1 database' },
  { id: 'sl', label: 'Service Layer' },
  { id: 'planning', label: 'Planning defaults' },
  { id: 'automation', label: 'Automation' },
  { id: 'ai', label: 'AI' },
  { id: 'users', label: 'Users' },
  { id: 'licence', label: 'Licence' },
  { id: 'backup', label: 'Backup' },
];
// These tabs save on their own (not through "Save settings"), so the unsaved-changes bar is hidden on them.
const OWN_SAVE: TabId[] = ['users', 'licence', 'backup'];

// ---------- a section that fails to render must not blank the whole page ----------
class SectionBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  render() {
    return this.state.error
      ? <div className="alert danger" role="alert">This section could not be shown: {this.state.error.message}</div>
      : this.props.children;
  }
}

// ---------- shared bits ----------
function ResultBox({ r }: { r: OkResult | null }) {
  if (!r) return null;
  return <div className={`alert ${r.ok ? 'ok' : 'danger'}`} role="status"><strong>{r.ok ? 'Success. ' : 'Failed. '}</strong>{r.message}</div>;
}

function Check({ id, label, checked, onChange, hint }: { id: string; label: string; checked: boolean; onChange: (v: boolean) => void; hint?: ReactNode }) {
  return (
    <div className="check-row">
      <input id={id} type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <label htmlFor={id}>{label}{hint && <span className="hint">{hint}</span>}</label>
    </div>
  );
}

function Num({ id, label, value, min, max, onChange, hint }: { id: string; label: string; value: number; min?: number; max?: number; onChange: (n: number) => void; hint?: string }) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="number" min={min} max={max} value={Number.isFinite(value) ? value : ''} onChange={(e) => onChange(e.target.value === '' ? NaN : Number(e.target.value))} />
      {hint && <span className="hint">{hint}</span>}
    </div>
  );
}

function PasswordField({ id, label, value, isSet, onChange, noun = 'password' }: { id: string; label: string; value: string; isSet: boolean; onChange: (v: string) => void; noun?: string }) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="password" autoComplete="new-password" value={value} placeholder={isSet ? 'unchanged (stored)' : ''} onChange={(e) => onChange(e.target.value)} />
      <span className="hint">{isSet ? `A ${noun} is stored on the server. Leave empty to keep it.` : `No ${noun} stored yet.`}</span>
    </div>
  );
}

function SectionCard({ title, badge, children, actions }: { title: string; badge?: ReactNode; children: ReactNode; actions?: ReactNode }) {
  return (
    <div className="card settings-card">
      <div className="card-h"><h3>{title}</h3>{badge}</div>
      <div className="card-b form-stack">{children}</div>
      {actions && <div className="card-f">{actions}</div>}
    </div>
  );
}

/** Runs a connection test with the CURRENT unsaved values and shows the result inline. */
function useProbe(run: () => Promise<OkResult>) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OkResult | null>(null);
  const go = async () => {
    setBusy(true); setResult(null);
    try { setResult(await run()); }
    catch (e) { setResult({ ok: false, message: (e as Error).message }); }
    finally { setBusy(false); }
  };
  return { busy, result, go, clear: () => setResult(null) };
}

// ---------- database sections ----------
function DbSection({ id, title, value, onChange, test, extra, note }: {
  id: string; title: string; value: DbSettings; onChange: (v: DbSettings) => void;
  test: (d: DbSettings) => Promise<OkResult>; extra?: ReactNode; note?: string;
}) {
  const set = <K extends keyof DbSettings>(k: K, v: DbSettings[K]) => onChange({ ...value, [k]: v });
  const probe = useProbe(() => test(value));
  const schema = useProbe(() => api.initSchema());
  const sql = value.authentication === 'Sql';
  return (
    <SectionCard
      title={title}
      actions={
        <>
          <button type="button" className="btn" disabled={probe.busy} onClick={probe.go}>{probe.busy ? 'Testing…' : 'Test connection'}</button>
          {extra && <button type="button" className="btn" disabled={schema.busy} onClick={schema.go}>{schema.busy ? 'Working…' : 'Create / update tables'}</button>}
          <span className="hint grow">{extra ? 'Tests use the values below, saved or not. Save first before creating tables.' : 'Tests use the values below, saved or not.'}</span>
        </>
      }
    >
      {note && <p className="muted" style={{ margin: 0 }}>{note}</p>}
      <div className="form-row">
        <div className="field"><label htmlFor={`${id}-server`}>Server</label>
          <input id={`${id}-server`} value={value.server} placeholder="host, host\instance or host,port" onChange={(e) => set('server', e.target.value)} /></div>
        <div className="field"><label htmlFor={`${id}-db`}>Database</label>
          <input id={`${id}-db`} value={value.database} onChange={(e) => set('database', e.target.value)} /></div>
      </div>
      <div className="field" style={{ maxWidth: 260 }}>
        <label htmlFor={`${id}-auth`}>Authentication</label>
        <select id={`${id}-auth`} value={value.authentication} onChange={(e) => set('authentication', e.target.value as DbSettings['authentication'])}>
          <option value="Windows">Windows (service account)</option>
          <option value="Sql">SQL Server login</option>
        </select>
      </div>
      {sql && (
        <div className="form-row">
          <div className="field"><label htmlFor={`${id}-user`}>User</label>
            <input id={`${id}-user`} autoComplete="off" value={value.userName} onChange={(e) => set('userName', e.target.value)} /></div>
          <PasswordField id={`${id}-pass`} label="Password" value={value.password} isSet={value.passwordSet} onChange={(v) => set('password', v)} />
        </div>
      )}
      <Check id={`${id}-trust`} label="Trust server certificate" checked={value.trustServerCertificate} onChange={(v) => set('trustServerCertificate', v)} />
      <details className="advanced">
        <summary>Advanced: connection string override</summary>
        <div className="field">
          <label htmlFor={`${id}-cs`}>Connection string</label>
          <textarea id={`${id}-cs`} rows={3} className="mono" value={value.connectionStringOverride} onChange={(e) => set('connectionStringOverride', e.target.value)} />
          <span className="hint">When set, this is used instead of the fields above. It may contain a password: treat it as a secret.</span>
        </div>
      </details>
      <ResultBox r={probe.result} />
      <ResultBox r={schema.result} />
    </SectionCard>
  );
}

// ---------- Service Layer ----------
function ServiceLayerSection({ value, onChange }: { value: ServiceLayerSettings; onChange: (v: ServiceLayerSettings) => void }) {
  const set = <K extends keyof ServiceLayerSettings>(k: K, v: ServiceLayerSettings[K]) => onChange({ ...value, [k]: v });
  const probe = useProbe(() => api.testServiceLayer(value));
  const setVendor = (i: number, patch: Partial<ItemVendor>) =>
    set('itemVendors', value.itemVendors.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  return (
    <>
      <SectionCard
        title="SAP B1 Service Layer"
        badge={<Pill tone={value.enabled ? 'warn' : undefined}>{value.enabled ? 'Write-back on' : 'Write-back off'}</Pill>}
        actions={
          <>
            <button type="button" className="btn" disabled={probe.busy} onClick={probe.go}>{probe.busy ? 'Testing…' : 'Test connection'}</button>
            <span className="hint grow">Tests use the values below, saved or not.</span>
          </>
        }
      >
        <Check id="sl-enabled" label="Enable release to SAP B1 (write-back)" checked={value.enabled} onChange={(v) => set('enabled', v)} />
        {value.enabled && <div className="alert warn" role="alert">Release will create real purchase/production orders in SAP B1.</div>}
        <div className="field"><label htmlFor="sl-url">Base URL</label>
          <input id="sl-url" value={value.baseUrl} placeholder="https://server:50000/b1s/v1" onChange={(e) => set('baseUrl', e.target.value)} /></div>
        <div className="form-row">
          <div className="field"><label htmlFor="sl-company">Company DB</label>
            <input id="sl-company" value={value.companyDb} onChange={(e) => set('companyDb', e.target.value)} /></div>
          <div className="field"><label htmlFor="sl-user">User</label>
            <input id="sl-user" autoComplete="off" value={value.userName} onChange={(e) => set('userName', e.target.value)} /></div>
        </div>
        <div className="form-row">
          <PasswordField id="sl-pass" label="Password" value={value.password} isSet={value.passwordSet} onChange={(v) => set('password', v)} />
          <div className="field"><label htmlFor="sl-vendor">Default vendor (CardCode)</label>
            <input id="sl-vendor" value={value.defaultVendor} onChange={(e) => set('defaultVendor', e.target.value)} />
            <span className="hint">Used only when an item has no preferred vendor in SAP B1 and no override below.</span></div>
        </div>
        <Check id="sl-cert" label="Allow invalid certificates" checked={value.allowInvalidCertificates} onChange={(v) => set('allowInvalidCertificates', v)} hint="Development only. Disables TLS validation for the Service Layer." />
        {value.allowInvalidCertificates && <div className="alert warn" role="alert">Development only: do not accept invalid certificates in production.</div>}
        <ResultBox r={probe.result} />
      </SectionCard>

      <SectionCard title="Vendor overrides (legacy, win over the B1 preferred vendor)" badge={<span className="muted">{value.itemVendors.length} mapped</span>}
        actions={<button type="button" className="btn sm" onClick={() => set('itemVendors', [...value.itemVendors, { itemCode: '', cardCode: '' }])}>+ Add mapping</button>}>
        <div className="alert info-note" role="note">Prefer Master data &gt; Items; entries here still win over everything.</div>
        {value.itemVendors.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>No overrides. Purchase orders use the B1 preferred vendor, then the default vendor.</p>
        ) : (
          <div className="table-wrap">
            <table className="data edit-table">
              <thead><tr><th className="nosort">Item code</th><th className="nosort">Vendor (CardCode)</th><th className="nosort"><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {value.itemVendors.map((r, i) => (
                  <tr key={i}>
                    <td><input aria-label={`Item code, row ${i + 1}`} value={r.itemCode} onChange={(e) => setVendor(i, { itemCode: e.target.value })} /></td>
                    <td><input aria-label={`Vendor CardCode, row ${i + 1}`} value={r.cardCode} onChange={(e) => setVendor(i, { cardCode: e.target.value })} /></td>
                    <td><button type="button" className="btn sm" aria-label={`Remove row ${i + 1}`} onClick={() => set('itemVendors', value.itemVendors.filter((_, j) => j !== i))}>Remove</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </SectionCard>
    </>
  );
}

// ---------- AI model picker (several models: the first is primary, the rest are fallbacks) ----------
const MAX_MODELS = 5;
const parseModels = (v: string) => v.split(/[,;\n]/).map((x) => x.trim()).filter(Boolean);

function ModelPicker({ value, models, onChange }: { value: string; models: string[]; onChange: (v: string) => void }) {
  const chosen = parseModels(value);
  const [q, setQ] = useState('');
  const commit = (list: string[]) => onChange(list.join(', '));
  const add = (id: string) => {
    const m = id.trim();
    if (!m || chosen.some((c) => c.toLowerCase() === m.toLowerCase()) || chosen.length >= MAX_MODELS) return;
    commit([...chosen, m]); setQ('');
  };
  const move = (i: number, d: number) => {
    const j = i + d; if (j < 0 || j >= chosen.length) return;
    const next = chosen.slice(); [next[i], next[j]] = [next[j], next[i]]; commit(next);
  };
  const remove = (i: number) => commit(chosen.filter((_, k) => k !== i));
  const term = q.trim().toLowerCase();
  const hits = term
    ? models.filter((m) => m.toLowerCase().includes(term) && !chosen.some((c) => c.toLowerCase() === m.toLowerCase())).slice(0, 8)
    : [];
  return (
    <div className="field model-picker">
      <label htmlFor="ai-model-add">Models</label>
      <ol className="model-chips" aria-label="Selected models">
        {chosen.length === 0 && <li className="hint">No model chosen: the default is used.</li>}
        {chosen.map((m, i) => (
          <li key={m} className="model-chip">
            <span className="mc-name">{m}</span>
            <span className="tag">{i === 0 ? 'Primary' : `Fallback ${i}`}</span>
            <button type="button" className="icon" aria-label={`Move ${m} up`} disabled={i === 0} onClick={() => move(i, -1)}>▲</button>
            <button type="button" className="icon" aria-label={`Move ${m} down`} disabled={i === chosen.length - 1} onClick={() => move(i, 1)}>▼</button>
            <button type="button" className="icon" aria-label={`Remove ${m}`} onClick={() => remove(i)}>✕</button>
          </li>
        ))}
      </ol>
      <div className="model-add">
        <input id="ai-model-add" autoComplete="off" value={q} placeholder={chosen.length >= MAX_MODELS ? `Up to ${MAX_MODELS} models` : 'Search loaded models or type an id, then press Enter'}
          disabled={chosen.length >= MAX_MODELS}
          onChange={(e) => setQ(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(hits[0] && !q.includes('/') ? hits[0] : q); } }} />
        <button type="button" className="btn sm" disabled={!q.trim() || chosen.length >= MAX_MODELS} onClick={() => add(q)}>Add</button>
      </div>
      {hits.length > 0 && (
        <ul className="model-hits" role="listbox" aria-label="Matching models">
          {hits.map((m) => <li key={m}><button type="button" role="option" aria-selected="false" onClick={() => add(m)}>{m}</button></li>)}
        </ul>
      )}
      <span className="hint">Any OpenRouter model that supports tool calling. The primary is used first; if it is down, rate-limited or refuses, OpenRouter tries the fallbacks in order. Up to {MAX_MODELS}.</span>
    </div>
  );
}

// ---------- Automation: scheduled runs and new-exception emails ----------
const fmtWhen = (iso?: string | null) => (iso ? new Date(iso).toLocaleString() : '—');

const MAX_SCHEDULES = 20;

/** One scheduled run: template, time, days and what it did last. Saved status comes from the server, edits stay in the draft. */
function ScheduleCard({ s, index, view, saved, templates, onChange, onRemove, onRun, running, result }: {
  s: ScheduleSettings; index: number; view?: ScheduleView; saved: boolean;
  templates: { id: string; name: string }[]; onChange: (v: ScheduleSettings) => void; onRemove: () => void;
  onRun: () => void; running: boolean; result?: OkResult | null;
}) {
  const set = <K extends keyof ScheduleSettings>(k: K, v: ScheduleSettings[K]) => onChange({ ...s, [k]: v });
  const toggleDay = (d: Weekday) => set('days', s.days.includes(d) ? s.days.filter((x) => x !== d) : [...s.days, d]);
  const missing = !!s.templateId && !templates.some((t) => t.id === s.templateId);
  const st = view?.status;
  const key = `sch-${index}`;
  const badTime = !/^([01]\d|2[0-3]):[0-5]\d$/.test(s.time);
  return (
    <div className="card settings-card sched-card" data-testid="schedule-card">
      <div className="card-h">
        <h3>{s.name.trim() || `Schedule ${index + 1}`}</h3>
        <Pill tone={s.enabled ? 'info' : undefined}>{s.enabled ? 'On' : 'Off'}</Pill>
        <span className="spacer" />
        <button type="button" className="btn sm" onClick={onRemove} aria-label={`Remove schedule ${index + 1}`}>Remove</button>
      </div>
      <div className="card-b form-stack">
        <Check id={`${key}-on`} label="Run this schedule" checked={s.enabled} onChange={(v) => set('enabled', v)} />
        <div className="form-row">
          <div className="field"><label htmlFor={`${key}-name`}>Name (optional)</label>
            <input id={`${key}-name`} value={s.name} maxLength={60} placeholder="e.g. Nightly full plan" onChange={(e) => set('name', e.target.value)} /></div>
          <div className="field"><label htmlFor={`${key}-tpl`}>Template</label>
            <select id={`${key}-tpl`} value={s.templateId} aria-invalid={missing || (s.enabled && !s.templateId)} onChange={(e) => set('templateId', e.target.value)}>
              <option value="">{s.enabled && !s.templateId && saved ? 'Settings defaults (older schedule: choose a template)' : 'Choose a saved template…'}</option>
              {missing && <option value={s.templateId}>(template missing)</option>}
              {templates.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select></div>
          <div className="field"><label htmlFor={`${key}-time`}>Time of day</label>
            <input id={`${key}-time`} type="time" value={s.time} aria-invalid={badTime} onChange={(e) => set('time', e.target.value)} /></div>
        </div>
        {missing && <div className="alert danger" role="alert">This template was deleted. The schedule is skipped (status: failed) until you choose another template.</div>}
        {!missing && s.enabled && !s.templateId && !saved && <div className="alert warn" role="alert">Choose a template: a schedule needs one to run.</div>}
        <div role="group" aria-label={`Days for schedule ${index + 1}`}>
          <div className="chips">
            {DAYS.map((d) => <button key={d} type="button" className={`chip ${s.days.includes(d) ? 'on' : ''}`} aria-pressed={s.days.includes(d)} onClick={() => toggleDay(d)}>{d}</button>)}
          </div>
          {s.enabled && s.days.length === 0 && <span className="hint err">Choose at least one day.</span>}
        </div>
      </div>
      <div className="card-f sched-status">
        {saved && st ? (
          <span className="grow" data-testid="schedule-status">
            <strong>Next:</strong> {s.enabled && st.nextDueAt ? fmtWhen(st.nextDueAt) : 'not scheduled'}
            {' · '}<strong>Last:</strong> {st.lastFiredAt ? <>{fmtWhen(st.lastFiredAt)} <Pill tone={st.lastOk ? 'ok' : 'danger'}>{st.lastOk ? 'OK' : 'Failed'}</Pill> <span className="muted">{st.lastMessage}</span></> : 'never'}
          </span>
        ) : <span className="hint grow">{saved ? 'Loading status…' : 'Save the settings to see its status and to use Run now.'}</span>}
        <button type="button" className="btn" disabled={!saved || running || view?.running} onClick={onRun}
          title={saved ? 'Runs MRP now with this schedule\'s saved template' : 'Save the settings first'}>{running || view?.running ? 'Running…' : 'Run now'}</button>
      </div>
      {result && <div className="card-f"><ResultBox r={result} /></div>}
    </div>
  );
}

function AutomationSection({ schedules, notifications, savedIds, onSchedules, onNotifications }: {
  schedules: ScheduleSettings[]; notifications: NotificationSettings; savedIds: Set<string>;
  onSchedules: (v: ScheduleSettings[]) => void; onNotifications: (v: NotificationSettings) => void;
}) {
  const setN = <K extends keyof NotificationSettings>(k: K, v: NotificationSettings[K]) => onNotifications({ ...notifications, [k]: v });
  const templatesQ = useRunTemplates();
  const templates = templatesQ.data ?? [];
  const views = useSchedules();
  const status = useAutomationStatus();
  const probe = useProbe(() => api.testEmail(notifications));
  const [runBusy, setRunBusy] = useState<string | null>(null);
  const [runResult, setRunResult] = useState<Record<string, OkResult>>({});
  const st = status.data;
  const viewOf = (id: string) => views.data?.find((v) => v.id === id);
  const blank = (templateId = ''): ScheduleSettings => ({ id: '', name: '', enabled: false, time: '02:00', days: [...DAYS], templateId });
  const runNow = async (id: string) => {
    setRunBusy(id);
    try { const r = await api.runScheduleNow(id); setRunResult((m) => ({ ...m, [id]: { ok: r.ok, message: r.message } })); }
    catch (e) { setRunResult((m) => ({ ...m, [id]: { ok: false, message: (e as Error).message } })); }
    finally { setRunBusy(null); void views.refetch(); }
  };
  return (
    <>
      <div className="sched-head">
        <div>
          <h3 className="sched-title">Scheduled runs</h3>
          <p className="muted">Each schedule runs one saved Run MRP template at its own time on its own days (server local time). Runs are named "template name · date time" and started by "scheduler". Schedules run one at a time; a run missed because the service was stopped for more than 6 hours is skipped and recorded.</p>
        </div>
        <button type="button" className="btn primary" disabled={schedules.length >= MAX_SCHEDULES} onClick={() => onSchedules([...schedules, blank()])}
          title={schedules.length >= MAX_SCHEDULES ? `At most ${MAX_SCHEDULES} schedules` : undefined}>Add schedule</button>
      </div>
      {views.isError && <div className="alert danger" role="alert">Could not load the schedule status: {(views.error as Error).message}</div>}
      {templatesQ.isError && <div className="alert danger" role="alert">Could not load the saved templates: {(templatesQ.error as Error).message}</div>}
      {schedules.length === 0 && (
        <div className="card settings-card"><div className="card-b empty-note" data-testid="schedules-empty">
          <strong>No scheduled runs yet.</strong>
          <p className="muted">{templates.length === 0
            ? 'Save your options as a template in the Run MRP wizard first, then add a schedule for it here.'
            : 'Add a schedule to run MRP automatically with one of your saved templates.'}</p>
        </div></div>
      )}
      {schedules.map((sc, i) => (
        <ScheduleCard key={sc.id || `new-${i}`} s={sc} index={i} view={viewOf(sc.id)} saved={!!sc.id && savedIds.has(sc.id)} templates={templates}
          onChange={(v) => onSchedules(schedules.map((x, j) => (j === i ? v : x)))}
          onRemove={() => onSchedules(schedules.filter((_, j) => j !== i))}
          onRun={() => void runNow(sc.id)} running={runBusy === sc.id} result={runResult[sc.id]} />
      ))}

      <SectionCard
        title="New-exception emails"
        badge={<Pill tone={notifications.enabled ? 'info' : undefined}>{notifications.enabled ? 'On' : 'Off'}</Pill>}
        actions={
          <>
            <button type="button" className="btn" disabled={probe.busy} onClick={probe.go}>{probe.busy ? 'Sending…' : 'Send test email'}</button>
            <span className="hint grow">The test uses the values below, saved or not.</span>
          </>
        }
      >
        <Check id="ntf-on" label="Email me when a run has new exceptions" checked={notifications.enabled} onChange={(v) => setN('enabled', v)}
          hint="Off by default. 'New' means the run before it did not have that exception. Nothing is sent when there is nothing new." />
        <div className="form-row">
          <div className="field"><label htmlFor="ntf-host">SMTP server</label>
            <input id="ntf-host" value={notifications.smtpHost} placeholder="smtp.example.com" onChange={(e) => setN('smtpHost', e.target.value)} /></div>
          <Num id="ntf-port" label="Port" min={1} max={65535} value={notifications.smtpPort} onChange={(n) => setN('smtpPort', n)} hint="587 (STARTTLS) is typical." />
        </div>
        <Check id="ntf-ssl" label="Secure the connection (TLS)" checked={notifications.useSsl} onChange={(v) => setN('useSsl', v)}
          hint="Uses STARTTLS. Implicit TLS on port 465 is not supported." />
        <div className="form-row">
          <div className="field"><label htmlFor="ntf-user">User name</label>
            <input id="ntf-user" autoComplete="off" value={notifications.userName} onChange={(e) => setN('userName', e.target.value)} /></div>
          <PasswordField id="ntf-pass" label="Password" value={notifications.password} isSet={notifications.passwordSet} onChange={(v) => setN('password', v)} />
        </div>
        <div className="form-row">
          <div className="field"><label htmlFor="ntf-from">From address</label>
            <input id="ntf-from" type="email" value={notifications.from} placeholder="mrp@yourcompany.com" onChange={(e) => setN('from', e.target.value)} /></div>
          <div className="field"><label htmlFor="ntf-url">Link in the email (optional)</label>
            <input id="ntf-url" value={notifications.publicUrl} placeholder="https://mrp.yourcompany.com" onChange={(e) => setN('publicUrl', e.target.value)} />
            <span className="hint">The email links to this address followed by /exceptions.</span></div>
        </div>
        <div className="field"><label htmlFor="ntf-to">Recipients</label>
          <textarea id="ntf-to" rows={3} value={notifications.recipients} placeholder={'planner@yourcompany.com\nbuyer@yourcompany.com: purchasing\nplantmanager@yourcompany.com: production+capacity'} onChange={(e) => setN('recipients', e.target.value)} />
          <span className="hint">Separate addresses with commas or new lines. An address alone gets every new exception; add ": purchasing", ": production", ": capacity" or ": general" to send that person only that area (join several with +). Purchasing = purchase orders, requests and transfers; production = work orders; capacity = work-center overloads; general = the rest, such as past-due demand.</span></div>
        <div className="field"><label htmlFor="ntf-when">Send for</label>
          <select id="ntf-when" value={notifications.notifyOn} onChange={(e) => setN('notifyOn', e.target.value as NotificationSettings['notifyOn'])}>
            <option value="ScheduledRuns">Scheduled runs only (recommended)</option>
            <option value="AllRuns">Every run, including ones started by hand</option>
          </select></div>
        <ResultBox r={probe.result} />
        {st?.lastNotice?.at && (
          <dl className="auto-status"><div><dt>Last email</dt><dd>{fmtWhen(st.lastNotice.at)} · {st.lastNotice.ok ? 'OK' : 'Problem'}<div className="muted">{st.lastNotice.message}</div></dd></div></dl>
        )}
      </SectionCard>
    </>
  );
}

// ---------- AI assistant ----------
function AiSection({ value, onChange }: { value: AiSettings; onChange: (v: AiSettings) => void }) {
  const set = <K extends keyof AiSettings>(k: K, v: AiSettings[K]) => onChange({ ...value, [k]: v });
  const probe = useProbe(() => api.testAi(value));
  const [models, setModels] = useState<string[]>([]);
  const [modelsBusy, setModelsBusy] = useState(false);
  const [modelsMsg, setModelsMsg] = useState<OkResult | null>(null);
  const loadModels = async () => {
    setModelsBusy(true); setModelsMsg(null);
    try {
      const r = await api.listAiModels(value);
      setModels(r.models); setModelsMsg({ ok: r.ok, message: r.message });
    } catch (e) { setModelsMsg({ ok: false, message: (e as Error).message }); }
    finally { setModelsBusy(false); }
  };
  return (
    <SectionCard
      title="AI assistant (OpenRouter)"
      badge={<Pill tone={value.enabled ? 'info' : undefined}>{value.enabled ? 'Enabled' : 'Off'}</Pill>}
      actions={
        <>
          <button type="button" className="btn" disabled={probe.busy} onClick={probe.go}>{probe.busy ? 'Testing…' : 'Test connection'}</button>
          <span className="hint grow">Tests use the values below, saved or not. The test sends one tiny request.</span>
        </>
      }
    >
      <Check id="ai-enabled" label="Enable the AI assistant" checked={value.enabled} onChange={(v) => set('enabled', v)}
        hint="Off by default. Planners and administrators can then ask questions about a saved run and get a plain-language summary." />
      <div className="alert info-note" role="note" id="ai-privacy">
        Plan data (item codes, quantities, dates, supplier codes) is sent to OpenRouter and the chosen model provider. The assistant is read-only: it cannot create, release, move or edit anything.
      </div>
      <PasswordField id="ai-key" label="OpenRouter API key" noun="key" value={value.apiKey} isSet={value.apiKeySet} onChange={(v) => set('apiKey', v)} />
      <div className="form-row">
        <ModelPicker value={value.model} models={models} onChange={(v) => set('model', v)} />
        <Num id="ai-max" label="Max tokens per answer" min={64} max={8192} value={value.maxTokens} onChange={(n) => set('maxTokens', n)} hint="Limits the length (and cost) of each answer." />
      </div>
      <div>
        <button type="button" className="btn sm" disabled={modelsBusy} onClick={loadModels}>{modelsBusy ? 'Loading…' : 'Load models'}</button>
        {models.length > 0 && <span className="hint" style={{ marginLeft: 10 }}>{models.length} models loaded: search them in the box above to add one.</span>}
      </div>
      <ResultBox r={modelsMsg} />
      <ResultBox r={probe.result} />
    </SectionCard>
  );
}

// ---------- Planning ----------
const DAYS: Weekday[] = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

type ResNum = 'hoursPerDay' | 'efficiencyPercent' | 'setupHoursPerOrder';

function ResourcesCard({ value, onChange }: { value: PlanningSettings; onChange: (v: PlanningSettings) => void }) {
  const [source, setSource] = useState<DataSource>(value.defaultSource);
  const [list, setList] = useState<ResourceInfo[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const load = async () => {
    setBusy(true); setErr('');
    try { setList(await api.listResources(source)); }
    catch (e) { setErr((e as Error).message); setList(null); }
    finally { setBusy(false); }
  };
  useEffect(() => { void load(); }, [source]); // eslint-disable-line react-hooks/exhaustive-deps

  // Server values supply names, units and defaults; the draft overrides win.
  const rows: ResourceInfo[] = (list ?? []).map((r) => {
    const o = value.resources.find((x) => x.code === r.code);
    return o ? { ...r, ...o, configured: true } : { ...r, configured: false };
  });
  const edit = (r: ResourceInfo, key: ResNum, n: number) => {
    const cur = value.resources.find((x) => x.code === r.code);
    const next = { code: r.code, hoursPerDay: r.hoursPerDay, efficiencyPercent: r.efficiencyPercent, setupHoursPerOrder: r.setupHoursPerOrder, ...(cur ?? {}), [key]: n };
    onChange({ ...value, resources: cur ? value.resources.map((x) => (x.code === r.code ? next : x)) : [...value.resources, next] });
  };
  const reset = (code: string) => {
    const orig = list?.find((x) => x.code === code);
    // Drop the override from the draft; the row falls back to the default.
    onChange({ ...value, resources: value.resources.filter((x) => x.code !== code) });
    if (orig) setList((l) => (l ? l.map((x) => (x.code === code ? { ...x, configured: false } : x)) : l));
  };
  const numCell = (r: ResourceInfo, key: ResNum, label: string, max: number) => (
    <input type="number" min={0} max={max} step="any" aria-label={`${label}, ${r.code}`} value={Number.isFinite(r[key]) ? r[key] : ''}
      onChange={(e) => edit(r, key, e.target.value === '' ? NaN : Number(e.target.value))} />
  );

  return (
    <SectionCard
      title="Resources"
      badge={<span className="muted">{value.resources.length} configured</span>}
      actions={
        <>
          <div className="inline-field">
            <label htmlFor="res-source">Source</label>
            <select id="res-source" value={source} onChange={(e) => setSource(e.target.value as DataSource)}>
              <option value="mock">Mock sample data</option><option value="b1-sql">SAP B1 (SQL)</option>
            </select>
          </div>
          <button type="button" className="btn" disabled={busy} onClick={load}>{busy ? 'Loading…' : 'Reload resources'}</button>
          <span className="hint grow">Changed rows are saved as overrides with the settings. Others use hours per unit x units, 100% efficiency, no setup.</span>
        </>
      }
    >
      {err && <div className="alert danger" role="alert">{err}</div>}
      {!err && list && list.length === 0 && <p className="muted" style={{ margin: 0 }}>No resources found in this source.</p>}
      {rows.length > 0 && (
        <div className="table-wrap">
          <table className="data edit-table">
            <thead><tr>
              <th className="nosort">Code</th><th className="nosort">Name</th><th className="nosort num">Units</th>
              <th className="nosort">Hours / day</th><th className="nosort">Efficiency %</th><th className="nosort">Setup h / order</th><th className="nosort">State</th>
            </tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.code}>
                  <td className="mono">{r.code}</td><td>{r.name}</td><td className="num">{r.units}</td>
                  <td>{numCell(r, 'hoursPerDay', 'Hours per day', 576)}</td>
                  <td>{numCell(r, 'efficiencyPercent', 'Efficiency percent', 200)}</td>
                  <td>{numCell(r, 'setupHoursPerOrder', 'Setup hours per order', 999)}</td>
                  <td>{r.configured
                    ? <><Pill tone="info">Configured</Pill> <button type="button" className="btn sm" onClick={() => reset(r.code)}>Reset</button></>
                    : <span className="muted">Default</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </SectionCard>
  );
}

function PlanningSection({ value, onChange }: { value: PlanningSettings; onChange: (v: PlanningSettings) => void }) {
  const set = <K extends keyof PlanningSettings>(k: K, v: PlanningSettings[K]) => onChange({ ...value, [k]: v });
  const toggleDay = (d: Weekday, on: boolean) => set('workingDays', DAYS.filter((x) => (x === d ? on : value.workingDays.includes(x))));
  const setNw = (i: number, patch: Partial<NonWorkingDate>) => set('nonWorkingDates', value.nonWorkingDates.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  return (
    <>
    <SectionCard title="Planning defaults">
      <div className="field" style={{ maxWidth: 300 }}>
        <label htmlFor="pl-source">Default data source</label>
        <select id="pl-source" value={value.defaultSource} onChange={(e) => set('defaultSource', e.target.value as DataSource)}>
          <option value="mock">Mock sample data</option>
          <option value="b1-sql">SAP B1 (SQL)</option>
        </select>
      </div>
      <div className="form-row">
        <Num id="pl-horizon" label="Horizon (days)" min={7} max={730} value={value.horizonDays} onChange={(n) => set('horizonDays', n)} />
        <Num id="pl-bucket" label="Bucket (days)" min={1} max={90} value={value.bucketDays} onChange={(n) => set('bucketDays', n)} />
      </div>
      <div className="form-row">
        <Num id="pl-hours" label="Hours per resource unit per day" min={0} max={24} value={value.hoursPerUnitPerDay} onChange={(n) => set('hoursPerUnitPerDay', n)} hint="SAP B1 stores no capacity, so it is defined here." />
        <Num id="pl-lookback" label="Forecast lookback (days)" min={0} max={365} value={value.forecastLookbackDays} onChange={(n) => set('forecastLookbackDays', n)} />
      </div>
      <div className="field" style={{ maxWidth: 300 }}>
        <label htmlFor="pl-consume">Forecast consumption</label>
        <select id="pl-consume" value={value.forecastConsumption} onChange={(e) => set('forecastConsumption', e.target.value as PlanningSettings['forecastConsumption'])}>
          <option value="Forward">Forward</option><option value="None">None</option>
        </select>
        <span className="hint">Forward nets the forecast against real sales orders so demand is not counted twice; None plans forecast and orders in full.</span>
      </div>
      <div className="form-row">
        <div className="field">
          <label htmlFor="pl-level">Default planning level</label>
          <select id="pl-level" value={value.defaultLevel ?? 'Warehouse'} onChange={(e) => set('defaultLevel', e.target.value as PlanningSettings['defaultLevel'])}>
            <option value="Warehouse">Warehouse (plan each warehouse separately)</option>
            <option value="Company">Company (pool the selected warehouses)</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor="pl-doc">Purchasing document</label>
          <select id="pl-doc" value={value.purchaseDocument ?? 'Order'} onChange={(e) => set('purchaseDocument', e.target.value as PlanningSettings['purchaseDocument'])}>
            <option value="Order">Purchase order</option>
            <option value="Request">Purchase request</option>
          </select>
        </div>
        <div className="field">
          <label htmlFor="pl-grp">Documents on release</label>
          <select id="pl-grp" value={value.documentGrouping ?? 'Supplier'} onChange={(e) => set('documentGrouping', e.target.value as PlanningSettings['documentGrouping'])}>
            <option value="Supplier">One document per supplier</option>
            <option value="SupplierAndDate">Per supplier and due date</option>
            <option value="None">No grouping (one per planned order)</option>
          </select>
        </div>
        <Num id="pl-trdays" label="Transfer time (days)" min={0} max={90} value={value.transferLeadTimeDays ?? 1} onChange={(n) => set('transferLeadTimeDays', n)}
          hint="Default for transfer requests between warehouses." />
      </div>
    </SectionCard>

    <SectionCard title="Lead times">
      <p className="muted" style={{ margin: 0 }}>Defaults for the Run MRP wizard and for the lead-time column in Master data.</p>
      <PriorityControl idPrefix="pl-lt" priority={value.leadTimePriority ?? ['Supplier', 'Item', 'ItemGroup']} mode={value.leadTimeMode ?? 'FirstAvailable'}
        onPriority={(p) => set('leadTimePriority', p)} onMode={(m) => set('leadTimeMode', m)} />
      <div style={{ maxWidth: 260 }}>
        <Num id="pl-ltdef" label="Default lead time (days)" min={0} max={730} value={value.defaultLeadTimeDays ?? 0} onChange={(n) => set('defaultLeadTimeDays', n)}
          hint="Used when neither the supplier, the item nor its group has a lead time." />
      </div>
    </SectionCard>

    <SectionCard title="Working calendar">
      <Check id="pl-b1cal" label="Use the SAP B1 company calendar" checked={value.useB1Calendar} onChange={(v) => set('useB1Calendar', v)}
        hint="Weekend days and holidays come from SAP B1 when it has a calendar; the working days below are the fallback." />
      <fieldset className="day-set">
        <legend>Working days{value.useB1Calendar && <span className="hint"> (used only when B1 has no calendar)</span>}</legend>
        <div className="day-row">
          {DAYS.map((d) => (
            <label key={d} className="day-check"><input type="checkbox" checked={value.workingDays.includes(d)} onChange={(e) => toggleDay(d, e.target.checked)} />{d}</label>
          ))}
        </div>
      </fieldset>
      <div>
        <div className="section-title">Extra non-working dates (always applied)</div>
        {value.nonWorkingDates.length === 0 ? <p className="muted" style={{ margin: '0 0 8px' }}>None.</p> : (
          <div className="table-wrap">
            <table className="data edit-table">
              <thead><tr><th className="nosort">Date</th><th className="nosort">Reason</th><th className="nosort"><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {value.nonWorkingDates.map((r, i) => (
                  <tr key={i}>
                    <td><input type="date" aria-label={`Date, row ${i + 1}`} value={r.date} onChange={(e) => setNw(i, { date: e.target.value })} /></td>
                    <td><input aria-label={`Reason, row ${i + 1}`} value={r.reason} onChange={(e) => setNw(i, { reason: e.target.value })} /></td>
                    <td><button type="button" className="btn sm" aria-label={`Remove row ${i + 1}`} onClick={() => set('nonWorkingDates', value.nonWorkingDates.filter((_, j) => j !== i))}>Remove</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <button type="button" className="btn sm" style={{ marginTop: 8 }} onClick={() => set('nonWorkingDates', [...value.nonWorkingDates, { date: '', reason: '' }])}>+ Add date</button>
      </div>
    </SectionCard>

    <ResourcesCard value={value} onChange={onChange} />
    </>
  );
}

// ---------- Users ----------
function UsersTab() {
  const { user: me } = useAuth();
  const users = useUsers();
  const create = useCreateUser();
  const del = useDeleteUser();
  const setPw = useSetUserPassword();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<Role>('Planner');
  const [pwFor, setPwFor] = useState<string | null>(null);
  const [newPw, setNewPw] = useState('');
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null);

  const admins = (users.data ?? []).filter((u) => u.role === 'Admin').length;

  const add = (e: FormEvent) => {
    e.preventDefault();
    setNotice(null);
    create.mutate({ username: username.trim(), password, role }, {
      onSuccess: (u) => { setUsername(''); setPassword(''); setNotice({ ok: true, text: `User "${u.username}" added.` }); },
      onError: (err) => setNotice({ ok: false, text: (err as Error).message }),
    });
  };
  const remove = (u: string) => {
    if (!window.confirm(`Delete user "${u}"?`)) return;
    setNotice(null);
    del.mutate(u, {
      onSuccess: () => setNotice({ ok: true, text: `User "${u}" deleted.` }),
      onError: (err) => setNotice({ ok: false, text: (err as Error).message }),
    });
  };
  const closePw = () => { setPwFor(null); setNewPw(''); setPw.reset(); };
  const savePw = (e: FormEvent) => {
    e.preventDefault();
    if (!pwFor) return;
    setPw.mutate({ username: pwFor, password: newPw }, {
      onSuccess: () => { setNotice({ ok: true, text: `Password changed for "${pwFor}".` }); closePw(); },
    });
  };

  return (
    <>
      <SectionCard title="Users" badge={<span className="muted">Admin: settings, users, release anything to SAP B1, Execute. Buyer: a planner who can also release purchase orders and requests. Planner: run MRP, view, edit forecasts.</span>}>
        {notice && <div className={`alert ${notice.ok ? 'ok' : 'danger'}`} role="status">{notice.text}</div>}
        {users.isLoading && <Loading />}
        {users.isError && <ErrorState error={users.error} />}
        {users.data && (
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th className="nosort">Username</th><th className="nosort">Role</th><th className="nosort"><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {users.data.map((u) => {
                  const self = u.username === me.username;
                  const lastAdmin = u.role === 'Admin' && admins <= 1;
                  const why = self ? 'You cannot delete your own account' : lastAdmin ? 'The last administrator cannot be deleted' : '';
                  return (
                    <tr key={u.username}>
                      <td>{u.username}{self && <span className="muted"> (you)</span>}</td>
                      <td><Pill tone={u.role === 'Admin' ? 'info' : undefined}>{u.role}</Pill></td>
                      <td className="row-actions">
                        <button type="button" className="btn sm" onClick={() => setPwFor(u.username)}>Change password</button>
                        <button type="button" className="btn sm danger" disabled={!!why} title={why || undefined} onClick={() => remove(u.username)}>Delete</button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </SectionCard>

      <div className="card settings-card">
        <div className="card-h"><h3>Add user</h3></div>
        <form className="card-b" onSubmit={add}>
          <div className="form-row-3">
            <div className="field"><label htmlFor="nu-name">Username</label>
              <input id="nu-name" required autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} /></div>
            <div className="field"><label htmlFor="nu-pass">Password</label>
              <input id="nu-pass" type="password" required minLength={8} autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
              <span className="hint">Minimum 8 characters.</span></div>
            <div className="field"><label htmlFor="nu-role">Role</label>
              <select id="nu-role" value={role} onChange={(e) => setRole(e.target.value as Role)}><option>Planner</option><option>Buyer</option><option>Admin</option></select></div>
          </div>
          <div style={{ marginTop: 14 }}><button type="submit" className="btn primary" disabled={create.isPending}>{create.isPending ? 'Adding…' : 'Add user'}</button></div>
        </form>
      </div>

      {pwFor && (
        <Dialog
          title={`Change password: ${pwFor}`}
          onClose={closePw}
          footer={<>
            <button type="button" className="btn" onClick={closePw}>Cancel</button>
            <button type="submit" form="pw-form" className="btn primary" disabled={setPw.isPending || newPw.length < 8}>{setPw.isPending ? 'Saving…' : 'Change password'}</button>
          </>}
        >
          <form id="pw-form" onSubmit={savePw} className="form-stack">
            <div className="field"><label htmlFor="pw-new">New password</label>
              <input id="pw-new" type="password" autoFocus autoComplete="new-password" minLength={8} value={newPw} onChange={(e) => setNewPw(e.target.value)} />
              <span className="hint">Minimum 8 characters.</span></div>
            {setPw.isError && <div className="alert danger" role="alert">{(setPw.error as Error).message}</div>}
          </form>
        </Dialog>
      )}
    </>
  );
}

// ---------- page ----------
function SettingsEditor({ initial }: { initial: AppSettings }) {
  const save = useSaveSettings();
  const [params] = useSearchParams();
  const wanted = params.get('tab');
  const [tab, setTab] = useState<TabId>(TABS.some((t) => t.id === wanted) ? (wanted as TabId) : 'cobalt');
  const [draft, setDraft] = useState<AppSettings>(initial);
  const [base, setBase] = useState<AppSettings>(initial);
  const [toast, setToast] = useState<{ ok: boolean; text: string } | null>(null);
  const timer = useRef<number>();

  const dirty = useMemo(() => JSON.stringify(draft) !== JSON.stringify(base), [draft, base]);
  const savedIds = useMemo(() => new Set(base.schedules.map((x) => x.id)), [base]);
  const forTemplate = params.get('schedule');
  const added = useRef(false);
  useEffect(() => {
    if (!forTemplate || added.current) return;
    added.current = true;
    setTab('automation');
    // a template that already has a schedule: just show it; otherwise start a new one for it
    setDraft((d) => (d.schedules.some((x) => x.templateId === forTemplate) || d.schedules.length >= MAX_SCHEDULES ? d
      : { ...d, schedules: [...d.schedules, { id: '', name: '', enabled: false, time: '02:00', days: [...DAYS], templateId: forTemplate }] }));
  }, [forTemplate]);
  useEffect(() => () => window.clearTimeout(timer.current), []);

  // Warn before closing the tab with unsaved changes.
  useEffect(() => {
    if (!dirty) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', h);
    return () => window.removeEventListener('beforeunload', h);
  }, [dirty]);

  const flash = (ok: boolean, text: string) => {
    setToast({ ok, text });
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setToast(null), ok ? 3500 : 9000);
  };

  const doSave = () => {
    if (![draft.planning.horizonDays, draft.planning.bucketDays, draft.planning.hoursPerUnitPerDay, draft.planning.forecastLookbackDays, draft.planning.defaultLeadTimeDays,
      ...draft.planning.resources.flatMap((r) => [r.hoursPerDay, r.efficiencyPercent, r.setupHoursPerOrder])].every(Number.isFinite)) {
      flash(false, 'Planning defaults contain an empty number.'); setTab('planning'); return;
    }
    if (!Number.isFinite(draft.ai.maxTokens)) { flash(false, 'AI max tokens needs a number.'); setTab('ai'); return; }
    for (const [i, sc] of draft.schedules.entries()) {
      const label = sc.name.trim() ? `Schedule "${sc.name.trim()}"` : `Schedule ${i + 1}`;
      const fail = (m: string) => { flash(false, `${label}: ${m}`); setTab('automation'); };
      if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(sc.time)) { fail('choose a time of day (HH:mm).'); return; }
      if (sc.enabled && sc.days.length === 0) { fail('choose at least one day.'); return; }
      if (sc.enabled && !sc.templateId && !(sc.id && base.schedules.some((o) => o.id === sc.id && o.enabled && !o.templateId))) { fail('choose a template to run.'); return; }
    }
    if (draft.notifications.enabled && (!draft.notifications.smtpHost.trim() || !draft.notifications.from.trim() || !draft.notifications.recipients.includes('@'))) { flash(false, 'Emails: enter the SMTP server, the From address and at least one recipient.'); setTab('automation'); return; }
    if (draft.notifications.enabled && !Number.isFinite(draft.notifications.smtpPort)) { flash(false, 'Emails: the port needs a number.'); setTab('automation'); return; }
    if (draft.planning.nonWorkingDates.some((d) => !d.date)) { flash(false, 'Every extra non-working date needs a date.'); setTab('planning'); return; }
    save.mutate(draft, {
      onSuccess: (raw) => { const fresh = normaliseSettings(raw); setDraft(fresh); setBase(fresh); flash(true, 'Settings saved.'); },
      onError: (e) => flash(false, `Save failed: ${(e as Error).message}`),
    });
  };
  const discard = () => setDraft(base);

  return (
    <>
      <PageHeader
        crumb="Configuration"
        title="Settings"
        actions={OWN_SAVE.includes(tab) ? undefined : (
          <>
            {dirty ? <span className="pill warn"><span className="dot" />Unsaved changes</span> : <span className="pill ok"><span className="dot" />All changes saved</span>}
            <button type="button" className="btn" disabled={!dirty || save.isPending} onClick={discard}>Discard</button>
            <button type="button" className="btn primary" disabled={!dirty || save.isPending} onClick={doSave}>{save.isPending ? 'Saving…' : 'Save settings'}</button>
          </>
        )}
      />
      <div className="page settings-page">
        <div className="tabs" role="tablist" aria-label="Settings sections">
          {TABS.map((t) => (
            <button key={t.id} type="button" role="tab" id={`tab-${t.id}`} aria-selected={tab === t.id} aria-controls={`panel-${t.id}`} className={`tab ${tab === t.id ? 'active' : ''}`} onClick={() => setTab(t.id)}>
              {t.label}
            </button>
          ))}
        </div>
        <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`} className="tab-panel">
          <SectionBoundary key={tab}>
          {tab === 'cobalt' && <DbSection id="cobalt" title="Cobalt MRP database" note="Where MRP runs and forecasts are stored." value={draft.cobaltDb} onChange={(v) => setDraft({ ...draft, cobaltDb: v })} test={api.testCobaltDb} extra />}
          {tab === 'b1' && <DbSection id="b1" title="SAP Business One database" note="Read-only access to the B1 company database." value={draft.b1Db} onChange={(v) => setDraft({ ...draft, b1Db: v })} test={api.testB1Db} />}
          {tab === 'sl' && <ServiceLayerSection value={draft.serviceLayer} onChange={(v) => setDraft({ ...draft, serviceLayer: v })} />}
          {tab === 'planning' && <PlanningSection value={draft.planning} onChange={(v) => setDraft({ ...draft, planning: v })} />}
          {tab === 'automation' && <AutomationSection schedules={draft.schedules} notifications={draft.notifications} savedIds={savedIds} onSchedules={(v) => setDraft({ ...draft, schedules: v })} onNotifications={(v) => setDraft({ ...draft, notifications: v })} />}
          {tab === 'ai' && <AiSection value={draft.ai} onChange={(v) => setDraft({ ...draft, ai: v })} />}
          {tab === 'users' && <UsersTab />}
          {tab === 'licence' && <LicencePanel />}
          {tab === 'backup' && <BackupPanel />}

          </SectionBoundary>
        </div>
        {!OWN_SAVE.includes(tab) && <p className="muted">Passwords are never sent back by the server and are not kept in this browser. Frontend: API base <span className="mono">{API_BASE}</span>, {USE_MOCK ? 'mock (offline) mode' : 'live API'}.</p>}
      </div>
      {toast && <div className={`toast ${toast.ok ? '' : 'error'}`} role="status">{toast.text}</div>}
    </>
  );
}

export default function Settings() {
  const { isAdmin } = useAuth();
  const q = useSettings(isAdmin);
  if (!isAdmin) return <Navigate to="/" replace />;
  if (q.isLoading) return <><PageHeader crumb="Configuration" title="Settings" /><Loading /></>;
  if (q.isError || !q.data) return <><PageHeader crumb="Configuration" title="Settings" /><ErrorState error={q.error} /></>;
  return <SettingsEditor initial={normaliseSettings(q.data)} />;
}
