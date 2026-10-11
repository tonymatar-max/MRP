using CobaltMrp.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Api.Services;

/// <summary>
/// Creates and upgrades the Cobalt MRP tables. There are no EF migrations: <c>EnsureCreated</c> builds a
/// brand-new database (and the database itself when the login may create one); anything added later goes
/// in <see cref="UpgradeAsync"/> as an idempotent step, so an existing install is brought forward in place.
/// </summary>
public static class CobaltSchema
{
    public static readonly string[] Tables =
        { "MrpRun", "PlannedOrder", "PeggingLink", "ExceptionMessage", "CapacityLoad", "Forecast", "CalendarEvent", "ItemPlanning", "SupplierPlanning" };

    public static async Task<string> EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<CobaltMrpDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new CobaltMrpDbContext(options);
        var created = await db.Database.EnsureCreatedAsync(ct);
        await UpgradeAsync(db, ct);
        var present = await CountTablesAsync(connectionString, ct);
        return created
            ? $"Created the database and tables ({present}/{Tables.Length})."
            : $"Tables are up to date ({present}/{Tables.Length}).";
    }

    /// <summary>Idempotent steps for installs created by an older version. Add new steps at the end.</summary>
    private static async Task UpgradeAsync(CobaltMrpDbContext db, CancellationToken ct)
    {
        // v2: Cobalt-managed forecasts.
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('dbo.Forecast') IS NULL
BEGIN
    CREATE TABLE dbo.Forecast (
        Id            NVARCHAR(32)   NOT NULL PRIMARY KEY,
        ItemCode      NVARCHAR(50)   NOT NULL,
        WarehouseCode NVARCHAR(8)    NOT NULL,
        PeriodStart   DATE           NOT NULL,
        Quantity      DECIMAL(19,6)  NOT NULL,
        Consumed      DECIMAL(19,6)  NOT NULL
    );
    CREATE INDEX IX_Forecast_Item ON dbo.Forecast (ItemCode, WarehouseCode, PeriodStart);
END", ct);

        // v3: preferred vendor on planned orders, and the planning calendar.
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('dbo.PlannedOrder', 'VendorCode') IS NULL
    ALTER TABLE dbo.PlannedOrder ADD VendorCode NVARCHAR(50) NULL;", ct);
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('dbo.CalendarEvent') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEvent (
        Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        MrpRunId      UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT FK_CalendarEvent_MrpRun REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
        [Date]        DATE           NOT NULL,
        Kind          INT            NOT NULL,
        ItemCode      NVARCHAR(50)   NOT NULL,
        WarehouseCode NVARCHAR(8)    NULL,
        Quantity      DECIMAL(19,6)  NOT NULL,
        Label         NVARCHAR(500)  NOT NULL,
        Ref           NVARCHAR(100)  NULL,
        Severity      NVARCHAR(10)   NOT NULL
    );
    CREATE INDEX IX_CalendarEvent_MrpRunId_Date ON dbo.CalendarEvent (MrpRunId, [Date]);
END", ct);

        // v4: wizard criteria on runs, lead-time/vendor provenance on planned orders, and the master-data overrides.
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('dbo.MrpRun', 'CriteriaJson') IS NULL ALTER TABLE dbo.MrpRun ADD CriteriaJson NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.PlannedOrder', 'LeadTimeDays') IS NULL ALTER TABLE dbo.PlannedOrder ADD LeadTimeDays INT NOT NULL CONSTRAINT DF_PlannedOrder_LeadTimeDays DEFAULT 0;
IF COL_LENGTH('dbo.PlannedOrder', 'LeadTimeSource') IS NULL ALTER TABLE dbo.PlannedOrder ADD LeadTimeSource NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.PlannedOrder', 'VendorSource') IS NULL ALTER TABLE dbo.PlannedOrder ADD VendorSource NVARCHAR(30) NULL;", ct);
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('dbo.ItemPlanning') IS NULL
    CREATE TABLE dbo.ItemPlanning (
        ItemCode           NVARCHAR(50)  NOT NULL PRIMARY KEY,
        PlanningMethod     INT           NULL,
        LotSizing          INT           NULL,
        LeadTimeDays       INT           NULL,
        MinOrderQty        DECIMAL(19,6) NULL,
        OrderMultiple      DECIMAL(19,6) NULL,
        SafetyStock        DECIMAL(19,6) NULL,
        ReorderPoint       DECIMAL(19,6) NULL,
        MaxStockLevel      DECIMAL(19,6) NULL,
        FixedLotSize       DECIMAL(19,6) NULL,
        PeriodOfSupplyDays INT           NULL,
        Supplier           NVARCHAR(50)  NULL,
        Notes              NVARCHAR(1000) NOT NULL,
        UpdatedBy          NVARCHAR(100) NOT NULL,
        UpdatedAt          DATETIMEOFFSET NOT NULL
    );", ct);
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('dbo.SupplierPlanning') IS NULL
    CREATE TABLE dbo.SupplierPlanning (
        CardCode     NVARCHAR(50)   NOT NULL PRIMARY KEY,
        LeadTimeDays INT            NULL,
        Notes        NVARCHAR(1000) NOT NULL,
        UpdatedBy    NVARCHAR(100)  NOT NULL,
        UpdatedAt    DATETIMEOFFSET NOT NULL
    );", ct);

        // v5: exceptions can be executed (ids, links to the order/document, dates, audit trail).
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('dbo.ExceptionMessage', 'ExceptionId') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ExceptionId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ExceptionMessage_ExceptionId DEFAULT NEWID();
IF COL_LENGTH('dbo.ExceptionMessage', 'SourceLine') IS NULL ALTER TABLE dbo.ExceptionMessage ADD SourceLine INT NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'PlannedOrderId') IS NULL ALTER TABLE dbo.ExceptionMessage ADD PlannedOrderId UNIQUEIDENTIFIER NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'CurrentDate') IS NULL ALTER TABLE dbo.ExceptionMessage ADD CurrentDate DATE NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ProposedDate') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ProposedDate DATE NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionStatus') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionStatus NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionBy') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionBy NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionAt') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionAt DATETIMEOFFSET NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionNote') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionNote NVARCHAR(500) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExceptionMessage_ExceptionId') CREATE INDEX IX_ExceptionMessage_ExceptionId ON dbo.ExceptionMessage (ExceptionId);", ct);

        // v6: transfer requests remember the sending warehouse.
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('dbo.PlannedOrder', 'FromWarehouse') IS NULL ALTER TABLE dbo.PlannedOrder ADD FromWarehouse NVARCHAR(8) NULL;", ct);
    }

    public static async Task<int> CountTablesAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name IN (" +
                          string.Join(",", Tables.Select(t => $"'{t}'")) + ")";
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }
}
