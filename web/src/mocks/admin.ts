// In-memory stand-in for the auth / users / settings / status endpoints (offline mock mode).
// Lives only in the JS heap of the page; nothing is written to browser storage.
import type {
  AppSettings, AuthStatus, ResourceInfo, AuthUser, DbSettings, NewUser, OkResult, Role, StatusInfo,
} from '../api/types';

export class MockHttpError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

const users: { username: string; role: Role; password: string }[] = [
  { username: 'admin', role: 'Admin', password: 'admin-mock' },
  { username: 'planner', role: 'Planner', password: 'planner-mock' },
  { username: 'buyer', role: 'Buyer', password: 'buyer-mock' },
];
let current: AuthUser | null = { username: 'admin', role: 'Admin' };
let storage: 'sql' | 'memory' = 'memory';

const db = (database: string, server = ''): DbSettings => ({
  server, database, authentication: 'Windows', userName: '', password: '', passwordSet: false,
  trustServerCertificate: true, connectionStringOverride: '',
});

let settings: AppSettings = {
  cobaltDb: db('CobaltMrp', 'localhost'),
  b1Db: db('', ''),
  serviceLayer: {
    enabled: false, baseUrl: 'https://b1-server:50000/b1s/v1', companyDb: '', userName: '', password: '',
    passwordSet: false, allowInvalidCertificates: false, defaultVendor: '', itemVendors: [],
  },
  planning: { defaultSource: 'mock', horizonDays: 60, bucketDays: 7, hoursPerUnitPerDay: 8, forecastLookbackDays: 0,
    forecastConsumption: 'Forward', leadTimePriority: ['Supplier', 'Item', 'ItemGroup'], leadTimeMode: 'FirstAvailable', defaultLeadTimeDays: 0, useB1Calendar: true, workingDays: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'],
    nonWorkingDates: [{ date: '2026-12-25', reason: 'Christmas' }], resources: [],
    defaultLevel: 'Warehouse', purchaseDocument: 'Order', documentGrouping: 'Supplier', transferLeadTimeDays: 1,
  },
  ai: { enabled: false, apiKey: '', apiKeySet: false, model: 'anthropic/claude-opus-5', maxTokens: 1024 },
  notifications: { enabled: false, smtpHost: '', smtpPort: 587, useSsl: true, userName: '', password: '', passwordSet: false, from: '', recipients: '', publicUrl: '', notifyOn: 'ScheduledRuns' },
  schedule: { enabled: false, time: '02:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'], templateId: '' },
};
// Mock "server side" secrets, kept apart from the object returned to the UI.
const secrets = { cobalt: '', b1: '', sl: '', ai: '', smtp: '' };

const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x));
export const currentUsername = () => current?.username ?? 'unknown';
export const requireSignedIn = () => { if (!current) throw new MockHttpError(401, 'Not signed in'); };
const requireAdmin = () => {
  requireSignedIn();
  if (current!.role !== 'Admin') throw new MockHttpError(403, 'Administrator role required');
};

export const authStatus = (): AuthStatus => ({ setupRequired: false, authenticated: !!current, user: current && { ...current } });

export function login(username: string, password: string): AuthUser {
  const u = users.find((x) => x.username.toLowerCase() === username.toLowerCase());
  if (!u || !password) throw new MockHttpError(401, 'Invalid username or password.');
  current = { username: u.username, role: u.role };
  return { ...current };
}
export const logout = () => { current = null; };
export const setup = (): never => { throw new MockHttpError(400, 'Setup is not available: an administrator already exists.'); };

