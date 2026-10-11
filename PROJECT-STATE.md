# Cobalt MRP: project state (read this first)

Last updated: 2026-10-11. Purpose: a new session reads this file instead of the whole conversation history.

## What it is
SAP Business One MRP / CRP / forecast add-on. .NET 8 minimal API + EF Core (SQL Server) + Dapper (read-only B1 SQL) + React/Vite/TS (TanStack Query) served from `wwwroot`. Runs as Windows service `CobaltMRP` on :5050, installed at `C:\CobaltMRP` (data in `C:\CobaltMRP\data`, never overwrite). All config (SQL strings, Service Layer, planning defaults, calendar, resources) lives in the in-app Settings screen. Cookie auth, roles Admin / Planner (Planner cannot execute, release or change settings). Owner: Tony (tony.matar@seidor.com).

Repo: `C:\Claude\Cobalt MRP` (not a git repo). Skill `cobalt-service-standard` holds the shared Windows-service pattern; skill `cobalt-design-system` the UI look. Memory notes: `cobalt-mrp.md`, `sap-b1-schema-facts.md`.

## Layout
- `src/CobaltMrp.Domain`: enums, run results, `RunCriteria` (wizard criteria + `Validate()`).
- `src/CobaltMrp.Engine`: `MrpEngine.cs` (+ `.Transfers.cs`), `CompanyLevel.cs`, `CriteriaApplier.cs`, `PlanningOverlay`, `CrpCalculator`, `CalendarEventBuilder`.
- `src/CobaltMrp.Data`: `B1SqlDataProvider` (read-only), `MockB1DataProvider` (2 warehouses WH01/WH02), `ServiceLayer/ServiceLayerClient.cs` + `PlannedOrderWriteback.cs`, `CobaltMrpDbContext`.
- `src/CobaltMrp.Api`: `Ai/` (OpenRouter assistant, see below), `Program.cs`, `Api/*Endpoints.cs`, `Services/RunPlanner.cs` (single pipeline for run AND preview), `PreviewMetrics`, `ExceptionExecution.cs`, `ReleaseService`, `CobaltSchema.cs` (idempotent upgrades v2 to v6), `Settings/`.
- `web/`: React app; build output goes to `src/CobaltMrp.Api/wwwroot`.
- `deploy/publish.ps1, install-service.ps1, uninstall-service.ps1`; `sql/cobalt-mrp-schema.sql`.
- `docs/`: contracts (`planning-level`, `settings-api`, `calendar-and-planning`, `wizard-and-masterdata`, `exceptions-execute`, `preview-details`), `prd.md`, `roadmap.md`, `backlog.md`, `market-research.md`.

## Features built
- MRP engine: low-level coding, time-phased netting per item x warehouse, lot sizing (min/max/multiple/fixed, max-qty splits), safety stock, reorder point and min/max, lead time (supplier > item > item group, configurable order), working calendar (B1 holidays), pegging, exceptions (Expedite, Defer, Cancel, PastDue, ReleaseInPast, CapacityOverload, NotPlanned), CRP, forecast with forward consumption.
- Run MRP wizard: scope, horizon, demand/supply, lead-time rule, lot sizing, planning level, review with click-through numbers (counts and drill-down from the same selectors).
- **Planning level (newest):** Company level = pool all selected warehouses per item, PR or PO only, never transfers, orders received in item default warehouse else the warehouse with most stock+demand. Warehouse level = plan each warehouse separately, optional transfer requests (TR) from other selected warehouses when stock can be spared (never creating a shortage at the source), remaining shortage becomes PR/PO or production. Purchasing document choice: Order or Request. Contract: `docs/planning-level-contract.md`. Algorithm: `MrpEngine.Transfers.cs` (dry pass, allocate earliest shortage first / largest spare first, real pass).
- Planned orders (4 types: PurchaseOrder, PurchaseRequest, TransferRequest `from -> to`, ProductionOrder), firm, release to SAP B1 via Service Layer, pegging.
- Exceptions with Execute: release planned order, move PO date, close PO / cancel production order; preview + stale check + audit.
- Master data screen (edits stay Cobalt-side, NEVER written to SAP): item overrides, supplier lead times, default supplier.
- Calendar, Capacity, Forecasts, Settings, Dashboard.
- UI restyled to the Cobalt design system (2026-10-10).
- **AI assistant (2026-10-11, optional, OFF by default, read-only):** Settings > AI (Admin: enable, OpenRouter key masked/protected like the SL password, model + Load models, max tokens, Test connection). `src/CobaltMrp.Api/Ai/`: `OpenRouterClient` (plain HttpClient, friendly 401/402/429/5xx/timeout/malformed mapping), `RunToolbox` (6 read-only tools over a saved run: get_run_summary, list_planned_orders, get_pegging, list_exceptions, get_item, get_calendar_events; capped, rounded; NO write tools), `AiLoop` (max 6 tool rounds, then one final call without tools; timeouts; cancellation), `AiAssistant`, `AiRateLimiter` (20/min/user). Endpoints `GET /api/ai/status`, `POST /api/ai/ask`, `POST /api/ai/summary` (Admin + Planner; 400 off/no key, 404, 429, 502), `POST /api/settings/test/ai`, `POST /api/settings/ai/models`. UI: "Ask AI" drawer (Dashboard, Planned Orders, Exceptions), "Summarise this run" card on the Dashboard, hidden when off (admins get an "Enable AI in Settings" hint). Usage logged (user, run, model, tokens, tool names; never key/question/answer). Base URL defaults to https://openrouter.ai/api/v1; overridable ONLY by config `Ai:BaseUrl` / `Ai:TimeoutSeconds` (tests). Contract: `docs/ai-contract.md`.

