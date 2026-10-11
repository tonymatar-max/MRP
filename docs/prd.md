# Cobalt MRP — Product Requirements Document

**Status:** Draft v1 · **Owner:** Product (Cobalt) · **Date:** 2026-10-09
**Stack:** .NET 8 Web API + EF Core + SQL Server · React + Vite (Cobalt-branded) · Windows service `CobaltMRP` · SAP Business One (SQL read + Service Layer/OData write-back)

> A parallel market-research deliverable (`docs/market-research.md`) is expected. Where it exists, its differentiators feed positioning and prioritisation; this PRD is written to stand alone until it lands.

---

## 1. Vision

SAP Business One ships with "MRP Wizard" — a serviceable but shallow planning tool: single-pass, weak pegging, no true capacity view, clumsy forecast consumption, and a UX that planners actively avoid. Cobalt MRP replaces it with a **sophisticated, fully regenerative MRP engine** wrapped in a planner-grade workbench: fast, explainable, multi-warehouse and multi-company aware, with real pegging and exception management, and a Capacity Requirements Planning (CRP) layer that B1 lacks entirely.

**Vision statement:** *Give every SAP B1 shop a planning engine they trust enough to act on — where every planned order traces to the demand that caused it, every exception tells the planner exactly what to do, and a 10,000-item plan runs while they refill their coffee.*

**Why now / why us:** Cobalt already serves the B1 ecosystem with branded, SQL-native add-ons. MRP is the highest-pain, highest-stickiness planning gap in B1. Owning the plan means owning the purchase and production order flow downstream.

---

## 2. Target users

| User | Context | What they live in today |
|------|---------|-------------------------|
| **Planner / MRP controller** | Runs the plan, works the exception list, converts recommendations to orders | Excel exports of B1 MRP, manual re-keying |
| **Production manager** | Owns the shop floor schedule; cares about feasibility and capacity | Routings, work-center load, whiteboards |
| **Buyer / purchaser** | Turns purchase recommendations into POs; manages supplier lead times | B1 purchase recommendation report |
| **Plant / operations manager** | Wants confidence the plan is feasible and demand is covered | Status meetings, gut feel |

---

## 3. Personas & jobs-to-be-done

### Priya — Planner (primary persona)
- **JTBD:** "When demand changes, help me see *what to do next* without rebuilding a spreadsheet."
- Pains: B1 MRP doesn't peg; she can't tell which sales order drove a purchase recommendation. Re-running is slow and opaque.
- Success: works a ranked exception list, trusts the recommendations, releases orders in minutes.

### Marco — Production Manager
- **JTBD:** "Tell me before I commit whether the plan will overload a work center."
- Pains: B1 has no CRP. He discovers overloads on the floor.
- Success: sees work-center load per time bucket, flags overloads before release.

### Sana — Buyer
- **JTBD:** "Give me clean, lead-time-correct purchase recommendations I can action."
- Pains: recommendations ignore min-order/multiple rules; timing is off.
- Success: lot-sized, lead-time-offset recommendations grouped by supplier.

### Dan — Plant Manager
- **JTBD:** "Reassure me demand is covered and the plan is feasible."
- Pains: no single trustworthy view; auditability is nil.
- Success: a run summary — demand covered, exceptions by severity, capacity status.

---

## 4. In-scope v1 features (with acceptance criteria)

> v1 = the MVP engine + run-results workbench on the mock data provider, structured so Phase 2 swaps in the live B1 provider with no engine change. CRP and forecast-consumption polish are scoped but land across phases per the roadmap; v1 delivers the engine core, netting, pegging, exceptions, lot sizing, and reorder/min-max.

