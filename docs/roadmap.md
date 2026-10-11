# Cobalt MRP — Roadmap

**Date:** 2026-10-09 · **Companion docs:** `prd.md`, `backlog.md`
Effort is rough, in engineer-weeks (ew), assuming a small team (1–2 backend, 1 frontend). "S/M/L/XL" ≈ <1 / 1–2 / 3–5 / 6+ ew.

---

## Phasing at a glance

| Phase | Theme | Goal / exit criteria |
|-------|-------|----------------------|
| **MVP** | Engine + mock data + run-results UI | Full regenerative run on seedable mock data; planned orders, netting, pegging, exceptions, lot sizing, reorder/min-max; workbench to explore results. No live B1. |
| **Phase 2** | Live B1 read + write-back | Real B1 SQL provider behind `IB1DataProvider`; multi-company connections; convert recommendations to PO/production orders via Service Layer (idempotent). |
| **Phase 3** | CRP + forecasting polish | Routings → work-center load per bucket, overload flags; forecast create/import + forward/backward consumption surfaced in the run. |
| **Phase 4** | What-if & APS-lite | Multi-scenario runs, side-by-side compare, parameter simulation, simple capacity-aware re-sequencing. |

---

## Milestone table

### MVP — Engine + mock data + run-results UI
| # | Milestone | Effort |
|---|-----------|--------|
| M0 | Solution skeleton: .NET 8 Web API, EF Core + SQL Server run store, `CobaltMRP` service host, React+Vite Cobalt shell | M |
| M1 | `IB1DataProvider` contract + seedable **mock provider** (items, BOMs, routings, on-hand, open docs, demand) | M |
| M2 | Demand aggregation + **low-level coding** + BOM explosion (incl. phantoms) | M |
| M3 | Time-phased **netting** with per-warehouse POH buckets; scheduled-receipt consumption; safety stock | L |
| M4 | **Lot sizing** (LFL, fixed, min/max/multiple, period-of-supply) + **lead-time offset** | M |
| M5 | **Pegging** (peg links both directions) + **exception** generation (expedite/defer/cancel/past-due/release-in-past) | M |
| M6 | **Reorder-point & min/max** planning in the same pass | S |
| M7 | Run-results **workbench**: planned orders, exceptions, pegging drill, POH grid; filters/sort/CSV | L |
| M8 | **Golden-master** regression suite + perf harness (10k-item < 60s target) | M |
| **MVP total** | | **~14–18 ew** |

### Phase 2 — Live B1 read + Service Layer write-back
| # | Milestone | Effort |
|---|-----------|--------|
| P2.1 | **Real B1 SQL provider** implementing `IB1DataProvider` (item/warehouse planning data, BOMs, routings, on-hand, open POs/prod orders, sales orders) | L |
| P2.2 | Multi-company connection management; per-company run isolation; secret storage | M |
| P2.3 | Provider parity tests (mock vs. live shape) + data-quality diagnostics (missing lead times, broken BOMs) | M |
| P2.4 | **Service Layer (OData) write-back**: create purchase & production orders from recommendations; idempotency + per-doc result reporting | L |
| P2.5 | Reconciliation: re-run picks up just-created orders as scheduled receipts | S |
| **Phase 2 total** | | **~9–12 ew** |

### Phase 3 — CRP + forecasting polish
| # | Milestone | Effort |
|---|-----------|--------|
| P3.1 | **CRP**: routings → work-center load per time bucket vs. available capacity; overload flags + contributing orders | L |
| P3.2 | Capacity view in workbench (load heat per center/bucket, drill to orders) | M |
| P3.3 | **Forecast management**: create/import forecasts (CSV + provider) | M |
| P3.4 | **Forward/backward consumption** against actual sales orders; forecast/consumed/remaining per bucket in run output | M |
| P3.5 | Planner ergonomics: saved filters, exception bulk-action, run-to-run diff of exceptions | M |
| **Phase 3 total** | | **~9–12 ew** |

### Phase 4 — What-if simulation & APS-lite
| # | Milestone | Effort |
|---|-----------|--------|
| P4.1 | **Scenario runs**: clone inputs, override parameters (lead time, lot rules, demand), run without touching baseline | M |
| P4.2 | **Multi-scenario compare**: side-by-side KPIs (coverage, exceptions, capacity, projected inventory) | M |
| P4.3 | Parameter **what-if** sweeps (e.g. safety-stock / lot-size sensitivity) | M |
| P4.4 | **APS-lite**: simple finite re-sequencing of production orders to relieve flagged overloads within lead-time windows | L |
| P4.5 | Scenario → promote to baseline → write-back | S |
| **Phase 4 total** | | **~8–11 ew** |

---

## Sequencing notes & risks

- **Provider seam first (M1) is the linchpin:** building the engine entirely against the mock provider de-risks Phase 2 — the live B1 provider becomes an implementation detail, not a rewrite.
- **Golden-master before live data (M8):** lock engine correctness on known mock inputs before real-world data variance arrives, so Phase 2 bugs are provenance (data) not logic (engine).
- **Write-back idempotency (P2.4)** is the highest-risk external integration — treat Service Layer failures, partial batches, and retries as first-class.
- **CRP depends on clean routings (P3.1):** gate on data-quality diagnostics from P2.3; many B1 shops have incomplete routings.
- **APS-lite (P4.4)** is deliberately "lite" — finite scheduling is a product in itself; keep scope to overload relief within existing lead-time windows.