export function listUsers() {
  requireAdmin();
  return users.map(({ username, role }) => ({ username, role }));
}
export function addUser(n: NewUser) {
  requireAdmin();
  if (!n.username.trim()) throw new MockHttpError(400, 'Username is required.');
  if (n.password.length < 8) throw new MockHttpError(400, 'Password must be at least 8 characters.');
  if (users.some((u) => u.username.toLowerCase() === n.username.toLowerCase())) throw new MockHttpError(400, 'That username already exists.');
  users.push({ username: n.username.trim(), role: n.role, password: n.password });
  return { username: n.username.trim(), role: n.role };
}
export function deleteUser(username: string) {
  requireAdmin();
  if (current?.username === username) throw new MockHttpError(400, 'You cannot delete your own account.');
  const u = users.find((x) => x.username === username);
  if (!u) return;
  if (u.role === 'Admin' && users.filter((x) => x.role === 'Admin').length === 1) throw new MockHttpError(400, 'Cannot delete the last administrator.');
  users.splice(users.indexOf(u), 1);
}
export function setPassword(username: string, password: string) {
  requireAdmin();
  if (password.length < 8) throw new MockHttpError(400, 'Password must be at least 8 characters.');
  const u = users.find((x) => x.username === username);
  if (u) u.password = password;
}

export function getSettings(): AppSettings {
  requireAdmin();
  return clone(settings);
}
export function putSettings(s: AppSettings): AppSettings {
  requireAdmin();
  const next = clone(s);
  if (next.cobaltDb.password) secrets.cobalt = next.cobaltDb.password;
  if (next.b1Db.password) secrets.b1 = next.b1Db.password;
  if (next.serviceLayer.password) secrets.sl = next.serviceLayer.password;
  if (next.ai.apiKey) secrets.ai = next.ai.apiKey;
  next.ai.apiKey = ''; next.ai.apiKeySet = !!secrets.ai;
  if (next.notifications.password) secrets.smtp = next.notifications.password;
  next.notifications.password = ''; next.notifications.passwordSet = !!secrets.smtp;
  next.cobaltDb.password = ''; next.b1Db.password = ''; next.serviceLayer.password = '';
  next.cobaltDb.passwordSet = !!secrets.cobalt;
  next.b1Db.passwordSet = !!secrets.b1;
  next.serviceLayer.passwordSet = !!secrets.sl;
  settings = next;
  return clone(settings);
}

const dbReady = (d: DbSettings) =>
  !!(d.connectionStringOverride || (d.server && d.database)) &&
  (d.authentication === 'Windows' || !!d.userName);

export function testCobaltDb(d: DbSettings): OkResult {
  requireAdmin();
  if (!dbReady(d)) return { ok: false, message: 'Server and database are required (and a user name for SQL authentication).' };
  return { ok: true, message: `Connected to ${d.server || 'server'} (Microsoft SQL Server 2019). Database "${d.database}" found; Cobalt MRP tables ${storage === 'sql' ? 'are up to date.' : 'are missing - use "Create / update tables".'}` };
}
export function testB1Db(d: DbSettings): OkResult {
  requireAdmin();
  if (!dbReady(d)) return { ok: false, message: 'Server and database are required (and a user name for SQL authentication).' };
  return { ok: true, message: 'Connected. Found 1,284 items and 37 open orders.' };
}
export function testServiceLayer(s: AppSettings['serviceLayer']): OkResult {
  requireAdmin();
  if (!s.baseUrl || !s.companyDb || !s.userName) return { ok: false, message: 'Base URL, company DB and user are required.' };
  if (!s.password && !s.passwordSet) return { ok: false, message: 'Login failed: no password supplied.' };
  return { ok: true, message: 'Service Layer login and logout succeeded.' };
}
export function testEmail(n: AppSettings['notifications']): OkResult {
  requireAdmin();
  if (!n.smtpHost.trim()) return { ok: false, message: 'Enter the SMTP server.' };
  if (!n.from.trim()) return { ok: false, message: 'Enter the From address.' };
  if (!n.recipients.split(/[,;\n]/).some((x) => x.includes('@'))) return { ok: false, message: 'Enter at least one recipient email address.' };
  return { ok: true, message: 'Test email sent (mock: nothing was really sent).' };
}
export function automationStatus() {
  requireAdmin();
  const s = settings.schedule;
  const next = new Date(); next.setDate(next.getDate() + 1); next.setHours(Number(s.time.slice(0, 2)), Number(s.time.slice(3, 5)), 0, 0);
  return {
    schedule: { ...s, nextRunAt: s.enabled ? next.toISOString() : null },
    notificationsConfigured: settings.notifications.enabled && !!settings.notifications.smtpHost,
    lastRun: { at: null, id: null, ok: null, message: null },
    lastNotice: { at: null, ok: null, message: null, newExceptions: 0 },
  };
}
export function testAi(a: AppSettings['ai']): OkResult {
  requireAdmin();
  if (!a.apiKey && !a.apiKeySet) return { ok: false, message: 'Enter the OpenRouter API key first.' };
  return { ok: true, message: `Connected (mock). ${a.model || 'The model'} answered (12 tokens used).` };
}
export const listAiModels = () => {
  requireAdmin();
  return { ok: true, message: '4 models available.', models: ['anthropic/claude-opus-5', 'google/gemini-2.0-flash-001', 'meta-llama/llama-3.3-70b-instruct:free', 'openai/gpt-4o-mini'] };
};
export function aiStatus() {
  requireSignedIn();
  return { enabled: settings.ai.enabled, configured: !!secrets.ai, model: settings.ai.model };
}
const mockAiGuard = () => {
  requireSignedIn();
  if (!settings.ai.enabled) throw new MockHttpError(400, 'The AI assistant is turned off. An administrator can enable it in Settings > AI.');
  if (!secrets.ai) throw new MockHttpError(400, 'The AI assistant has no API key yet. An administrator can add one in Settings > AI.');
};
const mockUsage = { promptTokens: 410, completionTokens: 96, totalTokens: 506 };
export function aiAsk(_runId: string, question: string) {
  mockAiGuard();
  return {
    answer: `(Mock answer) You asked: "${question}". In live mode the assistant looks this up in the saved run and quotes item codes, quantities and dates exactly.`,
    toolCalls: [{ name: 'list_planned_orders', args: 'limit=20' }, { name: 'get_run_summary', args: '' }],
    usage: mockUsage,
  };
}
export function aiSummary(_runId: string) {
  mockAiGuard();
  return {
    summary: [
      '- (Mock summary) 3 exceptions need attention first: release-in-past orders cannot arrive on time.',
      '- Mostly purchase orders, a few transfers between warehouses.',
      '- No capacity overload.',
    ].join('\n'),
    toolCalls: [{ name: 'get_run_summary', args: '' }, { name: 'list_exceptions', args: 'status=open, limit=10' }],
    usage: mockUsage,
  };
}

