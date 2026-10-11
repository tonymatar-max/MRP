import type { ExceptionType, PlannedOrderType } from '../api/types';

export function fmtDate(iso: string | undefined | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' });
}

export function fmtDateTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

export function fmtNum(n: number): string {
  return n.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

const TODAY_ISO = '2026-10-09';
export function isPast(iso: string): boolean {
  return iso < TODAY_ISO;
}

// Low-level code colour coding
const LLC_COLORS = ['var(--accent)', 'var(--ok)', 'var(--warn)', 'var(--violet)', 'var(--danger)', 'var(--muted)'];
export function llcColor(level: number): string {
  return LLC_COLORS[level % LLC_COLORS.length];
}

const TYPE_META: Record<PlannedOrderType, { label: string; tag: string }> = {
  PurchaseOrder: { label: 'Purchase', tag: 'po' },
  PurchaseRequest: { label: 'Purchase request', tag: 'pr' },
  ProductionOrder: { label: 'Production', tag: 'prod' },
  TransferRequest: { label: 'Transfer', tag: 'tr' },
};
export const ORDER_TYPES = Object.keys(TYPE_META) as PlannedOrderType[];
export function orderTypeLabel(t: PlannedOrderType): string {
  return TYPE_META[t]?.label ?? t;
}
export function orderTypeTag(t: PlannedOrderType): string {
  return TYPE_META[t]?.tag ?? 'po';
}

export const EXCEPTION_META: Record<ExceptionType, { label: string; tone: 'danger' | 'warn' | 'info' }> = {
  Expedite: { label: 'Expedite', tone: 'warn' },
  Defer: { label: 'Defer', tone: 'info' },
  Cancel: { label: 'Cancel', tone: 'danger' },
  PastDue: { label: 'Past Due', tone: 'danger' },
  ReleaseInPast: { label: 'Release In Past', tone: 'danger' },
  CapacityOverload: { label: 'Capacity Overload', tone: 'warn' },
  NotPlanned: { label: 'Not Planned', tone: 'info' },
};

export type SortDir = 'asc' | 'desc';
export function sortBy<T>(rows: T[], key: keyof T, dir: SortDir): T[] {
  const copy = [...rows];
  copy.sort((a, b) => {
    const av = a[key];
    const bv = b[key];
    let cmp = 0;
    if (typeof av === 'number' && typeof bv === 'number') cmp = av - bv;
    else cmp = String(av).localeCompare(String(bv));
    return dir === 'asc' ? cmp : -cmp;
  });
  return copy;
}
