import { ReactNode, createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { AR } from './ar';

/* ------------------------------------------------------------------------------------------------
   Minimal EN / Arabic support, following the Cobalt design system (EN/AR toggle, <html dir="rtl" lang="ar">).
   The English text IS the key: t('Planned Orders') returns the Arabic if there is one, otherwise the English,
   so a string nobody has translated yet just stays English. `{name}` placeholders are filled from `vars`.
   ------------------------------------------------------------------------------------------------ */

export type Lang = 'en' | 'ar';
const KEY = 'cobalt-mrp-lang';

export type TFn = (text: string, vars?: Record<string, string | number>) => string;

interface Ctx { lang: Lang; setLang: (l: Lang) => void; t: TFn; dir: 'ltr' | 'rtl' }

const fill = (s: string, vars?: Record<string, string | number>) =>
  vars ? s.replace(/\{(\w+)\}/g, (m, k: string) => (k in vars ? String(vars[k]) : m)) : s;

/** Pure lookup, also used by tests. */
export function translate(lang: Lang, text: string, vars?: Record<string, string | number>): string {
  return fill(lang === 'ar' ? AR[text] ?? text : text, vars);
}

const Context = createContext<Ctx>({ lang: 'en', setLang: () => {}, t: (s, v) => fill(s, v), dir: 'ltr' });

function readLang(): Lang {
  try { return localStorage.getItem(KEY) === 'ar' ? 'ar' : 'en'; } catch { return 'en'; }
}

export function LangProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(readLang);
  const dir = lang === 'ar' ? 'rtl' : 'ltr';

  // The document carries lang and dir, so the [dir="rtl"] stylesheet and the browser's own text handling follow.
  useEffect(() => {
    document.documentElement.lang = lang;
    document.documentElement.dir = dir;
  }, [lang, dir]);

  const setLang = useCallback((l: Lang) => {
    setLangState(l);
    try { if (l === 'ar') localStorage.setItem(KEY, 'ar'); else localStorage.removeItem(KEY); } catch { /* the choice just lasts this visit */ }
  }, []);
  const t = useCallback<TFn>((text, vars) => translate(lang, text, vars), [lang]);
  const value = useMemo(() => ({ lang, setLang, t, dir } as Ctx), [lang, setLang, t, dir]);
  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export const useLang = () => useContext(Context);
export const useT = () => useContext(Context).t;

/** Compact EN / عربي switch (the Cobalt design-system `.lang-switch`). */
export function LangSwitch() {
  const { lang, setLang } = useLang();
  return (
    <div className="lang-switch" role="group" aria-label="Language">
      <button type="button" lang="en" className={lang === 'en' ? 'on' : ''} aria-pressed={lang === 'en'} onClick={() => setLang('en')}>EN</button>
      <button type="button" lang="ar" className={lang === 'ar' ? 'on' : ''} aria-pressed={lang === 'ar'} onClick={() => setLang('ar')}>عربي</button>
    </div>
  );
}
