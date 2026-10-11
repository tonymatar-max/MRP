# Calendar, vendors and planning-realism API additions

Extends `settings-api-contract.md`. Same auth rules (cookie, 401 signed out; Admin-only where stated). JSON camelCase, enums as strings, dates `YYYY-MM-DD`.

## 1. Planned orders gain vendor information
Every planned order in `GET /runs/{id}` (and in the release response) has two new fields:

| field | meaning |
|---|---|
| `vendorCode` | `string \| null`. The supplier CardCode that **Release** will use for a purchase order. Always `null` for production orders. |
| `vendorSource` | `"settings-override" \| "b1-preferred" \| "default" \| null`. Where `vendorCode` came from. Precedence: Settings item override, then the item's preferred vendor in SAP B1, then the Settings default vendor. `null` means **no vendor: the purchase order will fail to release** until one is configured. |

UI: show a **Vendor** column on Planned Orders (purchase orders only) with a small source badge ("B1", "override", "default"), and a red "No vendor" badge when `vendorSource` is null on a purchase order. The release button tooltip/toast should mention how many selected purchase orders have no vendor.

## 2. Planning settings additions (`planning` object in `/settings`)
```jsonc
"planning": {
  "defaultSource": "mock", "horizonDays": 180, "bucketDays": 7,
  "hoursPerUnitPerDay": 8, "forecastLookbackDays": 0,          // existing
  "forecastConsumption": "Forward",     // NEW "Forward" | "None": net forecast against real sales orders so demand is not counted twice
  "useB1Calendar": true,                // NEW use the SAP B1 company calendar (weekend days + holidays) when it exists
  "workingDays": ["Mon","Tue","Wed","Thu","Fri"],   // NEW used when useB1Calendar is false or B1 has no calendar
  "nonWorkingDates": [ { "date": "2026-12-25", "reason": "Christmas" } ],   // NEW extra closures, always applied
  "resources": [                        // NEW per-resource overrides (SAP B1 stores no capacity hours)
    { "code": "R300004", "hoursPerDay": 8, "efficiencyPercent": 100, "setupHoursPerOrder": 0 }
  ]
}
```
Resources not listed use `hoursPerUnitPerDay x units`, 100% efficiency, no setup.

`GET /resources?source=mock|b1-sql` -> `[ { code, name, units, hoursPerDay, efficiencyPercent, setupHoursPerOrder, configured } ]` merging the source's resources with the saved overrides (`configured` true when a saved override exists), so the Settings UI can list every resource and let the admin edit its numbers. Returns 400 `{message}` like other source calls when the B1 database is not configured.

## 3. Calendar
`GET /runs/{id}/calendar?from=2026-10-01&to=2026-10-31&kinds=Demand,Supply&item=ABC&warehouse=01`
All query parameters except `from` and `to` are optional. `kinds` is a comma list; omitted = all. `item` is a case-insensitive "contains" on item code.

```jsonc
{
  "from": "2026-10-01", "to": "2026-10-31",
  "horizonStart": "2026-10-09", "horizonEnd": "2027-04-07",
  "truncated": false,                   // true when more than 5000 events matched; the UI should say "narrow the filters"
  "nonWorking": [ { "date": "2026-10-10", "reason": "Weekend" }, { "date": "2026-12-25", "reason": "Christmas Day" } ],
  "events": [
    { "id": "abc", "date": "2026-10-12", "kind": "PlannedRelease", "itemCode": "BIKE-01", "warehouseCode": "WH01",
      "quantity": 20, "label": "Release production order", "ref": "<plannedOrderId>", "severity": "info" }
  ]
}
```
`kind` values and what they mean:

| kind | meaning | `ref` |
|---|---|---|
| `Demand` | open sales order line due that day | document key (e.g. `ORDR:123`) |
| `Forecast` | forecast period start (net of consumption) | `FCST` |
| `Supply` | open purchase or production order due that day | document key |
| `PlannedRelease` | MRP recommends **releasing** (ordering/starting) that day | planned order id |
| `PlannedDue` | MRP planned order **due** (receipt) that day | planned order id |
| `Exception` | planner action message (expedite, defer, past due...) dated that day | exception type name (e.g. `Expedite`) |
| `CapacityOverload` | a work center is over capacity in the bucket starting that day | work center code |

`severity` is `"info" | "warn" | "danger"`. Suggested colours: Demand blue, Forecast light-blue/dashed, Supply green, PlannedRelease orange, PlannedDue purple, Exception by severity, CapacityOverload red. `quantity` can be 0 for exceptions.

`ref` for PlannedRelease/PlannedDue is a planned order id: link to Planned Orders with that order expanded. For `Exception` link to the Exceptions page.

## 4. UI requirements for the Calendar screen
- New nav item **Calendar** (after Capacity). Uses the run selected in the existing run picker.
- **Month grid** (default) with prev/next/today, plus an **Agenda** (day-by-day list) toggle and a **Week** toggle if it is cheap to do. Only dates inside `[horizonStart, horizonEnd]` are navigable beyond which show a muted notice.
- Each day cell shows coloured count chips per kind (not all events), a tinted background for non-working days (with the reason as a tooltip), a stronger marker for the current day and a red corner mark on days with `CapacityOverload` or `danger` events.
- Clicking a day opens a **day panel** (side panel or modal) listing that day's events grouped by kind, each with item, warehouse, quantity, label and a link per the `ref` rules above.
- **Filters**: kind chips (toggle), item search, warehouse select (from the events already loaded). Loading is per visible range (`from`/`to` = the visible grid), cached with TanStack Query; show a "narrow the filters" note when `truncated`.
- A legend explaining colours. Accessible: cells are buttons with an aria-label such as "12 October, 5 events".
- Works in mock mode with plausible generated data (including weekends as non-working, one holiday, a few overloads).
