// Domain model — mirrors the Cobalt MRP .NET backend DTOs.

export type PlannedOrderType = 'PurchaseOrder' | 'PurchaseRequest' | 'ProductionOrder' | 'TransferRequest';

export type ExceptionType =
  | 'Expedite'
  | 'Defer'
  | 'Cancel'
  | 'PastDue'
  | 'ReleaseInPast'
  | 'NotPlanned'
  | 'CapacityOverload';

export type DataSource = 'mock' | 'b1-sql';

export interface MrpRun {
  id: string;
  runAt: string; // ISO datetime
  runBy: string;
  horizonStart: string; // ISO date
  horizonEnd: string; // ISO date
  bucketDays: number;
  notes?: string;
  plannedOrderCount: number;
  exceptionCount: number;
}

export interface PlannedOrder {
  id: string;
  itemCode: string;
  warehouseCode: string;
  /** TransferRequest only: the warehouse the stock leaves (warehouseCode receives it). */
  fromWarehouse?: string | null;
  type: PlannedOrderType;
  quantity: number;
  dueDate: string; // ISO date
  releaseDate: string; // ISO date
  lowLevelCode: number;
  firmed: boolean;
  b1DocEntry?: number | null;
  /** Supplier CardCode Release will use (purchase orders only; null for production). */
  vendorCode?: string | null;
  /** Where vendorCode came from; null on a purchase order = no vendor, release will fail. */
  vendorSource?: VendorSource | null;
  leadTimeDays?: number | null;
  leadTimeSource?: LeadTimeSource | null;
}

export type VendorSource = 'settings-override' | 'master-data' | 'b1-preferred' | 'default';
export type LeadTimeSource = 'Supplier' | 'Item' | 'ItemGroup' | 'Default' | 'None';

export interface PeggingLink {
  plannedOrderId: string;
  demandSource: string; // e.g. 'SalesOrder', 'Forecast', 'DependentDemand'
  quantity: number;
  demandDueDate: string; // ISO date
  demandDocKey: string;
  parentItemCode?: string | null;
}

export interface ExceptionMessage {
  id: string;
  type: ExceptionType;
  itemCode: string;
  warehouseCode: string;
  message: string;
  relatedDate: string; // ISO date
  sourceDocKey?: string | null;
  quantity: number;
  plannedOrderId?: string | null;
  /** Purchase order line for OPOR sources, else null. */
  sourceLine?: number | null;
  /** Existing order's due date / the date the plan wants (Expedite, Defer). */
  currentDate?: string | null;
  proposedDate?: string | null;
  /** What Execute can do right now; empty = no Execute button. */
  actions?: ExceptionAction[];
  actionStatus?: 'Done' | null;
  actionBy?: string | null;
  actionAt?: string | null; // ISO datetime
  actionNote?: string | null;
}

export type ExceptionAction = 'ReleaseOrder' | 'MoveDueDate' | 'Close';

export interface ExecuteRequest {
  action: ExceptionAction;
  /** MoveDueDate only. */
  newDate?: string;
}
export interface ExecutePayload extends ExecuteRequest {
  note: string;
  confirm: true;
}
export interface ExecutePreview {
  action: ExceptionAction;
  canExecute: boolean;
  title: string;
  summary: string;
  current: Record<string, string | number | null>;
  proposed: Record<string, string | number | null>;
  target: { kind: 'PurchaseOrder' | 'ProductionOrder' | 'PlannedOrder'; docEntry?: number | null; line?: number | null };
  warnings: string[];
  blockers: string[];
}
export interface ExecuteResult {
  ok: boolean;
  message: string;
  exception: ExceptionMessage;
}

export interface CapacityLoad {
  workCenterCode: string;
  bucketStart: string; // ISO date
  requiredHours: number;
  availableHours: number;
  overloaded: boolean;
}

export interface Forecast {
  id: string;
  itemCode: string;
  warehouseCode: string;
  periodStart: string; // ISO date
  quantity: number;
  consumed: number;
}

// Full run detail returned by GET /api/runs/{id}
export interface MrpRunDetail extends MrpRun {
  plannedOrders: PlannedOrder[];
  pegging: PeggingLink[];
  exceptions: ExceptionMessage[];
  capacityLoads: CapacityLoad[];
  /** null for runs made before criteria were recorded. */
  criteria?: RunCriteria | null;
}

