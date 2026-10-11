using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

/// <summary>Turns the administrator's Settings into the calendar and resource data the engine plans with.</summary>
public static class PlanningSetup
{
    private static readonly (string Name, DayOfWeek Day)[] Days =
    {
        ("Mon", DayOfWeek.Monday), ("Tue", DayOfWeek.Tuesday), ("Wed", DayOfWeek.Wednesday),
        ("Thu", DayOfWeek.Thursday), ("Fri", DayOfWeek.Friday), ("Sat", DayOfWeek.Saturday), ("Sun", DayOfWeek.Sunday),
    };

    /// <summary>
    /// The SAP B1 company calendar when it exists and is switched on, otherwise the working weekdays from Settings;
    /// the extra non-working dates from Settings always apply on top.
    /// </summary>
    public static WorkCalendar BuildCalendar(WorkCalendar? b1Calendar, PlanningSettings p)
    {
        WorkCalendar baseCalendar;
        if (p.UseB1Calendar && b1Calendar is not null) baseCalendar = b1Calendar;
        else
        {
            var working = new HashSet<string>(p.WorkingDays, StringComparer.OrdinalIgnoreCase);
            // An empty list would mean the plant never works; treat that as "not configured": Mon-Fri.
            if (working.Count == 0) foreach (var d in new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }) working.Add(d);
            baseCalendar = new WorkCalendar(Days.Where(d => !working.Contains(d.Name)).Select(d => d.Day));
        }

        var extra = new WorkCalendar(null, p.NonWorkingDates.Select(n =>
            new KeyValuePair<DateOnly, string>(n.Date, string.IsNullOrWhiteSpace(n.Reason) ? "Closed" : n.Reason)));
        return baseCalendar.Union(extra);
    }

    /// <summary>Apply the per-resource hours, efficiency and setup time entered in Settings.</summary>
    public static (IReadOnlyList<WorkCenter> Centers, IReadOnlyList<RoutingOperation> Routings) ApplyResources(
        IReadOnlyList<WorkCenter> centers, IReadOnlyList<RoutingOperation> routings, PlanningSettings p)
    {
        if (p.Resources.Count == 0) return (centers, routings);
        var by = p.Resources.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        var newCenters = centers.Select(c => by.TryGetValue(c.Code, out var r)
            ? new WorkCenter
            {
                Code = c.Code, Name = c.Name, Units = c.Units,
                DailyCapacityHours = r.HoursPerDay, EfficiencyFactor = r.EfficiencyPercent / 100m,
            }
            : c).ToList();

        // B1 has no setup time; a setup entered in Settings applies when the routing itself carries none.
        var newRoutings = routings.Select(o => by.TryGetValue(o.WorkCenterCode, out var r) && o.SetupHours == 0 && r.SetupHoursPerOrder > 0
            ? new RoutingOperation
            {
                ItemCode = o.ItemCode, Sequence = o.Sequence, WorkCenterCode = o.WorkCenterCode,
                HoursPerUnit = o.HoursPerUnit, SetupHours = r.SetupHoursPerOrder,
            }
            : o).ToList();
        return (newCenters, newRoutings);
    }
}

/// <summary>Where a purchase order's supplier comes from, in order of precedence.</summary>
public static class VendorResolver
{
    public const string Override = "settings-override";
    public const string MasterData = "master-data";
    public const string B1Preferred = "b1-preferred";
    public const string Default = "default";

    /// <summary>
    /// 1. an explicit entry in Settings (legacy), 2. the supplier recorded on the order when it was planned (Master data
    /// default supplier, else SAP B1's preferred vendor), 3. the default vendor in Settings.
    /// </summary>
    public static (string? Code, string? Source) Resolve(PlannedOrder o, ServiceLayerSettings sl)
    {
        if (o.Type is not (PlannedOrderType.PurchaseOrder or PlannedOrderType.PurchaseRequest)) return (null, null);

        var hit = sl.ItemVendors.FirstOrDefault(v => string.Equals(v.ItemCode, o.ItemCode, StringComparison.OrdinalIgnoreCase));
        if (hit is not null && !string.IsNullOrWhiteSpace(hit.CardCode)) return (hit.CardCode.Trim(), Override);
        if (!string.IsNullOrWhiteSpace(o.VendorCode)) return (o.VendorCode.Trim(), o.VendorSource == MasterData ? MasterData : B1Preferred);
        if (!string.IsNullOrWhiteSpace(sl.DefaultVendor)) return (sl.DefaultVendor.Trim(), Default);
        return (null, null);
    }
}

/// <summary>A planned order as the UI sees it: the saved order plus the supplier Release would use right now.</summary>
public sealed record PlannedOrderDto(
    Guid Id, string ItemCode, string WarehouseCode, PlannedOrderType Type, decimal Quantity,
    DateOnly DueDate, DateOnly ReleaseDate, int LowLevelCode, bool Firmed, string? B1DocEntry,
    string? VendorCode, string? VendorSource, int LeadTimeDays, string? LeadTimeSource, string? FromWarehouse = null)
{
    public static PlannedOrderDto From(PlannedOrder o, ServiceLayerSettings sl)
    {
        var (code, source) = VendorResolver.Resolve(o, sl);
        return new PlannedOrderDto(o.Id, o.ItemCode, o.WarehouseCode, o.Type, o.Quantity, o.DueDate, o.ReleaseDate,
            o.LowLevelCode, o.Firmed, o.B1DocEntry, code, source, o.LeadTimeDays, o.LeadTimeSource, o.FromWarehouse);
    }
}
