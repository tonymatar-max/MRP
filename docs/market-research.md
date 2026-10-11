# Cobalt MRP — Market & Competitive Research

**Prepared for:** Cobalt (independent SAP Business One product studio)
**Product:** Cobalt MRP — regenerative MRP engine + reorder/min-max + CRP + forecast management for SAP Business One
**Stack:** .NET 8 + SQL Server + React; reads SAP B1 SQL, writes back purchase/production orders via the B1 Service Layer
**Author:** Presales / Market-Research analyst (tony.matar@seidor.com)
**Date:** 2026-10-09

> **Sourcing note.** Most public material on B1 manufacturing add-ons is vendor marketing or SAP-partner blogs, not independently audited feature lists, and several pages are undated or describe older B1 versions (8.82 / 9.0 / 10.0). Where a claim is vendor-sourced or sales-motivated, it is flagged. Treat capability and pricing figures as directional and confirm against live vendor documentation before they reach a customer-facing proposal. All URLs were accessed **2026-10-09**.

---

## 1. Executive summary

SAP Business One ships a built-in **MRP Wizard** that is genuinely useful for basic netting and order recommendations, but it is a *scenario/recommendation tool*, not a persistent planning system: results are transient, there is no finite capacity scheduling, pegging is not persisted, and forecasting/forecast-consumption is thin. The established third-party answer is to buy a **heavy manufacturing suite** — Beas Manufacturing (Boyum), CompuTec ProcessForce, or Produmex Manufacturing — which bundle full shop-floor, routing, APS and quality control. These are powerful but **expensive (Beas starts around $10,000 and is quote-only), broad, and implementation-heavy** — you buy an entire manufacturing platform to get better planning.

That gap is Cobalt MRP's opening: a **focused, persistent, planner-centric MRP + CRP + forecasting layer** that is dramatically lighter and cheaper to adopt than a full APS suite, yet materially more capable than the native wizard — with the modern-SaaS planner UX (workbench, pegging, exception messages, what-if) that B1 customers see in NetSuite / MRPeasy / Katana but can't get inside B1 today.

See **Section 7** for the five biggest differentiation opportunities.

---

## 2. SAP Business One native MRP — what it does and where it falls short

### What the native MRP Wizard does (verified)
- A **wizard-driven MRP run**: the planner defines a *scenario* by choosing items, data sources, and a planning horizon, then executes a run.
- **Netting across standard demand/supply sources**: sales orders, forecasts, BOM-derived dependent demand, existing inventory, POs/production orders, lead times, order multiples/minimums, and planning rules.
- **Make/buy order recommendations with dates**: outputs recommendations to create production orders, purchase orders, purchase quotations, and inventory transfer requests, each with a suggested date.
- **Lead-time offsetting**: component due dates are back-scheduled from parent due dates; past-due items are flagged.
- **Basic forecasting**: forecasts can be built from historical sales or entered manually and adjusted by a percentage; a run can optionally consume a selected forecast.
- **Company vs. Warehouse netting modes**: "Company" nets across all warehouses; "Warehouse" analyzes each separately.
- B1 10.0 provides **resource master data** (capacity attributes) and BOMs that can carry **resource lines** — so some master data exists even though scheduling logic does not.