export interface TriggerRunRequest {
  criteria?: RunCriteria;
  warehouses?: string[];
  horizonDays?: number;
  bucketDays?: number;
  source?: DataSource;
  notes?: string;
}

export interface ReleaseRequest {
  plannedOrderIds: string[];
}

export interface ReleaseResult {
  released: number;
  /** Present when the API reports Service Layer write-back status. */
  writebackEnabled?: boolean;
  pushedToB1?: number;
  failures?: { plannedOrderId: string | null; error: string }[];
  plannedOrders: PlannedOrder[];
}

// ---------- Auth / users ----------
export type Role = 'Admin' | 'Planner';

export interface AuthUser {
  username: string;
  role: Role;
}

export interface AuthStatus {
  setupRequired: boolean;
  authenticated: boolean;
  user: AuthUser | null;
}

export interface Credentials {
  username: string;
  password: string;
}

export interface NewUser extends Credentials {
  role: Role;
}

// ---------- Settings ----------
export type DbAuthentication = 'Windows' | 'Sql';

export interface DbSettings {
  server: string;
  database: string;
  authentication: DbAuthentication;
  userName: string;
  /** Always "" from the server; "" on save means keep the stored one. */
  password: string;
  passwordSet: boolean;
  trustServerCertificate: boolean;
  connectionStringOverride: string;
}

export interface ItemVendor {
  itemCode: string;
  cardCode: string;
}

export interface ServiceLayerSettings {
  enabled: boolean;
  baseUrl: string;
  companyDb: string;
  userName: string;
  password: string;
  passwordSet: boolean;
  allowInvalidCertificates: boolean;
  defaultVendor: string;
  itemVendors: ItemVendor[];
}

export interface PlanningSettings {
  defaultSource: DataSource;
  horizonDays: number;
  bucketDays: number;
  hoursPerUnitPerDay: number;
  forecastLookbackDays: number;
  forecastConsumption: 'Forward' | 'None';
  leadTimePriority: LeadTimeLevel[];
  leadTimeMode: LeadTimeMode;
  defaultLeadTimeDays: number;
  useB1Calendar: boolean;
  workingDays: Weekday[];
  nonWorkingDates: NonWorkingDate[];
  resources: ResourceOverride[];
  defaultLevel: PlanningLevel;
  purchaseDocument: PurchaseDocument;
  transferLeadTimeDays: number;
}

export type Weekday = 'Mon' | 'Tue' | 'Wed' | 'Thu' | 'Fri' | 'Sat' | 'Sun';
export interface NonWorkingDate { date: string; reason: string }
export interface ResourceOverride {
  code: string;
  hoursPerDay: number;
  efficiencyPercent: number;
  setupHoursPerOrder: number;
}
export interface ResourceInfo extends ResourceOverride {
  name: string;
  units: number;
  configured: boolean;
}

// ---------- Calendar ----------
export type CalendarKind =
  | 'Demand' | 'Forecast' | 'Supply' | 'PlannedRelease' | 'PlannedDue' | 'Exception' | 'CapacityOverload';
export interface CalendarEvent {
  id: string;
  date: string;
  kind: CalendarKind;
  itemCode: string;
  warehouseCode: string;
  quantity: number;
  label: string;
  ref: string;
  severity: 'info' | 'warn' | 'danger';
}
export interface CalendarResponse {
  from: string;
  to: string;
  horizonStart: string;
  horizonEnd: string;
  truncated: boolean;
  nonWorking: { date: string; reason: string }[];
  events: CalendarEvent[];
}
export interface CalendarQuery {
  from: string;
  to: string;
  kinds?: CalendarKind[];
  item?: string;
  warehouse?: string;
}

export interface AiSettings {
  enabled: boolean;
  /** Only ever sent TO the server. A GET returns "" and apiKeySet says whether one is stored. */
  apiKey: string;
  apiKeySet: boolean;
  model: string;
  maxTokens: number;
}

export interface AppSettings {
  cobaltDb: DbSettings;
  b1Db: DbSettings;
  serviceLayer: ServiceLayerSettings;
  planning: PlanningSettings;
  ai: AiSettings;
}

