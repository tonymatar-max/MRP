import { createContext, FormEvent, ReactNode, useContext, useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, setLicenseRequiredHandler, setUnauthorizedHandler } from '../api/client';
import type { AuthStatus, AuthUser, LicenseStatus } from '../api/types';
import { BrandMark, Loading } from './ui';
import { LangSwitch, useT } from '../i18n';
import { LicenceForm, LicencePill } from './Licence';

interface AuthCtx {
  user: AuthUser;
  isAdmin: boolean;
  signOut: () => void;
}
const Ctx = createContext<AuthCtx | null>(null);

export function useAuth(): AuthCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error('useAuth must be used inside <AuthGate>');
  return c;
}

const SIGNED_OUT: AuthStatus = { setupRequired: false, authenticated: false, user: null };

const NET_PTS: [number, number][] = [[8, 18], [26, 10], [44, 24], [70, 12], [88, 28], [16, 46], [38, 52], [62, 42], [84, 58], [12, 78], [34, 88], [56, 72], [78, 86], [94, 74]];

function Net() {
  const lines: JSX.Element[] = [];
  NET_PTS.forEach((a, i) => NET_PTS.forEach((b, j) => {
    if (j > i && Math.hypot(a[0] - b[0], a[1] - b[1]) < 26) lines.push(<line key={`${i}-${j}`} x1={a[0]} y1={a[1]} x2={b[0]} y2={b[1]} />);
  }));
  return (
    <svg className="net" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
      <g stroke="#fff" strokeWidth=".15">{lines}</g>
      <g fill="#fff">{NET_PTS.map((p, i) => <circle key={i} cx={p[0]} cy={p[1]} r=".7" />)}</g>
    </svg>
  );
}

function AuthShell({ title, intro, children }: { title: string; intro?: string; children: ReactNode }) {
  const t = useT();
  useEffect(() => { document.title = `${t(title)} · Cobalt MRP`; }, [title, t]);
  return (
    <div className="auth">
      <section className="auth-hero">
        <Net />
        <div className="top">
          <BrandMark />
          <div className="product-title"><strong>Cobalt</strong><span className="product-sub">MRP</span></div>
        </div>
        <div className="pitch">
          <h1>{t('Plan supply with confidence.')}</h1>
          <p className="lead">{t('Material and capacity planning for SAP Business One.')}</p>
        </div>
        <div className="foot">© {new Date().getFullYear()} Solvep · Cobalt product family</div>
      </section>
      <section className="auth-panel">
        <div className="auth-lang"><LangSwitch /></div>
        <div className="auth-card">
          <div className="auth-lockup product-title"><strong>Cobalt</strong><span className="product-sub">MRP</span></div>
          <BrandMark large />
          <h2>{t(title)}</h2>
          {intro && <p className="muted auth-intro">{t(intro)}</p>}
          {children}
        </div>
      </section>
    </div>
  );
}

function CredentialsForm({ submitLabel, busy, error, onSubmit, confirm }: {
  submitLabel: string; busy: boolean; error?: string; confirm?: boolean;
  onSubmit: (username: string, password: string) => void;
}) {
  const t = useT();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [again, setAgain] = useState('');
  const mismatch = !!confirm && again !== '' && again !== password;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (mismatch || (confirm && !again)) return;
    onSubmit(username.trim(), password);
  };
  return (
    <form className="auth-form" onSubmit={submit}>
      <div className="field">
        <label htmlFor="auth-user">{t('Username')}</label>
        <input id="auth-user" autoComplete="username" autoFocus required value={username} onChange={(e) => setUsername(e.target.value)} />
      </div>
      <div className="field">
        <label htmlFor="auth-pass">{t('Password')}</label>
        <input id="auth-pass" type="password" autoComplete={confirm ? 'new-password' : 'current-password'} required minLength={confirm ? 8 : undefined} value={password} onChange={(e) => setPassword(e.target.value)} />
        {confirm && <span className="hint">{t('At least 8 characters.')}</span>}
      </div>
      {confirm && (
        <div className="field">
          <label htmlFor="auth-pass2">{t('Confirm password')}</label>
          <input id="auth-pass2" type="password" autoComplete="new-password" required value={again} onChange={(e) => setAgain(e.target.value)} />
          {mismatch && <span className="hint err">{t('Passwords do not match.')}</span>}
        </div>
      )}
      {error && <div className="alert danger" role="alert">{error}</div>}
      <button type="submit" className="btn primary lg auth-submit" disabled={busy || mismatch}>{busy ? t('Please wait…') : t(submitLabel)}</button>
    </form>
  );
}

function SetupPage({ onDone }: { onDone: (u: AuthUser) => void }) {
  const m = useMutation({ mutationFn: (c: { username: string; password: string }) => api.setup(c), onSuccess: onDone });
  return (
    <AuthShell title="Create the first administrator" intro="No users exist yet. This step can only be completed from a browser running on the Cobalt MRP server itself.">
      <CredentialsForm confirm submitLabel="Create administrator" busy={m.isPending} error={m.isError ? (m.error as Error).message : undefined} onSubmit={(username, password) => m.mutate({ username, password })} />
    </AuthShell>
  );
}

