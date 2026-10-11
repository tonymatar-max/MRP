# Run wizard, master data and lead-time rules: API contract

Extends the earlier contracts. Same auth (cookie; 401 signed out). JSON camelCase, enums as strings, dates `YYYY-MM-DD`.
**Roles:** Admin and Planner may run MRP and edit master data (overrides live in Cobalt, never in SAP B1). Release to B1 and Settings stay Admin-only.

## 1. Run criteria (the wizard's payload)
`POST /runs` accepts `{ "criteria": RunCriteria, "notes": "..." }`. The old simple body (`source`, `horizonDays`, `bucketDays`, `warehouses`) still works and is mapped onto defaults.

```jsonc
RunCriteria {
  "source": "mock",                       // "mock" | "b1-sql"
  "scope": {                              // WHICH ITEMS GET PLANNED (the whole BOM graph still drives demand)
    "warehouses": [],                     // empty = all
    "itemGroups": [],                     // B1 item-group codes (strings); empty = all
    "suppliers": [],                      // CardCodes: only items whose effective supplier is one of these; empty = all
    "itemSearch": "",                     // contains, in item code or name (case-insensitive)
    "itemCodes": [],                      // explicit list; empty = no restriction
    "procurement": "Both",                // "Both" | "Buy" | "Make"
    "planningMethods": [],                // subset of "Mrp","ReorderPoint","MinMax"; empty = all
    "includeComponents": true             // also plan every component below the selected make items
  },
  "horizon": { "startDate": null, "days": 180, "bucketDays": 7 },     // startDate null = today
  "demand": {
    "salesOrders": true, "salesOrderFrom": null, "salesOrderTo": null,   // optional ship-date window
    "forecasts": true, "forecastConsumption": "Forward",                 // "Forward" | "None"
    "forecastLookbackDays": 0,
    "productionComponents": true,         // components still to be issued to open production orders
    "pastDue": "FoldToStart",             // "FoldToStart" (plan overdue demand as due now) | "Ignore"
    "safetyStock": true                   // keep stock at the item's minimum level
  },
  "supply": { "onHand": true, "openPurchaseOrders": true, "openProductionOrders": true },
  "leadTime": {
    "priority": ["Supplier", "Item", "ItemGroup"],   // which lead time wins; any order of these three
    "mode": "FirstAvailable",                        // "FirstAvailable" (first in priority that has a value) | "Longest" (safest)
    "defaultDays": 0                                 // used when none of the three has a value
  },
  "lotSizing": { "applyMinOrder": true, "applyMultiples": true },
  "calendar": { "useWorkingCalendar": true },        // false = every day is a working day
  "capacity": { "enabled": true }                    // run capacity planning (CRP)
}
```
Where each lead time comes from: **Supplier** = the lead time entered for the item's effective supplier (Master data > Suppliers; SAP B1 has none). **Item** = the item's lead time (Master data override, else SAP B1 `OITM.LeadTime`). **ItemGroup** = SAP B1 `OITB.LeadTime`. Make items have no supplier, so "Supplier" is skipped for them.

`GET /runs/defaults?source=mock|b1-sql` -> a full `RunCriteria` pre-filled from Settings (horizon, bucket, forecast consumption, lookback, lead-time priority/mode) and sensible defaults. The wizard starts from this.

`GET /runs/{id}` now also returns `criteria` (the RunCriteria the run used, or `null` for runs made before this feature) and its planned orders gain:

| field | meaning |
|---|---|
| `leadTimeDays` | lead time used for the order |
| `leadTimeSource` | `"Supplier" \| "Item" \| "ItemGroup" \| "Default" \| "None"` |
| `vendorSource` | now one of `"settings-override" \| "master-data" \| "b1-preferred" \| "default" \| null` |

Errors from `POST /runs` and `POST /runs/preview`: 400 `{ "message": "..." }` (e.g. B1 database not configured, `itemGroups` not numeric, `horizon.days` out of range 1..1095).

