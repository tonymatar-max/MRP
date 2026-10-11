# Cobalt MRP

Sophisticated MRP for SAP Business One: regenerative MRP, reorder/min-max planning, CRP, forecast management.
.NET 8 + SQL Server + React. Reads B1 SQL (read-only), writes purchase/production orders back via the Service Layer.
Runs as the Windows service **CobaltMRP**. All configuration is done in the app's **Settings** screen.

## Install as a Windows service
From the repo root, in PowerShell:
```
.\deploy\publish.ps1                 # builds the UI + publishes to C:\CobaltMRP
.\deploy\install-service.ps1         # run from an ELEVATED PowerShell: creates and starts the service
```
Then open **http://localhost:5050** on the server:
1. Create the first administrator (only possible from a browser on the server itself).
2. Open **Settings** and configure, then **Test connection** on each:
   - **Cobalt database**: SQL Server for runs and forecasts. Click **Create / update tables** (creates the database too, if the login may).
   - **SAP B1 database**: the company database, read-only.
   - **Service Layer**: base URL, company DB, user, vendors. Write-back is **off** until you enable it.
   - **Planning defaults** and **Users**.
Changes apply immediately, no restart. Until the Cobalt database is configured, runs are kept in memory and the UI says so.

Upgrade: `.\deploy\publish.ps1` again, then `Restart-Service CobaltMRP`. Settings, users and keys live in `C:\CobaltMRP\data` and are never overwritten.
Remove: `.\deploy\uninstall-service.ps1` (leaves files and data).

**Windows authentication note:** with SQL Server Windows authentication, the database sees the *service account* (LocalSystem appears as `DOMAIN\MACHINE$`). Either use SQL authentication in Settings, or install with `-Account DOMAIN\svc_mrp -Password ...` and grant that account access.

## Develop
```
dotnet test
dotnet run --project src/CobaltMrp.Api          # http://localhost:5050
cd web && npm install && npm run dev             # http://localhost:5173 (proxies /api to :5050)
```
`npm run dev` uses in-browser mock data by default; set `VITE_USE_MOCK=false` in `web/.env.local` to use the API.

## Layout
- `src/CobaltMrp.Domain` planning inputs/results, `IB1DataProvider`
- `src/CobaltMrp.Engine` MRP engine (low-level coding, netting, lot sizing, pegging, exceptions, CRP)
- `src/CobaltMrp.Data` mock + B1 SQL providers, Service Layer client, EF persistence
- `src/CobaltMrp.Api` Web API, settings, auth, hosts the built UI from `wwwroot`
- `web/` React UI. `deploy/` service scripts. `sql/` schema script. `docs/` market research, PRD, roadmap, API contract
- `tests/` engine, Service Layer client and AI assistant tests

## AI assistant (optional, read-only)
Settings > **AI** (Admin) turns on an assistant that explains a saved run. It uses OpenRouter (any model with tool calling; default `anthropic/claude-sonnet-4.5`) with **your own API key**: the key is encrypted at rest like the Service Layer password, never returned by the API, and a blank field keeps the stored key. **Test connection** sends one tiny request; **Load models** fills the model list from OpenRouter.
- **Ask AI** (Dashboard, Planned Orders, Exceptions): a chat drawer for the selected run with starter questions, "Based on N lookups" (what data it used), Copy, and the note *AI can be wrong; check the order before acting.* **Summarise this run** (Dashboard) gives a short plain-language summary of what needs attention first.
- It answers only from six **read-only** lookups (run summary, planned orders, pegging, exceptions, item, calendar events). It has **no write tools at all**: it cannot create, release, execute, move or edit anything; it suggests actions in words and you click Execute yourself.
- **Privacy:** plan data returned by those lookups (item codes, quantities, dates, supplier codes) is sent to OpenRouter and the model provider you choose. No connection strings, settings, secrets or user passwords are sent. Each call is logged (user, run, model, token usage, tool names) without the key or the question text; 20 calls a minute per user.
- Off by default; when off (or without a key) the buttons are hidden (administrators see an "Enable AI in Settings" hint). Details: `docs/ai-contract.md`.

## Security model
- Sign-in is cookie based; roles **Admin** (settings, users, release to B1) and **Planner**.
- Passwords and raw connection strings are encrypted at rest (ASP.NET DataProtection, keys in `data\keys`, DPAPI machine scope) and are never returned by the API; saving with a blank password keeps the stored one.
- The service binds to `localhost`. Expose it to planners through IIS or a reverse proxy with TLS.

