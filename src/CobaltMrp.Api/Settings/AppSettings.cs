using System.Text.Json.Serialization;
using CobaltMrp.Domain;
using Microsoft.Data.SqlClient;

namespace CobaltMrp.Api.Settings;

/// <summary>One SQL Server connection, edited as fields (or as a raw connection-string override).</summary>
public sealed class DbSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>"Windows" or "Sql".</summary>
    public string Authentication { get; set; } = "Windows";
    public string UserName { get; set; } = "";
    /// <summary>Plain text in memory and on the wire from the UI only. Never returned by GET (see PasswordSet).</summary>
    public string Password { get; set; } = "";
    public bool PasswordSet { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>Advanced: when set, used verbatim instead of the fields above.</summary>
    public string ConnectionStringOverride { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionStringOverride) ||
        (!string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database));

    /// <summary>Build the connection string. <paramref name="readOnlyIntent"/> is used for the B1 database.</summary>
    public string? BuildConnectionString(bool readOnlyIntent = false)
    {
        if (!IsConfigured) return null;
        if (!string.IsNullOrWhiteSpace(ConnectionStringOverride)) return ConnectionStringOverride.Trim();

        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            InitialCatalog = Database.Trim(),
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 15,
            ApplicationName = "CobaltMRP",
        };
        if (string.Equals(Authentication, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            b.UserID = UserName;
            b.Password = Password;
        }
        else b.IntegratedSecurity = true;
        if (readOnlyIntent) b.ApplicationIntent = ApplicationIntent.ReadOnly;
        return b.ConnectionString;
    }

    public DbSettings Clone() => (DbSettings)MemberwiseClone();

    public const string Mask = "********";

    /// <summary>Hide the password inside a raw connection string (for GET).</summary>
    public static string MaskOverride(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return "";
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            if (!string.IsNullOrEmpty(b.Password)) b.Password = Mask;
            return b.ConnectionString;
        }
        catch { return System.Text.RegularExpressions.Regex.Replace(cs, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=" + Mask); }
    }

    /// <summary>Put the stored password back where the UI sent the mask, so editing other parts of the
    /// string does not wipe the secret.</summary>
    public static string UnmaskOverride(string incoming, string stored)
    {
        if (string.IsNullOrWhiteSpace(incoming) || !incoming.Contains(Mask)) return incoming;
        string pwd = "";
        try { pwd = new SqlConnectionStringBuilder(stored).Password; } catch { }
        return incoming.Replace(Mask, pwd);
    }
}

public sealed class ItemVendor
{
    public string ItemCode { get; set; } = "";
    public string CardCode { get; set; } = "";
}

public sealed class ServiceLayerSettings
{
    /// <summary>OFF by default: when on, Release creates real purchase/production orders in SAP B1.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string CompanyDb { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool PasswordSet { get; set; }
    public bool AllowInvalidCertificates { get; set; }
    public string DefaultVendor { get; set; } = "";
    public List<ItemVendor> ItemVendors { get; set; } = new();

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(CompanyDb) &&
        !string.IsNullOrWhiteSpace(UserName);

    public string? ResolveVendor(string itemCode)
    {
        var hit = ItemVendors.FirstOrDefault(v => string.Equals(v.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));
        var card = hit?.CardCode;
        if (string.IsNullOrWhiteSpace(card)) card = DefaultVendor;
        return string.IsNullOrWhiteSpace(card) ? null : card;
    }

    public ServiceLayerSettings Clone()
    {
        var c = (ServiceLayerSettings)MemberwiseClone();
        c.ItemVendors = ItemVendors.Select(v => new ItemVendor { ItemCode = v.ItemCode, CardCode = v.CardCode }).ToList();
        return c;
    }
}

