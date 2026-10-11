import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import type {
  AppSettings, BulkItemRequest, CalendarQuery, DataSource, Forecast, MasterItemQuery, MasterOverride, MasterSupplierQuery, NewUser,
  ExceptionMessage, RunCriteria, SapLogQuery, SupplierImportRow, PreviewDetailsRequest, TriggerRunRequest, ExecuteRequest, ExecutePayload,
} from './types';

export function useRuns() {
  return useQuery({ queryKey: ['runs'], queryFn: api.listRuns });
}

export function useRun(id: string | undefined) {
  return useQuery({
    queryKey: ['run', id],
    queryFn: () => api.getRun(id!),
    enabled: !!id,
  });
}

export function useTriggerRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: TriggerRunRequest) => api.triggerRun(req),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['runs'] }),
  });
}

export function useReleaseOrders(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) => api.releaseOrders(runId, ids),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['run', runId] });
      qc.invalidateQueries({ queryKey: ['runs'] });
      qc.invalidateQueries({ queryKey: ['sap-log'] });
    },
  });
}

/** Read-only preview; refetches when the action or date changes. Never cached (live SAP B1 data). */
export function useExecutePreview(runId: string, id: string, req: ExecuteRequest, enabled = true) {
  return useQuery({
    enabled,
    queryKey: ['exec-preview', runId, id, req.action, req.newDate ?? ''],
    queryFn: () => api.previewExecute(runId, id, req),
    gcTime: 0, staleTime: 0, retry: false,
    placeholderData: (prev) => prev,
  });
}

export function useExecuteException(runId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: string; body: ExecutePayload }) => api.executeException(runId, v.id, v.body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['run', runId] });
      qc.invalidateQueries({ queryKey: ['calendar'] });
    },
  });
}

export function useForecasts() {
  return useQuery({ queryKey: ['forecasts'], queryFn: api.listForecasts });
}

export function useUpsertForecast() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (fc: Forecast) => api.upsertForecast(fc),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['forecasts'] }),
  });
}

// ---------- status / settings / users ----------
export function useStatus(enabled = true) {
  return useQuery({ queryKey: ['status'], queryFn: api.getStatus, enabled, staleTime: 30_000 });
}

export function useSettings(enabled = true) {
  return useQuery({ queryKey: ['settings'], queryFn: api.getSettings, enabled, staleTime: 0, gcTime: 0 });
}

export function useSaveSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: AppSettings) => api.saveSettings(s),
    onSuccess: (data) => {
      qc.setQueryData(['settings'], data);
      qc.invalidateQueries({ queryKey: ['status'] });
      qc.invalidateQueries({ queryKey: ['ai-status'] });
    },
  });
}

export function useUsers() {
  return useQuery({ queryKey: ['users'], queryFn: api.listUsers });
}

export function useCreateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (u: NewUser) => api.createUser(u),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  });
}

export function useDeleteUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (username: string) => api.deleteUser(username),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  });
}

export function useSetUserPassword() {
  return useMutation({ mutationFn: (v: { username: string; password: string }) => api.setUserPassword(v.username, v.password) });
}

export function useCalendar(runId: string | undefined, q: CalendarQuery) {
  return useQuery({
    queryKey: ['calendar', runId, q.from, q.to, (q.kinds ?? []).join(','), q.item ?? '', q.warehouse ?? ''],
    queryFn: () => api.getCalendar(runId!, q),
    enabled: !!runId,
    placeholderData: (prev) => prev,
    staleTime: 60_000,
  });
}

// ---------- run wizard / lookups ----------
export function useRunDefaults(source: DataSource, enabled = true) {
  return useQuery({ queryKey: ['run-defaults', source], queryFn: () => api.runDefaults(source), enabled, staleTime: 0, gcTime: 0 });
}
export function usePreviewRun() {
  return useMutation({ mutationFn: (c: RunCriteria) => api.previewRun(c) });
}
export function useItemGroups(source: DataSource) {
  return useQuery({ queryKey: ['lookup-groups', source], queryFn: () => api.lookupItemGroups(source), staleTime: 60_000 });
}
export function useSupplierLookup(source: DataSource, search: string, enabled = true) {
  return useQuery({
    queryKey: ['lookup-suppliers', source, search], queryFn: () => api.lookupSuppliers(source, search), enabled,
    placeholderData: (prev) => prev, staleTime: 30_000,
  });
}