### Well-known limitations (the Cobalt opportunity)
| Limitation | Evidence / status |
|---|---|
| **No finite/capacity scheduling** — the wizard gives make/buy recommendations with dates but **no schedule**; it won't sequence or refuse to overload a work center, and has no schedule board or capacity-based promise date | Verified (vendor-flagged, ECOSIRE; corroborated across partner material). Master data for resources exists in 10.0 but the *scheduling logic* does not. |
| **Transient / non-persistent results** — a run is a scenario; recommendations are not retained as a durable, auditable plan that can be compared run-over-run | Could not find an SAP statement confirming persistence; partner walkthroughs treat the run as a regenerate-each-time scenario. Flag as "strongly indicated, not SAP-confirmed." |
| **Pegging not persisted** — no durable supply→demand traceability chain a planner can drill into after the run | No B1 pegging feature found in SAP docs (pegging material in results was SAP ERP PP/APO, a different product). |
| **Weak forecasting & consumption** — forecast is a flat table with %-adjust; no statistical models, no multi-level consumption logic surfaced | Verified thin; no documented consumption controls. |
| **No exception-message framework** — no structured "expedite / defer / cancel / de-expedite" action messages as in mainstream MRP | Not present in native wizard material. |
| **Weak multi-warehouse traceability** — Company mode can *hide* a shortage when another warehouse has stock; Warehouse mode loses cross-warehouse netting | Verified (Firebear). |
| **Basic routings / no CRP** — routings are basic and finite scheduling is limited | Verified via partner comparison material. |

---

## 3. Competitive landscape — third-party B1 manufacturing / MRP add-ons

| Add-on (vendor) | 2026 status | Positioning | MRP / planning approach |
|---|---|---|---|
| **Beas Manufacturing** (Boyum IT) | **Current, market-leading** | Full advanced manufacturing suite: production planning, capacity scheduling, quality control, shop-floor control; **Beas APS** adds drag-and-drop finite scheduling, bottleneck detection, scenario simulation. Actively modernized toward the SAP Web Client. | MRP I & II re-checking inventory/demand/supply; run shows resource impact. Planning is a module *inside a whole manufacturing platform*. |
| **CompuTec ProcessForce** | **Current** | Deep process + discrete manufacturing (pharma, food & bev, industrial); multi-level BOM, routings, product costing, MRP II, production scheduling. | **Integrates with the native B1 MRP Wizard / Order Recommendation** and creates ProcessForce manufacturing orders, back-scheduled from due dates. Extends — does not replace — B1's engine. |
| **Produmex Manufacturing** (Boyum IT) | **Current** | Process/recipe manufacturing, batch + co-product/by-product handling, deep WMS (Produmex WMS). Suits process + distribution-heavy ops. | Per third-party comparison, **MRP logic largely comes from native B1**; Produmex extends into process/batch scenarios rather than rewriting the planning engine. |
| **OrchestraWeb, CompuTek, Datapel, eBECS, Argentis, Citixsys/iVend, Milliken** | **Could not verify current B1 MRP offering in this research pass** | Named in brief but no authoritative current-product page surfaced for their B1 *MRP* capability. | Unknown — flag for a dedicated follow-up pass; do not cite as competitors without confirmation. |

**Key structural observation:** the two strongest add-ons behave in opposite ways — Beas largely **replaces** planning with its own APS stack (and sells the whole suite), while ProcessForce and Produmex mostly **wrap/extend the native wizard**. Neither offers a *standalone, planning-only* product. A B1 customer who wants better MRP today must buy an entire manufacturing platform.

### Standalone / ERP MRP benchmarks (feature ideas, not B1 competitors)
- **NetSuite** — re-architected supply-planning engine with MRP, **persistent pegging** (trace supply↔demand, across locations), **Planners Workbench** with action/exception messages (pull-in / push-out / cancel), what-if scenarios, and **DRP** (inter-location transfer planning). CRP not clearly documented. *This is the closest "target UX" for Cobalt's planner experience.*
- **MRPeasy** — manufacturing-first SaaS; clean web UX, "set up in a weekend," per-user tiers. Good model for low-friction adoption.
- **Katana** — visual production scheduling, real-time inventory, flat monthly pricing. Good model for the schedule board.
- **Fishbowl** — inventory-centric, QuickBooks-anchored; advanced manufacturing tiers quoted.
- **SAP IBP** — enterprise statistical forecasting / S&OP benchmark (far up-market; forecasting feature ideas only).

---

## 4. Gap analysis — where Cobalt MRP can win

