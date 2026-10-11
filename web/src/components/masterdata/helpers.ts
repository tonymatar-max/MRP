import type { MasterItemRow, MasterOverride, OverridableField } from '../../api/types';

export const FIELD_LABEL: Record<OverridableField, string> = {
  planningMethod: 'Planning method', lotSizing: 'Lot sizing', leadTimeDays: 'Lead time (days)', minOrderQty: 'Min order qty',
  orderMultiple: 'Order multiple', safetyStock: 'Safety stock', reorderPoint: 'Reorder point', maxStockLevel: 'Max stock level',
  fixedLotSize: 'Fixed lot size', periodOfSupplyDays: 'Period of supply (days)', supplier: 'Supplier',
};
export const NUMERIC_FIELDS: OverridableField[] = ['leadTimeDays', 'minOrderQty', 'orderMultiple', 'safetyStock', 'reorderPoint', 'maxStockLevel', 'fixedLotSize', 'periodOfSupplyDays'];
export const ALL_FIELDS: OverridableField[] = ['supplier', 'leadTimeDays', 'minOrderQty', 'orderMultiple', 'safetyStock', 'reorderPoint', 'maxStockLevel', 'planningMethod', 'lotSizing', 'fixedLotSize', 'periodOfSupplyDays'];

export const METHOD_LABEL: Record<string, string> = { Mrp: 'MRP', ReorderPoint: 'Reorder point', MinMax: 'Min / max', None: 'None' };
export const LOT_LABEL: Record<string, string> = { LotForLot: 'Lot for lot', FixedQuantity: 'Fixed quantity', MinMaxMultiple: 'Min / max / multiple', PeriodOfSupply: 'Period of supply' };
export const LT_SOURCE_LABEL: Record<string, string> = { Supplier: 'Supplier', Item: 'Item', ItemGroup: 'Group', Default: 'Default', None: 'None' };

/** The full override object to PUT after changing some fields (null removes an override). */
export function overrideBody(row: MasterItemRow, patch: Partial<Record<OverridableField, number | string | null>>, notes?: string): Partial<MasterOverride> {
  const cur: Record<string, unknown> = {};
  for (const f of row.overriddenFields) cur[f] = (row.override as unknown as Record<string, unknown>)[f];
  for (const [k, v] of Object.entries(patch)) { if (v === null || v === '') delete cur[k]; else cur[k] = v; }
  return { ...(cur as Partial<MasterOverride>), notes: notes ?? row.override?.notes ?? '' };
}

/** Parses a days field: empty means clear (null). */
export function parseDays(text: string): { value: number | null; error?: string } {
  if (text.trim() === '') return { value: null };
  const n = Number(text);
  if (!Number.isFinite(n) || n < 0) return { value: null, error: 'lead time must be a number of 0 or more.' };
  return { value: n };
}

/** Tooltip text for a row's lead time: all three levels, which one is used, and that supplier can be edited. */
export function leadTimeTooltip(r: MasterItemRow): string {
  const used = r.leadTime.source;
  const mark = (k: string, v: number | null) => `${k}: ${fmtVal(v)}${used === k ? ' (used)' : ''}`;
  return `Effective ${r.leadTime.days ?? 'none'} d, from ${LT_SOURCE_LABEL[used]} (the value marked used). `
    + `${mark('Supplier', r.leadTime.supplierDays)}, ${mark('Item', r.leadTime.itemDays)}, ${mark('ItemGroup', r.leadTime.groupDays)}. `
    + 'Click to edit the item or supplier lead time here.';
}

export const fmtVal = (v: unknown) => (v === null || v === undefined || v === '' ? '—' : typeof v === 'number' ? v.toLocaleString(undefined, { maximumFractionDigits: 3 }) : String(v));
