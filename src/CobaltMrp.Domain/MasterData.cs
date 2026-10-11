namespace CobaltMrp.Domain;

/// <summary>An item as SAP B1 holds it, with its group, for the Master data screen.</summary>
public sealed record MasterItem(PlanItem Plan, string? GroupCode, string? GroupName);

public sealed record MasterSupplier(string CardCode, string? CardName);

public sealed record ItemGroupInfo(string Code, string Name, int? LeadTimeDays, int ItemCount);

/// <summary>The slice of SAP B1 master data that planning cares about.</summary>
public sealed class MasterData
{
    public IReadOnlyList<MasterItem> Items { get; init; } = Array.Empty<MasterItem>();
    public IReadOnlyList<MasterSupplier> Suppliers { get; init; } = Array.Empty<MasterSupplier>();
    public IReadOnlyList<ItemGroupInfo> Groups { get; init; } = Array.Empty<ItemGroupInfo>();
}

/// <summary>Reads master data cheaply (items, groups, suppliers only; no transactions).</summary>
public interface IMasterDataProvider
{
    Task<MasterData> LoadMasterAsync(CancellationToken ct = default);
}

/// <summary>
/// Planning data a planner has changed in Cobalt MRP for one item. A null field means "not overridden":
/// SAP B1's value is used. Overrides never change SAP B1.
/// </summary>
public sealed class ItemOverride
{
    public string ItemCode { get; set; } = "";
    public PlanningMethod? PlanningMethod { get; set; }
    public LotSizingRule? LotSizing { get; set; }
    public int? LeadTimeDays { get; set; }
    public decimal? MinOrderQty { get; set; }
    public decimal? OrderMultiple { get; set; }
    public decimal? SafetyStock { get; set; }
    public decimal? ReorderPoint { get; set; }
    public decimal? MaxStockLevel { get; set; }
    public decimal? FixedLotSize { get; set; }
    public int? PeriodOfSupplyDays { get; set; }
    /// <summary>Default supplier (CardCode) for this item.</summary>
    public string? Supplier { get; set; }
    public string Notes { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when no field is overridden (the row can be deleted).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        PlanningMethod is null && LotSizing is null && LeadTimeDays is null && MinOrderQty is null &&
        OrderMultiple is null && SafetyStock is null && ReorderPoint is null && MaxStockLevel is null &&
        FixedLotSize is null && PeriodOfSupplyDays is null && string.IsNullOrWhiteSpace(Supplier);

    public IReadOnlyList<string> OverriddenFields()
    {
        var f = new List<string>();
        if (PlanningMethod is not null) f.Add("planningMethod");
        if (LotSizing is not null) f.Add("lotSizing");
        if (LeadTimeDays is not null) f.Add("leadTimeDays");
        if (MinOrderQty is not null) f.Add("minOrderQty");
        if (OrderMultiple is not null) f.Add("orderMultiple");
        if (SafetyStock is not null) f.Add("safetyStock");
        if (ReorderPoint is not null) f.Add("reorderPoint");
        if (MaxStockLevel is not null) f.Add("maxStockLevel");
        if (FixedLotSize is not null) f.Add("fixedLotSize");
        if (PeriodOfSupplyDays is not null) f.Add("periodOfSupplyDays");
        if (!string.IsNullOrWhiteSpace(Supplier)) f.Add("supplier");
        return f;
    }

    public ItemOverride Clone() => (ItemOverride)MemberwiseClone();
}

/// <summary>Per-supplier planning data. SAP B1 has no supplier lead time, so it lives here.</summary>
public sealed class SupplierSetting
{
    public string CardCode { get; set; } = "";
    public int? LeadTimeDays { get; set; }
    public string Notes { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => LeadTimeDays is null && string.IsNullOrWhiteSpace(Notes);
    public SupplierSetting Clone() => (SupplierSetting)MemberwiseClone();
}