**What B1 native lacks** (Section 2): persistence, pegging, CRP/finite capacity, exception messages, real forecasting, clean planner UX.

**What the add-ons charge for:** to close those gaps today a customer buys a **full manufacturing suite** — Beas (≈$10k+ entry, quote-only, implementation-heavy) or ProcessForce/Produmex — acquiring shop-floor, quality, WMS and APS they may not need, with a correspondingly heavy project.

**Where Cobalt wins — concrete differentiators:**
1. **Planning-only, not a platform.** Sell *better MRP/CRP/forecasting* without forcing a shop-floor/WMS suite. Faster, cheaper, lower-risk adoption than Beas/ProcessForce/Produmex.
2. **Persistent, auditable plans.** Store every run; diff run-over-run; keep recommendations as durable records — directly answering the native wizard's transient nature.
3. **Persisted pegging & traceability.** Durable supply→demand pegging a planner can drill into *after* the run, cross-warehouse — matching NetSuite, beating native B1.
4. **Real CRP against work centers.** Finite capacity load/overload visibility and a schedule board — the single most-cited native gap — without buying Beas APS.
5. **Modern exception-message workbench.** Structured expedite/defer/cancel/de-expedite action messages + what-if, in a React planner UI that feels like NetSuite/MRPeasy, embedded in the B1 world.
6. **Proper forecast management.** Statistical forecasting + multi-level forecast consumption, beyond native's flat %-adjust table.
7. **Clean write-back via Service Layer.** Approve recommendations → write POs/production orders back through the B1 Service Layer (modern, supported API) rather than brittle DI-API/SQL hacks.
8. **Independent-studio pricing & speed.** Undercut suite pricing with a transparent per-named-user or per-company model (Section 6).

---

## 5. Feature comparison matrix

Legend: **Full** = native/strong · **Partial** = limited/basic · **No** = absent · **Planned** = Cobalt roadmap commitment

| # | Capability | Cobalt MRP (target) | B1 Native MRP | Beas (Boyum) | Produmex Mfg | NetSuite (benchmark) |
|---|---|---|---|---|---|---|
| 1 | Regenerative MRP / netting | Full | Full | Full | Partial (native engine) | Full |
| 2 | Multi-level BOM explosion w/ low-level coding | Full | Full | Full | Full | Full |
| 3 | Time-phased planning buckets | Full | Partial | Full | Partial | Full |
| 4 | Lot sizing (min/max/multiple/FOQ) | Full | Partial | Full | Partial | Full |
| 5 | Lead-time offsetting | Full | Full | Full | Full | Full |
| 6 | **Persisted pegging / traceability** | **Full (Planned)** | **No** | Partial | Partial | Full |
| 7 | **Exception / action messages** | **Full (Planned)** | **No** | Partial | Partial | Full |
| 8 | **Capacity Requirements Planning (CRP)** | **Full (Planned)** | **No** | Full (APS) | Partial | Partial/unclear |
| 9 | Finite scheduling / schedule board | Planned | No | Full (APS) | Partial | Partial |
| 10 | Reorder / min-max planning | Full | Partial | Full | Full | Full |
| 11 | **Forecast mgmt + consumption** | **Full (Planned)** | **Partial** | Partial | Partial | Full |
| 12 | Statistical forecasting | Planned | No | Partial | No | Full (IBP-grade up-market) |
| 13 | **Persistent / auditable plan history** | **Full (Planned)** | **No** | Partial | Partial | Full |
| 14 | What-if / scenario comparison | Full | Partial (re-run) | Full | Partial | Full |
| 15 | Write-back of PO / production orders | Full (Service Layer) | Full | Full | Full | N/A (native ERP) |
| 16 | Modern web/React planner UX | Full | No (SAP client) | Partial (Web Client push) | No | Full |
| 17 | DRP / inter-warehouse transfer planning | Planned | Partial | Partial | Full | Full |
| 18 | Adoption footprint (lighter = better) | **Light** | Light (but weak) | Heavy suite | Heavy suite | Full ERP replacement |

