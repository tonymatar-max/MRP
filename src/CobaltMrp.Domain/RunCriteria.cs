using System.Text.Json;

namespace CobaltMrp.Domain;

public enum ProcurementScope { Both = 0, Buy = 1, Make = 2 }
public enum ForecastConsumptionMode { Forward = 0, None = 1 }
public enum PastDueMode { FoldToStart = 0, Ignore = 1 }
/// <summary>Where a lead time can come from. SAP B1 holds the item and item-group ones; Cobalt holds the supplier one.</summary>
public enum LeadTimeSourceKind { Supplier = 0, Item = 1, ItemGroup = 2 }
public enum LeadTimeMode { FirstAvailable = 0, Longest = 1 }
/// <summary>Plan each warehouse separately (with optional transfers) or pool all selected warehouses into one company-wide plan.</summary>
public enum PlanningLevel { Warehouse = 0, Company = 1 }
public enum PurchaseDocument { Order = 0, Request = 1 }

public sealed class ScopeCriteria
{
    /// <summary>Empty = all warehouses.</summary>
    public List<string> Warehouses { get; set; } = new();
    /// <summary>SAP B1 item-group codes. Empty = all.</summary>
    public List<string> ItemGroups { get; set; } = new();
    /// <summary>Supplier CardCodes: only items whose effective supplier is listed. Empty = all.</summary>
    public List<string> Suppliers { get; set; } = new();
    /// <summary>Contains-match on item code or name.</summary>
    public string ItemSearch { get; set; } = "";
    public List<string> ItemCodes { get; set; } = new();
    public ProcurementScope Procurement { get; set; } = ProcurementScope.Both;
    public List<PlanningMethod> PlanningMethods { get; set; } = new();
    /// <summary>Also plan every component below the selected make items.</summary>
    public bool IncludeComponents { get; set; } = true;
}

public sealed class HorizonCriteria
{
    /// <summary>Null = today.</summary>
    public DateOnly? StartDate { get; set; }
    public int Days { get; set; } = 180;
    public int BucketDays { get; set; } = 7;
}

public sealed class DemandCriteria
{
    public bool SalesOrders { get; set; } = true;
    public DateOnly? SalesOrderFrom { get; set; }
    public DateOnly? SalesOrderTo { get; set; }
    public bool Forecasts { get; set; } = true;
    public ForecastConsumptionMode ForecastConsumption { get; set; } = ForecastConsumptionMode.Forward;
    public int ForecastLookbackDays { get; set; }
    /// <summary>Components still to be issued to open production orders.</summary>
    public bool ProductionComponents { get; set; } = true;
    public PastDueMode PastDue { get; set; } = PastDueMode.FoldToStart;
    public bool SafetyStock { get; set; } = true;
}

public sealed class SupplyCriteria
{
    public bool OnHand { get; set; } = true;
    public bool OpenPurchaseOrders { get; set; } = true;
    public bool OpenProductionOrders { get; set; } = true;
}

public sealed class LeadTimeCriteria
{
    /// <summary>Which lead time wins, in order. Make items skip <see cref="LeadTimeSourceKind.Supplier"/>.</summary>
    public List<LeadTimeSourceKind> Priority { get; set; } =
        new() { LeadTimeSourceKind.Supplier, LeadTimeSourceKind.Item, LeadTimeSourceKind.ItemGroup };
    public LeadTimeMode Mode { get; set; } = LeadTimeMode.FirstAvailable;
    /// <summary>Used when none of the sources has a value.</summary>
    public int DefaultDays { get; set; }
}

public sealed class LotSizingCriteria
{
    public bool ApplyMinOrder { get; set; } = true;
    public bool ApplyMultiples { get; set; } = true;
}

public sealed class PurchasingCriteria
{
    /// <summary>What a purchased item produces: purchase orders or purchase requests.</summary>
    public PurchaseDocument Document { get; set; } = PurchaseDocument.Order;
}

