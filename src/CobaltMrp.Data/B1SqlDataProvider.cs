using System.Data;
using CobaltMrp.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace CobaltMrp.Data;

/// <summary>
/// Reads planning inputs directly from a SAP Business One company database (SQL Server) using
/// Dapper, and maps them into the engine's <see cref="PlanningDataSet"/>.
/// </summary>
/// <remarks>
/// The SQL lives in clearly-labelled <c>const</c> strings so it can be tuned per customer without
/// touching the mapping code. Wherever a real B1 table/column name is uncertain it carries a
/// <c>TODO(verify-b1-schema)</c> comment rather than guessing silently. Verified against B1 10.0 (SBODemoHO). All queries are read-only
/// and parameterised. The same column/table shape is reproduced by <c>sql/mock-b1-schema.sql</c>
/// so this provider can be exercised against a throwaway database.
/// </remarks>
public sealed class B1SqlDataProvider : IB1DataProvider, IMasterDataProvider
{
    private readonly string _connectionString;
    private readonly decimal _hoursPerUnitPerDay;

    /// <summary>Create the provider for a given SQL Server connection string to the B1 company DB.</summary>
    /// <param name="connectionString">SQL Server connection string to the B1 company database.</param>
    /// <param name="hoursPerUnitPerDay">B1 stores no daily capacity for a resource, only a unit count
    /// (ORSC.NumResUnit). Daily capacity is modelled as units x this value (default 8h shift).</param>
    public B1SqlDataProvider(string connectionString, decimal hoursPerUnitPerDay = 8m)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _hoursPerUnitPerDay = hoursPerUnitPerDay;
    }

    /// <inheritdoc />
    public string SourceName => "b1-sql";

    /// <inheritdoc />
    public async Task<PlanningDataSet> LoadAsync(IReadOnlyCollection<string> warehouses, CancellationToken ct = default)
    {
        // An empty filter means "all warehouses". We pass a nullable table-less flag plus a TVP-free
        // approach: when a filter is supplied we post-filter warehouse-scoped rows in memory, which
        // keeps the SQL simple and portable across B1 versions.
        var whSet = warehouses is { Count: > 0 }
            ? new HashSet<string>(warehouses, StringComparer.OrdinalIgnoreCase)
            : null;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var items       = (await conn.QueryAsync<ItemRow>(new CommandDefinition(SqlItems, cancellationToken: ct))).ToList();
        var stock       = (await conn.QueryAsync<StockRow>(new CommandDefinition(SqlStock, cancellationToken: ct))).ToList();
        var warehouseR  = (await conn.QueryAsync<WarehouseRow>(new CommandDefinition(SqlWarehouses, cancellationToken: ct))).ToList();
        var bom         = (await conn.QueryAsync<BomRow>(new CommandDefinition(SqlBom, cancellationToken: ct))).ToList();
        var routings    = (await conn.QueryAsync<RoutingRow>(new CommandDefinition(SqlRoutings, cancellationToken: ct))).ToList();
        var workCenters = (await conn.QueryAsync<WorkCenterRow>(new CommandDefinition(SqlWorkCenters, cancellationToken: ct))).ToList();
        var salesDemand = (await conn.QueryAsync<DemandRow>(new CommandDefinition(SqlSalesDemand, cancellationToken: ct))).ToList();
        var poSupply    = (await conn.QueryAsync<SupplyRow>(new CommandDefinition(SqlPurchaseSupply, cancellationToken: ct))).ToList();
        var woSupply    = (await conn.QueryAsync<SupplyRow>(new CommandDefinition(SqlProductionSupply, cancellationToken: ct))).ToList();
        var components  = (await conn.QueryAsync<DemandRow>(new CommandDefinition(SqlProductionComponents, cancellationToken: ct))).ToList();
        var forecasts   = (await conn.QueryAsync<ForecastRow>(new CommandDefinition(SqlForecasts, cancellationToken: ct))).ToList();
        var calHeader   = (await conn.QueryAsync<CalendarHeaderRow>(new CommandDefinition(SqlCalendarHeader, cancellationToken: ct))).FirstOrDefault();
        var holidays    = (await conn.QueryAsync<HolidayRow>(new CommandDefinition(SqlHolidays, cancellationToken: ct))).ToList();

        bool WhOk(string? wh) => whSet is null || (wh is not null && whSet.Contains(wh));

        _ = warehouseR; // OWHS is read for validation/future warehouse metadata; not projected today.

        return new PlanningDataSet
        {
            Items = items.Select(MapItem).ToList(),
            Stock = stock.Where(s => WhOk(s.WarehouseCode)).Select(MapStock).ToList(),
            Bom = bom.Select(MapBom).ToList(),
            Routings = routings.Select(MapRouting).ToList(),
            WorkCenters = workCenters.Select(MapWorkCenter).ToList(),
            Supply = poSupply.Concat(woSupply).Where(s => WhOk(s.WarehouseCode)).Select(MapSupply).ToList(),
            Demand = salesDemand.Where(d => WhOk(d.WarehouseCode)).Select(MapDemand)
                .Concat(components.Where(d => WhOk(d.WarehouseCode)).Select(MapComponentDemand)).ToList(),
            Forecasts = forecasts.Where(f => WhOk(f.WarehouseCode)).Select(MapForecast).ToList(),
            Calendar = MapCalendar(calHeader, holidays),
        };
    }

    // ---- Mapping (B1 row -> domain record) --------------------------------------------------

    private static PlanItem MapItem(ItemRow r)
    {
        var reorderPoint = r.ReorderPoint ?? 0m;
        var min = r.SafetyStock ?? 0m;
        var max = r.MaxStockLevel ?? 0m;
        var fixedLot = r.FixedLotSize ?? 0m;
        var minOrder = r.MinOrderQty ?? 0m;
        var multiple = r.OrderMultiple ?? 0m;

        // Planning policy is derived from standard B1 fields (no UDFs required):
        //   PlaningSys 'N'                 -> not planned
        //   reorder point set              -> reorder-point planning (fixed reorder qty if given)
        //   min and max level both set     -> min/max planning
        //   otherwise                      -> regenerative MRP with MinLevel as the floor
        var method =
            string.Equals(r.PlanningSys, "N", StringComparison.OrdinalIgnoreCase) ? PlanningMethod.None
            : reorderPoint > 0 ? PlanningMethod.ReorderPoint
            : (min > 0 && max > min) ? PlanningMethod.MinMax
            : PlanningMethod.Mrp;

        var lot =
            method == PlanningMethod.ReorderPoint && fixedLot > 0 ? LotSizingRule.FixedQuantity
            : (minOrder > 0 || multiple > 0) ? LotSizingRule.MinMaxMultiple
            : LotSizingRule.LotForLot;

        return new PlanItem
        {
            ItemCode = r.ItemCode,
            ItemName = r.ItemName,
            // OITM.PrcrmntMtd: 'M' = produced/make, 'B' = bought/buy.
            Procurement = string.Equals(r.ProcurementMethod, "M", StringComparison.OrdinalIgnoreCase)
                ? ProcurementType.Make : ProcurementType.Buy,
            PlanningMethod = method,
            LotSizing = lot,
            LeadTimeDays = r.LeadTime ?? 0,
            SafetyStock = min,
            ReorderPoint = method == PlanningMethod.MinMax ? min : reorderPoint,
            MinOrderQty = minOrder,
            MaxStockLevel = max,
            OrderMultiple = multiple,
            FixedLotSize = fixedLot,
            HomeWarehouse = string.IsNullOrWhiteSpace(r.HomeWarehouse) ? null : r.HomeWarehouse,
            PreferredVendor = string.IsNullOrWhiteSpace(r.PreferredVendor) ? null : r.PreferredVendor.Trim(),
            GroupCode = r.GroupCode?.ToString(),
            GroupLeadTimeDays = r.GroupLeadTime is > 0 ? r.GroupLeadTime : null,
            // TODO(verify-b1-schema): OITM.OrdrIntrvl (ordering interval) could drive period-of-supply.
        };
    }

    /// <summary>
    /// The company calendar: weekend days (OHLD.WndFrm..WndTo, numbered 1 = Sunday ... 7 = Saturday, wrapping, so
    /// 7 to 1 is Saturday and Sunday) plus the holiday dates in HLD1. Null when the company has no calendar.
    /// </summary>
    private static WorkCalendar? MapCalendar(CalendarHeaderRow? h, List<HolidayRow> holidays)
    {
        if (h is null) return null;
        var weekend = new List<DayOfWeek>();
        if (h.WeekendFrom is >= 1 and <= 7 && h.WeekendTo is >= 1 and <= 7)
        {
            var d = h.WeekendFrom.Value;
            for (var i = 0; i < 7; i++)
            {
                weekend.Add((DayOfWeek)(d - 1));          // 1 = Sunday = DayOfWeek.Sunday (0)
                if (d == h.WeekendTo.Value) break;
                d = d % 7 + 1;
            }
        }

        var closures = new Dictionary<DateOnly, string>();
        foreach (var r in holidays)
        {
            var from = DateOnly.FromDateTime(r.StrDate);
            var to = DateOnly.FromDateTime(r.EndDate < r.StrDate ? r.StrDate : r.EndDate);
            for (var day = from; day <= to && day.DayNumber - from.DayNumber < 60; day = day.AddDays(1))
                closures[day] = string.IsNullOrWhiteSpace(r.Rmrks) ? "Holiday" : r.Rmrks.Trim();
        }
        return new WorkCalendar(weekend, closures);
    }

    private static WarehouseStock MapStock(StockRow r) => new()
    {
        ItemCode = r.ItemCode,
        WarehouseCode = r.WarehouseCode,
        OnHand = r.OnHand,
        Committed = r.Committed,
        Ordered = r.Ordered,
        PlanningEnabled = true,
    };

    private static BomComponent MapBom(BomRow r) => new()
    {
        ParentItemCode = r.ParentItemCode,
        ComponentItemCode = r.ComponentItemCode,
        QuantityPer = r.QuantityPer,
        // ITT1 stores wastage as a percentage; convert to a fraction. Null -> 0.
        ScrapFactor = (r.ScrapPercent ?? 0m) / 100m,
        WarehouseCode = r.WarehouseCode,
    };

    private static RoutingOperation MapRouting(RoutingRow r) => new()
    {
        ItemCode = r.ItemCode,
        Sequence = r.Sequence,
        WorkCenterCode = r.WorkCenterCode,
        HoursPerUnit = r.HoursPerUnit,
        SetupHours = r.SetupHours,
    };

    private WorkCenter MapWorkCenter(WorkCenterRow r) => new()
    {
        Code = r.Code,
        Name = r.Name,
        Units = r.Units <= 0 ? 1m : r.Units,
        DailyCapacityHours = (r.Units <= 0 ? 1m : r.Units) * _hoursPerUnitPerDay,
        EfficiencyFactor = 1m,
    };

    /// <summary>Components still to be issued to an open production order are demand on the component.</summary>
    private static DemandOrder MapComponentDemand(DemandRow r) => new()
    {
        ItemCode = r.ItemCode, WarehouseCode = r.WarehouseCode, Source = DemandSource.DependentDemand,
        Quantity = r.OpenQty, DueDate = DateOnly.FromDateTime(r.DueDate), SourceDocKey = r.SourceDocKey,
    };

    private static SupplyOrder MapSupply(SupplyRow r) => new()
    {
        ItemCode = r.ItemCode,
        WarehouseCode = r.WarehouseCode,
        Type = r.IsProduction ? SupplyType.ProductionOrder : SupplyType.PurchaseOrder,
        Quantity = r.OpenQty,
        DueDate = DateOnly.FromDateTime(r.DueDate),
        SourceDocKey = r.SourceDocKey,
        SourceLine = r.LineNum,
    };

    private static DemandOrder MapDemand(DemandRow r) => new()
    {
        ItemCode = r.ItemCode,
        WarehouseCode = r.WarehouseCode,
        Source = DemandSource.SalesOrder,
        Quantity = r.OpenQty,
        DueDate = DateOnly.FromDateTime(r.DueDate),
        SourceDocKey = r.SourceDocKey,
    };

    private static Forecast MapForecast(ForecastRow r) => new()
    {
        ItemCode = r.ItemCode,
        WarehouseCode = r.WarehouseCode,
        PeriodStart = DateOnly.FromDateTime(r.PeriodStart),
        Quantity = r.Quantity,
        Consumed = r.Consumed ?? 0m,
    };

    // ---- SQL (tune per customer) ------------------------------------------------------------

    /// <summary>
    /// Item master. OITM holds master data; make/buy is PrcrmntMtd, MRP flag is PlaningSys.
    /// LeadTime/MinOrder etc. live on OITM. Reorder point and min/max policy are frequently UDFs,
    /// so those columns are read defensively.
    /// </summary>
    private const string SqlItems = @"