> Bolded rows are the clearest native-B1 gaps and Cobalt's primary wedge. Beas/Produmex ratings are directional, drawn from vendor marketing and partner comparisons, not audited.

---

## 6. Pricing models observed (to inform positioning)

| Product | Model | Figures found (directional, third-party) |
|---|---|---|
| **SAP Business One** | Named-user; cloud subscription or on-prem perpetual + maintenance | Cloud ~$95–$250 /user/mo (CostBench 2026: $110–$219); on-prem $3,500–$5,500 one-time per named user + 18–20% annual maintenance; implementation $15k–$150k |
| **Beas Manufacturing** | **Quote-only, not per-user** | Starts ≈ **$10,000** (ITQlick, Software Advice). Capterra's "$10,000/user/month" is almost certainly a listing error. Implementation extra. |
| **Produmex Manufacturing** | Quote-only | No public pricing found |
| **CompuTec ProcessForce** | Quote-only (partner) | No public pricing found |
| **MRPeasy** | **Per-user/month, published tiers** | Starter $49, Professional $69, Enterprise $99, Unlimited $149 (per user/mo); extra users ~$79 per 10; free trial |
| **Katana** | Flat monthly, unlimited users (conflicting) | ~$359–$399/mo base (one source lists $299/user/mo); onboarding ~$2,000 |
| **Fishbowl** | One-time + tiered | ~$4,395 one-time (one source) or from ~$349/mo (G2); Advanced Manufacturing from ~$675/mo; higher tiers quoted |

**Positioning implication.** The B1 suite competitors are **opaque and quote-only, starting ~$10k**. The standalone SaaS benchmarks are **transparent and per-user ($49–$149)**. Cobalt can differentiate on *both* price and clarity: a **published per-named-user or per-company subscription** that sits well below a Beas-class project, framed as "modern planning without the manufacturing-suite price tag." A per-company (site) tier avoids punishing teams with many casual viewers and undercuts per-user suites for planning-heavy shops.

---

## 7. Five biggest differentiation opportunities (summary)

1. **Planning-only, not a platform.** Deliver persistent, modern MRP/CRP/forecasting as a *focused add-on* — the capability customers currently can only get by buying a full Beas/ProcessForce/Produmex manufacturing suite, at a fraction of the cost, risk and implementation time.
2. **Persistence & pegging where B1 has none.** Durable, auditable, run-over-run plans with persisted supply→demand pegging and cross-warehouse traceability — closing the native wizard's most fundamental architectural gap and matching NetSuite.
3. **Real CRP against work centers without Beas APS.** Finite-capacity load/overload visibility and a schedule board — the single most-cited native B1 limitation — unbundled from an entire APS/shop-floor stack.
4. **Modern planner workbench UX.** React-based exception/action messages (expedite/defer/cancel), what-if scenario comparison, and drill-down pegging — the NetSuite/MRPeasy experience, embedded in the B1 ecosystem and writing back cleanly via the Service Layer.
5. **Transparent, lighter pricing.** A published per-named-user / per-company model positioned under the opaque ~$10k+ quote-only suites — "better MRP than native, without buying a manufacturing platform."

---

## 8. Open items for a follow-up research pass
- Verify current B1 MRP offerings (if any) for **OrchestraWeb, CompuTek, Datapel, eBECS, Argentis, Citixsys/iVend, Milliken** — none confirmed in this pass.
- Confirm **transient-results / plan-persistence** behavior of the native wizard against SAP Help / SAP Notes for B1 10.0 (currently strongly indicated, not SAP-confirmed).
- Obtain real **Beas / Produmex / ProcessForce** quotes via a partner to pin positioning.
- Confirm whether **NetSuite** exposes true CRP, and capture **SAP IBP** statistical-forecasting features as a forecasting roadmap benchmark.

---