## Test status (last run)
- `dotnet test`: 124 engine + 9 data + 46 API/AI tests (`tests/CobaltMrp.Api.Tests`: tool caps/filters/no-write, loop bound, error mapping, rate limiter, settings secrets) pass. `npx tsc --noEmit` clean, `npm run build` ok.
- Scratchpad scripts (`C:\Users\tonym\AppData\Local\Temp\claude\C--Claude\eb767d4c-a00a-46c4-b4b9-8995e093a4cc\scratchpad`): `e2e.sh` (151 checks; needs `fake_sl.py` on :5099 logging to `sl.log`), `e2e_level.sh` (23), `qa1_api.sh` (83), `b1check.sh` (31, read-only vs SBODemoHO), `fake_sl.py` (fake Service Layer incl. PR/TR). QA UI was done with playwright-core + headless Edge (built-in browser blocks ports 5060/5061: avoid them).
  AI tests (same scratchpad): `fake_openrouter.py` (stand-in OpenRouter on :5090: /models, tool_call then answer, 401/402/429/500/timeout/malformed JSON), `e2e_ai.sh` (81 checks, API on :5081, no SQL needed), `ui_ai.sh` + `pw_ai/ui.mjs` (46 UI checks in headless Edge at 1440 and 375, API on :5082; screenshots in `shots_ai/`).
- Real company check (SBODemoHO, read-only SQL): planned orders and transfers hand-verified.

## NOT verified yet (do not claim otherwise)
- **A real OpenRouter call has never been made** (no key available). Everything AI was tested against a fake OpenRouter that follows the OpenAI chat-completions + tool-calling shape. Needs the user's key: confirm Test connection, Load models, one Ask and one Summarise, that the chosen model really supports tool calling, and the Windows Event Log lines (source "Cobalt MRP", category CobaltMrp.Api.Ai; verified on the console logger only, not the Event Log).
- Nothing has been posted to a real SAP B1. PR (`PurchaseRequests`, header field `RequriedDate` is SAP's spelling) and TR (`InventoryTransferRequests`) payloads are checked only against the fake Service Layer. Production orders are now always created as `boposPlanned` (SAP error "Production orders can be created in planned status only"); not yet confirmed live.
- Execute Close / Move on multi-line orders in the browser.
- New restyled UI: agent verified in headless Edge; user has not reviewed the look.

## Known open items / ideas
- Forecast dialog item/warehouse lists come from fixed mock data even on a B1 source.
- Calendar legend chips show a placeholder "n"; user menu is a plain Sign out button; RTL/Arabic not done; no dark mode.
- Transfer + lot sizing can buy slightly more in total than one larger lot (plan stays feasible; left as is).
- AI: "Ask the plan" chat and run summary are DONE (see Features). Still ideas: exception triage, master-data clean-up, forecast baseline, supplier email drafts, streaming answers, per-item "Explain" buttons on rows. Rules kept: AI only suggests, a person clicks Execute; key in Settings (masked) with an off switch; send only needed data; log calls.
- Other ideas: supplier lead-time import, notifications for new exceptions.

## Rules to keep (hard constraints)
- Never `taskkill /IM` or otherwise touch the user's installed `CobaltMRP` service or `C:\CobaltMRP`; stop test instances only by listening port. I am not elevated: cannot install/restart services; give the user the commands.
- Never print secrets from settings. Do not guess Service Layer field names silently; flag unverified payloads.
- Master-data edits stay in Cobalt; never write to SAP B1 except through explicit release/execute with write-back on.
- Test DBs are named `CobaltMrp_*` and must be dropped after tests (only `CobaltWms` is the user's, never touch). SBODemoHO is read-only.
- Posting to a real B1 needs the user's Service Layer details; do not hunt for credentials.
- The Cloud Portal (`C:\Claude\Cloud Portal`) and the invoices/payments request were for other projects; not part of this repo.

## Publish (user runs, elevated PowerShell, from THIS folder)
```powershell
cd "C:\Claude\Cobalt MRP"; Stop-Service CobaltMRP; .\deploy\publish.ps1; Start-Service CobaltMRP
```
(Once they ran it from the Cloud Portal folder by mistake; that republished nothing here.)

## Next steps
1. User publishes, then does a real B1 test on a test company: release one TR, one PR, one production order; send any error text.
2. Finish loose ends above; refresh README and the `cobalt-service-standard` skill templates (copy updated e2e / fake_sl).
3. AI: user adds a real OpenRouter key in Settings > AI and does the live checks listed under NOT verified. Publish includes the new `tests` project only in the repo; no deploy change needed (the base URL override is config-only).