SELECT
    T0.ItemCode    AS ItemCode,
    T0.ItemName    AS ItemName,
    T0.PrcrmntMtd  AS ProcurementMethod,   -- 'M' make / 'B' buy
    T0.PlaningSys  AS PlanningSys,          -- 'M' MRP / 'N' none
    T0.LeadTime    AS LeadTime,             -- days
    T0.MinOrdrQty  AS MinOrderQty,
    T0.OrdrMulti   AS OrderMultiple,
    T0.MinLevel    AS SafetyStock,          -- B1 'Required inventory level' (minimum), used as the floor
    T0.MaxLevel    AS MaxStockLevel,        -- B1 maximum inventory level (min/max planning)
    T0.ReorderPnt  AS ReorderPoint,
    T0.ReorderQty  AS FixedLotSize,         -- fixed reorder quantity for reorder-point items
    T0.DfltWH      AS HomeWarehouse,        -- default warehouse; often blank, so the engine falls back sensibly
    T0.CardCode    AS PreferredVendor,      -- OITM.CardCode: the item's preferred supplier
    T0.ItmsGrpCod  AS GroupCode,
    G.LeadTime     AS GroupLeadTime         -- OITB.LeadTime: lead time defined on the item group
FROM OITM T0
LEFT JOIN OITB G ON G.ItmsGrpCod = T0.ItmsGrpCod
WHERE T0.validFor = 'Y' AND T0.InvntItem = 'Y';";

    /// <summary>Per-warehouse stock from OITW (IsCommited/OnOrder/OnHand/Min/Max).</summary>
    private const string SqlStock = @"