// ---------- master data ----------
export function useMasterItems(q: MasterItemQuery) {
  return useQuery({ queryKey: ['md-items', q], queryFn: () => api.listMasterItems(q), placeholderData: (prev) => prev });
}
export function useMasterSuppliers(q: MasterSupplierQuery) {
  return useQuery({ queryKey: ['md-suppliers', q], queryFn: () => api.listMasterSuppliers(q), placeholderData: (prev) => prev });
}
function useInvalidateMaster() {
  const qc = useQueryClient();
  return () => {
    qc.invalidateQueries({ queryKey: ['md-items'] });
    qc.invalidateQueries({ queryKey: ['md-suppliers'] });
  };
}
export function usePutMasterItem() {
  const inv = useInvalidateMaster();
  return useMutation({ mutationFn: (v: { code: string; override: Partial<MasterOverride> }) => api.putMasterItem(v.code, v.override), onSuccess: inv });
}
export function useBulkMasterItems() {
  const inv = useInvalidateMaster();
  return useMutation({ mutationFn: (r: BulkItemRequest) => api.bulkMasterItems(r), onSuccess: inv });
}
export function useResetMasterItem() {
  const inv = useInvalidateMaster();
  return useMutation({ mutationFn: (code: string) => api.resetMasterItem(code), onSuccess: inv });
}
/**
 * Sets (or clears, with null) a supplier's lead time, preserving the supplier's existing notes
 * (looked up first). Refreshes items and suppliers so every row from that supplier updates.
 */
export function useSetSupplierLeadTime(source: DataSource) {
  const inv = useInvalidateMaster();
  return useMutation({
    mutationFn: async (v: { cardCode: string; leadTimeDays: number | null }) => {
      const page = await api.listMasterSuppliers({ source, search: v.cardCode, pageSize: 20 });
      const notes = page.suppliers.find((s) => s.cardCode === v.cardCode)?.notes ?? '';
      return api.putMasterSupplier(v.cardCode, { leadTimeDays: v.leadTimeDays, notes });
    },
    onSuccess: inv,
  });
}
export function usePutMasterSupplier() {
  const inv = useInvalidateMaster();
  return useMutation({
    mutationFn: (v: { cardCode: string; leadTimeDays: number | null; notes: string }) =>
      api.putMasterSupplier(v.cardCode, { leadTimeDays: v.leadTimeDays, notes: v.notes }),
    onSuccess: inv,
  });
}

/** Review step drill-down. Re-queries when the metric, search or criteria change; keeps the old rows while loading. */
export function usePreviewDetails(req: PreviewDetailsRequest | null) {
  return useQuery({
    queryKey: ['preview-details', req?.metric, req?.search ?? '', req?.take ?? 200, JSON.stringify(req?.criteria ?? null)],
    queryFn: () => api.previewDetails(req!),
    enabled: !!req,
    placeholderData: (prev) => prev,
    staleTime: 0, gcTime: 0, retry: false,
  });
}

export function usePreviewDetailsExport() {
  return useMutation({ mutationFn: (req: PreviewDetailsRequest) => api.previewDetailsExport(req) });
}

// ---------- AI assistant ----------
export function useAiStatus() {
  return useQuery({ queryKey: ['ai-status'], queryFn: api.getAiStatus, staleTime: 30_000, retry: false });
}

/** The log of writes to SAP B1. Always fresh: it changes whenever someone releases or executes. */
export function useSapLog(q: SapLogQuery) {
  return useQuery({ queryKey: ['sap-log', q], queryFn: () => api.listSapLog(q), staleTime: 0 });
}

/** Warehouses of the chosen data source (mock sample or SAP B1). */
export function useWarehouses(source: DataSource) {
  return useQuery({ queryKey: ['warehouses', source], queryFn: () => api.getWarehouses(source), staleTime: 60_000 });
}

/** What identifies "the same exception" from one run to the next (ids are new every run). */
export const exceptionKey = (e: ExceptionMessage) => [e.type, e.itemCode, e.warehouseCode ?? '', e.sourceDocKey ?? '', e.sourceLine ?? ''].join('|');

/**
 * Exceptions in this run that the run before it did not have: what a planner needs to look at first.
 * `hasPrevious` is false when there is no earlier run to compare with (then nothing is called new).
 */
export function useNewExceptions(runId: string | undefined) {
  const runs = useRuns().data;
  const idx = runs && runId ? runs.findIndex((r) => r.id === runId) : -1;
  const prevId = idx >= 0 ? runs?.[idx + 1]?.id : undefined;
  const cur = useRun(runId).data;
  const prev = useRun(prevId).data;
  return useMemo(() => {
    if (!cur || !prev) return { hasPrevious: false, count: 0, isNew: (_: ExceptionMessage) => false };
    const seen = new Set(prev.exceptions.map(exceptionKey));
    const isNew = (e: ExceptionMessage) => !seen.has(exceptionKey(e));
    return { hasPrevious: true, count: cur.exceptions.filter((e) => e.actionStatus !== 'Done' && isNew(e)).length, isNew };
  }, [cur, prev]);
}

export function useImportSuppliers() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { source: DataSource; rows: SupplierImportRow[]; dryRun: boolean }) => api.importMasterSuppliers(v.source, v.rows, v.dryRun),
    onSuccess: (r) => { if (!r.dryRun) { qc.invalidateQueries({ queryKey: ['md-suppliers'] }); qc.invalidateQueries({ queryKey: ['md-items'] }); } },
  });
}