### 4.1 Demand aggregation
- **As a planner, I can** have the engine aggregate independent demand from sales orders, forecasts (net of consumption), and safety-stock requirements into time-phased gross requirements per item/warehouse.
  - **AC:** Given a sales order line due on date D for qty Q in warehouse W, the run produces a gross requirement of Q in W's bucket containing D.
  - **AC:** Safety stock is treated as a standing requirement that projected-on-hand must not fall below.
  - **AC:** Dependent demand from parent planned orders appears as gross requirement on components (see BOM explosion).

### 4.2 BOM explosion with low-level coding
- **As a planner, I can** trust that each item is planned exactly once, after all its parents, regardless of BOM depth or shared components.
  - **AC:** Low-level code is computed for every item; the engine processes items in low-level-code order.
  - **AC:** A component used at multiple BOM levels is netted once, at its lowest level, with demand from all parents aggregated.
  - **AC:** Phantom BOMs pass demand straight through to their children without creating a planned order for the phantom.

### 4.3 Time-phased netting
- **As a planner, I can** see projected-on-hand (POH) buckets per item per warehouse across the planning horizon.
  - **AC:** POH(t) = POH(t-1) + scheduled receipts(t) + planned receipts(t) − gross requirements(t).
  - **AC:** A net requirement is raised when POH would fall below safety stock in a bucket.
  - **AC:** Scheduled receipts (open POs / production orders already in B1) are consumed before new planned orders are created.
  - **AC:** Buckets are configurable (daily / weekly) per run.

### 4.4 Lot sizing
- **As a planner, I can** choose a lot-sizing policy per item and have planned-order quantities respect it.
  - **AC:** Supported policies: lot-for-lot, fixed quantity, min/max/multiple, period-of-supply.
  - **AC:** Min-order-qty, order-multiple, and max-order-qty constraints are enforced and combinable.
  - **AC:** Period-of-supply groups net requirements across N buckets into a single planned order.

### 4.5 Lead-time offset
- **As a planner, I can** have planned orders released early enough to arrive when needed.
  - **AC:** Purchased items offset by purchase lead time; manufactured items by production lead time (fixed + variable where defined).
  - **AC:** An order whose computed release date is before the run date generates a **release-in-past** exception rather than silently clamping.

### 4.6 Pegging
- **As a planner, I can** trace every planned order back to the specific demand(s) that caused it.
  - **AC:** Each planned order stores one or more peg links to source demand (sales order line, forecast bucket, safety stock, or parent planned order).
  - **AC:** From a planned order I can drill up the full peg chain to the originating independent demand.
  - **AC:** From a demand I can drill down to the planned supply allocated to it.

### 4.7 Exception messages
- **As a planner, I can** work a prioritised list of actionable exceptions instead of reading a plan line by line.
  - **AC:** Engine emits: **expedite**, **defer**, **cancel**, **past-due**, **release-in-past**.
  - **AC:** Each exception names the order, the recommended new date/qty/action, and the pegged demand.
  - **AC:** Exceptions are filterable by item, warehouse, severity, and type, and sortable by due date.

### 4.8 Reorder point & min/max planning
- **As a planner, I can** plan reorder-point and min/max items in the **same regenerative pass** as MRP items.
  - **AC:** Item planning method (MRP / reorder-point / min-max) is read per item.
  - **AC:** Reorder-point items trigger a replenishment to the order-up-to level when POH crosses the reorder point.
  - **AC:** Results appear in the same run output and exception stream as MRP items.

### 4.9 Capacity Requirements Planning (CRP)
- **As a production manager, I can** see how planned production orders load each work center per time bucket and where overloads occur.
  - **AC:** Routings map each planned production order to work-center operations with setup + run time.
  - **AC:** Load is summed per work center per bucket against available capacity; buckets exceeding 100% are flagged.
  - **AC:** Overloaded centers are listed with the planned orders contributing to the load.

### 4.10 Forecast management & consumption
- **As a planner, I can** create or import forecasts and have actual sales orders consume them so I don't double-count demand.
  - **AC:** Forecasts can be created in-app and imported (CSV / provider).
  - **AC:** Forward and backward consumption windows are configurable; a sales order consumes forecast within the window before adding net new demand.
  - **AC:** The run shows, per bucket, forecast / consumed / remaining.

