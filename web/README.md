# Cobalt MRP — Web UI

React 18 + Vite + TypeScript front end for Cobalt MRP (SAP Business One MRP add-on).
Uses React Router, TanStack Query and Recharts; styling is plain CSS with variables (`src/styles/theme.css`).

## Run

```bash
npm install
npm run dev      # http://localhost:5173
npm run build    # type-check + production build into ../src/CobaltMrp.Api/wwwroot
```

## Screens

Dashboard, Planned Orders (sort/filter, pegging expand, multi-select release to SAP B1, LLC colour coding),
Exceptions (grouped by type, jump to order), Capacity (CRP chart + table), Calendar, Forecasts (add/edit, consumed vs remaining), Settings.

**Calendar** (`/calendar`): month / week / agenda views of the selected run (demand, forecast, supply, planned release/due, exceptions, capacity overloads) with per-kind count chips, non-working-day tint, today and overload markers, a day panel with links (`/planned-orders?order=<id>` expands that order), kind/item/warehouse filters and per-range fetching via `GET /runs/{id}/calendar`. **Planned Orders** shows the vendor (source badge B1 / override / default, red "No vendor" for purchase orders that cannot release). Contract: `docs/calendar-and-planning-contract.md`.

**Run wizard** (Dashboard > Run MRP): 5 steps (Scope, Demand, Supply and lead times, Planning rules, Review) prefilled from `GET /runs/defaults`, with a lead-time priority reorder control, a dry-run preview (`POST /runs/preview`) and notes; "Quick run" keeps the old simple dialog. The Dashboard shows each run's **Criteria** with "Re-run with these criteria".
**Planning level** (top of the wizard's Scope step, `PlanningSection.tsx`): Company / Warehouse radio cards, Purchasing document (Purchase order / Purchase request), warehouses relabelled by level, and at Warehouse level a transfer toggle with "Transfer from" chips (default every selected warehouse) and "Transfer time (days)". Transfers need 2+ warehouses (when none are selected the warehouse lookup is the universe); `validateStep` enforces it and that sources are a subset. The Review step shows the preview `planning` block and its warnings; the Dashboard Criteria lists level, document and transfers. Planned Orders has Purchase / Purchase request / Production / Transfer types (transfer rows show `from -> to`; "No vendor" only for purchase orders). Settings > Planning defaults has `defaultLevel`, `purchaseDocument`, `transferLeadTimeDays`. Mock mode collapses warehouses at company level and generates up to 4 transfer rows (reducing purchase quantities) at warehouse level. Contract: `docs/planning-level-contract.md`.
**Review drill-down**: every non-zero number on the wizard's Review step (except the sales quantity) is a button that opens `PreviewDetailsPanel` (right-hand panel): summary chips, debounced server search, client-sortable table driven by the response `columns`, "Showing N of M", Download CSV, Escape/Close returns focus to the number. Uses the wizard's current criteria via `POST /runs/preview/details` (+ `/export`). Contract: `docs/preview-details-contract.md`; mock generator (row counts equal the mock preview): `src/mocks/previewDetails.ts`.
**Master data** (`/master-data`, Admin and Planner): Items (server-paged grid, inline edit, edit panel with SAP B1 vs override per field, bulk edit, CSV export) and Suppliers (inline lead time). Overrides live in Cobalt MRP only. Contract: `docs/wizard-and-masterdata-contract.md`. Mock data and rules: `src/mocks/masterdata.ts`.

**Lead time editing**: the Items grid lead-time cell opens a popover with the item override and the supplier lead time (a supplier value applies to every item from that supplier, so it needs a second confirmation; `leadTime.supplierItemCount` gives the count). The item edit panel has a separate "Supplier lead time" section, and bulk edit can set a supplier lead time for each distinct supplier of the selection. Supplier writes use `PUT /masterdata/suppliers/{cardCode}` preserving existing notes; items and suppliers queries are refreshed afterwards.

**Exceptions > Execute** (Admin only; Planners see a disabled button): rows with `actions` get an Execute button that opens `ExecuteDialog` (action choice, new due date for MoveDueDate, note, live preview via `POST /runs/{id}/exceptions/{exId}/execute/preview`, amber warnings, red blockers, a required "I understand this changes SAP Business One" tick, then `.../execute`). Done rows show a green pill with who/when and the note on hover; an Open / Done / All filter (default Open) sits above the list. A 409 shows the server message plus a Re-run MRP hint. Contract: `docs/exceptions-execute-contract.md`; mock simulation in `src/mocks/exceptions.ts` (executing flips the row to Done in memory; a second try gives 409; releasing a purchase order without a vendor is a blocker).

## Mock mode (default)

`VITE_USE_MOCK` defaults to `true`: the app serves realistic sample data from `src/mocks/data.ts`
with simulated latency, so everything (including Run MRP, release and forecast edits) works offline.
State lives in memory and resets on reload.

## Pointing at the real API

Copy `.env.example` to `.env` and set:

```
VITE_USE_MOCK=false
VITE_API_BASE=/api            # or a full URL, e.g. https://host/api
(dev proxy for /api targets http://localhost:5050; edit vite.config.ts)
```

Endpoints used: `GET/POST /runs`, `GET /runs/{id}`, pegging, `POST /runs/{id}/release`, `GET/POST /forecasts`,
plus auth (`/auth/*`), `/users`, `/settings` (+ `/settings/test/*`, `/settings/init-schema`) and `/status`
as defined in `docs/settings-api-contract.md`. Types: `src/api/types.ts`; client: `src/api/client.ts`.

## Sign-in and roles

On load the app calls `GET /auth/status`: first run shows **Create the first administrator** (only works from a browser
on the server itself), otherwise **Sign in**. Any 401 returns the user to the sign-in page. **Admin** sees Settings, Users
and *Release to SAP B1*; **Planner** can run MRP, view and edit forecasts. In mock mode you start signed in as `admin`
(sign out, then sign in as `planner` with any password to see the Planner view).

## Settings (Admin)

Tabs: Cobalt database, SAP B1 database, Service Layer, Planning defaults (forecast consumption, working calendar, extra non-working dates, per-resource capacity overrides loaded from `GET /resources`), Users. One **Save settings** for the whole page
(`PUT /settings`). Each connection tab has **Test connection**, which sends the current unsaved values; the Cobalt tab also
has **Create / update tables**. Passwords are write-only: the UI shows "unchanged (stored)" and sends an empty string when
untouched. Nothing secret is kept in the browser. A banner warns when the server reports in-memory storage.

## Production build

`npm run build` uses `.env.production` (`VITE_USE_MOCK=false`, `VITE_API_BASE=/api`) and writes to
`../src/CobaltMrp.Api/wwwroot` (emptied first). The .NET service serves it from `/` and must fall back to `index.html` for
unknown non-`/api` paths (client-side routing).