## SAP B1 data (b1-sql provider)
Read-only. Verified against B1 10.0 company `SBODemoHO` (115 items, 1,408 stock rows, 1,698 open sales-order lines; loaded totals match independent SQL).
Planning policy comes from **standard fields, no UDFs**: `PlaningSys`, `PrcrmntMtd`, `LeadTime`, `MinLevel`/`MaxLevel`, `ReorderPnt`/`ReorderQty`, `MinOrdrQty`, `OrdrMulti`, `DfltWH`, `CardCode` (preferred vendor).
- **Vendors:** purchase orders use the item's preferred vendor from B1. Precedence is Settings override, then B1 preferred vendor, then the Settings default vendor. The Planned Orders screen shows each order's vendor and where it came from, and flags purchase orders with none ("No vendor": they cannot be released until one is set).
- **Working calendar:** the company calendar (`OADM.HldCode` -> `OHLD` weekend days, `HLD1` holidays) is used when it exists; otherwise the working days from Settings. Extra closures from Settings always apply. Recommended release dates never fall on a non-working day, and capacity per bucket counts working days only.
- **Resources:** `ITT1.Type = 290` lines on production BOMs are the routing. B1 stores no capacity, efficiency or setup time, so Settings has a per-resource table (hours per day, efficiency %, setup hours per order); resources not listed use units x the default hours per unit per day.
- **Open production orders:** components still to be issued (`WOR1` planned minus issued, open orders only) count as demand on those components, as in B1's own MRP.
- **Forecast consumption:** real sales orders inside a forecast period replace that part of the forecast, so demand is not counted twice (Settings: Forward / None). Forecast periods that started before the plan start are ignored (lookback setting).
- Items are planned only where they have stock, demand or supply (B1 creates a stock row in every warehouse). Zero-stock items fall back to the default warehouse, a single stock location, or a `NotPlanned` exception.
- Past-due demand is reported once per item/warehouse. Demand for inactive or non-inventory items is reported as `NotPlanned`, never dropped silently.
- B1's "ordering interval" (`OOIN`) is only a category list with no day count, so it is not used for period-of-supply. Supplier lead time does not exist in B1; it lives in Cobalt (see above).
- `sql/mock-b1-schema.sql` is obsolete; test against a real B1 database.

## Run MRP wizard
**Run MRP** on the Dashboard opens a five-step wizard (a "Quick run" link keeps the simple dialog):
1. **Scope**: data source, warehouses, item groups, suppliers, item search, explicit item codes, Buy/Make, planning methods, "also plan components of selected make items".
2. **Demand**: sales orders (optional ship-date window), forecasts (consumption, lookback), components of open production orders, past-due handling (plan as due now / ignore), safety stock.
3. **Supply and lead times**: on-hand, open purchase orders, open production orders, and the **lead time rule**.
4. **Planning rules**: start date, horizon, bucket, minimum order / multiples, working calendar, capacity planning.
5. **Review**: a dry run with counts (items in scope, demand and supply lines, where lead times come from) and warnings (purchased items with no supplier or lead time, overdue demand), then run. **Every number is clickable**: it opens a panel with a short summary (lines, quantity, items, earliest/latest due, overdue) and the records behind it (item, document, warehouse, quantity, due date...), with search and **Download CSV** for the full list. The count and the list come from the same selector on the server, so they always agree (checked automatically for all 17 numbers, on the mock data and against the real demo company).

Every run stores the criteria it used. The Dashboard shows them with **Re-run with these criteria**. Scope decides which items' results are kept; demand still flows through the whole bill of materials, so a purchased item is planned correctly even when only its supplier is selected.

## Lead times: supplier, item or group first
SAP B1 holds a lead time on the **item** (`OITM.LeadTime`) and on the **item group** (`OITB.LeadTime`) but none on the **supplier**, so the supplier lead time is entered in Cobalt (Master data > Suppliers). The rule is chosen in the wizard (defaults in Settings): put Supplier, Item and Item group in priority order; **First available** takes the first that has a value, **Longest** takes the largest. Purchased items use all three; make items skip the supplier. If none has a value, the default (0 days unless you set one) applies. Each planned order records the lead time used and its source (Supplier / Item / Item group / Default). A supplier lead time can be edited from any item row (click its lead time: "this item" and "supplier" fields, with a confirmation that it changes every item from that supplier), from the item edit panel, in bulk for the suppliers of the selected items, or on the Suppliers tab.

