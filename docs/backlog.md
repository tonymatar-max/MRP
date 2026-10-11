# Cobalt MRP — Prioritised Backlog (MoSCoW)

**Date:** 2026-10-09 · **Companion docs:** `prd.md`, `roadmap.md`
Grouped by area: **Engine · Data/Integration · API · UI · Ops/Deploy**.
Priority: **M** Must (v1/MVP), **S** Should (near-term, Phase 2–3), **C** Could (later), **W** Won't (now).
IDs are stable handles for developers to pull from.

---

## Engine

| ID | Pri | Epic / Story |
|----|-----|--------------|
| ENG-1 | **M** | **Low-level coding** — compute low-level code for every item; process in LLC order so each item nets exactly once after all parents. |
| ENG-2 | **M** | **BOM explosion** — explode multi-level BOMs; handle shared components and **phantom** pass-through (no planned order for phantoms). |
| ENG-3 | **M** | **Demand aggregation** — merge sales orders, forecasts (net of consumption), safety stock, and dependent demand into time-phased gross requirements per item/warehouse. |
| ENG-4 | **M** | **Time-phased netting** — per-warehouse projected-on-hand buckets; consume scheduled receipts before planned; raise net requirement below safety stock. |
| ENG-5 | **M** | **Lot sizing** — lot-for-lot, fixed, min/max/multiple, period-of-supply; constraints combinable. |
| ENG-6 | **M** | **Lead-time offset** — purchase vs. production lead times; release-in-past exception when release date < run date. |
| ENG-7 | **M** | **Pegging** — bidirectional peg links (planned order ↔ demand ↔ parent planned order); full chain drill. |
| ENG-8 | **M** | **Exception engine** — expedite / defer / cancel / past-due / release-in-past, each with order, recommended action, and pegged demand. |
| ENG-9 | **M** | **Reorder-point & min/max** planning in the same regenerative pass as MRP items. |
| ENG-10 | **M** | **Determinism & golden-master** — identical outputs for identical inputs; regression suite over mock dataset. |
| ENG-11 | **S** | **CRP load calc** — routings → work-center load per bucket vs. capacity; overload flags + contributing orders. |
| ENG-12 | **S** | **Forecast consumption** — forward/backward windows; forecast/consumed/remaining per bucket. |
| ENG-13 | **S** | **Parallel netting by LLC band** to hit perf targets on large datasets. |
| ENG-14 | **C** | **Scenario engine** — cloned inputs + parameter overrides, isolated from baseline. |
| ENG-15 | **C** | **APS-lite re-sequencing** — relieve flagged overloads within lead-time windows. |
| ENG-16 | **W** | Full finite-capacity scheduling / sequencing optimisation. |
| ENG-17 | **W** | Statistical / ML demand forecasting models. |

## Data / Integration

| ID | Pri | Epic / Story |
|----|-----|--------------|
| DATA-1 | **M** | **`IB1DataProvider` contract** — items, item-warehouse planning data, BOMs, routings, on-hand, open POs/prod orders, sales orders, forecasts. |
| DATA-2 | **M** | **Mock provider** — seedable sample dataset (realistic multi-level BOMs, multi-warehouse, mixed planning methods) for dev + golden-master. |
| DATA-3 | **S** | **Real B1 SQL provider** — read-only SQL implementing the contract against a B1 company DB. |
| DATA-4 | **S** | **Multi-company connections** — manage/secure multiple B1 DB connections; per-company run isolation. |
| DATA-5 | **S** | **Service Layer (OData) write-back** — create purchase & production orders; idempotency + per-doc success/failure. |
| DATA-6 | **S** | **Provider parity tests** — mock vs. live return the same shapes; contract tests. |
| DATA-7 | **S** | **Data-quality diagnostics** — detect missing lead times, broken/cyclic BOMs, routings with no capacity. |
| DATA-8 | **C** | **Forecast import** — CSV + provider ingestion with validation. |
| DATA-9 | **C** | **Re-run reconciliation** — just-created orders picked up as scheduled receipts next run. |

## API

| ID | Pri | Epic / Story |
|----|-----|--------------|
| API-1 | **M** | **Run lifecycle** — POST run (horizon, bucket size, scope); status/progress; cancel; GET run results. |
| API-2 | **M** | **Results queries** — planned orders, exceptions (filter/sort), item time-phased grid, peg drill endpoints. |
| API-3 | **M** | **Immutable run store** — persist inputs snapshot, params, outputs, exceptions, peg links, per-phase timings. |
| API-4 | **S** | **Write-back endpoint** — convert selected recommendations to B1 documents; return per-doc results. |
| API-5 | **S** | **CRP + forecast result endpoints** — work-center load, forecast/consumed/remaining. |
| API-6 | **S** | **AuthN/Z** — role-gated endpoints (planner / buyer / production / admin). |
| API-7 | **C** | **Scenario endpoints** — create/compare/promote scenarios. |
| API-8 | **C** | **CSV export** endpoints for every grid. |

## UI (React + Vite, Cobalt-branded)

| ID | Pri | Epic / Story |
|----|-----|--------------|
| UI-1 | **M** | **Cobalt app shell** — branding, nav, auth, run picker. |
| UI-2 | **M** | **Run launcher** — set horizon/buckets/scope; live progress + duration. |
| UI-3 | **M** | **Planned orders grid** — filter/sort/CSV; purchase vs. production. |
| UI-4 | **M** | **Exceptions workbench** — ranked, filterable (item/wh/severity/type), recommended action visible. |
| UI-5 | **M** | **Pegging drill** — traverse peg chain up/down from any order or demand. |
| UI-6 | **M** | **Item time-phased grid** — POH buckets, gross/net req, scheduled/planned receipts. |
| UI-7 | **S** | **Capacity view** — work-center load heat per bucket; drill to contributing orders. |
| UI-8 | **S** | **Forecast view** — forecast vs. consumed vs. remaining; create/import UI. |
| UI-9 | **S** | **Write-back action** — multi-select recommendations → create B1 orders with result toast/report. |
| UI-10 | **C** | **Scenario compare** — side-by-side KPIs across runs/scenarios. |
| UI-11 | **C** | **Saved filters & bulk exception actions.** |
| UI-12 | **C** | **Run-to-run exception diff.** |

## Ops / Deploy

| ID | Pri | Epic / Story |
|----|-----|--------------|
| OPS-1 | **M** | **`CobaltMRP` Windows service** host — install/start/stop; survives restart. |
| OPS-2 | **M** | **Structured logging + per-phase timings** (aggregation/explosion/netting/CRP); health endpoint. |
| OPS-3 | **M** | **Perf harness** — automated 1k/10k/50k-item run timing vs. targets, in CI. |
| OPS-4 | **S** | **Secret management** — DPAPI/secret store for SQL + Service Layer credentials. |
| OPS-5 | **S** | **Cancellable long runs** + no partial write-back on failure. |
| OPS-6 | **S** | **Backup/retention** policy for the immutable run store. |
| OPS-7 | **C** | **Deploy/upgrade runbook** + schema migration strategy (EF Core). |

---

## Top-10 pull order for the first sprints
ENG-1 → ENG-2 → ENG-3 → ENG-4 → DATA-1 → DATA-2 → ENG-5 → ENG-6 → ENG-7 → ENG-8
(low-level coding and explosion unblock aggregation and netting; the provider seam + mock data run in parallel; lot sizing, lead-time offset, pegging, and exceptions complete the engine core — then UI-1..UI-6 make it usable.)
