namespace CobaltMrp.Domain;

/// <summary>
/// The complete set of planning inputs for one MRP run, frozen at run time.
/// </summary>
public sealed class PlanningDataSet
{
    public IReadOnlyList<PlanItem> Items { get; init; } = Array.Empty<PlanItem>();
    public IReadOnlyList<WarehouseStock> Stock { get; init; } = Array.Empty<WarehouseStock>();
    public IReadOnlyList<BomComponent> Bom { get; init; } = Array.Empty<BomComponent>();
    public IReadOnlyList<RoutingOperation> Routings { get; init; } = Array.Empty<RoutingOperation>();
    public IReadOnlyList<WorkCenter> WorkCenters { get; init; } = Array.Empty<WorkCenter>();
    public IReadOnlyList<SupplyOrder> Supply { get; init; } = Array.Empty<SupplyOrder>();
    public IReadOnlyList<DemandOrder> Demand { get; init; } = Array.Empty<DemandOrder>();
    public IReadOnlyList<Forecast> Forecasts { get; init; } = Array.Empty<Forecast>();
    /// <summary>The source system's working calendar, or null when it has none (every day is then a working day).</summary>
    public WorkCalendar? Calendar { get; init; }
    /// <summary>Warehouses SAP B1 marks as Drop Ship (OWHS.DropShip = 'Y'): only usable on sales orders, never as the source or target of a transfer.</summary>
    public IReadOnlyCollection<string> DropShipWarehouses { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Reads planning inputs from a source (SAP B1 SQL, or a mock). The engine depends only
/// on this abstraction, so the same run logic works against real B1 and local sample data.
/// </summary>
public interface IB1DataProvider
{
    /// <summary>A short identifier for the source (e.g. "mock", "b1-sql").</summary>
    string SourceName { get; }

    /// <summary>Load the full planning dataset for the given warehouses (empty = all).</summary>
    Task<PlanningDataSet> LoadAsync(IReadOnlyCollection<string> warehouses, CancellationToken ct = default);
}