SELECT
    T0.ItemCode   AS ItemCode,
    T0.WhsCode    AS WarehouseCode,
    T0.OnHand     AS OnHand,
    T0.IsCommited AS Committed,
    T0.OnOrder    AS Ordered
FROM OITW T0;";

    /// <summary>Warehouse master OWHS (read for validation / future metadata).</summary>
    private const string SqlWarehouses = @"
SELECT
    T0.WhsCode AS Code,
    T0.WhsName AS Name
FROM OWHS T0;";

    /// <summary>
    /// Bill of materials: OITT is the BOM header (one per parent/product), ITT1 the component lines.
    /// Quantity is ITT1.Quantity; Warehouse is ITT1.Warehouse; wastage is ITT1.Wastage (percent).
    /// </summary>
    private const string SqlBom = @"
SELECT
    T0.Code      AS ParentItemCode,
    T1.Code      AS ComponentItemCode,
    -- ITT1.Quantity is per OITT.Qauntity (the BOM base quantity, sic), so normalise to per one unit.
    T1.Quantity / NULLIF(T0.Qauntity, 0) AS QuantityPer,
    CAST(0 AS DECIMAL(19,6)) AS ScrapPercent,   -- B1 BOMs carry no wastage column in this version
    T1.Warehouse AS WarehouseCode