### 4.11 Run results workbench (UI)
- **As a planner, I can** launch a run, watch progress, and explore results in a Cobalt-branded workbench.
  - **AC:** Launch run with horizon, bucket size, warehouse/company scope; see status and duration.
  - **AC:** Views: Planned orders, Exceptions, Pegging drill, Item time-phased grid (POH buckets), Capacity load, Forecast vs. actual.
  - **AC:** Every grid is filterable, sortable, and exportable (CSV).

### 4.12 Write-back (scoped v1, delivered Phase 2)
- **As a buyer/planner, I can** convert selected recommendations into B1 purchase or production orders via the Service Layer.
  - **AC:** Multi-select recommendations → create documents through B1 Service Layer (OData), with per-document success/failure reporting and idempotency (no duplicate creation on retry).

---

## 5. Out of scope (v1)

- Multi-site DRP / inter-warehouse transfer planning (consumed as fixed demand only in v1).
- Finite-capacity scheduling / full APS and sequencing (APS-lite explored Phase 4).
- Supplier collaboration portal, EDI, demand sensing / statistical forecasting models.
- Cost/price optimisation, sourcing split across multiple suppliers, quota arrangements.
- Mobile apps; attribute/characteristic (configurable-product) planning.
- Direct B1 UI (SAP add-on form) embedding — Cobalt MRP is a standalone web workbench in v1.

---

## 6. Non-functional requirements

| Area | Requirement |
|------|-------------|
| **Run performance** | 1,000 items < 5 s; **10,000 items < 60 s**; 50,000 items < 8 min, on reference hardware (8 vCPU / 32 GB, SQL Server local). Regenerative run is single-threaded-correct but parallelisable by low-level-code band. |
| **Scalability** | Horizon up to 24 months; up to ~500k demand lines per run without OOM (streamed netting). |
| **Auditability** | Every run is persisted immutably: inputs snapshot, parameters, outputs, exceptions, peg links, timings. Runs are re-openable and comparable; nothing is overwritten. |
| **Multi-warehouse** | Netting and POH are per item **per warehouse**; planning data (lead time, lot rules, method) resolvable at item or item-warehouse level. |
| **Multi-company** | A run targets one B1 company DB; the service supports multiple company connections and keeps runs/data isolated per company. |
| **Explainability** | Any planned order, exception, or POH figure can be explained via peg chain + the netting math that produced it. |
| **Data provider abstraction** | All B1 reads go through `IB1DataProvider`; a seedable mock provider and the real B1 SQL provider are interchangeable with zero engine change. |
| **Correctness** | Deterministic: same inputs + parameters → identical outputs. Golden-master regression suite over the mock dataset gates every release. |
| **Security** | Read-only SQL for the read path; write-back only via Service Layer with scoped B1 credentials; secrets via DPAPI/secret store; role-gated UI. |
| **Reliability** | Runs as Windows service `CobaltMRP`; survives restart; long runs are cancellable; failed runs leave no partial write-back. |
| **Observability** | Structured logs, per-phase timings (aggregation / explosion / netting / CRP), health endpoint. |

---

## 7. Success metrics

- **Adoption:** % of planning cycles run in Cobalt MRP vs. B1 MRP Wizard (target > 80% within 60 days of go-live per customer).
- **Trust:** planner-reported "I act on the recommendation without re-checking in Excel" (survey; target > 70%).
- **Speed:** median 10k-item run time (target < 60 s); reduction in planning-cycle wall-clock vs. baseline (target > 50%).
- **Action rate:** % of purchase/production recommendations converted to B1 orders in-app (target > 60% once write-back ships).
- **Exception hygiene:** median open-exception age trending down cycle over cycle.
- **Stability:** zero duplicate/partial write-backs; golden-master suite green on every release.
