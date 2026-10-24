import type {
  AppSettings, AuthStatus, AuthUser, Credentials, DbSettings, NewUser, OkResult, ServiceLayerSettings, StatusInfo,
  Forecast,
  CalendarQuery,
  CalendarResponse,
  ResourceInfo,
  MrpRun,
  MrpRunDetail,
  PeggingLink,
  ReleaseResult,
  TriggerRunRequest,
  BulkItemRequest, DataSource, ItemGroupInfo, MasterItemPage, MasterItemQuery, MasterItemRow, MasterOverride,
  MasterSupplierPage, MasterSupplierQuery, MasterSupplierRow, RunCriteria, RunPreview, SupplierLookup,
  PreviewDetails, PreviewDetailsRequest,
  AiAskResponse, AiModelsResult, AiSettings, AiStatus, AiSummaryResponse, AiTurn,
  BackupRunResult, BackupSettings, BackupView, LicenseStatus,
} from './types';
import * as mlc from '../mocks/licence';
import * as mock from '../mocks/data';
import * as adm from '../mocks/admin';
import * as msl from '../mocks/saplog';
import * as mtp from '../mocks/templates';
import * as md from '../mocks/masterdata';
import * as mex from '../mocks/exceptions';
import * as mpd from '../mocks/previewDetails';
import type { ExecutePayload, ExecutePreview, ExecuteRequest, ExecuteResult, SapLogEntry, SapLogQuery, SupplierImportResponse, SupplierImportRow, PushPreview, PushRequestItem, PushExecuteResult, RunTemplate, NotificationSettings, AutomationStatus, ScheduledOutcome, ScheduleView } from './types';

const API_BASE = (import.meta.env.VITE_API_BASE as string) || '/api';
export const USE_MOCK =
  (import.meta.env.VITE_USE_MOCK ?? 'true').toString().toLowerCase() !== 'false';

// Simulate network latency in mock mode so loading/empty states are visible.
const delay = (ms = 350) => new Promise((r) => setTimeout(r, ms));

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

// Called when any non-auth API call answers 401 (session expired / signed out elsewhere).
let onUnauthorized: (() => void) | null = null;
export const setUnauthorizedHandler = (fn: (() => void) | null) => { onUnauthorized = fn; };

// Called when an API call answers 402 {licenseRequired:true}: the whole app switches to the Licence screen.
// Sign-in and licence/backup calls are never 402, so they never trigger it.
let onLicenseRequired: ((message: string) => void) | null = null;
export const setLicenseRequiredHandler = (fn: ((message: string) => void) | null) => { onLicenseRequired = fn; };
const isLicenseGate = (status: number, text: string, path: string): boolean => {
  if (status !== 402 || path.startsWith('/auth/')) return false;
  try { return JSON.parse(text)?.licenseRequired === true; } catch { return false; }
};

async function http<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    credentials: 'same-origin',
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers || {}) },
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    let message = text;
    try {
      const j = JSON.parse(text);
      if (j && typeof j.message === 'string') message = j.message;
      else if (j && typeof j.title === 'string') message = j.title;
    } catch { /* not JSON */ }
    if (res.status === 401 && !path.startsWith('/auth/')) onUnauthorized?.();
    if (isLicenseGate(res.status, text, path)) onLicenseRequired?.(message);
    throw new ApiError(res.status, message || `${res.status} ${res.statusText}`);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Run a mock handler with latency; translate mock HTTP errors like the real client does. */
async function mocked<T>(fn: () => T, ms = 250, auth = false): Promise<T> {
  await delay(ms);
  try {
    return fn();
  } catch (e) {
    if (e instanceof adm.MockHttpError) {
      if (e.status === 401 && !auth) onUnauthorized?.();
      throw new ApiError(e.status, e.message);
    }
    throw e;
  }
}
const post = <T>(path: string, body?: unknown) =>
  http<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

