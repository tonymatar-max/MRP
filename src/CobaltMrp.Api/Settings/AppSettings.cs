using System.Text.Json.Serialization;
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

/// <summary>Everything an administrator configures from the Settings screen.</summary>
public sealed class AppSettings
{
    // Empty by default: until an administrator configures it, runs are kept in memory (the UI warns).
    public DbSettings CobaltDb { get; set; } = new();
    public DbSettings B1Db { get; set; } = new();
    public ServiceLayerSettings ServiceLayer { get; set; } = new();
    public PlanningSettings Planning { get; set; } = new();
    public AiSettings Ai { get; set; } = new();

    public AppSettings Clone() => new()
    {
        CobaltDb = CobaltDb.Clone(), B1Db = B1Db.Clone(), ServiceLayer = ServiceLayer.Clone(),
        Planning = Planning.Clone(), Ai = Ai?.Clone() ?? new AiSettings(),
    };
}