FROM OITT T0
INNER JOIN ITT1 T1 ON T1.Father = T0.Code
WHERE T0.TreeType = 'P'    -- production BOMs ('S' sales / 'T' template are not planned)
  AND T1.Type = 4;         -- 4 = item line; 290 = resource line (see SqlRoutings)
";

    /// <summary>
    /// Routing operations loading work centers.
    /// Verified against B1 10.0 (SBODemoHO): B1 has no routing table. Resources are production-BOM
    /// lines (ITT1.Type = 290) whose Quantity is resource time per OITT.Qauntity units of the parent.
    /// Setup time is not modelled in standard B1. A customer that keeps routings in UDTs should replace
    /// this query.
    /// </summary>
    private const string SqlRoutings = @"
-- B1 has no routing table: resources sit on the production BOM as ITT1 lines of Type 290, and
-- Quantity is the resource time per OITT.Qauntity units of the parent. No setup time is modelled.
SELECT
    T0.Code         AS ItemCode,
    ROW_NUMBER() OVER (PARTITION BY T0.Code ORDER BY T1.ChildNum) * 10 AS Sequence,
    T1.Code         AS WorkCenterCode,
    T1.Quantity / NULLIF(T0.Qauntity, 0) AS HoursPerUnit,
    CAST(0 AS DECIMAL(19,6)) AS SetupHours