export interface AiStatus { enabled: boolean; configured: boolean; model: string }
export interface AiToolUse { name: string; args: string }
export interface AiUsage { promptTokens: number; completionTokens: number; totalTokens: number }
export interface AiTurn { role: 'user' | 'assistant'; content: string }
export interface AiAskResponse { answer: string; toolCalls: AiToolUse[]; usage: AiUsage }
export interface AiSummaryResponse { summary: string; toolCalls: AiToolUse[]; usage: AiUsage }
export interface AiModelsResult { ok: boolean; message: string; models: string[] }

export interface OkResult {
  ok: boolean;
  message: string;
}

export interface StatusInfo {
  version: string;
  storage: 'sql' | 'memory';
  b1Configured: boolean;
  writebackEnabled: boolean;
  defaultSource: DataSource;
  horizonDays: number;
  bucketDays: number;
}

// ---------- Run criteria / wizard ----------
export type LeadTimeLevel = 'Supplier' | 'Item' | 'ItemGroup';
export type LeadTimeMode = 'FirstAvailable' | 'Longest';
export type Procurement = 'Both' | 'Buy' | 'Make';
export type PlanningMethod = 'Mrp' | 'ReorderPoint' | 'MinMax';
export type PlanningLevel = 'Warehouse' | 'Company';
export type PurchaseDocument = 'Order' | 'Request';

export interface RunCriteria {
  source: DataSource;
  level: PlanningLevel;
  purchasing: { document: PurchaseDocument };
  /** Warehouse level only; sourceWarehouses empty = every selected warehouse. */
  transfers: { enabled: boolean; sourceWarehouses: string[]; leadTimeDays: number };
  scope: {
    warehouses: string[]; itemGroups: string[]; suppliers: string[]; itemSearch: string; itemCodes: string[];
    procurement: Procurement; planningMethods: PlanningMethod[]; includeComponents: boolean;
  };
  horizon: { startDate: string | null; days: number; bucketDays: number };
  demand: {
    salesOrders: boolean; salesOrderFrom: string | null; salesOrderTo: string | null;
    forecasts: boolean; forecastConsumption: 'Forward' | 'None'; forecastLookbackDays: number;
    productionComponents: boolean; pastDue: 'FoldToStart' | 'Ignore'; safetyStock: boolean;
  };
  supply: { onHand: boolean; openPurchaseOrders: boolean; openProductionOrders: boolean };
  leadTime: { priority: LeadTimeLevel[]; mode: LeadTimeMode; defaultDays: number };
  lotSizing: { applyMinOrder: boolean; applyMultiples: boolean };
  calendar: { useWorkingCalendar: boolean };
  capacity: { enabled: boolean };
}

export interface RunPreview {
  items: { total: number; inScope: number; buy: number; make: number; noSupplier: number; noLeadTime: number };
  demand: { salesOrderLines: number; salesOrderQuantity: number; pastDueLines: number; forecastLines: number; productionComponentLines: number };
  supply: { purchaseOrderLines: number; productionOrderLines: number; stockRows: number };
  leadTime: { fromSupplier: number; fromItem: number; fromItemGroup: number; defaulted: number };
  warnings: string[];
  planning?: PreviewPlanning;
}
export interface PreviewPlanning {
  level: PlanningLevel; warehouses: string[]; purchaseDocument: PurchaseDocument;
  transfersEnabled: boolean; transferSources: string[]; transferLeadTimeDays: number;
}

// ---------- Review step drill-down (docs/preview-details-contract.md) ----------
export type PreviewMetric =
  | 'items.total' | 'items.inScope' | 'items.buy' | 'items.make' | 'items.noSupplier' | 'items.noLeadTime'
  | 'demand.salesOrders' | 'demand.pastDue' | 'demand.forecasts' | 'demand.productionComponents'
  | 'supply.purchaseOrders' | 'supply.productionOrders' | 'supply.stock'
  | 'leadTime.supplier' | 'leadTime.item' | 'leadTime.itemGroup' | 'leadTime.defaulted';
export type DetailsColumnType = 'text' | 'number' | 'date' | 'flag' | 'badge';
export interface DetailsColumn { key: string; label: string; type: DetailsColumnType }
export type DetailsCell = string | number | boolean | null;
export type DetailsRow = Record<string, DetailsCell>;
export interface PreviewDetailsRequest { criteria: RunCriteria; metric: PreviewMetric; search?: string; take?: number }
export interface PreviewDetails {
  metric: PreviewMetric; title: string; description: string;
  total: number; matched: number; shown: number; truncated: boolean;
  summary: { label: string; value: string }[];
  columns: DetailsColumn[]; rows: DetailsRow[];
}

