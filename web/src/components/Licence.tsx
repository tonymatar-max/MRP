import { FormEvent, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { LicenseStatus } from '../api/types';
import { ErrorState, Loading, Pill } from './ui';

/** Copies text; falls back to a hidden selection for browsers that block the async clipboard (plain http). */
async function copyText(text: string, fallbackEl: HTMLInputElement | null): Promise<boolean> {
  try { await navigator.clipboard.writeText(text); return true; }
  catch {
    try { fallbackEl?.focus(); fallbackEl?.select(); return document.execCommand('copy'); } catch { return false; }
  }
}

export function daysLeft(expiresOn: string | null): number | null {
  if (!expiresOn) return null;
  const d = new Date(expiresOn);
  if (Number.isNaN(d.getTime())) return null;
  const today = new Date(); today.setUTCHours(0, 0, 0, 0);
  return Math.round((d.getTime() - today.getTime()) / 864e5);
}

export function LicencePill({ s }: { s: LicenseStatus }) {
  if (s.isValid) {
    const left = daysLeft(s.expiresOn);
    return <Pill tone={left != null && left <= 30 ? 'warn' : 'ok'}>{left != null && left <= 30 ? `Valid · ${left} day${left === 1 ? '' : 's'} left` : 'Valid'}</Pill>;
  }
  if (s.state === 'Expired') return <Pill tone="danger">Expired</Pill>;
  if (s.state === 'NotLicensed') return <Pill tone="danger">Not licensed</Pill>;
  return <Pill tone="danger">Key not accepted</Pill>;
}

/** The facts about this licence: customer, expiry, seats. Only what the server verified. */
function Facts({ s }: { s: LicenseStatus }) {
  const rows: [string, string][] = [];
  if (s.customer) rows.push(['Customer', s.customer]);
  if (s.expiresOn) rows.push(['Expires', s.expiresOn.slice(0, 10)]);
  if (s.maxSeats != null) rows.push(['Seats', String(s.maxSeats)]);
  if (!rows.length) return null;
  return (
    <dl className="auto-status lic-facts">
      {rows.map(([k, v]) => <div key={k}><dt>{k}</dt><dd>{v}</dd></div>)}
    </dl>
  );
}

/**
 * Hardware key (read-only, with Copy) + a box to paste a licence key. Used inside Settings > Licence and on the full-page
 * "Licence required" screen. Only an administrator can save; the server refuses everyone else with 403.
 */
export function LicenceForm({ status, canSave, onSaved }: { status: LicenseStatus; canSave: boolean; onSaved: (s: LicenseStatus) => void }) {
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const hwRef = useRef<HTMLInputElement>(null);

  const copy = async () => {
    const ok = await copyText(status.hardwareKey, hwRef.current);
    setCopied(ok);
    window.setTimeout(() => setCopied(false), 2000);
  };
  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (!key.trim()) { setError('Paste the licence key first.'); return; }
    setBusy(true); setError(null);
    try { const s = await api.saveLicense(key.trim()); setKey(''); onSaved(s); }
    catch (err) { setError((err as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <div className="form-stack">
      <div className="field">
        <label htmlFor="lic-hw">Hardware Key</label>
        <div className="lic-hw-row">
          <input id="lic-hw" ref={hwRef} className="mono" readOnly value={status.hardwareKey} onFocus={(e) => e.currentTarget.select()} />
          <button type="button" className="btn" onClick={copy}>{copied ? 'Copied' : 'Copy'}</button>
        </div>
        <span className="hint">Send this key to your Cobalt contact (product <strong>{status.productCode}</strong>) to receive a licence key. It is tied to this server and to the install folder: moving the folder or renaming the server needs a new key.</span>
      </div>
      {canSave ? (
        <form className="form-stack" onSubmit={save}>
          <div className="field">
            <label htmlFor="lic-key">{status.isValid ? 'Replace the licence key' : 'Licence key'}</label>
            <textarea id="lic-key" rows={4} className="mono lic-key" spellCheck={false} autoComplete="off" placeholder="ICL1.…" value={key} onChange={(e) => setKey(e.target.value)} />
          </div>
          {error && <div className="alert danger" role="alert">{error}</div>}
          <div className="lic-actions">
            <button type="submit" className="btn primary" disabled={busy || !key.trim()}>{busy ? 'Checking…' : 'Save licence'}</button>
            <span className="hint">The key is verified on the server before it is stored; a wrong key never replaces a good one.</span>
          </div>
        </form>
      ) : (
        <div className="alert warn" role="status">Ask an administrator to enter a licence key.</div>
      )}
    </div>
  );
}

/** Settings > Licence (Admin). */
export function LicencePanel() {
  const q = useQuery({ queryKey: ['licence'], queryFn: api.licenseStatus, staleTime: 0, gcTime: 0 });
  const [saved, setSaved] = useState(false);
  if (q.isLoading) return <Loading />;
  if (q.isError || !q.data) return <ErrorState error={q.error} />;
  const s = q.data;
  return (
    <div className="card settings-card">
      <div className="card-h"><h3>Licence</h3><LicencePill s={s} /></div>
      <div className="card-b form-stack">
        <p className="muted" style={{ margin: 0 }}>{s.isValid ? 'This server is licensed.' : s.message}</p>
        <Facts s={s} />
        {saved && s.isValid && <div className="alert ok" role="status">Licence saved. Cobalt MRP is licensed.</div>}
        <LicenceForm status={s} canSave onSaved={() => { setSaved(true); void q.refetch(); }} />
      </div>
    </div>
  );
}