export function initSchema(): OkResult {
  requireAdmin();
  if (!dbReady(settings.cobaltDb)) return { ok: false, message: 'Save a valid Cobalt database connection first.' };
  storage = 'sql';
  return { ok: true, message: 'Created 7 tables; schema is at version 3.' };
}

export function getStatus(): StatusInfo {
  requireSignedIn();
  return {
    version: '0.1.0-mock', storage, b1Configured: dbReady(settings.b1Db),
    writebackEnabled: settings.serviceLayer.enabled, defaultSource: settings.planning.defaultSource,
    horizonDays: settings.planning.horizonDays, bucketDays: settings.planning.bucketDays,
  };
}

const MOCK_RESOURCES = [
  { code: 'WC-MACH', name: 'CNC machining centre', units: 2 },
  { code: 'WC-ASSY', name: 'Assembly line', units: 3 },
  { code: 'WC-WIND', name: 'Motor winding', units: 1 },
  { code: 'WC-TEST', name: 'Test bench', units: 1 },
];
export function listResources(source: string): ResourceInfo[] {
  requireAdmin();
  if (source === 'b1-sql' && !(settings.b1Db.connectionStringOverride || (settings.b1Db.server && settings.b1Db.database)))
    throw new MockHttpError(400, 'The SAP B1 database is not configured. Set it up in Settings first.');
  return MOCK_RESOURCES.map((r) => {
    const o = settings.planning.resources.find((x) => x.code === r.code);
    return {
      ...r,
      hoursPerDay: o?.hoursPerDay ?? settings.planning.hoursPerUnitPerDay * r.units,
      efficiencyPercent: o?.efficiencyPercent ?? 100,
      setupHoursPerOrder: o?.setupHoursPerOrder ?? 0,
      configured: !!o,
    };
  });
}

// ----- helpers shared with the master-data / wizard mocks -----
export const planningSettings = () => settings.planning;
export const currentUserName = () => current?.username ?? 'unknown';
export const b1Configured = () => dbReady(settings.b1Db);
