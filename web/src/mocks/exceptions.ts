// Simulated Execute preview / execute for exceptions (mock mode). See docs/exceptions-execute-contract.md.
import type { ExceptionMessage, ExecuteRequest, ExecutePayload, ExecutePreview, ExecuteResult, PlannedOrder } from '../api/types';
import { getRunDetail } from './data';
import { MockHttpError, currentUsername } from './admin';

const RELEASE_NOUN: Record<PlannedOrder['type'], string> = { PurchaseOrder: 'purchase order', PurchaseRequest: 'purchase request', ProductionOrder: 'production order', TransferRequest: 'transfer request' };
const today = () => new Date().toISOString().slice(0, 10);

function find(runId: string, id: string) {
  const d = getRunDetail(runId);
  const ex = d?.exceptions.find((e) => e.id === id);
  if (!d || !ex) throw new MockHttpError(404, 'Exception not found.');
  const order = d.plannedOrders.find((p) => p.id === ex.plannedOrderId);
  return { ex, order };
}

function docLabel(ex: ExceptionMessage): string {
  const [kind, entry] = (ex.sourceDocKey ?? '').split(':');
  return kind === 'OWOR' ? `production order ${entry}` : `purchase order ${entry}${ex.sourceLine != null ? ` line ${ex.sourceLine}` : ''}`;
}

function validate(ex: ExceptionMessage, req: ExecuteRequest) {
  if (ex.actionStatus === 'Done') throw new MockHttpError(409, 'This exception was already executed. Re-run MRP to get a fresh plan.');
  if (!ex.actions?.includes(req.action)) throw new MockHttpError(400, `Action ${req.action} is not available for this exception.`);
  if (req.action === 'MoveDueDate') {
    if (!req.newDate || !/^\d{4}-\d{2}-\d{2}$/.test(req.newDate)) throw new MockHttpError(400, 'A new date is required.');
    if (req.newDate < today()) throw new MockHttpError(400, 'The new date cannot be before today.');
  }
}

export function previewExecute(runId: string, id: string, req: ExecuteRequest): ExecutePreview {
  const { ex, order } = find(runId, id);
  validate(ex, req);
  const warnings: string[] = [];
  const blockers: string[] = [];
  const isProd = (ex.sourceDocKey ?? '').startsWith('OWOR');
  const entry = Number((ex.sourceDocKey ?? '').split(':')[1]) || null;
  const n = Number(ex.id.replace(/\D/g, '')) || 0;

  if (req.action === 'ReleaseOrder') {
    const o = order as PlannedOrder;
    if (o.type === 'PurchaseOrder' && !o.vendorCode) {
      warnings.push('This purchase order has no vendor.');
      blockers.push('No vendor is set for this item. Set one in Master data or Settings, then try again.');
    }
    return {
      action: req.action, canExecute: blockers.length === 0,
      title: `Release planned ${RELEASE_NOUN[o.type] ?? 'order'} for ${o.itemCode}`,
      summary: `Firm the planned order and create it in SAP B1 (quantity ${o.quantity}, due ${o.dueDate}).`,
      current: { status: 'Planned', dueDate: o.dueDate },
      proposed: { status: 'Released in SAP B1', quantity: o.quantity, vendor: o.vendorCode ?? '(none)' },
      target: { kind: 'PlannedOrder' }, warnings, blockers,
    };
  }

  const kind = isProd ? 'ProductionOrder' : 'PurchaseOrder';
  const target = { kind: kind as ExecutePreview['target']['kind'], docEntry: entry, line: ex.sourceLine ?? null };
  if (n % 3 === 0) warnings.push(isProd ? 'Production has already started on this order.' : 'The order is already received in part.');
  if (req.action === 'MoveDueDate') {
    return {
      action: req.action, canExecute: true,
      title: `Move ${docLabel(ex)}`,
      summary: `Due date ${ex.currentDate} -> ${req.newDate}`,
      current: { dueDate: ex.currentDate ?? null, status: 'Open' },
      proposed: { dueDate: req.newDate ?? null },
      target, warnings, blockers,
    };
  }
  return {
    action: req.action, canExecute: true,
    title: `Close ${docLabel(ex)}`,
    summary: `Close ${docLabel(ex)} (due ${ex.currentDate}). Open quantities will no longer count as supply.`,
    current: { dueDate: ex.currentDate ?? null, status: 'Open' },
    proposed: { status: 'Closed' },
    target, warnings, blockers,
  };
}

export function executeException(runId: string, id: string, body: ExecutePayload): ExecuteResult {
  const { ex, order } = find(runId, id);
  if (body.confirm !== true) throw new MockHttpError(400, 'Confirmation is required.');
  validate(ex, body);
  let message: string;
  if (body.action === 'ReleaseOrder') {
    const o = order as PlannedOrder;
    if (o.type === 'PurchaseOrder' && !o.vendorCode) throw new MockHttpError(400, 'SAP B1 rejected the order: vendor is required.');
    o.firmed = true;
    o.b1DocEntry = o.b1DocEntry ?? 14000 + Number(ex.id.replace(/\D/g, ''));
    message = `Planned order released as SAP B1 document ${o.b1DocEntry}.`;
  } else if (body.action === 'MoveDueDate') {
    message = `${docLabel(ex).replace(/^./, (c) => c.toUpperCase())} moved to ${body.newDate}.`;
  } else {
    message = `${docLabel(ex).replace(/^./, (c) => c.toUpperCase())} closed.`;
  }
  ex.actionStatus = 'Done';
  ex.actions = [];
  ex.actionBy = currentUsername();
  ex.actionAt = new Date().toISOString();
  ex.actionNote = body.note || message;
  return { ok: true, message, exception: { ...ex } };
}