export interface ItemGroupInfo { code: string; name: string; leadTimeDays: number | null; itemCount: number }
export interface SupplierLookup { cardCode: string; cardName: string }

// ---------- Master data ----------
export type ItemPlanningMethod = 'Mrp' | 'ReorderPoint' | 'MinMax' | 'None';
export type LotSizingRule = 'LotForLot' | 'FixedQuantity' | 'MinMaxMultiple' | 'PeriodOfSupply';

export interface MasterB1Values {
  planningMethod: ItemPlanningMethod; lotSizing: LotSizingRule;
  leadTimeDays: number; groupLeadTimeDays: number;
  minOrderQty: number; orderMultiple: number; safetyStock: number; reorderPoint: number; maxStockLevel: number;
  fixedLotSize: number; periodOfSupplyDays: number;
  supplier: string | null; supplierName: string | null;
}
export interface MasterOverride {
  planningMethod: ItemPlanningMethod | null; lotSizing: LotSizingRule | null; leadTimeDays: number | null;
  minOrderQty: number | null; orderMultiple: number | null; safetyStock: number | null; reorderPoint: number | null;
  maxStockLevel: number | null; fixedLotSize: number | null; periodOfSupplyDays: number | null;
  supplier: string | null; notes: string;
  updatedBy?: string; updatedAt?: string;
}
export type OverridableField = Exclude<keyof MasterOverride, 'notes' | 'updatedBy' | 'updatedAt'>;
export interface MasterItemRow {
  itemCode: string; itemName: string; groupCode: string; groupName: string; procurement: 'Buy' | 'Make';
  b1: MasterB1Values;
  override: MasterOverride | null;
  effective: MasterB1Values;
  leadTime: { days: number | null; source: LeadTimeSource; supplierDays: number | null; itemDays: number | null; groupDays: number | null; supplierItemCount: number };
  overriddenFields: string[];
}
export interface MasterItemQuery {
  source: DataSource; search?: string; group?: string; supplier?: string; procurement?: '' | 'Buy' | 'Make';
  filter?: 'all' | 'overridden' | 'noSupplier' | 'noLeadTime';
  sort?: 'itemCode' | 'itemName' | 'leadTime'; dir?: 'asc' | 'desc'; page?: number; pageSize?: number;
}
export interface MasterItemPage { total: number; page: number; pageSize: number; items: MasterItemRow[] }
export interface BulkItemRequest { itemCodes: string[]; set: Partial<Record<OverridableField, number | string>>; clear: OverridableField[] }

export interface MasterSupplierRow {
  cardCode: string; cardName: string; leadTimeDays: number | null; notes: string; itemCount: number;
  updatedBy?: string; updatedAt?: string;
}
export interface MasterSupplierQuery {
  source: DataSource; search?: string; filter?: 'all' | 'withLeadTime' | 'withoutLeadTime' | 'inUse'; page?: number; pageSize?: number;
}
export interface MasterSupplierPage { total: number; page: number; pageSize: number; suppliers: MasterSupplierRow[] }

// ----- log of writes to SAP B1 -----
export type SapPushAction =
  | 'CreatePurchaseOrder' | 'CreatePurchaseRequest' | 'CreateTransferRequest' | 'CreateProductionOrder'
  | 'MoveDueDate' | 'CloseDocument' | 'Session';
export interface SapLogEntry {
  id: string; at: string; userName: string; action: SapPushAction; success: boolean;
  runId?: string | null; plannedOrderId?: string | null; exceptionId?: string | null;
  itemCode?: string | null; warehouseCode?: string | null; quantity?: number | null;
  target?: string | null; summary: string; error?: string | null;
}
export interface SapLogQuery {
  success?: boolean; action?: SapPushAction | ''; item?: string; user?: string; from?: string; to?: string; limit?: number;
}

// ----- supplier lead-time import -----
export interface SupplierImportRow { line: number; cardCode: string; leadTimeDays: string; notes: string }
export interface SupplierImportResultRow { line: number; cardCode: string; status: 'change' | 'same' | 'error'; message: string; before: number | null; after: number | null }
export interface SupplierImportResponse { dryRun: boolean; applied: number; willChange: number; same: number; errors: number; results: SupplierImportResultRow[] }
