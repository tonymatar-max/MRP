import type { SapLogEntry, SapLogQuery } from '../api/types';

const ago = (min: number) => new Date(Date.now() - min * 60_000).toISOString();

const ENTRIES: SapLogEntry[] = [
  { id: 'l1', at: ago(5), userName: 'admin', action: 'CloseDocument', success: false, itemCode: 'ITM-0031', warehouseCode: 'WH01', quantity: 40, target: 'OPOR:1187',
    summary: 'Close purchase order', error: 'SAP B1: (-10) Document cannot be closed: it has open goods receipts.' },
  { id: 'l2', at: ago(12), userName: 'admin', action: 'MoveDueDate', success: true, itemCode: 'ITM-0012', warehouseCode: 'WH01', quantity: 250, target: 'OPOR:1175',
    summary: 'Purchase order 1175 line 0 moved to 2026-10-28.' },
  { id: 'l3', at: ago(31), userName: 'admin', action: 'CreateTransferRequest', success: true, itemCode: 'ITM-0007', warehouseCode: 'WH02', quantity: 60, target: 'OWTQ:412',
    summary: 'Transfer request: ITM-0007 x 60, due 2026-10-20, warehouse WH01 -> WH02' },
  { id: 'l4', at: ago(31), userName: 'admin', action: 'CreatePurchaseOrder', success: false, itemCode: 'ITM-0044', warehouseCode: 'WH01', quantity: 500,
    summary: 'Purchase order: ITM-0044 x 500, due 2026-11-03, warehouse WH01', error: 'No vendor resolved for item ITM-0044.' },
  { id: 'l5', at: ago(31), userName: 'admin', action: 'CreateProductionOrder', success: true, itemCode: 'ITM-0100', warehouseCode: 'WH01', quantity: 80, target: 'OWOR:96',
    summary: 'Production order: ITM-0100 x 80, due 2026-10-30, warehouse WH01' },
  { id: 'l6', at: ago(1440), userName: 'admin', action: 'Session', success: false,
    summary: 'Service Layer write-back stopped while releasing 3 planned order(s)', error: 'Service Layer: Invalid session or session already timeout.' },
];

export function listSapLog(q: SapLogQuery): SapLogEntry[] {
  const item = q.item?.trim().toLowerCase(), user = q.user?.trim().toLowerCase();
  return ENTRIES
    .filter((e) => (q.success === undefined || e.success === q.success)
      && (!q.action || e.action === q.action)
      && (!item || (e.itemCode ?? '').toLowerCase().includes(item))
      && (!user || e.userName.toLowerCase().includes(user)))
    .slice(0, q.limit ?? 200);
}
