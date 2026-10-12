# SAP push log contract

A permanent record of every write Cobalt MRP makes to SAP Business One through the Service Layer, whether it worked or not. Reads (previews, `GET`s) are not logged: only changes.

Code: `Domain/SapPushLog.cs` (entity, actions), `Api/Services/SapPushLogStore.cs` (stores + recorder), hooks in `ReleaseService` and `ExceptionExecutor`, endpoint `GET /api/sap-log`, UI page `web/src/pages/SapLog.tsx` (nav: Data > SAP push log). Tests: `tests/CobaltMrp.Api.Tests/SapPushLogTests.cs`.

## What is logged
| Action | When |
|---|---|
| `CreatePurchaseOrder`, `CreatePurchaseRequest`, `CreateTransferRequest`, `CreateProductionOrder` | Release on Planned Orders, or Execute > Release on a release-in-past exception. One entry per order, success or failure. |
| `MoveDueDate` | Execute > move on an Expedite/Defer exception (purchase order line or production order). |
| `UpdateItem` | Master data > Items > **Push to SAP B1**: one entry per item whose item-master fields were written (summary lists each field, old -> new; target `OITM:<item code>`). |
| `CloseDocument` | Execute > close a purchase order / cancel a production order. |
| `Session` | The Service Layer session itself failed (login, network, expired session) so no document could be attempted, or the write-back stopped part-way. |

Not logged, because nothing was sent to SAP B1: Release with write-back off (orders are only firmed), orders skipped as "not firmed" or "already pushed", and Execute attempts refused by the preview/safety checks (changed since the plan, multi-line close, past date).

## Entry
`id, at (UTC), userName, action, success, runId, plannedOrderId, exceptionId, itemCode, warehouseCode, quantity, target, summary, error`

- `target` is the SAP document: `OPOR:<DocEntry>` purchase order, `OPRQ` purchase request, `OWTQ` transfer request, `OWOR` production order. For a create it is the new DocEntry; for a move/close it is the document changed.
- `summary` is a short plain sentence of what was sent. `error` is SAP B1's complete message (trimmed at 4,000 characters).
- Never stored: passwords, session ids, request bodies.

## Guarantees
- Entries are only added: no edit or delete endpoint exists.
- Logging never breaks a push. If the database write fails the push result is still returned and the failure is written to the Event Log. The Event Log also gets one line per push ("SAP B1 push OK" / "SAP B1 push FAILED"), under the logger of the service that pushed (`ReleaseService` or `ExceptionExecutor`).
- A release is logged immediately after SAP B1 answers, before Cobalt saves `B1DocEntry`, so a document created in SAP B1 is never missing from the log.
- Stored in the Cobalt MRP database table `SapPushLog` (created by the idempotent schema upgrade v7; **Init schema** in Settings, or the next start, adds it to an existing install). While no Cobalt database is configured it is held in memory and lost on restart.

## API
`GET /api/sap-log` (any signed-in user, read-only), newest first.
Query: `success` (true/false), `action`, `item` (contains), `user` (contains), `from`, `to` (ISO date-times), `limit` (default 200, max 1000). Returns an array of entries.

## UI
Data > **SAP push log**: filter by result (All / Succeeded / Failed), action, item, user; table with when, user, action, result, item, warehouse, quantity, SAP document, details (summary + SAP's error in red); **Export CSV**. Refreshes after every release/execute.