## Master data (what a planner can change)
**Master data** shows SAP B1 items (and suppliers) with what the planner may override for planning: default supplier, lead time, minimum order, order multiple, safety stock, reorder point, maximum level, planning method, lot sizing, fixed lot size and period of supply. Every field shows the SAP B1 value next to the override, with per-field revert and **Reset to SAP B1**; there are server-paged filters (search, group, supplier, Buy/Make, overridden, no supplier, no lead time), inline editing, bulk edit and CSV export.
**Overrides are stored in Cobalt MRP and used by planning. They never change SAP Business One.** The default supplier set here is the one purchase orders use (it beats B1's preferred vendor).
Supplier precedence for a purchase order: an entry in Settings > Service Layer (legacy) > the item's default supplier in Master data > SAP B1's preferred vendor > the Settings default vendor.

## Exceptions: Execute
Each exception that has a well-defined action shows an **Execute** button (Admin only; Planners see it disabled). It always opens a dialog first:
- **Preview** reads the live SAP B1 document and shows current against proposed, with warnings (for example "already received in part") and blockers (write-back off, order not open, no supplier).
- **Acknowledgement** ("I understand this changes SAP Business One") and an optional note are required.
- **Actions:**
  - **Release the planned order** (release-in-past exceptions): firms it and creates the purchase or production order in SAP B1, exactly like Release on Planned Orders.
  - **Move the due date** (Expedite, Defer): changes that purchase order **line** (or the production order's due date). The PATCH names only that line and asks the Service Layer not to replace the other lines. The change is read back from SAP B1 and only then reported as done.
  - **Close the order** (Defer): closes a single-line purchase order, or cancels a *planned* production order. A multi-line purchase order and a released production order are blocked with an explanation: close those in SAP B1.
- **Safety:** if the document changed in SAP B1 after the plan was made (its date no longer matches what the plan saw), Execute refuses with a message to re-run MRP. Executing twice is refused. Past due, capacity overload and not planned exceptions have no automatic action.
- **Audit:** the exception keeps who executed it, when, the note and the result (visible to everyone, with an Open / Done / All filter). Cobalt MRP never closes or moves a document it did not just preview.
- One order line never gets both an Expedite and a Defer: if MRP already covered the early shortage with a new order, only the Expedite is shown.
- The Service Layer calls used are `GET`, `PATCH` (with `B1S-ReplaceCollectionsOnPatch: false`) and `POST .../Close` on `PurchaseOrders` / `ProductionOrders`. They follow the documented API and were verified against a stand-in server in automated tests; **confirm the first real execution of each action type on your own SAP B1**.

## Calendar
The Calendar screen shows everything dated in a run: sales orders due, forecasts, open purchase/production orders, recommended releases and due dates, planner action messages, capacity overloads, and non-working days. Month, week and agenda views; filter by kind, item and warehouse; click a day for its list with links to the orders. Events are stored with each run (table `CalendarEvent`), so the calendar always matches the plan it came from. Overdue items sit on the first day of the horizon.

## Release to B1
Release firms the selected orders and saves them, then (if enabled) creates the documents through the Service Layer one by one. Failures are reported per order and do not block the others; orders already pushed are skipped on retry, so re-releasing never creates duplicates. Purchase-order vendors come from Settings (a default plus per-item overrides).

## Upgrading
Publish over the top and restart the service. The database is brought forward automatically at startup (new tables and columns are added in place, for example the master-data override tables and the exception audit columns; nothing is dropped). Settings, users and keys in `data\` are kept.

## Status / known gaps
- Persistence, settings, auth, vendors and the calendar were verified on SQL Server 2022, with the published exe, and read-only against the real B1 demo company `SBODemoHO`.
- Write-back was verified against a **fake** Service Layer in automated tests. The production-order payload fields (`ItemNo`, `PlannedQuantity`, ...) are from the documented API shape; confirm on your live system.
- Not available from B1, so not modelled except through Settings: setup time, real capacity and efficiency per resource.
- Cobalt-managed forecasts apply to mock runs; `b1-sql` runs use B1's own forecasts.
- This follows the standard captured in the `cobalt-service-standard` skill.
