using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CobaltMrp.Data;

/// <summary>EF Core context persisting each MRP run as a saved snapshot (run + children).</summary>
public sealed class CobaltMrpDbContext : DbContext
{
    public CobaltMrpDbContext(DbContextOptions<CobaltMrpDbContext> options) : base(options) { }

    public DbSet<MrpRun> Runs => Set<MrpRun>();
    public DbSet<PlannedOrder> PlannedOrders => Set<PlannedOrder>();
    public DbSet<PeggingLink> PeggingLinks => Set<PeggingLink>();
    public DbSet<ExceptionMessage> Exceptions => Set<ExceptionMessage>();
    public DbSet<CapacityLoad> CapacityLoads => Set<CapacityLoad>();
    public DbSet<ForecastRow> Forecasts => Set<ForecastRow>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<ItemOverride> ItemOverrides => Set<ItemOverride>();
    public DbSet<SupplierSetting> SupplierSettings => Set<SupplierSetting>();
    public DbSet<SapPushLogEntry> SapPushLog => Set<SapPushLogEntry>();
    public DbSet<RunTemplate> RunTemplates => Set<RunTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Table/column shapes match sql/cobalt-mrp-schema.sql.
        b.Entity<MrpRun>(e =>
        {
            e.ToTable("MrpRun");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunBy).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.CriteriaJson);          // nvarchar(max): the wizard criteria used
            e.HasMany(x => x.PlannedOrders).WithOne().HasForeignKey("MrpRunId").OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Pegging).WithOne().HasForeignKey("MrpRunId").OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Exceptions).WithOne().HasForeignKey("MrpRunId").OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.CapacityLoads).WithOne().HasForeignKey("MrpRunId").OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne().HasForeignKey("MrpRunId").OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.PlannedOrders).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Pegging).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Exceptions).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.CapacityLoads).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<PlannedOrder>(e =>
        {
            e.ToTable("PlannedOrder");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.WarehouseCode).HasMaxLength(8);
            e.Property(x => x.FromWarehouse).HasMaxLength(8);
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.B1DocEntry).HasMaxLength(50);
            e.Property(x => x.VendorCode).HasMaxLength(50);
            e.Property(x => x.VendorSource).HasMaxLength(30);
            e.Property(x => x.LeadTimeSource).HasMaxLength(20);
            e.Property<Guid>("MrpRunId");
            e.HasIndex("MrpRunId");
        });

        // PeggingLink/ExceptionMessage/CapacityLoad have no natural key: use a shadow identity Id.
        b.Entity<PeggingLink>(e =>
        {
            e.ToTable("PeggingLink");
            e.Property<long>("Id").ValueGeneratedOnAdd();
            e.HasKey("Id");
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.DemandDocKey).HasMaxLength(100);
            e.Property(x => x.ParentItemCode).HasMaxLength(50);
            e.Property<Guid>("MrpRunId");
            e.HasIndex("MrpRunId");
            e.HasIndex(x => x.PlannedOrderId); // plain column, no FK (avoids multiple cascade paths)
        });

        b.Entity<ExceptionMessage>(e =>
        {
            e.ToTable("ExceptionMessage");
            e.Property<long>("Id").ValueGeneratedOnAdd();
            e.HasKey("Id");
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.WarehouseCode).HasMaxLength(8);
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.SourceDocKey).HasMaxLength(100);
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.ActionStatus).HasMaxLength(20);
            e.Property(x => x.ActionBy).HasMaxLength(100);
            e.Property(x => x.ActionNote).HasMaxLength(500);
            e.Property<Guid>("MrpRunId");
            e.HasIndex("MrpRunId");
            e.HasIndex(x => x.ExceptionId);
        });

        // Planner overrides on top of SAP B1 master data (Cobalt-side only; nothing here is written to SAP B1).
        b.Entity<ItemOverride>(e =>
        {
            e.ToTable("ItemPlanning");
            e.HasKey(x => x.ItemCode);
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.Supplier).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            foreach (var n in new[] { nameof(ItemOverride.MinOrderQty), nameof(ItemOverride.OrderMultiple), nameof(ItemOverride.SafetyStock),
                                      nameof(ItemOverride.ReorderPoint), nameof(ItemOverride.MaxStockLevel), nameof(ItemOverride.FixedLotSize) })
                e.Property(n).HasPrecision(19, 6);
            e.Ignore(x => x.IsEmpty);
        });

        b.Entity<SupplierSetting>(e =>
        {
            e.ToTable("SupplierPlanning");
            e.HasKey(x => x.CardCode);
            e.Property(x => x.CardCode).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Ignore(x => x.IsEmpty);
        });

        b.Entity<RunTemplate>(e =>
        {
            e.ToTable("RunTemplate");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.CriteriaJson);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
        });

        // Every write to SAP B1, kept for good (who, when, what, outcome).
        b.Entity<SapPushLogEntry>(e =>
        {
            e.ToTable("SapPushLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.WarehouseCode).HasMaxLength(8);
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.Target).HasMaxLength(100);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(4000);
            e.HasIndex(x => x.At);
        });

        b.Entity<CalendarEvent>(e =>
        {
            e.ToTable("CalendarEvent");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.WarehouseCode).HasMaxLength(8);
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.Label).HasMaxLength(500);
            e.Property(x => x.Ref).HasMaxLength(100);
            e.Property(x => x.Severity).HasMaxLength(10);
            e.Property<Guid>("MrpRunId");
            // The Calendar screen reads one run over a date range.
            e.HasIndex("MrpRunId", nameof(CalendarEvent.Date));
        });

        b.Entity<ForecastRow>(e =>
        {
            e.ToTable("Forecast");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.ItemCode).HasMaxLength(50);
            e.Property(x => x.WarehouseCode).HasMaxLength(8);
            e.Property(x => x.Quantity).HasPrecision(19, 6);
            e.Property(x => x.Consumed).HasPrecision(19, 6);
            e.HasIndex(x => new { x.ItemCode, x.WarehouseCode, x.PeriodStart });
        });

        b.Entity<CapacityLoad>(e =>
        {
            e.ToTable("CapacityLoad");
            e.Property<long>("Id").ValueGeneratedOnAdd();
            e.HasKey("Id");
            e.Property(x => x.WorkCenterCode).HasMaxLength(50);
            e.Property(x => x.RequiredHours).HasPrecision(19, 6);
            e.Property(x => x.AvailableHours).HasPrecision(19, 6);
            e.Ignore(x => x.Overloaded); // computed
            e.Property<Guid>("MrpRunId");
            e.HasIndex("MrpRunId");
        });
    }
}

/// <summary>A Cobalt-managed demand forecast (edited on the Forecasts screen).</summary>
public sealed class ForecastRow
{
    public string Id { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string WarehouseCode { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public decimal Quantity { get; set; }
    public decimal Consumed { get; set; }
}

/// <summary>Design-time factory so <c>dotnet ef</c> can build the context headlessly.
/// Set env var COBALT_MRP_CONNECTION to override the default LocalDB connection.</summary>
public sealed class CobaltMrpDbContextFactory : IDesignTimeDbContextFactory<CobaltMrpDbContext>
{
    public CobaltMrpDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("COBALT_MRP_CONNECTION")
                 ?? "Server=(localdb)\\MSSQLLocalDB;Database=CobaltMrp;Trusted_Connection=True;TrustServerCertificate=True";
        var opts = new DbContextOptionsBuilder<CobaltMrpDbContext>().UseSqlServer(cs).Options;
        return new CobaltMrpDbContext(opts);
    }
}