public sealed class NonWorkingDate
{
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Per-resource capacity. SAP B1 stores no hours, so the administrator states them here.</summary>
public sealed class ResourceSetting
{
    public string Code { get; set; } = "";
    public decimal HoursPerDay { get; set; } = 8m;
    public decimal EfficiencyPercent { get; set; } = 100m;
    public decimal SetupHoursPerOrder { get; set; }
}

public sealed class PlanningSettings
{
    /// <summary>"mock" or "b1-sql".</summary>
    public string DefaultSource { get; set; } = "mock";
    public int HorizonDays { get; set; } = 180;
    public int BucketDays { get; set; } = 7;
    /// <summary>B1 stores no capacity hours; capacity = resource units x this.</summary>
    public decimal HoursPerUnitPerDay { get; set; } = 8m;
    public int ForecastLookbackDays { get; set; }
    /// <summary>"Forward" nets forecast against real sales orders in the same period; "None" counts both.</summary>
    public string ForecastConsumption { get; set; } = "Forward";
    /// <summary>Use the SAP B1 company calendar (weekend days and holidays) when it has one.</summary>
    public bool UseB1Calendar { get; set; } = true;
    /// <summary>Working weekdays when the B1 calendar is off or absent (Mon..Sun).</summary>
    public List<string> WorkingDays { get; set; } = new() { "Mon", "Tue", "Wed", "Thu", "Fri" };
    /// <summary>Extra closures, always applied on top of the calendar.</summary>
    public List<NonWorkingDate> NonWorkingDates { get; set; } = new();
    public List<ResourceSetting> Resources { get; set; } = new();
    /// <summary>Which lead time wins, in order: any order of Supplier, Item, ItemGroup. The wizard starts from this.</summary>
    public List<string> LeadTimePriority { get; set; } = new() { "Supplier", "Item", "ItemGroup" };
    /// <summary>"FirstAvailable" or "Longest".</summary>
    public string LeadTimeMode { get; set; } = "FirstAvailable";
    public int DefaultLeadTimeDays { get; set; }
    /// <summary>"Warehouse" or "Company": where the wizard starts.</summary>
    public string DefaultLevel { get; set; } = "Warehouse";
    /// <summary>"Order" or "Request": what a purchased item produces.</summary>
    public string PurchaseDocument { get; set; } = "Order";
    /// <summary>"None", "Supplier" or "SupplierAndDate": the wizard starts from this (see DocumentGrouping).</summary>
    public string DocumentGrouping { get; set; } = "Supplier";
    public int TransferLeadTimeDays { get; set; } = 1;

    public PlanningSettings Clone() => new()
    {
        DefaultSource = DefaultSource, HorizonDays = HorizonDays, BucketDays = BucketDays,
        HoursPerUnitPerDay = HoursPerUnitPerDay, ForecastLookbackDays = ForecastLookbackDays,
        ForecastConsumption = ForecastConsumption, UseB1Calendar = UseB1Calendar,
        WorkingDays = WorkingDays.ToList(),
        LeadTimePriority = LeadTimePriority.ToList(), LeadTimeMode = LeadTimeMode, DefaultLeadTimeDays = DefaultLeadTimeDays,
        DefaultLevel = DefaultLevel, PurchaseDocument = PurchaseDocument, DocumentGrouping = DocumentGrouping, TransferLeadTimeDays = TransferLeadTimeDays,
        NonWorkingDates = NonWorkingDates.Select(n => new NonWorkingDate { Date = n.Date, Reason = n.Reason }).ToList(),
        Resources = Resources.Select(r => new ResourceSetting
        {
            Code = r.Code, HoursPerDay = r.HoursPerDay, EfficiencyPercent = r.EfficiencyPercent,
            SetupHoursPerOrder = r.SetupHoursPerOrder,
        }).ToList(),
    };
}

/// <summary>The optional AI assistant (OpenRouter). OFF by default; read-only over a saved run.</summary>
public sealed class AiSettings
{
    public const string DefaultModel = "anthropic/claude-opus-5";
    public const int DefaultMaxTokens = 1024;

    public bool Enabled { get; set; }
    /// <summary>Plain text in memory and on the wire from the UI only. Never returned by GET (see ApiKeySet).</summary>
    public string ApiKey { get; set; } = "";
    public bool ApiKeySet { get; set; }
    public string Model { get; set; } = DefaultModel;
    public int MaxTokens { get; set; } = DefaultMaxTokens;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);

    public AiSettings Clone() => (AiSettings)MemberwiseClone();
}