FROM OITT T0
INNER JOIN ITT1 T1 ON T1.Father = T0.Code
WHERE T0.TreeType = 'P' AND T1.Type = 290;";

    /// <summary>
    /// Work centers / resources.
    /// Verified against B1 10.0 (SBODemoHO): ORSC is the resource master. B1 stores no daily hours, only
    /// a unit count, so capacity is units x the provider's configurable hours per unit per day.
    /// </summary>
    private const string SqlWorkCenters = @"
-- Resources: ORSC. B1 stores no daily hours, only the number of units (NumResUnit); capacity is
-- units x the provider's hoursPerUnitPerDay (see constructor).
SELECT
    T0.ResCode                  AS Code,
    T0.ResName                  AS Name,
    ISNULL(T0.NumResUnit, 1)    AS Units
FROM ORSC T0;";

    /// <summary>Open sales-order lines -> independent demand. ORDR header + RDR1 lines, OpenQty > 0.</summary>
    private const string SqlSalesDemand = @"
SELECT
    T1.ItemCode                              AS ItemCode,
    T1.WhsCode                               AS WarehouseCode,
    T1.OpenQty                               AS OpenQty,
    ISNULL(T1.ShipDate, T0.DocDueDate)       AS DueDate,  -- line ship date, else header due date
    ('ORDR:' + CAST(T0.DocEntry AS VARCHAR)) AS SourceDocKey
FROM ORDR T0
INNER JOIN RDR1 T1 ON T1.DocEntry = T0.DocEntry
WHERE T0.DocStatus = 'O' AND T1.LineStatus = 'O' AND T1.OpenQty > 0;";

    /// <summary>Open purchase-order lines -> scheduled receipts. OPOR header + POR1 lines.</summary>
    private const string SqlPurchaseSupply = @"
SELECT
    T1.ItemCode                              AS ItemCode,
    T1.WhsCode                               AS WarehouseCode,
    T1.OpenQty                               AS OpenQty,
    ISNULL(T1.ShipDate, T0.DocDueDate)       AS DueDate,
    CAST(0 AS BIT)                           AS IsProduction,
    ('OPOR:' + CAST(T0.DocEntry AS VARCHAR)) AS SourceDocKey,
    T1.LineNum                               AS LineNum       -- so an expedite/defer can target exactly this line
FROM OPOR T0
INNER JOIN POR1 T1 ON T1.DocEntry = T0.DocEntry
WHERE T0.DocStatus = 'O' AND T1.LineStatus = 'O' AND T1.OpenQty > 0;";

    /// <summary>
    /// Open production orders -> scheduled receipts of the parent item. OWOR is the production order
    /// header; PlannedQty - CmpltQty is the open quantity still to be received.
    /// Component (dependent) demand is derived by the engine from the BOM, so only the parent supply
    /// is projected here.
    /// </summary>
    private const string SqlProductionSupply = @"
SELECT
    T0.ItemCode                               AS ItemCode,
    T0.Warehouse                              AS WarehouseCode,
    (T0.PlannedQty - T0.CmpltQty)             AS OpenQty,
    ISNULL(T0.DueDate, T0.PostDate)           AS DueDate,
    CAST(1 AS BIT)                            AS IsProduction,
    ('OWOR:' + CAST(T0.DocEntry AS VARCHAR))  AS SourceDocKey