## 2. Preview (the wizard's Review step)
`POST /runs/preview` body `{ "criteria": RunCriteria }` -> counts, without creating a run:
```jsonc
{
  "items":   { "total": 115, "inScope": 40, "buy": 30, "make": 10, "noSupplier": 5, "noLeadTime": 12 },
  "demand":  { "salesOrderLines": 13, "salesOrderQuantity": 220, "pastDueLines": 3, "forecastLines": 3, "productionComponentLines": 8 },
  "supply":  { "purchaseOrderLines": 18, "productionOrderLines": 5, "stockRows": 140 },
  "leadTime":{ "fromSupplier": 4, "fromItem": 20, "fromItemGroup": 2, "defaulted": 4 },       // purchased items in scope
  "warnings": [ "5 purchased items in scope have no supplier: their orders cannot be released to SAP B1." ]
}
```
Show it as a tidy summary with the warnings highlighted.

## 3. Lookups (for the wizard and master data filters)
- `GET /lookups/item-groups?source=` -> `[ { code, name, leadTimeDays, itemCount } ]`
- `GET /lookups/suppliers?source=&search=&take=50` -> `[ { cardCode, cardName } ]` (type-ahead)
- `GET /warehouses?source=` (existing) -> `["01","02"]`

## 4. Master data: items
`GET /masterdata/items?source=&search=&group=&supplier=&procurement=Buy|Make&filter=all|overridden|noSupplier|noLeadTime&sort=itemCode|itemName|leadTime&dir=asc|desc&page=1&pageSize=100`
-> `{ total, page, pageSize, items: MasterItemRow[] }`

```jsonc
MasterItemRow {
  "itemCode": "A00001", "itemName": "...", "groupCode": "100", "groupName": "Items", "procurement": "Buy",
  "b1": {                                  // what SAP B1 holds (as the planner sees it)
    "planningMethod": "Mrp",               // "Mrp" | "ReorderPoint" | "MinMax" | "None"
    "lotSizing": "LotForLot",              // "LotForLot" | "FixedQuantity" | "MinMaxMultiple" | "PeriodOfSupply"
    "leadTimeDays": 7, "groupLeadTimeDays": 5,
    "minOrderQty": 0, "orderMultiple": 0, "safetyStock": 0, "reorderPoint": 0, "maxStockLevel": 0,
    "fixedLotSize": 0, "periodOfSupplyDays": 0,
    "supplier": "V10000", "supplierName": "..." },
  "override": null | {                     // Cobalt-side overrides: a null field means "not overridden"
    "planningMethod": null, "lotSizing": null, "leadTimeDays": 14, "minOrderQty": null, "orderMultiple": null,
    "safetyStock": null, "reorderPoint": null, "maxStockLevel": null, "fixedLotSize": null, "periodOfSupplyDays": null,
    "supplier": "V1010", "notes": "", "updatedBy": "admin", "updatedAt": "2026-10-09T10:00:00Z" },
  "effective": { ...same fields as b1, with overrides applied... },
  "leadTime": { "days": 14, "source": "Item", "supplierDays": 21, "itemDays": 14, "groupDays": 5 },   // under the CURRENT default priority/mode
  "overriddenFields": ["leadTimeDays", "supplier"]
}
```
- `PUT /masterdata/items/{itemCode}` body = the `override` object (fields you want overridden; `null`/absent = not overridden, i.e. clears it). Returns the updated `MasterItemRow`. Validates: numbers >= 0, enums valid, `supplier` must be a known CardCode (400 otherwise).
- `POST /masterdata/items/bulk` body `{ "itemCodes": [..], "set": { "leadTimeDays": 14, "supplier": "V1010" }, "clear": ["safetyStock"] }` -> `{ "updated": 12 }`.
- `DELETE /masterdata/items/{itemCode}` -> 204 (removes every override: back to SAP B1 values).
- `GET /masterdata/items/export?...same filters...` -> `text/csv` download of the effective values.

