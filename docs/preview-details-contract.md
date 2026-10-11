# Review step drill-down (API contract)

Extends `wizard-and-masterdata-contract.md` (section 2, Preview). Same auth (cookie; Admin and Planner may use it). JSON camelCase; dates `YYYY-MM-DD`.

Every number on the wizard's **Review** step becomes clickable. Clicking opens a **details panel** (modal or side panel) with a short **summary** and the **list of records** behind that number. The count shown and the rows listed always come from the same selector on the server, so `total` in the details always equals the number that was clicked.

## Endpoint
`POST /runs/preview/details`
```jsonc
{ "criteria": RunCriteria, "metric": "demand.pastDue", "search": "", "take": 200 }
```
- `metric`: one of the metric ids below. Unknown -> 400 `{ "message": "..." }`.
- `search`: optional, case-insensitive "contains" over the row's text columns (item code/name, document, supplier...).
- `take`: rows to return, default 200, max 1000.

Response:
```jsonc
{
  "metric": "demand.pastDue",
  "title": "Overdue demand lines",
  "description": "Demand that was already due before the plan starts; it is planned as due on day one.",
  "total": 1873,                  // how many records match (ignores `search`; equals the preview number)
  "matched": 1873,                // how many match `search`
  "shown": 200,                   // rows returned
  "truncated": true,              // matched > shown
  "summary": [ { "label": "Lines", "value": "1,873" }, { "label": "Quantity", "value": "8,214" },
               { "label": "Oldest due", "value": "2007-04-01" }, { "label": "Items", "value": "42" } ],
  "columns": [ { "key": "document", "label": "Document", "type": "text" },
               { "key": "quantity", "label": "Quantity", "type": "number" },
               { "key": "dueDate", "label": "Due", "type": "date" },
               { "key": "overdue", "label": "Overdue", "type": "flag" } ],
  "rows": [ { "document": "ORDR:12", "itemCode": "A00001", "quantity": 5, "dueDate": "2014-12-05", "overdue": true } ]
}
```
Column `type` is one of `text`, `number`, `date`, `flag` (boolean, show as a dot/check), `badge` (short enum-like text, show as a pill). Render columns in the given order, right-align numbers, format dates like the rest of the app. Every row has all column keys (null allowed).

`POST /runs/preview/details/export` takes the same body and returns the **whole** list as a CSV download (`text/csv`, UTF-8 with BOM, file name `<metric>.csv`), ignoring `take`, honouring `search`.

## Metric ids and which Review number each one is
| metric | Review number | what the list is |
|---|---|---|
| `items.total` | Items "of total" | every item the source offers |
| `items.inScope` | Items "in scope" | items the run will plan |
| `items.buy` / `items.make` | Buy / Make | in-scope items by procurement |
| `items.noSupplier` | No supplier | in-scope purchased items with no supplier |
| `items.noLeadTime` | No lead time | in-scope purchased items with no lead time from any source |
| `demand.salesOrders` | Sales order lines | open sales order lines |
| `demand.pastDue` | Past due lines | demand already overdue at the plan start |
| `demand.forecasts` | Forecast lines | forecast lines in the plan |
| `demand.productionComponents` | Production components | components still to be issued to open production orders |
| `supply.purchaseOrders` | Purchase order lines | open purchase order lines |
| `supply.productionOrders` | Production order lines | open production orders |
| `supply.stock` | Stock rows | item x warehouse stock positions |
| `leadTime.supplier` / `leadTime.item` / `leadTime.itemGroup` / `leadTime.defaulted` | the four "Lead times (purchased items)" numbers | purchased in-scope items by where their lead time came from |

Item lists include (as columns): item code, name, group, type (badge), planning method (badge), supplier, supplier source (badge: master data / SAP B1), lead time days, lead time source (badge). Demand/supply lists include the document key, item (code + name), warehouse, quantity, due date and an overdue flag. Stock rows: item, warehouse, on hand, committed, on order.

## UI requirements
- In the Review step every count/number is a button-styled link (visible affordance: underline or chip, hover state, `aria-label` like "Show the 1,873 overdue demand lines"). Numbers that are 0 are plain text.
- The details panel shows: title, one-line description, the **summary** as small stat chips, a **search box** (debounced, re-queries the server with `search`), the table (sticky header, sortable client-side on the loaded rows is enough), a footer "Showing N of M" with a note when `truncated` ("refine the search or export to see everything"), a **Download CSV** button, and **Close** (Escape closes, focus returns to the number that was clicked).
- Clicking another number while the panel is open switches the content. The wizard keeps its state; closing the panel returns to the Review step.
- The panel must call the endpoint with the wizard's **current, unsaved criteria** (the same object used for the preview).
- Show a loading state, and the server `{message}` on errors.
- Works in mock mode with plausible generated rows (counts must equal the mock preview numbers).