function qs(o: Record<string, string | number | undefined>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(o)) if (v !== undefined && v !== '') p.set(k, String(v));
  return p.toString();
}
const put = <T>(path: string, body: unknown) => http<T>(path, { method: 'PUT', body: JSON.stringify(body) });
const itemQs = (q: MasterItemQuery) => qs({ ...q } as Record<string, string | number | undefined>);

export const api = {
  // ----- run wizard, lookups -----
  async runDefaults(source: DataSource): Promise<RunCriteria> {
    if (USE_MOCK) return mocked(() => md.runDefaults(source), 200);
    return http<RunCriteria>(`/runs/defaults?${qs({ source })}`);
  },
  async previewRun(criteria: RunCriteria): Promise<RunPreview> {
    if (USE_MOCK) return mocked(() => md.preview(criteria), 600);
    return post<RunPreview>('/runs/preview', { criteria });
  },
  async previewDetails(req: PreviewDetailsRequest): Promise<PreviewDetails> {
    if (USE_MOCK) return mocked(() => mpd.previewDetails(req), 350);
    return post<PreviewDetails>('/runs/preview/details', req);
  },
  /** Whole list as CSV (ignores `take`, honours `search`). Returns the blob and the file name to save it as. */
  async previewDetailsExport(req: PreviewDetailsRequest): Promise<{ blob: Blob; filename: string }> {
    if (USE_MOCK) {
      await delay(250);
      return { blob: new Blob([mpd.previewDetailsCsv(req)], { type: 'text/csv;charset=utf-8' }), filename: `${req.metric}.csv` };
    }
    const res = await fetch(`${API_BASE}/runs/preview/details/export`, {
      method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(req),
    });
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      let message = text;
      try { const j = JSON.parse(text); if (j && typeof j.message === 'string') message = j.message; } catch { /* not JSON */ }
      if (res.status === 401) onUnauthorized?.();
      if (isLicenseGate(res.status, text, '/runs/preview/details/export')) onLicenseRequired?.(message);
      throw new ApiError(res.status, message || `${res.status} ${res.statusText}`);
    }
    const cd = res.headers.get('Content-Disposition') ?? '';
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
    return { blob: await res.blob(), filename: m ? decodeURIComponent(m[1]) : `${req.metric}.csv` };
  },
  async lookupItemGroups(source: DataSource): Promise<ItemGroupInfo[]> {
    if (USE_MOCK) return mocked(() => md.lookupGroups(source), 150);
    return http<ItemGroupInfo[]>(`/lookups/item-groups?${qs({ source })}`);
  },
  async lookupSuppliers(source: DataSource, search: string): Promise<SupplierLookup[]> {
    if (USE_MOCK) return mocked(() => md.lookupSuppliers(source, search, 50), 150);
    return http<SupplierLookup[]>(`/lookups/suppliers?${qs({ source, search, take: 50 })}`);
  },

  // ----- master data -----
  async listMasterItems(q: MasterItemQuery): Promise<MasterItemPage> {
    if (USE_MOCK) return mocked(() => md.listItems(q), 250);
    return http<MasterItemPage>(`/masterdata/items?${itemQs(q)}`);
  },
  async putMasterItem(code: string, o: Partial<MasterOverride>): Promise<MasterItemRow> {
    if (USE_MOCK) return mocked(() => md.putItem(code, o), 200);
    return put<MasterItemRow>(`/masterdata/items/${encodeURIComponent(code)}`, o);
  },
  async bulkMasterItems(req: BulkItemRequest): Promise<{ updated: number }> {
    if (USE_MOCK) return mocked(() => md.bulkItems(req), 350);
    return post<{ updated: number }>('/masterdata/items/bulk', req);
  },
  async resetMasterItem(code: string): Promise<void> {
    if (USE_MOCK) return mocked(() => md.deleteItem(code), 200);
    return http<void>(`/masterdata/items/${encodeURIComponent(code)}`, { method: 'DELETE' });
  },
  /** Live mode: a URL for a plain download link carrying the current filters. Mock mode: null (use exportMockCsv). */
  masterItemsExportHref(q: MasterItemQuery): string | null {
    if (USE_MOCK) return null;
    const { page: _p, pageSize: _s, ...rest } = q; void _p; void _s;
    return `${API_BASE}/masterdata/items/export?${itemQs(rest as MasterItemQuery)}`;
  },
  exportMockCsv(q: MasterItemQuery): string { return md.exportCsv(q); },
  async listMasterSuppliers(q: MasterSupplierQuery): Promise<MasterSupplierPage> {
    if (USE_MOCK) return mocked(() => md.listSuppliers(q), 250);
    return http<MasterSupplierPage>(`/masterdata/suppliers?${qs({ ...q } as Record<string, string | number | undefined>)}`);
  },
  async listRunTemplates(): Promise<RunTemplate[]> {
    if (USE_MOCK) return mocked(() => mtp.listTemplates(), 200);
    return http<RunTemplate[]>('/run-templates');
  },
  async saveRunTemplate(name: string, criteria: RunCriteria): Promise<RunTemplate> {
    if (USE_MOCK) return mocked(() => mtp.saveTemplate(name, criteria), 250);
    return post<RunTemplate>('/run-templates', { name, criteria });
  },
  async updateRunTemplate(id: string, body: { name?: string; criteria?: RunCriteria }): Promise<RunTemplate> {
    if (USE_MOCK) return mocked(() => mtp.updateTemplate(id, body), 250);
    return put<RunTemplate>(`/run-templates/${encodeURIComponent(id)}`, body);
  },
  async deleteRunTemplate(id: string): Promise<void> {
    if (USE_MOCK) return mocked(() => mtp.deleteTemplate(id), 200);
    await http<void>(`/run-templates/${encodeURIComponent(id)}`, { method: 'DELETE' });
  },
  async previewPushToSap(itemCodes: string[]): Promise<PushPreview> {
    if (USE_MOCK) return mocked(() => md.previewPush(itemCodes), 400);
    return post<PushPreview>('/masterdata/items/push-to-sap/preview', { itemCodes });
  },
  async pushToSap(items: PushRequestItem[], clearOverrides: boolean): Promise<PushExecuteResult> {
    if (USE_MOCK) return mocked(() => md.pushToSap(items, clearOverrides), 600);
    return post<PushExecuteResult>('/masterdata/items/push-to-sap', { items, confirm: true, clearOverrides });
  },
  async importMasterSuppliers(source: DataSource, rows: SupplierImportRow[], dryRun: boolean): Promise<SupplierImportResponse> {
    if (USE_MOCK) return mocked(() => md.importSuppliers(rows, dryRun), 400);
    return post<SupplierImportResponse>(`/masterdata/suppliers/import?${qs({ source, dryRun: String(dryRun) })}`, { rows });
  },
  async putMasterSupplier(cardCode: string, body: { leadTimeDays: number | null; notes: string }): Promise<MasterSupplierRow> {
    if (USE_MOCK) return mocked(() => md.putSupplier(cardCode, body), 200);
    return put<MasterSupplierRow>(`/masterdata/suppliers/${encodeURIComponent(cardCode)}`, body);
  },

  // ----- licence + scheduled backup (Admin; the server never licence-gates these) -----
  async licenseStatus(): Promise<LicenseStatus> {
    if (USE_MOCK) return mocked(() => mlc.licenseStatus(), 150, true);
    return http<LicenseStatus>('/admin/license');
  },
  async saveLicense(licenseKey: string): Promise<LicenseStatus> {
    if (USE_MOCK) return mocked(() => mlc.saveLicense(licenseKey), 300);
    return post<LicenseStatus>('/admin/license', { licenseKey });
  },
  async backupView(): Promise<BackupView> {
    if (USE_MOCK) return mocked(() => mlc.backupView(), 200);
    return http<BackupView>('/admin/backup/schedule');
  },
  async saveBackupSchedule(s: BackupSettings): Promise<BackupView> {
    if (USE_MOCK) return mocked(() => mlc.saveBackup(s), 250);
    return post<BackupView>('/admin/backup/schedule', s);
  },
  async runBackup(): Promise<{ result: BackupRunResult; view: BackupView }> {
    if (USE_MOCK) return mocked(() => mlc.runBackup(), 500);
    return post<{ result: BackupRunResult; view: BackupView }>('/admin/backup/run');
  },
  /** The backup zip right now, for the browser to save. */
  async downloadBackup(): Promise<{ blob: Blob; filename: string }> {
    if (USE_MOCK) { await delay(250); return { blob: new Blob(['mock backup'], { type: 'application/zip' }), filename: 'cobalt-mrp-backup-mock.zip' }; }
    const res = await fetch(`${API_BASE}/admin/backup/download`, { credentials: 'same-origin' });
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      let message = text;
      try { const j = JSON.parse(text); if (j && typeof j.message === 'string') message = j.message; } catch { /* not JSON */ }
      if (res.status === 401) onUnauthorized?.();
      throw new ApiError(res.status, message || `${res.status} ${res.statusText}`);
    }
    const cd = res.headers.get('Content-Disposition') ?? '';
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
    return { blob: await res.blob(), filename: m ? decodeURIComponent(m[1]) : 'cobalt-mrp-backup.zip' };
  },

  // ----- auth -----
  async authStatus(): Promise<AuthStatus> {
    if (USE_MOCK) return mocked(adm.authStatus, 150, true);
    return http<AuthStatus>('/auth/status');
  },
  async setup(c: Credentials): Promise<AuthUser> {
    if (USE_MOCK) return mocked(() => adm.setup(), 250, true);
    return post<AuthUser>('/auth/setup', c);
  },
  async login(c: Credentials): Promise<AuthUser> {
    if (USE_MOCK) return mocked(() => adm.login(c.username, c.password), 250, true);
    return post<AuthUser>('/auth/login', c);
  },
  async logout(): Promise<void> {
    if (USE_MOCK) return mocked(adm.logout, 100, true);
    return post<void>('/auth/logout');
  },
  async getStatus(): Promise<StatusInfo> {
    if (USE_MOCK) return mocked(adm.getStatus, 150);
    return http<StatusInfo>('/status');
  },

  // ----- users (Admin) -----
  async listUsers(): Promise<AuthUser[]> {
    if (USE_MOCK) return mocked(adm.listUsers);
    return http<AuthUser[]>('/users');
  },
  async createUser(u: NewUser): Promise<AuthUser> {
    if (USE_MOCK) return mocked(() => adm.addUser(u));
    return post<AuthUser>('/users', u);
  },
  async deleteUser(username: string): Promise<void> {
    if (USE_MOCK) return mocked(() => adm.deleteUser(username));
    return http<void>(`/users/${encodeURIComponent(username)}`, { method: 'DELETE' });
  },
  async setUserPassword(username: string, password: string): Promise<void> {
    if (USE_MOCK) return mocked(() => adm.setPassword(username, password));
    return post<void>(`/users/${encodeURIComponent(username)}/password`, { password });
  },

  // ----- settings (Admin) -----
  async getSettings(): Promise<AppSettings> {
    if (USE_MOCK) return mocked(adm.getSettings);
    return http<AppSettings>('/settings');
  },
  async saveSettings(s: AppSettings): Promise<AppSettings> {
    if (USE_MOCK) return mocked(() => adm.putSettings(s), 400);
    // The contract does not say what PUT returns; re-read afterwards for a clean state.
    await http<unknown>('/settings', { method: 'PUT', body: JSON.stringify(s) });
    return http<AppSettings>('/settings');
  },
  async testCobaltDb(d: DbSettings): Promise<OkResult> {
    if (USE_MOCK) return mocked(() => adm.testCobaltDb(d), 700);
    return post<OkResult>('/settings/test/cobalt-db', d);
  },
  async testB1Db(d: DbSettings): Promise<OkResult> {
    if (USE_MOCK) return mocked(() => adm.testB1Db(d), 700);
    return post<OkResult>('/settings/test/b1-db', d);
  },
  async testServiceLayer(s: ServiceLayerSettings): Promise<OkResult> {
    if (USE_MOCK) return mocked(() => adm.testServiceLayer(s), 700);
    return post<OkResult>('/settings/test/service-layer', s);
  },
  async testEmail(n: NotificationSettings): Promise<OkResult> {
    if (USE_MOCK) return mocked(() => adm.testEmail(n), 700);
    return post<OkResult>('/settings/test/email', n);
  },
  async automationStatus(): Promise<AutomationStatus> {
    if (USE_MOCK) return mocked(() => adm.automationStatus(), 200);
    return http<AutomationStatus>('/automation/status');
  },
  async schedules(): Promise<ScheduleView[]> {
    if (USE_MOCK) return mocked(() => mtp.schedulesView(), 200);
    return http<ScheduleView[]>('/schedules');
  },
  async runScheduleNow(id: string): Promise<ScheduledOutcome> {
    if (USE_MOCK) return mocked(() => ({ ok: true, message: 'Mock: the scheduled run would start now.', runId: null }), 800);
    return post<ScheduledOutcome>(`/schedules/${encodeURIComponent(id)}/run-now`);
  },
  async testAi(a: AiSettings): Promise<OkResult> {
    if (USE_MOCK) return mocked(() => adm.testAi(a), 800);
    return post<OkResult>('/settings/test/ai', a);
  },
  async listAiModels(a: AiSettings): Promise<AiModelsResult> {
    if (USE_MOCK) return mocked(() => adm.listAiModels(), 500);
    return post<AiModelsResult>('/settings/ai/models', a);
  },

  // ----- AI assistant (read-only over a saved run) -----
  async getAiStatus(): Promise<AiStatus> {
    if (USE_MOCK) return mocked(adm.aiStatus, 100);
    return http<AiStatus>('/ai/status');
  },
  async aiAsk(runId: string, question: string, history: AiTurn[]): Promise<AiAskResponse> {
    if (USE_MOCK) return mocked(() => adm.aiAsk(runId, question), 900);
    return post<AiAskResponse>('/ai/ask', { runId, question, history });
  },
  async aiSummary(runId: string): Promise<AiSummaryResponse> {
    if (USE_MOCK) return mocked(() => adm.aiSummary(runId), 900);
    return post<AiSummaryResponse>('/ai/summary', { runId });
  },
  async initSchema(): Promise<OkResult> {
    if (USE_MOCK) return mocked(adm.initSchema, 900);
    return post<OkResult>('/settings/init-schema');
  },

  // ----- planning data -----
  async listResources(source: string): Promise<ResourceInfo[]> {
    if (USE_MOCK) return mocked(() => adm.listResources(source), 300);
    return http<ResourceInfo[]>(`/resources?source=${encodeURIComponent(source)}`);
  },
  async getCalendar(runId: string, q: CalendarQuery): Promise<CalendarResponse> {
    if (USE_MOCK) {
      await delay(200);
      return mock.getCalendar(runId, q);
    }
    const p = new URLSearchParams({ from: q.from, to: q.to });
    if (q.kinds?.length) p.set('kinds', q.kinds.join(','));
    if (q.item) p.set('item', q.item);
    if (q.warehouse) p.set('warehouse', q.warehouse);
    return http<CalendarResponse>(`/runs/${encodeURIComponent(runId)}/calendar?${p}`);
  },
  async getWarehouses(source: string): Promise<string[]> {
    if (USE_MOCK) return [...mock.META.WAREHOUSES];
    return http<string[]>(`/warehouses?source=${encodeURIComponent(source)}`);
  },
  async listRuns(): Promise<MrpRun[]> {
    if (USE_MOCK) {
      await delay();
      return [...mock.runs].sort((a, b) => b.runAt.localeCompare(a.runAt));
    }
    return http<MrpRun[]>('/runs');
  },

  async triggerRun(req: TriggerRunRequest): Promise<MrpRun> {
    if (USE_MOCK) {
      await delay(900);
      return mocked(() => { if (req.criteria) md.validateCriteria(req.criteria); return mock.createRun(req); }, 0);
    }
    return http<MrpRun>('/runs', { method: 'POST', body: JSON.stringify(req) });
  },

  async getRun(id: string): Promise<MrpRunDetail> {
    if (USE_MOCK) {
      await delay();
      const d = mock.getRunDetail(id);
      if (!d) throw new Error(`Run ${id} not found`);
      return structuredClone(d); // a copy, so in-memory mock edits are seen as changes by the query cache
    }
    return http<MrpRunDetail>(`/runs/${encodeURIComponent(id)}`);
  },

  async getPegging(runId: string, plannedOrderId: string): Promise<PeggingLink[]> {
    if (USE_MOCK) {
      await delay(150);
      const d = mock.getRunDetail(runId);
      return (d?.pegging ?? []).filter((p) => p.plannedOrderId === plannedOrderId);
    }
    return http<PeggingLink[]>(
      `/runs/${encodeURIComponent(runId)}/pegging/${encodeURIComponent(plannedOrderId)}`,
    );
  },

  async releaseOrders(runId: string, plannedOrderIds: string[]): Promise<ReleaseResult> {
    if (USE_MOCK) {
      await delay(700);
      return mock.releaseOrders(runId, plannedOrderIds);
    }
    return http<ReleaseResult>(`/runs/${encodeURIComponent(runId)}/release`, {
      method: 'POST',
      body: JSON.stringify({ plannedOrderIds }),
    });
  },

  async previewExecute(runId: string, id: string, req: ExecuteRequest): Promise<ExecutePreview> {
    if (USE_MOCK) return mocked(() => mex.previewExecute(runId, id, req), 300);
    return post<ExecutePreview>(`/runs/${encodeURIComponent(runId)}/exceptions/${encodeURIComponent(id)}/execute/preview`, req);
  },
  async executeException(runId: string, id: string, body: ExecutePayload): Promise<ExecuteResult> {
    if (USE_MOCK) return mocked(() => mex.executeException(runId, id, body), 700);
    return post<ExecuteResult>(`/runs/${encodeURIComponent(runId)}/exceptions/${encodeURIComponent(id)}/execute`, body);
  },

  async listForecasts(): Promise<Forecast[]> {
    if (USE_MOCK) {
      await delay();
      return [...mock.forecasts];
    }
    return http<Forecast[]>('/forecasts');
  },

  async upsertForecast(fc: Forecast): Promise<Forecast> {
    if (USE_MOCK) {
      await delay(250);
      const idx = mock.forecasts.findIndex((f) => f.id === fc.id);
      if (idx >= 0) mock.forecasts[idx] = fc;
      else mock.forecasts.push({ ...fc, id: fc.id || `FC-${Date.now()}` });
      return fc;
    }
    return http<Forecast>('/forecasts', { method: 'POST', body: JSON.stringify(fc) });
  },

  async listSapLog(q: SapLogQuery): Promise<SapLogEntry[]> {
    if (USE_MOCK) return mocked(() => msl.listSapLog(q), 300);
    return http<SapLogEntry[]>(`/sap-log?${qs({ success: q.success === undefined ? undefined : String(q.success), action: q.action, item: q.item, user: q.user, from: q.from, to: q.to, limit: q.limit })}`);
  },
};

export { API_BASE };
export const META = mock.META;
