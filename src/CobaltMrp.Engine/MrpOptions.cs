using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>Parameters that control a single MRP run.</summary>
public sealed class MrpOptions
{
    /// <summary>"Plan as of" date; defaults to today.</summary>
    public DateOnly HorizonStart { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public int HorizonDays { get; init; } = 180;
    public int BucketDays { get; init; } = 1;

    /// <summary>Restrict to these warehouses; empty = all found in the dataset.</summary>
    public IReadOnlyCollection<string> Warehouses { get; init; } = Array.Empty<string>();

    /// <summary>Include demand forecasts as independent demand.</summary>
    public bool UseForecasts { get; init; } = true;

    /// <summary>Net forecasts against real sales orders in the same period (forward consumption), so demand is not counted twice.</summary>
    public bool ConsumeForecast { get; init; } = true;

    /// <summary>Forecast periods that started before the plan start are history, not future demand, and
    /// are ignored. Raise this (e.g. 31) to keep a current period whose start date has just passed.</summary>
    public int ForecastLookbackDays { get; init; } = 0;

    /// <summary>Run capacity requirements planning after material planning.</summary>
    public bool RunCapacityPlanning { get; init; } = true;

    /// <summary>When set, only these items' planned orders (and their pegging, exceptions and capacity) are kept. Demand still
    /// flows through the whole BOM, so a component is planned correctly even when only it is selected.</summary>
    public IReadOnlySet<string>? ItemScope { get; init; }

    /// <summary>Pool every warehouse into one company-wide plan (no transfers); each order is received in the item's default warehouse.</summary>
    public bool CompanyLevel { get; init; }
    /// <summary>What purchased items produce.</summary>
    public PurchaseDocument PurchaseDocument { get; init; } = PurchaseDocument.Order;
    /// <summary>Warehouse level: cover shortages with transfers from other warehouses that have the quantity.</summary>
    public bool TransferEnabled { get; init; }
    /// <summary>Warehouses that may supply a transfer. Empty = every planned warehouse.</summary>
    public IReadOnlyCollection<string> TransferSources { get; init; } = Array.Empty<string>();
    public int TransferLeadDays { get; init; } = 1;

    public string RunBy { get; init; } = "system";
}