function LoginPage({ onDone }: { onDone: (u: AuthUser) => void }) {
  const m = useMutation({ mutationFn: (c: { username: string; password: string }) => api.login(c), onSuccess: onDone });
  return (
    <AuthShell title="Sign in" intro="Sign in to plan, review and release orders.">
      <CredentialsForm submitLabel="Sign in" busy={m.isPending} error={m.isError ? (m.error as Error).message : undefined} onSubmit={(username, password) => m.mutate({ username, password })} />
    </AuthShell>
  );
}

/**
 * Full-page "Licence required": shown instead of the app whenever the server answers 402 (or reports no valid licence).
 * Sign-in still works while unlicensed, so an administrator gets here, reads the hardware key and pastes the key.
 * Anyone else is told to ask an administrator.
 */
function LicenseRequiredScreen({ user, signOut, onLicensed }: { user: AuthUser; signOut: () => void; onLicensed: (s: LicenseStatus) => void }) {
  const q = useQuery({ queryKey: ['licence-screen'], queryFn: api.licenseStatus, staleTime: 0, gcTime: 0, retry: false });
  const isAdmin = user.role === 'Admin';
  const s = q.data;
  return (
    <AuthShell title="Licence required" intro="Cobalt MRP needs a valid licence for this server. Planning and settings are locked until one is entered.">
      {q.isLoading && <Loading />}
      {q.isError && (
        <>
          <div className="alert danger" role="alert">{(q.error as Error).message}</div>
          <button type="button" className="btn lg auth-submit" onClick={() => q.refetch()}>Retry</button>
        </>
      )}
      {s && (
        <div className="form-stack lic-screen">
          <div className="lic-state"><LicencePill s={s} /><span className="muted">{s.message}</span></div>
          <LicenceForm status={s} canSave={isAdmin} onSaved={onLicensed} />
          <div className="lic-actions">
            <button type="button" className="btn" onClick={async () => { const r = await q.refetch(); if (r.data?.isValid) onLicensed(r.data); }}>Check again</button>
            <button type="button" className="btn" onClick={signOut}>Sign out</button>
          </div>
        </div>
      )}
      {!s && !q.isLoading && !q.isError && <button type="button" className="btn" onClick={signOut}>Sign out</button>}
    </AuthShell>
  );
}

/** Resolves /auth/status and renders setup, login or the app. A 401 anywhere drops back to login. */
export function AuthGate({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['auth'], queryFn: api.authStatus, retry: false, staleTime: Infinity });
  // A 402 from ANY api call (the licence expired mid-session, or was never entered) blocks the whole app.
  const [blocked, setBlocked] = useState(false);
  const authed = !!q.data?.authenticated;
  const gate = useQuery({ queryKey: ['licence-gate'], queryFn: api.licenseStatus, enabled: authed, retry: false, staleTime: Infinity });

  const reset = (status: AuthStatus) => {
    setBlocked(false);
    // Drop every cached business query so nothing from a previous user leaks into the next session.
    qc.removeQueries({ predicate: (query) => query.queryKey[0] !== 'auth' });
    qc.setQueryData(['auth'], status);
  };

  useEffect(() => {
    setUnauthorizedHandler(() => reset(SIGNED_OUT));
    setLicenseRequiredHandler(() => setBlocked(true));
    return () => { setUnauthorizedHandler(null); setLicenseRequiredHandler(null); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [qc]);

  const signedIn = (u: AuthUser) => {
    setBlocked(false);
    qc.removeQueries({ predicate: (query) => query.queryKey[0] !== 'auth' });
    qc.setQueryData(['auth'], { setupRequired: false, authenticated: true, user: u } satisfies AuthStatus);
  };
  const signOut = () => {
    api.logout().catch(() => undefined).finally(() => reset(SIGNED_OUT));
  };

  if (q.isLoading) return <div className="auth-page"><Loading label="Connecting…" /></div>;
  if (q.isError) {
    return (
      <AuthShell title="Cannot reach the server">
        <div className="alert danger" role="alert">{(q.error as Error).message}</div>
        <button type="button" className="btn primary lg auth-submit" onClick={() => q.refetch()}>Retry</button>
      </AuthShell>
    );
  }
  const s = q.data!;
  if (s.setupRequired) return <SetupPage onDone={signedIn} />;
  if (!s.authenticated || !s.user) return <LoginPage onDone={signedIn} />;
  if (gate.isLoading) return <div className="auth-page"><Loading label="Checking the licence…" /></div>;
  if (blocked || gate.data?.isValid === false) {
    const licensed = (status: LicenseStatus) => {
      qc.removeQueries({ predicate: (query) => query.queryKey[0] !== 'auth' && query.queryKey[0] !== 'licence-gate' });
      qc.setQueryData(['licence-gate'], status);
      setBlocked(false);
    };
    return <LicenseRequiredScreen user={s.user} signOut={signOut} onLicensed={licensed} />;
  }
  return <Ctx.Provider value={{ user: s.user, isAdmin: s.user.role === 'Admin', signOut }}>{children}</Ctx.Provider>;
}
