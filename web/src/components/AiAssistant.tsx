import { ReactNode, createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { useAiStatus, useRun } from '../api/hooks';
import type { AiToolUse, AiUsage } from '../api/types';
import { useAuth } from './Auth';
import { useSelectedRun } from './RunContext';

/* ------------------------------------------------------------------------------------------------
   AI assistant UI. Read-only: it explains a saved run. It never changes anything; the planner clicks
   Execute themselves. Nothing is kept in browser storage (plan data stays in memory only).
   ------------------------------------------------------------------------------------------------ */

interface ChatMsg {
  id: number;
  role: 'user' | 'assistant';
  text: string;
  tools?: AiToolUse[];
  usage?: AiUsage;
}

interface PanelCtx {
  open: boolean;
  setOpen: (v: boolean) => void;
  messages: ChatMsg[];
  busy: boolean;
  error: string;
  ask: (question: string) => Promise<void>;
  clear: () => void;
}
const Ctx = createContext<PanelCtx | null>(null);

export function useAiPanel(): PanelCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error('AiPanelProvider is missing');
  return c;
}

let nextId = 1;

/** Models like markdown; the panel shows plain text, so drop the emphasis and heading marks. */
export const plain = (t: string) => t.replace(/\*\*(.+?)\*\*/g, '$1').replace(/^#{1,6}\s+/gm, '').replace(/`([^`]+)`/g, '$1').trim();

export function AiPanelProvider({ children }: { children: ReactNode }) {
  const { runId } = useSelectedRun();
  const [open, setOpen] = useState(false);
  const [messages, setMessages] = useState<ChatMsg[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const msgsRef = useRef<ChatMsg[]>([]);
  msgsRef.current = messages;
  // A thread belongs to one run: switching run starts a fresh conversation.
  const threadRun = useRef(runId);
  const gen = useRef(0);

  useEffect(() => {
    if (threadRun.current !== runId) {
      threadRun.current = runId;
      gen.current += 1;
      setMessages([]); setError(''); setBusy(false);
    }
  }, [runId]);

  const ask = useCallback(async (question: string) => {
    const q = question.trim();
    if (!q || !runId || busy) return;
    const my = gen.current;
    const history = msgsRef.current.map((m) => ({ role: m.role, content: m.text }));
    setMessages((m) => [...m, { id: nextId++, role: 'user', text: q }]);
    setBusy(true); setError('');
    try {
      const r = await api.aiAsk(runId, q, history);
      if (my !== gen.current) return;
      setMessages((m) => [...m, { id: nextId++, role: 'assistant', text: plain(r.answer), tools: r.toolCalls, usage: r.usage }]);
    } catch (e) {
      if (my === gen.current) setError((e as Error).message);
    } finally {
      if (my === gen.current) setBusy(false);
    }
  }, [runId, busy]);

  const clear = useCallback(() => { gen.current += 1; setMessages([]); setError(''); setBusy(false); }, []);
  const value = useMemo(() => ({ open, setOpen, messages, busy, error, ask, clear }), [open, messages, busy, error, ask, clear]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

/** The "Ask AI" button for page headers. Hidden when AI is off, except a gentle hint for administrators. */
export function AskAiButton() {
  const { isAdmin } = useAuth();
  const status = useAiStatus().data;
  const panel = useAiPanel();
  if (!status) return null;
  if (!status.enabled || !status.configured) {
    return isAdmin
      ? <Link className="btn ghost sm ai-hint" to="/settings?tab=ai">{status.enabled ? 'Add an AI key in Settings' : 'Enable AI in Settings'}</Link>
      : null;
  }
  return (
    <button type="button" className="btn sm ai-btn" aria-expanded={panel.open} onClick={() => panel.setOpen(!panel.open)}>
      <svg className="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3l1.8 4.7L18.5 9.5l-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8z" /><path d="M18.5 15l.8 2 2 .8-2 .8-.8 2-.8-2-2-.8 2-.8z" /></svg>
      Ask AI
    </button>
  );
}

/** True when AI is on and has a key (the same condition that shows "Ask AI"). */
function useAiReady() {
  const status = useAiStatus().data;
  return !!status && status.enabled && status.configured;
}

/** A small "Explain" button for a table row: opens the drawer and asks the assistant about that one row. */
export function ExplainButton({ question, label }: { question: string; label: string }) {
  const ready = useAiReady();
  const panel = useAiPanel();
  if (!ready) return null;
  return (
    <button
      type="button" className="btn ghost sm ai-explain" disabled={panel.busy} title="Ask the AI assistant to explain this" aria-label={`Explain: ${label}`}
      onClick={(e) => { e.stopPropagation(); panel.setOpen(true); void panel.ask(question); }}
    >
      <Spark /> Explain
    </button>
  );
}

/** One click: ask the assistant to rank the open exceptions of the selected run. It only suggests; the planner clicks Execute. */
export function TriageButton() {
  const ready = useAiReady();
  const panel = useAiPanel();
  if (!ready) return null;
  const q = 'Triage the open exceptions of this run. List the 5 I should deal with first, most urgent first. For each give the item code, the exception type, why it matters (what demand or capacity it affects, with dates and quantities) and the action you suggest. Then say in one line what can wait.';
  return (
    <button type="button" className="btn sm ai-btn" disabled={panel.busy} title="Ask the AI assistant which exceptions to handle first"
      onClick={() => { panel.setOpen(true); void panel.ask(q); }}>
      <Spark /> Triage
    </button>
  );
}

function copyText(text: string) {
  const fallback = () => {
    const ta = document.createElement('textarea');
    ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    try { document.execCommand('copy'); } finally { document.body.removeChild(ta); }
  };
  if (navigator.clipboard?.writeText) return navigator.clipboard.writeText(text).catch(fallback);
  fallback();
  return Promise.resolve();
}

function CopyButton({ text }: { text: string }) {
  const [done, setDone] = useState(false);
  return (
    <button type="button" className="btn ghost sm" onClick={() => { void copyText(text).then(() => { setDone(true); window.setTimeout(() => setDone(false), 1800); }); }}>
      {done ? 'Copied' : 'Copy'}
    </button>
  );
}

function Lookups({ tools, usage }: { tools?: AiToolUse[]; usage?: AiUsage }) {
  const n = tools?.length ?? 0;
  return (
    <div className="ai-meta">
      <details>
        <summary>{n === 0 ? 'No lookups used' : `Based on ${n} lookup${n === 1 ? '' : 's'}`}</summary>
        {n > 0 && (
          <ul>
            {tools!.map((t, i) => <li key={i}><span className="mono">{t.name}</span>{t.args && <span className="muted"> {t.args}</span>}</li>)}
          </ul>
        )}
      </details>
      {usage && <span className="faint">{usage.totalTokens} tokens</span>}
    </div>
  );
}

const Spark = () => (
  <svg className="i" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3l1.8 4.7L18.5 9.5l-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8z" /></svg>
);

export const AI_CAVEAT = 'AI can be wrong; check the order before acting.';

export function AiDrawer() {
  const panel = useAiPanel();
  const status = useAiStatus().data;
  const { runId } = useSelectedRun();
  const run = useRun(runId).data;
  const [text, setText] = useState('');
  const endRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const live = !!status?.enabled && !!status.configured;

  useEffect(() => { if (panel.open) inputRef.current?.focus(); }, [panel.open]);
  // On wide screens the drawer pushes the page aside (see ai.css) instead of covering its buttons.
  const shown = live && panel.open;
  useEffect(() => {
    document.body.classList.toggle('ai-open', shown);
    return () => document.body.classList.remove('ai-open');
  }, [shown]);
  useEffect(() => { endRef.current?.scrollIntoView?.({ block: 'end' }); }, [panel.messages.length, panel.busy, panel.error]);
  useEffect(() => {
    if (!panel.open) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') panel.setOpen(false); };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [panel]);

  // Starter questions come from the run itself so the item code is a real one.
  const buyItem = run?.plannedOrders.find((o) => o.type === 'PurchaseOrder' || o.type === 'PurchaseRequest')?.itemCode;
  const starters = [
    'What needs attention first?',
    buyItem ? `Why are we buying ${buyItem}?` : 'Why are we buying the first purchased item?',
    'Which transfers were planned and why?',
  ];

  if (!live || !panel.open) return null;
  const send = () => { const q = text; if (!q.trim() || panel.busy) return; setText(''); void panel.ask(q); };
  const last = [...panel.messages].reverse().find((m) => m.role === 'user');

  return (
    <aside className="ai-drawer" role="complementary" aria-label="Ask AI about this run">
      <div className="ai-h">
        <div className="ai-title"><span className="ai-ico"><Spark /></span><div><h3>Ask AI</h3><div className="muted ai-sub">{runId ? `Run ${runId.slice(0, 8)} · ${status?.model}` : 'No run selected'}</div></div></div>
        <div className="ai-h-actions">
          {panel.messages.length > 0 && <button type="button" className="btn ghost sm" onClick={panel.clear}>Clear</button>}
          <button type="button" className="btn ghost sm" aria-label="Close AI panel" onClick={() => panel.setOpen(false)}>Close</button>
        </div>
      </div>

      <div className="ai-thread" aria-live="polite">
        {!runId && <div className="alert warn">Pick a run first; the assistant explains one saved run at a time.</div>}
        {runId && panel.messages.length === 0 && !panel.busy && (
          <div className="ai-empty">
            <p className="muted">Ask about this plan. It looks the answer up in the saved run and cites item codes, quantities and dates. It cannot change anything.</p>
            <div className="ai-starters">
              {starters.map((s) => <button key={s} type="button" className="btn sm" onClick={() => void panel.ask(s)}>{s}</button>)}
            </div>
          </div>
        )}
        {panel.messages.map((m) => (
          <div key={m.id} className={`ai-msg ${m.role}`}>
            <div className="ai-bubble">{m.text}</div>
            {m.role === 'assistant' && (
              <div className="ai-foot">
                <Lookups tools={m.tools} usage={m.usage} />
                <CopyButton text={m.text} />
              </div>
            )}
          </div>
        ))}
        {panel.busy && <div className="ai-msg assistant"><div className="ai-bubble ai-thinking" role="status">Looking it up<span className="dots" aria-hidden="true" /></div></div>}
        {panel.error && (
          <div className="alert danger" role="alert">
            {panel.error}
            {last && <div style={{ marginTop: 8 }}><button type="button" className="btn sm" onClick={() => void panel.ask(last.text)}>Try again</button></div>}
          </div>
        )}
        <div ref={endRef} />
      </div>

      <div className="ai-f">
        <p className="ai-caveat">{AI_CAVEAT} <span className="faint">Plan data is sent to OpenRouter and the model provider.</span></p>
        <div className="ai-input">
          <textarea ref={inputRef} rows={2} maxLength={1000} value={text} disabled={!runId} aria-label="Your question" placeholder="Ask about this run…"
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } }} />
          <button type="button" className="btn primary" disabled={!runId || panel.busy || !text.trim()} onClick={send}>Send</button>
        </div>
      </div>
    </aside>
  );
}

/** Dashboard: "Summarise this run" button and the card it fills. */
export function useRunSummary(runId: string | undefined) {
  const [state, setState] = useState<{ run?: string; busy: boolean; error: string; summary: string; tools: AiToolUse[]; usage?: AiUsage }>({ busy: false, error: '', summary: '', tools: [] });
  const gen = useRef(0);
  useEffect(() => { gen.current += 1; setState({ busy: false, error: '', summary: '', tools: [] }); }, [runId]);
  const go = async () => {
    if (!runId) return;
    const my = ++gen.current;
    setState({ run: runId, busy: true, error: '', summary: '', tools: [] });
    try {
      const r = await api.aiSummary(runId);
      if (my === gen.current) setState({ run: runId, busy: false, error: '', summary: plain(r.summary), tools: r.toolCalls, usage: r.usage });
    } catch (e) {
      if (my === gen.current) setState({ run: runId, busy: false, error: (e as Error).message, summary: '', tools: [] });
    }
  };
  const dismiss = () => { gen.current += 1; setState({ busy: false, error: '', summary: '', tools: [] }); };
  return { ...state, go, dismiss };
}

export function SummariseButton({ onClick, busy }: { onClick: () => void; busy: boolean }) {
  const status = useAiStatus().data;
  if (!status?.enabled || !status.configured) return null;
  return <button type="button" className="btn sm ai-btn" disabled={busy} onClick={onClick}><Spark />{busy ? 'Summarising…' : 'Summarise this run'}</button>;
}

export function SummaryCard({ s }: { s: ReturnType<typeof useRunSummary> }) {
  if (!s.busy && !s.error && !s.summary) return null;
  return (
    <div className="card ai-summary" aria-live="polite">
      <div className="card-h">
        <h3><span className="ai-ico"><Spark /></span> Run summary <span className="pill info"><span className="dot" />AI-generated</span></h3>
        <div className="ai-h-actions">
          {s.summary && <CopyButton text={s.summary} />}
          <button type="button" className="btn ghost sm" onClick={s.dismiss}>Dismiss</button>
        </div>
      </div>
      <div className="card-b">
        {s.busy && <div className="ai-thinking" role="status">Reading the run<span className="dots" aria-hidden="true" /></div>}
        {s.error && <div className="alert danger" role="alert">{s.error}</div>}
        {s.summary && <div className="ai-summary-text">{s.summary}</div>}
        {s.summary && <Lookups tools={s.tools} usage={s.usage} />}
        <p className="ai-caveat">{AI_CAVEAT}</p>
      </div>
    </div>
  );
}