## 9. Sources (accessed 2026-10-09)

**SAP B1 native MRP**
- ECOSIRE, B1 production planning & scheduling — https://ecosire.com/ja/apps/sap-business-one/sap-b1-production-planning-scheduling
- Firebear Studio, Using the Wizard to Execute an MRP Run in SAP B1 — https://firebearstudio.com/blog/how-to-use-the-wizard-to-execute-an-mrp-run-in-sap-business-one.html
- Cleverence, Configure and use MRP in SAP B1 9.0 — https://cleverence.com/articles/sap-documentation/how-to-configure-and-use-mrp-in-sap-business-one-9-0-8347
- Cleverence, Optimized MRP in SAP B1 10.0 — https://www.cleverence.com/articles/sap-documentation/mrp-sap-business-one-10-0-4815/
- Wisys, How MRP works with SAP Business One — https://wisys.com/blog/how-material-requirements-planning-works-with-sap-business-one
- Leverage Technologies, SAP B1 MRP — https://www.leveragetech.com.au/blog/sap-business-one-mrp-material-requirements-planning/
- Vision33, SAP Business One for Manufacturing — https://www.vision33.com/hubfs/SAP%20Business%20One%20for%20Manufacturing.pdf

**Beas Manufacturing (Boyum)**
- Boyum, Complete Manufacturing Software — https://www.boyum-solutions.com/complete-manufacturing-software-by-boyum
- Boyum IT, Beas Manufacturing (Notion) — https://boyum-it.notion.site/Beas-Manufacturing-175be94de47e81b3a94fed57b966fff8
- Seidor, Beas Manufacturing brochure — https://proimg.seidor.com/sites/default/files/2022-02/Beas_Manufacturing-Seidor_brochure.pdf
- Capterra, Beas Manufacturing pricing — https://www.capterra.com/p/179965/Beas-Manufacturing/
- Software Advice, Beas profile — https://www.softwareadvice.com/manufacturing/beas-profile
- ITQlick, Beas Manufacturing — https://www.itqlick.com/beas-manufacturing

**CompuTec ProcessForce**
- CompuTec, ProcessForce MRP overview — https://learn.computec.one/docs/processforce/2.0/user-guide/mrp/overview
- Cloudiax, CompuTec Solutions for Manufacturing Logistics — https://www.cloudiax.com/wp-content/uploads/sites/38/2026/05/CompuTec_Solutions_for_Manufacturing_Logistics.pdf

**Produmex / B1 manufacturing comparison**
- ERP Research, SAP Business One manufacturing management — https://www.erpresearch.com/erp/sap-business-one/manufacturing-management
- Seidor, Enhance SAP B1 with Produmex Scan — https://seidor.com/en-us/blog/enhance-sap-business-one-produmex-scan

**Standalone / ERP benchmarks**
- NetSuite, What is NetSuite MRP — https://www.netsuite.com/portal/products/erp/supply-planning/mrp.shtml
- Net at Work, NetSuite re-architected supply planning engine — https://www.netatwork.com/?p=42231
- Cleverence, NetSuite Supply Planning Guide — https://www.cleverence.com/articles/oracle-documentation/supply-planning-netsuite-4837/
- Capterra, MRPeasy pricing — https://capterra.com/p/134177/MRPEasy/pricing/
- Qoblex, MRPeasy vs Fishbowl (2026) — https://qoblex.com/blog/mrpeasy-vs-fishbowl/
- CostBench, MRPeasy vs Katana (2026) — https://costbench.com/compare/mrpeasy-vs-katana/
- G2, Fishbowl vs MRPeasy — https://www.g2.com/compare/fishbowl-inventory-vs-mrpeasy

**SAP B1 pricing**
- CostBench, SAP Business One pricing 2026 — https://www.costbench.com/software/erp/sap-business-one/
- ERP Research, SAP Business One cost — https://erpresearch.com/pages/en-us/sap-business-one-cost
