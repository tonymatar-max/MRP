import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { BackupSettings, BackupView } from '../api/types';
import { ErrorState, Loading, Pill } from './ui';

const DAYS: [number, string][] = [[1, 'Monday'], [2, 'Tuesday'], [3, 'Wednesday'], [4, 'Thursday'], [5, 'Friday'], [6, 'Saturday'], [0, 'Sunday']];

function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

const kb = (n: number) => `${(n / 1024).toFixed(1)} KB`;
const when = (iso: string) => { const d = new Date(iso); return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(); };

/** Settings > Backup (Admin): the scheduled configuration backup. Has its own Save; it is not part of "Save settings". */
export function BackupPanel() {
  const q = useQuery({ queryKey: ['backup'], queryFn: api.backupView, staleTime: 0, gcTime: 0 });
  const [view, setView] = useState<BackupView | null>(null);
  const [draft, setDraft] = useState<BackupSettings | null>(null);
  const [busy, setBusy] = useState<'save' | 'run' | 'download' | null>(null);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);

  useEffect(() => { const data = q.data; if (data) { setView(data); setDraft((d) => d ?? data.settings); } }, [q.data]);

  if (q.isLoading) return <Loading />;
  if (q.isError || !view || !draft) return <ErrorState error={q.error} />;

  const set = <K extends keyof BackupSettings>(k: K, v: BackupSettings[K]) => setDraft({ ...draft, [k]: v });
  const dirty = JSON.stringify(draft) !== JSON.stringify(view.settings);
  const accept = (v: BackupView) => { setView(v); setDraft(v.settings); };
  const fail = (e: unknown) => setMsg({ ok: false, text: (e as Error).message });
  const keepOk = Number.isFinite(draft.keepLast) && draft.keepLast >= 1 && draft.keepLast <= 365;

  const save = async (): Promise<boolean> => {
    if (!keepOk) { setMsg({ ok: false, text: 'Keep the newest: enter a number from 1 to 365.' }); return false; }
    setBusy('save'); setMsg(null);
    try { accept(await api.saveBackupSchedule(draft)); setMsg({ ok: true, text: 'Backup schedule saved.' }); return true; }
    catch (e) { fail(e); return false; }
    finally { setBusy(null); }
  };
  const runNow = async () => {
    setMsg(null);
    // Save first when something changed, so "Back up now" uses the folder shown on screen.
    if (dirty && !(await save())) return;
    setBusy('run');
    try {
      const r = await api.runBackup();
      accept(r.view);
      setMsg(r.result.success ? { ok: true, text: `Backup saved: ${r.result.file}` } : { ok: false, text: `Backup failed: ${r.result.error ?? 'unknown error'}` });
    } catch (e) { fail(e); } finally { setBusy(null); }
  };
  const download = async () => {
    setBusy('download'); setMsg(null);
    try { const f = await api.downloadBackup(); saveBlob(f.blob, f.filename); setMsg({ ok: true, text: `Downloaded ${f.filename}.` }); }
    catch (e) { fail(e); } finally { setBusy(null); }
  };

  const last = view.last;
  return (
    <>
      <div className="card settings-card">
        <div className="card-h">
          <h3>Scheduled backup</h3>
          {view.settings.enabled ? <Pill tone="ok">On</Pill> : <Pill tone="warn">Off</Pill>}
        </div>
        <div className="card-b form-stack">
          <p className="muted" style={{ margin: 0 }}>
            Saves the app&apos;s configuration automatically to a folder on this server and keeps only the newest backups. If a scheduled time has
            passed and no backup exists since (a restart, downtime, or the first time you switch it on), one is taken straight away.
            Backups keep running even when the licence has expired.
          </p>
          <div className="check-row">
            <input id="bk-enabled" type="checkbox" checked={draft.enabled} onChange={(e) => set('enabled', e.target.checked)} />
            <label htmlFor="bk-enabled">Enable scheduled backup</label>
          </div>
          <div className="form-row">
            <div className="field"><label htmlFor="bk-freq">Frequency</label>
              <select id="bk-freq" value={draft.frequency} onChange={(e) => set('frequency', e.target.value as BackupSettings['frequency'])}>
                <option value="daily">Every day</option><option value="weekly">Every week</option>
              </select></div>
            {draft.frequency === 'weekly' && (
              <div className="field"><label htmlFor="bk-day">Day of week</label>
                <select id="bk-day" value={draft.dayOfWeek} onChange={(e) => set('dayOfWeek', Number(e.target.value))}>
                  {DAYS.map(([v, n]) => <option key={v} value={v}>{n}</option>)}
                </select></div>
            )}
            <div className="field"><label htmlFor="bk-time">Time (server local time)</label>
              <input id="bk-time" type="time" value={draft.time} onChange={(e) => set('time', e.target.value)} /></div>
            <div className="field"><label htmlFor="bk-keep">Keep the newest</label>
              <input id="bk-keep" type="number" min={1} max={365} value={Number.isFinite(draft.keepLast) ? draft.keepLast : ''} aria-invalid={!keepOk}
                onChange={(e) => set('keepLast', e.target.value === '' ? NaN : Number(e.target.value))} />
              <span className="hint">1 to 365 backups; older ones are deleted.</span></div>
          </div>
          <div className="field"><label htmlFor="bk-folder">Backup folder</label>
            <input id="bk-folder" className="mono" value={draft.folder} placeholder={view.defaultFolder} onChange={(e) => set('folder', e.target.value)} />
            <span className="hint">A full path on this server (or a share the service account can write). Leave empty for the default. Not inside the app&apos;s web folder.</span></div>
          <dl className="auto-status">
            <div><dt>Next backup</dt><dd>{view.settings.enabled && view.nextRunLocal ? `${view.nextRunLocal} (server time)` : 'Scheduled backup is off'}</dd></div>
            <div><dt>Saving to</dt><dd className="mono">{view.folder}</dd></div>
            <div><dt>Last run</dt><dd>{last
              ? <>{last.success ? <Pill tone="ok">Succeeded</Pill> : <Pill tone="danger">Failed</Pill>} {last.trigger}, {when(last.finishedAtUtc)}{last.success ? ` · ${last.file}` : ` · ${last.error ?? ''}`}</>
              : 'No backup has run since the service started.'}</dd></div>
          </dl>
          {msg && <div className={`alert ${msg.ok ? 'ok' : 'danger'}`} role={msg.ok ? 'status' : 'alert'}>{msg.text}</div>}
        </div>
        <div className="card-f">
          <button type="button" className="btn primary" disabled={busy !== null || !dirty} onClick={() => void save()}>{busy === 'save' ? 'Saving…' : 'Save schedule'}</button>
          <button type="button" className="btn" disabled={busy !== null} onClick={() => void runNow()}>{busy === 'run' ? 'Backing up…' : 'Back up now'}</button>
          <button type="button" className="btn" disabled={busy !== null} onClick={() => void download()}>{busy === 'download' ? 'Preparing…' : 'Download now'}</button>
          {dirty && <span className="hint grow">Unsaved schedule changes</span>}
        </div>
      </div>

      <div className="card settings-card">
        <div className="card-h"><h3>Backups in this folder</h3></div>
        <div className="card-b">
          {view.backups.length ? (
            <div className="table-wrap">
              <table className="data">
                <thead><tr><th>Backup file</th><th>Created</th><th style={{ textAlign: 'right' }}>Size</th></tr></thead>
                <tbody>{view.backups.map((b) => (
                  <tr key={b.name}><td className="mono">{b.name}</td><td>{when(b.createdUtc)}</td><td style={{ textAlign: 'right' }}>{kb(b.sizeBytes)}</td></tr>
                ))}</tbody>
              </table>
            </div>
          ) : <p className="muted" style={{ margin: 0 }}>No scheduled backups in this folder yet.</p>}
        </div>
      </div>

      <div className="card settings-card">
        <div className="card-h"><h3>What is in a backup, and what is not</h3></div>
        <div className="card-b form-stack">
          <p style={{ margin: 0 }}>Each backup is a zip of the state files in <span className="mono">{view.dataFolder}</span>:</p>
          <ul className="bk-list">
            <li><span className="mono">settings.json</span>: all configuration (its passwords are encrypted)</li>
            <li><span className="mono">users.json</span>: users and password hashes</li>
            <li><span className="mono">automation-state.json</span> and <span className="mono">backup-schedule.json</span>: schedule state and this schedule</li>
            <li><span className="mono">license.json</span>: the licence key</li>
            <li><span className="mono">keys\</span>: the encryption key ring. Without it the stored passwords cannot be decrypted after a restore.</li>
          </ul>
          <div className="alert warn" role="note">
            <strong>Planned runs, forecasts and master-data overrides are not in this backup.</strong> They are stored in the SQL database
            (the Cobalt MRP database), so take a SQL Server backup of it as well.
          </div>
          <p className="muted" style={{ margin: 0 }}>
            <strong>Restore is a manual copy:</strong> stop the CobaltMRP service, copy the files from the zip back into the data folder (keep the <span className="mono">keys</span> folder
            with them), start the service. Restore on the same server only: the encryption keys are bound to this machine. Backups hold password hashes and encrypted credentials:
            keep the folder access-controlled, and copy it off this server for a real disaster-recovery copy.
          </p>
        </div>
      </div>
    </>
  );
}