FROM OWOR T0
WHERE T0.Status IN ('R','P')                  -- Released / Planned
  AND (T0.PlannedQty - T0.CmpltQty) > 0;";

    /// <summary>
    /// Demand forecasts.
    /// Verified against B1 10.0 (SBODemoHO): OFCT header / FCT1 lines (joined on AbsID). B1 stores no
    /// consumed quantity, so Consumed is 0 and the engine nets forecast against actual sales orders.
    /// </summary>
    private const string SqlForecasts = @"
-- B1 forecasts (Forecast Management): OFCT header, FCT1 lines. There is no consumed quantity in B1.
SELECT
    T1.ItemCode                 AS ItemCode,
    T1.WhsCode                  AS WarehouseCode,
    T1.[Date]                   AS PeriodStart,
    T1.Quantity                 AS Quantity,
    CAST(0 AS DECIMAL(19,6))    AS Consumed
FROM OFCT T0
INNER JOIN FCT1 T1 ON T1.AbsID = T0.AbsID;";

    /// <summary>
    /// Components of open (planned or released) production orders that have not been issued yet. B1 itself
    /// plans these as demand; without them a component looks free while a released order is about to consume it.
    /// WOR1.ItemType 4 = item line (290 = resource). Verified against B1 10.0 (SBODemoHO).
    /// </summary>
    private const string SqlProductionComponents = @"
SELECT
    L.ItemCode                                         AS ItemCode,
    L.wareHouse                                        AS WarehouseCode,
    (L.PlannedQty - L.IssuedQty)                       AS OpenQty,
    ISNULL(L.StartDate, ISNULL(H.StartDate, H.DueDate)) AS DueDate,
    ('OWOR:' + CAST(H.DocEntry AS VARCHAR))            AS SourceDocKey
FROM WOR1 L
INNER JOIN OWOR H ON H.DocEntry = L.DocEntry
WHERE H.Status IN ('P','R') AND L.ItemType = 4 AND (L.PlannedQty - L.IssuedQty) > 0;";

    // ---- Master data (cheap: items, groups, suppliers only) --------------------------------------------------

    private const string SqlItemGroups = @"
SELECT G.ItmsGrpCod AS Code, G.ItmsGrpNam AS Name, G.LeadTime AS LeadTime,
       (SELECT COUNT(*) FROM OITM I WHERE I.ItmsGrpCod = G.ItmsGrpCod AND I.InvntItem = 'Y' AND I.validFor = 'Y') AS ItemCount
FROM OITB G
ORDER BY G.ItmsGrpCod;";

    private const string SqlSuppliers = @"
SELECT C.CardCode AS CardCode, C.CardName AS CardName
FROM OCRD C
WHERE C.CardType = 'S' AND C.validFor = 'Y'
ORDER BY C.CardCode;";

    public async Task<MasterData> LoadMasterAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var items = (await conn.QueryAsync<ItemRow>(new CommandDefinition(SqlItems, cancellationToken: ct))).ToList();
        var groups = (await conn.QueryAsync<GroupRow>(new CommandDefinition(SqlItemGroups, cancellationToken: ct))).ToList();
        var suppliers = (await conn.QueryAsync<SupplierRow>(new CommandDefinition(SqlSuppliers, cancellationToken: ct))).ToList();

        var names = groups.ToDictionary(g => g.Code.ToString(), g => g.Name);
        return new MasterData
        {
            Items = items.Select(r =>
            {
                var plan = MapItem(r);
                return new MasterItem(plan, plan.GroupCode, plan.GroupCode is not null && names.TryGetValue(plan.GroupCode, out var n) ? n : null);
            }).ToList(),
            Groups = groups.Select(g => new ItemGroupInfo(g.Code.ToString(), g.Name ?? g.Code.ToString(), g.LeadTime is > 0 ? g.LeadTime : null, g.ItemCount)).ToList(),
            Suppliers = suppliers.Select(x => new MasterSupplier(x.CardCode.Trim(), x.CardName)).ToList(),
        };
    }

    /// <summary>
    /// Company working calendar. OADM.HldCode names the company's holiday calendar; OHLD holds its weekend
    /// days and HLD1 its holiday dates. Verified against B1 10.0 (SBODemoHO).
    /// </summary>
    private const string SqlCalendarHeader = @"
