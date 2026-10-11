import { ReactNode, useEffect } from 'react';
import { EXCEPTION_META, llcColor } from './util';
import type { ExceptionType } from '../api/types';

export function BrandMark({ large }: { large?: boolean }) {
  return <div className={`brand-mark${large ? ' large' : ''}`} aria-hidden="true"><span className="brand-node" /></div>;
}

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="sk-stack" role="status" aria-busy="true" aria-label={label}>
      <div className="sk" /><div className="sk" /><div className="sk" />
      <span className="sr-only">{label}</span>
    </div>
  );
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <BrandMark large />
      <h4>{title}</h4>
      {children && <div>{children}</div>}
    </div>
  );
}

export function ErrorState({ error }: { error: unknown }) {
  return <div className="alert danger" role="alert">{String((error as Error)?.message ?? error)}</div>;
}

const KPI_ICON = {
  node: <svg className="i" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3.2" /><circle cx="12" cy="12" r="8" /></svg>,
  ok: <svg className="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5" /></svg>,
  warn: <svg className="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4l9 16H3z" /><path d="M12 10v4M12 17.5v.5" /></svg>,
};

export function Kpi({ label, value, meta, tone }: { label: string; value: ReactNode; meta?: ReactNode; tone?: 'warn' | 'danger' | 'ok' }) {
  return (
    <div className="kpi">
      <div className="top">
        <span>{label}</span>
        <span className={`ico ${tone ?? ''}`}>{tone === 'ok' ? KPI_ICON.ok : tone ? KPI_ICON.warn : KPI_ICON.node}</span>
      </div>
      <div className="val">{value}</div>
      {meta && <div className="foot">{meta}</div>}
    </div>
  );
}

export function Pill({ tone, children }: { tone?: 'ok' | 'warn' | 'danger' | 'info'; children: ReactNode }) {
  return (
    <span className={`pill ${tone ?? ''}`}>
      <span className="dot" />
      {children}
    </span>
  );
}

export function ExceptionPill({ type }: { type: ExceptionType }) {
  const m = EXCEPTION_META[type];
  return <Pill tone={m.tone}>{m.label}</Pill>;
}

export function Llc({ level }: { level: number }) {
  return <span className="llc" style={{ background: llcColor(level) }}>{level}</span>;
}

export function PageHeader({ crumb, title, actions }: { crumb?: string; title: string; actions?: ReactNode }) {
  useEffect(() => { document.title = `${title} · Cobalt MRP`; }, [title]);
  return (
    <div className="page-head">
      <div>
        {crumb && <div className="crumb">{crumb}</div>}
        <h1>{title}</h1>
      </div>
      <div className="actions">{actions}</div>
    </div>
  );
}

export function Dialog({ title, children, footer, onClose }: { title: string; children: ReactNode; footer: ReactNode; onClose: () => void }) {
  return (
    <div className="overlay" onClick={onClose}>
      <div className="dialog" onClick={(e) => e.stopPropagation()}>
        <div className="d-h"><h3>{title}</h3></div>
        <div className="d-b">{children}</div>
        <div className="d-f">{footer}</div>
      </div>
    </div>
  );
}
