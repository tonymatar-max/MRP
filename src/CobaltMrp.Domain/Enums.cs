namespace CobaltMrp.Domain;

/// <summary>How an item is planned during an MRP run.</summary>
public enum PlanningMethod
{
    /// <summary>Netted against time-phased demand (regenerative MRP).</summary>
    Mrp = 0,
    /// <summary>Replenished when projected on-hand drops to the reorder point.</summary>
    ReorderPoint = 1,
    /// <summary>Kept between a min and max level.</summary>
    MinMax = 2,
    /// <summary>Not planned (e.g. phantom handled via BOM, or planning disabled).</summary>
    None = 3,
}

/// <summary>How the required quantity for a planned order is sized.</summary>
public enum LotSizingRule
{
    /// <summary>One order exactly covering each bucket's net requirement.</summary>
    LotForLot = 0,
    /// <summary>Always order a fixed quantity (may leave surplus).</summary>
    FixedQuantity = 1,
    /// <summary>Round up to an order multiple, respecting min order qty.</summary>
    MinMaxMultiple = 2,
    /// <summary>Accumulate demand over N days into a single order.</summary>
    PeriodOfSupply = 3,
}

/// <summary>Whether an item is bought or made.</summary>
public enum ProcurementType
{
    Buy = 0,
    Make = 1,
}

/// <summary>The kind of planned order the engine recommends.</summary>
public enum PlannedOrderType
{
    PurchaseOrder = 0,
    ProductionOrder = 1,
    /// <summary>A request to move stock from another warehouse (SAP B1 inventory transfer request).</summary>
    TransferRequest = 2,
    /// <summary>A request for the buyers to purchase (SAP B1 purchase request) instead of a purchase order.</summary>
    PurchaseRequest = 3,
}

/// <summary>Where a demand quantity originates.</summary>
public enum DemandSource
{
    SalesOrder = 0,
    Forecast = 1,
    SafetyStock = 2,
    DependentDemand = 3,
    ReorderPoint = 4,
    /// <summary>Stock leaving a warehouse on a planned transfer to another warehouse.</summary>
    TransferOut = 5,
}

/// <summary>Where a supply quantity originates.</summary>
public enum SupplyType
{
    OnHand = 0,
    PurchaseOrder = 1,
    ProductionOrder = 2,
    PlannedOrder = 3,
    /// <summary>Stock arriving in a warehouse on a planned transfer from another warehouse.</summary>
    TransferIn = 4,
}

/// <summary>Planner action messages produced by a run.</summary>
public enum ExceptionType
{
    /// <summary>An existing supply order should be pulled in (arrives too late).</summary>
    Expedite = 0,
    /// <summary>An existing supply order should be pushed out (arrives too early).</summary>
    Defer = 1,
    /// <summary>An existing supply order is no longer needed.</summary>
    Cancel = 2,
    /// <summary>Demand or supply is already in the past.</summary>
    PastDue = 3,
    /// <summary>Lead time pushes the order start into the past; cannot be met on time.</summary>
    ReleaseInPast = 4,
    /// <summary>A work center is loaded beyond capacity in a bucket.</summary>
    CapacityOverload = 5,
    /// <summary>Demand or supply exists for an item that is not part of the plan (inactive, frozen, non-inventory).</summary>
    NotPlanned = 6,
}