public sealed class TransferCriteria
{
    /// <summary>Warehouse level only: cover a shortage with a transfer from another warehouse that has the quantity.</summary>
    public bool Enabled { get; set; }
    /// <summary>Warehouses that may supply a transfer. Empty = every selected warehouse.</summary>
    public List<string> SourceWarehouses { get; set; } = new();
    /// <summary>Days a transfer takes: the request must go out this many days before the stock is needed.</summary>
    public int LeadTimeDays { get; set; } = 1;
}

public sealed class CalendarCriteria { public bool UseWorkingCalendar { get; set; } = true; }
public sealed class CapacityCriteria { public bool Enabled { get; set; } = true; }

/// <summary>Everything the Run MRP wizard can choose. Stored with every run so it can be audited and re-run.</summary>
public sealed class RunCriteria
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>"mock" or "b1-sql".</summary>
    public string Source { get; set; } = "mock";
    public ScopeCriteria Scope { get; set; } = new();
    public HorizonCriteria Horizon { get; set; } = new();
    public DemandCriteria Demand { get; set; } = new();
    public SupplyCriteria Supply { get; set; } = new();
    public LeadTimeCriteria LeadTime { get; set; } = new();
    public LotSizingCriteria LotSizing { get; set; } = new();
    public CalendarCriteria Calendar { get; set; } = new();
    public CapacityCriteria Capacity { get; set; } = new();
    public PlanningLevel Level { get; set; } = PlanningLevel.Warehouse;
    public PurchasingCriteria Purchasing { get; set; } = new();
    public TransferCriteria Transfers { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RunCriteria? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RunCriteria>(json, Json); }
        catch (JsonException) { return null; }
    }

    public RunCriteria Clone() => FromJson(ToJson())!;

    /// <summary>A user-facing message when the criteria are unusable, otherwise null. Also normalises small things.</summary>
    public string? Validate()
    {
        if (Source is not ("mock" or "b1-sql")) return "Source must be 'mock' or 'b1-sql'.";
        if (Horizon.Days is < 1 or > 1095) return "Horizon must be between 1 and 1095 days.";
        if (Horizon.BucketDays is < 1 or > 90) return "Bucket size must be between 1 and 90 days.";
        if (Demand.ForecastLookbackDays < 0) return "Forecast lookback cannot be negative.";
        if (LeadTime.DefaultDays < 0) return "Default lead time cannot be negative.";
        if (Demand.SalesOrderFrom is { } f && Demand.SalesOrderTo is { } t && t < f)
            return "The sales order 'to' date is before the 'from' date.";
        foreach (var g in Scope.ItemGroups)
            if (!int.TryParse(g, out _)) return $"Item group '{g}' is not a valid SAP B1 group code.";
        if (Transfers.LeadTimeDays is < 0 or > 90) return "Transfer time must be between 0 and 90 days.";

        // The three lead-time sources can each appear once; missing ones are appended in the default order.
        var seen = LeadTime.Priority.Distinct().ToList();
        foreach (var k in Enum.GetValues<LeadTimeSourceKind>()) if (!seen.Contains(k)) seen.Add(k);
        LeadTime.Priority = seen;

        Scope.Warehouses = Clean(Scope.Warehouses);
        Scope.ItemGroups = Clean(Scope.ItemGroups);
        Scope.Suppliers = Clean(Scope.Suppliers);
        Scope.ItemCodes = Clean(Scope.ItemCodes);
        Scope.ItemSearch = (Scope.ItemSearch ?? "").Trim();
        Transfers.SourceWarehouses = Clean(Transfers.SourceWarehouses);
        // Company level pools the warehouses, so there is nothing to transfer between.
        if (Level == PlanningLevel.Company) Transfers.Enabled = false;
        if (Transfers.Enabled && Scope.Warehouses.Count > 0)
        {
            var outside = Transfers.SourceWarehouses.Where(w => !Scope.Warehouses.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
            if (outside.Count > 0) return $"Transfer source warehouse(s) {string.Join(", ", outside)} are not among the selected warehouses.";
            if (Scope.Warehouses.Count < 2) return "Transfers need at least two warehouses.";
        }
        Scope.PlanningMethods = Scope.PlanningMethods.Distinct().ToList();
        return null;
    }

    private static List<string> Clean(IEnumerable<string>? xs) =>
        (xs ?? Array.Empty<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