/// <summary>Email an alert when a run produces exceptions the run before it did not have. OFF by default.</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    /// <summary>587 (STARTTLS) by default. Implicit TLS on 465 is not supported by the .NET SMTP client.</summary>
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    /// <summary>Plain text in memory and on the wire from the UI only. Never returned by GET (see PasswordSet).</summary>
    public string Password { get; set; } = "";
    public bool PasswordSet { get; set; }
    public string From { get; set; } = "";
    /// <summary>Addresses separated by comma, semicolon or newline.</summary>
    public string Recipients { get; set; } = "";
    /// <summary>The address planners use to open Cobalt MRP; the email links to its Exceptions page. Optional.</summary>
    public string PublicUrl { get; set; } = "";
    /// <summary>"ScheduledRuns" (default: a person who starts a run sees the result anyway) or "AllRuns".</summary>
    public string NotifyOn { get; set; } = "ScheduledRuns";

    [JsonIgnore]
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(From) && RecipientList().Count > 0;

    public List<string> RecipientList() => RecipientRules().Select(r => r.Address).ToList();

    /// <summary>
    /// One rule per address. "buyer@x.com" gets every exception; "buyer@x.com: purchasing" only that area; several areas are
    /// joined with "+" ("pm@x.com: production+capacity"). Areas: purchasing, production, capacity, general. A repeated
    /// address merges its areas; an unknown area word is ignored (and if none is left the address gets everything).
    /// </summary>
    public List<RecipientRule> RecipientRules()
    {
        var map = new Dictionary<string, RecipientRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (Recipients ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.IndexOf(':');
            var address = (colon < 0 ? raw : raw[..colon]).Trim();
            if (!address.Contains('@')) continue;
            HashSet<string>? areas = null;
            if (colon >= 0)
            {
                var parsed = raw[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => a.ToLowerInvariant()).Where(a => ExceptionAreas.All.Contains(a)).ToHashSet();
                if (parsed.Count > 0 && !parsed.SetEquals(ExceptionAreas.All)) areas = parsed;
            }
            if (!map.TryGetValue(address, out var existing)) map[address] = new RecipientRule(address, areas);
            else if (existing.Areas is not null)
                map[address] = existing with { Areas = areas is null ? null : existing.Areas.Union(areas).ToHashSet() };
        }
        return map.Values.ToList();
    }

    /// <summary>The text form of the rules, the way it is stored.</summary>
    public string NormalisedRecipients() => string.Join(", ", RecipientRules().Select(r => r.Areas is null ? r.Address : r.Address + ": " + string.Join("+", ExceptionAreas.All.Where(r.Areas.Contains))));

    public NotificationSettings Clone() => (NotificationSettings)MemberwiseClone();
}

/// <summary>An alert recipient; Areas null = every exception.</summary>
public sealed record RecipientRule(string Address, HashSet<string>? Areas);

public static class ExceptionAreas
{
    public const string Purchasing = "purchasing", Production = "production", Capacity = "capacity", General = "general";
    public static readonly string[] All = { Purchasing, Production, Capacity, General };

    /// <summary>Capacity overloads are capacity; exceptions about (or planned as) work orders are production; those about
    /// purchase orders/requests and transfers are purchasing; the rest (e.g. past due demand) is general.</summary>
    public static string Of(ExceptionMessage e, IReadOnlyDictionary<Guid, PlannedOrderType> plannedTypes)
    {
        if (e.Type == ExceptionType.CapacityOverload) return Capacity;
        var doc = e.SourceDocKey ?? "";
        if (doc.StartsWith("OWOR", StringComparison.OrdinalIgnoreCase)) return Production;
        if (doc.StartsWith("OPOR", StringComparison.OrdinalIgnoreCase) || doc.StartsWith("OPRQ", StringComparison.OrdinalIgnoreCase)
            || doc.StartsWith("OWTQ", StringComparison.OrdinalIgnoreCase)) return Purchasing;
        if (e.PlannedOrderId is { } id && plannedTypes.TryGetValue(id, out var t))
            return t == PlannedOrderType.ProductionOrder ? Production : Purchasing;
        return General;
    }
}

/// <summary>One scheduled run: a saved Run MRP template, run at a time of day (server local time) on chosen days.</summary>
public sealed class ScheduleSettings
{
    public static readonly string[] AllDays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    public const int MaxSchedules = 20;
    /// <summary>Id given to the single schedule of an older settings file when it is moved into the list.</summary>
    public const string MigratedId = "default";