SELECT H.WndFrm AS WeekendFrom, H.WndTo AS WeekendTo
FROM OADM A
INNER JOIN OHLD H ON H.HldCode = A.HldCode;";

    private const string SqlHolidays = @"
SELECT D.StrDate, D.EndDate, D.Rmrks
FROM HLD1 D
INNER JOIN OADM A ON A.HldCode = D.HldCode;";

    // ---- DTO rows (match the SELECT aliases above) ------------------------------------------

    private sealed record ItemRow
    {
        public string ItemCode { get; init; } = "";
        public string? ItemName { get; init; }
        public string? ProcurementMethod { get; init; }
        public string? PlanningSys { get; init; }
        public int? LeadTime { get; init; }
        public decimal? MinOrderQty { get; init; }
        public decimal? OrderMultiple { get; init; }
        public decimal? SafetyStock { get; init; }
        public decimal? MaxStockLevel { get; init; }
        public decimal? ReorderPoint { get; init; }
        public decimal? FixedLotSize { get; init; }
        public string? HomeWarehouse { get; init; }
        public string? PreferredVendor { get; init; }
        public int? GroupCode { get; init; }
        public int? GroupLeadTime { get; init; }
    }

    private sealed record StockRow
    {
        public string ItemCode { get; init; } = "";
        public string WarehouseCode { get; init; } = "";
        public decimal OnHand { get; init; }
        public decimal Committed { get; init; }
        public decimal Ordered { get; init; }
    }

    private sealed record WarehouseRow
    {
        public string Code { get; init; } = "";
        public string? Name { get; init; }
    }

    private sealed record BomRow
    {
        public string ParentItemCode { get; init; } = "";
        public string ComponentItemCode { get; init; } = "";
        public decimal QuantityPer { get; init; }
        public decimal? ScrapPercent { get; init; }
        public string? WarehouseCode { get; init; }
    }

    private sealed record RoutingRow
    {
        public string ItemCode { get; init; } = "";
        public int Sequence { get; init; }
        public string WorkCenterCode { get; init; } = "";
        public decimal HoursPerUnit { get; init; }
        public decimal SetupHours { get; init; }
    }

    private sealed record WorkCenterRow
    {
        public string Code { get; init; } = "";
        public string? Name { get; init; }
        public decimal Units { get; init; }
    }

    private sealed record DemandRow
    {
        public string ItemCode { get; init; } = "";
        public string WarehouseCode { get; init; } = "";
        public decimal OpenQty { get; init; }
        public DateTime DueDate { get; init; }
        public string? SourceDocKey { get; init; }
    }

    private sealed record SupplyRow
    {
        public string ItemCode { get; init; } = "";
        public string WarehouseCode { get; init; } = "";
        public decimal OpenQty { get; init; }
        public DateTime DueDate { get; init; }
        public bool IsProduction { get; init; }
        public string? SourceDocKey { get; init; }
        public int? LineNum { get; init; }
    }

    private sealed record GroupRow
    {
        public int Code { get; init; }
        public string? Name { get; init; }
        public int? LeadTime { get; init; }
        public int ItemCount { get; init; }
    }

    private sealed record SupplierRow
    {
        public string CardCode { get; init; } = "";
        public string? CardName { get; init; }
    }

    private sealed record CalendarHeaderRow
    {
        public int? WeekendFrom { get; init; }
        public int? WeekendTo { get; init; }
    }

    private sealed record HolidayRow
    {
        public DateTime StrDate { get; init; }
        public DateTime EndDate { get; init; }
        public string? Rmrks { get; init; }
    }

    private sealed record ForecastRow
    {
        public string ItemCode { get; init; } = "";
        public string WarehouseCode { get; init; } = "";
        public DateTime PeriodStart { get; init; }
        public decimal Quantity { get; init; }
        public decimal? Consumed { get; init; }
    }
}
