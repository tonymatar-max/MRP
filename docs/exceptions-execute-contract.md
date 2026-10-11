# Exceptions: Execute (API contract)

Same auth as before (cookie; 401 signed out). JSON camelCase, enums as names, dates `YYYY-MM-DD`.
**Execute is Admin-only** (it creates or changes documents in SAP B1 through the Service Layer). Planners see exceptions but not the Execute buttons (show a disabled button with the tooltip "Only an administrator can execute").

## 1. Exceptions gain these fields (in `GET /runs/{id}` -> `exceptions[]`)
| field | meaning |
|---|---|
| `id` | GUID that identifies the exception (use it in the execute endpoints) |
| `plannedOrderId` | the planned order this exception relates to, or null |
| `sourceDocKey` | existing SAP B1 document, e.g. `OPOR:4501` (purchase order DocEntry) or `OWOR:3301` (production order DocEntry), or null |
| `sourceLine` | purchase order line number for `OPOR`, else null |
| `currentDate` / `proposedDate` | for Expedite/Defer: the existing order's due date and the date the plan wants |
| `actions` | what **Execute** can do for this exception right now, see below. Empty = no Execute button |
| `actionStatus` | `null` (open) or `"Done"` |
| `actionBy`, `actionAt`, `actionNote` | who executed it, when (ISO datetime), and the result/note |

`actions` values (zero or more):
- `"ReleaseOrder"`: firm the related planned order and create it in SAP B1 (ReleaseInPast exceptions, while the planned order is not yet released).
- `"MoveDueDate"`: change the due date of the existing SAP B1 purchase/production order (Expedite: to `proposedDate`; Defer: any date the user picks, default `proposedDate`).
- `"Close"`: close the existing SAP B1 order (Defer: when the order is no longer needed).
An exception with `actionStatus = "Done"` has `actions = []`.
PastDue, CapacityOverload and NotPlanned never have actions (the UI explains: "No automatic action: review the item").

## 2. Preview (read-only; nothing is changed)
`POST /runs/{runId}/exceptions/{id}/execute/preview` body `{ "action": "MoveDueDate", "newDate": "2026-10-14" }` (`newDate` only for MoveDueDate)
```jsonc
{
  "action": "MoveDueDate",
  "canExecute": true,
  "title": "Move purchase order 4501, line 0",
  "summary": "Due date 2026-10-18 -> 2026-10-14",
  "current":  { "dueDate": "2026-10-18", "status": "Open" },     // read live from SAP B1 for existing documents
  "proposed": { "dueDate": "2026-10-14" },
  "target": { "kind": "PurchaseOrder", "docEntry": 4501, "line": 0 },   // or ProductionOrder / PlannedOrder
  "warnings": [ "The order is already received in part." ],
  "blockers": [ "Service Layer write-back is off. Enable it in Settings." ]   // when non-empty, canExecute is false
}
```
For `ReleaseOrder` the preview describes the planned order (`target.kind = "PlannedOrder"`, item, quantity, due date, vendor and its source) and warns when a purchase order has no vendor (that is also a blocker).
For `Close` the summary says exactly what will be closed. Errors: 404 unknown exception, 400 `{message}` for an invalid action.

## 3. Execute
`POST /runs/{runId}/exceptions/{id}/execute` body `{ "action": "MoveDueDate", "newDate": "2026-10-14", "note": "agreed with supplier", "confirm": true }`
- `confirm` must be `true`, otherwise 400.
- Success: `{ "ok": true, "message": "Purchase order 4501 line 0 moved to 2026-10-14.", "exception": <the updated exception> }`. The exception becomes `actionStatus: "Done"` with `actionBy/At/Note`.
- 409 `{message}` if it is already Done, or if the SAP B1 document changed since the plan (current date no longer matches `currentDate`): the UI tells the user to re-run MRP.
- 400/502 `{message}` when SAP B1 rejected it (show the message verbatim); the exception stays open.
- Everything is Admin only and is also recorded on the exception (who, when, note).

## 4. UI requirements (Exceptions screen)
- Each exception row with non-empty `actions` shows an **Execute** button (one button; if there are several actions, the dialog offers them as radio options). Rows already Done show a green "Done" pill with "by <user> on <date>" and the note on hover, and no button.
- Quick filter: **Open / Done / All** (default Open).
- **Execute dialog**: choose the action (if more than one); for MoveDueDate a date input (default `proposedDate`, never before today); a **note** box; on opening, call **preview** (and again when the action or date changes) and show title, summary, current-vs-proposed table, warnings (amber) and blockers (red, Execute disabled). A required checkbox: "I understand this changes SAP Business One" (not needed for nothing: it is always needed because all actions write to B1). **Execute** is disabled until the box is ticked and there are no blockers. Show the server `{message}` on success or failure; on 409 show the message and a "Re-run MRP" hint.
- After success refresh the run so the row turns Done.
- Works in mock mode (preview and execute simulated, e.g. executing flips the row to Done in memory).
- Where exceptions are listed elsewhere (Calendar day panel) just show the Done state if it is cheap; not required.
