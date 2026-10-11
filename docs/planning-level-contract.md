# Planning level, purchase requests and transfer requests (API contract)

Extends `wizard-and-masterdata-contract.md`. Same auth. JSON camelCase, enums as names, dates `YYYY-MM-DD`.

## 1. New run criteria (top level of `RunCriteria`)
```jsonc
{
  "level": "Warehouse",                 // "Warehouse" (plan each warehouse separately) | "Company" (pool all selected warehouses)
  "purchasing": { "document": "Order" },// "Order" -> purchase orders | "Request" -> purchase requests. Applies to every purchased item, at both levels
  "transfers": {                        // only used at Warehouse level
    "enabled": false,
    "sourceWarehouses": [],             // warehouses that may SUPPLY a transfer; empty = every selected warehouse
    "leadTimeDays": 1                   // days a transfer takes (request must go out this many days before it is needed)
  },
  "scope": { "warehouses": ["01","02"], ... }   // existing: the warehouses included in the run (empty = all)
}
```
Meaning:
- **Company level**: the stock, open orders and demand of all selected warehouses are pooled per item and the item is planned once for the company. It creates purchase orders or purchase requests (and production orders for made items), never transfers. Each order is received in the item's default warehouse (SAP B1 default warehouse, else the selected warehouse with the most stock/demand). `transfers` is ignored.
- **Warehouse level**: each selected warehouse is planned on its own. With `transfers.enabled`, when a warehouse needs an item and another selected source warehouse has surplus of it (stock above its own safety stock and its own demand and open orders over the whole plan), MRP recommends a **transfer request** from that warehouse, and purchases or produces only what is left. Validation: transfers need at least 2 warehouses in play (400 `{message}` otherwise); every `sourceWarehouses` entry must be among the selected warehouses.

Defaults for the wizard come from `GET /runs/defaults` (now also reflecting the Settings planning defaults below).

## 2. Planned orders
`type` gains two values: `"PurchaseRequest"` and `"TransferRequest"` (existing: `"PurchaseOrder"`, `"ProductionOrder"`). New field `fromWarehouse` (string or null): for a `TransferRequest` it is the warehouse the stock leaves; `warehouseCode` is the warehouse that receives it.
- A purchase request does **not** need a vendor (`vendorCode` may still be shown); the "No vendor" badge and blocker apply only to `PurchaseOrder`.
- A transfer has no vendor and no lead-time source badge (show "Transfer, N d").

## 3. Preview
`POST /runs/preview` response gains:
```jsonc
"planning": { "level": "Warehouse", "warehouses": ["01","02"], "purchaseDocument": "Order",
              "transfersEnabled": true, "transferSources": ["01","02"], "transferLeadTimeDays": 1 }
```
and `warnings` may include: "Company level: stock in N warehouses is pooled; orders are received in each item's default warehouse." and "Transfers are on but only one warehouse is selected."

## 4. Settings (`planning`) additions
`"defaultLevel": "Warehouse"`, `"purchaseDocument": "Order"`, `"transferLeadTimeDays": 1`.

## 5. UI requirements
### Wizard
Put a **Planning level** section at the top of the **Scope** step (before data source/warehouses), or a new first step if that reads better. Keep the wizard's step count to what you judge cleanest.
- Two large radio cards: **Company level** ("Pool the stock of the selected warehouses and plan each item once. Creates purchase orders or purchase requests. No transfers.") and **Warehouse level** ("Plan each warehouse separately. Can transfer stock between the selected warehouses before buying.").
- A **Purchasing document** choice for purchased items: *Purchase order* / *Purchase request* (both levels).
- **Warehouses**: the existing multi-select chips, relabelled by level ("Warehouses included (stock is pooled)" vs "Warehouses to plan").
- At **Warehouse level** only: a toggle **Create transfer requests when another warehouse has the quantity**, and when on: **Transfer from** (multi-select chips of the selected warehouses, default all) and **Transfer time (days)**. Disable/hide with an explanation when fewer than 2 warehouses are selected.
- The Review step and the "Criteria" disclosure on the Dashboard show level, purchasing document and transfer settings. Add the `planning` block from the preview to the Review summary, plus its warnings.
### Planned Orders
- Type column/filter: Purchase, Purchase request, Production, Transfer. Transfer rows show `fromWarehouse -> warehouseCode` in the warehouse cell.
- Vendor badge logic: "No vendor" only for `PurchaseOrder`.
- Release button toast/dialog wording works for all types ("created ... in SAP B1").
### Exceptions / Execute
Titles for the release action should say "purchase request" / "transfer request" for those types (the server provides `title`; just display it).
### Settings -> Planning defaults
Controls for the three new defaults.
### Mock mode
Must work offline: mock runs honour level/purchasing/transfers (generate a couple of transfer rows when transfers are enabled and >=2 warehouses are selected; collapse to single-warehouse rows at company level).
