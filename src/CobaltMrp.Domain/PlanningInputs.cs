namespace CobaltMrp.Domain;

/// <summary>
/// Immutable planning inputs read from SAP B1 (or a mock) and frozen for one MRP run.
/// These mirror the relevant columns of B1 master/transaction tables but are decoupled
/// from B1 so the engine never depends on the source.
/// </summary>
public sealed record PlanItem
{
    public required string ItemCode { get; set; }
    public string? ItemName { get; set; }
    public ProcurementType Procurement { get; set; }
    public PlanningMethod PlanningMethod { get; set; } = PlanningMethod.Mrp;
    public LotSizingRule LotSizing { get; set; } = LotSizingRule.LotForLot;

    /// <summary>Lead time in calendar days (purchase lead time, or production lead time).</summary>
    public int LeadTimeDays { get; set; }

    public decimal SafetyStock { get; set; }
    public decimal ReorderPoint { get; set; }
    /// <summary>SAP B1 item group code (OITM.ItmsGrpCod), as text.</summary>
    public string? GroupCode { get; set; }
    /// <summary>Lead time defined on the item group (OITB.LeadTime); a source of lead time alongside the item's own.</summary>
    public int? GroupLeadTimeDays { get; set; }
    /// <summary>Where <see cref="LeadTimeDays"/> came from after the lead-time rule ran: Supplier, Item, ItemGroup, Default or None.</summary>
    public string? LeadTimeSource { get; set; }
    /// <summary>Where <see cref="PreferredVendor"/> came from: "b1-preferred" or "master-data".</summary>
    public string? SupplierSource { get; set; }

    /// <summary>Preferred supplier CardCode from the item master (SAP B1: OITM.CardCode). Used for purchase orders.</summary>
    public string? PreferredVendor { get; set; }

    /// <summary>Default/home warehouse, used when an item has no stock, demand or supply anywhere.</summary>
    public string? HomeWarehouse { get; set; }

    public decimal MinOrderQty { get; set; }
    public decimal MaxOrderQty { get; set; }
    /// <summary>Maximum stock level for min/max planning. Falls back to <see cref="MaxOrderQty"/> when 0.</summary>
    public decimal MaxStockLevel { get; set; }
    public decimal OrderMultiple { get; set; }
    public decimal FixedLotSize { get; set; }
    public int PeriodOfSupplyDays { get; set; }

    /// <summary>Low-level code, computed by the engine from the BOM graph (0 = top level).</summary>
    public int LowLevelCode { get; set; }
}

/// <summary>Per-warehouse stock position for an item.</summary>
public sealed record WarehouseStock
{
    public required string ItemCode { get; init; }
    public required string WarehouseCode { get; init; }
    public decimal OnHand { get; init; }
    public decimal Committed { get; init; }
    public decimal Ordered { get; init; }
    public bool PlanningEnabled { get; init; } = true;
}

/// <summary>A bill-of-materials component line (parent consumes child).</summary>
public sealed record BomComponent
{
    public required string ParentItemCode { get; init; }
    public required string ComponentItemCode { get; init; }
    public decimal QuantityPer { get; init; }
    /// <summary>Scrap/yield loss as a fraction (0.05 = 5% extra consumed).</summary>
    public decimal ScrapFactor { get; init; }
    public string? WarehouseCode { get; init; }
}

/// <summary>A routing operation that loads a work center (for CRP).</summary>
public sealed record RoutingOperation
{
    public required string ItemCode { get; init; }
    public int Sequence { get; init; }
    public required string WorkCenterCode { get; init; }
    /// <summary>Hours required per unit produced.</summary>
    public decimal HoursPerUnit { get; init; }
    /// <summary>Fixed setup hours per order, independent of quantity.</summary>
    public decimal SetupHours { get; init; }
}

/// <summary>A work center and its daily available capacity.</summary>
public sealed record WorkCenter
{
    public required string Code { get; init; }
    public string? Name { get; init; }
    /// <summary>Number of identical units (machines/people) the resource represents.</summary>
    public decimal Units { get; init; } = 1m;
    public decimal DailyCapacityHours { get; init; }
    public decimal EfficiencyFactor { get; init; } = 1m;
}

/// <summary>An existing scheduled receipt: open purchase or production order.</summary>
public sealed record SupplyOrder
{
    public required string ItemCode { get; init; }
    public required string WarehouseCode { get; init; }
    public SupplyType Type { get; init; }
    public decimal Quantity { get; init; }
    public DateOnly DueDate { get; init; }
    public string? SourceDocKey { get; init; }
    /// <summary>Document line (purchase order line number), so a change can target exactly this line.</summary>
    public int? SourceLine { get; init; }
}

/// <summary>An existing independent demand: open sales order line.</summary>
public sealed record DemandOrder
{
    public required string ItemCode { get; init; }
    public required string WarehouseCode { get; init; }
    public DemandSource Source { get; init; }
    public decimal Quantity { get; init; }
    public DateOnly DueDate { get; init; }
    public string? SourceDocKey { get; init; }
}

/// <summary>A demand forecast quantity for an item in a period.</summary>
public sealed record Forecast
{
    public required string ItemCode { get; init; }
    public required string WarehouseCode { get; init; }
    public DateOnly PeriodStart { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>Quantity already consumed by actual sales orders.</summary>
    public decimal Consumed { get; init; }
}