## 5. Master data: suppliers
- `GET /masterdata/suppliers?source=&search=&filter=all|withLeadTime|withoutLeadTime|inUse&page=1&pageSize=100` -> `{ total, page, pageSize, suppliers: [ { cardCode, cardName, leadTimeDays: number|null, notes, itemCount, updatedBy, updatedAt } ] }` (`itemCount` = items whose effective supplier is this one).
- `PUT /masterdata/suppliers/{cardCode}` body `{ "leadTimeDays": 21 | null, "notes": "" }` -> the updated row. `leadTimeDays: null` clears it.

## 6. Settings additions (`planning`)
`"leadTimePriority": ["Supplier","Item","ItemGroup"]`, `"leadTimeMode": "FirstAvailable"`, `"defaultLeadTimeDays": 0` (defaults for the wizard and for the lead-time column in Master data). The Service Layer tab's vendor-override table is now **legacy**: add a note "Prefer Master data > Items; entries here still win over everything" and keep it working.

## 7. UI requirements
### Run wizard (replaces the Run MRP dialog; keep a small "Quick run" link that opens the old simple dialog)
Full-screen or large modal, 5 steps with a progress header, Back/Next, state preserved between steps, validation per step, and everything prefilled from `GET /runs/defaults`.
1. **Scope**: data source; warehouses (chips); item groups (multi-select from lookups); suppliers (type-ahead multi-select); item search; explicit item codes (paste a list); Buy/Make/Both; planning methods; "Also plan components of selected make items".
2. **Demand**: sales orders (+ optional ship-date window), forecasts (+ consumption, lookback), components of open production orders, past-due handling, safety stock. A one-line explanation under each.
3. **Supply and lead times**: toggles for on-hand/open POs/open production orders; **lead time rule**: drag-to-order or up/down buttons for the priority list Supplier / Item / Item group, a Mode toggle (First available / Longest), default days, with a short worked example ("Supplier lead time 21d, item 7d, group 5d -> 21d").
4. **Planning rules**: start date, horizon, bucket size, lot sizing toggles (min order, multiples), working calendar, capacity planning.
5. **Review**: calls `POST /runs/preview` and shows the counts and warnings, a notes box, then **Run MRP**. After running, go to the new run's dashboard.
Also: a **Reset to defaults** link; on the Dashboard's run header a **Criteria** disclosure listing the criteria that run used, with **Re-run with these criteria** (opens the wizard prefilled).
### Planned Orders
Add a **Lead time** column (days + small source badge: Supplier / Item / Group / Default) and update the vendor badge for `master-data`.
### Master data screen (new nav item, after Calendar)
Tabs **Items** and **Suppliers**; source selector.
- **Items**: server-paged grid (page size 50/100/200). Columns: code, name, group, type, supplier (effective, badge "B1" / "override"), lead time (effective days with source badge), min order, multiple, safety stock, reorder point, max level, method. Overridden cells are highlighted and show the B1 value on hover. Click a cell (supplier, lead time, min order, multiple, safety stock, reorder point, max level) to edit inline, Enter saves via PUT; supplier uses the type-ahead. A row action opens an **edit panel** with every field (B1 value shown next to each input, "revert" per field, notes) and **Reset to SAP B1**. Filters: search, group, supplier, Buy/Make, quick filters (All / Overridden / No supplier / No lead time). Multi-select rows -> **Bulk edit** dialog (set supplier / lead time / safety stock / min order / multiple, or clear fields). **Export CSV**.
- **Suppliers**: grid with code, name, lead time (inline editable, "-" when none), items using it, notes; filters (All / With lead time / Without / In use); search.
- Explain once, on the page: "Changes are stored in Cobalt MRP and used by planning. They do not change SAP Business One."
Mock mode must support all of this offline.
