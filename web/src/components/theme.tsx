import { useState } from 'react';
import { useT } from '../i18n';

export type ThemeMode = 'auto' | 'light' | 'dark';
const KEY = 'cobalt-mrp-theme';
const ORDER: ThemeMode[] = ['auto', 'light', 'dark'];
const ICON: Record<ThemeMode, string> = { auto: '◐', light: '☀', dark: '☾' };
const LABEL: Record<ThemeMode, string> = { auto: 'Match system', light: 'Light', dark: 'Dark' };

export function readTheme(): ThemeMode {
  try { const v = localStorage.getItem(KEY); return v === 'light' || v === 'dark' ? v : 'auto'; } catch { return 'auto'; }
}

/** Auto leaves the attribute off so the OS setting decides; light/dark force it. */
export function applyTheme(mode: ThemeMode) {
  const root = document.documentElement;
  if (mode === 'auto') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', mode);
  try { if (mode === 'auto') localStorage.removeItem(KEY); else localStorage.setItem(KEY, mode); } catch { /* private window: the choice just lasts this visit */ }
}

export function ThemeToggle() {
  const tr = useT();
  const [mode, setMode] = useState<ThemeMode>(readTheme);
  const next = ORDER[(ORDER.indexOf(mode) + 1) % ORDER.length];
  const change = () => { applyTheme(next); setMode(next); };
  return (
    <button type="button" className="btn sm theme-toggle" onClick={change} title={tr('Theme: {a} (click for {b})', { a: tr(LABEL[mode]), b: tr(LABEL[next]) })} aria-label={tr('Theme: {a}. Switch to {b}', { a: tr(LABEL[mode]), b: tr(LABEL[next]) })}>
      <span aria-hidden="true">{ICON[mode]}</span>
    </button>
  );
}