    /// <summary>Stable id; empty from the UI for a new schedule (the server assigns one).</summary>
    public string Id { get; set; } = "";
    /// <summary>Optional label, up to 60 characters.</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>"HH:mm", 24-hour, the server's local time.</summary>
    public string Time { get; set; } = "02:00";
    public List<string> Days { get; set; } = new(AllDays);
    /// <summary>A saved Run MRP template (id). Required to enable a schedule; a template that was deleted later is allowed to
    /// stay saved: the schedule then reports "template missing" and is skipped.</summary>
    public string TemplateId { get; set; } = "";

    public ScheduleSettings Clone() => new() { Id = Id, Name = Name, Enabled = Enabled, Time = Time, Days = Days.ToList(), TemplateId = TemplateId };

    /// <summary>Day name ("Mon".."Sun") for any text that starts with one, else null.</summary>
    public static string? DayOf(string? d) => d is null ? null : AllDays.FirstOrDefault(x => d.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a legacy single schedule differs from the untouched default (so it is worth keeping).</summary>
    [JsonIgnore]
    public bool IsCustomised => Enabled || !string.IsNullOrWhiteSpace(TemplateId) || Time != "02:00" || (Days?.Count ?? 7) != 7;

    /// <summary>
    /// What the administrator must fix before saving, or null. A bad time is refused (not silently replaced); an enabled schedule
    /// needs a template and at least one day. <paramref name="stored"/> lets a schedule saved before templates were required
    /// stay enabled without one (it keeps using the Settings defaults) until somebody edits it.
    /// </summary>
    public static string? Validate(List<ScheduleSettings> incoming, List<ScheduleSettings> stored)
    {
        if (incoming.Count > MaxSchedules) return $"At most {MaxSchedules} schedules are allowed.";
        for (var n = 0; n < incoming.Count; n++)
        {
            var s = incoming[n];
            var label = string.IsNullOrWhiteSpace(s.Name) ? $"Schedule {n + 1}" : $"Schedule \"{s.Name.Trim()}\"";
            if (!TimeOnly.TryParseExact((s.Time ?? "").Trim(), "HH:mm", out _)) return $"{label}: the time must be HH:mm, for example 02:00.";
            if ((s.Days ?? new()).Any(d => DayOf(d) is null)) return $"{label}: the days must be Mon, Tue, Wed, Thu, Fri, Sat or Sun.";
            if (!s.Enabled) continue;
            if (!(s.Days ?? new()).Any()) return $"{label}: choose at least one day.";
            if (string.IsNullOrWhiteSpace(s.TemplateId))
            {
                var grandfathered = !string.IsNullOrEmpty(s.Id) && stored.Any(o => o.Id == s.Id && o.Enabled && string.IsNullOrWhiteSpace(o.TemplateId));
                if (!grandfathered) return $"{label}: choose a template to run.";
            }
        }
        return null;
    }
}

/// <summary>Everything an administrator configures from the Settings screen.</summary>
public sealed class AppSettings
{
    // Empty by default: until an administrator configures it, runs are kept in memory (the UI warns).
    public DbSettings CobaltDb { get; set; } = new();
    public DbSettings B1Db { get; set; } = new();
    public ServiceLayerSettings ServiceLayer { get; set; } = new();
    public PlanningSettings Planning { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    /// <summary>The scheduled runs (at most 20). Null on the wire from an older client = keep the stored ones.</summary>
    public List<ScheduleSettings>? Schedules { get; set; }
    /// <summary>LEGACY single schedule (before multiple schedules). Read from an old settings file or an old client and moved into
    /// <see cref="Schedules"/> once; never written back and never returned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScheduleSettings? Schedule { get; set; }

    public AppSettings Clone() => new()
    {
        CobaltDb = CobaltDb.Clone(), B1Db = B1Db.Clone(), ServiceLayer = ServiceLayer.Clone(),
        Planning = Planning.Clone(), Ai = Ai?.Clone() ?? new AiSettings(),
        Notifications = Notifications?.Clone() ?? new NotificationSettings(), Schedules = Schedules?.Select(x => x.Clone()).ToList(), Schedule = Schedule?.Clone(),
    };
}
