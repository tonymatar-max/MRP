namespace CobaltMrp.Domain;

/// <summary>A saved MRP run — a frozen snapshot plus its computed results.</summary>
public sealed class MrpRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset RunAt { get; init; } = DateTimeOffset.UtcNow;
    public string RunBy { get; init; } = "system";

    /// <summary>Planning horizon start (today, or a chosen "plan as of" date).</summary>
    public DateOnly HorizonStart { get; init; }
    public DateOnly HorizonEnd { get; init; }
    public int BucketDays { get; init; } = 1;

    public List<PlannedOrder> PlannedOrders { get; } = new();
    public List<PeggingLink> Pegging { get; } = new();
    public List<ExceptionMessage> Exceptions { get; } = new();
    public List<CapacityLoad> CapacityLoads { get; } = new();
    /// <summary>Everything dated in the plan horizon, for the Calendar screen.</summary>
    public List<CalendarEvent> Events { get; } = new();

    /// <summary>A short name the planner gave the run, so runs started in the same minute are easy to tell apart.</summary>
    public string? Name { get; set; }
    public string? Notes { get; set; }
    /// <summary>The wizard criteria this run used (JSON), so it can be audited and re-run.</summary>
    public string? CriteriaJson { get; set; }
}

/// <summary>A recommended purchase or production order for one item.</summary>
public sealed class PlannedOrder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ItemCode { get; init; }
    /// <summary>The warehouse the order is for; for a transfer, the warehouse that RECEIVES the stock.</summary>
    public required string WarehouseCode { get; set; }
    /// <summary>For a transfer request: the warehouse the stock leaves.</summary>
    public string? FromWarehouse { get; set; }
    public PlannedOrderType Type { get; init; }
    public decimal Quantity { get; set; }

    /// <summary>When the order must be received to cover demand.</summary>
    public DateOnly DueDate { get; init; }
    /// <summary>DueDate minus lead time — when the order must be released/started.</summary>
    public DateOnly ReleaseDate { get; init; }

    public int LowLevelCode { get; init; }
    /// <summary>Preferred supplier from the item master at planning time (purchase orders only).</summary>
    public string? VendorCode { get; init; }
    /// <summary>"b1-preferred" or "master-data" (which source supplied <see cref="VendorCode"/>).</summary>
    public string? VendorSource { get; init; }
    /// <summary>The lead time that was applied, and which source supplied it.</summary>
    public int LeadTimeDays { get; init; }
    public string? LeadTimeSource { get; init; }
    public bool Firmed { get; set; }
    /// <summary>Set once pushed to B1 via Service Layer.</summary>
    public string? B1DocEntry { get; set; }
}

/// <summary>Links a planned order (supply) to the demand that caused it (full traceability).</summary>
public sealed class PeggingLink
{
    public Guid PlannedOrderId { get; init; }
    public DemandSource DemandSource { get; init; }
    public decimal Quantity { get; init; }
    public DateOnly DemandDueDate { get; init; }
    public string? DemandDocKey { get; init; }
    /// <summary>Parent item when this is dependent demand from a BOM explosion.</summary>
    public string? ParentItemCode { get; init; }
}

/// <summary>A planner action message.</summary>
public sealed class ExceptionMessage
{
    /// <summary>Identifies this exception (the execute endpoints use it).</summary>
    public Guid ExceptionId { get; init; } = Guid.NewGuid();
    public ExceptionType Type { get; init; }
    public required string ItemCode { get; init; }
    public string? WarehouseCode { get; set; }
    public string Message { get; init; } = "";
    public DateOnly? RelatedDate { get; init; }
    /// <summary>An existing SAP B1 document, e.g. "OPOR:4501" or "OWOR:3301" (DocEntry).</summary>
    public string? SourceDocKey { get; init; }
    /// <summary>Line number on the existing purchase order (OPOR), when the exception is about one line.</summary>
    public int? SourceLine { get; init; }
    /// <summary>The planned order this exception relates to, when there is one.</summary>
    public Guid? PlannedOrderId { get; init; }
    /// <summary>Expedite/Defer: the existing order's due date, and the date the plan wants instead.</summary>
    public DateOnly? CurrentDate { get; init; }
    public DateOnly? ProposedDate { get; init; }
    public decimal Quantity { get; init; }

    // Filled in when a planner executes the exception (kept as an audit trail on the run).
    /// <summary>Null while open; "Done" once executed.</summary>
    public string? ActionStatus { get; set; }
    public string? ActionBy { get; set; }
    public DateTimeOffset? ActionAt { get; set; }
    public string? ActionNote { get; set; }
}

/// <summary>Work-center load vs capacity in one time bucket (CRP output).</summary>
public sealed class CapacityLoad
{
    public required string WorkCenterCode { get; init; }
    public DateOnly BucketStart { get; init; }
    public decimal RequiredHours { get; init; }
    public decimal AvailableHours { get; init; }
    public bool Overloaded => RequiredHours > AvailableHours;
}

/// <summary>What a <see cref="CalendarEvent"/> represents.</summary>
public enum CalendarEventKind
{
    Demand = 0,
    Forecast = 1,
    Supply = 2,
    PlannedRelease = 3,
    PlannedDue = 4,
    Exception = 5,
    CapacityOverload = 6,
    /// <summary>A day the plant is closed (weekend or holiday). Stored with the run so the calendar matches the plan.</summary>
    NonWorking = 7,
}

/// <summary>One dated thing on the planning calendar (a sales order due, an order to release, an overload...).</summary>
public sealed class CalendarEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateOnly Date { get; init; }
    public CalendarEventKind Kind { get; init; }
    public string ItemCode { get; init; } = "";
    public string? WarehouseCode { get; set; }
    public decimal Quantity { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Document key, planned order id, exception type or work center, depending on <see cref="Kind"/>.</summary>
    public string? Ref { get; init; }
    /// <summary>"info", "warn" or "danger".</summary>
    public string Severity { get; init; } = "info";
}
